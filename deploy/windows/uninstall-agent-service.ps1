[CmdletBinding()]
param(
    [string]$ServiceName = 'RelayLinkAgent',
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [string]$ErrorReportPath
)

$ErrorActionPreference = 'Stop'
trap {
    $message = $_.Exception.Message
    if (-not [string]::IsNullOrWhiteSpace($ErrorReportPath)) {
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($ErrorReportPath), $message, [Text.Encoding]::UTF8)
    }
    [Console]::Error.WriteLine($message)
    exit 1
}
. (Join-Path $PSScriptRoot 'agent-monitor-shortcut.ps1')
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Remove-AgentMonitorShortcut
    return
}
$registered = Get-WmiObject -Class Win32_Service -Filter "Name='$ServiceName'"
$expectedPrefix = '"{0}" --config ' -f ([IO.Path]::GetFullPath($ExecutablePath))
if (-not $registered -or -not $registered.PathName.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Service '$ServiceName' does not belong to this installation."
}
if ($service.Status -ne 'Stopped') {
    Stop-Service -Name $ServiceName -ErrorAction Stop
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}
$service.Dispose()
$deleteOutput = @(& sc.exe delete $ServiceName 2>&1)
$deleteExitCode = $LASTEXITCODE
$deleteOutput | Out-Host
if ($deleteExitCode -ne 0) { throw "Could not remove service '$ServiceName' (sc.exe exit code $deleteExitCode): $($deleteOutput -join ' ')" }
Remove-AgentMonitorShortcut
# ProgramData configuration, CA and generated identity are intentionally retained.
