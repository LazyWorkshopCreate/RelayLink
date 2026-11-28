[CmdletBinding()]
param(
    [string]$AgentServerHost,
    [string]$DataRoot = (Join-Path $PSScriptRoot '..\.local\docker-server'),
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$composeFile = Join-Path $repositoryRoot 'deploy\docker\compose.yaml'
$templateFile = Join-Path $repositoryRoot 'deploy\docker\server.docker.example.json'
$dataRootPath = [System.IO.Path]::GetFullPath($DataRoot)
$configurationDirectory = Join-Path $dataRootPath 'config'
$clientsDirectory = Join-Path $configurationDirectory 'clients'
$databaseDirectory = Join-Path $dataRootPath 'data'
$logsDirectory = Join-Path $databaseDirectory 'logs'
$configurationFile = Join-Path $configurationDirectory 'server.json'

function Protect-LocalPath([string]$Path, [bool]$IsDirectory) {
    if ($IsWindows) {
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $grant = if ($IsDirectory) { "${sid}:(OI)(CI)F" } else { "${sid}:F" }
        $systemGrant = if ($IsDirectory) { '*S-1-5-18:(OI)(CI)F' } else { '*S-1-5-18:F' }
        $adminGrant = if ($IsDirectory) { '*S-1-5-32-544:(OI)(CI)F' } else { '*S-1-5-32-544:F' }
        & icacls.exe $Path /inheritance:r /grant:r $grant $systemGrant $adminGrant | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Failed to restrict Windows ACL on $Path." }
    }
    else {
        $mode = if ($IsDirectory) { '700' } else { '600' }
        & chmod $mode $Path
        if ($LASTEXITCODE -ne 0) { throw "Failed to set mode $mode on $Path." }
    }
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker is not installed or is not available on PATH.'
}

docker compose version | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Docker Compose v2 is required.' }

New-Item -ItemType Directory -Force -Path $clientsDirectory, $databaseDirectory, $logsDirectory | Out-Null
foreach ($dir in @($dataRootPath, $configurationDirectory, $clientsDirectory, $databaseDirectory, $logsDirectory)) {
    Protect-LocalPath $dir $true
}

if (-not (Test-Path -LiteralPath $configurationFile)) {
    if ([string]::IsNullOrWhiteSpace($AgentServerHost)) {
        $AgentServerHost = Read-Host 'Agent 可访问的服务端 DNS 名称或 IP'
    }
    if ([string]::IsNullOrWhiteSpace($AgentServerHost) -or
        $AgentServerHost -in @('0.0.0.0', '::') -or
        [Uri]::CheckHostName($AgentServerHost) -eq [UriHostNameType]::Unknown) {
        throw "Invalid Agent server host: $AgentServerHost"
    }

    $passwordHash = & (Join-Path $repositoryRoot 'scripts\new-admin-password-hash.ps1')
    if ([string]::IsNullOrWhiteSpace($passwordHash)) { throw 'Administrator password hash generation failed.' }

    $configuration = (Get-Content -Raw -LiteralPath $templateFile).
        Replace('REPLACE_WITH_AGENT_SERVER_HOST', $AgentServerHost).
        Replace('REPLACE_WITH_ADMIN_PASSWORD_HASH', $passwordHash)
    [System.IO.File]::WriteAllText($configurationFile, $configuration, [System.Text.UTF8Encoding]::new($false))
    Protect-LocalPath $configurationFile $false
    Write-Host "Created $configurationFile"
}
else {
    Protect-LocalPath $configurationFile $false
    Write-Host "Using existing $configurationFile"
}

$previousDataRoot = $env:RELAYLINK_DOCKER_ROOT
$previousUid = $env:RELAYLINK_UID
$previousGid = $env:RELAYLINK_GID
try {
    $env:RELAYLINK_DOCKER_ROOT = $dataRootPath
    if (-not $IsWindows) {
        if ([string]::IsNullOrWhiteSpace($env:RELAYLINK_UID)) { $env:RELAYLINK_UID = (& id -u).Trim() }
        if ([string]::IsNullOrWhiteSpace($env:RELAYLINK_GID)) { $env:RELAYLINK_GID = (& id -g).Trim() }
    }
    if (-not $NoBuild) {
        docker compose --file $composeFile build
        if ($LASTEXITCODE -ne 0) { throw 'RelayLink Server image build failed.' }
    }

    docker compose --file $composeFile run --rm --no-deps relaylink-server --config /etc/relaylink/server.json --check-config
    if ($LASTEXITCODE -ne 0) { throw 'RelayLink Server configuration validation failed.' }

    docker compose --file $composeFile up --detach --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Docker Compose startup failed.' }

    docker compose --file $composeFile ps
    Write-Host 'RelayLink Server started. Dashboard default: http://127.0.0.1:18080/'
    Write-Warning 'The generated starter configuration disables tunnel TLS. Configure certificates before enabling Agent-to-Agent access or production traffic.'
}
finally {
    $env:RELAYLINK_DOCKER_ROOT = $previousDataRoot
    $env:RELAYLINK_UID = $previousUid
    $env:RELAYLINK_GID = $previousGid
}
