# Critical Implementation Review: 2026-08-09-relation-key-typing-implementation-plan (Round 2)

**Plan:** /home/ben/repositories/Iverson-fk-only/docs/plans/2026-08-09-relation-key-typing-implementation-plan.md
**Verified plan-level assumptions section:** present

⚠️ 4 commits since plan-write time (SHA `a199df2`); cited file:line references re-checked under §1. All four are documentation commits (round-2 design review, the plan, round-1 implementation review, the round-1 fix application) — no code drift.

## 0. Coverage enumeration

Enumeration re-derived against the current plan text before reading round 1's diff. The revised Task 3 Step 1 block is one row here, not the search area.

### Task 1 — Server registration guard

| Surface | Disposition |
|---|---|
| Step 1 prose + code — fixture retype | ok — `SimpleType` (`SchemaRegistrationOrchestratorTests.cs:36-44`) appends `extraScalars` as `ClrString`; adding `ArticleId`/`OwnerId` as separate `ClrGuid` properties afterwards is the same mutation the file already performs at `:226-228` on a protobuf `RepeatedField` |
| Step 2 code — four test methods | ok — re-resolved every identifier against the file's imports (`FluentAssertions`, `Grpc.Core`, `Iverson.Client.Contracts`, `Xunit`) and the `_sut`/`SimpleType` members; `Client.Contracts.RelationKind.{ManyToOne,ManyToMany,OneToMany}` all exist |
| Step 2 — fixtures reach the guard | ok — each passes `ValidateIdentifier` (`:35-37`), `ValidateEnrichmentTargets` (`:52`), `owner_field` (`:54-56`) and mandatory `tenant_field` (`:61-66`) before the new checks |
| Step 4 code — key guard + restructured relation loop | ok — `ColumnDescriptor(string Name, string SqlType, bool IsNullable)` (`SchemaDescriptor.cs:51`) is a record, so `column is null` is valid; `ClrTypeToSql` returns `mapping.SqlType` (`SchemaBuilder.cs:278-284`) yielding exactly `"UUID"` (`:236`), `"TEXT"` (`:237`), `"UUID[]"` (`:252`) |
| Step 4 wiring — membership before type; guard before DDL | ok — the restructured loop preserves `:386`'s message assertion, and both checks sit between `:50` (descriptor built) and `:84` (`ApplySchemaAsync`) |
| Commands (Steps 3, 5) | ok — `Iverson.Api.Tests.csproj` present; `--filter FullyQualifiedName~` is valid xUnit syntax |

### Task 2 — Go

| Surface | Disposition |
|---|---|
| Step 1 code — `assertFkProperty` flip + `TestGuidTagYieldsClrGuid` | ok — `coordinator_test.go:190-192` is the sole `CLR_STRING` expectation; `propsByName` (`:170`) takes `interface{}` and calls `NewSchemaRegistrar(nil, e)` + `buildRequest`, exactly as the existing passing tests do, so a nil stub is an established pattern |
| Step 3 code — tag const, `FieldMeta.IsGuid`, parse line | ok — `tags.go:100`, `:132`, `:235` are where the plan says |
| Step 4 code — override after `goTypeToClr`; FK retype at `:128` | ok — `registrar.go:69` is the sole call site; overriding `clrType` and leaving `isArray` preserves array rendering |
| Step 4 — no duplicate FK property | ok — `tags.go:315` routes any field with a non-empty `RelationKind` into `meta.Relations`; only the else-branch at `:324` appends to `meta.Fields` |
| Step 5 — sample tags | ok — `sample/models/{tag,author,article}.go:5,5,7` |
| Commands | ok — `go.mod` at `Iverson.Clients/Go/go.mod` |

### Task 3 — TypeScript

