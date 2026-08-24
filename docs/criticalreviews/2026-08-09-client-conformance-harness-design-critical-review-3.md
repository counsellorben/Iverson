# Critical Design Review: 2026-08-09-client-conformance-harness-design (Round 3)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-09-client-conformance-harness-design.md`
**Verified Assumptions section:** present

Coverage re-derived against the current spec text before consulting rounds 1–2. This round's search concentrated on the arrows the two prior rounds' fixes altered, and on the surfaces neither round reached.

## 0. Coverage enumeration

### Sections

| Row | Disposition |
|---|---|
| Header / Depends on | dropped — still describes the key-typing fix as pending; historical, no behavioural consequence (same disposition as round 2) |
| Problem | ok — narrative matches the record |
| Contract | ok — the yardstick: register a correct schema and round-trip create/read/update/delete without corrupting a relation |
| Architecture — file layout, orchestrator rationale | ok — parent paths exist; the flow-executor sequence exists once, in C# |
| Driver protocol — invocation flags | ok — `--scenario`, `--type`, `--tenant`, `--grpc`, `--token`, `--acting-token`, `--id-prefix`, `--out`. **Checked what the driver receives against what it must produce**: the run id is the only identity input, so key generation is the driver's, not the orchestrator's — which is what makes the key arrow load-bearing (see §2.1) |
| Driver protocol — output document | → §2.1 |
| Driver protocol — four properties | ok — each is a real property of the shape; the `typeDescriptor` bullet added in round 1 carries the `is_array` operand the registration assertion needs |
| Driver protocol — failed step is data, non-zero exit means the driver broke | ok |
| Registration and authorization are separate steps | ok — round 1's fix; the descriptor-replacement hazard and the rejected alternative are both recorded |
| S1 — type shape and step table | ok — three relation kinds declared, and after round 2's addition all three are now read back: `many_to_one` and `many_to_many` via the article depth-1 read, `one_to_many` via the author depth-1 read |
| S1 — depth-1 reads belong to the orchestrator | ok — A6 stands; the added sentence covers both reads |
| S2 — naming-rejected | ok — Go `registrar.go:110-111`, TypeScript `core.ts:244-254`, Python per A10; all three raise client-side before any RPC |
| S3 — nav-property-rejected | ok — hand-built `Struct` over raw gRPC is the only way to produce a payload no client can emit |
| S4 — interop | ok — 5 languages × 5 rows = the stated 25 reads; the shared type is .NET-registered so it carries authorization rules, which is what makes it writable by the other four (A7) |
| Isolation | → §2.1 |
| Verification — three-way comparison | ok — localization logic sound; the Postgres leg is not blinded by RLS (A16) |
| Verification — table naming | ok — `SchemaBuilder.cs:30` |
| Verification — registration assertions | ok — all three clauses have operands in the reported `TypeDescriptor` |
| Reporting | ok — matrix, per-failure detail, `--json`, exit code, non-silent skips. The exit-code rule is consistent with the re-based Expected-failures section (no xfails against current `main`) |
| Expected failures | ok — re-based in round 1; the fallback cause-based rule is correct |
| Lifecycle | ok — preflight, no compose management, tenant provisioning duplicated per A3 |
| CI readiness | ok — line-checked in round 2: `AuthentikFlowExecutorClient.cs:146` is the `cachedSecret ??=` site |
| Testing the harness | ok — both named mutations still bite |
| Consequences | ok — driver-maintenance cost, green-first-run, "will find more than it was built for" |
| Verified assumptions A1–A19 | see §1 |
| Known issues | ok — the drift bullet added in round 2 matches the verified mechanism (`SchemaDriftPolicy.Throw`, no unregister RPC) |

### Rules and operands

| Rule | Over-inclusion | Under-inclusion | Disposition |
|---|---|---|---|
| **Key derivation** (UUIDv5 over run id, language, logical name) | two runs collide | the orchestrator addresses a key no driver wrote | → §2.1 (the second direction) |
| **Registration verification** | flags a conforming descriptor | passes a broken one | ok — all three clauses have operands; checked the under-inclusion direction across all five clients' reported descriptors |
| **Three-way agreement** | declares a defect where sources differ legitimately | agrees vacuously | ok — the vacuous direction re-checked; RLS does not empty the Postgres leg |
| **Relation coverage** | asserts on a kind the types don't declare | declares a kind no step reads | ok — resolved by round 2; all three kinds are now both declared and read |
| **Expected-failure predicate** | marks a passing step xfail | leaves a known-failing step unmarked | ok — moot against current `main`; correct as a fallback rule |

### Data-flow arrows

