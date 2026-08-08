# Critical Implementation Review: 2026-08-07-relation-fk-only-write-contract-implementation-plan (Round 3)

**Plan:** `/home/ben/repositories/Iverson/docs/plans/2026-08-07-relation-fk-only-write-contract-implementation-plan.md`
**Verified plan-level assumptions section:** present

⚠️ 1 commit since plan-write time (SHA `25dba96`): `61b36ce`, the plan's own commit. No source-code drift; cited file:line references re-checked under §1 regardless.

## 0. Coverage enumeration

### Tasks × surfaces

| Task | Surface | Disposition |
|---|---|---|
| T1 | Step prose (test triage, carve-out comment) | ok — unchanged this round; retained/deleted split still maps onto the 34 methods in `RelationValidatorTests.cs` |
| T1 | Code block (rejection) | ok — `GetFieldValue`, `Value.KindOneofCase.NullValue`, `errors.Add` all resolve |
| T1 | Wiring (ctor drop, 7 call sites, deletions) | ok — re-grep'd; the 7 non-`RelationValidatorTests` sites still pass `_registry` |
| T1 | Commands | ok — csproj and `--filter` valid |
| T2 | Step prose + code block | ok — `RelationKind`, `ScalarColumns`, `RelationDescriptor` members all resolve; array-FK fixture exists at `SchemaRegistrationOrchestratorTests.cs:222-238` |
| T2 | Wiring (per-type loop placement) | ok — `SchemaRegistrationOrchestrator.cs:33` `foreach (… RootType … Concat(Dependents))` encloses the checks the plan inserts beside |
| T3 | Step prose (casing rule, omission sourcing) | ok — round-1 fix intact; `StructConverter.cs:12-17` camelCase cited correctly |
| T3 | Wiring (`ToStruct` param, 6 call sites) | ok — defaulting keeps `GraphAssembler.cs:95,209` compiling |
| T4 | Step prose (annotation check, constructor pattern) | ok — `SchemaRegistrar.java:342` still `private static`; `detectClrType`'s `ParameterizedType`/`Collection` branch (`:292-315`) declares `List<UUID> tagIds` as `UUID[]` |
| T4 | Code surface (`toValue` Collection branch) | ok — `StructConverter.java:102` still the `toString()` fallback |
| T5 | Step prose — write side (Steps 2–4) | ok — `core.py:330-341` ladder, `:186-188` exclusion, `:99` `_infer_fk` all as cited |
| T5 | **Step prose — read side (Steps 5–6, new)** | ok — `_from_struct` at `core.py:505` is an `EntityCoordinator` method; `self._cls` at `:382`; the ladder's terminal `else: setattr(obj, field_name, None)` at `:526-527` is exactly what Step 6 targets |
| T5 | Commands | ok — `pyproject.toml` testpaths; `tests/test_entity_coordinator.py` exists as the named home |
| T6 | Step prose — write side (Steps 2–3) | ok — round-1 signature threading intact; `core.ts:238` exclusion left alone, keeping the `@IversonArray` guard off the path |
| T6 | **Step prose — read side (Step 4, new)** | ok — `payloadToEntity<T>(cls: new () => T, data)` at `core.ts:374` genuinely already takes the class; `instance[field] = data[key]` at `:381` is untyped, so arrays pass through as the step claims |
| T6 | Commands | ok — `npm test` = typecheck + vitest |
| T7 | Step prose — Step 3 (round-2 fix) | ok — the hoist instruction and the `meta`-not-in-scope / `t.Name()` correction match `coordinator.go:425-455`; the contrast with `registrar.go:108-111` is stated and correct |
| T7 | Step prose — Step 2 (write slice branch) | ok — `coordinator.go:462-490` still has no slice case; `registrar.go:185-195` is the `[]byte` guard precedent |
| T7 | **Step prose — Step 5 (read key, new)** | ok on the mechanism — `structToEntity` at `:494` parses no tags today and `t` is in scope at `:496`; but see §2.1 for what the step does **not** exclude |
| T7 | **Step prose — Step 6 (read list case, new)** | → §2.1 |
| T7 | Wiring (synthesized property, `meta` in scope) | ok — `registrar.go:108-111`; field names match `:86-108` |
| T7 | Test-file placement | ok — `iverson/coordinator_test.go` is `package iverson` with 14 tests and a header explaining that external test packages cannot reach unexported symbols. Local test structs (`coordinatorArticle`, `:26`) are the existing pattern, so the round-trip test has a model to follow |
| T7 | Commands | ok — `go test ./...`, module `github.com/iverson/clients/go` |

### Cross-task interface contracts

