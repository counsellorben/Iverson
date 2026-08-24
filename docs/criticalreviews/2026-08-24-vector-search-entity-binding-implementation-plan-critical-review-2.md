# Critical Implementation Review: 2026-08-24-vector-search-entity-binding-implementation-plan (Round 2)

**Plan:** /home/ben/repositories/Iverson-followups/docs/plans/2026-08-24-vector-search-entity-binding-implementation-plan.md
**Verified plan-level assumptions section:** present (25 rows)

⚠️ 3 commits since plan-write time (SHA 1faae1c): `3a98b34` (plan), `318ffef` (round-1 review), `447d8cb` (round-1 fixes applied). No source changed; cited file:line references re-checked under §1 regardless.

## 0. Coverage enumeration

| Row | Disposition |
|---|---|
| T1 · step prose 1 (test-first, host class, assertions, seeding requirements) | ok — the three assertions now each name a payload entry the test seeds; `["key"]` and the uppercase-column value were added in round 1's fixes |
| T1 · step prose 2 (run and watch fail) | ok — sibling test at `:96` already reaches the `:278` loop |
| T1 · step prose 3 (name lookup + `key` special case + `UpperFirst` fallback) | ok — re-read `IntelligenceStoreConsumer.cs:417/:422/:435/:440` and `SchemaBuilder.cs:53`; the four payload-key producers are unchanged |
| T1 · step prose 3 (type mapping: case-insensitive, exact-not-prefix, parse fallback) | ok — re-read `SchemaBuilder.cs:351-380`; vocabulary unchanged, arrays still absent from it and so still fall to the string default |
| T1 · step prose 3 (`exemptField` → `schema.KeyColumn.Name`) | → §3.1 |
| T1 · step prose 3 (`using System.Globalization`, no reuse of `SqlTypeToPayloadKind`) | ok — still absent from the file; `SqlTypeToPayloadKind` still assigns `INTEGER[]` the `Integer` kind (`:369`) |
| T1 · step prose 4-6 (re-run, full suite, reap, commit) | ok — project in `Iverson.slnx`, reap script present, commit convention unchanged |
| T1 · code blocks | none — task stated wholly in prose |
| T1 · wiring text (lookup built once per request) | ok — `schema` bound at `:130`, loop at `:278`, same method body |
| T1 · dynamic: duplicate camelCase keys would throw from a dictionary build | dropped — structurally unreachable: `SchemaBuilder.cs:53` excludes the key from scalars, and a user property named `__TenantId` is rejected at registration (`SchemaRegistrationOrchestrator.cs:550`) |
| T1 · dynamic: renamed keys reaching the client projections | ok — after the rename the five typed projections bind the key property where they previously ignored it; no driver or scenario depends on it being unbound (see the contract rows below) |
| T2 · step prose 1 (rebuild image, redeploy) | ok — the plan header pins the worktree (`/home/ben/repositories/Iverson-followups`), and the runbook hardcodes no checkout path |
| T2 · step prose 2 (run the matrix per runbook) | ok — `docs/runbooks/client-conformance-matrix.md` present, with a "Running it" section |
| T2 · step prose 3 (acceptance criterion) | ok — round 1's fix restated it as exit-0 plus absence of the `FAIL:` line, matching `Program.cs:252-260` |
| T2 · step prose 4 (report only what was read) | ok |
| Contract T1 → T2, call site 1: `vector-search` cell sensitivity to the **naming** change | ok — the .NET driver reports `hit.Entity.Label` (`Program.cs:485`) and `VectorSearchScenario.ReadLabels` (`:351`) grades those labels; a client that cannot bind the payload field reports empty labels, which the scenario explicitly distinguishes from an empty result (`:303-308`). The cell is genuinely sensitive to the rename |
| Contract T1 → T2, call site 2: `vector-search` cell sensitivity to the **typed-value** change | → §1 span check |
| Contract T1 → T2 (deploy boundary: image must contain T1's commit) | ok — T2 step 1 rebuilds and redeploys before running |
| Rule · name resolution, both directions | ok — re-checked; payload keys are `ToCamelCase(name)` at all four producers, so the camelCase-keyed lookup matches exactly, and vector/FK keys take the documented `UpperFirst` fallback |
| Rule · type mapping, both directions | ok — over-inclusion barred by exact matching; under-inclusion checked against all three `ColumnDescriptor` producers (`SchemaBuilder.cs:57/:191/:198`) |
| Rule · identity/exclusion mechanics of `exemptField` | → §3.1 |

## 1. Verified-plan-assumptions cross-check

A1-A20 and A22-A25 — **still hold** under fresh reads of the cited evidence. A24 and A25, ratcheted in from round 1's span check, were re-read this round and both reconfirm (`IntelligenceVectorService.cs:202-207`; `AuthorizationFieldMasking.cs:171-178` with `SchemaDescriptor.cs:13,20-21`).

A21 — **fails, with evidence not cited in round 1.** The row still reads "Task 1 step 3 pins both together; step 1 tests it", and asserts that without the `"key"` special case "the identifier is masked under any FieldPermission." Both halves are false:

- `allFields` is seeded with `schema.KeyColumn.Name` (`RowFieldAuthorizationEvaluator.cs:84`), the key column is explicitly filtered out of `excluded` (`:76`), and the final set is `allFields.Where(f => !excluded.Contains(f)).ToHashSet()` (`:116`). The key column is therefore in `AllowedFields` whenever `AllowedFields` is non-null.
- So once Task 1 renames the identity entry to `Id`, `UpperFirst("Id")` is `"Id"`, which is in `AllowedFields`, and the field is never removed — at any value of `exemptField`.

The corrected fact: post-rename the identifier is protected by `AllowedFields` itself, not by the exemption. Pre-rename it is `"Key"`, which is absent from `AllowedFields`, and `exemptField: "Key"` is what protects it. The masking consequence the row asserts runs in the opposite direction from the one it states.

**Span check — one uncovered dependency, verified in-round:**

1. *No assumption establishes what the live matrix can verify.* Task 2 is the plan's only end-to-end check, and its subject type `VectorDoc` is `id: uuid.UUID` plus six `str` fields (`Iverson.Clients/Python/conformance/models.py:133-139`) — no numeric, boolean, or timestamp property. The matrix therefore exercises the **naming** half of Task 1 and cannot detect a broken **typed-value** mapping at all: a green `vector-search` cell is consistent with every numeric column emitting `Value.ForString`. Typed-value correctness rests entirely on Task 1's unit test, which round 1's fixes made sensitive to it. Not a defect in either task as written — but the plan should not be read as the matrix confirming both halves.

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

### 3.1 The `exemptField` change is inert; the plan and spec both call it required

**The choice.** Whether Task 1 keeps `exemptField: schema.KeyColumn.Name`, and what the plan and spec say about why.

**Why it's forced.** Spec §3 states: *"`MaskDisallowedFields`'s `exemptField: "Key"` becomes the key column's name. Without this the masking strips the identifier as soon as the keys become canonical."* The codebase contradicts the second sentence. Because the key column is unconditionally admitted to `AllowedFields` (`RowFieldAuthorizationEvaluator.cs:84`, `:76`, `:116`), the renamed identity field `Id` survives masking with `exemptField` set to the key column name, left at `"Key"`, or passed as `null`. The parameter does no work for the identity field after the rename. The plan cannot simultaneously keep the change, keep its stated rationale, and be accurate — and A21 currently documents a guarantee the evaluator does not provide. This is not a matter of test design (round 1's §2.1 read it as an untestable assertion; the mechanism is that there is nothing to test).

