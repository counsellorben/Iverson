# IVC-LIFE-007 Hydration Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-17-life-007-hydration-parity-design.md` (commit SHA: `7d64fef`)

**Goal:** Make a depth-resolved read hand the caller the related object's data in all five clients, regardless of how the relation is declared, and re-author `IVC-LIFE-007` so it stops encoding one language's object shape.

**Architecture:** Four client libraries gain read-path hydration (Java by removing a mis-applied write-path exclusion, Python/TypeScript by materializing an idiomatic member, Go by populating a well-known `Hydrated` carrier its structs must declare). Python and Go gain a type-name→type registry; TypeScript gains an accessor exposing the related-type constructor. The harness's `VerifyDepthCapability` gains a result-keyed carrier fallback, and the standard retires `IVC-LIFE-007` in favour of a `Behaviour` successor.

**Tech stack:** C# / xUnit / FluentAssertions (harness), Java 17 / Maven / JUnit, Python / pytest, TypeScript / vitest, Go 1.25.

---

## Global Constraints

Copied from the spec's decisions; every task is bound by these.

- **The write path is untouched.** Every client keeps excluding navigation members from what it sends. TypeScript and Go need *new* exclusions to preserve that; Python is provably exempt; .NET and Java already exclude.
- **No model's declaration style changes.** No client is asked to move a relation off its foreign-key member. (Go's models gain a `Hydrated` carrier field; that is an added member, not a moved relation.)
- **Hydrated children are typed instances**, not untyped maps, wherever the language admits it. An unregistered related type falls back to the untyped child rather than raising — a hydration miss must never turn a successful read into an exception.
- **The caller-facing member name is the existing suffix strip in full, plural included**: strip `Id`/`_id` for `many_to_one`/`one_to_one`; strip `Ids`/`_ids` **and append `s`** for `many_to_many`. Dropping the plural collapses `py_tag_ids` and `py_tag_id` onto one member.
- **Requirements never mandate a member, type or signature name** (`iverson-client-standard.md:35-37`). The successor statement must stay shape-neutral.

## File Structure

**Modify**
- `Iverson.Server/Iverson.ClientConformance/Verifier.cs` — result-keyed carrier fallback in `VerifyDepthCapability`
- `Iverson.Server/Iverson.ClientConformance/Requirements.cs` — renumber `LifeDepthResolvedReadHydrated`, update its doc comment
- `docs/standards/iverson-client-standard.md` — retire `IVC-LIFE-007`, author the successor, update LIFE's Coverage Evidence cell, and (Task 6) `#### Known non-conformance`
- `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/StructConverter.java` — read-path skip removal + `STRUCT_VALUE` case
- `Iverson.Clients/Python/iverson_client/annotations.py` — registry population at decoration
- `Iverson.Clients/Python/iverson_client/core.py` — `_from_struct` hydration pass
- `Iverson.Clients/TypeScript/src/annotations.ts` — new exported accessor preserving the type factory
- `Iverson.Clients/TypeScript/src/core.ts` — `payloadToEntity` hydration, `entityToPayload` exclusion
- `Iverson.Clients/Go/iverson/registrar.go` — registry population, carrier exclusion from registration
- `Iverson.Clients/Go/iverson/tags.go` — carrier exclusion from `ExtractMeta`
- `Iverson.Clients/Go/iverson/coordinator.go` — carrier population in `structToEntity`, exclusion in `entityToStruct`
- `Iverson.Clients/Go/conformance/models.go` — `Hydrated map[string]any` on the entity structs

**Test**
- `Iverson.Server/Iverson.ClientConformance.Tests/VerifierTests.cs`
- `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/StructConverterTest.java`
- `Iverson.Clients/Python/tests/test_entity_coordinator.py`
- `Iverson.Clients/TypeScript/tests/` (annotations + core)
- `Iverson.Clients/Go/iverson/coordinator_test.go`

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and by four CDR rounds; NOT re-verified here. Load-bearing items:

