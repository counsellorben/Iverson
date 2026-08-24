# Critical Implementation Review: 2026-08-14-relation-foreign-key-integrity-implementation-plan (Round 1)

**Plan:** /home/ben/repositories/Iverson-conformance/docs/plans/2026-08-14-relation-foreign-key-integrity-implementation-plan.md
**Verified plan-level assumptions section:** present

⚠️ 2 commits since plan-write time (SHA 43e4b9b); cited file:line references re-checked under §1. Both commits are documentation-only (`3762038` the round-2 design review, `fe18ff8` the plan itself) — no source drift.

## 0. Coverage enumeration

### Task 1 × surfaces

| # | Surface | Disposition |
|---|---|---|
| T1.a | Step prose — insertion anchors ("before the final `return pascal` at `core.py:111`", `core.ts:109`, `registrar.go:335`) | ok — all three anchors read and confirmed exact: `core.py:111` is `return pascal`, `core.ts:109` is `return pascal;`, `registrar.go:335` is `return fm.Name` |
| T1.b | Code block — Python branch | ok — `relation["kind"]` is the correct discriminator (`core.py:274-282` builds the dict; `annotations.py:203` sets `"many_to_many"`); `pascal[:-3] + "s"` verified by execution to yield `PyTags` |
| T1.c | Code block — TypeScript branch | ok — `kind` param is `RelationKindString`, which includes `'many_to_many'` (`annotations.ts:43`); `slice(0,-3)+'s'` on `TsTagIds` → `TsTags` |
| T1.d | Code block — Go branch | ok — `KindManyToMany` exists and is used identically at `registrar.go:142,301,313`; `fm.Name` is already PascalCase (Go field names), so no case conversion is needed |
| T1.e | Step prose — doc-comment updates (3) | ok — each named comment exists and says what the plan claims: `core.py:102-106`, `core.ts:96-101`, `registrar.go:325-327` ("For others: use the field name as-is") |
| T1.f | Step prose — test descriptions + "do NOT modify existing many_to_many tests" | ok — the three cited existing tests assert FK properties only (`test_schema_registrar.py:320`, `schema-registrar.test.ts:469-471`, `coordinator_test.go:212-214`); the constraint is satisfiable as written |
| T1.g | Commands — Step 5 | → §2.1 |
| T1.h | Step prose — mutation testing (Step 6) | ok — three independent reverts, each with a named expected-red test. Mechanically sound: reverting a branch restores `property_name == foreign_key`, which the new assertion checks directly |
| T1.i | Commit — Step 7 `git add` paths | → §2.1 (same root cause: working directory) |

### Task 2 × surfaces

| # | Surface | Disposition |
|---|---|---|
| T2.a | Step prose — "At `:63-66`, filter the field map; at `:73`, pass the generic type" | ok — `:63-66` is the `fieldMap` build loop and `:73` the `f.set(...)` call; the block replaces both loops and correctly leaves the `Map<String, Field> fieldMap = new HashMap<>();` declaration above them intact |
| T2.b | Code block — `fromStruct` | ok — `isNavigationProperty` is `private static` in the same class (`:129`), so callable; `f.getGenericType()` returns `Type`, already imported (`:13`) |
| T2.c | Code block — typed `fromValue` + `elementTypeOf` | ok — prefixing an `if` before the existing `return switch(...)` body is valid; `Value.KindCase.LIST_VALUE` is the correct enum constant (confirmed in use at `StructConverterTest.java:115`); `ParameterizedType`/`Collection` already imported (`:12,16`) |
| T2.d | Code block — untyped `fromValue` | ok — `case LIST_VALUE -> { ... yield items; }` is a valid arm in the existing switch expression; recursion through the 1-arg overload matches the existing untyped semantics |
| T2.e | Step prose — "add `java.util.ArrayList` and `java.util.List` to the imports" | ok — neither is imported (`:16-19` has only `Collection`, `HashMap`, `Map`, `UUID`). Adding `List` does not conflict with the existing fully-qualified `java.util.List<Field> getAllFields` at `:182` |
| T2.f | Step prose — the three test descriptions | ok — the fixture supports all three: `StructTestArticle` has bare `List<UUID> tagIds` (unannotated, so not skipped) and `@ManyToMany List<StructTestTag> tags`; `StructTestTag` carries `@IversonEntity` (`StructConverterTest.java:31`), which `isNavigationProperty` requires |
| T2.g | Commands — Step 5 (`mvn -pl client test`) | ok — single-line command; `<modules>` lists `client` (`Java/pom.xml`), and `mvn test` runs generate-sources through test in one lifecycle |
| T2.h | Step prose — mutation testing (Step 6) | ok — hand-traced the third revert, the subtle one: removing the skip puts `tags` back in `fieldMap`, the `LIST_VALUE` arm resolves `elementType = StructTestTag`, each `STRUCT_VALUE` element falls to `default -> null`, yielding `[null, null]` against an asserted `null`. Genuinely red |
| T2.i | Commit — Step 7 `git add` paths | ok — single `cd`-free context; paths are repo-root-relative and correct |

### Cross-task contracts and rule-like content

