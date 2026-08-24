# Critical Implementation Review: 2026-08-07-relation-fk-only-write-contract-implementation-plan (Round 1)

**Plan:** `/home/ben/repositories/Iverson/docs/plans/2026-08-07-relation-fk-only-write-contract-implementation-plan.md`
**Verified plan-level assumptions section:** present

⚠️ 1 commit since plan-write time (SHA `03d835d`): `985092d add relation fk-only write contract implementation plan` — the plan's own commit. No source drift; cited file:line references re-checked under §1 regardless.

## 0. Coverage enumeration

### Tasks × surfaces

| Task | Surface | Disposition |
|---|---|---|
| T1 | Step prose (which tests to delete/keep) | ok — the ~18 embedded-object cases and the 6 retained FK cases map onto the 34 methods in `RelationValidatorTests.cs`; the retained `PropertyNameEqualsForeignKey` case survives as a no-op assertion, harmless |
| T1 | Code block (rejection) | ok — `StructFieldAccess.GetFieldValue(Struct, string)` exists (used at `RelationValidator.cs:74`); `Value.KindOneofCase.NullValue` is the enum path already used at `:82`; `errors` accumulates as the surrounding method does |
| T1 | Wiring (ctor, deletions, call sites) | ok — `Program.cs:191` needs no change; the 7 remaining `new RelationValidator(_registry)` sites are exactly those in PA3; `Candidates` survives via `EntityKeyAccessor.cs:15,23` |
| T1 | Commands | ok — `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj` and the `--filter` form both valid; csproj exists |
| T1 | Dynamic: deleting restore changes the echoed response | ok — under the new contract clients send no nav property, so there is nothing to echo; `.NET` `EntityCoordinator` deserializes what arrives and leaves nav members null, which the read path re-hydrates |
| T2 | Step prose (four test cases) | ok — the array-FK acceptance case has an existing fixture shape to copy at `SchemaRegistrationOrchestratorTests.cs:222-238` (`TagIds`, `ClrGuid`, `IsArray`) |
| T2 | Code block | ok — `RelationKind` resolves to `Iverson.Api.Schema.RelationKind` (`SchemaDescriptor.cs:67`); `ScalarColumns`/`ColumnDescriptor.Name`, `relation.PropertyName/.Kind/.ForeignKey`, `descriptor.TypeName` all exist; `RpcException`/`Status`/`StatusCode` already used in the file |
| T2 | Wiring (placement) | ok — inserted after `ValidateFieldReference(descriptor, descriptor.TenantColumn, "tenant_field")` at `:66`, which is before `ApplySchemaAsync` at `:68`, so a rejected schema never reaches DDL |
| T2 | Dynamic: does the new check break existing registrations? | ok — both existing relation tests declare their FK property (`SchemaRegistrationOrchestratorTests.cs:207` `ArticleId` via `SimpleType`, `:225` `TagIds`). **Startup reload bypasses the check**: `SchemaRegistry.cs:24` hydrates via `repository.LoadAllAsync()`, not the orchestrator, so persisted schemas with unbacked FKs keep booting |
| T3 | Step prose + wiring (omission list) | → §2.1 |
| T3 | Dynamic: `GraphAssembler` call sites | ok — `:95,209` read the joinKey only; adding `ArticleIds` to `Tag` makes the m2m *read* work for the first time rather than breaking it |
| T3 | Commands | ok — `Iverson.Client.Core.Tests.csproj` exists |
| T4 | Step prose (`isRelationField` non-reuse, constructor pattern) | ok — `SchemaRegistrar.java:342` is `private static`, and `Article.java` sets `tags` by setter while `authorId` is constructor-set, matching the plan's instruction |
| T4 | Code surface (`toValue` Collection branch) | ok — `StructConverter.java:102` is the `toString()` fallback; no `Collection` branch exists to conflict with |
| T4 | Commands | ok — `Iverson.Clients/Java/pom.xml`, artifact `iverson-client-java` |
| T5 | Step prose (kind map, `_infer_fk`, list branch) | ok — `core.py:166` precedent, `:99` `_infer_fk(relation, this_type_name)`, `:330-341` ladder ending in `str(value)` |
| T5 | Wiring (synthesized property fields) | ok — field names `clr_type` / `is_array` / `is_nullable` / `is_key` match `mapping_pb.PropertyDescriptor` usage at `core.py:196-200`. The constant is `mapping_pb.CLR_STRING` (`core.py:36`), qualified — the plan's bare `CLR_STRING` is a field spec, not code |
| T5 | Commands | ok — `pyproject.toml` `[tool.pytest.ini_options] testpaths=["tests"]` |
| T6 | Step prose (kind map from `getRelations`) | → §2.2 |
| T6 | Wiring (synthesized property, exclusion untouched) | ok — leaving `core.ts:238` alone is what keeps the `@IversonArray` guard at `:244-250` off the path; `ClrType.CLR_STRING` is the correct reference (`core.ts:75`) |
| T6 | Commands | ok — `"test": "npm run typecheck && vitest run"` also type-checks `tests/` |
| T7 | Step prose (slice branch, kind exclusions) | ok — `coordinator.go:462-490` has no slice case as PA12 states; `registrar.go:185-195` is the `[]byte` guard precedent the plan cites |
| T7 | Wiring (synthesized property) | ok — field names `Name`/`ClrType`/`IsArray`/`IsNullable`/`IsKey` match `registrar.go:86-108`. Constant is `pb.ClrType_CLR_STRING` (`registrar.go:217`); the plan's bare `CLR_STRING` is a field spec, not code |
| T7 | Dynamic: does emitting FKs change existing Go writes? | ok — this is the intended behavior change; the spec's Consequences section records it and states the historical nulls are not backfilled |
| T7 | Commands | ok — `Iverson.Clients/Go/go.mod`, module `github.com/iverson/clients/go` |

