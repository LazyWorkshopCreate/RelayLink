[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('linux-x64', 'win-x64', 'osx-x64', 'osx-arm64')]
    [string]$RuntimeIdentifier,
    [Parameter(Mandatory = $true)]
    [ValidateSet('Server', 'Agent')]
    [string]$Component,
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts\publish')
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if ($Component -eq 'Server' -and $RuntimeIdentifier.StartsWith('osx-', [System.StringComparison]::Ordinal)) {
    throw 'macOS runtime identifiers are supported for Agent only.'
}

function Publish-RelayLinkComponent {
    param([string]$Project, [string]$Rid)

    $name = [System.IO.Path]::GetFileNameWithoutExtension($Project)
    $output = Join-Path $OutputRoot (Join-Path $Rid $name)
    dotnet restore (Join-Path $repositoryRoot $Project) --runtime $Rid --configfile (Join-Path $repositoryRoot 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw "Restore failed: $Project ($Rid)" }
    dotnet publish (Join-Path $repositoryRoot $Project) --configuration Release --runtime $Rid --self-contained true --no-restore --output $output
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $Project ($Rid)" }
}

if ($Component -eq 'Server') {
    Publish-RelayLinkComponent 'src/RelayLink.Server/RelayLink.Server.csproj' $RuntimeIdentifier
}

if ($Component -eq 'Agent') {
    Publish-RelayLinkComponent 'src/RelayLink.Agent/RelayLink.Agent.csproj' $RuntimeIdentifier
}
