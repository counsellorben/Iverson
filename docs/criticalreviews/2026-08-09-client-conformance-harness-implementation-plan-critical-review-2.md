# Critical Implementation Review: 2026-08-09-client-conformance-harness-implementation-plan (Round 2)

**Plan:** `/home/ben/repositories/Iverson/docs/plans/2026-08-09-client-conformance-harness-implementation-plan.md`
**Verified plan-level assumptions section:** present

⚠️ 3 commits since plan-write time (SHA `ece0171`); cited file:line references re-checked under §1. All three are this plan's own write/review/update cycle — no source drift.

Coverage re-derived against the current plan before consulting round 1. This round's sweep put the **verification mechanics** — how the three legs are actually compared, and how the key map is actually addressed across five drivers — under the contract discipline for the first time; round 1 checked that each leg had a source, not that the sources are comparable.

## 0. Coverage enumeration

### Tasks × surfaces

| Task | Surface | Disposition |
|---|---|---|
| T1 | csproj block | ok — versions and both `ProjectReference` paths re-resolved from the new directory |
| T1 | TOTP block | ok — nested conditional valid; env precedence ahead of the file read; sole call site's `?? throw` still reachable |
| T1 | preflight prose | ok — three named checks, each with a failure message naming the down service; no compose management |
| T1 | TokenBroker prose (incl. new tenant paragraph) | ok — `IVERSON_LOADTEST_TENANT_ID` is a real env read at `Program.cs:44` with the default the plan quotes; "ensure it exists rather than creating a harness-specific one" matches the `ListTenants`-then-`CreateTenant` shape |
| T1 | report prose | ok — cell vocabulary, reasoned skips, three observed values, exit-code rule all trace to the spec |
| T1 | commands | ok — `.slnx` build and the Api test project path |
| T2 | phase-document block | ok — `StepResult` carries every field the orchestrator reads; five-member phase enum partitions the steps |
| T2 | build/exec table | ok — each build command's tool matches the language; `dotnet run --no-build` is consistent with the "one build per run" rule; `-pl conformance -am` builds the client dependency first |
| T2 | toolchain-skip prose | ok — keys on the build command's own failure, so it degrades per-language as the spec requires |
| T2 | `--keys` fan-in prose | → §2.2 — the map's key space is not language-qualified |
| T2 | Reregistrar block | ok — `AuthorizationRules`/`RowPermission` members match `Program.cs:289-306`; **also checked the JSON round-trip**: all five printers emit proto3 JSON from the same shared proto, and `JsonParser.Default`'s unknown-field rejection is therefore not reachable |
| T3 | model block | ok — FK convention, distinct nav name, `Guid[]` for m2m; `OwnerId` present |
| T3 | auth-wiring prose | ok — public `EntityCoordinator<T>` ctor over interceptor-carrying stubs; `AddIversonClient`'s acting-user routing re-read |
| T3 | capture prose | ok — all four builders re-checked as non-public; interceptor is the .NET seam |
| T3 | phase-dispatch block (incl. new `OwnerId` clause) | ok — write and update both stamp `OwnerId`; failed-step-is-data rule stated; exec form delegated to T2's table |
| T4 | model / wiring / capture prose | ok — `many_to_many` is exported (`annotations.py:203`, `__init__.py:19`); one-constructor dual identity; wrapper stub accepted by `SchemaRegistrar(stub, *classes)` |
| T4 | commands | ok — pytest scoped to `tests` |
| T5 | tsconfig block | ok — `extends` + `outDir` override; include span makes the emitted layout match the driver's `../src/*.js` imports |
| T5 | model / wiring / capture prose | ok — `@IversonGuid()` on the key; `new SchemaRegistrar(...)` is exported; ts-proto `toJSON` for the descriptor |
| T5 | commands | ok — `npx tsc -p` and `npm test`'s two-stage script |
| T6 | model block | ok — `iverson_guid:"true"` on the key, relation tags on the FK members, `[]string` for m2m |
| T6 | wiring / capture prose | ok — `MappingClient` one-method interface; every coordinator method takes `ctx` |
| T6 | commands | ok — module-root build and test cover the new package |
| T7 | module/pom prose | ok — reactor `<modules>`, sample pom as template, `protobuf-java-util` compile-scope re-declaration, shade for `java -jar` |
| T7 | credentials / capture prose | ok — `CallCredentials` applied to all four stubs; `ManagedChannelBuilder.intercept` is the only public seam given the package-private `mappingStub` |
| T7 | commands | ok — matches the CodeQL workflow's own invocation |
| T8 | registration-assertion prose (incl. new m2m exemption) | ok — kind-scoped form now matches `RelationValidator.cs:20-24`; the m2m clause asserts FK-declared-with-`isArray`, which is what the clients actually emit |
| T8 | PostgresProbe prose | ok — **checked the SQL, not just the table rule**: `row_to_json(t)` over a table whose columns keep the descriptor's property names (`SchemaBuilder.cs:52-54`), so the Postgres leg's keys are the server's property names |
| T8 | three-way comparison prose | → §2.1 |
| T8 | S1 sequencing block | ok — orchestrator rows sit between `read` and `update`; matches the spec's phase model |
| T9 | S2 prose | ok — `register`-phase-only and `--scenario`-selected, so the misnamed type cannot abort S1's registration |
| T9 | S3 prose | ok — `MappingWriteRequest` is the right message; `RelationValidator.cs:49-60` produces the asserted error |
| T10 | register-once prose | ok — stated as a step with its wholesale-replacement rationale |
| T10 | `--keys` fan-out prose | → §2.2 |
| T11 | mutation prose | ok — names the spec's two mutations, extends to every assertion the plan adds, states the green-mutation rule |
| T11 | clean-tree commands | ok — all five suites verified |

