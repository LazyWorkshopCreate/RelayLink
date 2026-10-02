[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('linux-x64', 'win-x64', 'osx-x64', 'osx-arm64')]
    [string]$RuntimeIdentifier,
    [Parameter(Mandatory = $true)]
    [ValidateSet('Server', 'Agent')]
    [string]$Component,
    [ValidateSet('SelfContained', 'FrameworkDependent')]
    [string]$WindowsAgentMode = 'FrameworkDependent',
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts\publish')
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if ($Component -eq 'Server' -and $RuntimeIdentifier.StartsWith('osx-', [System.StringComparison]::Ordinal)) {
    throw 'macOS runtime identifiers are supported for Agent only.'
}
if ($PSBoundParameters.ContainsKey('WindowsAgentMode') -and ($Component -ne 'Agent' -or $RuntimeIdentifier -ne 'win-x64')) {
    throw 'WindowsAgentMode is supported for win-x64 Agent only.'
}
$componentOutputRoot = Join-Path $OutputRoot $RuntimeIdentifier
if ($Component -eq 'Agent' -and $RuntimeIdentifier -eq 'win-x64') {
    $modeDirectory = if ($WindowsAgentMode -eq 'SelfContained') { 'self-contained' } else { 'framework-dependent' }
    $componentOutputRoot = Join-Path $componentOutputRoot $modeDirectory
}

function Publish-RelayLinkComponent {
    param([string]$Project, [string]$Rid)

    $name = [System.IO.Path]::GetFileNameWithoutExtension($Project)
    $output = Join-Path $componentOutputRoot $name
    dotnet restore (Join-Path $repositoryRoot $Project) --runtime $Rid --configfile (Join-Path $repositoryRoot 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw "Restore failed: $Project ($Rid)" }
    $selfContained = $Component -ne 'Agent' -or $Rid -ne 'win-x64' -or $WindowsAgentMode -eq 'SelfContained'
    dotnet publish (Join-Path $repositoryRoot $Project) --configuration Release --runtime $Rid --self-contained $selfContained --no-restore --output $output
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $Project ($Rid)" }
}

if ($Component -eq 'Server') {
    Publish-RelayLinkComponent 'src/RelayLink.Server/RelayLink.Server.csproj' $RuntimeIdentifier
}

if ($Component -eq 'Agent') {
    Publish-RelayLinkComponent 'src/RelayLink.Agent/RelayLink.Agent.csproj' $RuntimeIdentifier
    Publish-RelayLinkComponent 'src/RelayLink.Agent.ConfigMigrator/RelayLink.Agent.ConfigMigrator.csproj' $RuntimeIdentifier
    $agentOutput = Join-Path $componentOutputRoot 'RelayLink.Agent'
    $migratorOutput = Join-Path $componentOutputRoot 'RelayLink.Agent.ConfigMigrator'
    Get-ChildItem -LiteralPath $migratorOutput -File -Filter 'RelayLink.Agent.ConfigMigrator*' |
        Copy-Item -Destination $agentOutput -Force
    if ($RuntimeIdentifier.StartsWith('osx-', [System.StringComparison]::Ordinal)) {
        $wrapper = Join-Path $agentOutput 'start-relaylink-agent.sh'
        Copy-Item -LiteralPath (Join-Path $repositoryRoot 'deploy/macos/start-relaylink-agent.sh') -Destination $wrapper -Force
        if (-not $IsWindows) { & chmod 0755 $wrapper }
    }
}
