# Architecture

Phase 1 decision record. Every significant choice here follows
**Problem → Options → Trade-offs → Decision → Failure scenarios**.

Nothing in this document has been deployed. There is no Azure subscription attached
to this repository yet; claims about Azure behaviour are design intent, not test results.

---

## 1. System diagram

```text
                        ┌──────────────┐
                        │   Client     │
                        └──────┬───────┘
                               │ HTTPS + Bearer (Entra ID access token)
                               v
                     ┌─────────────────────┐
                     │ Azure API Management│  routing · JWT validation · rate limit
                     └──────┬───────┬──────┘  versioning · CORS · correlation
                            │       │
        ┌───────────────────┘       └────────────────┬──────────────────┐
        v                                            v                  v
┌────────────────┐                        ┌──────────────────┐  ┌───────────────┐
│  UserService   │                        │  CatalogService  │  │ OrderService  │
└───┬────────────┘                        └───┬──────────┬───┘  └───┬───────┬───┘
    │ MI                                      │ MI       │ MI       │ MI    │ MI
    v                                         v          v          v       v
┌────────── Azure Database for PostgreSQL Flexible Server (one server) ────────┐
│   userdb         │        catalogdb         │            orderdb            │
└──────────────────────────────────────────────────────────────────────────────┘
                                              │                        │
                                              v                        │
                                   ┌────────────────────────┐          │
                                   │ Blob Storage (private) │          │
                                   │   product-images       │          │
                                   └────────────────────────┘          │
                                                                       v
                                   ┌───────────────────────────────────────┐
                                   │ Azure Service Bus                     │
                                   │   topic order-events                  │
                                   │     sub catalog-stock  (+ DLQ)        │
                                   └───────────────────────────────────────┘

Cross-cutting: Entra ID · Managed Identity (one per service) · Key Vault
               App Insights → Log Analytics → Azure Monitor alerts
Future:        CartService → Azure Cache for Redis
```

## 2. Service responsibilities

| Service | Owns | Explicitly does not |
|---|---|---|
| **UserService** | Application user: profile, addresses, preferences, consent, `entra_object_id` mapping. JIT-provisions a row on first authenticated call. | Issue tokens, store passwords, do MFA — Entra ID does that. |
| **CatalogService** | Products, variants, SKUs, categories, prices, stock, image *metadata*. Sole writer of the image blob container. | Store binaries in Postgres; own orders. |
| **OrderService** | Orders, lines, order state machine, price snapshots, the **outbox**. | Read another service's tables; depend on Catalog being up to serve an existing order. |
| **CartService** (future) | Redis-backed ephemeral cart. | — out of scope; only the APIM route shape and event contract are reserved. |

**Hard rule:** no service touches another's schema. Cross-service reads go over HTTP;
cross-service state changes go over Service Bus.

## 3. Azure service responsibilities

