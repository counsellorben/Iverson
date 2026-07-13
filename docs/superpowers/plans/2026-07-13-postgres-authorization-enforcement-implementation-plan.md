# Postgres Authorization Enforcement (Part 5b) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/superpowers/specs/2026-07-13-postgres-authorization-enforcement-design.md` (commit SHA: `79eacc1`)

**Goal:** Wire Part 5a's `IRowFieldAuthorizationEvaluator` into every Postgres-backed write/read RPC (`ObjectMappingGrpcService`, `ObjectPersistenceGrpcService`, `ObjectRetrievalGrpcService`) so row-level and field-level authorization is actually enforced.

**Architecture:** Two new static helpers (`MaskDisallowedFields` for reads, `RejectDisallowedFields` for writes) centralize the field-level enforcement logic; each RPC calls `IRowFieldAuthorizationEvaluator.Evaluate(...)` once per request (or once per related-entity fetch, recursively, for `ObjectMappingGrpcService.Get`) and branches on the returned `AuthorizationDecision`.

**Tech stack:** .NET/C#, gRPC (`Grpc.Core`), NSubstitute + xUnit + FluentAssertions for tests — all inherited from the spec's verified assumptions.

---

## File Structure

- Create: `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs` — `MaskDisallowedFields`/`RejectDisallowedFields` static helpers.
- Create: `Iverson.Server/Iverson.Api.Tests/Helpers/ActingUserFixtures.cs` — shared `ClaimsPrincipal` test-builder, used by all 3 grpc test files instead of tripling the same helper.
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` — DI wiring (Task 1); `Get` + relation resolvers (Task 2); `Post` (Task 4); `Delete` (Task 5); `Update` (Task 6).
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs` — DI wiring incl. new `IEntityRepository` (Task 1); `Post` (Task 4); `Update` (Task 6).
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs` — DI wiring (Task 1); `Get`/`GetMany` (Task 3).
- Modify: `Iverson.Server/Iverson.Api.Tests/Helpers/SchemaFixtures.cs` — add a permissive bypass `Authorization` to every fixture (Task 1).
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` — constructor update, default bypass `ActingUser`, `MakeSchema` helper gets bypass `Authorization` too (Task 1); new auth tests (Tasks 2, 4, 5, 6).
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs` — constructor update, default bypass `ActingUser` (Task 1); new auth tests (Tasks 4, 6).
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs` — constructor update, default bypass `ActingUser` (Task 1); new auth tests (Task 3).
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/RegisterSchemaAuthorizationIntegrationTests.cs` — constructor call site update only, no new tests (Task 1). This file only exercises `RegisterSchema`, which has no authorization check, so it needs no bypass fixture — just the two new constructor args to keep compiling.

## Global Constraints

- All new field-level and row-level checks canonicalize payload keys via `StructSerializer.UpperFirst(key)` before comparing against `AllowedFields` — never raw keys, never `StructFieldAccess.Candidates` (which resolves the opposite direction).
- Write-path denial is always `RpcException(StatusCode.PermissionDenied, ...)`. Read-path denial always reuses the RPC's existing not-found response shape — never a distinct "forbidden" signal.
- The Update ownership-immutability check (Task 6) must source the owner field name from `schema.Authorization?.OwnerField`, never from `decision.OwnerFieldName` (which is `null` for bypass callers).
- `RejectDisallowedFields` calls in the owner-field-force-set paths (Post, and Update's create-fallback and existing-row branches) always pass `exemptField: decision.OwnerFieldName`.
- Test bypass role name is `"test-bypass"` (an arbitrary string local to test fixtures, matching no production role) — used consistently across `SchemaFixtures.cs` and all 3 grpc test classes' default `ActingUser`.

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time; not re-verified here:

- `ObjectMappingGrpcService`'s constructor (`ObjectMappingGrpcService.cs:23-36`) has neither `IActingUserAccessor` nor `IRowFieldAuthorizationEvaluator`. `ObjectPersistenceGrpcService`'s constructor (`ObjectPersistenceGrpcService.cs:16-22`) has neither, nor `IEntityRepository`. `ObjectRetrievalGrpcService`'s constructor (`ObjectRetrievalGrpcService.cs:9-12`) has neither `IActingUserAccessor` nor `IRowFieldAuthorizationEvaluator`.
- `StructFieldAccess.GetFieldString(Struct, string)` (`StructFieldAccess.cs:25-31`) exists with that signature; all lookups in this file go through `Candidates(name)`, matching a field name and its camelCase form.
- `Struct.Fields[key] = value` direct mutation and `.Remove(key)` are safe, already-established patterns in this codebase.
- `ObjectMappingGrpcService.Delete`'s pre-fetch (`FetchByKeyAsync`, line 253) happens well before the delete transaction (line 265), leaving room to insert the ownership check using the already-fetched `rowJson`.
- All three relation resolvers (`ObjectMappingGrpcService.cs:352-423`) share an identical "fetch, [check], then splice" shape, each with `relatedSchema` already in scope for the recursive `Evaluate` call.
- `IRowFieldAuthorizationEvaluator` is registered `AddSingleton` at `Program.cs:173`.
- Both `Update` methods (`ObjectMappingGrpcService.cs:186-244`, `ObjectPersistenceGrpcService.cs:93-158`) extract `key` immediately after schema resolution, well before the write call.
- Response proto shapes: `MappingResponse`/`MappingDeleteResponse` have `success`+`error`; `PersistResponse` has `success`+`error` (unused for denial — `RpcException` is used instead); `RetrievalResponse` has only `found`+`data`+`trace_id`, no `error` field.
- `RowFieldAuthorizationEvaluator`'s row-level bypass logic is: `Denied` iff no rules, no identity, or (no bypass role matched AND no `owner_field` configured); otherwise bypass (`OwnershipRequired=false`) or ownership (`OwnershipRequired=true`, `OwnerFieldName`/`OwnerValue` populated from `sub`). `OwnerFieldName`/`OwnerValue` are `null` whenever `bypass=true`.
- `ObjectRetrievalGrpcService.GetMany` (`ObjectRetrievalGrpcService.cs:42-78`) resolves `schema` once before its streaming loop.
- Exactly 4 files directly construct one of the 3 services: `ObjectMappingGrpcServiceTests.cs`, `ObjectPersistenceGrpcServiceTests.cs`, `ObjectRetrievalGrpcServiceTests.cs`, `RegisterSchemaAuthorizationIntegrationTests.cs`.
- `StructSerializer.UpperFirst(string)` (`ProtoPayloadHelper.cs:11-12`) is the existing canonicalization `SerializePayload` already applies; `SerializePayload` runs *after* the point in every call site where the new checks are proposed to run, so the checks must canonicalize independently.

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs` does not exist yet | `ls Iverson.Server/Iverson.Api/Grpc/` — no match for `AuthorizationFieldMasking` or `Masking` |
| 2 | File path | `Iverson.Server/Iverson.Api.Tests/Helpers/ActingUserFixtures.cs` does not exist yet | `ls Iverson.Server/Iverson.Api.Tests/Helpers/` — contains `AuthTestWebApplicationFactory.cs`, `SchemaFixtures.cs`, `StartupNoOpFakes.cs`, `TestJwtFactory.cs`, `TestServerCallContext.cs`; no `ActingUserFixtures.cs` |
| 3 | Function signature | `IEntityRepository.FetchByKeyAsync(TableSchema schema, string key)` — the exact signature Task 6's new pre-fetch in `ObjectPersistenceGrpcService.Update` must call, wrapped via `SchemaBuilder.ToTableSchema(schema)` (already used elsewhere in the same file, e.g. lines 46, 113) | `IRecordStoreRoles.cs:27-34` |
| 4 | Function signature | `IEntityRepository` is registered `AddSingleton` via `Iverson.Sql.ServiceCollectionExtensions.AddPostgres`, called from `Program.cs:147` — so adding it to `ObjectPersistenceGrpcService`'s constructor needs no new DI registration | `ServiceCollectionExtensions.cs:25-26` (`services.AddSingleton<IEntityRepository>(...)`); `Program.cs:147` calls `AddPostgres` |
| 5 | Command | `dotnet test Iverson.Api.Tests --filter "<Name>"` run from `Iverson.Server/` is a valid, previously-used invocation in this repo | Confirmed via prior plan `docs/superpowers/plans/2026-06-24-architecture-remediation.md:101,214` and `Iverson.Server/Iverson.Api.Tests/` + `Iverson.Server.slnx` both existing at that path |
| 6 | Consumer impact (Cat 6) | `RegisterSchemaAuthorizationIntegrationTests.cs`'s `new ObjectMappingGrpcService(...)` call (lines 205-217) is a 12-positional-arg call that will need 2 more args inserted for the new `IActingUserAccessor`/`IRowFieldAuthorizationEvaluator` params — but this file only exercises `RegisterSchema`, never `Post`/`Update`/`Delete`/`Get`, so it needs no bypass-`Authorization` fixture, only the constructor-call fix | Full read of `RegisterSchemaAuthorizationIntegrationTests.cs:198-257` — the only test method calls `sut.RegisterSchema(...)`, nothing else |
| 7 | Consumer impact (Cat 6) | `ObjectMappingGrpcServiceTests.cs` has a **second**, file-local schema builder (`MakeSchema(string typeName)`, lines 79-89) independent of `SchemaFixtures.cs`, used by exactly one test (`Post_ReturnsPayloadAsData_NotDbRefetch`, line 403) — this local helper also has no `Authorization` and must independently gain the same bypass rules, or that one test breaks even after `SchemaFixtures.cs` is fixed | `grep -n "MakeSchema(" ObjectMappingGrpcServiceTests.cs` → 2 hits: definition (79) + single call site (403) |
| 8 | Consumer impact (Cat 6) | No production or test code outside the 4 already-known files constructs any of the 3 services directly | `grep -rln "new ObjectMappingGrpcService(\|new ObjectPersistenceGrpcService(\|new ObjectRetrievalGrpcService("` across the repo → exactly the same 4 files the spec's Cat 6 finding already named |
| 9 | Task ordering | Tasks 2-6 touch disjoint RPC methods (`Get`+resolvers / `Get`+`GetMany` / `Post`×2 / `Delete` / `Update`×2) with no task importing a symbol introduced by a later task — only Task 1's helpers/DI wiring are a shared prerequisite | Direct read of all 3 production files' current method boundaries; no cross-references between `Get`, `Post`, `Update`, `Delete` bodies in either service |
| 10 | Code validity | `ActingUserAccessor` (the concrete class backing `IActingUserAccessor`) has a public settable `ActingUser` property and a public parameterless-constructible shape (`new ActingUserAccessor { ActingUser = ... }`), usable directly in test constructors without a DI container or NSubstitute | `IActingUserAccessor.cs:10-13` |
| 11 | Code validity | `Grpc.Core.StatusCode.PermissionDenied` exists (needed for every write-path denial `RpcException`) | `grep -n "PermissionDenied" .../grpc.core.api/2.64.0/lib/netstandard2.1/Grpc.Core.Api.xml` → `F:Grpc.Core.StatusCode.PermissionDenied` |
| 12 | Commit convention | This repo's commit messages for this exact feature use `feat(api): ...`/`fix(api): ...`/`test(api): ...` prefixes | `git log --oneline -15` → `fix(api): include FK/vector/chunk columns in AllowedFields`, `feat(api): add shared row/field authorization evaluator`, `test(api): add TestContainers integration test for schema authorization provisioning`, `feat(api): add schema authorization metadata (...)` |
| 13 | Drift check | Spec's last-modifying commit (`79eacc1`) equals current repo HEAD — no drift since the spec was written | `git log -1 --format=%H HEAD` and `git log -1 --format=%H -- docs/superpowers/specs/2026-07-13-postgres-authorization-enforcement-design.md` both return `79eacc1ad7a3b39a9307de204287607c6243b01c` |

