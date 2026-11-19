# Installs WinStatusBar for the current user.
#   One-line install (PowerShell):
#     irm https://raw.githubusercontent.com/umefor-labs/windows-statusbar-messenger/main/tools/install.ps1 | iex
#   From a local build: .\tools\install.ps1 -Source .\publish\WinStatusBar.exe
#
# - The bar starts at logon with normal rights (HKCU Run), so its web logins work.
# - A small helper (WinStatusBar.exe --sensors) starts at logon with admin rights via a
#   scheduled task, only to read the CPU temperature into sensors.json.
param(
    [string]$Source,
    [string]$User = "$env:USERDOMAIN\$env:USERNAME",
    [string]$AppData = $env:LOCALAPPDATA
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repo = 'umefor-labs/windows-statusbar-messenger'
$log = Join-Path $env:TEMP 'WinStatusBar-install.log'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    # Re-run this script elevated. Under `irm | iex` there is no script file, so fetch it again.
    $self = Join-Path $env:TEMP 'WinStatusBar-install.ps1'
    if ($PSCommandPath) { Copy-Item -Force $PSCommandPath $self }
    else { Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/$repo/main/tools/install.ps1" -OutFile $self }
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$self`"",
        '-User', "`"$User`"", '-AppData', "`"$AppData`"")
    if ($Source) { $argList += @('-Source', "`"$((Resolve-Path $Source).Path)`"") }
    Write-Host 'WinStatusBar: asking Windows for admin rights to install...'
    $p = Start-Process powershell -Verb RunAs -ArgumentList $argList -Wait -PassThru
    Get-Content $log -ErrorAction SilentlyContinue
    return
}

function Say($msg) { Write-Host "WinStatusBar: $msg"; Add-Content -Path $log -Value $msg }

function Has-DesktopRuntime10 {
    try { return [bool](& dotnet --list-runtimes 2>$null | Select-String 'Microsoft\.WindowsDesktop\.App 10\.') }
    catch { return $false }
}

$dir = Join-Path $AppData 'Programs\WinStatusBar'
$exe = Join-Path $dir 'WinStatusBar.exe'
Set-Content -Path $log -Value "Install started $(Get-Date -Format s)"

try {
    # 1. .NET 10 Desktop Runtime (the exe is framework-dependent).
    if (-not (Has-DesktopRuntime10)) {
        Say 'installing .NET 10 Desktop Runtime...'
        $rt = Join-Path $env:TEMP 'windowsdesktop-runtime-10-win-x64.exe'
        Invoke-WebRequest -UseBasicParsing 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe' -OutFile $rt
        Start-Process $rt -ArgumentList '/install', '/quiet', '/norestart' -Wait
    }

    # 2. PawnIO driver, so the CPU temperature can be read (optional).
    # winget skips it quickly when it is already installed.
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        Say 'checking PawnIO driver (CPU temperature)...'
        & winget install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements | Out-Null
    }
    else { Say 'winget not found: skipping PawnIO, CPU temperature will show "--".' }

    # 3. Get the exe: local build if given, otherwise the latest GitHub release.
    if (-not $Source) {
        Say 'downloading the latest release...'
        $Source = Join-Path $env:TEMP 'WinStatusBar-download.exe'
        Invoke-WebRequest -UseBasicParsing "https://github.com/$repo/releases/latest/download/WinStatusBar.exe" -OutFile $Source
    }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null

    # 4. Close the running bar (it releases its screen strip on close) and the sensors helper.
    if (Get-Process WinStatusBar -ErrorAction SilentlyContinue) {
        Add-Type 'using System;using System.Runtime.InteropServices;public class WsbW{[DllImport("user32.dll",CharSet=CharSet.Unicode)]public static extern IntPtr FindWindow(string c,string t);[DllImport("user32.dll")]public static extern bool PostMessage(IntPtr h,int m,IntPtr w,IntPtr l);}'
        $h = [WsbW]::FindWindow($null, 'WinStatusBar')
        if ($h -ne [IntPtr]::Zero) { [WsbW]::PostMessage($h, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
        Start-Sleep -Seconds 2
        Get-Process WinStatusBar -ErrorAction SilentlyContinue | Stop-Process -Force
    }

    # 5. Copy. A just-exited exe can stay locked for a moment, so retry; as a last resort
    #    rename the old file out of the way (Windows allows renaming a running exe).
    Get-ChildItem $dir -Filter 'WinStatusBar.old*.exe' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 20; $i++) {
        try { Copy-Item -Force $Source $exe; break }
        catch {
            if ($i -eq 10 -and (Test-Path $exe)) { Move-Item -Force $exe (Join-Path $dir "WinStatusBar.old$i.exe") }
            elseif ($i -eq 19) { throw }
            Start-Sleep -Milliseconds 500
        }
    }

    # 6. Autostart: sensors helper elevated via a task, the bar itself with normal rights.
    Unregister-ScheduledTask -TaskName 'WinStatusBar' -Confirm:$false -ErrorAction SilentlyContinue
    $action = New-ScheduledTaskAction -Execute $exe -Argument '--sensors' -WorkingDirectory $dir
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $User
    $principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -Priority 4
    Register-ScheduledTask -TaskName 'WinStatusBar Sensors' -Action $action -Trigger $trigger `
        -Principal $principal -Settings $settings -Force | Out-Null

    Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'WinStatusBar' -Value "`"$exe`""

    Start-ScheduledTask -TaskName 'WinStatusBar Sensors'
    # explorer.exe starts the bar with the user's normal (non-admin) rights.
    Start-Process explorer.exe -ArgumentList "`"$exe`""
    Say "OK: installed to $exe"
}
catch {
    Say "FAIL: $($_.Exception.Message)"
    exit 1
}
