# Critical Implementation Review: 2026-08-09-relation-key-typing-implementation-plan (Round 3)

**Plan:** /home/ben/repositories/Iverson-fk-only/docs/plans/2026-08-09-relation-key-typing-implementation-plan.md
**Verified plan-level assumptions section:** present

⚠️ 6 commits since plan-write time (SHA `a199df2`); cited file:line references re-checked under §1. All six are documentation commits (two design/implementation reviews per round plus the plan and its two fix applications) — no code drift.

## 0. Coverage enumeration

Enumeration re-derived against the current plan before consulting rounds 1–2. Two rows below deliberately redo checks earlier rounds made with weaker evidence.

### Task 1 — Server registration guard

| Surface | Disposition |
|---|---|
| Step 1 prose + code — fixture retype | ok — `SimpleType` (`SchemaRegistrationOrchestratorTests.cs:36-44`) types `extraScalars` as `ClrString`; adding `ArticleId`/`OwnerId` as separate `ClrGuid` properties is the mutation the file already performs at `:226-228` |
| Step 2 code — four test methods | ok — all identifiers resolve against the file's imports and members; `Detail` assertion shape matches `:401` |
| Step 2 — fixtures clear the earlier validations | ok — `ValidateIdentifier` (`:35-37`), `ValidateEnrichmentTargets` (`:52`), `owner_field` (`:54-56`), `tenant_field` (`:61-66`) all pass for each |
| **Consumer sweep — every non-UUID key in the server test tree** | ok — **redone properly this round.** Round 1's grep required `IsKey = true` and `ClrString` on the same source line, which would miss a key whose `ClrType` sits on a different line. Re-swept all 20 `IsKey = true` sites across `Iverson.Api.Tests` with surrounding context: every one is `ClrType.ClrGuid` (`SchemaRegistrationOrchestratorTests.cs:38,61,74,98,112,128,225,261,289,305,320,341,357,373`; `RegisterSchemaAuthorizationIntegrationTests.cs:200`; `ObjectMappingGrpcServiceTests.cs:153`; `SchemaBuilderTests.cs:24,43,65,121`). The key guard breaks no existing test |
| **Consumer sweep — every registered relation in the server test tree** | ok — **widened this round** from `RelationDescriptor` to all `Relations` references. `RegisterSchemaAuthorizationIntegrationTests` has none. `ObjectMappingGrpcServiceTests`' hits are a hand-built `SchemaDescriptor` (`:133`, `:428`) and assertions on projected/`GetSchema` output (`:346,347,379,414,436,541`) — none is a `Client.Contracts.RelationDescriptor` passed through `RegisterAsync`. The two breaking fixtures remain exactly `:205` and `:441` |
| Step 4 code — key guard + restructured relation loop | ok — `ColumnDescriptor(string Name, string SqlType, bool IsNullable)` (`SchemaDescriptor.cs:51`) is a record so `column is null` is valid; `ClrTypeToSql` returns `mapping.SqlType` (`SchemaBuilder.cs:278-284`) yielding exactly `"UUID"`/`"TEXT"`/`"UUID[]"` (`:236,237,252`) |
| Step 4 wiring — ordering and placement | ok — membership check stays ahead of the type check (preserving `:386`'s message assertion); both sit between `:50` (descriptor built) and `:84` (`ApplySchemaAsync`) |
| **Dynamic — partial registration when a dependent fails the guard** | dropped — `RegisterAsync` loops `RootType` then `Dependents` (`:33`), applying DDL and registering each type before moving on, so a dependent rejected by the new guard leaves the root already applied. But this is the loop's pre-existing behaviour, shared by the `owner_field`, `tenant_field`, and FK-membership checks the plan doesn't touch, and neither the spec nor the plan claims multi-type atomicity. For the single-type case the guard runs strictly before `ApplySchemaAsync`, so a rejected schema applies no DDL. Fails literal-wrongness against the stated outcome |
| Commands (Steps 3, 5) | ok — `Iverson.Api.Tests.csproj` present; `--filter FullyQualifiedName~` is valid xUnit syntax |

### Task 2 — Go

| Surface | Disposition |
|---|---|
| Step 1 code — `assertFkProperty` flip | ok — `coordinator_test.go:190-192`; its four call sites (`:205,209,213,217`) cover m2o, o2o, m2m and o2m, so one flip updates the whole set |
| Step 1 code — `TestGuidTagYieldsClrGuid` | ok — the fixture declares a key and an `iverson_tenant` field, satisfying `tags.go:329`. `props["Id"]`/`props["Name"]` are correct keys because `registrar.go:86` sets `Name: fm.Name` from the Go field name. **Checked the arity/form mismatch**: existing call sites pass values (`propsByName(t, Article{})`) while the plan passes `&GuidTagEntity{}` — both work, since `buildRequest` unwraps a pointer via `t.Kind() == reflect.Ptr → t.Elem()` (`registrar.go:59-61`) |
| Step 3 code — tag const, `FieldMeta.IsGuid`, parse line | ok — `tags.go:100`, `:132`, `:235` |
| Step 4 code — override after `goTypeToClr`; FK retype at `:128` | ok — `registrar.go:69` sole call site; overriding `clrType` and leaving `isArray` preserves array rendering |
| Step 4 — no duplicate FK property | ok — `tags.go:315` routes relation-tagged fields into `meta.Relations`; only the else-branch at `:324` appends to `meta.Fields` |
| Step 5 — sample tags | ok — `sample/models/{tag,author,article}.go:5,5,7`; `sample/` is a package in the same module, so `go test ./...` and `go vet ./...` compile it and would catch a broken sample |
| Commands | ok — `go.mod` at `Iverson.Clients/Go/go.mod` |

### Task 3 — TypeScript

| Surface | Disposition |
|---|---|
| Step 1 prose — insertion point, idiom, import instruction | ok — `describe('_buildRequest — relations')` at `schema-registrar.test.ts:244` builds inline via `registrar._buildRequest(X)` + `Object.fromEntries` (`:248`, `:257-261`) and declares fixtures locally inside a test (`:265-281`); the added import sentence names the correct block (`:7-23`) and the correct current contents (`ManyToOne`, `OneToMany`) |
| Step 1 code — identifiers | ok — `ManyToMany(typeFactory: () => Function)` exists (`annotations.ts:333`), so the thunk form is right; `IversonEntity`/`IversonKey`/`IversonTenant`/`ClrType`/`RegAuthor`/`RegArticle` are imported or file-local (`:7-32`, `:37`, `:48`) |
| Step 1 code — `new SchemaRegistrar(makeStub())` arity | dropped — the constructor takes two arguments (`:247`, `:258`, `:284`, `:305`), but the step's closing sentence directs the implementer to the surrounding tests' construction form. Self-correcting; fails literal-wrongness. (Recorded here rather than omitted, since it is a real discrepancy the reader will hit) |
| Step 1 code — `TaggedPost`'s array relation member | ok — `regAuthorIds: string[]` never reaches the array-without-`@IversonArray` throw (`core.ts:271-278`) because `:266` skips relation fields first |
| Step 1 code — the asserted FK name | ok — naming enforcement (`core.ts:244-254`) covers `many_to_one`/`one_to_one` only, so the m2m member name is unconstrained and `inferFk` yields `RegAuthorIds` |
| Step 3 code — decorator + `getGuidFields` | ok — mirrors `annotations.ts:240-261`; a property decorator's `target` is the prototype, so `target.constructor` keys the class, exactly as `IversonArray` does |
| Step 4 wiring + code | ok — `core.ts:216` is inside `describeEntity` (`:200`), which also holds the loop (`:265-291`) and `:280`; `arrayElement ?? guid ?? jsTypeToClr` keeps `@IversonArray` precedence; `:319`'s `isArray` untouched |
| Step 5 — sample decoration | ok — `sample/models/{Tag,Article,Author}.ts:7,15,7`; the step instructs adding the import to each |
| Commands | ok — `"test": "npm run typecheck && vitest run"` |

### Task 4 — Python

| Surface | Disposition |
|---|---|
| Step 1 prose — flip `:315`, sweep for others | ok — **the sweep's scope checked in both directions this round.** Under-inclusion: the only other synthesized-FK assertion is `:322` (`mtm_prop.clr_type == mapping_pb.CLR_STRING`), which the sweep sentence reaches. Over-inclusion: `test_schema_registrar.py:649,655,661` assert `clr_type` on ordinary declared properties (`Tags`, `Counts`, `Blob`) and would break if flipped — the step's "leave assertions on ordinary declared properties alone" excludes them correctly |
| **Consumer sweep — synthesized-FK assertions outside `test_schema_registrar.py`** | ok — swept the whole `tests/` directory: the only other `CLR_STRING` hits are `test_auth.py:171,195`, a `GetSchema` round-trip over a hand-built `SchemaField(name="title")` with no relation involved. `test_entity_coordinator.py` asserts payload struct **field names** (`:158`, `:169`), not `clr_type`, so the retype does not touch it |
| Step 3 code — retype at `:252-259` | ok — `core.py:254` is the `clr_type` line, `:257` the `is_array` line preserved |
| Step 4 code — `id: uuid.UUID` + `import uuid` | ok — `sample/models.py:2` has `from __future__ import annotations`, so annotations are strings resolved by `get_type_hints` (`core.py:172`), which needs `uuid` module-bound; `iverson_key()` is a default-value marker and Python enforces no annotation |
| Step 4 prose — leave `author_id: str` alone | ok — `core.py` skips `relation_fields` in the property loop, so the synthesized property is the only source of that column's type |
| **Dynamic — retyped samples at runtime** | ok — `_entity_to_struct` carries both an `isinstance(value, str)` and an `isinstance(value, uuid.UUID)` branch (`core.py:400-405`), so sample or user code assigning either form still serializes; `sample/main.py` makes no `get`/`delete` call that would pass a `UUID` object into a protobuf `string` field |
| Commands | ok — `pyproject.toml:25-26` `testpaths = ["tests"]` |

### Cross-task contracts and rule-like content

| Row | Disposition |
|---|---|
| Cross-task contracts | ok — none exist. Four disjoint file sets across four language trees; no task consumes an artifact another produces; client tasks mock the transport. No persistence-boundary handoff to flag |
| Rule: kind → required SQL type, over-inclusion | ok — no conforming shape is rejected: m2o/o2o FKs are scalar in all five clients → `"UUID"`; m2m FKs are arrays in all five → `"UUID[]"` |
| Rule: kind → required SQL type, under-inclusion | ok — only `OneToMany` is exempt, and its FK is checked when the related type registers its reciprocal; `TEXT`, `TEXT[]`, and scalar-`UUID`-where-`UUID[]`-is-required all fail the exact-string comparison |
| Rule: key column must be `UUID`, both directions | ok — `SchemaBuilder.cs:163` passes `isArray: false` so `"UUID"` is the only conforming value, and the SQL type derives from the declared `ClrType` rather than being hardcoded, making the check falsifiable |

## 1. Verified-plan-assumptions cross-check

All 22 reconfirmed under fresh reads.

- **6** — re-confirmed on stronger evidence than round 1: the full 20-site `IsKey = true` sweep and the widened `Relations` sweep both agree that exactly two fixtures (`:205`, `:441`) break.
- **7** — re-confirmed: `RegisterSchemaAuthorizationIntegrationTests` has no `Relations` reference at all; `ObjectMappingGrpcServiceTests`' references are hand-built descriptors and output assertions.
- **12, 22** (both amended after rounds 1–2) — re-read clause by clause and accurate as written, including row 22's new import-inventory clause: `schema-registrar.test.ts:7-23` imports `ManyToOne` and `OneToMany` only.
- **14** — still correct: TypeScript has no synthesized-FK `clrType` assertion to flip; Python's are `:315` and `:322`; Go's is `assertFkProperty` at `:190`.
- **2, 3, 8, 9, 10, 15, 16, 17, 18, 19, 20, 21** — evidence re-read and unchanged.

### Span check

Span check found no uncovered dependency. The two gaps rounds 1–2 opened (the TypeScript test file's helper/fixture inventory, and its relation-decorator imports) are now both recorded in row 22, and this round's two widened consumer sweeps — non-UUID keys anywhere in the server test tree, and synthesized-FK assertions anywhere in the Python test tree — found nothing that rows 6, 7 and 14 do not already cover as scoped.

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1** — Task 3's tests called `propertiesOf` (nonexistent) and `RegRelKindsArticle` (a Python fixture name), and asserted `RegTagIds` against a fixture with no many-to-many. Resolved: tests now target `describe('_buildRequest — relations')`, index inline, use `RegArticle` for the m2o half and a local fixture for the m2m half.
- **Round 1 §2.2** — Step 4's wiring named `getRelations` as the resolution site for `getGuidFields`. Resolved: it now names `core.ts:216`, inside `describeEntity`, and row 12 pins the enclosing function.
- **Round 2 §2.1** — the new many-to-many fixture used `@ManyToMany`, which the test file does not import. Resolved: Step 1 now instructs adding it to the `'../src/annotations.js'` import list, and row 22 records the file's current relation-decorator imports.

## 5. Recommendation

✅ **Approve as-is**

No failed assumptions, no findings, no forced decisions. This round put its effort into two sweeps earlier rounds had made on weaker evidence — every `IsKey = true` site in the server test tree read with context rather than matched on a single line, and every `clr_type` assertion in the Python test tree checked in both directions against the plan's sweep instruction — plus a dynamic pass over the guard's multi-type loop and the retyped Python samples' serialization path. All came back clean. The plan is ready for `subagent-driven-development`.