- **A3** Java's `fromValue(Value, Class<?>, Type)` has no `STRUCT_VALUE` case (`:172-200`); removing the skip alone nulls every navigation field.
- **A9** TypeScript's `getRelations` collapses the factory to `relatedType: typeFactory().name`; the constructor survives only in the raw `IVERSON_RELATIONS` metadata.
- **A13** A `Hydrated map[string]any` field reaches `goTypeToClr` through `ExtractMeta` (`tags.go:228-231`) and fails `Register`; an extraction/registration exclusion is required.
- **A23** TS `entityToPayload` iterates the live instance (`core.ts:470`), so a hydrated member would be sent on the next write.
- **A21** Go `entityToStruct` iterates `t.NumField()`; an untagged `Hydrated` would be serialized.
- **A22** Python `_entity_to_struct` iterates `__annotations__`, so a dynamic attribute cannot leak — no exclusion needed.
- **A24** The plural-preserving strip is what keeps `py_tag_ids`/`py_tag_id` distinct.
- **A25** The carrier fallback must be keyed on the lookup yielding no hydrated objects, not on absence: `relationPropertyName` returns `fm.Name` unchanged for `one_to_many` (`registrar.go:343`), so Go's declared member shadows the carrier entry under the same name.
- **A26** Go's `GoAuthor.GoArticles` is `[]string`, which is why Go's `one_to_many` routes to the carrier and its `OneToMany` skip stays.
- **A8 / A11** The derived member is *not* guarded against a model that separately declares a field of that name. The implementation must detect or reject that collision itself.

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | All twelve files the tasks modify exist at the cited paths | `Verifier.cs` 679 lines, `Requirements.cs` 329, `StructConverter.java` 222, `core.py` 808, `annotations.py` 338, `core.ts` 866, `annotations.ts` 394, `coordinator.go` 803, `registrar.go` 343, `tags.go` 343, `models.go` 73, standard 326 |
| P8 | Signature | `TypeDescriptor` is proto-generated and its relations expose `PropertyName`/`ForeignKey` | `Verifier.cs:85` `Parser.Parse<TypeDescriptor>`, used at `:200` and `:446` |
| P9 | Signature | `FindProperty` and `CountHydratedObjects` are `static` members of `Verifier`, reachable from `VerifyDepthCapability` without a signature change | `Verifier.cs:464`, `:484`, same class as `:443` |
| P10 | Signature | Python's `iverson_entity` is a class decorator that sets `_iverson_meta["type_name"] = cls.__name__` and returns the class | `annotations.py:221`, `:320-321` |
| P12 | Signature | Go's `Register` holds the entity's `reflect.Type` | `registrar.go:65`, `t := reflect.TypeOf(e)` |
| P13 | Signature | `structToEntity[T any](s *structpb.Struct) (T, error)` takes no registrar, so the registry must be package-level | `coordinator.go:614` |
| P14 | Command | Gate/harness tests run via `dotnet test Iverson.Server/Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj` | csproj exists; same form used by every task on this branch |
| P15 | Command | Java builds with Maven from `Iverson.Clients/Java/pom.xml` | aggregator `pom.xml` plus `client/pom.xml` both present |
| P16 | Command | Python tests run under pytest with `testpaths = ["tests"]` | `pyproject.toml:25-26`, `pytest>=8.0` at `:17` |
| P17 | Command | TypeScript `npm test` runs typecheck **then** vitest | `package.json:16`, `"test": "npm run typecheck && vitest run"` |
| P18 | Command | Go module is `github.com/iverson/clients/go`, Go 1.25 | `go.mod:1,3` |
| P19 | Command | The live matrix is `dotnet run --project Iverson.Server/Iverson.ClientConformance -- --scenarios crud-roundtrip` | same invocation used by the predecessor plan (`2026-08-15-…-plan.md:221,244`) |
| P20 | Command | **No `docker compose build iverson-api` is required by this plan** — no task changes server code; the harness and drivers run locally against `IVERSON_GRPC_URL` (default `http://localhost:8080`). The stack must be *running*, not rebuilt | `Program.cs:16`; every modified file is under `Iverson.ClientConformance`, `Iverson.Clients/`, or `docs/` |
| P21 | Ordering | Task 1 introduces no symbol Tasks 2-5 consume — the const's **name** is unchanged, only its value | `Requirements.cs:164`; consumers reference the symbol (`Verifier.cs:456`, `VerifierTests.cs:766,781`), never the literal |
| P22 | Ordering | Tasks 2-5 touch disjoint trees and may run in any order | Java / Python / TypeScript / Go directories share no file |
| P23 | Ordering | Task 1 alone keeps the gate green — check 1 compares Active IDs to consts, and both change together | retiring `IVC-LIFE-007` + authoring the successor + renumbering the const is one atomic edit set |
| P24 | Code validity | `Value.KindCase.STRUCT_VALUE` / `getStructValue()` is the correct protobuf idiom here | `StructConverter.java:173` already uses `Value.KindCase.LIST_VALUE` |
| P25 | Code validity | `map[string]any` is valid in this module | `go.mod:3`, `go 1.25.0` |
| P26 | Code validity | `IVERSON_RELATIONS` metadata entries carry `typeFactory: () => Function`, and the symbol is **module-private** | `annotations.ts:340-344` (`PendingRelationMeta`), `:32` (`const IVERSON_RELATIONS`, not exported) |
| P28 | Consumer impact | `getRelations` is public API with 3 production and 12 test call sites, so Task 4 adds a **new** accessor rather than changing its return shape | exported at `index.ts:26`; used at `core.ts:255,469,490` and throughout `tests/annotations.test.ts` |
| P29 | Consumer impact | `structToEntity` has 7 call sites, all in `coordinator.go`; carrier population is additive and breaks none | `coordinator.go:249,271,291,307,334,371,397` |
| P30 | Consumer impact | **An existing Java test asserts the behaviour Task 2 removes** and must be rewritten, not deleted | `StructConverterTest.java:183`, `fromStruct_skipsNavigationPropertyLeavingItNull`; production callers are `EntityCoordinator.java:140,156` |
| P31 | Consumer impact | Python `_from_struct` has 8 call sites, all in `core.py`; a second pass is additive | `core.py:583,599,613,627,641,651,660` |
| P32 | Consumer impact | `VerifyDepthCapability` has one production caller and three tests; the fallback needs no signature change | `CrudRoundtripScenario.cs:165`; `VerifierTests.cs:756,770,785` |
| P33 | Consumer impact | Renumbering the const's value breaks nothing — every consumer references the symbol | `Verifier.cs:456`, `VerifierTests.cs:766,781` |
| P34 | Sibling sweep | Every identifier the tasks name resolves at its point of use (meta-class: every referenced name resolves) | `VerifyDepthCapability:443`, `FindProperty:464`, `CountHydratedObjects:484`, `Normalize:94`, `isNavigationProperty`/`fromValue`/`toStruct` in `StructConverter.java`, `_from_struct:685`/`_entity_to_struct:390`/`_relation_property_name:100` in `core.py`, `payloadToEntity:486`/`entityToPayload:466`/`getRelations:387` in TS, `structToEntity:614`/`entityToStruct:487`/`protoValueToGoValue:658`/`ExtractMeta`/`relationPropertyName:329`/`goTypeToClr` in Go |

