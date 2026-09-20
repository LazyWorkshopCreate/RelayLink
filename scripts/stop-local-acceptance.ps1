[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory = $true)][string]$StatePath)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$acceptanceRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '.local\local-acceptance'))
$resolvedState = (Resolve-Path -LiteralPath $StatePath).Path
$runDirectory = [IO.Path]::GetDirectoryName($resolvedState)
if (-not $runDirectory.StartsWith($acceptanceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($resolvedState) -ne 'state.json') {
    throw 'State file must be inside a local-acceptance run directory.'
}
$state = Get-Content -LiteralPath $resolvedState -Raw | ConvertFrom-Json
if (-not [string]::Equals([IO.Path]::GetFullPath($state.runDirectory), $runDirectory, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'State file run directory does not match its location.'
}
$expectedDlls = @{
    server = 'server-bin\RelayLink.Server.dll'
    'agent-a' = 'agent-bin\RelayLink.Agent.dll'
    'agent-b' = 'agent-bin\RelayLink.Agent.dll'
    'target-alpha' = 'target-bin\RelayLink.SimulatedTarget.dll'
    'target-beta' = 'target-bin\RelayLink.SimulatedTarget.dll'
    'target-gamma' = 'target-bin\RelayLink.SimulatedTarget.dll'
}
foreach ($entry in $state.processes) {
    if (-not $expectedDlls.ContainsKey([string]$entry.name)) { throw "Unknown process name in state: $($entry.name)" }
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$([int]$entry.pid)" -ErrorAction SilentlyContinue
    if (-not $process) { continue }
    $expectedDll = Join-Path $runDirectory $expectedDlls[[string]$entry.name]
    if ($process.Name -ne 'dotnet.exe' -or -not $process.CommandLine.Contains($expectedDll, [StringComparison]::OrdinalIgnoreCase)) {
        throw "PID $($entry.pid) no longer belongs to this acceptance run; it was not stopped."
    }
    if ($PSCmdlet.ShouldProcess("$($entry.name) (PID $($entry.pid))", 'Stop acceptance process')) {
        Stop-Process -Id ([int]$entry.pid) -ErrorAction Stop
        Write-Output "Stopped $($entry.name) (PID $($entry.pid))"
    }
}
Write-Output "Acceptance evidence remains in $runDirectory"
