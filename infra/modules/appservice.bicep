// Azure App Service module
// Deploys App Service Plan + Web App for the API (Blazor WASM hosted in ASP.NET Core)
// Linux container hosting with .NET 10 on App Service

@description('Location for resources')
param location string

@description('Tags to apply to resources')
param tags object

@description('App Service name (e.g. app-poseereview2)')
param appName string

@description('App Service Plan SKU — F1 free tier per NET_RULES 5.2')
param skuName string = 'F1'

@description('Key Vault URI for Managed Identity secret loading')
param keyVaultEndpoint string

@description('Storage Table endpoint')
param storageTableEndpoint string

@description('Storage Blob endpoint')
param storageBlobEndpoint string

@description('Application Insights connection string')
param applicationInsightsConnectionString string

@description('ASPNETCORE_ENVIRONMENT value')
param aspnetcoreEnvironment string = 'Production'

// The live site was destroyed and recreated on 2026-08-14 (moved off a shared plan in PoShared
// onto its own dedicated F1 plan) and is bound to `asp-PoSeeReview-f1`, not the `asp-${appName}`
// this var used to compute (`asp-app-poseereview`). deploy.yml never runs this Bicep — it deploys
// straight to the existing site via azure/webapps-deploy@v3 — so the two names being different
// was latent, not broken. Hardcoded to match the live plan so a future `azd provision` adopts it
// instead of creating a second, orphaned plan under the old naming.
var planName = 'asp-PoSeeReview-f1'

// ─── App Service Plan (Linux) ───────────────────────────────────────────────

resource appServicePlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: skuName
  }
  properties: {
    reserved: true // required for Linux plans
  }
}

// ─── Web App ─────────────────────────────────────────────────────────────────
// Identity is System-Assigned (PoShared identity-centralization standard): the
// platform manages the principal lifecycle with the app, and Key Vault / Storage
// RBAC is granted to webApp.identity.principalId. No connection strings or
// AZURE_CLIENT_ID required — DefaultAzureCredential resolves the system identity.

resource webApp 'Microsoft.Web/sites@2024-04-01' = {
  name: appName
  location: location
  tags: union(tags, {
    'azd-service-name': 'api'
  })
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      // Explicit startup command: the publish output ships BOTH PoSeeReview.Api.runtimeconfig.json
      // and PoSeeReview.Client.runtimeconfig.json (hosted Blazor WASM), which breaks Linux App
      // Service auto-detection — the container then serves the hostingstart parking page.
      appCommandLine: 'dotnet PoSeeReview.Api.dll'
      alwaysOn: false          // allow scale-to-zero on free/B1
      http20Enabled: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/health/live'

      // ── App settings ──────────────────────────────────────────────────────
      // NOTE: WEBSITES_CONTAINER_START_TIME_LIMIT is critical.
      // .NET 10 on App Service Linux runs update-ca-certificates (~5 min) followed
      // by Managed Identity cold-start token acquisition (~2 min) = ~10 min total.
      // The default probe timeout (230 s) causes ContainerTimeout crash loops.
      // 900 s (15 min) gives enough headroom for the worst-case cold start.
      appSettings: [
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: aspnetcoreEnvironment
        }
        {
          name: 'WEBSITES_CONTAINER_START_TIME_LIMIT'
          value: '900'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: applicationInsightsConnectionString
        }
        {
          name: 'ApplicationInsightsAgent_EXTENSION_VERSION'
          value: '~3'
        }
        {
          // Config key the app actually reads (KeyVaultConfigurationExtensions → "KeyVault:Uri").
          name: 'KeyVault__Uri'
          value: keyVaultEndpoint
        }
        {
          name: 'AzureStorage__TableEndpoint'
          value: storageTableEndpoint
        }
        {
          name: 'AzureStorage__BlobEndpoint'
          value: storageBlobEndpoint
        }
      ]
    }
  }
}

// ─── Outputs ─────────────────────────────────────────────────────────────────

@description('Default hostname of the App Service')
output hostName string = webApp.properties.defaultHostName

@description('App Service name')
output appName string = webApp.name

@description('System-Assigned Managed Identity principal ID (for Key Vault / Storage RBAC)')
output identityPrincipalId string = webApp.identity.principalId
