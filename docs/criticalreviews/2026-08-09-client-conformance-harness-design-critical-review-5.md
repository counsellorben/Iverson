# Critical Design Review: 2026-08-09-client-conformance-harness-design (Round 5)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-09-client-conformance-harness-design.md`
**Verified Assumptions section:** present

Coverage re-derived against the current spec before consulting rounds 1–4. The phase model introduced last round is the newest surface and gets a full pass here, not a spot-check.

## 0. Coverage enumeration

### Sections

| Row | Disposition |
|---|---|
| Header / Depends on | dropped — still describes the key-typing fix as pending; historical, no behavioural consequence (same disposition since round 2) |
| Problem | ok — narrative matches the record |
| Contract | ok — the yardstick is unchanged |
| Architecture — file layout | ok — parent paths exist; the Java aggregator takes a new module additively |
| Architecture — `DriverRunner` "execs it once per phase" | ok — consistent with the driver-protocol text after round 4 |
| Architecture — why an orchestrator | ok — the flow-executor sequence exists once, in C# |
| Driver protocol — the phase enum `register`/`write`/`read`/`delete` | → §2.1 |
| Driver protocol — invocation flags | ok — `--phase` present; `--keys` on phases after `register`; `--out` per phase |
| Driver protocol — output document | → §2.1 (the example contradicts its own `phase` field) |
| Driver protocol — four properties | ok — report-never-assert, public-API-only, typed `entity`, full `typeDescriptor`; all survive phasing unchanged |
| Driver protocol — failed step is data, non-zero exit means the driver broke | ok — per-phase documents preserve the distinction |
| Registration and authorization are separate steps | ok — round 1's fix, and the phase model now gives the orchestrator's re-registration an explicit slot between `register` and `write` |
| S1 — step table | → §2.1 |
| S1 — "the phase boundaries are what make this order real" | ok — the claim is true for every boundary except the one §2.1 identifies |
| S2 — naming-rejected | ok — a `register`-phase-only scenario; the client-side rejection (Go `registrar.go:110-111`, TypeScript `core.ts:244-254`, Python per A10) happens before any RPC, so no later phase is reachable or needed |
| S3 — nav-property-rejected | ok — orchestrator-only; no driver, no phases |
| S4 — interop | ok — **checked the phase interaction**: if all five drivers ran `--phase register` they would each re-register `SharedArticle`, and `SchemaRegistry.RegisterAsync` replaces the stored descriptor wholesale (`SchemaRegistry.cs:47-56`), so four of them would overwrite .NET's. The spec's ".NET driver registers `SharedAuthor` and `SharedArticle` **once**" governs — the other four have no `register` phase for this scenario |
| S4 — `--keys` on the read phase | ok — key collection happens in the `write` phase, which precedes every `read` phase, so the map is complete before any cross-read |
| Isolation | ok — round 3's fix; keys are UUIDs, driver-chosen, reported by logical name |
| Verification — three-way comparison | ok — after round 4 the orchestrator's two observations are taken between the `read` and `delete` phases, against live rows |
| Verification — table naming | ok — `SchemaBuilder.cs:30` |
| Verification — registration assertions | ok — operands present in the reported `TypeDescriptor` |
| Reporting | ok — the matrix is per language × scenario, which phasing does not change; a phase failure surfaces as that scenario's cell |
| Expected failures | ok — re-based in round 1; fallback cause-based rule correct |
| Lifecycle | ok — preflight, no compose management, tenant provisioning duplicated per A3. Missing-toolchain degradation still works: an absent `mvn` skips every Java phase |
| CI readiness | ok — line-checked in round 2 |
| Testing the harness | ok — both named mutations target assertions that survive phasing |
| Consequences | ok — driver-maintenance cost is if anything understated now, but that is a cost statement, not a correctness claim |
| Verified assumptions A1–A19 | see §1 |
| Known issues | ok — drift bullet matches the verified mechanism |

### Rules and operands

| Rule | Over-inclusion | Under-inclusion | Disposition |
|---|---|---|---|
| **Phase label → driver behaviour** | one label triggers work belonging to another phase | a step no label reaches | → §2.1 (both directions, same defect) |
| **Three-way agreement** | declares a defect where sources differ legitimately | agrees vacuously | ok — the vacuous direction closed by round 4's sequencing; the orchestrator now observes live rows |
| **Key identity** | two runs collide | orchestrator addresses a key no driver wrote | ok — round 3's reported-map fix |
| **Registration verification** | flags a conforming descriptor | passes a broken one | ok — operands present |
| **Relation coverage** | asserts on an undeclared kind | declares a kind no step reads | ok — all three kinds declared and read, and after round 4 the reads can execute |
| **Expected-failure predicate** | marks a passing step xfail | leaves a known-failing step unmarked | ok — moot against current `main` |

### Data-flow arrows

| Arrow | Disposition |
|---|---|
| orchestrator → driver `--phase <p>` → driver's step selection | → §2.1 — the parameter does not determine the operation |
| driver phase → `--out` document → orchestrator | ok — one document per phase; `typeDescriptor` and `keys` are each produced by the phase that owns them |
| orchestrator → re-`RegisterSchema` between `register` and `write` | ok — the descriptor is available from the `register` phase's document before the `write` phase starts |
| orchestrator → `MappingGet(article, depth: 1)` between `read` and `delete` | ok — row is live; key comes from the `write` phase's `keys` map |
| orchestrator → `MappingGet(author, depth: 1)` between `read` and `delete` | ok — same |
| orchestrator → Postgres `SELECT` | ok — same window, same key source |
| driver B `read` phase ← every driver's `keys` via `--keys` | ok — the `write` phase of all five precedes the `read` phase of any |
| orchestrator → row deletion on completion | ok — its own S3/S4 rows |

