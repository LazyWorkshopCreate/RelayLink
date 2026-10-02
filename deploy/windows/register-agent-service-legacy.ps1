[CmdletBinding()]
param(
    [string]$ExecutablePath = 'C:\Program Files\RelayLink\Agent\RelayLink.Agent.exe',
    [string]$ConfigurationPath = 'C:\ProgramData\RelayLink\Agent\agent.json',
    [string]$ServiceName = 'RelayLinkAgent',
    [switch]$ReplaceExisting
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated Windows PowerShell session.'
}

$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$resolvedConfiguration = (Resolve-Path -LiteralPath $ConfigurationPath).Path
if ([IO.Path]::GetExtension($resolvedConfiguration) -ne '.json') {
    throw 'ConfigurationPath must point to an Agent JSON configuration file.'
}

& $resolvedExecutable --config $resolvedConfiguration --check-config
if ($LASTEXITCODE -ne 0) {
    throw 'Agent configuration validation failed. The service was not changed.'
}

$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    if (-not $ReplaceExisting) {
        throw "Service '$ServiceName' already exists. Inspect it first, or rerun with -ReplaceExisting."
    }

    if ($existingService.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -ErrorAction Stop
        $existingService.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }

    # ServiceController keeps an SCM handle open. On older Windows versions,
    # deleting while that handle is open can leave the service marked for
    # deletion and make an immediate create fail with Win32 error 1072.
    $existingService.Dispose()
    $existingService = $null

    $deleteOutput = @(& sc.exe delete $ServiceName 2>&1)
    $deleteExitCode = $LASTEXITCODE
    $deleteOutput | Out-Host
    if ($deleteExitCode -ne 0) {
        throw "Could not delete the existing service '$ServiceName' (sc.exe exit code $deleteExitCode): $($deleteOutput -join ' ')"
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 500
        $remainingService = Get-WmiObject -Class Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
    } while ($remainingService -and [DateTime]::UtcNow -lt $deadline)
    if ($remainingService) {
        throw "Service '$ServiceName' is still pending deletion. Reboot Windows, then run the script again."
    }
}

$binaryPath = '"' + $resolvedExecutable + '" --config "' + $resolvedConfiguration + '"'
$serviceClass = Get-WmiObject -List -Class Win32_Service
$createResult = $serviceClass.Create(
    $ServiceName,
    'RelayLink Agent',
    $binaryPath,
    16,
    1,
    'Automatic',
    $false,
    'NT AUTHORITY\LocalService',
    $null,
    $null,
    $null,
    $null)
if ($createResult.ReturnValue -ne 0) {
    $createMeaning = switch ([int]$createResult.ReturnValue) {
        2 { 'Access denied' }
        8 { 'Unknown failure' }
        9 { 'Executable path not found' }
        14 { 'Service is disabled' }
        16 { 'Service is marked for deletion or another SCM failure occurred' }
        22 { 'Account is invalid or lacks service logon rights' }
        23 { 'The requested service already exists' }
        default { 'See the Win32_Service.Create return codes' }
    }
    throw "Could not create service '$ServiceName' (Win32_Service.Create return code $($createResult.ReturnValue): $createMeaning)."
}

$created = $true
try {
    & sc.exe description $ServiceName 'RelayLink reverse TCP proxy agent' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Could not set the service description.' }

    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure service recovery.' }

    & sc.exe failureflag $ServiceName 1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Could not enable recovery for non-crash failures.' }

    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    & sc.exe qc $ServiceName | Out-Host
    Write-Output "Service '$ServiceName' is running."
} catch {
    if ($created) {
        Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
        & sc.exe delete $ServiceName | Out-Null
    }
    throw
}
