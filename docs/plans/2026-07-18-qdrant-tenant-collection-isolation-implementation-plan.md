# Qdrant Tenant Collection Isolation (Part B2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-07-18-qdrant-tenant-collection-isolation-design.md` (commit SHA: `9b0a720`)

**Goal:** Add a database-enforced tenant-isolation backstop for Qdrant by making each Qdrant collection itself the tenant boundary — one physical collection per (entity type, tenant) pair, accessed only via a short-lived, per-call JWT whose access claim names exactly one collection — plus transport TLS for the Qdrant connection.

**Architecture:** `IntelligenceStoreConsumer` lazily creates per-tenant collections on first write; a new `QdrantTenantScope` class computes tenant-qualified collection names and mints scoped JWTs; `ObjectSearchGrpcService`'s two search methods and `IntelligenceStoreConsumer`'s upsert/delete paths wrap each Qdrant call in a JWT scoped to that call's exact collection. A null tenant value routes to a permanently-nonexistent sentinel collection name, so a missing claim fails at Qdrant's own access check rather than an app-level guard.

**Tech stack:** Qdrant 1.18.2 (JWT RBAC, HS256), `Qdrant.Client` 1.18.1, `System.IdentityModel.Tokens.Jwt` (new dependency), Helm (Sprig `genSelfSignedCert`), .NET 10 `X509Certificate2`.

---

## Global Constraints

