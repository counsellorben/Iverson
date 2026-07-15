# Row/Field Authorization Evaluator Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source specs:**
- `docs/specs/2026-07-14-row-field-authorization-null-ownervalue-fix-design.md` (commit SHA: `a1d96f1`)
- `docs/specs/2026-07-14-row-field-authorization-key-column-exclusion-fix-design.md` (commit SHA: `991d0dd`)

**Goal:** Fix two independently-discovered gaps in `RowFieldAuthorizationEvaluator.Evaluate`, the shared authorization evaluator behind Parts 5a–5d: (1) `OwnershipRequired: true` can currently return with a null `OwnerValue` when the acting user's token lacks a `sub` claim, which crashes or narrowly bypasses ownership checks at various consumers instead of failing closed; (2) a `FieldPermission` naming the schema's key column can currently exclude it from `AllowedFields`, which would silently strip the key column from 5b/5c response masking. Both fixes are combined into one plan since they touch the same file in disjoint regions and share a test file.

**Architecture:** Both fixes are internal to `RowFieldAuthorizationEvaluator.Evaluate` — no changes to `AuthorizationDecision`'s shape, no changes to any of the ~14–19 downstream call sites across `Iverson.Api`/`Iverson.StarRocks`. Fix 1 adds a `sub`-presence guard to the existing ownership-required branch, returning the same `Denied` shape the method's other 3 denial branches already use. Fix 2 adds one filter step to the existing field-exclusion computation, so the key column's name can never enter the excluded set.

**Tech stack:** C# / .NET 10, xUnit + FluentAssertions.

---

## File Structure

- Modify: `Iverson.Server/Iverson.Api/Authorization/RowFieldAuthorizationEvaluator.cs` — both fixes
- Modify: `Iverson.Server/Iverson.Api/Authorization/IRowFieldAuthorizationEvaluator.cs` — `AllowedFields` doc-comment update (key-column fix only)
- Modify: `Iverson.Server/Iverson.Api.Tests/Authorization/RowFieldAuthorizationEvaluatorTests.cs` — 2 new tests

## Inherited from spec

The following were verified by `thorough-brainstorming` at spec-write time and are **not** re-verified here:

From the null-`OwnerValue` fix spec:
- `Evaluate`'s current structure: the `else if (!string.IsNullOrEmpty(rules.OwnerField))` branch is the only place `ownershipRequired` is set `true`; the 3 sibling `Denied` branches return the exact literal `new AuthorizationDecision(true, false, null, null, null)` (item 1)
- `AuthorizationDecision`'s constructor order: `(bool Denied, bool OwnershipRequired, string? OwnerFieldName, string? OwnerValue, IReadOnlySet<string>? AllowedFields)` (item 2)
- Every consumer of `decision.OwnerValue!`/`decision.OwnerFieldName!` gates the dereference behind `decision.OwnershipRequired` — zero call-site changes needed (item 3)
- `Value.ForString(null)` and `Conditions.MatchKeyword(field, null)` both throw `ArgumentNullException`; C# `null != null` is `false` (items 4–6)
- No existing test constructs a `ClaimsPrincipal`/`ClaimsIdentity` without a `sub` claim (item 7)
- `RowFieldAuthorizationEvaluatorTests.cs`'s established fixture pattern (item 8)
- Bypass-role callers are unaffected by this fix regardless of `sub` presence (item 9)

