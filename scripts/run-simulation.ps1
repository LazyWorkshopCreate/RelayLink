[CmdletBinding()]
param(
    [int[]]$TargetPorts = @(19101, 19102, 19103),
    [int]$ConnectionsPerCaller = 4,
    [int]$BytesPerConnection = 16384
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$targetDll = Join-Path $repositoryRoot 'tools\RelayLink.SimulatedTarget\bin\Debug\net10.0\RelayLink.SimulatedTarget.dll'
$callerDll = Join-Path $repositoryRoot 'tools\RelayLink.SimulatedCaller\bin\Debug\net10.0\RelayLink.SimulatedCaller.dll'
if (!(Test-Path $targetDll) -or !(Test-Path $callerDll)) { throw 'Build the solution before running the simulation.' }

$targets = @()
try {
    foreach ($port in $TargetPorts) {
        $targets += Start-Process dotnet -ArgumentList "`"$targetDll`" --port $port" -WorkingDirectory $repositoryRoot -WindowStyle Hidden -PassThru
    }
    Start-Sleep -Seconds 1
    $callers = foreach ($port in $TargetPorts) {
        Start-Process dotnet -ArgumentList "`"$callerDll`" --host 127.0.0.1 --port $port --connections $ConnectionsPerCaller --bytes $BytesPerConnection" -WorkingDirectory $repositoryRoot -WindowStyle Hidden -PassThru
    }
    $callers | Wait-Process
    $failed = @($callers | Where-Object { $_.ExitCode -ne 0 })
    if ($failed.Count) { throw "Simulation callers failed: $($failed.Id -join ', ')" }
    Write-Output "SIMULATION_PASS targets=$($TargetPorts.Count) callers=$($callers.Count) connectionsPerCaller=$ConnectionsPerCaller bytesPerConnection=$BytesPerConnection"
}
finally {
    foreach ($target in $targets) { if (!$target.HasExited) { Stop-Process -Id $target.Id -Force } }
}
