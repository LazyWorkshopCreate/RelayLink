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
$configurationFile = Join-Path $configurationDirectory 'server.json'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker is not installed or is not available on PATH.'
}

docker compose version | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Docker Compose v2 is required.' }

New-Item -ItemType Directory -Force -Path $clientsDirectory, $databaseDirectory | Out-Null

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
    Write-Host "Created $configurationFile"
}
else {
    Write-Host "Using existing $configurationFile"
}

$previousDataRoot = $env:RELAYLINK_DOCKER_ROOT
try {
    $env:RELAYLINK_DOCKER_ROOT = $dataRootPath
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
}
