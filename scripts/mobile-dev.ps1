#requires -Version 7
<#
.SYNOPSIS
  Dev loop for the native app (src/PoSeeReview.Mobile) against a local API.
.DESCRIPTION
  1. Finds adb and the attached Android device (USB phone or emulator).
  2. adb reverse: the device's localhost:5000 -> this PC's API port, so the Debug app's default
     base URL (http://localhost:5000) reaches the local API with no IP, firewall or cert setup.
  3. Optionally builds, installs and launches the Debug app (-Run).
  The API must already be running (e.g. mock mode, see CLAUDE.md) on -ApiPort.
.EXAMPLE
  pwsh scripts/mobile-dev.ps1 -ApiPort 7208 -Run
.EXAMPLE
  pwsh scripts/mobile-dev.ps1 -Link poseereview://comic/ChIJMockPikeChowder001   # open a comic link in the app
#>
param(
    [int] $ApiPort = 5000,
    [switch] $Run,
    [string] $Link
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$adb = @(
    (Get-Command adb -ErrorAction SilentlyContinue)?.Source
    "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"
    "${env:ProgramFiles(x86)}\Android\android-sdk\platform-tools\adb.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $adb) { throw 'adb not found. Install the Android SDK platform-tools (Visual Studio or Android Studio).' }

$devices = & $adb devices | Select-Object -Skip 1 | Where-Object { $_ -match '\tdevice$' }
if (-not $devices) { throw 'No Android device attached. Plug in a phone with USB debugging on, or start an emulator.' }
Write-Host "Device: $(($devices | Select-Object -First 1) -replace '\t.*')"

& $adb reverse tcp:5000 "tcp:$ApiPort" | Out-Null
Write-Host "adb reverse: device localhost:5000 -> PC localhost:$ApiPort"

try { $null = Invoke-WebRequest "http://localhost:$ApiPort/health/live" -TimeoutSec 3 }
catch { Write-Warning "Nothing answered on http://localhost:$ApiPort/health/live — start the API first." }

if ($Run) {
    dotnet build "$root/src/PoSeeReview.Mobile/PoSeeReview.Mobile.csproj" -f net10.0-android -c Debug -t:Run
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
}

if ($Link) {
    & $adb shell am start -a android.intent.action.VIEW -d $Link com.punkouter.poseereview | Out-Null
    Write-Host "Opened $Link"
}
