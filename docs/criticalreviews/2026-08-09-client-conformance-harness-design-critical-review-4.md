# Critical Design Review: 2026-08-09-client-conformance-harness-design (Round 4)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-09-client-conformance-harness-design.md`
**Verified Assumptions section:** present

Coverage re-derived against the current spec before consulting rounds 1–3. This round's sweep put the driver's *execution model* — as opposed to its data shapes, which three rounds have now worked over — under the arrow discipline for the first time.

## 0. Coverage enumeration

### Sections

| Row | Disposition |
|---|---|
| Header / Depends on | dropped — still describes the key-typing fix as pending; historical, no behavioural consequence (same disposition as rounds 2–3) |
| Problem | ok — narrative matches the record |
| Contract | ok — the yardstick: register a correct schema and round-trip create/read/update/delete without corrupting a relation |
| Architecture — file layout, orchestrator rationale | ok — parent paths exist; the flow-executor sequence exists once, in C# |
| Architecture — `DriverRunner.cs` "builds and execs one driver, reads its JSON output" | → §2.1 — this is the sentence that fixes the execution model as one-shot |
| Driver protocol — "A driver is a subprocess with a fixed contract" | → §2.1 |
| Driver protocol — invocation flags | ok — no phase or key-input flag; the driver receives `--scenario` and runs it to completion |
| Driver protocol — output document | ok as a *shape*: `keys` map (round 3) and `typeDescriptor` (round 1) now carry what their consumers read. Its *timing* is §2.1 |
| Driver protocol — four properties | ok — each is a real property; the report-never-assert split is what keeps assertions in one place |
| Driver protocol — failed step is data, non-zero exit means the driver broke | ok — the distinction survives phasing unchanged |
| Registration and authorization are separate steps | ok — round 1's fix; descriptor-replacement hazard recorded |
| S1 — step table | → §2.1 |
| S1 — depth-1 reads belong to the orchestrator | ok as *ownership*; → §2.1 as *sequencing* |
| S2 — naming-rejected | ok — single-step negative scenario, no orchestrator interleaving, no cross-driver data. Unaffected by §2.1 |
| S3 — nav-property-rejected | ok — orchestrator-only, no driver involved at all |
| S4 — interop | → §2.1 |
| Isolation | ok — round 3's fix; keys are UUIDs, driver-chosen, reported by logical name |
| Verification — three-way comparison | → §2.1 — two of its three observations are taken after the driver exits |
| Verification — table naming | ok — `SchemaBuilder.cs:30` |
| Verification — registration assertions | ok — all three clauses have operands in the reported `TypeDescriptor` |
| Reporting | ok — matrix, per-failure detail, `--json`, exit code, non-silent skips |
| Expected failures | ok — re-based in round 1; fallback cause-based rule correct |
| Lifecycle | ok — preflight, no compose management, tenant provisioning duplicated per A3 |
| CI readiness | ok — line-checked in round 2 |
| Testing the harness | ok — both named mutations still bite, and both target assertions that survive §2.1's fix |
| Consequences | ok — driver-maintenance cost, green-first-run, "will find more than it was built for" |
| Verified assumptions A1–A19 | see §1 |
| Known issues | ok — drift bullet matches the verified mechanism |

### Rules and operands

| Rule | Over-inclusion | Under-inclusion | Disposition |
|---|---|---|---|
| **Three-way agreement** | declares a defect where sources differ legitimately | agrees vacuously | → §2.1 — the vacuous direction is now reachable for a new reason: two of the three sources observe a deleted row and agree on "absent" |
| **Registration verification** | flags a conforming descriptor | passes a broken one | ok — operands present; checked across all five clients' reported descriptors |
| **Key identity** | two runs collide | orchestrator addresses a key no driver wrote | ok — round 3's reported-map fix; driver-chosen UUIDs incorporating the run id |
| **Relation coverage** | asserts on an undeclared kind | declares a kind no step reads | ok — all three kinds declared and read after round 2 |
| **Expected-failure predicate** | marks a passing step xfail | leaves a known-failing step unmarked | ok — moot against current `main`; correct as a fallback |

