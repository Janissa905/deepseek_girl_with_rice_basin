# DSH desktop pet watchdog: starts the pet when DeepSeek Harness is running,
# stops it when the app exits. Runs hidden from the Startup shortcut.
$ErrorActionPreference = "SilentlyContinue"

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $dir "DSHPet.exe"
$flag = Join-Path $dir "stopped.flag"

$mutex = New-Object System.Threading.Mutex($false, "Local\DshPetWatcher")
if (-not $mutex.WaitOne(0)) { exit }

$wasRunning = $false
while ($true) {
    $dsh = Get-Process -Name "DeepSeek Harness" -ErrorAction SilentlyContinue | Select-Object -First 1
    $pet = Get-Process -Name "DSHPet" -ErrorAction SilentlyContinue | Select-Object -First 1

    if ($dsh) {
        $wasRunning = $true
        if (-not $pet -and -not (Test-Path $flag)) {
            Start-Process -FilePath $exe -WorkingDirectory $dir
        }
    }
    else {
        if ($wasRunning -and $pet) {
            $pet.CloseMainWindow() | Out-Null
            Start-Sleep -Milliseconds 800
            $pet.Refresh()
            if (-not $pet.HasExited) { Stop-Process -Id $pet.Id -Force }
        }
        $wasRunning = $false
        if (Test-Path $flag) { Remove-Item $flag -Force }
    }

    Start-Sleep -Seconds 2
}
