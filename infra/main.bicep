// Ecommerce platform — root template.
//
// Subscription scope, so the resource group itself is infrastructure-as-code. A resource
// group created by hand is the first piece of untracked state, and everything downstream
// inherits its tags, location and naming mistakes.
//
// Deployment order is expressed through dependencies, not sequence:
//   identities -> monitoring -> {keyvault, postgres, storage, servicebus} -> compute
// Identities come first because every other module takes a principalId to create its own
// role assignments.
//
// NOT DEPLOYED. There is no Azure subscription attached to this repository. Everything
// here compiles (`bicep build`, enforced in CI); nothing here has been validated against
// live Azure resources with `what-if` or an actual deployment.

targetScope = 'subscription'

// ---------------------------------------------------------------------------
// Parameters
// ---------------------------------------------------------------------------

@description('Environment name. Drives SKUs, redundancy, and naming. Keep it short — it is part of every resource name.')
@minLength(2)
@maxLength(8)
param environmentName string

@description('Azure region for every resource.')
param location string

@description('Resource group to create. One group per environment: the blast radius of a bad `az group delete` should be one environment, never two.')
param resourceGroupName string = 'rg-ecommerce-${environmentName}'

@description('Object id of the Entra principal made PostgreSQL admin. Use a GROUP — a server whose only admin is one departed employee is a support ticket nobody can resolve.')
param entraAdminObjectId string

@description('Display name / UPN of that principal.')
param entraAdminPrincipalName string

@allowed([
  'User'
  'Group'
  'ServicePrincipal'
])
param entraAdminPrincipalType string = 'Group'

@description('Container images per service. Placeholder images let the infrastructure deploy before the services are built; replace with your registry once Phase 4 ships.')
param userServiceImage string = 'mcr.microsoft.com/k8se/quickstart:latest'
param catalogServiceImage string = 'mcr.microsoft.com/k8se/quickstart:latest'
param orderServiceImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Entra tenant id the services validate tokens against. Not a secret.')
param authTenantId string = subscription().tenantId

@description('Audience the APIs accept — the App ID URI, e.g. api://ecommerce-dev. Must match the aud claim Entra actually issues, which depends on how the client requested the token. Mismatch here is the most common cause of "valid token, 401 anyway".')
param authAudience string

@description('Production-shaped defaults: HA, zone redundancy, longer retention, no scale-to-zero. Off for dev.')
param isProduction bool = false

@description('Extra tags merged into the standard set.')
param additionalTags object = {}

// ---------------------------------------------------------------------------
// Naming and tagging
// ---------------------------------------------------------------------------

// Deterministic per (subscription, environment): the same inputs always produce the same
// names, and two environments in one subscription never collide.
var resourceToken = toLower('${environmentName}${uniqueString(subscription().id, environmentName)}')

var tags = union(
  {
    'azd-env-name': environmentName
    environment: environmentName
    application: 'ecommerce'
    managedBy: 'bicep'
  },
  additionalTags
)

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

// ---------------------------------------------------------------------------
// Identities — first, because everything else grants to them
// ---------------------------------------------------------------------------

module identities 'modules/identities.bicep' = {
  scope: rg
  name: 'identities'
  params: {
    location: location
    resourceToken: resourceToken
    tags: tags
  }
}

var allPrincipalIds = [
  identities.outputs.byService.user.principalId
  identities.outputs.byService.catalog.principalId
  identities.outputs.byService.order.principalId
]

// ---------------------------------------------------------------------------
// Observability
// ---------------------------------------------------------------------------

module monitoring 'modules/monitoring.bicep' = {
  scope: rg
  name: 'monitoring'
  params: {
    location: location
    resourceToken: resourceToken
    tags: tags
    retentionInDays: isProduction ? 90 : 30
    // A daily cap protects the bill but DROPS telemetry once hit — including the
    // telemetry that would explain why you hit it. Capped in dev where a runaway loop
    // is likely and the data is cheap to lose; uncapped in production where it is not.
    dailyQuotaGb: isProduction ? -1 : 1
    telemetryPublisherPrincipalIds: allPrincipalIds
  }
}

// ---------------------------------------------------------------------------
// Data and messaging
// ---------------------------------------------------------------------------

