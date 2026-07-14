# StarRocks Authorization Enforcement (Part 5c)

## Context

Part 5 of the identity-management initiative adds row/field-level authorization to Iverson's three data stores. Part 5a built the shared foundation (`AuthorizationRules` schema metadata, `IRowFieldAuthorizationEvaluator`). Part 5b wired that evaluator into Postgres's write/read gRPC surfaces (`ObjectMappingGrpcService`, `ObjectPersistenceGrpcService`, `ObjectRetrievalGrpcService`). This part (5c) wires the same evaluator into `ObjectSearchGrpcService`'s StarRocks-backed read surfaces: `Search`, `Aggregate`, `GroupBy`, `Pipeline`.

`SearchSimilar` and `SearchChunks` (Qdrant-backed vector search) are explicitly out of scope — that is Part 5d.

## Goals

- A caller's row-level ownership restriction (from `AuthorizationRules.OwnerField` + `RowPermission`) is enforced as a genuine SQL-level row filter on every StarRocks query these 4 RPCs can generate, including joined types.
- A caller's field-level restriction (`FieldPermission`) is enforced on every field these RPCs can expose or compute over, including joined types.
- Enforcement reuses the existing `IRowFieldAuthorizationEvaluator` unchanged — no new evaluator variant, no duplicated authorization logic.
- `Iverson.StarRocks` (which has zero dependency on `Iverson.Api.Authorization`/`Iverson.Api.Schema` today) stays that way — all `ClaimsPrincipal`/evaluator logic lives in `Iverson.Api`; only plain, decoupled data crosses the project boundary.

## Non-goals

- `SearchSimilar`/`SearchChunks` (Qdrant) — Part 5d.
- Any change to `IRowFieldAuthorizationEvaluator`'s own logic or `AuthorizationDecision` shape (Part 5a's contract is reused as-is).
- Migrating/backfilling `AuthorizationRules` onto existing registered schemas (same accepted-out-of-scope position as 5a/5b).

## Design

### 1. New decoupled type: `AuthorizationConstraint`

Owned by `Iverson.StarRocks`, no reference to `Iverson.Api.Authorization`:

```csharp
namespace Iverson.StarRocks;

public sealed record AuthorizationConstraint(
    IReadOnlySet<string>? AllowedFields,   // null = unrestricted
    string? OwnerColumn,                    // null = no ownership predicate needed
    string? OwnerValue);
```

`ObjectSearchGrpcService` calls the existing `IRowFieldAuthorizationEvaluator.Evaluate(schema, actingUser, AuthorizationAction.Read)` once per involved registered type — the primary type named in the request, plus each distinct type referenced by a `JoinSpec`/`PipelineJoin` — and converts each resulting `AuthorizationDecision` into an `AuthorizationConstraint`. These are collected into `IReadOnlyDictionary<string, AuthorizationConstraint>` (keyed by `TypeName`) and passed as a new optional parameter into `IEngagementStoreSearchService`'s 4 methods. `Iverson.StarRocks` only ever consumes this plain data — the same boundary-crossing pattern `SchemaBuilder.ToStarRocksQuerySchema` already establishes for schema info.

### 2. Row-level ownership filtering

**Primary type** (Search/Aggregate/GroupBy's base table; Pipeline's `base_where`): build the WHERE clause exactly as today, then — if the primary type's constraint has a non-null `OwnerColumn` — wrap it and AND on the ownership predicate:

```
(<existing where>) AND `ownerCol` = @ownerVal
```

Skipped entirely when `OwnerColumn` is null (bypass or unrestricted caller). Applied at:
- `StarRocksQueryBuilder.BuildSearch`'s `where` (both the join and no-join branch)
- `StarRocksQueryBuilder.BuildAggregate`'s `where` (both branches)
- `StarRocksQueryBuilder.BuildGroupBy`'s `where`
- `StarRocksPipelineBuilder`'s `baseWhere` (the implicit `base` CTE)

**Joined types** (`JoinSpec` on Search/Aggregate/GroupBy; `PipelineJoin` on Pipeline steps): the ownership predicate is appended to the **join's own `ON` clause**, never the outer `WHERE`. `JoinSpec`/`PipelineJoin` support `LEFT`/`RIGHT`/`FULL` in addition to `INNER` (verified: `JoinKind` proto enum); an ownership condition added to the outer `WHERE` would silently collapse a `LEFT JOIN` into an `INNER JOIN` (a non-matching or unauthorized right-side row would drop the *entire* combined row instead of just nulling out the right side) — the classic SQL "outer join defeated by a WHERE-clause predicate on the outer side" bug. Appending to `ON` preserves whatever join-kind semantics the caller specified, uniformly:

