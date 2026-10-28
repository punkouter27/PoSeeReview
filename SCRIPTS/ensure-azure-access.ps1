# Makes Azure and GitHub match what PoSeeReview needs: the app identity's roles, the CI deploy identity, and the
# repo variables the deploy workflow reads. Run by a subscription owner:  pwsh SCRIPTS/ensure-azure-access.ps1
$Repo = 'PoSeeReview'; $Branch = 'master'; $ResourceGroup = 'PoSeeReview'; $App = 'app-poseereview2'
$Identity = 'system'          # 'system', or the name of a user-assigned identity in PoShared
$KeyVaultAccess = $true
$DeployResourceGroups = @('PoSeeReview')
$AppRoles = @(                  # role | scope under the subscription ('' = the subscription itself)
    'Storage Table Data Contributor|/resourceGroups/PoSeeReview/providers/Microsoft.Storage/storageAccounts/stposeereview2'
    'Storage Blob Data Contributor|/resourceGroups/PoSeeReview/providers/Microsoft.Storage/storageAccounts/stposeereview2'
    'Contributor|/resourceGroups/PoSeeReview/providers/Microsoft.Storage/storageAccounts/stposeereview2'
)
# ---- Same in every Po web repo below this line. Idempotent: re-run it after any re-provision. ----
# Grants exactly what the app and its CI need, so "the old resources had a role nobody wrote down"
# cannot happen again. Needs Owner (or User Access Administrator) on the subscription.
$ErrorActionPreference = 'Stop'
$Sub = '14d963a2-badc-4a1f-9900-553bf01f8de2'; $Tenant = '28e2074a-9756-49cb-9bde-314aba4d2bbe'; $Vault = 'kv-poshared2'
function Step($what) { if ($LASTEXITCODE) { throw "$what failed ($LASTEXITCODE)" } "  ok  $what" }
az account set -s $Sub

# 1. The app's identity: data roles, plus get/list on the shared vault (an access-policy vault, not RBAC).
$mi = if ($Identity -eq 'system') { az webapp identity show -g $ResourceGroup -n $App --query principalId -o tsv }
      else { az identity show -g PoShared -n $Identity --query principalId -o tsv }
if (!$mi) { throw "$App has no '$Identity' identity" }
foreach ($g in $AppRoles) {
    $role, $scope = $g -split '\|', 2
    az role assignment create --assignee-object-id $mi --assignee-principal-type ServicePrincipal --role $role --scope "/subscriptions/$Sub$scope" -o none; Step "app: $role @ $(if ($scope) { $scope } else { 'subscription' })"
}
if ($KeyVaultAccess) { az keyvault set-policy -n $Vault --object-id $mi --secret-permissions get list -o none; Step "app: $Vault get/list" }

# 2. The CI deploy identity: GitHub OIDC, no secret, Contributor on its own resource group(s) only.
$name = "gh-$($Repo.ToLower())-deploy"
$appId = az ad app list --display-name $name --query "[0].appId" -o tsv
if (!$appId) { $appId = az ad app create --display-name $name --query appId -o tsv }
$spId = az ad sp list --filter "appId eq '$appId'" --query "[0].id" -o tsv
if (!$spId) { $spId = az ad sp create --id $appId --query id -o tsv }
$repoId = gh api "repos/punkouter27/$Repo" -q .id
$have = @(az ad app federated-credential list --id $appId --query "[].subject" -o tsv)
$i = $have.Count
foreach ($s in "repo:punkouter27/${Repo}:ref:refs/heads/$Branch", "repo:punkouter27/${Repo}:environment:production",
               "repo:punkouter27@121304072/${Repo}@${repoId}:ref:refs/heads/$Branch", "repo:punkouter27@121304072/${Repo}@${repoId}:environment:production") {
    if ($s -in $have) { continue }
    $i++; $f = Join-Path $env:TEMP "fc-$Repo-$i.json"
    @{ name = "gh27-$i"; issuer = 'https://token.actions.githubusercontent.com'; subject = $s; audiences = @('api://AzureADTokenExchange') } | ConvertTo-Json -Compress | Set-Content $f
    az ad app federated-credential create --id $appId --parameters "@$f" -o none; $code = $LASTEXITCODE; Remove-Item $f; $LASTEXITCODE = $code; Step "deploy: trust $s"
}
foreach ($rg in $DeployResourceGroups) {
    az role assignment create --assignee-object-id $spId --assignee-principal-type ServicePrincipal --role Contributor --scope "/subscriptions/$Sub/resourceGroups/$rg" -o none; Step "deploy: Contributor @ $rg"
}

# 3. What the workflow reads. Variables, not secrets: none of these is a secret.
foreach ($v in @{ AZURE_CLIENT_ID = $appId; AZURE_TENANT_ID = $Tenant; AZURE_SUBSCRIPTION_ID = $Sub; AZURE_WEBAPP_NAME = $App }.GetEnumerator()) {
    gh variable set $v.Key -R "punkouter27/$Repo" --body $v.Value; Step "github var $($v.Key)"
}
