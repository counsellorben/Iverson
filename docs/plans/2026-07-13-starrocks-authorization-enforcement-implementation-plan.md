# StarRocks Authorization Enforcement (Part 5c) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/superpowers/specs/2026-07-13-starrocks-authorization-enforcement-design.md` (commit SHA: `690d661`)

**Goal:** Wire the existing `IRowFieldAuthorizationEvaluator` into `ObjectSearchGrpcService`'s StarRocks-backed RPCs (`Search`, `Aggregate`, `GroupBy`, `Pipeline`), enforcing row-level ownership and field-level restrictions on every query these RPCs can generate, including joined types.

**Architecture:** A new decoupled `AuthorizationConstraint` record crosses the `Iverson.Api`/`Iverson.StarRocks` boundary as plain data (computed once per involved type from the existing evaluator). Row-level ownership is enforced as a wrap-and-AND SQL predicate (primary type) or an `ON`-clause-appended predicate (joined types, to avoid collapsing outer joins). Field-level restriction is enforced by post-fetch masking (`Search`, and Pipeline's implicit-passthrough safety net) or reject-on-reference (`Aggregate`/`GroupBy`/`Pipeline`'s explicit constructs, including `Expression` fields via a shared tokenizer).

**Tech stack:** C# / .NET 10, gRPC, Dapper + MySqlConnector (StarRocks wire protocol), xUnit + NSubstitute + FluentAssertions.

---

## Global Constraints

- All row-level ownership predicates are appended via wrap-and-AND on the primary type's own `WHERE`, or via an additional `AND` condition on a joined type's `ON` clause — **never** appended to the outer `WHERE` for a joined type, to avoid silently collapsing `LEFT`/`RIGHT`/`FULL` joins into `INNER` joins.
- All field-level rejections throw `RpcException(StatusCode.InvalidArgument, ...)` via the existing `StarRocksQueryTranslationException` → `InvalidArgument` mapping already in place in `ObjectSearchGrpcService`'s try/catch blocks — no new exception type.
- Primary-type denial (no `AuthorizationRules`, or no acting-user identity) is empty-result, never an exception: `Search`/`GroupBy`/`Pipeline` close their stream having written zero rows; `Aggregate` returns `AggregateResponse { Results = { } }`.
- Joined-type denial rejects the whole call: `RpcException(InvalidArgument, ...)` naming the joined type.
- The `Expression`-token check (wherever it applies — `AggregationDescriptor.Expression`, `MetricSpec.Expression` in both `GroupBy` and `Pipeline`) reuses the exact tokenize-and-whitelist pattern `StarRocksPipelineBuilder.ValidateDeriveExpr` already uses for `derive` — not a new, divergent implementation.
- `Iverson.StarRocks` gets zero new dependency on `Iverson.Api.Authorization`/`Iverson.Api.Schema`. Only `AuthorizationConstraint` (a plain record) and `IReadOnlyDictionary<string, AuthorizationConstraint>?` cross the boundary.

## File Structure

