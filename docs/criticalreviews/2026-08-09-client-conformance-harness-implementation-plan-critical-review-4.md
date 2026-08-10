# Critical Implementation Review: 2026-08-09-client-conformance-harness-implementation-plan (Round 4)

**Plan:** `/home/ben/repositories/Iverson/docs/plans/2026-08-09-client-conformance-harness-implementation-plan.md`
**Verified plan-level assumptions section:** present

⚠️ 7 commits since plan-write time (SHA `ece0171`); cited file:line references re-checked under §1. All seven are this plan's own write/review/update cycle — no source drift.

Coverage re-derived against the current plan before consulting rounds 1–3. This round's sweep read the plan **per entity rather than per step** — S1 handles two entities, and every prior round's traversal followed the phase sequence, which is organised by step. It also gave Task 11's mutation claims a falsifiability pass against the server's actual registration validation.

## 0. Coverage enumeration

### Tasks × surfaces

| Task | Surface | Disposition |
|---|---|---|
| T1 | csproj block | ok — both project references and the two package versions re-resolved against `Iverson.LoadTest.csproj` |
| T1 | TOTP block | ok — env precedence, sole call site, `?? throw` reachable when both sources are empty |
| T1 | preflight prose | ok — three checks; each names the down service |
| T1 | TokenBroker prose | ok — env names and default match `Program.cs:27-47`; `GetSubAsync` exists at `ActingUserTokenProvider.cs:51` |
| T1 | report prose | ok — `xfail` is in the cell vocabulary and the spec's expected-fail set is empty against current `main`, so no task needs to populate it |
| T1 | `--keep` | dropped — no step implements deletion, but keys are run-scoped, so accumulation changes no verdict |
| T1 | commands | ok — `.slnx` build; Api test project path |
| T2 | phase-document block | ok — `StepResult` carries every consumed field |
| T2 | build/exec table | ok — five languages; tools match; `-pl conformance -am`; `--no-build` consistent with build-once |
| T2 | `--keys` shape prose | ok — language-qualified; passing it to the `write` phase is vacuous rather than wrong, since drivers choose their own keys there |
| T2 | toolchain-skip prose | ok — keyed on build-command failure |
| T2 | Reregistrar block | ok — rules shape matches `Program.cs:289-306` |
| T3 | model block | ok — FK convention, distinct nav name, `Guid[]` m2m, `OwnerId`, tenant field |
| T3 | auth-wiring prose | ok — public coordinator ctor over interceptor-carrying stubs |
| T3 | capture prose | ok — four builders non-public; interceptor is the .NET seam |
| T3 | phase-dispatch block | → §2.1 — the `read` phase covers one of S1's two verified entities |
| T4–T7 | model / wiring / capture prose | ok — Python `many_to_many` exported; TS `@IversonGuid()`; Go `MappingClient` interface; Java dual-header `CallCredentials` and channel interceptor |
| T4–T7 | commands | ok — pytest scoping, `npm test` two stages, Go module-root build, CodeQL-matching mvn |
| T8 | registration-assertion prose | ok — **checked against the server's own registration validation**: `SchemaRegistrationOrchestrator.cs:83-105` enforces FK-declared and FK-SQL-type but *not* `propertyName != foreignKey`, so the harness's m2o check is genuinely additional coverage rather than a restatement of a server guard |
| T8 | PostgresProbe prose | ok — `row_to_json` over property-named columns; superuser not RLS-blinded |
| T8 | three-way comparison prose | → §2.1 |
| T8 | S1 sequencing block | ok as an *order*; → §2.1 as *entity coverage* |
| T9 | S2 prose | ok — client-side rejection precedes any RPC; `--scenario`-selected so the misnamed type never reaches the server and cannot trip `SchemaDriftPolicy.Throw`; .NET/Java skip reason correct |
| T9 | S3 prose | ok — self-contained fixture, both headers, status-code assertion; all three preconditions now stated |
| T10 | Steps 1–2 | ok — same type names in five languages; register-once with its wholesale-replacement rationale |
| T10 | Step 3 (re-register) | ok — sits between the .NET register phase and any write phase, once rather than per language |
| T10 | Step 4 (write, cross-read) | ok — **checked the FK's referential requirement**: `ValidateSingleRelation` requires a well-formed GUID, not an existing row (`RelationValidator.cs:82-89`), so five article rows pointing at their own authors are writable; "agree on the foreign-key value" reads per row, which the "own run-scoped UUID key" clause makes explicit |
| T11 | mutation prose | ok — **falsifiability checked, not assumed**: reverting Python's relation-property-name helper yields `PropertyName == ForeignKey`, which registration does *not* reject (`SchemaRegistrationOrchestrator.cs:83-105`), so the descriptor is stored and the depth-1 read really is where it surfaces — the plan's stated red cell is the one that would actually go red |
| T11 | clean-tree commands | ok — five suites; restoration precedes them |