```
... LEFT JOIN `authors` AS `Author` ON `Article`.`AuthorId` = `Author`.`Id` AND `Author`.`OwnerId` = @ownerVal
```

Applied at:
- `StarRocksQueryBuilder.BuildFromWithJoins` (shared by Search/Aggregate/GroupBy) — verified: its join-emission is a single `sb.Append($" {kind} JOIN ...")` call producing one complete `ON` clause per join, straightforwardly extensible with `AND <predicate>`.
- `StarRocksPipelineBuilder`'s per-step join emission (`EmitStep`, non-aggregate branch) — verified: same single-`ON`-clause-per-join shape (`sb.Append($" {kind} JOIN {target} ON {string.Join(" AND ", conds)}")`).

**Explicitly excluded from row-filter treatment** (verified, no ownership/field logic needed):
- `BuildHaving` (used by Aggregate's `having` and Pipeline steps' `having`) — its own doc comment confirms HAVING clauses reference SQL *output aliases* (`doc_count`, `metric_val`, a step's own metric alias), never raw schema columns. A HAVING clause can't itself leak a raw restricted column — whatever it references was already validated (or masked) at the point that alias's underlying field/metric was defined.
- Pipeline steps' own `where`/`nonAggWhere` (post-base steps) — verified: these resolve exclusively against the *prior step's already-filtered* output columns (`input.Columns`), never a fresh join source. Ownership was already applied once, upstream, at the base step or at whichever step first joined that type.

### 3. Field-level enforcement (differs per RPC, matching how each exposes fields)

**Search** — verified: `StarRocksQueryBuilder.BuildSelectColumns`'s joined-query branch resolves columns only against the primary `schema` parameter, never `tableMap` — a joined type's columns never appear in a `Search` response regardless of `JoinSpec`. Field masking only needs the **primary type's** `AllowedFields`: fetch the row exactly as today, then strip any key not in `AllowedFields` from the resulting dict before building the response `Struct` — same post-fetch masking mechanism as Part 5b's Postgres `Get`.

**Aggregate & GroupBy** — every output field is an explicit reference (`AggregationSpec.Field`/`GroupByFields`, `GroupByRequest.Keys`/`MetricSpec.Field`), resolved via the existing `ResolveColumn(tableMap, ...)` helper that already spans primary + joined types. Resolve each referenced field to its owning type first, then — if that type's `AllowedFields` is non-null and excludes it — reject the whole call: `RpcException(InvalidArgument, ...)` naming the field.

`AggregationSpec.Expression`/`MetricSpec.Expression` are **not** exempt from this check, despite the existing code comments describing them as "trusted server-side" input — those fields are ordinary public proto fields any RPC caller can set directly, so an unchecked `Expression` would let a caller bypass the `Field`-based check entirely by embedding a restricted column name in raw SQL instead. Every `Expression` is parsed the same way `StarRocksPipelineBuilder.ValidateDeriveExpr` already parses `DeriveColumn.expr` for Pipeline: tokenize it, and require every identifier token that isn't a whitelisted SQL keyword/function to resolve to a column the caller's `AllowedFields` permits (for whichever type it belongs to); an unresolvable or disallowed token rejects the whole call the same way a disallowed `Field` reference does.

**Filter clauses** — `SearchQuery.Clauses` (used by `Search`'s `query`, `Aggregate`'s `query`, and `GroupBy`'s `query`) and `PipelineRequest.BaseWhere`/`PipelineStep.Where` can reference any schema field purely to filter on it (e.g. `WHERE Salary > 100000`), without that field ever appearing in the response — an unchecked filter reference would let a caller infer a restricted field's value via a boolean-oracle side channel. For `Search`/`Aggregate`/`GroupBy`, each clause's `property` is resolved to its owning type (primary or joined, via the same `ResolveColumn`/`tableMap` machinery `BuildWhere` already uses) and checked against that type's `AllowedFields` directly; a disallowed reference rejects the whole call. For Pipeline's `BaseWhere`/`PipelineStep.Where`, the same restriction is realized via the column-introduction mechanism described under Pipeline below, not a per-reference type resolution. `HAVING` remains exempt, unaffected — it only ever resolves against already-computed output aliases (`doc_count`/`metric_val`/a step's own metric alias), never a raw schema column, so there is nothing for it to leak that wasn't already checked when the underlying field/metric was declared.

**Pipeline** — two layers:
1. **Column introduction, not per-reference resolution.** A column only has a resolvable owning type at the moment it is first introduced from a registered schema: the base step's initial column set (`ColumnsFor(schema)`) and each fresh `PipelineJoin` target's initial column set (`ResolveJoinSources`) — everywhere else, `StepColumns` tracks a flat name-to-name mapping with no type provenance (verified: its own doc comment, and `ValidateStepAndComputeOutput`'s `output` dictionary carries no type tag). So the check happens at those two introduction points only: omit any column excluded by that type's `AllowedFields` from the resulting dictionary, rather than including it and trying to re-check it downstream. Every later `select`/`group_by`/`metrics`/`window`/`derive`/`join.on`/`where` reference that names an excluded column then fails through `TrackAndValidate`'s existing `RequireColumn` lookup with its current generic "unknown column or alias" error (`StarRocksQueryTranslationException`, mapped to `InvalidArgument` exactly as any other invalid Pipeline reference already is) — no separate authorization-specific check or column-to-type provenance tracking is needed anywhere downstream of these two introduction points, since a name can only be referenced later if it was already present (already allowed) when introduced. An `all: true` select item is a deliberate exception to this implicit behavior, but **only when its source is a fresh `PipelineJoin` target** (a real registered type with a real `AllowedFields` to check): it is checked and rejected outright rather than silently expanding to the allowed subset — a caller who names specific columns simply doesn't get the ones they didn't ask for, but a caller who asks for `all` against a type should not silently receive less than "all" without being told. When `all: true`'s source is instead a *prior step* (including `"base"` itself, re-joined — `ResolveJoinSources` resolves earlier steps before falling back to the schema registry, and a step is not a registered type with a constraint to look up), no such rejection applies: it silently expands to whatever that step's already-filtered `Columns` dictionary contains, consistent with how every other reference to a prior step already behaves — nothing unsafe can result, since that dictionary can only ever contain names that already survived introduction-time filtering.
2. **Implicit passthrough** (a step with no explicit `select` — only possible for base-type columns *from that step's own join sources*, since any step with joins is required to have an explicit `select`; an implicit-passthrough step can still carry forward an *earlier* step's already-multi-type output) reaches the final output unchecked by layer 1's reference-naming check. This matters because the excluded-from-`StepColumns` dictionary is validation-only — it does not change what SQL `StarRocksPipelineBuilder.Build` actually emits: the base CTE is a literal `SELECT * FROM <table>`, so an excluded column's raw value is still physically present in every step's data until something narrows it. The Pipeline's final output is masked by keeping only the keys present in the **last step's own `StepColumns.Columns`** (the `lastCols` dictionary `Build` already computes and captures — today only for the final `ORDER BY` — via `TrackAndValidate`'s existing pass) and stripping everything else. Every name in `lastCols` reached it by surviving layer 1's introduction-time filtering and/or reference checks, so keeping exactly those (rather than masking against the base type's raw `AllowedFields`) is correct for base columns, renamed joined-type columns, and `derive`/`window`/metric aliases alike — none of them need re-validating, and none of them get incorrectly stripped for not being a literal base-schema column name. Anything NOT in `lastCols` is, by construction, an excluded column that only reached the physical row via an implicit `SELECT *` and was never legitimately referenced.

