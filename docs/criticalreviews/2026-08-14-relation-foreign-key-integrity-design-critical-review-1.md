# Critical Design Review: 2026-08-14-relation-foreign-key-integrity-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson-conformance/docs/specs/2026-08-14-relation-foreign-key-integrity-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

### Sections

| # | Section | Disposition |
|---|---|---|
| S1 | Problem / Defect 1 | ok — re-read `EntityRelationResolver.cs:137` (`entityStruct.Fields[relation.PropertyName] = Value.ForList(...)`) and all three derivation functions; the collision mechanism is stated correctly |
| S2 | Problem / Defect 2 | ok — `StructConverter.java:162-180` has cases for `STRING_VALUE`/`NUMBER_VALUE`/`BOOL_VALUE` then `default -> null`; `:110` handles `Collection` on the write side. One-directional gap confirmed |
| S3 | Design / Part A | ok — see rules R1/R2 below |
| S4 | Design / Part B | → §2.1 |
| S5 | Not changed | ok — no server/orchestrator/driver/assertion edit is implied by either part; verified the Verifier reads `relation.PropertyName` (`Verifier.cs:254`) and so follows the descriptor automatically |
| S6 | Consequence: server rejection path goes live | ok — `RelationValidator.cs:23,47` gate re-read; all three write paths keyed off `inferFk` (`core.py:409`, `core.ts:468`, `coordinator.go:547`), so no client can trip the newly-live rejection |
| S7 | Testing | ok — `test_schema_registrar.py:341-358` and `schema-registrar.test.ts:321-322` pin `many_to_one` only; `StructConverterTest.java:110-120` is write-direction only. No existing test contradicts either part |
| S8 | Verified assumptions | see §1 |
| S9 | Out of scope | ok — both items (array-column-mapping Java re-audit; Java/.NET FK-member annotation shape) are genuinely outside the two defects and are not load-bearing for either fix |

### Rules and operands

| # | Rule | Disposition |
|---|---|---|
| R1 | Part A strip rule, **under-inclusion**: does "ends with `Ids`" miss a colliding case? | ok — the failure condition is `PropertyName == ForeignKey`, and `many_to_many` FKs are always `{RelatedType}Ids` (`core.py:120`, `core.ts:122`, `registrar.go:317`). So `pascal == fk` implies `pascal` ends with `Ids`; the proxy is a strict superset of the failure set. No collision escapes it |
| R2 | Part A strip rule, **over-inclusion**: does it rename members that were not colliding? | dropped — yes it does (member `tag_ids` with related type `PyTag` gives `TagIds` ≠ FK `PyTagIds`, and is still renamed to `Tags`), but no client reads a nav property by name: all three read paths key off `inferFk` for `many_to_many` (`core.py:696`, `core.ts:488`, `coordinator.go:640`). Renaming a non-colliding nav name changes no behavior. Fails literal-wrongness |
| R3 | Part A: could a stripped name collide with a **real scalar column** on the same entity, creating a new overwrite? | dropped — mechanically possible (declare `py_tag_ids` as `many_to_many` plus a scalar `py_tags`), but no reference or sample model across the five clients declares such a pair (`Python/conformance/models.py:24-50`, `Go/sample/models/tag.go`, `TypeScript/conformance/models.ts`). Speculative; fails literal-wrongness |
| R4 | Part B element typing, **under-inclusion**: raw `List` / `List<?>` with no resolvable type argument | dropped — the spec's `ParameterizedType` unwrap yields no element `Class`, leaving such fields at today's `null`. No entity in the codebase declares a raw collection (`StructConverterTest.java:53` `List<UUID>`, `JavaArticle.java:35` `List<UUID>`). Behavior is unchanged from today, so the asked-for outcome does not fail |
| R5 | Part B element typing, **over-inclusion**: `LIST_VALUE` arriving for a field the design did not intend to populate | → §2.1 |

### Data-flow arrows