### Cross-task interface contracts

| Contract | Disposition |
|---|---|
| T2 `PhaseDocument` → T3–T7 | ok — each consumed field has a producing step |
| T2 build/exec table → T3–T7 | ok — all five defer to it |
| T1 `--owner-id` → written rows | ok — stamped on write and update |
| T1 tenant → `--tenant` | ok — acting user's own tenant |
| T3–T7 `keys` → T8 (S1) | ok — logical names unique within a document |
| T3–T7 `keys` → T2 union → T10 (S4) | ok — language-qualified |
| T3 `typeDescriptor` → T2 `Reregistrar` (serialization boundary) | ok — proto3 JSON both ways |
| T2 `Reregistrar` → S1 caller (T8) | ok — row in S1's step table |
| T2 `Reregistrar` → S4 caller (T10) | ok — Step 3 |
| T9 S3 → registered type + authorized caller | ok — established by S3's own steps |
| **T3–T7 driver `read` document → T8's three-way comparison, per entity** | → §2.1 — one row per *entity*, not per operation: the article has a driver leg, the author does not |
| T8 `Verifier` → T9, T10 | ok — defined before both consumers |

### Rule-like content, both directions

| Rule | Over-inclusion | Under-inclusion | Disposition |
|---|---|---|---|
| Three-way agreement | disagrees on spelling | **agrees on fewer than three legs** | → §2.1 (the under-inclusion direction, for the author entity) |
| Registration assertion (kind-scoped) | flags a conforming descriptor | passes a broken one | ok — and confirmed additional to the server's own registration guards |
| Key identity, within and across languages | collision | unaddressable row | ok |
| S3 expected-error predicate | passes on a different `InvalidArgument` | a different status arrives first | ok — status code now asserted alongside the message |
| S4 FK agreement | declares disagreement across distinct rows | agrees vacuously | ok — per-row comparison; five rows, five readers each |
| Toolchain-absent → skip | skips a present toolchain | fails on an absent one | ok |

## 1. Verified-plan-assumptions cross-check

Assumptions 1–39 re-read against cited evidence. **All 39 still hold.** Notes:

- **39** (added by the round-3 update) — reconfirmed independently: `ObjectMappingGrpcService.cs:292,294,298` in that order.
- **12, 17, 18** — the four registrars and four capture seams unchanged.
- **27** — the FK-on-member versus separate-FK-field split unchanged across the five sample models.

### Span check — one uncovered dependency

**Nothing covers which entities each driver's `read` phase actually returns.** Assumption 38 covers how the three legs are *keyed*; no assumption covers *which entities* the driver leg exists for. Verified in-round from the plan's own text: Task 3's `read` phase is `get article at depth 0` and reports one `entity`, while S1 verifies two entities. → §2.1.

## 2. Literal-wrongness findings

### §2.1 — The author entity has no driver leg, so its three-way comparison silently becomes a two-way one

**Description.** Task 8, Step 3 requires that "the driver's reported entity, the orchestrator's own `MappingGet`, and the Postgres row must agree", and the spec it implements says the orchestrator compares three independent observations **for each entity**. S1 verifies two entities: the article, whose `many_to_one` FK must survive depth-1 hydration, and the author, whose `one_to_many` must resolve through the reverse foreign-key lookup — the direction the foreign-key-only work actually broke, and the reason round 2 of the *spec's* review added that read at all.

The driver reads only one of them. Task 3's `read` phase is `get article at depth 0` reporting a single `entity`, and Tasks 4–7 mirror Task 3. For the author, the driver leg does not exist, so the comparison has two legs — the orchestrator's `MappingGet` and the Postgres row.

