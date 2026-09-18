[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [Parameter(Mandatory = $true)][string]$ConfigurationPath,
    [string]$ServiceName = 'RelayLinkServer'
)

$ErrorActionPreference = 'Stop'
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$resolvedConfiguration = (Resolve-Path -LiteralPath $ConfigurationPath).Path

& $resolvedExecutable --config $resolvedConfiguration --check-config
if ($LASTEXITCODE -ne 0) { throw 'Server configuration validation failed.' }

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Windows Service '$ServiceName' already exists. Stop and remove it explicitly before reinstalling."
}

$binaryPath = '"{0}" --config "{1}"' -f $resolvedExecutable, $resolvedConfiguration
New-Service -Name $ServiceName -BinaryPathName $binaryPath -DisplayName 'RelayLink Server' -Description 'RelayLink reverse TCP proxy server' -StartupType Automatic
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null
Start-Service -Name $ServiceName
