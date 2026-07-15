# Row/Field Authorization: Key-Column-Exclusion Fix

## Context

`RowFieldAuthorizationEvaluator.Evaluate` (Part 5a) computes `AllowedFields` by starting from every field on a schema (key column, scalar columns, FK columns, vector/chunk source properties) and removing whatever a `FieldPermission` excludes for the caller's role. Nothing prevents a `FieldPermission` from naming the key column, so a schema admin can (accidentally or deliberately) configure a role restriction that excludes it.

This is documented as a known-but-unfixed gap across Parts 5b/5c/5d's specs and plans: 5b's `ObjectMapping`/`ObjectRetrieval` response masking and 5c's `Search` response masking would silently strip the key column from every response to a caller whose role trips that `FieldPermission` — the caller can no longer correlate the response with the entity they asked for. The only place in the codebase that currently defends against this is `StarRocksPipelineBuilder.ColumnsFor` (Part 5c, Pipeline-specific), which manually re-seeds the key column into its own column set before applying the `AllowedFields` filter — its own comment already asserts "the key column is never excluded, per `IRowFieldAuthorizationEvaluator`'s existing contract," which is currently false; that comment describes an aspiration, not the evaluator's actual behavior.

Part 5d's Qdrant masking is unaffected by this gap — its `exemptField: "Key"` constant protects the Qdrant point-identifier payload key independent of `AllowedFields` membership — so this fix doesn't touch 5d.

## Goal

`RowFieldAuthorizationEvaluator.Evaluate` never excludes the schema's key column from `AllowedFields`, regardless of how `FieldPermissions` is configured — making the evaluator's contract match what `StarRocksPipelineBuilder.ColumnsFor`'s comment already (currently, inaccurately) claims it is.

## Design

In `Iverson.Server/Iverson.Api/Authorization/RowFieldAuthorizationEvaluator.cs`, the `excluded` computation gains one filter step, so the key column's name can never enter the excluded set in the first place:

```csharp
var excluded = rules.FieldPermissions
    .Where(fp =>
    {
        var roles = action == AuthorizationAction.Read ? fp.ReadableRoles : fp.WritableRoles;
        return roles.Count > 0 && !roles.Any(userGroups.Contains);
    })
    .Select(fp => fp.FieldName)
    .Where(f => !string.Equals(f, schema.KeyColumn.Name, StringComparison.OrdinalIgnoreCase))
    .ToHashSet();
```

`StringComparison.OrdinalIgnoreCase` matches the exact comparison style `RegisterSchema`'s existing `owner_field` validation already uses for name equality elsewhere in this codebase.

`IRowFieldAuthorizationEvaluator.cs`'s `AllowedFields` XML doc comment currently reads "...minus whichever of those a `FieldPermission` excluded" — grammatically including the key column among what can be excluded, which described the real (buggy) behavior until now. Updating it to state the key column is always included closes the gap between the doc and the code (`StarRocksPipelineBuilder.ColumnsFor`'s comment already assumed this was true — this makes it true, and makes the two comments agree):

```csharp
/// <summary>
/// Null means unrestricted. Non-null is the full set of field names the caller may access for
/// this action — the key column, every scalar column, every FK column, and every vector/chunk
/// field's source property name — minus whichever of those a <c>FieldPermission</c> excluded.
/// The key column itself is always included, even if a <c>FieldPermission</c> names it.
/// </summary>
```

No other file changes. Every downstream consumer of `AllowedFields` (14 call sites across `Iverson.Api` and `Iverson.StarRocks` — masking, reject-on-reference, and the write-path field rejection) derives its behavior from this one computation, so all of them are fixed by this single change with no call-site edits needed.

## One adjacent observation — explicitly not in scope

None of the 5b/5c *masking* call sites exempt `OwnerField` the way the write-path `RejectDisallowedFields` call already does (`exemptField: decision.OwnerFieldName`) — a `FieldPermission` excluding the owner field could theoretically mask it out of a caller's own read response too. Lower severity than the key-column gap (the caller already knows their own identity), not part of this fix, noted only for a possible future investigation.

## Verified assumptions

