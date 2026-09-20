using '../main.bicep'

// Production environment.
//
// `isProduction = true` is not cosmetic. It switches: PostgreSQL to General Purpose with
// zone-redundant HA (which DOUBLES the compute bill) and 35-day backups, Storage to ZRS,
// Key Vault to 90-day retention with purge protection (irreversible), Log Analytics to
// 90-day retention with no daily cap, and every app to minReplicas >= 1.
//
// Read docs/architecture.md §14 before deploying this. The step from dev to prod here is
// roughly $80/mo to $400-900/mo, and APIM plus PostgreSQL HA are most of the difference.

param environmentName = 'prod'
param location = 'westeurope'

param entraAdminObjectId = '00000000-0000-0000-0000-000000000000'
param entraAdminPrincipalName = 'sg-ecommerce-dba-prod'
param entraAdminPrincipalType = 'Group'

param authAudience = 'api://ecommerce'

param isProduction = true

// Production must never deploy a placeholder. These are set by the release pipeline to
// an immutable digest — not a tag. A tag can be re-pointed, which makes "what is actually
// running" unanswerable during an incident.
param userServiceImage = 'REPLACED_BY_PIPELINE'
param catalogServiceImage = 'REPLACED_BY_PIPELINE'
param orderServiceImage = 'REPLACED_BY_PIPELINE'

param additionalTags = {
  costCenter: 'engineering'
  dataClassification: 'confidential'
}
