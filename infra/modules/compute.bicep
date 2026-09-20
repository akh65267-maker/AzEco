// Azure Container Apps: one environment, three apps.
//
// Why Container Apps over the alternatives:
//   App Service  simpler, but scaling is per-plan rather than per-app, so one busy
//                service forces you to scale all three.
//   AKS          unjustifiable at three services — a cluster is a full-time job.
//   Container Apps  user-assigned MI support, scale-to-zero in dev (a large and free
//                cost saving), and KEDA scaling on Service Bus queue depth for the
//                consumer in Phase 6. Dapr is available and deliberately unused: it is
//                a second mental model for the same problems we solve explicitly.

@description('Azure region.')
param location string

@description('Naming suffix shared by every resource in this environment.')
param resourceToken string

@description('Tags applied to every resource.')
param tags object = {}

@description('Log Analytics workspace that receives container stdout/stderr. This is the CONSOLE log stream, separate from the OpenTelemetry pipeline the app exports to Application Insights — you want both: OTel for structured application telemetry, console for startup crashes that happen before OTel is wired up.')
param logAnalyticsWorkspaceId string

@description('Apps to deploy. Each: { name, image, identityResourceId, env (array of {name,value}), targetPort, minReplicas, maxReplicas, external }')
param apps array

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: last(split(logAnalyticsWorkspaceId, '/'))
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${resourceToken}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: workspace.properties.customerId
        // The only listKeys() in this template. Container Apps' log shipper does not
        // support Managed Identity to Log Analytics, so a shared key is unavoidable.
        // It is never persisted: ARM resolves it at deploy time. Worth naming as the
        // one place the credential-free story has a hole.
        sharedKey: workspace.listKeys().primarySharedKey
      }
    }
    zoneRedundant: false
  }
}

resource containerApps 'Microsoft.App/containerApps@2024-03-01' = [
  for app in apps: {
    name: 'ca-${app.name}-${resourceToken}'
    location: location
    tags: union(tags, { service: app.name })
    identity: {
      // User-assigned only. No system-assigned identity, so nothing here is tied to the
      // lifetime of this container app — recreating it does not orphan a single GRANT.
      type: 'UserAssigned'
      userAssignedIdentities: {
        '${app.identityResourceId}': {}
      }
    }
    properties: {
      managedEnvironmentId: environment.id
      configuration: {
        ingress: {
          // External in dev so you can curl a service directly while building it.
          // Production flips this to internal and lets APIM be the only way in —
          // at which point the "do services also validate tokens?" question stops
          // being theoretical, and the answer is still yes.
          external: app.external
          targetPort: app.targetPort
          transport: 'auto'
          allowInsecure: false
        }
        // No secrets block: every credential this app needs is obtained at runtime
        // through its managed identity.
      }
      template: {
        containers: [
          {
            name: app.name
            image: app.image
            resources: {
              cpu: json('0.25')
              memory: '0.5Gi'
            }
            env: app.env
            probes: [
              {
                // Liveness hits /alive, which checks NOTHING but the process. A failure
                // here means restart me.
                type: 'Liveness'
                httpGet: {
                  path: '/alive'
                  port: app.targetPort
                }
                initialDelaySeconds: 10
                periodSeconds: 30
                failureThreshold: 3
              }
              {
                // Readiness hits /health/ready, which DOES check dependencies. A failure
                // here means take me out of rotation — but do not restart me, or a
                // 20-second Postgres blip becomes a cluster-wide restart storm.
                type: 'Readiness'
                httpGet: {
                  path: '/health/ready'
                  port: app.targetPort
                }
                initialDelaySeconds: 5
                periodSeconds: 10
                failureThreshold: 3
              }
            ]
          }
        ]
        scale: {
          // minReplicas: 0 in dev is the single largest cost lever in this template.
          // The price is a cold start on the first request after idling, and — more
          // importantly — a scaled-to-zero app runs no background services, so the
          // OrderService outbox dispatcher must never be allowed to scale to zero.
          minReplicas: app.minReplicas
          maxReplicas: app.maxReplicas
          rules: [
            {
              name: 'http-concurrency'
              http: {
                metadata: {
                  concurrentRequests: '50'
                }
              }
            }
          ]
        }
      }
    }
  }
]

output environmentId string = environment.id
output environmentName string = environment.name
output appFqdns array = [
  for (app, i) in apps: {
    name: app.name
    fqdn: containerApps[i].properties.configuration.ingress.fqdn
  }
]
