# PoSeeReview's post-deploy smoke. The shared .github/workflows/deploy.yml runs it inside the deploy
# job, after the health gate, on a fresh ubuntu-latest runner (Node preinstalled); a failure here rolls
# the deploy back. Anonymous only: every URL it touches is AllowAnonymous, so it needs no credentials.
#
#   pwsh ./scripts/post-deploy-smoke.ps1 -BaseUrl https://app-poseereview2.azurewebsites.net

param([Parameter(Mandatory)][string]$BaseUrl)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$BaseUrl = $BaseUrl.TrimEnd('/')

function Invoke-Native([string]$What, [scriptblock]$Command) {
    Write-Host "== $What"
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit $LASTEXITCODE)." }
}

# post-deploy-smoke.mjs imports 'playwright'; installed here, not saved to any package.json.
Invoke-Native 'Install Playwright' { npm install --no-save --no-audit --no-fund playwright@1.49.0 }
Invoke-Native 'Install Chromium' { npx playwright install --with-deps chromium }

# NET_RULES 5.4: Blazor render tree initialises, header branding renders, no boot console errors,
# /health not Unhealthy.
$env:BASE_URL = $BaseUrl
Invoke-Native 'Smoke checks' { node scripts/post-deploy-smoke.mjs }

# Budgets that fail the run: layout shift (the boot splash once cost 0.20 CLS on every load) and the
# accessibility score. Performance and LCP only warn: an F1 cold start and a WASM boot under mobile
# throttling swing them run to run. /login because it is the one page an anonymous run can render.
Invoke-Native 'Lighthouse budgets' {
    npx --yes @lhci/cli@0.15.1 autorun --config=scripts/lighthouserc.json "--collect.url=$BaseUrl/login"
}

Write-Host 'Post-deploy smoke passed.'
