# Local development

No Azure subscription required. Everything below runs on Docker plus the .NET SDK.

## Prerequisites

- .NET SDK 10.0.300+ (pinned in `global.json`)
- Docker Desktop
- Optionally: a **free Microsoft Entra tenant** for real tokens (recommended — see below)

## Start the infrastructure

```bash
docker compose up -d
```

| Service | Endpoint | Notes |
|---|---|---|
| PostgreSQL | `localhost:5432` | `userdb`, `catalogdb`, `orderdb` created with three least-privilege roles |
| Azurite (blob) | `localhost:10000` | Well-known dev account key |
| Service Bus emulator | `localhost:5672` | Topic `order-events`, subscription `catalog-stock` |
| Aspire Dashboard | <http://localhost:18888> | Traces, metrics, logs |

Services export OTLP to `localhost:4317`, which Compose maps to the dashboard's
OTLP port. Open the dashboard before making requests — it does not retain history
from before it started.

## Run the services

```bash
dotnet run --project src/UserService/UserService.Api
```

| Service | Port |
|---|---|
| UserService | 5001 |
| CatalogService | 5002 |
| OrderService | 5003 |

There is no local API Management. Call services directly on those ports. APIM policies
are validated in the Azure dev environment; do not try to replicate them locally or you
will end up maintaining a second gateway.

## Authentication locally

Two modes. Pick per developer, not per environment.

### Mode A — real Entra tenant (recommended)

You can create a free Entra tenant without a paid Azure subscription. This is the
highest-fidelity option: real token shapes, real `oid`/`scp`/`roles` claims, real
issuer and key rollover behaviour. Every 401 you hit locally is a 401 you would
have hit in Azure.

Set in **User Secrets** (never `appsettings.json`):

```bash
dotnet user-secrets --project src/UserService/UserService.Api set "Auth:TenantId" "<tenant-guid>"
dotnet user-secrets --project src/UserService/UserService.Api set "Auth:Audience" "api://ecommerce-dev"
```

Leave `Auth:LocalDevSigningKey` unset — its presence is what switches modes.

### Mode B — local symmetric-key issuer (offline)

`appsettings.Development.json` ships with `Auth:LocalDevSigningKey` set, so tokens
signed with that key are accepted. Use it on a plane, or in tests.

This path is guarded by `builder.Environment.IsDevelopment()` in
`AuthenticationExtensions`. That guard is load-bearing: a symmetric signing key in
configuration is a forgeable identity, and it must be impossible to activate in any
deployed environment. Do not remove the guard, and do not copy the key into a
non-Development config file.

## Fidelity gaps you must know about

| Gap | Consequence |
|---|---|
| Azurite has **no user-delegation SAS and no RBAC** | The security-critical blob authorization path is only ever exercised in Azure. Locally the code takes an account-key SAS branch. Review that code by reading it, not by running it. |
| SB emulator has no RBAC, sessions at scale, or auto-forward | Connection-string auth locally, Managed Identity in Azure. Lock renewal and delivery-count behaviour are close but not guaranteed identical. |
| No APIM | Rate limits, JWT pre-validation, versioning and CORS policies are untested until the Azure dev environment. |
| Postgres uses password auth | Azure uses an Entra token as the password, refreshed hourly. Only the connection factory differs. |

None of these are worked around with `if (isLocal)` branches in domain code. Where the
two differ, the difference lives behind one interface with two implementations, and only
the Azure implementation is security-critical.

## Environment matrix

| | Local | Azure Dev | Production |
|---|---|---|---|
| Postgres | Docker, password | Flexible Server B1ms, Entra token | GP + HA + PITR, Entra token |
| Blob | Azurite, account key | Storage LRS, user-delegation SAS | ZRS + soft delete |
| Service Bus | Emulator, connection string | Standard, Managed Identity | Standard, Managed Identity |
| Secrets | User Secrets | Key Vault | Key Vault |
| Identity | Free tenant or local issuer | Entra app regs (dev) | Entra app regs (prod) |
| Gateway | none | APIM Developer | APIM Basic v2+ |
| Telemetry | Aspire Dashboard | App Insights (100% sampling) | App Insights (20% sampling) |

## Tests

```bash
dotnet test
```

Integration tests that need real Postgres/Azurite/Service Bus will use Testcontainers
(added in Phase 4) rather than assuming `docker compose` is already running — CI must
not depend on a developer's local stack.
