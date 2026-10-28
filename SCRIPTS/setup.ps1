#Requires -Version 5.1
<#
.SYNOPSIS
    PoSeeReview first-run setup script.
    Ensures all prerequisites are installed and configured for local development.

.DESCRIPTION
    This script handles:
    - Winget installation check
    - Docker Desktop initialization
    - Azure CLI installation and login check
    - Key Vault access verification
    - .NET 10 SDK verification
    - Node.js verification (for Playwright E2E tests)

    Run this script on a fresh checkout (e.g., on an MSI laptop) to ensure
    the app works immediately.
#>

param(
    [switch]$SkipDocker,
    [switch]$SkipAzureCli,
    [switch]$Help
)

if ($Help) {
    Get-Help $MyInvocation.MyCommand.Definition -Detailed
    exit 0
}

$ErrorActionPreference = "Stop"
$script:HasErrors = $false

function Write-Step {
    param([string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Message)
    Write-Host "  [OK] $Message" -ForegroundColor Green
}

function Write-Warn {
    param([string]$Message)
    Write-Host "  [WARN] $Message" -ForegroundColor Yellow
}

function Write-Fail {
    param([string]$Message)
    Write-Host "  [FAIL] $Message" -ForegroundColor Red
    $script:HasErrors = $true
}

# ── 1. Check .NET 10 SDK ──────────────────────────────────────────────────────
Write-Step "Checking .NET 10 SDK..."
try {
    $dotnetVersion = dotnet --version 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($dotnetVersion)) {
        Write-Fail ".NET SDK not found. Install from https://dotnet.microsoft.com/download"
    } elseif ($dotnetVersion -notmatch "^10\.") {
        Write-Warn ".NET SDK version is $dotnetVersion — expected 10.x. Some features may not work."
    } else {
        Write-Ok ".NET SDK $dotnetVersion installed"
    }
} catch {
    Write-Fail ".NET SDK check failed: $_"
}

# ── 2. Check Node.js (for Playwright E2E tests) ───────────────────────────────
Write-Step "Checking Node.js..."
try {
    $nodeVersion = node --version 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($nodeVersion)) {
        Write-Warn "Node.js not found. E2E tests require Node.js. Install via: winget install OpenJS.NodeJS.LTS"
    } else {
        Write-Ok "Node.js $nodeVersion installed"
    }
} catch {
    Write-Warn "Node.js check failed: $_"
}

# ── 3. Check Docker Desktop ───────────────────────────────────────────────────
if (-not $SkipDocker) {
    Write-Step "Checking Docker Desktop..."
    try {
        $dockerVersion = docker --version 2>$null
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($dockerVersion)) {
            Write-Fail "Docker not found. Install Docker Desktop via: winget install Docker.DockerDesktop"
        } else {
            Write-Ok "$dockerVersion installed"

            # Check if Docker is running
            $dockerInfo = docker info 2>&1
            if ($LASTEXITCODE -ne 0) {
                Write-Fail "Docker is installed but not running. Start Docker Desktop and re-run this script."
            } else {
                Write-Ok "Docker daemon is running"
            }
        }
    } catch {
        Write-Fail "Docker check failed: $_"
    }
} else {
    Write-Step "Skipping Docker check (-SkipDocker)"
}

# ── 4. Check Azure CLI ────────────────────────────────────────────────────────
if (-not $SkipAzureCli) {
    Write-Step "Checking Azure CLI..."
    try {
        $azVersion = az --version 2>$null | Select-String "azure-cli"
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($azVersion)) {
            Write-Warn "Azure CLI not found. Install via: winget install Microsoft.AzureCLI"
            Write-Warn "Azure CLI is required for Key Vault access in local development."
        } else {
            Write-Ok "Azure CLI installed ($($azVersion.Trim()))"

            # Check login status
            $azAccount = az account show 2>&1
            if ($LASTEXITCODE -ne 0) {
                Write-Warn "Not logged into Azure. Run 'az login' to enable Key Vault access."
            } else {
                $accountName = ($azAccount | ConvertFrom-Json).name
                Write-Ok "Logged into Azure as: $accountName"

                # Verify Key Vault access
                Write-Step "Verifying Key Vault access (kv-poshared2)..."
                $kvCheck = az keyvault secret list --vault-name kv-poshared2 2>&1
                if ($LASTEXITCODE -ne 0) {
                    Write-Warn "Cannot access Key Vault 'kv-poshared2'. Ensure your account has 'Key Vault Secrets User' role."
                    Write-Warn "Run: az role assignment create --role 'Key Vault Secrets User' --assignee $(az account show --query id -o tsv) --scope /subscriptions/$(az account show --query id -o tsv)/resourceGroups/Punkouter26"
                } else {
                    Write-Ok "Key Vault 'kv-poshared2' is accessible"
                }
            }
        }
    } catch {
        Write-Warn "Azure CLI check failed: $_"
    }
} else {
    Write-Step "Skipping Azure CLI check (-SkipAzureCli)"
}

# ── 5. Verify project builds ──────────────────────────────────────────────────
Write-Step "Verifying project builds..."
try {
    $buildOutput = dotnet build "$PSScriptRoot\..\PoSeeReview.sln" --verbosity quiet 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "Solution build failed. Run 'dotnet build' for details."
    } else {
        Write-Ok "Solution builds successfully"
    }
} catch {
    Write-Fail "Build verification failed: $_"
}

# ── Summary ───────────────────────────────────────────────────────────────────
Write-Host "`n========================================" -ForegroundColor White
if ($script:HasErrors) {
    Write-Host "Setup completed with ERRORS. Review the output above." -ForegroundColor Red
    Write-Host "Fix the issues and re-run this script." -ForegroundColor Yellow
    exit 1
} else {
    Write-Host "Setup complete! You're ready to run PoSeeReview." -ForegroundColor Green
    Write-Host "`nQuick start:" -ForegroundColor White
    Write-Host "  1. Start Azurite:  dotnet run --project src/PoSeeReview.Api (uses docker-compose)" -ForegroundColor Gray
    Write-Host "  2. Start API:      dotnet run --project src/PoSeeReview.Api --launch-profile https" -ForegroundColor Gray
    Write-Host "  3. Open browser:   https://localhost:5001" -ForegroundColor Gray
    exit 0
}
