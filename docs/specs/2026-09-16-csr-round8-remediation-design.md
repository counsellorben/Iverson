# CSR Round 8 Remediation — Design

Source: `docs/criticalreviews/2026-09-15-iverson-critical-security-review-8.md` (all 8 findings). Each finding is independently small and touches a mostly-disjoint set of files, so this design covers all 8 as one spec rather than eight separate cycles — matching this session's established precedent for prior CSR remediation designs.

---

## 1. Health port serves the whole HTTP surface (Finding #1)

Files: `Iverson.Server/Iverson.Api/Program.cs`, `Iverson.Server/Iverson.Api.Tests/AuthenticationPipelineTests.cs`, `Iverson.Server/Iverson.Api.Tests/BuildIdentityEndpointTests.cs`

Kestrel serves two listeners — `Grpc` on `:8080` (Http2) and `Http` on `:8081` (Http1) — but ASP.NET Core does not partition endpoint routing by listener, and nothing in `Program.cs` restricts either. The NetworkPolicy rule for `:8081` is `from: []` (all sources, all namespaces — verified against the Kubernetes NetworkPolicy spec), intended only for kubelet health probes. In reality `:8081` also serves `/admin/reconcile/{typeName}`, `/admin/dlq`, `/admin/dlq/{id}/replay`, and — confirmed during this design's verification pass — `TenantLifecycleGrpcService`/`TenantAdminGrpcService` over grpc-web (`app.UseGrpcWeb()` is global middleware, and grpc-web runs over plain HTTP/1.1, which `:8081` serves). Authentication still gates all of these, so this is a network-segmentation gap, not an auth bypass — but it removes the one compensating control the platform's own threat model names for pervasive in-cluster plaintext, for the one pod holding every datastore credential.

**Fix:** partition by listener using `RequireHost`, which matches on the `Host` header's port:

```csharp
// the four anonymous/observability endpoints — health-listener only
app.MapGet("/health/live", ...).AllowAnonymous().RequireHost("*:8081");
app.MapGet("/health",      ...).AllowAnonymous().RequireHost("*:8081");
app.MapGet("/build",       ...).AllowAnonymous().RequireHost("*:8081");
app.MapPrometheusScrapingEndpoint().AllowAnonymous().RequireHost("*:8081");

// the three admin endpoints — data-plane listener only
app.MapPost("/admin/reconcile/{typeName}", ...).RequireHost("*:8080");
app.MapGet ("/admin/dlq",                  ...).RequireHost("*:8080");
app.MapPost("/admin/dlq/{id}/replay",      ...).RequireHost("*:8080");

// the two grpc-web admin services — same listener as every other gRPC service
app.MapGrpcService<TenantLifecycleGrpcService>().RequireAuthorization("Operator").EnableGrpcWeb().RequireHost("*:8080");
app.MapGrpcService<TenantAdminGrpcService>().RequireAuthorization("TenantAdmin").EnableGrpcWeb().RequireHost("*:8080");
```

**Test infrastructure change (required, not optional):** `WebApplicationFactory`'s default test client has `BaseAddress = http://localhost/` with no port — verified empirically (a real minimal repro against `RequireHost("*:8080")` returns 404 for a request with no port in its `Host` header, 200 once the client's `BaseAddress` includes `:8080`). `AuthenticationPipelineTests.cs` (10 references to `/admin/dlq`/`/admin/reconcile`) and `BuildIdentityEndpointTests.cs` both currently call these endpoints via the default portless client, so both files' client construction must be updated to an explicit `:8080`- or `:8081`-based `BaseAddress` matching the endpoint under test, or every existing test against these routes will 404 the moment `RequireHost` is added. Add one new test asserting `/admin/dlq` 404s when hit on `:8081`.

**Out of scope, per your choice:** narrowing the NetworkPolicy rule itself (from `from: []` to the node CIDR + Prometheus pod) is not part of this fix. The `RequireHost` partitioning alone closes the actual gap; the NetworkPolicy rule stays broad but inert for anything but the intended health/metrics traffic.

---

## 2. Schema catalog leaks cross-tenant metadata via `GetSchema` (Finding #2)

Files: `Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs`, `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`, `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`, `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs`, `Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs`, `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`

