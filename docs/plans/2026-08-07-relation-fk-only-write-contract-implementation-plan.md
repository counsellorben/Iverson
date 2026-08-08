# Foreign-Key-Only Relation Write Contract — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-07-relation-fk-only-write-contract-design.md` (commit SHA: `03d835d`)

**Goal:** Make every client write relations as foreign keys only, declare the FK columns those keys land in, and reject navigation properties on the server.

**Architecture:** The server's `RelationValidator` collapses from an embedded-object normalizer to an FK validator that rejects nav properties, and `SchemaRegistrationOrchestrator` gains a check that every non-`OneToMany` relation's foreign key names a declared column. Each of the five clients stops sending nav properties and starts emitting the FK under the inferred column name; the three clients that mark the FK-bearing field also begin declaring that column, synthesized from the relation descriptor.

**Tech stack:** .NET 10 (server + one client), Java 21/Maven, Python/pytest, TypeScript/vitest, Go 1.25.

---

## Global Constraints

Project-wide rules every task must hold to, taken from the spec:

- **A write payload carries foreign keys only.** ManyToOne/OneToOne → `{RelatedType}Id`; ManyToMany → `{RelatedType}Ids`, a list of id strings; OneToMany → no key at all.
- **Classification is by relation kind first, never by member type or name.** `OneToMany` is omitted in every client. A type test is used only in .NET and Java, where a nav member and an FK field genuinely coexist.
- **The FK is emitted under the inferred FK column name**, not the field's own name.
- **Reads are unaffected.** This contract governs writes only.
- **Server and clients must ship together** — nav-property rejection is not backward-compatible. This is a merge-time property of the branch, not a task ordering constraint.

## File Structure

**Modify — server**
- `Iverson.Server/Iverson.Api/Grpc/RelationValidator.cs` — collapse to FK validation + nav rejection (Task 1)
- `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` — delete capture/restore and their four call sites (Task 1)
- `Iverson.Server/Iverson.Api/Grpc/StructFieldAccess.cs` — delete `RemoveField` (Task 1)
- `Iverson.Server/Iverson.Api/Authorization/RowFieldAuthorizationEvaluator.cs` — rewrite the carve-out comment (Task 1)
- `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` — add the FK-column check (Task 2)

**Modify — clients**
- `Iverson.Clients/DotNet/Iverson.Client.Sample/Models/Tag.cs`, `Iverson.Client.Core/StructConverter.cs`, `EntityCoordinator.cs` (Task 3)
- `Iverson.Clients/Java/.../models/Article.java`, `client/.../core/StructConverter.java` (Task 4)
- `Iverson.Clients/Python/iverson_client/core.py` (Task 5)
- `Iverson.Clients/TypeScript/src/core.ts` (Task 6)
- `Iverson.Clients/Go/iverson/coordinator.go`, `iverson/registrar.go` (Task 7)

**Modify — tests**
- `Iverson.Server/Iverson.Api.Tests/Grpc/RelationValidatorTests.cs` — rewritten (Task 1)
- `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` — delete two cases, fix six constructor calls (Task 1)
- `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs` — fix one constructor call (Task 1)
- `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs` (Task 2)
- One serialization/registration test file per client (Tasks 3–7)

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time across three review rounds; **not re-verified here**. Full evidence is in the spec's `Verified assumptions` table (A1–A32). The load-bearing ones:

