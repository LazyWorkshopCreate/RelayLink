[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$ConfigurationPath,
    [string]$ServiceName = 'RelayLinkAgent',
    [string]$DataDirectory = (Join-Path $env:ProgramData 'RelayLink\Agent')
)

$ErrorActionPreference = 'Stop'
if (-not [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator privileges are required to install the Agent service.'
}
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Service '$ServiceName' already exists. Uninstall or upgrade it explicitly before installing."
}

$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$resolvedConfiguration = (Resolve-Path -LiteralPath $ConfigurationPath).Path
if ([IO.Path]::GetExtension($resolvedConfiguration) -ne '.json') { throw 'Select an Agent JSON configuration file.' }
& $resolvedExecutable --config $resolvedConfiguration --check-config
if ($LASTEXITCODE -ne 0) { throw 'Agent configuration validation failed.' }

$configuration = Get-Content -LiteralPath $resolvedConfiguration -Raw | ConvertFrom-Json
$dashboardPort = if ($null -eq $configuration.dashboardPort) { 18081 } else { [int]$configuration.dashboardPort }
. (Join-Path $PSScriptRoot 'agent-monitor-shortcut.ps1')
$caPath = $null
if ($configuration.useTls -and -not [string]::IsNullOrWhiteSpace([string]$configuration.trustedCaPemPath)) {
    $sourceDirectory = Split-Path -Parent $resolvedConfiguration
    $caPath = [string]$configuration.trustedCaPemPath
    if (-not [IO.Path]::IsPathRooted($caPath)) { $caPath = Join-Path $sourceDirectory $caPath }
    $caPath = [IO.Path]::GetFullPath($caPath)
}

$resolvedDataDirectory = [IO.Path]::GetFullPath($DataDirectory)
New-Item -ItemType Directory -Path $resolvedDataDirectory -Force | Out-Null
# Identity and port-state files are created beside the installed configuration.
& icacls.exe $resolvedDataDirectory /reset | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not reset the Agent data directory permissions.' }
& icacls.exe $resolvedDataDirectory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)M' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not secure the Agent data directory.' }

if ($caPath) {
    $caDestination = Join-Path $resolvedDataDirectory 'trusted-ca.pem'
    if (-not [string]::Equals($caPath, $caDestination, [StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $caPath -Destination $caDestination -Force
    }
    $configuration.trustedCaPemPath = $caDestination
}

$installedConfiguration = Join-Path $resolvedDataDirectory 'agent.json'
$configuration | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $installedConfiguration -Encoding utf8
& icacls.exe $installedConfiguration /reset | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not reset the Agent configuration permissions.' }
& icacls.exe $installedConfiguration /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' '*S-1-5-19:R' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not secure the Agent configuration.' }
if ($caPath) {
    & icacls.exe $caDestination /reset | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not reset the trusted CA file permissions.' }
    & icacls.exe $caDestination /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' '*S-1-5-19:R' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not secure the trusted CA file.' }
}
& $resolvedExecutable --config $installedConfiguration --check-config
if ($LASTEXITCODE -ne 0) { throw 'Installed Agent configuration validation failed.' }

$binaryPath = '"{0}" --config "{1}"' -f $resolvedExecutable, $installedConfiguration
$created = $false
try {
    New-Service -Name $ServiceName -BinaryPathName $binaryPath -DisplayName 'RelayLink Agent' -Description 'RelayLink reverse TCP proxy agent' -StartupType Automatic | Out-Null
    $created = $true
    & sc.exe config $ServiceName obj= 'NT AUTHORITY\LocalService' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not set the Agent service account.' }
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure Agent service recovery.' }
    Start-Service -Name $ServiceName
    New-AgentMonitorShortcut -DashboardPort $dashboardPort
} catch {
    if ($created) {
        Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
        & sc.exe delete $ServiceName | Out-Null
    }
    throw
}
