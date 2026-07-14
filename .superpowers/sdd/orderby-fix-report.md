# StarRocks ORDER BY authorization-gate fix — report

## Context

Final whole-branch review of the completed StarRocks authorization-enforcement plan (Part 5c)
found that `BuildSearch`'s `BuildOrder` helper and `BuildGroupBy`'s inline `orderSql`
construction, in `Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs`, resolved sort
columns via `ResolveColumn` with **no `IsFieldAllowed` gate** — unlike every other
field-reference site in the file (WHERE clauses via `BuildWhere`'s `resolve` closures,
aggregation `spec.Field`/`spec.Expression`/`spec.GroupByFields` in `BuildAggregate`, and GROUP BY
`Keys`/`MetricSpec.Field`/`MetricSpec.Expression` in `BuildGroupBy`). A caller could sort results
by a field outside their `AllowedFields`, leaking that field's *relative ordering* across rows
even though they could not select, filter, or aggregate on it directly (filter-clause gating
already blocks binary-searching the actual value via WHERE, but ordering is a distinct side
channel).

`BuildAggregate` was confirmed to have no equivalent surface (it only orders by computed
`doc_count`/`bucket_key`, never a caller-referenceable field), and `StarRocksPipelineBuilder`'s
`OrderBy` handling was confirmed to already be structurally protected by its
`TrackAndValidate`/`RequireColumn` machinery. Neither was touched.

## What changed

### `Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs`

**`BuildSearch`**: the `order = BuildOrder(...)` call previously ran *before* the join/no-join
branch that determines whether a `tableMap` exists, so it had no way to alias-qualify a joined
sort column or gate it against a joined type's `AllowedFields`. Moved the `BuildOrder` call
inside each branch (mirroring how `BuildWhere` is called separately in the joined vs. no-join
paths), passing the branch's `tableMap` (or `null` in the no-join case) plus `authz` through to
`BuildOrder`.

**`BuildOrder`** signature changed from `(schema, sorts)` to
`(schema, sorts, tableMap, authz)`. Body now: resolves each sort property the same way the
join/no-join case requires (`ResolveColumn(tableMap, ...)` vs `ResolveColumn(schema, ...)`),
skips unresolvable properties (preserving the pre-existing silent-skip behavior — this was never
a security boundary, just an unknown-property tolerance), then calls
`IsFieldAllowed(resolved, schema, tableMap, authz, out typeName)` on anything that *did*
resolve and throws `StarRocksQueryTranslationException` naming the type/field when disallowed.
Quoting also now branches on `tableMap` presence (`QuoteQualified` for joined, bare backtick
otherwise) — same convention already used everywhere else in the file for alias-qualified vs.
unqualified columns.

**`BuildGroupBy`**'s `orderSql` projection: `BuildGroupBy` already always has a non-null
`tableMap` (even with zero joins), so no signature change was needed there. Added the same
`IsFieldAllowed` check inline, in the same style as the existing `keyCols` projection just above
it in the file — throws `StarRocksQueryTranslationException` naming the type/field on a
disallowed reference. Preserved the existing `?? s.Property` fallback for unresolved properties
(pre-existing behavior, distinct concern from authorization).

Both throw sites use the plan's mandated exception type, `StarRocksQueryTranslationException` —
no new exception type — consistent with every other reject-on-reference check in this file. That
exception is already mapped to `RpcException(InvalidArgument)` by existing, untouched
`ObjectSearchGrpcService` exception handling.

### Tests added

**`Iverson.Server/Iverson.StarRocks.Tests/StarRocksQueryBuilderTests.cs`** (9 new tests):
- `BuildSearch_Sort_AllowedField_ProducesOrderBy` — allowed field still produces `ORDER BY`.
- `BuildSearch_Sort_RestrictedField_ThrowsTranslationException` — disallowed field throws.
- `BuildSearch_Sort_RestrictedField_ViaJoinedType_ThrowsTranslationException` — disallowed field
  on a joined type throws, message names both type and field.
