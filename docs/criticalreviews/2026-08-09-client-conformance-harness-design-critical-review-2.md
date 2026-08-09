# Critical Design Review: 2026-08-09-client-conformance-harness-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-09-client-conformance-harness-design.md`
**Verified Assumptions section:** present

Coverage was re-derived against the current spec before consulting round 1. The three round-1 fixes are single rows below, not the search area.

## 0. Coverage enumeration

### Sections

| Row | Disposition |
|---|---|
| Header / Depends on | dropped — the header still says Go and TypeScript "cannot pass this harness's read steps until that fix lands", which is now historical and sits in tension with the re-based Expected-failures section. Documentation inconsistency with no behavioural consequence; fails literal-wrongness |
| Problem | ok — the FK-only narrative matches the record; `Iverson.Clients/Common/testdata/*.json` and the .NET-only `Iverson.LoadTest` are as described |
| Contract | ok — "register a correct schema and round-trip an entity through create, read, update and delete **without corrupting a relation**" is the yardstick the rest of the review measures against |
| Architecture — file layout | ok — every parent path exists; the Java aggregator `pom.xml` takes a new module additively |
| Architecture — why an orchestrator | ok — `AuthentikFlowExecutorClient` is the single C# implementation of the flow-executor sequence |
| Driver protocol — invocation, `--out` | ok — stdout hazard (A14) avoided by the file |
| Driver protocol — reported `typeDescriptor` | ok — round 1's fix. **Checked the serialization boundary it introduces**: five independent proto libraries must produce JSON the .NET orchestrator can parse. ts-proto's generated `TypeDescriptor.toJSON` (`generated/object_mapping.ts:111-132`) emits camelCase keys and omits defaults — canonical proto3 JSON, which `JsonParser` accepts; Go `protojson`, Python `MessageToJson`, Java `JsonFormat` and .NET `JsonFormatter` are canonical by construction. Dropped as a finding: the natural path in every language is compatible, so no encoding clause is load-bearing |
| Driver protocol — four properties | ok — count now matches the bullets |
| Driver protocol — failed step is data, non-zero exit means the driver broke | ok |
| Registration and authorization are separate steps | ok — round 1's fix; the descriptor-replacement hazard is now stated and the rejected alternative recorded |
| S1 — type shape and step table | → §2.1 |
| S1 — depth-1 belongs to the orchestrator | ok — A6 stands; the per-type-descriptor reasoning is sound |
| S2 — naming-rejected | ok — Go `registrar.go:110-111`, TypeScript `core.ts:244-254`, Python per A10; the .NET/Java skip rationale holds |
| S3 — nav-property-rejected | ok — requires a registered type whose relation has `PropertyName != ForeignKey`, which round 1's fix now guarantees survives re-registration |
| S4 — interop | ok — UUIDv5 keys resolve round 1's §2.2; the 25-read matrix is the only cross-client check in the design |
| Isolation | → §3.1 (the type-name half, not the key half) |
| Verification — three-way comparison | ok — reconfirmed the Postgres leg is not vacuous (see §1, A16) |
| Verification — table naming | ok — `SchemaBuilder.cs:30` |
| Verification — registration assertions | ok — all three clauses now have operands in the reported `TypeDescriptor` |
| Reporting | ok — matrix, per-failure detail, `--json`, exit code, non-silent skips |
| Expected failures | ok — re-based in round 1; the cause-based rule (steps whose SQL casts the key) is correct, and `Update` is correctly excluded because it routes through the outbox upsert |
| Lifecycle — preflight, no compose management | ok — matches `Iverson.LoadTest`'s contract |
| Lifecycle — tenant provisioning duplicated | ok — A3 reconfirmed: `EnsureTenantProvisionedAsync` is a static local at `Program.cs:267`, called from `:95`; not reachable from another project |
| CI readiness | ok — **line-checked**: `AuthentikFlowExecutorClient.cs:146` is `cachedSecret ??= LoadCachedTotpSecret()`, so the env fallback really does land on one null-coalescing operator at exactly the cited line |
| Testing the harness | ok — both named mutations still bite: reverting Python's `_relation_property_name` reintroduces `PropertyName == ForeignKey`, and round 1's fix is what lets that collision survive re-registration to reach the depth-1 assertion |
| Consequences | ok — driver-maintenance cost, green-first-run (re-based), "will find more than it was built for" |
| Verified assumptions A1–A18 | see §1 |
| Known issues | ok — both live defects are now fixed and merged; the compose-management and CI-seeding exclusions stand |

### Rules and operands

