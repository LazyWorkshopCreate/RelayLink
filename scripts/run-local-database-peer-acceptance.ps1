param([string]$DatabaseStatePath = '')

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $DatabaseStatePath) {
    $DatabaseStatePath = Get-ChildItem -LiteralPath (Join-Path $repoRoot '.local/database-acceptance') -Filter state.json -Recurse -File |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $DatabaseStatePath -or -not (Test-Path -LiteralPath $DatabaseStatePath)) { throw 'Run scripts/run-local-database-acceptance.ps1 first.' }
$database = Get-Content -LiteralPath $DatabaseStatePath -Raw | ConvertFrom-Json
if (-not (Test-Path -LiteralPath $database.credentialsFile)) { throw 'The database acceptance credentials file is missing.' }
$credentials = Get-Content -LiteralPath $database.credentialsFile -Raw | ConvertFrom-Json
$mssqlTargetPort = [int]($database.mssqlDirect -split ':')[-1]
$pgTargetPort = [int]($database.postgresDirect -split ':')[-1]
foreach ($name in @($database.mssqlContainer, $database.postgresContainer)) {
    $status = & docker inspect --format '{{.State.Running}}' $name 2>$null
    if ($LASTEXITCODE -ne 0 -or $status -ne 'true') { throw "Database container $name is not running." }
}

$runDir = Join-Path $repoRoot ('.local/database-peer-acceptance/' + [Guid]::NewGuid().ToString('N'))
foreach ($directory in @('clients', 'visited', 'caller', 'tls', 'server-bin', 'agent-bin')) { New-Item -ItemType Directory -Path (Join-Path $runDir $directory) -Force | Out-Null }
$reserved = [System.Collections.Generic.HashSet[int]]::new()
function Get-FreePort {
    do {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
        $listener.Stop()
    } until ($reserved.Add($port))
    return $port
}
function New-Secret {
    $bytes = [byte[]]::new(32)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($bytes)
}
function Invoke-OpenSsl([string]$name, [string[]]$arguments) {
    $process = Start-Process -FilePath $script:openssl -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $runDir "$name.stdout.log") -RedirectStandardError (Join-Path $runDir "$name.stderr.log")
    if ($process.ExitCode -ne 0) { throw "OpenSSL $name failed; inspect $runDir/$name.stderr.log" }
}
function Assert-Equal([string]$name, [string]$actual, [string]$expected) {
    if ($actual.Trim() -ne $expected) { throw "$name expected '$expected', got '$actual'." }
    Write-Output "PASS $name = $expected"
}
function Invoke-Mssql([string]$query) {
    $output = & $script:sqlcmd -S "127.0.0.1,$script:mssqlPeerPort" -U sa -d master -C -b -h -1 -W -Q "SET NOCOUNT ON; $query" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "MSSQL via peer failed: $output" }
    return (($output | Where-Object { "$_".Trim() -ne '' }) -join "`n").Trim()
}
function Invoke-Pg([string]$query) {
    $output = & docker run --rm -e PGPASSWORD postgres:17 psql -X -q -A -t -v ON_ERROR_STOP=1 -h host.docker.internal -p $script:pgPeerPort -U relaylink_test -d relaylink_acceptance -c $query 2>&1
    if ($LASTEXITCODE -ne 0) { throw "PostgreSQL via peer failed: $output" }
    return (($output | Where-Object { "$_".Trim() -ne '' }) -join "`n").Trim()
}

$controlPort = Get-FreePort
$dataPort = Get-FreePort
$dashboardPort = Get-FreePort
$agentDashboardPort = Get-FreePort
$privateMssqlPort = Get-FreePort
$privatePgPort = Get-FreePort
$openssl = 'C:\Program Files\Git\usr\bin\openssl.exe'
$sqlcmd = 'C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\SQLCMD.EXE'
if (-not (Test-Path -LiteralPath $openssl) -or -not (Test-Path -LiteralPath $sqlcmd)) { throw 'Git OpenSSL and SQLCMD.EXE are required.' }
$env:SQLCMDPASSWORD = $credentials.mssqlPassword
$env:PGPASSWORD = $credentials.postgresPassword