module keyvault 'modules/keyvault.bicep' = {
  scope: rg
  name: 'keyvault'
  params: {
    location: location
    resourceToken: resourceToken
    tags: tags
    secretsUserPrincipalIds: allPrincipalIds
    softDeleteRetentionInDays: isProduction ? 90 : 7
    enablePurgeProtection: isProduction
  }
}

module postgres 'modules/postgres.bicep' = {
  scope: rg
  name: 'postgres'
  params: {
    location: location
    resourceToken: resourceToken
    tags: tags
    skuName: isProduction ? 'Standard_D2ds_v5' : 'Standard_B1ms'
    skuTier: isProduction ? 'GeneralPurpose' : 'Burstable'
    storageSizeGb: isProduction ? 128 : 32
    backupRetentionDays: isProduction ? 35 : 7
    highAvailability: isProduction
    entraAdminObjectId: entraAdminObjectId
    entraAdminPrincipalName: entraAdminPrincipalName
    entraAdminPrincipalType: entraAdminPrincipalType
    allowAzureServices: true
  }
}

module storage 'modules/storage.bicep' = {
  scope: rg
  name: 'storage'
  params: {
    location: location
    resourceToken: resourceToken
    tags: tags
    skuName: isProduction ? 'Standard_ZRS' : 'Standard_LRS'
    // CatalogService alone writes images. Order and User have no business here.
    imageWriterPrincipalIds: [
      identities.outputs.byService.catalog.principalId
    ]
    blobSoftDeleteDays: isProduction ? 30 : 7
  }
}

module servicebus 'modules/servicebus.bicep' = {
  scope: rg
  name: 'servicebus'
  params: {
    location: location
    resourceToken: resourceToken
    tags: tags
    skuName: 'Standard'
    // Least privilege in its most literal form: exactly one sender, exactly one
    // receiver, each scoped to one entity.
    eventSenderPrincipalIds: [
      identities.outputs.byService.order.principalId
    ]
    stockConsumerPrincipalIds: [
      identities.outputs.byService.catalog.principalId
    ]
  }
}

// ---------------------------------------------------------------------------
// Compute
// ---------------------------------------------------------------------------

// Configuration that every service needs, and none of which is a secret. All of it is an
// environment variable rather than a Key Vault entry — see docs/architecture.md §9 for
// which kind of setting belongs where.
var commonEnv = [
  {
    name: 'ASPNETCORE_ENVIRONMENT'
    value: isProduction ? 'Production' : 'Development'
  }
  {
    name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
    value: monitoring.outputs.appInsightsConnectionString
  }
  {
    name: 'Auth__TenantId'
    value: authTenantId
  }
  {
    name: 'Auth__Audience'
    value: authAudience
  }
  {
    name: 'KeyVault__Uri'
    value: keyvault.outputs.vaultUri
  }
  {
    name: 'Postgres__Host'
    value: postgres.outputs.fullyQualifiedDomainName
  }
  {
    // The Bicep enables PgBouncer in transaction pooling mode, which hands a different
    // backend connection to every transaction. Anything session-scoped therefore breaks
    // — server-side prepared statements above all — so Npgsql must be told. Forgetting
    // this produces 'prepared statement "_p1" already exists', but only under load.
    name: 'Postgres__UseTransactionPooling'
    value: 'true'
  }
]

// AZURE_CLIENT_ID tells DefaultAzureCredential WHICH user-assigned identity to use.
// Without it, a container with exactly one assigned identity often still works — and
// then breaks the moment a second one is added. So it is always set explicitly.
//
// (This would read better as a user-defined function taking clientId + extras, but Bicep
// functions may only use values known at the start of the deployment, and commonEnv
// depends on module outputs. BCP341.)
var userEnv = concat(commonEnv, [
  {
    name: 'AZURE_CLIENT_ID'
    value: identities.outputs.byService.user.clientId
  }
  {
    name: 'Postgres__Database'
    value: 'userdb'
  }
  {
    // The PostgreSQL role name IS the managed identity's resource name — that is how
    // Azure maps a Postgres role to an Entra principal. A mismatch here fails the
    // login with a generic authentication error that names nothing.
    name: 'Postgres__Username'
    value: identities.outputs.byService.user.name
  }
])

