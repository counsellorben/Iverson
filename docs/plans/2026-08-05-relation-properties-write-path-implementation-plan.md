# Relation Properties on the Write Path Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-05-relation-properties-write-path-design.md` (commit SHA: `d2b8e05`)

**Goal:** A field-restricted caller can write an entity carrying relation properties; an embedded existing-entity reference populates the FK column; a keyless embedded object fails loudly instead of writing a NULL FK.

**Architecture:** Two coupled server-side changes. The authorization evaluator adds relation property names to the write-side allowed-field set, gated by each relation's FK-column permission. The relation validator then also normalizes an embedded existing-entity reference into that FK column, treating a `NullValue` FK as absent and stripping case-variant keys before writing.

**Tech stack:** C# / .NET 10, xunit + FluentAssertions + NSubstitute, Google.Protobuf `Struct`/`Value`.

---

## Global Constraints

- **Task 2 must not land without Task 1.** Authorization runs before validation, so Task 2's FK write happens *after* the field check. Task 1's FK gate is what stops a caller barred from writing `AuthorId` from setting it via `Author`. Landing Task 2 alone opens a privilege-escalation hole.
- **Relation names are added for `AuthorizationAction.Write` only.** `AllowedFields` also drives search filter/sort/vector authorization (`ObjectSearchGrpcService.cs:101,140,150,312`), which evaluates with `Read`. Adding relation names there would widen search permissions.
- **Commit messages:** plain lowercase imperative, no Conventional-Commits prefix (verified dominant: 45/60 recent commits).

## File Structure

**Modify**
- `Iverson.Server/Iverson.Api/Authorization/RowFieldAuthorizationEvaluator.cs` — add relation names to the write-side allowed-field set
- `Iverson.Server/Iverson.Api/Authorization/IRowFieldAuthorizationEvaluator.cs` — correct the `AllowedFields` doc comment that enumerates the set
- `Iverson.Server/Iverson.Api/Grpc/StructFieldAccess.cs` — add case-variant-safe `SetField`
- `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs` — `SetAuthoritativeField` delegates to `SetField`
- `Iverson.Server/Iverson.Api/Grpc/RelationValidator.cs` — rename to `ValidateAndNormalizeRelations`; `NullValue`-as-absent; normalize embedded references
- `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs:43,114` — call-site rename
- `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs:298,351` — call-site rename

**Test**
- `Iverson.Server/Iverson.Api.Tests/Authorization/RowFieldAuthorizationEvaluatorTests.cs` — 5 new tests
- `Iverson.Server/Iverson.Api.Tests/Grpc/AuthorizationFieldMaskingTests.cs` — 1 new test
- `Iverson.Server/Iverson.Api.Tests/Grpc/RelationValidatorTests.cs` — 8 new tests, 7 call-site renames

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time (18 listed cold, 2 failed and reshaped the design) plus A19-A21 added by CDR round 1's span check. Ground truth; NOT re-verified here.

