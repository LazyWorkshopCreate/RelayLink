param([int]$DatabaseReadyTimeoutSeconds = 120)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runDir = Join-Path $repoRoot ('.local/database-acceptance/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $runDir 'clients') -Force | Out-Null

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
function New-TestPassword {
    return 'Rl1!' + [Guid]::NewGuid().ToString('N')
}
function Assert-Equal([string]$label, [string]$actual, [string]$expected) {
    if ($actual.Trim() -ne $expected) { throw "$label expected '$expected' but got '$actual'." }
    Write-Output "PASS $label = $expected"
}
function Invoke-SqlServer([string]$query) {
    $output = & $script:sqlcmd -S "127.0.0.1,$script:mssqlProxyPort" -U sa -d master -C -b -h -1 -W -Q "SET NOCOUNT ON; $query" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "SQL Server query failed: $output" }
    return (($output | Where-Object { "$_".Trim() -ne '' }) -join "`n").Trim()
}
function Invoke-Postgres([string]$query) {
    $output = & docker run --rm -e PGPASSWORD postgres:17 psql -X -q -A -t -v ON_ERROR_STOP=1 -h host.docker.internal -p $script:pgProxyPort -U relaylink_test -d relaylink_acceptance -c $query 2>&1
    if ($LASTEXITCODE -ne 0) { throw "PostgreSQL query failed: $output" }
    return (($output | Where-Object { "$_".Trim() -ne '' }) -join "`n").Trim()
}

$controlPort = Get-FreePort
$dataPort = Get-FreePort
$dashboardPort = Get-FreePort
$mssqlTargetPort = Get-FreePort
$pgTargetPort = Get-FreePort
$mssqlProxyPort = Get-FreePort
$pgProxyPort = Get-FreePort
$mssqlContainer = 'relaylink-acceptance-mssql-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$pgContainer = 'relaylink-acceptance-pg-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$env:MSSQL_SA_PASSWORD = New-TestPassword
$env:POSTGRES_PASSWORD = New-TestPassword
$env:PGPASSWORD = $env:POSTGRES_PASSWORD
$env:SQLCMDPASSWORD = $env:MSSQL_SA_PASSWORD
$credentialsPath = Join-Path $runDir 'test-credentials.json'
@{ mssqlUser = 'sa'; mssqlPassword = $env:MSSQL_SA_PASSWORD; postgresUser = 'relaylink_test'; postgresPassword = $env:POSTGRES_PASSWORD; postgresDatabase = 'relaylink_acceptance' } | ConvertTo-Json | Set-Content -LiteralPath $credentialsPath -Encoding utf8
$sqlcmd = 'C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\SQLCMD.EXE'
if (-not (Test-Path -LiteralPath $sqlcmd)) { throw 'SQLCMD.EXE is required for the MSSQL acceptance caller.' }

$secretBytes = [byte[]]::new(32)
$random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $random.GetBytes($secretBytes) } finally { $random.Dispose() }
$secret = [Convert]::ToBase64String($secretBytes)
$serverConfig = @{
    schemaVersion = 1
    tunnel = @{ listenAddress = '127.0.0.1'; port = $controlPort; dataPort = $dataPort; tlsEnabled = $false; certificatePemPath = ''; privateKeyPemPath = ''; handshakeTimeoutSeconds = 10; heartbeatIntervalSeconds = 5; heartbeatTimeoutSeconds = 20 }
    dashboard = @{ listenAddress = '127.0.0.1'; port = $dashboardPort; refreshSeconds = 1; admin = @{ username = 'admin'; passwordHash = 'PBKDF2-SHA256$210000$AA==$AA=='; sessionLifetimeMinutes = 60 } }
    clientsDirectory = (Join-Path $runDir 'clients')
    limits = @{ maxConnections = 100; maxPendingConnections = 100; maxUnauthenticatedConnections = 100; maxChannelsPerClient = 10; openTimeoutSeconds = 15; blockedWriteTimeoutSeconds = 120; halfCloseDrainTimeoutSeconds = 300 }
}
$clientConfig = @{
    schemaVersion = 1; clientId = 'database-agent'; displayName = 'Local database acceptance'; enabled = $true; secret = $secret; maxConnections = 100; maxPendingConnections = 100
    channels = @(
        @{ channelId = 'mssql'; displayName = 'SQL Server'; enabled = $true; listenAddress = '127.0.0.1'; listenPort = $mssqlProxyPort; targetHost = '127.0.0.1'; targetPort = $mssqlTargetPort; maxConnections = 100; targetConnectTimeoutSeconds = 10 },
        @{ channelId = 'postgres'; displayName = 'PostgreSQL'; enabled = $true; listenAddress = '127.0.0.1'; listenPort = $pgProxyPort; targetHost = '127.0.0.1'; targetPort = $pgTargetPort; maxConnections = 100; targetConnectTimeoutSeconds = 10 }
    )
}
$agentConfig = @{
    serverHost = '127.0.0.1'; serverPort = $controlPort; dataPort = $dataPort; useTls = $false; clientId = 'database-agent'; secret = $secret; dashboardPort = 0
    reconnect = @{ initialDelaySeconds = 1; maxDelaySeconds = 2; permanentErrorDelaySeconds = 2 }
}
$serverPath = Join-Path $runDir 'server.json'
$clientPath = Join-Path $runDir 'clients/database-agent.json'
$agentPath = Join-Path $runDir 'agent.json'
$serverConfig | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $serverPath -Encoding utf8
$clientConfig | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $clientPath -Encoding utf8
$agentConfig | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $agentPath -Encoding utf8