### 4. Denied-caller handling

**Primary type Denied** (no `AuthorizationRules` configured, or no acting-user identity) — evaluated once per RPC in `ObjectSearchGrpcService`, before calling into `IEngagementStoreSearchService`:
- `Search`/`GroupBy`/`Pipeline`: close the response stream having written zero rows (StarRocks is never queried).
- `Aggregate`: return `AggregateResponse { Results = { } }` (empty).

This mirrors 5b's "denial looks like absence, not an error" principle — it doesn't distinguish "no rows matched" from "you're not authorized," avoiding a signal that the type/schema exists or has rules configured.

**Joined type Denied** — a join is structural (baked into `FROM`/`ON`, or a `GROUP BY` key), unlike Get's nested relations which can be gracefully omitted. Reject the whole call: `RpcException(InvalidArgument, ...)` naming the joined type. Evaluated for every distinct type referenced by a `JoinSpec`/`PipelineJoin` before the call proceeds.

### 5. Wiring changes

- `ObjectSearchGrpcService` gains `IActingUserAccessor` + `IRowFieldAuthorizationEvaluator` constructor parameters (same pattern as the 3 Postgres-facing services in 5b). Both are already DI-registered (`Program.cs`) and require no new registration.
- `IEngagementStoreSearchService`'s 4 methods (`SearchAsync`/`AggregateAsync`/`GroupByAsync`/`PipelineAsync`) each gain one new optional parameter: `IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null`. Implemented in `StarRocksRepository`, threaded into `StarRocksQueryBuilder`/`StarRocksPipelineBuilder`.
- `ObjectSearchGrpcServiceTests.cs` (the only direct-construction site besides DI) needs its constructor call updated for the 2 new params, plus a default `ActingUser` (group `"test-bypass"`) added — matching the pattern the other 3 grpc test files already use. Its existing schema fixtures (`SchemaFixtures.AuthorSchema()`/`ArticleSchema()`/etc.) already carry a permissive bypass `AuthorizationRules` from 5a/5b's fixture fix, so no further fixture changes are needed there. The one inline `new SchemaDescriptor { ... }` in this file (`SearchSimilar_ThrowsRpcException_WhenNoCollection`) exercises `SearchSimilar`, which is out of scope for 5c and unaffected.