| Contract | Disposition |
|---|---|
| T2's validation ← FK columns from T4/T5/T6/T7 | ok — re-verified per producer: Java via `detectClrType`'s Collection branch, the other three via the synthesized descriptor each appends |
| T1's nav rejection ← each client's omission (5 rows) | ok — no client emits a nav key after its task; Go/Python/TS emit only the inferred FK, .NET/Java omit entity-typed members |
| **Each client's write key ← its own read key (5 rows, one per client)** | ok for Python/TS/Go m2m after Steps 5/4/5 respectively; ok for .NET and Java by naming (PA25). This is the contract the round-2 sweep dropped and the spec now mandates |
| `payloadToEntity` **call sites (4 rows)** — `:499` get, `:519` getMany, `:598` search, `:608` searchSimilar | ok — all four take `cls` as a parameter and pass it through, so T6 Step 4's redirect applies uniformly. Search rows return the row's columns, which include the FK, so the redirect is correct there too |
| T7 Step 6's ListValue case ← keys that can carry a list on read | → §2.1 — the producer set is larger than the FK column |

### Rule-like content

| Rule | Over-inclusion | Under-inclusion | Disposition |
|---|---|---|---|
| T1's nav-rejection predicate | rejects a legitimate FK | misses a real nav property | ok — Go sends distinct `Author`/`AuthorId` and emits only the FK; Python/TS send both equal |
| T2's `!= OneToMany` exemption | exempts a kind needing the check | checks a kind whose FK is remote | ok — all five registrars infer `OneToMany`'s FK onto the related row |
| **T5/T6/T7 read redirect: "`many_to_many` only"** | redirects a kind that should not be | **leaves a kind unredirected that should be** | ok as scoped — the spec itself limits the redirect to ManyToMany; the m2o/o2o convention dependency is recorded under §1's span check rather than treated as a plan defect |
| **T7 Step 6's ListValue → slice conversion** | converts a list that is not an FK list | — | → §2.1 (over-inclusion direction) |
| T3's entity-typed omission test | omits an FK field | keeps a nav member | ok — `.NET` `RelationDescriptor.Property.PropertyType` distinguishes them; FK fields carry no attribute |
| Python/TS "`{field: kind}` map" phrasing across T5 Step 3/5 and T6 Step 2/4 | — | map carries only the kind, but `_infer_fk`/`inferFk` also need `related_type` | dropped — following it literally leaves the implementer one field short at a call they are already writing; resolved in seconds, produces no wrong outcome |
| PA26 listing a Java round-trip home | — | — | dropped — Java needs no read change per PA25, so the row is over-broad rather than wrong; no execution consequence |

## 1. Verified-plan-assumptions cross-check

Fresh reads of all twenty-six. **All still hold.** Checked in detail this round (the eight added since round 2):

- **PA19** — `core.py:505` `_from_struct` is an `EntityCoordinator` method; `self._cls` assigned at `:382`; `_infer_fk` module-level at `:99`.
- **PA20** — `core.py:517-527`: `string_value`/`number_value`/`bool_value`, then `else: setattr(obj, field_name, None)`.
- **PA21** — `core.ts:374` signature confirmed, and all four call sites (`:499`, `:519`, `:598`, `:608`) pass a class. The asymmetry with the write side is real.
- **PA22** — `coordinator.go:494-516` uses only `sf.Name`/`sf.Type`; zero `ParseTag` occurrences; `t` in scope at `:496`.
- **PA23** — `coordinator.go:519` signature `(pbVal, target reflect.Value, targetType reflect.Type)`; handles String/Number/Bool at `:521-551`.
- **PA24** — `iverson/coordinator_test.go` is `package iverson`, 14 tests, with a header documenting precisely why the external package cannot host it.
- **PA25** — `StructConverter.java:49-61` builds a `toPascalCase(field).toLowerCase()` lookup and matches incoming keys case-insensitively.
- **PA26** — the four non-Java homes all exist as named.

### Span check — one uncovered dependency

**No assumption covers that a ManyToOne/OneToOne member's own name equals its inferred FK.** The plan writes every non-`OneToMany` kind under the inferred FK name (T5 Step 3, T6 Step 2, T7 Step 3) but redirects only ManyToMany on read (T5 Step 5, T6 Step 4, T7 Step 5), justified by "the inferred name already equals the member's PascalCase name". That is a naming *convention*, not a verified property: a Python field `writer_id` declared `many_to_one("Author")` infers `AuthorId`, so it would be written under `AuthorId` and read under `WriterId`.

