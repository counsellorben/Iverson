# Qdrant Tenant Collection Isolation (Part B2) — Design Spec

**Status:** Draft, pending user review
**Date:** 2026-07-18
**Part of:** Tenant Data Isolation & Compliance Readiness initiative, Part B (DB-level defense-in-depth), sub-part B2 of 3 — see [Scope and decomposition](#scope-and-decomposition)

## Motivation

Part A (`2026-07-17-mandatory-tenant-boundary-design.md`) made every registered schema's tenant boundary mandatory and enforced it at the application layer. Part B1 (`2026-07-18-postgres-row-level-security-design.md`) added a database-enforced backstop for Postgres, so an application-layer bug can't silently reintroduce a cross-tenant leak there. This spec does the equivalent for Qdrant: today, `QdrantFilterBuilder.ApplyTenant` injects a payload-match filter into search requests at the application layer, but nothing beneath that layer enforces it — if the app forgot to call `ApplyTenant`, or computed the wrong tenant value, a shared Qdrant collection would return another tenant's vectors with nothing to stop it.

**This spec's design differs materially from B1's original premise, and from how B2 was described when Part B was first decomposed.** B1's own spec (and this initiative's earlier planning) described B2 as "Qdrant JWT-based payload-filter RBAC" — a token whose `access` claim could carry a payload filter that Qdrant itself would enforce on every query, mirroring Postgres RLS closely. **That Qdrant feature no longer exists.** JWT payload filters were deprecated in Qdrant v1.15 and fully removed in v1.16 (per Qdrant maintainer confirmation: tokens using them are now rejected outright) — this repo runs `qdrant/qdrant:v1.18.2`, well past removal. Qdrant's current JWT RBAC only supports collection-*level* access (global or per-collection `read`/`read-write`/`manage`), not point/payload-level filtering.

The only way to get a database-enforced tenant boundary in Qdrant today is to make the **collection itself** the tenant boundary: one physical collection per (entity type, tenant) pair, with a per-request JWT whose `access` claim restricts it to exactly that one collection. This is a materially bigger change than B1's "add a policy to an existing shared table" — it changes Qdrant's collection topology, not just its access-control layer. The rest of this document treats that as the accepted premise (confirmed with the user directly), not an open question.

## Scope and decomposition

See B1's spec for the full three-way split of Part B (Postgres RLS / Qdrant / StarRocks) and why StarRocks (B3) is materially larger and deferred. This section supersedes B1's one-line description of B2 with the corrected premise above.

Explicitly out of scope for this spec: B3 (StarRocks); any change to Part A's application-level enforcement (unchanged); backfilling tenant values onto pre-Part-A legacy rows; fixing the pre-existing lack of TLS on the Qdrant gRPC channel (see "Known limitations" below — a real, separate gap, not introduced or worsened in kind by this design).

## Design

### The core mechanism

Qdrant's native JWT RBAC (`jwt_rbac: true` in server config, alongside the existing `api_key`) validates a JWT using the configured `api_key` as an HMAC-SHA256 signing secret — no new secret to provision. A token's `access` claim can restrict it to one or more specific collections, each with `"r"` (read-only) or `"rw"` (read-write) access:

```json
{
  "exp": 1737230445,
  "access": [{ "collection": "articles_acme-corp", "access": "rw" }]
}
```

If a token's `access` claim doesn't name a collection, Qdrant refuses any operation against it — this is the actual enforcement point. A per-tenant, per-call JWT that only ever names that one tenant's collection makes it structurally impossible for a request — regardless of what the application code intended — to touch a different tenant's data, the same backstop property B1 achieves via Postgres RLS, achieved by a different mechanism appropriate to what Qdrant actually offers.

### Collection topology and naming

Today, `SchemaBuilder` computes one `CollectionName` per registered type (`SchemaBuilder.cs:102`, `= tableName` when the type has vector or chunk fields, else `null`), and a `_chunks` sibling collection name is computed independently, inline, at exactly three call sites: `ObjectSearchGrpcService.cs:239` (`SearchChunks`), and `IntelligenceStoreConsumer.cs:149,220` (upsert and delete). Those are the same three places this design touches for the main-vs-tenant naming step, too.

