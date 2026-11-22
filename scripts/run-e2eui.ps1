# Runs the browser tier (E2EUI) against the TRIMMED RELEASE publish - the same build that deploys -
# so a trimming or publish-only fault shows up here, not in production.
#
#   pwsh scripts/run-e2eui.ps1 [-Port 7318] [-Filter <xunit filter>] [-Headed]
#
# 1. dotnet publish -c Release (client baked as "Test", so the guest sign-in exists; same trimming)
# 2. Azurite in Docker on 21000-21002 (its own container, removed afterwards)
# 3. the published app (ASPNETCORE_ENVIRONMENT=Test, UseMockData=true: no secrets, nothing billed)
# 4. dotnet test tests/PoSeeReview.E2EUI against it, then stops everything it started.
param(
    [int] $Port = 7318,
    [string] $Filter = '',
    [switch] $Headed
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path ([IO.Path]::GetTempPath()) "poseereview-e2eui-$Port"
$container = "poseereview-e2eui-azurite-$Port"
$baseUrl = "http://localhost:$Port"
$app = $null

try {
    Write-Host "Publishing the Release build to $publish"
    if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
    dotnet publish (Join-Path $root 'src/PoSeeReview.Api/PoSeeReview.Api.csproj') -c Release -o $publish `
        -p:WasmApplicationEnvironmentName=Test --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

    docker info *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Docker is not running (Azurite needs it).' }
    docker rm -f $container *> $null
    docker run -d --rm --name $container -p 21000:10000 -p 21001:10001 -p 21002:10002 `
        mcr.microsoft.com/azure-storage/azurite:latest azurite --blobHost 0.0.0.0 --queueHost 0.0.0.0 `
        --tableHost 0.0.0.0 --skipApiVersionCheck --loose | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Azurite did not start' }

    $env:ASPNETCORE_ENVIRONMENT = 'Test'
    $env:ASPNETCORE_URLS = $baseUrl
    $env:UseMockData = 'true'
    $env:KeyVault__Uri = ''
    $env:Storage__UseAzurite = 'true'
    $env:Storage__AzuritePorts = '21000,21001,21002'
    $app = Start-Process dotnet -ArgumentList (Join-Path $publish 'PoSeeReview.Api.dll') -WorkingDirectory $publish `
        -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $publish 'app.out.log') -RedirectStandardError (Join-Path $publish 'app.err.log')

    $up = $false
    foreach ($i in 1..60) {
        try { if ((Invoke-WebRequest "$baseUrl/health/live" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) { $up = $true; break } } catch { }
        Start-Sleep -Seconds 1
    }
    if (-not $up) { throw "The published app did not answer on $baseUrl (see $publish/app.*.log)" }
    Write-Host "Published app is up on $baseUrl"

    $env:E2E_BASE_URL = $baseUrl
    if ($Headed) { $env:HEADED = '1' }
    $testArgs = @('test', (Join-Path $root 'tests/PoSeeReview.E2EUI'), '-c', 'Release', '--nologo')
    if ($Filter) { $testArgs += @('--filter', $Filter) }
    dotnet @testArgs
    $code = $LASTEXITCODE
}
finally {
    if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force }
    docker rm -f $container *> $null
    foreach ($name in 'ASPNETCORE_ENVIRONMENT', 'ASPNETCORE_URLS', 'UseMockData', 'KeyVault__Uri', 'Storage__UseAzurite', 'Storage__AzuritePorts', 'E2E_BASE_URL', 'HEADED') {
        Remove-Item "Env:$name" -ErrorAction SilentlyContinue
    }
}
exit $code
