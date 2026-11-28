[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [Parameter(Mandatory = $true)][string]$ConfigurationPath,
    [string]$ServiceName = 'RelayLinkServer',
    [string]$ServiceAccount = 'NT AUTHORITY\LocalService'
)

$ErrorActionPreference = 'Stop'
$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator privileges are required to install the Server service.'
}

$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$resolvedConfiguration = (Resolve-Path -LiteralPath $ConfigurationPath).Path

& $resolvedExecutable --config $resolvedConfiguration --check-config
if ($LASTEXITCODE -ne 0) { throw 'Server configuration validation failed.' }

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Windows Service '$ServiceName' already exists. Stop and remove it explicitly before reinstalling."
}

$binaryPath = '"{0}" --config "{1}"' -f $resolvedExecutable, $resolvedConfiguration
$created = $false
try {
    $createResult = Invoke-CimMethod -ClassName Win32_Service -MethodName Create -Arguments @{
        Name = $ServiceName
        DisplayName = 'RelayLink Server'
        PathName = $binaryPath
        ServiceType = [uint32]16
        ErrorControl = [uint32]1
        StartMode = 'Automatic'
        DesktopInteract = $false
        StartName = $ServiceAccount
    }
    if ($createResult.ReturnValue -ne 0) {
        throw "Could not create the Server service (Win32_Service.Create return code $($createResult.ReturnValue))."
    }
    $created = $true

    & sc.exe description $ServiceName 'RelayLink reverse TCP / UDP proxy server' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not set the Server service description (sc.exe exit code $LASTEXITCODE)." }

    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not configure Server service recovery (sc.exe exit code $LASTEXITCODE)." }

    & sc.exe failureflag $ServiceName 1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not enable Server recovery for non-crash failures (sc.exe exit code $LASTEXITCODE)." }

    Start-Service -Name $ServiceName
} catch {
    if ($created) {
        Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
        & sc.exe delete $ServiceName | Out-Null
    }
    throw
}
