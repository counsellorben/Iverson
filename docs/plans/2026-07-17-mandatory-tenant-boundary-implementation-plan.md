# Mandatory Tenant Boundary Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-07-17-mandatory-tenant-boundary-design.md` (commit SHA: `f7af019`)

**Goal:** Give every registered schema a platform-enforced tenant partition so no row can be read or written by a caller whose token's `tenant_id` doesn't match — independent of whatever RBAC rules the schema configures.

**Architecture:** A mandatory `tenant_field` on `TypeDescriptor` names an existing scalar property holding the row's tenant. `RowFieldAuthorizationEvaluator` reads the caller's `tenant_id` claim and emits `TenantColumn`/`TenantValue` on its decision; each store's existing ownership-enforcement point gains a tenant predicate ANDed alongside the ownership one (StarRocks/Qdrant), or a post-fetch in-app comparison (Postgres). The write path stamps the tenant server-side on create and treats it as immutable on update/delete. Authentik issues `tenant_id` per human (user attribute) and per service client (constant scope-mapping).

**Tech stack:** .NET 10, gRPC (Grpc.Tools 2.81.1, shared `Iverson.Client.Contracts` proto), Dapper/Npgsql (Postgres), StarRocks (MySQL wire), Qdrant.Client 1.18.1, Authentik (blueprint YAML), xUnit + NSubstitute + TestContainers.

---

## Global Constraints

- **Tenant is strictly additive.** The existing role/ownership logic in `RowFieldAuthorizationEvaluator` is unchanged. A schema with no `AuthorizationRules` still denies everything (`RowFieldAuthorizationEvaluator.cs:11-12`). Tenant enforcement only narrows schemas that configure `RowPermissions` with `CanReadAll`/`CanWriteAll`/`CanDeleteAll`.
- **No operator bypass.** Do NOT add any `operators`-group exception to the tenant predicate anywhere. Operators carry a `tenant_id` like any caller and are tenant-scoped on the 4 data-plane services.
- **Hard cutover.** `tenant_field` is mandatory at registration (per registered type, root and dependents). A legacy schema with no tenant column (loaded from a pre-change `_iverson_schema` row) denies all data access until re-registered — this is the approved cutover behavior, implemented via the null-`TenantColumn` deny in Task 2.
- **Absent-tenant denial follows the per-path convention** (user-confirmed): the evaluator returns `Denied` when the schema has a tenant column but the caller has no `tenant_id`; reads then return empty/not-found, writes throw `PermissionDenied`. No new uniform-rejection path.
- **Commit convention:** Conventional Commits, matching the repo (`feat(api):`, `fix(starrocks):`, `test(api):`, etc.).

## File Structure

**Modify:**
- `Iverson.Clients/Common/Proto/object_mapping.proto` — add `tenant_field` (field 5) to `TypeDescriptor`.
- `Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs` — add nullable `TenantColumn`.
- `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs` — read `tenant_field` into `TenantColumn`.
- `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` — presence + property + string-valued + chunk-key validation for `tenant_field`.
- `Iverson.Server/Iverson.Api/Authorization/IRowFieldAuthorizationEvaluator.cs` — add `TenantColumn`/`TenantValue` to `AuthorizationDecision`.
- `Iverson.Server/Iverson.Api/Authorization/RowFieldAuthorizationEvaluator.cs` — read `tenant_id`; populate/deny.
- `Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs`, `ObjectMappingGrpcService.cs` — tenant match at the 3 read sites + Delete.
- `Iverson.Server/Iverson.Api/Grpc/EntityRelationResolver.cs` — tenant match in `TryAuthorizeAndMask`.
- `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs` — tenant stamp on create, tenant-match + immutability on update.
- `Iverson.Server/Iverson.StarRocks/AuthorizationConstraint.cs` — add `TenantColumn`/`TenantValue`.
- `Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs` — tenant predicate at the ownership sites.
- `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` — pass tenant fields into `AuthorizationConstraint`; `ApplyTenant` in SearchSimilar/SearchChunks.
- `Iverson.Server/Iverson.Vector/QdrantFilterBuilder.cs` — add `ApplyTenant`.
- `Iverson.Server/Iverson.Api.Tests/Helpers/SchemaFixtures.cs` + the ~13 test files and `Iverson.Server/Iverson.LoadTest/` entities that build `SchemaRequest`/`SchemaDescriptor` — add a tenant field/column.
- Authentik blueprints: `deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml` and `deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml`.

