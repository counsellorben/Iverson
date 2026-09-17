# CSR Round 9 Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-16-csr-round9-remediation-design.md` (commit SHA: `d84d39e0c27997130622939f20110791b49dd402`)

**Goal:** Fix CSR round 9's Findings #1, #2, #4, and the Postgres half of #3; mitigate Finding #5's 1-RPC oracle (accepting a 2-RPC residual); update the cross-language conformance matrix and its normative standard for the behavioral consequence of the Finding #5 fix.

**Architecture:** Six independently-committable changes across the .NET 10 gRPC server (`Iverson.Server/Iverson.Api`), its unit test suite, two Helm chart templates, and the client-conformance harness (production scenario code, its tests, and the normative markdown standard it grades against). Tasks 1-4 are each self-contained. Task 5 (the server-side authorization narrowing) and Task 6 (its conformance-matrix consequence) are ordered sequentially — Task 6 documents and re-grades the behavioral change Task 5 makes, and Task 6's `Requirements.cs` edit must land before its own `IdentityScenario.cs`/`IdentityScenarioTests.cs` edits reference the new consts, so within Task 6 file order matters.

**Tech stack:** .NET 10, ASP.NET Core (Kestrel, minimal hosting), Npgsql 10.0.3, xUnit + FluentAssertions + NSubstitute, Helm 3 (CloudNativePG-managed Postgres).

---

## File Structure

- **Modify** — `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` — write-side ownership check (Task 1)
- **Modify** — `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs` — 2 new tests (Task 1)
- **Modify** — `Iverson.Server/Iverson.Api/Program.cs` — pre-auth rate limiter + listener-partition markers (Tasks 2, 4)
- **Modify** — `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml` — Postgres TLS volume/connection string (Task 3)
- **Modify** — `Iverson.Server/deploy/helm/iverson/charts/worker/templates/deployment.yaml` — identical Postgres TLS change (Task 3)
- **Modify** — `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs` — narrowed `Update` read + RLS-collision catch (Task 5)
- **Modify** — `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` — same, `MappingResponse` shape (Task 5)
- **Modify** — `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs` — rewrite `Update_TenantMismatch_…` (Task 5)
- **Modify** — `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` — rewrite `Update_TenantMismatch_…` (Task 5)
- **Modify** — `Iverson.Server/Iverson.ClientConformance/Requirements.cs` — retire `IdnTenancyDerivedAndEnforced`, author two successors (Task 6)
- **Modify** — `Iverson.Server/Iverson.ClientConformance/Scenarios/IdentityScenario.cs` — guarded predicate, rewritten details, Backstop rewrite, repointed crefs (Task 6)
- **Modify** — `Iverson.Server/Iverson.ClientConformance.Tests/IdentityScenarioTests.cs` — inverted/renamed/repointed tests (Task 6)
- **Modify** — `docs/standards/iverson-client-standard.md` — retire/author IDs, Coverage split, Deferred rows, Backstop rewrite, count (Task 6)

## Inherited from spec

