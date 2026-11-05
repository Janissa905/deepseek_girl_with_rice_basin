# Install the DSH desktop pet: register the hidden watchdog at logon (HKCU Run)
# and launch it now.
$ErrorActionPreference = "Stop"

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $dir "DSHPet.exe"
$wscript = Join-Path $env:WINDIR "System32\wscript.exe"
$vbs = Join-Path $dir "watch.vbs"

if (-not (Test-Path $exe)) { throw "DSHPet.exe not found in $dir" }

# Logon autostart lives in the registry, so no file has to be placed on C:.
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
Set-ItemProperty -Path $runKey -Name "DSHDesktopPet" -Value ('"' + $wscript + '" "' + $vbs + '"')

# Restart a previously running watchdog so it picks up this folder.
Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -match 'watch\.ps1"?\s*$' -and $_.ProcessId -ne $PID } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

Remove-Item (Join-Path $dir "stopped.flag") -Force -ErrorAction SilentlyContinue

# Keepalive task: the watchdog itself is started by Task Scheduler, so closing
# DSH cannot take it down with the app's process tree. wscript is a GUI
# subsystem host, so the minute check never flashes a console window.
$taskName = "DSH Desktop Pet Watchdog"
$tr = 'wscript.exe "' + (Join-Path $dir "watch.vbs") + '"'
& schtasks.exe /Create /TN $taskName /TR $tr /SC MINUTE /MO 1 /IT /F | Out-Null
& schtasks.exe /Run /TN $taskName | Out-Null

Start-Sleep -Seconds 2
$dsh = Get-Process -Name "DeepSeek Harness" -ErrorAction SilentlyContinue | Select-Object -First 1
$pet = Get-Process -Name "DSHPet" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($dsh -and -not $pet) {
    Start-Process -FilePath $exe -WorkingDirectory $dir
}

Write-Host "DSH 桌宠已安装。"
Write-Host "  程序目录: $dir"
Write-Host "  登录启动: HKCU Run\DSHDesktopPet"
Write-Host "  守护任务: $taskName（每分钟检查一次，DSH 重启也能拉起桌宠）"
Write-Host "  DSH 启动后桌宠会自动出现；本窗口可直接关闭。"
