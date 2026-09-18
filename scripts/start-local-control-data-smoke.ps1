param([int]$Connections = 16, [int]$BytesPerConnection = 1048576)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runDir = Join-Path $repoRoot ('.local/control-data-smoke/' + [Guid]::NewGuid().ToString('N'))
$statePath = Join-Path $runDir 'state.json'
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

$controlPort = Get-FreePort
$dataPort = Get-FreePort
$proxyPort = Get-FreePort
$targetPort = Get-FreePort
$dashboardPort = Get-FreePort
$secretBytes = [byte[]]::new(32)
$random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $random.GetBytes($secretBytes) } finally { $random.Dispose() }
$secret = [Convert]::ToBase64String($secretBytes)
$serverConfig = @{
    schemaVersion = 1
    tunnel = @{ listenAddress = '127.0.0.1'; port = $controlPort; dataPort = $dataPort; tlsEnabled = $false; certificatePemPath = ''; privateKeyPemPath = ''; handshakeTimeoutSeconds = 10; heartbeatIntervalSeconds = 5; heartbeatTimeoutSeconds = 20 }
    dashboard = @{ listenAddress = '127.0.0.1'; port = $dashboardPort; refreshSeconds = 1; admin = @{ username = 'admin'; passwordHash = 'PBKDF2-SHA256$210000$AA==$AA=='; sessionLifetimeMinutes = 60 } }
    clientsDirectory = (Join-Path $runDir 'clients')
    limits = @{ maxConnections = 100; maxPendingConnections = 100; maxUnauthenticatedConnections = 100; maxChannelsPerClient = 10; openTimeoutSeconds = 10; blockedWriteTimeoutSeconds = 120; halfCloseDrainTimeoutSeconds = 300 }
}
$clientConfig = @{
    schemaVersion = 1; clientId = 'smoke-agent'; displayName = 'Local smoke agent'; enabled = $true; secret = $secret; maxConnections = 100; maxPendingConnections = 100
    channels = @(@{ channelId = 'echo'; displayName = 'Echo'; enabled = $true; listenAddress = '127.0.0.1'; listenPort = $proxyPort; targetHost = '127.0.0.1'; targetPort = $targetPort; maxConnections = 100; targetConnectTimeoutSeconds = 5 })
}
$agentConfig = @{
    serverHost = '127.0.0.1'; serverPort = $controlPort; dataPort = $dataPort; useTls = $false; clientId = 'smoke-agent'; secret = $secret; trustedCaPemPath = $null; dashboardPort = 0
    reconnect = @{ initialDelaySeconds = 1; maxDelaySeconds = 2; permanentErrorDelaySeconds = 2 }
}
$serverPath = Join-Path $runDir 'server.json'
$clientPath = Join-Path $runDir 'clients/smoke-agent.json'
$agentPath = Join-Path $runDir 'agent.json'
$serverConfig | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $serverPath -Encoding utf8
$clientConfig | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $clientPath -Encoding utf8
$agentConfig | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $agentPath -Encoding utf8

