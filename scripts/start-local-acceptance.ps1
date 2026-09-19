[CmdletBinding()]
param(
    [ValidateRange(1, 64)][int]$ConnectionsPerChannel = 8,
    [ValidateRange(1, 8388608)][int]$BytesPerConnection = 1048576
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$runDirectory = Join-Path $repositoryRoot ('.local\local-acceptance\' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $runDirectory 'clients') -Force | Out-Null
$reservedPorts = [System.Collections.Generic.HashSet[int]]::new()
function Get-FreePort {
    do {
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
        $listener.Stop()
    } until ($reservedPorts.Add($port))
    return $port
}
function New-Secret {
    $bytes = [byte[]]::new(32)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    return [Convert]::ToBase64String($bytes)
}
function Save-Json([object]$value, [string]$path) {
    $value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding utf8
}
function Publish-Component([string]$project, [string]$destination) {
    & dotnet publish (Join-Path $repositoryRoot $project) --configuration Release --self-contained false --output $destination -p:SkipAdminWebBuild=true
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
}
function Start-LocalProcess([string]$name, [string]$dll, [string]$arguments) {
    $commandLine = '"{0}" {1}' -f $dll, $arguments
    $process = Start-Process -FilePath 'dotnet' -ArgumentList $commandLine -WorkingDirectory $repositoryRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $runDirectory "$name.stdout.log") -RedirectStandardError (Join-Path $runDirectory "$name.stderr.log")
    $script:processes += [pscustomobject]@{ name = $name; pid = $process.Id }
    return $process
}
function Invoke-Caller([int]$port, [string]$tag, [int]$connections, [int]$bytes) {
    & dotnet $callerDll --host 127.0.0.1 --port $port --connections $connections --bytes $bytes --half-close true --expected-tag $tag
    if ($LASTEXITCODE -ne 0) { throw "Caller failed on port $port ($tag)." }
}
function Assert-RejectedFrame([int]$port, [byte]$type) {
    $socket = [Net.Sockets.TcpClient]::new()
    try {
        $socket.Connect('127.0.0.1', $port)
        $socket.ReceiveTimeout = 3000
        $payload = [Text.Encoding]::UTF8.GetBytes('{}')
        $header = [byte[]]@([byte][char]'N', [byte][char]'T', [byte][char]'P', [byte][char]'1', 2, $type, 0, 0, 0, 0, 0, 2)
        $stream = $socket.GetStream()
        $stream.Write($header, 0, $header.Length)
        $stream.Write($payload, 0, $payload.Length)
        $reply = [byte[]]::new(12)
        $read = 0
        while ($read -lt $reply.Length) {
            $count = $stream.Read($reply, $read, $reply.Length - $read)
            if ($count -eq 0) { throw "Port $port closed without a rejection frame." }
            $read += $count
        }
        if ($reply[5] -ne 14) { throw "Port $port did not reject frame type $type." }
    }
    finally { $socket.Dispose() }
}

$controlPort = Get-FreePort
$dataPort = Get-FreePort
$dashboardPort = Get-FreePort
$targetPorts = @(Get-FreePort; Get-FreePort; Get-FreePort)
$proxyPorts = @(Get-FreePort; Get-FreePort; Get-FreePort)
$secretA = New-Secret
$secretB = New-Secret
$reconnect = @{ initialDelaySeconds = 1; maxDelaySeconds = 2; permanentErrorDelaySeconds = 2 }
$historyPath = Join-Path $runDirectory 'traffic-history.db'
$serverPath = Join-Path $runDirectory 'server.json'
$agentAPath = Join-Path $runDirectory 'agent-a.json'
$agentBPath = Join-Path $runDirectory 'agent-b.json'
Save-Json @{
    schemaVersion = 1
    tunnel = @{ listenAddress = '127.0.0.1'; port = $controlPort; dataPort = $dataPort; tlsEnabled = $false; certificatePemPath = ''; privateKeyPemPath = ''; handshakeTimeoutSeconds = 10; heartbeatIntervalSeconds = 5; heartbeatTimeoutSeconds = 20 }
    dashboard = @{ listenAddress = '127.0.0.1'; port = $dashboardPort; refreshSeconds = 1; admin = @{ username = 'admin'; passwordHash = 'PBKDF2-SHA256$210000$AA==$AA=='; sessionLifetimeMinutes = 60 } }
    clientsDirectory = (Join-Path $runDirectory 'clients')
    history = @{ enabled = $true; filePath = $historyPath; sampleIntervalSeconds = 1; retentionDays = 90 }
    audit = @{ filePath = $historyPath; retentionDays = 90 }
    limits = @{ maxConnections = 200; maxPendingConnections = 100; maxUnauthenticatedConnections = 100; maxChannelsPerClient = 10; openTimeoutSeconds = 10; blockedWriteTimeoutSeconds = 120; halfCloseDrainTimeoutSeconds = 300 }
} $serverPath
$channelsA = @(
    @{ channelId = 'alpha'; displayName = 'Alpha'; enabled = $true; listenAddress = '127.0.0.1'; listenPort = $proxyPorts[0]; targetHost = '127.0.0.1'; targetPort = $targetPorts[0]; maxConnections = 100; targetConnectTimeoutSeconds = 5 },
    @{ channelId = 'beta'; displayName = 'Beta'; enabled = $true; listenAddress = '127.0.0.1'; listenPort = $proxyPorts[1]; targetHost = '127.0.0.1'; targetPort = $targetPorts[1]; maxConnections = 100; targetConnectTimeoutSeconds = 5 }
)
$channelsB = @(@{ channelId = 'gamma'; displayName = 'Gamma'; enabled = $true; listenAddress = '127.0.0.1'; listenPort = $proxyPorts[2]; targetHost = '127.0.0.1'; targetPort = $targetPorts[2]; maxConnections = 100; targetConnectTimeoutSeconds = 5 })
Save-Json @{ schemaVersion = 1; clientId = 'accept-a'; displayName = 'Acceptance A'; enabled = $true; secret = $secretA; maxConnections = 100; maxPendingConnections = 100; channels = $channelsA; outboundMappings = @() } (Join-Path $runDirectory 'clients\accept-a.json')
Save-Json @{ schemaVersion = 1; clientId = 'accept-b'; displayName = 'Acceptance B'; enabled = $true; secret = $secretB; maxConnections = 100; maxPendingConnections = 100; channels = $channelsB; outboundMappings = @() } (Join-Path $runDirectory 'clients\accept-b.json')
Save-Json @{ serverHost = '127.0.0.1'; serverPort = $controlPort; dataPort = $dataPort; useTls = $false; clientId = 'accept-a'; secret = $secretA; dashboardPort = 0; reconnect = $reconnect } $agentAPath
Save-Json @{ serverHost = '127.0.0.1'; serverPort = $controlPort; dataPort = $dataPort; useTls = $false; clientId = 'accept-b'; secret = $secretB; dashboardPort = 0; reconnect = $reconnect } $agentBPath

$processes = @()
try {
    Publish-Component 'src\RelayLink.Server\RelayLink.Server.csproj' (Join-Path $runDirectory 'server-bin')
    Publish-Component 'src\RelayLink.Agent\RelayLink.Agent.csproj' (Join-Path $runDirectory 'agent-bin')
    Publish-Component 'tools\RelayLink.SimulatedTarget\RelayLink.SimulatedTarget.csproj' (Join-Path $runDirectory 'target-bin')
    Publish-Component 'tools\RelayLink.SimulatedCaller\RelayLink.SimulatedCaller.csproj' (Join-Path $runDirectory 'caller-bin')
    $serverDll = Join-Path $runDirectory 'server-bin\RelayLink.Server.dll'
    $agentDll = Join-Path $runDirectory 'agent-bin\RelayLink.Agent.dll'
    $targetDll = Join-Path $runDirectory 'target-bin\RelayLink.SimulatedTarget.dll'
    $callerDll = Join-Path $runDirectory 'caller-bin\RelayLink.SimulatedCaller.dll'
    & dotnet $serverDll --config $serverPath --check-config
    if ($LASTEXITCODE -ne 0) { throw 'Server configuration validation failed.' }
    foreach ($entry in @(@{ name = 'alpha'; port = $targetPorts[0]; tag = 'ALPHA-' }, @{ name = 'beta'; port = $targetPorts[1]; tag = 'BETA-' }, @{ name = 'gamma'; port = $targetPorts[2]; tag = 'GAMMA-' })) {
        Start-LocalProcess "target-$($entry.name)" $targetDll "--port $($entry.port) --tag $($entry.tag)" | Out-Null
    }
    Start-LocalProcess 'server' $serverDll ('--config "{0}"' -f $serverPath) | Out-Null
    Start-LocalProcess 'agent-a' $agentDll ('--config "{0}"' -f $agentAPath) | Out-Null
    Start-LocalProcess 'agent-b' $agentDll ('--config "{0}"' -f $agentBPath) | Out-Null
    $ready = $false
    for ($attempt = 0; $attempt -lt 150; $attempt++) {
        Start-Sleep -Milliseconds 200
        if (@($processes | Where-Object { -not (Get-Process -Id $_.pid -ErrorAction SilentlyContinue) }).Count -gt 0) { break }
        try {
            $overview = Invoke-RestMethod -Uri "http://127.0.0.1:$dashboardPort/api/v1/overview" -TimeoutSec 1
            if ($overview.clientsOnline -eq 2 -and $overview.channelsAvailable -eq 3) { $ready = $true; break }
        } catch { }
    }
    if (-not $ready) { throw "Two Agents and three channels did not become ready; inspect $runDirectory logs." }
    Write-Output 'PASS two Agents online and three channels available'
    Assert-RejectedFrame $controlPort 10
    Assert-RejectedFrame $dataPort 10
    Assert-RejectedFrame $dataPort 1
    Write-Output 'PASS control and data ports reject unauthenticated or wrong entry frames'
    for ($index = 0; $index -lt 3; $index++) {
        Invoke-Caller $proxyPorts[$index] @('ALPHA-', 'BETA-', 'GAMMA-')[$index] 1 65536
    }
    Write-Output 'PASS three tagged channels return exact bytes and EOF after half-close'
    $callers = @()
    for ($index = 0; $index -lt 3; $index++) {
        $callerArguments = '"{0}" --host 127.0.0.1 --port {1} --connections {2} --bytes {3} --half-close true --expected-tag {4}' -f $callerDll, $proxyPorts[$index], $ConnectionsPerChannel, $BytesPerConnection, @('ALPHA-', 'BETA-', 'GAMMA-')[$index]
        $callers += Start-Process -FilePath 'dotnet' -ArgumentList $callerArguments -WorkingDirectory $repositoryRoot -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $runDirectory "caller-$index.stdout.log") -RedirectStandardError (Join-Path $runDirectory "caller-$index.stderr.log")
    }
    $callers | Wait-Process
    if (@($callers | Where-Object { $_.ExitCode -ne 0 }).Count -gt 0) { throw "Concurrent callers failed; inspect $runDirectory caller logs." }
    Write-Output "PASS concurrent isolation: 3 channels x $ConnectionsPerChannel connections x $BytesPerConnection bytes"
    $overview = Invoke-RestMethod -Uri "http://127.0.0.1:$dashboardPort/api/v1/overview" -TimeoutSec 5
    if ($overview.bytesToTarget -le 0 -or $overview.bytesToCaller -le 0 -or $overview.activeConnections -ne 0) { throw 'Overview counters or connection cleanup are incorrect.' }
    $historyReady = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Seconds 1
        $historyReady = $true
        foreach ($clientChannel in @(@('accept-a', 'alpha'), @('accept-a', 'beta'), @('accept-b', 'gamma'))) {
            $history = Invoke-RestMethod -Uri "http://127.0.0.1:$dashboardPort/api/v1/history?clientId=$($clientChannel[0])&channelId=$($clientChannel[1])&hours=1" -TimeoutSec 5
            if (@($history.samples).Count -eq 0) { $historyReady = $false }
        }
        if ($historyReady) { break }
    }
    if (-not $historyReady) { throw 'Minute traffic history did not contain all three client/channel pairs.' }
    if (-not (Test-Path -LiteralPath $historyPath -PathType Leaf)) { throw 'SQLite history/audit database was not created.' }
    Write-Output 'PASS nonzero traffic counters and SQLite history for three distinct client/channel pairs'
    $state = [ordered]@{ runDirectory = $runDirectory; dashboardUrl = "http://127.0.0.1:$dashboardPort/"; controlPort = $controlPort; dataPort = $dataPort; proxyPorts = $proxyPorts; targetPorts = $targetPorts; processes = $processes; passed = @('online', 'port-isolation', 'byte-integrity', 'half-close', 'concurrency', 'history') }
    Save-Json $state (Join-Path $runDirectory 'state.json')
    $state | ConvertTo-Json -Depth 8
}
catch {
    foreach ($entry in $processes) { Stop-Process -Id $entry.pid -ErrorAction SilentlyContinue }
    throw
}