## 1. Verified-assumptions cross-check

All nineteen reconfirmed under a fresh read. A1–A15 as recorded, with A6, A7 and A8 the three failures and their design responses in place; A16–A19 have unchanged cited evidence (`PostgresSchemaManager.cs:138-148` plus the absence of `FORCE ROW LEVEL SECURITY`; `SchemaType`/`SchemaField` carrying no `tenant_field`; `SchemaBuilder.cs:163,236` with `RelationValidator.cs:88,110`; `SchemaRegistrationOrchestrator.cs:113` with the six-RPC service surface).

Note on A7: the phase model added last round makes A7's consequence concrete rather than changing it — the orchestrator's re-registration now has a named slot between the `register` and `write` phases. No assumption text needs amending.

### Span check — one uncovered dependency

**Nothing establishes that a phase label uniquely determines what a driver does.** The phase model is the design's own construct, so no codebase fact can cover it — but the design does not state the property it relies on, and the S1 table violates it. → §2.1.

## 2. Literal-wrongness findings

### §2.1 — The `write` phase is invoked twice with different intended meanings, and the driver cannot tell them apart

**Description.** The driver is *"invoked once per **phase** — `register`, `write`, `read`, `delete`"*, and receives that phase as `--phase <p>`. The phase label is the driver's only instruction about what to do. S1's step table assigns `*driver* write` to two different rows, separated by the `read` phase and both orchestrator rows:

| Phase | Step |
|---|---|
| *driver* `write` | write author, tag, article with both FKs |
| *driver* `read` | get article (depth 0) |
| *orchestrator* | read the article at depth 1 |
| *orchestrator* | read the author at depth 1 |
| *driver* `write` | update title |
| *driver* `delete` | delete |

Both invocations pass `--phase write`. The driver has no parameter distinguishing them, so one of two things happens, and neither is the design's intent: it re-runs the initial three writes (re-upserting author, tag and article, and never performing the update), or the update is simply unreachable because no label selects it. Either way the step *"update title — write path works against an existing row"* does not execute, and with it goes the only coverage of the update path against a persisted row.

The same under-determination shows in the protocol example, which carries `"phase": "write"` while listing `register`, `write`, `get`, `update` and `delete` steps in one document. The surrounding prose frames it as a combined reference, but as the spec's only concrete illustration of a phase document it contradicts the field it now carries.

This is a defect in round 4's own fix. That round correctly identified that a one-shot driver cannot interleave with the orchestrator, and correctly split the run into phases; it did not check that the resulting phase labels partition the steps.

**Evidence.**
- Spec, Driver protocol — the phase enum is exactly `register`, `write`, `read`, `delete`, and `--phase write` is the invocation example.
- Spec, S1 step table — `*driver* write` appears in two non-adjacent rows with different steps.
- Spec, Driver protocol output block — `"phase": "write"` on a document listing all four phases' steps.

**Proposed fix.** Make the phase set partition the steps. Add `update` to the enum, so it reads `register`, `write`, `read`, `update`, `delete`, and change S1's second `*driver* write` row to `*driver* update`. Then relabel the example document to the phase whose steps it shows, or split it so the `"phase"` field and the `steps` array agree — one small document per phase is clearer than one combined block now that the field exists.

A narrower alternative, if growing the enum is unwelcome: fold the update into the `read` phase (`read` becomes "get, then update, then get again"), which keeps four phases but makes the label a poor description of what that phase does and puts a write inside a phase named for reads. The enum growth is the cleaner of the two, and the enum is a design-time constant rather than a runtime cost.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1** — no source for the re-registered shape, and re-registration would replace the descriptor under test. Resolved: full `TypeDescriptor` reported; authorization-only mutation; A17 records the gap.
- **Round 1 §2.2** — prefixed string row keys were not writable to a `UUID` column. Resolved: keys are UUIDs; A18 records the gap.
- **Round 1 §2.3** — the expected-failure set omitted `delete` and predicted a red first run. Resolved: re-based; fallback rule restated by cause.
- **Round 2 §2.1** — S1 declared a `one_to_many` no step read back. Resolved: the author-side depth-1 read is a step, and after round 4 it can actually execute.
- **Round 2 §3.1** — no recovery path when a driver's entity shape changes. Resolved by the user's pick of option (c); A19 records the failed assumption.
- **Round 3 §2.1** — the orchestrator could not address the rows drivers wrote. Resolved: `keys` map reported by logical name, authoritative.
- **Round 4 §2.1** — the one-shot driver could not interleave with the orchestrator, and S4's cross-reads were unaddressable. Resolved: phased invocation with `--keys`. The residual defect in that fix is §2.1 above.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**

One finding, and it is narrow: the phase enum does not partition S1's steps, so the update step is unreachable. The fix is one enum member and one table cell. Everything else in the phase model checks out on a full pass — the orchestrator's re-registration has a real slot before the first write, the depth reads land between `read` and `delete` against live rows, key collection precedes every cross-read, and S2, S3 and S4 each interact with phasing correctly, including S4's register-once rule which would otherwise let four drivers overwrite the descriptor under test.

Two rounds in a row have now found a defect in the previous round's fix rather than in original material. That is worth weighing when deciding how many more rounds to run before planning: the remaining findings are getting smaller and more localised to the last edit, which is the pattern of a spec approaching convergence rather than one hiding a structural problem.