## Tasks

### Task 1: The harness fallback, the requirement, and the const

These land together: check 1 compares the standard's `Active` IDs against `Requirements.cs`'s consts, and the axis-completeness check's mode 3 rejects a `Retired` ID in an Evidence cell. Splitting them reddens the gate at a task boundary.

**Files:**
- Modify: `Iverson.Server/Iverson.ClientConformance/Verifier.cs`, `Iverson.Server/Iverson.ClientConformance/Requirements.cs`, `docs/standards/iverson-client-standard.md`
- Test: `Iverson.Server/Iverson.ClientConformance.Tests/VerifierTests.cs`

**Interfaces:**
- Produces: the successor requirement ID that Tasks 2-5 are graded against.

- [ ] **Step 1: Retire `IVC-LIFE-007` and author its successor** in `docs/standards/iverson-client-standard.md`. Set the existing row's Status to `Retired`, leaving its Statement cell **byte-identical** — the standard's immutability convention (`:132-133`) requires it. Add a new row taking the next free LIFE number, `Active | Behaviour`, stating that the entity a depth-resolved read returns carries the related object's data, including that object's own key and not only the foreign key. Record the retirement rationale and the Kind rationale in the prose below the table, following the shape `IVC-LIFE-005`'s retirement note already uses at `:261-267`. Do **not** touch `#### Known non-conformance` — Task 6 owns it.

- [ ] **Step 2: Update LIFE's `#### Coverage` Evidence cell.** The "Depth-resolved read hydration" row cites `IVC-LIFE-007`; point it at the successor. A `Retired` ID left in an Evidence cell fires the axis-completeness check's mode 3.

- [ ] **Step 3: Renumber the const.** `Requirements.cs:164` holds `LifeDepthResolvedReadHydrated = "IVC-LIFE-007"`. Change the **value** only; the symbol name stays, so no consumer changes (P33). Update its doc comment to describe the successor's statement and its `Behaviour` Kind.

