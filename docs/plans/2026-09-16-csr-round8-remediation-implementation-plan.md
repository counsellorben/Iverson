# CSR Round 8 Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-16-csr-round8-remediation-design.md` (commit SHA: `21ffdbbae36e10348fb32f82dc0734a914acdd16`)

**Goal:** Implement all 8 fixes from the CSR round 8 remediation design against the Iverson repository.

**Architecture:** Each of the 8 findings is independently scoped and touches a mostly-disjoint set of files (per the spec's own opening note); this plan covers all 8 as one document with 8 tasks, matching the precedent of the CSR round 7 remediation plan.

**Tech stack:** .NET 10 (ASP.NET Core minimal APIs, gRPC, `System.Threading.RateLimiting`), Python (setuptools/pip), bash.

---

## Global Constraints

- Never use `git add -A`; stage the specific files each task touches.
- `docs/plans/`, `docs/criticalreviews/`, `docs/specs/`, `docs/security/` are gitignored in this repo — use `git add -f` for any commit touching them. `docs/runbooks/` is **not** gitignored (verified: `git check-ignore -v docs/runbooks/chunk-collection-cleanup-2026-07.md` exits 1) — stage it normally, no `-f`.
- Commit messages match this repo's existing convention: a lowercase, imperative one-line summary; no `feat:`/`fix:`-style prefixes.
- **`Iverson.Server/Iverson.Api/Program.cs` is modified by Tasks 1, 3, and 7, in disjoint regions**: Task 1 touches the endpoint-registration lines (`MapGet`/`MapPost`/`MapGrpcService`/`MapPrometheusScrapingEndpoint`) plus adds one new middleware near the top of the middleware section; Task 3 touches the `AddRateLimiter` service-registration block, the two `options.Interceptors.Add<>()` lines, and the `/health` handler's body; Task 7 touches only the `/admin/reconcile/{typeName}` handler's call to `ReconcileTypeAsync`. Locate each by matching the quoted code shown in that task, not by a fixed line offset — if another task has already run, line numbers will have shifted.
- **`Iverson.Server/Iverson.Api/Reconciliation/ReconciliationService.cs` is modified by Tasks 1 and 7, in disjoint regions**: Task 1 touches only the `LogCritical` operator message inside `RecordFailureAsync` (near the bottom of the file); Task 7 rewrites `ReconcileTypeAsync`'s body (near the top) and removes the now-dead private `ExtractKey` helper. Same locate-by-quoted-content rule applies.

## File Structure

- **Modify:** `Iverson.Server/Iverson.Api/Program.cs`, `Iverson.Server/Iverson.Api/Reconciliation/ReconciliationService.cs`, `docs/runbooks/chunk-collection-cleanup-2026-07.md`, `Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs`, `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`, `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`, `Iverson.Server/Iverson.Api/Grpc/RateLimitInterceptor.cs`, `Iverson.Server/Iverson.StarRocks/TenantIdentifier.cs`, `scripts/generate-compose-secrets.sh`, `Iverson.Clients/Python/pyproject.toml`, `Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs`
- **Test:** `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`, `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`, a new `RequireListenerPort` test in `Iverson.Server/Iverson.Api.Tests/AuthenticationPipelineTests.cs`, a new `/health`-caching test, `Iverson.Server/Iverson.StarRocks.Tests/TenantIdentifierTests.cs`, `Iverson.Server/Iverson.Api.Tests/Reconciliation/ReconciliationServiceTests.cs`, `Iverson.Server/Iverson.Vector.Tests/ServiceCollectionExtensionsTests.cs`

## Inherited from spec

The following were verified by `thorough-brainstorming` (design) and its two `critical-design-review` rounds, and are NOT re-verified here — see the source spec's own "Verified assumptions" section for the full list with evidence. Highlights load-bearing for this plan:

- `RequireHost` was considered and empirically rejected as the listener-partition mechanism (spoofable `Host` header; portless real-Ingress traffic breaks). `HttpContext.Connection.LocalPort`, checked against endpoint metadata with a `LocalPort == 0` carve-out, is the adopted mechanism, already written and reasoned on the unmerged `admin-console-landing-page` branch.
- `WebApplicationFactory`'s default in-memory client reports `LocalPort == 0` — verified empirically — so no existing test needs client-construction changes for Task 1.
- `SchemaDescriptor` allows a new nullable non-`required` property with no breakage; a legacy JSON row missing the key deserializes that property as `null`.
- `ISchemaRegistrationOrchestrator.RegisterAsync` does not currently receive the registering principal; its sole *production* call site is `ObjectMappingGrpcService.cs:52`.
- One registered type's Postgres table holds every tenant's rows (RLS, not per-table separation) — this is why the design deliberately does **not** extend the tenant check to the addressability paths (`RequireSchema`/`registry.Get` sites), only to `GetSchema`'s discovery sweep.
- `:8080` is Kestrel's `Http2`-only listener; pinning HTTP/1.1 JSON endpoints there requires their callers to switch to HTTP/2 prior-knowledge — verified empirically (plain `curl` → 400, `curl --http2-prior-knowledge` → 200).
- `FetchKeysAndTenantsPagedAsync` + `FetchManyByKeysAsync` (with `EntityAccess.CrossTenantMaintenance`) is an established, already-tested pattern for "sweep every tenant's rows of a type" (used by `DocumentRerenderQueueWorker`, `PopularitySignalReconciliationWorker`).
- No client anywhere in the repo depends on `/admin/reconcile`'s synchronous response shape.
- A `GlobalLimiter` and a named `RequireRateLimiting` policy both apply to the same request (verified empirically) — `/v1/traces`'s existing 60/min policy stays binding there regardless of the new global limiter.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time:

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | Task ordering / consumer impact | `context.GetEndpoint()` resolves the matched endpoint's metadata inside a plain `app.Use(...)` middleware even with no explicit `UseRouting()` call anywhere in `Program.cs` (load-bearing for both Task 1's marker check and Task 3's `GetNoLimiter` branch) | Built and ran a real minimal Kestrel app mirroring the exact structure (no `UseRouting()`, a `MapGet` + a plain `app.Use` middleware reading `context.GetEndpoint()`): request to `/probe` reported `endpoint=HTTP: GET /probe`, not null |
| 2 | Code-in-plan validity | `.WithMetadata(new RequireListenerPort(...))` compiles chained after `.RequireAuthorization()` and after `.RequireAuthorization().RequireRateLimiting(...)`, at this repo's exact `Grpc.AspNetCore`/`Grpc.AspNetCore.Web` version (2.80.0) | Built a real throwaway project at that exact package version reproducing both chains (`MapGet(...).RequireAuthorization().WithMetadata(...)`; `MapPost(...).RequireAuthorization().RequireRateLimiting("x").WithMetadata(...)`) — compiled with 0 errors |
| 3 | File path / chainability | The 3 admin endpoints, the 2 grpc-web services, and `/v1/traces` each end in a chain `.WithMetadata(...)` can attach to | Read `Program.cs:429-505` (admin endpoints, end in `.WithName(...).RequireAuthorization("Operator")`), `:566-567` (grpc-web, end in `.EnableGrpcWeb()`), `:586-614` (`/v1/traces`, ends in `.RequireAuthorization().RequireRateLimiting("traces")`) directly |
| 4 | Task ordering | Placing the new `RequireListenerPort` middleware as the first `app.Use*`/`app.UseX()` call — immediately before `app.UseHttpsRedirection()` — introduces no ordering conflict, since only 2 `Map` calls (Prometheus, conditionally OpenApi) precede it and neither is a middleware | Read `Program.cs` from `var app = builder.Build();` through `app.UseHttpsRedirection();` directly — confirmed no `Use*` call exists before that point today |
| 5 | Consumer impact (Cat 6, sibling sweep) | `ISchemaRegistrationOrchestrator.RegisterAsync`'s only *production* call site is `ObjectMappingGrpcService.cs:52`; its only *test-double* (mocked) call sites are 2, both in `ObjectMappingGrpcServiceTests.cs:201,233`; its DI registration (`Program.cs:274`) needs no change since it registers the type, not a call | `grep -rn "ISchemaRegistrationOrchestrator"` across `Iverson.Server/` — every hit classified; `RegisterSchemaAuthorizationIntegrationTests.cs:221` is a comment, not a call site |
| 6 | Consumer impact (Cat 6, forced decision) | `SchemaRegistrationOrchestratorTests.cs` has a **separate, much larger** population: 70 direct calls to `_sut.RegisterAsync(request, CancellationToken.None)` against the real (non-mocked) orchestrator — every one binds positionally and breaks under a signature change unless the new parameter is placed before `ct` (per your choice) and every call site is updated | `grep -c "_sut.RegisterAsync("` → 70; cross-checked `grep -c "CancellationToken.None)"` → also 70, and every one of those 70 lines contains `RegisterAsync(` (verified via `grep -v "RegisterAsync("` on the same search returning zero non-matching lines) — confirms a single scoped find-replace of `, CancellationToken.None)` → `, null, CancellationToken.None)` is safe and complete for this file |
| 7 | Code validity | `descriptor with { OwnerTenantId = ownerTenantId }` is valid C# against `SchemaDescriptor`'s declared shape | Read `SchemaDescriptor.cs:3` — `sealed record` — record `with`-expressions are valid against any record type regardless of which properties are `required`/`init`-only |
| 8 | File path / signature | `AddRateLimiter`'s current block, `RateLimitInterceptor.cs`'s current shape (no logger, no `OnRejected` anywhere), and the `/health` handler's exact current body | Read `Program.cs:90-116` and `RateLimitInterceptor.cs` (full file, 45 lines) and `Program.cs:390-424` directly |
| 9 | Consumer impact | `FetchKeysAndTenantsPagedAsync`'s in-method (not cross-tick) loop precedent is `PopularitySignalReconciliationWorker.SweepSignalAsync`, not `DocumentRerenderQueueWorker` (which persists a cursor across worker ticks instead) — the correct model for `ReconcileTypeAsync`, a single synchronous call | Read `PopularitySignalReconciliationWorker.cs:46-66` directly — a `while (!ct.IsCancellationRequested) { page = ...; if (page.Count == 0) break; ...; afterKey = page[^1].Key; }` loop fully contained within one method call |
| 10 | Consumer impact (Cat 6) | `ReconciliationService.cs`'s private `ExtractKey` helper has exactly one caller (inside `ReconcileTypeAsync`, being removed) and one declaration — safe to delete entirely, not dead-code-in-waiting | `grep -n "\bExtractKey\b" ReconciliationService.cs` → exactly 2 hits: the declaration and the one call site being removed |
| 11 | Consumer impact (Cat 6) | Of the 3 `ReconciliationServiceTests.cs` tests the spec says "mock `FetchAllAsync`", only 2 (`ReconcileTypeAsync_ReadsUnderTheCrossTenantMaintenanceRole`, `ReconcileTypeAsync_RepublishesEveryRow_ReturnsCount`) actually need changes; `ReconcileTypeAsync_UnknownType_ReturnsNull` never reaches the paginated fetch (its schema lookup returns null first) and needs no change | Read all 3 tests directly (`ReconciliationServiceTests.cs:39-79`) — the unknown-type test only calls `_sut.ReconcileTypeAsync("NoSuchType")` and asserts null, never stubbing `FetchAllAsync` |
| 12 | Consumer impact (Cat 6, sibling sweep) | `AddQdrant(`'s only call sites repo-wide are 1 production (`Program.cs:247`) + 3 test (`ServiceCollectionExtensionsTests.cs:16,28,46`) | `grep -rn "AddQdrant("` across the whole repo (worktrees excluded) — exactly 4 real call sites; `AuthTestWebApplicationFactory.cs:32` is a comment, not a call |
| 13 | Test/file path | `Iverson.Server/Iverson.StarRocks.Tests/TenantIdentifierTests.cs` exists and uses a direct `TenantIdentifier.IsValid(...)` assertion style (no DI/mocking) — the right place and shape for the new trailing-newline test | Read the file directly (`[Theory]`/`[Fact]` static-call convention, `:1-40`) |
| 14 | Test/file path | `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs` exists and tests `IdentifierPattern`'s effect only through the full `RegisterAsync(...)` → `RpcException(InvalidArgument)` path, never by calling the regex directly — the new trailing-newline test must match this convention, not invent a lower-level unit test | Read the file's setup and one full existing test (`RegisterAsync_WithInvalidOwnerField_ThrowsInvalidArgument`, `:54-64`) directly |
| 15 | Test/build command | A clean `pip install` of the Python SDK succeeds once `build-backend` is corrected to `setuptools.build_meta`, and the flat package layout's sibling dirs (`conformance`, `sample`, `scripts`, `tests`) raise no discovery ambiguity given `[tool.setuptools.packages.find] include = ["iverson_client*"]` is already explicit | Reusing this spec's own already-run-tier-verified evidence (recorded during this design's CDR round 2): `python3 -c "import setuptools.backends.legacy"` → `ModuleNotFoundError`; with the corrected backend substituted on a scratch copy, `setuptools.build_meta.build_wheel(...)` returned a real `iverson_client-0.1.0-py3-none-any.whl` |

## Tasks

### Task 1: Health port listener partitioning

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Modify: `Iverson.Server/Iverson.Api/Reconciliation/ReconciliationService.cs`
- Modify: `docs/runbooks/chunk-collection-cleanup-2026-07.md`
- Test: `Iverson.Server/Iverson.Api.Tests/AuthenticationPipelineTests.cs`

- [ ] **Step 1: Add the marker type and the port-check middleware**
  In `Program.cs`, add near the top of the file (e.g., directly above `var builder = WebApplication.CreateBuilder(args);`, or any top-level location — it's a plain internal class, not tied to any other declaration):
  ```csharp
  internal sealed class RequireListenerPort(int port) { public int Port => port; }
  ```
  Then, locate `var app = builder.Build();` and insert the following middleware immediately before the existing `app.UseHttpsRedirection();` line (currently the first `Use*` call in the file — verify this is still true before inserting, per this task's own Global Constraints note):
  ```csharp
  app.Use(async (HttpContext context, Func<Task> next) =>
  {
      var required = context.GetEndpoint()?.Metadata.GetMetadata<RequireListenerPort>();
      if (required is not null && context.Connection.LocalPort is not 0 &&
          context.Connection.LocalPort != required.Port)
      {
          context.Response.StatusCode = StatusCodes.Status404NotFound;
          return;
      }
      await next();
  });
  ```

- [ ] **Step 2: Mark the four anonymous/observability endpoints for the health listener**
  Find each of the following (exact current lines, verify against the file since other tasks may shift them) and append `.WithMetadata(new RequireListenerPort(8081))` to its existing chain:
  - `app.MapPrometheusScrapingEndpoint().AllowAnonymous();` → `app.MapPrometheusScrapingEndpoint().AllowAnonymous().WithMetadata(new RequireListenerPort(8081));`
  - `app.MapGet("/health/live", () => Results.Ok(new { status = "alive" })).WithName("HealthLive").AllowAnonymous();` → append `.WithMetadata(new RequireListenerPort(8081))`
  - The `/build` handler's closing `.WithName("BuildIdentity").AllowAnonymous();` → append `.WithMetadata(new RequireListenerPort(8081))`
  - The `/health` handler's closing `.WithName("Health").AllowAnonymous();` → append `.WithMetadata(new RequireListenerPort(8081))`

- [ ] **Step 3: Mark the three admin endpoints and the two grpc-web services for the data-plane listener**
  Append `.WithMetadata(new RequireListenerPort(8080))` to each of:
  - `/admin/reconcile/{typeName}`'s closing `.WithName("Reconcile").RequireAuthorization("Operator");`
  - `/admin/dlq`'s closing `.WithName("ListDlq").RequireAuthorization("Operator");`
  - `/admin/dlq/{id}/replay`'s closing `.WithName("ReplayDlq").RequireAuthorization("Operator");`
  - `app.MapGrpcService<TenantLifecycleGrpcService>().RequireAuthorization("Operator").EnableGrpcWeb();`
  - `app.MapGrpcService<TenantAdminGrpcService>().RequireAuthorization("TenantAdmin").EnableGrpcWeb();`

- [ ] **Step 4: Mark `/v1/traces` for the data-plane listener**
  Append `.WithMetadata(new RequireListenerPort(8080))` to `/v1/traces`'s closing `.RequireAuthorization().RequireRateLimiting("traces");` (inside the `if (workloadRole == "api")` block).

- [ ] **Step 5: Update the two operator-facing HTTP/2 documents**
  In `docs/runbooks/chunk-collection-cleanup-2026-07.md`, change both:
  ```
  curl -X POST http://localhost:8080/admin/reconcile/{TypeName}
  ```
  and
  ```
  kubectl -n <ns> exec deploy/<release>-api -- curl -X POST http://localhost:8080/admin/reconcile/{TypeName}
  ```
  to `curl --http2-prior-knowledge -X POST ...` (same URL, same flag added to both).

  In `ReconciliationService.cs`'s `RecordFailureAsync`, change the `LogCritical` message from:
  ```csharp
  "[Reconciliation] Giving up on type={Type} key={Key} after {Attempts} attempts — " +
  "requires a manual in-cluster POST to /admin/reconcile/{Type} (kubectl port-forward or " +
  "exec — not reachable via the ingress). Last error: {Error}",
  ```
  to:
  ```csharp
  "[Reconciliation] Giving up on type={Type} key={Key} after {Attempts} attempts — " +
  "requires a manual in-cluster HTTP/2 POST (e.g. curl --http2-prior-knowledge) to " +
  "/admin/reconcile/{Type} (kubectl port-forward or exec — not reachable via the ingress). " +
  "Last error: {Error}",
  ```

- [ ] **Step 6: Add a regression test proving the marker actually partitions**
  `AuthenticationPipelineTests`'s existing `IClassFixture<AuthTestWebApplicationFactory>` shares one in-memory (`LocalPort == 0`) fixture across the whole class — inadequate for this test, which needs two *real* bound ports to prove the marker discriminates by listener, not just that it exists. Add the new test to `AuthenticationPipelineTests.cs` (same file, matching this endpoint's existing test neighbors) but have it construct and dispose its own separate `WebApplicationFactory<Program>`, configured via `ConfigureWebHost(builder => builder.UseKestrel(k => { k.ListenLocalhost(0, o => o.Protocols = HttpProtocols.Http1); k.ListenLocalhost(0, o => o.Protocols = Http2); }))` (or equivalent — two real loopback ports, distinct from the shared fixture, torn down at the end of the test) — do not modify the shared class fixture, which every other test in the file depends on. Assert: a request to the real `/admin/dlq` route (already marked `RequireListenerPort(8080)` by Step 3) succeeds on the Http2-bound port and 404s on the Http1-bound port.

- [ ] **Step 7: Run the tests**
  ```bash
  cd Iverson.Server && dotnet test --filter "Category!=Integration&FullyQualifiedName~AuthenticationPipelineTests"
  ```

- [ ] **Step 8: Commit**
  ```bash
  git add Iverson.Server/Iverson.Api/Program.cs \
          Iverson.Server/Iverson.Api/Reconciliation/ReconciliationService.cs \
          docs/runbooks/chunk-collection-cleanup-2026-07.md \
          Iverson.Server/Iverson.Api.Tests/AuthenticationPipelineTests.cs
  git commit -m "partition endpoints by accepting listener port instead of the spoofable Host header"
  ```

---

### Task 2: Schema catalog tenant-scoping

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`

**Interfaces:**
- Consumes: nothing from another task in this plan.
- Produces: nothing another task in this plan consumes.

- [ ] **Step 1: Add `OwnerTenantId` to `SchemaDescriptor`**
  Immediately after the existing `PopularitySignalColumn` property (matching that property's own established "defaulted, not required — legacy rows predate this, absent is benign" comment convention), add:
  ```csharp
  // Defaulted, not required: legacy _iverson_schema rows predate this marker and carry no such
  // key — deserializes to null, meaning "no recorded tenant, visible platform-wide" (the
  // conservative default). Populated at registration time from the acting user's tenant_id
  // claim; null when a service-token-only caller registers with no acting-user header.
  public string? OwnerTenantId { get; init; }
  ```

- [ ] **Step 2: Thread `ownerTenantId` through `ISchemaRegistrationOrchestrator.RegisterAsync` (conventional ordering, per your choice)**
  Change the interface:
  ```csharp
  // before
  Task<IReadOnlyList<string>> RegisterAsync(SchemaRequest request, CancellationToken ct);
  // after
  Task<IReadOnlyList<string>> RegisterAsync(SchemaRequest request, string? ownerTenantId, CancellationToken ct);
  ```
  and the implementation's signature identically. In phase 3's registration loop, immediately before `await registry.RegisterAsync(descriptor);`, add:
  ```csharp
  descriptor = descriptor with { OwnerTenantId = ownerTenantId };
  ```

- [ ] **Step 3: Resolve and pass `ownerTenantId` at the one production call site**
  In `ObjectMappingGrpcService.RegisterSchema`, change:
  ```csharp
  var registered = await _schemaRegistration.RegisterAsync(request, context.CancellationToken);
  ```
  to:
  ```csharp
  var ownerTenantId = _actingUserAccessor.ActingUser?.FindFirst("tenant_id")?.Value;
  var registered = await _schemaRegistration.RegisterAsync(request, ownerTenantId, context.CancellationToken);
  ```

- [ ] **Step 4: Filter `GetSchema`'s sweep**
  Immediately before the existing `foreach (var schema in _registry.All.Values)` loop, add:
  ```csharp
  var callerTenant = _actingUserAccessor.ActingUser?.FindFirst("tenant_id")?.Value;
  ```
  and as the first line inside the loop body (before `var decision = _authEvaluator.Evaluate(...)`):
  ```csharp
  if (schema.OwnerTenantId is not null && schema.OwnerTenantId != callerTenant)
      continue;
  ```

- [ ] **Step 5: Update the 2 mocked test call sites**
  In `ObjectMappingGrpcServiceTests.cs`, update both occurrences of:
  ```csharp
  mockOrchestrator.RegisterAsync(Arg.Any<SchemaRequest>(), Arg.Any<CancellationToken>())
  ```
  to:
  ```csharp
  mockOrchestrator.RegisterAsync(Arg.Any<SchemaRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
  ```

- [ ] **Step 6: Bulk-fix the 70 direct call sites in `SchemaRegistrationOrchestratorTests.cs`**
  Every one of the 70 calls is the identical literal pattern ending in `, CancellationToken.None)` (verified — assumption #6 above — this exact substring occurs 70 times in the file and all 70 are `RegisterAsync(` calls, so a scoped, whole-file find-replace is safe). Replace every occurrence of:
  ```
  , CancellationToken.None)
  ```
  with:
  ```
  , null, CancellationToken.None)
  ```
  Re-run a `grep -c "_sut.RegisterAsync("` vs. a fresh `grep -c ", null, CancellationToken.None)"` count after editing to confirm both equal 70 (population closure check — catches a partial replace).

- [ ] **Step 7: Run the tests**
  ```bash
  cd Iverson.Server && dotnet test --filter "Category!=Integration&(FullyQualifiedName~ObjectMappingGrpcServiceTests|FullyQualifiedName~SchemaRegistrationOrchestratorTests)"
  ```

- [ ] **Step 8: Commit**
  ```bash
  git add Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs \
          Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs \
          Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs \
          Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs \
          Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs
  git commit -m "scope GetSchema's catalog to the registering tenant, closing the cross-tenant metadata leak"
  ```

---

### Task 3: Rate limiting for `/admin/*` and `/health`

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/RateLimitInterceptor.cs`

**Interfaces:**
- Consumes: Task 1's `RequireListenerPort` marker exists on the same endpoints this task's `GetNoLimiter` branch must exclude by identity, not by re-deriving the port list — reuse the same metadata check (`context.GetEndpoint()?.Metadata.GetMetadata<RequireListenerPort>()`), not a hardcoded path list, so the two tasks' endpoint enumeration can never drift apart.

- [ ] **Step 1: Add the branching `GlobalLimiter`**
  In `Program.cs`'s existing `AddRateLimiter` call, before `options.AddPolicy("traces", ...)`, add:
  ```csharp
  options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
  {
      // Exclude the gRPC data plane (already governed by RateLimitInterceptor's own 50,000/min
      // budget) and the anonymous probe/observability endpoints (kubelet, Prometheus — a 429
      // there pulls the pod out of service). Both are marked RequireListenerPort(8081) or are
      // gRPC methods, identified the same way Task 1's middleware identifies them, so this
      // list can never silently diverge from Task 1's own endpoint enumeration.
      var isHealthListenerEndpoint = ctx.GetEndpoint()?.Metadata.GetMetadata<RequireListenerPort>()?.Port == 8081;
      var isGrpcCall = ctx.Request.ContentType?.StartsWith("application/grpc") == true;
      if (isHealthListenerEndpoint || isGrpcCall)
          return RateLimitPartition.GetNoLimiter(ctx.Request.Path.ToString());

      return RateLimitPartition.GetSlidingWindowLimiter(
          ctx.User.FindFirst("sub")?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
          _ => new SlidingWindowRateLimiterOptions
          {
              PermitLimit = 6_000,
              Window = TimeSpan.FromMinutes(1),
              SegmentsPerWindow = 6,
              QueueLimit = 0
          });
  });
  ```

- [ ] **Step 2: Add rejection telemetry on both sides**
  In the same `AddRateLimiter` call, add:
  ```csharp
  options.OnRejected = (ctx, _) =>
  {
      ctx.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>()
          .LogWarning("[RateLimit] Rejected {Path}", ctx.HttpContext.Request.Path);
      return ValueTask.CompletedTask;
  };
  ```
  In `RateLimitInterceptor.cs`, add an `ILogger<RateLimitInterceptor>` constructor parameter and log inside `Enforce` when the lease is not acquired:
  ```csharp
  public sealed class RateLimitInterceptor(ILogger<RateLimitInterceptor> logger) : Interceptor
  {
      // ... existing _limiter field unchanged ...

      private void Enforce(ServerCallContext context)
      {
          var subject = context.GetHttpContext().User.FindFirst("sub")?.Value ?? "unknown";
          using var lease = _limiter.AttemptAcquire(subject);
          if (!lease.IsAcquired)
          {
              logger.LogWarning("[RateLimit] Rejected gRPC call for subject {Subject}", subject);
              throw new RpcException(new Status(StatusCode.ResourceExhausted, "Rate limit exceeded."));
          }
      }
  }
  ```
  `AddSingleton<RateLimitInterceptor>()` (already registered, `Program.cs:90`) resolves the added constructor dependency automatically — no registration change needed.

- [ ] **Step 3: Cache `/health`'s composite result**
  Add `IMemoryCache cache` as a new parameter to the `/health` handler's lambda (alongside its existing 5 parameters). Wrap the existing body:
  ```csharp
  app.MapGet("/health", async (
      IRecordStoreQueryExecutor db,
      IEngagementStoreHealthCheck sr,
      IVectorSchemaManager vector,
      IEventBrokerHealthCheck kafka,
      IOptions<EngagementStoreOptions> engagementOptions,
      IMemoryCache cache) =>
  {
      if (cache.TryGetValue("health-composite", out IResult? cached))
          return cached!;

      // ... existing body unchanged, computing `readiness`/`checks` ...

      var result = readiness.Ready
          ? Results.Ok(new { status = readiness.FullyHealthy ? "healthy" : "degraded", checks })
          : Results.Json(new { status = "degraded", checks }, statusCode: 503);

      cache.Set("health-composite", result, TimeSpan.FromSeconds(2));
      return result;
  })
  ```

- [ ] **Step 4: Swap the interceptor registration order**
  ```csharp
  // before
  options.Interceptors.Add<ActingUserInterceptor>();
  options.Interceptors.Add<RateLimitInterceptor>();
  // after
  options.Interceptors.Add<RateLimitInterceptor>();
  options.Interceptors.Add<ActingUserInterceptor>();
  ```

- [ ] **Step 5: Add a test for `/health` caching**
  The shared `AuthTestWebApplicationFactory` does not substitute `/health`'s 4 dependencies (`IRecordStoreQueryExecutor`, `IEngagementStoreHealthCheck`, `IVectorSchemaManager`, `IEventBrokerHealthCheck`) with test doubles — verify this against the file directly before writing the test. Add the new test to `AuthenticationPipelineTests.cs`, constructing its own separate `WebApplicationFactory<Program>` (same pattern as Task 1 Step 6's test) whose `ConfigureWebHost` calls `services.RemoveAll<T>(); services.AddSingleton(Substitute.For<T>());` for each of the 4 interfaces (mirroring `AuthTestWebApplicationFactory`'s own established `RemoveAll`/`AddSingleton` substitution pattern for its other startup-blocking services), each stubbed to return a healthy/successful result. Issue 2 sequential `GET /health` calls against this factory's client within the 2-second cache window and assert each of the 4 substituted dependencies was invoked exactly once (e.g. `await fakeDb.Received(1).QuerySingleOrDefaultAsync<int>(Arg.Any<string>())`), not twice.

- [ ] **Step 6: Run the tests**
  ```bash
  cd Iverson.Server && dotnet test --filter "Category!=Integration&(FullyQualifiedName~AuthenticationPipelineTests|FullyQualifiedName~RateLimitInterceptorTests)"
  ```

- [ ] **Step 7: Commit**
  ```bash
  git add Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api/Grpc/RateLimitInterceptor.cs
  git commit -m "add a global rate limiter for /admin/* and /health, with rejection telemetry and a health-check cache"
  ```

---

### Task 4: Identifier allowlist regex anchors

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`
- Modify: `Iverson.Server/Iverson.StarRocks/TenantIdentifier.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`
- Test: `Iverson.Server/Iverson.StarRocks.Tests/TenantIdentifierTests.cs`

- [ ] **Step 1: Anchor both patterns with `\A`/`\z`**
  ```csharp
  // SchemaRegistrationOrchestrator.cs — before
  internal static readonly Regex IdentifierPattern = new("^[A-Za-z][A-Za-z0-9]*$", RegexOptions.Compiled);
  // after
  internal static readonly Regex IdentifierPattern = new(@"\A[A-Za-z][A-Za-z0-9]*\z", RegexOptions.Compiled);
  ```
  ```csharp
  // TenantIdentifier.cs — before
  private static readonly Regex AllowedPattern = new("^(?!.*--)([A-Za-z0-9_-]{1,52})$", RegexOptions.Compiled);
  // after
  private static readonly Regex AllowedPattern = new(@"\A(?!.*--)([A-Za-z0-9_-]{1,52})\z", RegexOptions.Compiled);
  ```

- [ ] **Step 2: Add a regression test per pattern**
  In `SchemaRegistrationOrchestratorTests.cs`, matching the file's `RegisterAsync(...) → RpcException(InvalidArgument)` convention:
  ```csharp
  [Fact]
  public async Task RegisterAsync_WithATrailingNewlineTypeName_ThrowsInvalidArgument()
  {
      var td = SimpleType("Widget\n", "Name");

      var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, null, CancellationToken.None);

      var ex = await act.Should().ThrowAsync<RpcException>();
      ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
  }
  ```
  In `TenantIdentifierTests.cs`, matching its direct-call convention:
  ```csharp
  [Fact]
  public void IsValid_RejectsTrailingNewline()
  {
      TenantIdentifier.IsValid("ab\n").Should().BeFalse();
  }
  ```

- [ ] **Step 3: Run the tests**
  ```bash
  cd Iverson.Server && dotnet test --filter "Category!=Integration&(FullyQualifiedName~SchemaRegistrationOrchestratorTests|FullyQualifiedName~TenantIdentifierTests)"
  ```

- [ ] **Step 4: Commit**
  ```bash
  git add Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs \
          Iverson.Server/Iverson.StarRocks/TenantIdentifier.cs \
          Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs \
          Iverson.Server/Iverson.StarRocks.Tests/TenantIdentifierTests.cs
  git commit -m "anchor the identifier allowlist regexes with \A/\z so a trailing newline cannot slip through"
  ```

---

### Task 5: `generate-compose-secrets.sh` file permissions

**Files:**
- Modify: `scripts/generate-compose-secrets.sh`

- [ ] **Step 1: Set a restrictive umask before creation, and correct any pre-existing file**
  ```bash
  # before
  touch "$ENV_FILE"
  # after
  umask 077
  touch "$ENV_FILE"
  chmod 600 "$ENV_FILE"
  ```

- [ ] **Step 2: Verify**
  ```bash
  bash scripts/generate-compose-secrets.sh && ls -l Iverson.Server/.env
  ```
  Confirm the mode is `-rw-------`.

- [ ] **Step 3: Commit**
  ```bash
  git add scripts/generate-compose-secrets.sh
  git commit -m "restrict generate-compose-secrets.sh's .env output to owner-only permissions"
  ```

---

### Task 6: Python SDK build backend

**Files:**
- Modify: `Iverson.Clients/Python/pyproject.toml`

- [ ] **Step 1: Correct the backend**
  ```toml
  # before
  build-backend = "setuptools.backends.legacy:build"
  # after
  build-backend = "setuptools.build_meta"
  ```

- [ ] **Step 2: Verify with a clean install**
  ```bash
  python3 -m venv /tmp/iverson-sdk-verify-venv
  /tmp/iverson-sdk-verify-venv/bin/pip install ./Iverson.Clients/Python
  rm -rf /tmp/iverson-sdk-verify-venv
  ```
  Expect a successful install with no `ModuleNotFoundError`.

- [ ] **Step 3: Commit**
  ```bash
  git add Iverson.Clients/Python/pyproject.toml
  git commit -m "fix the Python SDK's build-backend to the real setuptools.build_meta module"
  ```

---

### Task 7: `/admin/reconcile` pagination and cancellation

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Reconciliation/ReconciliationService.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Reconciliation/ReconciliationServiceTests.cs`

**Interfaces:**
- Consumes: nothing from another task in this plan.
- Produces: nothing another task in this plan consumes.

- [ ] **Step 1: Rewrite `ReconcileTypeAsync` to page instead of loading the whole table**
  Replace the method body (keeping the signature `ReconcileTypeAsync(string typeName, CancellationToken ct = default)` unchanged) with:
  ```csharp
  public async Task<int?> ReconcileTypeAsync(string typeName, CancellationToken ct = default)
  {
      var schema = registry.Get(typeName);
      if (schema is null) return null;

      var tableSchema = SchemaBuilder.ToTableSchema(schema);
      var targetStores = StoreTargeting.DetermineTargetStores(schema);
      var traceId = Activity.Current?.TraceId.ToString() ?? string.Empty;
      var count = 0;
      string? afterKey = null;

      // Cross-tenant by design: an admin reconcile of a type re-projects EVERY tenant's rows
      // of that type back through the fan-out pipeline. Paginated (not FetchAllAsync) so the
      // whole table is never held in memory at once; the loop observes `ct` itself so a client
      // disconnect actually stops the work (passing the token alone stops nothing, since none
      // of FetchKeysAndTenantsPagedAsync/FetchManyByKeysAsync/ProduceAsync accepts one).
      while (!ct.IsCancellationRequested)
      {
          var page = (await entities.FetchKeysAndTenantsPagedAsync(
              tableSchema, afterKey, BatchSize, EntityAccess.CrossTenantMaintenance)).ToList();
          if (page.Count == 0) break;

          var rows = (await entities.FetchManyByKeysAsync(
              tableSchema, page.Select(p => p.Key).ToList(), EntityAccess.CrossTenantMaintenance)).ToList();

          foreach (var row in rows)
          {
              await events.ProduceAsync(
                  EntityTopics.Events,
                  row.Key,
                  new EntityEvent(
                      EntityEventType.Updated,
                      typeName,
                      row.Key,
                      row.Data,
                      traceId,
                      "1",
                      DateTimeOffset.UtcNow,
                      targetStores));
              count++;
          }

          afterKey = page[^1].Key;
      }

      logger.LogInformation("[Reconcile] Re-projected {Count} {Type} records to Kafka", count, typeName.SanitizeForLog());
      return count;
  }
  ```
  Remove the now-dead private `ExtractKey(string, string)` helper (verified: its only caller was the loop just replaced).

- [ ] **Step 2: Pass a real cancellation token at the call site**
  In `Program.cs`'s `/admin/reconcile/{typeName}` handler, change:
  ```csharp
  var count = await reconciliation.ReconcileTypeAsync(typeName);
  ```
  to:
  ```csharp
  var count = await reconciliation.ReconcileTypeAsync(typeName, httpContext.RequestAborted);
  ```

- [ ] **Step 3: Update the 2 tests that exercise the paginated path**
  In `ReconciliationServiceTests.cs`, update `ReconcileTypeAsync_ReadsUnderTheCrossTenantMaintenanceRole` and `ReconcileTypeAsync_RepublishesEveryRow_ReturnsCount` to stub `FetchKeysAndTenantsPagedAsync`/`FetchManyByKeysAsync` instead of `FetchAllAsync`, matching `KeyedTenantRow`/`KeyedRow`'s real shapes:
  ```csharp
  [Fact]
  public async Task ReconcileTypeAsync_ReadsUnderTheCrossTenantMaintenanceRole()
  {
      await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
      _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<EntityAccess>())
          .Returns(Array.Empty<KeyedTenantRow>());

      await _sut.ReconcileTypeAsync("Author");

      await _entities.Received(1).FetchKeysAndTenantsPagedAsync(
          Arg.Any<TableSchema>(), Arg.Any<string?>(), Arg.Any<int>(), EntityAccess.CrossTenantMaintenance);
  }

  [Fact]
  public async Task ReconcileTypeAsync_RepublishesEveryRow_ReturnsCount()
  {
      await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
      var page = new[]
      {
          new KeyedTenantRow("11111111-1111-1111-1111-111111111111", "tenant-a"),
          new KeyedTenantRow("22222222-2222-2222-2222-222222222222", "tenant-b")
      };
      _entities.FetchKeysAndTenantsPagedAsync(Arg.Is<TableSchema>(s => s.TableName == "authors"), null, Arg.Any<int>(), Arg.Any<EntityAccess>())
          .Returns(page);
      _entities.FetchKeysAndTenantsPagedAsync(Arg.Is<TableSchema>(s => s.TableName == "authors"), Arg.Is<string?>(k => k == "22222222-2222-2222-2222-222222222222"), Arg.Any<int>(), Arg.Any<EntityAccess>())
          .Returns(Array.Empty<KeyedTenantRow>());
      _entities.FetchManyByKeysAsync(Arg.Is<TableSchema>(s => s.TableName == "authors"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<EntityAccess>())
          .Returns(new[]
          {
              new KeyedRow("11111111-1111-1111-1111-111111111111", """{"Id":"11111111-1111-1111-1111-111111111111","Name":"Alice"}"""),
              new KeyedRow("22222222-2222-2222-2222-222222222222", """{"Id":"22222222-2222-2222-2222-222222222222","Name":"Bob"}""")
          });

      var count = await _sut.ReconcileTypeAsync("Author");

      count.Should().Be(2);
      await _events.Received(2).ProduceAsync(
          EntityTopics.Events,
          Arg.Any<string>(),
          Arg.Is<EntityEvent>(e => e.TypeName == "Author" && e.EventType == EntityEventType.Updated));
  }
  ```
  `ReconcileTypeAsync_UnknownType_ReturnsNull` needs no change (verified — assumption #11 above).

- [ ] **Step 4: Run the tests**
  ```bash
  cd Iverson.Server && dotnet test --filter "Category!=Integration&FullyQualifiedName~ReconciliationServiceTests"
  ```

- [ ] **Step 5: Commit**
  ```bash
  git add Iverson.Server/Iverson.Api/Reconciliation/ReconciliationService.cs \
          Iverson.Server/Iverson.Api/Program.cs \
          Iverson.Server/Iverson.Api.Tests/Reconciliation/ReconciliationServiceTests.cs
  git commit -m "paginate /admin/reconcile's row sweep instead of loading the whole table into memory"
  ```

---

### Task 8: `AddQdrant` empty-key validation

**Files:**
- Modify: `Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs`
- Test: `Iverson.Server/Iverson.Vector.Tests/ServiceCollectionExtensionsTests.cs`

- [ ] **Step 1: Strengthen the guard**
  ```csharp
  // before
  if (apiKey is null)
  {
      throw new ArgumentException(
          "Qdrant:ApiKey is required (used both as the admin API key and the JWT signing secret)",
          nameof(apiKey));
  }
  // after
  if (string.IsNullOrWhiteSpace(apiKey))
  {
      throw new ArgumentException(
          "Qdrant:ApiKey is required (used both as the admin API key and the JWT signing secret)",
          nameof(apiKey));
  }
  if (System.Text.Encoding.UTF8.GetByteCount(apiKey) < 32)
  {
      throw new ArgumentException(
          "Qdrant:ApiKey must be at least 32 bytes (used as an HMAC-SHA256 signing key)",
          nameof(apiKey));
  }
  ```

- [ ] **Step 2: Update the 3 existing test literals**
  In `ServiceCollectionExtensionsTests.cs`, replace all 3 occurrences of `apiKey: "test-api-key"` (12 bytes) with `apiKey: "test-signing-key-0123456789abcdef"` (33 bytes, matching this test tree's established convention).

- [ ] **Step 3: Run the tests**
  ```bash
  cd Iverson.Server && dotnet test --filter "Category!=Integration&FullyQualifiedName~ServiceCollectionExtensionsTests"
  ```

- [ ] **Step 4: Commit**
  ```bash
  git add Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs \
          Iverson.Server/Iverson.Vector.Tests/ServiceCollectionExtensionsTests.cs
  git commit -m "reject an empty or too-short Qdrant API key at startup instead of at first use"
  ```

---

## Known issues inherited from spec

- **NetworkPolicy narrowing for `:8081`** (per your choice) — the `from: []` rule stays broad; only the code-level listener-port partitioning is in scope for this plan. Revisit if a cluster-profile-portable node-CIDR source becomes available.
- **Python SDK/Agents lockfile and hash-pinning adoption** (per your choice) — out of scope for this plan. The build-backend line is corrected; introducing `pip-compile`/hash pinning for either Python tree is a separate tooling decision for later.
- **The addressability-path name-existence oracle** (per your choice, following this design's CDR round 1) — `ObjectRetrievalGrpcService`'s two sites already return the same `Found = false` for a foreign tenant's registered type and for an unregistered one alike (RLS makes them indistinguishable), but `ObjectPersistenceGrpcService.RequireSchema` and `ObjectMappingGrpcService.RequireSchema` throw `FailedPrecondition` only for a genuinely unregistered type — so a caller can still probe which type names exist platform-wide via those two. This plan creates no new oracle and closes none of this pre-existing one; it's left open because the only mechanism available (`OwnerTenantId` as a last-writer-wins gate) would break real tenant data access once two tenants share a type name, which happens routinely on restart. If this residual needs closing later, the CSR report's own alternative — an explicit `IsShared` flag, defaulted to `false` and requiring `SchemaAdmin` to opt a type in — avoids the re-registration hazard entirely.