`SchemaRegistry` is a single process-global dictionary keyed only on type name; `SchemaDescriptor` carries no tenant dimension. `GetSchema`'s per-type authorization check is role-based (via `RowFieldAuthorizationEvaluator`), never tenant-based, so any tenant's acting user sees every other tenant's registered type names, descriptions, and field metadata for any type declaring an `OwnerField` — confirmed by direct read of `SchemaRegistry.cs`, `ObjectMappingGrpcService.cs`, and `RowFieldAuthorizationEvaluator.cs`, and consistent with the platform's own stated design ("callers define their own schemas"). Row data itself remains correctly tenant-isolated; this is a metadata leak, not a data leak.

**Fix:** record the registering tenant and filter every caller-facing schema lookup on it.

1. Add `string? OwnerTenantId` to `SchemaDescriptor` (nullable, not `required` — legacy rows and any schema registered without an acting-user token rehydrate as `null`, meaning "no recorded tenant — visible platform-wide," the conservative default matching current behavior for anything ambiguous).
2. `SchemaRegistrationOrchestrator.RegisterAsync` does **not** currently receive the registering principal at all (its signature is `RegisterAsync(SchemaRequest request, CancellationToken ct)`) — this contradicts the CSR report's assumption that it "already has the principal in hand." Add an `ownerTenantId` parameter to the interface and implementation; the sole production call site (`ObjectMappingGrpcService.cs:52`) resolves it from `_actingUserAccessor.ActingUser?.FindFirst("tenant_id")?.Value` before calling `RegisterAsync`.
3. In `GetSchema`'s sweep (`ObjectMappingGrpcService.cs:80-84`), before the existing `_authEvaluator.Evaluate` call, skip any schema whose `OwnerTenantId` is non-null and doesn't match the caller's own `tenant_id`.
4. Apply the identical predicate to `ObjectPersistenceGrpcService.RequireSchema` and both of `ObjectRetrievalGrpcService`'s `registry.Get` call sites — today a foreign type name reaches `FailedPrecondition`/`Found = false` only when the type doesn't exist at all, which is itself a name-existence oracle; after the fix, a foreign-tenant type looks identical to a genuinely unregistered one.
5. **Extended scope (folded in per your choice):** apply the same predicate to `ObjectSearchGrpcService`'s primary-type-resolution call sites — the ones resolving a caller-supplied type name directly (e.g. the entry points backing `Search`/`Aggregate`/`GroupBy`/`Pipeline`), which share the identical caller-picks-a-type-name shape as the two sites CSR named. Exact call-site enumeration (of the ~9 `registry.Get` references in this file, several of which resolve *related* types via relation traversal rather than a caller-supplied name, and don't need the same check) happens at plan-writing time.

---

## 3. Rate limiting misses `/admin/*` and the anonymous, backend-touching `/health` (Finding #3)

Files: `Iverson.Server/Iverson.Api/Program.cs`

`AddRateLimiter` defines exactly one named policy (`"traces"`) and no `GlobalLimiter`, so any endpoint without an explicit `RequireRateLimiting` call is unlimited — including the three `/admin/*` endpoints and the anonymous `/health` endpoint, which performs four live backend round-trips per call with no caching. Separately (confirmed by direct read, matching TMA F5): `RateLimitInterceptor.Enforce` throws with no logger and no counter; `AddRateLimiter` registers no `OnRejected`; and `Program.cs` registers `ActingUserInterceptor` before `RateLimitInterceptor`, so JWT validation, the tenant-status cache lookup, and the audit-log write all run before the cheap rate-limit check.

**Fix:**

1. Add `options.GlobalLimiter` to the existing `AddRateLimiter` call — a sliding-window `PartitionedRateLimiter<HttpContext, string>` keyed on `sub` (falling back to remote IP for anonymous callers), generously sized. Verified empirically that a global limiter and a named per-endpoint policy both apply simultaneously (a request must pass both), so `/v1/traces`'s existing 60/min policy remains the binding constraint there and its existing tests are unaffected.
2. Add an `options.OnRejected` handler that logs and increments a counter, closing the "no rejection telemetry" gap for the HTTP side; add the equivalent logging to `RateLimitInterceptor.Enforce` for the gRPC side.
3. Cache `/health`'s composite result for ~2 seconds using the already-registered `IMemoryCache` (injected directly as a handler parameter, matching the DI pattern `TenantStatusCache` already establishes), so concurrent probes share one round-trip per backend. No existing test currently covers `/health` (verified — none exists), so this is a new test, not a modification.
4. Swap the two `options.Interceptors.Add<>()` registration lines so `RateLimitInterceptor` runs before `ActingUserInterceptor`. Verified safe: `RateLimitInterceptor.Enforce` reads only `context.GetHttpContext().User` (the primary principal, populated by standard ASP.NET Core authentication middleware before any gRPC interceptor runs), not anything `ActingUserInterceptor` itself sets — no coupling between the two.