| # | Item | Disposition |
|---|---|---|
| C1 | Task 1 "Produces: descriptors where `property_name != foreign_key`. Nothing in Task 2 consumes this." | ok — verified disjoint: Task 1 touches Python/TS/Go registrar derivation, Task 2 touches only `StructConverter.java`. No shared symbol, file, or language. The tasks are genuinely order-independent |
| C2 | Strip rule, **under-inclusion** — does `endsWith("Ids")` miss a colliding case? | ok — `many_to_many` FKs are always `{RelatedType}Ids`, so `pascal == fk` implies the suffix. Superset of the failure set |
| C3 | Strip rule, **over-inclusion** — does the length guard admit a bad case? | ok — `len > 3` means a member named exactly `Ids` is untouched; `TagIds` (len 6) → `Tags`. The narrowest true collision, a member named `Ids` on a type named `Id`, cannot arise since Go rejects non-conventional FK names and the other two derive the FK independently |
| C4 | Java nav-skip predicate, **over-inclusion** — could the skip wrongly drop a real FK column? | ok — this is the important direction. `isNavigationProperty` requires the collection's element type to carry `@IversonEntity` (`:141-145`). A single-member shape (`@ManyToMany List<UUID> javaTagIds`) has element type `UUID`, unannotated, so it is NOT skipped and still deserializes. The skip is correctly narrow |
| C5 | Dead 2-arg `fromValue` overload after the change | dropped — the plan keeps it "so nothing else has to change", but `fromStruct:73` was its only caller and now uses the 3-arg form, leaving it unused. `client/pom.xml` sets only `<source>21</source>`/`<target>21</target>` with no `-Werror` or `<compilerArgs>`, so an unused private method is at most a javac note. Build succeeds; spec outcome unaffected. Fails literal-wrongness |

## 1. Verified-plan-assumptions cross-check

All 19 assumptions reconfirmed under fresh read. Ones re-verified rather than accepted:

- **#2** (the corrected Go test path) — still holds: `Iverson.Clients/Go/iverson/` contains only `auth_test.go` and `coordinator_test.go`; the registrar tests are in the latter.
- **#7** (both `fromValue` overloads private, two in-file callers) — still holds: `:153,162` declarations, callers at `:73,90` only.
- **#9-#12** (the four test commands) — each re-read from its own config file; all four are valid invocations.
- **#14/#15** (the derivations) — #14 was verified by execution at plan-write time; #15 re-derived from `toPascalCase` at `core.ts:91-94`.
- **#17/#18** (consumer impact on existing tests) — re-read all three assertions; none touches `property_name`.

**Span check — one uncovered dependency, verified in-round:**

Task 1 Step 5 invokes bare `pytest`, which assumes both that `pytest` is on `PATH` and that `import iverson_client` resolves without an editable install. No listed assumption covers either. Verified: `pytest` resolves to `/home/ben/.local/bin/pytest`, and there is no `conftest.py` at either `Iverson.Clients/Python/` or `tests/` — so resolution relies on pytest's default `prepend` import mode inserting `Iverson.Clients/Python` (the first non-package ancestor of `tests/`) onto `sys.path`, which makes the top-level `iverson_client` package importable. The invocation works as written. Recorded because it is an environment precondition the plan states nowhere.

## 2. Literal-wrongness findings

### §2.1 — Task 1's Step 5 command block silently skips two of the three suites, and Step 7's `git add` then fails

**Description.** Step 5 is three sequential lines that each `cd` with a **relative** path:

```bash
cd Iverson.Clients/Python     && pytest
cd Iverson.Clients/TypeScript && npm test
cd Iverson.Clients/Go         && go test ./... && go vet ./... && gofmt -l .
```

Run as a block from the repository root, line 1 succeeds and leaves the shell in
`Iverson.Clients/Python`. Line 2 then resolves `Iverson.Clients/TypeScript` **relative to that
directory** — `Iverson.Clients/Python/Iverson.Clients/TypeScript`, which does not exist. `cd` exits
non-zero, and because the line is `cd … && npm test`, the TypeScript suite never runs. Line 3 fails
identically, so the Go suite, `go vet` and `gofmt -l` never run either.

The failure is quiet in the way that matters: the implementer sees a passing pytest run plus two
`cd: no such file or directory` messages, and can plausibly report Step 5 complete. Step 6's
mutation testing then depends on suites that were never executed, and the plan's own
"Python was 188 and TypeScript 184 before this task" checkpoint cannot be evaluated.

The same defect propagates to **Step 7**, whose `git add` arguments are repo-root-relative but
would execute from `Iverson.Clients/Python` (the last successful `cd`), so every path fails to
resolve.

This is exactly the class of defect the plan's own Global Constraint on mutation testing exists to
prevent — a step that reports success while its verification never ran.

**Evidence.**
- Plan Task 1, Step 5 — the three-line block above.
- Plan Task 1, Step 7 — `git add Iverson.Clients/Python/iverson_client/core.py \ …`, repo-root-relative.
- Directory layout: `Iverson.Clients/{Python,TypeScript,Go}` are siblings, so no relative path from one reaches another without `../`.
- Task 2 Step 5 (`cd Iverson.Clients/Java && mvn -pl client test`) is a single line and is unaffected; the defect is specific to Task 1's multi-line block.

**Proposed fix.** Wrap each invocation in a subshell so the working directory never leaks between
lines, leaving Step 7 to run from the repository root:

```bash
(cd Iverson.Clients/Python     && pytest)
(cd Iverson.Clients/TypeScript && npm test)
(cd Iverson.Clients/Go         && go test ./... && go vet ./... && gofmt -l .)
```

Add one sentence to Step 5 stating that all commands are run from the repository root, so Step 7's
`git add` paths resolve. Applying the same subshell form to Task 2's Step 5 is not required but
keeps the two tasks' command style consistent.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — address §2.1, then proceed to `subagent-driven-development`.
