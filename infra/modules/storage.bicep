@description('Azure region for resources')
param location string

@description('Resource tags')
param tags object

@description('Unique resource token')
param resourceToken string

// Storage Account
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  // resourceToken always resolves to 14+ alphanumeric chars (environmentName + uniqueString); static analysis can't infer this
  #disable-next-line BCP334
  name: 'st${take(replace(resourceToken, '-', ''), 21)}'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    accessTier: 'Hot'
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

// Table Service
resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

// Blob Service
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: 7
    }
    // CORS for the comic <img>. Without a rule here Azure Blob returns no
    // Access-Control-Allow-Origin, so a crossorigin="anonymous" <img> is discarded by the
    // browser and the comic panel renders broken. The client therefore does NOT set that
    // attribute today; with this rule deployed, WebGL can read the pixels without tainting the
    // canvas and the print post-process (js/comic-fx.js) starts working on its own.
    // GET/HEAD only: the client never writes to storage, it only displays a SAS-signed read.
    cors: {
      corsRules: [
        {
          allowedOrigins: [
            'https://app-poseereview2.azurewebsites.net'
          ]
          allowedMethods: [
            'GET'
            'HEAD'
          ]
          allowedHeaders: [
            '*'
          ]
          exposedHeaders: [
            'Content-Length'
            'Content-Type'
          ]
          maxAgeInSeconds: 3600
        }
      ]
    }
  }
}

// Lifecycle management — comics are ephemeral (24h cache, 8-day SAS). Cool-tier
// cold blobs after 7 days and hard-delete everything by 30 days so storage cost
// and stale artifacts stay bounded. (Telemetry budgets & pruning — directive #9.)
resource lifecyclePolicy 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'expire-ephemeral-blobs'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: {
              blobTypes: ['blockBlob']
            }
            actions: {
              baseBlob: {
                tierToCool: {
                  daysAfterModificationGreaterThan: 7
                }
                delete: {
                  daysAfterModificationGreaterThan: 30
                }
              }
              snapshot: {
                delete: {
                  daysAfterCreationGreaterThan: 7
                }
              }
            }
          }
        }
      ]
    }
  }
}

output id string = storage.id
output name string = storage.name
output primaryEndpoints object = storage.properties.primaryEndpoints
output tableEndpoint string = storage.properties.primaryEndpoints.table
output blobEndpoint string = storage.properties.primaryEndpoints.blob
#disable-next-line outputs-should-not-contain-secrets
// No connectionString output: emitting one would surface a storage ACCOUNT KEY in the
// deployment history. Consumers use the endpoints above with Managed Identity (NET_RULES 5.4).
