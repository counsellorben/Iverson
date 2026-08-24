# Critical Implementation Review: 2026-08-07-relation-fk-only-write-contract-implementation-plan (Round 2)

**Plan:** `/home/ben/repositories/Iverson/docs/plans/2026-08-07-relation-fk-only-write-contract-implementation-plan.md`
**Verified plan-level assumptions section:** present

⚠️ 3 commits since plan-write time (SHA `03d835d`): the plan's own commit, the round-1 review, and the round-1 fixes. No source-code drift; cited file:line references re-checked under §1 regardless.

## 0. Coverage enumeration

### Tasks × surfaces

| Task | Surface | Disposition |
|---|---|---|
| T1 | Step prose (test triage, carve-out comment rewrite) | ok — the retained/deleted split still maps onto the 34 methods in `RelationValidatorTests.cs`; Step 6's instruction that the *code* at `RowFieldAuthorizationEvaluator.cs:92-95` is unchanged and only the comment moves is consistent with the spec's ordering constraint |
| T1 | Code block (rejection) | ok — `GetFieldValue`, `Value.KindOneofCase.NullValue`, `errors.Add` all resolve; the block sits where the strip did, so it runs after the per-kind switch and accumulates rather than short-circuits |
| T1 | Wiring (ctor drop, deletions, 7 call sites) | ok — re-grep'd: `Program.cs:191` resolves the ctor by DI with no explicit arg; the 7 non-`RelationValidatorTests` sites still read `new RelationValidator(_registry)` |
| T1 | Commands | ok — csproj path and `--filter` form both valid |
| T1 | Dynamic: rejection vs. `NullValue` from .NET/Java | ok — .NET serializes every property, so an unset nav member arrives as `NullValue`; the block's `KindCase != NullValue` guard is what prevents every .NET write with an unset relation from being rejected |
| T2 | Step prose (four test cases) | ok — the array-FK case has a fixture to copy at `SchemaRegistrationOrchestratorTests.cs:222-238` |
| T2 | Code block | ok — `RelationKind` resolves to `Iverson.Api.Schema.RelationKind`; `ScalarColumns`/`ColumnDescriptor.Name`, `relation.PropertyName/.Kind/.ForeignKey`, `descriptor.TypeName` all present |
| T2 | Wiring (placement inside the per-type loop) | ok — `SchemaRegistrationOrchestrator.cs:33` is `foreach (var typeDesc in new[] { request.RootType }.Concat(request.Dependents))`, and the `owner_field`/`tenant_field` checks the plan inserts beside are inside it, so **dependents are validated too**, not just the root type |
| T2 | Dynamic: does the check fire on paths that must not fail? | ok — startup hydration bypasses it (`SchemaRegistry.cs:24` `repository.LoadAllAsync()`); both existing orchestrator relation fixtures declare their FK property |
| T3 | Step prose (casing rule, omission sourcing) | ok — the round-1 fix now states the camelCase/PascalCase variance and names `StructFieldAccess.Candidates` as the behaviour to mirror |
| T3 | Wiring (`ToStruct` optional param, 6 call sites) | ok — defaulting keeps `GraphAssembler.cs:95,209` compiling; those two read FK values only |
| T3 | Dynamic: `Tag.ArticleIds` effect on the read path | ok — adding it makes `GraphAssembler`'s m2m joinKey read work for the first time rather than breaking it |
| T4 | Step prose (`isRelationField` non-reuse, constructor pattern) | ok — `SchemaRegistrar.java:342` still `private static`; `Article.java` sets `tags` by setter, so `tagIds` following the setter-only pattern is consistent |
| T4 | Wiring: **does Java's registrar declare `List<UUID> tagIds`?** | ok — `SchemaRegistrar.java:292-315` `detectClrType(Type)` handles `ParameterizedType` where the raw type is a `Collection` with one `Class<?>` argument: `List<UUID>` → element `UUID` → `CLR_GUID` with `isArray=true`. So `TagIds` registers as a `UUID[]` column and satisfies T2's new check. (The same path is why `List<Tag> tags` is already excluded — element `Tag` yields null.) |
| T4 | Code surface (`toValue` Collection branch) | ok — `StructConverter.java:102` is the `toString()` fallback; no `Collection` branch exists to conflict |
| T4 | Commands | ok — `mvn -f Iverson.Clients/Java/pom.xml test`, artifact `iverson-client-java` |
| T5 | Step prose (kind map, `_infer_fk` arguments) | ok — `_infer_fk(relation, this_type_name)` needs a relation dict and a type name; `_entity_to_struct` reads `_iverson_meta` (`core.py:314`), which carries both `["relations"]` and `["type_name"]` (used at `core.py:160`) |
| T5 | Wiring (synthesized property fields) | ok — `clr_type`/`is_array`/`is_nullable`/`is_key` match the `PropertyDescriptor` construction at `core.py:196-200` |
| T5 | Commands | ok — `pyproject.toml` testpaths |
| T6 | Step prose (threaded `cls`, kind map) | ok — the round-1 fix widens the signature and names both call sites `:428`/`:448`, which is where `this._cls` lives |
| T6 | Wiring (exclusion untouched) | ok — leaving `core.ts:238` alone keeps the `@IversonArray` guard at `:244-250` off the path, which is the whole point of synthesizing |
| T6 | Commands | ok — `npm test` runs typecheck then vitest |
| T7 | Step prose (slice branch, kind exclusions, emit expression) | → §2.1 |
| T7 | Code surface (`goValueToProtoValue` slice case) | ok — `coordinator.go:462-490` still has no slice case; `registrar.go:185-195` is the `[]byte` guard precedent the plan cites |
| T7 | Wiring (synthesized property in `registrar.go`) | ok — `meta` **is** in scope there (`registrar.go:108-111`), and the field names `Name`/`ClrType`/`IsArray`/`IsNullable`/`IsKey` match `registrar.go:86-108` |
| T7 | Commands | ok — `go test ./...`, module `github.com/iverson/clients/go` |

