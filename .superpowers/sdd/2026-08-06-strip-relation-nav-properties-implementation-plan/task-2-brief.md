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
        var rawKey        = StructFieldAccess.GetFieldValue(nested, keyColumnName)?.StringValue;

        if (ReadNestedKey(nested, relatedTypeName) is null)
        {
            errors.Add(
                $"'{path}': embedded new entities are not supported — create the related " +
                $"{relatedTypeName} first, then reference it by '{foreignKey}' (GUID) or by an " +
                $"embedded object containing only '{keyColumnName}'.");
            return null;
        }

        // ... existing `extras` block, unchanged, using keyColumnName and rawKey ...

        // The RAW spelling, not the parsed form: this value is written into the FK column, and the
        // projection stores keep payload strings verbatim. Canonicalising here would write an FK
        // that no longer matches the related row's key in StarRocks and Qdrant.
        return rawKey;
    }
```

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

Expect **641 passed / 0 failed** (633 after Task 1 + 8). Run it in the foreground and wait for it to finish.

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