- **Additive backstop, not a replacement.** This does not change Part A's application-level enforcement. `ApplyOwnership`'s filtering is unaffected — only `ApplyTenant` (now redundant) is removed.
- **No new credential beyond the existing `Qdrant:ApiKey`.** It becomes doubly-purposed: still the raw admin key for collection management, now also the JWT HS256 signing secret.
- **Fail-closed.** A tenant-scoped call reached with a null tenant value must still fail — never fall through to an unfiltered or unscoped result. This governs both the read path (mint a JWT scoped to a permanently-nonexistent sentinel name) and the write path (skip lazy-create for that same sentinel name, so it's never actually created).
- **The shared `QdrantClient` carries no static admin credential.** `Qdrant.Client`'s per-call `RequestHeaders.Use` header adds a second `api-key` metadata entry rather than replacing the client's static one, and Qdrant honors whichever value arrives first — the static one, since `QdrantChannel.CreateCallInvoker()` adds it before the per-call value (empirically confirmed against a live `jwt_rbac`-enabled container). A static admin key on the shared client would therefore silently defeat every one of Task 3's scoped JWTs. Fix (CIR round 1 Finding 1 / forced decision, user-resolved): the client is constructed with no static key at all; every caller — admin-only collection management (`QdrantCollectionManager`) included — wraps its own calls in `RequestHeaders.Use`.
- **docker-compose stays plaintext; only the Helm deployment gets TLS.** `Qdrant:CertPath` is unset in docker-compose (unchanged plaintext client construction) and set via a mounted Secret in Helm.
- **Commit convention:** lowercase imperative summary, no Conventional-Commits prefix — matches this repo's actual git history (confirmed: `add qdrant-tenant-collection-isolation design spec`, `applied 4 fixes from ...`, and the sibling Postgres RLS plan's corrected commits after that plan's own CDR round 1 caught a Conventional-Commits-prefix defect).

## File Structure

**Create:**
- `Iverson.Server/Iverson.Vector/QdrantTenantScope.cs` — tenant-qualified collection naming + JWT minting.
- `Iverson.Server/Iverson.Vector.Tests/QdrantTenantScopeTests.cs` — unit tests for the above.

**Modify:**
- `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs` — generalize `EnsureChunkCollectionAsync` into a name-agnostic `EnsureCollectionAsync`; wire tenant-qualified naming, lazy-create-or-skip, and JWT-scoped calls into both the main-vector and chunk upsert blocks (`HandleAsync`) and both delete calls (`HandleDeleteAsync`); source the delete path's tenant value from `ev.PayloadJson` instead of a Postgres re-fetch.
- `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` — remove the two eager `vector.ApplyCollectionAsync` calls.
- `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` — `SearchSimilar`/`SearchChunks`: tenant-qualify collection name, mint read-only JWT, wrap the `SearchNamedAsync` call; remove both `ApplyTenant` calls.
- `Iverson.Server/Iverson.Vector/QdrantFilterBuilder.cs` — delete the now-unused `ApplyTenant` method.
- `Iverson.Server/Iverson.Vector.Tests/QdrantFilterBuilderTests.cs` — delete the 4 `ApplyTenant` unit tests.
- `Iverson.Server/Iverson.Vector/Iverson.Vector.csproj` — add `System.IdentityModel.Tokens.Jwt` package reference.
- `Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs` — `AddQdrant` gains an optional `certPath` parameter; branches between the existing plaintext constructor and a new TLS channel constructor; the shared `QdrantClient` is constructed with no static admin key (`apiKey: null`) in both branches; `QdrantCollectionManager`'s registration becomes a factory that also supplies the raw admin key.
- `Iverson.Server/Iverson.Vector/QdrantCollectionManager.cs` — constructor gains the raw admin API key; `EnsureCollectionAsync`/`ApplyCollectionAsync` each wrap their body in `RequestHeaders.Use("api-key", apiKey)`, since the shared client itself no longer carries a static credential.
- `Iverson.Server/Iverson.Api/Program.cs` — read a new `Qdrant:CertPath` config value and pass it to `AddQdrant`; enable `jwt_rbac`-aware startup is not needed here (server-side config only, no app-side gate).
- `Iverson.Server/docker-compose.yml` — add `QDRANT__SERVICE__JWT_RBAC: "true"` to the qdrant service.
- `Iverson.Server/deploy/helm/iverson/charts/qdrant/templates/secret.yaml` — add a self-signed cert/key pair (Sprig `genSelfSignedCert`, same `lookup`+`keep` idiom as the existing `api-key` entry).
- `Iverson.Server/deploy/helm/iverson/charts/qdrant/templates/statefulset.yaml` — add `QDRANT__SERVICE__JWT_RBAC`/`QDRANT__SERVICE__ENABLE_TLS`/`QDRANT__TLS__CERT`/`QDRANT__TLS__KEY` env vars, a cert Secret volume/mount, and switch the readiness probe to `scheme: HTTPS`.
- `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml` — mount the same qdrant-tls Secret's `cert.pem` (public cert only) and add a `Qdrant__CertPath` env var, mirroring the existing `kafka-ca` volume/mount pattern.

**Test:**
- `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs` — updated for the new tenant-qualified naming, delete-path `ev.PayloadJson` sourcing, and lazy-create-skip behavior.
- `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` — updated for the removed `ApplyTenant` calls and new JWT-wrapped call pattern.
- `Iverson.Server/Iverson.Vector.Tests/QdrantIntegrationTests.cs` (or a new sibling file) — real-container tests: cross-tenant JWT isolation, fail-closed sentinel (read and write), `NotFound`-as-empty, lazy-create idempotency, read/write access-level enforcement.

## Inherited from spec

The following were verified by `thorough-brainstorming` (across two `critical-design-review` rounds) and are NOT re-verified here:

- Qdrant's JWT payload-filter RBAC (what "Part B2" was originally named after) was removed in v1.16; this repo runs v1.18.2. Current JWT RBAC is collection-level only (`access: [{collection, "r"|"rw"}]` + `exp`), validated via HS256 using the configured `api_key` as the signing secret.
- A JWT is presented via the same `api-key` header the client already uses; Qdrant checks the claim against whatever collection name the *request* addresses (alias or physical), never silently resolving to a different name — confirmed live, including that `MigrateCollectionAsync`'s alias-swap is fully transparent to this scheme since the design always mints and addresses by the same tenant-qualified logical name.
- `Qdrant.Client.RequestHeaders.Use(key, value)` is a static, `AsyncLocal`-backed, `IDisposable`-scoped per-call header override (decompiled directly).
- A scoped JWT against a nonexistent collection returns Qdrant's real `404`/gRPC `NotFound` (empirically confirmed against a live `jwt_rbac`-enabled container, both read and write) — distinguishable from the `403 Forbidden` returned for a JWT scoped to a *different* real collection.
- The delete path's tenant value comes from `ev.PayloadJson` (the event's own pre-delete row snapshot), not a Postgres re-fetch — the row is already gone from Postgres by delete-consume time. This is a narrower trust decision than the reason owner/tenant values are normally re-derived from Postgres (that reason — CSR #7 — is about a value getting *persisted* into Qdrant's stored payload and relied on by every future read-time filter; the delete path's use of this value is transient, selecting a collection to route to, never persisted).
- Every real (non-legacy) row has a non-null tenant value by the time it's processed; every registrable, vector-bearing type is guaranteed to have a non-null `TenantColumn` (Part A's mandatory `tenant_field` validation).
- `SearchSimilar`, `SearchChunks`, and `IntelligenceStoreConsumer`'s upsert/delete are the only production code paths touching entity/chunk Qdrant collections; writes only ever happen asynchronously via `IntelligenceStoreConsumer` (no synchronous Qdrant write inside the gRPC `Post`/`Update` handlers — no `Update`-carve-out analog needed, unlike Postgres RLS).
- `Qdrant.Client`'s `ClientConfiguration.CertificateThumbprint` requires **SHA-256** (`certificate.GetCertHashString(HashAlgorithmName.SHA256)`), not .NET's default `X509Certificate2.Thumbprint` property (SHA-1) — decompiled directly, confirmed load-bearing.
- Qdrant's server TLS needs only a self-signed cert/key pair for `enable_tls` (no CA-signed chain, no `ca_cert`, unless client-cert verification or p2p TLS is also enabled — neither is here). The qdrant StatefulSet's `readOnlyRootFilesystem: true` means the cert/key must arrive via a mounted volume.
- No other service in this deployment self-provisions its own TLS cert (Postgres/CNPG and Kafka/Strimzi both delegate to their own operator); this design mirrors the existing `api-key` Secret's idempotent `lookup`+`keep` generation pattern instead.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time:

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | Function signature | `CollectionSchema` (`Iverson.Vector/CollectionSchema.cs`) is a plain positional record `(string CollectionName, IReadOnlyList<NamedVector> Vectors, IReadOnlyList<PayloadIndex> PayloadIndexes)` — supports `with { CollectionName = ... }` | Read directly |
| 2 | Function signature | `SchemaBuilder.ToCollectionSchema(SchemaDescriptor)`/`ToChunkCollectionSchema(SchemaDescriptor)` (`SchemaBuilder.cs:141-159`) are `internal static`, callable from `IntelligenceStoreConsumer.cs` since both live in `Iverson.Api` | Read directly |
| 3 | Consumer impact (Cat 6) | `EnsureChunkCollectionAsync` (`IntelligenceStoreConsumer.cs:249-262`) has exactly one caller, inside `HandleAsync`'s chunk-upsert block (line 150) | Read directly, confirmed via repo-wide grep for the method name |
| 4 | Sibling-set sweep | The "call site needing tenant-qualified naming + JWT mint + `RequestHeaders.Use` wrap" class has **6 members, not 3**: `SearchSimilar` (main, read), `SearchChunks` (chunks, read), `HandleAsync`'s main-vector-upsert block (`:97-137`, gated on `namedVectors.Count > 0`), `HandleAsync`'s chunk-upsert block (`:140-179`, gated on `schema.ChunkFields.Count > 0`, one JWT per block covering all chunk-field iterations since they all target the same chunks collection), `HandleDeleteAsync`'s main delete (`:215`), `HandleDeleteAsync`'s chunks delete-by-filter (`:219-220`, gated on `schema.ChunkFields.Count > 0`) | Read `IntelligenceStoreConsumer.cs:60-224` in full directly — the spec's "3 sites" language describes 3 *methods/consumers*, not 3 wrap points; main and chunks are different Qdrant collections requiring independently-scoped JWTs |
| 5 | Consumer impact (Cat 6) | `SchemaRegistrationOrchestratorTests.cs` has an `IVectorSchemaManager` substitute (`_vector`, line 20) but zero `Received()`/call-count assertions against it — removing the eager `ApplyCollectionAsync` calls breaks nothing there | Read directly, confirmed via grep for `ApplyCollectionAsync`/`vector\.` in that file (no hits beyond the field declaration) |
| 6 | Consumer impact (Cat 6) | `RegisterSchemaAuthorizationIntegrationTests.cs`'s one real `[Fact]` registers `SimpleType("ArticleWithAuth", "Title", "OwnerId", "TenantId")` — plain scalar properties, no vector/chunk fields, so `VectorFields.Count`/`ChunkFields.Count` are both 0 and this test never triggers eager Qdrant collection creation regardless of this plan's change | Read the test directly (`RegisterSchemaAuthorizationIntegrationTests.cs:195-201,235`) |
| 7 | Consumer impact (Cat 6) | `AddQdrant` (`ServiceCollectionExtensions.cs:8`) has exactly 2 callers: `Program.cs:169` (positional) and `ServiceCollectionExtensionsTests.cs:14` (`services.AddQdrant("localhost", 6334, apiKey: "test-api-key")`, named `apiKey:` arg) — adding a new optional `certPath` parameter after `apiKey` is non-breaking for both | Repo-wide grep for `AddQdrant(` |
| 8 | Code validity | `QdrantChannel`/`QdrantGrpcClient` live in `Qdrant.Client.Grpc`; `ClientConfiguration` lives in `Qdrant.Client` (top-level) | Decompiled `Qdrant.Client.dll` 1.18.1 directly (`ilspycmd -l c`) |
| 9 | File path | `docker-compose.yml`'s qdrant service block is at lines 64-79; its healthcheck is a raw TCP check (`/dev/tcp/localhost/6333`), not HTTP-based, so it needs no scheme change when `jwt_rbac` is enabled (TLS itself stays off for docker-compose per Global Constraints) | Read directly |
| 10 | File path / consumer impact | `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml` exists; it already has `readOnlyRootFilesystem: true` (line 66) and an established cert-mount pattern to mirror exactly (`kafka-ca` Secret volume + read-only mount + path-based env var, lines 116-117,164-167,171-173); existing `Qdrant__ApiKey`/`Qdrant__Host`/`Qdrant__Port` env vars are at lines 97-102, the natural place to add `Qdrant__CertPath` | Read directly |
| 11 | Code validity | `QdrantVectorService` (`QdrantVectorService.cs:7`) has no `Task.Run`/thread-hop in its call chain — plain async methods calling `QdrantClient` directly — so `RequestHeaders.Use`'s `AsyncLocal` state set at a gRPC-service/consumer call site correctly flows through the `IVectorQueryService`/`IVectorWriteService` interface call into the singleton `QdrantClient`'s actual gRPC call, per standard .NET `AsyncLocal` semantics | Read directly, confirmed no `Task.Run`/`ConfigureAwait` calls that would suppress flow |
| 12 | Test command | `dotnet test Iverson.Server/Iverson.Vector.Tests` and `dotnet test Iverson.Server/Iverson.Api.Tests --filter "Category!=Integration"` are valid, current invocations, matching the convention already used in the sibling `2026-07-18-postgres-row-level-security-implementation-plan.md` | Ran `--list-tests` for both; succeeded |
| 13 | Task ordering | Task 1 (collection lifecycle) and Task 2 (`QdrantTenantScope`) have no cross-dependency on each other; Task 3 (call-site wiring) depends on both; Task 4 (TLS) is independent of Tasks 1-3 (pure transport-level config, no tenant-logic dependency) | Derived from reading all 4 tasks' file lists — no import/call from Task 1 or 2's new code into the other, confirmed by design |
| 14 | Consumer impact (Cat 6) | `QdrantCollectionManager` (`QdrantCollectionManager.cs`) has exactly 2 public methods (`EnsureCollectionAsync`, `ApplyCollectionAsync`); its private helpers (`MigrateCollectionAsync`, `CopyPointsAsync`, `CreateNamedVectorCollectionAsync`, `ApplyPayloadIndexesAsync`, `ResolvePhysicalCollectionAsync`) are only ever called from within `ApplyCollectionAsync`'s own async call chain, so wrapping only the 2 public entry points in `RequestHeaders.Use` is sufficient — the `AsyncLocal` value flows through to every private helper the same way Assumption #11 already established for the tenant-scoped paths | Read `QdrantCollectionManager.cs` in full directly (CIR round 1 follow-up) |

