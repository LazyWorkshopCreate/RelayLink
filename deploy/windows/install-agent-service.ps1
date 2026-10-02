[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [string]$ConfigurationPath,
    [ValidateSet('Install', 'Update', 'Reconfigure')][string]$Mode = 'Install',
    [string]$ServiceName = 'RelayLinkAgent',
    [string]$DataDirectory = (Join-Path $env:ProgramData 'RelayLink\Agent'),
    [string]$ErrorReportPath
)

$ErrorActionPreference = 'Stop'
trap {
    $message = $_.Exception.Message
    if (-not [string]::IsNullOrWhiteSpace($ErrorReportPath)) {
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($ErrorReportPath), $message, (New-Object Text.UTF8Encoding($false)))
    }
    [Console]::Error.WriteLine($message)
    exit 1
}
$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator privileges are required to install the Agent service.'
}
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$installationDirectory = Split-Path -Parent $resolvedExecutable
$migratorExecutable = Join-Path (Split-Path -Parent $resolvedExecutable) 'RelayLink.Agent.ConfigMigrator.exe'
$resolvedDataDirectory = [IO.Path]::GetFullPath($DataDirectory)
$installedConfiguration = Join-Path $resolvedDataDirectory 'agent.json'
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($Mode -eq 'Install' -and $existingService) { throw "Service '$ServiceName' already exists. Choose an explicit upgrade mode." }
if ($Mode -ne 'Install' -and -not $existingService) { throw "Service '$ServiceName' does not exist." }
if ($existingService) {
    $registered = Get-WmiObject -Class Win32_Service -Filter "Name='$ServiceName'"
    $expectedPath = '"{0}" --config "{1}"' -f $resolvedExecutable, $installedConfiguration
    $legacyPath = '"{0}" --config "{1}"' -f (Join-Path $installationDirectory 'program\RelayLink.Agent.exe'), $installedConfiguration
    if (-not $registered -or (-not [string]::Equals($registered.PathName.Trim(), $expectedPath, [StringComparison]::OrdinalIgnoreCase) -and
        -not [string]::Equals($registered.PathName.Trim(), $legacyPath, [StringComparison]::OrdinalIgnoreCase))) {
        throw "Service '$ServiceName' does not belong to this installation."
    }
    if ($existingService.Status -ne 'Stopped') { throw "Service '$ServiceName' must be stopped before upgrading." }
}
$binaryPath = '"{0}" --config "{1}"' -f $resolvedExecutable, $installedConfiguration
function Set-AgentServiceExecutable {
    if (-not $registered -or [string]::Equals($registered.PathName.Trim(), $binaryPath, [StringComparison]::OrdinalIgnoreCase)) { return }
    $parameters = $registered.PSBase.GetMethodParameters('Change')
    $parameters['PathName'] = $binaryPath
    $result = $registered.PSBase.InvokeMethod('Change', $parameters, $null)
    if ($result.ReturnValue -ne 0) { throw "Could not update the Agent service executable (Win32_Service.Change return code $($result.ReturnValue))." }
}
if ($Mode -eq 'Update') {
    if (-not (Test-Path -LiteralPath $installedConfiguration -PathType Leaf)) { throw 'Installed Agent configuration is missing.' }
    if (-not (Test-Path -LiteralPath $migratorExecutable -PathType Leaf)) { throw 'Agent configuration migrator is missing.' }
    $legacyConfiguration = Get-Content -LiteralPath $installedConfiguration -Raw | ConvertFrom-Json
    if ($null -eq $legacyConfiguration.servers) {
        $legacyClientId = [string]$legacyConfiguration.clientId
        if ($legacyClientId -notmatch '^[a-z0-9][a-z0-9_-]{0,63}$') { throw 'Legacy Agent client ID is invalid.' }
        $legacyIdentityPath = Join-Path $resolvedDataDirectory ($legacyClientId + '.e2e.pfx')
        if (Test-Path -LiteralPath $legacyIdentityPath -PathType Leaf) {
            $identityItem = Get-Item -LiteralPath $legacyIdentityPath -Force
            if ($identityItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Legacy Agent identity is a reparse point.' }
            # Older Agent identities grant only LocalService access. Grant the
            # installer administrator read access without exposing the key.
            & takeown.exe /F $legacyIdentityPath /A | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not take ownership of the legacy Agent identity.' }
            & icacls.exe $legacyIdentityPath /grant '*S-1-5-32-544:R' | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not grant migration access to the legacy Agent identity.' }
        }
    }
    $migration = New-Object Diagnostics.ProcessStartInfo
    $migration.FileName = $migratorExecutable
    $migration.Arguments = '--config "' + $installedConfiguration + '"'
    $migration.UseShellExecute = $false
    $migration.RedirectStandardError = $true
    $migration.CreateNoWindow = $true
    $migrationProcess = [Diagnostics.Process]::Start($migration)
    $migrationError = $migrationProcess.StandardError.ReadToEnd().Trim()
    $migrationProcess.WaitForExit()
    if ($migrationProcess.ExitCode -ne 0) {
        if ($migrationError.StartsWith('Agent configuration migration failed: ', [StringComparison]::Ordinal) -and
            $migrationError.Length -le 400 -and $migrationError -notmatch '[\r\n]') {
            throw "Installed Agent configuration migration failed: $migrationError; service remains stopped."
        }
        if ($migrationError.StartsWith('You must install or update .NET to run this application.', [StringComparison]::Ordinal)) {
            throw 'Installed Agent configuration migration failed: x64 .NET runtime was not found; service remains stopped.'
        }
        throw "Installed Agent configuration migration failed (exit code $($migrationProcess.ExitCode)); service remains stopped."
    }
    & $resolvedExecutable --config $installedConfiguration --check-config
    if ($LASTEXITCODE -ne 0) { throw 'Installed Agent configuration validation failed after update.' }
    # The local management page replaces agent.json when profiles change.
    & icacls.exe $installedConfiguration /grant:r '*S-1-5-19:M' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not grant Agent service configuration write access.' }
    Set-AgentServiceExecutable
    Start-Service -Name $ServiceName
    return
}
if ([string]::IsNullOrWhiteSpace($ConfigurationPath)) { throw 'An Agent JSON configuration is required.' }
$resolvedConfiguration = (Resolve-Path -LiteralPath $ConfigurationPath).Path
if ([IO.Path]::GetExtension($resolvedConfiguration) -ne '.json') { throw 'Select an Agent JSON configuration file.' }
& $resolvedExecutable --config $resolvedConfiguration --check-config
if ($LASTEXITCODE -ne 0) { throw 'Agent configuration validation failed.' }

$configuration = Get-Content -LiteralPath $resolvedConfiguration -Raw | ConvertFrom-Json
$configurationBytes = [IO.File]::ReadAllBytes($resolvedConfiguration)
$dashboardPort = if ($null -eq $configuration.dashboardPort) { 18081 } else { [int]$configuration.dashboardPort }
. (Join-Path $PSScriptRoot 'agent-monitor-shortcut.ps1')

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
            if ($file.Name -match '\.e2e\.pfx$') {
                # Legacy identities grant only LocalService access. Reconfiguration
                # explicitly discards them, so grant the installer deletion access.
                & takeown.exe /F $file.FullName /A | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Could not take ownership of the legacy Agent identity for reconfiguration.' }
                & icacls.exe $file.FullName /grant '*S-1-5-32-544:F' '*S-1-5-18:F' | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Could not grant deletion access to the legacy Agent identity.' }
            }
            Remove-Item -LiteralPath $file.FullName -Force
        }
        $stateRoot = Join-Path $resolvedDataDirectory 'state'
        if (Test-Path -LiteralPath $stateRoot) {
            $stateItem = Get-Item -LiteralPath $stateRoot -Force
            if (-not $stateItem.PSIsContainer -or ($stateItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Refusing to clear an unexpected Agent state entry.'
            }
            foreach ($profileDirectory in (Get-ChildItem -LiteralPath $stateRoot -Force)) {
                if (-not $profileDirectory.PSIsContainer -or $profileDirectory.Name -notmatch '^[a-z0-9][a-z0-9_-]{0,63}$' -or
                    ($profileDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                    -not [IO.Path]::GetFullPath($profileDirectory.FullName).StartsWith([IO.Path]::GetFullPath($stateRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Refusing to clear an unexpected Agent profile state entry.'
                }
                foreach ($stateFile in (Get-ChildItem -LiteralPath $profileDirectory.FullName -Force)) {
                    if ($stateFile.PSIsContainer -or ($stateFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                        $stateFile.Name -notmatch '^(identity\.pfx|ports\.json|relaylink-diagnostics\.jsonl(?:\.1)?)(?:\.[0-9a-f]{32}\.tmp)?$') {
                        throw 'Refusing to clear an unexpected Agent state file.'
                    }
                    if ($stateFile.Name -eq 'identity.pfx') {
                        # Agent identities deliberately grant only LocalService access.
                        # Reconfiguration explicitly discards them, so first give the
                        # installer enough access to remove this exact managed file.
                        & takeown.exe /F $stateFile.FullName /A | Out-Null
                        if ($LASTEXITCODE -ne 0) { throw 'Could not take ownership of the Agent identity for reconfiguration.' }
                        & icacls.exe $stateFile.FullName /grant '*S-1-5-32-544:F' '*S-1-5-18:F' | Out-Null
                        if ($LASTEXITCODE -ne 0) { throw 'Could not grant deletion access to the Agent identity.' }
                    }
                    Remove-Item -LiteralPath $stateFile.FullName -Force
                }
                Remove-Item -LiteralPath $profileDirectory.FullName -Force
            }
            Remove-Item -LiteralPath $stateRoot -Force
        }
    }
}
New-Item -ItemType Directory -Path $resolvedDataDirectory -Force | Out-Null
# Each server profile keeps its identity and port state under state/<profileId>/.
& icacls.exe $resolvedDataDirectory /reset | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not reset the Agent data directory permissions.' }
& icacls.exe $resolvedDataDirectory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)M' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not secure the Agent data directory.' }

[IO.File]::WriteAllBytes($installedConfiguration, $configurationBytes)
& icacls.exe $installedConfiguration /reset | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not reset the Agent configuration permissions.' }
& icacls.exe $installedConfiguration /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' '*S-1-5-19:M' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not secure the Agent configuration.' }
& $resolvedExecutable --config $installedConfiguration --check-config
if ($LASTEXITCODE -ne 0) { throw 'Installed Agent configuration validation failed.' }

$created = $false
try {
    if ($Mode -eq 'Install') {
        $serviceClass = Get-WmiObject -List -Class Win32_Service
        $createResult = $serviceClass.Create($ServiceName, 'RelayLink Agent', $binaryPath, 16, 1, 'Automatic', $false, 'NT AUTHORITY\LocalService', $null, $null, $null, $null)
        if ($createResult.ReturnValue -ne 0) {
            throw "Could not create the Agent service (Win32_Service.Create return code $($createResult.ReturnValue))."
        }
        $created = $true
        & sc.exe description $ServiceName 'RelayLink reverse TCP proxy agent' | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Could not set the Agent service description (sc.exe exit code $LASTEXITCODE)." }
        & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not configure Agent service recovery (sc.exe exit code $LASTEXITCODE)." }
        & sc.exe failureflag $ServiceName 1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not enable Agent recovery for non-crash failures (sc.exe exit code $LASTEXITCODE)." }
    }
    if ($Mode -eq 'Reconfigure') { Set-AgentServiceExecutable }
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
