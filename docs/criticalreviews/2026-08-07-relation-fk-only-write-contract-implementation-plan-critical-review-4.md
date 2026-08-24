# Critical Implementation Review — Foreign-Key-Only Relation Write Contract (round 4)

**Plan:** docs/plans/2026-08-07-relation-fk-only-write-contract-implementation-plan.md
**Source spec:** `docs/specs/2026-08-07-relation-fk-only-write-contract-design.md` (`24ed54c`)
**Date:** 2026-08-08

Drift: `git log --oneline 24ed54c..HEAD` returns one commit — `dd33579`, the plan update this review covers. No codebase drift since the spec was written.

Round 4 reviews the plan as a whole, with the naming-enforcement delta (Tasks 5–7, PA27–PA29) as new surface.

## 0. Coverage enumeration

### Tasks × surfaces

| Row | Disposition |
|---|---|
| T1 step prose (delete list, rejection rule) | ok — every deleted symbol matches `RelationValidator.cs`; the `navIsDistinctKey` + NullValue rule matches the spec's clause 1 |
| T1 code block | ok — `StructFieldAccess.GetFieldValue`, `Value.KindOneofCase.NullValue` both real |
| T1 commands / commit | ok — `dotnet test … Iverson.Api.Tests.csproj`; seven paths in `git add` match the Files list |
| T1 wiring (ctor arity) | ok — `Program.cs:191` takes no argument; 8 test-side `new RelationValidator(` sites confirmed by grep |
| T2 step prose + code block | ok — `SchemaDescriptor.cs:10` `ScalarColumns`, `:14` `Relations`; membership-only check, `OneToMany` exempt |
| T2 commands / commit | ok |
| T3 step prose (casing rule) | ok — round 1's finding is carried; both casings asserted |
| T3 wiring (four write call sites, two read call sites keep compiling) | ok — 6 `ToStruct` occurrences across `EntityCoordinator`/`GraphAssembler` |
| T4 step prose / `Collection` branch / annotation check | ok — `isRelationField` still `private static` (`SchemaRegistrar.java:342`), and the step says to write a local check |
| T5 steps 1–4, 6–9 | ok — unchanged from round 3, all cited lines re-read |
| **T5 Step 5 (new naming check)** | → §2.1 |
| **T5 Step 1's new test case** | → §2.1 (same root cause: the fixture it will run against violates the rule) |
| T5 commit (`git add … tests/`) | ok — the whole `tests/` directory is staged, so a fixture edit is covered |
| T6 steps 1–3, 5–7 | ok |
| **T6 Step 4 (new naming check)** | → §2.1 |
| T6 commit (`git add … tests/core.test.ts`) | → §2.1 — path list is too narrow once `tests/schema-registrar.test.ts` must change |
| T7 steps 1–3, 5–8 | ok |
| **T7 Step 4's added naming check** | ok — `buildSchema` returns `(…, error)` (`registrar.go:71`); relation loop at `:108-118` has `fm.Name`/`fm.RelatedType`; every Go m2o declaration in the repo is `AuthorId`/`Author` (`sample/models/article.go:13`, `iverson_test/registrar_test.go:81`, `tags_test.go:261,472`) — no fixture breaks |
| T7's stated non-redundancy rationale | ok — `inferFK` returns `fm.Name` for both kinds (`registrar.go:264-269`); the column-name framing is correct |

### Cross-task interface contracts

| Contract | Disposition |
|---|---|
| T2's server check ← the FK columns T5/T6/T7 synthesize | ok — all three synthesize under the same `inferFK`/`_infer_fk`/`inferFk` name the relation descriptor's `ForeignKey` carries |
| T5/T6/T7 write key → same task's read key | ok — ManyToMany redirected on both sides; m2o/o2o now *made* equal by the new check rather than assumed |
| T5/T6/T7 naming check → the read redirect's ManyToMany-only scope | ok — the dependency the check exists to close; stated in each task |
| New check (registration) ← existing fixtures each suite registers | → §2.1 — the consuming operation is `SchemaRegistrar._build_request` / `_buildRequest`, whose input is a test fixture class, not the sample model PA27 enumerates |
| T1's validator ← T2's registration guarantee | ok — independent; T1 does not assume the column exists |

### Rule-like content