**Test:** new/extended tests colocated in the existing `*.Tests` projects (see each task).

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time; NOT re-verified here:

- `owner_field` is nested in the optional `AuthorizationRules` proto message; `tenant_field` must be its own top-level `TypeDescriptor` field (so it exists even when `AuthorizationRules` is absent). Evidence: `object_mapping.proto:82-93`.
- `SchemaRegistry.RegisterAsync` performs an unconditional blind replace — re-registration needs no extra mechanism. Evidence: `SchemaRegistry.cs:47-56`.
- Claims are read directly in `RowFieldAuthorizationEvaluator` (`FindFirst("sub")`, `FindAll("groups")`), not via a parsed accessor. Evidence: `RowFieldAuthorizationEvaluator.cs:17,35`.
- Postgres read enforcement is a post-fetch in-application comparison, not a WHERE predicate. Evidence: `ObjectRetrievalGrpcService.cs:41-42,97-98`, `ObjectMappingGrpcService.cs:82`.
- Write enforcement uses `EnforceWriteAuthorization` (owner force-set on create `:46-47`, owner-match/immutability on update `:52-66`); no tenant path exists today. Evidence: `AuthorizationFieldMasking.cs:28-68`.
- `ObjectMappingGrpcService.Delete` uses its own inline authorization check (`:191-202`), not `EnforceWriteAuthorization`. Evidence: `ObjectMappingGrpcService.cs:175-202`.
- StarRocks `AuthorizationConstraint` shape is `AllowedFields`/`OwnerColumn`/`OwnerValue`; the joined-type constraint dict is keyed `OrdinalIgnoreCase` (the 5c cross-tenant-bypass fix). Evidence: `AuthorizationConstraint.cs:3-6`, `ObjectSearchGrpcService.cs:443`.
- Qdrant ownership uses `QdrantFilterBuilder.ApplyOwnership`. Evidence: `QdrantFilterBuilder.cs:54-59`.
- Authentik `groups` claim comes from a scope-mapping expression over the `user` object (`return [group.name for group in user.ak_groups.all()]`), proving `user` is available in human-flow scope-mapping expressions. Evidence: `service-clients.yaml:37-43`.

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | Code-in-plan | Proto regenerates C# on build via Grpc.Tools wildcard include; `TypeDescriptor` uses field numbers 1–4, so `tenant_field = 5` is free | `Iverson.Client.Contracts.csproj:12,17` (`Grpc.Tools 2.81.1`, `Protobuf Include="../../Common/Proto/*.proto"`); `object_mapping.proto:88-93` |
| 2 | File path | Only one `object_mapping.proto` exists, consumed solely by `Iverson.Client.Contracts` (server + .NET LoadTest client share it); no per-language SDK copies to sync | `find object_mapping.proto` → 1 hit; `grep object_mapping.proto *.csproj` → only `Iverson.Client.Contracts.csproj` |
| 3 | Signature | `tenant_field` validation mirrors the full `owner_field` block: names a declared scalar property (`:46-51`), string-valued SqlType (`:64-70`), chunk-key non-collision (`:72-82`) — all apply to tenant (string keyword match, chunk payload) | `SchemaRegistrationOrchestrator.cs:45-83` |
| 4 | Task ordering | Per-type loop registers root + each dependent independently, so mandatory `tenant_field` applies per registered type | `SchemaRegistrationOrchestrator.cs:37-105` |
| 5 | Consumer impact | `AuthorizationDecision` is constructed at exactly 5 sites, all in `RowFieldAuthorizationEvaluator`; no test constructs it directly. Adding two positional fields touches only those 5 sites; the 5 consumer files read named properties (unaffected) | `grep "new AuthorizationDecision("` → 5 hits (`RowFieldAuthorizationEvaluator.cs:12,15,37,45,72`); consumers `ObjectRetrievalGrpcService`/`ObjectSearchGrpcService`/`EntityRelationResolver`/`ObjectMappingGrpcService`/`AuthorizationFieldMasking` |
| 6 | Consumer impact | **4th Postgres read site** (spec under-enumerated): `EntityRelationResolver.TryAuthorizeAndMask` filters related entities on ownership during `Get` depth-traversal — needs the tenant check too, or `depth>0` leaks cross-tenant related rows. One central helper covers all relation kinds | `EntityRelationResolver.cs:46-54`, called from `:71,102,132` |
| 7 | Consumer impact | `AuthorizationConstraint` is constructed at exactly 2 sites (`EvaluateAuthorization`), none in tests; adding two positional fields touches only those 2 | `grep "new AuthorizationConstraint("` → `ObjectSearchGrpcService.cs:448,458` only |
| 8 | Consumer impact | `ApplyOwnership` is called at 2 RPC sites (SearchSimilar `:154`, SearchChunks `:220`); `ApplyTenant` mirrors its signature and is added right after each | `ObjectSearchGrpcService.cs:154,220`; `QdrantFilterBuilder.cs:54-59` |
| 9 | Code-in-plan | Projection consumers re-derive **owner** from Postgres but copy other scalars from the event payload; tenant is server-stamped before the outbox publish, so it flows as a normal scalar with **zero consumer code change** (re-derivation would return the identical value) | `IntelligenceStoreConsumer.cs:94-131`, `EngagementStoreConsumer.cs:56-63` |
| 10 | Code-in-plan | `SchemaDescriptor.TenantColumn` must be **nullable**, not `required`: `SchemaRegistry.LoadAsync` deserializes pre-change `_iverson_schema` rows via `JsonSerializer.Deserialize<SchemaDescriptor>`; a `required` member missing from old JSON throws at startup. Nullable + evaluator-denies-on-null implements the hard cutover without a boot crash | `SchemaRegistry.cs:31,47-56`; `SchemaDescriptor.cs:3-20` (existing `required` members) |
| 11 | Consumer impact | ~13 test fixtures + LoadTest build `SchemaRequest`/`SchemaDescriptor` and will fail once `tenant_field` is mandatory / `TenantColumn` is read; `SchemaFixtures.cs` is the central domain-descriptor helper (8 fixtures) | `grep` for `SchemaRequest\|TypeDescriptor\|new SchemaDescriptor` across `Iverson.Api.Tests` + `Iverson.LoadTest`; `SchemaFixtures.cs:1-130` |
| 12 | Command | Tests run via `dotnet test`; hand-signed-JWT and schema fixtures already exist as precedents for minting a `tenant_id` claim and building schemas | `TestJwtFactory.cs`, `ActingUserFixtures.cs`, `SchemaFixtures.cs`, `RegisterSchemaAuthorizationIntegrationTests.cs` (TestContainers) all present |
| 13 | Code-in-plan | Human `tenant_id` sources from a user attribute in a scope-mapping (`user.attributes.get("tenant_id")`), on the same `user` object the `groups` mapping already uses; service clients get per-client constant scope-mappings (user-confirmed) — avoids the unverifiable client_credentials→service-account→attribute path | `service-clients.yaml:37-43` (groups mapping precedent); user decision |