## Tasks

### Task 1: Shared enforcement infrastructure (DI wiring, helpers, test fixtures)

**Files:**
- Create: `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs`
- Create: `Iverson.Server/Iverson.Api.Tests/Helpers/ActingUserFixtures.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` (constructor only)
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs` (constructor only)
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs` (constructor only)
- Modify: `Iverson.Server/Iverson.Api.Tests/Helpers/SchemaFixtures.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` (constructor + `MakeSchema` helper only)
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs` (constructor only)
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs` (constructor only)
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/RegisterSchemaAuthorizationIntegrationTests.cs` (constructor call site only)

**Interfaces:**
- Produces: `AuthorizationFieldMasking.MaskDisallowedFields`/`RejectDisallowedFields` (consumed by Tasks 2-6); the new constructor shapes of all 3 services (consumed by Tasks 2-6); `ActingUserFixtures.Principal(...)` (consumed by Tasks 2-6's new tests); the bypass `Authorization` on every `SchemaFixtures.*Schema()` fixture (consumed by all pre-existing tests, unmodified).

- [ ] **Step 1: Add the two static field-enforcement helpers**
Create `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs`, matching the existing `StructFieldAccess.cs`/`ProtoPayloadHelper.cs` convention (internal static class, `Iverson.Api.Grpc` namespace):
```csharp
using Google.Protobuf.WellKnownTypes;