| Arrow | Disposition |
|---|---|
| driver → `--out` JSON → orchestrator (`typeDescriptor`) | **crosses a serialization boundary** — ok; canonical proto3 JSON in all five clients (ts-proto's generated `toJSON` verified in round 2) |
| driver → `--out` JSON → orchestrator (**row keys**) | **crosses a serialization boundary** — → §2.1 |
| orchestrator → `MappingGet(article, depth: 1)` | → §2.1 — the operation requires a key parameter the artifact does not supply |
| orchestrator → `MappingGet(author, depth: 1)` | → §2.1 — same, and this is a second key |
| orchestrator → Postgres `SELECT … WHERE key = …` | → §2.1 — same key dependency; the table name is derivable but the row identity is not |
| orchestrator → row deletion on completion | → §2.1 — same |
| orchestrator → re-`RegisterSchema` (driver's descriptor + authorization) | ok — round 1's fix; the descriptor is reported, so this arrow's parameters all exist |
| driver write → UUID key column | ok — UUIDv5 satisfies the column type |
| driver's client → `RegisterSchema` | ok — the descriptor under test |

## 1. Verified-assumptions cross-check

All nineteen reconfirmed under a fresh read. A16–A19 were added by rounds 1–2 and their cited evidence is unchanged: `PostgresSchemaManager.cs:138-148` plus the absence of `FORCE ROW LEVEL SECURITY` (A16); `SchemaType`/`SchemaField` carrying no `tenant_field` (A17); `SchemaBuilder.cs:163,236` and `RelationValidator.cs:88,110` (A18); `SchemaRegistrationOrchestrator.cs:113` and the six-RPC service surface (A19). A1–A15 are as recorded, with A6, A7, A8 still the three failures and their design responses in place.

### Span check — one uncovered dependency

**No assumption covers how the orchestrator learns which row keys exist.** A11 covers table naming and A18 covers the key's *type*, but nothing covers key *identity* — the fact that the orchestrator's four key-consuming operations need the same values the drivers wrote. Verified in-round: the invocation passes only `--id-prefix`, so keys are generated driver-side, and the reported document carries one `key` field for a step that writes three rows. → §2.1.

## 2. Literal-wrongness findings

### §2.1 — The orchestrator cannot address the rows the drivers wrote

**Description.** Four orchestrator operations take a row key: the depth-1 read of the article, the depth-1 read of the author (added in round 2), the Postgres verification query, and the deletion of rows on completion. Two things stop those keys from reaching it.

**(a) The reported document carries one key for three rows.** S1's write step is *"write author, tag, article with both FKs"* — three rows — and the driver protocol reports `{"name": "write", "ok": true, "key": "…"}`, a single scalar. Even reading it generously as "the article's key", the author's key is now required too and appears nowhere. The consuming operation's parameter does not exist in the artifact its stage reads.

**(b) The alternative source — re-deriving the key — requires six implementations to agree on an unspecified algorithm.** Isolation says keys are *"UUIDv5 values derived from the run id, the language and the row's logical name"*. UUIDv5 is a SHA-1 hash over a **namespace UUID** and a **name string**; the spec pins neither. Five drivers and the orchestrator would each choose a namespace constant and a concatenation format independently. Any divergence — a different namespace, `"run7a3f:python:author"` versus `"run7a3f-python-author"`, a different case convention — produces a different UUID, and every orchestrator read then addresses a key no driver wrote. The failure is silent in the worst way: `MappingGet` returns not-found, which S1's own delete step treats as the *success* condition, so a total key mismatch could read as a passing delete while every verification step fails for a reason the report attributes to the client.

This is the same class of defect round 1 found in the descriptor arrow — a consuming operation whose required parameter is absent from the artifact its stage reads — on the other field of the same document. Round 1 fixed the descriptor half and left the key half untouched; round 2's added read made the gap wider by requiring a second key.

**Evidence.**
- Spec, driver protocol output block — `{"name": "write", "ok": true, "key": "…"}`, one scalar.
- Spec, S1 step table — *"write author, tag, article with both FKs"*, three rows; two subsequent orchestrator reads (`reads at depth 1`, `reads the author at depth 1`).
- Spec, driver protocol invocation — the driver receives `--id-prefix` and no key material, so keys originate driver-side.
- Spec, Isolation — the derivation names its inputs but not the namespace UUID or the name-string format that UUIDv5 requires.
- Spec, Verification — the Postgres leg and the `MappingGet` leg are both per-entity, so both need the key.

**Proposed fix.** Make the reported keys authoritative and stop requiring cross-process derivation. Replace the write step's scalar with a map from logical name to the key actually used:

```json
{"name": "write", "ok": true, "keys": {"author": "…", "tag": "…", "article": "…"}}
```

and amend Isolation to say the driver derives or generates its keys however it likes, provided each is a UUID and the run id makes it collision-free across runs, with the reported map — not a shared algorithm — being what the orchestrator uses. That preserves the collision-freedom and human-traceability Isolation asks for, removes the six-way agreement requirement entirely, and costs the drivers nothing they are not already doing: they must know these keys to write the rows.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1** — the orchestrator had no source for the shape it re-registered, and re-registration would have replaced the descriptor under test. Resolved: drivers report the full `TypeDescriptor`; re-registration alters only the authorization block; A17 covers the gap.
- **Round 1 §2.2** — row keys of the form `shared-<lang>-<runid>` were not writable to a `UUID` column. Resolved: keys are UUIDs; A18 covers the gap. (The *identity* half of that mechanism is what §2.1 above addresses — the fix made the keys well-typed without making them knowable to the orchestrator.)
- **Round 1 §2.3** — the expected-failure set omitted `delete` and predicted a red first run. Resolved: re-based on the merged key-typing fix, with the fallback rule restated by cause.
- **Round 2 §2.1** — S1 declared a `one_to_many` that no step read back. Resolved: the author-side depth-1 read is now a step, and the orchestrator-owns-depth paragraph covers both reads.
- **Round 2 §3.1** — no recovery path when a driver's entity shape changes. Resolved by your pick of option (c): Known issues documents the manual remedy; A19 records the failed assumption.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**

One finding, and it is the last unexamined field of the document the design's two prior fixes both touched. The descriptor arrow was fixed in round 1 and the relation-coverage gap in round 2; the key arrow sat between them, load-bearing for four separate orchestrator operations, and became more load-bearing when round 2 added a second depth read. The proposed fix is a protocol change of one field and a sentence in Isolation, and it removes a cross-process agreement requirement rather than adding one.

Everything else came back clean on a fresh pass, including the surfaces the earlier rounds had already cleared and the two checks that could have gone the other way (canonical JSON across five languages; RLS not emptying the Postgres leg).