## Tasks

### Task 1: Collection lifecycle — jwt_rbac bootstrap, lazy per-tenant creation

**Files:**
- Modify: `Iverson.Server/docker-compose.yml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/qdrant/templates/statefulset.yaml` (JWT_RBAC env var only — TLS-specific env vars/volumes are Task 4)
- Modify: `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs` (idempotency of the generalized ensure-collection helper only — tenant-qualified naming and JWT wrapping are Task 3)

**Interfaces:**
- Produces: `IntelligenceStoreConsumer.EnsureCollectionAsync(string name, CollectionSchema schema)` — a name-agnostic generalization of today's chunks-only `EnsureChunkCollectionAsync`, consumed by Task 3's call-site wiring.

- [ ] **Step 1: Enable `jwt_rbac` on the Qdrant server.** In `docker-compose.yml`'s qdrant service (lines 64-79), add alongside the existing `QDRANT__SERVICE__API_KEY`:
```yaml
QDRANT__SERVICE__JWT_RBAC: "true"
```
In the Helm chart's `statefulset.yaml`, add the equivalent `QDRANT__SERVICE__JWT_RBAC` env var next to where `QDRANT__SERVICE__API_KEY` is already wired from the chart's Secret.

- [ ] **Step 2: Generalize `EnsureChunkCollectionAsync` into `EnsureCollectionAsync`.** In `IntelligenceStoreConsumer.cs`, rename the per-process idempotency cache field (`_ensuredChunkCollections` → `_ensuredCollections`, still a `HashSet<string>`) and generalize the method to take an already-built `CollectionSchema` instead of building one internally from `schema.ChunkFields`:
```csharp
private async Task EnsureCollectionAsync(CollectionSchema collectionSchema)
{
    if (_ensuredCollections.Contains(collectionSchema.CollectionName)) return;
    await vectorSchema.ApplyCollectionAsync(collectionSchema);
    _ensuredCollections.Add(collectionSchema.CollectionName);
}
```
Do not wire this into any call site yet — Task 3 does that. This step only generalizes the primitive.

