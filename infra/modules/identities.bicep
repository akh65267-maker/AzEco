// User-assigned managed identities — one per service.
//
// Deployed FIRST, before anything they need access to, because every other module
// takes a principalId as input to create its own role assignments.
//
// Why user-assigned rather than system-assigned:
//   A system-assigned identity's lifecycle is tied to the compute resource. Delete or
//   recreate the Container App and the principal is gone, taking every role assignment
//   with it — including the CREATE ROLE / GRANT statements run inside PostgreSQL, which
//   Azure has no idea about and will not recreate. User-assigned identities outlive the
//   compute, so a redeploy is a non-event.
//
// Why one per service rather than one shared:
//   Least privilege only means something if the grants differ. A shared identity means a
//   compromised CatalogService can drain the order topic.

@description('Azure region for the identities.')
param location string

@description('Naming suffix shared by every resource in this environment.')
param resourceToken string

@description('Tags applied to every resource.')
param tags object = {}

// Declared individually rather than in a loop. Three named identities with three
// different grant sets are not really a collection, and consumers referencing
// `identities[1]` by position is exactly how the wrong identity ends up with blob access.
resource userIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-user-${resourceToken}'
  location: location
  tags: tags
}

resource catalogIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-catalog-${resourceToken}'
  location: location
  tags: tags
}

resource orderIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-order-${resourceToken}'
  location: location
  tags: tags
}

@description('Keyed by service name so consumers read byService.catalog.principalId.')
output byService object = {
  user: {
    name: userIdentity.name
    resourceId: userIdentity.id
    principalId: userIdentity.properties.principalId
    clientId: userIdentity.properties.clientId
  }
  catalog: {
    name: catalogIdentity.name
    resourceId: catalogIdentity.id
    principalId: catalogIdentity.properties.principalId
    clientId: catalogIdentity.properties.clientId
  }
  order: {
    name: orderIdentity.name
    resourceId: orderIdentity.id
    principalId: orderIdentity.properties.principalId
    clientId: orderIdentity.properties.clientId
  }
}