$rootCa = Join-Path $runDir 'tls/root-ca.pem'
$rootKey = Join-Path $runDir 'tls/root-key.pem'
$serverCert = Join-Path $runDir 'tls/server-cert.pem'
$serverKey = Join-Path $runDir 'tls/server-key.pem'
$serverCsr = Join-Path $runDir 'tls/server.csr'
Invoke-OpenSsl 'root' @('req', '-x509', '-newkey', 'rsa:2048', '-sha256', '-nodes', '-days', '1', '-subj', '/CN=RelayLinkTestRoot', '-addext', 'basicConstraints=critical,CA:TRUE', '-addext', 'keyUsage=critical,keyCertSign,cRLSign', '-keyout', $rootKey, '-out', $rootCa)
Invoke-OpenSsl 'server-request' @('req', '-newkey', 'rsa:2048', '-sha256', '-nodes', '-subj', '/CN=127.0.0.1', '-addext', 'subjectAltName=IP:127.0.0.1', '-addext', 'extendedKeyUsage=serverAuth', '-addext', 'keyUsage=digitalSignature', '-keyout', $serverKey, '-out', $serverCsr)
Invoke-OpenSsl 'server-sign' @('x509', '-req', '-in', $serverCsr, '-CA', $rootCa, '-CAkey', $rootKey, '-CAcreateserial', '-days', '1', '-sha256', '-copy_extensions', 'copy', '-out', $serverCert)
Invoke-OpenSsl 'server-verify' @('verify', '-CAfile', $rootCa, '-purpose', 'sslserver', $serverCert)

Copy-Item -Path (Join-Path $repoRoot 'src/RelayLink.Server/bin/Debug/net10.0/*') -Destination (Join-Path $runDir 'server-bin') -Recurse -Force
Copy-Item -Path (Join-Path $repoRoot 'src/RelayLink.Agent/bin/Debug/net10.0/*') -Destination (Join-Path $runDir 'agent-bin') -Recurse -Force
$serverDll = Join-Path $runDir 'server-bin/RelayLink.Server.dll'
$agentDll = Join-Path $runDir 'agent-bin/RelayLink.Agent.dll'
$visitedSecret = New-Secret
$callerSecret = New-Secret
$mssqlAccessSecret = New-Secret
$pgAccessSecret = New-Secret
$caBase64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($rootCa))
$reconnect = @{ initialDelaySeconds = 1; maxDelaySeconds = 2; permanentErrorDelaySeconds = 2 }
$visitedAgentPath = Join-Path $runDir 'visited/agent.json'
$callerAgentPath = Join-Path $runDir 'caller/agent.json'
@{ serverHost = '127.0.0.1'; serverPort = $controlPort; dataPort = $dataPort; useTls = $true; clientId = 'db-visited'; secret = $visitedSecret; trustedCaPemBase64 = $caBase64; dashboardPort = 0; reconnect = $reconnect } |
    ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $visitedAgentPath -Encoding utf8
@{ serverHost = '127.0.0.1'; serverPort = $controlPort; dataPort = $dataPort; useTls = $true; clientId = 'db-caller'; secret = $callerSecret; trustedCaPemBase64 = $caBase64; dashboardPort = $agentDashboardPort; reconnect = $reconnect } |
    ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $callerAgentPath -Encoding utf8
$fingerprint = (& dotnet $agentDll --config $visitedAgentPath --show-e2e-fingerprint).Trim()
if ($LASTEXITCODE -ne 0 -or $fingerprint -notmatch '^[0-9A-F]{64}$') { throw 'Could not create the visited Agent identity fingerprint.' }