### Cross-task interface contracts

| Contract | Disposition |
|---|---|
| T2's validation ← the FK columns T5/T6/T7 declare | ok — no *test* coupling: server tests supply their own `TypeDescriptor`s, and client tests use substitutes rather than a live server. The coupling is real only against a running stack, which is the spec's "ship together" merge constraint, correctly excluded from task ordering |
| T3's `Tag.ArticleIds` ← nothing; → `GraphAssembler` joinKey read | ok — producer and consumer are both inside T3's own file set |
| T1's deleted `RemoveField` ← any consumer | ok — `grep RemoveField Iverson.Server/` returns only the declaration and `RelationValidator.cs:51`, both inside T1 |
| Each client's serialize-side FK name ← that client's declare-side FK name (**5 rows, one per client**) | → §2.1 for .NET; ok for the other four — Python `_to_pascal_case` + `_infer_fk`, TS `toPascalCase` + `inferFk`, Java `toPascalCase` + `inferForeignKey`, Go raw field names + `inferFK` all produce PascalCase on both sides |

### Rule-like content

| Rule | Over-inclusion | Under-inclusion | Disposition |
|---|---|---|---|
| T1's nav-rejection predicate (`navIsDistinctKey` && present && not `NullValue`) | rejects a legitimate FK | misses a real nav property | ok — Go sends `PropertyName=Author` / `ForeignKey=AuthorId` (distinct, and Go emits only the FK); Python and TS send both equal, so the guard correctly never fires for them |
| T2's kind exemption (`!= OneToMany`) | exempting a kind that needs checking | checking a kind whose FK is remote | ok — `OneToMany`'s FK is a column on the related row in all five registrars |
| T3/T4's "declared type is an entity or collection of entities" test | omitting an FK field | keeping a nav member | → §2.1 (the .NET half fails at the removal step, not the classification step) |
| T7's Go kind exclusion (`KindOneToMany` + struct-typed) | omitting a real FK | emitting `{ThisType}Id` onto the wrong row | ok — `tags.go:120` identifier confirmed; `author.go:8` is the real declaration that makes the exclusion load-bearing |

## 1. Verified-plan-assumptions cross-check

Fresh reads of all seventeen. **All still hold.** Re-checked in detail:

- **PA3** — the eight sites still read `new RelationValidator(_registry)` at the cited lines.
- **PA5** — `SchemaDescriptor.cs:10,15` unchanged.
- **PA7** — `EntityCoordinator.cs:54,70,105,121` and `GraphAssembler.cs:95,209`.
- **PA8** — `SchemaRegistrar.java:342` still `private static`.
- **PA12** — re-read `coordinator.go:462-490`; still no `reflect.Slice` case, and `grep reflect.Slice Iverson.Clients/Go/iverson/*.go` still returns only `registrar.go` hits. The correction to spec A28 stands.
- **PA15** — all five command surfaces re-confirmed.

### Span check — one uncovered dependency