The following were verified by `thorough-brainstorming` at spec-write time and are trusted as ground truth (full text and evidence in the spec's own `Verified assumptions` table, rows 1-23):

- The ownership comparison must source from `registry.Get(typeDesc.TypeName)`, not `priorDescriptor` (row 1).
- No existing test registers the same type under two different non-null owners (row 2); the conformance/loadtest drivers use one tenant identity per run (row 3); the only cross-tenant-token test scenario exercises `Update`, not `RegisterSchema` (row 4); the thrown `RpcException` isn't caught before the gRPC caller (row 5).
- `app.UseRateLimiter(RateLimiterOptions)` and DI-configured `app.UseRateLimiter()` coexist without conflict; `GetEndpoint()` and `RemoteIpAddress` are both safely readable pre-auth (rows 6-8).
- Npgsql 10.0.3 accepts `SSL Mode=VerifyFull;Root Certificate=<path>` (row 9); CNPG's default CA-secret naming applies with no `certificates:` override, live-gated at merge time (row 10); no volume/mount collision at `/etc/postgres-tls` in either chart (row 11).
- `:8081` cannot reach the four unmarked gRPC services today regardless of the new markers (row 12); no existing test asserts they're unmarked (row 13).
- `EnforceWriteAuthorization`'s `TenantMismatch` branch has exactly 2 real call sites (row 14); the existing `Update_TenantMismatch_…` tests assert only `ThrowAsync<RpcException>()`, no status code (row 15) — this is what makes Task 5's test rewrite a behavior change, not merely an assertion-tightening.
- `Get`/`Delete` don't share Finding #5's oracle (row 16); the entity tables' key column is a plain non-composite `PRIMARY KEY` (row 17); the exact upsert SQL is the `ON CONFLICT ("Key") DO UPDATE` form (row 18); narrowing to `ForTenant` produces `SQLSTATE 42501: new row violates row-level security policy` against a real Postgres 16 + RLS container, not a silent no-op (row 19); `42501` is also used for genuine permission errors, so the catch also checks the message text (row 20).
- The enforcement predicate's operand `code` (`IdentityScenario.cs:458`) folds "no denied step", "attempt threw", and "malformed code" into the same `null` as genuine acceptance — the predicate must guard on `deniedStep is { Ok: true }` (row 21); `DeniedStatusCode` has exactly 10 references, 4 in-scenario + 6 in-test, with `:614`/`:652` bypassing `JudgeHappy` (row 22); the Backstop's "strictly weaker" claim is false against `IVC-IDN-007` in the no-seeded-row state (row 23).

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time (this session, against the repo at commit `d84d39e0c27997130622939f20110791b49dd402`, working tree otherwise clean):

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path / ordering | The ownership check inserts between `SchemaRegistrationOrchestrator.cs:76` (end of the `ValidateIdentifier` loop) and `:78` (`var declared = DeclaredModel(typeDesc);`) | Read `SchemaRegistrationOrchestrator.cs:56-105` directly; `:100-102`'s `priorDescriptor` lookup (the one the fix must NOT reuse) starts at `:100`, well after the insertion point |
| 2 | Function signature | `SchemaRegistry.Get(string)` returns `SchemaDescriptor?` from the persisted `_schemas` map only | Read `SchemaRegistry.cs:24-25` |
| 3 | Consumer impact | No other call site of `SchemaRegistrationOrchestrator.RegisterAsync` passes a non-null `ownerTenantId` today | `grep -rn "OwnerTenantId"` — one definition, one write site (`:346`, phase 3), one read site (`ObjectMappingGrpcService.cs:85`, unrelated to this fix) |
| 4 | Task ordering | Task 1's two new tests need no fixture beyond what `SchemaRegistrationOrchestratorTests.cs` already provides (`SimpleType`, `_sut`, `_registry`) | Read the file's constructor (`:24-42`) and an existing structurally-identical test (`RegisterAsync_WithADependentDeclaringATenantField_ThrowsInvalidArgument`, `:373-394`) — confirms `SchemaRequest.Dependents` supports `{ }` collection-initializer syntax and the `ThrowAsync<RpcException>()` / `ex.Which.StatusCode` / `ex.Which.Status.Detail.Should().Contain(...)` idiom this plan's new tests reuse |
| 5 | Code validity | `Program.cs` does not currently import `Microsoft.AspNetCore.RateLimiting` (where `RateLimiterOptions` lives) via any explicit `using` or the Web SDK's implicit global usings | Read `Program.cs:1-20`; read the SDK-generated `obj/Debug/net10.0/Iverson.Api.GlobalUsings.g.cs` — only `Microsoft.AspNetCore.{Builder,Hosting,Http,Routing}` are global, not `.RateLimiting` |
| 6 | Function signature | `RateLimiterOptions` has a public parameterless constructor and public settable `GlobalLimiter`/`RejectionStatusCode` properties; `AddPolicy` is callable on it with the spec's exact syntax | Reflected over `Microsoft.AspNetCore.RateLimiting.RateLimiterOptions` from the real `Npgsql`-adjacent .NET 10 shared framework in a throwaway console app; separately compiled and ran the spec's exact `new RateLimiterOptions{...}; opts.AddPolicy("traces", _ => RateLimitPartition.GetNoLimiter<string>("unlimited"));` snippet — compiled and ran clean |
| 7 | File path | `Program.cs`'s pipeline is `:397 app.Use(ListenerPortGateAsync)` → `:399 UseHttpsRedirection` → `:401 UseAuthentication` → `:402 UseAuthorization` → `:403 UseRateLimiter()` → `:404 UseGrpcWeb`; the four unmarked `MapGrpcService` calls are at `:607-610` | `grep -n` for each literal call in `Program.cs`, this session |
| 8 | File path | Both Helm chart connection strings are single-line YAML scalars at `charts/api/templates/deployment.yaml:84` and `charts/worker/templates/deployment.yaml` (equivalent offset); the `qdrant-tls` volume/mount precedent (the shape to mirror) is at `charts/api/templates/deployment.yaml:200-220` | Read both files directly this session |
| 9 | Command | `helm template <release> . -f values-laptop.yaml` renders both chart templates without error against the current (pre-fix) connection string, from the `Iverson.Server/deploy/helm/iverson` chart root, after `helm dependency build` | Ran both commands for real this session; the pre-fix `ConnectionStrings__Postgres` line rendered as expected in the output |
| 10 | Function signature | `EntityAccess.ForTenant(string?)` is the correct replacement call, and its operand should be `actingUserAccessor.ActingUser?.FindFirst("tenant_id")?.Value` (the claim directly) — NOT `decision.TenantValue`, which in both `Update` methods isn't computed until `authEvaluator.Evaluate(...)` runs *after* the read this fix narrows | Read `IRecordStoreRoles.cs:130-158` for `EntityAccess`'s shape; read both `Update` methods in full (`ObjectPersistenceGrpcService.cs:93-163`, `ObjectMappingGrpcService.cs:354-425`) — `decision` is assigned at `:132`/`:386`, after the read at `:109`/`:370`; the sibling `Get`/`Delete` call sites in the same files (`ObjectMappingGrpcService.cs:261,435`) already use the direct-claim form, confirming it's the established pattern, not a new one |
| 11 | Function signature | `Npgsql.PostgresException` has a public constructor `(string messageText, string severity, string invariantSeverity, string sqlState)`, and constructing one with `sqlState: "42501"` and a message containing "row-level security policy" round-trips through `.SqlState`/`.MessageText` correctly | Reflected the real `Npgsql 10.0.3` assembly's `PostgresException` type in a throwaway console app; constructed one via the 4-arg public constructor and printed `.SqlState`/`.MessageText`/`.Severity` back — matched what was passed |
| 12 | Consumer impact | Neither `ObjectPersistenceGrpcService.cs` nor `ObjectMappingGrpcService.cs` currently has `using Npgsql;`, so both need it added; the codebase's established convention for catching `PostgresException` is a bare `using Npgsql;` + unqualified `catch (PostgresException ex) when (...)`, not a fully-qualified name | `grep -n "using Npgsql"` on both files (zero hits); `grep -n "using Npgsql;" \| "PostgresException"` on `Iverson.Sql/PostgresRepository.cs` and `PostgresSchemaManager.cs` (both use the bare form) |
| 13 | Consumer impact | Both `Update_TenantMismatch_LogsAuditDeniedWithTenantMismatch` tests construct their SUT with a *real* `OutboxWriter` (not a mocked `IOutboxWriter`), wired to a mocked `IRecordStoreQueryExecutor`/`IRecordStoreTransactionRunner` — so simulating the RLS-violation-on-write requires making the mocked `_txRunner.ExecuteInTransactionAsync(...)` throw, not configuring a mock outbox writer | Read both test classes' constructors (`ObjectPersistenceGrpcServiceTests.cs:34-71`, `ObjectMappingGrpcServiceTests.cs:44-` +) — both pass `new OutboxWriter(ReconciliationSchema.TableName, _sql, _txRunner)` as the concrete `outboxWriter` parameter |
| 14 | Consumer impact | `_entities.FetchByKeyAsync(...)` already defaults to returning `null` in both test constructors (an existing default predating this plan, per an existing comment referencing an unrelated prior "Task 6"), so the rewritten `Update_TenantMismatch_…` tests need no override for the read — only the `_txRunner` reconfiguration and the assertion rewrite | Read `ObjectPersistenceGrpcServiceTests.cs:40-46` and `ObjectMappingGrpcServiceTests.cs:59-63` — both already `.Returns((string?)null)` by default |
| 15 | Consumer impact (Category 6, touched-function) | The enforcement assertion's `Name` string changes from "...is denied a write to this row" to "...is answered without a gRPC error status" (matching the new Statement). `IdentityScenarioTests.cs`'s `Named(assertions, fragment)` helper is `.Single(a => a.Name.Contains(fragment, StringComparison.Ordinal))` — an exact-match-required, throw-on-zero-or-many lookup. 8 call sites pass the fragment `"denied a write"`: `:375,376,382,389,399,407,429,438`. All 8 must have their fragment updated in the same change or they throw `InvalidOperationException`, not a clean assertion failure | Read the `Named` helper (`:68-69`); `grep -n '"denied a write"'` across the test file — exactly 8 hits, all listed above; confirmed no other assertion Name in the file would ambiguously match a renamed fragment |
| 16 | Consumer impact (sibling sweep) | Every reference to the retired `Requirements.IdnTenancyDerivedAndEnforced` const, code-level or in prose comments, across the whole non-test and test tree | `grep -rn "IdnTenancyDerivedAndEnforced"` (repo-wide, non-worktree): 10 sites — `Requirements.cs:494,544,563`; `IdentityScenario.cs:134,480,567,580`; `IdentityScenarioTests.cs:261,619,629`. Separately, broad (non-quote-anchored) `grep -n "IVC-IDN-003"` on `IdentityScenarioTests.cs` alone returns 12 sites, including 5 comment-only mentions (`:176,195,229,237,263,622` — note `:229` contains two mentions) that the const-name grep above does not surface, because they reference the ID string in prose, not the C# symbol |
| 17 | Consumer impact | Of the 12 `IdentityScenarioTests.cs` sites above, exactly which repoint to the new derivation successor vs. the new response-shape successor vs. need no ID change (comment-only, still accurate) | Read every site in context this session: `:192,195,209` (derivation, `JudgeTenantDerivation_*` tests) → derivation successor; `:229,237` (comment prose contrasting IDN-005 from IDN-003's derivation half) → derivation successor; `:263` (comment explaining the 3-count baseline) and `:261` (the `Count(...).Should().Be(3)` code) → splits 2 (derivation) + 1 (response-shape), per the spec's own item 3; `:300` (`JudgeTenantDerivation`'s second assertion) → derivation successor; `:345` (`JudgeTenantDerivation_ProbeThrew_…`, calls `JudgeTenantDerivation` directly, both its 2 citations are derivation) → derivation successor, count stays 2; `:376` (the enforcement assertion, will also need its `Named` fragment updated per row 15) → response-shape successor; `:453` (`Cited(assertions, "IVC-IDN-003")` in the empty-document test, which exercises both halves) → becomes two `Cited` checks, one per successor; `:619,622,628-629` (`GradeReads_…` wiring test, grades the derivation half specifically per its own comment) → derivation successor; `:176` (section header comment) → update to name both new IDs |
| 18 | Command / consumer impact | `IdentityScenarioTests.cs:614,652` construct `DeniedStep(IdentityScenario.DeniedStatusCode)` inline (bypassing the shared `JudgeHappy` fixture default at `:62`), and neither test asserts on the enforcement predicate's own polarity — `:614` grades cell/detail wiring, `:652` grades a different language's cell status — so both should pass a literal `7` in place of the deleted const, per `IVC-IDN-003`'s retired-but-still-real numeric value | Read both tests in full (`:600-634`, `:648-656`) this session; confirmed neither asserts `Passed` on the enforcement cell |
| 19 | Command | `docs/standards/iverson-client-standard.md`'s IDN table (`:393-397`), Coverage ledger (`:560-564`), the two Deferred rows this fix falsifies (`:569` IDN axis, `:962` ERR axis), the Backstop section (`:574-584`), Global Constraint 3 (`:166-169`), and the "45 Active requirements" count sentence (`:77-78`) are all at the line numbers cited, unchanged since `d84d39e` (confirmed no codebase/standard drift between rounds 6 and 7's HEAD and now) | Read every cited region directly this session; `git diff --stat` between the spec's last two commits confirmed `docs/standards` and `Iverson.Server` are byte-identical across that range (carried over from CDR round 7's own drift check, independently re-confirmed by this session's direct reads) |
| 20 | Command | `dotnet build Iverson.slnx` then `dotnet test Iverson.slnx --filter "Category!=Integration"`, run from the repo root, is the established build/test invocation | Read `.github/workflows/dotnet-build.yml:19-20` — this is CI's own command, not an invented one |
| 21 | Command | Git commit messages in this repo are plain, lowercase-first-word, imperative, with no type-prefix scheme (`feat:`, `fix:`, etc.) | `git log --oneline -15` — every recent message ("add TS SDK...", "bump vite to...", "clarify why...", "applied N fixes...") matches this shape; none carries a prefix |
| 22 | Consumer impact / naming | The two new const names, `IdnTenancyDerivedFromActingUser` (IVC-IDN-006) and `IdnCrossTenantUpdateAnsweredWithoutError` (IVC-IDN-007), don't collide with any existing identifier | `grep -n "public const string Idn"` in `Requirements.cs` — existing names are `IdnDualIdentityAcceptedOnWrite`, `IdnActingUserPropagatedToRow`, `IdnTenancyDerivedAndEnforced` (being retired), `IdnServerTenantColumnAbsentFromPointRead`; no collision |
| 23 | Consumer impact (touched-function, Task 6's Deferred-row rewrites) | A header-less caller (no acting-user token) is denied by an EARLIER check than the one Task 5 narrows, and is therefore untouched by Task 5 — while a wrong-tenant caller (valid token, different tenant) is not caught by that earlier check and is the one now silently swallowed. An initial draft of this plan assumed both were now swallowed identically; that assumption was wrong and is corrected in Task 6, Step 4 | Read `RowFieldAuthorizationEvaluator.Evaluate` (`:13-14`): `if (actingUser is null) return new AuthorizationDecision(true, false, ...)` — `Denied = true` for a null acting user. Read `AuthorizationFieldMasking.EnforceWriteAuthorization` (`:82-87`): the `decision.Denied` check and its `AccessDenied` throw run BEFORE the `existingRowJson is null` branch (`:89`) that Task 5 narrows — so a header-less caller never reaches the branch this fix changes at all |

---

## Tasks

### Task 1: Schema `OwnerTenantId` write-side ownership check

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:76-78`
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`

- [ ] **Step 1: Add the ownership check**

  In `SchemaRegistrationOrchestrator.cs`, between the existing `ValidateIdentifier` loop (ending `:76`) and `var declared = DeclaredModel(typeDesc);` (`:78`), insert:

  ```csharp
  var priorRegistered = registry.Get(typeDesc.TypeName);
  if (priorRegistered is not null && priorRegistered.OwnerTenantId != ownerTenantId)
      throw new RpcException(new Status(StatusCode.PermissionDenied,
          $"Type '{typeDesc.TypeName}' is registered to another tenant and cannot be re-registered here."));
  ```

  This deliberately sources from `registry.Get` directly, not the `priorDescriptor` lookup two lines below (which is `batchDescriptors`-first and would false-positive on a same-request duplicate type name, per Inherited row 1).

- [ ] **Step 2: Add the two new tests**

  In `SchemaRegistrationOrchestratorTests.cs`, add (matching the existing `ex.Which.StatusCode` / `ex.Which.Status.Detail.Should().Contain(...)` idiom used throughout the file):

  ```csharp
  [Fact]
  public async Task RegisterAsync_TypeAlreadyOwnedByAnotherTenant_ThrowsPermissionDenied()
  {
      await _sut.RegisterAsync(new SchemaRequest { RootType = SimpleType("Widget", "Name") },
          "tenant-a", CancellationToken.None);

      var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = SimpleType("Widget", "Name") },
          "tenant-b", CancellationToken.None);

      var ex = await act.Should().ThrowAsync<RpcException>();
      ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
      ex.Which.Status.Detail.Should().Contain("'Widget'");
      ex.Which.Status.Detail.Should().Contain("registered to another tenant");
  }

  [Fact]
  public async Task RegisterAsync_SameRequestNamesTheSameTypeTwiceUnderOneTenant_RegistersSuccessfully()
  {
      var request = new SchemaRequest
      {
          RootType = SimpleType("Widget", "Name"),
          Dependents = { SimpleType("Widget", "Name") }
      };

      var act = () => _sut.RegisterAsync(request, "tenant-a", CancellationToken.None);

      await act.Should().NotThrowAsync();
  }
  ```

  The second test is a regression guard for exactly the `batchDescriptors`-vs-`registry` ordering bug Step 1's fix avoids: if the check were ever changed to read `priorDescriptor` instead of `registry.Get`, this test would start failing (the in-batch descriptor's `OwnerTenantId` is always `null`, `null != "tenant-a"` is true, and the same-request duplicate would be wrongly rejected).

- [ ] **Step 3: Build, test, commit**
  ```bash
  dotnet build Iverson.slnx
  dotnet test Iverson.slnx --filter "Category!=Integration"
  git add Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs
  git commit -m "reject cross-tenant schema re-registration (CSR round 9 Finding #1)"
  ```

---

### Task 2: Pre-authentication rate limiting

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Program.cs:1-20` (using directive), `:397-404` (pipeline)

- [ ] **Step 1: Add the using directive**

  Add `using Microsoft.AspNetCore.RateLimiting;` to `Program.cs`'s using block (needed for `RateLimiterOptions`, not covered by the Web SDK's implicit global usings).

- [ ] **Step 2: Insert the pre-auth limiter and reorder the pipeline**

  Replace `Program.cs:397-404`:
  ```csharp
  app.Use(ListenerPortGateAsync);

  app.UseHttpsRedirection();

  app.UseAuthentication();
  app.UseAuthorization();
  app.UseRateLimiter();
  app.UseGrpcWeb();
  ```
  with:
  ```csharp
  var preAuthOptions = new RateLimiterOptions
  {
      RejectionStatusCode = StatusCodes.Status429TooManyRequests
  };
  preAuthOptions.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
  {
      var isHealthListenerEndpoint = ctx.GetEndpoint()?.Metadata.GetMetadata<RequireListenerPort>()?.Port == 8081;
      if (isHealthListenerEndpoint)
          return RateLimitPartition.GetNoLimiter("unlimited");

      return RateLimitPartition.GetSlidingWindowLimiter(
          ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
          _ => new SlidingWindowRateLimiterOptions
          {
              PermitLimit = 6_000,
              Window = TimeSpan.FromMinutes(1),
              SegmentsPerWindow = 6,
              QueueLimit = 0
          });
  });
  // The existing "traces" named policy (registered only on the DI-configured post-auth options
  // below) must also exist on this instance — RateLimiterOptions' policy map is per-instance, and
  // /v1/traces carries .RequireRateLimiting("traces"); without this, every request to that endpoint
  // throws InvalidOperationException before ever reaching the endpoint. No-op here (not a clone of
  // the real per-sub policy, which has no "sub" claim to key on this early): the real 60/min budget
  // stays enforced by the unchanged post-auth limiter below.
  preAuthOptions.AddPolicy("traces", _ => RateLimitPartition.GetNoLimiter<string>("unlimited"));

  app.Use(ListenerPortGateAsync);
  app.UseRateLimiter(preAuthOptions);
  app.UseHttpsRedirection();
  app.UseAuthentication();
  app.UseAuthorization();
  app.UseRateLimiter();   // existing DI-configured GlobalLimiter, unchanged
  app.UseGrpcWeb();
  ```

  `RequireListenerPort` (referenced in the new limiter callback) is declared later in this same file (`:681`) as a nested type used across the file — no additional using or reference needed.

- [ ] **Step 2: Build, test, commit**
  ```bash
  dotnet build Iverson.slnx
  dotnet test Iverson.slnx --filter "Category!=Integration"
  git add Iverson.Server/Iverson.Api/Program.cs
  git commit -m "add pre-authentication rate limiting (CSR round 9 Finding #2)"
  ```

---

### Task 3: Postgres connection TLS certificate verification

**Files:**
- Modify: `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml:79-84,200-220`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/worker/templates/deployment.yaml` (equivalent regions)

**Interfaces:**
- Produces: the `/etc/postgres-tls/ca.crt` mount and `VerifyFull` connection string both charts now share — nothing downstream in this plan consumes it, but the pre-merge verification gate below depends on it existing correctly in both.

- [ ] **Step 1: Add the volume and mount to both templates**

  In `charts/api/templates/deployment.yaml`, add to the existing `volumeMounts:` list (`:200-208`, alongside `kafka-ca`/`qdrant-tls`/`tmp`):
  ```yaml
      - name: postgres-tls
        mountPath: /etc/postgres-tls
        readOnly: true
  ```
  and to the existing `volumes:` list (`:209-220`):
  ```yaml
    - name: postgres-tls
      secret:
        secretName: {{ .Release.Name }}-postgres-ca
        items:
          - key: ca.crt
            path: ca.crt
  ```
  The `items:` projection mirrors the existing `qdrant-tls` volume's shape exactly (`:213-218`) — it exposes only the public `ca.crt`, not the CA's private key. Repeat identically in `charts/worker/templates/deployment.yaml`'s equivalent `volumeMounts:`/`volumes:` lists.

- [ ] **Step 2: Change the connection string in both templates**

  Replace, in both `charts/api/templates/deployment.yaml:84` and the worker chart's equivalent line:
  ```
  Host={{ .Release.Name }}-postgres-rw;Port=5432;Database=iverson;Username=iverson;Password=$(POSTGRES_APP_PASSWORD)
  ```
  with:
  ```
  Host={{ .Release.Name }}-postgres-rw;Port=5432;Database=iverson;Username=iverson;Password=$(POSTGRES_APP_PASSWORD);SSL Mode=VerifyFull;Root Certificate=/etc/postgres-tls/ca.crt
  ```

- [ ] **Step 3: Render-check both templates**
  ```bash
  cd Iverson.Server/deploy/helm/iverson
  helm dependency build
  helm lint .
  helm template csr9-check . -f values-laptop.yaml | grep -A2 "ConnectionStrings__Postgres"
  cd -
  ```
  Confirm the rendered connection string carries `SSL Mode=VerifyFull;Root Certificate=/etc/postgres-tls/ca.crt` and `helm lint` reports no errors.

- [ ] **Step 4: Record the pre-merge live-cluster verification gate (manual, not automatable here)**

  This fix must not merge to a real cluster until confirmed against it — there is no cluster in this session to verify against. Before merging to any environment, run:
  ```bash
  kubectl get secret <release>-postgres-ca -o jsonpath='{.data}'
  kubectl get secret <release>-postgres-ca -o jsonpath='{.data.ca\.crt}' | base64 -d > ca.crt
  kubectl get secret <release>-postgres-server -o jsonpath='{.data.tls\.crt}' | base64 -d > server.crt
  openssl x509 -in server.crt -noout -ext subjectAltName
  openssl verify -CAfile ca.crt -verify_hostname <release>-postgres-rw server.crt
  ```
  and confirm the SAN list contains `<release>-postgres-rw` and the verify command succeeds, for both the api and worker Deployments' target cluster, before either Deployment is allowed to come ready with this connection string.

- [ ] **Step 5: Commit**
  ```bash
  git add Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml Iverson.Server/deploy/helm/iverson/charts/worker/templates/deployment.yaml
  git commit -m "verify Postgres connection TLS via VerifyFull (CSR round 9 Finding #3, Postgres half)"
  ```

---

### Task 4: Complete the listener partition

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Program.cs:607-610`

- [ ] **Step 1: Mark the four unmarked gRPC services**

  Replace:
  ```csharp
  app.MapGrpcService<ObjectMappingGrpcService>();
  app.MapGrpcService<ObjectPersistenceGrpcService>();
  app.MapGrpcService<ObjectRetrievalGrpcService>();
  app.MapGrpcService<ObjectSearchGrpcService>();
  ```
  with:
  ```csharp
  app.MapGrpcService<ObjectMappingGrpcService>().WithMetadata(new RequireListenerPort(8080));
  app.MapGrpcService<ObjectPersistenceGrpcService>().WithMetadata(new RequireListenerPort(8080));
  app.MapGrpcService<ObjectRetrievalGrpcService>().WithMetadata(new RequireListenerPort(8080));
  app.MapGrpcService<ObjectSearchGrpcService>().WithMetadata(new RequireListenerPort(8080));
  ```

- [ ] **Step 2: Build, test, commit**
  ```bash
  dotnet build Iverson.slnx
  dotnet test Iverson.slnx --filter "Category!=Integration"
  git add Iverson.Server/Iverson.Api/Program.cs
  git commit -m "mark the 4 core gRPC services with their listener-port requirement (CSR round 9 Finding #4)"
  ```

---

### Task 5: Narrow the cross-tenant `Update` collision (server fix)

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs:1-16` (using), `:104-163` (Update)
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs:1-14` (using), `:367-424` (Update)
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs` (`Update_TenantMismatch_…`)
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` (`Update_TenantMismatch_…`)

**Interfaces:**
- Produces: the swallowed-success response shape (`Success = true`, no error) and the `"BlockedCrossTenantWrite"` audit reason — both of which Task 6's conformance-matrix update assumes without re-deriving.

- [ ] **Step 1: `ObjectPersistenceGrpcService.cs` — narrow the read, catch the RLS collision**

  Add `using Npgsql;` to the file's using block.

  Replace `:109-110`:
  ```csharp
  var existingRowJson = await entities.FetchByKeyAsync(
      SchemaBuilder.ToTableSchema(schema), key, EntityAccess.CrossTenantMaintenance);
  ```
  with:
  ```csharp
  var existingRowJson = await entities.FetchByKeyAsync(
      SchemaBuilder.ToTableSchema(schema), key,
      EntityAccess.ForTenant(actingUserAccessor.ActingUser?.FindFirst("tenant_id")?.Value));
  ```

  Replace `:133-138`:
  ```csharp
  var decision = authEvaluator.Evaluate(schema, actingUserAccessor.ActingUser, AuthorizationAction.Write);
  var outboxRowId = await outboxWriter.UpsertAndEnqueueOutboxAsync(
      SchemaBuilder.ToTableSchema(schema),
      request.TypeName,
      key,
      payloadJson,
      tenantId: decision.TenantValue);
  ```
  with:
  ```csharp
  var decision = authEvaluator.Evaluate(schema, actingUserAccessor.ActingUser, AuthorizationAction.Write);
  Guid outboxRowId;
  try
  {
      outboxRowId = await outboxWriter.UpsertAndEnqueueOutboxAsync(
          SchemaBuilder.ToTableSchema(schema),
          request.TypeName,
          key,
          payloadJson,
          tenantId: decision.TenantValue);
  }
  catch (PostgresException ex) when (ex.SqlState == "42501" && ex.MessageText.Contains("row-level security policy"))
  {
      auditLog.Denied(actingUserAccessor.ActingUser, "Update", schema.TypeName, key, "BlockedCrossTenantWrite");
      return new PersistResponse { Success = true, Key = key, TraceId = request.TraceId };
  }
  ```
  The rest of `Update` (the opportunistic publish at `:140-155` and the final `return`) is unchanged — it runs only on the non-exceptional path, after the try/catch, and still consumes `outboxRowId` and `existingRowJson` exactly as before.

- [ ] **Step 2: `ObjectMappingGrpcService.cs` — same narrowing, `MappingResponse` shape**

  Add `using Npgsql;` to the file's using block.

  Replace `:370`:
  ```csharp
  var existingRowJson = await FetchByKeyAsync(schema, key, EntityAccess.CrossTenantMaintenance);
  ```
  with:
  ```csharp
  var existingRowJson = await FetchByKeyAsync(schema, key,
      EntityAccess.ForTenant(_actingUserAccessor.ActingUser?.FindFirst("tenant_id")?.Value));
  ```

  Replace `:386-389`:
  ```csharp
  var decision = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Write);
  var outboxRowId = await _outboxWriter.UpsertAndEnqueueOutboxAsync(
      SchemaBuilder.ToTableSchema(schema), request.TypeName, key, payloadJson,
      tenantId: decision.TenantValue);
  ```
  with:
  ```csharp
  var decision = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Write);
  Guid outboxRowId;
  try
  {
      outboxRowId = await _outboxWriter.UpsertAndEnqueueOutboxAsync(
          SchemaBuilder.ToTableSchema(schema), request.TypeName, key, payloadJson,
          tenantId: decision.TenantValue);
  }
  catch (PostgresException ex) when (ex.SqlState == "42501" && ex.MessageText.Contains("row-level security policy"))
  {
      _auditLog.Denied(_actingUserAccessor.ActingUser, "Update", schema.TypeName, key, "BlockedCrossTenantWrite");
      AuthorizationFieldMasking.RemoveTenantColumn(request.Payload);
      return new MappingResponse { Success = true, Data = request.Payload, TraceId = request.TraceId };
  }
  ```
  `RemoveTenantColumn` here is load-bearing: on this swallowed path, `EnforceWriteAuthorization` already took the create branch and force-set the tenant column into `request.Payload`; the genuine-success path at the bottom of `Update` (`:422`) strips it the same way, and omitting it here would leak that column back to the caller only on this path. The rest of `Update` (`:390-424`) is unchanged and runs only on the non-exceptional path.

- [ ] **Step 3: Rewrite `ObjectPersistenceGrpcServiceTests.cs`'s `Update_TenantMismatch_…` test**

  Replace the existing test:
  ```csharp
  [Fact]
  public async Task Update_TenantMismatch_LogsAuditDeniedWithTenantMismatch()
  {
      await _registry.RegisterAsync(OwnedAuthorSchema());
      var authorId = Guid.NewGuid().ToString();
      var crossTenantJson = $$"""{"Id":"{{authorId}}","Name":"Alice","OwnerId":"test-user","TenantId":"other-tenant"}""";
      _entities
          .FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
          .Returns(crossTenantJson);

      var payload = MakePayload(new()
      {
          ["Id"]      = Value.ForString(authorId),
          ["Name"]    = Value.ForString("Alice Updated"),
          ["OwnerId"] = Value.ForString("test-user")
      });
      var request = new PersistRequest { TypeName = "Author", Payload = payload };

      var act = async () => await _sut.Update(request, TestServerCallContext.Create());

      await act.Should().ThrowAsync<RpcException>();
      AssertAuditLogged("TenantMismatch");
  }
  ```
  with (the row is now genuinely invisible to `ForTenant`, so `_entities`'s existing default `null` return already applies — no override needed; the collision now surfaces from the write, not the read):
  ```csharp
  [Fact]
  public async Task Update_CrossTenantKeyCollidesOnUpsert_SwallowsAsSuccessAndLogsBlockedCrossTenantWrite()
  {
      await _registry.RegisterAsync(OwnedAuthorSchema());
      var authorId = Guid.NewGuid().ToString();
      _txRunner
          .ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>())
          .Returns<Task>(_ => throw new PostgresException(
              "new row violates row-level security policy for table \"authors\"",
              "ERROR", "ERROR", "42501"));

      var payload = MakePayload(new()
      {
          ["Id"]      = Value.ForString(authorId),
          ["Name"]    = Value.ForString("Alice Updated"),
          ["OwnerId"] = Value.ForString("test-user")
      });
      var request = new PersistRequest { TypeName = "Author", Payload = payload };

      var response = await _sut.Update(request, TestServerCallContext.Create());

      response.Success.Should().BeTrue();
      response.Key.Should().Be(authorId);
      AssertAuditLogged("BlockedCrossTenantWrite");
  }
  ```
  Add `using Npgsql;` to this test file's using block if not already present.

- [ ] **Step 4: Rewrite `ObjectMappingGrpcServiceTests.cs`'s `Update_TenantMismatch_…` test**

  Same transformation, mirrored for `MappingWriteRequest`/`MappingResponse`:
  ```csharp
  [Fact]
  public async Task Update_CrossTenantKeyCollidesOnUpsert_SwallowsAsSuccessAndLogsBlockedCrossTenantWrite()
  {
      await _registry.RegisterAsync(OwnedAuthorSchema());
      _txRunner
          .ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>())
          .Returns<Task>(_ => throw new PostgresException(
              "new row violates row-level security policy for table \"authors\"",
              "ERROR", "ERROR", "42501"));

      var payload = MakePayload(new()
      {
          ["Id"]      = Value.ForString(AuthorId),
          ["Name"]    = Value.ForString("Alice Updated"),
          ["OwnerId"] = Value.ForString("test-user")
      });

      var response = await _sut.Update(
          new MappingWriteRequest { TypeName = "Author", Payload = payload },
          TestServerCallContext.Create());

      response.Success.Should().BeTrue();
      AssertAuditLogged("BlockedCrossTenantWrite");
  }
  ```
  Add `using Npgsql;` to this test file's using block if not already present.

- [ ] **Step 5: Build, test, commit**
  ```bash
  dotnet build Iverson.slnx
  dotnet test Iverson.slnx --filter "Category!=Integration"
  git add Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs
  git commit -m "narrow the cross-tenant Update read to make a foreign row invisible rather than denied (CSR round 9 Finding #5)"
  ```

---

### Task 6: Update the conformance matrix for the narrowed `Update` fix

**Files:**
- Modify: `Iverson.Server/Iverson.ClientConformance/Requirements.cs:494-587`
- Modify: `Iverson.Server/Iverson.ClientConformance/Scenarios/IdentityScenario.cs:64,125-134,184,456-486,567,580`
- Modify: `Iverson.Server/Iverson.ClientConformance.Tests/IdentityScenarioTests.cs` (12 sites, listed in Verified assumption 17)
- Modify: `docs/standards/iverson-client-standard.md` (IDN table, Coverage, 2 Deferred rows, Backstop, count)

**Interfaces:**
- Consumes: Task 5's swallowed-success response shape and `"BlockedCrossTenantWrite"` audit reason (documented, not re-verified, by the Deferred row this task adds).

File order within this task matters: `Requirements.cs` first (defines the two new consts), then `IdentityScenario.cs` and `IdentityScenarioTests.cs` (reference them), then the standard (no compile dependency, but documents the same change).

- [ ] **Step 1: `Requirements.cs` — retire the old const, author two successors**

  Delete the doc comment and declaration at `:502-544` (the whole `IdnTenancyDerivedAndEnforced` block, including its "Enforcement" list item and "The conjoined control" paragraph — those describe the mechanism this fix removes). Replace with two consts, each with a doc comment in this file's existing style (see `IdnServerTenantColumnAbsentFromPointRead`, `:565-587`, for the "Supersedes the retired..." convention this mirrors):

  ```csharp
  /// <summary>
  /// The server derives a row's tenant from the acting-user identity rather than from the write
  /// payload — never from the payload itself. Discharged by <c>IdentityScenario.JudgeTenantDerivation</c>'s
  /// two assertions, graded ORCHESTRATOR-side because the row's real tenant lives in the
  /// server-owned <c>__TenantId</c> column, stripped from every outbound path and therefore
  /// unreachable from any client library by design: a <c>PostgresProbe</c> read of the row each
  /// driver seeded must show <c>__TenantId</c> carrying the acting user's own tenant, and the
  /// deliberately wrong value the driver stamped (<see cref="Scenarios.IdentityScenario.WrongTenantValue"/>)
  /// must still be sitting in the ORDINARY user column the driver declared for it, having not
  /// become the row's tenant.
  ///
  /// <para><b>Supersedes the retired IVC-IDN-003.</b> That Statement conjoined this derivation
  /// claim with an ENFORCEMENT claim ("...and denies an acting user of another tenant who attempts
  /// to write that row") that a later fix (mitigating CSR round 9's Finding #5) made false: the
  /// server no longer denies a cross-tenant write on the wire at all, by design, so no assertion
  /// discharges the enforcement half any more. Global Constraint 3 makes Statement cells immutable,
  /// so the correction is a retirement plus two successors, not an edit — this one restates only
  /// the still-true derivation half.</para>
  /// </summary>
  public const string IdnTenancyDerivedFromActingUser = "IVC-IDN-006";

  /// <summary>
  /// A mapped update attempted by an acting user of another tenant is answered without a gRPC
  /// error status, the same as an accepted one. Discharged by <c>IdentityScenario.Judge</c>'s
  /// enforcement assertion, over the numeric gRPC status code the driver reported from its
  /// <c>denied_update_wrong_acting_user</c> step — the harness observes only the numeric status
  /// code, never the response body, so this Statement is written at exactly that altitude and no
  /// wider.
  ///
  /// <para><b>Supersedes the retired IVC-IDN-003.</b> After CSR round 9's Finding #5 mitigation,
  /// the cross-tenant write is silently swallowed as a success rather than denied — this
  /// requirement states what the assertion that used to grade a DENIAL now actually observes: an
  /// acceptance-shaped response. The genuine enforcement gap this leaves (no client-observable
  /// assertion discharges cross-tenant write denial any more) is recorded as a Deferred area in the
  /// standard's IDN coverage ledger, not claimed here.</para>
  /// </summary>
  public const string IdnCrossTenantUpdateAnsweredWithoutError = "IVC-IDN-007";
  ```

- [ ] **Step 2: `IdentityScenario.cs` — guarded predicate, rewritten details, repointed crefs, Backstop rewrite**

  Replace the class doc comment's Backstop paragraph (`:125-134`):
  ```
  /// <para><b>Backstop assertion.</b> <see cref="Judge"/>'s "the write phase reported a row key for
  /// this language" assertion is this axis's backstop, in the sense
  /// <c>docs/standards/iverson-client-standard.md</c>'s REL authoring notes require. Without a
  /// seeded row the negative leg's update would take the create branch described above and SUCCEED,
  /// and a scenario whose denial never had anything to deny would render green. The backstop fires
  /// unconditionally, on every language, before and outside both the read-back and the denial
  /// assertions. It carries no requirement ID: no <c>IVC-IDN-*</c> statement owns "this language
  /// seeded a row" as such — that is a property of the harness's fixture, not of a client — and it is
  /// strictly weaker than <see cref="Requirements.IdnActingUserPropagatedToRow"/> and
  /// <see cref="Requirements.IdnTenancyDerivedAndEnforced"/> wherever either can fail.</para>
  ```
  with:
  ```
  /// <para><b>Backstop assertion.</b> <see cref="Judge"/>'s "the write phase reported a row key for
  /// this language" assertion is this axis's backstop, in the sense
  /// <c>docs/standards/iverson-client-standard.md</c>'s REL authoring notes require. Since CSR
  /// round 9's Finding #5 mitigation, EVERY cross-tenant update takes the create branch described
  /// above and SUCCEEDS — there is no longer a denial assertion for a missing row to defeat, which
  /// makes this backstop MORE load-bearing than before, not less: with no seeded row,
  /// <see cref="Requirements.IdnCrossTenantUpdateAnsweredWithoutError"/>'s "answered without a gRPC
  /// error status" assertion is satisfied vacuously, so "the write phase reported a row key for this
  /// language" is the only assertion left that separates a genuine swallowed cross-tenant write from
  /// a scenario that had nothing to update. The backstop fires unconditionally, on every language,
  /// before and outside both the read-back and the enforcement assertions. It carries no requirement
  /// ID: no <c>IVC-IDN-*</c> statement owns "this language seeded a row" as such — that is a
  /// property of the harness's fixture, not of a client — and it stays strictly weaker than
  /// <see cref="Requirements.IdnActingUserPropagatedToRow"/> alone (it is NOT weaker than
  /// <see cref="Requirements.IdnCrossTenantUpdateAnsweredWithoutError"/>: in the no-seeded-row state
  /// the backstop fails while that assertion passes).</para>
  ```

  Replace the enforcement assertion block (`:456-486`):
  ```csharp
          // ── IVC-IDN-003, enforcement: another tenant's acting user is denied ──────────────────
          var deniedStep = document.Steps.FirstOrDefault(s => s.Name == DeniedStepName);
          var code = deniedStep is { Ok: true } ? ReadStatusCode(deniedStep.Entity) : null;

          // The status NAME and MESSAGE are reported alongside the code purely as diagnostics — no
          // assertion grades them, because the server's message is byte-identical across the
          // refusals this axis can provoke (see the class doc comment's "What the status code cannot
          // distinguish"). Carrying them in the detail is what let that be established empirically
          // rather than only read off the server source.
          var reportedStatus = ReadString(deniedStep?.Entity, "status");
          var reportedDetail = ReadString(deniedStep?.Entity, "detail");

          assertions.Add(Assertion.From(
              $"{language}: an acting user of another tenant is denied a write to this row",
              code == DeniedStatusCode,
              deniedStep is null
                  ? $"the driver reported no '{DeniedStepName}' step"
                  : !deniedStep.Ok
                      ? $"the attempt itself broke, so no denial was observed: {deniedStep.Error ?? "no error text"}"
                      : code is null
                          ? "the driver reported no gRPC status code, which is what it reports when the " +
                            "wrong acting user's write was ACCEPTED"
                          : $"the driver reported gRPC status {code}, expected {DeniedStatusCode} " +
                            "(PERMISSION_DENIED)",
              Requirements.IdnTenancyDerivedAndEnforced));
  ```
  with:
  ```csharp
          // ── IVC-IDN-007, response shape: a cross-tenant update is answered without an error ────
          var deniedStep = document.Steps.FirstOrDefault(s => s.Name == DeniedStepName);
          var code = deniedStep is { Ok: true } ? ReadStatusCode(deniedStep.Entity) : null;

          // The status NAME and MESSAGE are reported alongside the code purely as diagnostics — no
          // assertion grades them; carrying them in the detail is what let the prior denial-shaped
          // behavior be established empirically rather than only read off the server source.
          var reportedStatus = ReadString(deniedStep?.Entity, "status");
          var reportedDetail = ReadString(deniedStep?.Entity, "detail");

          assertions.Add(Assertion.From(
              $"{language}: an acting user of another tenant is answered without a gRPC error status",
              deniedStep is { Ok: true } && code is null,
              deniedStep is null
                  ? $"the driver reported no '{DeniedStepName}' step"
                  : !deniedStep.Ok
                      ? $"the attempt itself broke, so no answer was observed: {deniedStep.Error ?? "no error text"}"
                      : code is null
                          ? "the driver reported no gRPC status code, as expected for an accepted write"
                          : $"the driver reported gRPC status {code}, expected no gRPC error status",
              Requirements.IdnCrossTenantUpdateAnsweredWithoutError));
  ```
  Note: `DeniedStatusCode` (`:184`, and its cref at `:64`) is deleted in this same step — `const int` cannot express "no status code" and C# forbids `const int?`; the predicate now compares `code` against `null` directly, needing no replacement constant. Remove the const's declaration and doc comment (`:180-184`) and its cref at `:64` (adjust the surrounding sentence to read naturally without the cref, e.g. "compared NUMERICALLY, because the five languages spell the same code five ways").

  Repoint the two `JudgeTenantDerivation` citations (`:567,580`) from `Requirements.IdnTenancyDerivedAndEnforced` to `Requirements.IdnTenancyDerivedFromActingUser`.

- [ ] **Step 3: `IdentityScenarioTests.cs` — invert, rename, repoint (12 sites)**

  Per Verified assumptions 15, 17, 18:
  - `:375,376,382,389,399,407,429,438` — change the `Named(..., "denied a write")` fragment to `Named(..., "answered without a gRPC error status")` in all 8 call sites (the assertion's `Name` changed in Step 2).
  - `:371,380,387` (the three tests currently named `Judge_WrongActingUsersUpdateWasDenied_Idn003EnforcementPasses`, `Judge_WrongActingUsersUpdateSucceeded_Idn003EnforcementFails`, `Judge_WrongActingUsersUpdateFailedWithSomeOtherCode_Idn003EnforcementFails`) — invert polarity for the first two (accepted now passes, denied now fails) per the spec's item 2; the third stays `BeFalse()` (a code other than none still means the swallow didn't fire) but repoints its `RequirementId` check (`:376`) to `"IVC-IDN-007"`.
  - `:394,403,446` (`Judge_NoDeniedStepAtAll_…`, `Judge_DeniedStepItselfBroke_…`, `Judge_EmptyDocument_…`) — need no polarity change under the guarded predicate (verified fixture-by-fixture in the spec and re-confirmed in CDR round 7); only the `Named` fragment (already covered above) and, for `:453` (`Cited(assertions, "IVC-IDN-003")` inside the empty-document test), replace with **three** `Cited` checks, not two — `Judge`'s body (`:397-488`) unconditionally calls `JudgeTenantDerivation` at `:454` regardless of document content, so both new successors are cited on every call, not just the response-shape one:
    ```csharp
    Cited(assertions, "IVC-IDN-002").Should().NotBeEmpty();
    Cited(assertions, "IVC-IDN-006").Should().NotBeEmpty();
    Cited(assertions, "IVC-IDN-007").Should().NotBeEmpty();
    ```
  - `:192,209` (`JudgeTenantDerivation_ServerStoppedInjectingTheColumn_…`, `JudgeTenantDerivation_GrpcReadCarriesTheServerOwnedColumn_…`) — `RequirementId.Should().Be("IVC-IDN-003")` → `"IVC-IDN-006"`.
  - `:229` — the failure-message prose ("...NOT IVC-IDN-003's DERIVATION statement; citing IVC-IDN-003...") → "...NOT IVC-IDN-006's DERIVATION statement; citing IVC-IDN-006...".
  - `:300` (the "stayed in the client's own column" derivation assertion) → `"IVC-IDN-006"`.
  - `:261-267` (the `Count(...).Should().Be(3)` assertion) — split into two:
    ```csharp
    JudgeHappy().Count(a => a.RequirementId == Requirements.IdnTenancyDerivedFromActingUser)
        .Should().Be(2, "IVC-IDN-006 is graded by exactly two assertions: the stored tenant being " +
            "the acting user's own, and the client's value not having become it");
    JudgeHappy().Count(a => a.RequirementId == Requirements.IdnCrossTenantUpdateAnsweredWithoutError)
        .Should().Be(1, "IVC-IDN-007 is graded by exactly one assertion: the wrong acting user's " +
            "update being answered without a gRPC error status");
    ```
    Preserve the surrounding mutation-testing rationale comment (`:232-260`) in its existing structure and level of rigor, updating const names and the historical "IDN-003"/"three assertions" framing to match the new two-const, 2/1 split — do not re-derive new mutant scenarios; the DUP1/DUP2 analysis it documents (Check1/Check3 catching a repointed or duplicated const value) applies unchanged to each of the two new consts independently.
  - `:237` — same rationale-comment update (references "this const" and "IVC-IDN-003" in the DUP1 mutant description).
  - `:345` (`JudgeTenantDerivation_ProbeThrew_…`, calls `JudgeTenantDerivation` directly) → repoint to `Requirements.IdnTenancyDerivedFromActingUser`, count stays `2`.
  - `:614,652` — replace `DeniedStep(IdentityScenario.DeniedStatusCode)` with `DeniedStep(7)` (neither test asserts on the enforcement predicate's polarity).
  - `:619,628-629,622` — repoint `Requirements.IdnTenancyDerivedAndEnforced` to `Requirements.IdnTenancyDerivedFromActingUser`; update the `:622` comment's "IVC-IDN-003's derivation half" to "IVC-IDN-006's derivation half".
  - `:176,195` — update the section-header comments to name both new IDs (e.g. "── IVC-IDN-006/007: tenancy derivation, and the response shape of a cross-tenant write ──").

- [ ] **Step 4: `docs/standards/iverson-client-standard.md` — retire, author, split, rewrite**

  In the IDN table (`:393-397`), change `IVC-IDN-003`'s row's `Status` from `Active` to `Retired` (Statement cell byte-unchanged, per Global Constraint 3), and add two new rows immediately after `IVC-IDN-005`'s row:
  ```
  | IVC-IDN-006 | Active | Behaviour | The server derives a row's tenant from the acting-user identity rather than from the write payload |
  | IVC-IDN-007 | Active | Behaviour | A mapped update attempted by an acting user of another tenant is answered without a gRPC error status, the same as an accepted one |
  ```

  In the Coverage ledger (`:562`), replace:
  ```
  | Tenancy derived from the acting user and enforced against another tenant's acting user | Covered | IVC-IDN-003 |
  ```
  with two rows:
  ```
  | Tenancy derived from the acting user | Covered | IVC-IDN-006 |
  | A cross-tenant update is answered without a gRPC error status | Covered | IVC-IDN-007 |
  ```

  Add a new Deferred row (immediately after the existing IDN Deferred rows, before the Backstop section) for the enforcement gap:
  ```
  | Cross-tenant write denial signaled on the wire | Deferred | The server no longer distinguishes a cross-tenant write from an accepted one on the wire at all, by design (CSR round 9's Finding #5 mitigation) — the only evidence is the server's own audit entry (`reason=BlockedCrossTenantWrite`), which no conformance run reads. No assertion can discharge this without a server-side change to signal the denial differently, which this axis does not make. |
  ```

  Update the existing Deferred row at `:569` ("Distinguishing 'denied for WHO is calling'..."). Its premise — that a header-less caller and a wrong-tenant caller are indistinguishable — is now FALSE, but not because both are now denied identically; verified directly against `RowFieldAuthorizationEvaluator.Evaluate` (`:13-14`, `if (actingUser is null) return new AuthorizationDecision(true, false, ...)`, i.e. `Denied = true`) and `AuthorizationFieldMasking.EnforceWriteAuthorization` (`:82-87`), the `decision.Denied`/`AccessDenied` check fires BEFORE the `existingRowJson` branch this fix narrows, so a truly header-less caller (no acting-user token at all) is still denied exactly as before, untouched by Task 5. A wrong-tenant caller (who DOES carry a valid acting-user token, just for a different tenant) is not denied by that earlier check — they now reach the narrowed read, get `existingRowJson = null`, take the create branch, and are silently swallowed as success by Task 5's catch. So the two cases are now distinguishable, but in the OPPOSITE direction from what the old row worried about, and not by any designed contract:
  ```
  | A header-less caller is now distinguishable from a wrong-tenant caller, but only by accident | Deferred | `IVC-IDN-007`'s assertion grades whether an attempted cross-tenant update is answered without a gRPC error status. A caller with no acting-user token at all is still denied at `RowFieldAuthorizationEvaluator.Evaluate`'s earlier `actingUser is null` check (`AccessDenied`, `PermissionDenied`) — unchanged by CSR round 9's Finding #5 mitigation, since that check runs before the row read this fix narrows — so a header-less caller is graded red by `IVC-IDN-007`, while a genuine wrong-tenant caller's write is now silently swallowed and graded green. This distinguishes the two cases, but not by a designed signal: no requirement asserts on it, and a future change to either check could re-merge or re-invert the two outcomes with nothing here to catch it. |
  ```

  Update the ERR-axis Deferred row at `:962` ("The refusal reason behind `PermissionDenied`"), whose cross-reference to `IVC-IDN-003` is now stale. Precisely: a wrong-tenant `Update` still calls `EnforceWriteAuthorization` (unlike a header-less caller's early `AccessDenied` exit), but since `existingRowJson` is now always null for it, it takes the CREATE branch (`:89-100`), not the `TenantMismatch` branch (`:107-111`, reachable only when `existingRowJson` is non-null) — the actual denial now happens later, at the database layer, and is caught and swallowed as a success by the calling gRPC method, never reaching `EnforceWriteAuthorization`'s shared `deniedMessage`/`PermissionDenied(7)` throw at all:
  ```
  | The refusal reason behind `PermissionDenied` | Deferred | `PermissionDenied` (7) is the server's answer to several distinct refusals on the mapped write path, carrying one literal `deniedMessage` into every branch of `AuthorizationFieldMasking.EnforceWriteAuthorization` and setting no trailers — though a cross-tenant `Update`'s `TenantMismatch` branch specifically is now unreachable (CSR round 9's Finding #5 mitigation narrows the read so `existingRowJson` is always null for a foreign key, so the create branch fires instead and the actual denial, when it happens, is caught and swallowed at the database layer rather than thrown here), so this now applies only to the remaining branches (access denial, owner mismatch, tenant-immutability). No `ERR` assertion observes the distinction either, and closing it needs the server to distinguish refusals on the wire. |
  ```

  Rewrite the Backstop section (`:574-584`) per Task 5's fix consequence (mirrors the `IdentityScenario.cs` class-doc rewrite in Step 2 above):
  ```
  `IDN`'s negative leg used to be a denial only while the row it targets existed; since CSR round
  9's Finding #5 mitigation, EVERY cross-tenant update takes `EnforceWriteAuthorization`'s
  no-existing-row branch and succeeds, so there is no longer a denial assertion for a missing row
  to defeat. This makes the backstop MORE load-bearing than before, not less: with no seeded row,
  `IVC-IDN-007`'s "answered without a gRPC error status" assertion is satisfied vacuously, so
  `IdentityScenario.Judge`'s "the write phase reported a row key for this language" assertion is the
  only thing left that separates a genuine swallowed cross-tenant write from a scenario that had
  nothing to update. It fires unconditionally, on every language, before and outside both the
  read-back and the enforcement assertions. Like `REL`'s, `QRY`'s, `SCH`'s and `VEC`'s it carries no
  requirement ID: no `IVC-IDN-*` statement owns "this language seeded a row" as such — it is a
  property of the harness's own fixture, not of a client — and it stays strictly weaker than
  `IVC-IDN-002` alone (it is NOT weaker than `IVC-IDN-007`: in the no-seeded-row state the backstop
  fails while `IVC-IDN-007` passes).
  ```

  Update the "45 `Active` requirements" count sentence (`:77-78`) to 46 (net +1: −1 retired, +2 authored); "nine axes" is unchanged (both successors join the existing IDN axis).

- [ ] **Step 5: Build, test, commit**
  ```bash
  dotnet build Iverson.slnx
  dotnet test Iverson.slnx --filter "Category!=Integration"
  ```
  Confirm specifically that `RequirementsCoverageGateTests.cs` passes (Check1's Active-ID/const balance, Check2's citation coverage, Check3's ID-shape/uniqueness, Check4's Modes 3/5/7 over the split Coverage rows) — this is the test suite that would catch a `Requirements.cs`/standard-doc mismatch.
  ```bash
  git add Iverson.Server/Iverson.ClientConformance/Requirements.cs Iverson.Server/Iverson.ClientConformance/Scenarios/IdentityScenario.cs Iverson.Server/Iverson.ClientConformance.Tests/IdentityScenarioTests.cs docs/standards/iverson-client-standard.md
  git commit -m "retire IVC-IDN-003 and author IVC-IDN-006/007 for the narrowed cross-tenant Update fix (CSR round 9 Finding #5 consequence)"
  ```

---

## Known issues inherited from spec

- **Finding #1's null-owner and cross-concrete-tenant consequences** (per your explicit choice, after being shown the concrete effect) — the write-side ownership check has no special case for a `null` incumbent owner. Every type registered before this fix (or by any header-less call) becomes permanently unclaimable by any acting-user-bearing caller going forward; and once a type has any concrete owner, a different tenant's routine, otherwise-legitimate re-registration of that same shared type name is now rejected, reversing round 8's own deliberate tolerance for that pattern. If this needs revisiting later, the `IsShared` opt-in flag (referenced in Finding #6's own remediation) is the identified path to reconciling ownership enforcement with legitimate type-name sharing.
- **StarRocks TLS, the Authentik admin-token hop, OIDC discovery over TLS, and a service mesh** (per your choice) — all deferred; StarRocks has no TLS infrastructure today (would need new cert issuance and chart changes, not a quick win), and the Authentik/OIDC/mesh work needs real infrastructure decisions this spec doesn't make.
- **The `job-revoke-cross-db` bootstrap Job's Postgres connection** (per your explicit choice) — this one-shot Helm Job connects with the same `iverson` credential as the api/worker Deployments, with no `PGSSLMODE` set, and so shares the same unverified-TLS condition Finding #3 targets. Deferred rather than fixed in this round: its exposure window is one Job run per `helm upgrade` rather than continuous traffic, and its entire SQL surface is two `REVOKE`/`GRANT` statements — it never reads or writes tenant data.
- **Finding #4's startup assertion** (architectural improvement, not required for the primary fix) — deferred; would need to correctly enumerate every legitimate unmarked endpoint to avoid false positives.
- **Finding #2's `UseForwardedHeaders`/trusted-proxy configuration** — deferred; needs the cluster's actual trusted-proxy CIDR. Without it, the new pre-auth limiter degrades to one shared bucket per ingress pod for external traffic, rather than true per-client throttling.
- **Findings #6 and #7** (per your choice) — no code change. Finding #6 (type-name existence oracle) becomes safe to close later once ownership can no longer be silently reassigned by this round's Finding #1 fix, but is not closed by it alone — the `IsShared` flag is still the identified path if it's ever revisited. Finding #7 (residual prompt-injection surface in the reasoning agent) has mitigations already proportionate to its retrieval-only, tenant-bounded scope; re-assess only if the agent's tool surface grows beyond retrieval.