## Tasks

### Task 1: Schema contract, domain, registration validation, and fixture cutover

**Files:**
- Modify: `Iverson.Clients/Common/Proto/object_mapping.proto` (`TypeDescriptor`)
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs`
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs:98-112`
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:37-83`
- Modify: `Iverson.Server/Iverson.Api.Tests/Helpers/SchemaFixtures.cs` and every test/LoadTest site that builds a `SchemaRequest`/`SchemaDescriptor` (see Assumption 11)
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`

**Interfaces:**
- Produces: `SchemaDescriptor.TenantColumn` (consumed by Tasks 2–5) and the `tenant_field` proto field (consumed by clients/fixtures).

- [ ] **Step 1: Add `tenant_field` to the proto.**
```proto
message TypeDescriptor {
    string                    type_name     = 1;
    repeated PropertyDescriptor properties  = 2;
    repeated RelationDescriptor relations   = 3;
    AuthorizationRules         authorization = 4;
    string                     tenant_field  = 5;  // REQUIRED; names a declared scalar property holding the row's tenant id
}
```

- [ ] **Step 2: Add nullable `TenantColumn` to `SchemaDescriptor`** (nullable per Assumption 10 — a missing value in a legacy `_iverson_schema` row must deserialize, not throw):
```csharp
public string? TenantColumn { get; init; }
```