**`SchemaDescriptor.CollectionName` stays exactly as it is today** — the base/logical name, computed once per type, unrelated to any tenant. A new tenant-qualification step, added at the same three call sites (plus `ObjectSearchGrpcService.cs:173`'s `SearchSimilar` call, which doesn't already have inline chunk-suffix logic but needs the same tenant-qualification), computes the physical collection name actually sent to Qdrant:

- Main: `{CollectionName}_{tenantId}`
- Chunks: `{CollectionName}_chunks_{tenantId}`

`QdrantCollectionManager.ApplyCollectionAsync`/`MigrateCollectionAsync` are naming-agnostic today — they operate purely on whatever `CollectionSchema.CollectionName` string they're given (confirmed by reading `QdrantCollectionManager.cs` directly: every reference is `schema.CollectionName`, with no assumption baked in about what that string represents). Calling this idempotent logic once per (type, tenant) pair, the first time each pair is seen, is architecturally identical to calling it once per type today — each tenant-qualified name is just an independent collection as far as this logic is concerned.

### Collection lifecycle: lazy creation

Tenants aren't provisioned ahead of time — they appear as a claim value on first use, with no tenant registry to iterate eagerly. Per-tenant collections are therefore created **lazily, on first write**, extending `IntelligenceStoreConsumer`'s existing `EnsureChunkCollectionAsync` pattern (which already does a per-process-cached, idempotent check-then-create for the chunks collection at ingest time) to also cover the main collection, both keyed by the tenant-qualified name.

`SchemaRegistrationOrchestrator` **stops** eagerly calling `vector.ApplyCollectionAsync` at registration time (`SchemaRegistrationOrchestrator.cs:72,75` today) — there is no longer a single "the" collection for a type to create at registration time, only per-tenant instances created on demand. Registration still validates the schema shape (mandatory `tenant_field`, vector/chunk field validation) exactly as it does today; it just no longer creates Qdrant collections itself.

**Reads against a tenant with no collection yet** (a brand-new tenant, zero writes so far): Qdrant returns a distinct, empirically-confirmed `404`/gRPC `NotFound` for a search against a nonexistent collection (see Verified assumptions). `SearchSimilar`/`SearchChunks` catch this specific status and treat it as an empty result set, without creating a collection just to search it.

### JWT minting

A new `QdrantTenantScope` class in `Iverson.Vector` mints a JWT per Qdrant call:

```csharp
string MintScopedApiKey(string collectionName, bool readOnly)
```

using `System.IdentityModel.Tokens.Jwt`'s `JwtSecurityTokenHandler` with a `SymmetricSecurityKey` built from the existing `Qdrant:ApiKey` config value and `HmacSha256Signature`:

- `exp`: `now + 30 seconds` — just long enough to cover one Qdrant call, mirroring B1's transaction-scoped `SET LOCAL ROLE`.
- `access`: `[{ "collection": collectionName, "access": readOnly ? "r" : "rw" }]`.

**Fail-closed carve-out.** Part A's evaluator already denies upstream whenever a tenant-scoped schema's request is missing a `tenant_id` claim, so these call sites should never be reached with a null tenant value in practice. Mirroring B1's philosophy exactly — never trust that upstream code already gated correctly — if a tenant-scoped call is ever reached with a null tenant value anyway, `MintScopedApiKey` is called with the collection name computed as `{CollectionName}__no-tenant-claim__` (or the chunks equivalent, `{CollectionName}_chunks__no-tenant-claim__`) instead of skipping the mint. A `__no-tenant-claim__`-suffixed name can never collide with a real `{CollectionName}_{tenantId}` name, since tenant IDs are Authentik-issued claim values, not free-form strings under application control. Qdrant's own access check is what fails the request against this permanently-nonexistent collection, not an app-level null check.