| Service | Why it is here | What it must not do |
|---|---|---|
| API Management | Single public ingress, JWT pre-validation, rate limiting, versioning, partner subscriptions | No business logic |
| Entra ID | Identity provider, tokens, app roles, scopes | Not an app-data store (Graph extension attributes are slow and quota'd) |
| Managed Identity | Credential-free auth to PG, Blob, Service Bus, Key Vault | Not for user auth |
| Key Vault | The few genuine secrets; TLS certs | Not a config store |
| PostgreSQL Flexible Server | All relational state, one database per service | Not a message bus, not a cache |
| Blob Storage | Product image binaries | Not metadata, not queryable state |
| Service Bus | Reliable async domain events, retries, DLQ | Not RPC |
| Application Insights | OTel backend: traces, metrics, logs, App Map | Not the only alerting layer |
| Azure Monitor | Alerts, workbooks over app + platform metrics | — |

Deliberately **not** used: Front Door, Event Grid, Cosmos DB, AKS, Dapr, service mesh.
Front Door becomes the right answer when a WAF or edge caching is needed; it is
flagged then rather than pre-built.

## 4. Authentication and authorization

```text
1. Client → Entra ID           Auth Code + PKCE, scope api://ecom-api/access_as_user
2. Entra ID → Client           access token (aud = api://ecom-api) + refresh token
3. Client → APIM               Authorization: Bearer <token>
4. APIM validate-jwt           signature, issuer, audience, expiry, scope present → else 401
5. APIM → service              forwards the ORIGINAL token untouched
6. Service                     re-validates the same token (JwtBearer)
7. Service                     authorizes: scopes/roles + resource ownership (oid == order.user_id)
```

### Q1 — APIM validation vs service validation

**Problem.** Duplicate validation costs latency and risks config drift; single
validation risks a confused deputy.

**Options.** (a) APIM only, services trust a header. (b) Services only, APIM is a dumb
proxy. (c) Both.

**Trade-offs.** (a) is fastest, but anything that reaches the service can impersonate any
user — a misconfigured NSG or an internal tool is enough. (b) lets garbage traffic burn
DB connections and forfeits per-user rate limiting at the edge. (c) costs ~1–3 ms in
APIM (JWKS cached) and near-zero in the service.

**Decision: (c).** APIM does *coarse* validation (signature, issuer, audience, expiry,
scope present). Services do *fine* validation — the same signature check plus the real
authorization: roles, scopes, and **resource ownership**, which APIM cannot do because
it has no data. Implemented in
[`AuthenticationExtensions`](../src/Shared/Ecommerce.ServiceDefaults/AuthenticationExtensions.cs).

**Never** put authorization logic in APIM policies beyond "has a valid token with scope X".

### Q2 — Should services trust APIM?

Only for *transport* concerns (correlation id, forwarded client IP). Never for identity.
To enforce "only APIM may call me", use network restriction (VNet integration + private
endpoints in prod; IP restriction as a cheap interim), not a shared header secret —
a shared gateway key is a secret that gets logged and leaked.

### Q3 — Service-to-service authentication

Two distinct cases; conflating them is a common bug.

- **On-behalf-of the user** (OrderService needs the caller's address from UserService):
  forward the user's token when the audience covers both APIs, or use OBO. Preserves
  the user's authorization context, so UserService can still enforce "only your own profile".
- **App-only / daemon** (a background job with no user): client credentials using the
  service's **Managed Identity**, authorized on an **app role** (`Users.Read.All`),
  not a delegated scope. No client secrets.

**Failure scenarios.** Entra outage → token caching keeps in-flight users alive ~1h;
new logins fail (accepted). Clock skew > 5 min → mass 401s. JWKS cache TTL must be
≤ 24h or a key rollover starts rejecting valid tokens.

## 5. Database architecture

### Q5 — Server vs database vs schema

| Option | Isolation | Cost (dev) | Ops | Blast radius |
|---|---|---|---|---|
| 3 Flexible Servers | Strongest — separate compute, backup, failover | ~3× | 3× patching/monitoring | Contained |
| **1 server, 3 databases** | Strong logical: separate roles, connections, migrations | 1× | 1× | Shared CPU/IOPS/connections |
| 1 server, 1 db, 3 schemas | Weak — one `search_path` slip and the boundary is gone | 1× | 1× | Also shared transactions |

**Decision.** One Flexible Server, **three databases**, three roles, each service's MI
granted exactly one role on exactly one database — enforced by `GRANT`, not convention
(mirrored locally in [`local/postgres-init.sql`](../local/postgres-init.sql)).

In production, extract **OrderService to its own server first**, when either order write
volume contends with catalog reads, or financial data needs a different PITR policy.
That migration is a single-database `pg_dump`/restore — cheap *because* we used
databases, not schemas.

Schemas-in-one-database is rejected outright: it makes the cross-service join
*possible*, and anything possible eventually happens in a hotfix at 2 a.m.

### Connection management — the most likely real outage

Burstable/GP tiers cap connections hard (B1ms ≈ 50). Three services × EF Core pools ×
scale-out exhausts that. Use **PgBouncer** (built into Flexible Server, transaction
pooling) and set `Max Pool Size` explicitly low (~20). Transaction pooling breaks
session-scoped features — Npgsql needs `Max Auto Prepare=0` / `No Reset On Close`.
Alert on connection count at 80% of max.

**Auth.** Entra authentication for Postgres: `DefaultAzureCredential` fetches a token
used as the password, refreshed via Npgsql's periodic-password-provider (tokens live ~1h).
No password in any connection string.

### Q4 — Entra identity vs application user data

```text
users
  id              uuid PK           -- our own key; every FK points here
  entra_object_id uuid UNIQUE NOT NULL   -- the 'oid' claim
  entra_tenant_id uuid NOT NULL
  email           citext            -- a COPY; may go stale; never authoritative
  display_name    text
  created_at, updated_at
```

`oid` is the join key, not the primary key. Using `oid` as the PK couples every foreign
key in the system to the IdP; adding B2C, social login or a second tenant later would
mean migrating every FK instead of adding a row to a `user_identities` table. Cheap
insurance taken on day one.

Profile data does not live in Entra: Graph writes are slow, rate-limited, and
impossible to join with orders. **Entra owns who you are; Postgres owns what you have
done with us.**

**GDPR.** Deleting the Entra user deletes nothing here. An explicit erasure path is
required: anonymize `users`, keep `orders` with a tombstoned reference — statutory
retention on financial records outranks erasure. Decide this before writing the schema.

## 6. Service Bus architecture

### Q7 — Queue vs topic

- **Topic `order-events`** for *events* (facts: `OrderPlaced`, `OrderPaid`, `OrderCancelled`).
  One publisher, N subscribers, each with its own retry state and DLQ. Adding a
  notification consumer later needs zero change to OrderService — that is the whole point.
- **Queue** for *commands* (imperatives with exactly one owner: `ReserveStock`, `IssueRefund`).
  A command with two consumers is a bug.

Initial topology: topic `order-events` + subscription `catalog-stock` with a SQL filter
on `EventType`. No unused queues are provisioned.

### Q8 — Outbox

**Problem.** `SaveChangesAsync()` and `SendMessageAsync()` are not one atomic operation.
Crash between them gives either a lost event (order exists, nobody knows) or a phantom
event (stock decremented for an order that rolled back). Phantoms are worse.

**Options.** (a) publish-then-commit → phantoms. (b) commit-then-publish → loss.
(c) transactional outbox → one extra table and a dispatcher. (d) CDC/Debezium → most
robust, an entire Kafka Connect stack for three services.

**Decision: (c), in OrderService only.** The outbox row is written in the same EF Core
transaction as the order. A `BackgroundService` polls unsent rows
(`FOR UPDATE SKIP LOCKED`, batched), publishes, marks sent. Delivery is at-least-once,
so consumers must be idempotent.

No outbox in User/CatalogService — neither publishes events yet, and ceremony without
a payoff is just cost.

**No MassTransit initially** — it hides exactly the mechanics worth understanding.
Swapping to it later is a contained change and worth doing for a real product.

### Q9 — Idempotency

Two levels, both required.

- **Inbound HTTP.** `POST /orders` accepts an `Idempotency-Key` header; store
  `(key, user_id) → response` with a TTL. Without it, a flaky mobile network sells
  two laptops.
- **Inbound messages.** Every event carries a stable `EventId` (the outbox row id).
  Each consumer writes to `processed_messages(message_id)` with a unique constraint,
  **in the same transaction as the side effect**. Duplicate → unique violation →
  complete the message. `DeliveryCount > 1` is a hint, never a guarantee.

Also make the *effect* idempotent: `UPDATE stock SET qty = qty - 1` is not;
`INSERT INTO stock_reservations(order_id, sku, qty)` with a unique key is.

### Q10 — Retries and DLQ

Three layers that must not fight each other:

1. **SDK** (`ServiceBusRetryOptions`) — transient transport faults only. Max ~3, short.
2. **Broker** (`MaxDeliveryCount` = 5, default 10) — abandon and redeliver after lock
   expiry. For transient *processing* faults: DB deadlock, downstream 503.
3. **Dead-letter** — after `MaxDeliveryCount`, and *immediately and explicitly* for
   poison messages (deserialization failure, unknown event version, permanently violated
   business rule). Retrying a malformed payload five times is five wasted seconds and a
   misleading metric.

`LockDuration` (30s) must exceed p99 handler time, with renewal for long handlers. A
handler that outruns its lock causes silent duplicate processing — the subtlest bug
in the system.

**A DLQ nobody watches is data loss with extra steps.** Alert on
`DeadletteredMessages > 0` and build a replay path.

### Event versioning

1. Additive-only changes to existing types. Never rename or remove a field.
2. Breaking change → new type name: `OrderPlaced` → `OrderPlacedV2`, same topic,
   `EventType`/`EventVersion` in application properties so subscriptions can filter.
3. Publish both during migration; retire V1 when all consumers have moved.
4. Consumers ignore unknown fields and **dead-letter unknown versions** rather than guessing.

Encoded in [`IntegrationEvent`](../src/Shared/Ecommerce.Contracts/IntegrationEvent.cs).

### Where messaging is *not* used

Reading a user profile during order creation, catalog browsing, and price/stock
validation at checkout are all synchronous — the user is waiting on the answer.
The stock *reservation* that follows is async. Async where it buys decoupling or
absorbs load; sync where someone is waiting.

## 7. Blob Storage architecture

**Private container, no anonymous access, ever** — `allowBlobPublicAccess: false` at the
account level so no container can be flipped, and `allowSharedKeyAccess: false` so no
account key exists to leak.

**Upload (admin).**

```text
Client → APIM → CatalogService   POST /products/{id}/images   (metadata only)
CatalogService                   validate role; mint a user-delegation SAS
                                 (write-only, 15 min, single blob path)
       → Client                  { uploadUrl, blobName }
Client → Blob Storage            PUT directly — bypasses our compute entirely
Client → CatalogService          POST .../images/{blobName}/commit
CatalogService                   HEAD the blob: verify existence, size, content type;
                                 then write the metadata row
```

Direct-to-blob because a 5 MB image streamed through the API burns request time, memory
and bandwidth for no value. The trade-off: the client controls the bytes, so verification
happens after the fact — check size, sniff the real content type, never trust the
declared one.

**Download.** Options were (a) short-lived read SAS per image, (b) proxy the bytes
through the service, (c) CDN with origin auth. (a) breaks caching if the URL churns and
leaks a bearer-in-URL into logs; (b) puts compute on the hot path for every thumbnail;
(c) is right at scale but is another service and another bill.

**Decision: (a) with a 1-hour TTL and a stable start time**, so the URL is stable within
the hour and stays cacheable — a deliberate compromise. Revisit with Front Door/CDN when
image egress justifies it.

**User-delegation SAS, not account-key SAS.** Signed with a key obtained via the
service's Managed Identity: no storage key exists in configuration, and access is
revocable by revoking the delegation key. RBAC needed: `Storage Blob Data Contributor`
scoped to *the container*, plus `Storage Blob Delegator` on the account — the second
one is commonly missed and the error message is unhelpful.

**Metadata/binary split.** Postgres holds
`product_images(id, product_id, blob_name, content_type, bytes, width, height, sort_order)`;
Blob holds bytes. They can drift: a blob without a row is an orphan (lifecycle rule
sweeps a `staging/` prefix), a row without a blob is a broken image (hence HEAD before
commit). Accept the drift; design the reconciliation.

## 8. Observability architecture

```text
Your code
  └─ OpenTelemetry SDK            how telemetry is PRODUCED (vendor-neutral)
        │ traces (Activity) · metrics (Meter) · logs (ILogger)
        v
  Azure Monitor OTel Distro       the exporter — the only vendor-specific part
        v
  Application Insights            the PRODUCT: App Map, end-to-end transaction view
        v
  Log Analytics Workspace         the STORE: KQL over requests/dependencies/traces
        v
  Azure Monitor                   alerts, workbooks, action groups — over app AND
                                  platform metrics (SB depth, PG CPU, APIM capacity)
```

### Q12 — App Insights vs Azure Monitor

Not a choice: App Insights is an Azure Monitor component specialised for APM. Use
**workspace-based** App Insights so app and infra telemetry share one workspace and one
KQL query can join "orders failing" to "Postgres CPU at 98%". That join is the entire
reason to co-locate them.

Locally the same instrumentation exports OTLP to the Aspire Dashboard. Application code
never knows the difference — see
[`ObservabilityExtensions`](../src/Shared/Ecommerce.ServiceDefaults/ObservabilityExtensions.cs).

### Q11 — Distributed tracing

W3C Trace Context end to end, native to .NET via `Activity`.

The HTTP path works out of the box. **The messaging path does not**, and this is where
people lose the trace: the producing span ends when the message is sent, and with an
outbox the dispatcher runs *later than the request that created the row*. So:

- Producer: persist `Activity.Current.Id` **into the outbox row**, not just the message.
- Dispatcher: restore it as parent and set `ApplicationProperties["traceparent"]`.
- Consumer: extract it and start the handler activity with `ActivityKind.Consumer`.

Without this the App Map shows two unrelated islands and "why was this order's stock
never decremented" becomes unanswerable.

### The three signals

- **Traces** — sampled (20% in production, 100% elsewhere), with sampling that keeps all
  telemetry for a sampled trace together. Always keep 100% of failures.
- **Metrics** — unsampled and cheap; prefer them to counting log lines. Business signals,
  not just infra: `orders.placed`, `orders.failed`, **`outbox.pending.count`** (the
  earliest signal that async is broken), `servicebus.dlq.count`.
- **Logs** — structured `ILogger` with source-generated `LoggerMessage`, never string
  interpolation. Every log inside a request carries `TraceId`/`SpanId` automatically.

**Correlation id:** `traceparent` *is* it. A second id means two ids that disagree.
The response header `x-request-id` simply surfaces it — see
[`CorrelationMiddleware`](../src/Shared/Ecommerce.ServiceDefaults/CorrelationMiddleware.cs).

### Minimum alert set

1. 5xx rate > 1% over 5 min · 2. `DeadletteredMessages > 0` · 3. `outbox.pending.count > 100`
or oldest-unsent age > 5 min · 4. PG CPU > 80% or connections > 80% of max ·
5. dependency failure rate to Entra/Blob/SB · 6. availability test on `/health/ready`
through APIM.

Health endpoints are split deliberately: `/alive` (no dependencies — failure means
*restart me*) and `/health/ready` (dependencies — failure means *take me out of
rotation*). Conflating them turns a 20-second Postgres blip into a restart storm.

## 9. Managed Identity and Key Vault

**One user-assigned managed identity per service.**

*User-assigned over system-assigned*: the identity outlives the compute resource, so
role assignments and Postgres roles survive a redeploy. With system-assigned, deleting
the app deletes the principal and every grant must be re-created — including
`CREATE ROLE` in Postgres.

*Per-service over shared*: least privilege only means something if the grants differ.
A shared identity means a compromised CatalogService can drain the order topic.

| Identity | Grants |
|---|---|
| `user-mi` | PG role `user_app` on `userdb`; KV Secrets User |
| `catalog-mi` | PG `catalog_app` on `catalogdb`; Blob Data Contributor (**container** scope); Blob Delegator (account); SB Data Receiver on `catalog-stock`; KV Secrets User |
| `order-mi` | PG `order_app` on `orderdb`; SB Data Sender on `order-events`; KV Secrets User |
| `apim-mi` | KV Secrets User (TLS certs) |

Scope assignments to the narrowest resource — the container, the topic, the
subscription. RBAC is the actual security boundary.

### Q13 — Where each kind of configuration belongs

| Location | Contents | Examples |
|---|---|---|
| `appsettings.json` (committed) | Non-secret, environment-invariant | Log levels, retry counts, serializer options |
| `appsettings.{Env}.json` (committed) | Non-secret, environment-specific | Local Docker connection strings |
| Environment variables / App Settings | Non-secret, set by deployment | MI `AZURE_CLIENT_ID`, PG host/db/user, SB namespace, Blob account URL, App Insights connection string |
| Key Vault | Genuine secrets only | Payment provider API key, TLS certs, SMTP creds |
| Managed Identity | The *replacement* for most secrets | PG, Blob, Service Bus, Key Vault access |
| Entra ID | Identity config (all public values) | Tenant id, client ids, scopes, app roles |
| User Secrets (dev, never committed) | Local-only sensitive values | Local third-party test keys |

**The point:** after Managed Identity, Key Vault holds far less than people expect. A
vault full of Azure connection strings *is* the credential problem you were avoiding.
The correct end state is a nearly-empty vault holding only secrets from systems that
do not speak Entra.

Key Vault is read **at startup with a reload interval**, never per request — KV is
rate-limited, and a per-request read makes it a single point of failure for everything.

## 10. Local vs Azure development

See [local-development.md](local-development.md) for the practical setup. Summary of
fidelity, stated honestly:

| Azure service | Local | Fidelity |
|---|---|---|
| PostgreSQL | Docker `postgres:17` | **Excellent** — same engine; only auth differs |
| Blob Storage | Azurite | **Good** — no user-delegation SAS, no RBAC. Real gap. |
| Service Bus | Official SB emulator | **Good** — no sessions at scale, auto-forward or RBAC |
| App Insights | Aspire Dashboard via OTLP | **Excellent** — same instrumentation |
| Key Vault | User Secrets, same config keys | **Good** — code is identical |
| Managed Identity | `DefaultAzureCredential` → Azure CLI login | **Good** — same code path |
| **Entra ID** | **No emulator.** Use a free Entra tenant (recommended), or the Development-only symmetric-key issuer for offline work | Partial |
| **APIM** | **No usable emulator.** Call services directly; optionally YARP for routing | Policies are validated in Azure dev only |

Three environments, one invariant: **the same image and the same code run in all
three.** Only configuration and credential source change. `if (isLocal)` in domain code
means the abstraction is in the wrong place.

## 11. Bicep strategy

```text
infra/
├── main.bicep                targetScope = 'subscription' (creates the RG)
├── modules/
│   ├── identities.bicep      user-assigned MIs — deployed FIRST, everything refs them
│   ├── monitoring.bicep      Log Analytics + workspace-based App Insights
│   ├── keyvault.bicep        vault + RBAC to the MIs
│   ├── postgres.bicep        Flexible Server + 3 databases + Entra admin
│   ├── storage.bicep         account + private container + RBAC + lifecycle rules
│   ├── servicebus.bicep      namespace + topic + subscriptions + RBAC
│   ├── apim.bicep            instance + APIs + products + policies
│   ├── compute.bicep         Container Apps environment + 3 apps
│   └── rbac.bicep            scoped role-assignment helper
└── parameters/{dev,prod}.bicepparam
```

- No secrets in parameter files; sensitive values arrive as `@secure()` params from the
  pipeline or via Key Vault references.
- `uniqueString(...)` for globally-unique names rather than hand-naming.
- Role assignments named with `guid(scope, principalId, roleDefinitionId)` for
  idempotency.
- `az deployment sub what-if` as a PR gate once a subscription exists.

**Compute choice: Azure Container Apps** — cheapest credible host for three containers,
supports user-assigned MI, scale-to-zero in dev, and KEDA scaling on Service Bus queue
depth. App Service is simpler but scales worse per service; AKS is unjustifiable at
three services.

## 12. Main trade-offs

| Decision | Chose | Gave up | Revisit when |
|---|---|---|---|
| APIM **and** service token validation | Defense in depth | ~2 ms, duplicated config | Never |
| One PG server, three databases | Cost + ops simplicity | Noisy-neighbour isolation | Order writes contend with catalog reads |
| Topic over queue for events | Future consumers are free | Slight config overhead | Never |
| Hand-rolled outbox | Understanding, no dependency | MassTransit's maturity | Before a real launch |
| Private blobs + read SAS | No public data leak | CDN cacheability | Image egress becomes a cost line |
| User-assigned MI per service | Stable RBAC, least privilege | More Bicep | Never |
| Container Apps | Cost, MI, KEDA | AKS control | >10 services or custom networking |
| OTel → App Insights | Vendor-neutral code | Minor distro friction | Never |
| No Front Door / CDN yet | ~$35/mo + complexity | WAF, edge caching | Public launch or DDoS concern |
| Free Entra tenant locally | Real tokens | Requires internet | Never |

## 13. Major failure scenarios

| Failure | Blast radius | Mitigation | Residual risk |
|---|---|---|---|
| Entra ID outage | All authenticated traffic | Token cache ~1h; anonymous browsing survives | New logins fail. Accepted. |
| Postgres failover | Services on that server | EF `EnableRetryOnFailure`; HA in prod | ~60s of 503s on non-HA dev |
| **Connection exhaustion** | Every service on the server | PgBouncer, capped pool, alert at 80% | **Most likely real outage** |
| Outbox dispatcher stalls | Events stop; orders still succeed | Alert on pending count and oldest-unsent age | Delayed stock decrement |
| Service Bus throttling | Consumers lag | Outbox retains everything — delay, not loss | Outbox must not grow unbounded |
| Poison message | One subscription blocked | Explicit dead-letter + DLQ alert + replay path | Needs a human |
| Duplicate delivery | Double stock decrement | `processed_messages` in the same transaction | Only if skipped |
| **Lock expiry mid-handler** | Silent reprocessing | `LockDuration` > p99, renewal, idempotent handlers | **Subtlest bug here** |
| Missing/orphan blob | Broken image | HEAD before commit; lifecycle sweep | Cosmetic |
| SAS leak | One blob, ≤1h | Short TTL, single-blob scope, revocable delegation key | Low |
| APIM misconfiguration | Total outage or auth bypass | Config in Bicep, `what-if` gate, post-deploy smoke test | Deployment-time only |
| Schema migration failure | One service down | Expand/contract; migrations as a separate job, never at app startup | Requires discipline |

The two worth losing sleep over: **connection exhaustion** (silent, gradual,
catastrophic) and **lock expiry causing duplicates** (invisible until a card is charged
twice). Both get explicit instrumentation.

## 14. Azure cost drivers

Order-of-magnitude USD/month; varies by region.

| Rank | Service | Dev | Prod-ish | Note |
|---|---|---|---|---|
| 1 | **API Management** | ~$50 (Developer, **no SLA**) | ~$150 (Basic v2) → ~$2,800 (Premium) | The biggest step function. Premium only for VNet + multi-region. |
| 2 | **PostgreSQL** | ~$15–30 (B1ms) | ~$150–400 (GP + HA) | HA doubles compute. Three servers triples it — the reason for one-server-three-databases. |
| 3 | Compute (Container Apps) | ~$0–20 (scale to zero) | ~$50–200 | Scale-to-zero in dev is a large, easy saving. |
| 4 | **Log Analytics ingestion** | ~$5 | **$50–500+** | ~$2.30/GB. Verbose logging silently becomes the #2 bill. **Most under-estimated line.** |
| 5 | Service Bus | ~$10 (Standard) | ~$10–50 | Premium (~$670) only for VNet isolation. |
| 6 | Blob Storage | ~$1 | ~$5–50 | Storage is cheap; **egress and transactions** are the real cost. |
| 7 | Key Vault | ~$1 | ~$1–5 | Negligible if cached. |

**Rough totals:** dev ~$80–120/mo (mostly APIM Developer), production ~$400–900/mo at
modest scale. **Biggest levers:** APIM tier (consider deferring APIM entirely in early
dev — it is over half the dev bill), Log Analytics sampling and retention, scale-to-zero
dev compute, and not splitting Postgres prematurely.

## 15. Complexity deliberately avoided

- **No CQRS or event sourcing.** Three CRUD-ish services do not need it. Event sourcing
  is a permanent commitment made for a temporary feeling of sophistication.
- **No MediatR, no repository over `DbContext`.** `DbContext` is already a unit of work
  and a repository; wrapping it removes EF's query composition and adds indirection.
- **No uniform Clean Architecture.** User and Catalog get API + Infrastructure + a small
  Domain. **OrderService gets four layers** because it has a real domain — a state
  machine, invariants, an outbox. Layering should be proportional to domain complexity.
- **No Dapr, service mesh, or Kubernetes.**
- **No shared "Common" DTO package.** That is how you rebuild the monolith. Only event
  contracts are shared, and they are versioned explicitly.
- **No async messaging for reads. No multi-region.**

## 16. Implementation order

| Phase | Deliverable | Status |
|---|---|---|
| 1 | Architecture (this document) | ✅ |
| 2 | Repo + solution, `ServiceDefaults`, docker-compose, CI | ✅ |
| 3 | Bicep skeleton (compiling only — no subscription to deploy to) | next |
| 4 | UserService: EF Core, Entra auth, JIT provisioning, tests | |
| 5 | CatalogService: products, images, Azurite SAS flow | |
| 6 | OrderService: state machine, outbox, SB publish, idempotent consumer, DLQ | |
| 7 | APIM: Bicep + policies, rate limits, versioning | |
| 8 | Observability hardening: message-path trace propagation, metrics, alerts | |
| 9 | Deployment docs + runbooks (DLQ replay, outbox stall, connection exhaustion) | |
| 10 | CartService: Redis, cache-aside, TTL, source of truth, degradation | |

Two changes from the obvious ordering: **Bicep moves ahead of the services** (writing IaC
afterwards means the code makes assumptions the infrastructure cannot satisfy, and the
MI/RBAC design has to be concrete anyway), and **`ServiceDefaults` is built first** so
every service inherits observability and auth rather than having them bolted on.
