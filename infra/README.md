# Infrastructure

Parameterized Bicep for the whole platform.

> **Not deployed.** There is no Azure subscription attached to this repository.
> Everything here **compiles and lints cleanly** (`bicep build`, `bicep lint`,
> `bicep build-params`, enforced in CI). Nothing here has been validated with
> `what-if` or an actual deployment. Treat "it compiles" as the only verified claim.

## Layout

```text
infra/
├── main.bicep                targetScope = 'subscription' — creates the resource group
├── modules/
│   ├── identities.bicep      three user-assigned MIs — deployed FIRST
│   ├── monitoring.bicep      Log Analytics + workspace-based App Insights
│   ├── keyvault.bicep        RBAC-authorized vault (deliberately almost empty)
│   ├── postgres.bicep        Flexible Server, Entra-only auth, 3 databases, PgBouncer
│   ├── storage.bicep         private container, no account keys, lifecycle sweep
│   ├── servicebus.bicep      order-events topic + catalog-stock subscription + filter
│   └── compute.bicep         Container Apps environment + three apps
├── parameters/
│   ├── dev.bicepparam
│   └── prod.bicepparam
└── scripts/
    └── grant-managed-identities.sql   post-deploy; see "What Bicep cannot do" below
```

`apim.bicep` arrives in **Phase 7**. Deploying a gateway in front of services that do
not exist yet would teach nothing and cost ~$50/month while it taught it.

## Deployment order

Expressed through dependencies, not sequence:

```text
identities ──> monitoring ──┬──> keyvault
                            ├──> postgres
                            ├──> storage      ──> compute
                            └──> servicebus
```

**Identities first** is the load-bearing part. Every other module takes a `principalId`
and creates its own role assignments, scoped to its own resource. That is why there is
no generic `rbac.bicep` helper: a role assignment needs the target resource in scope,
and a shared module would have to re-fetch each resource with `existing` — more
indirection, and the scope becomes something you have to go look up rather than read.

## Design decisions worth knowing before you deploy

| Decision | Consequence |
|---|---|
| PostgreSQL `passwordAuth: 'Disabled'` | There is no admin password anywhere — not in Bicep, not in Key Vault, not in a pipeline variable. You authenticate as the Entra admin group. |
| Storage `allowSharedKeyAccess: false` | Account keys stop working entirely. User-delegation SAS becomes the only SAS that functions, which is the intent. |
| Service Bus `disableLocalAuth: true` | No connection strings. Every send/receive is attributable to a specific managed identity in the audit log. |
| Key Vault `enableRbacAuthorization: true` | No access policies. Vault permissions are auditable alongside every other role assignment. |
| Role assignments scoped to container / topic / subscription | Not to the resource group. RBAC is the actual security boundary; a resource-group-scoped grant silently includes every resource added later. |
| `enablePurgeProtection` in prod only | It is **irreversible** — a vault with purge protection cannot be fully deleted until retention expires. That is correct for production and actively annoying in dev. |
| OrderService `minReplicas: 1` always | A scaled-to-zero Container App runs no `BackgroundService`, so the outbox dispatcher would stop. Scale-to-zero is the biggest dev cost saving and OrderService cannot have it. |

## What Bicep cannot do

**PostgreSQL roles and grants.** Azure has no resource type for `CREATE ROLE` or `GRANT`.
The server is an Azure resource; the roles inside it are PostgreSQL state. After
deploying, run [`scripts/grant-managed-identities.sql`](scripts/grant-managed-identities.sql)
once per database, authenticated as the Entra admin. Until that runs, the services can
reach the server and will be rejected at login.

**Entra app registrations.** App registrations, scopes, app roles and admin consent are
Microsoft Graph objects, not ARM resources. Bicep genuinely cannot create them. They are
either clicked once per environment or scripted with `az ad app`; either way that script
belongs with the identity documentation, not here.

**Log Analytics shared key for Container Apps.** The one `listKeys()` in this template.
Container Apps' log shipper does not support Managed Identity to Log Analytics, so a
shared key is unavoidable. ARM resolves it at deploy time and it is never persisted —
but it is worth naming as the single hole in the credential-free story.

## Commands

```bash
bicep build infra/main.bicep
```

```bash
bicep lint infra/main.bicep
```

```bash
bicep build-params infra/parameters/dev.bicepparam
```

Once a subscription exists, the real gate is `what-if` — it is the only step that
catches a policy denial, a quota limit, a name collision, or an API version your
subscription has not been onboarded to:

```bash
az deployment sub what-if --location westeurope --template-file infra/main.bicep --parameters infra/parameters/dev.bicepparam
```

```bash
az deployment sub create --location westeurope --template-file infra/main.bicep --parameters infra/parameters/dev.bicepparam
```

## Before the first deployment

1. Create the Entra **API app registration** (App ID URI `api://ecommerce-dev`, a
   delegated scope `access_as_user`, and the app roles the services check). Put its App
   ID URI in `authAudience`.
2. Create an Entra **group** for database administrators and put its object id in
   `entraAdminObjectId`. Use a group, not a person — a server whose only admin has left
   the company is a support ticket nobody can resolve.
3. Deploy, then run the post-deploy SQL once per database.
