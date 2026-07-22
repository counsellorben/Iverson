# LoadTest tenant provisioning design

## Goal

Dogfood the Tenant Admin API (Part D of the tenant-isolation initiative:
`TenantLifecycleGrpcService`/`TenantAdminGrpcService`, `docs/specs/2026-07-20-tenant-admin-apis-design.md`)
by having `Iverson.LoadTest` provision its own tenant at startup — instead of
relying only on the 5 static dev tenants Authentik blueprints + the API's own
startup backfill already register (`tenant-loadtest`, `tenant-webtest`,
`tenant-admin`, `tenant-smoke-test`, `tenant-bypass` — see
`Iverson.Server/Iverson.Api/Program.cs:384`) — and use the resulting login as
the identity behind LoadTest's regular gRPC traffic.

## Current state (verified)

LoadTest has two independent identity mechanisms today:

1. **Base channel identity** — an optional `client_credentials` credential
   (`IVERSON_CLIENT_ID`/`_SECRET`/`_TOKEN_ENDPOINT`/`_SCOPE` env vars) passed
   to `AddIversonClient`. `Iverson.Client.Core.ServiceCollectionExtensions`
   attaches this **one credential to all four** typed gRPC clients it creates
   (`ObjectMappingServiceClient`, `ObjectPersistenceServiceClient`,
   `ObjectRetrievalServiceClient`, `ObjectSearchServiceClient`) via a single
   `AddCallCredentials` call per client
   (`Iverson.Clients/DotNet/Iverson.Client.Core/ServiceCollectionExtensions.cs:44-52`).
   Without a valid token here, every call is rejected outright — the 4
   data-plane gRPC services have `FallbackPolicy = RequireAuthenticatedUser()`
   (`Iverson.Server/Iverson.Api/Program.cs:142-144`), and `RegisterSchema`
   additionally requires the `SchemaAdmin` policy (`operators` group or
   `schema_admin` scope — `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs:37`).

2. **Acting-user identities** — two human TOTP+PKCE identities
   (`iverson-acting-user-smoke-test` / tenant `tenant-smoke-test`,
   `iverson-loadtest-bypass-user` / tenant `tenant-bypass`), minted via
   `AuthentikFlowExecutorClient`/`ActingUserTokenProvider` and attached
   per-call as the `x-acting-user-authorization` header
   (`ActingUserIdentities.PickRandom()` picks between them 50/50 —
   `Iverson.Server/Iverson.LoadTest/Auth/ActingUserTokenProvider.cs:69`).
   `ReadPathScenario` and `WritePathRunner` attach this header on **every**
   call, with no exceptions (verified by reading both files in full).

**Why this matters for the design:** `ActingUserInterceptor.ValidateActingUserAsync`
is the *only* place that populates `IActingUserAccessor.ActingUser`
(`Iverson.Server/Iverson.Api/Grpc/ActingUserInterceptor.cs:52`), and every
tenant/row/field authorization check in the 4 data-plane services reads
exclusively from that accessor, never from the primary channel principal.
Since the acting-user header is always present on LoadTest's data-plane
calls, **the base channel identity's own `tenant_id` claim has no effect on
what those calls can read or write** — its only two jobs are (a) satisfying
`FallbackPolicy` so the call is accepted at all, and (b) (today) satisfying
`SchemaAdmin` for `RegisterSchema`.

`TenantLifecycleGrpcService.CreateTenant` requires the `Operator` policy
(`Iverson.Server/Iverson.Api/Program.cs:399`) and, per call, creates **one**
tenant registry row plus **one** human-style Authentik user (username/email/
password, group `tenant-admins`) as that tenant's admin
(`Iverson.Server/Iverson.Api/Grpc/TenantLifecycleGrpcService.cs:24-26`) — it
does not create a `client_credentials` service account. `TenantAdminAuthorizationPolicy`
only checks for the `tenant-admins` group
(`Iverson.Server/Iverson.Api/TenantAdminAuthorizationPolicy.cs`), which never
satisfies `SchemaAdmin` — so a tenant-admin login can never call
`RegisterSchema`. `TenantRepository.InsertAsync` (used by `CreateTenant`) is
a plain `INSERT` with no conflict handling
(`Iverson.Server/Iverson.Sql/TenantRepository.cs:10-16`) — calling
`CreateTenant` twice for the same `tenant_id` throws.

