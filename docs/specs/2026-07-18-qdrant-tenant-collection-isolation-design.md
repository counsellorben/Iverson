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

Explicitly out of scope for this spec: B3 (StarRocks); any change to Part A's application-level enforcement (unchanged); backfilling tenant values onto pre-Part-A legacy rows. Qdrant transport TLS — originally out of scope as a pre-existing gap — is now **in scope** (see "Qdrant transport TLS" below), added because this design's per-tenant JWTs would otherwise inherit that gap unchanged.

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

### Qdrant transport TLS (Helm/production only; docker-compose stays plaintext)

Qdrant's own documentation calls out sending `api_key`/JWT bearer values over an unencrypted channel as insecure — a pre-existing gap in this deployment (`QdrantClient` is constructed with `https: false` today, `ServiceCollectionExtensions.cs:14`) that this design's per-tenant JWTs would otherwise inherit unchanged. This section brings TLS into scope for the Helm (production/staging) deployment; `docker-compose.yml` (local dev) is explicitly left on plaintext, consistent with its existing hardcoded dev-only API key.

**Why this needs its own design, not a one-line flag flip:** neither Postgres (CloudNativePG) nor Kafka (Strimzi) in this deployment provision their own certs — both delegate TLS entirely to their own Kubernetes operator, which auto-generates and rotates certs internally. Qdrant is a raw `StatefulSet` with no operator, so there is no existing in-repo pattern to extend for cert lifecycle; this design self-provisions a cert via Helm, mirroring the existing idempotent generate-and-keep pattern already used for the `api-key` Secret (`secret.yaml`'s `lookup` + `helm.sh/resource-policy: keep`).

**Certificate provisioning.** Extend the qdrant chart's Secret template with a self-signed cert/key pair generated via Helm's built-in `genSelfSignedCert` (Sprig), using the same `lookup`-then-`keep` idiom already used for `api-key` so re-running `helm upgrade` doesn't rotate the cert on every deploy:

```yaml
{{- $existingCert := lookup "v1" "Secret" .Release.Namespace (printf "%s-qdrant-tls" .Release.Name) }}
{{- if $existingCert }}
  cert.pem: {{ index $existingCert.data "cert.pem" }}
  key.pem: {{ index $existingCert.data "key.pem" }}
{{- else }}
  {{- $cert := genSelfSignedCert (printf "%s-qdrant" .Release.Name) nil (list (printf "%s-qdrant" .Release.Name) (printf "%s-qdrant.%s.svc.cluster.local" .Release.Name .Release.Namespace)) 3650 }}
  cert.pem: {{ $cert.Cert | b64enc }}
  key.pem: {{ $cert.Key | b64enc }}
{{- end }}
```

(SAN list covers both the short and fully-qualified in-cluster service DNS names the StatefulSet's headless service exposes; a 10-year validity avoids a rotation story this design doesn't otherwise need to solve, matching the "self-provisioned, not operator-managed" reality above.)

**Qdrant server config** (confirmed against the shipped `qdrant/qdrant:v1.18.2` image's own `config.yaml`): `QDRANT__SERVICE__ENABLE_TLS=true`, `QDRANT__TLS__CERT=/qdrant-tls/cert.pem`, `QDRANT__TLS__KEY=/qdrant-tls/key.pem`, added to `statefulset.yaml` alongside the existing `QDRANT__SERVICE__API_KEY`. `verify_https_client_certificate` and `cluster.p2p.enable_tls` stay off — no client-cert (mTLS) verification and no inter-node TLS are needed for this design's threat model (a per-tenant JWT already restricts what a caller can do; node-to-node raft traffic isn't part of this spec's scope). The cert/key land in the pod via a Secret volume mount (the StatefulSet's `readOnlyRootFilesystem: true` means they cannot be written at runtime) — a new volume/mount pair alongside the existing `qdrant-storage`/`snapshots`/`tmp` volumes. The readiness probe (`statefulset.yaml`, currently plain `httpGet`) gains `scheme: HTTPS`.

**`.NET` client changes.** `https: true` on the existing simple `QdrantClient(host, port, https, apiKey)` constructor is **not sufficient** on its own — that path has no certificate-validation override and fails TLS trust against a non-CA-signed cert. `ServiceCollectionExtensions.cs`'s client construction changes to the channel-based constructor instead:

```csharp
var channel = QdrantChannel.ForAddress($"https://{host}:{port}", new ClientConfiguration
{
    ApiKey = apiKey,
    CertificateThumbprint = qdrantCertThumbprint
});
var client = new QdrantClient(new QdrantGrpcClient(channel));
```

`qdrantCertThumbprint` is computed by the API at startup, not hand-computed in a Helm template (Helm/Sprig has no built-in to correctly compute an X.509 DER-based thumbprint from PEM text). The API pod mounts the *same* `{release}-qdrant-tls` Secret's `cert.pem` key (public certificate only — no private key needed or granted to the API), loads it via `X509Certificate2`, and computes the thumbprint at startup: **`cert.GetCertHashString(HashAlgorithmName.SHA256)`** — confirmed by decompiling `Qdrant.Client` 1.18.1's `CertificateValidation.Thumbprint(...)` directly: it hashes with **SHA-256**, not .NET's default `X509Certificate2.Thumbprint` property (which is SHA-1) — using the wrong hash algorithm here would be a real, silent TLS-trust failure, not a cosmetic mismatch.

### Known limitations (surfaced, not fixed by this design)

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
| No other service in this deployment self-provisions its own TLS cert (a pattern to mirror, or its absence, for Qdrant TLS) | Read `charts/postgres/templates/cluster.yaml` (CNPG rejects an explicit `ssl` param — operator-managed, unconditional), `charts/kafka/templates/kafka.yaml` (Strimzi internal listener `tls: true`, operator-managed cluster CA), and `deploy/terraform/modules/operators/main.tf` (no cert-manager installed) directly | Confirmed — no existing in-repo precedent for self-provisioned certs beyond the `api-key` Secret's `lookup`+`keep` idiom, which this design's cert Secret mirrors instead. |
| Qdrant's server TLS requires only a self-signed cert/key pair (no CA-signed chain) for `enable_tls`, and the exact config keys/paths | Read the shipped `qdrant/qdrant:v1.18.2` image's own `config.yaml` directly (`service.enable_tls`, `tls.cert`/`tls.key`/`tls.ca_cert`, `cluster.p2p.enable_tls`) | Confirmed — `ca_cert` is only required if `verify_https_client_certificate` or `cluster.p2p.enable_tls` is also turned on; neither is needed for this design. |
| Helm's `genSelfSignedCert` Sprig function exists and produces a usable `Cert`/`Key` PEM pair from a CN/SAN/validity-days input | WebFetch of Helm's own chart template function-list docs | Confirmed — `genSelfSignedCert CN (list IPs) (list DNSNames) validityDays` returns `{Cert, Key}` as PEM strings; no CA-signing step required for a self-signed leaf cert. |
| `Qdrant.Client`'s `ClientConfiguration.CertificateThumbprint` expects a specific hash algorithm/format that the .NET app must match when computing it | Decompiled `Qdrant.Client.dll` 1.18.1's `CertificateValidation.Thumbprint`/`ValidateThumbprint` methods directly (not just the XML doc summary, which doesn't state the algorithm) | **Load-bearing correction found during verification**: hashes with `certificate.GetCertHashString(HashAlgorithmName.SHA256)` — SHA-256, not .NET's default `X509Certificate2.Thumbprint` property (SHA-1). Colon/hyphen separators are normalized away and comparison is case-insensitive, but the algorithm itself is not negotiable. Confirmed self-signed certs are supported: `Thumbprint(...)`'s returned validator falls back to validating the leaf certificate directly when no trusted chain exists, not only chain elements. |
| The qdrant StatefulSet's `readOnlyRootFilesystem` setting means a cert/key can't be written at runtime and must arrive via a mounted volume | Read `statefulset.yaml:48` (`containerSecurityContext.readOnlyRootFilesystem: true`) directly | Confirmed — the design mounts the cert Secret as a volume, consistent with how `qdrant-storage`/`snapshots`/`tmp` are already mounted rather than written ad hoc. |
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
- A kind-based Helm/kind smoke test confirming the API pod successfully connects to Qdrant over TLS using the computed SHA-256 thumbprint (proving the end-to-end cert-generation → mount → thumbprint-compute → trust-validation chain works, not just each piece in isolation).