The minted JWT is attached per-call via `Qdrant.Client.RequestHeaders.Use("api-key", jwt)` — a scoped, `AsyncLocal`-backed override (confirmed via decompiling `Qdrant.Client` 1.18.1) that returns an `IDisposable`, correctly reverting after the `using` block and correctly flowing through nested async calls:

```csharp
using (RequestHeaders.Use("api-key", QdrantTenantScope.MintScopedApiKey(collectionName, readOnly: true)))
{
    var results = await vector.SearchNamedAsync(collectionName, vectorName, queryVector, topK, filter);
}
```

No new client/connection is needed — the existing singleton `QdrantClient` (constructed once with the static admin `api_key`) is reused for every call; the per-call header override supersedes the static key for that one call only.

### Call-site wiring (3 sites, matching B1's "direct call at the point of use" pattern — no interceptor)

An earlier direction considered minting inside a gRPC server interceptor (to centralize the logic the way `ActingUserInterceptor` centralizes acting-user extraction). This was rejected: minting requires knowing the exact target collection, which requires a `TypeName` → `SchemaDescriptor` → `CollectionName` lookup and knowing whether the call is against the main or chunks collection — logic that already lives inline in each service method (`RequireSchema(request.TypeName)`-style lookups), and which `IntelligenceStoreConsumer` (a Kafka `BackgroundService`, not a gRPC call at all) couldn't go through an interceptor for regardless. All three call sites use the same direct helper call instead:

- **`ObjectSearchGrpcService.SearchSimilar`/`SearchChunks`**: after computing `tenantScoped`/tenant value (the same claim-sourcing this design's read paths already use for `ApplyTenant` today), compute the tenant-qualified collection name, mint a **read-only** (`readOnly: true`) JWT, wrap the `SearchNamedAsync` call.
- **`IntelligenceStoreConsumer`**: re-derive the tenant value from the authoritative Postgres row, the same pattern already used for owner re-derivation (`FetchAuthoritativeOwnerValueAsync`, `IntelligenceStoreConsumer.cs:233-246`) — `SchemaDescriptor.TenantColumn` is read the same way `Authorization.OwnerField` is read today. Ensure the tenant's collection(s) exist (lazy create, described above), mint a **read-write** (`readOnly: false`) JWT, wrap the upsert/delete calls.

### `QdrantFilterBuilder.ApplyTenant` is removed

Once collections are split per tenant, a search against `articles_acme-corp` can only ever return `acme-corp`'s points — `ApplyTenant`'s payload-based tenant filter (`QdrantFilterBuilder.cs:62`) becomes structurally incapable of doing anything at the two read call sites that call it today. It is removed from those call sites as part of this change. `ApplyOwnership` is unaffected — ownership is an orthogonal dimension that still varies *within* a single tenant's collection, so it continues to filter exactly as it does today.

### Deployment/configuration

Enable `jwt_rbac: true` on the Qdrant server (currently unset/off) — `QDRANT__SERVICE__JWT_RBAC=true` alongside the existing `QDRANT__SERVICE__API_KEY` in `docker-compose.yml`, and the equivalent addition to the Helm chart's StatefulSet env vars (`Iverson.Server/deploy/helm/iverson/charts/qdrant/templates/statefulset.yaml`, next to where `QDRANT__SERVICE__API_KEY` is already wired from the chart's Secret). No new secret or credential is introduced — the existing `Qdrant:ApiKey` config value becomes doubly-purposed: still the raw admin key used for collection-management calls (`QdrantCollectionManager`), now also the JWT signing secret for minted per-tenant tokens.

### Known limitations (surfaced, not fixed by this design)

