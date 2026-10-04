# CSR Round 10 — Server Authorization Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-10-03-csr-round10-server-authorization-design.md` (commit SHA: `422e95a4`)

**Goal:** Fix server-side authorization findings #1, #8, #10, #11, #13 and #14 from CSR round 10, and apply the interim patch for #7. #2 is refuted, with no code change.

**Architecture:**
- **Write path (Tasks 1–2):**
  - Update answers NotFound instead of creating a row, after the denial check.
  - Fields the caller may not write are carried forward from the stored row, and so is the owner column. The server-forced tenant column is exempt from field rejection.
  - The Mapping Update response is masked with the Read decision, and the carried keys are stripped from it.
- **Query path (Tasks 4–5):** join columns are field-authorized, and the expression validator stops treating an uncalled function name as a non-column.
- **Identity (Tasks 6–9):** a Postgres revocation table behind a 30 s cache, checked in `OnTokenValidated` on both JwtBearer schemes. `/v1/traces` accepts console-audience tokens only, and the admin-console endpoints refuse callers whose tenant is not active.
- **Conformance (Task 3):** IVC-IDN-007 is retired for IVC-IDN-008 (cross-tenant update answered NOT_FOUND).

**Tech stack:**
- .NET 10: ASP.NET Core gRPC, minimal APIs, JwtBearer.
- xUnit, FluentAssertions, NSubstitute, Testcontainers (`postgres:16-alpine`).
- Npgsql/Dapper through `IRecordStoreQueryExecutor`.
- StarRocks SQL builders.
- The Helm umbrella chart and docker-compose.

**Proof:** every code block in Tasks 1–9 was applied and committed on a scratch branch (`planproof-csr10a`, `e637bada..2bd1caa8`). The whole solution was then run there: see "Whole-plan proof" under the verified plan-level assumptions.

---

## Global Constraints

Verbatim from the spec:
- One branch, one merge. No proto change and no SDK change.
- The conformance change is server-side only: one requirement retired, one added, and the judge and its tests re-pinned.
- Security write-ups (comments, test names, the standard) stay at the level of conditions and behaviour, never step-by-step exploitation.
- `docs/` is gitignored: commit with `git add -f`.

Plan-level:
- **Commit messages** are lowercase imperative sentences with no prefix, ending with the session's `Co-Authored-By` trailer.
- **Staging:** stage specific paths, never `-A`.
- **Builds:** never run two `dotnet` builds or tests at once (9 GB machine).
- **Mutation testing:** task reviews mutation-test every guard spec §10 lists. Each task's assumptions table records the mutations already proven red.

## File Structure

**Write path (Tasks 1–2)**
- Modify `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs`:
  - `requireExistingRow` and NotFound;
  - the audit label;
  - the exemption set;
  - `CarryForwardRestrictedFields`, including the owner column;
  - the inserted-key return.
- Modify `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs`: Update passes `true`, and the `42501` catch and stale comments go.
- Modify `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`: the same, plus Read masking of the response and stripping the carried keys from it.
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/{AuthorizationFieldMaskingTests,ObjectPersistenceGrpcServiceTests,ObjectMappingGrpcServiceTests}.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Helpers/SchemaFixtures.cs`, the reserved-`__TenantId` Dossier fixtures.
- Create test: `Iverson.Server/Iverson.Api.Tests/Grpc/UpdateCarryForwardPostgresIntegrationTests.cs`, which runs the real `OutboxWriter` on Postgres.

**Conformance (Task 3)**
- Modify:
  - `Iverson.Server/Iverson.ClientConformance/Requirements.cs`;
  - `Iverson.Server/Iverson.ClientConformance/Scenarios/IdentityScenario.cs`;
  - `docs/standards/iverson-client-standard.md`;
  - the five drivers' comments, only: `Iverson.Clients/{DotNet/Iverson.Client.Conformance.Driver/Program.cs,Python/conformance/driver.py,TypeScript/conformance/driver.ts,Go/conformance/main.go,Java/conformance/src/main/java/io/iverson/conformance/Driver.java}`.
- Test: `Iverson.Server/Iverson.ClientConformance.Tests/IdentityScenarioTests.cs`

**Query path (Tasks 4–5)**
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs`, `Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs`
- Test: `Iverson.Server/Iverson.StarRocks.Tests/{StarRocksQueryBuilderTests,StarRocksPipelineBuilderTests}.cs`

**Identity (Tasks 6–9)**
- Create:
  - `Iverson.Server/Iverson.Sql/TokenRevocationRepository.cs`;
  - `Iverson.Server/Iverson.Api/Tenancy/{ITokenRevocationCache,TokenRevocationCache}.cs`;
  - `Iverson.Server/Iverson.Api/ConsoleClientAuthorizationPolicy.cs`;
  - `Iverson.Server/Iverson.Api/Console/ActiveTenantEndpointFilter.cs`.
- Modify:
  - `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs`, the repository interface;
  - `Iverson.Server/Iverson.Api/Program.cs`: DI, startup table, `OnTokenValidated` on both schemes, the `ConsoleClient` policy, and the traces route;
  - `Iverson.Server/Iverson.Api/Tenancy/{IIdpAdminClient,IdpAdminClient}.cs` (`IdpUser.Uid`);
  - `Iverson.Server/Iverson.Api/Grpc/TenantAdminGrpcService.cs`, revoke before Authentik;
  - `Iverson.Server/Iverson.Api/Console/AdminConsoleEndpoints.cs`, the filter on the four endpoints;
  - `Iverson.Server/docker-compose.yml`, `Authentication__ConsoleAudience` on `iverson-api`;
  - `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml`, the same variable from the human-oidc-client secret.
- Create tests:
  - `Iverson.Server/Iverson.Sql.Tests/TokenRevocationRepositoryPostgresIntegrationTests.cs`;
  - `Iverson.Server/Iverson.Api.Tests/Tenancy/TokenRevocationCacheTests.cs`;
  - `Iverson.Server/Iverson.Api.Tests/Grpc/TokenRevocationPipelineTests.cs`;
  - `Iverson.Server/Iverson.Api.Tests/ConsoleClientAuthorizationPolicyTests.cs`;
  - `Iverson.Server/Iverson.Api.Tests/AdminConsoleActiveTenantPipelineTests.cs`.
- Modify tests:
  - `Iverson.Server/Iverson.Sql.Tests/ContainerCollection.cs`;
  - `Iverson.Server/Iverson.Api.Tests/Grpc/TenantAdminGrpcServiceTests.cs`;
  - `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikAdminClientTests.cs`;
  - `Iverson.Server/Iverson.Api.Tests/TracesRelayEndpointTests.cs`;
  - `Iverson.Server/Iverson.Api.Tests/Helpers/{AuthTestWebApplicationFactory,AdminConsoleTestWebApplicationFactory,StartupNoOpFakes}.cs`.

## Inherited from spec

The following assumptions were verified by `thorough-brainstorming` and the two CDR rounds, and are NOT re-verified here. They are trusted as ground truth. The evidence for each is in the spec's "Verified assumptions" table, under the same number.

1. A token's `sub` equals the Authentik user's `uid`; tokens carry a numeric `iat`.
2. Authentik's user list endpoint returns `uid`.
3. `RequireUserInTenantAsync` already holds the user record, so `Uid` costs no extra Authentik call.
4. Allow-listed keywords can't be referenced as unquoted columns in StarRocks; function names can.
5. StarRocks accepts a space between function name and `(`; `"…"` is a string literal.
6. Every SDK passes gRPC NotFound on mapped update through as a gRPC error its driver classifies.
7. `AllowedFields` never contains the tenant column, and is null when no field is excluded.
8. The stored row JSON holds every column, restricted ones included, under canonical names.
9. `OnTokenValidated` fires for `AuthenticateAsync("ActingUser")`, and `context.Fail` yields Unauthenticated.
10. A raw-DDL platform table works under Helm's Postgres roles without grants.
11. Console tokens carry `aud` = the console client id, as a single string.
12. Nothing depends on Update creating a row.
13. The set of server tests affected by NotFound is known.
14. `DeriveWhitelist` is referenced only at the four scan sites.
15. No legitimate expression uses `"` or `\`.
16. `EngagementQueryTranslationException` maps to InvalidArgument.
17. Mapping Get masks its response with the Read decision.
18. Pipeline-test hosts can substitute the revocation repository.
19. Operators carry no `tenant_id`; tenant status values are `active/suspended/deleted`, null when unknown. An operator's console token carries `tenant_id: null`, which .NET reads as an empty claim (CIR-1 PK, PD); Task 9 treats empty as absent.
20. No deployment holds a template descriptor persisted before `410cb83b` (user confirmation, 2026-10-03).
21. Carried-forward values survive the `row_to_json` → payload → `json_populate_record` round trip for every column type. Task 2's Testcontainers test now closes this item's caveats as well.
22. Every Authentik provider uses the default subject mode.
23. Revocation reaches every caller of the ActingUser scheme (`ActingUserInterceptor` and the three `/admin` routes).

## Verified plan-level assumptions

Each task's table was produced by applying that task's code on the scratch branch, building it, running its tests, and grepping every consumer of each changed signature. Each task's mutation checks were run there too: every listed mutation turned at least one of the task's tests red.

### Whole-plan proof

`dotnet test Iverson.Server.slnx` at the scratch branch's final commit (`2bd1caa8`, Tasks 1–9 applied), run by the plan writer rather than taken from the proving subagents: **3673 passed, 0 failed.** Re-measured after CIR-1's §2.1 and §2.2 fixes: `Iverson.Api.Tests` **1380/0**, in a scratch worktree at `2bd1caa8` with both fixes applied. Re-measured again after CIR-2 §2.1: **1381/0**. No other project is touched by these fixes, so the solution total is **3679**.

| Project | Before (`e637bada`, per proving agents' baselines) | After (`2bd1caa8`) |
|---|---|---|
| `Iverson.Api.Tests` | 1318 | 1381 |
| `Iverson.Sql.Tests` | 112 | 116 |
| `Iverson.StarRocks.Tests` (incl. 56 Testcontainers) | 497 | 525 |
| `Iverson.ClientConformance.Tests` | 638 | 640 |
| `Iverson.Patterns.Tests` / `Vector` / `Events` / `Embeddings` / `LoadTest` | 620 / 183 / 41 / 54 / 119 | unchanged |

`RequirementsCoverageGateTests` passes 30/30 after Task 3. The Helm API chart renders `Authentication__ConsoleAudience` from the human-oidc-client secret (Task 8). The five driver edits are comment-only: the diff filter over non-comment changed lines gives 0 lines.

| Category | Assumption | Evidence |
|---|---|---|
| code validity | A restricted integer above 2^53 is carried as its exact JSON text, which `json_populate_record` parses back exactly | CIR-2 §2.1 (the API accepts any Struct, so a string-sent BIGINT above 2^53 is stored exactly); `Update_ByAFieldRestrictedCaller_KeepsARestrictedIntegerAbove2Pow53Exact` 183/0; helper call removed → 1 failed |
| code validity | An exception thrown inside `OnTokenValidated` fails the request rather than skipping authentication, so a revocation-cache reload failure fails closed | CIR-1 probe PB (run): `ListAsync` throwing → `/v1/traces` 500 with nothing forwarded, gRPC Unknown; span S8 |

### Task 1 assumptions: Update is strictly an update

| Category | Assumption | Evidence |
|---|---|---|
| file path | The write gate is `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs`; the RPCs are `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs` and `ObjectMappingGrpcService.cs`; the tests are `Iverson.Server/Iverson.Api.Tests/Grpc/{AuthorizationFieldMaskingTests,ObjectPersistenceGrpcServiceTests,ObjectMappingGrpcServiceTests}.cs` | Edited and built at scratch commit `4a54dd03` |
| signature | `EnforceWriteAuthorization(IRowFieldAuthorizationEvaluator, ClaimsPrincipal?, SchemaDescriptor, Struct, AuthorizationAction, string deniedMessage, string? existingRowJson, AuditLog, IPayloadSizeValidator)` before the change; `bool requireExistingRow` goes after `existingRowJson`. Required, not defaulted, so every caller must choose | `AuthorizationFieldMasking.cs:42-51` at `e637bada`; build of the whole `Iverson.Server.slnx` succeeds after the change (0 errors) |
| signature | Mapping has no `Create` RPC: its create RPC is `ObjectMappingGrpcService.Post` (`:139`). The spec's "Mapping `Create`" means it | `grep -n "public override" ObjectMappingGrpcService.cs`: `RegisterSchema, GetSchema, Get, Post, Update, Delete` |
| code validity | `resourceKey` (already computed at the top of the method by `StructFieldAccess.GetFieldString(payload, schema.KeyColumn.Name)`) holds the same key the RPC extracted: both use `StructFieldAccess.Candidates` (canonical, then camelCase) | `AuthorizationFieldMasking.cs:54`; `EntityKeyAccessor.cs:20-26` |
| code validity | NotFound message text `'{TypeName}:{key}' not found.` matches Mapping Get/Delete | `ObjectMappingGrpcService.cs:103, 121, 291, 316` (`$"'{request.TypeName}:{request.Key}' not found."`); test asserts `Detail.Should().Be($"'Author:{AuthorId}' not found.")` |
| code validity | `using Npgsql;` in both services and both service test files was used only by the deleted `PostgresException` catches / swallow tests | `grep -n "Postgres\|Npgsql"` over the four files: only the `using` lines and the catch/throw sites; the solution builds with them removed |
| code validity | `EntityAccess` is a `readonly record struct`, so `Received(1).FetchByKeyAsync(..., EntityAccess.ForTenant("test-tenant"))` matches by value | `Iverson.Sql/IRecordStoreRoles.cs:132`; the deleted swallow tests used the same assertion and the new tests pass |
| code validity | The fixture default `FetchByKeyAsync → null` is what the 5 named tests relied on; `AuthorJson`/`ArticleJson` (Mapping) carry `TenantId: test-tenant`, so the existing-row branch's tenant match passes for the default principal | `ObjectMappingGrpcServiceTests.cs:44-45`; `ActingUserFixtures.Principal` defaults `tenant_id` to `test-tenant`; the 5 tests pass with the stub |
| code validity | Owner force-set coverage already exists on the create path in both files, so deleting the two `WhenRowDoesNotExistYet` theories loses nothing | `Post_ForOrdinaryCaller_ForceSetsOwnerFieldToActingUserSub` and `Post_WithBypassRole_LeavesOwnerFieldUntouched` (both theories `null`/`"someone-else"`) in each file, plus `Post_ForOrdinaryCaller_WithFieldPermissionRestrictingOwnerColumn_StillForceSetsOwnerField` |
| code validity | The smuggled-tenant check is schema-independent (keys on the payload key via `SchemaDescriptor.IsTenantColumn`), so the legacy `AuthorSchema` fixture is enough for the smuggled-ordering test | `AuthorizationFieldMasking.cs:73`; test passes, and goes red when the NotFound check is moved above it (mutation M3) |
| code validity | Both services' denial audit goes through the gate: `decision.Denied` → `auditLog.Denied(actingUser, auditAction, …, "AccessDenied")` inside `EnforceWriteAuthorization`, so one label change covers both, and the test lives in both service classes. `AuditLog.Denied` formats `action={Action} resourceType={ResourceType}`, so the substring `action=Update resourceType=Author` pins the label | `AuthorizationFieldMasking.cs:65, 97`; `Iverson.Api/Grpc/AuditLog.cs:7-17` |
| code validity | `auditAction` is used by every `auditLog.Denied` in the gate (AccessDenied, TenantMismatch, TenantImmutable, OwnerMismatch, OwnerImmutable). With `requireExistingRow` true the label is "Update" for all of them; the existing-row checks already ran only with a stored row, where it was "Update" anyway. Only the denied-missing-key path changes | `grep -n auditAction AuthorizationFieldMasking.cs`: 1 assignment + 5 uses |
| command | `dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~Iverson.Api.Tests.Grpc.<Class>|…"` from `Iverson.Server` selects exactly those classes | Baseline counts per class: Persistence 43, Mapping 108, Masking 19 (sum 170 = combined run) |
| ordering | Task 1 must land before Task 2: Task 2's tests call `EnforceWriteAuthorization` with `requireExistingRow:` and Task 2's Mapping tests rely on Update with a stubbed row | Executed in that order; Task 2 commit `3192d4ef` sits on `4a54dd03`. The audit-label part of Task 1 was proven later as `2c19b739` (on scratch HEAD `166d692d`, after Tasks 3–9, none of which touch these files: `git diff --stat 3192d4ef 166d692d` over them is empty) |
| consumer impact | Callers of `EnforceWriteAuthorization` (changed signature). Production: `ObjectPersistenceGrpcService.cs:36` (Post), `:115` (Update); `ObjectMappingGrpcService.cs:147` (Post), `:209` (Update). Tests: `AuthorizationFieldMaskingTests.cs` 7 sites (`Enforce` helper `:64`, `:194`, `:311`, `EnforceAndCatch` `:352`, `:475`, `:512`, `:576`, line numbers at `e637bada`). No other code caller; comment-only mentions in `LoadTest/Scenarios/BenchmarkIngestScenario.cs:182`, `ClientConformance/Scenarios/NavPropertyRejectedScenario.cs:22`, `ClientConformance/Scenarios/IdentityScenario.cs:36,110`, `ClientConformance/Requirements.cs:536`, `Api/Grpc/SchemaRegistrationOrchestrator.cs:565,623`, `Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs:237` | `command grep -rn "EnforceWriteAuthorization" --include=*.cs Iverson.Server`; full solution build 0 errors |
| consumer impact | `IdentityScenario.cs:108-114` ("Why every cross-tenant update now takes the create branch … caught later, at the database layer") becomes stale with this task. It is inside spec §9's `:93-145` rewrite range, so §9's task owns it | `sed -n 104,116p Iverson.ClientConformance/Scenarios/IdentityScenario.cs` |
| consumer impact | `BlockedCrossTenantWrite` / "swallowed as success" survive only in `docs/standards/iverson-client-standard.md` (§9's task) and historical round-9 docs | `command grep -rln` over md/cs/py/ts/go/java |

#### Mutation checks

| Guard | Mutation | Tests that failed |
|---|---|---|
| NotFound check | condition forced false (`if (false && requireExistingRow && …)`) | both classes: `Update_WhenNoRowExistsInTheCallersTenant_ThrowsNotFound`, `Update_WhenNotFound_WritesNothingPublishesNothingAndAuditsNothing` (4 failed / 168) |
| NotFound after the denial check | NotFound check moved above `authEvaluator.Evaluate` | both classes: `Update_DeniedCallerWithNoExistingRow_GetsPermissionDeniedNotNotFound`, `Update_WithNoActingUser_ThrowsPermissionDenied`, `Update_WithNoAuthorizationRulesConfigured_ThrowsPermissionDenied` (6 failed) |
| NotFound after the smuggled-column check | NotFound check moved above the smuggled-column check | both classes: `Update_SmuggledTenantColumnWithNoExistingRow_GetsInvalidArgumentNotNotFound` plus the three denial tests above (8 failed) |
| Update call sites pass `true` | both `requireExistingRow: true` flipped to `false` | both classes: `Update_WhenNoRowExistsInTheCallersTenant_ThrowsNotFound`, `Update_WhenNotFound_WritesNothingPublishesNothingAndAuditsNothing` (4 failed) |
| Audit action label follows the RPC | `requireExistingRow \|\| existingRowJson is not null ? "Update"` → `existingRowJson is not null ? "Update"` (the old label) | both classes: `Update_DeniedCallerWithNoExistingRow_IsAuditedAsAnUpdate` (2 failed / 178) |

Each mutation was applied, run, and restored from a byte copy. `git status` was clean against the commit afterwards.

#### Counts

- Baseline at `e637bada`: `Iverson.Api.Tests` whole project **1318 passed / 0 failed** (6 m 47 s). The three touched classes: **170** (Persistence 43, Mapping 108, Masking 19).
- Red after Step 4 (tests only): Persistence + Mapping **145 passed / 6 failed / 151**. Observed in two parts. Without the audit tests: 145 / 4 / 149. The two `…_IsAuditedAsAnUpdate` tests, run alone before the label change, failed 2/2. The pre-Task-1 label is the same expression (`existingRowJson is null ? "Create" : "Update"`), so they are red at Step 5 too.
- After Task 1: the three classes **170 passed / 0 failed**. Observed: the original 168 tests, then 178 after the audit tests were added on top of Task 2 (176 + 2).

### Task 2 assumptions: restricted fields carried forward; tenant column exempt

| Category | Assumption | Evidence |
|---|---|---|
| file path | Fixtures live in `Iverson.Server/Iverson.Api.Tests/Helpers/SchemaFixtures.cs`, and every existing fixture there uses the legacy `TenantColumn = "TenantId"` | File read at `4a54dd03`: 11 fixtures, all `TenantColumn = "TenantId"` |
| file path | The Postgres Testcontainers convention in `Iverson.Api.Tests` is a per-class `IAsyncLifetime` fixture (`postgres:16-alpine`, `PostgresRepository` + `PostgresSchemaManager`, `EnsureRolesAsync`) used through `IClassFixture`, with the class in `[Collection(ContainerCollection.Name)]` and `[Trait("Category", "Integration")]` | `Reconciliation/ReconciliationQueuePostgresIntegrationTests.cs:21-53`, `Reconciliation/DocumentRerenderQueuePostgresIntegrationTests.cs:17-48` (two byte-identical fixture classes), `ContainerCollection.cs` doc |
| file path | The test sits in `Iverson.Api.Tests`, not `Iverson.Sql.Tests`: carry-forward is in `Iverson.Api` (`AuthorizationFieldMasking` is `internal`, visible to `Iverson.Api.Tests` only), and the round trip under test runs through the real `ObjectPersistenceGrpcService.Update` | `Iverson.Sql.Tests` does not reference `Iverson.Api`; `AuthorizationFieldMaskingTests` already calls the internal class |
| signature | `RejectDisallowedFields(Struct, IReadOnlySet<string>?, string? exemptField = null)` before; `EnforceWriteAuthorization` (`:160` at `e637bada`) is its only caller anywhere, so there are no tests to update to the new signature | `command grep -rn "RejectDisallowedFields" --include=*.cs Iverson.Server`: one call (the gate) plus comments (`BenchmarkIngestScenario.cs:182`, `AuthorizationFieldMaskingTests.cs:152,385`) |
| signature | `MaskDisallowedFields(Struct, IReadOnlySet<string>?, string? exemptField = null)` is unchanged; Mapping Update calls it as Get does, with no `exemptField` | `AuthorizationFieldMasking.cs`; `ObjectMappingGrpcService.cs:126` (Get) |
| signature | `AuthorizationRules`, `RowPermission(Role, CanReadAll, CanWriteAll, CanDeleteAll)` and `FieldPermission(FieldName, ReadableRoles, WritableRoles)` are records, so `schema.Authorization! with { FieldPermissions = … }` compiles | `Iverson.Api/Schema/SchemaDescriptor.cs:145-174` |
| code validity | `RowFieldAuthorizationEvaluator` excludes the reserved tenant column from `AllowedFields`, sets `AllowedFields` only when at least one field is excluded, and keys exclusion on the action's role list (`ReadableRoles` for Read, `WritableRoles` for Write) | `RowFieldAuthorizationEvaluator.cs:66-117`; the test with `"premium"` (no exclusions) gets null and carries nothing |
| code validity | `AllowedFields` is an ordinal `HashSet<string>` of canonical names; the stored row's keys are canonical column names (`row_to_json`), so `UpperFirst(storedKey)` is an identity for every column and the ordinal `Contains` is right | `RowFieldAuthorizationEvaluator.cs:116` (`.ToHashSet()`); `EntityRepository.cs:7-10` (`SELECT row_to_json(t)::text`) |
| code validity | On the update branch the tenant column is force-set into the payload before carry-forward runs, so carry-forward never copies it | `AuthorizationFieldMasking.cs` existing-row branch `SetAuthoritativeField(payload, decision.TenantColumn, …)`; test asserts `__TenantId` = `test-tenant` and `carried` = exactly `Secret, SealedAt, Seal` |
| code validity | Case-insensitive match matters for the owner field: it is exempt from rejection, so an ownership-scoped caller restricted from writing it may still send it, camelCase. `StructSerializer.FoldKeys` throws InvalidArgument on two keys that fold to one | `ProtoPayloadHelper.cs:16-33`; mutation M7 (Ordinal) fails `EnforceWriteAuthorization_CamelCasePayloadKey_MatchesTheCanonicalStoredKey` |
| code validity | `carriedForward` is a `List<string>` returned as the method's `IReadOnlyCollection<string>`; iterating `existingRow.Fields.Keys` while `CarryForward` writes to a *different* Struct (`payload`) is safe | Compiles with 0 warnings in the touched files; 183/0 |
| code validity | The read decision for "test-bypass" with `RowPermission("test-bypass", false, true, false)`: no OwnerField → Denied, `AllowedFields` null; with OwnerField → ownership-required, read `AllowedFields` = all minus `Notes` (still lists `Secret`). Either way read masking alone would return the carried `Secret` | `RowFieldAuthorizationEvaluator.cs:36-64`; mutation M9 (drop the carried-key removal) fails both write-only tests |
| code validity | Values survive the real round trip on `postgres:16-alpine`: `TIMESTAMPTZ` `row_to_json` text with offset and `BYTEA` `\x…` hex both go back through `json_populate_record` unchanged, and so does a BIGINT/BIGINT[] above 2^53, which `PreserveExactIntegers` carries as exact JSON text (CIR-2 §2.1) | `Update_ByAFieldRestrictedCaller_LeavesOmittedRestrictedValuesIntactInPostgres` compares `GetRawText()` of `Secret`, `SealedAt`, `Seal` before and after; closes spec VA-21's two caveats (`postgres:16`, .NET hop) |
| code validity | RLS in the Postgres test: `ApplySchemaAsync` on a schema with a `TenantColumn` grants `iverson_runtime` and FORCEs RLS; `FetchByKeyAsync(ForTenant)` and `OutboxWriter`'s tenant-scoped upsert run as that role; the outbox insert resets to the superuser | `Iverson.Sql.Tests/TenantScopedAccessIntegrationTests.cs:246-293` (same writer, same role switch); test passes |
| code validity | The owner carry-forward keys on `schema.Authorization?.OwnerField`, not `decision.OwnerFieldName`: the evaluator leaves `OwnerFieldName` null for bypass callers. `AuthorizationRules.OwnerField` normalizes `""` to null, so a schema with no ownership dimension skips the step. The stored owner key is found case-insensitively, because registration admits an `OwnerField` that matches its column only case-insensitively (`SchemaRegistrationOrchestrator.cs:680-686`) | `RowFieldAuthorizationEvaluator.cs:44-60`; `SchemaDescriptor.cs:145-167`; mutation M14 (decision instead of schema) fails only the bypass cases |
| code validity | The owner step runs AFTER `RejectDisallowedFields`. A bypass caller is not owner-exempt (`OwnerFieldName` null), so an owner carried in before the rejection would be rejected as a field it may not write, whenever the owner column is also field-restricted | `EnforceWriteAuthorization` order; the exemption list is `{OwnerFieldName, TenantColumn}` |
| code validity | An owner column that is both field-restricted and omitted is copied once: the restricted pass inserts it, then `CarryForward` sees it present and returns false, so the key is not added twice | `CarryForward` checks the payload case-insensitively before writing |
| code validity | The owner value the payload already carries (any casing) wins and is still checked by `OwnerMismatch`/`OwnerImmutable` before carry-forward. Carry-forward only fills an omitted owner, and only with the stored value, so it cannot change ownership | Existing-row branch order; `CarryForward` returns false on a case-insensitive match |
| code validity | Mapping Update strips the carried owner from its response because it is in the returned collection. The ownership-scoped writer (`ReservedTenantOwnedDossierSchema`) and the `CanWriteAll` writer (`ReservedTenantWriteOnlyDossierSchema(withOwnerField: true)`) both get a response without `OwnerId` | `Update_OmittingTheOwnerColumn_KeepsTheStoredOwner_AndTheResponseDoesNotEchoIt` (3 cases, including an `OwnerField` spelled `ownerId`) |
| code validity | The upserted JSON is observable at the fake transaction's `ExecuteAsync` for the `json_populate_record` statement, via the anonymous `Json` property; the same technique is used in `OutboxWriterTests.CaptureUpsertJsonAsync` | `Iverson.Sql.Tests/OutboxWriterTests.cs:166-185` |
| command | Filter that adds the Postgres class: `FullyQualifiedName~Iverson.Api.Tests.Grpc.UpdateCarryForwardPostgresIntegrationTests` | 177 = 168 + 9 observed |
| ordering | Step 5 red is a compile error, not a test failure: the new masking-test helper consumes the return value Step 6 adds | Observed with the production files reverted to `4a54dd03`: exactly one error, `AuthorizationFieldMaskingTests.cs(617,9): error CS0029` |
| ordering | Mapping response masking runs AFTER `SerializePayload`/publish, so the published payload keeps the carried and read-restricted values | The three Mapping tests assert `PayloadJson` contains `"Secret":"classified"` / `"Notes":"new notes"` while `Data` omits them |
| consumer impact | `EnforceWriteAuthorization` now returns `IReadOnlyCollection<string>`. Callers: `ObjectMappingGrpcService.cs:209` (Update) uses it; `:147` (Post), `ObjectPersistenceGrpcService.cs:36` (Post) and `:115` (Update) discard it as a statement; the 7 `AuthorizationFieldMaskingTests` sites discard it; the new `EnforceWithRealEvaluator` helper returns it | `command grep -rn "EnforceWriteAuthorization(" --include=*.cs`; full solution build 0 errors |
| consumer impact | `RejectDisallowedFields` signature change: the only caller is `AuthorizationFieldMasking.cs` itself | grep above |
| consumer impact | `CarryForwardRestrictedFields` and the private `CarryForward` are new; the only callers are `EnforceWriteAuthorization` and (for `CarryForward`) `CarryForwardRestrictedFields` | grep above |
| consumer impact | Mapping Update's response changes for field-restricted readers: read-restricted fields are now omitted (as Get omits them), and carried values are never returned. Persistence's `PersistResponse` carries only the key | `ObjectMappingGrpcService.cs` Update tail; `Iverson.Clients/Common/Proto/object_persistence.proto:19-24`: `PersistResponse { success, key, trace_id, error }`, no payload |
| consumer impact | `SchemaFixtures` gains 3 public fixtures and 1 private builder; no existing fixture changes, so no existing test is affected | Whole `Iverson.Api.Tests` run 1325/0 |

#### Mutation checks

Final code, i.e. owner carry-forward included. The four classes, 182 tests, with the Postgres test included. The owner-related rows were re-measured after CIR-1 §2.2 added the third owner-theory case. These rows were measured before CIR-2 §2.1 added the BIGINT Postgres test (183 tests); the last row was measured with it.

| Guard | Mutation | Tests that failed |
|---|---|---|
| Tenant exemption | `new[] { decision.OwnerFieldName, decision.TenantColumn }` → `new[] { decision.OwnerFieldName }` | `EnforceWriteAuthorization_FieldRestrictedCreate_OnAReservedTenantSchema_Succeeds`, `…_FieldRestrictedUpdate_CarriesOmittedRestrictedFieldsForward`, `…_CamelCasePayloadKey_MatchesTheCanonicalStoredKey`, `Update_ResponseOmitsFieldsTheCallerMayNotRead_…`, both write-only response tests, all three owner-theory cases, the Postgres test (10 failed / 182) |
| Restricted carry-forward | `CarryForwardRestrictedFields`'s condition forced false (`if (false && !allowedFields.Contains(…) && …)`) | `…_FieldRestrictedUpdate_CarriesOmittedRestrictedFieldsForward`, the three Mapping response tests (published payload loses `Secret`), the Postgres test (5 failed) |
| Carry-forward case-insensitive match (shared `CarryForward`) | `StringComparison.OrdinalIgnoreCase` → `Ordinal` | `EnforceWriteAuthorization_CamelCasePayloadKey_MatchesTheCanonicalStoredKey` (1 failed) |
| Carry-forward no-op for unrestricted callers | removed `if (allowedFields is null) return [];` | `EnforceWriteAuthorization_UpdateByACallerWithNoFieldRestriction_CarriesNothing` and 13 existing Update tests (NullReferenceException) (14 failed) |
| Owner carry-forward | owner step disabled (`if (false && storedOwnerKey is not null && CarryForward(…))`) | `Update_OmittingTheOwnerColumn_KeepsTheStoredOwner_AndTheResponseDoesNotEchoIt` all three cases (3 failed) |
| Owner key matched case-insensitively | `StringComparison.OrdinalIgnoreCase` → `Ordinal` in the stored-owner-key lookup | the same theory, `ownerId` case (1 failed) |
| Owner sourced from the schema, not the decision | `schema.Authorization?.OwnerField` → `decision.OwnerFieldName` | the same theory, both `bypassWriter: True` cases (2 failed) |
| Carried owner joins the returned collection | owner copied but not added to `carriedForward` | the same theory, all three cases (response echoes `OwnerId`) (3 failed) |
| Mapping Read masking | `MaskDisallowedFields(request.Payload, readDecision.AllowedFields)` → `RemoveTenantColumn(request.Payload)` | `Update_ResponseOmitsFieldsTheCallerMayNotRead_WhileThePublishedPayloadKeepsThem` (1 failed) |
| Mapping carried-key removal | deleted the `foreach (var carried in carriedForward) request.Payload.Fields.Remove(carried);` loop | `Update_ResponseOmitsFieldsTheCallerMayNotRead_…`, both write-only response tests, all three owner-theory cases (6 failed) |
| Exact large integers carried | `PreserveExactIntegers(existingStruct, existingRowJson);` removed | `Update_ByAFieldRestrictedCaller_KeepsARestrictedIntegerAbove2Pow53Exact` (1 failed / 183) |

Each mutation was applied, run, and restored from a byte copy. `git status` was clean against the commit afterwards.

#### Counts

- Before Task 2 (after Task 1): the three classes **170 / 0**.
- After Task 2: the four classes **183 passed / 0 failed** (Postgres tests included, about 6 s; 181 before CIR-1 §2.2 added the third owner-theory case, 182 before CIR-2 §2.1 added the BIGINT test).
- Whole `Iverson.Api.Tests` at the Task 2 point: **1329 passed / 0 failed / 1329** (7 m 3 s). Run in a throwaway worktree at `3192d4ef` with `2c19b739` and `9963c672` cherry-picked, then removed. Baseline 1318 → −10 deleted cases + 10 (Task 1) + 11 (Task 2) = 1329. An earlier run at `81448753`, before the amendments, was 1325 / 0.
- Whole `Iverson.Api.Tests` at scratch HEAD `9963c672` (Tasks 1–9 plus both fixups): **1375 passed / 0 failed / 1375** (7 m 1 s).
- `dotnet build Iverson.Server.slnx`: Build succeeded, 0 errors (at `3192d4ef`; the fixups touch only `Iverson.Api` and its tests, which built clean).

### Task 3 assumptions

| Category | Assumption | Evidence |
|---|---|---|
| file path | `Iverson.Server/Iverson.ClientConformance/Requirements.cs` holds the IDN consts; IDN-007 is at `:567-583` | Read at `53723baa` (unchanged since `e637bada`: `git diff --quiet e637bada --` over the four files) |
| file path | `Iverson.Server/Iverson.ClientConformance/Scenarios/IdentityScenario.cs` holds the judge (`Judge`, enforcement assertion `:459-490`) and the class doc (`:7-144`) | Read |
| file path | `Iverson.Server/Iverson.ClientConformance.Tests/IdentityScenarioTests.cs` holds the judge tests; the spec's cited `DeniedStep` sites `:62, :116, :146, :374, :383, :401, :450, :642, :680` are exactly where the spec says | Read; `grep -n "DeniedStep("` at baseline matched all nine |
| file path | `docs/standards/iverson-client-standard.md` is gitignored and must be staged with `git add -f` | `.gitignore:46-51` (memory); the commit used `git add -f` |
| file path | The standard's spec-cited lines (`:399, :411, :437-443, :466-472, :479-516, :567, :573, :575, :580-600, :1012`) are at those numbers in the scratch worktree | `cat -n` of the file at `53723baa`; earlier tasks did not touch it |
| signature | `Requirements.IdnCrossTenantUpdateAnsweredNotFound` is a `public const string` = `"IVC-IDN-008"`, which the gate reflects | `RequirementsCoverageGateTests` Check1 reflects consts; 30/30 green after the change |
| signature | `IdentityScenario.TypeName`, `DeniedStepName` and `Judge` are `internal` and visible to the test project | The existing tests already use `IdentityScenario.DeniedStepName`; the new detail test uses `IdentityScenario.TypeName`, which compiles |
| signature | `ReadStatusCode` returns null for a malformed code, so `malformedCode ⇒ code is null` | `IdentityScenario.cs:644-650` (`ValueKind == Number && TryGetInt32`) |
| command | `cd Iverson.Server && dotnet test Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj [--filter "FullyQualifiedName~IdentityScenarioTests"]` runs the suites | Run: baseline 638/0 (gate 30, identity 48); after 640/0 (gate 30, identity 50) |
| command | `command grep` (not shell `grep`) is needed to search `docs/` | Memory: shell grep is gitignore-aware ugrep |
| ordering | Task 3's unit tests do not depend on Task 1's code. Its live meaning does: against a pre-Task-1 server the cross-tenant update returns no status and IVC-IDN-008 fails on every driver. Ship it in the same merge as Task 1 (spec's one-branch constraint) | Spec §9 "forced by §1"; agent A's Task 1 commit `4a54dd03` precedes this in the scratch branch |
| ordering | Step 2's red is a compile error (CS0117) because the tests reference the new const before it exists; the behavioural red was confirmed separately | Observed: CS0117 at `IdentityScenarioTests.cs(267,65)`. With the const added alone (judge unchanged), 13/50 failed |
| ordering | Between Step 6 and Step 7 the coverage gate is red (IDN-008 const with no standard row, and IDN-007 still Active with no const) | By Check1's construction; the full suite went green only after Step 7 (640/0) |
| code validity | Assertion name `"{language}: an update by an acting user of another tenant is answered with gRPC status NOT_FOUND"` is matched by exactly one assertion via `Named(..., "answered with gRPC status NOT_FOUND")` (`Single`) | 50/50 green; no other IDN assertion name contains the fragment |
| code validity | The predicate keeps `!malformedCode` exactly as spec §9 writes it, though it is implied by `code == 5`. The malformed case's DIAGNOSIS is pinned by the detail assertion in `Judge_DeniedStepReportsAMalformedStatusCode_Idn008FailsNamingTheMalformedReport` | Mutation M3 below (survives, as expected); mutation M4 (red) |
| code validity | Removing row `:575` leaves the ledger well-formed; a Deferred row's Evidence is free text the gate does not parse for IDs | `RequirementsCoverageGateTests.cs:689-697`: Deferred rows checked only for a non-empty reason; gate 30/30 |
| code validity | The ERR row at `:1012` may mention `IVC-IDN-008` without tripping Check4's same-axis rule, because that rule applies only to Covered rows' Evidence | Same lines; Mode 3 splits Evidence only for `Status == "Covered"` |
| code validity | "49 `Active` requirements" stays correct | One Active row became Retired, one Active row added; gate Check1 green |
| consumer impact | Removed const `IdnCrossTenantUpdateAnsweredWithoutError`. Every reference: `IdentityScenario.cs:122` (cref), `:141` (cref), `:484` (citation); `IdentityScenarioTests.cs:266`. All four are rewritten | `command grep -rn IdnCrossTenantUpdateAnsweredWithoutError` over the repo (excluding specs/plans/criticalreviews): those four plus the declaration; zero after the change |
| consumer impact | The assertion's NAME changed ("answered without a gRPC error status" → "answered with gRPC status NOT_FOUND"). Nothing outside `IdentityScenarioTests.cs` matches on it: no report fixture, README or driver | `command grep -rn "answered without a gRPC error status"` across `*.cs, *.md, *.py, *.ts, *.go, *.java`: only the standard, the scenario and the tests |
| consumer impact | No driver or SDK change: drivers already report `statusCode` as data on a gRPC error | Spec verified assumption 6 |
| consumer impact | Driver comments (spec §9 amendment `422e95a4`). The `denied_update_wrong_acting_user` comments in all five drivers are rewritten to describe NOT_FOUND (5): the tail of the "payload's tenant value" comment and the no-error-branch comment, plus .NET's register-phase comment, which referred to the negative leg being denied. Only comment lines change | `git show 2bd1caa8 -U0`, filtered to changed lines that are not `//` or `#` comments: 0 lines. Diffstat: 5 files, 36+/36− |
| file path | The five driver files live at `Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Program.cs`, `Iverson.Clients/Python/conformance/driver.py`, `Iverson.Clients/TypeScript/conformance/driver.ts`, `Iverson.Clients/Go/conformance/main.go` and `Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance/Driver.java`. The stale 5-line tail is byte-identical in all five apart from the comment prefix and indent | Read; the edit script asserted exactly one match per file, and exactly five lines from "fires there today. The" to "…a conforming client would send." |
| code validity | The rewrapped comment keeps the file's existing width (body ≤ 97 characters, as the surrounding lines) | Longest changed line is 116 characters in .NET (indent 16, `// `, body 97), equal to the existing longest line in that comment |
| command | Light per-language checks run offline. Python: `py_compile` OK. Go: `gofmt -l main.go` is clean, and `GOPROXY=off go build` is OK. .NET: `dotnet build` of the driver csproj gives 0 errors. TypeScript is not checked, because the scratch worktree has no `node_modules`. Java's `mvn -o -q compile` fails against a stale offline SDK artifact in `~/.m2` (`MatchPatternBuilder`, `iverson.ObjectSearch.*` missing) with 20 `cannot find symbol` errors. They are identical before and after this edit (the HEAD and modified sources were compiled and the error lists diffed: IDENTICAL), so they are pre-existing | Run in the scratch worktree. The `gofmt -l .` hit on `models.go` is pre-existing; that file is untouched |
| file path | Other driver comments mentioning the negative leg are still accurate and left as they are: the scenario headers (.NET `Program.cs:36-39`, Go `main.go:53-56`, Java `Driver.java:80-83`), Java's `doIdentityRead` Javadoc, and the "SECOND channel/client/invoker" comments. All of them only say the driver reports the status code it received. .NET `Program.cs:1240-1242` ("gone"/"read denied"/"tenant mismatch") is the delete scenario's read-back, not this leg | Read |

#### Grep disposition (spec §9 / brief sweep)

Patterns `IVC-IDN-007`, `IdnCrossTenantUpdateAnsweredWithoutError` and "answered without a gRPC error status", over the whole worktree, excluding `docs/specs`, `docs/plans`, `docs/criticalreviews`, `bin`, `obj`, `node_modules` and `.git`. No other worktrees are nested in the scratch worktree.

| Hit (pre-change) | Disposition |
|---|---|
| standard `:399` table row | Status → Retired; IDN-008 row added |
| standard `:411` | rewritten (IDN-008, enforcement third) |
| standard `:437` | paragraph `:434-441` rewritten (7 vs 5, by design) |
| standard `:443` | IDN-006/IDN-008 |
| standard `:466` | bullet rewritten (NotFound) |
| standard `:567` | repointed to IDN-008 |
| standard `:573` | rewritten Deferred row |
| standard `:584` | backstop paragraph rewritten |
| standard `:599-600` | IDN-008 |
| `IdentityScenario.cs:12` | class summary rewritten |
| `IdentityScenario.cs:122`, `:141` | crefs → `IdnCrossTenantUpdateAnsweredNotFound`, paragraphs rewritten |
| `IdentityScenario.cs:459`, `:472`, `:484` | judge re-pinned |
| `IdentityScenario.cs:656` | `IsStatusCodeMalformed` doc rewritten |
| `Requirements.cs:583` | const removed (replaced by IDN-008) |
| `IdentityScenarioTests.cs:238` | historical mutant-analysis comment: kept, and extended with "and to IVC-IDN-007's own successor, IVC-IDN-008" |
| `IdentityScenarioTests.cs:266-268` | count re-pointed at IDN-008 |
| `IdentityScenarioTests.cs:376-450` (8 tests) | replaced by the 10-test IDN-008 block |
| `IdentityScenarioTests.cs:466` | `Cited(..., "IVC-IDN-008")` |

Related stale text with no pattern hit, also fixed:
- standard `:515-516`: the pointer to the deleted row;
- standard `:575`: deleted;
- standard `:1012`;
- `IdentityScenario.cs:91-113`: "takes the create branch", "caught later, at the database layer";
- `Requirements.cs:558-564`: IDN-006's "the server no longer denies a cross-tenant write on the wire at all", which is now past tense, plus a pointer to IDN-008.

Hits remaining after the change are all intentional retirement or history references:
- the standard: `:399`, `:526`, `:527`, `:531`;
- `Requirements.cs`: `:564`, `:577`, `:578`, `:581`;
- `IdentityScenarioTests.cs`: `:238`, `:386`.

Also checked: `BlockedCrossTenantWrite`, "swallowed as a success" and "silently swallowed" have no remaining hits in the standard, the conformance project or its tests. The remaining "swallow" hits in `ClientConformance.Tests` are unrelated: string-literal, comment and exception-swallowing contexts.

#### Mutation checks

| Mutation (in `IdentityScenario.Judge` unless stated) | Result |
|---|---|
| M1 `code == 5` → `(code == 5 \|\| code is null)` (accepts an accepted update) | RED: `Judge_WrongActingUsersUpdateWasAccepted_Idn008Fails` (1 failed / 640) |
| M2 `code == 5` → `(code == 5 \|\| code == 7)` (accepts PERMISSION_DENIED) | RED: `Judge_WrongActingUsersUpdateWasPermissionDenied_Idn008Fails` (1 failed / 640) |
| M3 drop `!malformedCode` from the predicate | survives, 640/640. Expected: it is implied by `code == 5`, because a malformed code reads as null. Kept because spec §9 writes the predicate that way |
| M4 detail's `malformedCode ?` branch → `false ?` (malformed diagnosed as "accepted") | RED: `Judge_DeniedStepReportsAMalformedStatusCode_Idn008FailsNamingTheMalformedReport` |
| M5 standard: a second `Covered \| IVC-IDN-008` ledger row (old `:575` area made Covered) | RED: `RequirementsCoverageGateTests.Check4_AxisCoverageLedgers_BindClaimedAreasToActiveRequirements` |
| M6 standard: IDN-007 row left `Active` | RED: `Check1_ActiveIdsInStandard_ExactlyMatchConstsInRegistry` and `Check4_…` |

Every mutation was restored, and byte-equality was verified with `cmp` against a pre-mutation copy before the commit.

#### Driver-comment fixup (`2bd1caa8`, on top of `e5240e7b`)

- Comment-only proof: there are 0 changed lines outside `//` or `#` comments.
- Checks: Python `py_compile` OK; Go `gofmt` clean and `go build` OK; .NET build 0 errors. Java's offline compile shows the same 20 pre-existing errors before and after. TypeScript was skipped because it has no `node_modules`.
- No conformance-suite effect: the drivers are not part of `Iverson.ClientConformance.Tests`.

#### Counts

- Baseline at `53723baa` (scratch HEAD before this task):
  - `Iverson.ClientConformance.Tests` 638 passed / 0 failed;
  - `RequirementsCoverageGateTests` 30/30;
  - `IdentityScenarioTests` 48/48.
- Red (Step 2): build error CS0117. With the const alone, 13 failed / 37 passed of 50.
- After (`166d692d`):
  - `Iverson.ClientConformance.Tests` **640 passed / 0 failed**;
  - `RequirementsCoverageGateTests` **30/30**;
  - `IdentityScenarioTests` **50/50**.

### Task 4 assumptions

| Category | Assumption | Evidence |
|---|---|---|
| file path | `Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs` holds `BuildFromWithJoins`, which resolves `leftCol`/`rightCol` | Read at scratch HEAD `a07599ce`: `BuildFromWithJoins` at `:843`, `leftCol`/`rightCol` at `:881-886` (as the spec says) |
| file path | `Iverson.Server/Iverson.StarRocks.Tests/StarRocksQueryBuilderTests.cs` holds the `BuildFromWithJoins` tests and the `BuildRegistry`, `AuthorSchema` and `ArticleSchema` helpers | `BuildRegistry` at `:1618`; `AuthorSchema` (`Name, Bio, Rating, PublishedAt`) and `ArticleSchema` (`Title, Body`) at `:12-16`; joined-type tenant section ends just before `// ── BuildGroupBy ───` |
| signature | `BuildFromWithJoins(EngagementQuerySchema, IReadOnlyList<JoinSpec>, Func<string, EngagementQuerySchema?>, DynamicParameters, out IReadOnlyDictionary<string, JoinContext>, IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null, string? tenantDatabase = null)` is unchanged; the new check is a local function | `git show bcd28556`: signature lines untouched |
| signature | `AuthorizationConstraint(IReadOnlySet<string>? AllowedFields, string? OwnerColumn, string? OwnerValue, string? TenantColumn = null, string? TenantValue = null)` | `Iverson.StarRocks/AuthorizationConstraint.cs:3-8` |
| code validity | `ResolveColumn(schema, f)` returns the canonical-case column name (case-insensitive lookup), and never the tenant column | `StarRocksQueryBuilder.cs:763-772`; test `BuildFromWithJoins_AllowedJoinFieldsUnderRestriction_ProducesJoinClause` joins on `name`/`title` and emits `` `Name` ``/`` `Title` `` |
| code validity | Keying the constraint by `join.LeftType`/`join.RightType` (the spec's wording) is safe in production because every production `authz` dictionary uses `StringComparer.OrdinalIgnoreCase` | `ObjectSearchGrpcService.cs:1080`, `AggregateReader.cs:68`, `PopularitySignalConsumer.cs:70`. `MatchPattern.cs:62` builds an empty one. The neighbouring owner and tenant blocks key by `join.RightType` the same way |
| code validity | `EngagementQueryTranslationException` maps to InvalidArgument at every search RPC | Spec VA-16 (not re-verified) |
| ordering | Task 4 has no dependency on Tasks 1–3 or 5–9; it touches only `BuildFromWithJoins` and a new test section | Built on scratch HEAD `a07599ce` (Tasks 1, 2, 6–9 committed), which has no StarRocks changes |
| command | `dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName!~IntegrationTests"` runs every unit test and none of the Testcontainers classes | `--list-tests`: 525 tests in total. The substring `IntegrationTests` matches exactly 4 classes, all Testcontainers-backed: `MatchRowsIntegrationTests` (25), `PipelineIntegrationTests` (5), `StarRocksIntegrationTests` (15) and `TenantIsolationIntegrationTests` (11), each `[Collection(StarRocksCollection.Name)]`. 525 − 56 = 469 after Task 5, matching the filtered count |
| consumer impact | No signature changed. Callers of `BuildFromWithJoins`, and so of the new check: `BuildSearch` (`StarRocksQueryBuilder.cs:73`), `BuildAggregate` (`:197`) and `BuildGroupBy` (`:379`), plus the direct tests in `StarRocksQueryBuilderTests.cs` | `command grep -rn BuildFromWithJoins --include=*.cs` |
| consumer impact | No existing test joins on a field outside an `AllowedFields` restriction | All 441 baseline unit tests still pass. Testcontainers classes: `command grep -n "AllowedFields: new\|JoinSpec" *IntegrationTests.cs` finds joins only with no `AllowedFields`, so they are unaffected (not run) |
| consumer impact | `Iverson.Api.Tests` does not run the real builder for joins | Its references to `StarRocksQueryBuilder` are comments only (`ObjectSearchGrpcServiceTests.cs:286, 479`; `SchemaBuilderTests.cs:385`). The search service is an NSubstitute substitute |

#### Mutation checks

| Guard | Mutation | Test(s) that failed |
|---|---|---|
| left-side join check | `RequireJoinFieldAllowed(join.LeftType, …)` commented out | `BuildFromWithJoins_RestrictedLeftJoinField_ThrowsTranslationException` |
| right-side join check | `RequireJoinFieldAllowed(join.RightType, …)` commented out | `BuildFromWithJoins_RestrictedRightJoinField_ThrowsTranslationException` |
| allowed-field comparison | `!constraint.AllowedFields.Contains(…)` → `constraint.AllowedFields.Contains(…)` | `BuildFromWithJoins_AllowedJoinFieldsUnderRestriction_ProducesJoinClause`, `…RestrictedLeftJoinField…`, `…RestrictedRightJoinField…` |
| no-`authz` pass-through | `authz is not null && authz.TryGetValue` → `authz!.TryGetValue` | `BuildFromWithJoins_NoAuthz_JoinOnAnyField_ProducesJoinClause` (plus 4 pre-existing no-`authz` join tests) |

Each mutation was restored with `git checkout` after its run. The worktree was clean afterwards.

#### Counts

- **Baseline** at scratch HEAD `a07599ce`: `Iverson.StarRocks.Tests` unit (`FullyQualifiedName!~IntegrationTests`) 441/441 pass. Builder classes (`StarRocksQueryBuilderTests|StarRocksPipelineBuilderTests`) 285/285.
- **Red step:** the `BuildFromWithJoins` filter gave 2 failed, 15 passed of 17.
- **After Task 4** (`bcd28556`): unit 445/445 pass.

### Task 5 assumptions

| Category | Assumption | Evidence |
|---|---|---|
| file path | `DeriveWhitelist` lives at `StarRocksPipelineBuilder.cs:28-32` | Read at `bcd28556` |
| file path | The four scan sites are at `StarRocksQueryBuilder.cs:253` (Aggregate) and `:531` (GroupBy metric), and at `StarRocksPipelineBuilder.cs:223` (pipeline metric) and `:396` (Derive) | Read at `bcd28556`. Task 4 inserts lines only below `:860`, so these QueryBuilder line numbers are unchanged after Task 4 |
| file path | `RejectForbiddenCharacters` is at `StarRocksPipelineBuilder.cs:374-388`, with its XML doc at `:364-373` | Read at `bcd28556` |
| file path | The stale test comment is in `Q1StyleRequest()` (`StarRocksQueryBuilderTests.cs:1981` at `a07599ce`, `:2109` after both tasks) | `command grep -n DeriveWhitelist` |
| signature | New: `internal static bool IsNonColumnToken(string expr, Match token)` in `StarRocksPipelineBuilder` | Matches the spec §4 signature. `internal` is needed because `StarRocksQueryBuilder` calls it (same assembly) |
| signature | Removed: `internal static readonly HashSet<string> DeriveWhitelist`. Added: `private static readonly HashSet<string> DeriveFunctions` and `DeriveKeywords`, both `OrdinalIgnoreCase` | After the change, `command grep -rn DeriveWhitelist --include=*.cs Iverson.Server` returns 0 hits. The only readers of the two sets are inside `IsNonColumnToken` |
| signature | `RejectForbiddenCharacters(string expr, string errorContext)` is unchanged; only its body, message and doc change | `git show 53723baa` |
| code validity | `TokenRx` matches carry `Index`/`Length` into the same string passed as `expr`, so `token.Index + token.Length` is the first character after the token | `TokenRx` is `new Regex("[A-Za-z_][A-Za-z0-9_]*")`. Each site passes the string it ran `TokenRx.Matches` on (`spec.Expression`, `metric.Expression`, `m.Expression`, `d.Expr`) |
| code validity | StarRocks accepts `SUM (x)`; keywords can't be unquoted columns; function names can | Spec VA-4 and VA-5 (not re-verified) |
| code validity | The pipeline's "restricted column" is modelled by passing `AllowedFields` without `Sum` to `TrackAndValidate`, because `ColumnsFor` drops non-allowed columns from the base step's column set, which feeds both `input.Columns` (metric) and `available` (Derive) | At `bcd28556`: `ColumnsFor` at `StarRocksPipelineBuilder.cs:38-56`; `TrackAndValidate` passes `authz[schema.TypeName]` to `ColumnsFor` at `:95-96`. The bare-`Sum` tests went red under the old whitelist and green under the fix |
| code validity | No existing test asserts the old forbidden-character message verbatim | `command grep -rn "no semicolons\|backticks, or\|forbidden character ("` over the repo finds only the source line and the spec. The tests match `Contains("forbidden character")`, and one also matches `Contains("SQL comment sequences")` (`StarRocksPipelineBuilderTests.cs:718`); both survive |
| code validity | No legitimate expression in the repo uses `"`, `\` or a bare function name | Spec VA-15. Repo-wide grep of `Expression`/`Expr` literals that hold a whitelisted function name not followed by `(` found only `Type = AggregationType.Sum` false positives (`ObjectSearchGrpcServiceTests.cs:979, 2268`). The only Testcontainers expression is `"100.0 * n / SUM(n) OVER ()"` (`PipelineIntegrationTests.cs:143`), whose identical unit twin `Validate_DeriveAllowsWhitelistedWindowExpr` passes |
| ordering | Task 5 follows Task 4: both edit `StarRocksQueryBuilder.cs` and `StarRocksQueryBuilderTests.cs`, in disjoint regions | Task 5 was built and committed on top of `bcd28556`. The plan's before-snippets were verified verbatim against `bcd28556` and its after-snippets against `53723baa` (scripted substring check) |
| command | `--filter "FullyQualifiedName!~IntegrationTests"` excludes exactly the four Testcontainers classes | See `task-4-assumptions.md`: 525 − 56 = 469 |
| consumer impact | Callers of `IsNonColumnToken`: `StarRocksQueryBuilder.cs:253, :531` and `StarRocksPipelineBuilder.cs:244, :417` | `command grep -rn IsNonColumnToken --include=*.cs` |
| consumer impact | Callers of `RejectForbiddenCharacters`, which now rejects two more characters: `StarRocksQueryBuilder.cs:250, :528` and `StarRocksPipelineBuilder.cs:241, :414` | `command grep -rn "RejectForbiddenCharacters(" --include=*.cs` |
| consumer impact | `Iverson.Api.Tests` doesn't run the real expression validator | References are comments only. Its expression tests (`ObjectSearchGrpcServiceTests.cs:979, 2268`) stub the search service to throw |

#### Mutation checks

| Guard | Mutation | Test(s) that failed |
|---|---|---|
| `(` lookahead | `return i < expr.Length && expr[i] == '(';` → `return true;` | All 4 bare-`Sum` tests: `BuildAggregate_ExpressionUsesRestrictedColumnNamedLikeFunction_ThrowsTranslationException`, `BuildGroupBy_MetricExpressionUsesRestrictedColumnNamedLikeFunction_ThrowsTranslationException`, `Validate_MetricExpressionUsesRestrictedColumnNamedLikeFunction_Throws`, `Validate_DeriveUsesRestrictedColumnNamedLikeFunction_Throws` |
| whitespace skip before `(` | `while (… char.IsWhiteSpace …) i++;` removed | The `"SUM (Amount)"` case at all 4 sites |
| keyword set consulted | `if (DeriveKeywords.Contains(token.Value)) return true;` removed | The `"SUM(Amount) OVER (PARTITION BY Region ORDER BY Amount DESC)"` case at all 4 sites, plus the pre-existing `Validate_DeriveAllowsWhitelistedWindowExpr` and `Build_RunningSumAndDerive_EmitOverAndExpression` |
| `"` forbidden | `expr.Contains('"')` removed | The `COALESCE(Amount, "a")` case at all 4 sites |
| `\` forbidden | `expr.Contains('\\')` removed | The `Amount \ 2` case at all 4 sites |
| Aggregate site uses the lookahead | call → `(IsNonColumnToken(spec.Expression, m) \|\| m.Value.Equals("Sum", OrdinalIgnoreCase))` (old behaviour for `Sum`) | `BuildAggregate_ExpressionUsesRestrictedColumnNamedLikeFunction_ThrowsTranslationException` |
| GroupBy site uses the lookahead | same mutation on `metric.Expression` | `BuildGroupBy_MetricExpressionUsesRestrictedColumnNamedLikeFunction_ThrowsTranslationException` |
| pipeline metric site uses the lookahead | same mutation on `m.Expression, tok` | `Validate_MetricExpressionUsesRestrictedColumnNamedLikeFunction_Throws` |
| Derive site uses the lookahead | same mutation on `d.Expr, m` | `Validate_DeriveUsesRestrictedColumnNamedLikeFunction_Throws` |

Each mutation was restored with `git checkout` after its run. The worktree was clean afterwards.

#### Counts

- **Baseline** at `bcd28556` (after Task 4): unit 445/445.
- **Red step:** unit 12 failed, 457 passed of 469. The failures are the bare-`Sum` test and the two `"`/`\` cases at each of the 4 sites.
- **After Task 5** (`53723baa`):
  - unit (`FullyQualifiedName!~IntegrationTests`) 469/469;
  - builder classes 313/313 (`StarRocksQueryBuilderTests` 196, `StarRocksPipelineBuilderTests` 117).
- **Testcontainers classes** (`MatchRowsIntegrationTests`, `PipelineIntegrationTests`, `StarRocksIntegrationTests`, `TenantIsolationIntegrationTests`): not run. No new tests were added there, and the brief limits container runs to the tests a task adds.

### Task 6 assumptions: token revocation store and cache

| Category | Assumption | Evidence |
|---|---|---|
| file path | Platform repositories live in `Iverson.Server/Iverson.Sql/`; their interfaces are in `Iverson.Sql/IRecordStoreRoles.cs` (`IEnrichmentStateRepository` at `:177`) | `grep -n "interface I" Iverson.Sql/IRecordStoreRoles.cs`; built at `450b01da` |
| file path | Tenancy services live in `Iverson.Server/Iverson.Api/Tenancy/` (`TenantStatusCache.cs`, `ITenantStatusCache.cs`); their tests in `Iverson.Api.Tests/Tenancy/` (`TenantStatusCacheTests.cs`) | `ls` of both directories |
| file path | Test-host NoOp classes live in `Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs` and are registered in `Helpers/AuthTestWebApplicationFactory.cs` (`NoOpTenantRepository` registered at `:63-64` at `3192d4ef`) | Read both files; the spec's "in `AuthTestWebApplicationFactory.cs`" is satisfied by the registration there |
| file path | Container-backed `Iverson.Sql.Tests` classes use a per-class `IAsyncLifetime` fixture with `postgres:16-alpine`, `[Trait("Category","Integration")]` and `[Collection(ContainerCollection.Name)]` | `TenantRepositoryPostgresIntegrationTests.cs`, `DlqRepositoryPostgresIntegrationTests.cs` |
| signature | `IRecordStoreQueryExecutor.ExecuteAsync(string, object?, RecordStoreRole = Connection, string? = null)` and `QueryAsync<T>(…)` | `IRecordStoreRoles.cs:48-53` |
| signature | `PostgresRepository(string connectionString, ILogger<PostgresRepository>)` implements `IRecordStoreQueryExecutor` | `TenantRepositoryPostgresContainerFixture` constructs it the same way |
| code validity | Dapper maps a positional value tuple `(string sub, DateTime revoked_at)` from `timestamptz`, with UTC `Kind`, so `new DateTimeOffset(dt, TimeSpan.Zero)` does not throw | `SchemaRegistryRepository.LoadAllAsync` uses the same tuple pattern; `RevokeAsync_ThenListAsync_ReturnsTheSubWithTheDatabaseTime` passes against real Postgres and asserts `Offset == 0` |
| code validity | `Microsoft.Extensions.TimeProvider.Testing` / `FakeTimeProvider` is referenced nowhere | `grep -rn "TimeProvider" --include=*.csproj --include=*.cs Iverson.Server`: only `EngagementRepository.cs` and the hand-rolled `ManualTimeProvider` in `EngagementRepositoryLivenessRewrapTests.cs` |
| code validity | `TimeProvider` is not registered in the API's DI, so the cache is registered with a factory passing `TimeProvider.System` | no `AddSingleton(TimeProvider` / `TimeProvider.System` registration in `Program.cs` (grep) |
| code validity | `NSubstitute.ExceptionExtensions.ThrowsAsync` is already used in `Iverson.Api.Tests` | `SchemaRegistrationOrchestratorTests.cs:10`, `SimilarityResolverIntegrationTests.cs:8` |
| code validity | An NSubstitute `Returns(pending.Task)` with an incomplete `TaskCompletionSource` makes concurrent callers wait under the semaphore, so the concurrency test is deterministic (no timers, no `Task.Delay`) | The test passes; mutation C3 (re-check removed) yields `Received(8)` → red |
| command | `dotnet test Iverson.Sql.Tests/Iverson.Sql.Tests.csproj --filter "FullyQualifiedName~TokenRevocationRepository"` selects the 4 new tests | observed 4/4 |
| command | `dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~TokenRevocationCacheTests\|FullyQualifiedName~ActingUserInterceptorSuspensionTests\|FullyQualifiedName~TracesRelayEndpointTests"` → 19 | observed 19/19 at `450b01da` (9 + 4 + 6) |
| ordering | Program.cs's `EnsureTableAsync` runs after the re-render-queue bootstrap and before `EnsureRolesAsync`, like the two raw-DDL plumbing tables; it needs no roles or grants | `Program.cs:705-712` at `e637bada` (spec VA-10) |
| ordering | Every `WebApplicationFactory<Program>` host must have the NoOp registered in the same commit as the startup call, or every pipeline test fails at host start (real `TokenRevocationRepository` → unreachable Postgres) | NoOp + startup call land together in `450b01da`; the full `Iverson.Api.Tests` run is green |
| ordering | Task 6 must land before Task 7: Task 7's handler resolves `ITokenRevocationCache`, `TenantAdminGrpcService` takes `ITokenRevocationRepository`, and the pipeline factories need the NoOp | Executed in that order |
| consumer impact | `ITokenRevocationRepository`, `ITokenRevocationCache`, `TokenRevocationRepository` and `TokenRevocationCache` are new; no existing signature changes | `git show --stat 450b01da` |
| consumer impact | Every class deriving from `AuthTestWebApplicationFactory` inherits the NoOp: `AdminConsoleTestWebApplicationFactory`, the three CORS factories in `AdminConsoleCorsTestWebApplicationFactories.cs`, and every `IClassFixture<AuthTestWebApplicationFactory>` | `grep -rn ": AuthTestWebApplicationFactory"`; full suite green |
| consumer impact | `Iverson.Sql.Tests/ContainerCollection.cs`'s doc comment said "its three Postgres-backed classes" (`Dlq`, `PostgresIntegrationTests`, `TenantRepository`); the new class makes four | `grep -ln "ContainerCollection.Name" Iverson.Sql.Tests/*.cs` |

#### Mutation checks

Each mutation was applied to the committed file from a byte copy, run, and restored. `git status` was clean afterwards.

| Guard | Mutation | Tests that failed |
|---|---|---|
| revocation comparison `<=` | `issuedAt <= revokedAt` → `issuedAt < revokedAt` | `IsRevokedAsync_IssuedAtTheRevocationInstant_IsRevoked` |
| missing `iat` is revoked | `(issuedAt is null \|\| issuedAt <= revokedAt)` → `(issuedAt is not null && issuedAt <= revokedAt)` | `IsRevokedAsync_MissingIatWithRevokedSub_IsRevoked` (+3 others that probe with `null`) |
| one reload at a time | re-check of `_snapshot` under the lock removed | `IsRevokedAsync_ConcurrentCallers_TriggerOneReload` |
| 30 s TTL (reload) | `IsStale` → `false` | `IsRevokedAsync_AfterThirtySeconds_ReloadsAndSeesANewRevocation` |
| 30 s TTL (no early reload) | `IsStale` → `true` | `IsRevokedAsync_WithinThirtySeconds_ServesTheSnapshotWithoutReloading`, `IsRevokedAsync_ConcurrentCallers_TriggerOneReload` |
| `RevokeAsync` upsert | `ON CONFLICT (sub) DO UPDATE SET revoked_at = EXCLUDED.revoked_at` → `ON CONFLICT (sub) DO NOTHING` | `RevokeAsync_SubAlreadyRevoked_MovesRevokedAtForwardInPlace` |

`IsRevokedAsync_ReloadFailure_Propagates` pins the absence of a catch. There is no guard to invert: the code has no try/catch around `ListAsync`.

#### Counts

- **Baseline** at `3192d4ef`: `Iverson.Api.Tests` 1325 / 0 (6 m), `Iverson.Sql.Tests` 112 / 0.
- **Red step:** the `Iverson.Sql.Tests` build fails with CS0246 on `TokenRevocationRepository`.
- **After Task 6:** `TokenRevocationRepository` 4/4, `TokenRevocationCacheTests` 9/9, and the 19-test pipeline smoke is green.
- **After Tasks 6–9 (HEAD `a07599ce`):**
  - `Iverson.Api.Tests` 1371 / 0 (6 m 15 s);
  - `Iverson.Sql.Tests` 116 / 0;
  - `Iverson.Server.slnx` builds with 0 errors. Its 4 warnings are all the pre-existing CS0618 (`ContainerBuilder()` obsolete) in the Patterns, StarRocks and Vector test projects.

### Task 7 assumptions: revocation check and writers

| Category | Assumption | Evidence |
|---|---|---|
| file path | Both JwtBearer schemes are configured in `Iverson.Server/Iverson.Api/Program.cs` (`AddJwtBearer(options => …)` and `.AddJwtBearer("ActingUser", …)`, the latter with an existing `JwtBearerEvents { OnMessageReceived = … }`) | `Program.cs:181-230` at `3192d4ef`; no other `options.Events` assignment in the file (`grep -n Events`) |
| file path | The writer RPCs are in `Iverson.Api/Grpc/TenantAdminGrpcService.cs`; `IdpUser` is declared in `Iverson.Api/Tenancy/IIdpAdminClient.cs:3` and built in `Tenancy/IdpAdminClient.cs:163` | Read |
| signature | `TokenValidatedContext` exposes `Principal`, `HttpContext` and `Fail(string)`; `JwtBearerEvents.OnTokenValidated` is `Func<TokenValidatedContext, Task>` | Builds with 0 errors; `context.Fail` yields Unauthenticated (pipeline tests; spec VA-9) |
| signature | A static member of `public partial class Program` is in scope in top-level statements, so both schemes' `Events` can name `RejectRevokedTokenAsync` unqualified | `app.Use(ListenerPortGateAsync)` (`Program.cs:507`) already does this; builds |
| code validity | With `MapInboundClaims = false`, `sub` and `iat` reach `context.Principal` under their JWT names; `iat` is a Unix-seconds integer string | Both schemes set `MapInboundClaims = false`; `PrimaryScheme_RevokedSub_TokenIssuedAfterRevocation_IsAuthenticated` passes only when `iat` is read (mutation M3); spec VA-1 (live token `iat` is a JSON number) |
| code validity | `TestJwtFactory.CreateToken(..., extraClaims: [new Claim("iat", "<n>", ClaimValueTypes.Integer64)])` produces a token whose principal carries `iat`; the factory adds no `iat` of its own | `JwtSecurityToken(audience:, claims:, expires:, signingCredentials:)` passes no `issuedAt`; spec VA-9 logged `iat=` (empty) for existing tokens; the revoked/not-revoked split in the new tests depends on it |
| code validity | The ActingUser scheme's failed result reaches `ActingUserInterceptor`'s `!result.Succeeded` branch → `Unauthenticated "Acting-user token is invalid."` | `ActingUser_RevokedSub_ThrowsUnauthenticated` asserts that exact detail |
| code validity | An Aggregate call with a valid service token and no schema fails `FailedPrecondition` (business logic), so "not Unauthenticated" proves authentication passed | Same probe and comment as `ActingUserInterceptorSuspensionTests.Call_ActiveTenant_DoesNotThrowPermissionDenied` |
| code validity | Authentik's user-list rows carry `uid` (string), the same value as the token `sub` | Spec VA-1, VA-2 (live probe) |
| code validity | `Received.InOrder(() => { … })` with non-awaited async calls is the repo's ordering idiom | `DocumentRerenderQueueWorkerTests.cs:224`, `EngagementStoreConsumerKafkaOrderingTests.cs:128` |
| code validity | Every `AuthentikAdminClientTests` list row that matches the requested tenant is parsed before the pagination checks, so the throw-tests need `uid` too (strict `GetProperty("uid")` would otherwise throw `KeyNotFoundException`, not the asserted `InvalidOperationException`) | `IdpAdminClient.cs:145-196`: rows iterate first, the pagination checks follow; 27/27 pass with the rows updated |
| command | The Step 8 filter selects 69 tests: `TokenRevocationPipelineTests` 5, `TokenRevocationCacheTests` 9, `TenantAdminGrpcServiceTests` 18, `TenantAdminGrpcServiceAuthorizationPipelineTests` 3, `AuthentikAdminClientTests` 27, `ActingUserInterceptor*` 7 | Each class run separately at HEAD; the sum matches the combined 69 |
| ordering | Revoke happens after `RequireUserInTenantAsync` (the user record is needed for `Uid`, and a cross-tenant target must be refused before any write) and before the Authentik call | Spec §5d; the existing cross-tenant tests still pass, and the ordering tests pin the rest |
| consumer impact | `IdpUser` constructor gains `string Uid`. All construction sites in the repo: `Iverson.Api/Tenancy/IdpAdminClient.cs:163`, `Iverson.Api.Tests/Tenancy/AuthentikAdminClientTests.cs:233`, `Iverson.Api.Tests/Grpc/TenantAdminGrpcServiceTests.cs:29-30`. Other `IdpUser` mentions are type-only (`IIdpAdminClient.cs:22`, `IdpAdminClient.cs:135,139`, `TenantAdminGrpcService.cs:91`, `TenantAdminGrpcServiceTests.cs` `Task.FromResult<IEnumerable<IdpUser>>`, `TenantAdminGrpcServiceAuthorizationPipelineTests.cs:44`) | `command grep -rn "IdpUser" --include=*.cs .` over the whole repo; none outside `Iverson.Server` |
| consumer impact | `TenantAdminGrpcService` constructor gains `ITokenRevocationRepository`. The only direct construction is `TenantAdminGrpcServiceTests.cs:35`; production and `TenantAdminGrpcServiceAuthorizationPipelineTests` resolve it from DI (Task 6 registered the real repository / the NoOp) | `command grep -rn "new Iverson.Api.Grpc.TenantAdminGrpcService\|new TenantAdminGrpcService(" --include=*.cs ..` |
| consumer impact | From this task on, every authenticated request in every pipeline-test host calls `NoOpTokenRevocationRepository.ListAsync()` (empty), so no existing pipeline test changes behaviour | Full `Iverson.Api.Tests` 1371/0 at HEAD |
| consumer impact | `/admin/reconcile`, `/admin/dlq` and `/admin/dlq/{id}/replay` call `AuthenticateAsync("ActingUser")` and get the same refusal for a revoked acting user | Spec VA-23 (call sites `Program.cs:600, 620, 643`); not separately tested here |

#### Mutation checks

| Guard | Mutation | Tests that failed |
|---|---|---|
| default scheme checks revocation | `options.Events = new JwtBearerEvents { OnTokenValidated = RejectRevokedTokenAsync };` deleted | `TokenRevocationPipelineTests.PrimaryScheme_RevokedSub_ThrowsUnauthenticated` |
| ActingUser scheme checks revocation | `OnTokenValidated = RejectRevokedTokenAsync` removed from its `JwtBearerEvents` | `TokenRevocationPipelineTests.ActingUser_RevokedSub_ThrowsUnauthenticated` |
| handler passes `iat` through | `IsRevokedAsync(sub, issuedAt)` → `IsRevokedAsync(sub, null)` | `PrimaryScheme_RevokedSub_TokenIssuedAfterRevocation_IsAuthenticated` |
| `RemoveUser` revokes before Authentik | the two lines swapped | `RemoveUser_RevokesTheUsersTokensBeforeDeactivatingInAuthentik`, `RemoveUser_RevocationFails_ThrowsAndDoesNotDeactivate` |
| `SetTenantAdmin(false)` revokes before Authentik | the two lines swapped | `SetTenantAdmin_Revoke_RevokesTheUsersTokensBeforeRemovingTheGroup`, `SetTenantAdmin_Revoke_RevocationFails_ThrowsAndDoesNotRemoveTheGroup` |
| revocation keyed by `uid`, not pk | `RevokeAsync(user.Uid)` → `RevokeAsync(request.UserId)` (RemoveUser) | `RemoveUser_RevokesTheUsersTokensBeforeDeactivatingInAuthentik` |
| `SetTenantAdmin(true)` doesn't revoke | grant branch also calls `RevokeAsync` | `SetTenantAdmin_Grant_DoesNotRevoke` |
| `IdpAdminClient` parses `uid` | `GetProperty("uid")` → `GetProperty("username")` | `AuthentikAdminClientTests.ListUsersByTenantAsync_FiltersByAttributesTenantId` |

The revocation comparison (`<=`, missing `iat`) is mutation-checked in Task 6's cache tests.

#### Counts

- **Before (Task 6 committed):** `TenantAdminGrpcServiceTests` 13, `AuthentikAdminClientTests` 27.
- **Red step:** CS1729 on `IdpUser` (×3) and `TenantAdminGrpcService` (×1).
- **After:** Step 8 filter 69 / 0.
- **Full `Iverson.Api.Tests` at HEAD `a07599ce`:** 1371 / 0.

### Task 8 assumptions: `/v1/traces` console-only

| Category | Assumption | Evidence |
|---|---|---|
| file path | Authorization-policy helpers live at `Iverson.Server/Iverson.Api/*AuthorizationPolicy.cs` as public static classes with `IsSatisfiedBy` (`OperatorAuthorizationPolicy`, `SchemaAdminAuthorizationPolicy`, `TenantAdminAuthorizationPolicy`); their tests are `Iverson.Api.Tests/<Name>Tests.cs` | `ls Iverson.Api/*.cs`; `OperatorAuthorizationPolicyTests.cs` (`TenantAdminAuthorizationPolicy` has none) |
| file path | `ConsoleAudience` goes on compose service `iverson-api` only (beside `Authentication__ValidAudiences__0`, `docker-compose.yml:483`). `iverson-worker` (`:589`) also sets `ValidAudiences__0`, but it has `WORKLOAD_ROLE=worker`, and `/v1/traces` is mapped only under `workloadRole == "api"`, so the worker gets no `ConsoleAudience` (spec amendment `422e95a4`, §6) | `python3 yaml.safe_load` of the compose file prints only `iverson-api ['Authentication__ConsoleAudience=dev-iverson-human-oidc-client-id']`; `Program.cs` `if (workloadRole == "api")` around the `/v1/traces` mapping |
| code validity | The Step 5 compose `replace` block is unique in the file: it ends at the `# The LoadTest's data-plane calls …` comment, which only `iverson-api` has (`iverson-worker`'s `ValidAudiences__3` is followed directly by `__4`) | The block matches once in the pre-task file; the rendered `task-8.md` blocks were checked verbatim against the files |
| file path | The Helm API env lives in `deploy/helm/iverson/charts/api/templates/deployment.yaml`, `Authentication__ValidAudiences__0` from `{{ .Release.Name }}-authentik-human-oidc-client` / `client-id` (`:141-143`) | Read; rendered |
| signature | `TestJwtFactory.CreateToken(string audience, string subject, DateTime? expires = null, IEnumerable<Claim>? extraClaims = null)` | `Helpers/TestJwtFactory.cs` |
| code validity | With `MapInboundClaims = false`, a single-string `aud` arrives as one `"aud"` claim; an array arrives as several | Pipeline tests pass with the single-string test tokens; spec VA-11 (console tokens' `aud` is a single string = client id) |
| code validity | `builder.UseSetting(key, value)` in `WebApplicationFactory.ConfigureWebHost`, and in a `WithWebHostBuilder` override, is visible to `Program.cs`'s `cfg`. This holds even when read before `builder.Build()`, so neither a per-evaluation read nor an env var is needed | Experiment: the policy's argument was replaced with a `var consoleAudienceAtStartup = cfg["Authentication:ConsoleAudience"];` captured before `AddAuthorization`, and all 8 `TracesRelayEndpointTests` still passed, including the `""` override. Reverted; the committed code reads `cfg[...]` inside the assertion |
| code validity | An authenticated caller failing a policy gets 403 through `AuditingAuthorizationMiddlewareResultHandler`; the endpoint body (and the fake Jaeger) is never reached | `PostTraces_NonConsoleAudience_Returns403_AndIsNeverForwarded` asserts 403 and `CallCount == 0` |
| code validity | A bare `helm template t .` fails on `networkpolicies.yaml:4` ("networkPolicy.clusterCidrs must list at least one CIDR …"), independently of this change; `-f values-laptop.yaml` renders | Observed both |
| command | `helm` is installed (`/home/ben/.local/bin/helm`). The scratch worktree has no `charts/*.tgz` (`find deploy/helm -name '*.tgz'` is empty; they are gitignored, `.gitignore:76`), and every dependency is `file://charts/<name>`, so `helm template` reads the live subchart without `helm dependency build` | Rendered output shows the new env entry at lines 1771-1774 |
| command | The Step 7 filter selects 41 tests: `TracesRelayEndpointTests` 8, `ConsoleClientAuthorizationPolicyTests` 5, plus `AdminConsoleCorsPipelineTests` and `AuthenticationPipelineTests` | Observed 41/41 |
| ordering | The factory's `ConsoleAudience` and its addition to the default scheme's `ValidAudiences` must land with the policy, otherwise the six existing relay tests break: with no `ConsoleAudience` in `ValidAudiences` their console tokens would fail validation (401), and with no `UseSetting` the policy is unconfigured, so they would get 403. Inferred from the mechanism, not separately run | Executed together in `b68c255b` |
| consumer impact | `TracesRelayEndpointTests.CreateAuthenticatedClient` gains two optional parameters; its only callers are the 8 tests in that file. Existing calls (`CreateAuthenticatedClient()` and `CreateAuthenticatedClient(subject: …)`) compile unchanged | `grep -n CreateAuthenticatedClient TracesRelayEndpointTests.cs` |
| consumer impact | Adding `test-console-audience` to the default scheme's `ValidAudiences` in `AuthTestWebApplicationFactory` widens what the test host accepts; no existing test mints that audience, so nothing else changes | `grep -rn "test-console-audience"`: only the factory constant; full suite 1371/0 |
| consumer impact | Other `/v1/traces` tests: the CORS preflight (`AdminConsoleCorsPipelineTests.ConfiguredOrigin_PreflightOptions_TracesRelay_OnAdminListener_AnsweredByCors`) and the listener-gate theory (`AuthenticationPipelineTests.TracesRelay_PassesTheListenerPortGate`) don't authenticate and are unaffected | Both pass |
| consumer impact | `docs/user-management-and-security.md:488-493` lists the compose `Authentication__*` variables and does not mention `ConsoleAudience`; `appsettings.json` has no `ConsoleAudience` key (unset → fail closed). Neither is changed: the spec doesn't ask | `command grep -rn ValidAudiences__0` |
| consumer impact | Every deployment must now set `Authentication__ConsoleAudience`, or the admin console's browser tracing gets 403. Compose and the Helm api chart are the only API deployment definitions | `command grep -rln ValidAudiences__0` over yml/yaml/json/sh: compose, the api deployment template, and the authentik secret template (which defines the secret, not API env) |

#### Helm render (observed)

```text
$ cd Iverson.Server/deploy/helm/iverson && helm template t . -f values-laptop.yaml | grep -n -B4 -A3 "Authentication__ConsoleAudience"
1768-            - name: Authentication__ValidAudiences__0
1769-              valueFrom:
1770-                secretKeyRef: { name: t-authentik-human-oidc-client, key: client-id }
1771-            # The admin console's client id, from the same secret: /v1/traces accepts only its tokens.
1772:            - name: Authentication__ConsoleAudience
1773-              valueFrom:
1774-                secretKeyRef: { name: t-authentik-human-oidc-client, key: client-id }
1775-            - name: Authentication__ValidAudiences__1
```

#### Mutation checks

| Guard | Mutation | Tests that failed |
|---|---|---|
| the policy | `IsSatisfiedBy` → `true` | `TracesRelayEndpointTests.PostTraces_NonConsoleAudience_Returns403_AndIsNeverForwarded`, `PostTraces_ConsoleAudienceUnset_Returns403_AndIsNeverForwarded`, and 4 `ConsoleClientAuthorizationPolicyTests` |
| fail closed when unset | `!string.IsNullOrEmpty(consoleAudience) &&` removed | `ConsoleClientAuthorizationPolicyTests.IsSatisfiedBy_ConsoleAudienceUnset_ReturnsFalse("")` only. The pipeline cannot see this guard: no validated token has an empty `aud` |
| ordinal comparison | `Ordinal` → `OrdinalIgnoreCase` | `IsSatisfiedBy_AudienceComparedOrdinally_ReturnsFalseForACaseVariant` |
| the route uses the policy | `.RequireAuthorization("ConsoleClient")` → `.RequireAuthorization()` | `PostTraces_NonConsoleAudience_Returns403_AndIsNeverForwarded`, `PostTraces_ConsoleAudienceUnset_Returns403_AndIsNeverForwarded` |

#### Counts

- **Before:** `TracesRelayEndpointTests` 6.
- **Red steps (observed):**
  - with only the relay tests written, 2 failed / 6 passed (both new tests got 202);
  - with `ConsoleClientAuthorizationPolicyTests.cs` also present, the build fails: `ConsoleClientAuthorizationPolicyTests.cs(11,9): error CS0103: The name 'ConsoleClientAuthorizationPolicy' does not exist in the current context`, and likewise at `(17,9)`, `(23,9)` and `(32,9)`. Observed by restoring Task 8's parent `Program.cs` and removing the policy file, then restoring both.
- **After:** Step 7 filter 41 / 0.
- **Full `Iverson.Api.Tests` at HEAD `a07599ce`:** 1371 / 0.

#### Amendment (spec `422e95a4`, §6: API service only)

- The proving run first added the variable to `iverson-worker` too. That line was removed in scratch commit `e5240e7b` (`fixup: task 8 api service only`), on top of `9963c672`.
- `task-8.md` now describes the API-only edit.
- Net Task 8 compose state (`b68c255b` + `e5240e7b`): one `Authentication__ConsoleAudience` line, in `iverson-api`. Verified by `yaml.safe_load`, and by `git show e5240e7b:Iverson.Server/docker-compose.yml | grep -c ConsoleAudience` → 1.
- No C# changed. Re-run of the Task 8 test classes at `e5240e7b` (`TracesRelayEndpointTests|ConsoleClientAuthorizationPolicyTests|AdminConsoleCorsPipelineTests|AuthenticationPipelineTests`): **41 passed, 0 failed**.

### Task 9 assumptions: console tenant-status filter

| Category | Assumption | Evidence |
|---|---|---|
| file path | The four console endpoints are mapped in `Iverson.Server/Iverson.Api/Console/AdminConsoleEndpoints.cs` (`MapAdminConsoleEndpoints`, `:49-78`), namespace `Iverson.Api.Console` | Read |
| file path | The console pipeline-test host is `Iverson.Api.Tests/Helpers/AdminConsoleTestWebApplicationFactory.cs` (sealed, derives from `AuthTestWebApplicationFactory`), with `AdminConsoleTenantRepository` in the same file | Read |
| signature | `ITenantStatusCache.GetStatusAsync(string tenantId) → Task<string?>`; statuses `active` / `suspended` / `deleted`, null when unknown | `Tenancy/ITenantStatusCache.cs`; spec VA-19 |
| signature | `IEndpointFilter.InvokeAsync(EndpointFilterInvocationContext, EndpointFilterDelegate) → ValueTask<object?>`; `RouteHandlerBuilder.AddEndpointFilter<TFilter>()` constructs the filter with DI | Builds; the filter receives the substituted cache in the tests |
| code validity | Authorization runs before endpoint filters, so the filter only sees callers that already passed the endpoint's policy; tokens carrying `operators` + the reader group reach the filter on all four endpoints | `ActiveTenant_Returns200` and `NoTenantIdClaim_Returns200` → 200 on all four |
| code validity | `/schema` and `/data-volume` answer 200 for an operator with or without `tenant_id`, or with an empty `tenant_id` (CIR-1 PK/PD: real operator tokens carry `tenant_id: null`) (they report withheld/denied counts rather than failing) | `AdminConsoleEndpointsPipelineTests.Operator_Schema_ReportsEveryTypeWithheldRatherThanAnEmptyCatalog`, `Operator_DataVolume_ReportsEveryTypeDeniedRatherThanZeroRows`; the new tests pass |
| code validity | NSubstitute auto-values an unconfigured `Task<string?>` to `""`, not null, so the "unknown tenant" fake is a hand-rolled class | Agent A's `FetchByKeyAsync` comment in `ObjectMappingGrpcServiceTests` and `ObjectPersistenceGrpcServiceTests` |
| code validity | `AdminConsoleTestWebApplicationFactory` uses the real `TenantStatusCache` → `AdminConsoleTenantRepository.GetAsync`, which returned null; the filter would therefore 403 `AdminConsoleEndpointsPipelineTests`' `tenant_alpha` reader | Reverting `GetAsync` to `null` after the filter landed: 6 failed (`AuthenticatedNonOperator_AuthenticatedOnlyEndpoint_Returns200` ×2, `Reader_DataVolume_CountsThePermittedTypes`, `Reader_DataVolume_DeniedTypeIsDistinguishableFromAZeroCount`, `Reader_Schema_ProjectsRelationEdges`, `Reader_Schema_ReturnsOnlyThePermittedTypesAsProjections`) |
| command | `--filter "FullyQualifiedName~AdminConsoleActiveTenantPipelineTests\|FullyQualifiedName~AdminConsoleEndpointsPipelineTests"` → 42 (24 + 18); `--filter "FullyQualifiedName~AdminConsole"` → 78 | Observed (78 re-measured after CIR-1 §2.1) |
| ordering | The `GetAsync` fixture change must land with (or before) the filter | Same commit `a07599ce` |
| ordering | Independent of Tasks 6–8 except for sharing `AuthTestWebApplicationFactory` (Task 8's `UseSetting` and Task 6's NoOp are inherited, harmlessly) | Full suite green |
| consumer impact | `AdminConsoleTenantRepository.GetAsync` now returns rows; its only consumer is `TenantStatusCache` inside `AdminConsoleTestWebApplicationFactory` hosts, used by `AdminConsoleEndpointsPipelineTests`, `AdminConsoleActiveTenantPipelineTests` (which replaces the cache anyway), `AdminConsoleSchemaEndpointTests` and `AdminConsoleDataVolumeEndpointTests` (which use only its constants and `AdminConsoleSchemaRegistryRepository` descriptors, never a host or `AdminConsoleTenantRepository`) | `grep -rln AdminConsoleTestWebApplicationFactory Iverson.Api.Tests`; all pass |
| consumer impact | `/admin/console/metrics` (`AdminConsoleMetricsEndpoint`, a separate registration) is not one of the four and gets no filter, per the spec's "all four endpoints in `MapAdminConsoleEndpoints`" | `Program.cs` maps it with its own `MapAdminConsoleMetricsEndpoint()` |

#### Mutation checks

| Guard | Mutation | Tests that failed |
|---|---|---|
| the filter refuses inactive tenants | `if (status is null or "suspended" or "deleted")` → `if (false)` | all 12 `TenantNotActive_Returns403` cases |
| unknown (null) refused | `null or` dropped | the 4 `tenant_unknown` cases |
| suspended refused | `"suspended" or` dropped | the 4 `tenant_suspended` cases |
| deleted refused | `or "deleted"` dropped | the 4 `tenant_deleted` cases |
| no `tenant_id` passes | `if (tenantId is null) return 403;` inserted | all 4 `NoTenantIdClaim_Returns200` cases |
| empty `tenant_id` passes | `!string.IsNullOrEmpty(tenantId)` → `tenantId is not null` | all 4 `EmptyTenantIdClaim_Returns200` cases (re-measured after CIR-1 §2.1) |
| filter on `/tenants` | its `.AddEndpointFilter<ActiveTenantEndpointFilter>()` removed | 3 failed (that endpoint's unknown, suspended and deleted cases) |
| filter on `/schema` | same | 3 failed |
| filter on `/data-volume` | same | 3 failed |
| filter on `/qdrant` | same | 3 failed |

#### Counts

- **Before:** `AdminConsoleEndpointsPipelineTests` 18; `AdminConsole*` 54.
- **Red step:** 12 failed / 26 passed.
- **After:** `AdminConsole*` 78 / 0 (74 before CIR-1 §2.1 added the 4 empty-`tenant_id` cases).
- **Full `Iverson.Api.Tests` at HEAD `a07599ce`:** 1371 / 0.

## Tasks

**Ordering:**
- Task 2 builds on Task 1, which owns the `EnforceWriteAuthorization` signature.
- Task 7 builds on Task 6, which owns the revocation interfaces.
- Tasks 6 and 8 both edit `Program.cs` and the test factories, so they run in the order given.
- Tasks 3, 4, 5 and 9 are independent.
- Task 10 runs last, on the finished branch.

The plan was proven in this order: 1, 2, 6, 7, 8, 9, 4, 5, 3. Executing it as 1–10 is equivalent, because Tasks 3–5 touch no file that Tasks 6–9 touch.

### Task 1: Update is strictly an update

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs` (new `requireExistingRow` parameter; NotFound check after the denial check; audit action label follows the RPC; create-branch comment made create-only)
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs` (`Post` passes `false`, `Update` passes `true`; fetch-site comment rewritten; `42501` catch and `using Npgsql;` deleted)
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` (same three changes for `Post`/`Update`)
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/AuthorizationFieldMaskingTests.cs` (call sites gain the new argument)

**Interfaces:** Produces: `AuthorizationFieldMasking.EnforceWriteAuthorization(..., string? existingRowJson, bool requireExistingRow, AuditLog auditLog, IPayloadSizeValidator payloadSizeValidator)` (still `void`; Task 2 changes the return type).

Note on naming: the spec says "`Post` and Mapping `Create` pass `false`". Mapping has no `Create` RPC; its create RPC is `ObjectMappingGrpcService.Post`. Both `Post`s pass `false`.

- [ ] **Step 1: Delete the create-on-update and swallow tests (both test files)**

In `ObjectPersistenceGrpcServiceTests.cs` **and** `ObjectMappingGrpcServiceTests.cs`, delete these three tests outright (5 cases per file):
- `Update_ForOrdinaryCaller_WhenRowDoesNotExistYet_ForceSetsOwnerFieldToActingUserSub` (the `[Theory]` with `[InlineData(null)]`/`[InlineData("someone-else")]`);
- `Update_WithBypassRole_WhenRowDoesNotExistYet_LeavesOwnerFieldUntouched` (same shape);
- `Update_CrossTenantKeyCollidesOnUpsert_SwallowsAsSuccessAndLogsBlockedCrossTenantWrite` (its tenant-scoped-fetch assertion is carried into the new `Update_WhenNoRowExistsInTheCallersTenant_ThrowsNotFound` in Step 4).

The owner force-set coverage already exists on the create path in both files, so nothing moves: `Post_ForOrdinaryCaller_ForceSetsOwnerFieldToActingUserSub` and `Post_WithBypassRole_LeavesOwnerFieldUntouched` (each a two-case theory, `null` and `"someone-else"`), plus `Post_ForOrdinaryCaller_WithFieldPermissionRestrictingOwnerColumn_StillForceSetsOwnerField`.

Then delete the now-unused `using Npgsql;` line at the top of both test files (the swallow tests were its only users).

- [ ] **Step 2: Rewrite the `FetchByKeyAsync` default comment (both test files)**

In both constructors, replace:

```csharp
        // NSubstitute's auto-value for an unconfigured Task<string?> member is Task.FromResult(""),
        // not null — default every FetchByKeyAsync call to "row not found" so Update's new
        // pre-fetch (Task 6) doesn't try to JSON-parse an empty string in tests that don't care
        // about the pre-existing-row branch. Individual tests override this with .Returns(...)
        // for the specific TableSchema/key they need.
```

with:

```csharp
        // NSubstitute's auto-value for an unconfigured Task<string?> member is Task.FromResult(""),
        // not null — default every FetchByKeyAsync call to "row not found" so Update's pre-fetch
        // doesn't try to JSON-parse an empty string. Update answers a missing row with NotFound,
        // so every Update test that expects a write overrides this with .Returns(...) for the
        // specific TableSchema/key it needs.
```

- [ ] **Step 3: Stub an existing row in the five fixture-default tests**

`ObjectPersistenceGrpcServiceTests.cs` has no stored-row constant, so add one directly after the `private readonly ObjectPersistenceGrpcService _sut;` field:

```csharp
    private readonly ObjectPersistenceGrpcService _sut;

    // A stored AuthorSchema row in the acting user's tenant, for Update tests that need the key to
    // exist: Update answers NotFound when the tenant-scoped pre-fetch finds nothing.
    private const string ExistingAuthorJson =
        """{"Id":"11111111-0000-0000-0000-000000000001","Name":"Alice","TenantId":"test-tenant"}""";
```

Insert this stub as the line(s) immediately after `await _registry.RegisterAsync(...)` in each test below, with `<JSON>` as given:

```csharp
        _entities
            .FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns(<JSON>);
```

| File | Test | `<JSON>` |
|---|---|---|
| `ObjectPersistenceGrpcServiceTests.cs` | `Update_ExecutesSqlUpsert_WithPayloadJson` | `ExistingAuthorJson` |
| `ObjectPersistenceGrpcServiceTests.cs` | `Update_WithCarriageReturnLineFeedInKey_LogsSanitizedKeyWithoutRawNewline` | `ExistingAuthorJson` |
| `ObjectMappingGrpcServiceTests.cs` | `Update_WithValidKey_EmitsUpdatedEvent` | `AuthorJson` (existing constant) |
| `ObjectMappingGrpcServiceTests.cs` | `Update_ExecutesUpsertSql_DirectlyToPostgres` | `AuthorJson` |
| `ObjectMappingGrpcServiceTests.cs` | `Update_InsertsReconciliationQueueRowInSameTransactionAsUpsert` (registers `ArticleSchema`) | `ArticleJson` (existing constant) |

The stubs use `Arg.Any<string>()` for the key, so the CRLF test's forged key still matches.

- [ ] **Step 4: Write the NotFound, ordering and audit-label tests**

Append this section at the end of `ObjectPersistenceGrpcServiceTests` (just before the class's closing brace):

```csharp
    // ── Update is strictly an update ─────────────────────────────────────────
    //
    // Update never creates a row. A key with no row in the caller's tenant — one that exists
    // nowhere, or one owned by another tenant, which the tenant-scoped pre-fetch cannot see —
    // answers NotFound. The check sits AFTER the denial check, so a caller who is denied anyway
    // gets PermissionDenied whether or not the key exists.

    [Fact]
    public async Task Update_WhenNoRowExistsInTheCallersTenant_ThrowsNotFound()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var authorId = Guid.NewGuid().ToString();

        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(authorId),
            ["Name"] = Value.ForString("Alice Updated")
        });
        var act = () => _sut.Update(
            new PersistRequest { TypeName = "Author", Payload = payload }, TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
        ex.Which.Status.Detail.Should().Be($"'Author:{authorId}' not found.");
        await _entities.Received(1).FetchByKeyAsync(
            Arg.Any<TableSchema>(), authorId, EntityAccess.ForTenant("test-tenant"));
    }

    [Fact]
    public async Task Update_WhenNotFound_WritesNothingPublishesNothingAndAuditsNothing()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());

        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(Guid.NewGuid().ToString()),
            ["Name"] = Value.ForString("Alice Updated")
        });
        var act = () => _sut.Update(
            new PersistRequest { TypeName = "Author", Payload = payload }, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.NotFound);
        await _txRunner.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>());
        await _events.DidNotReceive().ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityEvent>());
        _auditLogger.DidNotReceive().Log(
            LogLevel.Warning, Arg.Any<EventId>(), Arg.Any<object>(), Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task Update_DeniedCallerWithNoExistingRow_GetsPermissionDeniedNotNotFound()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema() with { Authorization = null });

        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(Guid.NewGuid().ToString()),
            ["Name"] = Value.ForString("Alice Updated")
        });
        var act = () => _sut.Update(
            new PersistRequest { TypeName = "Author", Payload = payload }, TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        AssertAuditLogged("AccessDenied");
    }

    [Fact]
    public async Task Update_DeniedCallerWithNoExistingRow_IsAuditedAsAnUpdate()
    {
        // The audit action follows the RPC, not the row: with no stored row, deriving it from the
        // row would label this denied Update a "Create".
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema() with { Authorization = null });

        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(Guid.NewGuid().ToString()),
            ["Name"] = Value.ForString("Alice Updated")
        });
        var act = () => _sut.Update(
            new PersistRequest { TypeName = "Author", Payload = payload }, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.PermissionDenied);
        AssertAuditLogged("action=Update resourceType=Author");
    }

    [Fact]
    public async Task Update_SmuggledTenantColumnWithNoExistingRow_GetsInvalidArgumentNotNotFound()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var payload = MakePayload(new()
        {
            ["Id"]                              = Value.ForString(Guid.NewGuid().ToString()),
            ["Name"]                            = Value.ForString("Alice Updated"),
            [SchemaDescriptor.TenantColumnName] = Value.ForString("test-tenant")
        });
        var act = () => _sut.Update(
            new PersistRequest { TypeName = "Author", Payload = payload }, TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("reserved server-owned column");
    }
```

Insert this section in `ObjectMappingGrpcServiceTests` immediately before the `// ── Delete ──…` section comment (i.e. after `Update_OwnerImmutable_LogsAuditDeniedWithOwnerImmutable`):

```csharp
    // ── Update is strictly an update ─────────────────────────────────────────
    //
    // Update never creates a row. A key with no row in the caller's tenant — one that exists
    // nowhere, or one owned by another tenant, which the tenant-scoped pre-fetch cannot see —
    // answers NotFound, with the same text Get and Delete use. The check sits AFTER the denial
    // check, so a caller who is denied anyway gets PermissionDenied whether or not the key exists.

    [Fact]
    public async Task Update_WhenNoRowExistsInTheCallersTenant_ThrowsNotFound()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());

        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(AuthorId),
            ["Name"] = Value.ForString("Alice Updated")
        });
        var act = () => _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
        ex.Which.Status.Detail.Should().Be($"'Author:{AuthorId}' not found.");
        await _entities.Received(1).FetchByKeyAsync(
            Arg.Any<TableSchema>(), AuthorId, EntityAccess.ForTenant("test-tenant"));
    }

    [Fact]
    public async Task Update_WhenNotFound_WritesNothingPublishesNothingAndAuditsNothing()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());

        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(AuthorId),
            ["Name"] = Value.ForString("Alice Updated")
        });
        var act = () => _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.NotFound);
        await _txRunner.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>());
        await _events.DidNotReceive().ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityEvent>());
        _auditLogger.DidNotReceive().Log(
            LogLevel.Warning, Arg.Any<EventId>(), Arg.Any<object>(), Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task Update_DeniedCallerWithNoExistingRow_GetsPermissionDeniedNotNotFound()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema() with { Authorization = null });

        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(AuthorId),
            ["Name"] = Value.ForString("Alice Updated")
        });
        var act = () => _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        AssertAuditLogged("AccessDenied");
    }

    [Fact]
    public async Task Update_DeniedCallerWithNoExistingRow_IsAuditedAsAnUpdate()
    {
        // The audit action follows the RPC, not the row: with no stored row, deriving it from the
        // row would label this denied Update a "Create".
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema() with { Authorization = null });

        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(AuthorId),
            ["Name"] = Value.ForString("Alice Updated")
        });
        var act = () => _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.PermissionDenied);
        AssertAuditLogged("action=Update resourceType=Author");
    }

    [Fact]
    public async Task Update_SmuggledTenantColumnWithNoExistingRow_GetsInvalidArgumentNotNotFound()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var payload = MakePayload(new()
        {
            ["Id"]                              = Value.ForString(AuthorId),
            ["Name"]                            = Value.ForString("Alice Updated"),
            [SchemaDescriptor.TenantColumnName] = Value.ForString("test-tenant")
        });
        var act = () => _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("reserved server-owned column");
    }
```

- [ ] **Step 5: Run the tests to verify they fail**

```bash
cd Iverson.Server && dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~Iverson.Api.Tests.Grpc.ObjectPersistenceGrpcServiceTests|FullyQualifiedName~Iverson.Api.Tests.Grpc.ObjectMappingGrpcServiceTests"
```

Expected: `Failed: 6, Passed: 145, Total: 151`. The six failures are `Update_WhenNoRowExistsInTheCallersTenant_ThrowsNotFound`, `Update_WhenNotFound_WritesNothingPublishesNothingAndAuditsNothing` and `Update_DeniedCallerWithNoExistingRow_IsAuditedAsAnUpdate` in each class (the last because the gate labels a denied write with no stored row "Create"). The denied and smuggled ordering tests pass already; their job is to pin the order once the check exists.

- [ ] **Step 6: Add the parameter and the NotFound check to `EnforceWriteAuthorization`**

In `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs`, replace the `existingRowJson` param doc:

```csharp
    /// <param name="existingRowJson">
    /// JSON of the row being written, or null when there is no pre-existing row — either
    /// because this is a pure create (Post) or because Update's key doesn't exist yet (the
    /// upsert will create it). When null, ownership is force-set rather than validated.
    /// </param>
```

with:

```csharp
    /// <param name="existingRowJson">
    /// JSON of the row being written, or null when there is no pre-existing row. Null on a create
    /// (Post), where ownership is force-set rather than validated; on an Update, null means the
    /// key has no row in the caller's tenant, which <paramref name="requireExistingRow"/> turns
    /// into NotFound.
    /// </param>
    /// <param name="requireExistingRow">
    /// True for Update, which never creates a row: a null <paramref name="existingRowJson"/>
    /// answers NotFound. False for Post.
    /// </param>
```

Add the parameter after `existingRowJson`:

```csharp
        string? existingRowJson,
        bool requireExistingRow,
        AuditLog auditLog,
        IPayloadSizeValidator payloadSizeValidator)
```

Make the audit action label follow the RPC rather than the row. Replace the first line of the body:

```csharp
        var auditAction = existingRowJson is null ? "Create" : "Update";
```

with:

```csharp
        // The RPC, not the row: a denied Update of a key with no stored row is still an Update.
        var auditAction = requireExistingRow || existingRowJson is not null ? "Update" : "Create";
```

Then replace, immediately after the `decision.Denied` block:

```csharp
            throw new RpcException(new Status(StatusCode.PermissionDenied, deniedMessage));
        }

        if (existingRowJson is null)
        {
            // No pre-existing row — either a pure create (Post) or an Update whose key
            // doesn't exist yet and will be created by the upsert. Force-set the tenant
            // column unconditionally (tenant is strictly additive — it applies to bypass
            // callers too, unlike ownership below). Force-set the owner field for
            // ownership-required callers; leave it untouched for bypass callers.
```

with:

```csharp
            throw new RpcException(new Status(StatusCode.PermissionDenied, deniedMessage));
        }

        // Update never creates a row (CSR round 10 Finding #10). AFTER the denial throw, never
        // before it: a caller who is denied anyway must get PermissionDenied whether or not the
        // key exists, or the status would tell them which keys exist. The existing-row read is
        // tenant-scoped, so a key that exists nowhere and a key owned by another tenant are both
        // null here and get the same answer. No audit entry, matching Mapping Get's not-found.
        if (requireExistingRow && existingRowJson is null)
            throw new RpcException(new Status(StatusCode.NotFound,
                $"'{schema.TypeName}:{resourceKey}' not found."));

        if (existingRowJson is null)
        {
            // No pre-existing row: a create (Post). Force-set the tenant column unconditionally
            // (tenant is strictly additive — it applies to bypass callers too, unlike ownership
            // below). Force-set the owner field for ownership-required callers; leave it
            // untouched for bypass callers.
```

`resourceKey` is the method's existing `StructFieldAccess.GetFieldString(payload, schema.KeyColumn.Name)`; it uses the same canonical-then-camelCase lookup as `EntityKeyAccessor.ExtractKey`, which both Update RPCs have already checked is non-empty.

- [ ] **Step 7: Wire `ObjectPersistenceGrpcService`**

In `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs`, delete `using Npgsql;`.

In `Post`:

```csharp
            "Not authorized to create this entity.",
            existingRowJson: null,
            requireExistingRow: false,
            auditLog,
            payloadSizeValidator);
```

In `Update`, replace the fetch-site comment:

```csharp
        // Narrowed to the acting tenant (CSR round 9 Finding #5): a cross-tenant read here let
        // EnforceWriteAuthorization observe a foreign-tenant row and deny on tenant mismatch,
        // which made "key exists under another tenant" and "key doesn't exist" distinguishable
        // to the caller — an information-disclosure oracle. Scoping to ForTenant makes a foreign
        // row genuinely invisible; a write against its key now falls through to the create path
        // below and collides with the real row at the database layer under RLS, which is caught
        // there and swallowed as a fake success.
```

with:

```csharp
        // Narrowed to the acting tenant (CSR round 9 Finding #5): a cross-tenant read here let
        // EnforceWriteAuthorization observe a foreign-tenant row and deny on tenant mismatch,
        // which made "key exists under another tenant" and "key doesn't exist" distinguishable
        // to the caller — an information-disclosure oracle. Scoping to ForTenant makes a foreign
        // row genuinely invisible, so both cases come back null and EnforceWriteAuthorization
        // answers both with the same NotFound (CSR round 10 Finding #10): Update never creates.
```

pass the flag:

```csharp
            "Not authorized to update this entity.",
            existingRowJson,
            requireExistingRow: true,
            auditLog,
            payloadSizeValidator);
```

and replace the try/catch:

```csharp
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
            logger.LogWarning(
                "[Persistence.Update] RLS collision swallowed as success: type={Type} key={Key} traceId={TraceId} message={Message}",
                schema.TypeName.SanitizeForLog(), key.SanitizeForLog(), request.TraceId.SanitizeForLog(), ex.MessageText.SanitizeForLog());
            auditLog.Denied(actingUserAccessor.ActingUser, "Update", schema.TypeName, key, "BlockedCrossTenantWrite");
            return new PersistResponse { Success = true, Key = key, TraceId = request.TraceId };
        }
```

with:

```csharp
        var outboxRowId = await outboxWriter.UpsertAndEnqueueOutboxAsync(
            SchemaBuilder.ToTableSchema(schema),
            request.TypeName,
            key,
            payloadJson,
            tenantId: decision.TenantValue);
```

- [ ] **Step 8: Wire `ObjectMappingGrpcService`**

In `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`, delete `using Npgsql;`.

In `Post`, replace:

```csharp
            AuthorizationAction.Write, "Not authorized to create this entity.", existingRowJson: null, _auditLog,
            _payloadSizeValidator);
```

with:

```csharp
            AuthorizationAction.Write, "Not authorized to create this entity.", existingRowJson: null,
            requireExistingRow: false, _auditLog, _payloadSizeValidator);
```

In `Update`, replace the fetch-site comment:

```csharp
        // Narrowed to the acting tenant (CSR round 9 Finding #5) — see the identical read in
        // ObjectPersistenceGrpcService.Update for why a cross-tenant read here was an
        // information-disclosure oracle, and how the RLS collision below replaces it.
```

with:

```csharp
        // Narrowed to the acting tenant (CSR round 9 Finding #5) — see the identical read in
        // ObjectPersistenceGrpcService.Update for why a cross-tenant read here was an
        // information-disclosure oracle. A key with no row in this tenant, foreign or absent,
        // gets NotFound from EnforceWriteAuthorization (CSR round 10 Finding #10).
```

pass the flag:

```csharp
            "Not authorized to update this entity.",
            existingRowJson,
            requireExistingRow: true,
            _auditLog,
            _payloadSizeValidator);
```

and replace the try/catch:

```csharp
        Guid outboxRowId;
        try
        {
            outboxRowId = await _outboxWriter.UpsertAndEnqueueOutboxAsync(
                SchemaBuilder.ToTableSchema(schema), request.TypeName, key, payloadJson,
                tenantId: decision.TenantValue);
        }
        catch (PostgresException ex) when (ex.SqlState == "42501" && ex.MessageText.Contains("row-level security policy"))
        {
            _logger.LogWarning(
                "[Mapping.Update] RLS collision swallowed as success: type={Type} key={Key} traceId={TraceId} message={Message}",
                schema.TypeName.SanitizeForLog(), key.SanitizeForLog(), request.TraceId.SanitizeForLog(), ex.MessageText.SanitizeForLog());
            _auditLog.Denied(_actingUserAccessor.ActingUser, "Update", schema.TypeName, key, "BlockedCrossTenantWrite");
            AuthorizationFieldMasking.RemoveTenantColumn(request.Payload);
            return new MappingResponse { Success = true, Data = request.Payload, TraceId = request.TraceId };
        }
```

with:

```csharp
        var outboxRowId = await _outboxWriter.UpsertAndEnqueueOutboxAsync(
            SchemaBuilder.ToTableSchema(schema), request.TypeName, key, payloadJson,
            tenantId: decision.TenantValue);
```

- [ ] **Step 9: Update the `EnforceWriteAuthorization` call sites in `AuthorizationFieldMaskingTests.cs`**

All seven calls gain the new argument on the line after their `existingRowJson` argument:
- the four that pass `existingRowJson: null` (the `Enforce` helper, `EnforceWriteAuthorization_FieldRestrictedCaller_NestedRelationStructNotRejected`, `EnforceWriteAuthorization_PayloadWithoutTheTenantColumn_IsUnaffected`, `EnforceWriteAuthorization_OversizedTextColumn_ThrowsInvalidArgument`): add `requireExistingRow: false,`, e.g.

```csharp
            existingRowJson: null,
            requireExistingRow: false,
            new AuditLog(NullLogger<AuditLog>.Instance),
```
- the two that pass a JSON literal (`EnforceWriteAuthorization_ExistingRow_ForceSetsTheTenantColumnIntoThePayload`, `EnforceWriteAuthorization_TenantMismatchWithOversizedPayload_StillGetsPermissionDenied`): add `requireExistingRow: true,`.
- the `EnforceAndCatch` helper, which forwards its `existingRowJson` parameter:

```csharp
            existingRowJson,
            requireExistingRow: existingRowJson is not null,
            new AuditLog(NullLogger<AuditLog>.Instance),
            payloadSizeValidator ?? Substitute.For<IPayloadSizeValidator>());
```

- [ ] **Step 10: Run the tests**

```bash
cd Iverson.Server && dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~Iverson.Api.Tests.Grpc.ObjectPersistenceGrpcServiceTests|FullyQualifiedName~Iverson.Api.Tests.Grpc.ObjectMappingGrpcServiceTests|FullyQualifiedName~Iverson.Api.Tests.Grpc.AuthorizationFieldMaskingTests"
```

Expected: `Passed: 170, Failed: 0, Total: 170` (baseline 170 = 43 + 108 + 19; minus 10 deleted cases; plus 10 new). `Update_WithNoActingUser_ThrowsPermissionDenied` and `Update_WithNoAuthorizationRulesConfigured_ThrowsPermissionDenied` pass unedited in both classes.

- [ ] **Step 11: Commit**

```bash
git add Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/AuthorizationFieldMaskingTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs
git commit -m "answer update of a key with no row in the caller's tenant with notfound instead of creating it

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 2: Restricted fields carried forward; tenant column exempt

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs` (`RejectDisallowedFields` takes an exemption collection; new `CarryForwardRestrictedFields` and private `CarryForward`; `EnforceWriteAuthorization` exempts owner + tenant, carries restricted fields and an omitted owner column forward on update, and returns the carried keys)
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` (`Update` masks its response for Read, then removes the carried keys)
- Modify: `Iverson.Server/Iverson.Api.Tests/Helpers/SchemaFixtures.cs` (reserved-`__TenantId` Dossier fixtures)
- Create: `Iverson.Server/Iverson.Api.Tests/Grpc/UpdateCarryForwardPostgresIntegrationTests.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/AuthorizationFieldMaskingTests.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`

**Interfaces:** Consumes: Task 1's `EnforceWriteAuthorization(..., string? existingRowJson, bool requireExistingRow, ...)`. Produces: `EnforceWriteAuthorization` returns `IReadOnlyCollection<string>`; `RejectDisallowedFields(Struct, IReadOnlySet<string>?, IReadOnlyCollection<string> exemptFields)`; `CarryForwardRestrictedFields(Struct payload, Struct existingRow, IReadOnlySet<string>? allowedFields) : IReadOnlyCollection<string>`. The returned collection also includes the owner column when the gate carried it forward.

`ObjectPersistenceGrpcService` and both `Post`s are untouched: they call `EnforceWriteAuthorization` as a statement and discard the (empty, for creates) result. `PersistResponse` carries only the key, so Persistence Update needs no response masking.

- [ ] **Step 1: Add the reserved-tenant fixtures**

The class's doc comment claims every descriptor in the file is legacy-shaped; correct it. Replace:

```csharp
/// Every descriptor in this file — and 45 sites across Iverson.Api.Tests — sets
/// <c>TenantColumn = "TenantId"</c>, the CLIENT-DECLARED legacy shape, against 18 sites using
/// <c>SchemaDescriptor.TenantColumnName</c>. So this assembly's DEFAULT descriptor is one
/// <c>SchemaBuilder.BuildDescriptor</c> can no longer produce.
```

with:

```csharp
/// Every descriptor in this file except the reserved-tenant Dossier fixtures at the end — and 45
/// sites across Iverson.Api.Tests — sets <c>TenantColumn = "TenantId"</c>, the CLIENT-DECLARED
/// legacy shape, against 18 sites using <c>SchemaDescriptor.TenantColumnName</c>. So this
/// assembly's DEFAULT descriptor is one <c>SchemaBuilder.BuildDescriptor</c> can no longer produce.
```

Then append to `SchemaFixtures` (before the class's closing brace) in `Iverson.Server/Iverson.Api.Tests/Helpers/SchemaFixtures.cs`:

```csharp
    // ── Reserved-tenant fixtures ──────────────────────────────────────────────
    //
    // Everything above uses the legacy client-declared TenantColumn = "TenantId". These use the
    // reserved SchemaDescriptor.TenantColumnName, the shape SchemaBuilder.BuildDescriptor produces,
    // because the field-restricted write path depends on which shape it gets:
    // RowFieldAuthorizationEvaluator never lists the reserved column in AllowedFields.
    //
    // Dossier's field permissions are the same in every variant:
    //   Secret, SealedAt, Seal: anyone may read, only "premium" may write;
    //   Notes:                  anyone may write, only "premium" may read.
    // The default acting user (ActingUserFixtures.Principal("test-user", "test-bypass")) holds
    // neither "premium" grant, so it is field-restricted on both actions.

    // "test-bypass" may read and write every row. No OwnerField.
    public static SchemaDescriptor ReservedTenantDossierSchema() =>
        DossierSchema(ownerField: null, [new RowPermission("test-bypass", true, true, true)]);

    // No row permission, so every caller is ownership-scoped through OwnerId.
    public static SchemaDescriptor ReservedTenantOwnedDossierSchema() =>
        DossierSchema(ownerField: "OwnerId", []);

    // "test-bypass" may write every row but has no CanReadAll: CanWriteAll without CanReadAll.
    // ownerFieldName lets a test register the OwnerField spelled unlike its column ("ownerId"),
    // which registration accepts because it matches columns case-insensitively.
    public static SchemaDescriptor ReservedTenantWriteOnlyDossierSchema(bool withOwnerField, string ownerFieldName = "OwnerId") =>
        DossierSchema(withOwnerField ? ownerFieldName : null, [new RowPermission("test-bypass", false, true, false)]);

    private static SchemaDescriptor DossierSchema(string? ownerField, List<RowPermission> rowPermissions) => new()
    {
        TypeName       = "Dossier",
        TableName      = "dossiers",
        CollectionName = null,
        KeyColumn      = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns  =
        [
            new ColumnDescriptor("Title",    "TEXT",        true),
            new ColumnDescriptor("Secret",   "TEXT",        true),
            new ColumnDescriptor("SealedAt", "TIMESTAMPTZ", true),
            new ColumnDescriptor("Seal",     "BYTEA",       true),
            new ColumnDescriptor("Notes",    "TEXT",        true),
            new ColumnDescriptor("OwnerId",  "TEXT",        true),
            new ColumnDescriptor(SchemaDescriptor.TenantColumnName, "TEXT", false),
        ],
        FkColumns      = [],
        VectorFields   = [],
        ChunkFields    = [],
        Relations      = [],
        Authorization  = new AuthorizationRules(
            ownerField,
            rowPermissions,
            new List<FieldPermission>
            {
                new("Secret",   new List<string>(), new List<string> { "premium" }),
                new("SealedAt", new List<string>(), new List<string> { "premium" }),
                new("Seal",     new List<string>(), new List<string> { "premium" }),
                new("Notes",    new List<string> { "premium" }, new List<string>()),
            }),
        TenantColumn   = SchemaDescriptor.TenantColumnName
    };
```

- [ ] **Step 2: Write the field-masking tests**

In `Iverson.Server/Iverson.Api.Tests/Grpc/AuthorizationFieldMaskingTests.cs`, add `using Iverson.Api.Tests.Helpers;` after `using Iverson.Api.Schema;`, then append this section at the end of the class:

```csharp
    // ── Restricted fields carried forward; the forced tenant column exempt (CSR round 10 #8) ──
    //
    // An update is a full-row replace in Postgres and StarRocks, so a field the caller may not
    // write, omitted from its payload, used to be cleared. The stored value is now carried into the
    // payload instead. Real evaluator and reserved-tenant fixtures throughout: the defect lived in
    // how the evaluator's AllowedFields (which never lists the reserved tenant column) met the
    // force-set column.

    private const string DossierId = "33333333-0000-0000-0000-000000000003";

    private static string StoredDossierJson(string ownerId = "test-user") =>
        $$"""{"Id":"{{DossierId}}","Title":"old title","Secret":"classified","SealedAt":"2026-10-03T12:34:56.789+00:00","Seal":"\\x01ff","Notes":"old notes","OwnerId":"{{ownerId}}","__TenantId":"test-tenant"}""";

    private static IReadOnlyCollection<string> EnforceWithRealEvaluator(
        SchemaDescriptor schema, Struct payload, string? existingRowJson, ClaimsPrincipal? actingUser = null) =>
        AuthorizationFieldMasking.EnforceWriteAuthorization(
            new RowFieldAuthorizationEvaluator(),
            actingUser ?? ActingUserFixtures.Principal("test-user", "test-bypass"),
            schema,
            payload,
            AuthorizationAction.Write,
            "Not authorized to write this entity.",
            existingRowJson,
            requireExistingRow: existingRowJson is not null,
            new AuditLog(NullLogger<AuditLog>.Instance),
            Substitute.For<IPayloadSizeValidator>());

    [Fact]
    public void EnforceWriteAuthorization_FieldRestrictedCreate_OnAReservedTenantSchema_Succeeds()
    {
        // The coupled defect: AllowedFields never lists the reserved tenant column, so the column
        // the server had just force-set was itself rejected as a field the caller may not write.
        var payload = new Struct { Fields = { ["Title"] = Value.ForString("new title") } };

        var carried = EnforceWithRealEvaluator(
            SchemaFixtures.ReservedTenantDossierSchema(), payload, existingRowJson: null);

        carried.Should().BeEmpty();
        payload.Fields[SchemaDescriptor.TenantColumnName].StringValue.Should().Be("test-tenant");
    }

    [Fact]
    public void EnforceWriteAuthorization_FieldRestrictedUpdate_CarriesOmittedRestrictedFieldsForward()
    {
        var payload = new Struct
        {
            Fields = { ["Id"] = Value.ForString(DossierId), ["Title"] = Value.ForString("new title") }
        };

        var carried = EnforceWithRealEvaluator(
            SchemaFixtures.ReservedTenantDossierSchema(), payload, StoredDossierJson());

        carried.Should().BeEquivalentTo("Secret", "SealedAt", "Seal");
        payload.Fields["Secret"].StringValue.Should().Be("classified");
        payload.Fields["SealedAt"].StringValue.Should().Be("2026-10-03T12:34:56.789+00:00");
        payload.Fields["Seal"].StringValue.Should().Be("\\x01ff");
        payload.Fields[SchemaDescriptor.TenantColumnName].StringValue.Should().Be("test-tenant");
        // Only fields the caller may NOT write are carried. Title it sent; Notes and OwnerId it may
        // write and left out, so the full-row replace clears them, as it always has.
        payload.Fields["Title"].StringValue.Should().Be("new title");
        payload.Fields.Should().NotContainKey("Notes");
        payload.Fields.Should().NotContainKey("OwnerId");
    }

    [Fact]
    public void EnforceWriteAuthorization_FieldRestrictedUpdate_WithTheRestrictedFieldPresent_IsStillRejected()
    {
        var payload = new Struct
        {
            Fields =
            {
                ["Id"]     = Value.ForString(DossierId),
                ["Title"]  = Value.ForString("new title"),
                ["Secret"] = Value.ForString("overwritten"),
            }
        };

        var act = () => EnforceWithRealEvaluator(
            SchemaFixtures.ReservedTenantDossierSchema(), payload, StoredDossierJson());

        var ex = act.Should().Throw<RpcException>().Which;
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain("not permitted").And.Contain("Secret");
    }

    [Fact]
    public void EnforceWriteAuthorization_UpdateByACallerWithNoFieldRestriction_CarriesNothing()
    {
        // "premium" may write every field, so the write decision's AllowedFields is null: the
        // caller owns the whole row and an omitted field is cleared, exactly as before.
        var payload = new Struct
        {
            Fields = { ["Id"] = Value.ForString(DossierId), ["Title"] = Value.ForString("new title") }
        };

        var carried = EnforceWithRealEvaluator(
            SchemaFixtures.ReservedTenantDossierSchema(), payload, StoredDossierJson(),
            ActingUserFixtures.Principal("test-user", "test-bypass", "premium"));

        carried.Should().BeEmpty();
        payload.Fields.Keys.Should().BeEquivalentTo("Id", "Title", SchemaDescriptor.TenantColumnName);
    }

    [Fact]
    public void EnforceWriteAuthorization_CamelCasePayloadKey_MatchesTheCanonicalStoredKey()
    {
        // The owner field is exempt from rejection, so an ownership-scoped caller restricted from
        // writing it may still send it — and the .NET client sends it camelCase. Carrying the
        // stored "OwnerId" over the payload's "ownerId" would leave both keys, and SerializePayload
        // rejects two keys that fold to one.
        var schema = SchemaFixtures.ReservedTenantOwnedDossierSchema();
        schema = schema with
        {
            Authorization = schema.Authorization! with
            {
                FieldPermissions = schema.Authorization.FieldPermissions
                    .Append(new FieldPermission("OwnerId", new List<string>(), new List<string> { "premium" }))
                    .ToList()
            }
        };
        var payload = new Struct
        {
            Fields =
            {
                ["id"]      = Value.ForString(DossierId),
                ["title"]   = Value.ForString("new title"),
                ["ownerId"] = Value.ForString("test-user"),
            }
        };

        var carried = EnforceWithRealEvaluator(schema, payload, StoredDossierJson(ownerId: "test-user"));

        carried.Should().NotContain("OwnerId");
        payload.Fields.Should().NotContainKey("OwnerId");
        payload.Fields["ownerId"].StringValue.Should().Be("test-user");
        var act = () => StructSerializer.SerializePayload(payload);
        act.Should().NotThrow();
    }
```

- [ ] **Step 3: Write the Mapping response tests**

Append this section at the end of `ObjectMappingGrpcServiceTests` (after `Get_OmitsTheServerOwnedTenantColumnFromTheResponse`; it reuses that section's `CaptureEvents()` helper). The owner theory covers both an ownership-scoped writer and a `CanWriteAll` bypass writer omitting `OwnerId`:

```csharp
    // ── Update response: masked for Read, carried-forward values removed ──────
    //
    // EnforceWriteAuthorization carries the stored values of fields the caller may not write into
    // request.Payload (CSR round 10 Finding #8), and Update returns that same object as
    // MappingResponse.Data. The published payload must keep those values — it is the full-row
    // replace StarRocks applies — but the response must not hand back what Get would not: fields
    // the caller may not read, and carried values from a row Get would refuse outright.

    private const string DossierKey = "33333333-0000-0000-0000-000000000003";

    private void StubStoredDossier(string ownerId) =>
        _entities
            .FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns($$"""{"Id":"{{DossierKey}}","Title":"old title","Secret":"classified","SealedAt":"2026-10-03T12:34:56.789+00:00","Seal":"\\x01ff","Notes":"old notes","OwnerId":"{{ownerId}}","__TenantId":"test-tenant"}""");

    private Task<MappingResponse> UpdateDossierAsync(Dictionary<string, Value> fields) =>
        _sut.Update(new MappingWriteRequest { TypeName = "Dossier", Payload = MakePayload(fields) }, MakeContext());

    [Fact]
    public async Task Update_ResponseOmitsFieldsTheCallerMayNotRead_WhileThePublishedPayloadKeepsThem()
    {
        await _registry.RegisterAsync(SchemaFixtures.ReservedTenantDossierSchema());
        StubStoredDossier(ownerId: "someone-else");
        var events = CaptureEvents();

        var response = await UpdateDossierAsync(new()
        {
            ["Id"]    = Value.ForString(DossierKey),
            ["Title"] = Value.ForString("new title"),
            ["Notes"] = Value.ForString("new notes"),
        });

        response.Success.Should().BeTrue();
        response.Data.Fields["Title"].StringValue.Should().Be("new title");
        response.Data.Fields.Should().NotContainKey("Notes");   // writable, not readable
        response.Data.Fields.Should().NotContainKey("Secret");  // carried forward
        response.Data.Fields.Should().NotContainKey(SchemaDescriptor.TenantColumnName);
        events.Should().ContainSingle();
        events[0].PayloadJson.Should().Contain("\"Notes\":\"new notes\"")
            .And.Contain("\"Secret\":\"classified\"");
    }

    [Fact]
    public async Task Update_ByAWriterWithoutReadAccess_OnATypeWithNoOwnerField_OmitsCarriedFieldsFromTheResponse()
    {
        // CanWriteAll without CanReadAll and no OwnerField: the read decision is Denied, so its
        // AllowedFields is null and read masking alone would return every carried value.
        await _registry.RegisterAsync(SchemaFixtures.ReservedTenantWriteOnlyDossierSchema(withOwnerField: false));
        StubStoredDossier(ownerId: "someone-else");
        var events = CaptureEvents();

        var response = await UpdateDossierAsync(new()
        {
            ["Id"]    = Value.ForString(DossierKey),
            ["Title"] = Value.ForString("new title"),
        });

        response.Success.Should().BeTrue();
        response.Data.Fields.Keys.Should().NotContain(["Secret", "SealedAt", "Seal"]);
        events.Should().ContainSingle();
        events[0].PayloadJson.Should().Contain("\"Secret\":\"classified\"");
    }

    [Fact]
    public async Task Update_ByAWriterWithoutReadAccess_OfARowItDoesNotOwn_OmitsCarriedFieldsFromTheResponse()
    {
        // Same role, on a type with an OwnerField: the write is a CanWriteAll bypass, but reading is
        // ownership-scoped and this row is someone else's, so Get would answer not-found. Its read
        // AllowedFields still lists Secret, so read masking alone would return the carried value.
        await _registry.RegisterAsync(SchemaFixtures.ReservedTenantWriteOnlyDossierSchema(withOwnerField: true));
        StubStoredDossier(ownerId: "someone-else");
        var events = CaptureEvents();

        var response = await UpdateDossierAsync(new()
        {
            ["Id"]    = Value.ForString(DossierKey),
            ["Title"] = Value.ForString("new title"),
        });

        response.Success.Should().BeTrue();
        response.Data.Fields.Keys.Should().NotContain(["Secret", "SealedAt", "Seal"]);
        events.Should().ContainSingle();
        events[0].PayloadJson.Should().Contain("\"Secret\":\"classified\"");
    }

    /// <summary>
    /// Runs the upsert+outbox transaction against a fake context and returns the JSON the entity
    /// upsert received — the full row Postgres will hold (the shape of OutboxWriterTests'
    /// CaptureUpsertJsonAsync).
    /// </summary>
    private List<string> CaptureUpsertedJson()
    {
        var upserted = new List<string>();
        var tx = Substitute.For<IDbTransactionContext>();
        tx.WhenForAnyArgs(t => t.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()))
          .Do(call =>
          {
              if (!call.ArgAt<string>(0).Contains("json_populate_record")) return;
              var param = call.ArgAt<object?>(1);
              upserted.Add((string)param!.GetType().GetProperty("Json")!.GetValue(param)!);
          });
        _txRunner.ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>())
            .Returns(ci => ci.Arg<Func<IDbTransactionContext, Task>>()(tx));
        return upserted;
    }

    [Theory]
    [InlineData(false, "test-user",    "OwnerId")] // ownership-scoped: may update only its own row
    [InlineData(true,  "someone-else", "OwnerId")] // CanWriteAll bypass: may update anyone's row
    [InlineData(true,  "someone-else", "ownerId")] // OwnerField spelled unlike its stored column
    public async Task Update_OmittingTheOwnerColumn_KeepsTheStoredOwner_AndTheResponseDoesNotEchoIt(
        bool bypassWriter, string storedOwner, string ownerField)
    {
        // The owner column is writable, so restricted-field carry-forward never reaches it, and
        // OwnerImmutable only compares an owner that is present. Without the owner carry-forward
        // this full-row upsert would write the row with a NULL owner.
        await _registry.RegisterAsync(bypassWriter
            ? SchemaFixtures.ReservedTenantWriteOnlyDossierSchema(withOwnerField: true, ownerFieldName: ownerField)
            : SchemaFixtures.ReservedTenantOwnedDossierSchema());
        StubStoredDossier(ownerId: storedOwner);
        var upserted = CaptureUpsertedJson();

        var response = await UpdateDossierAsync(new()
        {
            ["Id"]    = Value.ForString(DossierKey),
            ["Title"] = Value.ForString("new title"),
        });

        response.Success.Should().BeTrue();
        upserted.Should().ContainSingle().Which.Should().Contain($"\"OwnerId\":\"{storedOwner}\"");
        response.Data.Fields.Should().NotContainKey("OwnerId");
    }
```

- [ ] **Step 4: Write the Postgres carry-forward test**

Create `Iverson.Server/Iverson.Api.Tests/Grpc/UpdateCarryForwardPostgresIntegrationTests.cs`. It reuses the assembly's existing `ReconciliationQueuePostgresContainerFixture` (`Reconciliation/ReconciliationQueuePostgresIntegrationTests.cs`: `postgres:16-alpine`, `PostgresRepository` + `PostgresSchemaManager`, `EnsureRolesAsync` on init) through its own `IClassFixture`, so it gets its own container, and joins `ContainerCollection` as that file's doc requires:

```csharp
using System.Text.Json;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Iverson.Api.Authorization;
using Iverson.Api.Grpc;
using Iverson.Api.Reconciliation;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Api.Tests.Reconciliation;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

/// <summary>
/// Carry-forward (CSR round 10 Finding #8) against a real Postgres: a stored value leaves as
/// <c>row_to_json</c> text through <c>FetchByKeyAsync</c>, travels through the payload Struct and
/// <c>SerializePayload</c>, and goes back in through <c>OutboxWriter</c>'s
/// <c>json_populate_record</c> full-row upsert. A mock cannot show that a <c>TIMESTAMPTZ</c> or a
/// <c>BYTEA</c> value survives that round trip unchanged.
/// </summary>
[Trait("Category", "Integration")]
[Collection(ContainerCollection.Name)]
public sealed class UpdateCarryForwardPostgresIntegrationTests(ReconciliationQueuePostgresContainerFixture fixture)
    : IClassFixture<ReconciliationQueuePostgresContainerFixture>
{
    private readonly PostgresRepository _repo = fixture.Repository;
    private readonly PostgresSchemaManager _schemaManager = fixture.SchemaManager;

    [Fact]
    public async Task Update_ByAFieldRestrictedCaller_LeavesOmittedRestrictedValuesIntactInPostgres()
    {
        var schema = SchemaFixtures.ReservedTenantDossierSchema() with
        {
            TableName = "dossiers_" + Guid.NewGuid().ToString("N")[..8]
        };
        var table = SchemaBuilder.ToTableSchema(schema);
        await _schemaManager.ApplySchemaAsync(table);
        await _schemaManager.ApplySchemaAsync(ReconciliationSchema.Table);

        var key = Guid.CreateVersion7();
        // The container's connection is a superuser, so this seed bypasses RLS.
        await _repo.ExecuteAsync(
            $"""
            INSERT INTO "{schema.TableName}" ("Id", "Title", "Secret", "SealedAt", "Seal", "{SchemaDescriptor.TenantColumnName}")
            VALUES (@Id, 'old title', 'classified', '2026-10-03 12:34:56.789+00', '\x01ff'::bytea, 'test-tenant')
            """,
            new { Id = key });

        var registry = new SchemaRegistry(new SchemaRegistryRepository(_repo), NullLogger<SchemaRegistry>.Instance);
        await registry.RegisterAsync(schema);
        var sut = new ObjectPersistenceGrpcService(
            Substitute.For<IOutboxPublisher>(),
            registry,
            new RelationValidator(),
            new PayloadSizeValidator(),
            new EntityKeyAccessor(),
            new OutboxWriter(ReconciliationSchema.TableName, _repo, _repo),
            NullLogger<ObjectPersistenceGrpcService>.Instance,
            new EntityRepository(_repo),
            new ActingUserAccessor { ActingUser = ActingUserFixtures.Principal("test-user", "test-bypass") },
            new RowFieldAuthorizationEvaluator(),
            new AuditLog(NullLogger<AuditLog>.Instance));

        var before = await ReadRowAsync(schema.TableName, key);

        var payload = new Struct
        {
            Fields =
            {
                ["Id"]    = Value.ForString(key.ToString()),
                ["Title"] = Value.ForString("new title"),
            }
        };
        var response = await sut.Update(
            new PersistRequest { TypeName = "Dossier", Payload = payload }, TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        var after = await ReadRowAsync(schema.TableName, key);
        after.GetProperty("Title").GetString().Should().Be("new title");
        foreach (var restricted in new[] { "Secret", "SealedAt", "Seal" })
        {
            after.GetProperty(restricted).ValueKind.Should().NotBe(JsonValueKind.Null, restricted);
            after.GetProperty(restricted).GetRawText().Should().Be(before.GetProperty(restricted).GetRawText());
        }
        after.GetProperty(SchemaDescriptor.TenantColumnName).GetString().Should().Be("test-tenant");
    }

    [Fact]
    public async Task Update_ByAFieldRestrictedCaller_KeepsARestrictedIntegerAbove2Pow53Exact()
    {
        // A Struct number is a double; a stored BIGINT above 2^53 must not come back rounded.
        var baseSchema = SchemaFixtures.ReservedTenantDossierSchema();
        var schema = baseSchema with
        {
            TableName = "dossiers_" + Guid.NewGuid().ToString("N")[..8],
            ScalarColumns = [.. baseSchema.ScalarColumns,
                new ColumnDescriptor("Serial", "BIGINT", true), new ColumnDescriptor("Serials", "BIGINT[]", true)],
            Authorization = baseSchema.Authorization! with
            {
                FieldPermissions = [.. baseSchema.Authorization.FieldPermissions,
                    new Iverson.Api.Schema.FieldPermission("Serial", new List<string>(), new List<string> { "premium" }),
                    new Iverson.Api.Schema.FieldPermission("Serials", new List<string>(), new List<string> { "premium" })]
            }
        };
        await _schemaManager.ApplySchemaAsync(SchemaBuilder.ToTableSchema(schema));
        await _schemaManager.ApplySchemaAsync(ReconciliationSchema.Table);
        var key = Guid.CreateVersion7();
        await _repo.ExecuteAsync(
            $"""
            INSERT INTO "{schema.TableName}" ("Id", "Title", "Serial", "Serials", "{SchemaDescriptor.TenantColumnName}")
            VALUES (@Id, 'old title', 9007199254740993, ARRAY[9007199254740993, 7]::bigint[], 'test-tenant')
            """,
            new { Id = key });
        var registry = new SchemaRegistry(new SchemaRegistryRepository(_repo), NullLogger<SchemaRegistry>.Instance);
        await registry.RegisterAsync(schema);
        var sut = new ObjectPersistenceGrpcService(
            Substitute.For<IOutboxPublisher>(), registry, new RelationValidator(), new PayloadSizeValidator(),
            new EntityKeyAccessor(), new OutboxWriter(ReconciliationSchema.TableName, _repo, _repo),
            NullLogger<ObjectPersistenceGrpcService>.Instance, new EntityRepository(_repo),
            new ActingUserAccessor { ActingUser = ActingUserFixtures.Principal("test-user", "test-bypass") },
            new RowFieldAuthorizationEvaluator(), new AuditLog(NullLogger<AuditLog>.Instance));

        await sut.Update(new PersistRequest
        {
            TypeName = "Dossier",
            Payload = new Struct { Fields = { ["Id"] = Value.ForString(key.ToString()), ["Title"] = Value.ForString("new title") } }
        }, TestServerCallContext.Create());

        var stored = await _repo.QuerySingleOrDefaultAsync<string>(
            $"""SELECT "Serial"::text || ' ' || "Serials"::text FROM "{schema.TableName}" WHERE "Id" = @Id""", new { Id = key });
        stored.Should().Be("9007199254740993 {9007199254740993,7}");
    }

    private async Task<JsonElement> ReadRowAsync(string tableName, Guid key)
    {
        var json = await _repo.QuerySingleOrDefaultAsync<string>(
            $"""SELECT row_to_json(t)::text FROM "{tableName}" t WHERE "Id" = @Id""", new { Id = key });
        return JsonDocument.Parse(json!).RootElement.Clone();
    }
}
```

- [ ] **Step 5: Run the tests to verify they fail**

```bash
cd Iverson.Server && dotnet build Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

Expected: one compile error, `AuthorizationFieldMaskingTests.cs(617,9): error CS0029: Cannot implicitly convert type 'void' to 'System.Collections.Generic.IReadOnlyCollection<string>'` (`EnforceWithRealEvaluator` uses the return value Step 6 adds).

- [ ] **Step 6: Implement the exemption and carry-forward in `AuthorizationFieldMasking`**

In `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs`, replace the class summary's first sentence ending:

```csharp
    /// denies/throws as appropriate, force-sets or validates the owner field, and rejects
    /// any field the caller isn't allowed to write.
```

with:

```csharp
    /// denies/throws as appropriate, force-sets or validates the owner field, rejects any field
    /// the caller isn't allowed to write, and — on Update — carries forward the stored values of
    /// the fields the caller isn't allowed to write and left out (<see cref="CarryForwardRestrictedFields"/>)
    /// and of the owner column if it was left out.
```

Replace the signature line:

```csharp
    /// create- or update-specific wording ("Not authorized to create/update this entity.").
    /// </param>
    public static void EnforceWriteAuthorization(
```

with:

```csharp
    /// create- or update-specific wording ("Not authorized to create/update this entity.").
    /// </param>
    /// <returns>
    /// The canonical keys carried forward into <paramref name="payload"/> from the stored row: the
    /// restricted fields <see cref="CarryForwardRestrictedFields"/> inserted, plus the owner column
    /// when the payload left it out. Always empty for a create. A caller that returns the payload
    /// to the client removes these keys from its response.
    /// </returns>
    public static IReadOnlyCollection<string> EnforceWriteAuthorization(
```

Hoist the parsed existing row so it outlives the `else` branch. Replace:

```csharp
        if (existingRowJson is null)
        {
            // No pre-existing row: a create (Post). Force-set the tenant column unconditionally
```

with:

```csharp
        Struct? existingStruct = null;
        if (existingRowJson is null)
        {
            // No pre-existing row: a create (Post). Force-set the tenant column unconditionally
```

and:

```csharp
        else
        {
            var existingStruct = JsonParser.Default.Parse<Struct>(existingRowJson);
```

with:

```csharp
        else
        {
            existingStruct = JsonParser.Default.Parse<Struct>(existingRowJson);
            PreserveExactIntegers(existingStruct, existingRowJson);
```

Replace the `RejectDisallowedFields` call:

```csharp
        RejectDisallowedFields(payload, decision.AllowedFields, exemptField: decision.OwnerFieldName);
```

with:

```csharp
        // The owner and tenant columns are exempt: by this point the server has force-set or
        // verified both. The tenant column has to be, because AllowedFields never lists it (it is not
        // permissionable), so without the exemption every field-restricted write to a schema with
        // the reserved tenant column failed here on the column the server itself had just set.
        RejectDisallowedFields(payload, decision.AllowedFields,
            new[] { decision.OwnerFieldName, decision.TenantColumn }.OfType<string>().ToList());

        // Update branch only. AFTER RejectDisallowedFields, so a restricted field the caller did
        // send is still an error, and a carried-in owner is never mistaken for one the caller sent;
        // BEFORE the size guard, so the guard sees the payload as written.
        var carriedForward = new List<string>();
        if (existingStruct is not null)
        {
            carriedForward.AddRange(CarryForwardRestrictedFields(payload, existingStruct, decision.AllowedFields));

            // The owner column too, for every caller: sourced from the schema, not the decision,
            // whose OwnerFieldName is null for bypass callers. The column is writable, so the
            // restricted-field rule never reaches it, and OwnerImmutable only compares an owner
            // that is present — an Update that omits it would write the row with a NULL owner,
            // orphaning it from its owner.
            var ownerField = schema.Authorization?.OwnerField;
            var storedOwnerKey = ownerField is null ? null : existingStruct.Fields.Keys.FirstOrDefault(
                k => string.Equals(k, ownerField, StringComparison.OrdinalIgnoreCase));
            if (storedOwnerKey is not null && CarryForward(payload, existingStruct, storedOwnerKey))
                carriedForward.Add(storedOwnerKey);
        }
```

and end the method with the return:

```csharp
        payloadSizeValidator.ValidateTextColumnSizes(payload, schema);

        return carriedForward;
    }
```

Replace `RejectDisallowedFields`:

```csharp
    public static void RejectDisallowedFields(
        Struct payload,
        IReadOnlySet<string>? allowedFields,
        string? exemptField = null)
    {
        if (allowedFields is null) return;

        var disallowed = payload.Fields.Keys
            .Select(StructSerializer.UpperFirst)
            .Where(canonical => !allowedFields.Contains(canonical) && canonical != exemptField)
            .ToList();
```

with:

```csharp
    public static void RejectDisallowedFields(
        Struct payload,
        IReadOnlySet<string>? allowedFields,
        IReadOnlyCollection<string> exemptFields)
    {
        if (allowedFields is null) return;

        var disallowed = payload.Fields.Keys
            .Select(StructSerializer.UpperFirst)
            .Where(canonical => !allowedFields.Contains(canonical) && !exemptFields.Contains(canonical))
            .ToList();
```

and add the two new methods directly after it, as the class's last members:

```csharp
    /// <summary>
    /// Copies into <paramref name="payload"/> the stored value of every field the caller may not
    /// write and did not send, and returns the keys it inserted.
    /// <para>
    /// An update is a full-row replace in Postgres (<c>OutboxWriter</c>'s upsert sets every column)
    /// and in StarRocks (a Primary Key insert), so a field missing from the payload is cleared. For
    /// a field the caller may write that is the caller's choice; for one it may not write, it was
    /// a way to erase a value the caller was never allowed to change.
    /// </para>
    /// <para>
    /// <paramref name="existingRow"/> is the stored row's <c>row_to_json</c>, so its keys are the
    /// canonical column names and it holds restricted columns too. A payload key matching a stored
    /// key in any casing counts as sent: the client serializes camelCase, and inserting the
    /// canonical key beside it would leave two keys that <c>SerializePayload</c> folds into one.
    /// A no-op when <paramref name="allowedFields"/> is null (no field restriction).
    /// </para>
    /// </summary>
    public static IReadOnlyCollection<string> CarryForwardRestrictedFields(
        Struct payload,
        Struct existingRow,
        IReadOnlySet<string>? allowedFields)
    {
        if (allowedFields is null) return [];

        var inserted = new List<string>();
        foreach (var storedKey in existingRow.Fields.Keys)
            if (!allowedFields.Contains(StructSerializer.UpperFirst(storedKey)) &&
                CarryForward(payload, existingRow, storedKey))
                inserted.Add(storedKey);
        return inserted;
    }

    /// <summary>
    /// Copies <paramref name="existingRow"/>'s <paramref name="storedKey"/> into
    /// <paramref name="payload"/> under that canonical name, unless the stored row lacks it or the
    /// payload already carries it in any casing. Returns whether it copied.
    /// </summary>
    private static bool CarryForward(Struct payload, Struct existingRow, string storedKey)
    {
        if (!existingRow.Fields.TryGetValue(storedKey, out var storedValue)) return false;
        if (payload.Fields.Keys.Any(k => string.Equals(k, storedKey, StringComparison.OrdinalIgnoreCase))) return false;

        payload.Fields[storedKey] = storedValue;
        return true;
    }

    /// <summary>
    /// A Struct number is a double, so an integer above 2^53 in the stored row would come back
    /// rounded. Such a value (or array element) is carried as its exact JSON text instead, which
    /// json_populate_record parses back into BIGINT exactly.
    /// </summary>
    private static void PreserveExactIntegers(Struct row, string rowJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(rowJson);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind == System.Text.Json.JsonValueKind.Number && !IsDoubleExact(p.Value))
                row.Fields[p.Name] = Value.ForString(p.Value.GetRawText());
            else if (p.Value.ValueKind == System.Text.Json.JsonValueKind.Array &&
                     p.Value.EnumerateArray().Any(e => e.ValueKind == System.Text.Json.JsonValueKind.Number && !IsDoubleExact(e)))
                row.Fields[p.Name] = Value.ForList(p.Value.EnumerateArray().Select(e =>
                    e.ValueKind == System.Text.Json.JsonValueKind.Null ? Value.ForNull()
                    : IsDoubleExact(e) ? Value.ForNumber(e.GetDouble()) : Value.ForString(e.GetRawText())).ToArray());
        }
    }

    private static bool IsDoubleExact(System.Text.Json.JsonElement e) =>
        !e.TryGetInt64(out var l) || Math.Abs(l) <= (1L << 53);
```

The tenant column is never copied by carry-forward: on the update branch it is already force-set into the payload under its canonical name before this runs. An owner column that is also field-restricted is copied once, by the restricted pass; `CarryForward` then sees it present and the owner step adds nothing.

- [ ] **Step 7: Mask Mapping Update's response**

In `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`, in `Update` only, capture the result:

```csharp
        var carriedForward = AuthorizationFieldMasking.EnforceWriteAuthorization(
            _authEvaluator,
            _actingUserAccessor.ActingUser,
```

and replace the response strip at the end of `Update`:

```csharp
        // Strip the server-owned tenant column from the Struct that becomes MappingResponse.Data.
        // EnforceWriteAuthorization force-set it INTO this very object (SetAuthoritativeField ->
        // StructFieldAccess.SetField mutates in place), and `Data = request.Payload` below returns
        // that same object — so without this the column goes back to the caller on every write.
        //
        // AFTER SerializePayload, deliberately. payloadJson is what OutboxPublisher puts on Kafka,
        // and it is the only source of the tenant value for the StarRocks projection
        // (EngagementRepository.UpsertAsync) and the Qdrant point payload
        // (IntelligenceStoreConsumer.BuildObjectPointPayload). Stripping before serialization
        // would leave the StarRocks row's tenant column NULL — StarRocks' Primary Key model
        // treats a partial INSERT as a full-row replace — and every subsequent StarRocks read for
        // that tenant would return nothing. OutboxWriter remains the sole *injector* for the
        // Postgres write; this is only a response-shaping strip.
        AuthorizationFieldMasking.RemoveTenantColumn(request.Payload);

        return new MappingResponse { Success = true, Data = request.Payload, TraceId = request.TraceId };
    }

    public override async Task<MappingDeleteResponse> Delete(
```

with:

```csharp
        // Shape the Struct that becomes MappingResponse.Data. EnforceWriteAuthorization wrote the
        // server-owned tenant column and any carried-forward values INTO this very object
        // (StructFieldAccess.SetField and CarryForwardRestrictedFields mutate in place), and
        // `Data = request.Payload` below returns that same object.
        //
        // AFTER SerializePayload, deliberately. payloadJson is what OutboxPublisher puts on Kafka:
        // the only source of the tenant value for the StarRocks projection
        // (EngagementRepository.UpsertAsync) and the Qdrant point payload
        // (IntelligenceStoreConsumer.BuildObjectPointPayload), and the carrier of the restricted
        // values StarRocks' full-row replace would otherwise clear. Masking before serialization
        // would empty those columns in the projections.
        //
        // First the Read masking Get applies (MaskDisallowedFields also strips the tenant column).
        // Then the carried-forward keys, unconditionally: read masking alone is not enough, because
        // a caller whose read is Denied, or ownership-scoped on a row it does not own, gets a null
        // or permissive read AllowedFields, and Get would refuse it the whole row.
        var readDecision = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Read);
        AuthorizationFieldMasking.MaskDisallowedFields(request.Payload, readDecision.AllowedFields);
        foreach (var carried in carriedForward)
            request.Payload.Fields.Remove(carried);

        return new MappingResponse { Success = true, Data = request.Payload, TraceId = request.TraceId };
    }

    public override async Task<MappingDeleteResponse> Delete(
```

(`Post` keeps its identical `RemoveTenantColumn` strip; only `Update` changes. The loop variable is `carried` because `key` is already in scope.)

- [ ] **Step 8: Run the tests**

```bash
cd Iverson.Server && dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~Iverson.Api.Tests.Grpc.ObjectPersistenceGrpcServiceTests|FullyQualifiedName~Iverson.Api.Tests.Grpc.ObjectMappingGrpcServiceTests|FullyQualifiedName~Iverson.Api.Tests.Grpc.AuthorizationFieldMaskingTests|FullyQualifiedName~Iverson.Api.Tests.Grpc.UpdateCarryForwardPostgresIntegrationTests"
```

Expected: `Passed: 183, Failed: 0, Total: 183` (Task 1's 170, plus 5 field-masking tests, 3 Mapping-response tests, the 3-case owner theory and 2 Postgres tests; the Postgres test starts one `postgres:16-alpine` container).

Then the whole project once:

```bash
cd Iverson.Server && dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

Expected: `Passed: 1331, Failed: 0, Total: 1331` (baseline 1318 at `e637bada`; Task 1 net 0, Task 2 +13). About 8 minutes with the container suites.

- [ ] **Step 9: Commit**

```bash
git add Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api.Tests/Helpers/SchemaFixtures.cs Iverson.Server/Iverson.Api.Tests/Grpc/AuthorizationFieldMaskingTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/UpdateCarryForwardPostgresIntegrationTests.cs
git commit -m "carry restricted fields forward on update and exempt the forced tenant column from field rejection

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 3: Conformance IVC-IDN-007 → IVC-IDN-008

**Files:**
- Modify: `Iverson.Server/Iverson.ClientConformance/Requirements.cs` (IDN-007 const removed, IDN-008 const added; IDN-006 doc's successor sentence)
- Modify: `Iverson.Server/Iverson.ClientConformance/Scenarios/IdentityScenario.cs` (judge predicate, name, details, citation; class doc `:12`, `:91-143`; `IsStatusCodeMalformed` doc)
- Modify: `docs/standards/iverson-client-standard.md` (gitignored: `git add -f`) (IDN table, prose, retirement note, coverage ledger, backstop prose, ERR row)
- Modify (comments only): `Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Program.cs`, `Iverson.Clients/Python/conformance/driver.py`, `Iverson.Clients/TypeScript/conformance/driver.ts`, `Iverson.Clients/Go/conformance/main.go`, `Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance/Driver.java` (the `denied_update_wrong_acting_user` comments)
- Test: `Iverson.Server/Iverson.ClientConformance.Tests/IdentityScenarioTests.cs`

**Interfaces:** Consumes: Task 1's behaviour (a cross-tenant mapped update answers gRPC NOT_FOUND (5); a caller with no acting-user identity gets PERMISSION_DENIED (7)). Produces: `Requirements.IdnCrossTenantUpdateAnsweredNotFound = "IVC-IDN-008"`; removes `Requirements.IdnCrossTenantUpdateAnsweredWithoutError`. No proto change, no SDK change, and no driver code change (only driver comments change, in Step 8).

Line numbers below are from `e637bada`; locate every edit by its text.

- [ ] **Step 1: Re-pin the identity judge tests**

In `IdentityScenarioTests.cs`:

1a. `JudgeHappy`'s default denied step becomes NOT_FOUND:

Before:

```csharp
            ReadDocument(read ?? ReadStep(Key, Tenant, Owner), denied ?? DeniedStep(null)),
```

After:

```csharp
            ReadDocument(read ?? ReadStep(Key, Tenant, Owner), denied ?? DeniedStep(5)),
```

1b. The two other null sites that stand for a well-behaved server, `Judge_NoSeededKey_BackstopFailsAndCarriesNoRequirementId` (`:116`) and `Judge_NoReadStepAtAll_Idn002FailsRatherThanBeingSkipped` (`:146`), change `DeniedStep(null)` to `DeniedStep(5)`:

Before (`:116`):

```csharp
            ReadDocument(ReadStep(Key, Tenant, Owner), DeniedStep(null)),
            IdentityScenario.TenantObservation.NotAttempted);
```

After:

```csharp
            ReadDocument(ReadStep(Key, Tenant, Owner), DeniedStep(5)),
            IdentityScenario.TenantObservation.NotAttempted);
```

Before (`:146`):

```csharp
            ReadDocument(DeniedStep(null)), Derived());
```

After:

```csharp
            ReadDocument(DeniedStep(5)), Derived());
```

1c. The two `GradeReads` tests (`:642` in `GradeReads_TheReadPhaseJudgement_ReachesTheCellCarryingItsIdnCitations`, `:680` in `GradeReads_ALanguageWhoseDriverReportedNoReadDocument_IsNotGreen`) change `DeniedStep(7)` to `DeniedStep(5)`. The line is identical at both sites:

Before:

```csharp
            [("dotnet", ReadDocument(ReadStep(Key, Tenant, Owner), DeniedStep(7)))],
```

After:

```csharp
            [("dotnet", ReadDocument(ReadStep(Key, Tenant, Owner), DeniedStep(5)))],
```

1d. Section header (`:176`):

Before:

```csharp
    // ── IVC-IDN-006/007: tenancy derivation, and the response shape of a cross-tenant write ───
```

After:

```csharp
    // ── IVC-IDN-006/008: tenancy derivation, and the answer to a cross-tenant update ──────────
```

1e. In `JudgeTenantDerivation_TheGrpcControl_CitesIdn004AndNotIdn003`, the comment and the second count:

Before (`:237-238`):

```csharp
        // still IVC-IDN-003; the analysis carries over UNCHANGED to each of its two successors,
        // IVC-IDN-006 and IVC-IDN-007, independently):
```

After:

```csharp
        // still IVC-IDN-003; the analysis carries over UNCHANGED to each of its two successors,
        // IVC-IDN-006 and IVC-IDN-007, independently, and to IVC-IDN-007's own successor,
        // IVC-IDN-008):
```

Before (`:266-268`):

```csharp
        JudgeHappy().Count(a => a.RequirementId == Requirements.IdnCrossTenantUpdateAnsweredWithoutError)
            .Should().Be(1, "IVC-IDN-007 is graded by exactly one assertion: the wrong acting user's " +
                "update being answered without a gRPC error status");
```

After:

```csharp
        JudgeHappy().Count(a => a.RequirementId == Requirements.IdnCrossTenantUpdateAnsweredNotFound)
            .Should().Be(1, "IVC-IDN-008 is graded by exactly one assertion: the wrong acting user's " +
                "update being answered with gRPC status NOT_FOUND");
```

1f. Delete the eight enforcement tests that sit between `JudgeTenantDerivation_TheServerBehaved_AllThreeAssertionsPass` and the `// ── every assertion fires on every language, whatever the driver reported` header (`:371-453`), including the `<summary>` doc on the detail test:
- `Judge_WrongActingUsersUpdateWasRefused_Idn007Fails`
- `Judge_WrongActingUsersUpdateWasAccepted_Idn007Passes`
- `Judge_DeniedStepReportsAMalformedStatusCode_Idn007FailsRatherThanPassingVacuously`
- `Judge_WrongActingUsersUpdateFailedWithSomeOtherCode_Idn003EnforcementFails`
- `Judge_NoDeniedStepAtAll_Idn003EnforcementFailsRatherThanBeingSkipped`
- `Judge_DeniedStepItselfBroke_Idn003EnforcementFails`
- `Judge_Idn003EnforcementDetail_CarriesTheReportedStatusNameAndMessage`
- `Judge_DriverReportedNoStatusNameOrMessage_DetailSaysSoRatherThanThrowing`

Insert in their place (ten tests: code 5 passes; null, 7, 0, a malformed code, 16, a missing step and a broken step fail; the two detail tests are re-pinned to a NOT_FOUND report):

```csharp
    [Fact]
    public void Judge_WrongActingUsersUpdateWasAnsweredNotFound_Idn008Passes()
    {
        var assertions = JudgeHappy(denied: DeniedStep(5));

        Named(assertions, "answered with gRPC status NOT_FOUND").Passed.Should().BeTrue(
            "a key the caller's tenant holds no row under is answered NOT_FOUND, whoever else holds it");
        Named(assertions, "answered with gRPC status NOT_FOUND").RequirementId.Should().Be("IVC-IDN-008");
    }

    [Fact]
    public void Judge_WrongActingUsersUpdateWasAccepted_Idn008Fails()
    {
        Named(JudgeHappy(denied: DeniedStep(null)), "answered with gRPC status NOT_FOUND").Passed.Should().BeFalse(
            "a wrong-tenant acting user's update that succeeded is the acceptance-shaped answer IVC-IDN-007 " +
            "graded and IVC-IDN-008 exists to refuse");
    }

    [Fact]
    public void Judge_WrongActingUsersUpdateWasPermissionDenied_Idn008Fails()
    {
        Named(JudgeHappy(denied: DeniedStep(7)), "answered with gRPC status NOT_FOUND").Passed.Should().BeFalse(
            "PermissionDenied (7) is what a caller refused before the row is read gets — a client that " +
            "dropped the acting-user identity, not one that carried another tenant's");
    }

    [Fact]
    public void Judge_WrongActingUsersUpdateReportedStatusOk_Idn008Fails()
    {
        Named(JudgeHappy(denied: DeniedStep(0)), "answered with gRPC status NOT_FOUND").Passed.Should().BeFalse(
            "OK (0) reported as a code is still an accepted update");
    }

    [Fact]
    public void Judge_DeniedStepReportsAMalformedStatusCode_Idn008FailsNamingTheMalformedReport()
    {
        var denied = new StepResult(IdentityScenario.DeniedStepName, true,
            Entity: JsonSerializer.SerializeToElement(new { statusCode = "not-a-number" }));

        var assertion = Named(JudgeHappy(denied: denied), "answered with gRPC status NOT_FOUND");

        assertion.Passed.Should().BeFalse(
            "a status code that is present but unparseable is a malformed report, not a NOT_FOUND");
        assertion.Detail.Should().Contain("could not be parsed",
            "a malformed report must be diagnosed as one, never as an accepted update");
    }

    [Fact]
    public void Judge_WrongActingUsersUpdateFailedWithSomeOtherCode_Idn008Fails()
    {
        Named(JudgeHappy(denied: DeniedStep(16)), "answered with gRPC status NOT_FOUND").Passed.Should().BeFalse(
            "Unauthenticated (16) is a token problem, not the NOT_FOUND answer this requirement names");
    }

    [Fact]
    public void Judge_NoDeniedStepAtAll_Idn008FailsRatherThanBeingSkipped()
    {
        var assertions = IdentityScenario.Judge("dotnet", Tenant, Owner, Key,
            ReadDocument(ReadStep(Key, Tenant, Owner)), Derived());

        Named(assertions, "answered with gRPC status NOT_FOUND").Passed.Should().BeFalse();
    }

    [Fact]
    public void Judge_DeniedStepItselfBroke_Idn008Fails()
    {
        var denied = new StepResult(IdentityScenario.DeniedStepName, false, Error: "channel closed");

        Named(JudgeHappy(denied: denied), "answered with gRPC status NOT_FOUND").Passed.Should().BeFalse(
            "a driver whose attempt never reached the server observed no answer");
    }

    /// <summary>
    /// The status NAME and MESSAGE ride in the assertion's detail as diagnostics, and nothing
    /// grades them — the server answers a key held by another tenant and a key held by no tenant
    /// with the identical code AND message, which is exactly why they are reported rather than
    /// asserted on. This test exists so that carrying them cannot be dropped silently: they are the
    /// evidence that establishes the indistinguishability empirically, from what the driver itself
    /// received.
    /// </summary>
    [Fact]
    public void Judge_Idn008Detail_CarriesTheReportedStatusNameAndMessage()
    {
        var denied = new StepResult(IdentityScenario.DeniedStepName, true,
            Entity: JsonSerializer.SerializeToElement(new
            {
                statusCode = 5,
                status = "NotFound",
                detail = $"'{IdentityScenario.TypeName}:{Key}' not found.",
            }));

        var detail = Named(JudgeHappy(denied: denied), "answered with gRPC status NOT_FOUND").Detail;

        detail.Should().Contain("NotFound");
        detail.Should().Contain($"'{IdentityScenario.TypeName}:{Key}' not found.");
    }

    [Fact]
    public void Judge_DriverReportedNoStatusNameOrMessage_DetailSaysSoRatherThanThrowing()
    {
        var detail = Named(JudgeHappy(denied: DeniedStep(5)), "answered with gRPC status NOT_FOUND").Detail;

        detail.Should().Contain("<none>");
```

1g. `Judge_EmptyDocument_EveryIdnRequirementStillHasAFailingAssertion` (`:466`):

Before:

```csharp
        Cited(assertions, "IVC-IDN-007").Should().NotBeEmpty();
```

After:

```csharp
        Cited(assertions, "IVC-IDN-008").Should().NotBeEmpty();
```

- [ ] **Step 2: Run the identity tests red**

```bash
cd Iverson.Server && dotnet test Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj --filter "FullyQualifiedName~IdentityScenarioTests"
```

Expected: build error `CS0117: 'Requirements' does not contain a definition for 'IdnCrossTenantUpdateAnsweredNotFound'`. (Observed. With the const added alone and the judge unchanged, 13 of 50 fail, including `Judge_WrongActingUsersUpdateWasAccepted_Idn008Fails`, `Judge_WrongActingUsersUpdateWasPermissionDenied_Idn008Fails` and `Judge_ReadBackAndDenialBothAsExpected_AllAssertionsPass`.)

- [ ] **Step 3: Replace the IDN-007 const with IDN-008 in `Requirements.cs`**

Replace the whole IDN-007 `<summary>` and const (`:567-583`):

Before:

```csharp
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

After:

```csharp
    /// A mapped update attempted by an acting user of another tenant is answered with gRPC status
    /// NOT_FOUND (5), the status an update of a key that exists nowhere receives. Discharged by
    /// <c>IdentityScenario.Judge</c>'s enforcement assertion, over the numeric gRPC status code the
    /// driver reported from its <c>denied_update_wrong_acting_user</c> step — the harness observes
    /// only the numeric status code, never the response body, so this Statement is written at
    /// exactly that altitude and no wider.
    ///
    /// <para><b>Supersedes the retired IVC-IDN-007.</b> That Statement said such an update is
    /// answered without a gRPC error status, the same as an accepted one — true for as long as
    /// Update created a row for any key the caller's tenant could not see. Update is now strictly an
    /// update: a key with no row in the caller's tenant is answered NOT_FOUND, so a cross-tenant
    /// write no longer receives an acceptance-shaped response and IVC-IDN-007's assertion would fail
    /// on every driver. Global Constraint 3 makes Statement cells immutable, so the correction is a
    /// retirement plus this successor, not an edit.</para>
    ///
    /// <para><b>Why NOT_FOUND, and why the Statement names the equivalence.</b> The server's
    /// existing-row read is scoped to the caller's own tenant, so a key held by another tenant and a
    /// key held by no tenant are the same observation to it, and it answers both the same way; a
    /// distinct answer would tell the caller the key exists elsewhere. That sameness, not the code
    /// alone, is the property, which is why the Statement says "the status an update of a key that
    /// exists nowhere receives". The assertion accepts code 5 and nothing else: no status at all is
    /// an accepted update, and PERMISSION_DENIED (7) is the answer to a caller refused before any
    /// row is read, such as a call that arrived carrying no acting-user identity rather than
    /// another tenant's.</para>
    /// </summary>
    public const string IdnCrossTenantUpdateAnsweredNotFound = "IVC-IDN-008";
```

(The `/// <summary>` line above each block is unchanged.)

In `IdnTenancyDerivedFromActingUser`'s doc (`:558-564`), the "two successors" paragraph is brought up to date, because one of those successors is now retired:

Before:

```csharp
    /// <para><b>Supersedes the retired IVC-IDN-003.</b> That Statement conjoined this derivation
    /// claim with an ENFORCEMENT claim ("...and denies an acting user of another tenant who attempts
    /// to write that row") that a later fix (mitigating CSR round 9's Finding #5) made false: the
    /// server no longer denies a cross-tenant write on the wire at all, by design, so no assertion
    /// discharges the enforcement half any more. Global Constraint 3 makes Statement cells immutable,
    /// so the correction is a retirement plus two successors, not an edit — this one restates only
    /// the still-true derivation half.</para>
```

After:

```csharp
    /// <para><b>Supersedes the retired IVC-IDN-003.</b> That Statement conjoined this derivation
    /// claim with an ENFORCEMENT claim ("...and denies an acting user of another tenant who attempts
    /// to write that row") that a later fix (mitigating CSR round 9's Finding #5) made false: the
    /// server stopped denying a cross-tenant write on the wire, by design, so no assertion
    /// discharged the enforcement half any more. Global Constraint 3 makes Statement cells immutable,
    /// so the correction is a retirement plus two successors, not an edit — this one restates only
    /// the still-true derivation half. The other successor, IVC-IDN-007, is itself retired; what a
    /// cross-tenant update is answered with today is <see cref="IdnCrossTenantUpdateAnsweredNotFound"/>.</para>
```

- [ ] **Step 4: Re-pin the judge in `IdentityScenario.cs`**

The enforcement assertion (`:459-484`) becomes:

Before:

```csharp
        // ── IVC-IDN-007, response shape: a cross-tenant update is answered without an error ────
        var deniedStep = document.Steps.FirstOrDefault(s => s.Name == DeniedStepName);
        var code = deniedStep is { Ok: true } ? ReadStatusCode(deniedStep.Entity) : null;

        // The status NAME and MESSAGE are reported alongside the code purely as diagnostics — no
        // assertion grades them; carrying them in the detail is what let the prior denial-shaped
        // behavior be established empirically rather than only read off the server source.
        var reportedStatus = ReadString(deniedStep?.Entity, "status");
        var reportedDetail = ReadString(deniedStep?.Entity, "detail");

        var malformedCode = deniedStep is { Ok: true } && IsStatusCodeMalformed(deniedStep.Entity);

        assertions.Add(Assertion.From(
            $"{language}: an acting user of another tenant is answered without a gRPC error status",
            deniedStep is { Ok: true } && code is null && !malformedCode,
            deniedStep is null
                ? $"the driver reported no '{DeniedStepName}' step"
                : !deniedStep.Ok
                    ? $"the attempt itself broke, so no answer was observed: {deniedStep.Error ?? "no error text"}"
                    : code is null
                        ? malformedCode
                            ? "the driver reported a 'statusCode' that could not be parsed as a number, " +
                              "which is a malformed report rather than evidence of an accepted write"
                            : "the driver reported no gRPC status code, as expected for an accepted write"
                        : $"the driver reported gRPC status {code}, expected no gRPC error status",
            Requirements.IdnCrossTenantUpdateAnsweredWithoutError));
```

After:

```csharp
        // ── IVC-IDN-008: a cross-tenant update is answered NOT_FOUND ───────────────────────────
        var deniedStep = document.Steps.FirstOrDefault(s => s.Name == DeniedStepName);
        var code = deniedStep is { Ok: true } ? ReadStatusCode(deniedStep.Entity) : null;

        // The status NAME and MESSAGE are reported alongside the code purely as diagnostics — no
        // assertion grades them; carrying them in the detail is what let the prior denial-shaped
        // behavior be established empirically rather than only read off the server source.
        var reportedStatus = ReadString(deniedStep?.Entity, "status");
        var reportedDetail = ReadString(deniedStep?.Entity, "detail");

        var malformedCode = deniedStep is { Ok: true } && IsStatusCodeMalformed(deniedStep.Entity);

        assertions.Add(Assertion.From(
            $"{language}: an update by an acting user of another tenant is answered with gRPC status NOT_FOUND",
            deniedStep is { Ok: true } && !malformedCode && code == 5,
            deniedStep is null
                ? $"the driver reported no '{DeniedStepName}' step"
                : !deniedStep.Ok
                    ? $"the attempt itself broke, so no answer was observed: {deniedStep.Error ?? "no error text"}"
                    : malformedCode
                        ? "the driver reported a 'statusCode' that could not be parsed as a number, " +
                          "which is a malformed report rather than an answer"
                        : code is null
                            ? "the driver reported no gRPC status code, so the update was accepted; " +
                              "expected NOT_FOUND (5)"
                            : code == 5
                                ? "the driver reported gRPC status 5 (NOT_FOUND), as expected"
                                : $"the driver reported gRPC status {code}, expected NOT_FOUND (5)",
            Requirements.IdnCrossTenantUpdateAnsweredNotFound));
```

The `assertions[^1] = assertions[^1] with { Detail = … }` block after it, which appends the reported status name and message, is unchanged.

`IsStatusCodeMalformed`'s doc (`:652-658`):

Before:

```csharp
    /// is correct for most callers; the IVC-IDN-007 assertion needs to tell them apart, because a
    /// malformed report must still fail rather than being graded as an accepted write.
```

After:

```csharp
    /// is correct for most callers; the IVC-IDN-008 assertion needs to tell them apart in its detail,
    /// because both fail it and a malformed report must be diagnosed as one rather than as an
    /// accepted write.
```

- [ ] **Step 5: Rewrite the stale class docs in `IdentityScenario.cs`**

Class summary opening (`:11-13`):

Before:

```csharp
/// acting user rather than from the payload, and that an acting user belonging to a different
/// tenant is answered without a gRPC error status when attempting a write to that row — the same
/// as an accepted one, per CSR round 9's Finding #5 mitigation.
```

After:

```csharp
/// acting user rather than from the payload, and that an acting user belonging to a different
/// tenant who attempts an update of that row is answered with gRPC status NOT_FOUND — the same
/// answer an update of a key that exists nowhere receives.
```

End of the "immutability branch is NOT dead code" paragraph (`:92-97`):

Before:

```csharp
/// therefore finds no visible row and takes the no-existing-row branch whatever the target schema's
/// registration history — so neither the tenant MISMATCH nor the immutability refusal fires on this
/// leg any more, and the refusal it used to observe is gone (see below). The drivers still send the
/// acting user's own tenant in their user column, so this leg keeps sending a payload a conforming
/// client would send.</para>
```

After:

```csharp
/// therefore finds no visible row whatever the target schema's registration history — so neither the
/// tenant MISMATCH nor the immutability refusal fires on this leg any more, and what it observes
/// instead is the NOT_FOUND answer described below. The drivers still send the acting user's own
/// tenant in their user column, so this leg keeps sending a payload a conforming client would
/// send.</para>
```

The four paragraphs from "What used to be indistinguishable" through the end of the backstop-worth paragraph (`:99-143`, ending just before `/// </summary>` and `public sealed class IdentityScenario(`) are replaced. Includes the two crefs to the removed const (`:122`, `:141`):

Before:

```csharp
/// <para><b>What used to be indistinguishable no longer is, by accident.</b> A caller with no
/// acting-user token at all is still denied before this row is ever read
/// (<c>RowFieldAuthorizationEvaluator.Evaluate</c> returns <c>Denied</c> on a null acting user,
/// <c>reason=AccessDenied</c>) — unchanged by this fix. A wrong-tenant caller's write, by contrast,
/// now reaches the narrowed read, finds no visible row, and is silently swallowed as a success. The
/// two cases used to grade identically (both <c>PermissionDenied</c>, indistinguishable to a
/// client); they now grade oppositely, but not by any designed signal — see the standard's IDN
/// Deferred ledger.</para>
///
/// <para><b>Why every cross-tenant update now takes the create branch.</b> With the read narrowed
/// to the caller's own tenant, a foreign-tenant row is never visible to
/// <c>EnforceWriteAuthorization</c> — so every wrong-tenant update takes the same no-existing-row
/// branch a genuine create does, and the actual collision is caught later, at the database layer,
/// per CSR round 9's Finding #5 mitigation. This is what makes the backstop below load-bearing
/// rather than decorative.</para>
///
/// <para><b>Backstop assertion.</b> <see cref="Judge"/>'s "the write phase reported a row key for
/// this language" assertion is this axis's backstop, in the sense
/// <c>docs/standards/iverson-client-standard.md</c>'s REL authoring notes require. Since CSR
/// round 9's Finding #5 mitigation, EVERY cross-tenant update takes the create branch described
/// above and SUCCEEDS, so there is no denial left for a missing row to defeat — and with no seeded
/// row, every driver still derives a well-formed key for a row that was never created, that update
/// is accepted as an ordinary create, and the driver reports NO gRPC status code. So
/// <see cref="Requirements.IdnCrossTenantUpdateAnsweredWithoutError"/>'s "answered without a gRPC
/// error status" assertion PASSES in the no-seeded-row state exactly as it does for a genuine
/// swallowed cross-tenant write: it cannot tell the two apart. That is signal this leg has LOST —
/// before the mitigation the same assertion demanded status 7 and therefore reddened when there was
/// nothing to deny.</para>
///
/// <para>What the backstop is worth, stated no higher than it is. It is NOT the only assertion that
/// reddens in that state: with no seeded row,
/// <see cref="Requirements.IdnActingUserPropagatedToRow"/>'s two assertions,
/// <see cref="Requirements.IdnTenancyDerivedFromActingUser"/>'s two and
/// <see cref="Requirements.IdnServerTenantColumnAbsentFromPointRead"/>'s one all fail as well, so
/// the cell goes red with or without it. What it uniquely supplies is the DIAGNOSIS — it is the
/// only assertion whose subject is the fixture precondition itself, so it attributes that red cell
/// to the harness having seeded nothing rather than to five clients having broken at once. The
/// backstop fires unconditionally, on every language, before and outside both the read-back and the
/// enforcement assertions. It carries no requirement ID: no <c>IVC-IDN-*</c> statement owns "this
/// language seeded a row" as such — that is a property of the harness's fixture, not of a client —
/// and it stays strictly weaker than <see cref="Requirements.IdnActingUserPropagatedToRow"/>
/// (wherever the backstop fails, the read-back fails too). It is NOT weaker than
/// <see cref="Requirements.IdnCrossTenantUpdateAnsweredWithoutError"/>, and the relation does not
/// merely fail to hold — it inverts: in the no-seeded-row state the backstop fails while that
/// assertion passes.</para>
```

After:

```csharp
/// <para><b>A header-less caller and a wrong-tenant caller get different answers, by design.</b> A
/// caller with no acting-user token at all is denied before this row is ever read
/// (<c>RowFieldAuthorizationEvaluator.Evaluate</c> returns <c>Denied</c> on a null acting user,
/// <c>reason=AccessDenied</c>) and is answered <c>PermissionDenied</c> (7). A wrong-tenant caller
/// passes that check, reaches the tenant-scoped read, finds no visible row, and is answered
/// <c>NotFound</c> (5). The order is deliberate: the denial check runs before the existence check,
/// so a caller who is refused anyway is refused the same way whether or not the key exists. Only
/// the wrong-tenant answer is graded; the header-less one is a Deferred area in the standard's IDN
/// coverage ledger.</para>
///
/// <para><b>Why every cross-tenant update is answered NOT_FOUND.</b> With the read narrowed to the
/// caller's own tenant, a foreign-tenant row is never visible to
/// <c>EnforceWriteAuthorization</c>, so a wrong-tenant update finds no existing row, exactly as an
/// update of a key that exists nowhere does. Update never creates a row, so both are answered
/// <c>NotFound</c> with the same message, and neither reaches the database. This is what makes the
/// backstop below load-bearing rather than decorative.</para>
///
/// <para><b>Backstop assertion.</b> <see cref="Judge"/>'s "the write phase reported a row key for
/// this language" assertion is this axis's backstop, in the sense
/// <c>docs/standards/iverson-client-standard.md</c>'s REL authoring notes require. EVERY
/// cross-tenant update is answered NOT_FOUND because the caller's tenant holds no row under that
/// key, and with no seeded row the caller's tenant holds no row under that key either: every driver
/// still derives a well-formed key for a row that was never created, that update is answered
/// NOT_FOUND for the same reason, and <see cref="Requirements.IdnCrossTenantUpdateAnsweredNotFound"/>'s
/// assertion PASSES in the no-seeded-row state exactly as it does for a genuine cross-tenant update:
/// it cannot tell the two apart, by design, because the server answers them identically. That is
/// signal this leg lost with CSR round 9's Finding #5 mitigation and has not regained — before the
/// mitigation the same assertion demanded status 7 and therefore reddened when there was nothing to
/// deny.</para>
///
/// <para>What the backstop is worth, stated no higher than it is. It is NOT the only assertion that
/// reddens in that state: with no seeded row,
/// <see cref="Requirements.IdnActingUserPropagatedToRow"/>'s two assertions,
/// <see cref="Requirements.IdnTenancyDerivedFromActingUser"/>'s two and
/// <see cref="Requirements.IdnServerTenantColumnAbsentFromPointRead"/>'s one all fail as well, so
/// the cell goes red with or without it. What it uniquely supplies is the DIAGNOSIS — it is the
/// only assertion whose subject is the fixture precondition itself, so it attributes that red cell
/// to the harness having seeded nothing rather than to five clients having broken at once. The
/// backstop fires unconditionally, on every language, before and outside both the read-back and the
/// enforcement assertions. It carries no requirement ID: no <c>IVC-IDN-*</c> statement owns "this
/// language seeded a row" as such — that is a property of the harness's fixture, not of a client —
/// and it stays strictly weaker than <see cref="Requirements.IdnActingUserPropagatedToRow"/>
/// (wherever the backstop fails, the read-back fails too). It is NOT weaker than
/// <see cref="Requirements.IdnCrossTenantUpdateAnsweredNotFound"/>, and the relation does not
/// merely fail to hold — it inverts: in the no-seeded-row state the backstop fails while that
/// assertion passes.</para>
```

The backstop assertion's own detail string (`:416-417`, "the cross-tenant enforcement assertion passes vacuously") is left unchanged: with no seeded row the update is answered NOT_FOUND and IVC-IDN-008 passes, so it is still accurate.

- [ ] **Step 6: Run the identity tests green**

```bash
cd Iverson.Server && dotnet test Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj --filter "FullyQualifiedName~IdentityScenarioTests"
```

Expected: 50 passed, 0 failed (was 48 at baseline: 8 tests removed, 10 added). The coverage gate is red until Step 7.

- [ ] **Step 7: Update the standard (`docs/standards/iverson-client-standard.md`)**

7a. IDN table: retire IDN-007 and add IDN-008 (`:399`):

Before:

```markdown
| IVC-IDN-007 | Active | Behaviour | A mapped update attempted by an acting user of another tenant is answered without a gRPC error status, the same as an accepted one |
```

After:

```markdown
| IVC-IDN-007 | Retired | Behaviour | A mapped update attempted by an acting user of another tenant is answered without a gRPC error status, the same as an accepted one |
| IVC-IDN-008 | Active | Behaviour | A mapped update attempted by an acting user of another tenant is answered with gRPC status NOT_FOUND (5), the status an update of a key that exists nowhere receives |
```

7b. The "thirds" paragraph (`:411-415`):

Before:

```markdown
`IVC-IDN-002`, `IVC-IDN-006` and `IVC-IDN-007` are the propagation, derivation and response-shape
thirds of what the acting-user identity is FOR: a client that propagates an acting user the server
can read, but under which the server's tenancy scoping does not actually hold, or whose cross-tenant
write the server does not visibly refuse, is non-conformant in a way a single conflated requirement
would report only as one undifferentiated red cell.
```

After:

```markdown
`IVC-IDN-002`, `IVC-IDN-006` and `IVC-IDN-008` are the propagation, derivation and enforcement
thirds of what the acting-user identity is FOR: a client that propagates an acting user the server
can read, but under which the server's tenancy scoping does not actually hold, or whose cross-tenant
update is not answered as an update of a key that does not exist, is non-conformant in a way a
single conflated requirement would report only as one undifferentiated red cell.
```

7c. The header-less/wrong-tenant paragraph and the lead-in after it (`:434-444`):

Before:

```markdown
**A header-less caller and a wrong-tenant caller are no longer indistinguishable — but not because
either is now more observable.** `RowFieldAuthorizationEvaluator.Evaluate` denies a caller with no
acting-user token at all (`PermissionDenied`, `reason=AccessDenied`) before the row this axis
targets is ever read — unchanged by the narrowed-read fix `IVC-IDN-007` grades. A wrong-tenant
caller's write, by contrast, now reaches the narrowed read, finds no visible row, and is silently
swallowed as a success — so the two cases now grade oppositely (red versus green) where they used to
grade identically. Neither distinction is a designed signal a client can rely on: see the Coverage
table below.

`IVC-IDN-006` and `IVC-IDN-007` are each verified from what they observe, and neither is gradeable
from a payload the client controls:
```

After:

```markdown
**A header-less caller and a wrong-tenant caller get different answers, by design.**
`RowFieldAuthorizationEvaluator.Evaluate` denies a caller with no acting-user token at all
(`PermissionDenied` (7), `reason=AccessDenied`) before the row this axis targets is ever read. A
wrong-tenant caller passes that check, reaches the existing-row read, which is scoped to the
caller's own tenant, finds no row, and is answered `NotFound` (5) — the answer an update of a key
that exists nowhere receives, which is what `IVC-IDN-008` grades. The order is deliberate: the
denial check runs before the existence check, so a caller who would be refused anyway is refused
the same way whether or not the key exists. Only the wrong-tenant answer is graded; see the Coverage
table below for the header-less one.

`IVC-IDN-006` and `IVC-IDN-008` are each verified from what they observe, and neither is gradeable
from a payload the client controls:
```

7d. The negative-leg bullet (`:466-473`; its last four lines, from "same operation against the same server", are unchanged):

Before:

```markdown
- **Response shape, graded as `IVC-IDN-007`.** The orchestrator mints a SECOND acting-user token,
  for a different, active tenant (`TokenBroker.GetOtherTenantActingTokenAsync`), and passes it to
  every driver as `--wrong-acting-token`. Each driver attempts a mapped update of the row it just
  created while carrying that token in place of its own, and reports the gRPC status code it
  received as data — it judges nothing. The orchestrator asserts the driver reported no gRPC error
  status, the same shape a genuinely accepted write produces: the server no longer denies this write
  at all (CSR round 9's Finding #5 mitigation swallows it as a success at the database layer), so
  the assertion observes the response envelope rather than a denial. All five drivers attempt the
```

After:

```markdown
- **Cross-tenant update, graded as `IVC-IDN-008`.** The orchestrator mints a SECOND acting-user
  token, for a different, active tenant (`TokenBroker.GetOtherTenantActingTokenAsync`), and passes
  it to every driver as `--wrong-acting-token`. Each driver attempts a mapped update of the row it
  just created while carrying that token in place of its own, and reports the gRPC status code it
  received as data — it judges nothing. The orchestrator asserts the driver reported `NotFound` (5):
  Update never creates a row, and the server's existing-row read is scoped to the caller's own
  tenant, so a key held by another tenant is answered exactly as a key held by no tenant. Any other
  answer fails: no status at all (the update was accepted), `PermissionDenied` (7) (the caller was
  refused before the row was read, as a call carrying no acting-user identity is), or any other
  code. All five drivers attempt the
```

7e. Negative-leg prose (`:479-516`). Only its last paragraph is stale; `:479-508` (the payload-tenant and legacy-branch paragraphs) describe the immutability branch, which this change does not touch, and stay as they are. The last paragraph also pointed at the ledger row deleted in 7i:

Before:

```markdown
That branch still exists and still matters for the deployment just described. What it is no longer
is the thing THIS harness's negative leg exercises, and for a broader reason than the harness
registering its types fresh: CSR round 9's Finding #5 mitigation narrows the existing-row read to
the acting tenant unconditionally, for every schema, legacy or fresh. A wrong-tenant caller's update
therefore finds no visible row and takes the no-existing-row branch whatever the target schema's
registration history — so neither the tenant MISMATCH nor the immutability refusal fires on this leg
any more, and the refusal it used to observe is gone. See the IDN coverage ledger's "Cross-tenant
write denial signaled on the wire" row.
```

After:

```markdown
That branch still exists and still matters for the deployment just described. What it is no longer
is the thing THIS harness's negative leg exercises, and for a broader reason than the harness
registering its types fresh: CSR round 9's Finding #5 mitigation narrows the existing-row read to
the acting tenant unconditionally, for every schema, legacy or fresh. A wrong-tenant caller's update
therefore finds no visible row whatever the target schema's registration history — so neither the
tenant MISMATCH nor the immutability refusal fires on this leg any more. What the leg observes
instead is the `NotFound` (5) answer `IVC-IDN-008` grades: Update never creates a row, so a key the
caller's tenant cannot see is answered as missing.
```

7f. Retirement note, inserted as a new paragraph immediately before the existing "`IVC-IDN-004` is retired and re-authored as `IVC-IDN-005`." paragraph (`:522`), in that note's style:

Before:

```markdown
`IVC-IDN-004` is retired and re-authored as `IVC-IDN-005`. Its Statement said "any mapped path",
```

After:

```markdown
`IVC-IDN-007` is retired and re-authored as `IVC-IDN-008`. Its Statement — that a cross-tenant
mapped update is answered without a gRPC error status, the same as an accepted one — described the
server as CSR round 9's Finding #5 mitigation left it, when Update created a row for any key the
caller's tenant could not see. Update no longer answers a cross-tenant write with an
acceptance-shaped response: it is strictly an update, and a key with no row in the caller's tenant
is answered `NotFound` (5) whether another tenant holds the key or none does, so `IVC-IDN-007`'s
assertion would fail on every driver against a current server. The retirement is for that change
alone. The Statement cell is unchanged, per this document's immutable-Statement convention, and
the negative leg, the drivers and the step they report are unchanged too; only the code the
orchestrator expects moved.

`IVC-IDN-004` is retired and re-authored as `IVC-IDN-005`. Its Statement said "any mapped path",
```

7g. Coverage ledger: repoint row `:567` so IDN-008 has a single Covered claimant:

Before:

```markdown
| A cross-tenant update is answered without a gRPC error status | Covered | IVC-IDN-007 |
```

After:

```markdown
| A cross-tenant update is answered with gRPC status NOT_FOUND | Covered | IVC-IDN-008 |
```

7h. Coverage ledger: rewrite the Deferred row `:573` (it cited IDN-007 and described the swallow):

Before:

```markdown
| A header-less caller is now distinguishable from a wrong-tenant caller, but only by accident | Deferred | `IVC-IDN-007`'s assertion grades whether an attempted cross-tenant update is answered without a gRPC error status. A caller with no acting-user token at all is still denied at `RowFieldAuthorizationEvaluator.Evaluate`'s earlier `actingUser is null` check (`AccessDenied`, `PermissionDenied`) — unchanged by CSR round 9's Finding #5 mitigation, since that check runs before the row read this fix narrows — so a header-less caller is graded red by `IVC-IDN-007`, while a genuine wrong-tenant caller's write is now silently swallowed and graded green. This distinguishes the two cases, but not by a designed signal: no requirement asserts on it, and a future change to either check could re-merge or re-invert the two outcomes with nothing here to catch it. |
```

After:

```markdown
| A header-less caller's answer, as distinct from a wrong-tenant caller's | Deferred | `IVC-IDN-008`'s assertion grades the wrong-tenant answer only: an update by an acting user of another tenant must be answered `NotFound` (5). A caller with no acting-user token at all is refused earlier, at `RowFieldAuthorizationEvaluator.Evaluate`'s `actingUser is null` check (`AccessDenied`, `PermissionDenied` (7)), before any row is read. The two answers differ by design — the denial check runs before the existence check, so a caller who is refused anyway is refused the same way whether or not the key exists — but the harness never sends an update without an acting-user identity, so no assertion observes the header-less answer and no requirement constrains it. |
```

7i. Coverage ledger: **delete** row `:575` outright (chosen over keeping it Deferred, see the note below):

Before:

```markdown
| Cross-tenant write denial signaled on the wire | Deferred | The server no longer distinguishes a cross-tenant write from an accepted one on the wire at all, by design (CSR round 9's Finding #5 mitigation) — the only evidence is the server's own audit entry (`reason=BlockedCrossTenantWrite`), which no conformance run reads. No assertion can discharge this without a server-side change to signal the denial differently, which this axis does not make. |
```

After: the line is removed; the ledger's next line is the blank line before `#### Backstop assertion (non-normative)`.

Why delete rather than keep it Deferred: the area it named ("cross-tenant write denial signaled on the wire") is now signaled, as NOT_FOUND, and `IVC-IDN-008` covers exactly that. What remains unsignaled is the difference between a key another tenant holds and a key no tenant holds, and that is deliberate non-enumeration, not deferred work. A Deferred row would read as an invitation to make it observable. It must never be Covered either: that would make it a second Covered claimant of IDN-008 and fail Check4 (confirmed by mutation, see the assumptions file). The only prose reference to this row (the old `:515-516`) is removed in 7e.

7j. Backstop prose (`:579-587`, and the last sentence at `:599-600`):

Before:

```markdown
`IDN`'s negative leg used to be a denial only while the row it targets existed; since CSR round 9's
Finding #5 mitigation, EVERY cross-tenant update takes `EnforceWriteAuthorization`'s
no-existing-row branch and succeeds, so there is no denial left for a missing row to defeat. With no
seeded row, every driver still derives a well-formed key for a row that was never created, that
update is accepted as an ordinary create, and the driver reports no gRPC status code — so
`IVC-IDN-007`'s "answered without a gRPC error status" assertion PASSES in that state exactly as it
does for a genuine swallowed cross-tenant write, and cannot tell the two apart. That is signal this
leg has LOST: before the mitigation the same assertion demanded `PermissionDenied` (7) and therefore
reddened when there was nothing to deny.
```

After:

```markdown
`IDN`'s negative leg used to be a denial only while the row it targets existed. It no longer is one:
EVERY cross-tenant update is answered `NotFound` (5) because the caller's tenant holds no row under
that key, and with no seeded row the caller's tenant holds no row under that key either. Every
driver still derives a well-formed key for a row that was never created, that update is answered
`NotFound` for the same reason, and `IVC-IDN-008`'s assertion PASSES in that state exactly as it
does for a genuine cross-tenant update. It cannot tell the two apart, by design: the server answers
them identically, so the answer reveals nothing about a key another tenant holds. That is signal
this leg lost with CSR round 9's Finding #5 mitigation and has not regained: before the mitigation
the same assertion demanded `PermissionDenied` (7) and therefore reddened when there was nothing to
deny.
```

Before:

```markdown
the backstop fails, the read-back fails too). Against `IVC-IDN-007` the relation does not merely
fail to hold, it inverts: in the no-seeded-row state the backstop fails while `IVC-IDN-007` passes.
```

After:

```markdown
the backstop fails, the read-back fails too). Against `IVC-IDN-008` the relation does not merely
fail to hold, it inverts: in the no-seeded-row state the backstop fails while `IVC-IDN-008` passes.
```

7k. ERR Deferred row "The refusal reason behind `PermissionDenied`" (`:1012`). The row is one line; only its parenthetical sentence changes. The whole line:

Before:

```markdown
| The refusal reason behind `PermissionDenied` | Deferred | `PermissionDenied` (7) is the server's answer to several distinct refusals on the mapped write path, and `AuthorizationFieldMasking.EnforceWriteAuthorization` sets no trailers on any of them: `AccessDenied`, `TenantMismatch` and `OwnerMismatch` share one literal `deniedMessage`, while `TenantImmutable` and `OwnerImmutable` carry their own fixed literals (`"Tenant field is immutable."`, `"Owner field is immutable after creation."`) and `RejectDisallowedFields` throws `InvalidArgument` rather than `PermissionDenied`. A cross-tenant `Update`'s `TenantMismatch` branch specifically is now unreachable (CSR round 9's Finding #5 mitigation narrows the read so `existingRowJson` is always null for a foreign key, so the create branch fires instead and the actual denial, when it happens, is caught and swallowed at the database layer rather than thrown here), leaving `AccessDenied`, `TenantImmutable`, `OwnerMismatch` and `OwnerImmutable` as the reachable `PermissionDenied` branches. No `ERR` assertion observes the distinction either, and closing it needs the server to distinguish refusals on the wire. |
```

After:

```markdown
| The refusal reason behind `PermissionDenied` | Deferred | `PermissionDenied` (7) is the server's answer to several distinct refusals on the mapped write path, and `AuthorizationFieldMasking.EnforceWriteAuthorization` sets no trailers on any of them: `AccessDenied`, `TenantMismatch` and `OwnerMismatch` share one literal `deniedMessage`, while `TenantImmutable` and `OwnerImmutable` carry their own fixed literals (`"Tenant field is immutable."`, `"Owner field is immutable after creation."`) and `RejectDisallowedFields` throws `InvalidArgument` rather than `PermissionDenied`. A cross-tenant `Update`'s `TenantMismatch` branch specifically is unreachable (CSR round 9's Finding #5 mitigation narrows the read so `existingRowJson` is always null for a foreign key, and Update answers a null existing row with `NotFound` (5), as it does a key held by no tenant — see `IVC-IDN-008` — before the tenant comparison is reached), leaving `AccessDenied`, `TenantImmutable`, `OwnerMismatch` and `OwnerImmutable` as the reachable `PermissionDenied` branches. No `ERR` assertion observes the distinction either, and closing it needs the server to distinguish refusals on the wire. |
```

The requirement-count sentence ("This document declares 49 `Active` requirements across nine axes") stays correct: one retired, one added.

- [ ] **Step 8: Rewrite the drivers' negative-leg comments (comment-only)**

Spec §9's "Driver comments" bullet (amended at `422e95a4`). The `denied_update_wrong_acting_user` comments in all five conformance drivers still say the step observes a tenant MISMATCH refusal, and that the harness registering its types fresh is the only reason the leg ignores the payload tenant. They are rewritten to describe NOT_FOUND (5). These are comment edits only; no code line changes.

In each driver, two comments change. First, the tail of the long "update payload's tenant value" comment, i.e. its last five lines, from the line ending "fires there today. The". Second, the comment on the no-error branch. In the .NET driver only, a third comment changes: the register-phase comment, which referred to "the reason the negative leg is" denied.

**.NET** (`Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Program.cs`):

Before:

```csharp
            // orchestrator re-registers it with one before any driver's write phase, without which
            // the positive leg would be denied for the same reason the negative leg is.
```

After:

```csharp
            // orchestrator re-registers it with one before any driver's write phase, without which
            // every write, the positive leg's included, would be denied for want of row permissions.
```

Before:

```csharp
                // InvalidArgument guard does not match — and the immutability branch fires there today. The
                // conformance harness registers its types fresh, which is the ONLY reason this leg is insensitive
                // to the payload tenant. The refusal this step observes is the tenant MISMATCH between the
                // existing row's __TenantId and this wrong acting user's own claim. The acting user's real tenant
                // is still sent here so this leg keeps sending a payload a conforming client would send.
```

After:

```csharp
                // InvalidArgument guard does not match — and the immutability branch fires there today. Even there
                // this leg never reaches it: the server reads the existing row only within the caller's own tenant,
                // so this wrong acting user finds no row, and the update is answered NotFound (5), the same answer
                // an update of a key that exists nowhere receives. The acting user's real tenant is still sent here
                // so this leg keeps sending a payload a conforming client would send.
```

Before:

```csharp
                // No exception: the server accepted the wrong acting user's write. Reported as a
                // missing status code rather than judged here.
```

After:

```csharp
                // No exception: the server accepted the wrong acting user's write, where a conforming
                // server answers NotFound (5). Reported as a missing status code rather than judged here.
```

**Python** (`Iverson.Clients/Python/conformance/driver.py`):

Before:

```python
            # InvalidArgument guard does not match — and the immutability branch fires there today. The
            # conformance harness registers its types fresh, which is the ONLY reason this leg is insensitive
            # to the payload tenant. The refusal this step observes is the tenant MISMATCH between the
            # existing row's __TenantId and this wrong acting user's own claim. The acting user's real tenant
            # is still sent here so this leg keeps sending a payload a conforming client would send.
```

After:

```python
            # InvalidArgument guard does not match — and the immutability branch fires there today. Even there
            # this leg never reaches it: the server reads the existing row only within the caller's own tenant,
            # so this wrong acting user finds no row, and the update is answered NotFound (5), the same answer
            # an update of a key that exists nowhere receives. The acting user's real tenant is still sent here
            # so this leg keeps sending a payload a conforming client would send.
```

Before:

```python
            # No error: the server accepted the wrong acting user's write. Reported as a missing
            # status code rather than judged here.
```

After:

```python
            # No error: the server accepted the wrong acting user's write, where a conforming server
            # answers NotFound (5). Reported as a missing status code rather than judged here.
```

**TypeScript** (`Iverson.Clients/TypeScript/conformance/driver.ts`):

Before:

```typescript
            // InvalidArgument guard does not match — and the immutability branch fires there today. The
            // conformance harness registers its types fresh, which is the ONLY reason this leg is insensitive
            // to the payload tenant. The refusal this step observes is the tenant MISMATCH between the
            // existing row's __TenantId and this wrong acting user's own claim. The acting user's real tenant
            // is still sent here so this leg keeps sending a payload a conforming client would send.
```

After:

```typescript
            // InvalidArgument guard does not match — and the immutability branch fires there today. Even there
            // this leg never reaches it: the server reads the existing row only within the caller's own tenant,
            // so this wrong acting user finds no row, and the update is answered NotFound (5), the same answer
            // an update of a key that exists nowhere receives. The acting user's real tenant is still sent here
            // so this leg keeps sending a payload a conforming client would send.
```

Before:

```typescript
            // No error: the server accepted the wrong acting user's write. Reported as a missing
            // status code rather than judged here.
```

After:

```typescript
            // No error: the server accepted the wrong acting user's write, where a conforming server
            // answers NotFound (5). Reported as a missing status code rather than judged here.
```

**Go** (`Iverson.Clients/Go/conformance/main.go`):

Before:

```go
			// InvalidArgument guard does not match — and the immutability branch fires there today. The
			// conformance harness registers its types fresh, which is the ONLY reason this leg is insensitive
			// to the payload tenant. The refusal this step observes is the tenant MISMATCH between the
			// existing row's __TenantId and this wrong acting user's own claim. The acting user's real tenant
			// is still sent here so this leg keeps sending a payload a conforming client would send.
```

After:

```go
			// InvalidArgument guard does not match — and the immutability branch fires there today. Even there
			// this leg never reaches it: the server reads the existing row only within the caller's own tenant,
			// so this wrong acting user finds no row, and the update is answered NotFound (5), the same answer
			// an update of a key that exists nowhere receives. The acting user's real tenant is still sent here
			// so this leg keeps sending a payload a conforming client would send.
```

Before:

```go
				// The server accepted the wrong acting user's write. Reported as a missing status
				// code rather than judged here.
```

After:

```go
				// The server accepted the wrong acting user's write, where a conforming server
				// answers NotFound (5). Reported as a missing status code rather than judged here.
```

**Java** (`Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance/Driver.java`):

Before:

```java
            // InvalidArgument guard does not match — and the immutability branch fires there today. The
            // conformance harness registers its types fresh, which is the ONLY reason this leg is insensitive
            // to the payload tenant. The refusal this step observes is the tenant MISMATCH between the
            // existing row's __TenantId and this wrong acting user's own claim. The acting user's real tenant
            // is still sent here so this leg keeps sending a payload a conforming client would send.
```

After:

```java
            // InvalidArgument guard does not match — and the immutability branch fires there today. Even there
            // this leg never reaches it: the server reads the existing row only within the caller's own tenant,
            // so this wrong acting user finds no row, and the update is answered NotFound (5), the same answer
            // an update of a key that exists nowhere receives. The acting user's real tenant is still sent here
            // so this leg keeps sending a payload a conforming client would send.
```

Before:

```java
            // The server accepted the wrong acting user's write. Reported as a missing status code
            // rather than judged here.
```

After:

```java
            // The server accepted the wrong acting user's write, where a conforming server answers
            // NotFound (5). Reported as a missing status code rather than judged here.
```

Check that the edit is comment-only. The command prints nothing:

```bash
git diff -U0 -- Iverson.Clients | grep '^[+-]' | grep -v '^+++\|^---' | grep -v '^[+-][[:space:]]*\(//\|#\)'
```

Light checks, none of which needs the network:

```bash
python3 -m py_compile Iverson.Clients/Python/conformance/driver.py
gofmt -l Iverson.Clients/Go/conformance/main.go          # prints nothing
(cd Iverson.Clients/Go/conformance && GOPROXY=off go build -o /dev/null .)
dotnet build Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Iverson.Client.Conformance.Driver.csproj
```

Expected: all pass (the .NET build reports 0 errors). TypeScript and Java are not compile-checked. The TS SDK has no installed `node_modules`. The Java offline `mvn -o compile` fails against a stale SDK artifact in `~/.m2` with 20 `cannot find symbol` errors, and those errors are identical before and after this edit.

- [ ] **Step 9: Sweep for leftovers**

```bash
command grep -rn --exclude-dir=node_modules --exclude-dir=bin --exclude-dir=obj --exclude-dir=.git \
  -e "IVC-IDN-007" -e "IdnCrossTenantUpdateAnsweredWithoutError" -e "answered without a gRPC error status" . \
  | command grep -v "^./docs/specs\|^./docs/plans\|^./docs/criticalreviews"
```

Expected, and nothing else (every one a retirement or history reference):
- `docs/standards/iverson-client-standard.md`: the Retired table row (`:399`) and three lines of the retirement note;
- `Requirements.cs`: the IDN-006 successor sentence (one line) and three lines of the IDN-008 "Supersedes" paragraph;
- `IdentityScenarioTests.cs`: the historical mutant-analysis comment (`:238`) and the reason string of `Judge_WrongActingUsersUpdateWasAccepted_Idn008Fails`.

`command grep` is needed because shell `grep` skips gitignored `docs/`.

- [ ] **Step 10: Run the tests**

```bash
cd Iverson.Server && dotnet test Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj
```

Expected: 640 passed, 0 failed (baseline 638). The driver comment edits do not affect this suite. Of those, `RequirementsCoverageGateTests` is 30/30 and `IdentityScenarioTests` 50/50.

- [ ] **Step 11: Commit**

```bash
git add Iverson.Server/Iverson.ClientConformance/Requirements.cs \
  Iverson.Server/Iverson.ClientConformance/Scenarios/IdentityScenario.cs \
  Iverson.Server/Iverson.ClientConformance.Tests/IdentityScenarioTests.cs \
  Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Program.cs \
  Iverson.Clients/Python/conformance/driver.py \
  Iverson.Clients/TypeScript/conformance/driver.ts \
  Iverson.Clients/Go/conformance/main.go \
  Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance/Driver.java
git add -f docs/standards/iverson-client-standard.md
git commit -m "retire ivc-idn-007 and grade a cross-tenant mapped update as notfound under ivc-idn-008

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 4: Join fields field-authorized

**Files:**
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs` (`BuildFromWithJoins`: check each resolved join column against its own type's `AllowedFields`)
- Test: `Iverson.Server/Iverson.StarRocks.Tests/StarRocksQueryBuilderTests.cs`

Search, Aggregate and GroupBy all build their joins in `BuildFromWithJoins`, so one check there covers all three. `StarRocksPipelineBuilder` already gates join columns through `ColumnsFor`/`RequireColumn` and is not touched.

- [ ] **Step 1: Write the failing tests**

In `StarRocksQueryBuilderTests.cs`, insert a new section immediately after `BuildFromWithJoins_TwoJoins_EachJoinedTypeTenantConstraint_UsesDistinctParameterNames` and before the `// ── BuildGroupBy ───` banner. The tests reuse the class's existing `AuthorSchema()`, `ArticleSchema()` and `BuildRegistry(...)` helpers.

```csharp
    // ── BuildFromWithJoins — join-field authorization ──────────────────────────
    // Search, Aggregate and GroupBy all build their joins here, so these cover all three.

    [Fact]
    public void BuildFromWithJoins_RestrictedLeftJoinField_ThrowsTranslationException()
    {
        var registry = BuildRegistry(AuthorSchema(), ArticleSchema());
        var joins = new List<JoinSpec>
        {
            new() { LeftType = "Author", RightType = "Article", LeftField = "Bio", RightField = "Title", Kind = JoinKind.Inner }
        };
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Author"] = new(AllowedFields: new HashSet<string> { "Id", "Name" }, OwnerColumn: null, OwnerValue: null)
        };

        var act = () => StarRocksQueryBuilder.BuildFromWithJoins(AuthorSchema(), joins, registry, new DynamicParameters(), out _, authz);

        act.Should().Throw<EngagementQueryTranslationException>()
            .WithMessage("Field 'Bio' on 'Author' referenced in join is not authorized for this caller.");
    }

    [Fact]
    public void BuildFromWithJoins_RestrictedRightJoinField_ThrowsTranslationException()
    {
        var registry = BuildRegistry(AuthorSchema(), ArticleSchema());
        var joins = new List<JoinSpec>
        {
            new() { LeftType = "Author", RightType = "Article", LeftField = "Name", RightField = "Body", Kind = JoinKind.Left }
        };
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Article"] = new(AllowedFields: new HashSet<string> { "Id", "Title" }, OwnerColumn: null, OwnerValue: null)
        };

        var act = () => StarRocksQueryBuilder.BuildFromWithJoins(AuthorSchema(), joins, registry, new DynamicParameters(), out _, authz);

        act.Should().Throw<EngagementQueryTranslationException>()
            .WithMessage("Field 'Body' on 'Article' referenced in join is not authorized for this caller.");
    }

    [Fact]
    public void BuildFromWithJoins_AllowedJoinFieldsUnderRestriction_ProducesJoinClause()
    {
        var registry = BuildRegistry(AuthorSchema(), ArticleSchema());
        var joins = new List<JoinSpec>
        {
            new() { LeftType = "Author", RightType = "Article", LeftField = "name", RightField = "title", Kind = JoinKind.Inner }
        };
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Author"]  = new(AllowedFields: new HashSet<string> { "Id", "Name" }, OwnerColumn: null, OwnerValue: null),
            ["Article"] = new(AllowedFields: new HashSet<string> { "Id", "Title" }, OwnerColumn: null, OwnerValue: null)
        };

        var from = StarRocksQueryBuilder.BuildFromWithJoins(AuthorSchema(), joins, registry, new DynamicParameters(), out _, authz);

        // The check runs on the resolved (canonical) column, so a case variant of an allowed
        // field is allowed.
        from.Should().Be("FROM `authors` INNER JOIN `articles` ON `authors`.`Name` = `articles`.`Title`");
    }

    [Fact]
    public void BuildFromWithJoins_NoAuthz_JoinOnAnyField_ProducesJoinClause()
    {
        var registry = BuildRegistry(AuthorSchema(), ArticleSchema());
        var joins = new List<JoinSpec>
        {
            new() { LeftType = "Author", RightType = "Article", LeftField = "Bio", RightField = "Body", Kind = JoinKind.Inner }
        };

        var from = StarRocksQueryBuilder.BuildFromWithJoins(AuthorSchema(), joins, registry, new DynamicParameters(), out _);

        from.Should().Be("FROM `authors` INNER JOIN `articles` ON `authors`.`Bio` = `articles`.`Body`");
    }
```

- [ ] **Step 2: Run the tests and watch two fail**

```bash
cd Iverson.Server && dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName~BuildFromWithJoins"
```
Expected: `Failed: 2, Passed: 15, Total: 17`. The two failures are `BuildFromWithJoins_RestrictedLeftJoinField_ThrowsTranslationException` and `BuildFromWithJoins_RestrictedRightJoinField_ThrowsTranslationException`, because no exception is thrown yet. The allowed and no-`authz` tests already pass.

- [ ] **Step 3: Add the join-field check**

In `StarRocksQueryBuilder.cs`, `BuildFromWithJoins`, declare a local function between the `joins.Count == 0` early return and the `foreach (var join in joins)` loop.

Before:
```csharp
        if (joins.Count == 0)
        {
            tableMap = map;
            return sb.ToString();
        }

        foreach (var join in joins)
        {
```
After:
```csharp
        if (joins.Count == 0)
        {
            tableMap = map;
            return sb.ToString();
        }

        // Same rule as IsFieldAllowed: no authz, no entry for the type, or a null AllowedFields
        // means unrestricted.
        void RequireJoinFieldAllowed(string typeName, string field, string resolvedColumn)
        {
            if (authz is not null && authz.TryGetValue(typeName, out var constraint)
                && constraint.AllowedFields is not null && !constraint.AllowedFields.Contains(resolvedColumn))
                throw new EngagementQueryTranslationException(
                    $"Field '{field}' on '{typeName}' referenced in join is not authorized for this caller.");
        }

        foreach (var join in joins)
        {
```

Then call it for both sides, right after `rightCol` is resolved.

Before:
```csharp
            var rightCol = ResolveColumn(rightCtx.Schema, join.RightField)
                ?? throw new EngagementQueryTranslationException(
                    $"Unknown field '{join.RightField}' on type '{join.RightType}' referenced in join.");

            var kind = join.Kind switch
```
After:
```csharp
            var rightCol = ResolveColumn(rightCtx.Schema, join.RightField)
                ?? throw new EngagementQueryTranslationException(
                    $"Unknown field '{join.RightField}' on type '{join.RightType}' referenced in join.");

            // Join columns are field-authorized like any other reference: a join condition over a
            // restricted column would disclose its values through which rows match. Each side is
            // checked against its own type's constraint.
            RequireJoinFieldAllowed(join.LeftType, join.LeftField, leftCol);
            RequireJoinFieldAllowed(join.RightType, join.RightField, rightCol);

            var kind = join.Kind switch
```

The check uses the resolved (canonical-case) column, the same value `IsFieldAllowed` compares. Its constraint is looked up by `join.LeftType`/`join.RightType`, the same key the existing owner and tenant blocks below use. Production `authz` dictionaries are `StringComparer.OrdinalIgnoreCase`.

- [ ] **Step 4: Run the tests**

```bash
cd Iverson.Server && dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName!~IntegrationTests"
```
Expected: `Passed: 445, Failed: 0` (the 441 baseline plus 4 new). The filter leaves out the four Testcontainers classes (`MatchRowsIntegrationTests`, `PipelineIntegrationTests`, `StarRocksIntegrationTests`, `TenantIsolationIntegrationTests`), none of which joins under an `AllowedFields` restriction.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs Iverson.Server/Iverson.StarRocks.Tests/StarRocksQueryBuilderTests.cs
git commit -m "field-authorize join columns on both sides of every starrocks join

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 5: Expression validator interim patch

**Files:**
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs`:
  - split `DeriveWhitelist` into `DeriveFunctions`/`DeriveKeywords`;
  - add `IsNonColumnToken`;
  - use it at the pipeline-metric and Derive scan sites;
  - make `RejectForbiddenCharacters` reject `"` and `\`, with the new message and XML doc;
  - fix one stale comment.
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs` (use `IsNonColumnToken` at the Aggregate-expression and GroupBy-metric-expression scan sites, and fix two stale comments)
- Test: `Iverson.Server/Iverson.StarRocks.Tests/StarRocksQueryBuilderTests.cs` (Aggregate and GroupBy sites, and the stale `DeriveWhitelist` comment in `Q1StyleRequest`)
- Test: `Iverson.Server/Iverson.StarRocks.Tests/StarRocksPipelineBuilderTests.cs` (pipeline-metric and Derive sites)

**How "restricted" is modelled at each site.** Every site uses a `Ledger` type with columns `Sum`, `Amount`, `Region` and an `AllowedFields` of `{Id, Amount, Region}`, so `Sum` is the restricted column.
- At the two `StarRocksQueryBuilder` sites, `Sum` resolves as a column and fails the `IsFieldAllowed` check.
- At the two pipeline sites, the same constraint is passed to `TrackAndValidate`. `ColumnsFor` then drops `Sum` from the base step's columns, so `Sum` is absent from `input.Columns` (metric) and `available` (Derive) and is rejected as "neither an input column nor a whitelisted function".

The `"`/`\` tests pass no `authz`, since the denylist runs before any token scan.

- [ ] **Step 1: Write the failing QueryBuilder tests**

In `StarRocksQueryBuilderTests.cs`, extend the `// ── Fixtures ───` block by appending after `TagSchema()`:

```csharp
    private static EngagementQuerySchema TagSchema() => new(
        "Tag", "tags", "Id", ["Label"]);

    // A column named after a whitelisted function, restricted from the caller: the expression
    // validator must treat a bare "Sum" as this column, not as the SUM function.
    private static EngagementQuerySchema LedgerSchema() => new(
        "Ledger", "ledgers", "Id", ["Sum", "Amount", "Region"]);

    private static Dictionary<string, AuthorizationConstraint> LedgerAuthzWithoutSum() => new()
    {
        ["Ledger"] = new(AllowedFields: new HashSet<string> { "Id", "Amount", "Region" }, OwnerColumn: null, OwnerValue: null)
    };
```

Insert the Aggregate-site section immediately after `BuildAggregate_NoAuthz_DoesNotEnforceFieldRestrictions` and before `// ── BuildSearch — Equals clause (parameterization) ───`:

```csharp
    // ── BuildAggregate — Expression function names vs. columns ─────────────────

    [Fact]
    public void BuildAggregate_ExpressionUsesRestrictedColumnNamedLikeFunction_ThrowsTranslationException()
    {
        var spec = new AggregationDescriptor(
            "total", AggregationKind.Avg, "Amount", Expression: "Sum * 2");

        var act = () => StarRocksQueryBuilder.BuildAggregate(
            "ledgers", LedgerSchema(), null, spec, authz: LedgerAuthzWithoutSum());

        act.Should().Throw<EngagementQueryTranslationException>()
            .WithMessage("Aggregation field 'Sum' on 'Ledger' is not authorized for this caller.");
    }

    [Theory]
    [InlineData("SUM(Amount)")]
    [InlineData("SUM (Amount)")]
    [InlineData("SUM(Amount) OVER (PARTITION BY Region ORDER BY Amount DESC)")]
    public void BuildAggregate_ExpressionCallsFunction_DoesNotThrow(string expr)
    {
        var spec = new AggregationDescriptor("total", AggregationKind.Avg, "Amount", Expression: expr);

        var act = () => StarRocksQueryBuilder.BuildAggregate(
            "ledgers", LedgerSchema(), null, spec, authz: LedgerAuthzWithoutSum());

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("COALESCE(Amount, \"a\")")]
    [InlineData("Amount \\ 2")]
    public void BuildAggregate_ExpressionWithDoubleQuoteOrBackslash_ThrowsTranslationException(string expr)
    {
        var spec = new AggregationDescriptor("total", AggregationKind.Avg, "Amount", Expression: expr);

        var act = () => StarRocksQueryBuilder.BuildAggregate("ledgers", LedgerSchema(), null, spec);

        act.Should().Throw<EngagementQueryTranslationException>()
            .Where(e => e.Message.Contains("forbidden character"));
    }
```

Insert the GroupBy-site section immediately after `BuildGroupBy_NoAuthz_DoesNotEnforceFieldRestrictions` and before `// ── BuildGroupBy — ORDER BY field reject-on-reference ───`. It reuses the class's existing `BuildRegistry(...)`:

```csharp
    // ── BuildGroupBy — metric expression function names vs. columns ────────────

    private static GroupByRequest LedgerRequest(string expression)
    {
        var request = new GroupByRequest { TypeName = "Ledger", Keys = { "Region" } };
        request.Metrics.Add(new MetricSpec { Name = "m", Type = AggregationType.Avg, Expression = expression });
        return request;
    }

    [Fact]
    public void BuildGroupBy_MetricExpressionUsesRestrictedColumnNamedLikeFunction_ThrowsTranslationException()
    {
        var act = () => StarRocksQueryBuilder.BuildGroupBy(
            "ledgers", LedgerSchema(), LedgerRequest("Sum * 2"), BuildRegistry(LedgerSchema()), authz: LedgerAuthzWithoutSum());

        act.Should().Throw<EngagementQueryTranslationException>()
            .WithMessage("Field 'Sum' on 'Ledger' referenced by metric 'm' expression is not authorized for this caller.");
    }

    [Theory]
    [InlineData("SUM(Amount)")]
    [InlineData("SUM (Amount)")]
    [InlineData("SUM(Amount) OVER (PARTITION BY Region ORDER BY Amount DESC)")]
    public void BuildGroupBy_MetricExpressionCallsFunction_DoesNotThrow(string expr)
    {
        var act = () => StarRocksQueryBuilder.BuildGroupBy(
            "ledgers", LedgerSchema(), LedgerRequest(expr), BuildRegistry(LedgerSchema()), authz: LedgerAuthzWithoutSum());

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("COALESCE(Amount, \"a\")")]
    [InlineData("Amount \\ 2")]
    public void BuildGroupBy_MetricExpressionWithDoubleQuoteOrBackslash_ThrowsTranslationException(string expr)
    {
        var act = () => StarRocksQueryBuilder.BuildGroupBy(
            "ledgers", LedgerSchema(), LedgerRequest(expr), BuildRegistry(LedgerSchema()));

        act.Should().Throw<EngagementQueryTranslationException>()
            .Where(e => e.Message.Contains("forbidden character"));
    }
```

In `Q1StyleRequest()`, the comment names the removed set. Change:

```csharp
        // COALESCE is a DeriveWhitelist-recognized function (unlike LENGTH, which the new
```
to:
```csharp
        // COALESCE is a DeriveFunctions-recognized function (unlike LENGTH, which the new
```

- [ ] **Step 2: Write the failing Pipeline tests**

In `StarRocksPipelineBuilderTests.cs`, insert this section immediately after `Validate_MetricExpressionUsesWhitelistedFunction_DoesNotThrow` and before `// ── Authorization — "all: true" scoping (Step 2) ───`. It reuses the class's existing `EmptyRegistry()` and `AssertInvalid(...)`:

```csharp
    // ── Expressions — function names vs. columns ───────────────────────────────
    // "Sum" is both a column and a whitelisted function name. Field restriction drops it from
    // the step's visible columns (ColumnsFor), so a bare reference must be rejected rather than
    // skipped as the SUM function.

    private static EngagementQuerySchema LedgerSchema() => new(
        "Ledger", "ledgers", "Id", ["Sum", "Amount", "Region"]);

    private static Dictionary<string, AuthorizationConstraint> LedgerAuthzWithoutSum() => new()
    {
        ["Ledger"] = new(AllowedFields: new HashSet<string> { "Id", "Amount", "Region" }, OwnerColumn: null, OwnerValue: null)
    };

    private static PipelineRequest LedgerRequest(PipelineStep step)
    {
        var r = new PipelineRequest { TypeName = "Ledger" };
        r.Steps.Add(step);
        return r;
    }

    private static PipelineStep MetricStep(string expression)
    {
        var step = new PipelineStep { Name = "agg" };
        step.GroupBy.Add(new GroupKey { Field = "Region" });
        step.Metrics.Add(new MetricSpec { Name = "m", Type = AggregationType.Avg, Expression = expression });
        return step;
    }

    private static PipelineStep DeriveStep(string expression)
    {
        var step = new PipelineStep { Name = "d" };
        step.Derive.Add(new DeriveColumn { Alias = "x", Expr = expression });
        return step;
    }

    [Fact]
    public void Validate_MetricExpressionUsesRestrictedColumnNamedLikeFunction_Throws()
    {
        var act = () => StarRocksPipelineBuilder.TrackAndValidate(
            LedgerSchema(), LedgerRequest(MetricStep("Sum * 2")), EmptyRegistry(), LedgerAuthzWithoutSum());

        act.Should().Throw<EngagementQueryTranslationException>()
            .WithMessage("Step 'agg': metric 'm' expression references 'Sum', which is neither an input column nor a whitelisted function.");
    }

    [Theory]
    [InlineData("SUM(Amount)")]
    [InlineData("SUM (Amount)")]
    [InlineData("SUM(Amount) OVER (PARTITION BY Region ORDER BY Amount DESC)")]
    public void Validate_MetricExpressionCallsFunction_DoesNotThrow(string expr)
    {
        var act = () => StarRocksPipelineBuilder.TrackAndValidate(
            LedgerSchema(), LedgerRequest(MetricStep(expr)), EmptyRegistry(), LedgerAuthzWithoutSum());

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("COALESCE(Amount, \"a\")")]
    [InlineData("Amount \\ 2")]
    public void Validate_MetricExpressionWithDoubleQuoteOrBackslash_Throws(string expr) =>
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            LedgerSchema(), LedgerRequest(MetricStep(expr)), EmptyRegistry()), "forbidden character");

    [Fact]
    public void Validate_DeriveUsesRestrictedColumnNamedLikeFunction_Throws()
    {
        var act = () => StarRocksPipelineBuilder.TrackAndValidate(
            LedgerSchema(), LedgerRequest(DeriveStep("Sum * 2")), EmptyRegistry(), LedgerAuthzWithoutSum());

        act.Should().Throw<EngagementQueryTranslationException>()
            .WithMessage("Step 'd': derive 'x' references 'Sum', which is neither an input column nor a whitelisted function.");
    }

    [Theory]
    [InlineData("SUM(Amount)")]
    [InlineData("SUM (Amount)")]
    [InlineData("SUM(Amount) OVER (PARTITION BY Region ORDER BY Amount DESC)")]
    public void Validate_DeriveCallsFunction_DoesNotThrow(string expr)
    {
        var act = () => StarRocksPipelineBuilder.TrackAndValidate(
            LedgerSchema(), LedgerRequest(DeriveStep(expr)), EmptyRegistry(), LedgerAuthzWithoutSum());

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("COALESCE(Amount, \"a\")")]
    [InlineData("Amount \\ 2")]
    public void Validate_DeriveWithDoubleQuoteOrBackslash_Throws(string expr) =>
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            LedgerSchema(), LedgerRequest(DeriveStep(expr)), EmptyRegistry()), "forbidden character");
```

- [ ] **Step 3: Run the tests and watch twelve fail**

```bash
cd Iverson.Server && dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName!~IntegrationTests"
```
Expected: `Failed: 12, Passed: 457, Total: 469`. The twelve failures are three at each of the four sites: the bare-`Sum` test (the old whitelist skips `Sum` as a function), and both `"`/`\` cases (not yet in the denylist). The 12 function-call cases already pass. They are regression guards for the lookahead and the whitespace skip.

- [ ] **Step 4: Split the whitelist and add `IsNonColumnToken`**

In `StarRocksPipelineBuilder.cs`, replace:

```csharp
    // Identifiers a Derive expression may use besides input columns. Anything else —
    // including SELECT/FROM/WHERE, which blocks subqueries — fails validation.
    internal static readonly HashSet<string> DeriveWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "AVG", "MIN", "MAX", "COUNT", "OVER", "PARTITION", "BY", "ORDER",
        "ASC", "DESC", "COALESCE", "NULLIF", "ROUND", "ABS", "AND", "OR", "NOT", "NULL"
    };
```
with:
```csharp
    // Identifiers a raw expression may use besides input columns. Anything else — including
    // SELECT/FROM/WHERE, which blocks subqueries — fails validation. A function name counts
    // only when called (see IsNonColumnToken): a column can share a function's name, and a bare
    // reference to it must be checked as a column. Keywords can't be unquoted column names.
    private static readonly HashSet<string> DeriveFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "AVG", "MIN", "MAX", "COUNT", "COALESCE", "NULLIF", "ROUND", "ABS"
    };

    private static readonly HashSet<string> DeriveKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "OVER", "PARTITION", "BY", "ORDER", "ASC", "DESC", "AND", "OR", "NOT", "NULL"
    };

    /// <summary>
    /// True when <paramref name="token"/> (a <see cref="TokenRx"/> match in <paramref name="expr"/>)
    /// is a whitelisted keyword, or a whitelisted function name whose next non-whitespace
    /// character is <c>(</c>. Every other token is the caller's to resolve as a column.
    /// </summary>
    internal static bool IsNonColumnToken(string expr, Match token)
    {
        if (DeriveKeywords.Contains(token.Value)) return true;
        if (!DeriveFunctions.Contains(token.Value)) return false;

        var i = token.Index + token.Length;
        while (i < expr.Length && char.IsWhiteSpace(expr[i])) i++;
        return i < expr.Length && expr[i] == '(';
    }
```

The two sets are `private`: after this task only `IsNonColumnToken` reads them, and `StarRocksQueryBuilder` calls `IsNonColumnToken` instead.

- [ ] **Step 5: Use `IsNonColumnToken` at all four scan sites**

`StarRocksPipelineBuilder.cs`, pipeline metric expression (in `ValidateStepAndComputeOutput`). Change:
```csharp
                        if (DeriveWhitelist.Contains(tok.Value)) continue;
```
to:
```csharp
                        if (IsNonColumnToken(m.Expression, tok)) continue;
```

`StarRocksPipelineBuilder.cs`, Derive (`ValidateDeriveExpr`). Change:
```csharp
            if (DeriveWhitelist.Contains(m.Value)) continue;
            if (available.ContainsKey(m.Value)) continue;
```
to:
```csharp
            if (IsNonColumnToken(d.Expr, m)) continue;
            if (available.ContainsKey(m.Value)) continue;
```

`StarRocksQueryBuilder.cs`, Aggregate expression (`BuildAggregate`). Change:
```csharp
                if (StarRocksPipelineBuilder.DeriveWhitelist.Contains(m.Value)) continue;
                CheckFieldAllowed(m.Value);
```
to:
```csharp
                if (StarRocksPipelineBuilder.IsNonColumnToken(spec.Expression, m)) continue;
                CheckFieldAllowed(m.Value);
```

`StarRocksQueryBuilder.cs`, GroupBy metric expression (`BuildMetricExpr`). Change:
```csharp
                if (StarRocksPipelineBuilder.DeriveWhitelist.Contains(m.Value)) continue;
                var resolvedToken = ResolveColumn(tableMap, m.Value)
```
to:
```csharp
                if (StarRocksPipelineBuilder.IsNonColumnToken(metric.Expression, m)) continue;
                var resolvedToken = ResolveColumn(tableMap, m.Value)
```

Three comments name the removed set. In each, replace `TokenRx/DeriveWhitelist` with `TokenRx/IsNonColumnToken` and change nothing else:
- `StarRocksQueryBuilder.cs`, the comment above `AggregationKind.Avg` in `BuildAggregate` (`// RejectForbiddenCharacters + the TokenRx/DeriveWhitelist identifier allow-list before`);
- `StarRocksQueryBuilder.cs`, the comment above `BuildMetricExpr` (`// the TokenRx/DeriveWhitelist identifier allow-list, and subject to the same field`);
- `StarRocksPipelineBuilder.cs`, the comment in `EmitMetric` (`// TokenRx/DeriveWhitelist identifier allow-list; do not weaken either check based on an`).

- [ ] **Step 6: Forbid `"` and `\` in `RejectForbiddenCharacters`**

In `StarRocksPipelineBuilder.cs`, replace the XML doc and the check.

Before:
```csharp
    /// <summary>
    /// Rejects a raw SQL fragment that contains a semicolon, quote, backtick, or SQL comment
    /// sequence — characters that a token-shaped identifier allow-list (<see cref="TokenRx"/>/
    /// <see cref="DeriveWhitelist"/>) alone would never inspect, since none of them ever match
    /// an identifier pattern in the first place. Shared by every raw-expression field spliced
    /// into generated SQL (<see cref="ValidateDeriveExpr"/> below, and
    /// <c>StarRocksQueryBuilder.BuildAggregate</c>/<c>BuildMetricExpr</c>) so all such fields
    /// get the same defense-in-depth denylist, not just whichever one a reviewer happened to
    /// look at most recently.
    /// </summary>
```
After:
```csharp
    /// <summary>
    /// Rejects a raw SQL fragment that contains a semicolon, quote (single or double), backtick,
    /// backslash, or SQL comment sequence — characters that a token-shaped identifier allow-list
    /// (<see cref="TokenRx"/>/<see cref="IsNonColumnToken"/>) alone would never inspect, since
    /// none of them ever match an identifier pattern in the first place. Shared by every
    /// raw-expression field spliced into generated SQL (<see cref="ValidateDeriveExpr"/> below,
    /// and <c>StarRocksQueryBuilder.BuildAggregate</c>/<c>BuildMetricExpr</c>) so all such fields
    /// get the same defense-in-depth denylist, not just whichever one a reviewer happened to
    /// look at most recently.
    /// </summary>
```

Before:
```csharp
        if (expr.Contains(';') || expr.Contains('\'') || expr.Contains('`') ||
            expr.Contains('#') ||
            expr.Contains("--") || expr.Contains("/*") || expr.Contains("*/"))
            throw Invalid($"{errorContext} contains a forbidden character " +
                          "(no semicolons, quotes, backticks, or SQL comment sequences, including '#').");
```
After:
```csharp
        if (expr.Contains(';') || expr.Contains('\'') || expr.Contains('"') || expr.Contains('`') ||
            expr.Contains('\\') || expr.Contains('#') ||
            expr.Contains("--") || expr.Contains("/*") || expr.Contains("*/"))
            throw Invalid($"{errorContext} contains a forbidden character " +
                          "(no semicolons, quotes (single or double), backticks, backslashes, or SQL comment sequences, including '#').");
```

No existing test asserts the old message verbatim. The existing denylist tests match on `Contains("forbidden character")`, and one also on `Contains("SQL comment sequences")` (`StarRocksPipelineBuilderTests.Build_DeriveExprWithSqlCommentSequence_Throws`). Both substrings survive the new text, so no existing test changes.

- [ ] **Step 7: Run the tests**

```bash
cd Iverson.Server && dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName!~IntegrationTests"
```
Expected: `Passed: 469, Failed: 0`, which is 445 after Task 4 plus 24 new cases (6 per site × 4).

Then run the two builder classes in full:
```bash
cd Iverson.Server && dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName~StarRocksQueryBuilderTests|FullyQualifiedName~StarRocksPipelineBuilderTests"
```
Expected: `Passed: 313, Failed: 0` (`StarRocksQueryBuilderTests` 196, `StarRocksPipelineBuilderTests` 117).

- [ ] **Step 8: Commit**

```bash
git add Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs Iverson.Server/Iverson.StarRocks.Tests/StarRocksQueryBuilderTests.cs Iverson.Server/Iverson.StarRocks.Tests/StarRocksPipelineBuilderTests.cs
git commit -m "treat a whitelisted function name as a column unless it is called, and forbid double quotes and backslashes in raw expressions

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 6: Token revocation store and cache

**Files:**
- Create: `Iverson.Server/Iverson.Sql/TokenRevocationRepository.cs`
- Modify: `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs` (new `ITokenRevocationRepository`, after `IEnrichmentStateRepository`)
- Create: `Iverson.Server/Iverson.Api/Tenancy/ITokenRevocationCache.cs`
- Create: `Iverson.Server/Iverson.Api/Tenancy/TokenRevocationCache.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs` (DI registrations beside `TenantStatusCache`; `EnsureTableAsync` at startup after the re-render-queue bootstrap)
- Modify: `Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs` (new `NoOpTokenRevocationRepository`)
- Modify: `Iverson.Server/Iverson.Api.Tests/Helpers/AuthTestWebApplicationFactory.cs` (registers it beside `NoOpTenantRepository`)
- Modify: `Iverson.Server/Iverson.Sql.Tests/ContainerCollection.cs` (doc comment: "three" Postgres-backed classes becomes "four")
- Test: `Iverson.Server/Iverson.Sql.Tests/TokenRevocationRepositoryPostgresIntegrationTests.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Tenancy/TokenRevocationCacheTests.cs`

**Interfaces:** Produces:
- `Iverson.Sql.ITokenRevocationRepository { Task EnsureTableAsync(); Task RevokeAsync(string sub); Task<IEnumerable<(string Sub, DateTimeOffset RevokedAt)>> ListAsync(); }`, registered as a singleton;
- `Iverson.Api.Tenancy.ITokenRevocationCache { Task<bool> IsRevokedAsync(string sub, DateTimeOffset? issuedAt); }`, registered as a singleton;
- `NoOpTokenRevocationRepository` in every `AuthTestWebApplicationFactory` host.

Task 7 consumes all three.

Notes:
- `TimeProvider`: `Microsoft.Extensions.TimeProvider.Testing` (`FakeTimeProvider`) is referenced nowhere in `Iverson.Server`, so the cache test hand-rolls a minimal `TimeProvider` subclass, as `Iverson.StarRocks.Tests/EngagementRepositoryLivenessRewrapTests.cs` does. `TimeProvider` is not registered in DI, so `Program.cs` passes `TimeProvider.System` from a factory registration.
- The repository has no mocked unit test (unlike `EnrichmentStateRepositoryTests`). Spec §10 asks only for Testcontainers coverage, and every method is plain SQL that only a real Postgres exercises.
- The spec puts the NoOp "in `AuthTestWebApplicationFactory.cs`". Its sibling NoOp classes live in `Helpers/StartupNoOpFakes.cs` and are only *registered* in the factory, so the class goes there and the registration goes in the factory.

- [ ] **Step 1: Write the repository's Testcontainers tests**

Create `Iverson.Server/Iverson.Sql.Tests/TokenRevocationRepositoryPostgresIntegrationTests.cs`. It has its own per-class container fixture, following the assembly's convention (`TenantRepositoryPostgresContainerFixture`, `DlqRepositoryPostgresContainerFixture`). It needs no `SchemaManager` and no roles: the table is raw DDL run as the owner, as `EnrichmentStateRepository`'s is.

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace Iverson.Sql.Tests;

/// <summary>
/// Exercises <see cref="TokenRevocationRepository"/> against a real Postgres instance: its DDL,
/// its upsert and its <c>timestamptz</c> read are all SQL that a substituted
/// <see cref="IRecordStoreQueryExecutor"/> never runs (see
/// <see cref="TenantRepositoryPostgresContainerFixture"/> for the <c>timestamptz</c>/Dapper defect
/// class a mocked test cannot see).
/// </summary>
public sealed class TokenRevocationRepositoryPostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public PostgresRepository Repository { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Repository = new PostgresRepository(
            _container.GetConnectionString(),
            NullLogger<PostgresRepository>.Instance);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[Trait("Category", "Integration")]
[Collection(ContainerCollection.Name)]
public sealed class TokenRevocationRepositoryPostgresIntegrationTests(TokenRevocationRepositoryPostgresContainerFixture fixture)
    : IClassFixture<TokenRevocationRepositoryPostgresContainerFixture>
{
    private readonly PostgresRepository _sql = fixture.Repository;

    private async Task<TokenRevocationRepository> FreshRepositoryAsync()
    {
        await _sql.ExecuteAsync("DROP TABLE IF EXISTS iverson_token_revocations");
        var repo = new TokenRevocationRepository(_sql);
        await repo.EnsureTableAsync();
        return repo;
    }

    [Fact]
    public async Task EnsureTableAsync_CreatesAnEmptyTable_AndIsIdempotent()
    {
        var repo = await FreshRepositoryAsync();

        await repo.EnsureTableAsync();

        (await repo.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task RevokeAsync_ThenListAsync_ReturnsTheSubWithTheDatabaseTime()
    {
        var repo = await FreshRepositoryAsync();

        await repo.RevokeAsync("user-uid-1");

        var rows = (await repo.ListAsync()).ToList();
        rows.Should().ContainSingle();
        rows[0].Sub.Should().Be("user-uid-1");
        rows[0].RevokedAt.Offset.Should().Be(TimeSpan.Zero);
        rows[0].RevokedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task RevokeAsync_SubAlreadyRevoked_MovesRevokedAtForwardInPlace()
    {
        var repo = await FreshRepositoryAsync();
        await _sql.ExecuteAsync(
            "INSERT INTO iverson_token_revocations (sub, revoked_at) VALUES ('user-uid-1', '2000-01-01T00:00:00Z')");

        await repo.RevokeAsync("user-uid-1");

        var rows = (await repo.ListAsync()).ToList();
        rows.Should().ContainSingle();
        rows[0].RevokedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task ListAsync_ReturnsEveryRevokedSub()
    {
        var repo = await FreshRepositoryAsync();

        await repo.RevokeAsync("user-uid-1");
        await repo.RevokeAsync("user-uid-2");

        (await repo.ListAsync()).Select(r => r.Sub).Should().BeEquivalentTo("user-uid-1", "user-uid-2");
    }
}
```

In `Iverson.Server/Iverson.Sql.Tests/ContainerCollection.cs`, the class comment counts the container-backed classes. Replace:

```csharp
/// The collection every container-backed test class in this assembly joins, so its three
```

with:

```csharp
/// The collection every container-backed test class in this assembly joins, so its four
```

- [ ] **Step 2: Write the cache tests**

Create `Iverson.Server/Iverson.Api.Tests/Tenancy/TokenRevocationCacheTests.cs`:

```csharp
using FluentAssertions;
using Iverson.Api.Tenancy;
using Iverson.Sql;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Iverson.Api.Tests.Tenancy;

public class TokenRevocationCacheTests
{
    private static readonly DateTimeOffset RevokedAt = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly ITokenRevocationRepository _repository = Substitute.For<ITokenRevocationRepository>();
    private readonly ManualTimeProvider _time = new(RevokedAt.AddMinutes(5));
    private readonly TokenRevocationCache _sut;

    public TokenRevocationCacheTests()
    {
        _repository.ListAsync().Returns(Rows(("revoked-sub", RevokedAt)));
        _sut = new TokenRevocationCache(_repository, _time);
    }

    private static IEnumerable<(string Sub, DateTimeOffset RevokedAt)> Rows(
        params (string Sub, DateTimeOffset RevokedAt)[] rows) => rows;

    /// <summary>A clock that only moves when told to.</summary>
    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task IsRevokedAsync_IssuedBeforeRevocation_IsRevoked()
    {
        (await _sut.IsRevokedAsync("revoked-sub", RevokedAt.AddMinutes(-10))).Should().BeTrue();
    }

    [Fact]
    public async Task IsRevokedAsync_IssuedAtTheRevocationInstant_IsRevoked()
    {
        (await _sut.IsRevokedAsync("revoked-sub", RevokedAt)).Should().BeTrue();
    }

    [Fact]
    public async Task IsRevokedAsync_IssuedAfterRevocation_IsNotRevoked()
    {
        (await _sut.IsRevokedAsync("revoked-sub", RevokedAt.AddSeconds(1))).Should().BeFalse();
    }

    [Fact]
    public async Task IsRevokedAsync_MissingIatWithRevokedSub_IsRevoked()
    {
        (await _sut.IsRevokedAsync("revoked-sub", issuedAt: null)).Should().BeTrue();
    }

    [Fact]
    public async Task IsRevokedAsync_UnknownSub_IsNotRevoked()
    {
        (await _sut.IsRevokedAsync("someone-else", RevokedAt.AddMinutes(-10))).Should().BeFalse();
        (await _sut.IsRevokedAsync("someone-else", issuedAt: null)).Should().BeFalse();
    }

    [Fact]
    public async Task IsRevokedAsync_WithinThirtySeconds_ServesTheSnapshotWithoutReloading()
    {
        await _sut.IsRevokedAsync("revoked-sub", null);
        _repository.ListAsync().Returns(Rows());
        _time.Advance(TimeSpan.FromSeconds(29));

        (await _sut.IsRevokedAsync("revoked-sub", null)).Should().BeTrue();
        await _repository.Received(1).ListAsync();
    }

    [Fact]
    public async Task IsRevokedAsync_AfterThirtySeconds_ReloadsAndSeesANewRevocation()
    {
        (await _sut.IsRevokedAsync("newly-revoked-sub", null)).Should().BeFalse();
        _repository.ListAsync().Returns(Rows(("revoked-sub", RevokedAt), ("newly-revoked-sub", RevokedAt.AddMinutes(1))));
        _time.Advance(TimeSpan.FromSeconds(31));

        (await _sut.IsRevokedAsync("newly-revoked-sub", null)).Should().BeTrue();
        await _repository.Received(2).ListAsync();
    }

    [Fact]
    public async Task IsRevokedAsync_ConcurrentCallers_TriggerOneReload()
    {
        var pending = new TaskCompletionSource<IEnumerable<(string Sub, DateTimeOffset RevokedAt)>>();
        _repository.ListAsync().Returns(pending.Task);

        var callers = Enumerable.Range(0, 8)
            .Select(_ => _sut.IsRevokedAsync("revoked-sub", null))
            .ToList();
        pending.SetResult(Rows(("revoked-sub", RevokedAt)));
        var results = await Task.WhenAll(callers);

        results.Should().AllSatisfy(revoked => revoked.Should().BeTrue());
        await _repository.Received(1).ListAsync();
    }

    [Fact]
    public async Task IsRevokedAsync_ReloadFailure_Propagates()
    {
        _repository.ListAsync().ThrowsAsync(new InvalidOperationException("postgres is down"));

        var act = () => _sut.IsRevokedAsync("revoked-sub", null);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("postgres is down");
    }
}
```

- [ ] **Step 3: Run the tests to see them fail**

```bash
cd Iverson.Server && dotnet build Iverson.Sql.Tests/Iverson.Sql.Tests.csproj
```

Expected: the build fails with `error CS0246: The type or namespace name 'TokenRevocationRepository' could not be found` (observed). The Api test project fails the same way on `TokenRevocationCache` / `ITokenRevocationRepository`.

- [ ] **Step 4: Add the repository interface**

In `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs`, replace:

```csharp
    Task DeleteAsync(string tenantId, string typeName, string entityKey);
}

public interface ISchemaRegistryRepository
```

with:

```csharp
    Task DeleteAsync(string tenantId, string typeName, string entityKey);
}

public interface ITokenRevocationRepository
{
    Task EnsureTableAsync();
    Task RevokeAsync(string sub);
    Task<IEnumerable<(string Sub, DateTimeOffset RevokedAt)>> ListAsync();
}

public interface ISchemaRegistryRepository
```

- [ ] **Step 5: Implement the repository**

Create `Iverson.Server/Iverson.Sql/TokenRevocationRepository.cs`. The query shape reads `revoked_at` as `DateTime`, then converts it. Npgsql materializes `timestamptz` as a UTC `DateTime`, and Dapper does not convert that to `DateTimeOffset`; this is the defect class `TenantRepositoryPostgresIntegrationTests` documents.

```csharp
namespace Iverson.Sql;

/// <summary>
/// CSR round-10 #11: one row per user whose tokens must stop working — a removed user, or a
/// demoted tenant admin — keyed by the token's <c>sub</c>. A token is refused when its
/// <c>iat</c> is at or before <c>revoked_at</c>, so a fresh login afterwards is accepted. Rows
/// are never pruned: the table holds one row per removed or demoted user, and pruning would
/// mean depending on token lifetimes.
/// </summary>
public sealed class TokenRevocationRepository(IRecordStoreQueryExecutor sql) : ITokenRevocationRepository
{
    public Task EnsureTableAsync() =>
        sql.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS iverson_token_revocations (
                sub        TEXT PRIMARY KEY,
                revoked_at TIMESTAMPTZ NOT NULL
            )
            """);

    public Task RevokeAsync(string sub) =>
        sql.ExecuteAsync(
            """
            INSERT INTO iverson_token_revocations (sub, revoked_at)
            VALUES (@Sub, now())
            ON CONFLICT (sub) DO UPDATE
                SET revoked_at = EXCLUDED.revoked_at
            """,
            new { Sub = sub });

    // DateTime, not DateTimeOffset, in the query shape: Npgsql materializes timestamptz as a UTC
    // DateTime, and Dapper will not convert it (see TenantRow's note in IRecordStoreRoles.cs).
    public async Task<IEnumerable<(string Sub, DateTimeOffset RevokedAt)>> ListAsync()
    {
        var rows = await sql.QueryAsync<(string sub, DateTime revoked_at)>(
            "SELECT sub, revoked_at FROM iverson_token_revocations");
        return rows.Select(r => (r.sub, new DateTimeOffset(r.revoked_at, TimeSpan.Zero)));
    }
}
```

- [ ] **Step 6: Implement the cache**

Create `Iverson.Server/Iverson.Api/Tenancy/ITokenRevocationCache.cs`:

```csharp
namespace Iverson.Api.Tenancy;

public interface ITokenRevocationCache
{
    /// <summary>
    /// True when <paramref name="sub"/> has been revoked and the token was issued at or before
    /// that revocation, or carries no <c>iat</c> at all.
    /// </summary>
    Task<bool> IsRevokedAsync(string sub, DateTimeOffset? issuedAt);
}
```

Create `Iverson.Server/Iverson.Api/Tenancy/TokenRevocationCache.cs`:

```csharp
using Iverson.Sql;

namespace Iverson.Api.Tenancy;

/// <summary>
/// CSR round-10 #11: answers "has this token been revoked?" for every authenticated request
/// (both JwtBearer schemes' <c>OnTokenValidated</c>) from an in-memory snapshot of
/// <see cref="ITokenRevocationRepository"/>, reloaded at most every 30 s — the same staleness
/// <see cref="TenantStatusCache"/> accepts. One reload runs at a time; concurrent callers wait
/// for it rather than each querying Postgres. A reload failure propagates, so the request fails
/// rather than skipping the check, as <see cref="TenantStatusCache"/> does.
/// </summary>
public sealed class TokenRevocationCache(
    ITokenRevocationRepository repository,
    TimeProvider timeProvider) : ITokenRevocationCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private volatile Snapshot? _snapshot;

    private sealed record Snapshot(IReadOnlyDictionary<string, DateTimeOffset> RevokedAt, DateTimeOffset LoadedAt);

    public async Task<bool> IsRevokedAsync(string sub, DateTimeOffset? issuedAt)
    {
        var snapshot = await CurrentSnapshotAsync();

        // A token with no iat cannot show it was issued after the revocation, so it is refused.
        return snapshot.RevokedAt.TryGetValue(sub, out var revokedAt)
            && (issuedAt is null || issuedAt <= revokedAt);
    }

    private async Task<Snapshot> CurrentSnapshotAsync()
    {
        var snapshot = _snapshot;
        if (snapshot is not null && !IsStale(snapshot))
            return snapshot;

        await _reloadLock.WaitAsync();
        try
        {
            // Re-check under the lock: a caller that waited here behind another caller's reload
            // uses that reload instead of starting its own.
            snapshot = _snapshot;
            if (snapshot is not null && !IsStale(snapshot))
                return snapshot;

            var rows = await repository.ListAsync();
            snapshot = new Snapshot(
                rows.ToDictionary(r => r.Sub, r => r.RevokedAt, StringComparer.Ordinal),
                timeProvider.GetUtcNow());
            _snapshot = snapshot;
            return snapshot;
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    private bool IsStale(Snapshot snapshot) => timeProvider.GetUtcNow() - snapshot.LoadedAt > Ttl;
}
```

- [ ] **Step 7: Register both and create the table at startup**

In `Iverson.Server/Iverson.Api/Program.cs`, replace:

```csharp
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<Iverson.Api.Tenancy.ITenantStatusCache, Iverson.Api.Tenancy.TenantStatusCache>();
```

with:

```csharp
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<Iverson.Api.Tenancy.ITenantStatusCache, Iverson.Api.Tenancy.TenantStatusCache>();
// CSR round-10 #11: the subs of removed and demoted users, and the 30 s snapshot of them that
// the JwtBearer schemes check every token against.
builder.Services.AddSingleton<ITokenRevocationRepository>(sp =>
    new TokenRevocationRepository(sp.GetRequiredService<IRecordStoreQueryExecutor>()));
builder.Services.AddSingleton<Iverson.Api.Tenancy.ITokenRevocationCache>(sp =>
    new Iverson.Api.Tenancy.TokenRevocationCache(
        sp.GetRequiredService<ITokenRevocationRepository>(), TimeProvider.System));
```

Then replace:

```csharp
await app.Services.GetRequiredService<IDocumentRerenderQueueRepository>().EnsureTableAsync();
```

with:

```csharp
await app.Services.GetRequiredService<IDocumentRerenderQueueRepository>().EnsureTableAsync();

// Token revocations (CSR round-10 #11) — same bootstrap shape: raw DDL, run as the owner role
// through the plain executor, so no grants are needed.
await app.Services.GetRequiredService<ITokenRevocationRepository>().EnsureTableAsync();
```

- [ ] **Step 8: Give the pipeline-test host a no-op repository**

Every `WebApplicationFactory<Program>` host now runs `EnsureTableAsync()` at startup, and from Task 7 on, every authenticated request reads `ListAsync()`. With no Postgres, both must be faked.

In `Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs`, replace:

```csharp
    public Task DeleteAsync(string id) => Task.CompletedTask;
}

// /admin/dlq now authenticates the acting user before calling IDlqRepository.ListUnreplayedAsync,
```

with:

```csharp
    public Task DeleteAsync(string id) => Task.CompletedTask;
}

// Program.cs calls ITokenRevocationRepository.EnsureTableAsync() during hydration, and every
// authenticated request reads ListAsync() through TokenRevocationCache: nothing is revoked here.
internal sealed class NoOpTokenRevocationRepository : ITokenRevocationRepository
{
    public Task EnsureTableAsync() => Task.CompletedTask;
    public Task RevokeAsync(string sub) => Task.CompletedTask;
    public Task<IEnumerable<(string Sub, DateTimeOffset RevokedAt)>> ListAsync() =>
        Task.FromResult(Enumerable.Empty<(string Sub, DateTimeOffset RevokedAt)>());
}

// /admin/dlq now authenticates the acting user before calling IDlqRepository.ListUnreplayedAsync,
```

In `Iverson.Server/Iverson.Api.Tests/Helpers/AuthTestWebApplicationFactory.cs`, replace:

```csharp
            services.RemoveAll<ITenantRepository>();
            services.AddSingleton<ITenantRepository, NoOpTenantRepository>();
```

with:

```csharp
            services.RemoveAll<ITenantRepository>();
            services.AddSingleton<ITenantRepository, NoOpTenantRepository>();

            services.RemoveAll<ITokenRevocationRepository>();
            services.AddSingleton<ITokenRevocationRepository, NoOpTokenRevocationRepository>();
```

- [ ] **Step 9: Run the tests**

```bash
cd Iverson.Server && dotnet test Iverson.Sql.Tests/Iverson.Sql.Tests.csproj --filter "FullyQualifiedName~TokenRevocationRepository"
cd Iverson.Server && dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~TokenRevocationCacheTests|FullyQualifiedName~ActingUserInterceptorSuspensionTests|FullyQualifiedName~TracesRelayEndpointTests"
```

Expected:
- `TokenRevocationRepository`: 4 passed, 0 failed (Testcontainers, `postgres:16-alpine`).
- The second command: 19 passed, 0 failed. That is the 9 cache tests, plus 10 existing pipeline tests that show the host still boots with the new startup call.

- [ ] **Step 10: Commit**

```bash
git add Iverson.Server/Iverson.Sql/TokenRevocationRepository.cs Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs Iverson.Server/Iverson.Api/Tenancy/ITokenRevocationCache.cs Iverson.Server/Iverson.Api/Tenancy/TokenRevocationCache.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api.Tests/Helpers/AuthTestWebApplicationFactory.cs Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs Iverson.Server/Iverson.Api.Tests/Tenancy/TokenRevocationCacheTests.cs Iverson.Server/Iverson.Sql.Tests/TokenRevocationRepositoryPostgresIntegrationTests.cs Iverson.Server/Iverson.Sql.Tests/ContainerCollection.cs
git commit -m "add a token revocation store and a 30-second revocation cache over it

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 7: Revocation check and writers

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Program.cs` (`OnTokenValidated` on both JwtBearer schemes; new `Program.RejectRevokedTokenAsync`)
- Modify: `Iverson.Server/Iverson.Api/Tenancy/IIdpAdminClient.cs` (`IdpUser` gains `Uid`)
- Modify: `Iverson.Server/Iverson.Api/Tenancy/IdpAdminClient.cs` (`ListUsersByTenantAsync` reads `uid`)
- Modify: `Iverson.Server/Iverson.Api/Grpc/TenantAdminGrpcService.cs` (new `ITokenRevocationRepository` dependency; `RemoveUser` and `SetTenantAdmin(false)` revoke first)
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/TokenRevocationPipelineTests.cs` (new)
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/TenantAdminGrpcServiceTests.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikAdminClientTests.cs`

**Interfaces:**
- Consumes (Task 6):
  - `ITokenRevocationRepository.RevokeAsync`;
  - `ITokenRevocationCache.IsRevokedAsync`;
  - the `NoOpTokenRevocationRepository` registered by `AuthTestWebApplicationFactory`.
- Produces:
  - `IdpUser(string Id, string Username, string Email, string Uid)`;
  - `TenantAdminGrpcService(IIdpAdminClient, ITenantStatusCache, ITokenRevocationRepository, AuditLog)`.

Notes:
- **Handler location.** The shared handler is a static member of the existing `public partial class Program` at the bottom of `Program.cs`, beside `ListenerPortGateAsync`, so both schemes' `Events` can name it.
- **`iat` source.** It reads `iat` from `context.Principal`. `MapInboundClaims = false` leaves the claim as `"iat"`, a Unix-seconds string. An unparseable or missing `iat` becomes `null`, which the cache treats as revoked when the `sub` matches.
- **`IdpUser` construction sites** (`command grep -rn "IdpUser(" --include=*.cs` over the repo):
  - `IdpAdminClient.cs:163`;
  - `AuthentikAdminClientTests.cs:233`;
  - `TenantAdminGrpcServiceTests.cs:29-30`.

  None exist outside `Iverson.Server`.
- **`TenantAdminGrpcService` construction sites.** The only direct one is `TenantAdminGrpcServiceTests.cs:35`. `TenantAdminGrpcServiceAuthorizationPipelineTests` resolves it from DI, where Task 6's NoOp satisfies the new dependency.

- [ ] **Step 1: Write the pipeline tests**

Create `Iverson.Server/Iverson.Api.Tests/Grpc/TokenRevocationPipelineTests.cs`. The existing pipeline-test tokens carry no `iat`, so these mint one through `TestJwtFactory.CreateToken`'s `extraClaims` (a `ClaimValueTypes.Integer64` claim serializes as a JSON number).

```csharp
using System.Security.Claims;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Iverson.Api.Grpc;
using Iverson.Api.Tenancy;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

// CSR round-10 #11: both JwtBearer schemes refuse a token whose sub was revoked at or after the
// token's iat. Boots the real Program.cs pipeline (same base fixture and probe RPC as
// ActingUserInterceptorSuspensionTests) with the revocation repository substituted, so the real
// OnTokenValidated handler and the real TokenRevocationCache both run.
public class TokenRevocationPipelineTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private const string RevokedSub = "revoked-user-uid";
    private static readonly DateTimeOffset RevokedAt = DateTimeOffset.UtcNow.AddMinutes(-1);

    private readonly AuthTestWebApplicationFactory _baseFactory;

    public TokenRevocationPipelineTests(AuthTestWebApplicationFactory factory) =>
        _baseFactory = factory;

    private ObjectSearchService.ObjectSearchServiceClient CreateClient()
    {
        var revocations = Substitute.For<ITokenRevocationRepository>();
        revocations.ListAsync().Returns([(RevokedSub, RevokedAt)]);
        var tenantStatusCache = Substitute.For<ITenantStatusCache>();
        tenantStatusCache.GetStatusAsync("active-tenant").Returns("active");

        var factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITokenRevocationRepository>();
                services.AddSingleton(revocations);
                services.RemoveAll<ITenantStatusCache>();
                services.AddSingleton(tenantStatusCache);
            }));
        var channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = factory.Server.CreateHandler()
        });
        return new ObjectSearchService.ObjectSearchServiceClient(channel);
    }

    // The existing pipeline-test tokens carry no iat; these carry one from before the revocation,
    // the shape every token a user held when they were removed or demoted has.
    private static Claim IssuedBeforeRevocation() =>
        new("iat", RevokedAt.AddMinutes(-5).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64);

    private static string ServiceToken(string subject) =>
        TestJwtFactory.CreateToken("test-service-audience", subject, extraClaims: [IssuedBeforeRevocation()]);

    private static string ServiceTokenIssuedAfterRevocation(string subject) =>
        TestJwtFactory.CreateToken(
            "test-service-audience",
            subject,
            extraClaims: [new Claim("iat", RevokedAt.AddSeconds(30).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)]);

    private static string ActingUserToken(string subject) =>
        TestJwtFactory.CreateToken(
            "test-actinguser-audience",
            subject,
            extraClaims: [IssuedBeforeRevocation(), new Claim("tenant_id", "active-tenant")]);

    private static async Task<RpcException?> TryAggregateAsync(
        ObjectSearchService.ObjectSearchServiceClient client, Metadata headers)
    {
        try
        {
            await client.AggregateAsync(new AggregateRequest(), headers);
            return null;
        }
        catch (RpcException ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task PrimaryScheme_RevokedSub_ThrowsUnauthenticated()
    {
        var headers = new Metadata { { "authorization", $"Bearer {ServiceToken(RevokedSub)}" } };

        var ex = await TryAggregateAsync(CreateClient(), headers);

        ex.Should().NotBeNull();
        ex!.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task PrimaryScheme_SubNotRevoked_IsAuthenticated()
    {
        var headers = new Metadata { { "authorization", $"Bearer {ServiceToken("still-active-uid")}" } };

        var ex = await TryAggregateAsync(CreateClient(), headers);

        ex.Should().NotBeNull(); // FailedPrecondition from RequireSchema — business logic, not auth
        ex!.StatusCode.Should().NotBe(StatusCode.Unauthenticated);
    }

    // A login after the revocation (a re-activated user) is accepted: the handler passes the
    // token's iat through rather than refusing the sub outright.
    [Fact]
    public async Task PrimaryScheme_RevokedSub_TokenIssuedAfterRevocation_IsAuthenticated()
    {
        var headers = new Metadata { { "authorization", $"Bearer {ServiceTokenIssuedAfterRevocation(RevokedSub)}" } };

        var ex = await TryAggregateAsync(CreateClient(), headers);

        ex.Should().NotBeNull(); // FailedPrecondition from RequireSchema — business logic, not auth
        ex!.StatusCode.Should().NotBe(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task ActingUser_RevokedSub_ThrowsUnauthenticated()
    {
        var headers = new Metadata
        {
            { "authorization", $"Bearer {ServiceToken("ak-test-service")}" },
            { ActingUserInterceptor.MetadataKey, $"Bearer {ActingUserToken(RevokedSub)}" }
        };

        var ex = await TryAggregateAsync(CreateClient(), headers);

        ex.Should().NotBeNull();
        ex!.StatusCode.Should().Be(StatusCode.Unauthenticated);
        ex.Status.Detail.Should().Be("Acting-user token is invalid.");
    }

    [Fact]
    public async Task ActingUser_SubNotRevoked_IsAuthenticated()
    {
        var headers = new Metadata
        {
            { "authorization", $"Bearer {ServiceToken("ak-test-service")}" },
            { ActingUserInterceptor.MetadataKey, $"Bearer {ActingUserToken("still-active-uid")}" }
        };

        var ex = await TryAggregateAsync(CreateClient(), headers);

        ex.Should().NotBeNull(); // FailedPrecondition from RequireSchema — business logic, not auth
        ex!.StatusCode.Should().NotBe(StatusCode.Unauthenticated);
        ex.StatusCode.Should().NotBe(StatusCode.PermissionDenied);
    }
}
```

- [ ] **Step 2: Write the `TenantAdminGrpcService` ordering tests**

In `Iverson.Server/Iverson.Api.Tests/Grpc/TenantAdminGrpcServiceTests.cs`, replace:

```csharp
using Iverson.Client.Contracts;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
```

with:

```csharp
using Iverson.Client.Contracts;
using Iverson.Sql;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
```

Replace:

```csharp
    private readonly ITenantStatusCache _tenantStatusCache = Substitute.For<ITenantStatusCache>();
    private readonly ILogger<AuditLog> _auditLogger = Substitute.For<ILogger<AuditLog>>();
    private readonly AuditLog _auditLog;
    private readonly Iverson.Api.Grpc.TenantAdminGrpcService _sut;

    private static readonly IdpUser CallerTenantUser = new("user-1", "alice", "alice@acme.example");
    private static readonly IdpUser OtherTenantUser = new("user-99", "mallory", "mallory@globex.example");
```

with:

```csharp
    private readonly ITenantStatusCache _tenantStatusCache = Substitute.For<ITenantStatusCache>();
    private readonly ITokenRevocationRepository _tokenRevocations = Substitute.For<ITokenRevocationRepository>();
    private readonly ILogger<AuditLog> _auditLogger = Substitute.For<ILogger<AuditLog>>();
    private readonly AuditLog _auditLog;
    private readonly Iverson.Api.Grpc.TenantAdminGrpcService _sut;

    // Uid deliberately differs from Id: tokens carry the uid as their sub, so revocation must be
    // keyed by it, not by the Authentik pk the RPCs take as user_id.
    private static readonly IdpUser CallerTenantUser = new("user-1", "alice", "alice@acme.example", "uid-alice");
    private static readonly IdpUser OtherTenantUser = new("user-99", "mallory", "mallory@globex.example", "uid-mallory");
```

Replace:

```csharp
            _authentikAdminClient,
            _tenantStatusCache,
            _auditLog);
```

with:

```csharp
            _authentikAdminClient,
            _tenantStatusCache,
            _tokenRevocations,
            _auditLog);
```

Directly after `RemoveUser_UserBelongsToCallerTenant_Deactivates` (whose last line is `await _authentikAdminClient.Received(1).DeactivateUserAsync("user-1");`), insert:

```csharp
    // CSR round-10 #11: revoke first. If Authentik then fails, the user only has to log in again;
    // the reverse order could leave a deactivated user's tokens valid.
    [Fact]
    public async Task RemoveUser_RevokesTheUsersTokensBeforeDeactivatingInAuthentik()
    {
        _authentikAdminClient.ListUsersByTenantAsync("acme")
            .Returns(Task.FromResult<IEnumerable<IdpUser>>([CallerTenantUser]));

        await _sut.RemoveUser(new RemoveUserRequest { UserId = "user-1" }, ContextForCallerTenant());

        Received.InOrder(() =>
        {
            _tokenRevocations.RevokeAsync("uid-alice");
            _authentikAdminClient.DeactivateUserAsync("user-1");
        });
    }

    [Fact]
    public async Task RemoveUser_RevocationFails_ThrowsAndDoesNotDeactivate()
    {
        _authentikAdminClient.ListUsersByTenantAsync("acme")
            .Returns(Task.FromResult<IEnumerable<IdpUser>>([CallerTenantUser]));
        _tokenRevocations.RevokeAsync(Arg.Any<string>()).ThrowsAsync(new InvalidOperationException("postgres is down"));

        var act = () => _sut.RemoveUser(new RemoveUserRequest { UserId = "user-1" }, ContextForCallerTenant());

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _authentikAdminClient.DidNotReceive().DeactivateUserAsync(Arg.Any<string>());
    }
```

Directly after `SetTenantAdmin_RevokeForUserInCallerTenant_RemovesGroup`, insert:

```csharp
    // A demoted admin's existing tokens still carry the tenant-admins group, and the TenantAdmin
    // policy reads groups from the caller's own token — so demotion revokes them, first.
    [Fact]
    public async Task SetTenantAdmin_Revoke_RevokesTheUsersTokensBeforeRemovingTheGroup()
    {
        _authentikAdminClient.ListUsersByTenantAsync("acme")
            .Returns(Task.FromResult<IEnumerable<IdpUser>>([CallerTenantUser]));

        await _sut.SetTenantAdmin(new SetTenantAdminRequest { UserId = "user-1", Grant = false }, ContextForCallerTenant());

        Received.InOrder(() =>
        {
            _tokenRevocations.RevokeAsync("uid-alice");
            _authentikAdminClient.RemoveGroupAsync("user-1", "tenant-admins");
        });
    }

    [Fact]
    public async Task SetTenantAdmin_Revoke_RevocationFails_ThrowsAndDoesNotRemoveTheGroup()
    {
        _authentikAdminClient.ListUsersByTenantAsync("acme")
            .Returns(Task.FromResult<IEnumerable<IdpUser>>([CallerTenantUser]));
        _tokenRevocations.RevokeAsync(Arg.Any<string>()).ThrowsAsync(new InvalidOperationException("postgres is down"));

        var act = () => _sut.SetTenantAdmin(
            new SetTenantAdminRequest { UserId = "user-1", Grant = false }, ContextForCallerTenant());

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _authentikAdminClient.DidNotReceive().RemoveGroupAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task SetTenantAdmin_Grant_DoesNotRevoke()
    {
        _authentikAdminClient.ListUsersByTenantAsync("acme")
            .Returns(Task.FromResult<IEnumerable<IdpUser>>([CallerTenantUser]));

        await _sut.SetTenantAdmin(new SetTenantAdminRequest { UserId = "user-1", Grant = true }, ContextForCallerTenant());

        await _tokenRevocations.DidNotReceive().RevokeAsync(Arg.Any<string>());
    }
```

- [ ] **Step 3: Give every Authentik list row a `uid`**

In `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikAdminClientTests.cs`, every user-list result row gains `"uid": "uid-<username>"` directly after its `"pk"`. Without it, the strict `GetProperty("uid")` throws `KeyNotFoundException` before the pagination checks the throw-tests assert on. There are 11 rows in 8 tests:
- `ListUsersByTenantAsync_FiltersByAttributesTenantId` (alice, bob);
- `ListUsersByTenantAsync_FollowsPagination_AcrossMultiplePages` (alice, carol);
- `ListUsersByTenantAsync_MissingPaginationEnvelope_Throws` (alice);
- `ListUsersByTenantAsync_PaginationMissingNext_Throws` (alice);
- `ListUsersByTenantAsync_PaginationNextIsNotANumber_Throws` (alice);
- `ListUsersByTenantAsync_PaginationNextIsNegative_Throws` (alice);
- `DeactivateAllUsersInTenantAsync_UnrecognisedPaginationEnvelope_ThrowsAndDeactivatesNoOne` (alice);
- `DeactivateAllUsersInTenantAsync_ListsThenDeactivatesEachMatchingUser` (alice, bob).

The edit is identical in shape everywhere. For example:

```text
                {"pk": 1, "username": "alice", "email": "alice@example.invalid", "attributes": {"tenant_id": "tenant-a"}},
```

becomes:

```text
                {"pk": 1, "uid": "uid-alice", "username": "alice", "email": "alice@example.invalid", "attributes": {"tenant_id": "tenant-a"}},
```

(`bob` gets `"uid": "uid-bob"`, and `carol` gets `"uid": "uid-carol"`.)

Then, in `ListUsersByTenantAsync_FiltersByAttributesTenantId`, the assertion that pins parsing:

```csharp
        users[0].Should().BeEquivalentTo(new IdpUser("1", "alice", "alice@example.invalid"));
```

becomes:

```csharp
        users[0].Should().BeEquivalentTo(new IdpUser("1", "alice", "alice@example.invalid", "uid-alice"));
```

- [ ] **Step 4: Run the tests to see them fail**

```bash
cd Iverson.Server && dotnet build Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

Expected (observed): the build fails with
- `AuthentikAdminClientTests.cs(233,46): error CS1729: 'IdpUser' does not contain a constructor that takes 4 arguments`;
- the same at `TenantAdminGrpcServiceTests.cs(34,56)` and `(35,55)`;
- `TenantAdminGrpcServiceTests.cs(40,20): error CS1729: 'TenantAdminGrpcService' does not contain a constructor that takes 4 arguments`.

- [ ] **Step 5: Add `Uid` to `IdpUser` and read it from the list**

In `Iverson.Server/Iverson.Api/Tenancy/IIdpAdminClient.cs`, replace:

```csharp
public sealed record IdpUser(string Id, string Username, string Email);
```

with:

```csharp
/// <param name="Uid">
/// Authentik's <c>uid</c>, which every token Authentik issues for this user carries as its
/// <c>sub</c> — the key token revocation is recorded under. <see cref="Id"/> is Authentik's pk,
/// the id its user API and this service's RPCs take.
/// </param>
public sealed record IdpUser(string Id, string Username, string Email, string Uid);
```

In `Iverson.Server/Iverson.Api/Tenancy/IdpAdminClient.cs` (`ListUsersByTenantAsync`), replace:

```csharp
                matches.Add(new IdpUser(
                    ReadPk(user),
                    user.GetProperty("username").GetString()!,
                    user.GetProperty("email").GetString()!));
```

with:

```csharp
                matches.Add(new IdpUser(
                    ReadPk(user),
                    user.GetProperty("username").GetString()!,
                    user.GetProperty("email").GetString()!,
                    user.GetProperty("uid").GetString()!));
```

- [ ] **Step 6: Revoke before Authentik in `RemoveUser` and `SetTenantAdmin(false)`**

In `Iverson.Server/Iverson.Api/Grpc/TenantAdminGrpcService.cs`, replace:

```csharp
using Iverson.Client.Contracts;
```

with:

```csharp
using Iverson.Client.Contracts;
using Iverson.Sql;
```

Replace:

```csharp
    ITenantStatusCache tenantStatusCache,
    AuditLog auditLog) : Iverson.Client.Contracts.TenantAdminGrpcService.TenantAdminGrpcServiceBase
```

with:

```csharp
    ITenantStatusCache tenantStatusCache,
    ITokenRevocationRepository tokenRevocations,
    AuditLog auditLog) : Iverson.Client.Contracts.TenantAdminGrpcService.TenantAdminGrpcServiceBase
```

Replace the two RPC bodies' Authentik calls. In `RemoveUser`, replace:

```csharp
        await RequireUserInTenantAsync(request.UserId, tenantId);
        await authentikAdminClient.DeactivateUserAsync(request.UserId);
```

with:

```csharp
        var user = await RequireUserInTenantAsync(request.UserId, tenantId);
        // CSR round-10 #11: revoke the user's tokens BEFORE touching Authentik. If Authentik then
        // fails, the user only has to log in again; the reverse order could leave a deactivated
        // user's tokens valid until they expire.
        await tokenRevocations.RevokeAsync(user.Uid);
        await authentikAdminClient.DeactivateUserAsync(request.UserId);
```

In `SetTenantAdmin`, replace:

```csharp
        else
            await authentikAdminClient.RemoveGroupAsync(request.UserId, "tenant-admins");
```

with:

```csharp
        else
        {
            // A demoted admin's existing tokens still carry the tenant-admins group, which the
            // TenantAdmin policy reads from the token itself — so revoke them first, as RemoveUser
            // does. Granting needs no revocation.
            await tokenRevocations.RevokeAsync(user.Uid);
            await authentikAdminClient.RemoveGroupAsync(request.UserId, "tenant-admins");
        }
```

- [ ] **Step 7: Check every token against the revocation cache, on both schemes**

In `Iverson.Server/Iverson.Api/Program.cs`, in the default scheme's `AddJwtBearer`, replace:

```csharp
        // of which is in that remapping table, which is why this was never hit before.
        options.MapInboundClaims = false;
    })
```

with:

```csharp
        // of which is in that remapping table, which is why this was never hit before.
        options.MapInboundClaims = false;
        options.Events = new JwtBearerEvents { OnTokenValidated = RejectRevokedTokenAsync };
    })
```

In the `"ActingUser"` scheme's existing `JwtBearerEvents`, replace:

```csharp
                    context.NoResult();
                return Task.CompletedTask;
            }
        };
```

with:

```csharp
                    context.NoResult();
                return Task.CompletedTask;
            },
            OnTokenValidated = RejectRevokedTokenAsync
        };
```

At the bottom of the file, replace:

```csharp
public partial class Program
{
    internal sealed class RequireListenerPort(int port) { public int Port => port; }
```

with:

```csharp
public partial class Program
{
    /// <summary>
    /// CSR round-10 #11: both JwtBearer schemes' <c>OnTokenValidated</c>. Refuses a token whose
    /// <c>sub</c> was revoked (a removed user, or a demoted tenant admin) at or after its
    /// <c>iat</c>; a token with no <c>iat</c> cannot show it came later, so it is refused too.
    /// The default scheme then answers 401 / Unauthenticated, and the ActingUser scheme's callers
    /// treat the failed result as an invalid acting-user token. Service clients' subs are never
    /// revoked, so they always pass.
    /// </summary>
    internal static async Task RejectRevokedTokenAsync(TokenValidatedContext context)
    {
        var sub = context.Principal?.FindFirst("sub")?.Value;
        if (sub is null)
            return;

        DateTimeOffset? issuedAt = long.TryParse(context.Principal!.FindFirst("iat")?.Value, out var iat)
            ? DateTimeOffset.FromUnixTimeSeconds(iat)
            : null;

        var revocations = context.HttpContext.RequestServices.GetRequiredService<Iverson.Api.Tenancy.ITokenRevocationCache>();
        if (await revocations.IsRevokedAsync(sub, issuedAt))
            context.Fail("Token has been revoked.");
    }

    internal sealed class RequireListenerPort(int port) { public int Port => port; }
```

`JwtBearerEvents` and `TokenValidatedContext` come from `Microsoft.AspNetCore.Authentication.JwtBearer`, which `Program.cs` already imports.

- [ ] **Step 8: Run the tests**

```bash
cd Iverson.Server && dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~TokenRevocation|FullyQualifiedName~TenantAdminGrpcService|FullyQualifiedName~AuthentikAdminClientTests|FullyQualifiedName~ActingUserInterceptor"
```

Expected (observed): 69 passed, 0 failed. That is:
- `TokenRevocationPipelineTests` 5;
- `TokenRevocationCacheTests` 9;
- `TenantAdminGrpcServiceTests` 18 (13 existing + 5 new);
- `TenantAdminGrpcServiceAuthorizationPipelineTests` 3;
- `AuthentikAdminClientTests` 27;
- `ActingUserInterceptor*` 7.

- [ ] **Step 9: Commit**

```bash
git add Iverson.Server/Iverson.Api.Tests/Grpc/TenantAdminGrpcServiceTests.cs Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikAdminClientTests.cs Iverson.Server/Iverson.Api/Grpc/TenantAdminGrpcService.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api/Tenancy/IIdpAdminClient.cs Iverson.Server/Iverson.Api/Tenancy/IdpAdminClient.cs Iverson.Server/Iverson.Api.Tests/Grpc/TokenRevocationPipelineTests.cs
git commit -m "refuse revoked tokens on both jwt schemes and revoke a user's tokens before removing or demoting them

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 8: `/v1/traces` accepts console-issued tokens only

**Files:**
- Create: `Iverson.Server/Iverson.Api/ConsoleClientAuthorizationPolicy.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs` (`ConsoleClient` policy in `AddAuthorization`; `/v1/traces` uses it; relay comment)
- Modify: `Iverson.Server/docker-compose.yml` (`Authentication__ConsoleAudience` beside `Authentication__ValidAudiences__0` on `iverson-api` only)
- Modify: `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml` (`Authentication__ConsoleAudience` from the human-oidc-client secret)
- Modify: `Iverson.Server/Iverson.Api.Tests/Helpers/AuthTestWebApplicationFactory.cs` (`ConsoleAudience` constant, `UseSetting`, and the default scheme also accepts that audience)
- Test: `Iverson.Server/Iverson.Api.Tests/TracesRelayEndpointTests.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/ConsoleClientAuthorizationPolicyTests.cs` (new)

**Interfaces:** Produces `AuthTestWebApplicationFactory.ConsoleAudience` (`"test-console-audience"`). It is a valid default-scheme audience in every pipeline-test host, and `Authentication:ConsoleAudience` there.

Notes:
- The policy follows its three siblings (`OperatorAuthorizationPolicy`, `SchemaAdminAuthorizationPolicy`, `TenantAdminAuthorizationPolicy`): a public static `IsSatisfiedBy` in `Iverson.Api`, which `Program.cs` calls from `RequireAssertion`.
- **Why a unit test as well as the pipeline tests.** The fail-closed guard (`!string.IsNullOrEmpty(consoleAudience)`) cannot be falsified through the pipeline. With `""` configured, no validated token carries an empty `aud`, so the pipeline answers 403 with or without the guard. `IsSatisfiedBy_ConsoleAudienceUnset_ReturnsFalse("")`, which feeds an empty `aud`, is the test that goes red without the guard.
- **Test-host configuration.** `builder.UseSetting(...)` in `ConfigureWebHost` is visible to `Program.cs`, and so is a `WithWebHostBuilder` override. This was verified, including with the value captured *before* `builder.Build()`, so the policy's per-evaluation read is not load-bearing. No process-global env var is needed, so the CORS factories' env-var race does not apply.
- **API service only.** The variable goes on `iverson-api` alone. `iverson-worker` (`WORKLOAD_ROLE=worker`) also sets `Authentication__ValidAudiences__0`, but never maps `/v1/traces` (that route is mapped only when `workloadRole == "api"`), so it gets no `ConsoleAudience`. Likewise the Helm worker chart, which carries no `Authentication__*` variables, gets nothing.

- [ ] **Step 1: Configure the test host's console audience**

In `Iverson.Server/Iverson.Api.Tests/Helpers/AuthTestWebApplicationFactory.cs`, replace:

```csharp
public class AuthTestWebApplicationFactory : WebApplicationFactory<Program>
{
    static AuthTestWebApplicationFactory()
```

with:

```csharp
public class AuthTestWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// The admin console's client id in this test host: a valid audience for the default scheme,
    /// and Authentication:ConsoleAudience, the only audience /v1/traces accepts.
    /// </summary>
    public const string ConsoleAudience = "test-console-audience";

    static AuthTestWebApplicationFactory()
```

Replace:

```csharp
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
```

with:

```csharp
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // The ConsoleClient policy's audience (/v1/traces). A test overrides it the same way, via
        // WithWebHostBuilder; TracesRelayEndpointTests' unset case does.
        builder.UseSetting("Authentication:ConsoleAudience", ConsoleAudience);

        builder.ConfigureServices(services =>
```

Replace (in the default scheme's `PostConfigure`):

```csharp
                options.TokenValidationParameters.ValidAudiences = ["test-service-audience"];
```

with:

```csharp
                options.TokenValidationParameters.ValidAudiences = ["test-service-audience", ConsoleAudience];
```

- [ ] **Step 2: Write the relay tests**

In `Iverson.Server/Iverson.Api.Tests/TracesRelayEndpointTests.cs`, the helper mints console tokens by default. Replace:

```csharp
    private (HttpClient Client, FakeJaegerHandler JaegerHandler) CreateAuthenticatedClient(string subject = "trace-relay-test-user")
    {
        var jaegerHandler = new FakeJaegerHandler();
        var factory = _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
```

with:

```csharp
    // The token's audience defaults to the console's, the only one /v1/traces accepts (CSR
    // round-10 #13). configuredConsoleAudience, when given, replaces the factory's
    // Authentication:ConsoleAudience.
    private (HttpClient Client, FakeJaegerHandler JaegerHandler) CreateAuthenticatedClient(
        string subject = "trace-relay-test-user",
        string audience = AuthTestWebApplicationFactory.ConsoleAudience,
        string? configuredConsoleAudience = null)
    {
        var jaegerHandler = new FakeJaegerHandler();
        var factory = _baseFactory.WithWebHostBuilder(builder =>
        {
            if (configuredConsoleAudience is not null)
                builder.UseSetting("Authentication:ConsoleAudience", configuredConsoleAudience);

            builder.ConfigureServices(services =>
```

Replace:

```csharp
            new AuthenticationHeaderValue("Bearer", TestJwtFactory.CreateToken("test-service-audience", subject));
```

with:

```csharp
            new AuthenticationHeaderValue("Bearer", TestJwtFactory.CreateToken(audience, subject));
```

Insert directly before `PostTraces_ProtobufBody_IsAccepted`:

```csharp
    // CSR round-10 #13: a service client's token (or an acting-user token) is authenticated but
    // was not issued to the console, so it may not write spans.
    [Fact]
    public async Task PostTraces_NonConsoleAudience_Returns403_AndIsNeverForwarded()
    {
        var (client, jaegerHandler) = CreateAuthenticatedClient(audience: "test-service-audience");

        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"resourceSpans":[]}"""));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var response = await client.PostAsync("/v1/traces", content);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        jaegerHandler.CallCount.Should().Be(0);
    }

    // Fail closed: with no console audience configured, not even a console token is accepted.
    [Fact]
    public async Task PostTraces_ConsoleAudienceUnset_Returns403_AndIsNeverForwarded()
    {
        var (client, jaegerHandler) = CreateAuthenticatedClient(configuredConsoleAudience: "");

        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"resourceSpans":[]}"""));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var response = await client.PostAsync("/v1/traces", content);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        jaegerHandler.CallCount.Should().Be(0);
    }
```

The "console `aud` passes" case is the six existing relay tests. They now send a console token and still expect their 202 / 413 / 415 / 429.

Create `Iverson.Server/Iverson.Api.Tests/ConsoleClientAuthorizationPolicyTests.cs`:

```csharp
using FluentAssertions;
using Xunit;

namespace Iverson.Api.Tests;

public class ConsoleClientAuthorizationPolicyTests
{
    [Fact]
    public void IsSatisfiedBy_TokenIssuedToTheConsole_ReturnsTrue()
    {
        ConsoleClientAuthorizationPolicy.IsSatisfiedBy(["console-client-id"], "console-client-id").Should().BeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_TokenIssuedToAnotherClient_ReturnsFalse()
    {
        ConsoleClientAuthorizationPolicy.IsSatisfiedBy(["service-client-id"], "console-client-id").Should().BeFalse();
    }

    [Fact]
    public void IsSatisfiedBy_AudienceComparedOrdinally_ReturnsFalseForACaseVariant()
    {
        ConsoleClientAuthorizationPolicy.IsSatisfiedBy(["Console-Client-Id"], "console-client-id").Should().BeFalse();
    }

    // Fail closed: an unset console audience matches nothing, not even an empty aud claim.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsSatisfiedBy_ConsoleAudienceUnset_ReturnsFalse(string? consoleAudience)
    {
        ConsoleClientAuthorizationPolicy.IsSatisfiedBy(["", "console-client-id"], consoleAudience).Should().BeFalse();
    }
}
```

- [ ] **Step 3: Run the relay tests to see them fail**

```bash
cd Iverson.Server && dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~TracesRelayEndpointTests"
```

Expected: the build fails, because `ConsoleClientAuthorizationPolicyTests.cs` names the not-yet-existing `ConsoleClientAuthorizationPolicy` (see the observed error in `task-8-assumptions.md`).

The proving run also ran the relay tests red before the unit-test file existed, and observed 2 failed, 6 passed. The failures were `PostTraces_NonConsoleAudience_Returns403_AndIsNeverForwarded` and `PostTraces_ConsoleAudienceUnset_Returns403_AndIsNeverForwarded`, both of which got 202.

- [ ] **Step 4: Add the policy and use it on the route**

Create `Iverson.Server/Iverson.Api/ConsoleClientAuthorizationPolicy.cs`:

```csharp
namespace Iverson.Api;

// CSR round-10 #13: /v1/traces accepts only tokens the admin console's own OIDC client issued,
// told apart by their aud (Authentication:ConsoleAudience). Fails closed: with no console
// audience configured, no caller satisfies it.
public static class ConsoleClientAuthorizationPolicy
{
    public static bool IsSatisfiedBy(IEnumerable<string> audienceClaims, string? consoleAudience) =>
        !string.IsNullOrEmpty(consoleAudience) &&
        audienceClaims.Any(audience => string.Equals(audience, consoleAudience, StringComparison.Ordinal));
}
```

In `Iverson.Server/Iverson.Api/Program.cs`, replace:

```csharp
    options.AddPolicy("TenantAdmin", policy => policy.RequireAssertion(context =>
        TenantAdminAuthorizationPolicy.IsSatisfiedBy(
            context.User.FindAll("groups").Select(c => c.Value))));
});
```

with:

```csharp
    options.AddPolicy("TenantAdmin", policy => policy.RequireAssertion(context =>
        TenantAdminAuthorizationPolicy.IsSatisfiedBy(
            context.User.FindAll("groups").Select(c => c.Value))));
    // CSR round-10 #13: /v1/traces accepts console-issued tokens only; see
    // ConsoleClientAuthorizationPolicy.
    options.AddPolicy("ConsoleClient", policy => policy.RequireAssertion(context =>
        ConsoleClientAuthorizationPolicy.IsSatisfiedBy(
            context.User.FindAll("aud").Select(c => c.Value),
            cfg["Authentication:ConsoleAudience"])));
});
```

Replace:

```csharp
    // Served by the API so the browser never needs Jaeger's own network address, and
    // authenticated so only signed-in admin-ui sessions can write traces through it.
```

with:

```csharp
    // Served by the API so the browser never needs Jaeger's own network address, and limited to
    // tokens the console's own OIDC client issued (the ConsoleClient policy, CSR round-10 #13),
    // so only signed-in admin-ui sessions can write traces through it — not service clients or
    // acting-user tokens.
```

Replace:

```csharp
    }).RequireAuthorization().RequireRateLimiting("traces")
```

with:

```csharp
    }).RequireAuthorization("ConsoleClient").RequireRateLimiting("traces")
```

The rate limiter, the body limit and the `HttpMethodMetadata` line after it are unchanged.

- [ ] **Step 5: Wire the console audience in compose and Helm**

In `Iverson.Server/docker-compose.yml`, in `iverson-api`, replace:

```yaml
      - Authentication__ValidAudiences__0=dev-iverson-human-oidc-client-id
      - Authentication__ValidAudiences__1=dev-iverson-loadtest-client-id
      - Authentication__ValidAudiences__2=dev-iverson-webtest-client-id
      - Authentication__ValidAudiences__3=dev-iverson-admin-automation-client-id
      # The LoadTest's data-plane calls (Post/Get/Search) carry the tenant-admin *user* token as
```

with:

```yaml
      - Authentication__ValidAudiences__0=dev-iverson-human-oidc-client-id
      # The admin console's client id (ValidAudiences__0): /v1/traces accepts only its tokens.
      - Authentication__ConsoleAudience=dev-iverson-human-oidc-client-id
      - Authentication__ValidAudiences__1=dev-iverson-loadtest-client-id
      - Authentication__ValidAudiences__2=dev-iverson-webtest-client-id
      - Authentication__ValidAudiences__3=dev-iverson-admin-automation-client-id
      # The LoadTest's data-plane calls (Post/Get/Search) carry the tenant-admin *user* token as
```

(The block is unique because it ends at the `# The LoadTest's data-plane calls …` comment, which only `iverson-api` has. `iverson-worker`'s identical `ValidAudiences` lines are left alone.)

In `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml`, replace:

```yaml
            - name: Authentication__ValidAudiences__0
              valueFrom:
                secretKeyRef: { name: {{ .Release.Name }}-authentik-human-oidc-client, key: client-id }
```

with:

```yaml
            - name: Authentication__ValidAudiences__0
              valueFrom:
                secretKeyRef: { name: {{ .Release.Name }}-authentik-human-oidc-client, key: client-id }
            # The admin console's client id, from the same secret: /v1/traces accepts only its tokens.
            - name: Authentication__ConsoleAudience
              valueFrom:
                secretKeyRef: { name: {{ .Release.Name }}-authentik-human-oidc-client, key: client-id }
```

- [ ] **Step 6: Verify the compose file parses and the chart renders**

```bash
cd Iverson.Server && python3 -c "
import yaml
d=yaml.safe_load(open('docker-compose.yml'))
for n,svc in d['services'].items():
    hits=[e for e in (svc.get('environment') or []) if isinstance(e,str) and e.startswith('Authentication__ConsoleAudience')]
    if hits: print(n,hits)
"
cd Iverson.Server/deploy/helm/iverson && helm template t . -f values-laptop.yaml | grep -n -B4 -A3 "Authentication__ConsoleAudience"
```

Expected (observed):

```text
iverson-api ['Authentication__ConsoleAudience=dev-iverson-human-oidc-client-id']
```

and:

```text
1768-            - name: Authentication__ValidAudiences__0
1769-              valueFrom:
1770-                secretKeyRef: { name: t-authentik-human-oidc-client, key: client-id }
1771-            # The admin console's client id, from the same secret: /v1/traces accepts only its tokens.
1772:            - name: Authentication__ConsoleAudience
1773-              valueFrom:
1774-                secretKeyRef: { name: t-authentik-human-oidc-client, key: client-id }
1775-            - name: Authentication__ValidAudiences__1
```

Notes on the render:
- **No `helm dependency build`.** The worktree has no `charts/*.tgz` (`find deploy/helm -name '*.tgz'` is empty), and every dependency is a `file://charts/<name>` directory, so Helm reads the live subchart. If a checkout does hold stale tgz files, run `helm dependency build` first.
- **Why `values-laptop.yaml`.** A bare `helm template t .` fails on `networkpolicies.yaml:4` ("networkPolicy.clusterCidrs must list at least one CIDR"), whatever this change does: `values.yaml` deliberately ships no CIDR.

- [ ] **Step 7: Run the tests**

```bash
cd Iverson.Server && dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~TracesRelayEndpointTests|FullyQualifiedName~ConsoleClientAuthorizationPolicyTests|FullyQualifiedName~AdminConsoleCorsPipelineTests|FullyQualifiedName~AuthenticationPipelineTests"
```

Expected (observed): 41 passed, 0 failed. That breaks down as:
- `TracesRelayEndpointTests` 8;
- `ConsoleClientAuthorizationPolicyTests` 5;
- `AdminConsoleCorsPipelineTests` and `AuthenticationPipelineTests`, which still pass, including the `/v1/traces` preflight and listener-gate tests.

- [ ] **Step 8: Commit**

```bash
git add Iverson.Server/Iverson.Api.Tests/Helpers/AuthTestWebApplicationFactory.cs Iverson.Server/Iverson.Api.Tests/TracesRelayEndpointTests.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml Iverson.Server/docker-compose.yml Iverson.Server/Iverson.Api.Tests/ConsoleClientAuthorizationPolicyTests.cs Iverson.Server/Iverson.Api/ConsoleClientAuthorizationPolicy.cs
git commit -m "accept only console-issued tokens on /v1/traces

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 9: Console endpoints check tenant status

**Files:**
- Create: `Iverson.Server/Iverson.Api/Console/ActiveTenantEndpointFilter.cs`
- Modify: `Iverson.Server/Iverson.Api/Console/AdminConsoleEndpoints.cs` (filter on all four endpoints; `MapAdminConsoleEndpoints` doc)
- Modify: `Iverson.Server/Iverson.Api.Tests/Helpers/AdminConsoleTestWebApplicationFactory.cs` (`AdminConsoleTenantRepository.GetAsync` answers from its two rows; class doc)
- Test: `Iverson.Server/Iverson.Api.Tests/AdminConsoleActiveTenantPipelineTests.cs` (new)

**Interfaces:** none across tasks.

Notes:
- **The filter uses the real `ITenantStatusCache`.** In `AdminConsoleTestWebApplicationFactory`, that cache reads `AdminConsoleTenantRepository.GetAsync`, which returns `null` today. With the filter in place, `AdminConsoleEndpointsPipelineTests`' reader token (`tenant_id` = `tenant_alpha`) would then get 403.
  - Verified: 6 of its tests fail with the old `GetAsync`, namely both `AuthenticatedNonOperator_AuthenticatedOnlyEndpoint_Returns200` cases and the four `Reader_*` tests.
  - So `GetAsync` answers from the fixture's own `Tenants` rows, in which `tenant_alpha` is `active`.
- **The new tests substitute the cache.** They swap it for a fixed hand-rolled fake through `WithWebHostBuilder` on the existing factory, as `ActingUserInterceptorSuspensionTests` does. A hand-rolled class, not NSubstitute, because an unconfigured `Task<string?>` member auto-values to `""`, not `null`.
- **The tokens carry both `operators` and the reader group,** so every endpoint's own authorization passes and the filter is the only possible source of a 403.
- **The 403 is `Results.StatusCode(403)`, not `Results.Forbid()`.** It needs no authentication-handler round trip, and gives the same status the spec names.

- [ ] **Step 1: Make the fixture's tenant repository answer lookups**

In `Iverson.Server/Iverson.Api.Tests/Helpers/AdminConsoleTestWebApplicationFactory.cs`, replace:

```csharp
    public Task<TenantRow?> GetAsync(string id) => Task.FromResult<TenantRow?>(null);
```

with:

```csharp
    // Answers from the same two rows, so the real TenantStatusCache that the console endpoints'
    // ActiveTenantEndpointFilter consults finds the reader token's tenant_alpha active.
    public Task<TenantRow?> GetAsync(string id) =>
        Task.FromResult(AdminConsoleTestWebApplicationFactory.Tenants.FirstOrDefault(t => t.Id == id));
```

And, in the class's doc list, replace:

```csharp
/// <item><see cref="ITenantRepository"/> — two rows, replacing the base factory's empty NoOp.</item>
```

with:

```csharp
/// <item><see cref="ITenantRepository"/> — two rows, listed and looked up by id, replacing the base
/// factory's empty NoOp.</item>
```

- [ ] **Step 2: Write the filter tests**

Create `Iverson.Server/Iverson.Api.Tests/AdminConsoleActiveTenantPipelineTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using FluentAssertions;
using Iverson.Api.Tenancy;
using Iverson.Api.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Iverson.Api.Tests;

/// <summary>
/// CSR round-10 #14: <c>ActiveTenantEndpointFilter</c> on all four <c>/admin/console/*</c>
/// endpoints, through the real <c>Program.cs</c> pipeline. Every token here carries both the
/// <c>operators</c> group and the reader group, so it clears every endpoint's authorization
/// policy and the filter is the only thing that can answer 403.
/// </summary>
public class AdminConsoleActiveTenantPipelineTests : IClassFixture<AdminConsoleTestWebApplicationFactory>
{
    private const string Tenants    = "/admin/console/tenants";
    private const string Schema     = "/admin/console/schema";
    private const string DataVolume = "/admin/console/data-volume";
    private const string Qdrant     = "/admin/console/qdrant";

    private readonly HttpClient _client;

    public AdminConsoleActiveTenantPipelineTests(AdminConsoleTestWebApplicationFactory factory)
    {
        _client = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITenantStatusCache>();
                services.AddSingleton<ITenantStatusCache, FixedTenantStatusCache>();
            })).CreateClient();
    }

    /// <summary>One tenant per status the filter distinguishes; any other id is unknown (null).</summary>
    private sealed class FixedTenantStatusCache : ITenantStatusCache
    {
        public Task<string?> GetStatusAsync(string tenantId) => Task.FromResult(tenantId switch
        {
            "tenant_active"    => "active",
            "tenant_suspended" => "suspended",
            "tenant_deleted"   => "deleted",
            _                  => (string?)null
        });
    }

    private static string Token(string? tenantId)
    {
        var claims = new List<Claim>
        {
            new("groups", "operators"),
            new("groups", AdminConsoleTestWebApplicationFactory.ReaderGroup)
        };
        if (tenantId is not null)
            claims.Add(new Claim("tenant_id", tenantId));
        return TestJwtFactory.CreateToken("test-service-audience", "console-user", extraClaims: claims);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    public static TheoryData<string, string> InactiveTenantOnEveryEndpoint()
    {
        var data = new TheoryData<string, string>();
        foreach (var path in new[] { Tenants, Schema, DataVolume, Qdrant })
            foreach (var tenantId in new[] { "tenant_unknown", "tenant_suspended", "tenant_deleted" })
                data.Add(path, tenantId);
        return data;
    }

    [Theory]
    [MemberData(nameof(InactiveTenantOnEveryEndpoint))]
    public async Task TenantNotActive_Returns403(string path, string tenantId)
    {
        var response = await GetAsync(path, Token(tenantId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(Tenants)]
    [InlineData(Schema)]
    [InlineData(DataVolume)]
    [InlineData(Qdrant)]
    public async Task ActiveTenant_Returns200(string path)
    {
        var response = await GetAsync(path, Token("tenant_active"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(Tenants)]
    [InlineData(Schema)]
    [InlineData(DataVolume)]
    [InlineData(Qdrant)]
    public async Task NoTenantIdClaim_Returns200(string path)
    {
        var response = await GetAsync(path, Token(tenantId: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(Tenants)]
    [InlineData(Schema)]
    [InlineData(DataVolume)]
    [InlineData(Qdrant)]
    public async Task EmptyTenantIdClaim_Returns200(string path)
    {
        var response = await GetAsync(path, Token(""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
```

- [ ] **Step 3: Run the tests to see them fail**

```bash
cd Iverson.Server && dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~AdminConsoleActiveTenantPipelineTests|FullyQualifiedName~AdminConsoleEndpointsPipelineTests"
```

Expected (observed): 12 failed, 30 passed.
- The 12 failures are every `TenantNotActive_Returns403(path, tenantId)` case: 4 endpoints × unknown, suspended and deleted, each of which got 200.
- The 12 active, no-`tenant_id` and empty-`tenant_id` cases pass, as do all 18 `AdminConsoleEndpointsPipelineTests`.

- [ ] **Step 4: Implement the filter**

Create `Iverson.Server/Iverson.Api/Console/ActiveTenantEndpointFilter.cs`:

```csharp
using Iverson.Api.Tenancy;

namespace Iverson.Api.Console;

/// <summary>
/// CSR round-10 #14: the admin console's endpoints refuse a caller whose tenant is not active,
/// as the gRPC surfaces already do. The rule is <c>ActingUserInterceptor</c>'s: a caller with a
/// <c>tenant_id</c> claim whose tenant is unknown, suspended or deleted gets 403. A caller with
/// no <c>tenant_id</c> claim, or an empty one (an operator's console token carries
/// <c>tenant_id: null</c>), passes.
/// </summary>
public sealed class ActiveTenantEndpointFilter(ITenantStatusCache tenantStatusCache) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var tenantId = context.HttpContext.User.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrEmpty(tenantId))
        {
            var status = await tenantStatusCache.GetStatusAsync(tenantId);
            if (status is null or "suspended" or "deleted")
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        return await next(context);
    }
}
```

- [ ] **Step 5: Put it on all four endpoints**

In `Iverson.Server/Iverson.Api/Console/AdminConsoleEndpoints.cs`, replace:

```csharp
    /// <summary>
    /// Maps the four endpoints. Registered from <c>Program.cs</c> alongside the other
    /// <c>/admin</c> routes, and — like them — after <c>UseAuthentication</c>/<c>UseAuthorization</c>.
    /// </summary>
```

with:

```csharp
    /// <summary>
    /// Maps the four endpoints. Registered from <c>Program.cs</c> alongside the other
    /// <c>/admin</c> routes, and — like them — after <c>UseAuthentication</c>/<c>UseAuthorization</c>.
    /// Every one carries <see cref="ActiveTenantEndpointFilter"/>: a caller whose tenant is not
    /// active is refused here as it is on the gRPC surfaces.
    /// </summary>
```

Then append `.AddEndpointFilter<ActiveTenantEndpointFilter>()` to each of the four registrations. Replace:

```csharp
            .WithName("AdminConsoleTenants")
            .RequireAuthorization("Operator");
```

with:

```csharp
            .WithName("AdminConsoleTenants")
            .RequireAuthorization("Operator")
            .AddEndpointFilter<ActiveTenantEndpointFilter>();
```

Replace:

```csharp
            .WithName("AdminConsoleSchema")
            .RequireAuthorization();
```

with:

```csharp
            .WithName("AdminConsoleSchema")
            .RequireAuthorization()
            .AddEndpointFilter<ActiveTenantEndpointFilter>();
```

Replace:

```csharp
            .WithName("AdminConsoleDataVolume")
            .RequireAuthorization();
```

with:

```csharp
            .WithName("AdminConsoleDataVolume")
            .RequireAuthorization()
            .AddEndpointFilter<ActiveTenantEndpointFilter>();
```

Replace:

```csharp
            .WithName("AdminConsoleQdrant")
            .RequireAuthorization("Operator");
```

with:

```csharp
            .WithName("AdminConsoleQdrant")
            .RequireAuthorization("Operator")
            .AddEndpointFilter<ActiveTenantEndpointFilter>();
```

`AddEndpointFilter<T>` builds the filter per request from the request's services, so the singleton `ITenantStatusCache` is injected without any registration.

- [ ] **Step 6: Run the tests**

```bash
cd Iverson.Server && dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~AdminConsole"
```

Expected (observed): 78 passed, 0 failed. That count includes:
- `AdminConsoleActiveTenantPipelineTests`, 24;
- `AdminConsoleEndpointsPipelineTests`, 18;
- the console CORS, metrics and endpoint-unit suites.

- [ ] **Step 7: Commit**

```bash
git add Iverson.Server/Iverson.Api.Tests/Helpers/AdminConsoleTestWebApplicationFactory.cs Iverson.Server/Iverson.Api/Console/AdminConsoleEndpoints.cs Iverson.Server/Iverson.Api.Tests/AdminConsoleActiveTenantPipelineTests.cs Iverson.Server/Iverson.Api/Console/ActiveTenantEndpointFilter.cs
git commit -m "refuse admin console requests from callers whose tenant is not active

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 10: Live verification (spec §10 "Live check")

This task makes no commits. Before the merge, it proves the three spec §10 live-check behaviours on the finished branch, using an isolated compose project. Any probe program it needs lives in the session scratchpad and is never committed.

**Names used below:**
- `SP` is the session scratchpad.
- `BR` is the branch checkout's root (the SDD worktree holding Tasks 1–9).
- `MAIN` is `/home/ben/repositories/Iverson`. `.env` is gitignored, so it exists only at `$MAIN/Iverson.Server/.env`.

**Standing constraints for this task:**
- Copy `$BR/Iverson.Server/docker-compose.yml` into `$SP/live` with every `container_name:` line stripped. In that copy, rewrite both `image: iverson-api` lines to `image: csr10live-api`, so the probe build never moves the user's `iverson-api` tag.
- Run it under its own project name with `--project-directory $BR/Iverson.Server --env-file $MAIN/Iverson.Server/.env`. The build context and every relative mount then resolve inside the branch checkout.
- Never start, stop, remove or modify the user's existing `iversonserver` containers, volumes, networks or images. Snapshot all four before starting, and diff them after teardown.
- Read `AUTHENTIK_BOOTSTRAP_TOKEN` and every password from `$MAIN/Iverson.Server/.env` into environment variables. Never print them.
- Tear down with `down -v`.
- Shell variables do not persist between tool calls. Start every command block with the same four assignments: `SP=…; BR=…; MAIN=/home/ben/repositories/Iverson; DC="docker compose -p csr10live -f $SP/live/compose.yml --project-directory $BR/Iverson.Server --env-file $MAIN/Iverson.Server/.env"`.

- [ ] **Step 1: Stand up the isolated stack from the branch**

```bash
mkdir -p $SP/live
grep -v container_name $BR/Iverson.Server/docker-compose.yml \
  | sed 's/^    image: iverson-api$/    image: csr10live-api/' > $SP/live/compose.yml
docker ps -a --format '{{.Names}}' | sort > $SP/live/before-containers.txt
docker volume ls -q | sort > $SP/live/before-volumes.txt
docker network ls --format '{{.Name}}' | sort > $SP/live/before-networks.txt
docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort > $SP/live/before-images.txt
DC="docker compose -p csr10live -f $SP/live/compose.yml --project-directory $BR/Iverson.Server --env-file $MAIN/Iverson.Server/.env"
$DC up -d --build
```

Expected:
- `iverson-api` reaches healthy. Its healthcheck (`docker-compose.yml:516-524`) only succeeds once Kestrel listens, and that happens after the startup DDL, `EnsureTableAsync` included (`:510-513`).
- `$DC exec postgres psql -U iverson -d iverson -c '\d iverson_token_revocations'` shows the table with columns `sub` and `revoked_at`.
- Authentik's blueprints have applied: the `dev-iverson-loadtest-human-client-id` provider exists. Poll `/api/v3/providers/oauth2/?client_id=…` as the spec's VA-1 probe did.

- [ ] **Step 2: Check whether the probe stack's Authentik rewrites cached TOTP secrets**

Build the probe program: a copy of the spec probe's `subprobe` project, with its `ProjectReference` repointed at `$BR/Iverson.Server/Iverson.LoadTest/Iverson.LoadTest.csproj`, and the gRPC calls of Steps 3–5 added. Then mint one token for `iverson-loadtest-bypass-user` against the probe stack with `HOME` pointed at a scratch directory. Run the built dll directly, so no restore runs under the scratch `HOME`. `mint-bypass` is a probe mode that makes one `AuthentikFlowExecutorClient.MintAsync` call for `iverson-loadtest-bypass-user`.

```bash
mkdir -p "$SP/live/home" && HOME="$SP/live/home" dotnet <probe>/bin/Debug/net10.0/<probe>.dll mint-bypass && { if test -d "$SP/live/home/.cache/iverson"; then ls -la "$SP/live/home/.cache/iverson/"; echo CACHE_FILE_APPEARED; else echo NO_CACHE_FILE; fi; }
```

The probe reads `IVERSON_BYPASS_PASSWORD` from the environment. `mint-bypass` lets `MintAsync`'s exception propagate, so a failed mint exits non-zero. Take the "A file appeared" branch only on `CACHE_FILE_APPEARED`, and the "No cache file appeared" branch only on `NO_CACHE_FILE`. If neither marker prints, stop.

- **No cache file appeared:** Steps 3–4 do not touch `~/.cache/iverson`. Continue.
- **A file appeared:** copy `~/.cache/iverson` to `$SP/live/totp-cache-backup` now. Then copy the scratch secret over the real one, so Step 4's harness uses the secret the probe stack enrolled: `cp "$SP/live/home/.cache/iverson/acting-user-totp-secret-compose-iverson-loadtest-bypass-user.txt" ~/.cache/iverson/`. In Step 6, restore the backup and `diff -r` it against the backup.

- [ ] **Step 3: Revocation is enforced within 30 s**

Use the probe program:
1. Make tenant `T` exist and be active. Either drive `TenantLifecycle.CreateTenant` as the operator, or run the LoadTest tenant provisioning (which creates the tenant, its admin and a recovery link). Set the tenant admin's password from the recovery link.
2. Create user `U` in tenant `T` (an Authentik user with `attributes.tenant_id = T`), set its password from a recovery link, and mint its acting-user token through `dev-iverson-loadtest-human-client-id`.
3. Call a data-plane RPC, e.g. `ObjectRetrieval.Get` on any registered type, with the loadtest service token plus `x-acting-user-authorization: Bearer <U token>`. Expected: not Unauthenticated.
4. As `T`'s tenant admin, call `TenantAdmin.ListUsers` to get `U`'s `UserId`, then `TenantAdmin.RemoveUser(U)`. Expected: success.
5. Poll the step-3 call once a second for up to 35 s. Expected: every poll that starts more than 30 s after step 4 began answers `Unauthenticated` ("Acting-user token is invalid."). Earlier polls may still succeed. Record the elapsed time of the first refused poll; up to 30 s plus one poll interval is the specified behaviour.
6. Run `$DC exec postgres psql -U iverson -d iverson -c 'SELECT sub FROM iverson_token_revocations'`. Expected: one row whose `sub` equals the `sub` claim decoded from `U`'s token.

- [ ] **Step 4: The identity conformance scenario passes IVC-IDN-008 for .NET**

Export the compose-stack variables from `docs/runbooks/client-conformance-matrix.md`: `IVERSON_CLIENT_ID`, `IVERSON_CLIENT_SECRET` (read from `.env` without printing it), `IVERSON_TOKEN_ENDPOINT` and `IVERSON_CLIENT_SCOPE`. Also export the two passwords `TokenBroker` requires, read from `$MAIN/Iverson.Server/.env` without printing them (`TokenBroker.cs:59, :80`; `Iverson.ClientConformance/Program.cs:327-334`):
- `IVERSON_ACTING_USER_BYPASS_PASSWORD` from `IVERSON_BYPASS_PASSWORD`;
- `IVERSON_OTHER_TENANT_PASSWORD` from `IVERSON_SMOKE_TEST_PASSWORD`.

Then:

```bash
cd $BR/Iverson.Server/Iverson.ClientConformance
dotnet run -- --languages dotnet --scenarios identity
```

Expected: the `dotnet` / `identity` cell is `ok`, and the IVC-IDN-008 assertion passes with reported status code 5 (`NotFound`). This is a partial run, so only that cell is evidence; its untouched-requirement gate is off by design.

- [ ] **Step 5: Carried-forward values survive in Postgres, and DATETIME survives in StarRocks and Qdrant**

Use the probe program and the loadtest service token (scope `schema_admin tenant_id_loadtest`):
1. Register a type `LiveDossier` with:
   - `Title` (STRING), plus a chunk/vector field so Qdrant projects it;
   - `SealedAt` (DATETIME) and `Seal` (BYTES), both with a FieldPermission whose `WritableRoles` contains only `dossier-sealers`;
   - row permissions letting `tenant-users`, or whatever group the acting user carries, write.
2. As an acting user **in** `dossier-sealers`, create a row with `Title`, `SealedAt = 2026-10-03T12:34:56Z` and some `Seal` bytes through the .NET SDK.
3. Read the row from Postgres: `$DC exec postgres psql -U iverson -d iverson -c "SELECT \"SealedAt\", encode(\"Seal\", 'hex') FROM <table> WHERE \"Id\" = '<key>'"`. Record both values.
4. As an acting user **not** in `dossier-sealers`, Update that row, changing `Title` and omitting `SealedAt` and `Seal`. Expected: success.
5. **Postgres:** read the row again as in sub-step 3. `SealedAt` and the hex of `Seal` equal the values recorded in sub-step 3 byte for byte.
6. **StarRocks:** after projection (poll for up to 60 s), `ObjectSearch.Search` for the row as the sealer user returns the sub-step 4 `Title`, and a `SealedAt` whose instant equals `2026-10-03T12:34:56Z`. Search renders DATETIME as `ToString("o")` with no offset (`ObjectSearchGrpcService.cs:1260-1272`), so compare instants, not strings. `Seal` is not checked here; see Known issues.
7. **Qdrant:** the object point's payload (read through the API's vector read path, or `$DC exec` against the probe stack's Qdrant) carries the sub-step 4 `Title` and the same `SealedAt` instant.
8. The api and worker logs show no projection error, and there is no DLQ entry for the row (`/admin/dlq` has none for it).

If the group and permission model above needs a different but equivalent shape on this stack, use it, and write down the shape actually used.

- [ ] **Step 6: Tear down and confirm nothing else changed**

```bash
$DC down -v
diff <(docker ps -a --format '{{.Names}}' | sort) $SP/live/before-containers.txt
diff <(docker volume ls -q | sort) $SP/live/before-volumes.txt
diff <(docker network ls --format '{{.Name}}' | sort) $SP/live/before-networks.txt
docker rmi csr10live-api 2>/dev/null
diff <(docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort) $SP/live/before-images.txt
```

If Step 2 made a backup:

```bash
test -d "$SP/live/totp-cache-backup" && rm -rf ~/.cache/iverson && cp -a "$SP/live/totp-cache-backup" ~/.cache/iverson
diff -r ~/.cache/iverson $SP/live/totp-cache-backup
```

Expected:
- The container, volume, network and TOTP-cache diffs are empty.
- The images diff changes no pre-existing line: `iverson-api:latest` keeps its ID. Any lines it adds are base or intermediate images the build pulled, and those may stay.

- [ ] **Step 7: Record the outcome**

Record each step's result in the SDD ledger:
- Step 2: whether a cache file appeared.
- Step 3: the elapsed time.
- Step 4: the cell status.
- Step 5: the values read back.

A failure in Step 3, 4 or 5 blocks the merge and goes back to the owning task: 7, 3 or 2 respectively.

## Known issues inherited from spec

- **Finding #7's primary and architectural remediations** (the expression parser, and splitting the StarRocks credential): deferred by scope choice; §4 is the interim patch only.
- **Update's re-insert race** (§1): a concurrent delete between the fetch and the upsert re-inserts the row in the same tenant.
- **Owner-mismatch PermissionDenied** (§1): it tells a caller a key exists in their own tenant. This predates the change; keys are UUIDv7.
- **No audit entry for cross-tenant Update attempts**: they are now indistinguishable from missing keys, by design.
- **Revocation clock skew** (§5): a few seconds between Authentik's `iat` and Postgres `now()`.
- **Console users can still write spans** (§6): any console user's browser can post spans. A collector that stamps the caller and overwrites `service.name` (CSR #13's architectural item) is deferred.
- **Other round-10 findings** (#3–#6, #9, #12, #15–#24): belong to the other sub-projects.
- **BYTES encoding split (CIR-1 §3.1, pre-existing; plan-level):** an SDK client sends `byte[]` as base64 text, which Postgres stores as that text's bytes. The text projected to StarRocks differs between a fast-path write (the client's text) and a carried update (`row_to_json`'s `\x…` hex). Search cannot display BYTES (`ObjectSearchGrpcService.cs:1260-1272`). Task 10 therefore checks `Seal` in Postgres only. Qdrant's handling is unverified.