- Create: `Iverson.Server/Iverson.StarRocks/AuthorizationConstraint.cs` — the new decoupled record
- Modify: `Iverson.Server/Iverson.StarRocks/IEngagementStoreRoles.cs` — `IEngagementStoreSearchService`'s 4 method signatures gain `authz`
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksRepository.cs` — thread `authz` through to the builders
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs` — ownership (primary + joined), filter-clause field check, `Aggregate`/`GroupBy`'s field reject-on-reference + `Expression` check
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs` — ownership (`baseWhere` + per-step join `ON`), column-introduction filtering, `all: true` scoping, its own `Expression` check, Layer 2 masking; `TokenRx`/`DeriveWhitelist` become `internal`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` — DI wiring, per-type constraint computation, denied-caller handling, `Search`'s post-fetch masking, per-RPC `authz` threading
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` — constructor + mock-setup updates, new test cases per task

## Inherited from spec

The following were verified by `thorough-brainstorming` at spec-write time (see the spec's own `Verified assumptions` table, items 1–18) and are **not** re-verified here:
- `Iverson.StarRocks` has no project reference to `Iverson.Api` (item 1)
- `BuildSelectColumns`'s joined branch resolves only against the primary schema (item 2)
- `JoinSpec`/`PipelineJoin` support `LEFT`/`RIGHT`/`FULL`/`INNER` (item 3)
- `BuildFromWithJoins`'s and `EmitStep`'s per-join `ON` clauses are single, extensible strings (items 4, 5)
- `IRowFieldAuthorizationEvaluator`/`IActingUserAccessor` are DI-registered and reusable as-is (items 6, 7)
- Only one non-DI construction site of `ObjectSearchGrpcService` exists (item 8) — see also this plan's own Cat-6 finding below, which extends this to the test file's mock setups
- `SchemaRegistry.Get` returns the full `SchemaDescriptor` for any registered type (item 9)
- `TrackAndValidate` performs a full pre-SQL pass over every column reference (item 10)
- `StarRocksQueryTranslationException` → `InvalidArgument` mapping exists in all 4 RPC methods (item 11)
- `Aggregate`/`GroupBy` field references resolve via `ResolveColumn(tableMap, ...)` (item 12)
- Existing `ObjectSearchGrpcServiceTests.cs` schemas already carry bypass `AuthorizationRules` (item 13)
- Every WHERE/HAVING-shaped surface across all 4 RPCs is accounted for (item 14)
- `AggregationSpec.Expression`/`MetricSpec.Expression` are ordinary public proto fields, not trust-boundary-enforced (item 15)
- `StepColumns` has no per-column type/origin tag beyond introduction (item 16)
- The last step's `StepColumns.Columns` (`lastCols`) is exactly the set of currently-allowed output names (item 17)
- `ValidateStepAndComputeOutput`'s metrics loop has no existing validation hook for `MetricSpec.Expression` (item 18)

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | `Iverson.Server/Iverson.StarRocks/AuthorizationConstraint.cs` does not exist; new small single-purpose files are the established convention (`Aggregation.cs`, `StarRocksQuerySchema.cs`, etc.) | `ls Iverson.Server/Iverson.StarRocks/*.cs`; read `Aggregation.cs` as a precedent (one concern, `sealed record`s, no class wrapper) |
| 2 | Function signature | `StarRocksRepository`'s 4 methods (`SearchAsync`/`AggregateAsync`/`GroupByAsync`/`PipelineAsync`) are thin wrappers: build SQL via the matching `StarRocksQueryBuilder`/`StarRocksPipelineBuilder` call, then `QueryAsync<dynamic>` | Read `StarRocksRepository.cs:147-221` |
| 3 | Function signature | `Iverson.StarRocks.AggregationDescriptor` (internal record) has `Field`/`Expression`/`GroupByFields` properties, translated from the proto `AggregationSpec` by `ObjectSearchGrpcService.ProtoToSrSpec` — the plan's Aggregate-task checks operate on this internal type, not the proto type directly | Read `Aggregation.cs:14-23` and `ObjectSearchGrpcService.cs`'s `ProtoToSrSpec` (lines ~390-402) |
| 4 | Function signature | `MetricSpec` (proto-generated) has `Name`/`Type`/`Field`/`Expression` C# properties, used identically by `GroupByRequest.Metrics` and `PipelineStep.Metrics` | Read `object_search.proto:215-220`; confirmed via existing usage in `StarRocksQueryBuilder.BuildMetricExpr` and `StarRocksPipelineBuilder`'s metrics loop |
| 5 | Function signature | `JoinSpec` has `LeftType`/`RightType`/`LeftField`/`RightField`/`Kind` C# properties (proto `left_type` etc. → PascalCase) | Confirmed via existing usage in `StarRocksQueryBuilder.BuildFromWithJoins` |
| 6 | Function signature | `ObjectSearchGrpcService` uses C# primary-constructor syntax with unprefixed parameter names (`registry`, `search`, `vector`, `embedding`, `logger`) — new params (`actingUserAccessor`, `authEvaluator`) must follow the same unprefixed convention | Read `ObjectSearchGrpcService.cs:27-33` |
| 7 | Function signature | `IActingUserAccessor.ActingUser` is `ClaimsPrincipal? { get; set; }`; `IRowFieldAuthorizationEvaluator.Evaluate(SchemaDescriptor, ClaimsPrincipal?, AuthorizationAction)` returns `AuthorizationDecision(bool Denied, bool OwnershipRequired, string? OwnerFieldName, string? OwnerValue, IReadOnlySet<string>? AllowedFields)` | Read `IActingUserAccessor.cs`, `IRowFieldAuthorizationEvaluator.cs` |
| 8 | Test/build command | `cd Iverson.Server && dotnet test Iverson.Api.Tests` / `dotnet test Iverson.StarRocks.Tests` are the working invocations (used successfully earlier this session) | Prior successful runs in this session |
| 9 | Task ordering | Task 1's filter-clause field check lives in the `BuildWhere(StarRocksQuerySchema schema, ..., tableMap=null)` overload's `resolve` closure construction — **not** in the shared core `BuildWhere(Func<string,string?> resolveQuoted, ...)` loop that Pipeline's `baseWhere` calls directly. Adding a new parameter to the schema/tableMap overload cannot affect Pipeline's direct call to the core overload | Read `StarRocksQueryBuilder.cs`'s two `BuildWhere` overloads (lines ~272-336) and `StarRocksPipelineBuilder.Build`'s direct core-overload call (line ~313) |
| 10 | Task ordering | Tasks 2-5 touch non-overlapping method bodies: `Search`/`BuildSearch`, `Aggregate`+`RunAggregationAsync`/`BuildAggregate`, `GroupBy`/`BuildGroupBy`, `Pipeline`/`StarRocksPipelineBuilder` (a separate file) | Read `ObjectSearchGrpcService.cs` in full; confirmed each RPC method is a distinct, non-overlapping region |
| 11 | Consumer impact (Cat 6) | `IEngagementStoreSearchService` has exactly 3 non-implementation references besides `StarRocksRepository`/`ServiceCollectionExtensions`: `ObjectSearchGrpcService.cs` (real caller), `ObjectSearchGrpcServiceTests.cs` (mocks all 4 methods with explicit `Arg.Any<T>()` argument lists matching the *current* parameter count), and `ObjectSearchVectorIntegrationTests.cs` (uses an unconfigured `Substitute.For<IEngagementStoreSearchService>()` with no per-method setup) | `grep -rln "IEngagementStoreSearchService"` across the repo |
| 12 | Consumer impact (Cat 6) | Adding a new optional 8th parameter to the 4 interface methods will NOT be matched by `ObjectSearchGrpcServiceTests.cs`'s existing `_search.SearchAsync(Arg.Any<A>(), ..., Arg.Any<G>())`-style setups (7 explicit `Arg.Any<T>()` calls) once a real caller passes a non-null `authz` value — NSubstitute's argument matching is positional and exact-count; the omitted 8th argument defaults to a literal `null` at the setup call site, so the existing setups will only match calls where `authz == null`. `ObjectSearchVectorIntegrationTests.cs`'s unconfigured substitute is unaffected (no explicit setup to break) | Read `ObjectSearchGrpcServiceTests.cs:38-53`'s 4 mock setups; read `ObjectSearchVectorIntegrationTests.cs:64` (bare `Substitute.For<...>()`, no method configuration) |
| 13 | Consumer impact (Cat 6) | `ColumnsFor` has exactly 2 call sites: `TrackAndValidate`'s `baseColumns` and `ResolveJoinSources`'s fresh-join-source branch — both are the two "introduction points" the spec already identifies; no third caller exists that the column-introduction filtering could miss | `grep -n "ColumnsFor("` across `Iverson.StarRocks` |
| 14 | Consumer impact (Cat 6) | `TokenRx`/`DeriveWhitelist` (currently `private static readonly` in `StarRocksPipelineBuilder`) have no references anywhere outside that file — safe to widen to `internal` with no naming collision or unintended-exposure risk | `grep -rn "TokenRx\|DeriveWhitelist"` across `Iverson.Server` |
| 15 | Commit convention | `feat(starrocks): ...` / `feat(api): ...` / `test(starrocks): ...` prefixes are the established convention for this project | `git log --oneline` — recent commits use this shape; `git log --oneline --all \| grep "starrocks)"` confirms the `starrocks` scope specifically |
| 16 | Task ordering | `BuildGroupBy` declares `param` *after* its `BuildFromWithJoins` call (unlike `BuildSearch`/`BuildAggregate`, which declare it before) — Task 4 must reorder these two lines before threading `param` into `BuildFromWithJoins`, or the code doesn't compile | Read `StarRocksQueryBuilder.cs:190-192` (`BuildFromWithJoins` call) vs. `:99` / `:30` (`param` declared first in `BuildAggregate`/`BuildSearch`) — caught during this plan's self-review pass |
| 17 | Function signature | Task 1 Step 4's alias-to-owning-type resolution is a shared `StarRocksQueryBuilder.IsFieldAllowed(...)` helper (not an inline closure private to `BuildWhere`), callable directly by `BuildAggregate`/`BuildGroupBy`/`BuildMetricExpr` (Tasks 3/4), none of which call `BuildWhere` themselves | Traced `BuildGroupBy`'s `keyCols` (`ResolveColumn(tableMap, k)`, `StarRocksQueryBuilder.cs:196-200`) and `BuildMetricExpr`'s `metric.Field` branch (`ResolveColumn(tableMap, metric.Field)`, `:258`) — both produce `"alias.column"` strings needing the same type-resolution `IsFieldAllowed` now provides. (Resolved via CIR round 1 §2.1 — the original inline closure had no callable form for Tasks 3/4 to reuse.) |

## Tasks

### Task 1: Shared infrastructure — `AuthorizationConstraint`, interface/repository threading, shared filter-clause check, shared `Expression` tokenizer access, grpc-service DI wiring

**Files:**
- Create: `Iverson.Server/Iverson.StarRocks/AuthorizationConstraint.cs`
- Modify: `Iverson.Server/Iverson.StarRocks/IEngagementStoreRoles.cs`
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksRepository.cs`
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs` (the shared `BuildWhere` overload only — not `BuildSearch`/`BuildAggregate`/`BuildGroupBy` themselves)
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs` (widen `TokenRx`/`DeriveWhitelist` to `internal` only — no behavior change)
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` (constructor + new private helper only — no RPC-method behavior change yet)
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

**Interfaces:**
- Produces: `AuthorizationConstraint` record; `IEngagementStoreSearchService`'s widened signatures; a private `ObjectSearchGrpcService` helper that computes per-type constraints and detects denial, consumed by Tasks 2-5
- Consumes: existing `IRowFieldAuthorizationEvaluator`/`IActingUserAccessor` (Part 5a/5b), existing `SchemaRegistry`

- [ ] **Step 1: `AuthorizationConstraint` record**

`Iverson.Server/Iverson.StarRocks/AuthorizationConstraint.cs`:
```csharp
namespace Iverson.StarRocks;

public sealed record AuthorizationConstraint(
    IReadOnlySet<string>? AllowedFields,   // null = unrestricted
    string? OwnerColumn,                    // null = no ownership predicate needed
    string? OwnerValue);
```

- [ ] **Step 2: Widen `IEngagementStoreSearchService`'s 4 methods**

In `IEngagementStoreRoles.cs`, each of `SearchAsync`/`AggregateAsync`/`GroupByAsync`/`PipelineAsync` gains one trailing optional parameter: `IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null`.

- [ ] **Step 3: Thread `authz` through `StarRocksRepository`**

Each of the 4 methods in `StarRocksRepository.cs` gains the same trailing `authz` parameter and passes it straight into its `StarRocksQueryBuilder`/`StarRocksPipelineBuilder` call. To keep Task 1 self-contained and independently buildable, also give `BuildSearch`/`BuildAggregate`/`BuildGroupBy`/`StarRocksPipelineBuilder.Build` each a trailing `IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null` parameter here — declared but unused until Tasks 2-5 implement behavior against it, rather than each of those tasks also needing to add the parameter itself.

- [ ] **Step 4: Filter-clause field check in the shared `BuildWhere` overload**

In `StarRocksQueryBuilder.cs`, add a shared helper that resolves a column's owning type and checks it against that type's `AllowedFields` in one call — Tasks 3 and 4 call this same helper directly for their own field-checks, rather than each reimplementing the alias-to-type-name resolution:
```csharp
internal static bool IsFieldAllowed(
    string resolvedColumnOrAliasDotColumn,
    StarRocksQuerySchema schema,
    IReadOnlyDictionary<string, JoinContext>? tableMap,
    IReadOnlyDictionary<string, AuthorizationConstraint>? authz,
    out string typeName)
{
    var dotIdx = tableMap is null ? -1 : resolvedColumnOrAliasDotColumn.LastIndexOf('.');
    typeName = dotIdx < 0
        ? schema.TypeName
        : tableMap!.Values.First(v => v.Alias == resolvedColumnOrAliasDotColumn[..dotIdx]).Schema.TypeName;
    var bareField = dotIdx < 0 ? resolvedColumnOrAliasDotColumn : resolvedColumnOrAliasDotColumn[(dotIdx + 1)..];
    return authz is null || !authz.TryGetValue(typeName, out var constraint) || constraint.AllowedFields is null
        || constraint.AllowedFields.Contains(bareField);
}
```
The `BuildWhere(StarRocksQuerySchema schema, IEnumerable<SearchClause>? clauses, SearchLogic logic, DynamicParameters param, out int nextIdx, IReadOnlyDictionary<string, JoinContext>? tableMap = null)` overload gains a trailing `IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null` parameter. Its `resolve` closure construction becomes:
```csharp
Func<string, string?> resolve = tableMap is not null
    ? p =>
    {
        if (ResolveColumn(tableMap, p) is not { } qc) return null;
        if (!IsFieldAllowed(qc, schema, tableMap, authz, out var typeName))
            throw new StarRocksQueryTranslationException($"Filter property '{p}' on '{typeName}' is not authorized for this caller.");
        return QuoteQualified(qc);
    }
    : p =>
    {
        if (ResolveColumn(schema, p) is not { } c) return null;
        if (!IsFieldAllowed(c, schema, null, authz, out var typeName))
            throw new StarRocksQueryTranslationException($"Filter property '{p}' on '{typeName}' is not authorized for this caller.");
        return $"`{c}`";
    };
return BuildWhere(resolve, clauses, logic, param, "p", out nextIdx);
```
This overload is called identically by `BuildSearch`/`BuildAggregate`/`BuildGroupBy` — none of those 3 need their own filter-clause-check code as a result. Pipeline's `baseWhere` calls the *other*, core `BuildWhere(Func<string,string?> resolveQuoted, ...)` overload directly and is unaffected by this change (per Verified plan-level assumption #9).

- [ ] **Step 5: Widen `TokenRx`/`DeriveWhitelist` to `internal`**

In `StarRocksPipelineBuilder.cs`, change `private static readonly Regex TokenRx` and `private static readonly HashSet<string> DeriveWhitelist` to `internal static readonly`. No other change in this file for this step — Tasks 3/4/5 will consume these from `StarRocksQueryBuilder`/`StarRocksPipelineBuilder` itself.

- [ ] **Step 6: `ObjectSearchGrpcService` DI wiring + constraint-computation helper**

Constructor gains 2 trailing parameters, matching this file's existing unprefixed primary-constructor convention:
```csharp
public sealed class ObjectSearchGrpcService(
    SchemaRegistry registry,
    IEngagementStoreSearchService search,
    IVectorQueryService vector,
    IEmbeddingService embedding,
    ILogger<ObjectSearchGrpcService> logger,
    IActingUserAccessor actingUserAccessor,
    IRowFieldAuthorizationEvaluator authEvaluator)
    : ObjectSearchService.ObjectSearchServiceBase
```
New private helper (consumed by Tasks 2-5; not yet called from any RPC method in this task):
```csharp
private sealed record AuthzResult(
    bool PrimaryDenied,
    string? DeniedJoinedType,
    IReadOnlyDictionary<string, AuthorizationConstraint> Constraints);

private AuthzResult EvaluateAuthorization(SchemaDescriptor primary, IEnumerable<string> joinedTypeNames)
{
    var constraints = new Dictionary<string, AuthorizationConstraint>();

    var primaryDecision = authEvaluator.Evaluate(primary, actingUserAccessor.ActingUser, AuthorizationAction.Read);
    if (primaryDecision.Denied)
        return new AuthzResult(true, null, constraints);
    constraints[primary.TypeName] = new AuthorizationConstraint(
        primaryDecision.AllowedFields, primaryDecision.OwnerFieldName, primaryDecision.OwnerValue);

    foreach (var typeName in joinedTypeNames.Distinct().Where(t => t != primary.TypeName))
    {
        var joinedSchema = registry.Get(typeName)
            ?? throw new RpcException(new Status(StatusCode.FailedPrecondition, $"No schema registered for '{typeName}'."));
        var decision = authEvaluator.Evaluate(joinedSchema, actingUserAccessor.ActingUser, AuthorizationAction.Read);
        if (decision.Denied)
            return new AuthzResult(false, typeName, constraints);
        constraints[typeName] = new AuthorizationConstraint(
            decision.AllowedFields, decision.OwnerFieldName, decision.OwnerValue);
    }

    return new AuthzResult(false, null, constraints);
}
```

- [ ] **Step 7: Update test fixtures**

In `ObjectSearchGrpcServiceTests.cs`:
- Add `private readonly IActingUserAccessor _actingUserAccessor;` and `private readonly IRowFieldAuthorizationEvaluator _authEvaluator = new RowFieldAuthorizationEvaluator();` fields, matching `ObjectMappingGrpcServiceTests.cs`'s exact pattern.
- In the constructor, add `_actingUserAccessor = new ActingUserAccessor { ActingUser = ActingUserFixtures.Principal("test-user", "test-bypass") };` and pass `_actingUserAccessor, _authEvaluator` as the 2 new trailing constructor arguments to `_sut = new ObjectSearchGrpcService(...)`.
- Update all 4 existing `_search.SearchAsync(...)`/`AggregateAsync(...)`/`GroupByAsync(...)`/`PipelineAsync(...)` mock setups to add a trailing `Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>()` argument, matching the new signatures (per Verified plan-level assumption #12 — without this, these setups stop matching once Tasks 2-5 make real calls pass non-null `authz`).
- Add `using Iverson.Api.Authorization;` for `IRowFieldAuthorizationEvaluator`/`RowFieldAuthorizationEvaluator`/`AuthorizationAction`.

- [ ] **Step 8: Run tests and commit**
```bash
cd Iverson.Server
dotnet build Iverson.StarRocks
dotnet build Iverson.Api
dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~ObjectSearchGrpcServiceTests"
git add Iverson.Server/Iverson.StarRocks/AuthorizationConstraint.cs Iverson.Server/Iverson.StarRocks/IEngagementStoreRoles.cs Iverson.Server/Iverson.StarRocks/StarRocksRepository.cs Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs
git commit -m "feat(starrocks): wire authorization evaluator DI + shared filter-clause check into StarRocks search services"
```

### Task 2: `Search` enforcement

**Files:**
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs:20-66` (`BuildSearch`)
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:37-79` (`Search`)
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

**Interfaces:**
- Consumes: `AuthorizationConstraint` (Task 1), `EvaluateAuthorization` (Task 1)

- [ ] **Step 1: `BuildSearch` — primary-type ownership**

After `where = BuildWhere(...)` is computed in both the join and no-join branches, wrap it:
```csharp
if (authz is not null && authz.TryGetValue(schema.TypeName, out var primaryConstraint) && primaryConstraint.OwnerColumn is not null)
{
    var ownerPredicate = $"`{primaryConstraint.OwnerColumn}` = @__ownerVal";
    param.Add("__ownerVal", primaryConstraint.OwnerValue);
    where = where.Length > 0 ? $"({where}) AND {ownerPredicate}" : ownerPredicate;
}
```
(In the join branch, qualify the owner column with the primary alias: `` `{tableMap[schema.TypeName].Alias}`.`{primaryConstraint.OwnerColumn}` ``.)

- [ ] **Step 2: `BuildFromWithJoins` — joined-type ownership**

Inside the `foreach (var join in joins)` loop, after resolving `rightCtx`, append the joined type's ownership condition to the `ON` clause if `authz` has a constraint for `join.RightType` with a non-null `OwnerColumn`:
```csharp
var ownerCond = "";
if (authz is not null && authz.TryGetValue(join.RightType, out var joinedConstraint) && joinedConstraint.OwnerColumn is not null)
{
    var pName = $"__owner{map.Count}";
    param.Add(pName, joinedConstraint.OwnerValue);
    ownerCond = $" AND `{rightCtx.Alias}`.`{joinedConstraint.OwnerColumn}` = @{pName}";
}
sb.Append(
    $" {kind} JOIN `{rightCtx.TableName}` ON " +
    $"`{leftCtx.Alias}`.`{leftCol}` = `{rightCtx.Alias}`.`{rightCol}`{ownerCond}");
```
This requires `BuildFromWithJoins` to also gain a trailing `param`/`authz` parameter — thread `DynamicParameters param` and `IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null` into its signature, and update `BuildSearch` (this task), `BuildAggregate`/`BuildGroupBy` (Tasks 3/4) to pass their own `param`/`authz` through.

- [ ] **Step 3: `Search`'s post-fetch field masking + denied-caller handling**

In `ObjectSearchGrpcService.Search`, after `var schema = RequireSchema(request.TypeName);`:
```csharp
var joinedTypes = request.Joins.SelectMany(j => new[] { j.LeftType, j.RightType });
var auth = EvaluateAuthorization(schema, joinedTypes);
if (auth.PrimaryDenied)
    return; // empty stream — StarRocks never queried
if (auth.DeniedJoinedType is not null)
    throw new RpcException(new Status(StatusCode.InvalidArgument, $"Not authorized to join '{auth.DeniedJoinedType}'."));
```
Pass `authz: auth.Constraints` into `search.SearchAsync(...)`. In the row-processing loop, after building `dict` and before `DictToProtoStruct(dict)`, mask against the primary type's `AllowedFields`:
```csharp
if (auth.Constraints.TryGetValue(schema.TypeName, out var primaryConstraint) && primaryConstraint.AllowedFields is not null)
    foreach (var key in dict.Keys.Where(k => !primaryConstraint.AllowedFields.Contains(k)).ToList())
        dict.Remove(key);
```

- [ ] **Step 4: Add unit tests**

Per the spec's testing plan (Search-specific slice): denied (no rules)/denied (no identity) → empty stream; owner-match rows included, non-owner excluded; bypass sees all; restricted field masked from response; joined type denied → `InvalidArgument`; joined type ownership-restricted via a `LEFT JOIN` → only matching joined rows contribute, non-matching side nulls out rather than dropping the row (assert via row count / null-side inspection against the underlying mock).

- [ ] **Step 5: Run tests and commit**
```bash
cd Iverson.Server
dotnet test Iverson.StarRocks.Tests
dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~ObjectSearchGrpcServiceTests"
git add Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs
git commit -m "feat(starrocks): enforce row/field authorization on ObjectSearchGrpcService.Search"
```

### Task 3: `Aggregate` enforcement

**Files:**
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs:90-175` (`BuildAggregate`)
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:217-264` (`Aggregate`, `RunAggregationAsync`)
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

**Interfaces:**
- Consumes: `AuthorizationConstraint`/`EvaluateAuthorization` (Task 1), `BuildFromWithJoins`'s new `param`/`authz` parameters (Task 2)

- [ ] **Step 1: `BuildAggregate` — primary-type ownership**

Same wrap-and-AND pattern as Task 2 Step 1, applied to `BuildAggregate`'s own `where` variable (both the `tableMap is not null` and no-join branches).

- [ ] **Step 2: Field reject-on-reference + `Expression` check**

Before building SQL, resolve `spec.Field` (when set) and each entry of `spec.GroupByFields` (when set) the same way the existing `Resolve` local function does (`ResolveColumn(tableMap, ...)` when joined, `ResolveColumn(schema, ...)` otherwise), then call `StarRocksQueryBuilder.IsFieldAllowed(resolvedColumn, schema, tableMap, authz, out var typeName)` (Task 1 Step 4) on each; reject (`StarRocksQueryTranslationException`, naming `typeName` and the field) when it returns `false`. When `spec.Expression` is set instead of `spec.Field`, tokenize it using `StarRocksPipelineBuilder.TokenRx`/`DeriveWhitelist` (now `internal`, per Task 1 Step 5): every identifier token not in `DeriveWhitelist` must resolve to a column (via `ResolveColumn`) that `IsFieldAllowed` also confirms; an unresolvable or disallowed token throws the same way a disallowed `Field` does.

- [ ] **Step 3: Denied-caller handling**

In `Aggregate`, compute `EvaluateAuthorization` once (primary type + joined types from `request.Joins` — `AggregationSpec` entries don't carry their own join info, only the request-level `Joins` does); `PrimaryDenied` → return `AggregateResponse { TraceId = request.TraceId }` (empty `Results`) without dispatching any `RunAggregationAsync` calls; `DeniedJoinedType` → throw `InvalidArgument`. Pass `authz: auth.Constraints` into each `RunAggregationAsync`/`search.AggregateAsync(...)` call.

- [ ] **Step 4: Add unit tests**

Per the spec's testing plan (Aggregate-specific slice): denied → empty `Results`; restricted field via `spec.Field`/`GroupByFields` → `InvalidArgument`; restricted field via `spec.Expression` (instead of `Field`) → `InvalidArgument`, proving the bypass is closed; joined type denied → `InvalidArgument`; ownership-restricted rows correctly filtered.

- [ ] **Step 5: Run tests and commit**
```bash
cd Iverson.Server
dotnet test Iverson.StarRocks.Tests
dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~ObjectSearchGrpcServiceTests"
git add Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs
git commit -m "feat(starrocks): enforce row/field authorization on ObjectSearchGrpcService.Aggregate"
```

### Task 4: `GroupBy` enforcement

**Files:**
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs:184-270` (`BuildGroupBy`, `BuildMetricExpr`)
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:268-307` (`GroupBy`)
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

**Interfaces:**
- Consumes: `AuthorizationConstraint`/`EvaluateAuthorization` (Task 1), `BuildFromWithJoins`'s new `param`/`authz` parameters (Task 2)

- [ ] **Step 1: `BuildGroupBy` — primary-type ownership**

**Ordering fix required first**: unlike `BuildSearch`/`BuildAggregate` (both declare `param` before their `BuildFromWithJoins` call), `BuildGroupBy` currently declares `var param = new DynamicParameters();` *after* `var from = BuildFromWithJoins(schema, request.Joins, registry, out var tableMap);` (`StarRocksQueryBuilder.cs:190-192`). Since Task 2 threads a new `param` parameter into `BuildFromWithJoins`, move the `param` declaration above the `BuildFromWithJoins` call in this method.

Then apply the same wrap-and-AND pattern to `BuildGroupBy`'s own `where` variable (this method always calls `BuildFromWithJoins`, even with zero joins, per its existing structure — no no-join branch to handle separately).

- [ ] **Step 2: Field reject-on-reference + `Expression` check**

Each entry of `request.Keys` and each `MetricSpec.Field` already resolves via `ResolveColumn(tableMap, ...)` (`keyCols`; `BuildMetricExpr`'s `metric.Field` branch) — call `StarRocksQueryBuilder.IsFieldAllowed(resolvedColumn, schema, tableMap, authz, out var typeName)` (Task 1 Step 4) on each and reject (`StarRocksQueryTranslationException`, naming `typeName` and the field) when it returns `false`. `BuildMetricExpr`'s `metric.Expression` branch gets the same tokenize-and-check treatment as Task 3 Step 2 (`TokenRx`/`DeriveWhitelist` + `IsFieldAllowed` per token), reusing the same helpers.

- [ ] **Step 3: Denied-caller handling**

In `GroupBy`, compute `EvaluateAuthorization` (primary + joined types across `request.Joins`); `PrimaryDenied` → close the stream with zero rows; `DeniedJoinedType` → `InvalidArgument`. Pass `authz: auth.Constraints` into `search.GroupByAsync(...)`.

- [ ] **Step 4: Add unit tests**

Per the spec's testing plan (GroupBy-specific slice): denied → empty stream; restricted field via `Keys`/`MetricSpec.Field` → `InvalidArgument`; restricted field via `MetricSpec.Expression` → `InvalidArgument`; joined type denied → `InvalidArgument`; ownership-restricted rows correctly filtered.

- [ ] **Step 5: Run tests and commit**
```bash
cd Iverson.Server
dotnet test Iverson.StarRocks.Tests
dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~ObjectSearchGrpcServiceTests"
git add Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs
git commit -m "feat(starrocks): enforce row/field authorization on ObjectSearchGrpcService.GroupBy"
```

### Task 5: `Pipeline` enforcement (largest task, matching its complexity in the spec)

**Files:**
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs` (`ColumnsFor`, `TrackAndValidate`, `ResolveJoinSources`, `ValidateStepAndComputeOutput`, `Build`, `EmitStep`)
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:311-350` (`Pipeline`)
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

**Interfaces:**
- Consumes: `AuthorizationConstraint`/`EvaluateAuthorization` (Task 1), `TokenRx`/`DeriveWhitelist` (Task 1, already `internal` — this task's own file, no cross-class access needed)

- [ ] **Step 1: Column-introduction filtering**

`ColumnsFor(StarRocksQuerySchema schema)` gains a trailing `AuthorizationConstraint? constraint = null` parameter; when `constraint?.AllowedFields` is non-null, omit any column name not in it from the returned dictionary (the key column is never excluded, per the evaluator's existing contract). `TrackAndValidate`'s `baseColumns = ColumnsFor(schema)` call passes the primary type's constraint (looked up from a new `authz` parameter `TrackAndValidate`/`Build` both gain); `ResolveJoinSources`'s `ColumnsFor(joinedSchema)` call passes that joined type's constraint.

- [ ] **Step 2: `all: true` scoping**

In `ResolveSelectSource`/`EmitSelectItem`'s `all: true` handling (inside `ValidateStepAndComputeOutput`), when `item.All` is true: if `source` corresponds to a fresh `PipelineJoin` target (present in `joinSources` **and** resolvable via `registry`, i.e. not a prior step) and that type's constraint has a non-null `AllowedFields`, throw (naming the source type) rather than expanding. When `source` is a prior step (including `"base"`), no such check — expand normally; per Step 1, its `Columns` dictionary already only contains allowed names.

- [ ] **Step 3: Pipeline's own `MetricSpec.Expression` check**

In `ValidateStepAndComputeOutput`'s metrics loop, when `m.Expression` is non-empty (and `m.Field` is empty), tokenize it with `TokenRx`/`DeriveWhitelist` (same file, direct access) and require every non-whitelisted identifier token to be present in `input.Columns` — an unresolvable token throws the same `Invalid(...)` `StarRocksQueryTranslationException` the `m.Field` branch already uses.

- [ ] **Step 4: Ownership — `baseWhere` + per-step join `ON`**

`Build`'s `baseWhere` construction gets the same wrap-and-AND treatment as Tasks 2-4 (using the primary type's constraint from the new `authz` parameter). `EmitStep`'s per-join `ON`-clause construction gets the same `AND <ownerCol> = @val` append as Task 2 Step 2, keyed by whichever type `join.Source` resolves to (only applies when it resolves to a fresh registered type via `registry`, not a prior step — prior-step joins need no new ownership predicate, matching the "already filtered upstream" reasoning already established for Pipeline steps' own `where`).

- [ ] **Step 5: Layer 2 masking**

`Build` returns `lastCols` (the last step's `StepColumns.Columns`) alongside `(string Sql, DynamicParameters Param)` — change its return type to a 3-tuple or small record. `StarRocksRepository.PipelineAsync` uses this to mask each returned row's dict (strip any key not present in `lastCols`) before returning `IEnumerable<dynamic>` to its caller — this keeps Layer 2 entirely inside `Iverson.StarRocks`, consistent with the spec's decoupling goal.

- [ ] **Step 6: Denied-caller handling**

In `ObjectSearchGrpcService.Pipeline`, compute `EvaluateAuthorization` (primary + every distinct type any `PipelineJoin.Source` resolves to via `registry`, across all `request.Steps`); `PrimaryDenied` → close the stream with zero rows; `DeniedJoinedType` → `InvalidArgument`. Pass `authz: auth.Constraints` into `search.PipelineAsync(...)`.

- [ ] **Step 7: Add unit tests**

Per the spec's testing plan (Pipeline-specific slice): denied → empty stream; `TrackAndValidate` rejecting a disallowed field/`all: true`-against-a-fresh-type reference; an `Expression` token-parse rejecting a disallowed column in a step metric; a filter-clause (`where`) reference to a disallowed column being rejected; a step joining `all: true` against a *prior step* silently succeeding regardless of restriction; a multi-step Pipeline (explicit multi-type `select` in an earlier step, pure implicit passthrough in the final step) whose response correctly retains the joined-type/derived column; joined type denied → `InvalidArgument`.

- [ ] **Step 8: Run tests and commit**
```bash
cd Iverson.Server
dotnet test Iverson.StarRocks.Tests
dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~ObjectSearchGrpcServiceTests"
git add Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs Iverson.Server/Iverson.StarRocks/StarRocksRepository.cs Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs
git commit -m "feat(starrocks): enforce row/field authorization on ObjectSearchGrpcService.Pipeline"
```

## Tasks NOT in this plan

- `SearchSimilar`/`SearchChunks` (Qdrant) — Part 5d.
- Any change to `IRowFieldAuthorizationEvaluator`'s own logic or `AuthorizationDecision` shape (Part 5a's contract is reused as-is).
- Migrating/backfilling `AuthorizationRules` onto existing registered schemas (same accepted-out-of-scope position as 5a/5b).

## Known issues inherited from spec

Same 2 pre-existing consequences 5a/5b already documented, unaffected by this part: schemas with no `AuthorizationRules` reject every call; a rules-configured schema with no acting-user identity also rejects. Neither is fixed here.