$visitedClient = @{
    schemaVersion = 1; clientId = 'db-visited'; displayName = 'Database target Agent'; enabled = $true; secret = $visitedSecret; maxConnections = 50; maxPendingConnections = 30
    channels = @(
        @{ channelId = 'mssql'; displayName = 'Private MSSQL'; enabled = $true; authorizedClientsOnly = $true; accessSecret = $mssqlAccessSecret; e2eCertificateSha256 = $fingerprint; listenAddress = '127.0.0.1'; listenPort = $privateMssqlPort; targetHost = '127.0.0.1'; targetPort = $mssqlTargetPort; maxConnections = 20; targetConnectTimeoutSeconds = 10 },
        @{ channelId = 'postgres'; displayName = 'Private PostgreSQL'; enabled = $true; authorizedClientsOnly = $true; accessSecret = $pgAccessSecret; e2eCertificateSha256 = $fingerprint; listenAddress = '127.0.0.1'; listenPort = $privatePgPort; targetHost = '127.0.0.1'; targetPort = $pgTargetPort; maxConnections = 20; targetConnectTimeoutSeconds = 10 }
    )
    outboundMappings = @()
}
$callerClient = @{
    schemaVersion = 1; clientId = 'db-caller'; displayName = 'Database caller Agent'; enabled = $true; secret = $callerSecret; maxConnections = 50; maxPendingConnections = 30; channels = @()
    outboundMappings = @(
        @{ mappingId = 'mssql-e2e'; enabled = $true; localAddress = '127.0.0.1'; targetClientId = 'db-visited'; targetChannelId = 'mssql'; accessSecret = $mssqlAccessSecret; targetCertificateSha256 = $fingerprint },
        @{ mappingId = 'pg-e2e'; enabled = $true; localAddress = '127.0.0.1'; targetClientId = 'db-visited'; targetChannelId = 'postgres'; accessSecret = $pgAccessSecret; targetCertificateSha256 = $fingerprint }
    )
}
$visitedClient | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $runDir 'clients/db-visited.json') -Encoding utf8
$callerClient | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $runDir 'clients/db-caller.json') -Encoding utf8
$serverConfig = @{
    schemaVersion = 1
    tunnel = @{ listenAddress = '127.0.0.1'; port = $controlPort; dataPort = $dataPort; tlsEnabled = $true; certificatePemPath = $serverCert; privateKeyPemPath = $serverKey; trustedCaPemPath = $rootCa; handshakeTimeoutSeconds = 10; heartbeatIntervalSeconds = 5; heartbeatTimeoutSeconds = 20 }
    dashboard = @{ listenAddress = '127.0.0.1'; port = $dashboardPort; refreshSeconds = 1; admin = @{ username = 'admin'; passwordHash = 'PBKDF2-SHA256$210000$AA==$AA=='; sessionLifetimeMinutes = 60 } }
    clientsDirectory = (Join-Path $runDir 'clients')
    limits = @{ maxConnections = 100; maxPendingConnections = 100; maxUnauthenticatedConnections = 100; maxChannelsPerClient = 10; openTimeoutSeconds = 20; blockedWriteTimeoutSeconds = 120; halfCloseDrainTimeoutSeconds = 300 }
}
$serverPath = Join-Path $runDir 'server.json'
$serverConfig | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $serverPath -Encoding utf8
& dotnet $serverDll --config $serverPath --check-config
if ($LASTEXITCODE -ne 0) { throw 'Peer server configuration validation failed.' }

