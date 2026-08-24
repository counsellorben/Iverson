# Critical Implementation Review: 2026-08-24-vector-search-entity-binding-implementation-plan (Round 3)

**Plan:** /home/ben/repositories/Iverson-followups/docs/plans/2026-08-24-vector-search-entity-binding-implementation-plan.md
**Verified plan-level assumptions section:** present (25 rows; A21 removed, A26 added)

⚠️ 5 commits since plan-write time (SHA 1faae1c), including `d1ac501` which edited the source spec's §3 and one of its verified-assumption rows. Cited file:line references re-checked under §1.

## 0. Coverage enumeration

| Row | Disposition |
|---|---|
| T1 · step prose 1 (test-first, assertions, seeding, both-casings schema) | ok — each of the three assertions now names a payload entry the test seeds; the uppercase column carries a seeded value |
| T1 · step prose 2 (run and watch fail) | ok — sibling test at `:96` reaches the `:278` loop |
| T1 · step prose 3 (name lookup + `key` special case + `UpperFirst` fallback) | ok — re-read the four payload-key producers (`IntelligenceStoreConsumer.cs:417/:422/:435/:440`) and `SchemaBuilder.cs:53`; unchanged |
| T1 · step prose 3 (type mapping: case-insensitive, exact-not-prefix, parse fallback) | ok — re-read `SchemaBuilder.cs:351-380`; vocabulary and array forms unchanged |
| T1 · step prose 3 (masking left alone) | ok — now reads "leave `exemptField: \"Key\"` unchanged"; consistent with the corrected spec §3 at `d1ac501` |
| T1 · step prose 3 (`using System.Globalization`, no reuse of `SqlTypeToPayloadKind`) | ok — still absent from the file; `:369` still types `INTEGER[]` as `Integer` |
| T1 · step prose 4-6 (re-run, full suite, reap, commit) | ok — project in `Iverson.slnx`, `scripts/reap-testcontainers.sh` present |
| T1 · code blocks | none — task stated wholly in prose |
| T1 · wiring text (lookup built once per request) | ok — `schema` bound at `:130`, loop at `:278`, same method body |
| T1 · dynamic: two payload keys resolving to one emitted name (silent overwrite) | dropped — requires two descriptor columns differing only in first-letter case; the key column is excluded from `ScalarColumns` (`SchemaBuilder.cs:53`) and `__TenantId` is rejected as a user property (`SchemaRegistrationOrchestrator.cs:550`) |
| T1 · dynamic: FK and vector columns resolving through the lookup | ok — FK columns are also scalars, so `authorId` resolves to `AuthorId`; vector fields share the scalar's key upstream, so no second entry exists to collide |
| T2 · step prose 1-2 (rebuild, redeploy, run per runbook) | ok — plan header pins the worktree; runbook hardcodes no checkout path |
| T2 · step prose 3 (`vector-search` ok ×5) | → §3.1 (the exit-0 half) |
| T2 · step prose 3 (all five languages can reach `ok` at all) | ok — a missing toolchain is reported as `skip`, never `ok` (runbook `:57`); all five are installed on this machine (dotnet 10.0.111, python 3.14.4, node v22.16.0, go 1.22.12, java 21.0.5) |
| T2 · step prose 4 (report only what was read) | ok |
| Contract T1 → T2 (deploy boundary) | ok — step 1 rebuilds and redeploys before the run |
| Contract T2 → harness exit code | → §3.1 |
| Rule · name resolution, both directions | ok — re-checked against all four producers; camelCase-keyed lookup matches exactly, vector/FK keys take the documented fallback |
| Rule · type mapping, both directions | ok — over-inclusion barred by exact matching; under-inclusion checked against all three `ColumnDescriptor` producers (`SchemaBuilder.cs:57/:191/:198`) |

## 1. Verified-plan-assumptions cross-check

A1-A20 and A22-A26 — **all still hold** under fresh reads. Spot-re-read this round: A11 (`Iverson.slnx` contains `Iverson.Api.Tests`), A12 (`IClassFixture<QdrantGrpcContainerFixture>` at `ObjectSearchVectorIntegrationTests.cs:49`), A13 (`scripts/reap-testcontainers.sh`), A16 (`using Google.Protobuf.WellKnownTypes;` at `:1`), A19 (`MakeStream<T>()` at `:87`), plus the producer and vocabulary citations listed in §0. A26, added last round, reconfirms: `VectorDoc` remains `id` plus six `str` fields.

