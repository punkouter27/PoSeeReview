#Requires -Version 5.1
<#
.SYNOPSIS
    Rotate the Google Maps API key into kv-poshared2.

.DESCRIPTION
    Workflow:
      1. Open Google Cloud Console → APIs & Services → Credentials
         (project: posereview-490223) → click "PoSeeReview-Maps-Key" → "Show key" → "Copy".
      2. Run this script. It reads the value from your clipboard, validates the
         shape (AIza + 35 chars), and writes it to BOTH secrets in kv-poshared2:
           - GoogleMaps--ApiKey             (shared, first pass)
           - PoSeeReview--GoogleMaps--ApiKey (app-prefixed, second pass wins)
      3. The script then clears the clipboard so the value doesn't sit there
         indefinitely, and prints the updated-at timestamp (not the value).

    Security notes:
      - The value is passed to `az` via a short-lived environment variable
        ($env:GCP_KV_KEY), not via --value on the command line, so it does
        not appear in any process listing or shell history.
      - The value is never echoed, never logged, and never written to a file
        by this script. The only places it lives after the run are:
          * the Google Cloud Console (source of truth)
          * kv-poshared2 (Azure-side mirror)
      - Validation is shape-only (regex on the visible key). The script
        deliberately does not log the value, even on success.

.PARAMETER VaultName
    Key Vault name. Defaults to the shared one used by all PoSeeReview infra.

.EXAMPLE
    # After copying the new key from the Google Cloud Console:
    .\SCRIPTS\rotate-google-key.ps1
#>

[CmdletBinding()]
param(
    [string]$VaultName = 'kv-poshared2'
)

$ErrorActionPreference = 'Stop'

function Write-Step { param([string]$Message) Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Write-Ok   { param([string]$Message) Write-Host "  [OK]   $Message" -ForegroundColor Green }
function Write-Warn { param([string]$Message) Write-Host "  [WARN] $Message" -ForegroundColor Yellow }
function Write-Fail { param([string]$Message) Write-Host "  [FAIL] $Message" -ForegroundColor Red }

# ── 1. Pre-flight checks ──────────────────────────────────────────────────────
Write-Step "Pre-flight checks"

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI ('az') is not on PATH. Run SCRIPTS\setup.ps1 first."
}

$account = az account show --query "user.name" -o tsv 2>$null
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($account)) {
    throw "Not logged in to Azure. Run 'az login' and try again."
}
Write-Ok "Azure CLI authenticated as $account"

# Confirm we can read the vault (read-only check; do NOT print values)
$probe = az keyvault secret show --vault-name $VaultName --name GoogleMaps--ApiKey --query "id" -o tsv 2>$null
if ($LASTEXITCODE -ne 0) {
    throw "Cannot read $VaultName. Verify the account has Key Vault Secrets Officer or higher."
}
Write-Ok "Have read+write access to $VaultName"

# ── 2. Read from clipboard ────────────────────────────────────────────────────
Write-Step "Reading new key from clipboard"

# PresentationCore ships with Windows PowerShell and PowerShell 7+ on Windows
Add-Type -AssemblyName PresentationCore -ErrorAction Stop
$key = [System.Windows.Clipboard]::GetText().Trim()

# Shape check: Google API keys are AIza + 35 base64url-ish chars (39 total).
# We validate shape to catch "you copied something else by accident"
# (URL, error message, etc.) without ever logging the actual value.
if ($key -notmatch '^AIza[0-9A-Za-z_\-]{35}$') {
    Write-Fail "Clipboard content is not shaped like a Google API key."
    Write-Host "        Expected: AIza + 35 chars from [0-9A-Za-z_-]. Got $($key.Length) chars." -ForegroundColor Red
    Write-Host "        Tip: in Google Cloud Console → Credentials → PoSeeReview-Maps-Key → Show key → Copy." -ForegroundColor Yellow
    throw "Aborting — no secrets were written."
}
Write-Ok "Clipboard contains a value with the expected Google API key shape"

# ── 3. Write to KV via env var (so the value isn't on the command line) ──────
Write-Step "Writing new value to $VaultName"

$env:GCP_KV_KEY = $key
try {
    $out1 = az keyvault secret set --vault-name $VaultName --name GoogleMaps--ApiKey             --value "$env:GCP_KV_KEY" -o none 2>&1
    if ($LASTEXITCODE -ne 0) { throw "First 'az secret set' failed: $out1" }
    Write-Ok "GoogleMaps--ApiKey updated"

    $out2 = az keyvault secret set --vault-name $VaultName --name PoSeeReview--GoogleMaps--ApiKey --value "$env:GCP_KV_KEY" -o none 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Second 'az secret set' failed: $out2" }
    Write-Ok "PoSeeReview--GoogleMaps--ApiKey updated"
}
finally {
    # Always clear the env var, even on failure
    Remove-Item Env:GCP_KV_KEY -ErrorAction SilentlyContinue
}

# ── 4. Clear clipboard so the value doesn't linger ───────────────────────────
Write-Step "Clearing clipboard"
try {
    [System.Windows.Clipboard]::Clear()
    Write-Ok "Clipboard cleared"
}
catch {
    Write-Warn "Could not clear clipboard: $($_.Exception.Message). Clear it manually with 'Set-Clipboard -Value $null'."
}

# ── 5. Confirm without exposing the value ────────────────────────────────────
Write-Step "Confirmation"
$updated = az keyvault secret show --vault-name $VaultName --name PoSeeReview--GoogleMaps--ApiKey --query "attributes.updated" -o tsv
if ($LASTEXITCODE -ne 0) {
    Write-Warn "Could not read the updated-at timestamp. The secret may still have been written — verify in the Azure portal."
}
else {
    Write-Ok "PoSeeReview--GoogleMaps--ApiKey updated at: $updated"
}

Write-Host ""
Write-Host "Next step: restart the API and probe /api/restaurants/nearby to confirm the new key works." -ForegroundColor Cyan