### Data-flow arrows

| Arrow | Disposition |
|---|---|
| driver process → exit → orchestrator reads `--out` | **crosses a process boundary** — → §2.1. The orchestrator's earliest possible action is after the driver has run every step including `delete` |
| orchestrator → `MappingGet(article, depth: 1)` | → §2.1 — the row is deleted before this can run |
| orchestrator → `MappingGet(author, depth: 1)` | → §2.1 — same |
| orchestrator → Postgres `SELECT` for the three-way check | → §2.1 — same |
| driver B → reads row written by driver A (S4) | → §2.1 — driver A's key is reported to the orchestrator, not to driver B, and the invocation has no flag to pass it |
| driver → `--out` JSON → orchestrator (`typeDescriptor`) | ok — canonical proto3 JSON in all five clients; consumed after exit, which is fine for re-registration since that has no liveness dependency |
| driver → `--out` JSON → orchestrator (`keys` map) | ok as a shape (round 3); its consumers are the operations §2.1 blocks |
| orchestrator → re-`RegisterSchema` | ok — shape-preserving, no liveness dependency |
| orchestrator → row deletion on completion | ok — its own S3/S4 rows; unaffected |

## 1. Verified-assumptions cross-check

All nineteen reconfirmed under a fresh read. A1–A15 are as recorded with A6, A7 and A8 still the three failures and their design responses in place; A16–A19, added in rounds 1–2, have unchanged cited evidence (`PostgresSchemaManager.cs:138-148` and the absence of `FORCE ROW LEVEL SECURITY`; `SchemaType`/`SchemaField` carrying no `tenant_field`; `SchemaBuilder.cs:163,236` with `RelationValidator.cs:88,110`; `SchemaRegistrationOrchestrator.cs:113` with the six-RPC service surface).

### Span check — one uncovered dependency

**No assumption covers whether the orchestrator can act between a driver's steps.** Every listed assumption concerns a capability of the clients, the server, or the toolchain; none concerns the harness's own execution model. The design places two orchestrator operations *inside* a driver's step sequence and has a driver read rows another driver wrote, and nothing verifies that the subprocess contract permits either. Verified in-round from the spec's own text: the driver is one invocation that runs the scenario to completion and writes a single JSON document at the end. → §2.1.

## 2. Literal-wrongness findings

### §2.1 — The driver is a one-shot subprocess, but two scenarios require the orchestrator to act mid-sequence

**Description.** The driver contract is a single invocation: *"A driver is a subprocess with a fixed contract"*, invoked once per scenario, whose *"Output is one JSON document written to the `--out` path"*, and which `DriverRunner` *"builds and execs … and reads its JSON output"*. Every step runs inside that one process, and the orchestrator's earliest possible action is after it exits. Two places in the design require otherwise.

**(a) S1's orchestrator reads are sequenced before the driver's `delete`.** The step table places two orchestrator rows between the driver's `get` and `update` steps:

| … | |
|---|---|
| get article (depth 0) | *driver* |
| *orchestrator* reads at depth 1 | |
| *orchestrator* reads the author at depth 1 | |
| update title | *driver* |
| delete | *driver* |

The driver runs all five of its steps and exits. By the time the orchestrator can issue anything, `delete` has already removed the article — and S1's own delete step asserts *"row gone; a subsequent get reports not-found"*, so this is the intended end state, not an accident. Both depth-1 reads then return not-found. The design's marquee assertion — that a foreign key survives depth-resolved hydration, which is the exact defect that motivated the harness — cannot execute at all.

