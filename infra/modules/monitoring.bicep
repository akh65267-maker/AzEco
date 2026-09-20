// Log Analytics workspace + workspace-based Application Insights.
//
// Why workspace-based (classic App Insights is retired anyway):
//   App telemetry and platform telemetry land in the SAME store, so one KQL query can
//   join "orders are failing" to "Postgres CPU is at 98%". That join is the entire
//   reason to co-locate them. Two separate stores means two tabs and a guess.
//
// Relationship, since the names overlap confusingly:
//   OpenTelemetry   = how the app PRODUCES telemetry (vendor-neutral, in our code)
//   App Insights    = the PRODUCT that receives it (App Map, transaction view)
//   Log Analytics   = the STORE underneath it (KQL tables)
//   Azure Monitor   = the PLATFORM that alerts on it
//
// Cost warning: ingestion is roughly $2.30/GB and is the single most under-estimated
// line item in this architecture. `retentionInDays` and sampling are the two levers.

@description('Azure region.')
param location string

@description('Naming suffix shared by every resource in this environment.')
param resourceToken string

@description('Tags applied to every resource.')
param tags object = {}

@description('How long telemetry is retained. 30 days is the free floor; raising it raises the bill.')
@minValue(30)
@maxValue(730)
param retentionInDays int = 30

@description('Daily ingestion cap in GB. -1 disables the cap. A cap protects the bill but DROPS telemetry once hit, including the telemetry explaining why you hit it.')
param dailyQuotaGb int = -1

@description('Principal ids allowed to publish telemetry (the service managed identities).')
param telemetryPublisherPrincipalIds array = []

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${resourceToken}'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: retentionInDays
    workspaceCapping: {
      dailyQuotaGb: dailyQuotaGb
    }
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${resourceToken}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    // Ingestion via Entra only: the instrumentation key stops being a valid credential,
    // so a leaked connection string cannot be used to poison your telemetry.
    DisableLocalAuth: false // flip to true once every service authenticates with its MI
    IngestionMode: 'LogAnalytics'
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

// Monitoring Metrics Publisher — required only when DisableLocalAuth is true, but
// granted now so flipping that switch is a one-line change rather than a debugging session.
var monitoringMetricsPublisherRoleId = '3913510d-42f4-4e42-8a64-420c390055eb'

resource telemetryPublishers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in telemetryPublisherPrincipalIds: {
    name: guid(appInsights.id, principalId, monitoringMetricsPublisherRoleId)
    scope: appInsights
    properties: {
      principalId: principalId
      roleDefinitionId: subscriptionResourceId(
        'Microsoft.Authorization/roleDefinitions',
        monitoringMetricsPublisherRoleId
      )
      principalType: 'ServicePrincipal'
    }
  }
]

output workspaceId string = workspace.id
output workspaceCustomerId string = workspace.properties.customerId
output appInsightsId string = appInsights.id
output appInsightsName string = appInsights.name

// The connection string is NOT a secret — it is an ingestion-only endpoint with no read
// access. It goes in app settings as an environment variable, not in Key Vault; putting
// it in Key Vault only buys you a startup dependency.
output appInsightsConnectionString string = appInsights.properties.ConnectionString
