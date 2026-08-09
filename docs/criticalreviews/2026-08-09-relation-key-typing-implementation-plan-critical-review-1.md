# Critical Implementation Review: 2026-08-09-relation-key-typing-implementation-plan (Round 1)

**Plan:** /home/ben/repositories/Iverson-fk-only/docs/plans/2026-08-09-relation-key-typing-implementation-plan.md
**Verified plan-level assumptions section:** present

⚠️ 2 commits since plan-write time (SHA `a199df2`); cited file:line references re-checked under §1. Both are documentation commits (`179816d` the round-2 design review, `438a4fe` the plan itself) — no code drift.

## 0. Coverage enumeration

### Task 1 — Server registration guard

| Surface | Disposition |
|---|---|
| Step 1 prose — why the two server fixtures break | ok — `SimpleType` (`SchemaRegistrationOrchestratorTests.cs:36-44`) types every `extraScalars` entry `ClrString`; `:205` and `:441` route their FK through it. The stated mechanism is exact |
| Step 1 code — two `PropertyDescriptor` additions | ok — `PropertyDescriptor { Name, ClrType }` matches the shape used at `:226-228`; adding the property separately bypasses `extraScalars` as claimed |
| Step 2 code — four test methods | ok — every identifier resolves: `TypeDescriptor`, `PropertyDescriptor`, `SchemaRequest`, `Client.Contracts.RelationKind.{ManyToOne,ManyToMany,OneToMany}`, `_sut`, `SimpleType`, `RpcException`, `StatusCode.InvalidArgument`, `ex.Which.Status.Detail`. The `Detail` assertion shape matches the existing convention at `:401` |
| Step 2 — do the rejection tests actually reach the guard? | ok — each fixture passes the checks that run *earlier*: `ValidateIdentifier` (`:35-37`), `ValidateEnrichmentTargets` (`:52`), `owner_field` (`:54-56`), and mandatory `tenant_field` (`:61-66`) all pass, since every fixture declares `TenantField = "TenantId"` and a matching `ClrString` `TenantId` column |
| Step 2 — the m2m rejection test's fixture | ok — `TagIds` is declared (membership check at `:73` passes), scalar `ClrGuid` → `ClrTypeToSql(ClrGuid, false)` = `"UUID"` ≠ `"UUID[]"`, so the new arm is what fires |
| Step 4 code — key guard | ok — `descriptor.KeyColumn.SqlType` is real (`SchemaDescriptor.cs:51` `ColumnDescriptor(string Name, string SqlType, bool IsNullable)`), and `SchemaBuilder.cs:236` maps `ClrGuid` → the exact string `"UUID"`, `:237` `ClrString` → `"TEXT"`. String comparison is sound |
| Step 4 code — restructured relation loop | ok — `FirstOrDefault` on `IReadOnlyList<ColumnDescriptor>`; `ColumnDescriptor` is a record (reference type), so `column is null` is valid. `Schema.RelationKind.ManyToMany` exists (`SchemaDescriptor.cs:67`). `ClrTypeToSql` returns `mapping.SqlType` (`SchemaBuilder.cs:278-284`), so `"UUID[]"` (`:252`) is the literal a conforming m2m column carries |
| Step 4 wiring — membership-before-type ordering | ok — the plan states it and the restructured loop enforces it; `:386`'s `Detail.Should().Contain("Owner").And.Contain("OwnerId")` still gets the membership message |
| Step 4 wiring — guard placement | ok — `SchemaRegistrationOrchestrator.cs:50` builds the descriptor; `:84` applies the DDL. Both new checks land between, so a rejected schema never reaches `ApplySchemaAsync` |
| Commands (Steps 3, 5) | ok — `Iverson.Api.Tests.csproj` exists at the cited path; `--filter FullyQualifiedName~SchemaRegistrationOrchestratorTests` is valid xUnit filter syntax |

### Task 2 — Go