| Rule | Over-inclusion | Under-inclusion | Disposition |
|---|---|---|---|
| **Registration verification** (`propertyName != foreignKey`; FK among declared properties; `isArray` only for `many_to_many`) | flags a conforming descriptor | passes a broken one | ok — all three operands now exist in the reported descriptor. Checked the under-inclusion direction against all five clients: .NET/Java report a distinct nav member, Go/Python/TS report the `Id`-stripped name |
| **Three-way agreement** | declares a defect where sources legitimately differ | agrees vacuously | ok — the vacuous direction re-checked: no `FORCE ROW LEVEL SECURITY` exists and the app connects as superuser, so the Postgres leg returns rows rather than silently empty |
| **Relation coverage** (which relation kinds the round-trip actually exercises) | asserts on a kind the types don't declare | declares a kind no step reads back | → §2.1 (under-inclusion) |
| **Isolation identity** (stable type names, derived UUID keys) | two runs collide | runs cannot be told apart | → §3.1 — the key half is now correct; the type-name half has no recovery path when a driver's entity shape changes |
| **Expected-failure predicate** (steps whose SQL casts the key) | marks a passing step xfail | leaves a known-failing step unmarked | ok — moot against current `main`, and correct as a fallback rule: read-by-key, depth read and delete cast; write and update do not |

### Data-flow arrows