$processes = @()
$startedContainers = @()
function Start-RelayProcess([string]$name, [string]$dll, [string]$config) {
    $process = Start-Process -FilePath 'dotnet' -ArgumentList @($dll, '--config', $config) -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runDir "$name.stdout.log") -RedirectStandardError (Join-Path $runDir "$name.stderr.log")
    $script:processes += @{ name = $name; pid = $process.Id }
}
try {
    $mssqlId = & docker run -d --name $mssqlContainer -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD -p "127.0.0.1:${mssqlTargetPort}:1433" mcr.microsoft.com/mssql/server:2022-latest
    if ($LASTEXITCODE -ne 0) { throw 'Could not start MSSQL container.' }
    $startedContainers += $mssqlContainer
    $pgId = & docker run -d --name $pgContainer -e POSTGRES_PASSWORD -e POSTGRES_USER=relaylink_test -e POSTGRES_DB=relaylink_acceptance -p "127.0.0.1:${pgTargetPort}:5432" postgres:17
    if ($LASTEXITCODE -ne 0) { throw 'Could not start PostgreSQL container.' }
    $startedContainers += $pgContainer
    Write-Output "Containers: MSSQL=$mssqlContainer PostgreSQL=$pgContainer"
    $serverDll = Join-Path $repoRoot 'src/RelayLink.Server/bin/Debug/net10.0/RelayLink.Server.dll'
    $agentDll = Join-Path $repoRoot 'src/RelayLink.Agent/bin/Debug/net10.0/RelayLink.Agent.dll'
    if (-not (Test-Path $serverDll) -or -not (Test-Path $agentDll)) { throw 'Build RelayLink.slnx before running acceptance.' }
    Start-RelayProcess 'server' $serverDll $serverPath
    Start-RelayProcess 'agent' $agentDll $agentPath
    $ready = $false
    for ($attempt = 0; $attempt -lt 150; $attempt++) {
        Start-Sleep -Milliseconds 200
        try {
            $overview = Invoke-RestMethod -Uri "http://127.0.0.1:$dashboardPort/api/v1/overview" -TimeoutSec 1
            if ($overview.clientsOnline -eq 1 -and $overview.channelsAvailable -eq 2) { $ready = $true; break }
        } catch { }
        if (@($processes | Where-Object { -not (Get-Process -Id $_.pid -ErrorAction SilentlyContinue) }).Count -gt 0) { break }
    }
    if (-not $ready) { throw "Server/Agent did not become ready; inspect $runDir/*.log" }
    Write-Output 'PASS RelayLink Server/Agent online, two independent channels available'

    $mssqlReady = $false
    $pgReady = $false
    $deadline = (Get-Date).AddSeconds($DatabaseReadyTimeoutSeconds)
    $ErrorActionPreference = 'Continue'
    while ((Get-Date) -lt $deadline -and (-not $mssqlReady -or -not $pgReady)) {
        if (-not $mssqlReady) {
            $output = & $sqlcmd -S "127.0.0.1,$mssqlProxyPort" -U sa -d master -C -b -h -1 -W -Q 'SET NOCOUNT ON; SELECT 1' 2>$null
            $mssqlReady = $LASTEXITCODE -eq 0 -and (($output -join ' ').Trim() -eq '1')
        }
        if (-not $pgReady) {
            $output = & docker run --rm -e PGPASSWORD postgres:17 psql -X -A -t -v ON_ERROR_STOP=1 -h host.docker.internal -p $pgProxyPort -U relaylink_test -d relaylink_acceptance -c 'SELECT 1' 2>$null
            $pgReady = $LASTEXITCODE -eq 0 -and (($output -join ' ').Trim() -eq '1')
        }
        if (-not $mssqlReady -or -not $pgReady) { Start-Sleep -Seconds 2 }
    }
    $ErrorActionPreference = 'Stop'
    if (-not $mssqlReady -or -not $pgReady) { throw "Database readiness failed: MSSQL=$mssqlReady PostgreSQL=$pgReady; inspect container logs and $runDir/*.log" }
    Write-Output 'PASS both real database clients connected through RelayLink proxy ports'

    [void](Invoke-SqlServer "CREATE TABLE dbo.rl_acceptance_customers (id int PRIMARY KEY, name nvarchar(30) NOT NULL); CREATE TABLE dbo.rl_acceptance_orders (id int PRIMARY KEY, customer_id int NOT NULL REFERENCES dbo.rl_acceptance_customers(id), amount int NOT NULL); INSERT INTO dbo.rl_acceptance_customers VALUES (1,N'Ana'),(2,N'Bo'); INSERT INTO dbo.rl_acceptance_orders VALUES (1,1,10),(2,1,20),(3,2,7);")
    [void](Invoke-Postgres "CREATE TABLE rl_acceptance_customers (id integer PRIMARY KEY, name text NOT NULL); CREATE TABLE rl_acceptance_orders (id integer PRIMARY KEY, customer_id integer NOT NULL REFERENCES rl_acceptance_customers(id), amount integer NOT NULL); INSERT INTO rl_acceptance_customers VALUES (1,'Ana'),(2,'Bo'); INSERT INTO rl_acceptance_orders VALUES (1,1,10),(2,1,20),(3,2,7);")
    foreach ($engine in @('MSSQL', 'PostgreSQL')) {
        $invoke = if ($engine -eq 'MSSQL') { ${function:Invoke-SqlServer} } else { ${function:Invoke-Postgres} }
        $prefix = if ($engine -eq 'MSSQL') { 'dbo.rl_acceptance_' } else { 'rl_acceptance_' }
        Assert-Equal "$engine SELECT count" (& $invoke "SELECT COUNT(*) FROM ${prefix}orders") '3'
        $expectedJoin = if ($engine -eq 'MSSQL') { "Ana 30`nBo 7" } else { "Ana|30`nBo|7" }
        Assert-Equal "$engine JOIN + GROUP BY" (& $invoke "SELECT c.name, SUM(o.amount) FROM ${prefix}customers c JOIN ${prefix}orders o ON o.customer_id = c.id GROUP BY c.name ORDER BY c.name") $expectedJoin
        [void](& $invoke "UPDATE ${prefix}orders SET amount = 9 WHERE id = 3")
        Assert-Equal "$engine UPDATE + SUM" (& $invoke "SELECT SUM(amount) FROM ${prefix}orders") '39'
        [void](& $invoke "BEGIN TRANSACTION; UPDATE ${prefix}orders SET amount = 999 WHERE id = 1; ROLLBACK TRANSACTION")
        Assert-Equal "$engine ROLLBACK" (& $invoke "SELECT SUM(amount) FROM ${prefix}orders") '39'
        [void](& $invoke "INSERT INTO ${prefix}orders VALUES (4,2,11)")
        Assert-Equal "$engine INSERT" (& $invoke "SELECT SUM(amount) FROM ${prefix}orders") '50'
        [void](& $invoke "DELETE FROM ${prefix}orders WHERE id = 4")
        Assert-Equal "$engine DELETE" (& $invoke "SELECT COUNT(*) FROM ${prefix}orders") '3'
    }
    $state = [pscustomobject]@{ runDirectory = $runDir; serverPid = ($processes | Where-Object name -eq server).pid; agentPid = ($processes | Where-Object name -eq agent).pid; mssqlContainer = $mssqlContainer; postgresContainer = $pgContainer; mssqlDirect = "127.0.0.1:$mssqlTargetPort"; postgresDirect = "127.0.0.1:$pgTargetPort"; mssqlProxy = "127.0.0.1:$mssqlProxyPort"; postgresProxy = "127.0.0.1:$pgProxyPort"; dashboardUrl = "http://127.0.0.1:$dashboardPort/"; credentialsFile = $credentialsPath }
    $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runDir 'state.json') -Encoding utf8
    $state | ConvertTo-Json -Depth 5
} catch {
    foreach ($entry in $processes) { Stop-Process -Id $entry.pid -ErrorAction SilentlyContinue }
    foreach ($container in $startedContainers) { & docker rm -f $container *> $null }
    throw
} finally {
    Remove-Item Env:MSSQL_SA_PASSWORD, Env:POSTGRES_PASSWORD, Env:PGPASSWORD, Env:SQLCMDPASSWORD -ErrorAction SilentlyContinue
}
