[CmdletBinding()]
param(
    [string]$ServiceName = 'RelayLinkAgent',
    [Parameter(Mandatory = $true)][string]$ExecutablePath
)

$ErrorActionPreference = 'Stop'
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) { return }
$registered = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
$expectedPrefix = '"{0}" --config ' -f ([IO.Path]::GetFullPath($ExecutablePath))
if (-not $registered -or -not $registered.PathName.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Service '$ServiceName' does not belong to this installation."
}
if ($service.Status -ne 'Stopped') {
    Stop-Service -Name $ServiceName -ErrorAction Stop
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}
& sc.exe delete $ServiceName | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not remove service '$ServiceName'." }
# ProgramData configuration, CA and generated identity are intentionally retained.