- [ ] **Step 3: Populate `TenantColumn` in `SchemaBuilder.BuildDescriptor`** — set it from `typeDesc.TenantField` (empty string → null) in the returned descriptor initializer (`SchemaBuilder.cs:98-112`):
```csharp
TenantColumn = string.IsNullOrEmpty(typeDesc.TenantField) ? null : typeDesc.TenantField,
```

- [ ] **Step 4: Validate `tenant_field` in `SchemaRegistrationOrchestrator`,** after `BuildDescriptor` and alongside the existing `owner_field` block. It is REQUIRED and reuses the owner_field validation shape (property existence, string-valued SqlType, chunk-key collision). Reuse the existing `stringValuedSqlTypes` / `reservedChunkKeys` logic rather than duplicating it — factor a small local helper if that is cleaner than two near-identical blocks.
```csharp
// tenant_field is MANDATORY (unlike owner_field). Presence first, then the same
// property/string-valued/chunk-key checks owner_field already performs.
if (string.IsNullOrEmpty(descriptor.TenantColumn))
    throw new RpcException(new Status(StatusCode.InvalidArgument,
        $"tenant_field is required on '{descriptor.TypeName}'."));
if (!descriptor.ScalarColumns.Any(c => string.Equals(c.Name, descriptor.TenantColumn, StringComparison.OrdinalIgnoreCase)))
    throw new RpcException(new Status(StatusCode.InvalidArgument,
        $"tenant_field '{descriptor.TenantColumn}' on '{descriptor.TypeName}' does not match any declared scalar property."));
// ...string-valued SqlType check and chunk-key collision check, mirroring owner_field's.
```

