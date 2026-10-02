[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Install', 'Update', 'Reconfigure')][string]$Mode,
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [string]$ConfigurationPath,
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
if ($Mode -ne 'Update') {
    if ([string]::IsNullOrWhiteSpace($ConfigurationPath) -or -not (Test-Path -LiteralPath $ConfigurationPath -PathType Leaf)) {
        throw 'Select an Agent JSON configuration file.'
    }
    try { $configuration = Get-Content -LiteralPath $ConfigurationPath -Raw | ConvertFrom-Json }
    catch { throw 'Selected Agent configuration is not valid JSON.' }
    if ($null -eq $configuration -or -not ($configuration.PSObject.Properties.Name -contains 'servers') -or
        -not ($configuration.servers -is [array])) {
        throw 'Selected Agent configuration uses the old single-server format; convert it with RelayLink.Agent.ConfigMigrator first.'
    }
}
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$installationDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($ExecutablePath))
$legacyProgramDirectory = Join-Path $installationDirectory 'program'
foreach ($directoryPath in @($installationDirectory, $legacyProgramDirectory)) {
    if (Test-Path -LiteralPath $directoryPath) {
        $directory = Get-Item -LiteralPath $directoryPath -Force
        if (-not $directory.PSIsContainer -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Agent program directory must be a regular directory before replacement.'
        }
    }
}
if ($Mode -eq 'Install') {
    if ($service) { throw "Service '$ServiceName' already exists; this is not a new installation." }
    return
}
if (-not $service) { throw "Existing installation has no '$ServiceName' service; repair it before upgrading." }
$registered = Get-WmiObject -Class Win32_Service -Filter "Name='$ServiceName'"
$installedConfiguration = Join-Path ([IO.Path]::GetFullPath($DataDirectory)) 'agent.json'
$expectedPath = '"{0}" --config "{1}"' -f ([IO.Path]::GetFullPath($ExecutablePath)), $installedConfiguration
$legacyPath = '"{0}" --config "{1}"' -f (Join-Path $legacyProgramDirectory 'RelayLink.Agent.exe'), $installedConfiguration
if (-not $registered -or (-not [string]::Equals($registered.PathName.Trim(), $expectedPath, [StringComparison]::OrdinalIgnoreCase) -and
    -not [string]::Equals($registered.PathName.Trim(), $legacyPath, [StringComparison]::OrdinalIgnoreCase))) {
    throw "Service '$ServiceName' does not belong to this installation."
}
if ($Mode -eq 'Update' -and -not (Test-Path -LiteralPath (Join-Path ([IO.Path]::GetFullPath($DataDirectory)) 'agent.json') -PathType Leaf)) {
    throw 'Installed Agent configuration is missing; update cannot proceed.'
}
if ($service.Status -ne 'Stopped') {
    Stop-Service -Name $ServiceName -ErrorAction Stop
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}