That matters because of what the two remaining legs share. Both read the same Postgres row: `MappingGet` returns `row_to_json` parsed into a `Struct`, and the probe queries the same table. The pair that is *missing* is exactly the one the plan's own localization rule calls out as isolating the client's read path — "driver versus gRPC". So for the author, the check cannot distinguish a client that drops the hydrated collection from one that returns it correctly, which is the defect class the `one_to_many` step exists to catch. The comparison still reports agreement, so the cell goes green.

An implementer following "for each entity … must agree" literally has no third leg to fetch and will either fail on a missing field or quietly degrade to two legs; neither outcome is stated, and the green one is the likelier.

**Evidence.**
- Plan, Task 3 Step 4 — the `read` phase is `get article at depth 0` → `steps: [get] with entity`; a single entity.
- Plan, Task 8 Step 4 — S1's sequencing has two orchestrator reads, `MappingGet(article, depth 1)` and `MappingGet(author, depth 1)`.
- Plan, Task 8 Step 3 — the three-way requirement and the "driver vs gRPC isolates the client's read path" localization rule.
- Spec, Verification — "For each entity the orchestrator compares three independent observations and requires agreement."
- `Iverson.Server/Iverson.Sql/EntityRepository.cs:7-9` — the gRPC and Postgres legs both derive from the same `row_to_json` over the same row, which is why losing the driver leg loses the only independent one.

**Proposed fix.** Make the entity coverage explicit rather than implied. Either add the author to the driver's `read` phase in Task 3 Step 4 — `get article at depth 0, get author at depth 0`, reporting two `entity` steps, mirrored by Tasks 4–7 — so both entities have three legs; or state in Task 8 Step 3 that the three-way comparison applies to the article, and the author is verified two-way (orchestrator `MappingGet` versus Postgres) with the reason recorded, so the weaker check is a decision rather than an accident. The first is a step per driver and preserves the design's stated verification strength for the relation direction that actually regressed; the second is one sentence and leaves a known gap in it.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1** — the registration assertion failed every conforming `many_to_many`. Resolved: kind-scoped with the `RelationValidator.cs:20-24` rationale.
- **Round 1 §2.2** — `--owner-id` produced and never written. Resolved: stamped on write and update across all five driver tasks.
- **Round 1 §2.3** — undefined build/exec commands. Resolved: the five-language table in Task 2 Step 2.
- **Round 1 §2.4** — the tenant was never pinned. Resolved: `IVERSON_LOADTEST_TENANT_ID`.
- **Round 2 §2.1** — the comparison compared differently-spelled documents. Resolved: named value set, separator-insensitive resolution, parsed UUIDs.
- **Round 2 §2.2** — S4's `--keys` union collapsed five rows into one. Resolved: language-qualified key space plus the iteration rule.
- **Round 3 §2.1** — S4's shared types were never re-registered with authorization. Resolved: Task 10 Step 3, once rather than per language.
- **Round 3 §2.2** — S3 asserted an error it could not reach. Resolved: own fixture, both headers, status-code assertion.
- **Rounds 1–3 span checks** — tenant claim, key spelling, write-path gate order. Resolved: assumptions 37, 38, 39.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**

One finding, and it is the first this cycle that the plan's *structure* hid rather than its detail: every prior round traversed the plan by step, and the gap only appears when you traverse by entity. S1 verifies two entities and the driver reads one, so the author's three-way check quietly loses the leg that isolates the client — for the `one_to_many` direction, which is the direction the work that motivated this harness actually broke, and the cell reports green either way.

The fix is a step per driver or a sentence in Task 8, depending on whether the weaker check is acceptable for the author; the review states both and does not pick, because both are defensible and the difference is real coverage rather than wording.

Nothing else moved. The three claims most worth re-checking rather than inheriting all held on evidence: the server's registration validation does *not* enforce `propertyName != foreignKey`, so the harness's m2o assertion is additional coverage and Task 11's stated mutation really does surface at the depth-1 read; relation FKs need a well-formed GUID rather than an existing row, so S4's five articles are writable; and S2's misnamed type never reaches the server, so it cannot trip the drift policy.