Verification inherits the same break: two of its three observations (the orchestrator's `MappingGet` and its Postgres query) are per-entity and taken after the driver exits, so they observe a deleted row. Worse than failing loudly, they would *agree* — both report the row absent — and the three-way agreement rule reads agreement as success.

**(b) S4 requires each driver to read rows other drivers wrote, with no way to learn their keys.** *"Every language writes one row under its own run-scoped UUID key, then every language reads all five rows."* Round 3 made each driver's keys driver-chosen and reported to the orchestrator; the invocation has no flag for passing keys *in*. Driver B therefore cannot address driver A's row, and the twenty-five-read matrix — the only cross-client check in the design, and the only scenario that catches two clients disagreeing while each passes its own isolated test — reduces to five self-reads.

Both instances share one cause: the design treats the driver as a co-routine the orchestrator can interleave with, while the protocol specifies a batch process.

**Evidence.**
- Spec, Architecture — `DriverRunner.cs   builds and execs one driver, reads its JSON output`.
- Spec, Driver protocol — one invocation per scenario; *"Output is one JSON document written to the `--out` path"*; the `steps` array lists all five driver steps in a single document.
- Spec, S1 step table — two `*orchestrator*` rows between `get` and `update`, with `delete` last.
- Spec, S1 delete row — *"row gone; a subsequent get reports not-found"*, confirming the row is intended to be absent once the driver finishes.
- Spec, S4 — *"then every language reads all five rows"*, against an invocation block carrying no key input.

**Proposed fix.** Make the driver contract phased: invoke it once per phase rather than once per scenario, with `--phase write | read | delete` and a `--keys <json>` input on phases after the first. The orchestrator then runs its own assertions between phases, which is what it was always described as doing, and can hand each driver the key map collected from every driver's write phase — which is exactly what S4's cross-read needs and what round 3's reported-map fix already produces. Each phase writes its own `--out` document; the report-never-assert property, the failed-step-is-data convention, and the non-zero-exit-means-broken rule all carry over unchanged.

If phasing is judged too large a change to the protocol, the narrower alternative is to move `delete` out of the driver entirely — the orchestrator already deletes its own rows on completion — which fixes (a) alone and leaves S4's cross-read still unimplementable. That trade should be made explicitly rather than by omission, because it silently drops the design's only cross-client scenario.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1** — the orchestrator had no source for the shape it re-registered, and re-registration would have replaced the descriptor under test. Resolved: drivers report the full `TypeDescriptor`; re-registration alters only the authorization block; A17 records the gap.
- **Round 1 §2.2** — row keys of the form `shared-<lang>-<runid>` were not writable to a `UUID` column. Resolved: keys are UUIDs; A18 records the gap.
- **Round 1 §2.3** — the expected-failure set omitted `delete` and predicted a red first run. Resolved: re-based on the merged key-typing fix, fallback rule restated by cause.
- **Round 2 §2.1** — S1 declared a `one_to_many` no step read back. Resolved: the author-side depth-1 read is now a step. (That step is one of the two §2.1 above cannot currently execute — the coverage gap is closed in the design; the sequencing that would let it run is not.)
- **Round 2 §3.1** — no recovery path when a driver's entity shape changes. Resolved by the user's pick of option (c): Known issues documents the manual remedy; A19 records the failed assumption.
- **Round 3 §2.1** — the orchestrator could not address the rows drivers wrote. Resolved: the write step reports a `keys` map by logical name and that map is authoritative, removing the six-way derivation-agreement requirement.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**

One finding, and it is the largest so far: the design's central assertion cannot execute, because the row it inspects is deleted by the same subprocess run that created it. Three rounds worked over the *shapes* the driver reports — descriptor, key type, key identity — and each fix was correct; none examined *when* the orchestrator can act relative to the driver, which is where this sits. The two instances share one cause and one fix.

Worth noting for the sequencing of the work: round 3's `keys`-map fix is what makes the phased fix cheap, since the key material the read phase needs is already being collected. Nothing else in the spec changed status this round.