### Cross-task interface contracts

| Contract | Disposition |
|---|---|
| T2 `PhaseDocument` → T3–T7 emit it | ok — every consumed field has a producing step in each driver task |
| T2 build/exec table → T3–T7 consume it | ok — round 1's gap is closed; each driver task now points at the table rather than defining its own |
| T1 `GetOwnerIdAsync()` → `--owner-id` → written rows | ok — the write and update phases now stamp it |
| T1 tenant → `--tenant` → server-side scoping | ok — pinned to the acting user's own tenant |
| T3–T7 `keys` → T8 orchestrator reads/deletes (S1, per-language) | ok — within one language the logical names are unique and the orchestrator reads that language's own document |
| T3–T7 `keys` → T2 `--keys` union → T10 cross-reads (S4, five languages) | → §2.2 — **the same operation with a second caller**: S1's consumer reads one driver's map, S4's reads a union of five, and the union's key space is not the same key space |
| T3 register `typeDescriptor` → T2 `Reregistrar` (serialization boundary) | ok — proto3 JSON in all five, parsed back into the same message before the authorization-only mutation |
| T8 `Verifier` → T9, T10 | ok — registration assertions and the comparison are defined in T8 before either consumer |

### Rule-like content, both directions

| Rule | Over-inclusion | Under-inclusion | Disposition |
|---|---|---|---|
| Three-way agreement | declares a defect where the sources merely *spell* things differently | agrees vacuously | → §2.1 — the over-inclusion direction fires on every comparison |
| Registration assertion (kind-scoped) | flags a conforming descriptor | passes a broken one | ok — m2m exemption matches the server; m2o/o2o retain the strict check, which is the defect class the harness exists for |
| Key identity within a language | two runs collide | orchestrator addresses an unwritten key | ok — run id in every key; reported map authoritative |
| Key identity across languages (S4) | two languages' entries collide in one map | a driver cannot address another's row | → §2.2 (both directions, same defect) |
| `isArray` only for m2m | flags a conforming array FK | misses a wrongly-arrayed FK | ok — re-checked all three FK-on-member clients |
| Toolchain-absent → skip | skips a present toolchain | fails on an absent one | ok — keyed on build-command failure |

