# Critical Design Review: 2026-08-14-relation-foreign-key-integrity-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson-conformance/docs/specs/2026-08-14-relation-foreign-key-integrity-design.md`
**Verified Assumptions section:** present

Coverage was re-derived from the spec as it now stands, before consulting round 1. The round-1
fix (Part B's navigation-property skip) is a single enumeration row here, not the search area.

## 0. Coverage enumeration

### Sections

| # | Section | Disposition |
|---|---|---|
| S1 | Problem / Defect 1 | ok — re-read `EntityRelationResolver.cs:137`; the `Fields[relation.PropertyName]` assignment and the collision condition are stated correctly |
| S2 | Problem / Defect 2 | ok — `StructConverter.java:162-180` still has no `LIST_VALUE` case; `:110` still handles `Collection` on write |
| S3 | Design / Part A | ok — see R1-R2; both failure directions checked against the real derivation functions |
| S4 | Design / Part B (incl. round-1 skip) | ok — see R3-R5; the skip's predicate verified reachable, see R3 |
| S5 | Not changed | ok — re-confirmed no server/orchestrator/driver/assertion edit is implied; both parts are client-side only |
| S6 | Consequence: server rejection path goes live | ok on mechanics — `RelationValidator.cs:23,47` gate and the three write paths re-read. One rationale clause dropped, see A2 |
| S7 | Testing | ok — the three cited test files re-read; the added depth-1 nav-skip case is constructible as a unit test (build a `Struct` carrying a `JavaTags` list; no live stack needed) |
| S8 | Verified assumptions | see §1 |
| S9 | Out of scope | ok — both items remain genuinely outside the two defects and neither carries a safety argument for the design |

### Rules and operands

| # | Rule | Disposition |
|---|---|---|
| R1 | Part A strip rule, **under-inclusion** | ok — `many_to_many` FKs are always `{RelatedType}Ids` (`core.py:120`, `core.ts:122`, `registrar.go:317`), so `pascal == fk` implies the name ends in `Ids`. The proxy is a strict superset of the failure set; nothing colliding escapes. Re-derived independently this round rather than carried over |
| R2 | Part A strip rule, **over-inclusion** | dropped — renames some non-colliding members (`tag_ids` + type `PyTag`), but no client reads a nav property by name: `core.py:696`, `core.ts:488`, `coordinator.go:640` all key `many_to_many` reads off the FK. No behavior change. Fails literal-wrongness |
| R3 | Round-1 skip: is `isNavigationProperty` actually reachable for the field it must skip? | ok — **this was the round's main risk.** The predicate requires the collection's element type to carry `@IversonEntity` (`StructConverter.java:141-145`). `JavaTag` is annotated `@IversonEntity` (`JavaTag.java:11`), and `javaTags` is `List<JavaTag>` (`JavaArticle.java:38`), so the predicate returns true and the skip fires. Had `JavaTag` lacked the annotation, the round-1 fix would have been inert **and** `toStruct` would already be serializing `javaTags` as a column |
| R4 | Part B element typing, **under-inclusion**: raw `List` / `List<?>` | dropped — no resolvable element `Class`, so such fields stay at today's `null`. No entity in the codebase declares a raw collection (`StructConverterTest.java:53`, `JavaArticle.java:35`, `:38` all parameterized). Behavior unchanged from today |
| R5 | Part B element typing, **over-inclusion**: malformed element data now throws where it previously returned null | dropped — `UUID.fromString` on a bad string would surface as the existing `RuntimeException` wrapper (`:79-81`) instead of a silent `null`. Requires the server to emit a non-GUID into a UUID[] column, which `SchemaRegistrationOrchestrator.cs:95-105` forbids at registration. Speculative |

### Data-flow arrows

| # | Arrow | Disposition |
|---|---|---|
| D1 | client registrar → `RegisterSchema` → validation → DDL → registry **(persistence boundary)** | ok — checked deeper than round 1. Relation validation keys entirely off `relation.ForeignKey` (`SchemaRegistrationOrchestrator.cs:84-105`): it requires a matching `ScalarColumn` and, for `ManyToMany`, exactly `UUID[]`. `PropertyName` appears only in error message text. `ApplySchemaAsync(..., SchemaDriftPolicy.Throw)` consumes `ToTableSchema`, which carries columns only, so the rename produces no drift; `registry.RegisterAsync` replaces the descriptor wholesale, so re-registration picks the new name up cleanly |
| D2 | descriptor → `EntityRelationResolver` → hydrated payload → `Verifier` | ok — `Verifier.cs:255-256,270` read nav and FK from the same descriptor, so both track the rename together. `Verifier.cs:166-167` additionally asserts `PropertyName != ForeignKey` at register time, which Part A turns green |
| D3 | payload → `RelationValidator` on write **(trust boundary)** | ok — the newly-live rejection consumes `relation.PropertyName`; no client emits that key, since all three write paths derive from the FK (`core.py:409`, `core.ts:468`, `coordinator.go:547`) |
| D4 | server response → `fromStruct` → POJO → GSON → driver document | ok — with the round-1 skip in place, `javaTags` is excluded from `fieldMap` and `javaTagIds` populates via the new `LIST_VALUE` case. Element type resolves through the `ParameterizedType` unwrap; `List<UUID>` elements arrive as `STRING_VALUE` and convert at `:166` |
| D5 | server response → `fromStructAsMap` → `groupBy` / `pipeline` result maps | ok — `EntityCoordinator.java:250-258, 274-282` pass maps through untouched; previously-`null` values become `List<Object>`. Nested structs inside a list still resolve to `null` via the untyped `fromValue` default, unchanged from today |
| D6 | descriptor → `GetSchema` → agent-facing catalog | ok — `ObjectMappingGrpcService.cs:115` projects `PropertyName = r.PropertyName` into `SchemaRelation`, so the catalog reports the renamed nav property. That is the intended post-fix name and matches what the hydrated payload actually carries; no consumer compares it against a column list |

## 1. Verified-assumptions cross-check

All fifteen listed assumptions reconfirmed under fresh read, including `B7` added after round 1.
Spot-checks that mattered this round:

- **A6** — re-derived the four-kind sweep independently rather than trusting round 1's version. `one_to_many` FKs are `{ThisType}Id` on the *related* row and are exempt from the registration relation check entirely (`SchemaRegistrationOrchestrator.cs:84`, `.Where(r => r.Kind != OneToMany)`). Still holds.
- **B7** (the row added by round 1) — `StructConverter.java:39` remains the sole `isNavigationProperty` call site; `fromStruct:63-66` still builds its map from `getAllFields` unfiltered. The row's "Failed" status is accurate as written, and Part B now carries the corresponding skip.

**Span check — one uncovered dependency, verified in-round:**

Part A's naming rationale and the `Consequence` section each depend on a harness scenario that does
not exist. `Iverson.Server/Iverson.ClientConformance/Scenarios/` contains exactly one file,
`CrudRoundtripScenario.cs`; there is no interop scenario and no `nav-property-rejected` scenario
(the only scenario name defined anywhere is `crud-roundtrip`, `CrudRoundtripScenario.cs:31`). Both
clauses are written in the present tense about code that has not been built.

Verified, and **not** promoted to §2: neither clause is load-bearing for either defect's fix. The
FK-survival and Java-array outcomes hold regardless of whether S3 and S4 are ever written, and
round 1 independently established (R2, carried forward as this round's R2) that no client reads a
navigation property by name — so the naming choice is behaviorally inert either way. Recorded here
so the gap is visible rather than implied.

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1** — `fromStruct` had no navigation-property skip, so Part B's `LIST_VALUE` case
  would have reached hydrated navigation properties and produced `javaTags = [null, null]`.
  Resolved: Part B now specifies reusing `isNavigationProperty` when building `fieldMap`, and this
  round verified the predicate is actually reachable for `javaTags` (see R3).
- **Round 1 §1 span check** — the uncovered "`fromStruct` reads only what `toStruct` writes"
  dependency. Resolved: ratcheted into the spec's `Verified assumptions` table as row `B7`, recorded
  as Failed with its evidence.

## 5. Recommendation

✅ **Approve as-is** — §2 and §3 are both empty. Spec is ready for implementation planning.
