# Removes WinStatusBar (autostart entries and the installed exe).
#   irm https://raw.githubusercontent.com/umefor-labs/windows-statusbar-messenger/main/tools/uninstall.ps1 | iex
param([string]$AppData = $env:LOCALAPPDATA)

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $self = Join-Path $env:TEMP 'WinStatusBar-uninstall.ps1'
    if ($PSCommandPath) { Copy-Item -Force $PSCommandPath $self }
    else { Invoke-WebRequest -UseBasicParsing 'https://raw.githubusercontent.com/umefor-labs/windows-statusbar-messenger/main/tools/uninstall.ps1' -OutFile $self }
    Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$self`"", '-AppData', "`"$AppData`""
    'WinStatusBar removed.'
    return
}

Unregister-ScheduledTask -TaskName 'WinStatusBar' -Confirm:$false -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName 'WinStatusBar Sensors' -Confirm:$false -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'WinStatusBar' -ErrorAction SilentlyContinue
Get-Process WinStatusBar -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null; $_.WaitForExit(5000) | Out-Null }
Get-Process WinStatusBar -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
Remove-Item -Recurse -Force (Join-Path $AppData 'Programs\WinStatusBar') -ErrorAction SilentlyContinue
