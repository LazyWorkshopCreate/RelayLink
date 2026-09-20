[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [string]$ConfigurationPath,
    [ValidateSet('Install', 'Update', 'Reconfigure')][string]$Mode = 'Install',
    [string]$ServiceName = 'RelayLinkAgent',
    [string]$DataDirectory = (Join-Path $env:ProgramData 'RelayLink\Agent')
)

$ErrorActionPreference = 'Stop'
if (-not [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator privileges are required to install the Agent service.'
}
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$resolvedDataDirectory = [IO.Path]::GetFullPath($DataDirectory)
$installedConfiguration = Join-Path $resolvedDataDirectory 'agent.json'
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($Mode -eq 'Install' -and $existingService) { throw "Service '$ServiceName' already exists. Choose an explicit upgrade mode." }
if ($Mode -ne 'Install' -and -not $existingService) { throw "Service '$ServiceName' does not exist." }
if ($existingService) {
    $registered = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
    $expectedPath = '"{0}" --config "{1}"' -f $resolvedExecutable, $installedConfiguration
    if (-not $registered -or -not [string]::Equals($registered.PathName.Trim(), $expectedPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Service '$ServiceName' does not belong to this installation."
    }
    if ($existingService.Status -ne 'Stopped') { throw "Service '$ServiceName' must be stopped before upgrading." }
}
if ($Mode -eq 'Update') {
    if (-not (Test-Path -LiteralPath $installedConfiguration -PathType Leaf)) { throw 'Installed Agent configuration is missing.' }
    & $resolvedExecutable --config $installedConfiguration --check-config
    if ($LASTEXITCODE -ne 0) { throw 'Installed Agent configuration validation failed after update.' }
    Start-Service -Name $ServiceName
    return
}
if ([string]::IsNullOrWhiteSpace($ConfigurationPath)) { throw 'An Agent JSON configuration is required.' }
$resolvedConfiguration = (Resolve-Path -LiteralPath $ConfigurationPath).Path
if ([IO.Path]::GetExtension($resolvedConfiguration) -ne '.json') { throw 'Select an Agent JSON configuration file.' }
& $resolvedExecutable --config $resolvedConfiguration --check-config
if ($LASTEXITCODE -ne 0) { throw 'Agent configuration validation failed.' }

$configuration = Get-Content -LiteralPath $resolvedConfiguration -Raw | ConvertFrom-Json
$dashboardPort = if ($null -eq $configuration.dashboardPort) { 18081 } else { [int]$configuration.dashboardPort }
. (Join-Path $PSScriptRoot 'agent-monitor-shortcut.ps1')
$caPath = $null
$caContent = $null
if ($configuration.useTls -and -not [string]::IsNullOrWhiteSpace([string]$configuration.trustedCaPemPath)) {
    $sourceDirectory = Split-Path -Parent $resolvedConfiguration
    $caPath = [string]$configuration.trustedCaPemPath
    if (-not [IO.Path]::IsPathRooted($caPath)) { $caPath = Join-Path $sourceDirectory $caPath }
    $caPath = [IO.Path]::GetFullPath($caPath)
    $caContent = [IO.File]::ReadAllBytes($caPath)
}

# The installer owns only this fixed ProgramData location. Never recursively
# remove the directory itself or follow unexpected entries inside it.
if ($Mode -eq 'Reconfigure') {
    $expectedDataDirectory = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'RelayLink\Agent'))
    if (-not [string]::Equals($resolvedDataDirectory, $expectedDataDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Reconfiguration may only clear the managed Agent ProgramData directory.'
    }
    if (Test-Path -LiteralPath $resolvedDataDirectory) {
        $directory = Get-Item -LiteralPath $resolvedDataDirectory -Force
        if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Agent data directory is a reparse point.' }
        $parentDirectory = Get-Item -LiteralPath (Split-Path -Parent $resolvedDataDirectory) -Force
        if ($parentDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'RelayLink ProgramData directory is a reparse point.' }
        $managedFiles = Get-ChildItem -LiteralPath $resolvedDataDirectory -File -Force | Where-Object {
            $_.Name -in @('agent.json', 'trusted-ca.pem', 'relaylink-diagnostics.jsonl', 'relaylink-diagnostics.jsonl.1') -or
            $_.Name -match '^[a-z0-9][a-z0-9_-]{0,63}\.(?:e2e\.pfx|ports\.json)(?:\.[0-9a-f]{32}\.tmp)?$'
        }
        foreach ($file in $managedFiles) {
            if (-not [string]::Equals([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($file.FullName)), $resolvedDataDirectory, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Refusing to clear a file outside the managed Agent directory.'
            }
            if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing to clear a reparse point.' }
            Remove-Item -LiteralPath $file.FullName -Force
        }
    }
}
New-Item -ItemType Directory -Path $resolvedDataDirectory -Force | Out-Null
# Identity and port-state files are created beside the installed configuration.
& icacls.exe $resolvedDataDirectory /reset | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not reset the Agent data directory permissions.' }
& icacls.exe $resolvedDataDirectory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)M' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not secure the Agent data directory.' }

if ($caPath) {
    $caDestination = Join-Path $resolvedDataDirectory 'trusted-ca.pem'
    [IO.File]::WriteAllBytes($caDestination, $caContent)
    $configuration.trustedCaPemPath = $caDestination
}

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
    if ($Mode -eq 'Install') {
        New-Service -Name $ServiceName -BinaryPathName $binaryPath -DisplayName 'RelayLink Agent' -Description 'RelayLink reverse TCP proxy agent' -StartupType Automatic | Out-Null
        $created = $true
        & sc.exe config $ServiceName obj= 'NT AUTHORITY\LocalService' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not set the Agent service account.' }
        & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not configure Agent service recovery.' }
    }
    Start-Service -Name $ServiceName
    if ($Mode -eq 'Reconfigure') { Remove-AgentMonitorShortcut }
    New-AgentMonitorShortcut -DashboardPort $dashboardPort
} catch {
    if ($created) {
        Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
        & sc.exe delete $ServiceName | Out-Null
    }
    throw
}
