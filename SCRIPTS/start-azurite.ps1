# Starts the local storage emulator that Development uses by default (Storage:UseAzurite = true).
# Docker first (docker-compose.yml's official image); when Docker is not running, the npm build of
# Azurite instead, so a stopped Docker Desktop never blocks a local run. Idempotent: does nothing
# if something already answers on the blob port.

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

function Test-Listening([int] $port) {
    $client = [Net.Sockets.TcpClient]::new()
    try { return $client.ConnectAsync('127.0.0.1', $port).Wait(500) } catch { return $false } finally { $client.Dispose() }
}

if (Test-Listening 10000) {
    Write-Host 'Azurite is already running.'
    exit 0
}

docker info *> $null
if ($LASTEXITCODE -eq 0) {
    docker compose -f (Join-Path $root 'docker-compose.yml') up -d azurite
    exit $LASTEXITCODE
}

Write-Host 'Docker is not running; starting Azurite from npm instead.'
$data = Join-Path $root 'azurite'   # gitignored
New-Item -ItemType Directory -Force $data | Out-Null
# npx.cmd on Windows: resolve it, Start-Process does not search PATHEXT for scripts.
Start-Process -WindowStyle Hidden -FilePath (Get-Command npx).Source -ArgumentList @(
    '--yes', 'azurite@3', '--silent', '--skipApiVersionCheck', '--location', "`"$data`"")

# The first npx run downloads Azurite, so allow two minutes.
foreach ($i in 1..240) {
    if (Test-Listening 10000) { Write-Host 'Azurite is up.'; exit 0 }
    Start-Sleep -Milliseconds 500
}
Write-Error 'Azurite did not start within two minutes.'