## Verified assumptions

| # | Assumption | Verified |
|---|---|---|
| 1 | `Iverson.StarRocks.csproj` has no project reference to `Iverson.Api` | Read the `.csproj` — only references `Iverson.Client.Contracts` |
| 2 | `BuildSelectColumns`'s joined branch resolves only against the primary schema, never `tableMap` | Read the method: `ResolveColumn(schema, f)`, `schema` is always the primary param |
| 3 | `JoinSpec`/`PipelineJoin` support Left/Right/Full/Inner | Read `object_search.proto`'s `JoinKind` enum |
| 4 | `BuildFromWithJoins`'s per-join `ON` clause is a single, extensible string | Read the method's `sb.Append($" {kind} JOIN ...")` call |
| 5 | `StarRocksPipelineBuilder.EmitStep`'s per-join `ON` clause is the same shape | Read the method's equivalent `sb.Append` call |
| 6 | `IRowFieldAuthorizationEvaluator`/`RowFieldAuthorizationEvaluator` DI-registered, reusable as-is | Read `Program.cs`'s `AddSingleton<IRowFieldAuthorizationEvaluator, ...>` |
| 7 | `IActingUserAccessor.ActingUser` is `ClaimsPrincipal?`, matching the evaluator's parameter | Read `IActingUserAccessor.cs` |
| 8 | Only one non-DI construction site of `ObjectSearchGrpcService` exists | Grepped the repo for `new ObjectSearchGrpcService(` — one hit, in the test file |
| 9 | `SchemaRegistry.Get(typeName)` returns the full `SchemaDescriptor` (incl. `Authorization`) for any registered type name | Read `SchemaRegistry.cs`'s `Get` signature |
| 10 | `TrackAndValidate` performs a full pre-SQL pass resolving every column reference across all step constructs | Read the full method |
| 11 | `StarRocksQueryTranslationException` is caught and mapped to `InvalidArgument` in all 4 RPC methods | Read `ObjectSearchGrpcService.cs`'s try/catch blocks around `Search`/`RunAggregationAsync`/`GroupBy`/`Pipeline` |
| 12 | Aggregate/GroupBy field references resolve via the same `ResolveColumn(tableMap, ...)` helper | Read `BuildAggregate`'s `Resolve` local function and `BuildGroupBy`'s `keyCols`/`metricExprs` construction |
| 13 | Existing `ObjectSearchGrpcServiceTests.cs` schemas already carry bypass `AuthorizationRules` from 5a/5b's fixture fix | Read `SchemaFixtures.cs` — every factory method sets `Authorization = BypassAuthorization()` |
| 14 (recurrence) | Every WHERE/HAVING-shaped surface across all 4 RPCs is accounted for | Enumerated: `BuildSearch`/`BuildAggregate`/`BuildGroupBy`'s `where`, Pipeline's `baseWhere` (all need primary-type ownership); `BuildFromWithJoins`/`EmitStep`'s join `ON` clauses (joined-type ownership); `BuildHaving` confirmed exempt (alias-only) |
| 15 | `AggregationSpec.Expression`/`MetricSpec.Expression` are ordinary public proto fields reachable by any RPC caller, not a "trusted server-side only" input as the surrounding code comments describe | Read `object_search.proto` — both are plain `string` fields on `AggregateRequest.aggregations`/`GroupByRequest.metrics`/`PipelineStep.metrics`; grepped `Iverson.Api`/`Iverson.StarRocks` for any role/authorization gate restricting who may set them — none exists. (Corrected from CDR round 1 §2.1 — the "trusted server-side caller" comment describes the code's intended use, not an enforced trust boundary.) |
| 16 | `StepColumns` (Pipeline's internal column-tracking structure) has no per-column type/origin tag beyond the point of introduction — only the base step's `ColumnsFor(schema)` and each fresh join source's `ColumnsFor(joinedSchema)` are tied to a real registered type; every later step's own output dictionary is a flat name-to-name mapping | Read `StarRocksPipelineBuilder.cs` — `StepColumns`'s doc comment ("maps a referenced name... to the canonical output column name") and `ValidateStepAndComputeOutput`'s `output` dictionary construction (built via `AddOutput`, no type field anywhere). (Resolved via CDR round 2 §3.1 — Pipeline's field-level check is realized by excluding disallowed columns at the two introduction points, not by resolving a type per downstream reference.) |
| 17 | Every name that ever enters any step's `StepColumns.Columns`/`output` dictionary already survived layer 1's introduction-time filtering and/or reference-checking, so the last step's `Columns` dictionary (`lastCols`) is exactly the set of currently-allowed output names — no separate "running allowed set" needs to be built | Read `StarRocksPipelineBuilder.cs`'s `Build` method — `lastCols = byName[prev].Columns` is already computed from `TrackAndValidate`'s output and already captured (currently used only for the final `ORDER BY`). `ResolveJoinSources`'s own code comment ("resolution against prior steps or the schema registry") confirms a join/`all: true` source can be a prior step (incl. `"base"`) as well as a fresh registered type. (Resolved via CDR round 3 §2.1 and §3.1 — Layer 2 now masks against `lastCols` instead of the base type's raw `AllowedFields`; `all: true`'s outright-rejection is scoped to fresh registered-type sources only, per the user's choice of Option A.) |

## Testing plan

- `ObjectSearchGrpcServiceTests.cs`: denied (no rules)/denied (no identity) → empty result, per RPC; owner-match rows included; non-owner rows excluded; bypass-role sees all; restricted field masked from `Search` response; restricted field referenced by an `AggregationSpec`/`GroupByRequest` key or metric → `InvalidArgument`; restricted field referenced via `AggregationSpec.Expression`/`MetricSpec.Expression` (instead of `Field`) → `InvalidArgument`, proving the bypass is closed; restricted field referenced only in a filter clause (`SearchQuery.Clauses`/`PipelineRequest.BaseWhere`/`PipelineStep.Where`), never in output → `InvalidArgument`; joined type denied → `InvalidArgument`; joined type ownership-restricted → only matching joined rows survive (via a `LEFT JOIN`, confirming the non-matching side nulls out rather than dropping the whole row).
- `Iverson.StarRocks.Tests`: `StarRocksQueryBuilder`/`StarRocksPipelineBuilder` unit tests for the new `authz` parameter — wrap-and-AND SQL shape for primary-type ownership, `ON`-clause-appended predicate for joined-type ownership (including a `LEFT JOIN` case proving row-preservation), `TrackAndValidate` rejecting a disallowed field/`all: true`-against-a-fresh-type reference with the new constraint present, an `Expression` token-parse rejecting a disallowed column, a filter-clause (`where`) reference to a disallowed column being rejected, a step joining `all: true` against a *prior step* silently succeeding (not rejected) regardless of restriction, and a multi-step Pipeline (explicit multi-type `select` in an earlier step, pure implicit passthrough in the final step) whose response correctly retains the joined-type/derived column rather than having it stripped by the final masking pass.

## Known issues / accepted as out of scope

Same 2 pre-existing consequences 5a/5b already documented, unaffected by this part: schemas with no `AuthorizationRules` reject every call; a rules-configured schema with no acting-user identity also rejects. Neither is fixed here.