### Cross-task interface contracts

| Contract | Disposition |
|---|---|
| T2's validation ← FK columns declared by T4 (Java), T5, T6, T7 | ok — verified per producer this round rather than in aggregate: Java via `detectClrType`'s Collection branch, Python/TS/Go via the synthesized descriptor each task appends. All four produce a column whose name is the relation's `ForeignKey` |
| T2's validation ← .NET's FK columns (T3) | ok — `Guid[] ArticleIds` and `Guid AuthorId` are plain properties; `SchemaBuilder.cs:106-112` registers by name suffix regardless of type |
| T1's nav rejection ← each client's omission (**5 rows, one per client**) | ok — .NET omits by descriptor name with the casing rule (T3); Java omits entity-typed members (T4); Python/TS/Go omit by kind and emit under the inferred name, so no nav key exists to reject |
| T1's deleted symbols ← any consumer | ok — re-grep'd `RemoveField`, `CaptureNavProperties`, `RestoreNavProperties`: all hits are inside T1's own file set |
| Each client's serialize-side FK name ← its declare-side FK name (**5 rows**) | ok — all five now use the same inference helper on both sides; PA18 pins the casing |

### Rule-like content

| Rule | Over-inclusion | Under-inclusion | Disposition |
|---|---|---|---|
| T1's nav-rejection predicate | rejects a legitimate FK | misses a real nav property | ok — Go sends `PropertyName=Author`/`ForeignKey=AuthorId` (distinct, and Go emits only the FK); Python and TS send both equal, so the guard correctly never fires |
| T2's `!= OneToMany` exemption | exempting a kind that needs checking | checking a kind whose FK is remote | ok — all five registrars infer `OneToMany`'s FK as a column on the related row |
| T3's entity-typed omission test | omits an FK field | keeps a nav member | ok — `.NET` `RelationDescriptor.Property.PropertyType` distinguishes `Author`/`List<Tag>` from the plain `Guid`/`Guid[]` FK fields, which carry no relation attribute |
| T7's Go kind exclusion | omits a real FK | emits `{ThisType}Id` onto the wrong row | ok on the rule; → §2.1 on the expression that implements it |
| **Write-key vs read-key symmetry per client** (m2m) | — | a value that persists but never reloads | dropped — see §1 span check. Real asymmetry, but no regression and reads are explicitly out of the spec's scope |

## 1. Verified-plan-assumptions cross-check

Fresh reads of all eighteen. **All still hold.** Re-checked in detail this round:

- **PA3** — the seven non-`RelationValidatorTests` sites still read `new RelationValidator(_registry)`.
- **PA8** — `SchemaRegistrar.java:342` still `private static`.
- **PA11** — `coordinator.go:435` `entityToStruct`, `:462` `goValueToProtoValue`; `registrar.go:64` property loop, `:264` `inferFK`.
- **PA12** — `coordinator.go:462-490` still has no `reflect.Slice` case.
- **PA18** (added after round 1) — re-verified all five: `StructConverter.cs:12-17` camelCase; `core.py:329` `_to_pascal_case`; `core.ts:364` `toPascalCase`; `StructConverter.java:34` `toPascalCase`; `coordinator.go:449` raw `sf.Name`.

