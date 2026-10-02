[CmdletBinding()]
param(
    [string]$DataRoot = (Join-Path $PSScriptRoot '..\.local\docker-server'),
    [switch]$RemoveImage
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$composeFile = Join-Path $repositoryRoot 'deploy\docker\compose.yaml'
$dataRootPath = [System.IO.Path]::GetFullPath($DataRoot)
$previousDataRoot = $env:RELAYLINK_DOCKER_ROOT

try {
    $env:RELAYLINK_DOCKER_ROOT = $dataRootPath
    $arguments = @('compose', '--file', $composeFile, 'down')
    if ($RemoveImage) { $arguments += @('--rmi', 'local') }
    & docker @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Docker Compose shutdown failed.' }
}
finally {
    $env:RELAYLINK_DOCKER_ROOT = $previousDataRoot
}