| Surface | Disposition |
|---|---|
| Step 1 code — `assertFkProperty` flip | ok — `coordinator_test.go:190-192` is the sole `CLR_STRING` expectation in the Go tests |
| Step 1 code — `TestGuidTagYieldsClrGuid` | ok — `propsByName` (`coordinator_test.go:170`) takes `interface{}` and calls `NewSchemaRegistrar(nil, e)` + `buildRequest`. The fixture declares a key and an `iverson_tenant` field, satisfying `tags.go:329`'s tenant requirement; a function-local struct type reflects normally |
| Step 3 code — tag const, `FieldMeta.IsGuid`, parse line | ok — `tags.go:100` `KeyTagKey`, `:132` the `IsKey` field, `:235` `fm.IsKey = sf.Tag.Get(KeyTagKey) == "true"` are all where the plan says |
| Step 4 code — override after `goTypeToClr` | ok — `registrar.go:69` is the only call site; overriding `clrType` while leaving `isArray` untouched preserves array rendering, as the plan states |
| Step 4 — no duplicate FK property | ok — **checked the failure mode the plan doesn't mention**: `tags.go:315` routes any field with a non-empty `RelationKind` into `meta.Relations`, and only `:324`'s else-branch appends to `meta.Fields`. So the FK-bearing relation field is never emitted twice, and `:128`'s synthesized property is the only one carrying that name |
| Step 5 — sample tags | ok — `sample/models/{tag,author,article}.go:5,5,7` carry `Id string \`iverson_key:"true"\``; the relation field on `article.go` lives in `meta.Relations` and needs no tag |
| Commands | ok — `go.mod` at `Iverson.Clients/Go/go.mod`; `go test ./...` and `go vet ./...` are standard |

### Task 3 — TypeScript

| Surface | Disposition |
|---|---|
| Step 1 code — test identifiers | → §2.1 |
| Step 1 code — remaining identifiers after the finding | `IversonEntity`, `IversonKey`, `IversonTenant`, `ClrType.CLR_GUID`, `ClrType.CLR_STRING` all exist (`annotations.ts:58,70,273`); `@IversonGuid` is introduced by Step 3. Only the helper and fixture names are phantom |
| Step 3 code — decorator + `getGuidFields` | ok — mirrors `annotations.ts:240-261` exactly (module-scoped `Symbol`, `Reflect.getMetadata`/`defineMetadata` on `target.constructor`, a paired getter). `reflect-metadata` is already a dependency of that file |
| Step 4 code — the `clrType` expression | ok — `core.ts:280` is the only `jsTypeToClr` call site; `arrayElement ?? guid ?? jsTypeToClr` preserves `@IversonArray` precedence, and a scalar `string` key never trips `:271-278`'s array-without-decorator throw |
| Step 4 wiring — where to resolve `getGuidFields` | → §2.2 |
| Step 4 code — synthesized FK retype at `:318` | ok — `core.ts:318` `clrType: ClrType.CLR_STRING`, `:319` `isArray: rel.kind === 'many_to_many'`; leaving `:319` alone is correct |
| Step 5 — sample decoration | ok — `sample/models/{Tag,Article,Author}.ts:7,15,7` declare `id: string = ''`; each already imports from the annotations module |
| Commands | ok — `package.json` `scripts.test` = `"npm run typecheck && vitest run"`, so `tsc -p tsconfig.test.json` gates the run — which is why §2.1 is fatal rather than cosmetic |

### Task 4 — Python

| Surface | Disposition |
|---|---|
| Step 1 prose — flip `:315`, sweep for others | ok — the sweep sentence covers `:322`'s `mtm_prop.clr_type == mapping_pb.CLR_STRING`, which the preceding sentence's "add the same assertion" would otherwise leave contradicting the new one. Dropped as a finding: the sweep instruction is in the same step and resolves it |
| Step 3 code — retype at `:252-259` | ok — `core.py:254` `clr_type=mapping_pb.CLR_STRING`, `:257` `is_array=(rel["kind"] == "many_to_many")`; the plan changes the first and preserves the second |
| Step 4 code — `id: uuid.UUID` + `import uuid` | ok — `sample/models.py:2` carries `from __future__ import annotations`, so annotations are strings; the registrar resolves them via `get_type_hints` (`core.py:172`), which needs `uuid` bound in the module namespace — exactly what the added import provides. `core.py:90` then derives `"UUID"` via `__name__` → `CLR_GUID` (`:38`) |
| Step 4 prose — leave `author_id: str` alone | ok — `core.py` builds a `relation_fields` set from `meta["relations"]` and skips those field names in the property loop, so the relation member's own annotation is never read for a column type; the synthesized property (Step 3) is the only one |
| Commands | ok — `pyproject.toml:25-26` `[tool.pytest.ini_options] testpaths = ["tests"]` |

