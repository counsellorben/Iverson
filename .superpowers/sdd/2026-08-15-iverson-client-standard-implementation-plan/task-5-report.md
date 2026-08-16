# Task 5 — fix round 1

FIX_BASE: `ea4fd7a`. Branch `client-conformance-harness`. All seven review findings (4 Important, 3
Minor) addressed. This file is written and verified on disk this round — the prior round claimed a
report that did not exist; that failure mode is why this line is here.

## IMPORTANT 1 — IVC-REL-001 negative clause

`Verifier.VerifyRegistration` now emits a real, failable assertion for `one_to_many`: it checks the
declaring type for a spuriously-synthesized `{RelatedTypeName}Id`-shaped property and fails if one
exists, citing `Requirements.RelForeignKeySynthesizedForOwningKinds`. The const's doc comment no
longer presents the loop's `continue` as discharging the negative half.

## IMPORTANT 2 — IVC-REL-002 scope

Extended the naming assertion to `many_to_many`, expecting the kind-appropriate suffix (`Id` for
`ManyToOne`/`OneToOne`, `Ids` for `ManyToMany`). Deleted the exclusion comment. The standard's
statement was not weakened.

## IMPORTANT 3 — IVC-REL-010 over-fire / under-test

(a) `VerifyThreeWay` now cites `RelForeignKeyWellFormedUuid` only when `isKey` is false, so the
primary key (always present in `ComparedValueNames`) can never discharge it alone.
(b) Added a second, independent assertion in `VerifyRegistration` — `fkProperty.ClrType ==
ClrType.ClrGuid` for every owning-kind relation's foreign key — asserted directly from the
descriptor the driver reported. `ClrGuid` is exactly the CLR type
`SchemaRegistrationOrchestrator.cs` maps to a `UUID`/`UUID[]` SQL column; the array/scalar split is
already covered by the existing `IsArray` (REL-004) checks, so this assertion covers the "typed
UUID" half without duplicating that one.

## IMPORTANT 4 — no one_to_one fixture

Added a `one_to_one` relation to all five conformance drivers' Article fixture, related to Tag (the
existing many_to_many relation's own related type) through the **singular** FK name (`...TagId`
vs. the m2m's plural `...TagIds`) — this avoided introducing a whole new entity/table while still
giving REL-001/002/003 a genuine, distinct one_to_one relation to grade. (My first attempt reused
Author as the related type, which collided on the required FK name with the existing many_to_one
relation — caught by the unit suite before it reached any driver.) Server-side, `EntityRelationResolver.cs:39-40`
already treats `OneToOne` identically to `ManyToOne` for depth resolution, so no server change was
needed. Files touched per language: `.NET` (`DotNetArticle.cs`, `Program.cs`), `Java`
(`JavaArticle.java`, `Driver.java`), `Python` (`models.py`, `driver.py`), `TypeScript`
(`models.ts`, `driver.ts`), `Go` (`models.go`, `main.go`). `CrudRoundtripScenario.cs`'s expected
relation kinds for `article` now include `RelationKind.OneToOne`. Added unit fixtures
(`VerifyRegistration_passes_a_conforming_one_to_one_relation`,
`VerifyRegistration_fails_a_one_to_one_relation_whose_nav_property_equals_its_foreign_key`) so the
kind is exercised at the unit level too, not only live.

## MINOR 1 — REL-009 statement mutated

Restored the Statement cell to the spec's original wording; moved the retirement rationale into
prose immediately below the table. Added an "Authoring notes (for future axes)" subsection to the
standard's REL section stating statements are immutable across retirement.

## MINOR 2 — citation must be a constructor argument

Documented in the same authoring-notes subsection: a requirement ID must be passed as the
`requirementId` argument to `Assertion.From`/`Pass`/`Fail`, not merely appear in a comment or doc
string, because check 2 is a substring match.

## MINOR 3 — relation-shape backstop has no requirement ID and no stated purpose