$processes = @()
function Start-RelayProcess([string]$name, [string]$dll, [string]$config) {
    $process = Start-Process -FilePath 'dotnet' -ArgumentList @($dll, '--config', $config) -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runDir "$name.stdout.log") -RedirectStandardError (Join-Path $runDir "$name.stderr.log")
    $script:processes += @{ name = $name; pid = $process.Id }
}
try {
    Start-RelayProcess 'server' $serverDll $serverPath
    Start-RelayProcess 'visited' $agentDll $visitedAgentPath
    Start-RelayProcess 'caller' $agentDll $callerAgentPath
    $portsPath = Join-Path $runDir 'caller/db-caller.ports.json'
    $ready = $false
    for ($attempt = 0; $attempt -lt 200; $attempt++) {
        Start-Sleep -Milliseconds 200
        try {
            $overview = Invoke-RestMethod -Uri "http://127.0.0.1:$dashboardPort/api/v1/overview" -TimeoutSec 1
            if ($overview.clientsOnline -eq 2 -and (Test-Path -LiteralPath $portsPath)) {
                $ports = Get-Content -LiteralPath $portsPath -Raw | ConvertFrom-Json
                if ($ports.'mssql-e2e' -and $ports.'pg-e2e') { $ready = $true; break }
            }
        } catch { }
        if (@($processes | Where-Object { -not (Get-Process -Id $_.pid -ErrorAction SilentlyContinue) }).Count -gt 0) { break }
    }
    if (-not $ready) { throw "Peer Server/Agents did not become ready; inspect $runDir/*.log" }
    $mssqlPeerPort = [int]$ports.'mssql-e2e'
    $pgPeerPort = [int]$ports.'pg-e2e'
    Write-Output "PASS two TLS-authenticated Agents online; peer ports MSSQL=$mssqlPeerPort PostgreSQL=$pgPeerPort"

    $forbidden = [System.Net.Sockets.TcpClient]::new()
    try {
        try { $forbidden.Connect('127.0.0.1', $privateMssqlPort); throw 'Private MSSQL cloud proxy port unexpectedly accepted a connection.' }
        catch [System.Net.Sockets.SocketException] { Write-Output 'PASS private target channel has no anonymous cloud listener' }
    } finally { $forbidden.Dispose() }

    Assert-Equal 'MSSQL E2E SELECT count and sum' (Invoke-Mssql 'SELECT COUNT(*), SUM(amount) FROM dbo.rl_acceptance_orders') '3 39'
    Assert-Equal 'MSSQL E2E JOIN' (Invoke-Mssql 'SELECT c.name, SUM(o.amount) FROM dbo.rl_acceptance_customers c JOIN dbo.rl_acceptance_orders o ON o.customer_id = c.id GROUP BY c.name ORDER BY c.name') "Ana 30`nBo 9"
    Assert-Equal 'MSSQL E2E transaction rollback' (Invoke-Mssql 'BEGIN TRANSACTION; INSERT INTO dbo.rl_acceptance_orders VALUES (99,1,100); SELECT COUNT(*) FROM dbo.rl_acceptance_orders; ROLLBACK TRANSACTION; SELECT COUNT(*) FROM dbo.rl_acceptance_orders') "4`n3"
    Assert-Equal 'PostgreSQL E2E SELECT count and sum' (Invoke-Pg 'SELECT COUNT(*), SUM(amount) FROM rl_acceptance_orders') '3|39'
    Assert-Equal 'PostgreSQL E2E JOIN' (Invoke-Pg 'SELECT c.name, SUM(o.amount) FROM rl_acceptance_customers c JOIN rl_acceptance_orders o ON o.customer_id = c.id GROUP BY c.name ORDER BY c.name') "Ana|30`nBo|9"
    Assert-Equal 'PostgreSQL E2E transaction rollback' (Invoke-Pg 'BEGIN; INSERT INTO rl_acceptance_orders VALUES (99,1,100); SELECT COUNT(*) FROM rl_acceptance_orders; ROLLBACK; SELECT COUNT(*) FROM rl_acceptance_orders') "4`n3"

    $overview = Invoke-RestMethod -Uri "http://127.0.0.1:$dashboardPort/api/v1/overview"
    if ($overview.bytesToTarget -ne 0 -or $overview.bytesToCaller -ne 0 -or $overview.peerCiphertextToTarget -le 0 -or $overview.peerCiphertextToCaller -le 0) {
        throw 'Server traffic metrics did not show ciphertext-only peer relay.'
    }
    Write-Output "PASS server ordinary payload bytes=0; peer ciphertext bytes=$($overview.peerCiphertextToTarget)/$($overview.peerCiphertextToCaller)"
    $state = [pscustomobject]@{ runDirectory = $runDir; databaseStateFile = $DatabaseStatePath; serverPid = ($processes | Where-Object name -eq server).pid; visitedPid = ($processes | Where-Object name -eq visited).pid; callerPid = ($processes | Where-Object name -eq caller).pid; mssqlPeer = "127.0.0.1:$mssqlPeerPort"; postgresPeer = "127.0.0.1:$pgPeerPort"; dashboardUrl = "http://127.0.0.1:$dashboardPort/"; callerDashboardUrl = "http://127.0.0.1:$agentDashboardPort/"; peerCiphertextToTarget = $overview.peerCiphertextToTarget; peerCiphertextToCaller = $overview.peerCiphertextToCaller }
    $state | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runDir 'state.json') -Encoding utf8
    $state | ConvertTo-Json -Depth 6
} catch {
    foreach ($entry in $processes) { Stop-Process -Id $entry.pid -ErrorAction SilentlyContinue }
    throw
} finally {
    Remove-Item Env:SQLCMDPASSWORD, Env:PGPASSWORD -ErrorAction SilentlyContinue
}