- **A1–A3, A7** — the symbols Task 1 deletes (`registry` usage, `RemoveField`, capture/restore) have no consumer outside the code Task 1 touches; `StructFieldAccess.Candidates` survives via `EntityKeyAccessor`.
- **A4** — field authorization runs before the validator (`ObjectMappingGrpcService.cs:294` vs `:299`); the carve-out at `RowFieldAuthorizationEvaluator.cs:92-95` must stay.
- **A5** — exactly four `ValidateAndNormalizeRelations` call sites.
- **A6, A23** — no consumer reads a relation nav property from a write payload; the read path re-fetches.
- **A9, A13, A15, A17, A19** — each client's relation metadata is reachable at its serialization point.
- **A11** — .NET `Tag` has no `ArticleIds` property despite a comment claiming the convention.
- **A12** — Java `Article` declares no `TagIds` column and is StarRocks-ineligible today.
- **A14, A28** — Java's `toValue` has no `Collection` branch; Python's `_entity_to_struct` has no list branch. (A28's Go clause is corrected below — see PA12.)
- **A21** — Go's `one_to_many` is a real declaration (`author.go:8`), so the exclusion is load-bearing.
- **A29** — none of Go, Python, TypeScript declares its FK-bearing field as a property.
- **A30** — the registration check cannot reuse `ValidateFieldReference` (its string-valued `SqlType` gate rejects a `UUID[]` FK).
- **A32** — the FK field cannot safely enter Go's or TypeScript's property loop; the FK property must be synthesized.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| PA1 | File path | All four server files Task 1 modifies exist as cited | `RelationValidator.cs`, `ObjectMappingGrpcService.cs`, `StructFieldAccess.cs`, `RowFieldAuthorizationEvaluator.cs` all read this session |
| PA2 | Consumer impact | Dropping `RelationValidator`'s ctor parameter does not break DI | `Program.cs:191` — `AddSingleton<IRelationValidator, RelationValidator>()`, no explicit argument |
| PA3 | Consumer impact | **Eight** direct `new RelationValidator(_registry)` sites must lose the argument, not one | `ObjectMappingGrpcServiceTests.cs:104,210,237,475,1047,1080`; `ObjectPersistenceGrpcServiceTests.cs:63`; `RelationValidatorTests.cs:22` |
| PA4 | Consumer impact | Deleting `StructFieldAccess.RemoveField` breaks no test | `grep RemoveField Iverson.Server/Iverson.Api.Tests/` → no hits |
| PA5 | Signature | `SchemaDescriptor` exposes what Task 2's check needs | `SchemaDescriptor.cs:10` `ScalarColumns` (`ColumnDescriptor.Name`), `:15` `Relations` (`RelationDescriptor(PropertyName, Kind, RelatedTypeName, ForeignKey)`) |
| PA6 | File path | `SchemaRegistrationOrchestratorTests.cs` exists as Task 2's test home | file listed |
| PA7 | Signature | `ToStruct<T>` has six call sites — four writes, two reads | `EntityCoordinator.cs:54,70,105,121`; `GraphAssembler.cs:95,209` |
| PA8 | Signature | Java's `isRelationField` is **not** reusable from `StructConverter` | `SchemaRegistrar.java:342` — `private static boolean isRelationField(Field)`. Task 4 needs its own annotation check |
| PA9 | File path / signature | Python's edit points | `core.py:99` `_infer_fk` (module-level), `:186-188` property-loop exclusion, `:312` `_entity_to_struct` |
| PA10 | File path / signature | TypeScript's edit points | `core.ts:92` `inferFk` (module-level), `:238` property-loop exclusion, `:358` `entityToPayload` |
| PA11 | File path / signature | Go's edit points | `coordinator.go:435` `entityToStruct`, `:462` `goValueToProtoValue`; `registrar.go:64` property loop, `:264` `inferFK` |
| PA12 | Code validity | **Go's value serializer has no slice case** — contradicts spec A28's Go clause | `coordinator.go:462-490` switches on String/Bool/Int/Uint/Float/Ptr/Struct then `default: NewNullValue()`. `grep reflect.Slice Iverson.Clients/Go/iverson/*.go` → hits only in `registrar.go` (schema declaration). A `[]string` serializes as **null**. Task 7 adds the branch |
| PA13 | Sibling set | All five inferred-FK helpers exist with the names this plan cites | .NET `SchemaRegistrar.cs:268` `InferForeignKey`; Go `registrar.go:264` `inferFK`; Python `core.py:99` `_infer_fk`; TS `core.ts:92` `inferFk`; Java `SchemaRegistrar.java:332` `inferForeignKey` |
| PA14 | Sibling set | The `OneToMany` identifier each task's exclusion needs, per client | Go `tags.go:120` `KindOneToMany`; Python `annotations.py:210` `relation_kind="one_to_many"`; TS `annotations.ts:43` `'one_to_many'`; .NET `EntityRegistry.cs:69` `RelationKind.OneToMany`; Java `SchemaRegistrar.java:274` `RelationKind.ONE_TO_MANY` |
| PA15 | Command | Per-task verification command exists | `Iverson.Api.Tests.csproj` and `Iverson.Client.Core.Tests.csproj` exist; `Iverson.Clients/Java/pom.xml` (`iverson-client-java`); `pyproject.toml` `[tool.pytest.ini_options] testpaths=["tests"]`; `package.json` `"test": "npm run typecheck && vitest run"`; `Iverson.Clients/Go/go.mod` |
| PA16 | Command | Commit convention is plain lowercase imperative, no Conventional-Commits prefix | `git log --oneline -20` — e.g. `strip relation nav properties from write payloads`, `preserve caller's nav property key case when restoring echoed payload` |
| PA17 | Ordering | No task consumes a symbol another task introduces | Tasks 1 and 2 touch disjoint server files; Tasks 3–7 are five separate languages with separate build and test tooling. All seven are independent |
| PA18 | Sibling set | Payload-key casing per client, against which the omission and synthesis steps match names | **.NET emits camelCase** — `StructConverter.cs:12-17` `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`. The other four emit PascalCase: Python `_to_pascal_case` (`core.py:329`), TS `toPascalCase` (`core.ts:364`), Java `toPascalCase` (`StructConverter.java:34`), Go raw field names (`coordinator.go:449`). See Task 3 Step 3 |

## Tasks

Tasks 1–7 are **mutually independent** and may be implemented and reviewed in any order.

---

### Task 1: Collapse `RelationValidator` to FK validation with nav-property rejection

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/RelationValidator.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` (`:298,309,353,357` call sites; `:390-401` method bodies)
- Modify: `Iverson.Server/Iverson.Api/Grpc/StructFieldAccess.cs` (`:66` `RemoveField`)
- Modify: `Iverson.Server/Iverson.Api/Authorization/RowFieldAuthorizationEvaluator.cs` (comment at `:80-95` only)
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/RelationValidatorTests.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs`

- [ ] **Step 1: Rewrite `RelationValidatorTests.cs` against the new contract.**
Delete the embedded-object tests (the normalize, cascade-insert, key-only-rule and FK/nav-conflict cases — roughly eighteen of the current thirty-four). Keep the FK-validation cases: valid/invalid GUID, missing required non-nullable FK, missing optional nullable FK, invalid GUID in a ManyToMany list, `OneToMany` never validated, and the `PropertyName == ForeignKey` collision case. Add per-kind rejection tests asserting the message names both the nav property and the foreign key, plus one asserting a `NullValue` nav key is **tolerated**, not rejected. Change `_sut = new RelationValidator(_registry)` at `:22` to `new RelationValidator()`.

- [ ] **Step 2: Run the tests and watch them fail for the right reason.**
```bash
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~RelationValidatorTests"
```
Expect compile failure on the constructor first; that is the signal to proceed to Step 3, not a reason to revert Step 1.

- [ ] **Step 3: Collapse `RelationValidator.cs`.**
Delete `ValidateNestedObject`, `ReadNestedKey`, `KeyColumnNameFor`, the embedded-object branches in `ValidateSingleRelation` and `ValidateCollectionRelation`, and the FK/nav cross-check. Change the class declaration to `public sealed class RelationValidator : IRelationValidator` (no primary constructor). In the top-level loop, replace the strip with the rejection:
```csharp
if (navIsDistinctKey)
{
    var navValue = StructFieldAccess.GetFieldValue(payload, relation.PropertyName);
    // A NullValue nav key counts as ABSENT, matching the foreign-key rule below: .NET and
    // Java serialize every property, so an unset nav member arrives as `Author: null`.
    if (navValue is not null && navValue.KindCase != Value.KindOneofCase.NullValue)
        errors.Add(
            $"Relation '{relation.PropertyName}' is a navigation property and cannot be " +
            $"written — send '{relation.ForeignKey}' instead.");
}
```
Keep FK GUID validation for singles, per-element validation for ManyToMany lists, and the required-relation check for a non-nullable FK column — with the `or '<Name>' (embedded object)` half removed from its message. Errors still accumulate into one `InvalidArgument`.

- [ ] **Step 4: Delete the capture/restore machinery.**
In `ObjectMappingGrpcService.cs`, delete `CaptureNavProperties` and `RestoreNavProperties` and their four call sites. In `StructFieldAccess.cs`, delete `RemoveField`. Leave `Candidates` alone — `EntityKeyAccessor` uses it.

- [ ] **Step 5: Fix the seven remaining constructor call sites.**
`ObjectMappingGrpcServiceTests.cs:104,210,237,475,1047,1080` and `ObjectPersistenceGrpcServiceTests.cs:63` each pass `_registry`; drop the argument. In the same file, delete `MappingPost_NavPropertyPresentInResponsePayload` (`:725`) and `MappingPost_CamelCaseNavPropertyKeyPreservedInResponsePayload` (`:753`) — both pin echoed-payload behavior this design makes unreachable.

- [ ] **Step 6: Rewrite the authorization carve-out comment.**
In `RowFieldAuthorizationEvaluator.cs`, the code at `:92-95` stays exactly as it is. Only the comment above it changes: the carve-out no longer exists because `Author` is equivalent to writing `AuthorId` (nothing is normalized any more) but so that a caller sending a nav property reaches the validator's rejection instead of an opaque authorization error. A caller whose `AuthorId` is excluded still fails at authorization, which remains correct.

- [ ] **Step 7: Read `AuthorizationFieldMaskingTests.cs`** and update any case that depends on a nav property surviving a write payload. If none does, record that in the task report rather than editing the file.

- [ ] **Step 8: Run the full server suite.**
```bash
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

- [ ] **Step 9: Commit**
```bash
git add Iverson.Server/Iverson.Api/Grpc/RelationValidator.cs Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api/Grpc/StructFieldAccess.cs Iverson.Server/Iverson.Api/Authorization/RowFieldAuthorizationEvaluator.cs Iverson.Server/Iverson.Api.Tests/Grpc/RelationValidatorTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs
git commit -m "reject relation nav properties instead of normalizing them"
```

---

### Task 2: Reject registration when a relation's foreign key names no column

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`

- [ ] **Step 1: Write the tests first.**
Four cases: a `ManyToOne` whose `ForeignKey` matches no declared column is rejected with `InvalidArgument` naming the relation and the foreign key; a `OneToMany` whose `ForeignKey` matches no column is **accepted** (its FK lives on the related type); a `ManyToMany` whose FK column is an array type is **accepted** — this is the case `ValidateFieldReference` would have wrongly rejected; and a well-formed `ManyToOne` is accepted.

- [ ] **Step 2: Add the check.**
Beside the existing `owner_field` and `tenant_field` checks (`SchemaRegistrationOrchestrator.cs:53-66`), after `ValidateFieldReference(descriptor, descriptor.TenantColumn, "tenant_field")`:
```csharp
// Membership only — NOT ValidateFieldReference, which additionally requires a string-valued
// SqlType for Qdrant filtering and would reject a ManyToMany's UUID[] foreign key.
// OneToMany is exempt: its foreign key is a column on the RELATED type's row.
foreach (var relation in descriptor.Relations.Where(r => r.Kind != RelationKind.OneToMany))
{
    if (!descriptor.ScalarColumns.Any(c =>
            string.Equals(c.Name, relation.ForeignKey, StringComparison.OrdinalIgnoreCase)))
    {
        throw new RpcException(new Status(StatusCode.InvalidArgument,
            $"Relation '{relation.PropertyName}' ({relation.Kind}) on '{descriptor.TypeName}' " +
            $"declares foreign key '{relation.ForeignKey}', which is not a declared property."));
    }
}
```

- [ ] **Step 3: Run the suite.**
```bash
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

- [ ] **Step 4: Commit**
```bash
git add Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs
git commit -m "reject a relation whose foreign key names no declared column"
```

---

### Task 3: .NET client — declare `Tag`'s foreign key and omit nav properties

**Files:**
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Sample/Models/Tag.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/StructConverter.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs`
- Test: `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/` (new or existing serialization test file)

- [ ] **Step 1: Write a serialization test** asserting a written `Article` payload contains the foreign keys and contains **neither casing** of each nav property — not `Author` and not `author`, not `Tags` and not `tags`, not `UserArticles` and not `userArticles`. Asserting only the PascalCase form passes whether or not the omission works. Note the payload keys are camelCase, so the FK assertions read `authorId` and `tagIds`.

- [ ] **Step 2: Give `Tag` its foreign key.**
`Tag.cs` gains `public Guid[] ArticleIds { get; set; } = [];` beside the existing `[ManyToMany(typeof(Article))] List<Article> Articles`, mirroring how `Article` carries both `TagIds` and `Tags`. The stale comment claiming the convention is already followed goes.

- [ ] **Step 3: Teach `ToStruct` to omit nav properties.**
Add an optional parameter carrying the property names to omit, defaulted so `GraphAssembler.cs:95,209` keep compiling unchanged — they read FK values only and must not be disturbed. Remove the named keys after the JSON round-trip, matching **case-insensitively on the leading character**: `_jsonOpts` sets `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` (`StructConverter.cs:12-17`), so the Struct's keys are `author` and `tags` while the descriptor's names are `Author` and `Tags`. An exact-match removal silently removes nothing. `StructFieldAccess.Candidates` on the server exists for exactly this variance and is the behaviour to mirror.

- [ ] **Step 4: Supply the names at the four write call sites.**
`EntityCoordinator.cs:54,70,105,121` pass the nav-property names from `_descriptor.Relations` — the members whose declared type is an entity or a collection of entities. `_descriptor` is already in scope.

- [ ] **Step 5: Run the client tests.**
```bash
dotnet test Iverson.Clients/DotNet/Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Clients/DotNet/Iverson.Client.Sample/Models/Tag.cs Iverson.Clients/DotNet/Iverson.Client.Core/StructConverter.cs Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs Iverson.Clients/DotNet/Iverson.Client.Core.Tests/
git commit -m "send foreign keys only from the dotnet client"
```

---

### Task 4: Java client — declare the ManyToMany foreign key, omit nav members, serialize collections

**Files:**
- Modify: `Iverson.Clients/Java/sample/src/main/java/io/iverson/sample/models/Article.java`
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/StructConverter.java`
- Test: `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/StructConverterTest.java` (new or existing)

- [ ] **Step 1: Write the tests.** A written `Article` payload contains `AuthorId` and `TagIds`, does not contain `Author` or `Tags`, and `TagIds` is a **`ListValue` of id strings, not a string** — the last assertion is the one that fails today.

- [ ] **Step 2: Give `Article` its foreign key.**
Add `private List<UUID> tagIds;` with getter and setter beside the existing `@ManyToMany(type = Tag.class) private List<Tag> tags;`. The constructor gains no parameter — `authorId` is constructor-set today, but `tagIds` follows the setter-only pattern the nav members already use.

- [ ] **Step 3: Add the `Collection` branch to `toValue`.**
`StructConverter.java:102` currently falls through to `val.toString()` for any collection. Insert a branch before the fallback that emits a `ListValue` whose elements are each converted through `toValue` recursively. The `toString()` fallback stays for genuinely unknown types.

- [ ] **Step 4: Skip nav members in `toStruct`.**
`toStruct` iterates `getAllFields`. Skip any field carrying `@ManyToOne`, `@OneToOne`, `@ManyToMany` or `@OneToMany` whose declared type is an entity or a `Collection` of entities. **Do not** call `SchemaRegistrar.isRelationField` — it is `private static` (`SchemaRegistrar.java:342`) and not reachable from this class; write the annotation check locally.

- [ ] **Step 5: Run the tests.**
```bash
mvn -f Iverson.Clients/Java/pom.xml test
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Clients/Java/sample/src/main/java/io/iverson/sample/models/Article.java Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/StructConverter.java Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/
git commit -m "send foreign keys only from the java client"
```

---

### Task 5: Python client — kind-first serialization, list values, and a declared FK column

**Files:**
- Modify: `Iverson.Clients/Python/iverson_client/core.py`
- Test: `Iverson.Clients/Python/tests/` (new or existing)

- [ ] **Step 1: Write the tests.** Three: a written `Article` payload contains `AuthorId` and not `Articles`; a ManyToMany id list arrives as a `ListValue`, not a string; and a registered schema declares the FK column under the inferred name for the three non-`OneToMany` kinds and declares nothing for `OneToMany`.

- [ ] **Step 2: Add the list branch to `_entity_to_struct`.**
The type ladder at `core.py:330-341` ends in `else: s.fields[pascal].string_value = str(value)`. Insert a `list`/`tuple` branch before it emitting a `ListValue` of recursively-converted elements. Without this a ManyToMany id list arrives as a string and the server's `fkValue?.ListValue` read silently ignores it.

- [ ] **Step 3: Make serialization kind-first.**
Build a `{field: kind}` map from `meta["relations"]` — `core.py:166` already builds a relation-field set from the same source. Omit fields whose kind is `"one_to_many"`. Emit the rest under `_infer_fk(relation, type_name)` (`core.py:99`) rather than the field's PascalCase name; for `author_id` the two coincide, so nothing changes on the wire for ManyToOne.

- [ ] **Step 4: Append the synthesized FK property at registration.**
Leave the exclusion at `core.py:186-188` unchanged. After the property loop, append one `PropertyDescriptor` per non-`OneToMany` relation: named by `_infer_fk`, `clr_type=CLR_STRING`, `is_array=True` for `many_to_many` and `False` otherwise, `is_nullable=True`, `is_key=False`.

- [ ] **Step 5: Run the tests.**
```bash
cd Iverson.Clients/Python && pytest
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Clients/Python/iverson_client/core.py Iverson.Clients/Python/tests/
git commit -m "send and declare foreign keys only from the python client"
```

---

### Task 6: TypeScript client — kind-first serialization and a declared FK column

**Files:**
- Modify: `Iverson.Clients/TypeScript/src/core.ts`
- Test: `Iverson.Clients/TypeScript/tests/core.test.ts`

- [ ] **Step 1: Write the tests.** A written payload contains the FK key and not the nav key; a registered schema declares the FK column under the inferred name for the three non-`OneToMany` kinds and nothing for `OneToMany`.

- [ ] **Step 2: Make `entityToPayload` kind-first.**
`entityToPayload` (`core.ts:358`) currently copies every own property and takes only the instance, so it has no class to read metadata from — widen it to `entityToPayload(entity: object, cls: Function)` and pass `this._cls` at both call sites (`core.ts:428` in `persist`, `:448` in `update`). Build a `{field: kind}` map from `getRelations(cls)` — already imported at `core.ts:59`. Omit fields whose kind is `'one_to_many'`; emit the rest under `inferFk(kind, relatedType, typeName)` (`core.ts:92`) instead of `toPascalCase(field)`. Arrays are assigned raw and survive as `ListValue` through the proto layer, so no value-conversion change is needed.

- [ ] **Step 3: Append the synthesized FK property at registration.**
Leave the exclusion at `core.ts:238` unchanged — the loop below it throws on any array property lacking `@IversonArray`, which a reflected ManyToMany FK would trip. After the loop, append one `PropertyDescriptor` per non-`OneToMany` relation: named by `inferFk`, `clrType: ClrType.CLR_STRING`, `isArray` true for `'many_to_many'` and false otherwise.

- [ ] **Step 4: Run the tests** (this also type-checks `tests/`).
```bash
cd Iverson.Clients/TypeScript && npm test
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Clients/TypeScript/src/core.ts Iverson.Clients/TypeScript/tests/core.test.ts
git commit -m "send and declare foreign keys only from the typescript client"
```

---

### Task 7: Go client — serialize relation fields at all, including slices, and declare the FK column

**Files:**
- Modify: `Iverson.Clients/Go/iverson/coordinator.go`
- Modify: `Iverson.Clients/Go/iverson/registrar.go`
- Test: `Iverson.Clients/Go/iverson_test/` (new or existing)

This is the task that fixes the defect motivating the whole design: the Go client has never written a foreign key.

- [ ] **Step 1: Write the tests.** A written `Article` payload contains `AuthorId` at all — there is no existing coverage for this. A `Tag`'s `Articles` emits under `ArticleIds` as a `ListValue` of strings. An `Author`'s `one_to_many` `Articles` emits **nothing**. A registered `Article` schema declares an `AuthorId` property.

- [ ] **Step 2: Add the slice branch to `goValueToProtoValue`.**
`coordinator.go:462-490` has no `reflect.Slice` case, so every slice falls to `default: structpb.NewNullValue()` — a ManyToMany id list would serialize as null even after Step 3. Add a `reflect.Slice`/`reflect.Array` case emitting a `ListValue` of recursively-converted elements, guarding `[]byte` (`Elem().Kind() == reflect.Uint8`) as `registrar.go:185-195` already does on the schema side. This also repairs non-relation array fields, which have the same defect today.

- [ ] **Step 3: Stop discarding relation fields in `entityToStruct`.**
Replace the blanket `continue` at `coordinator.go:440-447`. Skip only `KindOneToMany` — `inferFK` returns `{ThisType}Id` for that kind, which names a column on the *related* row, and `author.go:8` is a real declaration that would otherwise emit under `AuthorId`. Skip a relation field whose type is a struct or slice-of-struct as a nav property. Emit everything else under `inferFK(fm, meta.TypeName)`: for ManyToOne and OneToOne that returns the field's own name; for ManyToMany it returns `{RelatedType}Ids`.

- [ ] **Step 4: Append the synthesized FK property at registration.**
Leave `tags.go`'s `meta.Fields`/`meta.Relations` split untouched — it also enforces that a tenant marker on a relation is not a tenant declaration (`tags.go:316-318`). In `registrar.go`, after the property loop at `:64`, append one `PropertyDescriptor` per non-`OneToMany` relation: `Name: inferFK(fm, meta.TypeName)`, `ClrType: CLR_STRING`, `IsArray: fm.RelationKind == KindManyToMany`, `IsNullable: true`, `IsKey: false`.

- [ ] **Step 5: Run the tests.**
```bash
cd Iverson.Clients/Go && go test ./...
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Clients/Go/iverson/coordinator.go Iverson.Clients/Go/iverson/registrar.go Iverson.Clients/Go/iverson_test/
git commit -m "send and declare foreign keys from the go client"
```

## Known issues inherited from spec

These exist in the implementation by design — accepted during brainstorming.

- **Historical null foreign keys are not backfilled** for Go, Python or TypeScript.
- **Python `str()` serialization for non-relation types.** The list branch above covers relation id lists; Python's converter still falls through to `str(value)` for any other unhandled type.
- **Client-side bandwidth for non-relation fields** is untouched.
