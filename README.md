# Azure Ecommerce Microservices Template

A production-oriented .NET microservices template targeting Azure: three services,
one gateway, event-driven where it earns its place, and observability designed as an
architecture rather than bolted on.

> **Not deployed.** There is no Azure subscription attached to this repository.
> The Bicep will compile; nothing here has been validated against live Azure resources.

## Documentation

| Document | Contents |
|---|---|
| [docs/architecture.md](docs/architecture.md) | The decision record: every significant choice as Problem → Options → Trade-offs → Decision → Failure scenarios |
| [docs/local-development.md](docs/local-development.md) | Running the whole stack with no Azure subscription, and the fidelity gaps that entails |

## Repository layout

```text
src/
  Shared/
    Ecommerce.ServiceDefaults/   observability, Entra auth, health, resilience
    Ecommerce.Contracts/         integration event envelope — the ONLY shared type
  UserService/                   Api · Domain · Infrastructure
  CatalogService/                Api · Domain · Infrastructure
  OrderService/                  Api · Application · Domain · Infrastructure
tests/
  UserService.Tests/ CatalogService.Tests/ OrderService.Tests/
infra/                           Bicep (Phase 3)
local/                           docker-compose support files
docs/
```

**Why OrderService has four layers and the others have three.** Layering should be
proportional to domain complexity, not applied uniformly. User and Catalog are
CRUD-shaped: an API, an EF Core infrastructure layer, and a thin domain is honest and
readable. OrderService has a real domain — a state machine, invariants that must hold
across aggregates, and a transactional outbox — so it gets an Application layer to keep
orchestration out of both the endpoints and the entities. Adding that layer to
UserService would be ceremony.

**Why there is no `Common` library.** A shared library of DTOs and helpers across
service boundaries is how a microservice system quietly becomes a distributed monolith:
one change forces a coordinated redeploy of everything. `Ecommerce.ServiceDefaults` is
allowed because it contains only cross-cutting *infrastructure* (nothing business-shaped),
and `Ecommerce.Contracts` is allowed because event contracts are a deliberate, versioned
public API.

## Quick start

```bash
docker compose up -d
dotnet run --project src/UserService/UserService.Api
```

Then open the Aspire Dashboard at <http://localhost:18888> to see traces.
Full setup, including authentication modes, is in
[docs/local-development.md](docs/local-development.md).

## UserService

| Endpoint | Auth | Notes |
|---|---|---|
| `GET /me` | user scope | Creates the local row on first call (JIT provisioning) |
| `PUT /me` | user scope | |
| `GET/POST /me/addresses` | user scope | First address becomes both defaults |
| `PUT/DELETE /me/addresses/{id}` | user scope | |
| `DELETE /me` | user scope | Right to erasure — redacts, does not delete |
| `GET /internal/users/{id}` | app **role** `Users.Read.All` | Service-to-service; not exposed through APIM |

There is **no `/register`** and **no `/users/{id}` for end users**. Entra already
performed the signup, so a registration endpoint would be a second source of truth about
who exists. And an endpoint that takes a user id is an endpoint where someone eventually
forgets the ownership check — if the id can only come from the token, that class of bug
cannot be written.

Migrations:

```bash
dotnet dotnet-ef migrations add <Name> --project src/UserService/UserService.Infrastructure --startup-project src/UserService/UserService.Infrastructure --output-dir Persistence/Migrations
```

They are **not** applied at startup. Schema changes are a deployment step, not a
side effect of a container booting — otherwise three replicas racing to migrate is the
first thing that happens on every scale-out.

## Conventions

- **Warnings are errors** (`Directory.Build.props`). CI enforces it.
- **Central Package Management** (`Directory.Packages.props`) — one version per package
  for the whole repo. `PackageReference` entries carry no `Version`.
- **No secrets in the repository.** Enforced by `.gitignore` plus a gitleaks job in CI,
  because "don't commit secrets" as a policy has a 100% eventual failure rate.

## Status

| Phase | | |
|---|---|---|
| 1 | Architecture | ✅ |
| 2 | Repository, solution, ServiceDefaults, local stack, CI | ✅ |
| 3 | Bicep infrastructure (compiles & lints; APIM deferred to Phase 7) | ✅ |
| 4 | UserService: domain, EF Core, Entra auth, JIT provisioning, tests | ✅ |