- `BuildSearch_Sort_AllowedField_ViaJoinedType_ProducesQualifiedOrderBy` — allowed joined-type
  sort produces alias-qualified `` `articles`.`Title` `` SQL.
- `BuildSearch_Sort_NoAuthz_DoesNotEnforceFieldRestrictions` — no `authz` means no gate (matches
  every other `*_NoAuthz_DoesNotEnforceFieldRestrictions` test in the file).
- `BuildGroupBy_OrderBy_AllowedField_ProducesOrderBy`
- `BuildGroupBy_OrderBy_RestrictedField_ThrowsTranslationException`
- `BuildGroupBy_OrderBy_RestrictedField_ViaJoinedType_ThrowsTranslationException`
- `BuildGroupBy_OrderBy_NoAuthz_DoesNotEnforceFieldRestrictions`

**`Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`** (2 new tests, following
the file's existing "mock the search service to throw `StarRocksQueryTranslationException`,
assert `RpcException(InvalidArgument)`" pattern used for Tasks 2-4's other reject-on-reference
checks):
- `Search_RestrictedSortFieldRejectedByBuilder_TranslatesToInvalidArgument`
- `GroupBy_RestrictedOrderByFieldRejectedByBuilder_TranslatesToInvalidArgument`

## Test results

```
dotnet test Iverson.StarRocks.Tests
Passed! - Failed: 0, Passed: 223, Skipped: 0, Total: 223, Duration: 1m 33s

dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~ObjectSearchGrpcServiceTests"
Passed! - Failed: 0, Passed: 68, Skipped: 0, Total: 68, Duration: 2s
```

Both runs pristine (no warnings besides one pre-existing, unrelated `CS9113` in
`Iverson.Vector/QdrantVectorService.cs` that predates this change).

## Scope verification

- `git diff --stat` touches exactly 3 files: `StarRocksQueryBuilder.cs`,
  `StarRocksQueryBuilderTests.cs`, `ObjectSearchGrpcServiceTests.cs`.
- `BuildAggregate` and `Iverson.StarRocks/StarRocksPipelineBuilder.cs` are untouched (verified via
  `git diff`).
- Both `BuildSearch`'s join and no-join branches are gated; `BuildGroupBy`'s single order-by path
  (always has a `tableMap`) is gated.
- Exception type is `StarRocksQueryTranslationException` everywhere, no new exception type
  introduced.
- New tests exercise the actual throw (disallowed-field case), not just a bare pass-through
  assertion, for both `BuildSearch`/`BuildOrder` and `BuildGroupBy`.

## Commit

`7d0bff1` — `fix(starrocks): gate Search/GroupBy ORDER BY against field-level authorization
(closes ordering-oracle side channel)`

---

## Follow-up: Critical regression fix (reviewer round 2)

Task reviewer found a real regression in the `BuildGroupBy` half of the fix above:
`orderSql`'s `ResolveColumn(tableMap, s.Property) ?? s.Property` fell back to the **raw,
unresolved property string** when `ResolveColumn` failed (e.g. a typo'd field, or a
dot-qualified reference to a nonexistent type, like `"NoSuchType.Field"`). That raw string was
then passed into `IsFieldAllowed`, which — seeing a dot — computed `dotIdx >= 0` and executed
`tableMap!.Values.First(v => v.Alias == resolvedColumnOrAliasDotColumn[..dotIdx])`. Since
`JoinContext.Alias` is always the physical table name (e.g. `"authors"`), never the `TypeName`
(`"Author"`), no entry ever matches an unresolved dotted prefix, so `.First()` throws
`InvalidOperationException: Sequence contains no matching element` — not
`StarRocksQueryTranslationException`. `ObjectSearchGrpcService.GroupBy`'s catch blocks only
translate `StarRocksQueryTranslationException` to `RpcException(InvalidArgument)`, so this
`InvalidOperationException` would have surfaced as an unhandled/Internal gRPC error instead of
the graceful `InvalidArgument` every other field-level rejection in this plan produces. The
`BuildSearch`/`BuildOrder` half of the original fix did not have this bug — it already does
`if (resolved is null) continue;` *before* ever calling `IsFieldAllowed`, so an unresolvable
property is silently skipped rather than misrouted into the allow-check.