### Span check — one uncovered dependency

**No assumption covers what each client's read path does with the m2m key the plan changes.** The plan moves the ManyToMany payload key from the field's own name to the inferred FK name in Go, Python and TypeScript — but all three read entities back by field name: Go `structToEntity` looks up `s.Fields[sf.Name]` (`coordinator.go:505`), TypeScript `payloadToEntity` looks up `toPascalCase(field)` (`core.ts:374-385`), Python `_from_struct` likewise. So a Go `Tag.Articles` written as `ArticleIds` never reloads into `Articles`.

Verified in-round, and deliberately **not** a §2 finding: it is not a regression — Go never wrote the field at all before, and Python's/TypeScript's m2m nav key was stripped server-side, so none of the three round-tripped previously either. The spec states "Reads are unaffected" and scopes the read path out. Recording it here so the gap is visible rather than absent: after this plan, the ids persist and are queryable server-side but do not repopulate the typed field in three clients.

## 2. Literal-wrongness findings

### §2.1 — Task 7 Step 3's emit expression uses two bindings that do not exist at that point in `entityToStruct`

**Description.** Step 3 instructs: *"Emit everything else under `inferFK(fm, meta.TypeName)`."* Neither binding is available where the emit happens.

`entityToStruct` (`coordinator.go:425`) reflects over the value directly — its locals are `v`, `t`, `fields`, and per iteration `sf` and `fv`. There is **no `meta`**; the function never loads an `EntityMeta`. The type name equivalent in scope is `t.Name()`.

`fm` is worse: it is declared *inside* the `if tag != ""` block and used only within the `switch` that follows (`coordinator.go:440-447`). At the emit site below that block, `fm` is out of scope entirely. Implementing Step 3 as written requires restructuring so the parsed tag survives to the emit point — a change the step does not mention, and the one place an implementer is most likely to take the shortcut of emitting under `sf.Name` instead, which silently reverts ManyToMany to the wrong key (`Articles` rather than `ArticleIds`) and then fails Task 2's registration check against the `ArticleIds` column Step 4 declares.

Note the second argument is inert for the kinds Go actually emits — `inferFK` returns `fm.Name` for ManyToOne/OneToOne and `{RelatedType}Ids` for ManyToMany, consulting `thisTypeName` only for `OneToMany`, which Step 3 excludes. It still has to compile.

**Evidence.**
- `Iverson.Clients/Go/iverson/coordinator.go:425-434` — `entityToStruct`'s declarations; no `meta`.
- `:440-447` — `fm, _ := ParseTag(sf.Name, tag)` scoped inside `if tag != ""`.
- `:449-455` — the emit site, where the plan's expression would go.
- Contrast `registrar.go:108-111`, where `meta` **is** in scope, so Task 7 Step 4's identical-looking `inferFK(fm, meta.TypeName)` is correct as written.

**Proposed fix.** Amend Step 3 to name the real bindings and the restructure they require:

> Replace the blanket `continue` at `coordinator.go:440-447`. Parse the tag once per field and keep the result in scope through the emit: hoist `fm` out of the `if tag != ""` block (with a flag or zero-value kind for untagged fields). Skip `KindOneToMany` — `inferFK` returns `{ThisType}Id` for that kind, which names a column on the *related* entity's row, and `author.go:8` is a real declaration that would otherwise emit under `AuthorId`. Skip a relation field whose type is a struct or slice-of-struct as a nav property. Emit a tagged field under `inferFK(fm, t.Name())` — note `meta` is not in scope in this function, unlike `registrar.go`; `t.Name()` is the type name here — and an untagged field under `sf.Name` as today.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1 (.NET PascalCase/camelCase omission mismatch)** — resolved. Task 3 Step 3 now states the casing variance, cites `StructConverter.cs:12-17`, and names `StructFieldAccess.Candidates` as the behaviour to mirror; Step 1's test now asserts both casings, so it can fail.
- **Round 1 §2.2 (`getRelations(cls)` with no `cls` in scope)** — resolved. Task 6 Step 2 widens `entityToPayload` to take the class and names both call sites.
- **Round 1 §1 span check (payload-key casing)** — resolved. PA18 pins the casing convention for all five clients.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**

One finding, contained to Task 7 Step 3's wording. §1 reconfirmed all eighteen assumptions including the three added after round 1, and the span check's uncovered dependency is recorded rather than escalated because it falls outside the spec's stated scope. §3 is empty.
