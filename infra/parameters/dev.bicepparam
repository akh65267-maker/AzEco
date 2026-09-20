using '../main.bicep'

// Development environment.
//
// Committed deliberately: there is not a single secret in this file. Tenant ids, object
// ids, audiences and region names are all public values. The moment something here needs
// to be secret, it becomes a @secure() parameter supplied by the pipeline instead — it
// does not get added to this file "just for now".

param environmentName = 'dev'
param location = 'westeurope'

// Prefer a GROUP over a person. A PostgreSQL server whose only admin is one departed
// employee is a support ticket nobody can resolve without a subscription owner.
param entraAdminObjectId = '00000000-0000-0000-0000-000000000000'
param entraAdminPrincipalName = 'sg-ecommerce-dba-dev'
param entraAdminPrincipalType = 'Group'

// The App ID URI of the API app registration. Must match the aud claim Entra issues.
param authAudience = 'api://ecommerce-dev'

param isProduction = false

// Placeholder images until Phase 4 ships real containers. Deploying infrastructure
// before the code exists is the point of doing IaC early — the RBAC and identity design
// has to be concrete either way.
param userServiceImage = 'mcr.microsoft.com/k8se/quickstart:latest'
param catalogServiceImage = 'mcr.microsoft.com/k8se/quickstart:latest'
param orderServiceImage = 'mcr.microsoft.com/k8se/quickstart:latest'

param additionalTags = {
  costCenter: 'engineering'
  autoShutdown: 'true'
}