| Surface | Disposition |
|---|---|
| Step 1 prose — insertion point and idiom | ok — `describe('_buildRequest — relations')` is at `schema-registrar.test.ts:244`; its tests do build inline via `registrar._buildRequest(X)` + `Object.fromEntries(...)` (`:248`, `:257-261`), and it does declare fixtures locally inside a test (`Author`/`NavArticle`, `:265-281`), as the prose claims |
| Step 1 code — `@ManyToMany` usage | → §2.1 |
| Step 1 code — remaining identifiers after the finding | `IversonEntity`, `IversonKey`, `IversonTenant`, `ClrType`, `RegAuthor`, `RegArticle` are all imported or file-local (`:7-32`, `:37`, `:48`). `ManyToMany(typeFactory: () => Function)` exists (`annotations.ts:333`), so the thunk form `() => RegAuthor` is correct |
| Step 1 code — `new SchemaRegistrar(makeStub())` arity | dropped — the real constructor takes two arguments (`new SchemaRegistrar(stub, [Cls])`, used at `:247`, `:258`, `:284`, `:305`), so the one-argument sketch is wrong. But the step's own closing sentence — *"Match the surrounding tests' exact registrar/stub construction rather than the sketch above if the block instantiates them differently"* — names this exact discrepancy and directs the implementer to the correct form. Self-correcting instruction; fails literal-wrongness |
| Step 1 code — `TaggedPost`'s array relation member | ok — `regAuthorIds: string[]` never reaches the array-without-`@IversonArray` throw (`core.ts:271-278`) because `:266` skips relation fields first |
| Step 1 code — the asserted FK name | ok — TypeScript's naming enforcement (`core.ts:244-254`) covers `many_to_one`/`one_to_one` only, so an m2m member name is unconstrained; `inferFk` yields `{RelatedType}Ids` = `RegAuthorIds`, matching the assertion |
| Step 3 code — decorator + `getGuidFields` | ok — mirrors `annotations.ts:240-261` (module `Symbol`, `Reflect.getMetadata`/`defineMetadata` on `target.constructor`, paired getter); `reflect-metadata` already imported by that module |
| Step 4 wiring — resolution site | ok — `core.ts:216` `const arrayFields = getArrayFields(cls);` is inside `describeEntity` (`:200`), which also holds the loop (`:265-291`) and `:280` |
| Step 4 code — `clrType` expression; FK retype at `:318` | ok — `:280` is the sole `jsTypeToClr` call site; `arrayElement ?? guid ?? jsTypeToClr` keeps `@IversonArray` precedence; `:319`'s `isArray` left alone |
| Step 5 — sample decoration | ok — `sample/models/{Tag,Article,Author}.ts:7,15,7` declare `id: string = ''` |
| Commands | ok — `package.json` `"test": "npm run typecheck && vitest run"` — the typecheck gate is why §2.1 is fatal |

### Task 4 — Python

| Surface | Disposition |
|---|---|
| Step 1 prose — flip `:315`, sweep for others | ok — the sweep sentence reaches `:322`'s `mtm_prop.clr_type == mapping_pb.CLR_STRING`, which the preceding "add the same assertion" phrasing would otherwise leave contradicting the new one |
| Step 3 code — retype at `:252-259` | ok — `core.py:254` is the `clr_type` line, `:257` the `is_array` line the plan preserves |
| Step 4 code — `id: uuid.UUID` + `import uuid` | ok — `sample/models.py:2` has `from __future__ import annotations`, so annotations are strings resolved by `get_type_hints` (`core.py:172`), which needs `uuid` module-bound; `iverson_key()` is a default-value marker and Python enforces no annotation, so the assignment is valid |
| Step 4 prose — leave `author_id: str` alone | ok — `core.py` builds `relation_fields` from `meta["relations"]` and skips those names in the property loop, so the synthesized property (Step 3) is the only source of that column's type |
| Step 4 — dynamic pass on the retyped samples | ok — `_entity_to_struct` has both an `isinstance(value, str)` and an `isinstance(value, uuid.UUID)` branch (`core.py:400-405`), so sample code that still assigns a `str` id keeps serializing; `sample/main.py` makes no `get`/`delete` call that would pass a `UUID` object into a protobuf `string` field |
| Commands | ok — `pyproject.toml:25-26` `testpaths = ["tests"]` |

### Cross-task contracts and rule-like content

| Row | Disposition |
|---|---|
| Cross-task contracts | ok — none exist. Four disjoint file sets across four language trees; no task consumes an artifact another produces; client tasks mock the transport so none depends on Task 1's guard. No persistence-boundary handoff to flag |
| Rule: kind → required SQL type, over-inclusion | ok — no conforming shape is rejected: m2o/o2o FKs are scalar in all five clients → `"UUID"`; m2m FKs are arrays in all five → `"UUID[]"` |
| Rule: kind → required SQL type, under-inclusion | ok — the only exemption is `OneToMany`, whose FK is checked when the related type registers its reciprocal; `TEXT`, `TEXT[]`, and scalar-`UUID`-where-`UUID[]`-is-required all fail the exact-string comparison |
| Rule: key column must be `UUID`, both directions | ok — `SchemaBuilder.cs:163` passes `isArray: false`, so `"UUID"` is the only conforming value, and the SQL type derives from the declared `ClrType` rather than being hardcoded, so the check is falsifiable |

## 1. Verified-plan-assumptions cross-check