**No assumption covers the payload-key casing each client emits.** PA13 covers the FK *name* helpers and PA14 the kind identifiers, but nothing covers the case convention of the keys that actually land in the `Struct` — and the plan's omission and synthesis steps both compare names against those keys. Four clients emit PascalCase; .NET emits camelCase (`StructConverter.cs:14`, `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`). That gap is what §2.1 falls through. Verified in-round; no forced decision needed.

## 2. Literal-wrongness findings

### §2.1 — .NET's nav-property omission compares PascalCase names against camelCase payload keys, so it removes nothing

**Description.** Task 3 Step 3 says to "remove the named keys after the JSON round-trip", and Step 4 sources those names from `_descriptor.Relations` — i.e. `RelationDescriptor.Property.Name`, which is a `PropertyInfo` name and therefore **PascalCase** (`Author`, `Tags`, `UserArticles`).

But `ToStruct` serializes through `_jsonOpts`, which sets `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` (`StructConverter.cs:12-17`). The keys in the resulting `Struct` are **camelCase** — `author`, `tags`, `userArticles`. An exact-match removal of `Author` against a Struct containing `author` removes nothing, silently.

The consequence is not a partial failure. Every nav property the .NET client sends survives into the payload; Task 1's server-side rule rejects any write whose nav key is present and distinct from the FK; so **every .NET `Post` and `Update` of an entity with a ManyToOne or ManyToMany relation fails with `InvalidArgument`** once both tasks land. The plan's own Task 3 Step 1 test — "does not contain `Author`, `Tags` or `UserArticles`" — would pass while the defect is present, because the payload contains `author`, `tags` and `userArticles`.

**Evidence.**
- `Iverson.Clients/DotNet/Iverson.Client.Core/StructConverter.cs:12-17` — `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`, applied by `ToStruct` at `:20-24`.
- `Iverson.Clients/DotNet/Iverson.Client.Core/RelationDescriptor.cs:8` — `public required PropertyInfo Property`, whose `.Name` is the declared PascalCase name.
- Contrast with the server, which needed `StructFieldAccess.Candidates` (`StructFieldAccess.cs:10`) precisely because .NET payload keys differ from schema names by leading-character case.

**Proposed fix.** Two edits to Task 3, neither changing its shape:

- Step 3: state the casing explicitly — remove each name in **both** its declared and camelCase forms, or match case-insensitively over `Struct.Fields`. The server's `StructFieldAccess.Candidates` is the established precedent for exactly this leading-character variance and is the wording to mirror.
- Step 1: strengthen the test so it can fail — assert the payload contains neither `Author` **nor** `author` (and the same for `tags`/`userArticles`). As written it asserts only the PascalCase form, which is absent either way.

### §2.2 — Task 6's `getRelations(cls)` has no `cls` in scope, and `entityToPayload`'s signature gives it no way to get one

**Description.** Task 6 Step 2 instructs: *"Build a `{field: kind}` map from `getRelations(cls)`."* `getRelations` takes a `Function` (the class). But `entityToPayload` is declared `function entityToPayload(entity: object)` (`core.ts:358`) — it receives an **instance**, and `cls` is not a binding in that scope. TypeScript will not compile the step as written.

Its two call sites are `EntityCoordinator.persist` (`core.ts:428`) and `EntityCoordinator.update` (`core.ts:448`), both of which hold `this._cls`. So the fix is available but is a signature change the plan does not mention, and it touches both call sites — a detail an implementer would otherwise discover only at the first `tsc` run, mid-task.

**Evidence.**
- `Iverson.Clients/TypeScript/src/core.ts:358` — `function entityToPayload(entity: object): Record<string, unknown>`.
- `:428`, `:448` — the two `payload: entityToPayload(entity)` call sites.
- `src/annotations.ts:354` — `export function getRelations(target: Function): RelationMeta[]`.

**Proposed fix.** Amend Task 6 Step 2 to thread the class through: `entityToPayload(entity: object, cls: Function)`, updating both call sites to pass `this._cls`. Add the two call sites to Task 6's `Files:` block so the change is in scope for review.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**

§2.1 is the one that matters — left unfixed it makes every .NET relation write fail after both tasks land, and the plan's own test would not catch it. §2.2 is a contained signature amendment to Task 6. §3 is empty, and §1 reconfirmed all seventeen plan-level assumptions including PA12's correction to the spec's A28.