**The options.**

- **(a) Keep the change; correct the rationale.** Task 1 still passes `schema.KeyColumn.Name`; A21 and spec §3 are reworded to say the exemption is inert for the identity field post-rename and is updated only so it names a field that exists. Smallest edit; requires an `update-design-doc` pass for spec §3.
- **(b) Drop the change.** Task 1 leaves `exemptField: "Key"` untouched, shrinking the diff to naming and typing. Requires amending spec §3 to remove the mandate, and A21 disappears rather than being reworded.
- **(c) Keep the change and both rationales as written.** No edits; the plan and spec continue to assert a masking guarantee that the evaluator does not implement. Costs nothing now and leaves a false statement in two documents for the next reader.

## 4. Previously addressed

- Round 1 §2.2 (no `"key"` entry seeded) — resolved: step 1 now requires seeding `["key"]` and states why.
- Round 1 §2.3 (uppercase-`SqlType` column declared but not populated) — resolved: step 1 now requires seeding a payload value for it and asserting its emitted `KindCase`.
- Round 1 §2.4 (`0 untouched of 43` not emitted by the harness) — resolved: the criterion is now exit-0 plus absence of the `FAIL:` line, matching `Program.cs:252-260`.
- Round 1 §1 span check — resolved: both uncovered dependencies are now table rows A24 and A25.
- Round 1 §2.1 (the masking assertion cannot fail) — **not resolved, and superseded.** It was deferred rather than applied; §3.1 above replaces it with the mechanism, on evidence round 1 did not cite. Its proposed fix — adding a `FieldPermission` so `AllowedFields` is non-null — would not have made the assertion falsifiable.

## 5. Recommendation

🛑 **Surface forced decisions to user.** §2 is empty and every other round-1 finding is resolved. §3.1 is the one open item, and it needs a decision rather than a fix: the evaluator makes the `exemptField` change inert, so the plan, the A21 row, and spec §3 currently agree with each other and disagree with the codebase. Options (a) and (b) both require touching the spec, so the resolution runs through `update-design-doc` as well as `update-implementation-plan`.
