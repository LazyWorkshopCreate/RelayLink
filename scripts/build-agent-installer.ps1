[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [string]$InnoCompiler
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publishDirectory = Join-Path $repositoryRoot 'artifacts\publish\win-x64\RelayLink.Agent'
$installerScript = Join-Path $repositoryRoot 'deploy\windows\relaylink-agent.iss'

if ([string]::IsNullOrWhiteSpace($InnoCompiler)) {
    $candidate = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($candidate) { $InnoCompiler = $candidate.Source }
    if (-not $InnoCompiler) {
        foreach ($path in @((Join-Path $repositoryRoot 'artifacts\tools\InnoSetup\ISCC.exe'), 'C:\Program Files (x86)\Inno Setup 7\ISCC.exe', 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe', 'C:\Program Files\Inno Setup 7\ISCC.exe')) {
            if (Test-Path -LiteralPath $path) { $InnoCompiler = $path; break }
        }
    }
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler)) {
    throw 'Inno Setup 6.3+ ISCC.exe is required. Install Inno Setup and pass -InnoCompiler if necessary.'
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$') { throw 'Version must be a semantic version.' }

& (Join-Path $PSScriptRoot 'publish.ps1') -RuntimeIdentifier win-x64 -Component Agent
if ($LASTEXITCODE -ne 0) { throw 'Agent publish failed.' }
if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'RelayLink.Agent.exe'))) { throw 'Agent executable is missing from publish output.' }

& $InnoCompiler "/DPublishDir=$publishDirectory" "/DPackageVersion=$Version" $installerScript
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
Write-Output (Join-Path $repositoryRoot "artifacts\installer\RelayLink-Agent-win-x64-$Version.exe")