namespace Iverson.Api.Grpc;

internal static class AuthorizationFieldMasking
{
    public static void MaskDisallowedFields(Struct payload, IReadOnlySet<string>? allowedFields)
    {
        if (allowedFields is null) return;

        var toRemove = payload.Fields.Keys
            .Where(key => !allowedFields.Contains(StructSerializer.UpperFirst(key)))
            .ToList();
        foreach (var key in toRemove)
            payload.Fields.Remove(key);
    }

    public static void RejectDisallowedFields(
        Struct payload, IReadOnlySet<string>? allowedFields, string? exemptField = null)
    {
        if (allowedFields is null) return;

        var disallowed = payload.Fields.Keys
            .Select(StructSerializer.UpperFirst)
            .Where(canonical => !allowedFields.Contains(canonical) && canonical != exemptField)
            .ToList();
        if (disallowed.Count > 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Field(s) not permitted for this caller: {string.Join(", ", disallowed)}"));
    }
}
```
Needs `using Grpc.Core;` for `RpcException`/`Status`/`StatusCode`.

- [ ] **Step 2: Wire `IActingUserAccessor` + `IRowFieldAuthorizationEvaluator` into all 3 services' constructors**
`ObjectMappingGrpcService` (`ObjectMappingGrpcService.cs:23-36`): add `IActingUserAccessor _actingUserAccessor, IRowFieldAuthorizationEvaluator _authEvaluator` to the primary constructor's parameter list.
`ObjectRetrievalGrpcService` (`ObjectRetrievalGrpcService.cs:14-18`): same two params (using this file's existing unprefixed-parameter-name convention, e.g. `actingUserAccessor, authEvaluator`).
`ObjectPersistenceGrpcService` (`ObjectPersistenceGrpcService.cs:16-23`): same two params, plus `IEntityRepository entities` (this service has none today), matching this file's existing unprefixed-parameter-name convention.
`IActingUserAccessor` needs no new `using` — it's in the `Iverson.Api` namespace, an enclosing namespace of `Iverson.Api.Grpc` (C#'s unqualified-name lookup walks up dot-separated enclosing namespaces even with file-scoped declarations; `ActingUserInterceptor.cs`, already in `Iverson.Api.Grpc`, references `IActingUserAccessor` today with no `using Iverson.Api;` line, confirming this holds in this codebase). `IRowFieldAuthorizationEvaluator`/`AuthorizationAction` are in `Iverson.Api.Authorization`, a sibling (not enclosing) namespace — add `using Iverson.Api.Authorization;` to all 3 files (none currently have it).

- [ ] **Step 3: Add a shared `ClaimsPrincipal` test-builder helper**
Create `Iverson.Server/Iverson.Api.Tests/Helpers/ActingUserFixtures.cs`, following the same shape as `RowFieldAuthorizationEvaluatorTests.cs`'s private `ActingUser` helper (`RowFieldAuthorizationEvaluatorTests.cs:13-18`), promoted to a shared, reusable static class:
```csharp
using System.Security.Claims;

namespace Iverson.Api.Tests.Helpers;

public static class ActingUserFixtures
{
    public static ClaimsPrincipal Principal(string sub, params string[] groups)
    {
        var claims = new List<Claim> { new("sub", sub) };
        claims.AddRange(groups.Select(g => new Claim("groups", g)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
```

- [ ] **Step 4: Give every `SchemaFixtures.*Schema()` fixture a permissive bypass `Authorization`**
In `SchemaFixtures.cs`, add `Authorization = new AuthorizationRules(null, new List<RowPermission> { new("test-bypass", true, true, true) }, new List<FieldPermission>())` to each of the 7 fixture methods (`AuthorSchema`, `ArticleSchema`, `ArticleWithOneToManySchema`, `UserArticleSchema`, `PostWithTagsSchema`, `TagSchema`, `ArticleWithProjectionSchema`). `OwnerField: null` is safe because bypass short-circuits ownership entirely (`RowFieldAuthorizationEvaluator.cs:29-32`); `CanReadAll`/`CanWriteAll`/`CanDeleteAll: true` covers every action any existing test exercises; empty `FieldPermissions` means `AllowedFields` stays `null` (unrestricted), so no existing field-level assertion is affected. No new `using` needed — `AuthorizationRules`/`RowPermission`/`FieldPermission` live in `Iverson.Api.Schema` (`SchemaDescriptor.cs:38-48`), already `using`d by this file.

- [ ] **Step 5: Give the local `MakeSchema` helper in `ObjectMappingGrpcServiceTests.cs` the same bypass `Authorization`**
At `ObjectMappingGrpcServiceTests.cs:79-89`, add the identical `Authorization = new AuthorizationRules(...)` (same shape as Step 4) to `MakeSchema`'s returned `SchemaDescriptor`, so `Post_ReturnsPayloadAsData_NotDbRefetch` (the one test using this helper) keeps passing.

- [ ] **Step 6: Update the 3 grpc test classes' constructors to pass the new params + set a default bypass `ActingUser`**
In each of `ObjectMappingGrpcServiceTests.cs` (constructor at lines 38-68), `ObjectPersistenceGrpcServiceTests.cs` (lines 26-43), `ObjectRetrievalGrpcServiceTests.cs` (lines 26-35):
- Add a `private readonly IActingUserAccessor _actingUserAccessor;` field, instantiated as `new ActingUserAccessor { ActingUser = ActingUserFixtures.Principal("test-user", "test-bypass") };`.
- Add `private readonly IRowFieldAuthorizationEvaluator _authEvaluator = new RowFieldAuthorizationEvaluator();` (the real evaluator, matching this repo's existing convention of using the real 5a evaluator rather than a mock — per the spec's Testing plan).
- Pass both into the `_sut = new XGrpcService(...)` call, in the same parameter position used in Step 2's constructor edit.
- `ObjectPersistenceGrpcServiceTests.cs` additionally needs `private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();` passed into its constructor call (matching the existing `_entities` field already present in the other two test files).
Same namespace-visibility rule as Step 2 applies here: `Iverson.Api.Tests.Grpc`'s enclosing-namespace chain (`Iverson.Api.Tests.Grpc` → `Iverson.Api.Tests` → `Iverson.Api` → global) includes `Iverson.Api`, so `IActingUserAccessor`/`ActingUserAccessor` need no new `using`. `Iverson.Api.Authorization` is not in that chain (it's a sibling of `Iverson.Api.Tests`, not an ancestor) — add `using Iverson.Api.Authorization;` to all 3 test files (none currently have it).

- [ ] **Step 7: Update `RegisterSchemaAuthorizationIntegrationTests.cs`'s constructor call site**
At lines 205-217, insert `Substitute.For<IActingUserAccessor>(), Substitute.For<IRowFieldAuthorizationEvaluator>()` into the positional argument list, in the same position used in Step 2's constructor edit. No bypass fixture needed (Assumption 6) — this file never calls `Post`/`Update`/`Delete`/`Get`.

- [ ] **Step 8: Run the full existing test suite to confirm zero regressions**
```bash
cd Iverson.Server
dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~ObjectMappingGrpcServiceTests|FullyQualifiedName~ObjectPersistenceGrpcServiceTests|FullyQualifiedName~ObjectRetrievalGrpcServiceTests"
```
All pre-existing tests in these 3 files must still pass unchanged. (`RegisterSchemaAuthorizationIntegrationTests` is `[Trait("Category", "Integration")]` and requires TestContainers — not run here; its compile-correctness is covered by Step 7's edit alone.)

- [ ] **Step 9: Commit**
```bash
git add Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs Iverson.Server/Iverson.Api.Tests/Helpers/ActingUserFixtures.cs Iverson.Server/Iverson.Api.Tests/Helpers/SchemaFixtures.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/RegisterSchemaAuthorizationIntegrationTests.cs
git commit -m "feat(api): wire authorization evaluator DI + field-masking helpers into Postgres RPC services"
```

### Task 2: `ObjectMappingGrpcService.Get` + recursive relation enforcement

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs:98-122` (`Get`), `:352-423` (the 3 relation resolvers)
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`

**Interfaces:**
- Consumes: `AuthorizationFieldMasking.MaskDisallowedFields`, `_actingUserAccessor`/`_authEvaluator` (Task 1)

- [ ] **Step 1: Enforce at the root fetch in `Get`**
In `Get` (`ObjectMappingGrpcService.cs:98-122`), move the existing `var entityStruct = JsonParser.Default.Parse<Struct>(rowJson);` line to immediately after the not-found check (its current position), then insert the auth check right after it, reusing `entityStruct` instead of re-parsing `rowJson`:
```csharp
var entityStruct = JsonParser.Default.Parse<Struct>(rowJson);

var decision = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Read);
if (decision.Denied ||
    (decision.OwnershipRequired &&
     StructFieldAccess.GetFieldString(entityStruct, decision.OwnerFieldName!) != decision.OwnerValue))
{
    return new MappingResponse
    {
        Success = false,
        Error   = $"'{request.TypeName}:{request.Key}' not found.",
        TraceId = request.TraceId
    };
}

AuthorizationFieldMasking.MaskDisallowedFields(entityStruct, decision.AllowedFields);

if (request.Depth > 0)
    await ResolveRelationsAsync(entityStruct, schema, request.Depth, context.CancellationToken);

return new MappingResponse { Success = true, Data = entityStruct, TraceId = request.TraceId };
```
This is a reordering of the existing method body (the `if (request.Depth > 0)` and `return` lines are unchanged, just now positioned after the new check/mask). Needs `using Iverson.Api.Authorization;`.

- [ ] **Step 2: Apply the same check + mask inside all 3 relation resolvers**
`ResolveSingleRelationAsync` (`:352-369`) — check before recursing (a denied related row's own relations are never worth fetching), `return;` on denial to leave the field absent from the parent:
```csharp
var relatedStruct = JsonParser.Default.Parse<Struct>(rowJson);

var decision = _authEvaluator.Evaluate(relatedSchema, _actingUserAccessor.ActingUser, AuthorizationAction.Read);
if (decision.Denied ||
    (decision.OwnershipRequired &&
     StructFieldAccess.GetFieldString(relatedStruct, decision.OwnerFieldName!) != decision.OwnerValue))
    return;
AuthorizationFieldMasking.MaskDisallowedFields(relatedStruct, decision.AllowedFields);

if (depth > 1)
    await ResolveRelationsAsync(relatedStruct, relatedSchema, depth - 1, ct);

entityStruct.Fields[relation.PropertyName] = Value.ForStruct(relatedStruct);
```
`ResolveManyToManyAsync` (`:371-398`) — the decision doesn't vary per item (same `relatedSchema`/`ActingUser`/action every iteration, matching §3's "evaluate once" reasoning for `GetMany`), so hoist it above the loop; each item's ownership match still varies per row:
```csharp
var rows = await _entities.FetchManyByKeysAsync(SchemaBuilder.ToTableSchema(relatedSchema), ids);
var rowsByKey = rows.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

var decision = _authEvaluator.Evaluate(relatedSchema, _actingUserAccessor.ActingUser, AuthorizationAction.Read);

var items = new List<Value>();
foreach (var id in ids)
{
    if (ct.IsCancellationRequested) break;
    if (!rowsByKey.TryGetValue(id, out var row)) continue;
    var relatedStruct = JsonParser.Default.Parse<Struct>(row.Data);

    if (decision.Denied ||
        (decision.OwnershipRequired &&
         StructFieldAccess.GetFieldString(relatedStruct, decision.OwnerFieldName!) != decision.OwnerValue))
        continue;
    AuthorizationFieldMasking.MaskDisallowedFields(relatedStruct, decision.AllowedFields);

    if (depth > 1)
        await ResolveRelationsAsync(relatedStruct, relatedSchema, depth - 1, ct);
    items.Add(Value.ForStruct(relatedStruct));
}

entityStruct.Fields[relation.PropertyName] = Value.ForList(items.ToArray());
```
`ResolveOneToManyAsync` (`:400-423`) — same hoisted-decision pattern as `ResolveManyToManyAsync`, applied to its `foreach (var rowJson in rows)` loop instead (decision hoisted above that loop, `continue` on denial inside it, same mask-then-recurse-then-add body).

- [ ] **Step 3: Add unit tests**
In `ObjectMappingGrpcServiceTests.cs`, add tests covering: denied (schema with `Authorization = null` — a locally-built schema, not a `SchemaFixtures` one), denied (bypass `ActingUser` swapped for `null`), owner-match allowed, bypass-role allowed, non-owner denied, restricted-field-absent-from-read-response, and recursive traversal with one allowed and one denied related entity in the same `Get` call (denied one omitted, rest present) — per the spec's Testing plan. Each test builds its own local `SchemaDescriptor` (cloning a `SchemaFixtures` fixture via `with { Authorization = ... }` where convenient) rather than mutating the shared bypass fixtures.

- [ ] **Step 4: Run tests and commit**
```bash
cd Iverson.Server
dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~ObjectMappingGrpcServiceTests"
git add Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs
git commit -m "feat(api): enforce row/field authorization on ObjectMappingGrpcService.Get, incl. recursive relations"
```

### Task 3: `ObjectRetrievalGrpcService.Get` / `GetMany` enforcement

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs:20-78`
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs`

**Interfaces:**
- Consumes: `AuthorizationFieldMasking.MaskDisallowedFields`, `_actingUserAccessor`/`_authEvaluator` (Task 1)

- [ ] **Step 1: Enforce in `Get`**
After the existing `if (rowJson is null) return ...;` check (`:31-32`) and before constructing the success response, parse the struct, evaluate `Evaluate(schema, actingUserAccessor.ActingUser, AuthorizationAction.Read)`, and on `Denied` or ownership mismatch return `new RetrievalResponse { Found = false, TraceId = request.TraceId }` — same shape as the existing not-found path. Otherwise mask fields via `AuthorizationFieldMasking.MaskDisallowedFields` before returning `Found = true`.

- [ ] **Step 2: Enforce in `GetMany`**
After `var schema = registry.Get(request.TypeName);`'s null-check (`:50-58`, unchanged) and before the streaming loop, call `Evaluate` **once**: `var decision = authEvaluator.Evaluate(schema, actingUserAccessor.ActingUser, AuthorizationAction.Read);`. Inside the existing `foreach (var key in keys)` loop (`:65-77`): if `decision.Denied`, stream `Found = false` for every key (same as the schema-not-registered path) — the loop can short-circuit to the not-found branch for all keys without calling `_entities.FetchManyByKeysAsync` at all if `Denied`, mirroring `Get`'s not-found reuse. Otherwise, for each row found, check ownership (`decision.OwnershipRequired` against that row's own owner-field value) per-row, mask via `MaskDisallowedFields`, then stream.

- [ ] **Step 3: Add unit tests**
Per the spec's Testing plan: denied (no rules), denied (no identity), owner-match allowed, bypass-role allowed, non-owner denied, restricted-field-absent-from-read-response — for both `Get` and `GetMany`.

- [ ] **Step 4: Run tests and commit**
```bash
cd Iverson.Server
dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~ObjectRetrievalGrpcServiceTests"
git add Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs
git commit -m "feat(api): enforce row/field authorization on ObjectRetrievalGrpcService.Get/GetMany"
```

### Task 4: `Post` enforcement (both services)

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs:124-184`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs:27-91`
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs`

**Interfaces:**
- Consumes: `AuthorizationFieldMasking.RejectDisallowedFields`, `_actingUserAccessor`/`_authEvaluator` (Task 1)

- [ ] **Step 1: `ObjectMappingGrpcService.Post`**
After `var schema = RequireSchema(request.TypeName);` (`:130`) and before `_relationValidator.ValidateRelations(...)` (`:132`):
```csharp
var decision = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Write);
if (decision.Denied)
    throw new RpcException(new Status(StatusCode.PermissionDenied, "Not authorized to create this entity."));

if (decision.OwnershipRequired)
    request.Payload.Fields[decision.OwnerFieldName!] = Value.ForString(decision.OwnerValue!);

AuthorizationFieldMasking.RejectDisallowedFields(request.Payload, decision.AllowedFields, exemptField: decision.OwnerFieldName);
```
The owner-field force-set (`decision.OwnershipRequired == true` branch) uses the same direct `Struct.Fields[...] = ...` mutation pattern already used elsewhere in this file (e.g. line 368) — it both handles the case where the client omitted the field and overwrites whatever the client sent, per spec §4 step 2. When `decision.OwnershipRequired == false` (bypass), that assignment is skipped entirely — the payload is left exactly as the client sent it.

- [ ] **Step 2: `ObjectPersistenceGrpcService.Post`**
Same shape as Step 1, inserted after `var schema = RequireSchema(request.TypeName);` (`:30`) and before `relationValidator.ValidateRelations(...)` (`:32`). Uses this file's unprefixed field-name convention (`entities`, `actingUserAccessor`, `authEvaluator` — no leading underscore, matching this file's existing `events`, `registry`, etc.).

- [ ] **Step 3: Add unit tests**
Per the spec's Testing plan, for both services: denied (no rules) → `PermissionDenied`; denied (no identity) → `PermissionDenied`; owner-field force-set for ordinary caller (payload's owner field ends up as the acting user's `sub` regardless of what the client sent, including when absent); bypass-role caller's payload owner field left untouched (including client-omitted); restricted-field-in-write-payload rejected → `InvalidArgument` (this is `RejectDisallowedFields`'s own status code, per its §1 signature — distinct from the `decision.Denied` case's `PermissionDenied`, per the Global Constraints). Assert the exact status code in each test.

- [ ] **Step 4: Run tests and commit**
```bash
cd Iverson.Server
dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~ObjectMappingGrpcServiceTests|FullyQualifiedName~ObjectPersistenceGrpcServiceTests"
git add Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs
git commit -m "feat(api): enforce row/field authorization on Post (ObjectMapping + ObjectPersistence)"
```

### Task 5: `Delete` enforcement (`ObjectMappingGrpcService` only)

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs:246-312`
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`

**Interfaces:**
- Consumes: `_actingUserAccessor`/`_authEvaluator` (Task 1). No field-level dimension (spec §6: `AllowedFields` is always `null` for `Delete`) — neither masking nor rejection helper is used here.

- [ ] **Step 1: Enforce using the existing pre-fetch**
In `Delete` (`ObjectMappingGrpcService.cs:246-312`), after the existing not-found check (`:253-260`) and before `var targetStores = ...` (`:262`):
```csharp
var decision = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Delete);
if (decision.Denied ||
    (decision.OwnershipRequired &&
     StructFieldAccess.GetFieldString(JsonParser.Default.Parse<Struct>(rowJson), decision.OwnerFieldName!) != decision.OwnerValue))
{
    return new MappingDeleteResponse
    {
        Success = false,
        Error   = $"'{request.TypeName}:{request.Key}' not found.",
        TraceId = request.TraceId
    };
}
```
Reuses the already-fetched `rowJson` — no new read.

- [ ] **Step 2: Add unit tests**
Per the spec's Testing plan: denied (no rules), denied (no identity), owner-match allowed, bypass-role allowed, non-owner denied — mirroring `Delete_WhenEntityNotFound_ReturnsFailureWithoutEmittingEvent`'s existing shape (same not-found response, same "no event emitted" assertion).

- [ ] **Step 3: Run tests and commit**
```bash
cd Iverson.Server
dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~ObjectMappingGrpcServiceTests"
git add Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs
git commit -m "feat(api): enforce row-level authorization on ObjectMappingGrpcService.Delete"
```

### Task 6: `Update` enforcement (both services)

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs:186-244`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs:93-158`
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs`

**Interfaces:**
- Consumes: `AuthorizationFieldMasking.RejectDisallowedFields`, `_actingUserAccessor`/`_authEvaluator` (Task 1); `IEntityRepository` on `ObjectPersistenceGrpcService` (Task 1).

- [ ] **Step 1: `ObjectMappingGrpcService.Update` — new pre-fetch + branch**
After the existing key extraction (`:194-197`) and before `_relationValidator.ValidateRelations(...)` (`:199`):
```csharp
var existingRowJson = await FetchByKeyAsync(schema, key);
var decision = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Write);
if (decision.Denied)
    throw new RpcException(new Status(StatusCode.PermissionDenied, "Not authorized to update this entity."));

if (existingRowJson is null)
{
    // Row doesn't exist yet — the UPSERT will create it. Same owner-field bypass/force-set
    // logic as Post (Task 4): force-set for ownership-required callers, leave untouched for bypass.
    if (decision.OwnershipRequired)
        request.Payload.Fields[decision.OwnerFieldName!] = Value.ForString(decision.OwnerValue!);
}
else
{
    var existingStruct = JsonParser.Default.Parse<Struct>(existingRowJson);
    if (decision.OwnershipRequired &&
        StructFieldAccess.GetFieldString(existingStruct, decision.OwnerFieldName!) != decision.OwnerValue)
        throw new RpcException(new Status(StatusCode.PermissionDenied, "Not authorized to update this entity."));

    var ownerFieldName = schema.Authorization?.OwnerField;
    if (!string.IsNullOrEmpty(ownerFieldName))
    {
        var attemptedOwnerValue = StructFieldAccess.GetFieldString(request.Payload, ownerFieldName);
        if (attemptedOwnerValue is not null &&
            attemptedOwnerValue != StructFieldAccess.GetFieldString(existingStruct, ownerFieldName))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Owner field is immutable after creation."));
    }
}
AuthorizationFieldMasking.RejectDisallowedFields(request.Payload, decision.AllowedFields, exemptField: decision.OwnerFieldName);
```

- [ ] **Step 2: `ObjectPersistenceGrpcService.Update` — same shape, plus new `IEntityRepository` dependency**
Same logic as Step 1, using the newly-injected `entities` field (Task 1) and this file's unprefixed naming convention: `var existingRowJson = await entities.FetchByKeyAsync(SchemaBuilder.ToTableSchema(schema), key);` (this service has no `FetchByKeyAsync` private wrapper like `ObjectMappingGrpcService` does — call `SchemaBuilder.ToTableSchema` directly, matching this file's existing usage at lines 46, 113).

- [ ] **Step 3: Add unit tests**
Per the spec's Testing plan, for both services: denied (no rules), denied (no identity), owner-match allowed, bypass-role allowed, non-owner denied, restricted-field-in-write-payload rejected, row-doesn't-exist-yet (create-path owner logic applies — payload owner field force-set for ordinary caller, untouched for bypass), ownership-immutability rejection with a non-bypass caller attempting to change the owner field, and ownership-immutability rejection with a **bypass**-role caller attempting to change the owner field (this is the case the spec's CDR fix specifically targeted — must use `schema.Authorization?.OwnerField`, not `decision.OwnerFieldName`, since the latter is `null` for bypass callers).

- [ ] **Step 4: Run tests and commit**
```bash
cd Iverson.Server
dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~ObjectMappingGrpcServiceTests|FullyQualifiedName~ObjectPersistenceGrpcServiceTests"
git add Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs
git commit -m "feat(api): enforce row/field authorization on Update (ObjectMapping + ObjectPersistence)"
```

## Tasks NOT in this plan

- StarRocks/Qdrant enforcement (5c/5d — separate plans).
- Any UI/tooling for authoring `AuthorizationRules` — schema registration remains a raw proto call, unchanged from 5a.
- Adding a `Delete` RPC to `ObjectPersistenceGrpcService` — it doesn't have one today and this plan doesn't add one.
- Migrating or backfilling `AuthorizationRules` onto any existing registered schema.

## Known issues inherited from spec

Two consequences flagged as accepted-in-principle during 5a's brainstorm now become live behavior once this plan lands:
- Every schema with no `AuthorizationRules` configured will reject every Post/Update/Delete/Get call — this affects every currently-registered schema in the system, since none has `AuthorizationRules` configured yet.
- Every call with no acting-user identity (`x-acting-user-authorization` header absent) to a rules-configured schema will also be rejected.

Neither is fixed by this plan; migrating/configuring existing schemas remains explicitly out of scope (see the spec's Non-goals).
