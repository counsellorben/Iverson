# Task 4 Report: StarRocks enforcement (Search/Aggregate/GroupBy/Pipeline)

## Status: DONE

## What I did

Wired `AuthorizationDecision.TenantColumn`/`TenantValue` (Task 2) through `AuthorizationConstraint`
into both StarRocks SQL-generation files — `StarRocksQueryBuilder.cs` (BuildSearch, BuildAggregate,
BuildGroupBy, BuildFromWithJoins) and `StarRocksPipelineBuilder.cs` (the separate Pipeline RPC's
`Build`/`EmitStep`) — adding a tenant predicate next to every existing ownership predicate. Every
tenant check is a separate `if`, gated only on `TenantColumn is not null`, never nested inside or
conditioned on the ownership block (which is skipped for `CanReadAll`/bypass callers).

## Per-file changes

### `Iverson.Server/Iverson.StarRocks/AuthorizationConstraint.cs`
Appended `string? TenantColumn = null, string? TenantValue = null` as positional parameters with
default values. **Design decision (flagging per the brief's request to flag ambiguity):** the brief
said "positional record, appended" but didn't specify defaults. I chose defaults because this
record is constructed directly (not via a factory) at ~130 call sites across
`StarRocksQueryBuilderTests.cs` and `StarRocksPipelineBuilderTests.cs` alone — making the new params
required would have forced a mechanical, unrelated edit to every existing owner-focused test. With
defaults, all pre-existing tests compiled and passed unmodified (verified), and only the new
tenant-focused tests I wrote supply real values. This is purely a test-construction convenience;
every real production call site (`ObjectSearchGrpcService.EvaluateAuthorization`, the only
non-test constructor) now always supplies both values.

### `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`
Lines 448 and 458 (primary and joined constraint construction sites in `EvaluateAuthorization`):
added `primaryDecision.TenantColumn, primaryDecision.TenantValue` / `decision.TenantColumn,
decision.TenantValue`. Left the `StringComparer.OrdinalIgnoreCase` dictionary keying untouched, as
instructed.

### `Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs`
Added 5 tenant blocks, each a standalone `if (authz is not null && authz.TryGetValue(...) &&
tenantConstraint.TenantColumn is not null)`:
- `BuildSearch`, with-joins primary branch (~line 67): fixed `@__tenantVal`, qualified with
  `primaryAlias`, wrap-and-AND onto `where`.
- `BuildSearch`, no-joins primary branch (~line 92): fixed `@__tenantVal`, unqualified.
- `BuildAggregate` primary (~line 232): fixed `@__tenantVal`, qualified via `tableMap` when present
  (mirrors the owner block immediately above it).
- `BuildGroupBy` primary (~line 327): fixed `@__tenantVal`, always qualified via `tableMap`
  (`BuildGroupBy` always has one, per the existing owner block's comment).
- `BuildFromWithJoins` joined-type ON clause (~line 765): **per-join unique** `$"__tenant{map.Count}"`,
  computed and added to `param` exactly like the existing `$"__owner{map.Count}"` block right above
  it, appended to `ownerCond`+`tenantCond` on the same JOIN's `ON` clause — never the outer `WHERE`.

### `Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs`
Added 2 tenant blocks, same independent-gating discipline:
- `Build`, primary (~line 401): fixed `@__tenantVal`, wrap-and-AND onto `baseWhere`.
- `EmitStep`, joined-type ON clause (~line 550): **per-join unique** `$"s{stepIdx}_j{joinIdx}_tenant"`
  (mirrors the existing `$"s{stepIdx}_j{joinIdx}_owner"` `pName`), gated additionally on
  `isFreshType` exactly like the owner block (a step-to-step join is never looked up in `authz`).

## Safety-critical detail verified (parameter-naming collision guard)

Confirmed by dedicated multi-join regression tests (not present before this task) in both files:
- `StarRocksQueryBuilderTests.BuildSearch_WithTwoJoins_EachJoinedTypeTenantConstraint_UsesDistinctParameterNames`
- `StarRocksQueryBuilderTests.BuildFromWithJoins_TwoJoins_EachJoinedTypeTenantConstraint_UsesDistinctParameterNames`
- `StarRocksQueryBuilderTests.BuildGroupBy_TwoJoins_EachJoinedTypeTenantConstraint_UsesDistinctParameterNames`
- `StarRocksPipelineBuilderTests.Build_TwoJoinsInSameStep_EachJoinedTypeTenantConstraint_UsesDistinctParameterNames`

Each constructs a 3-type join (Author→Article→Tag, or two joins in one pipeline step) where both
joined types carry a tenant column, and asserts the two generated parameter names/values are
distinct and each correctly bound — proving the per-join-unique naming holds and a fixed-name
collision cannot occur.

## Tests added

`StarRocksQueryBuilderTests.cs` (17 new tests): tenant analogs of every existing owner test —
`BuildAggregate` (no-joins wrap/no-existing-where/no-column/with-joins-primary/with-joins-joined),
`BuildSearch` (no-joins wrap/no-existing-where/no-column/owner+tenant-together/with-joins-primary/
two-joins-distinct-names), `BuildFromWithJoins` (joined ON not WHERE/LEFT JOIN preserves
kind/owner+tenant coexist on same ON/two-joins-distinct-names), `BuildGroupBy` (primary wrap/joined
ON/two-joins-distinct-names). Added a `TagSchema()` fixture for the 3-type join tests.

`StarRocksPipelineBuilderTests.cs` (6 new tests): `Build_PrimaryTenant_*` (wrap/combines-with-base-
where), `Build_PrimaryOwnerAndTenant_BothPredicatesApplyIndependently`,
`Build_JoinedTypeTenant_AppendsTenantConditionToJoinOn`,
`Build_TwoJoinsInSameStep_EachJoinedTypeTenantConstraint_UsesDistinctParameterNames`,
`Build_JoinAgainstPriorCte_NoTenantPredicateAppendedEvenWhenAuthzHasThatType` (step-source-vs-
registered-type name collision guard, mirroring the existing ownership test). Added
`TagSchemaLocal()`/`RegistryWithAuthorAndTag()`/`TwoJoinStep()` fixtures.

`ObjectSearchGrpcServiceTests.cs` (4 new tests): `Search_BypassCaller_ForwardsTenantConstraint_
EvenThoughOwnerColumnIsNull` (constraint-forwarding proof that tenant isn't gated on the bypass
short-circuit), `Search_BypassCaller_CrossTenant_StreamsEmptyResult` (the literal "cross-tenant
caller with CanReadAll gets an empty result" ask — the fake `_search.SearchAsync` stands in for the
real database and only "returns" its row when the forwarded constraint's `TenantValue` matches the
row's own tenant, modeling what the already-unit-tested `WHERE`/`ON` predicate does at execution
time), `Search_JoinedTypeTenantConstraint_ForwardsTenantColumnAndCallerTenantAsValue` (joined-type
analog of the existing joined-owner forwarding test).

## Test results

- Pre-existing suite baseline (before any test/prod changes): 232/232 `Iverson.StarRocks.Tests`
  green.
- After adding `AuthorizationConstraint`'s two new defaulted params: all pre-existing tests in both
  projects compiled and passed **unmodified** — confirms the default-value choice didn't require
  touching the ~130 existing owner-only call sites.
- `dotnet test Iverson.Server/Iverson.StarRocks.Tests --filter "Category!=Integration"`:
  **256/256 passed** (232 pre-existing + 17 QueryBuilder + 6 PipelineBuilder tenant tests + 1 fixture
  helper doesn't count as a test).
- `dotnet test Iverson.Server/Iverson.Api.Tests --filter "Category!=Integration"`:
  **393/393 passed** (389 pre-existing + 4 new tenant tests). Note: `SchemaFixtures.cs`/
  `ActingUserFixtures.cs` and all local schema helpers in this file already declared
  `TenantColumn = "TenantId"` from Task 2's cutover — no fixture fallout this time.
- Note on the brief's suggested single command (`dotnet test
  Iverson.Server/Iverson.StarRocks.Tests Iverson.Server/Iverson.Api.Tests`): this repo has no
  solution file wiring the two test projects together, and `dotnet test` rejects multiple project
  paths in one invocation (`MSB1008: Only one project can be specified`). Ran both projects
  separately instead — full commands and results above.

## Concern flagged per the task brief's instruction (ambiguous local-fixture check)

I checked `StarRocksQueryBuilderTests.cs` and `StarRocksPipelineBuilderTests.cs` for local
`AuthorizationConstraint`-constructing helpers that might need a "declare tenant column" fixture
fix analogous to `SchemaFixtures`/`ActingUserFixtures`. Neither file has a shared factory helper for
`AuthorizationConstraint` — every test constructs it inline via `new(...)` at the call site, so
there was no shared fixture to retrofit. Because I gave the two new fields default values of
`null`, none of the ~130 existing inline constructions needed any change; they simply continue to
represent "no tenant constraint" scenarios, which is a legitimate and still-meaningful test case
(e.g. proving the builder doesn't add a tenant predicate when none is supplied). I judged this
unambiguous given the record's usage pattern (no factory to retrofit), so I did not flag it as a
blocking question — but wanted to make the reasoning explicit per the brief's request.

## No other concerns

- Self-review completed: every one of the 7 sites named in the brief (5 in
  `StarRocksQueryBuilder.cs`, 2 in `StarRocksPipelineBuilder.cs`) has a tenant block that (a) uses
  the fixed `@__tenantVal` name for the primary type and a per-join-unique name for each joined
  type (verified with dedicated 2-join collision tests), and (b) is gated purely on
  `TenantColumn is not null`, independent of the ownership block's own gating.
- No operator bypass introduced anywhere — bypass/CanReadAll callers get the ownership predicate
  skipped but the tenant predicate always applied, confirmed by
  `Search_BypassCaller_ForwardsTenantConstraint_EvenThoughOwnerColumnIsNull` and
  `Build_PrimaryTenant_*`/`BuildAggregate_NoJoins_TenantConstraint_AppliesEvenWhenOwnerColumnIsNull`.