| Arrow | Disposition |
|---|---|
| driver → `--out` JSON → orchestrator | **crosses a serialization boundary** — ok, see the `typeDescriptor` row above; the encoding is canonical in all five languages |
| driver's client → `RegisterSchema` | ok — this is the descriptor under test |
| orchestrator → re-`RegisterSchema` (driver's descriptor + authorization) | ok — round 1's fix; shape-preserving by construction |
| orchestrator → `RegisterSchema` → `ApplySchemaAsync(..., SchemaDriftPolicy.Throw)` | → §3.1 |
| orchestrator → `MappingGet(depth: 1)` on `{Lang}Article` | ok — resolves the `many_to_one` (`FetchByKeyAsync`) and `many_to_many` (`FetchManyByKeysAsync`) legs |
| **`{Lang}Author`'s `one_to_many` → never read back** | → §2.1 |
| orchestrator → Postgres `SELECT` | ok — table name derivable; RLS does not blind it |
| orchestrator → row deletion on completion | ok — `DeleteAsync`'s uuid cast is satisfied now that keys are UUIDs |
| driver write → UUID key column | ok — UUIDv5 derivation resolves round 1's §2.2 |

## 1. Verified-assumptions cross-check

All eighteen reconfirmed under a fresh read, including the three added in round 1.

- **A1** — `AuthentikFlowExecutorClient.cs:22` and `ActingUserTokenProvider.cs:3` are both `public sealed`.
- **A2 / A15** — unchanged.
- **A3** — ⚠️ PARTIAL reconfirmed: `EnsureTenantProvisionedAsync` is a static local at `Program.cs:267`.
- **A4** — line-checked this round: `AuthentikFlowExecutorClient.cs:146` is the `cachedSecret ??=` site, so the spec's "one null-coalescing operator" is literally accurate.
- **A5 / A9 / A13** — unchanged.
- **A6 / A7** — ❌ stand; the design's responses (orchestrator owns the depth read; orchestrator re-registers with permissions) are in place.
- **A8** — ❌ as recorded and now historical; the key-typing fix has merged, so both clients can declare `CLR_GUID`.
- **A10 / A11 / A12 / A14** — unchanged.
- **A16** — ✅ reconfirmed: `PostgresSchemaManager.cs:138-148` enables RLS with a `current_setting` policy, no `FORCE ROW LEVEL SECURITY` exists anywhere, and the runtime role is entered only inside scoped transactions (`IRecordStoreRoles.cs:52`).
- **A17** — ❌ stands: `SchemaType`/`SchemaField` carry no `tenant_field`.
- **A18** — ❌ stands: key columns are `UUID`.

### Span check — one uncovered dependency

**No assumption covers whether the harness can re-register its own types after their shape changes.** A12 verifies that re-registering an *identical* column shape is idempotent — which is the case the harness hits on a repeat run of unchanged drivers. Nothing covers the case the spec's own Consequences section says will occur: *"A change to any client's public API may require a driver change in that language."* A driver change that alters an entity's columns makes the next registration a *different* shape against a stable type name. Verified in-round: `SchemaRegistrationOrchestrator.cs:113` applies with `SchemaDriftPolicy.Throw` and `:115` converts `SchemaDriftException` into `FailedPrecondition`, and the service exposes no unregister or drop RPC. → §3.1.

## 2. Literal-wrongness findings

### §2.1 — S1 declares a `one_to_many` that no step ever reads back

**Description.** S1's type shape is *"`{Lang}Author` carrying a `one_to_many`, and `{Lang}Article` carrying a `many_to_one` to it plus a `many_to_many` to `{Lang}Tag`."* The step table then exercises only the article side: the depth-1 read is on `{Lang}Article`, which resolves the `many_to_one` (`FetchByKeyAsync`) and the `many_to_many` (`FetchManyByKeysAsync`). Nothing ever reads `{Lang}Author` at depth, so `EntityRelationResolver`'s `OneToMany` branch — the one that calls `FetchByColumnAsync` — is never executed for any client. The only `one_to_many` assertion in the design is the registration-time one, "`one_to_many` declares none", which checks the *absence* of a column.

Two things make this a break rather than a scope choice. First, the design itself contemplates the missing step: Expected failures reserves an xfail for *"any step exercising a `one_to_many` read"*, and Consequences did the same before it was re-based — language that only makes sense if such a step exists. Second, and more damning, `FetchByColumnAsync` is precisely the path that carried the defect motivating this whole line of work: the FK-only work shipped a broken `OneToMany` for Go, Python and TypeScript, and the spec's own Known issues names it. A harness built to this design would have been green straight through the defect it was written to catch, because the one direction that was broken is the one direction it never reads.

The Contract promises the round-trip happens "without corrupting a relation." With `one_to_many` declared but never resolved, that promise is not kept for one of the three relation kinds the design deliberately includes.

**Evidence.**
- Spec, S1 step table — the only depth-resolved read is *"orchestrator reads at depth 1"* against the article.
- `Iverson.Server/Iverson.Api/Grpc/EntityRelationResolver.cs:46-47` — `OneToMany` is the only kind routed to `ResolveOneToManyAsync`, and `:154` is its `FetchByColumnAsync` call, unreachable from an article-side read.
- Spec, Expected failures — "any step exercising a `one_to_many` read", with no such step in S1.
- Spec, Known issues — "One-to-many resolution is broken for Go, Python and TypeScript … `EntityRelationResolver:154` resolves the reverse direction through `FetchByColumnAsync`".

**Proposed fix.** Add one row to S1's step table, immediately after the existing depth-1 row:

> | *orchestrator* reads the author at depth 1 | `one_to_many` resolves: the article list hydrates from the reverse foreign-key lookup |

and extend the surrounding prose that explains why the depth step is the orchestrator's, since the same reasoning covers both reads. No new types, writes or driver work are needed — the author row and its article already exist by that point in the scenario, so this is one additional orchestrator-side read against data the scenario has already created.

## 3. Forced decisions

### §3.1 — How the harness recovers when a driver's entity shape changes

**The choice.** Stable type names plus `SchemaDriftPolicy.Throw` mean the harness cannot re-register a type whose columns have changed. The spec must pick how that is handled.

**Why it's forced.** Three facts collide, and the spec asserts the first as a benefit without reconciling it with the other two:

1. Isolation fixes type names deliberately: *"Type names stay stable so schema drift detection remains meaningful across runs."*
2. Registration applies with `SchemaDriftPolicy.Throw` (`SchemaRegistrationOrchestrator.cs:113`), converting drift into `FailedPrecondition` (`:115`). A changed column set against an existing table is rejected, not migrated.
3. The service exposes no unregister or drop RPC — `Get`, `Post`, `Update`, `Delete`, `RegisterSchema`, `GetSchema` are the whole surface (`object_mapping.proto:10-15`). `SchemaRegistry.UnregisterAsync` exists but has no production caller and no RPC in front of it. And Lifecycle rules out the harness managing the stack.

So the first time someone adds a property to a driver's entity — which Consequences says to expect, since *"a change to any client's public API may require a driver change"* — that language's register step fails on every subsequent run, for a reason unrelated to conformance, with no remedy inside the design. The failure also masks the rest of that language's row, since every later step depends on registration.

**The options.**

- **(a) Give the orchestrator a `--reset` that drops its own types.** It already holds a Postgres connection for the verification leg, so it can drop the table and delete the `_iverson_schema` row without new server surface. Cost: the harness mutates schema out-of-band, which is a second thing it owns beyond assertions, and a `--reset` that is wrong is destructive.
- **(b) Put the run id in the type names** (`{Lang}Article_run7a3f`). Cost: abandons the drift-detection property Isolation names as the reason for stable names, and leaves a table per type per run accumulating in the database.
- **(c) Accept it and document the manual remedy.** Cost: the harness is bricked for a language until someone runs SQL by hand, and the report shows a bare `FAIL` that reads like a conformance defect.

## 4. Previously addressed

- **Round 1 §2.1** — the orchestrator had no source for the shape it re-registered, and re-registration would have replaced the descriptor under test. Resolved: the driver now reports the full `TypeDescriptor` it sent, the orchestrator re-registers that with only the authorization block altered, and the descriptor-replacement hazard plus the rejected alternative are both recorded. A17 covers the gap.
- **Round 1 §2.2** — row keys of the form `shared-<lang>-<runid>` could not be written to a `UUID` key column. Resolved: keys are UUIDv5 values derived from `(runId, language, logicalName)`, with the human-readable mapping recorded in the report. A18 covers the gap.
- **Round 1 §2.3** — the expected-failure set omitted `delete` and the section predicted a red first run. Resolved: re-based on the now-merged key-typing fix, with the fallback rule restated by cause rather than step category.
- **Round 1 span check** — the RLS question is now A16.

## 5. Recommendation

🛑 **Surface forced decisions to user**

§3.1 needs your pick before planning: the drift/reset question determines whether the orchestrator gains schema-mutating responsibility, and that changes the component list. §2.1 is a one-row addition to S1 and can be applied alongside whichever option you choose — but it is the more consequential finding of the two, because it means the harness as specified would not have caught the defect that motivated it.

Everything else came back clean on a fresh pass, including two checks that could have gone the other way: the five-language JSON encoding that round 1's own fix introduced is canonical in every client, and the Postgres verification leg genuinely sees rows despite RLS.