### Cross-task contracts and rule-like content

| Row | Disposition |
|---|---|
| Cross-task contracts | ok — there are none. The four tasks touch disjoint file sets across four language trees, no task consumes an artifact another produces, and the client tasks mock the transport so none depends on Task 1's guard existing. No persistence-boundary handoff exists to flag |
| Rule: kind → required SQL type (over-inclusion) | ok — could it reject a conforming schema? `ManyToOne`/`OneToOne` FKs are scalar in all five clients (Go/Py/TS set `isArray` false for those kinds; .NET `Guid AuthorId`; Java `UUID authorId`) → `"UUID"`. m2m FKs are arrays in all five → `"UUID[]"`. No conforming shape is rejected |
| Rule: kind → required SQL type (under-inclusion) | ok — could it admit a broken FK? The only escape is `OneToMany`, which is exempt by design and whose FK is checked when the related type registers its reciprocal. `TEXT`, `TEXT[]`, and scalar-`UUID`-where-`UUID[]`-is-required all fail the exact-string comparison |
| Rule: key column must be `UUID` (both directions) | ok — `SchemaBuilder.cs:163` passes `isArray: false`, so `"UUID"` is the only conforming value and the check is falsifiable (the SQL type is derived from the declared `ClrType`, not hardcoded) |

## 1. Verified-plan-assumptions cross-check

All 22 reconfirmed under fresh reads. Spot-notes where the re-read added detail:

- **2** — `SchemaDescriptor.cs:51` `public sealed record ColumnDescriptor(string Name, string SqlType, bool IsNullable)`. Confirmed; also confirms `column is null` in Step 4 is valid.
- **3** — `SchemaDescriptor.cs:67` `public enum RelationKind { OneToOne, OneToMany, ManyToOne, ManyToMany }`.
- **6** — re-swept all six relations in `SchemaRegistrationOrchestratorTests.cs`; the count of two breakages (`:205`, `:441`) is exact.
- **7** — the three orchestrator-constructing test files re-confirmed; `ObjectMappingGrpcServiceTests.cs:428`'s only `RelationDescriptor` is an `Iverson.Api.Schema.RelationDescriptor` on a hand-built descriptor, not a registered `Client.Contracts` one.
- **9** — `registrar.go:69` sole `goTypeToClr` call site; `:212`/`:218` the only `goScalarToClr` calls, both internal.
- **12** — `core.ts:280` confirmed as the sole `jsTypeToClr` call site. See the span check below for what this assumption does *not* cover.
- **14** — still correct: TypeScript has no synthesized-FK `clrType` assertion to flip.
- **22** — confirmed as a claim about *style*. See the span check.

### Span check — two uncovered dependencies, both load-bearing, both became findings

**1. No assumption covers the *names* of the TypeScript test helper and relation fixture the plan's code block calls.** Assumption 22 verifies the TS test *style* (vitest, decorators enabled) and assumption 14 verifies that no FK-type assertion exists to flip — but nothing verifies that `propertiesOf` and `RegRelKindsArticle`, which Task 3 Step 1's code calls by name, resolve in that file. They do not. → §2.1.

**2. No assumption covers which function encloses `core.ts`'s property loop.** Assumption 12 pins the derivation *line* (`:280`) but not its scope, and Task 3 Step 4's wiring prose names a different function as the place to resolve `getGuidFields`. → §2.2.

## 2. Literal-wrongness findings

### §2.1 — Task 3's TypeScript tests call a helper and a fixture that do not exist

**Description.** Task 3 Step 1's two test bodies reference three identifiers that are not defined in `tests/schema-registrar.test.ts`:

- `propertiesOf(...)` — no such function anywhere in the repo. The file's analogous helper is named `propsOf`, and it is **not** a module-level helper: it is declared twice as a nested function, once inside `describe('_buildRequest — metadata and descriptions')` (`:335`) and once inside `describe('_buildRequest — ingest enrichment targets')` (`:410`). Neither describe is where relation or key-typing tests belong, so even the corrected name is out of scope at the plan's intended insertion point.
- `RegRelKindsArticle` — this is a **Python** fixture name (`Iverson.Clients/Python/tests/test_schema_registrar.py`). The TypeScript file has no `RegRelKinds*` class. Its relation fixture is `RegArticle` (`:48`).
- `RegTagIds` — the second test asserts a many-to-many foreign key, but `RegArticle` declares only `@ManyToOne(() => RegAuthor) regAuthorId` (`:70-71`). **There is no many-to-many fixture in the file**, so no existing class produces a `RegTagIds` property to assert against.