| Rule | Over-inclusion | Under-inclusion | Disposition |
|---|---|---|---|
| **`PascalCase(member) == {RelatedType}Id`** | fires on a legitimately-named member | misses a divergent one | → §2.1 on over-inclusion: it fires on two *existing* fixtures |
| Kind scope of the check (m2o/o2o only) | would fire on m2m | would miss m2o | ok — m2m is redirected on read instead; `one_to_many` declares nothing |
| `_to_pascal_case` semantics under the rule | — | `part.capitalize()` lowercases the tail | ok — `reg_author_id` → `RegAuthorId`, exactly what `_infer_fk` yields |
| T7 Step 6's ListValue case | fires on a nav list | — | ok — round 3's fix (Step 5's `KindOneToMany` skip) is present |
| T3's entity-typed omission test | omits an FK field | keeps a nav member | ok |

## 1. Verified-plan-assumptions cross-check

Fresh reads of all twenty-nine. **All still hold**, including the three added this round:

- **PA2 / PA3** — `Program.cs:191` `AddSingleton<IRelationValidator, RelationValidator>()`; `grep -rn "new RelationValidator("` over the test tree returns 8.
- **PA4** — `grep RemoveField Iverson.Server/Iverson.Api.Tests/` → 0.
- **PA5** — `Schema/SchemaDescriptor.cs:10` `ScalarColumns`, `:14` `Relations`.
- **PA7** — 6 `ToStruct` occurrences across `EntityCoordinator.cs` and `GraphAssembler.cs`.
- **PA8** — `SchemaRegistrar.java:342` still `private static`.
- **PA12** — `grep reflect.Slice Iverson.Clients/Go/iverson/*.go` still hits `registrar.go` only.
- **PA24** — `iverson/coordinator_test.go` is `package iverson` with the white-box header.
- **PA27** — re-verified: Python `author_id`→`AuthorId` for `Author` (`sample/models.py:41`), TS `authorId` (`sample/models/Article.ts:33`), Go `AuthorId` (`article.go:13`), .NET `BenchmarkAuthorId`. Holds **as written** — the row is scoped to declared *sample* entities. Its scope is what §2.1 falls through.
- **PA28** — `core.py:244` `@staticmethod _validate_key_declarations`, called at `:172`; `core.ts:215` throws inside the same function that calls `getRelations(cls)` at `:225`; `registrar.go:71` returns `fmt.Errorf`, relation loop at `:108-118`.
- **PA29** — `annotations.py:281` emits `"related_type"`; `annotations.ts:48` `relatedType: string`; `registrar.go:112` reads `fm.RelatedType`.

### Span check

No uncovered dependency this round. Round 3's uncovered dependency — that a m2o/o2o member's name equals its inferred FK — is now covered twice over: PA27 records that every declared entity satisfies it, and the spec's A35 records why it must be enforced rather than assumed. The remaining gap is not a missing assumption but a too-narrow one; that is §2.1, not a span item.

## 2. Literal-wrongness findings

### §2.1 — Tasks 5 and 6 add a check that rejects the registrar fixtures those very tasks run, so both suites fail

**Description.** Task 5 Step 5 and Task 6 Step 4 reject a `many_to_one`/`one_to_one` member whose PascalCase name is not `{RelatedType}Id`. Two existing test fixtures violate exactly that rule — and they are not incidental fixtures, they are the class the whole registrar suite registers.

Python `tests/test_schema_registrar.py:43` declares `author_id: str = many_to_one("RegAuthor")` on `RegArticle`. PascalCase of `author_id` is `AuthorId`; the required name is `RegAuthorId`. `RegArticle` is registered at `:197, 204, 213, 224, 235, 246, 259` and beyond, so the new `ValueError` fires in every one of those tests, not just a relation-specific case. Task 5 Step 8 (`pytest`) fails wholesale.

TypeScript `tests/schema-registrar.test.ts:70` has the identical shape: `@ManyToOne(() => RegAuthor) authorId: string = ''`. `RegArticle` is registered throughout the file, so Task 6 Step 6 (`npm test`) fails the same way.

The divergence is not accidental — `test_schema_registrar.py:286` asserts `rel.foreign_key == "RegAuthorId"` and `schema-registrar.test.ts:256-262` asserts `rel.foreignKey === 'RegAuthorId'`. Both tests exist *to pin* that the FK is inferred from the related type and not from the member name, which is precisely the divergence the new check now forbids. The fixtures are the pre-existing counter-example to the convention PA27 checked.

PA27 does not catch this because it enumerates *declared sample entities* — `sample/models.py`, `sample/models/Article.ts`, `article.go`, the LoadTest entity. Test fixtures declare entities too, and the check runs at registration, which is what those suites exercise. Go is genuinely unaffected: every Go `many_to_one` in the repo is `AuthorId`/`Author`.

**Evidence.**
- `Iverson.Clients/Python/tests/test_schema_registrar.py:43` — `author_id: str = many_to_one("RegAuthor")`; `:286` — `assert rel.foreign_key == "RegAuthorId"`.
- `Iverson.Clients/TypeScript/tests/schema-registrar.test.ts:69-70` — `@ManyToOne(() => RegAuthor) authorId: string = ''`; `:256-262` — `expect(rel.foreignKey).toBe('RegAuthorId')`.
- `Iverson.Clients/Python/iverson_client/core.py:94-97` — `_to_pascal_case` is `"".join(part.capitalize() …)`, so `author_id` → `AuthorId`, never `RegAuthorId`.
- `Iverson.Clients/TypeScript/src/core.ts:86-88` — `toPascalCase` upper-cases the leading character only.
- `Iverson.Clients/Go/sample/models/article.go:13`, `Iverson.Clients/Go/iverson_test/registrar_test.go:81`, `tags_test.go:261,472` — every Go declaration is `AuthorId` for `Author`.

**Proposed fix.** Rename the fixture member in each suite and widen the task's file list. The rename keeps both FK assertions true unchanged, because `_infer_fk`/`inferFk` derive the FK from the related type, not the member: `reg_author_id` still infers `RegAuthorId`.

Task 5 — add to Step 5:

> Rename `RegArticle.author_id` to `reg_author_id` in `tests/test_schema_registrar.py:43`; it declares `many_to_one("RegAuthor")` and is the fixture the whole registrar suite registers, so the new check would otherwise fail every test in that file. `:286`'s `assert rel.foreign_key == "RegAuthorId"` is unaffected — the FK is inferred from the related type, not the member.

Task 5's Files list gains `Test: Iverson.Clients/Python/tests/test_schema_registrar.py`; its `git add … tests/` already covers the path.

Task 6 — add to Step 4:

> Rename `RegArticle.authorId` to `regAuthorId` in `tests/schema-registrar.test.ts:70`, for the same reason; `:256-262`'s `expect(rel.foreignKey).toBe('RegAuthorId')` is unaffected.

Task 6's Files list gains `Test: Iverson.Clients/TypeScript/tests/schema-registrar.test.ts`, and its Step 7 `git add` must name that file alongside `tests/core.test.ts`.

Task 7 needs no equivalent; state that in Step 4 so the asymmetry is not read as an omission.

## 3. Forced decisions

No forced decisions found. The rename direction is not a decision — the check's naming rule is the spec's, and the fixtures' current names carry no meaning the rename destroys.

## 4. Previously addressed

- **Round 1 §2.1** (.NET PascalCase/camelCase omission) — resolved; T3 Step 3 and PA18.
- **Round 1 §2.2** (`getRelations(cls)` with no `cls`) — resolved; T6 Step 2 widens the signature.
- **Round 2 §2.1** (`meta`/`fm` out of scope in `entityToStruct`) — resolved; T7 Step 3.
- **Round 3 §2.1** (ListValue case firing on server-injected nav lists) — resolved; T7 Step 5's `KindOneToMany` skip.
- **Round 3 §1 span check** (m2o/o2o name vs inferred FK unenforced) — resolved at spec level (A35, Naming enforcement) and implemented as T5 Step 5, T6 Step 4, T7 Step 4.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**

One finding, in two clients, entirely within the delta this round added. §1 reconfirmed all twenty-nine assumptions including PA27–PA29; the span check is empty for the first time across four rounds. §3 is empty.

The pattern from earlier rounds repeats once more: the defect is a *second consumer* of something the change touched — here, the test fixtures that register entities, as against the sample entities the assumption enumerated.
