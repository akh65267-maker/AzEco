# Infrastructure

Bicep lands here in **Phase 3**. Planned layout:

```text
infra/
├── main.bicep                targetScope = 'subscription' — creates the resource group
├── modules/
│   ├── identities.bicep      user-assigned MIs — deployed FIRST, everything references them
│   ├── monitoring.bicep      Log Analytics workspace + workspace-based App Insights
│   ├── keyvault.bicep        vault + RBAC assignments to the MIs
│   ├── postgres.bicep        Flexible Server + userdb/catalogdb/orderdb + Entra admin
│   ├── storage.bicep         account + private container + RBAC + lifecycle rules
│   ├── servicebus.bicep      namespace + order-events topic + subscriptions + RBAC
│   ├── apim.bicep            instance + APIs + products + policies
│   ├── compute.bicep         Container Apps environment + the three apps
│   └── rbac.bicep            scoped role-assignment helper
└── parameters/
    ├── dev.bicepparam
    └── prod.bicepparam
```

## Rules

- **No secrets in parameter files.** `dev.bicepparam` and `prod.bicepparam` are
  committed precisely because they contain none. Sensitive values arrive as
  `@secure()` parameters from the deployment pipeline, or via `getSecret()` against an
  existing Key Vault. Anything `*.local.bicepparam` is gitignored.
- **Identities first.** Role assignments are what make Managed Identity work, and they
  need a principal id that exists before the resource being granted access does.
- **Narrow scopes.** Grant on the container, the topic, the subscription — never the
  resource group. RBAC is the actual security boundary; everything else is paperwork.
- **Deterministic names** for role assignments: `guid(scope, principalId, roleDefinitionId)`,
  so redeploys are idempotent rather than erroring on a duplicate.

## Status

Nothing here has been deployed. There is no Azure subscription attached to this
repository. When one exists, the gate is:

```bash
az bicep build --file infra/main.bicep          # compiles — will run in CI now
az deployment sub what-if --location <loc> \
   --template-file infra/main.bicep \
   --parameters infra/parameters/dev.bicepparam # the real gate — needs a subscription
```

Until `what-if` has run against a real subscription, treat "it compiles" as the only
verified claim.