- **Pre-existing lack of TLS.** Qdrant's own documentation recommends TLS whenever `api_key` is configured (sending it over an unencrypted channel is explicitly called out as insecure). This repo's `QdrantClient` is constructed with `https: false` today (`ServiceCollectionExtensions.cs:14`) — already true with the static key, not introduced by this design. It does mean the new short-lived per-tenant JWTs also cross the wire in plaintext in the current deployment. Fixing Qdrant transport TLS is a separate, pre-existing gap outside this design's scope.
- **Collection-count growth.** Every (type, tenant) pair now costs up to two physical Qdrant collections (main + chunks), each with its own HNSW index/segments/disk footprint — a real per-collection cost Postgres RLS never had, since RLS adds zero storage-shape change to a shared table. At this stage there are no live tenants and tenant-count expectations are not yet known, so no specific collection-count mitigation (e.g. capping, sharding multiple tenants into one physical collection with a residual payload filter) is designed here. This is a known, accepted operational risk to revisit once real tenant-scale expectations exist, not a gap in this design's correctness.
- **Pre-Part-A legacy rows with a null tenant value.** If a row exists in Postgres under a now-tenant-scoped schema but predates Part A's tenant stamping (so its tenant column is genuinely null), `IntelligenceStoreConsumer`'s tenant re-derivation would find null for that row — triggering the same fail-closed sentinel-collection behavior as a missing claim. In practice such a row simply doesn't get its vector written until it's re-saved through the tenant-stamping write path, consistent with Part A's hard-cutover posture generally. Moot in practice today (no live tenant data exists yet, per direct confirmation from the user).

## Verified assumptions