- [ ] **Step 4: Add the result-keyed carrier fallback** to `VerifyDepthCapability` (`Verifier.cs:443-457`). Today it counts hydrated objects under `FindProperty(depth1Entity, r.PropertyName)`. Change it so that when that yields **zero** hydrated objects, it retries inside the hydration-carrier property — the well-known member named `Hydrated`, matched through the existing `Normalize`. Key the retry on the count, not on the property's absence: Go's `one_to_many` declared member sits at top level under exactly the registered `PropertyName`, empty, and would otherwise shadow the carrier entry (A25).

- [ ] **Step 5: Write the tests.** In `VerifierTests.cs`, alongside the three existing cases: a relation hydrating only inside the carrier passes; a relation whose top-level property is present-but-empty **and** whose carrier holds the hydrated child passes (this is the shadowing case, and it is the one that must not regress); a relation absent from both fails.

- [ ] **Step 6: Prove the fallback can fail.** Delete the carrier retry, run, and confirm the two carrier tests redden naming the relation; restore. Then change the retry's condition from "no hydrated objects" back to "property absent", run, and confirm the shadowing test reddens. Record the actual failure output for both. A mutation that reddens nothing means the fallback does not bind.

- [ ] **Step 7: Run the suite and commit.**
```bash
dotnet test Iverson.Server/Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj
git add docs/standards/iverson-client-standard.md Iverson.Server/Iverson.ClientConformance Iverson.Server/Iverson.ClientConformance.Tests
git commit -m "retire IVC-LIFE-007 for a shape-neutral successor and reach Go's hydration carrier"
```

### Task 2: Java read-path hydration

