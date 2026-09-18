function Get-AgentMonitorShortcutPath {
    param(
        [string]$DesktopDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonDesktopDirectory)
    )

    if ([string]::IsNullOrWhiteSpace($DesktopDirectory)) { throw 'Could not locate the shared desktop.' }
    return Join-Path $DesktopDirectory 'RelayLink Agent Monitor.url'
}

function New-AgentMonitorShortcut {
    param(
        [Parameter(Mandatory = $true)][ValidateRange(0, 65535)][int]$DashboardPort,
        [string]$DesktopDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonDesktopDirectory)
    )

    if ($DashboardPort -eq 0) { return }
    $shortcutPath = Get-AgentMonitorShortcutPath -DesktopDirectory $DesktopDirectory
    $content = "[InternetShortcut]`r`nURL=http://127.0.0.1:${DashboardPort}/`r`n;RelayLinkAgentMonitor=1`r`n"
    if (Test-Path -LiteralPath $shortcutPath) {
        if ([IO.File]::ReadAllText($shortcutPath) -ne $content) {
            throw "Desktop shortcut already exists and is not managed by this installer: $shortcutPath"
        }
        return
    }
    [IO.File]::WriteAllText($shortcutPath, $content, [Text.Encoding]::ASCII)
}

function Remove-AgentMonitorShortcut {
    param(
        [string]$DesktopDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonDesktopDirectory)
    )

    $shortcutPath = Get-AgentMonitorShortcutPath -DesktopDirectory $DesktopDirectory
    if (-not (Test-Path -LiteralPath $shortcutPath)) { return }
    $content = [IO.File]::ReadAllText($shortcutPath)
    if ($content -match '^\[InternetShortcut\]\r?\nURL=http://127\.0\.0\.1:[1-9][0-9]{0,4}/\r?\n;RelayLinkAgentMonitor=1\r?\n$') {
        Remove-Item -LiteralPath $shortcutPath
    }
}