### Before/after verification

Added `BuildGroupBy_OrderBy_UnresolvableProperty_ThrowsTranslationException_NotInvalidOperationException`
(request: `OrderBy.Property = "NoSuchType.Field"`) and ran it against the pre-fix code first:

```
Failed Iverson.StarRocks.Tests.StarRocksQueryBuilderTests.BuildGroupBy_OrderBy_UnresolvableProperty_ThrowsTranslationException_NotInvalidOperationException [FAIL]
  Expected a <Iverson.StarRocks.StarRocksQueryTranslationException> to be thrown, but found <System.InvalidOperationException>:
System.InvalidOperationException: Sequence contains no matching element
   at System.Linq.Enumerable.First[TSource](IEnumerable`1 source, Func`2 predicate)
   at Iverson.StarRocks.StarRocksQueryBuilder.IsFieldAllowed(...)
   at Iverson.StarRocks.StarRocksQueryBuilder.<>c__DisplayClass4_0.<BuildGroupBy>b__2(SearchSort s)
```

This confirms the exact crash the reviewer identified.

### Fix applied

`Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs`, `BuildGroupBy`'s `orderSql`
projection — changed the resolution line from a silent `?? s.Property` fallback to a
resolve-or-throw, mirroring the pre-existing `keyCols` pattern a few lines above in the same
method:

```csharp
var resolved = ResolveColumn(tableMap, s.Property)
    ?? throw new StarRocksQueryTranslationException(
        $"Unknown or ambiguous ORDER BY property '{s.Property}'.");
```

This eliminates the crash path entirely (an unresolvable property now throws
`StarRocksQueryTranslationException` before `IsFieldAllowed` is ever called) and keeps every
rejection in the file on the one mandated exception type.

### Tests added

- `Iverson.Server/Iverson.StarRocks.Tests/StarRocksQueryBuilderTests.cs`:
  - `BuildGroupBy_OrderBy_UnresolvableProperty_ThrowsTranslationException_NotInvalidOperationException`
    — the regression test above; re-run against the fix and now passes (throws the correct
    type).
  - `BuildSearch_Sort_UnresolvableProperty_IsSilentlySkipped_NoException` — confirmation test
    (per reviewer's Minor note) locking in that `BuildSearch`/`BuildOrder`'s existing
    `continue`-before-`IsFieldAllowed` pattern already handles the same
    `"NoSuchType.Field"`-shaped input correctly (silently omitted from `ORDER BY`, no exception)
    — this was passing before the regression fix too, since `BuildOrder` was never broken.

### Test results (after fix)

```
dotnet test Iverson.StarRocks.Tests
Passed! - Failed: 0, Passed: 225, Skipped: 0, Total: 225, Duration: 46s

dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~ObjectSearchGrpcServiceTests"
Passed! - Failed: 0, Passed: 68, Skipped: 0, Total: 68, Duration: 772 ms
```

Both pristine. No RPC-layer test was added for this follow-up since the reviewer's ask was
scoped to the `BuildGroupBy`-level crash and its unit-level regression coverage; the existing
`GroupBy_RestrictedOrderByFieldRejectedByBuilder_TranslatesToInvalidArgument` RPC-layer test
(added in the first round) already proves `StarRocksQueryTranslationException` maps to
`InvalidArgument` at the RPC layer generally, and that mapping is unaffected by this fix (same
exception type, different message).

### Scope verification (this follow-up)

`git diff --stat` for this round touches exactly 2 files: `StarRocksQueryBuilder.cs` (4 lines:
the one-line resolve-or-throw change) and `StarRocksQueryBuilderTests.cs` (42 lines: 2 new
tests). `BuildAggregate` and `StarRocksPipelineBuilder.cs` remain untouched.

### Follow-up commit

`<pending — see final report>`
