[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [string]$InnoCompiler
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publishRoot = Join-Path $repositoryRoot ('artifacts\installer-publish\' + [guid]::NewGuid().ToString('N'))
$publishDirectory = Join-Path $publishRoot 'win-x64\RelayLink.Agent'
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

& (Join-Path $PSScriptRoot 'publish.ps1') -RuntimeIdentifier win-x64 -Component Agent -OutputRoot $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'Agent publish failed.' }
if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'RelayLink.Agent.exe'))) { throw 'Agent executable is missing from publish output.' }
$runtimeConfigurationPath = Join-Path $publishDirectory 'RelayLink.Agent.runtimeconfig.json'
if (-not (Test-Path -LiteralPath $runtimeConfigurationPath)) { throw 'Framework-dependent Agent runtimeconfig is missing.' }
foreach ($runtimeFile in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) {
    if (Test-Path -LiteralPath (Join-Path $publishDirectory $runtimeFile)) {
        throw "Windows Agent publish must be framework-dependent but contains runtime file '$runtimeFile'."
    }
}
$runtimeConfiguration = Get-Content -LiteralPath $runtimeConfigurationPath -Raw | ConvertFrom-Json
$frameworkNames = @($runtimeConfiguration.runtimeOptions.frameworks | ForEach-Object { $_.name })
if ('Microsoft.NETCore.App' -notin $frameworkNames -or 'Microsoft.AspNetCore.App' -notin $frameworkNames) {
    throw 'Windows Agent runtimeconfig must require both Microsoft.NETCore.App and Microsoft.AspNetCore.App.'
}

& $InnoCompiler "/DPublishDir=$publishDirectory" "/DPackageVersion=$Version" $installerScript
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
Write-Output (Join-Path $repositoryRoot "artifacts\installer\RelayLink-Agent-win-x64-$Version.exe")