No failed assumptions this round. A21's removal is consistent with the resolution recorded in §4.

**Span check — two uncovered dependencies:**

1. *No assumption covers all five toolchains being installed.* Task 2 step 3 requires `vector-search` to be `ok` for all five languages, but a language whose toolchain is absent is reported as `skip` (runbook `:57`), which is not `ok` and which Task 1 cannot change. **Verified in-round and holds:** all five are present on this machine. Worth a table row so the dependency is stated rather than assumed.
2. *No assumption covers the matrix's non-`vector-search` cells passing.* This one cannot be verified by reading — see §3.1.

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

### 3.1 Task 2's exit-0 criterion is stricter than the plan's scope, and its reachability is unverified

**The choice.** Whether Task 2's success criterion stays "the full-matrix run exits 0", or narrows to the cells this plan actually changes.

**Why it's forced.** Round 1 replaced an unsatisfiable criterion (`0 untouched of 43`, which the harness never prints on success) with the harness's real success semantics. But those semantics are strictly stronger than "`vector-search` is green": `RunSucceeded = allPassed && (!fullMatrix || untouched.Count == 0)` (`Report.cs:96-98`), and `AllPassed` is `_cells.Where(c => c.Status is not (Skip or Xfail)).All(c => c.Status == Ok)` (`:69-71`) — every non-skipped cell in all ten scenarios, not just the four VEC requirements. Two facts sharpen this:

- There is no expected-failure escape hatch in use: `ReportCell.Xfail` exists on the type, but **no scenario constructs one** (grep for `Xfail(` across `Iverson.ClientConformance/` returns only the factory's own declaration). Every failing cell is a plain `Fail` and blocks exit 0.
- Skips do not block it — so the plan's inherited out-of-scope item ("the nine remaining conformance-matrix skips") is genuinely harmless here. Failures are the exposure.

The plan nowhere establishes that the only currently-failing cells are the three Task 1 addresses. If any non-`vector-search` cell fails for an unrelated reason, Task 2's step 3 is unsatisfiable no matter how correct Task 1 is, and the executor is left either blocked or tempted to repair unrelated scenarios to force a green exit — silent scope expansion the plan never authorized. This cannot be settled by reading: it requires a matrix run, which is Task 2's own work and outside a review's remit.

**The options.**

- **(a) Narrow the criterion to what this plan changes.** Step 3 becomes: `vector-search` is `ok` for all five languages, and no cell that passed before Task 1 now fails. Verifies the change and guards regressions without making Task 2 hostage to pre-existing failures elsewhere.
- **(b) Keep exit-0 and record the precondition.** Add a step 0 to Task 2: run the matrix *before* Task 1's change, record the failing set, and require that the post-change run's failing set be empty. Costs an extra full-matrix run; makes exit-0 meaningful rather than assumed.
- **(c) Keep exit-0 as-is and accept the risk.** Cheapest; if an unrelated cell is red, Task 2 fails for a reason outside its scope and the executor has to escalate.

## 4. Previously addressed

- Round 2 §3.1 (the `exemptField` change is inert) — resolved by option (b): Task 1 step 3 now leaves `exemptField: "Key"` alone, the A21 row is gone, and the source spec's §3 plus its verified-assumption row were corrected manually at `d1ac501` so spec and plan agree.
- Round 2 §1 A21 failure — resolved by the same change; the row no longer exists to be wrong.
- Round 2 §1 span check (matrix verifies naming only) — resolved: recorded as table row A26.

## 5. Recommendation

🛑 **Surface forced decisions to user.** §1 has no failed assumptions and §2 is empty — the plan's tasks are internally sound and every round-1 and round-2 finding is closed. The single open item is §3.1, and it is a scoping decision rather than a defect: Task 2's criterion is currently stronger than the plan's own scope, and whether that is safe depends on the state of cells this plan does not touch. Options (a) and (b) are both plan-only edits; none requires a spec change.