| # | Assumption | Verified |
|---|---|---|
| 1 | `Evaluate`'s current `excluded` computation is exactly `rules.FieldPermissions.Where(...).Select(fp => fp.FieldName).ToHashSet()`, and inserting a `.Where(...)` between `.Select` and `.ToHashSet()` is syntactically valid | Read `RowFieldAuthorizationEvaluator.cs` in full |
| 2 | `schema.KeyColumn.Name` is reachable inside `Evaluate` (schema is the method's own parameter) and uses the same PascalCase convention as `FieldPermission.FieldName` values elsewhere in the codebase | Read `RowFieldAuthorizationEvaluator.cs`; `ColumnDescriptor.Name` is populated from `PropertyDescriptor.Name` (PascalCase) throughout `SchemaBuilder.BuildDescriptor` |
| 3 | No existing test in `RowFieldAuthorizationEvaluatorTests.cs` configures a `FieldPermission` naming the key column ("Id") and expects it excluded — so this fix cannot regress a currently-passing test | `grep -n "KeyColumn\|\"Id\""` across the file — the only `"Id"`-related assertions are `AllowedFields.Should().Contain("Id")` in two existing tests (both currently pass because those tests' `FieldPermission`s name a different field); none names `"Id"` in a `FieldPermission` |
| 4 | All 14 `AllowedFields` call sites (`ObjectMappingGrpcService.cs` ×4, `ObjectRetrievalGrpcService.cs` ×2, `AuthorizationFieldMasking.cs` ×1, `ObjectSearchGrpcService.cs` ×5, `ObjectSearchGrpcService.cs`'s `AuthorizationConstraint` pass-through construction ×2, `StarRocksQueryBuilder.IsFieldAllowed`, `StarRocksPipelineBuilder.ColumnsFor`) use a generic `allowedFields.Contains(...)`/`!allowedFields.Contains(...)` check (or a pure pass-through with no check of its own) with no special-casing that assumes the key column can legitimately be excluded | Read every call site listed; none branches differently based on whether the excluded field happens to be the key column |
| 5 | `StarRocksPipelineBuilder.ColumnsFor`'s own key-column re-seed (`cols[schema.KeyColumnName] = schema.KeyColumnName`) doesn't interact with this fix in a way that could double-process or conflict | `StarRocksQuerySchema.ColumnNames` (the collection `ColumnsFor`'s loop iterates) is populated from `d.ScalarColumns.Select(c => c.Name)` in `SchemaBuilder.ToStarRocksQuerySchema` — the key column is never in `ColumnNames`, so the loop's `AllowedFields` check never applies to it either way; `ColumnsFor`'s re-seed and this fix are fully independent, not interacting |
| 6 | `AuthorizationFieldMasking.RejectDisallowedFields`'s existing `exemptField: decision.OwnerFieldName` parameter is unaffected by the key column now always being in `AllowedFields` | Read the method: the check is `!allowedFields.Contains(canonical) && canonical != exemptField` — an OR-style exemption (allowed via set membership OR explicit exempt name); the key column will now always satisfy the set-membership branch, independent of whatever `exemptField` is set to |
| 7 | `StarRocksQueryBuilder.IsFieldAllowed` has no key-column special-casing that would conflict with this fix | Read the method: `return authz is null || !authz.TryGetValue(typeName, out var constraint) || constraint.AllowedFields is null || constraint.AllowedFields.Contains(bareField);` — generic containment check, no special-casing |
| 8 | `AuthorizationDecision.AllowedFields`'s XML doc comment currently describes the buggy (pre-fix) behavior and needs updating to stay accurate | Read `IRowFieldAuthorizationEvaluator.cs:21-25` — current text: "...minus whichever of those a `FieldPermission` excluded," grammatically including the key column among what's excludable |

## Testing plan

One new test in `Iverson.Server/Iverson.Api.Tests/Authorization/RowFieldAuthorizationEvaluatorTests.cs`, matching the file's existing style (see `Evaluate_FieldLevelNonMatchingRoleExcludesField` for the closest precedent): an `AuthorizationRules` with a `FieldPermission` naming the key column (`"Id"`) with `ReadableRoles` the acting user's role doesn't satisfy — asserts `AllowedFields.Should().Contain("Id")` despite the `FieldPermission` targeting it, alongside the existing assertion style for the rest of the decision shape.

## Known issues / accepted as out of scope

- **The `OwnerField`-masking observation above** — a `FieldPermission` excluding the owner field could still mask it out of 5b/5c read responses, since (unlike the write path) no masking call site exempts it. Lower severity than the key-column gap; not addressed here.