From the key-column-exclusion fix spec:
- `Evaluate`'s current `excluded` computation and the validity of inserting a `.Where(...)` between `.Select` and `.ToHashSet()` (item 1)
- `schema.KeyColumn.Name` is reachable inside `Evaluate` and PascalCase-consistent with `FieldPermission.FieldName` (item 2)
- No existing test configures a `FieldPermission` naming the key column (item 3)
- All 14 `AllowedFields` call sites use a generic containment check or pure pass-through, no special-casing that assumes the key column can be excluded (item 4)
- `StarRocksPipelineBuilder.ColumnsFor`'s own key-column re-seed doesn't interact with this fix (item 5)
- `RejectDisallowedFields`'s `exemptField` parameter is unaffected (item 6)
- `StarRocksQueryBuilder.IsFieldAllowed` has no key-column special-casing (item 7)
- `AllowedFields`'s XML doc comment currently describes the pre-fix (buggy) behavior (item 8)

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path / drift | `RowFieldAuthorizationEvaluator.cs`'s content is unchanged since both specs verified it (no drift since spec-write time; HEAD is exactly at the key-column spec's last-modifying commit) | `git log --oneline 991d0dd..HEAD -- docs/specs/...` returned no commits; fresh `Read` of the file matches both specs' cited code verbatim |
| 2 | File path / drift | `IRowFieldAuthorizationEvaluator.cs`'s content is unchanged since the key-column spec verified it | Fresh `Read` of the file; doc comment text matches the spec's cited "current text" exactly |
| 3 | Consumer impact (Cat 6) | Combining both patches into one simultaneous edit introduces no interaction beyond what each spec verified independently | Traced control flow: the `excluded`/`allowedFields` computation (fix 2's location) runs unconditionally after the ownership branch completes (fix 1's location) regardless of which of the ownership branch's 3 paths (bypass / ownership-required-with-valid-sub / new early-return) was taken — the two fixes occupy disjoint, non-branching-into-each-other regions of the same method |
| 4 | File path / test structure | `RowFieldAuthorizationEvaluatorTests.cs` is exactly 408 lines; the last existing test (`Evaluate_WriteActionUsesWritableRolesNotReadableRoles`) closes at line 407, class closes at 408 — new tests are appended before the final `}` | `wc -l` + `Read` of lines 395-408 |
| 5 | Function signature | `SchemaWithAuthorization(AuthorizationRules? authorization)`'s schema has `KeyColumn = ColumnDescriptor("Id", "INT", false)`, `ScalarColumns = ["Name", "OwnerId"]`, no FK/vector/chunk fields | `Read` of the helper, lines 20-38 |
| 6 | Function signature | `ActingUser(string sub, params string[] groups)` unconditionally includes a `sub` claim — the null-`OwnerValue` test cannot use this helper and must construct its `ClaimsPrincipal` inline | `Read` of the helper, lines 13-18 |
| 7 | Code-in-plan validity | The key-column-exclusion test needs **two** `FieldPermission` entries (one naming `"Id"`, one naming `"Name"`), not one — a single `FieldPermission` naming only `"Id"` makes `excluded.Count` drop to 0 after the key-column filter removes it, skipping the `if (excluded.Count > 0)` block entirely and leaving `AllowedFields: null` (correct behavior — "nothing ends up restricted" — but not what the original single-`FieldPermission` test shape from the spec would have asserted against). Hand-traced with a second `FieldPermission` on `"Name"`: `excluded` after the key-column filter = `{"Name"}` (non-empty) → `allowedFields` = `allFields {"Id","Name","OwnerId"} − {"Name"}` = `{"Id","OwnerId"}` → assertions `AllowedFields.Should().Contain("Id")` and `.NotContain("Name")` both hold | Hand-trace against `SchemaWithAuthorization`'s exact field list (assumption 5) and the fixed `excluded` computation |
| 8 | Test/build command | `cd Iverson.Server && dotnet build Iverson.Api.Tests/Iverson.Api.Tests.csproj` then `dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~RowFieldAuthorizationEvaluatorTests"` is a working invocation (used successfully earlier this session against this exact test project) | Prior successful run this session |
| 9 | Commit convention | `fix(api): ...` is the established prefix for authorization-area fixes in this file's own history | `git log --oneline --all \| grep -iE "fix\(api\).*authoriz"` → `991ba8b fix(api): use case-insensitive comparer for authorization constraints dictionary to close joined-type-casing bypass` |
| 10 | Consumer impact (Cat 6) | `RowFieldAuthorizationEvaluator` is the sole production implementation of `IRowFieldAuthorizationEvaluator` — both fixes' guarantees hold everywhere the interface is consumed, not just for one of several implementations | `grep -rln ": IRowFieldAuthorizationEvaluator" Iverson.Server/Iverson.Api Iverson.Server/Iverson.StarRocks Iverson.Server/Iverson.Vector` → only `RowFieldAuthorizationEvaluator.cs` |

## Tasks

### Task 1: Fix null-`OwnerValue` and key-column-exclusion gaps in `RowFieldAuthorizationEvaluator`

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Authorization/RowFieldAuthorizationEvaluator.cs`
- Modify: `Iverson.Server/Iverson.Api/Authorization/IRowFieldAuthorizationEvaluator.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/Authorization/RowFieldAuthorizationEvaluatorTests.cs`

- [ ] **Step 1: Apply both fixes to `RowFieldAuthorizationEvaluator.cs`**

Replace the `Evaluate` method body with:
```csharp
public AuthorizationDecision Evaluate(SchemaDescriptor schema, ClaimsPrincipal? actingUser, AuthorizationAction action)
{
    var rules = schema.Authorization;
    if (rules is null)
        return new AuthorizationDecision(true, false, null, null, null);

    if (actingUser is null)
        return new AuthorizationDecision(true, false, null, null, null);

    var userGroups = actingUser.FindAll("groups").Select(c => c.Value).ToHashSet();
    var bypass = rules.RowPermissions.Any(p => userGroups.Contains(p.Role) && action switch
    {
        AuthorizationAction.Read   => p.CanReadAll,
        AuthorizationAction.Write  => p.CanWriteAll,
        AuthorizationAction.Delete => p.CanDeleteAll,
        _ => false
    });

    bool ownershipRequired;
    string? ownerFieldName = null, ownerValue = null;

    if (bypass)
    {
        ownershipRequired = false;
    }
    else if (!string.IsNullOrEmpty(rules.OwnerField))
    {
        var sub = actingUser.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(sub))
            return new AuthorizationDecision(true, false, null, null, null);

        ownershipRequired = true;
        ownerFieldName = rules.OwnerField;
        ownerValue = sub;
    }
    else
    {
        return new AuthorizationDecision(true, false, null, null, null);
    }

    IReadOnlySet<string>? allowedFields = null;
    if (action != AuthorizationAction.Delete && rules.FieldPermissions.Count > 0)
    {
        var excluded = rules.FieldPermissions
            .Where(fp =>
            {
                var roles = action == AuthorizationAction.Read ? fp.ReadableRoles : fp.WritableRoles;
                return roles.Count > 0 && !roles.Any(userGroups.Contains);
            })
            .Select(fp => fp.FieldName)
            .Where(f => !string.Equals(f, schema.KeyColumn.Name, StringComparison.OrdinalIgnoreCase))
            .ToHashSet();

        if (excluded.Count > 0)
        {
            var allFields = new[] { schema.KeyColumn.Name }
                .Concat(schema.ScalarColumns.Select(c => c.Name))
                .Concat(schema.FkColumns.Select(fk => fk.ColumnName))
                .Concat(schema.VectorFields.Select(v => v.PropertyName))
                .Concat(schema.ChunkFields.Select(c => c.PropertyName));
            allowedFields = allFields.Where(f => !excluded.Contains(f)).ToHashSet();
        }
    }

    return new AuthorizationDecision(false, ownershipRequired, ownerFieldName, ownerValue, allowedFields);
}
```

- [ ] **Step 2: Update the `AllowedFields` doc comment in `IRowFieldAuthorizationEvaluator.cs`**

Change:
```csharp
    /// <summary>
    /// Null means unrestricted. Non-null is the full set of field names the caller may access for
    /// this action — the key column, every scalar column, every FK column, and every vector/chunk
    /// field's source property name — minus whichever of those a <c>FieldPermission</c> excluded.
    /// </summary>