**Files:**
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/StructConverter.java`
- Test: `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/StructConverterTest.java`

- [ ] **Step 1: Add the `STRUCT_VALUE` case to `fromValue(Value, Class<?>, Type)`** (`:172-200`), recursing into `fromStruct(value.getStructValue(), targetType)`. Do this **before** Step 2: the list branch at `:173-182` recurses into this same function, so without the struct case both a single nested struct and a list of structs resolve to `null`.

- [ ] **Step 2: Remove `if (isNavigationProperty(f)) continue;`** from `fromStruct`'s field-map construction (`:68`). The identical check in `toStruct` (`:41`) **stays** — that one is the FK-only write contract.

- [ ] **Step 3: Rewrite the test that asserted the old behaviour.** `StructConverterTest.java:183` is `fromStruct_skipsNavigationPropertyLeavingItNull`, which asserts precisely what Step 2 removes. Replace it with a test asserting the navigation property is now populated from a nested struct, carrying the child's own key. **Do not delete it without a replacement** — a removed assertion is indistinguishable from a passing one in a green suite.

- [ ] **Step 4: Add a collection case test** — a `List<JavaArticle>`-typed navigation field hydrating from a list of structs, which exercises `elementTypeOf` plus the new struct case together.

- [ ] **Step 5: Prove both changes bind.** Revert Step 1 alone and confirm the new tests redden with `null` navigation fields rather than passing; restore. Record the output.

- [ ] **Step 6: Run and commit.**
```bash
mvn -q -f Iverson.Clients/Java/pom.xml test
git add Iverson.Clients/Java
git commit -m "hydrate navigation properties on Java's read path"
```

### Task 3: Python registry and hydration pass

**Files:**
- Modify: `Iverson.Clients/Python/iverson_client/annotations.py`, `Iverson.Clients/Python/iverson_client/core.py`
- Test: `Iverson.Clients/Python/tests/test_entity_coordinator.py`

- [ ] **Step 1: Populate a name→class registry** in `iverson_entity` (`annotations.py:221`), keyed by `cls.__name__` — the same string `_iverson_meta["type_name"]` already carries (`:320-321`) and the same string a relation's `related_type` holds. A module-level dict is enough; registration happens at import, before any read.

- [ ] **Step 2: Add the hydration pass to `_from_struct`** (`core.py:685`). After the existing annotation loop, walk the relation set: derive the wire key with the existing `_relation_property_name` (`:100`), and if the struct carries it, resolve the related class from the registry and recurse. Assign under the idiomatic member — the FK member minus `_id` for `many_to_one`/`one_to_one`, minus `_ids` **plus a trailing `s`** for `many_to_many`. For `one_to_many` the declared member is already the navigation member: hydrate it in place, replacing the raw dicts the first pass leaves there. An unregistered related type falls back to the untyped child rather than raising.

- [ ] **Step 3: Guard the collision A8 leaves open.** If the derived member name is already a declared annotated field, that is a model error, not a silent overwrite — raise at hydration time naming the entity and the member.

- [ ] **Step 4: Test** — `many_to_one`, `many_to_many` and `one_to_one` each hydrating a typed instance carrying its own key; `many_to_many` landing on the **plural** member while `one_to_one` lands on the singular one (the A24 collision case); `one_to_many` hydrating typed instances in the declared member; an unregistered type falling back rather than raising; and a round-trip proving `_entity_to_struct` still sends no hydrated member.

- [ ] **Step 5: Prove the pass binds.** Delete the hydration pass and confirm every new test reddens; restore. Record the output.

- [ ] **Step 6: Run and commit.**
```bash
cd Iverson.Clients/Python && python -m pytest && cd -
git add Iverson.Clients/Python
git commit -m "hydrate typed relation children on Python's read path"
```

### Task 4: TypeScript accessor, hydration, and write-path exclusion

**Files:**
- Modify: `Iverson.Clients/TypeScript/src/annotations.ts`, `Iverson.Clients/TypeScript/src/core.ts`
- Test: `Iverson.Clients/TypeScript/tests/annotations.test.ts`, `Iverson.Clients/TypeScript/tests/core.test.ts`

- [ ] **Step 1: Export a new accessor from `annotations.ts`** returning each relation's `field`, `kind` and its **unresolved** `typeFactory`. It must live in this file because `IVERSON_RELATIONS` is module-private (`:32`). Leave `getRelations` (`:387`) exactly as it is — it is public API with 3 production and 12 test call sites (P28), and it resolves the factory to a name by design.

- [ ] **Step 2: Hydrate in `payloadToEntity`** (`core.ts:486`). After the declared-field loop, walk the relations: derive the wire key, and if the payload carries it, construct the related class via the new accessor's factory and recurse. Assign to `instance[navMember]` — the field minus `Id` for `many_to_one`/`one_to_one`, minus `Ids` **plus a trailing `s`** for `many_to_many`. `one_to_many` hydrates in place at its declared member.

- [ ] **Step 3: Exclude hydrated members in `entityToPayload`** (`core.ts:466`). It iterates `Object.getOwnPropertyNames(entity)` on the live instance, so without this a `getMapped` → `updateMapped` round trip sends the hydrated child back as `TsAuthor` and violates the FK-only write contract (A23). Compute the excluded set from the same derivation Step 2 used.

- [ ] **Step 4: Guard the A11 collision** as Task 3 Step 3 does, throwing rather than overwriting a declared field.

- [ ] **Step 5: Test** — the three FK-member kinds hydrating typed instances; `tsTagIds`→`tsTags` and `tsTagId`→`tsTag` landing on distinct members; `one_to_many` hydrating in place; and a `getMapped`→`updateMapped` round trip asserting the payload carries the foreign keys and **no** navigation member.

- [ ] **Step 6: Prove each of the three changes binds.** Delete the hydration block, then the exclusion block, running between each and recording which tests redden; restore both. An exclusion whose deletion reddens nothing is not tested.

- [ ] **Step 7: Run and commit.**
```bash
cd Iverson.Clients/TypeScript && npm test && cd -
git add Iverson.Clients/TypeScript
git commit -m "hydrate typed relation children on TypeScript's read path"
```

### Task 5: Go registry, carrier, and exclusions

One task: the exclusion, the population and the model declaration are mutually load-bearing — the carrier field fails registration without the exclusion, and proves nothing without the population.

**Files:**
- Modify: `Iverson.Clients/Go/iverson/registrar.go`, `Iverson.Clients/Go/iverson/tags.go`, `Iverson.Clients/Go/iverson/coordinator.go`, `Iverson.Clients/Go/conformance/models.go`
- Test: `Iverson.Clients/Go/iverson/coordinator_test.go`

- [ ] **Step 1: Exclude the carrier from metadata extraction.** `ExtractMeta` (`tags.go:228`) iterates every field including untagged ones, so a `Hydrated map[string]any` reaches `goTypeToClr`, which has no mapping for a map type, and `Register` fails outright (A13). Skip the field by its well-known name `Hydrated`. The name must be well-known rather than tag-driven: Task 1's fallback has to locate the carrier in a driver's JSON report, and a tag would let it be called anything.

- [ ] **Step 2: Populate a package-level name→`reflect.Type` registry** in `Register` (`registrar.go:65`, which already holds `reflect.TypeOf(e)`). It must be package-level because `structToEntity[T]` (`coordinator.go:614`) takes only a `*structpb.Struct` and has no registrar to reach through (P13).

- [ ] **Step 3: Populate the carrier in `structToEntity`.** For each relation, look up the wire key; if present, resolve the related type from the registry, build a typed pointer, and store it in `Hydrated` under the wire name — for `one_to_many` as well as the other three kinds. **Keep** the existing `OneToMany` skip at `:634-637` for the declared member: `GoAuthor.GoArticles` is `[]string`, and `protoValueToGoValue` has no struct case, so routing structs there yields one empty string per related row with no error (A26). An unregistered type stores the untyped child rather than failing the read.

- [ ] **Step 4: Exclude `Hydrated` from `entityToStruct`** (`coordinator.go:487`), which iterates `t.NumField()` and would otherwise serialize the untagged carrier under its own name (A21).

- [ ] **Step 5: Declare the carrier** on the conformance entity structs in `conformance/models.go`.

- [ ] **Step 6: Test** — `Register` succeeds with a carrier-bearing struct (this is what Step 1 buys); the three FK-member kinds landing typed pointers in `Hydrated`; `one_to_many` landing in `Hydrated` while `GoArticles` stays untouched; `entityToStruct` emitting no `Hydrated` key; an unregistered type falling back.

- [ ] **Step 7: Prove each change binds.** Revert Step 1 and confirm the registration test reddens; revert Step 4 and confirm the write test reddens; revert Step 3's `one_to_many` branch and confirm that test reddens. Restore each and record the output.

- [ ] **Step 8: Run and commit.**
```bash
cd Iverson.Clients/Go && go test ./... && cd -
git add Iverson.Clients/Go
git commit -m "hydrate typed relation children into Go's carrier"
```

### Task 6: Live matrix, then the standard's non-conformance record

**Files:**
- Modify: `docs/standards/iverson-client-standard.md` (`#### Known non-conformance` only)