| # | Assumption | Evidence |
|---|---|---|
| A1 | `AllowedFields` has a single construction site | `RowFieldAuthorizationEvaluator.cs:55,75` |
| A2 | `schema.Relations` reachable in the evaluator | `Evaluate(SchemaDescriptor, …)` |
| A3 | `RelationKind` has exactly 4 values | `SchemaDescriptor.cs:67` |
| A4 | `RelationDescriptor.ForeignKey` matches the FK column name | `SchemaBuilder.cs:106-111` |
| A5 | `RejectDisallowedFields` upper-firsts payload keys | `AuthorizationFieldMasking.cs:155` |
| A6 | Read masking precedes relation injection at every read site | `ObjectMappingGrpcService.cs:273` then `:276` |
| A7 | **FAILED** — search consumes `AllowedFields` for filter/sort/vector auth | `ObjectSearchGrpcService.cs:101,140,150,312` → write-only scoping |
| A8 | **UNVERIFIED** — no guard against `PropertyName`/column-name collision | recorded as a known issue |
| A9 | `ValidateRelations` has exactly 4 call sites, all writes | Persistence `:43,:114`; Mapping `:298,:351` |
| A10 | One implementation, DI-registered | `Program.cs:191` |
| A11 | Payload mutations reach `SerializePayload` | same reference, `:43` → `:51` |
| A12 | The `ManyToMany` FK is a real declared column | `SchemaBuilder.cs:106-111` |
| A13 | `StructFieldAccess` tries canonical then camelCase | `StructFieldAccess.cs` |
| A14 | **FAILED** — client emits `authorId`/`author` as camelCase `NullValue` | probe → `NullValue`-as-absent + case-variant stripping |
| A15 | Authorization precedes validation at all 4 sites | `:33`<`:43`, `:104`<`:114`, `:294`<`:298`, `:341`<`:351` |
| A16 | `isExistingEntity` is the right reference/new discriminator | non-empty, parseable, non-`Guid.Empty` |
| A17 | Existing test helpers can express the new cases | `SchemaWithAuthorization`, `MakeSchemaWithRelation` |
| A18 | Nothing depends on the current behavior | one existing nav-property test; no client writes keyless embedded objects |
| A19 | FK columns are in `ScalarColumns`, not only `FkColumns` | `SchemaBuilder.cs:53-56` |
| A20 | All four write paths evaluate with `AuthorizationAction.Write` | `ObjectPersistenceGrpcService.cs:38,:109`; `ObjectMappingGrpcService.cs:295,:346` |
| A21 | Key-plus-extras nested objects are rejected before normalization | `RelationValidator.cs:124-127` |

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time against `d2b8e05`.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1-P7 | File path | All 7 files exist at the exact paths this plan names | `RowFieldAuthorizationEvaluator.cs` (82 lines), `IRowFieldAuthorizationEvaluator.cs` (29), `RowFieldAuthorizationEvaluatorTests.cs` (541), `AuthorizationFieldMaskingTests.cs` (139), `StructFieldAccess.cs` (43), `RelationValidator.cs` (128), `RelationValidatorTests.cs` (131) |
| P8 | Code validity | `RelationKind` resolves in the evaluator | `using Iverson.Api.Schema;` at `RowFieldAuthorizationEvaluator.cs:2` |
| P10 | Code validity | `allFields` is `IEnumerable<string>`, so conditional reassignment compiles | `RowFieldAuthorizationEvaluator.cs:70-74` — chained `.Concat(...)`. **Changed from the draft:** a `cond ? seq : []` ternary would depend on collection-expression target typing; reassignment does not |
| P11 | Signature | `internal static StructFieldAccess` is callable from `public sealed RelationValidator` | both in `Iverson.Api.Grpc`, same assembly (`StructFieldAccess.cs:3,8`) |
| P12 | Signature | `SetAuthoritativeField(Struct, string, string)` is `private static` with 2 call sites | `AuthorizationFieldMasking.cs:121`, called at `:56`, `:58` |
| P13 | Signature | `void ValidateRelations(Struct, SchemaDescriptor)` on interface and impl | `RelationValidator.cs:9` and `:14` |
| P14 | Consumer impact | The rename touches exactly 13 references | 4 production (`ObjectPersistenceGrpcService.cs:43,114`; `ObjectMappingGrpcService.cs:298,351`), interface `:9`, impl `:14`, 7 test call sites (`RelationValidatorTests.cs:44,56,67,78,89,106,121`) |
| P15 | Consumer impact | `RegisterSchemaAuthorizationIntegrationTests.cs:227` only constructs a substitute | absent from the `ValidateRelations` grep — never stubs or verifies the method by name |
| P16 | Consumer impact | `SetAuthoritativeField` keeps its `(Struct, string, string)` signature and delegates, so its 2 call sites are untouched | `AuthorizationFieldMasking.cs:56,58` pass strings; the `Value`-typed generality needed for `ManyToMany` lives in `StructFieldAccess.SetField` instead. No test references `SetAuthoritativeField` directly — the 3 masking tests exercise it via `EnforceWriteAuthorization` |
| P17 | Command | `dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --nologo` is valid from `Iverson.Server/` | csproj exists at that path; command used repeatedly this session |
| P18 | Command | Commit style is plain lowercase imperative, no Conventional-Commits prefix | 45/60 recent commits plain (39/54 excluding today's) |
| P19-P20 | Ordering | Tasks 1 and 2 touch disjoint files and share no symbol | Task 1: evaluator + interface + 2 test files. Task 2: `StructFieldAccess`, `AuthorizationFieldMasking`, `RelationValidator`, 2 grpc services, 1 test file |
| P21 | Code validity | Every identifier the plan's code blocks name resolves at its point of use | `Value.KindOneofCase.NullValue` (`ProtoPayloadHelper.cs:20`), `Value.ForList(params Value[])` (`EntityRelationResolver.cs:137`), `StructFieldAccess.Candidates`, `payload.Fields.Remove`, `registry.Get`, `RelationKind.OneToMany`, `AuthorizationAction.Write` |
| P22 | Signature | `SchemaRegistry.Get` returns `SchemaDescriptor?` | `SchemaRegistry.cs:15` |
| P23 | Consumer impact | `RelationValidator` is the only `IRelationValidator` implementation | full-repo grep; only other reference is one NSubstitute construction |
| P24 | Command | No pre-commit hook or extra build gate | `.git/hooks/` contains only `.sample` files |

## Tasks

### Task 1: Relation property names join the write-side allowed-field set

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Authorization/RowFieldAuthorizationEvaluator.cs:70-75`
- Modify: `Iverson.Server/Iverson.Api/Authorization/IRowFieldAuthorizationEvaluator.cs` (the `AllowedFields` doc comment)
- Test: `Iverson.Server/Iverson.Api.Tests/Authorization/RowFieldAuthorizationEvaluatorTests.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/AuthorizationFieldMaskingTests.cs`

**Interfaces:**
- Produces: relation property names present in `AuthorizationDecision.AllowedFields` for write actions — Task 2 depends on this for safety, not for compilation.

- [ ] **Step 1: Add the FK-gated, write-only relation term to `allFields`**

Replace the body of the `if (excluded.Count > 0)` block at `RowFieldAuthorizationEvaluator.cs:69-76`:

```csharp
            if (excluded.Count > 0)
            {
                var allFields = new[] { schema.KeyColumn.Name }
                    .Concat(schema.ScalarColumns.Select(c => c.Name))
                    .Concat(schema.FkColumns.Select(fk => fk.ColumnName))
                    .Concat(schema.VectorFields.Select(v => v.PropertyName))
                    .Concat(schema.ChunkFields.Select(c => c.PropertyName));

                // A relation property is writable exactly when its FK column is writable: writing
                // `Author` IS writing `AuthorId`, so one permission governs one concept. Without
                // this, restricting `AuthorId` gives no protection from a caller sending `Author`
                // instead — which matters because RelationValidator normalizes the embedded form
                // into the FK column *after* this check runs.
                //
                // OneToMany is the carve-out: its FK lives on the RELATED entity, so there is no
                // local column to gate on. Permitted unconditionally — inert on write (the
                // validator skips the kind) and injected on read after masking.
                //
                // Write actions only: AllowedFields also drives search filter/sort/vector
                // authorization, which evaluates with Read; relation names have no meaning there
                // and admitting them would widen search permissions.
                if (action == AuthorizationAction.Write)
                    allFields = allFields.Concat(schema.Relations
                        .Where(r => r.Kind == RelationKind.OneToMany || !excluded.Contains(r.ForeignKey))
                        .Select(r => r.PropertyName));

                allowedFields = allFields.Where(f => !excluded.Contains(f)).ToHashSet();
            }
```

The trailing `.Where(f => !excluded.Contains(f))` is unchanged and still applies to the relation names, so a `FieldPermission` naming the property directly (`"Author"`) continues to restrict it. Both spellings stay governable.

- [ ] **Step 2: Correct the `AllowedFields` doc comment**

In `IRowFieldAuthorizationEvaluator.cs`, the `AllowedFields` parameter doc enumerates the set's contents and omits relations. Add the relation clause, scoped to write actions. That stale comment is part of why this defect went unnoticed.

- [ ] **Step 3: Add evaluator tests**

In `RowFieldAuthorizationEvaluatorTests.cs`, extend the `SchemaWithAuthorization` helper to accept relations (it currently hard-codes `Relations = []`), then add:

1. `ManyToOne` nav property allowed on write when its FK is allowed
2. `ManyToOne` nav property excluded on write when its FK is excluded
3. `OneToMany` nav property allowed on write despite having no local FK column
4. A `FieldPermission` naming the property directly still excludes it
5. Relation names absent from `AllowedFields` for `AuthorizationAction.Read`

- [ ] **Step 4: Add the masking test**

In `AuthorizationFieldMaskingTests.cs`, add: a write payload carrying a nested relation struct is not rejected for a field-restricted caller.

- [ ] **Step 5: Run the suite**

```bash
cd Iverson.Server && dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --nologo
```

Baseline is 608 passing; expect 614.

- [ ] **Step 6: Commit**

```bash
git add Iverson.Server/Iverson.Api/Authorization/RowFieldAuthorizationEvaluator.cs Iverson.Server/Iverson.Api/Authorization/IRowFieldAuthorizationEvaluator.cs Iverson.Server/Iverson.Api.Tests/Authorization/RowFieldAuthorizationEvaluatorTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/AuthorizationFieldMaskingTests.cs
git commit -m "allow relation properties on the write path when their FK column is writable"
```

### Task 2: Validation also normalizes embedded references into the FK column

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/StructFieldAccess.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs:121`
- Modify: `Iverson.Server/Iverson.Api/Grpc/RelationValidator.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs:43,114`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs:298,351`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/RelationValidatorTests.cs`

**Interfaces:**
- Consumes: Task 1's write-side gate. Task 2 compiles without it but must not ship without it (see Global Constraints).

- [ ] **Step 1: Add case-variant-safe `SetField` to `StructFieldAccess`**

```csharp
    /// <summary>
    /// Sets <paramref name="canonicalName"/>, first removing any key that differs from it only by
    /// the leading character's case. Clients serialize camelCase, so a payload arrives carrying
    /// <c>authorId</c> while the schema column is <c>AuthorId</c>; setting the canonical key
    /// without removing the camelCase one leaves BOTH in the Struct, and
    /// <c>StructSerializer.SerializePayload</c> upper-firsts every key into a Dictionary —
    /// throwing "An item with the same key has already been added."
    /// </summary>
    public static void SetField(Struct s, string canonicalName, Value value)
    {
        foreach (var candidate in Candidates(canonicalName))
            if (candidate != canonicalName)
                s.Fields.Remove(candidate);

        s.Fields[canonicalName] = value;
    }
```

This lives here rather than in `AuthorizationFieldMasking` because `StructFieldAccess` already owns the canonical/camelCase `Candidates` logic, and because "authoritative field" describes an authorization concept, not an FK copy.

- [ ] **Step 2: Delegate `SetAuthoritativeField` to it**

In `AuthorizationFieldMasking.cs`, keep the existing doc comment (it records the tenant/owner-specific reason) and replace the body:

```csharp
    private static void SetAuthoritativeField(Struct payload, string canonicalName, string value) =>
        StructFieldAccess.SetField(payload, canonicalName, Value.ForString(value));
```

The two implementations are equivalent for the realistic key set: the old one removed keys where `UpperFirst(k) == canonicalName`, and `Candidates` generates exactly the canonical and first-char-lowered forms. The 3 existing masking tests cover this and must stay green.

- [ ] **Step 3: Rename the validator method**

`ValidateRelations` → `ValidateAndNormalizeRelations` on the interface (`RelationValidator.cs:9`), the implementation (`:14`), the 4 production call sites, and the 7 test call sites. The name states that it mutates the payload.

Leave the existing test *method names* (`ValidateRelations_ManyToOne_…`) unchanged — they describe the behavior under test, and renaming them adds diff noise without changing what they assert.

- [ ] **Step 4: Treat a `NullValue` FK as absent, and normalize single relations**

In `ValidateSingleRelation`:

```csharp
        var fkValue = StructFieldAccess.GetFieldValue(payload, relation.ForeignKey);
        // A NullValue FK counts as ABSENT. The .NET client serializes every property, so a null
        // nullable FK arrives as `authorId: null`; treating that as present made it fail GUID
        // validation (a nullable FK the validator explicitly intends to be omittable) and made
        // the embedded-object branch unreachable from that client entirely.
        if (fkValue is not null && fkValue.KindCase != Value.KindOneofCase.NullValue)
        {
            if (!Guid.TryParse(fkValue.StringValue, out var g) || g == Guid.Empty)
                errors.Add($"'{relation.ForeignKey}': must be a valid non-empty GUID.");
            return;
        }

        var navValue = StructFieldAccess.GetFieldValue(payload, relation.PropertyName);
        if (navValue?.StructValue is { } nested)
        {
            var nestedKey = ValidateNestedObject(
                nested, relation.PropertyName, relation.RelatedTypeName, relation.ForeignKey, errors);
            if (nestedKey is not null)
                StructFieldAccess.SetField(payload, relation.ForeignKey, Value.ForString(nestedKey));
            return;
        }
```

The trailing required/nullable check is unchanged. A JSON `null` navigation property is not an embedded object — `navValue?.StructValue` is null for a `NullValue`, so it falls through to that check, which is what a null `Author` from the .NET client must do.

- [ ] **Step 5: Make `ValidateNestedObject` return the key and reject keyless objects**

```csharp
    /// <returns>
    /// The nested entity's key when it is a valid existing-entity reference, or null when it is
    /// not — in which case an error has been recorded. Callers use the returned key to normalize
    /// the reference into the FK column.
    /// </returns>
    private string? ValidateNestedObject(
        Struct nested, string path, string relatedTypeName, string foreignKey, List<string> errors)
    {
        var relatedSchema  = registry.Get(relatedTypeName);
        var keyColumnName  = relatedSchema?.KeyColumn.Name ?? "Id";
        var nestedKeyValue = StructFieldAccess.GetFieldValue(nested, keyColumnName);
        var nestedKey      = nestedKeyValue?.StringValue;

        var isExistingEntity = !string.IsNullOrWhiteSpace(nestedKey)
                            && nestedKey != Guid.Empty.ToString()
                            && Guid.TryParse(nestedKey, out _);

        // Previously a keyless nested object passed silently and the FK was never populated, so
        // the row was written with a NULL FK. Cascade-inserting the related entity is out of
        // scope, so this is an explicit error instead.
        if (!isExistingEntity)
        {
            errors.Add(
                $"'{path}': embedded new entities are not supported — create the related " +
                $"{relatedTypeName} first, then reference it by '{foreignKey}' (GUID) or by an " +
                $"embedded object containing only '{keyColumnName}'.");
            return null;
        }

        if (nested.Fields.Count > 1)
        {
            errors.Add(
                $"'{path}': existing entity (key='{nestedKey}') must only include " +
                $"the key field '{keyColumnName}' — remove extra properties.");
            return null;
        }

        return nestedKey;
    }
```

- [ ] **Step 6: Normalize collection relations**

In `ValidateCollectionRelation`, the FK branch is unchanged — `fkValue?.ListValue is { } fkList` already treats a `NullValue` FK as absent, and an *empty* list is a supplied value meaning "no related entities", so it must not trigger normalization. Replace the nav branch:

```csharp
        var navValue = StructFieldAccess.GetFieldValue(payload, relation.PropertyName);
        if (navValue?.ListValue is { } navList)
        {
            var keys = new List<Value>(navList.Values.Count);
            var allResolved = true;

            for (var i = 0; i < navList.Values.Count; i++)
            {
                var item = navList.Values[i].StructValue;
                if (item is null)
                {
                    errors.Add($"'{relation.PropertyName}[{i}]': expected an object, got a scalar.");
                    allResolved = false;
                    continue;
                }

                var key = ValidateNestedObject(
                    item, $"{relation.PropertyName}[{i}]", relation.RelatedTypeName,
                    relation.ForeignKey, errors);
                if (key is null)
                    allResolved = false;
                else
                    keys.Add(Value.ForString(key));
            }

            if (allResolved)
                StructFieldAccess.SetField(payload, relation.ForeignKey, Value.ForList(keys.ToArray()));
        }
        // empty collection is valid
```

`allResolved` is local rather than `errors.Count == 0` so an unrelated relation's error doesn't suppress this one's normalization.

- [ ] **Step 7: Add validator tests**

In `RelationValidatorTests.cs`, add:

6. `ManyToOne` existing-entity reference → FK column populated with the nested key
7. `ManyToOne` keyless embedded object → throws, message names the property
8. `ManyToOne` FK already present → nav property ignored, FK untouched
9. `ManyToOne` FK present as `NullValue` + valid embedded reference → FK populated, and `StructSerializer.SerializePayload` does not throw a duplicate-key error
10. `ManyToOne` FK present as `NullValue`, nullable column, no nav property → no error
11. `ManyToMany` list of references → FK list populated in order
12. `ManyToMany` list containing a keyless item → throws
13. `OneToMany` nav property → no FK written, no error
14. The existing `ValidateRelations_NestedExistingEntityWithExtraProperties_Throws` still passes

Test 9 is the regression guard for the duplicate-key crash: build the payload with a camelCase `authorId` set to `Value.ForNull()` plus a PascalCase nested reference, then assert `SerializePayload` succeeds.

- [ ] **Step 8: Run the suite**

```bash
cd Iverson.Server && dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --nologo
```

Expect 622 passing (614 after Task 1, plus 8).

- [ ] **Step 9: Commit**

```bash
git add Iverson.Server/Iverson.Api/Grpc/StructFieldAccess.cs Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs Iverson.Server/Iverson.Api/Grpc/RelationValidator.cs Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/RelationValidatorTests.cs
git commit -m "normalize embedded relation references into the foreign key column"
```

## Tasks NOT in this plan

Inherited verbatim from the spec's *Out of scope*:

- **Cascade-inserting new related entities** from keyless embedded objects. Now an explicit error rather than a silent NULL FK. Deserves its own spec: it needs cross-type authorization, transactional rollback, and store targeting.
- **Client-side changes.** All five clients keep serializing relation properties; the server owns the rule, so a hand-rolled gRPC caller gets the same behavior.
- **Making `StructConverter` omit nulls.** Fixing the client to drop null keys would also address the section-3 symptom for .NET, but leaves the other four clients and hand-built payloads exposed. The server-side `NullValue`-as-absent rule covers all of them.

## Known issues inherited from spec

- **No registration-time guard against a relation `PropertyName` colliding with a scalar or FK column name.** Searched `SchemaValidator`/`SchemaRegistrationOrchestrator` and found none. The client attribute models make it structurally unlikely — a property is either scalar or relation — but nothing enforces it. If such a schema were registered, the C′ gate would produce ambiguous results. Accepted as out of scope (Ben, 2026-08-05).
- **`excluded` uses an ordinal, case-sensitive `HashSet`** while the key-column comparison beside it uses `OrdinalIgnoreCase`. The new FK check follows the surrounding ordinal style rather than changing it.
