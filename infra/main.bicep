// ─────────────────────────────────────────────────────────────────────────────────────────────
// APPLIED BY HAND, NOT BY CI. `.github/workflows/deploy.yml` only publishes and deploys the app
// — it never runs a deployment of this template. Infrastructure changes reach Azure through
// `az deployment sub create -f infra/main.bicep`, run by a person. That is deliberate: a pipeline
// that swaps a package is a smaller thing to keep working than a provisioning job nobody watches.
// It does mean a change here is not exercised until someone applies it.
// ─────────────────────────────────────────────────────────────────────────────────────────────

targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the environment which will be used to generate the resource names')
param environmentName string

@minLength(1)
@description('Primary location for all resources')
param location string

@description('Azure Developer CLI environment name override')
param azdEnvironmentName string = environmentName

@description('Region code used in deterministic resource names')
param regionCode string = 'wus2'

@description('Email addresses for budget alerts')
param budgetContactEmails array = []

// App Service Plan SKU
@description('App Service Plan SKU (F1 Free tier per NET_RULES 5.2, B1 Basic for production)')
param appServicePlanSku string = 'F1'

// Resource names
var resourceToken = toLower('poseereview-${environmentName}-${regionCode}')
var resourceGroupName = 'rg-${resourceToken}'
var tags = {
  'azd-env-name': azdEnvironmentName
  environment: environmentName
  application: 'SeeReview'
  app: 'poseereview'
  region: regionCode
  namingStandard: 'caf-derived'
}

// Resource group
resource rg 'Microsoft.Resources/resourceGroups@2024-11-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

// Monitoring - Log Analytics & Application Insights
module monitoring './modules/monitoring.bicep' = {
  name: 'monitoring'
  scope: rg
  params: {
    location: location
    tags: tags
    resourceToken: resourceToken
  }
}

// Storage - Azure Storage Account (Tables + Blobs)
module storage './modules/storage.bicep' = {
  name: 'storage'
  scope: rg
  params: {
    location: location
    tags: tags
    resourceToken: resourceToken
  }
}

// Secrets live in the shared vault kv-poshared2 (RG PoShared) under the PoSeeReview-- prefix; this
// template never creates or writes a vault, it only grants the app's identity read access.
@description('Shared Key Vault the app reads its secrets from.')
param sharedKeyVaultName string = 'kv-poshared2'

@description('Resource group holding the shared Key Vault.')
param sharedResourceGroupName string = 'PoShared'

// App Service — the only hosting target. Matches .github/workflows/deploy.yml.
module apiAppService './modules/appservice.bicep' = {
  name: 'api-appservice'
  scope: rg
  params: {
    location: location
    tags: tags
    appName: 'app-${resourceToken}'
    skuName: appServicePlanSku
    keyVaultEndpoint: 'https://${sharedKeyVaultName}${environment().suffixes.keyvaultDns}/'
    storageTableEndpoint: storage.outputs.tableEndpoint
    storageBlobEndpoint: storage.outputs.blobEndpoint
    applicationInsightsConnectionString: monitoring.outputs.applicationInsightsConnectionString
    aspnetcoreEnvironment: environmentName == 'prod' ? 'Production' : 'Development'
  }
}

// Key Vault Access - grant the API's managed identity read access to the shared vault
module keyVaultAccess './modules/keyvaultaccess.bicep' = {
  name: 'keyvaultaccess'
  scope: resourceGroup(sharedResourceGroupName)
  params: {
    keyVaultName: sharedKeyVaultName
    principalId: apiAppService.outputs.identityPrincipalId
  }
}

// Budget - Monthly spending alerts
module budget './modules/budget.bicep' = {
  name: 'budget'
  scope: rg
  params: {
    contactEmails: budgetContactEmails
  }
}

// Outputs
output AZURE_LOCATION string = location
output AZURE_TENANT_ID string = tenant().tenantId
output AZURE_RESOURCE_GROUP string = rg.name

output API_SERVICE_NAME string = apiAppService.outputs.appName
output API_URL string = 'https://${apiAppService.outputs.hostName}'

output APPLICATION_INSIGHTS_CONNECTION_STRING string = monitoring.outputs.applicationInsightsConnectionString
output APPLICATION_INSIGHTS_INSTRUMENTATION_KEY string = monitoring.outputs.applicationInsightsInstrumentationKey

output STORAGE_ACCOUNT_NAME string = storage.outputs.name
output KEY_VAULT_NAME string = sharedKeyVaultName
