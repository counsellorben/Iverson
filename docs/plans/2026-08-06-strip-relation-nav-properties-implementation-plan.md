# Strip Relation Nav Properties Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-06-strip-relation-nav-properties-design.md` (commit SHA: `63011d9d2321f7363adcf5c21ef69e726f98b74d`)

**Goal:** Strip relation navigation properties from write payloads server-side after FK normalization, and turn a disagreeing FK/nav pair into an explicit error.

**Architecture:** Both changes live in `RelationValidator`, the single choke point every payload-carrying write passes through. A new `StructFieldAccess.RemoveField` mirrors the existing `SetField`. `ValidateNestedObject` splits so that reading a nested key is separable from enforcing that the object is a bare reference — conflict detection needs the former without the latter.

**Tech stack:** C# / .NET 10, xunit + FluentAssertions + NSubstitute, protobuf `Struct` payloads.

---

## Global Constraints

- **The name guard is load-bearing.** `PropertyName` and `ForeignKey` are not guaranteed distinct (spec A7). When they collide, the nav property and the FK are the *same payload key*: stripping would delete the FK, and conflict detection would misread the FK's GUID strings as embedded objects. Both operations must be gated on `navIsDistinctKey`.
- **Nav properties arrive fully hydrated** (spec A17). The key-only rule must never gate cross-checking — only the FK-absent normalize path.
- **Comparison is on parsed `Guid` values, never raw strings.**
- **Commit messages:** plain lowercase imperative, no Conventional-Commits prefix (verified: dominant in the last 20 commits; the `chore(deps)` entries are Dependabot's).

## File Structure

**Modify**
- `Iverson.Server/Iverson.Api/Grpc/StructFieldAccess.cs` — add `RemoveField` alongside `SetField` (Task 1)
- `Iverson.Server/Iverson.Api/Grpc/RelationValidator.cs` — stripping in the top-level loop (Task 1); nested-key split and conflict detection (Task 2)

**Test**
- `Iverson.Server/Iverson.Api.Tests/Grpc/RelationValidatorTests.cs` — both tasks add cases here

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and NOT re-verified here:

- A1 — the top-level loop walks every declared relation, so a post-switch strip covers all 4 kinds (`RelationValidator.cs:18-40`)
- A2 — `Candidates` is public; `SetField` exists; no `RemoveField` yet (`StructFieldAccess.cs:10,52`)
- A3 — `ValidateSingleRelation` returns at the FK check, so it needs restructuring (`RelationValidator.cs:60-65`)
- A4 — `ValidateCollectionRelation` returns at the FK-list check (`RelationValidator.cs:91-100`)
- A5 — `ValidateNestedObject` returns the resolved key as `string?` (`RelationValidator.cs:138`)
- A6 — all payload write paths route through the validator; `EnrichmentConsumer` writes columns directly and carries no client payload
- A7 — **FAILED:** relation `PropertyName` CAN equal its `ForeignKey` (Python `core.py:227,105`; TypeScript `core.ts:287,98`; Java `SchemaRegistrar.java:281,336`)
- A8 — no consumer outside the 3 stores reads nav keys from the event payload
- A9 — no existing test asserts nav retention; `EntityRelationResolverTests.cs:55` is the read path
- A10 — the read path re-fetches related entities and never reads a stored nav property
- A11 — **FAILED:** `GetMany` does preserve key order; the hydrated collection can be a strict subset of the FK list (`GraphAssembler.cs:116`)
- A12 — authorization runs before validation and never re-adds relation names after
- A13 — FK values, nested keys, and FK-list elements are all `StringValue`
- A14 — write RPCs do not echo the request payload back
- A15 — the 4 relation kinds are the complete set; the switch has a throwing `default`
- A16 — every client's payload keys fall within `Candidates`' range
- A17 — **FAILED:** a nav property arriving alongside a present FK is a fully hydrated entity, not a bare key reference (`EntityRelationResolver.cs:96,137,176`)

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time:

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | All three touched files exist at the cited paths | `StructFieldAccess.cs` 2278 B, `RelationValidator.cs` 8354 B, `RelationValidatorTests.cs` 14113 B |
| P2 | Signature | `Candidates` is `public static IEnumerable<string>`, yielding canonical then first-char-lowered | `StructFieldAccess.cs:10-15` |
| P3 | Signature | `Struct.Fields.Remove(key)` is available — `SetField` already calls it | `StructFieldAccess.cs:54-56` |
| P4 | Signature | `private string? ValidateNestedObject(Struct, string, string, string, List<string>)` | `RelationValidator.cs:149` |
| P5 | Signature | Both per-kind methods are `private`, invoked only from the top-level switch | declared `RelationValidator.cs:49,98`; called `:24,:28` |
| P6 | Signature | Both the FK side and the nested-key side already call `Guid.TryParse`, so parsed values are in hand | `RelationValidator.cs:62`, `:148` |
| P7 | Signature | `RelationDescriptor(PropertyName, Kind, RelatedTypeName, ForeignKey)` — all strings | `Schema/SchemaDescriptor.cs:61-65` |
| P8 | Command | `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj`; baseline **627 passed / 0 failed / 627 total** | run on this tree at `ae73e0a` |
| P9 | Command | Commit convention is plain lowercase imperative, no CC prefix | `git log --oneline -20`; only Dependabot entries use `chore(deps)` |
| P10 | Ordering | Task 1 introduces `navIsDistinctKey`; Task 2 consumes it. Task 1 references nothing Task 2 creates | Task 1 touches only `RemoveField` + the loop |
| P11 | Code validity | Tests use xunit `[Fact]` + FluentAssertions; helper `MakeSchemaWithRelation(RelationKind, bool fkNullable)` | `RelationValidatorTests.cs:25-35` |
| P12 | Code validity | `Value.ForStruct` / `ForList` / `ForNull` / `ForString` are the constructors existing tests use | `RelationValidatorTests.cs:41-42,186-190,287` |
| P13 | Code validity | Key column resolves via `registry.Get(relatedTypeName)?.KeyColumn.Name ?? "Id"`, so a substituted registry falls back to `"Id"` | `RelationValidator.cs:141-142`; test registry built from a substituted executor at `RelationValidatorTests.cs:20-22` |
| P14 | Consumer impact | `IRelationValidator.ValidateAndNormalizeRelations(Struct, SchemaDescriptor)` is unchanged, so the 4 production call sites and the `Substitute.For<IRelationValidator>()` mock are unaffected | signature untouched by both tasks |
| P15 | Consumer impact | Nothing outside `RelationValidator.cs` calls the private methods being split | repo-wide grep for all three names returned no hits outside that file |
| P16 | Consumer impact (sibling sweep) | **Every existing test whose payload carries a relation `PropertyName` key still holds once that key is stripped.** 12 such tests; none asserts the nav key remains — all assert FK keys or thrown message text | payload-key assertions at `RelationValidatorTests.cs:190,191,308,329,350`; message assertions at `:158,:254` |
| P17 | Consumer impact | `AuthorizationFieldMaskingTests.EnforceWriteAuthorization_FieldRestrictedCaller_NestedRelationStructNotRejected` exercises masking only and never invokes the validator, so stripping cannot affect it | `AuthorizationFieldMaskingTests.cs:141-171` |

## Tasks

### Task 1: Strip nav properties from write payloads

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/StructFieldAccess.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/RelationValidator.cs:18-40`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/RelationValidatorTests.cs`

**Interfaces:**
- Produces: the `navIsDistinctKey` local in `ValidateAndNormalizeRelations`'s loop, which Task 2 passes to both per-kind methods.

- [ ] **Step 1: Add `RemoveField` to `StructFieldAccess`**

Place it directly after `SetField`, which it mirrors:

```csharp
    /// <summary>
    /// Removes <paramref name="canonicalName"/> and every case variant of it. Mirrors
    /// <see cref="SetField"/>'s variant handling: clients send camelCase while schemas declare
    /// PascalCase, so removing only the declared spelling would leave the camelCase key behind.
    /// </summary>
    public static void RemoveField(Struct s, string canonicalName)
    {
        foreach (var candidate in Candidates(canonicalName))
            s.Fields.Remove(candidate);
    }
```

- [ ] **Step 2: Compute the name guard and strip in the top-level loop**

In `ValidateAndNormalizeRelations`, inside `foreach (var relation in schema.Relations)`, add the guard before the `switch` and the strip after it. The `switch` body is unchanged.

```csharp
        foreach (var relation in schema.Relations)
        {
            // When PropertyName and ForeignKey collide — Python, TypeScript and Java can all
            // produce that for ManyToMany — the "nav property" and the foreign key are the SAME
            // payload key. There is no separate object to strip, and stripping would delete the
            // foreign key itself.
            var navIsDistinctKey = !string.Equals(
                relation.PropertyName, relation.ForeignKey, StringComparison.OrdinalIgnoreCase);

            switch (relation.Kind)
            {
                // ... unchanged ...
            }

            // The stores already ignore nav properties; removing them here keeps them out of the
            // Kafka event body, which is the only place they were still observable.
            if (navIsDistinctKey)
                StructFieldAccess.RemoveField(payload, relation.PropertyName);
        }
```

The per-kind methods need no change in this task. In the collision case with the FK absent, the FK and nav lookups hit the same absent key, so existing behavior is already correct; the guard only becomes load-bearing for the nav *read* in Task 2.

- [ ] **Step 3: Add tests**

Append to `RelationValidatorTests.cs`, following the file's existing style:

1. `ManyToOne_NavPropertyStrippedAndForeignKeySurvives` — `Author` embedded reference with a null FK; assert `AuthorId` present with the nested key and `payload.Fields.Should().NotContainKey("Author")`.
2. `ManyToOne_CamelCaseNavPropertyStripped` — payload carries `author` (camelCase); assert neither `author` nor `Author` remains.
3. `ManyToMany_NavListStripped` — `Tags` list of references; assert `TagIds` populated and `Tags` gone.
4. `OneToMany_NavPropertyStripped` — `Author` list on a `OneToMany` relation; assert it is removed and no FK is written.
5. `PropertyNameEqualsForeignKey_KeyNotStripped` — schema with `RelationDescriptor("TagIds", ManyToMany, "Tag", "TagIds")` and a payload carrying `TagIds` as a GUID list; assert `TagIds` **survives** with its values intact. This is the A7 regression guard: without the name guard the FK is deleted.

- [ ] **Step 4: Run the suite**

```bash
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

Expect **632 passed / 0 failed** (627 baseline + 5). Run it in the foreground and wait for it to finish.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.Api/Grpc/StructFieldAccess.cs Iverson.Server/Iverson.Api/Grpc/RelationValidator.cs Iverson.Server/Iverson.Api.Tests/Grpc/RelationValidatorTests.cs
git commit -m "strip relation nav properties from write payloads"
```

---

### Task 2: Error when the foreign key and nav property disagree

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/RelationValidator.cs:49-190`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/RelationValidatorTests.cs`

**Interfaces:**
- Consumes: Task 1's `navIsDistinctKey` local, now passed into both per-kind methods.

- [ ] **Step 1: Extract key-column resolution and a non-enforcing key reader**

Add both above `ValidateNestedObject`:

```csharp
    private string KeyColumnNameFor(string relatedTypeName) =>
        registry.Get(relatedTypeName)?.KeyColumn.Name ?? "Id";

    /// <summary>
    /// Reads a nested object's key as a parsed <see cref="Guid"/>, or null when it carries no
    /// usable one. Records NO error: callers decide what an unusable key means. The normalize
    /// path treats it as an unsupported cascade-insert; conflict detection treats it as "no
    /// second opinion" and lets the foreign key stand.
    /// </summary>
    private Guid? ReadNestedKey(Struct nested, string relatedTypeName)
    {
        var keyValue = StructFieldAccess.GetFieldValue(nested, KeyColumnNameFor(relatedTypeName));
        return Guid.TryParse(keyValue?.StringValue, out var key) && key != Guid.Empty ? key : null;
    }
```

- [ ] **Step 2: Rebuild `ValidateNestedObject` on top of the reader**

Its contract is unchanged — it still enforces the key-only rule and returns `string?` — but it now delegates key reading. The `extras` block is unchanged from its current form.

```csharp
    /// <returns>
    /// The nested entity's key when it is a valid bare existing-entity reference, or null when it
    /// is not — in which case an error has been recorded. Used by the normalize path only;
    /// conflict detection calls <see cref="ReadNestedKey"/> directly, because a cross-checked nav
    /// property arrives fully hydrated and must not be held to the key-only rule.
    /// </returns>
    private string? ValidateNestedObject(
        Struct nested, string path, string relatedTypeName, string foreignKey, List<string> errors)
    {
        var keyColumnName = KeyColumnNameFor(relatedTypeName);
        var nestedKey     = ReadNestedKey(nested, relatedTypeName);

        if (nestedKey is null)
        {
            errors.Add(
                $"'{path}': embedded new entities are not supported — create the related " +
                $"{relatedTypeName} first, then reference it by '{foreignKey}' (GUID) or by an " +
                $"embedded object containing only '{keyColumnName}'.");
            return null;
        }

        // ... existing `extras` block, unchanged, using keyColumnName and nestedKey ...

        return nestedKey.Value.ToString();
    }
```

`nestedKey` changes type from `string?` to `Guid?` here. The `extras` block uses it only inside
string interpolation, so it still compiles; the only visible difference is that the error message
now prints the canonical GUID form rather than the raw payload spelling. No test asserts that
substring.

- [ ] **Step 3: Cross-check in `ValidateSingleRelation`**

Add the `bool navIsDistinctKey` parameter and stop returning at the FK check. Read the nav value once, gated on the guard.

```csharp
    private void ValidateSingleRelation(
        Struct payload,
        RelationDescriptor relation,
        SchemaDescriptor schema,
        bool navIsDistinctKey,
        List<string> errors)
    {
        var fkCol = schema.ScalarColumns.FirstOrDefault(c =>
            string.Equals(c.Name, relation.ForeignKey, StringComparison.OrdinalIgnoreCase));

        var fkValue  = StructFieldAccess.GetFieldValue(payload, relation.ForeignKey);
        var navValue = navIsDistinctKey
            ? StructFieldAccess.GetFieldValue(payload, relation.PropertyName)
            : null;

        if (fkValue is not null && fkValue.KindCase != Value.KindOneofCase.NullValue)
        {
            if (!Guid.TryParse(fkValue.StringValue, out var fk) || fk == Guid.Empty)
            {
                errors.Add($"'{relation.ForeignKey}': must be a valid non-empty GUID.");
                return;
            }

            // Cross-check only. The nav object arrives fully hydrated from the read path, so the
            // key-only rule must not apply; an unreadable key just means no second opinion.
            if (navValue?.StructValue is { } crossCheck
                && ReadNestedKey(crossCheck, relation.RelatedTypeName) is { } navKey
                && navKey != fk)
            {
                errors.Add(
                    $"'{relation.PropertyName}' references '{navKey}' but '{relation.ForeignKey}' " +
                    $"is '{fk}'. Remove one, or make them agree.");
            }

            return;
        }

        if (navValue?.StructValue is { } nested)
        {
            // ... existing fkCol-null guard, ValidateNestedObject call and SetField, unchanged ...
        }

        if (fkCol is null || !fkCol.IsNullable)
            // ... existing required-relation error, unchanged ...
    }
```

- [ ] **Step 4: Cross-check in `ValidateCollectionRelation`**

Same parameter, same shape. Track FK validity in a local rather than inspecting `errors.Count`, which may already hold errors from an earlier relation.

```csharp
    private void ValidateCollectionRelation(
        Struct payload, RelationDescriptor relation, bool navIsDistinctKey, List<string> errors)
    {
        var fkValue  = StructFieldAccess.GetFieldValue(payload, relation.ForeignKey);
        var navValue = navIsDistinctKey
            ? StructFieldAccess.GetFieldValue(payload, relation.PropertyName)
            : null;

        if (fkValue?.ListValue is { } fkList)
        {
            var fkKeys     = new HashSet<Guid>();
            var fkAllValid = true;

            for (var i = 0; i < fkList.Values.Count; i++)
            {
                var str = fkList.Values[i].StringValue;
                if (!Guid.TryParse(str, out var key) || key == Guid.Empty)
                {
                    errors.Add($"'{relation.ForeignKey}[{i}]': invalid GUID '{str}'.");
                    fkAllValid = false;
                }
                else
                {
                    fkKeys.Add(key);
                }
            }

            // An empty nav list means "not supplied" and the FK list wins. A non-empty one is a
            // second opinion and must agree exactly.
            if (fkAllValid && navValue?.ListValue is { } crossList && crossList.Values.Count > 0)
            {
                var navKeys = new HashSet<Guid>();
                foreach (var item in crossList.Values)
                    if (item.StructValue is { } nested
                        && ReadNestedKey(nested, relation.RelatedTypeName) is { } key)
                        navKeys.Add(key);

                if (!navKeys.SetEquals(fkKeys))
                    errors.Add(
                        $"'{relation.PropertyName}' and '{relation.ForeignKey}' disagree. " +
                        $"Remove one, or make them agree.");
            }

            return;
        }

        if (navValue?.ListValue is { } navList)
        {
            // ... existing normalize block, unchanged ...
        }
        // empty collection is valid
    }
```

- [ ] **Step 5: Pass the guard at both call sites**

In the top-level `switch`, forward `navIsDistinctKey`:

```csharp
                case RelationKind.ManyToOne:
                case RelationKind.OneToOne:
                    ValidateSingleRelation(payload, relation, schema, navIsDistinctKey, errors);
                    break;

                case RelationKind.ManyToMany:
                    ValidateCollectionRelation(payload, relation, navIsDistinctKey, errors);
                    break;
```

- [ ] **Step 6: Add tests**

1. `ManyToOne_HydratedNavObjectMatchingFk_Accepted` — valid `AuthorId` plus an `Author` carrying the same `Id` **and non-null `Name`/`Bio`**; assert no throw and `AuthorId` unchanged. This is the round-trip regression guard: it fails if conflict detection inherits the key-only rule.
2. `ManyToOne_NavObjectDisagreesWithFk_Throws` — different GUIDs; assert the message names both property and FK.
3. `ManyToOne_CaseAndBraceVariantGuids_Accepted` — FK uppercase, nested key `{...}`-delimited, same value; assert no throw. Fails under raw-string comparison.
4. `ManyToOne_NavObjectWithNoUsableKey_ForeignKeyStands` — valid FK plus an `Author` with no `Id`; assert no throw.
5. `ManyToMany_HydratedNavListMatchingFkList_Accepted` — hydrated `Tags` with extras, same key set, different order; assert no throw.
6. `ManyToMany_NavListSubsetOfFkList_Throws` — FK list of 3, nav list of 2.
7. `ManyToMany_EmptyNavListWithFkList_Accepted` — assert no throw and the FK list is untouched.
8. `ManyToMany_PropertyNameEqualsForeignKey_NoConflictError` — the A7 collision schema with a plain GUID list; assert no throw. Fails if conflict detection is not gated on the guard.

- [ ] **Step 7: Run the suite**

```bash
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

Expect **640 passed / 0 failed** (632 after Task 1 + 8). Run it in the foreground and wait for it to finish.

- [ ] **Step 8: Commit**

```bash
git add Iverson.Server/Iverson.Api/Grpc/RelationValidator.cs Iverson.Server/Iverson.Api.Tests/Grpc/RelationValidatorTests.cs
git commit -m "error when a relation's foreign key and nav property disagree"
```

## Tasks NOT in this plan

Inherited verbatim from the spec's "Out of scope". A new spec → plan cycle is required to add any of these.

- Client-side stripping in any of the five clients. Server-side stripping fixes correctness and
  event content; it does not reclaim the upload bandwidth, which would require touching all five.
- Reconciling the fast-path and reconciliation publishers. Stripping makes their bodies agree in
  shape for relation keys, which was the observed divergence; making the event authoritative in
  general is a separate question.
- `StructConverter` still emits nulls.
- Cascade-inserting new related entities. Keyless embedded objects remain an explicit error.

## Known issues inherited from spec

These exist in the implementation by design — accepted by the user during brainstorming.

- **A partial nav subset against a present FK list errors, including when the subset arose from a
  deleted or unreadable referenced row.** Ben chose this option (2026-08-06) after the false-error
  risk was stated explicitly, preferring detection of removals over tolerance of read-time
  filtering. Callers hitting it must either send the FK list alone or re-hydrate before writing.
- **A stale hydrated nav object naming a different entity, previously ignored, now errors.** Intended
  tightening; a behavior change for existing callers, not just a cleanup.
- **Go many-to-many ids are never sent at all.** `coordinator.go:446` skips relation fields, and Go
  declares the id array *as* the relation field, so the ids reach neither the nav property nor the
  FK column. This predates and is independent of this design. Worth its own investigation.
