// Blob Storage for product images.
//
// The two properties that matter most here are both denials:
//
//   allowBlobPublicAccess: false   no container can be made anonymous, ever. Set at the
//                                  ACCOUNT level so it cannot be flipped per container.
//   allowSharedKeyAccess:  false   the account keys stop working entirely. A storage
//                                  account key is a permanent, un-scoped credential that
//                                  cannot be rotated without downtime; with this off, the
//                                  only way in is Entra + RBAC.
//
// The second one has a consequence worth stating: user-delegation SAS (signed with a key
// obtained via Managed Identity, scoped to one blob, short-lived, and revocable) is the
// ONLY SAS that still works. That is the intent — see docs/architecture.md §7.
//
// Azurite does not implement user-delegation SAS or RBAC, so this authorization path is
// only ever exercised in Azure. Known gap, documented in docs/local-development.md.

@description('Azure region.')
param location string

@description('Naming suffix shared by every resource in this environment.')
@minLength(4)
param resourceToken string

@description('Tags applied to every resource.')
param tags object = {}

@description('Standard_LRS for dev; Standard_ZRS for production (zone redundancy for a few dollars).')
@allowed([
  'Standard_LRS'
  'Standard_ZRS'
  'Standard_GRS'
])
param skuName string = 'Standard_LRS'

@description('Principal ids that read and write product images — CatalogService only.')
param imageWriterPrincipalIds array = []

@description('Days a blob under staging/ survives before cleanup. Uploads that were never committed become orphans; this sweeps them.')
param stagingRetentionDays int = 1

@description('Days deleted blobs remain recoverable.')
@minValue(1)
@maxValue(365)
param blobSoftDeleteDays int = 7

// Storage account names are the most constrained in Azure: 3-24 characters, lowercase
// alphanumeric ONLY — no hyphens, unlike every other resource here. uniqueString always
// returns exactly 13 lowercase alphanumeric characters, so 'st' + 13 is provably valid;
// deriving it from resourceToken instead would leave the length unprovable and the
// violation would surface at deploy time rather than build time.
var storageAccountName = 'st${uniqueString(resourceGroup().id, resourceToken)}'
var imagesContainerName = 'product-images'

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: skuName
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    defaultToOAuthAuthentication: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: blobSoftDeleteDays
    }
    containerDeleteRetentionPolicy: {
      enabled: true
      days: blobSoftDeleteDays
    }
    // CORS lets the browser PUT directly to Blob Storage with the SAS we mint, which is
    // the whole point of the direct-upload flow: a 5MB image never touches our compute.
    // allowedOrigins must be narrowed per environment — '*' here would let any site
    // upload with a leaked SAS.
    cors: {
      corsRules: []
    }
  }
}

resource imagesContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: imagesContainerName
  properties: {
    publicAccess: 'None'
  }
}

// Uncommitted uploads leave blobs with no metadata row. The application HEADs a blob
// before writing its row, so a missing row means the upload was abandoned; this rule
// collects them rather than paying to store them forever.
resource lifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = {
  parent: storageAccount
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'sweep-abandoned-uploads'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: {
              blobTypes: [
                'blockBlob'
              ]
              prefixMatch: [
                '${imagesContainerName}/staging/'
              ]
            }
            actions: {
              baseBlob: {
                delete: {
                  daysAfterCreationGreaterThan: stagingRetentionDays
                }
              }
            }
          }
        }
      ]
    }
  }
}

var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var storageBlobDelegatorRoleId = 'db58b8e5-c6ad-4a2a-8342-4190687cbf4a'

// Scoped to the CONTAINER, not the account. If this were account-scoped, CatalogService
// would gain access to every container anyone adds later.
resource imageWriters 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in imageWriterPrincipalIds: {
    name: guid(imagesContainer.id, principalId, storageBlobDataContributorRoleId)
    scope: imagesContainer
    properties: {
      principalId: principalId
      roleDefinitionId: subscriptionResourceId(
        'Microsoft.Authorization/roleDefinitions',
        storageBlobDataContributorRoleId
      )
      principalType: 'ServicePrincipal'
    }
  }
]

// Storage Blob Delegator is required to mint a user-delegation SAS and can ONLY be
// granted at account scope. It is the single most commonly missed role assignment in
// this pattern, and the failure it produces ("AuthorizationPermissionMismatch" on
// getUserDelegationKey) points at the wrong thing.
resource delegators 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in imageWriterPrincipalIds: {
    name: guid(storageAccount.id, principalId, storageBlobDelegatorRoleId)
    scope: storageAccount
    properties: {
      principalId: principalId
      roleDefinitionId: subscriptionResourceId(
        'Microsoft.Authorization/roleDefinitions',
        storageBlobDelegatorRoleId
      )
      principalType: 'ServicePrincipal'
    }
  }
]

output storageAccountId string = storageAccount.id
output storageAccountName string = storageAccount.name
output blobEndpoint string = storageAccount.properties.primaryEndpoints.blob
output imagesContainerName string = imagesContainerName