| # | Arrow | Disposition |
|---|---|---|
| D1 | client registrar → server `RegisterSchema` → stored descriptor **(persistence boundary)** | ok — `property_name` is a descriptor field, not a column; `SchemaBuilder.cs:133-145` maps it straight into `RelationDescriptor` and only `ScalarColumns`/`fks` drive DDL (`:107-112`). Renaming a nav property produces no column drift on re-registration |
| D2 | stored descriptor → `EntityRelationResolver` → hydrated payload → orchestrator `Verifier` | ok — `Verifier.VerifyRelationHydrated` (`Verifier.cs:255-256,270`) looks the nav up by `relation.PropertyName` and the FK by `relation.ForeignKey`, both from the same descriptor, so both follow the rename together. Note the harness *already* asserts `PropertyName != ForeignKey` at the register phase (`Verifier.cs:166-167`), so Part A also turns a currently-failing registration assertion green for the three clients — a second, independent confirmation that the collision is a defect and not a supported shape |
| D3 | server payload → `RelationValidator` on write **(trust boundary)** | ok — covered by S6; the newly-live rejection consumes `relation.PropertyName`, which no client emits as a payload key |
| D4 | server response → Java `fromStruct` → POJO → GSON → driver phase document | → §2.1 |
| D5 | server response → Java `fromStructAsMap` → `groupBy`/`pipeline` result maps | ok — `EntityCoordinator.java:250-258, 274-282` pass the maps through untouched; values that were `null` become `List<Object>`. Additive |

## 1. Verified-assumptions cross-check

All fourteen listed assumptions reconfirmed under a fresh read. Spot-checks that mattered:

- **A5** — re-read all six cited sites. `coordinator.go:640` (`key = inferFK(fm, t.Name())`) and `:547` confirm Go keys both directions off the FK, independent of `relationPropertyName`. Holds.
- **A8** — `RelationValidator.cs:23` `navIsDistinctKey` re-read; the spec's characterization of the dependent and its effect is accurate. Holds.
- **B1** — `isNavigationProperty` re-grep'd: exactly two occurrences in the file, its declaration at `:129` and its single call at `:39`. This *confirms* A5's scope claim while simultaneously exposing §2.1 below — the assumption as written ("`fromStruct` is the only struct→POJO path") is true, but it does not cover what `fromStruct` does with fields `toStruct` never writes.

**Span check — one uncovered dependency:**

Part B's safety argument rests on "nested lists and lists of structs stay unsupported, **matching what `toStruct` can produce** for a declared array column." No listed assumption verifies that `fromStruct` reads only what `toStruct` writes. It does not — see §2.1. Verified in-round rather than deferred.

## 2. Literal-wrongness findings

### §2.1 — `fromStruct` has no navigation-property skip, so Part B's element rule reaches fields `toStruct` never produces

**Description.** The spec justifies leaving lists-of-structs unsupported by appealing to symmetry with `toStruct`. That symmetry does not exist. `toStruct` skips navigation properties (`StructConverter.java:39`, `if (isNavigationProperty(field)) continue;`), but `fromStruct` builds its `fieldMap` from `getAllFields(type)` with no such filter (`:63-66`) and then assigns every matching struct key (`:68-73`). `isNavigationProperty` has exactly one call site, and it is not in `fromStruct`.

So `fromStruct`'s input is not "what `toStruct` wrote" — it is whatever the server sends, which at depth ≥ 1 includes hydrated navigation properties. `JavaArticle` declares `private List<JavaTag> javaTags` (`JavaArticle.java:38`), and Java exposes the depth-taking read publicly as `EntityCoordinator.getMapped(String id, int depth)` (`EntityCoordinator.java:185-189`).

**Evidence.**
- `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/StructConverter.java:39` — the only `isNavigationProperty` call, in `toStruct`.
- `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/StructConverter.java:63-73` — `fromStruct` enumerates all fields and assigns unconditionally.
- `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/EntityCoordinator.java:185-189` — `getMapped(id, depth)` is public API.
- `Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance/models/JavaArticle.java:38` — `List<JavaTag> javaTags`.

**Effect.** Today `fromValue` returns `null` for a `LIST_VALUE`, so `javaTags` is silently `null` — wrong, but inert. After Part B, the new `LIST_VALUE` case recurses into elements that are `STRUCT_VALUE`, for which the typed `fromValue` still has no case, so each element resolves to `null`. A `getMapped(id, 1)` caller receives `javaTags = [null, null]` — a populated-looking list of nulls, which is a worse failure mode than the current `null` because it is indistinguishable from "two tags that failed to load."

This does not affect the harness (all five drivers read at depth 0), which is precisely why it would not be caught by the conformance run the spec is written against.

**Proposed fix.** Add the navigation-property skip to `fromStruct`, mirroring `toStruct` — reuse the existing `isNavigationProperty(Field)` predicate when building `fieldMap` at `:63-66`. This makes the spec's stated rationale true rather than aspirational, keeps lists-of-structs genuinely out of scope, and leaves `javaTags` at `null` for depth-0 reads exactly as today. Populating navigation properties from hydrated structs is a separate feature and should stay out of this spec.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — address §2.1, then proceed to implementation planning.