`iverson-admin-automation`'s existing OAuth2 provider already carries
`admin`, `schema_admin`, and `tenant_id_admin` scope mappings together
(`Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml:107-131`),
so a single token request with `scope=admin schema_admin` satisfies both
`TenantLifecycleGrpcService`'s `Operator` check and `RegisterSchema`'s
`SchemaAdmin` check — no new Authentik blueprint entries are needed.

`DirectSeeder` (the `seed` command's direct-to-Postgres/StarRocks bulk
loader, bypassing gRPC entirely) stamps `OwnerId` on every row but has no
`TenantId` handling anywhere (verified by grep) — `TenantId` is a nullable
reflection-derived column (`string` properties are always detected
`isNullable: true` — `Iverson.Clients/DotNet/Iverson.Client.Core/SchemaRegistrar.cs:159`),
so `seed` doesn't crash, but every seeded row ends up with a `NULL`
`TenantId` that can never match any caller's `tenant_id` claim. Since
tenant enforcement has no bypass path (confirmed in the mandatory-tenant-
boundary design), this means `read-path` against seeded data already
returns empty results today, for every identity — independent of this
design, but directly relevant since fixing it is the only way `read-path`
produces meaningful output once wired to real identities.

## Design

### 1. Startup tenant provisioning (new, in `Program.cs`)

Before schema registration, LoadTest:

1. Mints a token from `iverson-admin-automation` via the existing
   `IversonClientCredentials`/`CachedClientCredentialsTokenProvider`
   machinery, requesting `scope=admin schema_admin` (extends today's
   `IVERSON_CLIENT_SCOPE` default/usage — no new mechanism).
2. Calls `TenantLifecycleGrpcService.ListTenants` with that token and checks
   for a fixed tenant id (default `iverson-loadtest-dynamic`, overridable via
   `IVERSON_LOADTEST_TENANT_ID`).
3. If absent, calls `CreateTenant` with that tenant id, a fixed display name,
   and a fixed admin username/email/password
   (`IVERSON_LOADTEST_TENANT_ADMIN_USERNAME`/`_EMAIL`/`_PASSWORD`, each with a
   dev-only default mirroring the existing acting-user env var pattern) — so
   re-running LoadTest against the same environment reuses the same tenant
   and login rather than erroring on `CreateTenant`'s duplicate-insert.
4. This one-off call uses a temporary `GrpcChannel` + `TenantLifecycleGrpcServiceClient`
   constructed directly in `Program.cs` (not registered in `AddIversonClient`,
   which has no reason to carry a tenant-lifecycle client for every consumer
   of that shared library) — mirroring how `AuthentikFlowExecutorClient` is
   already constructed directly rather than through DI-registered gRPC
   clients.

### 2. Split channel credentials (small `Iverson.Client.Core` change)

`AddIversonClient` currently takes one `IversonClientCredentials?` and
attaches it to all four typed clients. It gains a second, independent
optional token source used only for the mapping client:

```csharp
public static IServiceCollection AddIversonClient(
    this IServiceCollection services,
    string grpcEndpoint,
    IversonClientCredentials? credentials = null,
    Func<Task<string>>? dataPlaneTokenProvider = null,
    params Assembly[] entityAssemblies)
```

- `credentials` keeps attaching to the mapping client only (schema
  registration — stays on `iverson-admin-automation`, unchanged behavior).
