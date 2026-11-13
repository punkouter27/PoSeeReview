# PoSeeReview's extra build-time check. The shared .github/workflows/deploy.yml runs this from the
# repo root after the Release build and the Unit tier; a failure fails the build (and so the deploy).

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

# Tier quotas (NET_RULES 5.1: Unit 100 / Integration 50 / E2EAPI 25 / E2EUI 25), counted in
# [Fact]/[Theory] methods. `dotnet publish` never reads tests/, so this is the only place a suite
# that quietly grew past its cap is caught.
node scripts/check-test-quota.mjs
if ($LASTEXITCODE -ne 0) { throw "Test tier quota check failed (exit $LASTEXITCODE)." }