```
to:
```csharp
    /// <summary>
    /// Null means unrestricted. Non-null is the full set of field names the caller may access for
    /// this action — the key column, every scalar column, every FK column, and every vector/chunk
    /// field's source property name — minus whichever of those a <c>FieldPermission</c> excluded.
    /// The key column itself is always included, even if a <c>FieldPermission</c> names it.
    /// </summary>
```

- [ ] **Step 3: Add both regression tests**

Append to `RowFieldAuthorizationEvaluatorTests.cs`, before the class's closing `}`:
```csharp
[Fact]
public void Evaluate_OwnershipRequiredWithNoSubClaim_ReturnsDenied()
{
    var rules = new AuthorizationRules(
        "OwnerId",
        new List<RowPermission>(),
        new List<FieldPermission>());
    var schema = SchemaWithAuthorization(rules);
    var user = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim> { new("groups", "admin") }, "test"));

    var result = _evaluator.Evaluate(schema, user, AuthorizationAction.Read);

    result.Denied.Should().BeTrue();
    result.OwnershipRequired.Should().BeFalse();
    result.OwnerFieldName.Should().BeNull();
    result.OwnerValue.Should().BeNull();
    result.AllowedFields.Should().BeNull();
}

[Fact]
public void Evaluate_FieldLevelExclusionTargetingKeyColumn_NeverExcludesKeyColumn()
{
    var rules = new AuthorizationRules(
        "OwnerId",
        new List<RowPermission>
        {
            new("admin", CanReadAll: true, CanWriteAll: false, CanDeleteAll: false)
        },
        new List<FieldPermission>
        {
            new("Id", new List<string> { "superadmin" }, new List<string>()),
            new("Name", new List<string> { "superadmin" }, new List<string>())
        });
    var schema = SchemaWithAuthorization(rules);
    var user = ActingUser("user123", "admin");

    var result = _evaluator.Evaluate(schema, user, AuthorizationAction.Read);

    result.Denied.Should().BeFalse();
    result.AllowedFields.Should().NotBeNull();
    result.AllowedFields.Should().Contain("Id");
    result.AllowedFields.Should().NotContain("Name");
}
```

- [ ] **Step 4: Run tests and commit**
```bash
cd Iverson.Server
dotnet build Iverson.Api.Tests/Iverson.Api.Tests.csproj
dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~RowFieldAuthorizationEvaluatorTests"
git add Iverson.Server/Iverson.Api/Authorization/RowFieldAuthorizationEvaluator.cs Iverson.Server/Iverson.Api/Authorization/IRowFieldAuthorizationEvaluator.cs Iverson.Server/Iverson.Api.Tests/Authorization/RowFieldAuthorizationEvaluatorTests.cs
git commit -m "fix(api): guard against null OwnerValue and key-column exclusion in RowFieldAuthorizationEvaluator"
```

## Known issues inherited from spec

- **Whether Authentik (or any future IdP swap) can actually issue a validly-signed token without a `sub` claim in production** is not verified by either spec — `sub` is a required claim per the OIDC Core spec for ID tokens, but the `"ActingUser"` JWT bearer scheme validates a token supplied via a custom header, and nothing in this codebase enforces it's specifically an OIDC ID token rather than a bare access token. The null-`OwnerValue` fix is defense-in-depth regardless of whether the gap is reachable today with a correctly configured Authentik.
- **A `FieldPermission` excluding the owner field could still mask it out of 5b/5c read responses**, since (unlike the write path) no masking call site exempts `OwnerField` the way it exempts the key column after this fix. Lower severity than the key-column gap (the caller already knows their own identity); not addressed here.