- [ ] **Step 5: Cutover the fixtures.** In `SchemaFixtures.cs`, add a tenant scalar column (e.g. `new ColumnDescriptor("TenantId", "text", false)`) and `TenantColumn = "TenantId"` to each descriptor. In every test that builds a `SchemaRequest`/`TypeDescriptor` for registration (Assumption 11) and in the LoadTest entity/registration definitions, add a `TenantId` property and set `tenant_field`. This keeps the suite and LoadTest compiling and passing from Task 1 onward. (Enforcement tests that need cross-tenant behavior are added in Tasks 2–5; here, just make existing fixtures declare a tenant so they don't fail the new mandatory check.)

- [ ] **Step 6: Test** — extend `SchemaRegistrationOrchestratorTests` with: (a) missing `tenant_field` → `InvalidArgument`; (b) `tenant_field` naming a non-existent property → `InvalidArgument`; (c) valid `tenant_field` → registers. Mirror the existing owner_field test cases in that file.

- [ ] **Step 7: Build + test + commit**
```bash
dotnet test Iverson.Server/Iverson.Api.Tests
git add Iverson.Clients/Common/Proto/object_mapping.proto Iverson.Server/Iverson.Api/Schema Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs Iverson.Server/Iverson.Api.Tests Iverson.Server/Iverson.LoadTest
git commit -m "feat(api): add mandatory tenant_field to schema registration"
```

### Task 2: Evaluator emits tenant decision; acting-user tenant claim

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Authorization/IRowFieldAuthorizationEvaluator.cs` (`AuthorizationDecision`)
- Modify: `Iverson.Server/Iverson.Api/Authorization/RowFieldAuthorizationEvaluator.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Authorization/RowFieldAuthorizationEvaluatorTests.cs`

**Interfaces:**
- Consumes: `SchemaDescriptor.TenantColumn` (Task 1).
- Produces: `AuthorizationDecision.TenantColumn`/`TenantValue` (consumed by Tasks 3–5).

- [ ] **Step 1: Add two fields to `AuthorizationDecision`** (positional record; append after `AllowedFields`):
```csharp
public sealed record AuthorizationDecision(
    bool Denied,
    bool OwnershipRequired,
    string? OwnerFieldName,
    string? OwnerValue,
    IReadOnlySet<string>? AllowedFields,
    string? TenantColumn,   // null only on denied decisions
    string? TenantValue);   // the caller's tenant_id
```

- [ ] **Step 2: Update all 5 construction sites in `RowFieldAuthorizationEvaluator`.** The four early-deny returns pass `null, null` for the two new fields. The final success return (`:72`) computes tenant first and denies if it can't be enforced:
```csharp
// Near the top of Evaluate, after the existing rules/actingUser null-denies:
//   - schema has no tenant column (legacy, pre-cutover schema) → deny (hard cutover)
//   - caller has no tenant_id claim → deny (per-path convention: reads empty, writes PermissionDenied)
if (string.IsNullOrEmpty(schema.TenantColumn))
    return new AuthorizationDecision(true, false, null, null, null, null, null);
var tenantId = actingUser.FindFirst("tenant_id")?.Value;
if (string.IsNullOrEmpty(tenantId))
    return new AuthorizationDecision(true, false, null, null, null, null, null);
// ...existing bypass/ownership/field logic unchanged...
// Final success return threads tenant through:
return new AuthorizationDecision(false, ownershipRequired, ownerFieldName, ownerValue, allowedFields,
    schema.TenantColumn, tenantId);
```
Place the tenant checks so they run for every non-denied path (including bypass-role callers) — the tenant boundary applies even when `CanReadAll`/`CanWriteAll`/`CanDeleteAll` is set. Do not disturb the existing `rules is null` / `actingUser is null` early denies (they precede these and stay as-is with `null, null` appended).

- [ ] **Step 3: Unit tests** in `RowFieldAuthorizationEvaluatorTests` (mint principals via the existing helper): (a) tenant claim present + `CanReadAll` role → decision carries `TenantColumn`/`TenantValue`, not denied; (b) no `tenant_id` claim → `Denied`; (c) `schema.TenantColumn == null` → `Denied`; (d) a matching-role bypass caller still gets `TenantColumn`/`TenantValue` populated (proves bypass is still tenant-scoped).

- [ ] **Step 4: Build + test + commit**
```bash
dotnet test Iverson.Server/Iverson.Api.Tests
git add Iverson.Server/Iverson.Api/Authorization Iverson.Server/Iverson.Api.Tests/Authorization
git commit -m "feat(api): evaluate tenant boundary in row/field authorization"
```

### Task 3: Postgres read + relation-traversal + write + delete enforcement

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs` (Get `:41-42`, GetMany `:97-98`)
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` (Get `:82`, Delete `:191-202`)
- Modify: `Iverson.Server/Iverson.Api/Grpc/EntityRelationResolver.cs` (`TryAuthorizeAndMask` `:46-54`)
- Modify: `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs` (`EnforceWriteAuthorization` `:41-68`)
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs`, `ObjectMappingGrpcServiceTests.cs`, `ObjectPersistenceGrpcServiceTests.cs`; relation coverage via `ObjectMappingGrpcServiceTests`

**Interfaces:**
- Consumes: `AuthorizationDecision.TenantColumn`/`TenantValue` (Task 2).

- [ ] **Step 1: Read sites — extend the ownership comparison with tenant.** At each of the three read sites, AND a tenant comparison next to the existing `decision.OwnershipRequired && owner-mismatch` check: when `decision.TenantColumn` is non-null and the fetched row's value at that column ≠ `decision.TenantValue`, treat the row as not-found/denied (same branch that already handles an owner mismatch). Example (Retrieval.Get, `:40-45`):
```csharp
if (decision.Denied ||
    (decision.OwnershipRequired &&
     StructFieldAccess.GetFieldString(data, decision.OwnerFieldName!) != decision.OwnerValue) ||
    (decision.TenantColumn is not null &&
     StructFieldAccess.GetFieldString(data, decision.TenantColumn) != decision.TenantValue))
{
    return new RetrievalResponse { Found = false, TraceId = request.TraceId };
}
```
Apply the identical additional clause at GetMany (`:97-98`) and Mapping.Get (`:82`).

- [ ] **Step 2: Relation traversal — `EntityRelationResolver.TryAuthorizeAndMask`.** Add the same tenant clause so `Get` with `depth>0` cannot surface a related row from another tenant (folds in the 4th read site per Assumption 6):
```csharp
private bool TryAuthorizeAndMask(Struct relatedStruct, AuthorizationDecision decision)
{
    if (decision.Denied ||
        (decision.OwnershipRequired &&
         StructFieldAccess.GetFieldString(relatedStruct, decision.OwnerFieldName!) != decision.OwnerValue) ||
        (decision.TenantColumn is not null &&
         StructFieldAccess.GetFieldString(relatedStruct, decision.TenantColumn) != decision.TenantValue))
        return false;
    AuthorizationFieldMasking.MaskDisallowedFields(relatedStruct, decision.AllowedFields);
    return true;
}
```

- [ ] **Step 3: Write create — stamp tenant.** In `EnforceWriteAuthorization`, in the `existingRowJson is null` branch, force-set the tenant column from the decision (unconditionally, not gated on `OwnershipRequired` — the tenant boundary applies to bypass callers too):
```csharp
if (existingRowJson is null)
{
    if (decision.TenantColumn is not null)
        payload.Fields[decision.TenantColumn] = Value.ForString(decision.TenantValue!);
    if (decision.OwnershipRequired)
        payload.Fields[decision.OwnerFieldName!] = Value.ForString(decision.OwnerValue!);
}
```

- [ ] **Step 4: Write update — match + immutability.** In the `else` (existing-row) branch of `EnforceWriteAuthorization`, before the owner checks, reject a tenant mismatch on the existing row and reject any payload that changes the tenant column:
```csharp
if (decision.TenantColumn is not null)
{
    if (StructFieldAccess.GetFieldString(existingStruct, decision.TenantColumn) != decision.TenantValue)
        throw new RpcException(new Status(StatusCode.PermissionDenied, deniedMessage));
    var attemptedTenant = StructFieldAccess.GetFieldString(payload, decision.TenantColumn);
    if (attemptedTenant is not null && attemptedTenant != decision.TenantValue)
        throw new RpcException(new Status(StatusCode.PermissionDenied, "Tenant field is immutable."));
}
```

- [ ] **Step 5: Delete — inline tenant check.** In `ObjectMappingGrpcService.Delete` (`:191-202`), add the tenant clause to the inline authorization check, unconditionally (covers `CanDeleteAll` bypass callers), returning the same not-found response used for an owner mismatch:
```csharp
if (decision.Denied ||
    (decision.OwnershipRequired &&
     StructFieldAccess.GetFieldString(JsonParser.Default.Parse<Struct>(rowJson), decision.OwnerFieldName!) != decision.OwnerValue) ||
    (decision.TenantColumn is not null &&
     StructFieldAccess.GetFieldString(JsonParser.Default.Parse<Struct>(rowJson), decision.TenantColumn) != decision.TenantValue))
{
    return new MappingDeleteResponse { Success = false, Error = $"'{request.TypeName}:{request.Key}' not found.", TraceId = request.TraceId };
}
```

- [ ] **Step 6: Tests.** Using the existing mock/fixture conventions in these test files (a schema whose `TenantColumn` is set and a caller minted with a mismatched `tenant_id`, plus a `CanReadAll`/`CanDeleteAll` role to prove tenant still wins over bypass): (a) Retrieval.Get/GetMany return not-found for a cross-tenant row; (b) Mapping.Get + a `depth>0` relation into a cross-tenant related row omits it; (c) Persistence/Mapping.Post stamps the caller's tenant onto the created payload; (d) Update to a cross-tenant existing row → `PermissionDenied`; a payload changing the tenant → `PermissionDenied`; (e) Delete of a cross-tenant row → not-found even with `CanDeleteAll`.

- [ ] **Step 7: Build + test + commit**
```bash
dotnet test Iverson.Server/Iverson.Api.Tests
git add Iverson.Server/Iverson.Api/Grpc Iverson.Server/Iverson.Api.Tests/Grpc
git commit -m "feat(api): enforce tenant boundary on Postgres read/write/delete paths"
```

### Task 4: StarRocks enforcement

**Files:**
- Modify: `Iverson.Server/Iverson.StarRocks/AuthorizationConstraint.cs`
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs` (ownership predicate sites: primary `WHERE` `:61-78,202-207,287-290`; joined `ON` `:715-719`)
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs` (Pipeline ownership sites: primary `:394-397`; joined `:533-537`)
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:448,458`
- Test: `Iverson.Server/Iverson.StarRocks.Tests/StarRocksQueryBuilderTests.cs`, `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

**Interfaces:**
- Consumes: `AuthorizationDecision.TenantColumn`/`TenantValue` (Task 2).

- [ ] **Step 1: Extend `AuthorizationConstraint`** with `string? TenantColumn, string? TenantValue` (positional record, appended).

- [ ] **Step 2: Pass tenant through at both construction sites** in `ObjectSearchGrpcService.EvaluateAuthorization` (`:448` primary, `:458` joined): add `primaryDecision.TenantColumn, primaryDecision.TenantValue` / `decision.TenantColumn, decision.TenantValue`. The `OrdinalIgnoreCase` dict keying stays as-is (do not change it).

- [ ] **Step 3: AND the tenant predicate at each ownership site in `StarRocksQueryBuilder`.** Wherever the builder currently appends `` `alias`.`OwnerColumn` = @__ownerVal `` for a constraint whose `OwnerColumn is not null`, add — for a constraint whose `TenantColumn is not null` — the analogous `` `alias`.`TenantColumn` = @<param> `` predicate. **Mirror the owner parameter-naming split exactly:** the primary type uses a fixed `@__tenantVal` (paralleling `@__ownerVal` at `:61-63,76-78`); each joined type uses a per-join unique name `$"__tenant{map.Count}"` (paralleling `$"__owner{map.Count}"` at `:715-716`), added to `param` and interpolated into that join's `ON`. A single fixed name across joined types would collide, since tenant is mandatory on every joined type. For the primary type this ANDs onto its `WHERE`; for joined types it appends to that join's own `ON` clause (never the outer `WHERE`) — identical placement to the ownership predicate, to preserve outer-join semantics. Tenant applies even when `OwnerColumn is null` (bypass callers), so gate the tenant predicate on `TenantColumn is not null` independently of the ownership block. The Pipeline RPC uses a separate builder: apply the same tenant predicate at `StarRocksPipelineBuilder.cs`'s ownership sites too — primary type at `:394-397` (fixed `@__tenantVal`, alongside its `@__ownerVal`) and each joined type at `:533-537` (per-join unique `$"__tenant{…}"`, paralleling its `pName`). Same parameter-naming discipline as the `StarRocksQueryBuilder` sites above; tenant is mandatory on every type, so the joined-site collision guard applies identically.

- [ ] **Step 4: Tests.** In `StarRocksQueryBuilderTests`, assert the generated SQL for Search/Aggregate/GroupBy/Pipeline includes the tenant predicate in the correct clause (primary `WHERE`, joined `ON`) with a bound parameter. Add an adversarial-join regression mirroring the existing 5c test: a joined type's tenant predicate lands on the `ON`, not the outer `WHERE`. In `ObjectSearchGrpcServiceTests`, assert a cross-tenant caller with `CanReadAll` gets an empty result.

- [ ] **Step 5: Build + test + commit**
```bash
dotnet test Iverson.Server/Iverson.StarRocks.Tests Iverson.Server/Iverson.Api.Tests
git add Iverson.Server/Iverson.StarRocks Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.StarRocks.Tests Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs
git commit -m "feat(starrocks): enforce tenant boundary on search/aggregate/groupby/pipeline"
```

### Task 5: Qdrant enforcement

**Files:**
- Modify: `Iverson.Server/Iverson.Vector/QdrantFilterBuilder.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` (SearchSimilar `:154`, SearchChunks `:220`)
- Test: `Iverson.Server/Iverson.Vector.Tests/QdrantFilterBuilderTests.cs`

**Interfaces:**
- Consumes: `AuthorizationDecision.TenantColumn`/`TenantValue` (Task 2).

- [ ] **Step 1: Add `ApplyTenant`** mirroring `ApplyOwnership`'s signature exactly (`QdrantFilterBuilder.cs:54-59`), including the leading `bool` for sibling-consistency:
```csharp
public static Filter? ApplyTenant(Filter? filter, bool tenantRequired, string? tenantFieldCamelCase, string? tenantValue)
{
    if (!tenantRequired) return filter;
    filter ??= new Filter();
    filter.Must.Add(Conditions.MatchKeyword(tenantFieldCamelCase!, tenantValue!));
    return filter;
}
```

- [ ] **Step 2: Wire it into SearchSimilar and SearchChunks,** right after each `ApplyOwnership` call (`:154`, `:220`). Tenant is required whenever the decision carries a `TenantColumn`; the field is camelCased for the Qdrant payload key, same as the owner field:
```csharp
filter = QdrantFilterBuilder.ApplyTenant(filter, decision.TenantColumn is not null, decision.TenantColumn?.ToCamelCase(), decision.TenantValue);
```

- [ ] **Step 3: Tests** in `QdrantFilterBuilderTests`, mirroring the existing `ApplyOwnership` cases: `tenantRequired: false` → filter unchanged (null and existing-filter variants); `tenantRequired: true` → a `MatchKeyword` condition on the tenant field is appended, preserving any existing filter.

- [ ] **Step 4: Build + test + commit**
```bash
dotnet test Iverson.Server/Iverson.Vector.Tests Iverson.Server/Iverson.Api.Tests
git add Iverson.Server/Iverson.Vector Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Vector.Tests
git commit -m "feat(vector): enforce tenant boundary on SearchSimilar/SearchChunks"
```

### Task 6: Authentik `tenant_id` claim provisioning

**Files:**
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml`
- Modify: `Iverson.Server/deploy/scripts/mint_acting_user_token.py:316` — add `tenant_id` to the requested `scope`.

**Interfaces:**
- Consumes: nothing (blueprint-only). Independent of Tasks 3–5's code; enables end-to-end enforcement.

- [ ] **Step 1: Human `tenant_id` scope-mapping** (attribute-sourced, over the same `user` object the `groups` mapping uses):
```yaml
- model: authentik_providers_oauth2.scopemapping
  identifiers:
    scope_name: tenant_id
  attrs:
    name: "Iverson: tenant id"
    description: The caller's tenant identifier for data isolation
    expression: 'return {"tenant_id": request.user.attributes.get("tenant_id")}'
```
Bind it to the human providers (`iverson-oidc-default`, `iverson-loadtest-human`) via `property_mappings`, and add a `tenant_id` attribute to the seeded human users' `attrs`. The `tenant_id` claim is only issued when the token request sends `scope=tenant_id` (the same Authentik behavior handled for service clients in Step 2). Update `mint_acting_user_token.py`'s requested scope (`:316`, currently `"openid groups"`) to include `tenant_id`, and note that any human OIDC login (e.g. the operator flow via `iverson-oidc-default`) must likewise request the `tenant_id` scope for the claim to be issued.

- [ ] **Step 2: Per-client constant `tenant_id` scope-mappings** for the service clients (user-confirmed: constant per client, avoids the unverifiable service-account-attribute path). One mapping per service client, each returning that client's fixed tenant, bound only to that provider — e.g.:
```yaml
- model: authentik_providers_oauth2.scopemapping
  identifiers:
    scope_name: tenant_id_loadtest
  attrs:
    name: "Iverson: loadtest tenant id"
    expression: 'return {"tenant_id": "tenant-loadtest"}'
```
Bind `tenant_id_loadtest`/`tenant_id_webtest`/`tenant_id_admin` to `iverson-loadtest`/`iverson-webtest`/`iverson-admin-automation` respectively. Callers must request the `tenant_id*` scope in the token POST (the same `scope=` requirement that already applies to `admin`/`schema_admin` — Authentik does not auto-include bound scopes; see `docs/user-management-and-security.md`). Update the LoadTest token requests to include their tenant scope.

- [ ] **Step 3: Apply the identical edits to both blueprint files,** honoring their different YAML indentation (compose-only vs. Helm ConfigMap template — the template is one nesting level deeper). No C# build here; a full validation is a live smoke test (register a tenanted schema, write as tenant A, confirm tenant B cannot read/update/delete/search it), which is executed during rollout, not in this plan.

- [ ] **Step 4: Commit**
```bash
git add Iverson.Server/deploy/helm/iverson/charts/authentik
git commit -m "feat(deploy): issue tenant_id claim for human and service callers"
```

## Tasks NOT in this plan

Inherited verbatim from the spec's "Explicitly out of scope" (a new spec → plan cycle is required to add any of these):

- Part B — database-level defense-in-depth (Postgres RLS etc.).
- Part C — audit logging of data access / cross-tenant access attempts.
- Part D — tenant lifecycle management and the delegated per-tenant admin API.
- Backfilling tenant values onto existing rows of already-registered types (a necessary operational step before production traffic resumes for a re-registered type, but not a design/code deliverable of this plan).