function Copy-Runtime([string]$source, [string]$destination) {
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -Path (Join-Path $repoRoot "$source/*") -Destination $destination -Recurse -Force
}
Copy-Runtime 'tools/RelayLink.SimulatedTarget/bin/Debug/net10.0' (Join-Path $runDir 'target-bin')
Copy-Runtime 'src/RelayLink.Server/bin/Debug/net10.0' (Join-Path $runDir 'server-bin')
Copy-Runtime 'src/RelayLink.Agent/bin/Debug/net10.0' (Join-Path $runDir 'agent-bin')
Copy-Runtime 'tools/RelayLink.SimulatedCaller/bin/Debug/net10.0' (Join-Path $runDir 'caller-bin')
$targetDll = Join-Path $runDir 'target-bin/RelayLink.SimulatedTarget.dll'
$serverDll = Join-Path $runDir 'server-bin/RelayLink.Server.dll'
$agentDll = Join-Path $runDir 'agent-bin/RelayLink.Agent.dll'
$callerDll = Join-Path $runDir 'caller-bin/RelayLink.SimulatedCaller.dll'
$processes = @()
function Start-SmokeProcess([string]$name, [string[]]$arguments) {
    $process = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runDir "$name.stdout.log") -RedirectStandardError (Join-Path $runDir "$name.stderr.log")
    $script:processes += @{ name = $name; pid = $process.Id }
    return $process
}
function Assert-RejectedFrame([int]$port, [byte]$type, [object]$body) {
    $socket = [System.Net.Sockets.TcpClient]::new()
    try {
        $socket.Connect('127.0.0.1', $port)
        $socket.ReceiveTimeout = 3000
        $stream = $socket.GetStream()
        $payload = [System.Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Compress))
        $length = $payload.Length
        $header = [byte[]]@([byte][char]'N', [byte][char]'T', [byte][char]'P', [byte][char]'1', 2, $type, 0, 0,
            [byte](($length -shr 24) -band 255), [byte](($length -shr 16) -band 255), [byte](($length -shr 8) -band 255), [byte]($length -band 255))
        $stream.Write($header, 0, $header.Length)
        $stream.Write($payload, 0, $payload.Length)
        $reply = [byte[]]::new(12)
        $read = 0
        while ($read -lt $reply.Length) {
            $part = $stream.Read($reply, $read, $reply.Length - $read)
            if ($part -eq 0) { throw "Port $port closed without an error response." }
            $read += $part
        }
        if ($reply[5] -ne 14) { throw "Port $port accepted an invalid initial frame type $type." }
    } finally { $socket.Dispose() }
}
try {
    Start-SmokeProcess 'target' @($targetDll, '--port', "$targetPort") | Out-Null
    Start-SmokeProcess 'server' @($serverDll, '--config', $serverPath) | Out-Null
    Start-SmokeProcess 'agent' @($agentDll, '--config', $agentPath) | Out-Null
    $ready = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        Start-Sleep -Milliseconds 200
        try {
            $overview = Invoke-RestMethod -Uri "http://127.0.0.1:$dashboardPort/api/v1/overview" -TimeoutSec 1
            if ($overview.clientsOnline -eq 1) { $ready = $true; break }
        } catch { }
        if (@($processes | Where-Object { -not (Get-Process -Id $_.pid -ErrorAction SilentlyContinue) }).Count -gt 0) { break }
    }
    if (-not $ready) { throw "Server and Agent did not become ready; inspect $runDir/*.log." }
    $bogusBind = @{ sessionId = [Guid]::Empty; connectionId = [Guid]::NewGuid(); channelId = 'echo'; token = 'invalid' }
    Assert-RejectedFrame $controlPort 10 $bogusBind
    Assert-RejectedFrame $dataPort 10 $bogusBind
    Assert-RejectedFrame $dataPort 1 @{ clientId = 'smoke-agent'; secret = 'invalid'; agentVersion = 'smoke' }
    Write-Output 'SMOKE_AUTH_PASS control/data entry points reject unauthenticated or wrong-type connections'
    $processes | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath -Encoding utf8
    & dotnet $callerDll --host 127.0.0.1 --port $proxyPort --connections $Connections --bytes $BytesPerConnection --half-close true
    if ($LASTEXITCODE -ne 0) { throw "Simulated caller failed with exit code $LASTEXITCODE" }
    [pscustomobject]@{ runDirectory = $runDir; controlPort = $controlPort; dataPort = $dataPort; proxyPort = $proxyPort; targetPort = $targetPort; dashboardUrl = "http://127.0.0.1:$dashboardPort/"; processes = $processes } | ConvertTo-Json -Depth 5
} catch {
    foreach ($entry in $processes) { Stop-Process -Id $entry.pid -ErrorAction SilentlyContinue }
    throw
}
