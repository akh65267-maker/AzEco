// Key Vault.
//
// This vault is deliberately almost empty, and that is the point. After Managed Identity,
// PostgreSQL, Blob Storage, Service Bus and Application Insights all authenticate without
// a stored credential. A Key Vault full of Azure connection strings IS the credential
// problem you adopted Managed Identity to avoid.
//
// What genuinely belongs here: secrets from systems that do not speak Entra — a payment
// provider API key, an SMTP credential, a TLS certificate for APIM.
//
// Access pattern (see ObservabilityExtensions/AddAzureKeyVault in the app): read at
// STARTUP with a reload interval, never per request. Key Vault is rate-limited, and a
// per-request read makes it a single point of failure for every request in the system.

@description('Azure region.')
param location string

@description('Naming suffix shared by every resource in this environment.')
param resourceToken string

@description('Tags applied to every resource.')
param tags object = {}

@description('Principal ids granted Key Vault Secrets User (read-only).')
param secretsUserPrincipalIds array = []

@description('Soft-delete retention. 7 days is the minimum; production should not lower it.')
@minValue(7)
@maxValue(90)
param softDeleteRetentionInDays int = 7

@description('Purge protection blocks permanent deletion during the retention window. Irreversible once enabled — a vault with purge protection cannot be fully removed until retention expires, which is exactly why it is off in dev.')
param enablePurgeProtection bool = false

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv-${resourceToken}'
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId

    // RBAC instead of the legacy access-policy model. Access policies are per-vault ACLs
    // that no other Azure service understands: they cannot be audited alongside the rest
    // of your role assignments, and they have no deny semantics.
    enableRbacAuthorization: true

    enableSoftDelete: true
    softDeleteRetentionInDays: softDeleteRetentionInDays
    enablePurgeProtection: enablePurgeProtection ? true : null

    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

// Secrets User, not Secrets Officer: the services READ secrets. Nothing at runtime
// should be able to write or delete one.
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource secretsUsers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in secretsUserPrincipalIds: {
    name: guid(vault.id, principalId, keyVaultSecretsUserRoleId)
    scope: vault
    properties: {
      principalId: principalId
      roleDefinitionId: subscriptionResourceId(
        'Microsoft.Authorization/roleDefinitions',
        keyVaultSecretsUserRoleId
      )
      principalType: 'ServicePrincipal'
    }
  }
]

output vaultId string = vault.id
output vaultName string = vault.name
output vaultUri string = vault.properties.vaultUri