**Interfaces:**
- Consumes: every prior task.

- [ ] **Step 1: Confirm the stack is up.** No image rebuild is required — this plan changes no server code (P20) — but the harness needs a live server at `IVERSON_GRPC_URL`.
```bash
docker compose ps
```

- [ ] **Step 2: Run the full matrix.**
```bash
dotnet run --project Iverson.Server/Iverson.ClientConformance -- --scenarios crud-roundtrip
```

- [ ] **Step 3: Record what the successor requirement actually reports per client**, from the run's own output. Do not infer it from the code changes — the point of this step is that the design's prediction and the live result are separate facts.

- [ ] **Step 4: Rewrite or remove `#### Known non-conformance`** (`iverson-client-standard.md:283`) according to Step 3's evidence. Its current text names four failing clients and attributes all four to a premise this spec opens by falsifying, so it cannot survive unchanged. If every client now passes, remove the section; if any still fails, restate it with the real cause and the real client list.

- [ ] **Step 5: Run the gate once more and commit.**
```bash
dotnet test Iverson.Server/Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj
git add docs/standards/iverson-client-standard.md
git commit -m "record the live hydration result in the standard"
```

## Tasks NOT in this plan

Inherited verbatim from the spec's "What this does not do":

- It does not change any model's declaration style. No client is asked to move a relation off its
  foreign-key member.
- It does not touch the write path's contract, only guards it against two new leaks.
- It does not give Go a typed navigation field. Go's caller performs a type assertion on
  `Hydrated["GoAuthor"]`. That is inherent to `map[string]any` and was accepted when decision 2 was
  taken.

## Known issues inherited from spec

- Go's conformance proof is weaker than the other four clients': a map entry carrying a key rather
  than a typed field. Accepted under decision 2 — the language admits nothing stronger without
  requiring a declared navigation field, which decision 1 rejected.
- The registry's unregistered-type fallback means a hydration miss is silent at the library level.
  It is not silent at the harness level, where `IVC-LIFE-007`'s successor asserts reachability, but a
  library caller sees an untyped child rather than an error.
