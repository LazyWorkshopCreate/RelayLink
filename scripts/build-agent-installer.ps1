[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [string]$InnoCompiler,
    [ValidateSet('Both', 'SelfContained', 'FrameworkDependent')]
    [string]$Mode = 'Both',
    [string]$PublishRoot
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    $PublishRoot = Join-Path $repositoryRoot ('artifacts\installer-publish\' + [guid]::NewGuid().ToString('N'))
}
$PublishRoot = [System.IO.Path]::GetFullPath($PublishRoot, $repositoryRoot)
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

if ($Mode -eq 'Both') { $modes = @('SelfContained', 'FrameworkDependent') } else { $modes = @($Mode) }
foreach ($currentMode in $modes) {
    $modeName = if ($currentMode -eq 'SelfContained') { 'self-contained' } else { 'framework-dependent' }
    $publishDirectory = Join-Path $PublishRoot ("win-x64\$modeName\RelayLink.Agent")
    if (Test-Path -LiteralPath $publishDirectory) {
        throw "Publish destination already exists; choose an empty PublishRoot: $publishDirectory"
    }
    & (Join-Path $PSScriptRoot 'publish.ps1') -RuntimeIdentifier win-x64 -Component Agent -WindowsAgentMode $currentMode -OutputRoot $PublishRoot
    if ($LASTEXITCODE -ne 0) { throw "Agent publish failed: $currentMode." }
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'RelayLink.Agent.exe'))) { throw 'Agent executable is missing from publish output.' }
    foreach ($webFile in @('wwwroot/index.html', 'wwwroot/assets/app.js', 'wwwroot/assets/index.css')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $webFile))) { throw "Agent frontend file is missing from publish output: $webFile" }
    }
    foreach ($migratorFile in @('RelayLink.Agent.ConfigMigrator.exe', 'RelayLink.Agent.ConfigMigrator.dll', 'RelayLink.Agent.ConfigMigrator.runtimeconfig.json', 'RelayLink.Agent.ConfigMigrator.deps.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $migratorFile))) {
            throw "Agent configuration migrator file is missing from publish output: $migratorFile"
        }
    }
    $runtimeConfigurationPath = Join-Path $publishDirectory 'RelayLink.Agent.runtimeconfig.json'
    if (-not (Test-Path -LiteralPath $runtimeConfigurationPath)) { throw 'Agent runtimeconfig is missing.' }
    foreach ($runtimeFile in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) {
        $present = Test-Path -LiteralPath (Join-Path $publishDirectory $runtimeFile)
        if (($currentMode -eq 'SelfContained') -ne $present) {
            throw "Windows Agent $currentMode publish has unexpected runtime file state: $runtimeFile"
        }
    }
    # Keep the installer's root-directory cleanup in sync with publish output.
    foreach ($publishedItem in (Get-ChildItem -LiteralPath $publishDirectory -Force)) {
        if (($publishedItem.PSIsContainer -and $publishedItem.Name -ne 'wwwroot') -or
            (-not $publishedItem.PSIsContainer -and $publishedItem.Name -notmatch '^(?:.*\.(?:dll|pdb)|RelayLink\.Agent.*\.(?:exe|json)|createdump\.exe|web\.config)$')) {
            throw "Windows Agent publish contains a file not covered by installer cleanup: $($publishedItem.Name)"
        }
    }
    if ($currentMode -eq 'FrameworkDependent') {
        $runtimeConfiguration = Get-Content -LiteralPath $runtimeConfigurationPath -Raw | ConvertFrom-Json
        $frameworkNames = @($runtimeConfiguration.runtimeOptions.frameworks | ForEach-Object { $_.name })
        if ('Microsoft.NETCore.App' -notin $frameworkNames -or 'Microsoft.AspNetCore.App' -notin $frameworkNames) {
            throw 'Framework-dependent Agent must require both Microsoft.NETCore.App and Microsoft.AspNetCore.App.'
        }
    }
    $archiveDirectory = Join-Path $repositoryRoot 'artifacts\release'
    New-Item -ItemType Directory -Path $archiveDirectory -Force | Out-Null
    $archivePath = Join-Path $archiveDirectory "RelayLink-Agent-win-x64-$modeName-$Version.zip"
    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archivePath -Force
    $selfContainedDefine = if ($currentMode -eq 'SelfContained') { '1' } else { '0' }
    & $InnoCompiler "/DPublishDir=$publishDirectory" "/DPackageVersion=$Version" "/DPackageMode=$modeName" "/DPackageSelfContained=$selfContainedDefine" $installerScript
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed: $currentMode." }
    Write-Output $archivePath
    Write-Output (Join-Path $repositoryRoot "artifacts\installer\RelayLink-Agent-win-x64-$modeName-$Version.exe")
}