var catalogEnv = concat(commonEnv, [
  {
    name: 'AZURE_CLIENT_ID'
    value: identities.outputs.byService.catalog.clientId
  }
  {
    name: 'Postgres__Database'
    value: 'catalogdb'
  }
  {
    // The PostgreSQL role name IS the managed identity's resource name — that is how
    // Azure maps a Postgres role to an Entra principal. A mismatch here fails the
    // login with a generic authentication error that names nothing.
    name: 'Postgres__Username'
    value: identities.outputs.byService.catalog.name
  }
  {
    name: 'Storage__BlobEndpoint'
    value: storage.outputs.blobEndpoint
  }
  {
    name: 'Storage__ImagesContainer'
    value: storage.outputs.imagesContainerName
  }
  {
    name: 'ServiceBus__FullyQualifiedNamespace'
    value: servicebus.outputs.fullyQualifiedNamespace
  }
  {
    name: 'ServiceBus__Topic'
    value: servicebus.outputs.topicName
  }
  {
    name: 'ServiceBus__Subscription'
    value: servicebus.outputs.stockSubscriptionName
  }
])

var orderEnv = concat(commonEnv, [
  {
    name: 'AZURE_CLIENT_ID'
    value: identities.outputs.byService.order.clientId
  }
  {
    name: 'Postgres__Database'
    value: 'orderdb'
  }
  {
    // The PostgreSQL role name IS the managed identity's resource name — that is how
    // Azure maps a Postgres role to an Entra principal. A mismatch here fails the
    // login with a generic authentication error that names nothing.
    name: 'Postgres__Username'
    value: identities.outputs.byService.order.name
  }
  {
    name: 'ServiceBus__FullyQualifiedNamespace'
    value: servicebus.outputs.fullyQualifiedNamespace
  }
  {
    name: 'ServiceBus__Topic'
    value: servicebus.outputs.topicName
  }
])

module compute 'modules/compute.bicep' = {
  scope: rg
  name: 'compute'
  params: {
    location: location
    resourceToken: resourceToken
    tags: tags
    logAnalyticsWorkspaceId: monitoring.outputs.workspaceId
    apps: [
      {
        name: 'user'
        image: userServiceImage
        identityResourceId: identities.outputs.byService.user.resourceId
        targetPort: 8080
        external: true
        minReplicas: isProduction ? 1 : 0
        maxReplicas: isProduction ? 5 : 2
        env: userEnv
      }
      {
        name: 'catalog'
        image: catalogServiceImage
        identityResourceId: identities.outputs.byService.catalog.resourceId
        targetPort: 8080
        external: true
        minReplicas: isProduction ? 1 : 0
        maxReplicas: isProduction ? 10 : 2
        env: catalogEnv
      }
      {
        name: 'order'
        image: orderServiceImage
        identityResourceId: identities.outputs.byService.order.resourceId
        targetPort: 8080
        external: true
        // NEVER zero, even in dev. OrderService hosts the outbox dispatcher, and a
        // scaled-to-zero app runs no BackgroundService — events would sit unpublished
        // until the next HTTP request happened to wake it up.
        minReplicas: 1
        maxReplicas: isProduction ? 5 : 2
        env: orderEnv
      }
    ]
  }
}

// ---------------------------------------------------------------------------
// Outputs — consumed by the post-deployment SQL step and by Phase 7 (APIM)
// ---------------------------------------------------------------------------

output resourceGroupName string = rg.name
output resourceToken string = resourceToken

output postgresFqdn string = postgres.outputs.fullyQualifiedDomainName
output postgresDatabases array = postgres.outputs.databaseNames

output storageAccountName string = storage.outputs.storageAccountName
output blobEndpoint string = storage.outputs.blobEndpoint

output serviceBusNamespace string = servicebus.outputs.fullyQualifiedNamespace
output serviceBusTopic string = servicebus.outputs.topicName

output appInsightsName string = monitoring.outputs.appInsightsName
output keyVaultUri string = keyvault.outputs.vaultUri

output serviceFqdns array = compute.outputs.appFqdns

// Needed by the post-deploy SQL that creates each PostgreSQL role and maps it to its
// managed identity. Azure has no resource type for "CREATE ROLE"; see infra/README.md.
output identityNames object = {
  user: identities.outputs.byService.user.name
  catalog: identities.outputs.byService.catalog.name
  order: identities.outputs.byService.order.name
}