## 1. Verified-plan-assumptions cross-check

Assumptions 1–37 re-read against cited evidence. **All 37 still hold.** Notes on the ones the round-1 fixes touched:

- **37** (added by the round-1 update) — reconfirmed independently: `ObjectRetrievalGrpcService.cs:34-38` passes the claim as the fetch's tenant, and `RowFieldAuthorizationEvaluator.cs:18-22` short-circuits to `Denied` on an empty claim.
- **12, 17, 18** — re-read all four registrars and all four seams; unchanged.
- **24** — `AuthorizationRules`/`RowPermission` member names unchanged at `Program.cs:289-306`.
- **11** — the six-RPC surface and `MappingWriteRequest` unchanged.

### Span check — one uncovered dependency

**Nothing covers the key-name spelling of the three verification legs.** Assumption 11 covers table naming and 25 covers descriptor serialization; none covers what the driver's reported `entity` is keyed by relative to the server's `MappingGet` payload and the Postgres row. Verified in-round: the Postgres leg's keys are the descriptor's property names (`row_to_json` over columns built as `new ColumnDescriptor(prop.Name, …)`), the gRPC leg's are the same, and the driver leg's are whatever that language's own object serializes to. → §2.1.

## 2. Literal-wrongness findings

### §2.1 — The three-way comparison compares documents whose keys are spelled differently in every language, so it can never agree

**Description.** Task 8, Step 3 requires that "the driver's reported entity, the orchestrator's own `MappingGet`, and the Postgres row must agree", and the plan states nothing about how the three are aligned. Two of the three legs share a key space and the third does not.

The Postgres leg is `row_to_json(t)` over a table whose columns are created from the descriptor's property names verbatim — `BuildDescriptor` builds each column as `new ColumnDescriptor(prop.Name, …)`, and only the *table* name is snake-cased. The `MappingGet` leg is that same row JSON parsed into a `Struct`. So both carry the server's property names: `PyAuthorId`, `GoAuthorId`, `DotNetAuthorId`.

The driver leg is explicitly *not* that shape. The spec defines it as "the driver's own typed object after deserialization, serialized to JSON" — which is the point of the leg, since it is the only way to catch a read path that finds the right key and drops the value. That object is keyed by the language's own member naming: Python's `py_author_id`, Go's exported `GoAuthorId`, TypeScript's `pyAuthorId`-style camelCase, and the spec's own protocol example shows exactly that (`"entity": {"id": "…", "pyAuthorId": "c60c…"}`). A comparison that requires the three documents to agree therefore reports disagreement between the driver leg and the other two on every entity, in every language, on a fully conforming stack.

Worse than a flat failure: the plan's own localization rule reads "driver versus gRPC isolates the client's read path", so a universal spelling mismatch renders as *"the client's read path is broken"* for all five clients — the harness's most confident-looking verdict, produced by the harness's own omission.

**Evidence.**
- `Iverson.Server/Iverson.Sql/EntityRepository.cs:7-9` — `SELECT row_to_json(t)::text FROM "<table>" t WHERE "<KeyColumn>" = @Key::uuid`; keys are the quoted column names.
- `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs:30,52-54` — only the table name is snake-cased; columns are `new ColumnDescriptor(prop.Name, …)`.
- Spec, Driver protocol — `entity` is the driver's own typed object, and the example document is keyed `pyAuthorId`.
- Plan, Task 8 Step 3 — states the agreement requirement and the localization rule, with no alignment rule.

**Proposed fix.** State the comparison's normalization in Task 8, Step 3: the Verifier compares a small set of named values — the key and each relation's foreign key — not whole documents, resolving each leg's field by case-insensitive match with separator characters removed (so `py_author_id`, `pyAuthorId` and `PyAuthorId` resolve alike), and comparing UUID values parsed rather than as strings. One sentence; it also makes the "three observed values" the report prints well-defined.