---

## 4. Identifier allowlist regexes accept a trailing newline (Finding #4)

Files: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`, `Iverson.Server/Iverson.StarRocks/TenantIdentifier.cs`

.NET's `Regex` treats `$` (without `RegexOptions.Multiline`) as matching at the end of input *or* immediately before a single trailing `\n`. Both `IdentifierPattern` (`^[A-Za-z][A-Za-z0-9]*$`) and `TenantIdentifier.AllowedPattern` (`^(?!.*--)([A-Za-z0-9_-]{1,52})$`) are anchored with `$`, not `\z`. Every downstream sink quotes the identifier (double-quoted Postgres, backtick-quoted StarRocks), so this is not SQL-injectable today — but it's a validator whose documented contract ("must contain only letters and digits") silently isn't a closed set, which a future unquoted call site would inherit.

**Fix:** change both anchors from `^...$` to `\A...\z`. Add one test per pattern asserting rejection of a trailing-newline input (`"Foo\n"`, `"ab\n"`).

---

## 5. `generate-compose-secrets.sh` writes `.env` world-readable (Finding #5)

File: `scripts/generate-compose-secrets.sh`

`touch "$ENV_FILE"` and the subsequent `>>` redirects create the file at the default umask (typically `0644`), and it holds eleven live dev credentials, including the Authentik admin-orchestrator bearer token and the Qdrant API key (also the HMAC signing secret for scoped JWTs).

**Fix:** add `umask 077` before `touch`, and an explicit `chmod 600 "$ENV_FILE"` afterward to correct any `.env` already created by an earlier run of the script.

---

## 6. Python SDK declares a nonexistent build backend (Finding #6)

File: `Iverson.Clients/Python/pyproject.toml`

`build-backend = "setuptools.backends.legacy:build"` is not a real setuptools module — confirmed by direct read (line 3). Any PEP 517 build of the Python SDK fails, which is why `pip-audit-python-sdk` is red and excluded from `main`'s required status checks.

**Fix:** change to `build-backend = "setuptools.build_meta"`. Verify with a clean `pip install` in a fresh venv. Lockfile/hash-pinning adoption for either Python tree is explicitly out of scope for this fix (per your choice) — it's a separate tooling decision, not part of the literal build-backend defect.

---

## 7. `/admin/reconcile` materializes every tenant's rows in memory synchronously (Finding #7)

Files: `Iverson.Server/Iverson.Api/Reconciliation/ReconciliationService.cs`, `Iverson.Server/Iverson.Api/Program.cs`, `Iverson.Server/Iverson.Api.Tests/Reconciliation/ReconciliationServiceTests.cs`

`ReconcileTypeAsync` calls `entities.FetchAllAsync(..., EntityAccess.CrossTenantMaintenance)`, holding every row of the named type, across every tenant, as JSON strings in one in-memory collection before publishing one Kafka message per row. No batching, no timeout, no cancellation actually threaded through.

The CSR report's own suggested fix (convert to an async job queue via `ReconciliationQueueRepository`/`ReconciliationQueueWorker`) rests on a wrong assumption: that machinery is keyed on `EntityKey` for retrying individual already-failed entities, not for enqueueing type-wide reconcile jobs — reusing it as proposed would mean building new job-queue infrastructure from scratch, not the "moderate, reuse existing" effort the report estimated.

**Fix (keeps the existing synchronous response contract — no client of this endpoint exists to break, verified by repo-wide search):** replace the single `FetchAllAsync` call with the existing `FetchKeysAndTenantsPagedAsync` keyset-pagination method — already used by `DocumentRerenderQueueWorker` and `PopularitySignalReconciliationWorker` for the identical "sweep every tenant's rows of a type" scenario, with `entities.FetchManyByKeysAsync(..., EntityAccess.CrossTenantMaintenance)` fetching each page's row JSON in bulk (this exact access-level/method combination is already tested — `EntityRepositoryTests.cs:39`). Loop page-by-page (reusing the class's existing `BatchSize = 100` constant) until a short page signals completion. `ReconcileTypeAsync` already accepts `CancellationToken ct = default`; the `Program.cs` call site passes `httpContext.RequestAborted` instead of the implicit default, so a client disconnect actually stops the work. Update the three existing tests in `ReconciliationServiceTests.cs` that currently mock `FetchAllAsync` to mock the new paginated call shape instead.

---

## 8. `AddQdrant` accepts an empty API key (Finding #8)

File: `Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs`

`if (apiKey is null)` passes for an explicitly-configured empty or whitespace-only value, so startup succeeds and the failure surfaces only at first use (`MintScopedApiKey`, inside a request), as a `SymmetricSecurityKey` sizing exception — worse than a startup crash, since the pod reports ready and takes traffic first. The same file's Postgres connection-string guard a few lines away already gets this right.

**Fix:** change the guard to `string.IsNullOrWhiteSpace(apiKey)`, matching the established pattern for the Postgres connection string. Add a minimum-length assertion (32 bytes) so a too-short key also fails at startup, matching what the secret generator and Helm chart already produce.

---

## Verified assumptions

| Assumption | Evidence |
|---|---|
| `/health/live`, `/build`, `/health`, and the Prometheus scrape endpoint are each individually chainable `Map...()` calls | Read `Program.cs:382-427, 357` directly — each ends in a fluent chain `RequireHost` can attach to |
| `/admin/reconcile/{typeName}`, `/admin/dlq`, `/admin/dlq/{id}/replay` are likewise individually chainable | Read `Program.cs:429-504` directly — each ends in `.WithName(...).RequireAuthorization("Operator")` |
| `TenantLifecycleGrpcService`/`TenantAdminGrpcService` are reachable via grpc-web on `:8081` today | `Program.cs:367` (`app.UseGrpcWeb()`, global middleware) + `:566-567` (`.EnableGrpcWeb()` on both services) — grpc-web runs over HTTP/1.1, which `:8081` serves; no host restriction exists on either registration |
| `WebApplicationFactory`'s default test client sends no port in its `Host` header, and `RequireHost` requires an exact port match | Built and ran a real minimal repro (`WebApplicationFactory` + 3 endpoints, one `RequireHost`-restricted): default client → 404 on the restricted endpoint; client with `BaseAddress` including `:8080` → 200 on the matching endpoint, 404 on the `:8081`-restricted one |
| `AuthenticationPipelineTests.cs` and `BuildIdentityEndpointTests.cs` both call the affected endpoints via the default portless client | `grep` for `CreateClient()`/`BaseAddress` in both files — both use `factory.CreateClient()` with no override |
| `SchemaDescriptor` allows adding a new nullable, non-`required` property without breaking existing construction sites | Read `SchemaDescriptor.cs:3-67` — `sealed record` with `init`-only properties; `TenantColumn` is the only `required` one, added incrementally in an earlier change with the same rehydration-as-legacy-default pattern this design reuses |
| `SchemaRegistrationOrchestrator.RegisterAsync` does not currently receive the registering principal | Read the interface (`SchemaRegistrationOrchestrator.cs:12`) and implementation (`:56`) — signature is `RegisterAsync(SchemaRequest request, CancellationToken ct)`, no principal/claims parameter |
| `ISchemaRegistrationOrchestrator.RegisterAsync` has exactly one production call site | `grep -rn` across `Iverson.Server/` — one real caller (`ObjectMappingGrpcService.cs:52`), plus test substitutes |
| A legacy schema row's JSON, missing a new optional nullable key, deserializes that property as `null` rather than throwing | `SchemaDescriptor`'s properties are independently bound (not a positional record); System.Text.Json's documented behavior for a non-`required` property absent from JSON is to leave it at its default (`null` for `string?`) — contrasted directly against `TenantColumn`'s `required` modifier, which is what makes *that* property's absence throw |
| `ObjectPersistenceGrpcService.RequireSchema` and `ObjectRetrievalGrpcService`'s two `registry.Get` sites currently return, respectively, `FailedPrecondition` and `Found = false` for both a nonexistent type and (after this fix) a foreign-tenant type | Read `ObjectPersistenceGrpcService.cs:165-168` and `ObjectRetrievalGrpcService.cs:30-32, 82` directly |
| Sibling sweep of every `registry.Get`/`registry.All` consumer in `Iverson.Server/Iverson.Api` | Full `grep` across the project: internal workers/consumers (`DocumentRenderer`, `EnrichmentConsumer`, `PopularitySignalConsumer`, `DocumentRerenderConsumer`, `ReconciliationService`, `DlqMonitorConsumer`, etc.) and `SchemaRegistrationOrchestrator`'s own internal bookkeeping are platform-level/cross-tenant by design and correctly excluded; `ObjectSearchGrpcService`'s caller-facing type-resolution sites share Finding #2's shape and are folded in (see §2) |
| A `GlobalLimiter` and a named `RequireRateLimiting` policy both apply to the same request (not one overriding the other) | Built and ran a real repro: an endpoint with a stricter named policy (5/min) than the global limiter (2/min) was rejected at the *global* limiter's threshold, proving both apply simultaneously |
| `RateLimitInterceptor.Enforce` has no dependency on `ActingUserInterceptor`'s side effects | Read `RateLimitInterceptor.cs` directly — reads only `context.GetHttpContext().User`, populated by standard authentication middleware before any interceptor runs |
| No existing test covers `/health` | `grep` across `Iverson.Server/Iverson.Api.Tests/` for any reference to the `/health` route or a `Health`-named test class — none found |
| `IMemoryCache` is injectable directly as a minimal-API handler parameter | `Program.cs:286`: `builder.Services.AddMemoryCache()` already registered; `TenantStatusCache.cs` establishes the constructor-injection convention this design mirrors as a lambda parameter instead |
| Both regex literals and the compose-secrets script's content match the CSR report's citations exactly (no drift) | Read `SchemaRegistrationOrchestrator.cs:29`, `TenantIdentifier.cs:9`, and `scripts/generate-compose-secrets.sh` in full — byte-for-byte match |
| `Iverson.Clients/Python/pyproject.toml`'s exact current `build-backend` value | Read the file directly — line 3, matches CSR's citation |
| `FetchKeysAndTenantsPagedAsync`'s signature/return shape, and the established page-until-exhausted pattern | Read `EntityRepository.cs:48-60` and `DocumentRerenderQueueWorker.cs:130-160` — returns `KeyedTenantRow(Key, TenantId)`; existing caller loops until `page.Count < opts.PageSize` |
| `FetchManyByKeysAsync` with `EntityAccess.CrossTenantMaintenance` is an already-tested combination | `EntityRepositoryTests.cs:39` exercises exactly this pairing |
| `ReconcileTypeAsync`'s exact current signature already accepts a `CancellationToken` | Read `ReconciliationService.cs:24`: `ReconcileTypeAsync(string typeName, CancellationToken ct = default)` — no signature change needed, just passing a real token at the call site |
| The class's existing `BatchSize = 100` constant is reusable as the new page size | Read `ReconciliationService.cs:22` — currently used only by `ProcessQueuedFailuresAsync`, not `ReconcileTypeAsync` |
| No client of `/admin/reconcile` exists anywhere in the repo that depends on its synchronous response shape | Repo-wide `grep` for `admin/reconcile`/`ReconcileTypeAsync` — every hit is server-side code, tests, or docs; no client SDK, LoadTest script, or AdminUI reference |
| Three existing tests in `ReconciliationServiceTests.cs` mock `FetchAllAsync` and will need updating | Read the file directly — `ReconcileTypeAsync_UnknownType_ReturnsNull`, `ReconcileTypeAsync_ReadsUnderTheCrossTenantMaintenanceRole`, `ReconcileTypeAsync_RepublishesEveryRow_ReturnsCount` all mock `FetchAllAsync` |
| `AddQdrant`'s exact current guard and the Postgres connection-string pattern being matched | Read `ServiceCollectionExtensions.cs:21-26` and `Program.cs:199-204` directly |

## Known issues / accepted as out of scope

- **NetworkPolicy narrowing for `:8081`** (per your choice) — the `from: []` rule stays broad; only the code-level `RequireHost` partitioning is in scope for this fix. Revisit if a cluster-profile-portable node-CIDR source becomes available.
- **Python SDK/Agents lockfile and hash-pinning adoption** (per your choice) — out of scope for this fix. The build-backend line is corrected; introducing `pip-compile`/hash pinning for either Python tree is a separate tooling decision for later.
- **Exact `ObjectSearchGrpcService` call-site enumeration** — this design folds the fix in per your choice, but the precise subset of its ~9 `registry.Get` references that need the `OwnerTenantId` check (versus the ones resolving *related* types via relation traversal, which don't) is deferred to plan-writing, which will read the file in full before implementing.