- [ ] **Step 3: Remove eager Qdrant collection creation from `SchemaRegistrationOrchestrator`.** Delete the two `vector.ApplyCollectionAsync` calls at `SchemaRegistrationOrchestrator.cs:71-75` (both gated blocks: `if (descriptor.VectorFields.Count > 0) ...` and `if (descriptor.ChunkFields.Count > 0) ...`). Registration continues to validate schema shape (mandatory `tenant_field`, vector/chunk field validation) exactly as today — only the Qdrant collection creation itself is removed, since there is no longer a single "the" collection for a type to create at registration time.

- [ ] **Step 4: Tests.** Using the existing `Iverson.Vector.Tests`/`Iverson.Api.Tests` conventions: `EnsureCollectionAsync` is idempotent (calling it twice with the same `CollectionSchema` doesn't call `ApplyCollectionAsync` twice); calling it with two different-named schemas creates both. Confirm `SchemaRegistrationOrchestratorTests.cs` still passes unchanged (no assertions on eager Qdrant creation existed to update).

- [ ] **Step 5: Run tests and commit.**
```bash
dotnet test Iverson.Server/Iverson.Api.Tests --filter "Category!=Integration"
git add Iverson.Server/docker-compose.yml Iverson.Server/deploy/helm/iverson/charts/qdrant/templates/statefulset.yaml Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs
git commit -m "enable qdrant jwt_rbac and generalize lazy collection creation to be tenant-name-agnostic"
```

### Task 2: `QdrantTenantScope` — collection naming and JWT minting

**Files:**
- Create: `Iverson.Server/Iverson.Vector/QdrantTenantScope.cs`
- Modify: `Iverson.Server/Iverson.Vector/Iverson.Vector.csproj`
- Modify: `Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs`
- Modify: `Iverson.Server/Iverson.Vector/QdrantCollectionManager.cs`
- Test: `Iverson.Server/Iverson.Vector.Tests/QdrantTenantScopeTests.cs`

**Interfaces:**
- Produces: `QdrantTenantScope` — a DI-registered singleton (constructed with the Qdrant API key baked in, since neither `ObjectSearchGrpcService` nor `IntelligenceStoreConsumer` has any existing way to access that config value otherwise — confirmed via grep, `ObjectSearchGrpcService.cs` has no `IConfiguration`/API-key-related dependency today). Instance methods `ResolveCollectionName(string baseName, string? tenantId, bool isChunks)` and `MintScopedApiKey(string collectionName, bool readOnly)` — matching the spec's exact signature (no `apiKey` parameter — it's captured at construction) — consumed by Task 3's call-site wiring via constructor injection.
- Also removes the shared `QdrantClient`'s static admin credential and moves it into an explicit per-call wrap inside `QdrantCollectionManager` (resolves CIR round 1 Finding 1 and the forced decision it raised).

- [ ] **Step 1: Add the `System.IdentityModel.Tokens.Jwt` package.** In `Iverson.Vector.csproj`, add:
```xml
<PackageReference Include="System.IdentityModel.Tokens.Jwt" Version="8.15.0" />
```
(Already present transitively via `Microsoft.AspNetCore.Authentication.JwtBearer` elsewhere in the solution; this adds the direct reference `Iverson.Vector` itself needs.)