`npm test` runs `tsc -p tsconfig.test.json` before vitest (`package.json` `scripts.test`), so all three are hard compile errors: Task 3 cannot reach Step 2's "run and confirm failure", let alone Step 6. The task is unexecutable as written.

**Evidence.**
- `Iverson.Clients/TypeScript/tests/schema-registrar.test.ts:335`, `:410` — `function propsOf(cls: Function)`, both nested inside unrelated `describe` blocks. No `propertiesOf` exists.
- `Iverson.Clients/TypeScript/tests/schema-registrar.test.ts:48-71` — `RegArticle`'s full field list; its only relation is `@ManyToOne(() => RegAuthor) regAuthorId: string = ''`.
- `Iverson.Clients/TypeScript/tests/schema-registrar.test.ts:244` — `describe('_buildRequest — relations')`, whose tests build properties inline: `const req = registrar._buildRequest(RegArticle)` followed by `Object.fromEntries(req.rootType!.properties.map(p => [p.name, p]))` (the pattern at `:164`, `:206`, `:508`).
- `Iverson.Clients/TypeScript/package.json` — `"test": "npm run typecheck && vitest run"`.

**Proposed fix.** Place both tests inside `describe('_buildRequest — relations')` (`:244`) and follow that block's own idiom rather than a helper: build the request with `registrar._buildRequest(<fixture>)` and index with `Object.fromEntries(req.rootType!.properties.map(p => [p.name, p]))`. Use `RegArticle` for the many-to-one assertion (`RegAuthorId` → `CLR_GUID`, `isArray` false). For the many-to-many half, declare a local fixture inside the test — the block already does this for `NavArticle` at `:265-275` — carrying a `@ManyToMany` relation, and assert its `{RelatedType}Ids` property is `CLR_GUID` with `isArray` true. The `@IversonGuid` test needs no fixture from the file; keep it local as written, but index properties the same way.

### §2.2 — Task 3's wiring prose names the wrong enclosing function

**Description.** Step 4 instructs the implementer to import `getGuidFields` "alongside the existing `getArrayFields` import and resolve it beside `arrayFields` in `getRelations`' sibling setup". `arrayFields` is not resolved in `getRelations`. It is resolved at `core.ts:216`, inside `describeEntity` (`core.ts:200`) — the same function that holds the property loop (`:265-291`) and the `clrType` expression at `:280` the step then edits. An implementer following the prose literally adds the resolution to a function that neither declares `cls` in that shape nor contains the loop, and `guidFields` is then undefined at `:280` — a compile error under Step 6's typecheck.

**Evidence.**
- `Iverson.Clients/TypeScript/src/core.ts:200` — `export function describeEntity(cls: Function): TypeDescriptor {`
- `Iverson.Clients/TypeScript/src/core.ts:216` — `const arrayFields = getArrayFields(cls);`
- `Iverson.Clients/TypeScript/src/core.ts:266`, `:270`, `:280` — `relationFields`, `arrayElement`, and the `clrType` derivation, all inside `describeEntity`.

**Proposed fix.** Replace the location phrase with: *"add `const guidFields = getGuidFields(cls);` beside `const arrayFields = getArrayFields(cls);` at `core.ts:216`, inside `describeEntity`."*

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**

Both findings are confined to Task 3 and are mechanical — a corrected insertion point plus correct fixture and helper names. Tasks 1, 2 and 4 are clean: every identifier, command, and ordering claim resolved against the codebase, and the two failure modes most likely to have gone unnoticed both checked out (Go does not double-emit the FK-bearing relation field, because `tags.go:315` routes relation fields away from `meta.Fields`; and the Python sample's `from __future__ import annotations` is harmless because the registrar resolves through `get_type_hints`). The guard's kind→SQL-type rule was checked in both directions against all five clients and admits nor rejects nothing it shouldn't.