Documented in the same authoring-notes subsection which assertion (`VerifyRegistration`'s "declares
exactly the expected relation kinds", fired unconditionally outside the per-relation loop) backstops
the per-relation loop, and why it carries no requirement ID of its own.

## Verification

**Unit suite** (`dotnet test Iverson.Server/Iverson.ClientConformance.Tests/`): **141/141 passed**,
0 failed, 0 skipped. (Baseline before this round was 134; +1 fixture rename fix, +6 new tests for
the four Important fixes, +1 pre-existing-scenario fixture fix — see Residual below.)

**Mutation testing**, each new/changed assertion in isolation, run and reverted one at a time:

| Assertion mutated | Test(s) reddened |
|---|---|
| REL-001 negative (`!spurious` → `true`) | `VerifyRegistration_fails_a_one_to_many_relation_with_a_spurious_foreign_key` |
| REL-002 many_to_many suffix (forced `"Id"` for all kinds) | `VerifyRegistration_passes_a_conforming_fk_on_the_member_descriptor`, `VerifyRegistration_fails_when_a_many_to_many_foreign_key_is_not_named_relatedTypeIds` |
| REL-010 isKey-scoped citation (always cite) | `VerifyThreeWay_does_not_cite_REL010_for_the_primary_key` |
| REL-010 UUID typing (dropped `ClrType` check) | `VerifyRegistration_fails_when_an_owning_foreign_key_is_not_typed_uuid` |
| one_to_one grading (dropped `OneToOne` from the two kind-lists) | `VerifyRegistration_passes_a_conforming_one_to_one_relation`, `VerifyRegistration_fails_a_one_to_one_relation_whose_nav_property_equals_its_foreign_key` |

Every mutation reddened at least one test and only the expected ones; suite confirmed green again
after each restore.

**Gate re-proof**: deleted the sole citation of `Requirements.RelWritePayloadForeignKeyOnly`
(`NavPropertyRejectedScenario.cs:147`, replaced with `null`). `Check2_EveryRegistryConst_IsCitedByAssertionCodeOutsideRequirementsAndTests`
went red:
```
Expected uncited to be empty ... but these are uncited: RelWritePayloadForeignKeyOnly
```
Restored; check2 green again (confirmed by a scoped re-run).

**Live matrix** (`dotnet run --project Iverson.Server/Iverson.ClientConformance --
--languages dotnet,python,typescript,go,java`, all four scenarios — no `--scenarios` filter, so
`nav-property-rejected` runs and REL-005 is exercised). No server code was touched this round, so
the already-rebuilt `iverson-api` image (HEAD `c6cb271`, confirmed via `docker compose ps` — all 12
services up) did not need rebuilding.

First run surfaced a pre-existing, unrelated bug: `NavPropertyRejectedScenario`'s fixture used FK
name `AuthorId` for a `ManyToOne` relation to `S3NavAuthor`, which fails Task 4's registration-time
naming check (`{RelatedTypeName}Id` = `S3NavAuthorId`) before the scenario ever reaches the payload
it exists to test. This left `IVC-REL-005` untouched (`"requirements: 1 untouched of 9"`). Not one
of the seven findings in scope, but it directly blocked the explicit ask that REL-005 be exercised,
so I fixed the fixture's FK name (and the two hardcoded `'AuthorId'` strings in
`NavPropertyRejectedScenarioTests.cs`) and re-ran.

Final matrix, all languages, all four scenarios:

```
scenario               dotnet      python      typescript  go          java
crud-roundtrip         ok          ok          ok          ok          ok
naming-rejected        skip        ok          ok          ok          skip
nav-property-rejected  ok          skip        skip        skip        skip
interop                ok          ok          ok          ok          ok
requirements: 0 untouched of 9 registered
```

`naming-rejected`'s two skips (dotnet, java) and `nav-property-rejected`'s four skips
(python/typescript/go/java) are pre-existing, documented, structural — not a defect (the latter is
a single orchestrator-only check that legitimately runs once, not once per language). `crud-roundtrip`
ok on all five languages confirms the one_to_one fixture registers, hydrates at depth 1, and passes
the three-way comparison for every driver. **0 of 9 REL requirements untouched** — full axis
coverage, including REL-005, achieved in this run.

**Tree**: clean after commit (verified below).

## Residuals

- The `NavPropertyRejectedScenario` FK-naming fixture bug (pre-existing at `ea4fd7a`, predates
  Task 5) is now fixed as a byproduct of getting REL-005 exercised; flagging it here since it was
  not one of the seven assigned findings.
- Everything else in the seven findings is closed; no other residuals.