| Assumption | Verification | Result |
|---|---|---|
| Qdrant's JWT payload-filter claim (the mechanism B2 was originally named after) still exists and works | WebFetch/WebSearch against a Qdrant maintainer's direct statement in `github.com/orgs/qdrant/discussions/7987` and a second independent search confirming removal | **Wrong, corrected.** Deprecated in v1.15, fully removed in v1.16 — "API keys using them are rejected." This repo runs v1.18.2. Corrected the entire design premise to collection-per-tenant. |
| Qdrant's current JWT RBAC supports `exp` and per-collection `access` (`r`/`rw`/`m`) claims, self-hosted (not Cloud-only), as of the version this repo runs | WebFetch of the live `config.yaml` from `github.com/qdrant/qdrant` (master branch) directly, confirming `jwt_rbac: true` and `api_key` config keys and their documented behavior | Confirmed — both are real, current, self-hosted config keys; `jwt_rbac` explicitly documented as using the generated token "instead of API key." |
| A JWT can be presented via the same `api-key` header the client already uses (not a separate header requirement) | Cross-referenced Qdrant's config.yaml comment ("Use generated token instead of API key") with community/doc sources describing both `Authorization: Bearer` and `api-key` header acceptance | Confirmed — same header slot, JWT auto-detected. |
| `Qdrant.Client` v1.18.1 supports overriding the API key/JWT per individual call (not fixed at client construction) | Decompiled `Qdrant.Client.dll` (ilspycmd) directly | Confirmed — `RequestHeaders.Use(key, value)` is a static, `AsyncLocal`-backed, `IDisposable`-scoped override, correctly reverting on dispose and flowing to nested async calls. |
| `CollectionSchema.CollectionName`/`QdrantCollectionManager` have no baked-in assumption about the current non-tenant naming scheme | Read `CollectionSchema.cs`, `QdrantCollectionManager.cs` directly; repo-wide grep of every `CollectionName`/`_chunks` usage | Confirmed — the manager is naming-agnostic; every other production usage is confined to exactly the 3 call sites this design touches, plus test fixtures (implementation-time cutover, not a design blocker). |
| Every real (non-legacy) row has a non-null tenant value by the time `IntelligenceStoreConsumer` processes it | Read `AuthorizationFieldMasking.cs:48-49` (tenant stamped on every create where `decision.TenantColumn is not null`) plus the already-verified B1 invariant that `TenantColumn is not null` implies `TenantValue is not null` | Confirmed for all post-Part-A rows; pre-Part-A legacy rows are a documented, accepted edge case (see Known limitations) — moot today, no live tenant data exists. |
| `System.IdentityModel.Tokens.Jwt` can mint (not just validate) HS256 JWTs, and is available in this solution | Read package XML docs (`JwtSecurityTokenHandler.WriteToken`) and confirmed the package is already resolvable via `Microsoft.AspNetCore.Authentication.JwtBearer`'s transitive dependency tree | Confirmed — `WriteToken` mints tokens; package needs a direct reference added to `Iverson.Vector.csproj` (not currently referenced there), a mechanical addition. |
| Qdrant API keys in this repo (dev and Helm-generated) are long enough for secure HMAC-SHA256 signing | Read `docker-compose.yml:69` (49-char dev key) and `deploy/helm/iverson/charts/qdrant/templates/secret.yaml:13` (`randAlphaNum 32`, i.e. 32 bytes/256 bits) | Confirmed — both meet or exceed the 256-bit minimum generally recommended for HS256. |
| Qdrant returns a distinguishable "not found" status for a search against a nonexistent collection, safely treatable as empty results (not confused with other errors) | **Empirically tested directly** against the repo's own running `iverson-qdrant` container (v1.18.2) via `curl` — both a collection-info GET and a search POST against a fabricated collection name | Confirmed — HTTP 404 with an explicit `"doesn't exist"` error body; maps to gRPC `StatusCode.NotFound`. |
| `SearchSimilar`, `SearchChunks`, and `IntelligenceStoreConsumer`'s upsert/delete are the only production code paths touching entity/chunk Qdrant collections | Repo-wide grep for `CollectionName`, `_chunks"`, `SearchNamedAsync`, `UpsertNamedAsync`, `DeleteByFilterAsync`; confirmed `ReconciliationService` and `EngagementStoreConsumer` never reference Qdrant at all (StarRocks-only) | Confirmed — no other production call site reads or writes vector/chunk data. |
| Writes to Qdrant only ever happen asynchronously via `IntelligenceStoreConsumer` (no synchronous write inside the gRPC `Post`/`Update` handlers) | Read `StoreTargeting.cs:23` (vector/chunk-bearing schemas route to `StoreTarget.Intelligence`) and confirmed no `UpsertNamedAsync`/vector-write call exists in `ObjectMappingGrpcService.cs`/`ObjectPersistenceGrpcService.cs` | Confirmed — there is no "Update carve-out" analog to design for, unlike B1's `Update` pre-check; Postgres remains the sole synchronous source of truth. |
| Every registrable, vector-bearing type is guaranteed to have a non-null `TenantColumn` (no carve-out needed for an "unscoped but vector-bearing" type) | Read `SchemaRegistrationOrchestrator.cs:52,57,84` directly — `tenant_field` validated as mandatory at registration time, alongside Part A's hard-cutover denying all access to pre-cutover legacy types | Confirmed — no carve-out needed; every reachable Qdrant collection is tenant-scoped by construction. |

## Testing approach

- Integration tests (extending the existing `Iverson.Vector.Tests`/`QdrantIntegrationTests` pattern against a real Qdrant container) proving: a JWT scoped to tenant A's collection cannot read or write tenant B's collection, independent of any application-level filter — this proves the database backstop itself, not application logic.
- The fail-closed sentinel path: a tenant-scoped call reached with a null tenant value mints a JWT scoped to the unreachable sentinel collection name, and the resulting Qdrant call fails with a permission/not-found error rather than silently succeeding unfiltered.
- A read against a tenant with no collection yet returns an empty result (not an error) — proving the `NotFound`-as-empty handling.
- Lazy collection creation is idempotent: `IntelligenceStoreConsumer` processing two events for the same (type, tenant) pair creates the collection once, not twice, and doesn't error on the second attempt.
- Read-only vs read-write access level: a read-scoped (`"r"`) JWT cannot perform a write against its own tenant's collection; a write-scoped (`"rw"`) JWT can.
- `ApplyOwnership`'s filtering continues to function correctly within a single tenant's collection after `ApplyTenant`'s removal (no regression to the orthogonal ownership dimension).