Verified in-round for every entity that exists: Python `author_id`/`Author`, TypeScript `authorId`/`Author`, Go `AuthorId`/`Author`, .NET `BenchmarkAuthorId`/`BenchmarkAuthor` — all coincide. Recorded rather than escalated because the spec states this limit itself ("ManyToOne and OneToOne need no change: the field name already equals the inferred FK"), so narrowing it is the plan implementing the design faithfully, not a plan defect. If the convention should be enforced rather than assumed, that is a spec-level question.

## 2. Literal-wrongness findings

### §2.1 — Task 7 Step 6's ListValue case fires on server-injected nav lists, filling `OneToMany` string slices with empty strings

**Description.** Step 5 redirects only ManyToMany on read; every other field, including a `OneToMany`-tagged one, keeps its `s.Fields[sf.Name]` lookup. Step 6 then adds a `*structpb.Value_ListValue` case to `protoValueToGoValue` that builds a slice for any list-valued key.

Those two combine badly on the depth-resolving read path. `EntityRelationResolver.ResolveOneToManyAsync` injects the hydrated children as a list of **structs** under `relation.PropertyName` (`EntityRelationResolver.cs:176`), and Go's `relationPropertyName` returns the field name unchanged for `one_to_many` (`registrar.go:281-286`) — so for the sample `Author`, the injected key is exactly `Articles`, the field's own name.

Today that list reaches `protoValueToGoValue`, matches no case, and the field stays `nil`. After Step 6 it matches the new case: `reflect.MakeSlice([]string, n, n)` succeeds, each element is a `StructValue` that the recursive call cannot convert, and the field comes back as `["", ""]` — one empty string per child. A caller reading an `Author` at depth gets a populated-looking slice of empty ids where they previously got `nil`.

The write side already excludes `KindOneToMany` deliberately (Step 3); the read side does not, and Step 6 is what makes that omission observable.

**Evidence.**
- `Iverson.Clients/Go/iverson/coordinator.go:494-516` — `structToEntity` looks up every field under `sf.Name`; Step 5 changes this only for ManyToMany.
- `:519-553` — `protoValueToGoValue`'s switch; the new case is the first that would match a `ListValue`.
- `Iverson.Clients/Go/iverson/registrar.go:281-286` — `relationPropertyName` returns `fm.Name` for kinds other than ManyToOne/OneToOne.
- `Iverson.Server/Iverson.Api/Grpc/EntityRelationResolver.cs:176` — `entityStruct.Fields[relation.PropertyName] = Value.ForList(items)` where each item is `Value.ForStruct(relatedStruct)`.
- `Iverson.Clients/Go/sample/models/author.go:8` — `Articles []string` tagged `one_to_many:Article`, a real declaration that hits this path.

**Proposed fix.** Mirror the write-side exclusion on the read side, in Step 5, so the injected list never reaches the value ladder:

> **Step 5: Make the read path symmetric.**
> `structToEntity` (`coordinator.go:494`) looks every field up under `s.Fields[sf.Name]` and **parses no struct tags at all** today — so this step adds tag parsing as well as changing the lookup. Skip `KindOneToMany` fields entirely, mirroring Step 3's write-side exclusion: the server injects hydrated child *structs* under that field's own name on depth-resolved reads (`EntityRelationResolver.cs:176`), and without the skip Step 6's new list case would fill a `[]string` with one empty string per child. For a field whose kind is `KindManyToMany`, look it up under `inferFK(fm, t.Name())`; everything else keeps `sf.Name`. `t` is already in scope (`:496`).

Guarding `targetType.Kind() == reflect.Slice` inside Step 6 is worth doing regardless, but it does not fix this case — the target genuinely is a slice; the elements are the wrong shape.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1 (.NET PascalCase/camelCase omission mismatch)** — resolved; Task 3 Step 3 states the casing rule and Step 1's test asserts both casings.
- **Round 1 §2.2 (`getRelations(cls)` with no `cls` in scope)** — resolved; Task 6 Step 2 widens `entityToPayload` and names both call sites.
- **Round 1 §1 span check (payload-key casing)** — resolved; PA18.
- **Round 2 §2.1 (`meta`/`fm` out of scope in `entityToStruct`)** — resolved; Task 7 Step 3 now instructs the hoist, names `t.Name()`, and contrasts with `registrar.go` where `meta` genuinely is in scope.
- **Round 2 §1 span check (client read paths key by field name)** — resolved at the spec level and implemented here; the dropped row from round 2 is now Tasks 5/6/7's read steps, and PA24 additionally corrected the Go test path the earlier plan got wrong.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**

One finding, contained to Task 7's Step 5 wording. §1 reconfirmed all twenty-six assumptions including the eight added for the read-symmetry work, and the span check's uncovered dependency is recorded rather than escalated because the spec states that limit itself. §3 is empty.