- [ ] **Step 2: Collection name resolution.** In the new `QdrantTenantScope.cs`:
```csharp
namespace Iverson.Vector;

public sealed class QdrantTenantScope(string apiKey)
{
    private const string NoTenantSentinel = "__no-tenant-claim__";

    public string ResolveCollectionName(string baseName, string? tenantId, bool isChunks)
    {
        var suffix = isChunks ? "_chunks" : "";
        var qualifier = tenantId ?? NoTenantSentinel;
        return $"{baseName}{suffix}_{qualifier}";
    }
```
This matches the spec's naming exactly: main `{CollectionName}_{tenantId}`, chunks `{CollectionName}_chunks_{tenantId}`, and the fail-closed sentinel `{CollectionName}__no-tenant-claim__` / `{CollectionName}_chunks__no-tenant-claim__` when `tenantId` is null.

- [ ] **Step 3: JWT minting.** In the same class:
```csharp
    public string MintScopedApiKey(string collectionName, bool readOnly)
    {
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(apiKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var payload = new JwtPayload
        {
            ["exp"] = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds(),
            ["access"] = new[]
            {
                new Dictionary<string, string> { ["collection"] = collectionName, ["access"] = readOnly ? "r" : "rw" }
            }
        };
        var header = new JwtHeader(credentials);
        var token = new JwtSecurityToken(header, payload);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
```

