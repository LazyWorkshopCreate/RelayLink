[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Install', 'Update', 'Reconfigure')][string]$Mode,
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [string]$ServiceName = 'RelayLinkAgent',
    [string]$DataDirectory = (Join-Path $env:ProgramData 'RelayLink\Agent')
)

$ErrorActionPreference = 'Stop'
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($Mode -eq 'Install') {
    if ($service) { throw "Service '$ServiceName' already exists; this is not a new installation." }
    return
}
if (-not $service) { throw "Existing installation has no '$ServiceName' service; repair it before upgrading." }
$registered = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
$expectedPath = '"{0}" --config "{1}"' -f ([IO.Path]::GetFullPath($ExecutablePath)), (Join-Path ([IO.Path]::GetFullPath($DataDirectory)) 'agent.json')
if (-not $registered -or -not [string]::Equals($registered.PathName.Trim(), $expectedPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Service '$ServiceName' does not belong to this installation."
}
if ($Mode -eq 'Update' -and -not (Test-Path -LiteralPath (Join-Path ([IO.Path]::GetFullPath($DataDirectory)) 'agent.json') -PathType Leaf)) {
    throw 'Installed Agent configuration is missing; update cannot proceed.'
}
if ($service.Status -ne 'Stopped') {
    Stop-Service -Name $ServiceName -ErrorAction Stop
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}
