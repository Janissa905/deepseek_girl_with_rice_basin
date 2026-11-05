# Stop the pet and remove its logon entry. Program files are kept.
$ErrorActionPreference = "SilentlyContinue"

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$startup = [Environment]::GetFolderPath("Startup")
$lnk = Join-Path $startup "DSH 桌宠.lnk"
Remove-Item $lnk -Force
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "DSHDesktopPet" -Force
& schtasks.exe /Delete /TN "DSH Desktop Pet Watchdog" /F | Out-Null
& schtasks.exe /Delete /TN "DSH Desktop Pet Watchdog Logon" /F | Out-Null

Get-CimInstance Win32_Process |
    Where-Object { $_.CommandLine -match 'watch\.ps1"?\s*$' -and $_.ProcessId -ne $PID } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

Get-Process -Name "DSHPet" | Stop-Process -Force

Write-Host "已停止桌宠并移除开机启动。程序文件仍保留在: $dir"
