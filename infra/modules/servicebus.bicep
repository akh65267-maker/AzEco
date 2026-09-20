// Azure Service Bus.
//
// Topology mirrors local/servicebus-config.json exactly, so a handler that works against
// the local emulator is wired against the same names in Azure.
//
// TOPIC for events (facts: OrderPlaced, OrderPaid, OrderCancelled). One publisher,
// N subscribers, each with its own retry state and its own dead-letter queue. Adding a
// notification consumer later requires zero change to OrderService — that is the whole
// reason to pay the small extra configuration cost over a queue.
//
// QUEUE for commands (imperatives with exactly one owner: ReserveStock, IssueRefund).
// A command with two consumers is a bug. None are provisioned yet, because provisioning
// entities nobody sends to is just clutter that looks like architecture.
//
// Standard tier, not Premium: Premium is ~$670/mo and buys VNet isolation plus
// predictable latency. Neither is justified at this scale.

@description('Azure region.')
param location string

@description('Naming suffix shared by every resource in this environment.')
param resourceToken string

@description('Tags applied to every resource.')
param tags object = {}

@allowed([
  'Standard'
  'Premium'
])
param skuName string = 'Standard'

@description('Principal ids allowed to PUBLISH order events — OrderService only.')
param eventSenderPrincipalIds array = []

@description('Principal ids allowed to CONSUME the catalog-stock subscription — CatalogService only.')
param stockConsumerPrincipalIds array = []

var topicName = 'order-events'
var stockSubscriptionName = 'catalog-stock'

resource namespace 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: 'sb-${resourceToken}'
  location: location
  tags: tags
  sku: {
    name: skuName
    tier: skuName
  }
  properties: {
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'

    // No SAS connection strings. Every client authenticates with its Managed Identity,
    // which means there is no shared secret to rotate and every send/receive is
    // attributable to a specific service principal in the audit log.
    disableLocalAuth: true
  }
}

resource orderEvents 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: namespace
  name: topicName
  properties: {
    defaultMessageTimeToLive: 'P14D'
    enablePartitioning: false

    // Broker-side duplicate detection is deliberately OFF. It only catches duplicates
    // within its time window and only by MessageId — it is not a substitute for the
    // consumer-side processed_messages table, and enabling it invites the belief that
    // it is. Idempotency is the consumer's job; see docs/architecture.md §6.
    requiresDuplicateDetection: false
  }
}

resource catalogStock 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  parent: orderEvents
  name: stockSubscriptionName
  properties: {
    // Must exceed p99 handler time. A handler that outruns its lock has its message
    // redelivered while it is still working — silent duplicate processing, and the
    // subtlest bug in this architecture.
    lockDuration: 'PT30S'

    // 5, not the default 10. Retrying a poison message ten times is ten wasted seconds
    // and a metric that lies about the failure rate.
    maxDeliveryCount: 5

    deadLetteringOnMessageExpiration: true
    deadLetteringOnFilterEvaluationExceptions: true
    defaultMessageTimeToLive: 'P14D'
  }
}

// SQL filter on an application property, so the broker discards irrelevant events
// without the consumer deserializing them. The property is set by the outbox dispatcher.
resource stockFilter 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2024-01-01' = {
  parent: catalogStock
  name: 'order-placed-or-cancelled'
  properties: {
    filterType: 'SqlFilter'
    sqlFilter: {
      sqlExpression: 'EventType IN (\'OrderPlaced\',\'OrderCancelled\')'
    }
  }
}

var serviceBusDataSenderRoleId = '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
var serviceBusDataReceiverRoleId = '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'

// Scoped to the TOPIC. Account-scoped would let OrderService send to any entity in the
// namespace, including ones added later by another team.
resource eventSenders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in eventSenderPrincipalIds: {
    name: guid(orderEvents.id, principalId, serviceBusDataSenderRoleId)
    scope: orderEvents
    properties: {
      principalId: principalId
      roleDefinitionId: subscriptionResourceId(
        'Microsoft.Authorization/roleDefinitions',
        serviceBusDataSenderRoleId
      )
      principalType: 'ServicePrincipal'
    }
  }
]

// Scoped to the SUBSCRIPTION, so CatalogService can read its own subscription and
// nobody else's — including the DLQ replay path, which runs as the same identity.
resource stockConsumers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in stockConsumerPrincipalIds: {
    name: guid(catalogStock.id, principalId, serviceBusDataReceiverRoleId)
    scope: catalogStock
    properties: {
      principalId: principalId
      roleDefinitionId: subscriptionResourceId(
        'Microsoft.Authorization/roleDefinitions',
        serviceBusDataReceiverRoleId
      )
      principalType: 'ServicePrincipal'
    }
  }
]

output namespaceId string = namespace.id
output namespaceName string = namespace.name
output fullyQualifiedNamespace string = '${namespace.name}.servicebus.windows.net'
output topicName string = topicName
output stockSubscriptionName string = stockSubscriptionName