All 22 reconfirmed under fresh reads, including both rows amended after round 1:

- **12** (amended) — confirmed as written: `core.ts:200` `export function describeEntity(cls: Function)`, `:216` `const arrayFields = getArrayFields(cls);`, loop at `:265-291`, derivation at `:280`, and `jsTypeToClr` (`:73`) has that one caller.
- **22** (amended) — confirmed clause by clause: `propsOf` at `:335` and `:410`, each nested in a different `describe`; no `propertiesOf` anywhere; relations block at `:244` indexing inline via `Object.fromEntries`; top-level fixtures `RegAuthor` (`:37`) and `RegArticle` (`:48`, `@ManyToOne` only); no many-to-many fixture in the file.
- **2, 3** — `SchemaDescriptor.cs:51`, `:67` unchanged.
- **6, 7, 8** — the two breaking fixtures, the three orchestrator-constructing test files, and the ten direct-`ColumnDescriptor` files all re-confirmed.
- **9, 10** — `registrar.go:69` sole `goTypeToClr` caller; `tags.go:100,132,235` the tag-parsing sites.
- **14** — still correct: TypeScript has no synthesized-FK `clrType` assertion to flip.
- **15, 16, 17, 18, 19, 20, 21** — sample key fields, the four commands, task independence, and the server test style all re-read and unchanged.

### Span check

**One uncovered dependency.** Row 22 now records the test file's helper scoping and fixture inventory, but nothing in the table covers **which relation decorators that file imports** — and Task 3's revised test introduces the first use of one that isn't imported. → §2.1.

## 2. Literal-wrongness findings

### §2.1 — Task 3's new many-to-many fixture uses a decorator the test file does not import

**Description.** The revised Task 3 Step 1 declares `@ManyToMany(() => RegAuthor)` on `TaggedPost`. `ManyToMany` is exported from `src/annotations.ts`, but `tests/schema-registrar.test.ts` imports only `ManyToOne` and `OneToMany` from that module — the file has never had a many-to-many fixture, which is precisely why the round-1 fix had to introduce one. The step gives no instruction to extend the import list, and its closing hedge is scoped to "registrar/stub construction", not imports.

Under `npm test` this is `TS2304: Cannot find name 'ManyToMany'` at the typecheck gate, before vitest runs. Task 3 cannot reach Step 2's "run and confirm failure".

**Evidence.**
- `Iverson.Clients/TypeScript/tests/schema-registrar.test.ts:7-23` — the import block from `'../src/annotations.js'`; its relation entries are `ManyToOne,` and `OneToMany,` only.
- `Iverson.Clients/TypeScript/src/annotations.ts:333` — `export function ManyToMany(typeFactory: () => Function): PropertyDecorator` — exists and matches the thunk call form used in the plan.
- `Iverson.Clients/TypeScript/package.json` — `"test": "npm run typecheck && vitest run"`.

**Proposed fix.** Add one sentence to Task 3 Step 1, before the code block: *"Add `ManyToMany` to the existing `'../src/annotations.js'` import list at `schema-registrar.test.ts:7-23` — the file currently imports only `ManyToOne` and `OneToMany`, since no many-to-many fixture existed before."*

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1** — Task 3 Step 1's tests called `propertiesOf` (nonexistent) and `RegRelKindsArticle` (a Python fixture name), and asserted `RegTagIds` against a fixture with no many-to-many. Resolved: the tests now target `describe('_buildRequest — relations')` (`:244`), index inline via `Object.fromEntries`, use `RegArticle` for the many-to-one half, and declare a local many-to-many fixture for the other half. Assumption row 22 now records the file's helper scoping and fixture inventory.
- **Round 1 §2.2** — Step 4's wiring named `getRelations` as the place to resolve `getGuidFields`. Resolved: it now names `core.ts:216`, inside `describeEntity`, and assumption row 12 pins the enclosing function rather than only the line.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**

One finding, one sentence to fix. It is a direct consequence of round 1's own fix — introducing the first many-to-many fixture in a file that never had one — which is why the round-2 sweep re-derived coverage over the revised block rather than treating it as settled. Everything else came back clean on a fresh pass, including the two mechanisms most likely to bite silently at execution time (Go not double-emitting the FK-bearing relation field, and TypeScript's relation-field skip at `core.ts:266` keeping the new array-typed m2m member away from the `@IversonArray` throw). The one other arity error in the same block — `new SchemaRegistrar(makeStub())` against a two-argument constructor — is left as a dropped row rather than a finding, because the step's own closing sentence directs the implementer to the surrounding tests' construction form.