- [ ] **Step 4: Register as a DI singleton.** In `ServiceCollectionExtensions.cs`'s `AddQdrant`, add:
```csharp
services.AddSingleton(new QdrantTenantScope(apiKey!));
```
(`apiKey` is already `AddQdrant`'s existing parameter — confirm it's non-null in practice for any environment where JWT minting is actually exercised; the null-forgiving operator matches this file's existing style for this parameter.)

- [ ] **Step 5: Remove the shared client's static admin credential; wrap admin-only Qdrant calls explicitly instead.** (Resolves CIR round 1 Finding 1 and the forced decision it raised.) `Qdrant.Client`'s per-call `RequestHeaders.Use` header does not replace a client's static `api-key` — it adds a second `api-key` metadata entry alongside it, and Qdrant honors whichever one arrives first: the static one, since `QdrantChannel.CreateCallInvoker()` adds it before the per-call value. With the client's static key still set, every one of Task 3's scoped-JWT wraps would be silently inert (the admin key would win on every call). Fix: the shared `QdrantClient` must never carry a static admin key; every caller — admin-only collection management included — wraps its own calls in `RequestHeaders.Use`.

  In `ServiceCollectionExtensions.cs`, change the plaintext client construction from `apiKey: apiKey` to `apiKey: null`:
```csharp
services.AddSingleton(_ => new QdrantClient(host, port, https: false, apiKey: null));
```
  Change `QdrantCollectionManager`'s registration from `services.AddSingleton<QdrantCollectionManager>();` to a factory that also supplies the raw admin key:
```csharp
services.AddSingleton(sp => new QdrantCollectionManager(
    sp.GetRequiredService<QdrantClient>(), apiKey!, sp.GetRequiredService<ILogger<QdrantCollectionManager>>()));
```
  In `QdrantCollectionManager.cs`, add `apiKey` to the primary constructor and wrap the body of each of its two public methods in the admin-key header scope — its private helpers (`MigrateCollectionAsync`, `CopyPointsAsync`, etc., see Verified plan-level assumption #14) are called from within `ApplyCollectionAsync`'s own async call chain and inherit the same `RequestHeaders.Current` `AsyncLocal` value, so only the two public entry points need the explicit wrap:
```csharp
public class QdrantCollectionManager(
    QdrantClient client,
    string apiKey,
    ILogger<QdrantCollectionManager> logger) : IVectorSchemaManager
{
    public async Task EnsureCollectionAsync(string collectionName, ulong vectorSize)
    {
        using var _ = RequestHeaders.Use("api-key", apiKey);
        // ...existing body unchanged...
    }

    public async Task ApplyCollectionAsync(CollectionSchema schema)
    {
        using var _ = RequestHeaders.Use("api-key", apiKey);
        // ...existing body unchanged...
    }
}
```
  `Qdrant.Client` (for `RequestHeaders`) is already imported in this file — no new `using` needed. Real verification that this wrap actually supplies a working credential is covered by Task 3 Step 9's live-container integration tests, not invented here — `QdrantClient` itself isn't mockable.

- [ ] **Step 6: Tests.** `ResolveCollectionName`: real tenant (main and chunks variants), null tenant (sentinel, main and chunks variants) — assert the exact expected strings match the spec's naming scheme. `MintScopedApiKey`: decode the returned JWT (e.g. via `JwtSecurityTokenHandler().ReadJwtToken(...)`) and assert its `exp` claim is ~30s in the future, its `access` claim contains exactly one entry with the given collection name and `"r"`/`"rw"` per the `readOnly` argument, and that validating the token with `TokenValidationParameters` against the same signing key succeeds (proving the signature is genuinely verifiable, not just shaped correctly).

- [ ] **Step 7: Run tests and commit.**
```bash
dotnet test Iverson.Server/Iverson.Vector.Tests
git add Iverson.Server/Iverson.Vector/QdrantTenantScope.cs Iverson.Server/Iverson.Vector/Iverson.Vector.csproj Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs Iverson.Server/Iverson.Vector/QdrantCollectionManager.cs Iverson.Server/Iverson.Vector.Tests/QdrantTenantScopeTests.cs
git commit -m "add QdrantTenantScope for tenant-qualified collection naming and scoped JWT minting"
```

### Task 3: Call-site wiring and `ApplyTenant` removal

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`
- Modify: `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs`
- Modify: `Iverson.Server/Iverson.Vector/QdrantFilterBuilder.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`, `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs`, `Iverson.Server/Iverson.Vector.Tests/QdrantFilterBuilderTests.cs`, `Iverson.Server/Iverson.Vector.Tests/QdrantIntegrationTests.cs` (or a new sibling file)

**Interfaces:**
- Consumes: `QdrantTenantScope` (Task 2) — constructor-injected into both `ObjectSearchGrpcService` and `IntelligenceStoreConsumer` (it's a DI singleton, so this is an added constructor parameter on each, threaded through their existing DI registrations); `IntelligenceStoreConsumer.EnsureCollectionAsync` (Task 1).

There are **6 distinct wrap points**, not 3 — main and chunks are different Qdrant collections requiring independently-scoped JWTs (see Verified plan-level assumption #4):

- [ ] **Step 1: `ObjectSearchGrpcService.SearchSimilar`** (`:173`'s `SearchNamedAsync` call). Add a `QdrantTenantScope tenantScope` constructor parameter. Resolve `collectionName = tenantScope.ResolveCollectionName(schema.CollectionName!, decision.TenantValue, isChunks: false)`. Wrap the call:
```csharp
using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(collectionName, readOnly: true)))
{
    var results = await vector.SearchNamedAsync(collectionName, vectorName, queryVector, topK, filter);
}
```
Remove the `ApplyTenant` call at line 155.

- [ ] **Step 2: `ObjectSearchGrpcService.SearchChunks`** (`:239`'s call). Same pattern as Step 1, with `isChunks: true` and the tenant-qualified chunks name. Remove the `ApplyTenant` call at line 222.

- [ ] **Step 3: `IntelligenceStoreConsumer.HandleAsync`'s main-vector-upsert block** (`:97-137`, inside `if (schema.VectorFields.Count > 0)` and `if (namedVectors.Count > 0)` — but `authoritativeTenantValue` itself is NOT scoped to this block, see below). Add a `QdrantTenantScope tenantScope` constructor parameter to `IntelligenceStoreConsumer` (used by this step and Steps 4-6 below). `FetchAuthoritativeOwnerValueAsync` (`:233-247`) is already generic over which field to extract — its `ownerField` parameter is just a string, used as `ExtractString(doc.RootElement, ownerField)` internally. Reuse it directly for tenant sourcing (no new helper). **`authoritativeTenantValue` must be computed once, unconditionally, before both this block and Step 4's chunk block — mirroring exactly where `authoritativeOwnerValue` is computed today (`:94-97`, before either the vector or chunk block) — not gated by `if (schema.VectorFields.Count > 0)`, since a chunks-only schema (`VectorFields.Count == 0`, `ChunkFields.Count > 0`) needs it too:** `var authoritativeTenantValue = schema.TenantColumn is not null ? await FetchAuthoritativeOwnerValueAsync(schema, schema.TenantColumn, ev.Key, ct) : null;`. When `authoritativeTenantValue` is non-null: resolve the tenant-qualified main collection name, call `EnsureCollectionAsync` with `SchemaBuilder.ToCollectionSchema(schema) with { CollectionName = collectionName }`, mint a read-write JWT, wrap `UpsertNamedAsync`. When null: skip `EnsureCollectionAsync` (per the fail-closed carve-out) but still attempt the upsert against the resolved sentinel name under a JWT scoped to that sentinel — the call fails at Qdrant's access check precisely because `EnsureCollectionAsync` was never called for that name.

- [ ] **Step 4: `IntelligenceStoreConsumer.HandleAsync`'s chunk-upsert block** (`:140-179`, inside `if (schema.ChunkFields.Count > 0)`). Reuse `authoritativeTenantValue` from Step 3 (same event, same row — no second fetch). Resolve the tenant-qualified chunks name once for the whole block (not per chunk field/chunk), replacing today's `EnsureChunkCollectionAsync(chunksCollection, schema, ct)` call with `EnsureCollectionAsync(SchemaBuilder.ToChunkCollectionSchema(schema) with { CollectionName = chunksCollectionName })` under the same null-skip rule as Step 3, mint one read-write JWT for the whole block, wrap every `UpsertNamedAsync` call inside the `foreach` loops in that single `using` scope (all chunk points for this event share the same collection name, so one minted JWT correctly covers the whole batch).

- [ ] **Step 5: `IntelligenceStoreConsumer.HandleDeleteAsync`'s main delete** (`:215`). Source the tenant value from `ev.PayloadJson` (the pre-delete `rowJson` snapshot `ObjectMappingGrpcService.Delete` published at `ObjectMappingGrpcService.cs:239`) via the same `ExtractString`-style helper this file already uses for other scalar-field extraction — `ExtractString(payload, schema.TenantColumn!)` — not a Postgres re-fetch (the row is already gone by delete-consume time). Resolve the tenant-qualified main name, mint a read-write JWT, wrap `DeleteAsync`. Null tenant → sentinel name, no `EnsureCollectionAsync` call (there is none on the delete path today to skip — the delete simply targets a name that was never created, so it fails at Qdrant's access check the same way).

- [ ] **Step 6: `IntelligenceStoreConsumer.HandleDeleteAsync`'s chunks delete-by-filter** (`:219-220`, inside `if (schema.ChunkFields.Count > 0)`). Same tenant sourcing as Step 5. Resolve the tenant-qualified chunks name, mint a read-write JWT, wrap `DeleteByFilterAsync`.

- [ ] **Step 7: Delete `QdrantFilterBuilder.ApplyTenant`** (`QdrantFilterBuilder.cs:62-68`) and its 4 unit tests in `QdrantFilterBuilderTests.cs` (`ApplyTenant_NotRequired_NullFilter_ReturnsNull`, `ApplyTenant_NotRequired_ExistingFilter_ReturnsSameFilterUnchanged`, `ApplyTenant_Required_NullFilter_CreatesFilterWithMatchKeywordCondition`, `ApplyTenant_Required_ExistingFilter_AppendsConditionPreservingExisting`). Confirmed via repo-wide grep: no other callers exist anywhere. `ApplyOwnership` and its tests are untouched.

- [ ] **Step 8: Update existing NSubstitute mocks in `ObjectSearchGrpcServiceTests.cs` and `IntelligenceStoreConsumerTests.cs`.** Existing `SearchNamedAsync`/`UpsertNamedAsync`/`DeleteAsync`/`DeleteByFilterAsync` setups/verifications that assert on the old (non-tenant-qualified) collection name need updating to the new tenant-qualified name, or relaxed to `Arg.Any<string>()` where the test doesn't specifically assert on naming. `ObjectSearchGrpcServiceTests.cs` needs no fixture-data changes beyond this — `ActingUserFixtures.Principal` already sets a `tenant_id` claim ("test-tenant") for every test principal.

  `IntelligenceStoreConsumerTests.cs` needs more than assertion updates (CIR round 1 Finding 2): its `_entities.FetchByKeyAsync` stub (constructor default at line 61, plus per-test overrides at lines 205, 254) and every delete-path `EntityEvent.PayloadJson` fixture (currently `"{}"`, at lines 341, 362, 746) carry no `TenantId` value. Since Task 3's tenant re-derivation resolves `null` when the fixture data has no `TenantId` key, every one of these tests would silently route to the fail-closed sentinel collection name regardless of what the assertion strings are updated to. Add a `"TenantId":"<value>"` key to each of those fixtures (matching whatever literal tenant value the updated assertions expect). The stub override at line 291 (`HandleCreated_WithOwnerFieldAndNoAuthoritativeRow_OmitsOwnerKeyFromChunkPayload`) returns `(string?)null`, not JSON, so it can't take a `TenantId` key the same way (CIR round 2 Finding 2) — since the same stub now also serves the tenant fetch, "no authoritative row found" correctly implies "no tenant value either," so instead change that test's hardcoded `_vectorWrite.UpsertNamedAsync("articles_chunks", ...)` mock to the sentinel collection name the fail-closed path will actually produce. Additionally, two custom `SchemaDescriptor` fixtures in this file never set `TenantColumn` at all (CIR round 2 Finding 3): the "Doc"/"docs" schema (`:233-248`, used by `HandleCreated_WithForgedOwnerValueInPayload_PointPayloadUsesAuthoritativeValueNotPayloadValue`) and `customSchema` (`:534-545`, used by `ChunkSplitting_ProducesMultipleChunks_ForLongText`) — add `TenantColumn = "TenantId"` to both (matching the convention every `SchemaFixtures` schema already uses), supply a `TenantId` value via each test's entities stub, and update their hardcoded collection-name assertions (`:264-267`'s `"docs"`, `:561-565`'s `"docs_chunks"`) to the tenant-qualified names. Additionally, rewrite `HandleCreated_WithNoOwnerFieldConfigured_NeverCallsFetchByKeyAsync` (line 313): its premise — "no owner field configured ⇒ zero `FetchByKeyAsync` calls" — no longer holds, since tenant re-derivation now calls `FetchByKeyAsync` unconditionally (independent of whether `OwnerField` is configured). Change its assertion from `_entities.DidNotReceive().FetchByKeyAsync(...)` to `_entities.Received(1).FetchByKeyAsync(...)`, reflecting that only the *owner*-value fetch is skipped when `OwnerField` is null, not the *tenant*-value fetch.

- [ ] **Step 9: New integration tests** (real Qdrant container, per the spec's testing approach): a JWT scoped to tenant A's collection cannot read or write tenant B's collection; the fail-closed sentinel path on both read and write (explicitly: `IntelligenceStoreConsumer` skips lazy-create when tenant is null, and the subsequent write against the still-nonexistent sentinel collection fails); the delete path correctly sources tenant from `ev.PayloadJson` and removes the point from the real tenant's collection, not the sentinel; a read against a tenant with no collection yet returns empty, not an error; lazy collection creation is idempotent across two events for the same (type, tenant) pair; a read-scoped JWT cannot write, a write-scoped JWT can; `ApplyOwnership` continues to filter correctly within a tenant's collection; `QdrantCollectionManager`'s admin-only operations (`EnsureCollectionAsync`/`ApplyCollectionAsync`) still succeed against a `jwt_rbac`-enabled server purely from their own `RequestHeaders.Use(adminKey)` wrap, confirming the shared client's static credential removal (Task 2 Step 5) didn't silently break collection management.

- [ ] **Step 10: Run tests and commit.**
```bash
dotnet test Iverson.Server/Iverson.Api.Tests --filter "Category!=Integration"
dotnet test Iverson.Server/Iverson.Vector.Tests
git add Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs Iverson.Server/Iverson.Vector/QdrantFilterBuilder.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs Iverson.Server/Iverson.Vector.Tests/QdrantFilterBuilderTests.cs Iverson.Server/Iverson.Vector.Tests/QdrantIntegrationTests.cs
git commit -m "wire tenant-scoped Qdrant JWTs into search and intelligence-store call sites, remove redundant ApplyTenant"
```

### Task 4: Qdrant transport TLS

**Files:**
- Modify: `Iverson.Server/deploy/helm/iverson/charts/qdrant/templates/secret.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/qdrant/templates/statefulset.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml`
- Modify: `Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`

**Interfaces:**
- Consumes: nothing from Tasks 1-3 (transport-level only, independent of tenant logic).

- [ ] **Step 1: Self-signed cert Secret.** In `secret.yaml`, add a `cert.pem`/`key.pem` pair using the same `lookup`-then-`keep` idiom already used for `api-key`:
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
(This may be a new Secret resource — `{{ .Release.Name }}-qdrant-tls` — in the same file as the existing `api-key` Secret, or a new template file in the same directory; match whichever is more consistent with the existing file's structure once you read it.)

- [ ] **Step 2: Qdrant server TLS config.** In `statefulset.yaml`, add:
```yaml
QDRANT__SERVICE__ENABLE_TLS: "true"
QDRANT__TLS__CERT: "/qdrant-tls/cert.pem"
QDRANT__TLS__KEY: "/qdrant-tls/key.pem"
```
alongside the existing `QDRANT__SERVICE__API_KEY`/`QDRANT__SERVICE__JWT_RBAC` (Task 1) env vars. Add a new volume sourced from the `{{ .Release.Name }}-qdrant-tls` Secret, mounted read-only at `/qdrant-tls`, alongside the existing `qdrant-storage`/`snapshots`/`tmp` volumes. Change the readiness probe's `httpGet` to add `scheme: HTTPS`.

- [ ] **Step 3: Mount the cert in the API deployment.** In `deployment.yaml`, mirror the existing `kafka-ca` volume/mount pattern (lines 116-117, 164-167, 171-173) for the qdrant cert — mount only `cert.pem` (public certificate; the API never needs the private key) from the same `{{ .Release.Name }}-qdrant-tls` Secret, read-only, at e.g. `/etc/qdrant-tls`. Add a new env var:
```yaml
- name: Qdrant__CertPath
  value: "/etc/qdrant-tls/cert.pem"
