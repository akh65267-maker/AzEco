// Azure Database for PostgreSQL Flexible Server.
//
// ONE server, THREE databases (see docs/architecture.md §5).
//
//   3 servers      strongest isolation, ~3x the cost, 3x the patching. Premature here.
//   1 server, 3 db strong logical isolation; separate roles, connections, migrations.
//                  Shared CPU/IOPS is the real trade-off. <-- chosen
//   1 server, 1 db, 3 schemas
//                  rejected outright: it makes the cross-service JOIN *possible*, and
//                  anything possible eventually happens in a hotfix at 2am.
//
// Extracting OrderService to its own server later is a single-database pg_dump/restore —
// cheap precisely BECAUSE we used databases rather than schemas.
//
// NOT CREATED HERE: the per-service PostgreSQL roles and their GRANTs. Azure has no
// resource type for "CREATE ROLE ... IN DATABASE"; it is SQL, run once against the server
// by a deployment task authenticated as the Entra admin. Bicep provisions the server;
// something else provisions what lives inside it. See infra/README.md.

@description('Azure region.')
param location string

@description('Naming suffix shared by every resource in this environment.')
param resourceToken string

@description('Tags applied to every resource.')
param tags object = {}

@description('Compute SKU. Burstable B1ms is the cheapest credible dev tier; production wants General Purpose (e.g. Standard_D2ds_v5).')
param skuName string = 'Standard_B1ms'

@description('Burstable | GeneralPurpose | MemoryOptimized. Must match skuName.')
@allowed([
  'Burstable'
  'GeneralPurpose'
  'MemoryOptimized'
])
param skuTier string = 'Burstable'

@description('Storage in GB. Can be increased later but NEVER decreased — and IOPS scale with size, so an undersized disk is a performance ceiling, not just a capacity one.')
param storageSizeGb int = 32

@description('Backup retention in days.')
@minValue(7)
@maxValue(35)
param backupRetentionDays int = 7

@description('Zone-redundant HA doubles the compute bill. Off in dev, on in production.')
param highAvailability bool = false

@description('Object id of the Entra principal (user or group) made PostgreSQL admin. Strongly prefer a GROUP: a server whose only admin is one departed employee is a support ticket.')
param entraAdminObjectId string

@description('Display name / UPN of that principal. Cosmetic in the portal, but wrong values make the admin list unreadable.')
param entraAdminPrincipalName string

@description('User | Group | ServicePrincipal')
@allowed([
  'User'
  'Group'
  'ServicePrincipal'
])
param entraAdminPrincipalType string = 'Group'

@description('Allow other Azure services to reach the server (the 0.0.0.0 firewall rule). Required for Container Apps without VNet integration; production should use private endpoints and turn this OFF.')
param allowAzureServices bool = true

var databaseNames = [
  'userdb'
  'catalogdb'
  'orderdb'
]

resource server 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: 'psql-${resourceToken}'
  location: location
  tags: tags
  sku: {
    name: skuName
    tier: skuTier
  }
  properties: {
    version: '16'

    // Entra-only authentication. Note what is NOT here: administratorLogin and
    // administratorLoginPassword. With passwordAuth disabled there is no admin password
    // to store, rotate, leak, or put in Key Vault. This is the single biggest security
    // win in the whole template.
    authConfig: {
      activeDirectoryAuth: 'Enabled'
      passwordAuth: 'Disabled'
    }

    storage: {
      storageSizeGB: storageSizeGb
      autoGrow: 'Enabled'
    }

    backup: {
      backupRetentionDays: backupRetentionDays
      geoRedundantBackup: 'Disabled'
    }

    highAvailability: {
      mode: highAvailability ? 'ZoneRedundant' : 'Disabled'
    }

    network: {
      publicNetworkAccess: 'Enabled'
    }
  }
}

resource entraAdmin 'Microsoft.DBforPostgreSQL/flexibleServers/administrators@2024-08-01' = {
  parent: server
  name: entraAdminObjectId
  properties: {
    principalName: entraAdminPrincipalName
    principalType: entraAdminPrincipalType
    tenantId: subscription().tenantId
  }
}

resource databases 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = [
  for name in databaseNames: {
    parent: server
    name: name
    properties: {
      charset: 'UTF8'
      collation: 'en_US.utf8'
    }
    // Databases and the admin assignment both mutate server state; serialising them
    // avoids the server rejecting a concurrent update with a conflict.
    dependsOn: [
      entraAdmin
    ]
  }
]

// PgBouncer. Burstable B1ms caps connections around 50; three services with EF Core
// pools plus any scale-out exhausts that, and connection exhaustion is the most likely
// real outage in this architecture (silent, gradual, catastrophic).
//
// Transaction pooling breaks session-scoped features, so Npgsql must be configured with
// Max Auto Prepare=0 / No Reset On Close. That is the price, and it is worth paying.
resource pgBouncerEnabled 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2024-08-01' = {
  parent: server
  name: 'pgbouncer.enabled'
  properties: {
    value: 'true'
    source: 'user-override'
  }
  dependsOn: [
    databases
  ]
}

resource pgBouncerPoolMode 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2024-08-01' = {
  parent: server
  name: 'pgbouncer.default_pool_size'
  properties: {
    value: '50'
    source: 'user-override'
  }
  dependsOn: [
    pgBouncerEnabled
  ]
}

// 0.0.0.0-0.0.0.0 is the documented magic range meaning "Azure services", NOT "the whole
// internet" — but it does mean *any* Azure tenant's services, so it is a genuine
// widening. Production replaces it with VNet integration and a private endpoint.
resource allowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = if (allowAzureServices) {
  parent: server
  name: 'AllowAllAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
  dependsOn: [
    pgBouncerPoolMode
  ]
}

output serverId string = server.id
output serverName string = server.name
output fullyQualifiedDomainName string = server.properties.fullyQualifiedDomainName
output databaseNames array = databaseNames