- `dataPlaneTokenProvider`, if supplied, attaches to the persistence/
  retrieval/search clients instead of `credentials` (LoadTest passes the new
  tenant-admin's `ActingUserTokenProvider.GetTokenAsync`); if omitted, those
  three clients fall back to `credentials` exactly as today, so every other
  `AddIversonClient` caller is unaffected.

This is the same "small optional additive parameter" shape already used for
`SchemaRegistrar`'s authorization-supplement parameter — no other consumer
of `Iverson.Client.Core` needs to change.

### 3. Wiring in `Program.cs`

- The tenant-admin's login is minted the same way the 2 existing acting-user
  identities are: `AuthentikFlowExecutorClient` + `ActingUserTokenProvider`,
  same public client id (`iverson-loadtest-human`) since Authentik's
  Authorization Code + PKCE application isn't user-specific.
- `AddIversonClient(grpcUrl, adminAutomationCredentials, tenantAdminTokenProvider.GetTokenAsync, ...)`.
- The 2 existing `ActingUserIdentities` (smoke-test, bypass) are constructed
  exactly as today and continue to gate `ReadPathScenario`/`WritePathRunner`'s
  row/tenant/field authorization via the acting-user header — untouched.

### 4. `DirectSeeder` `TenantId` fix

The two stores need different mechanisms, since only Postgres uses a shared
table + column predicate for tenant scoping — StarRocks (per
`project-starrocks-tenant-database-isolation`, already merged to `main`)
isolates tenants as **separate physical databases**
(`iverson_tenant_{tenantId}`; see `TenantIdentifier.DatabaseName` and
`StarRocksRepository.cs`'s `Search`/`Aggregate`/`GroupBy`/`Pipeline` read
paths, which qualify every read with that database once a tenant claim is
present), lazily provisioned only through the gRPC write path. A `TenantId`
*column value* on a row in the shared, unqualified StarRocks table has no
effect on what `Search`/`Aggregate` can see.

- **Postgres** — every row `DirectSeeder` writes (articles, authors, tags)
  gets `TenantId` stamped alongside the existing `OwnerId` logic in its raw
  `COPY` statements, split 50/50 between `tenant-smoke-test` and
  `tenant-bypass` (mirroring `PickRandom`'s existing 50/50 split) — unchanged
  from the original approach.
- **StarRocks** — `DirectSeeder` stops writing these rows via
  `SrBatchInsertAsync` and instead posts them through the same gRPC write
  path `WritePathRunner` already uses (`EntityCoordinator<T>.PersistAsync`
  carrying the acting-user header), alternating between the `Regular`/
  `Bypass` identities per row (same 50/50 split as the Postgres side). The
  server's existing lazy per-tenant-database provisioning then creates and
  populates `iverson_tenant_tenant-smoke-test`/`iverson_tenant_tenant-bypass`
  itself, auto-stamping `TenantId` from the posting identity's own claim —
  the same mechanism `write-path` already relies on, so `DirectSeeder`
  doesn't compute a `TenantId` value for these rows itself.
  `ObjectPersistenceGrpcService.Post` always assigns its own server-generated
  key (client-supplied `Id`s are ignored —
  `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs:43-45`)
  and unconditionally upserts the entity into Postgres in the same call,
  regardless of which secondary stores `targetStores` names
  (`Iverson.Server/Iverson.Api/Schema/StoreTargeting.cs:19-25` only gates the
  Engagement/Intelligence Kafka projections, never Postgres) — so these
  posts also create a second, independently-keyed population of Postgres
  rows alongside `DirectSeeder`'s own COPY-inserted ones. This is harmless:
  neither `GetMany` (which loads its key pool fresh from Postgres with no
  correlation assumption) nor `Search`/`Aggregate` (which query by
  category/word-count/date, never by a specific pre-known id) needs the two
  stores' rows to share an identity. This trades `seed`'s previous
  raw-batched-`INSERT` StarRocks throughput for per-record gRPC calls (exact
  concurrency/throughput is an implementation-plan detail, not fixed here),
  and — like `write-path` — the data isn't visible to `Search`/`Aggregate`
  until Kafka has caught up, so `seed` should wait on the same
  Kafka-lag-probe convention `WritePathScenario` already uses before
  reporting done.

Whichever store, the newly-provisioned tenant (`iverson-loadtest-dynamic`)
does **not** need seeded data, since (per "Current state" above) it never
gates read-path visibility — its role is limited to being the base channel
identity.

## Out of scope

- Fixing the pre-existing `DirectSeeder` `TenantId` gap for the two already-
  existing acting-user identities is included here (§4) because it's needed
  to make this design's own read-path traffic meaningful; deeper load-test
  data-distribution changes are not part of this work.
- No change to `TenantLifecycleGrpcService`/`TenantAdminGrpcService`
  themselves, or to Authentik blueprints — this design only adds a caller.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| 1 | `CreateTenant`/`ListTenants` require the `Operator` policy | `Iverson.Server/Iverson.Api/Program.cs:399` |
| 2 | A `tenant-admins`-group login never satisfies `SchemaAdmin` | `TenantAdminAuthorizationPolicy.cs`, `SchemaAdminAuthorizationPolicy.cs` |
| 3 | `iverson-admin-automation` already carries `admin`+`schema_admin`+`tenant_id_admin` scope mappings | `blueprints/compose-only/service-clients.yaml:107-131` |
| 4 | `AddIversonClient` attaches one shared credential to all 4 typed clients | `Iverson.Client.Core/ServiceCollectionExtensions.cs:44-52` |
| 5 | Tenant/row/field authorization reads only `IActingUserAccessor.ActingUser`, populated only from the acting-user header | `Iverson.Server/Iverson.Api/Grpc/ActingUserInterceptor.cs:44-52` |
| 6 | `ReadPathScenario`/`WritePathRunner` attach the acting-user header on every call | full read of both files |
| 7 | `DirectSeeder` never writes `TenantId` | grep of `DirectSeeder.cs` |
| 8 | `TenantId` column is nullable (no NOT NULL crash on seed) | `SchemaRegistrar.cs:159` (`isNullable = !type.IsValueType`) |
| 9 | `ActingUserIdentities.PickRandom()` is a uniform 50/50 split | `ActingUserTokenProvider.cs:69` |
| 10 | Proposed tenant id `iverson-loadtest-dynamic` matches `TenantIdentifier.IsValid`'s pattern | `Iverson.Server/Iverson.StarRocks/TenantIdentifier.cs:7` |
| 11 | `CreateTenant` is not idempotent — a duplicate `tenant_id` throws | `TenantRepository.cs:10-16` (plain `INSERT`, no `ON CONFLICT`) |
| 12 | `Iverson.Client.Contracts` (tenant proto clients) already available to LoadTest transitively | `Iverson.Client.Core.csproj` → `Iverson.Client.Contracts.csproj` project reference |
| 13 | `AuthentikFlowExecutorClient`'s TOTP enroll-or-solve logic is generic, not hardcoded to the 2 existing identities — works for a brand-new user with no prior enrollment | `AuthentikFlowExecutorClient.cs:140-183` (`ak-stage-authenticator-totp` case generates+saves a fresh secret for any identity) |
| 14 | StarRocks tenant isolation is per-tenant physical databases (`iverson_tenant_{tenantId}`), not a column predicate on a shared table — provisioned lazily only via the gRPC write path | `Iverson.Server/Iverson.StarRocks/StarRocksRepository.cs:341-475` (`Search`/`Aggregate`/`GroupBy`/`Pipeline` qualify reads with `TenantIdentifier.DatabaseName(tenantId)` whenever a tenant claim is present), `TenantIdentifier.cs:11` |
| 15 | `ObjectPersistenceGrpcService.Post` always assigns its own server-generated key (ignores client-supplied `Id`) and unconditionally writes the entity to Postgres regardless of `targetStores`, which only gates secondary Engagement/Intelligence Kafka projections | `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs:43-45,54-65`, `Iverson.Server/Iverson.Api/Schema/StoreTargeting.cs:19-25` |