```
next to the existing `Qdrant__ApiKey`/`Qdrant__Host`/`Qdrant__Port` env vars (lines 97-102).

- [ ] **Step 4: `.NET` client TLS support.** In `ServiceCollectionExtensions.cs`, add `using Qdrant.Client.Grpc;`, `using System.Security.Cryptography;`, `using System.Security.Cryptography.X509Certificates;`. Change `AddQdrant`'s signature to accept an optional `string? certPath = null`, and branch the client construction:
```csharp
services.AddSingleton(_ =>
{
    if (certPath is not null)
    {
        using var cert = new X509Certificate2(certPath);
        var thumbprint = cert.GetCertHashString(HashAlgorithmName.SHA256);
        var channel = QdrantChannel.ForAddress($"https://{host}:{port}", new ClientConfiguration
        {
            CertificateThumbprint = thumbprint
        });
        return new QdrantClient(new QdrantGrpcClient(channel));
    }
    return new QdrantClient(host, port, https: false, apiKey: null);
});
```
(No static `ApiKey` in either branch — consistent with Task 2 Step 5: the shared client never carries a static admin credential; `RequestHeaders.Use` supplies the credential per-call for every caller, admin or tenant-scoped.)

- [ ] **Step 5: Wire the new config value.** In `Program.cs`, read `cfg["Qdrant:CertPath"]` and pass it as `AddQdrant`'s new argument (`null` when unset, matching docker-compose's unset `Qdrant__CertPath`).

- [ ] **Step 6: Tests.** Unit test for the branch in `ServiceCollectionExtensionsTests.cs`: `certPath: null` still resolves to a plaintext-constructed client (existing behavior, existing test at line 14 continues to pass unchanged); a non-null `certPath` pointing at a real test certificate file resolves to a client built via the TLS channel path (assert via the resolved client's type/configuration, not a live connection — a live TLS connection is Step 7's job). A kind-based Helm smoke test (new — no existing smoke-test script in this repo to extend) confirming the API pod successfully connects to Qdrant over TLS end-to-end using the computed SHA-256 thumbprint after a real `helm upgrade --install` against a kind cluster.

- [ ] **Step 7: Run tests and commit.**
```bash
dotnet test Iverson.Server/Iverson.Vector.Tests
dotnet test Iverson.Server/Iverson.Api.Tests --filter "Category!=Integration"
git add Iverson.Server/deploy/helm/iverson/charts/qdrant/templates/secret.yaml Iverson.Server/deploy/helm/iverson/charts/qdrant/templates/statefulset.yaml Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Vector.Tests/ServiceCollectionExtensionsTests.cs
git commit -m "add Qdrant transport TLS for the Helm deployment via a self-signed cert and thumbprint-pinned client"
```