### §2.2 — S4's `--keys` union collapses five drivers' rows into one, because the map is keyed by logical name only

**Description.** Task 2, Step 2 defines the `--keys` input as "the accumulated logical-name-to-key map", fed by "every driver's `write` phase output". Task 10, Step 3 then has the orchestrator "collect all five `keys` maps from the write phase and hand the union to every driver's `read` phase".

Those two statements are incompatible. The logical names are fixed by Task 3 and mirrored by Tasks 4–7 — in S4 every driver writes the same logical entity, so all five report the same key name with a different UUID. A union over a logical-name-keyed map keeps one entry per name, so four of the five keys are discarded, and no driver can name which language's row it is being asked to read. The twenty-five-read matrix reduces to five reads of whichever driver happened to win the merge — and S4 is the only scenario in the design that can catch two clients disagreeing about the wire format while each passes its own isolated test, which is precisely the coverage that silently disappears.

This is the same `keys` map contract round 1 confirmed for S1, at its **second call site**: S1's consumer reads one driver's own document, where logical names are unique; S4's consumer reads a cross-driver union, where they are not. Verifying the contract at the first call site said nothing about the second.

**Evidence.**
- Plan, Task 2 Step 2 — "`--keys <json>`, the accumulated logical-name-to-key map; every driver's `write` phase output feeds it".
- Plan, Task 10 Step 3 — "the orchestrator collects all five `keys` maps from the write phase and hands the union to every driver's `read` phase".
- Plan, Task 3 Step 4 — fixes the logical names (`author`, `tag`, `article`) that Tasks 4–7 mirror.
- Spec, S4 — "every language writes one row under its own run-scoped UUID key, then every language reads all five rows", and the orchestrator asserts all twenty-five reads agree.

**Proposed fix.** Qualify the key space by language in Task 2, Step 2: `--keys` carries `{"<language>": {"<logical name>": "<uuid>"}}`, and the driver's own `write` document keeps reporting the inner map unchanged (the orchestrator adds the language, which it already knows because it invoked the driver). Then say in Task 10, Step 3 that each driver's `read` phase iterates the five language entries for `shared_article`, which is what produces twenty-five reads rather than five.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1** — the registration assertion failed every conforming `many_to_many`. Resolved: Task 8 Step 1 is now kind-scoped, with the m2m exemption and its `RelationValidator.cs:20-24` rationale recorded so a later round doesn't restore the strict form.
- **Round 1 §2.2** — `--owner-id` was produced and never written. Resolved: the write and update phases stamp `OwnerId`, and the rule is stated as part of the shape Tasks 4–7 mirror.
- **Round 1 §2.3** — `DriverRunner` consumed build/exec commands no driver task defined. Resolved: the table lives in Task 2 Step 2 with all five languages, and Tasks 3–7 defer to it.
- **Round 1 §2.4** — the tenant was never pinned. Resolved: the harness runs in the acting user's own tenant via `IVERSON_LOADTEST_TENANT_ID`, with the dedicated-tenant alternative named and explicitly not taken.
- **Round 1 span check** — the tenant-claim dependency was uncovered. Resolved: assumption 37.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**

Two findings, both in the harness's verification mechanics rather than its plumbing, and both invisible until the sources are compared rather than merely sourced. §2.1 makes every S1 comparison disagree on a conforming stack and attributes the disagreement to the client's read path; §2.2 silently reduces S4 from twenty-five cross-language reads to five self-reads, removing the design's only cross-client check while the cell still reports green.

Both fixes are local — one normalization sentence in Task 8, one key-space qualification in Task 2 plus the iteration rule in Task 10 — and neither reshapes a task.

Round 1's four fixes all hold up on a fresh read, and the two that were most likely to have been applied too narrowly were not: the m2m exemption is scoped by relation kind rather than blanket-removed, and the `OwnerId` rule propagated to all five driver tasks rather than only to Task 3.
