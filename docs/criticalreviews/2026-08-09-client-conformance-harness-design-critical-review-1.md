# Critical Design Review: 2026-08-09-client-conformance-harness-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-09-client-conformance-harness-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

### Sections

| Row | Disposition |
|---|---|
| Header / Depends on | ok — `2026-08-09-relation-key-typing-design.md` exists and has since been implemented and merged (`main@b67458d`), so the dependency is now satisfied rather than pending |
| Problem | ok — `Iverson.Clients/Common/testdata/*.json` golden fixtures exist; `Iverson.LoadTest` is .NET-only and latency-oriented; the FK-only defect narrative matches the record |
| Contract | ok — scope is one question per client, on demand, not a benchmark |
| Architecture — file layout | ok — every parent path exists (`Iverson.Server/`, the four client trees); the Java `pom.xml` is a real aggregator at `Iverson.Clients/Java/pom.xml`, so a `conformance` module is additive |
| Architecture — why an orchestrator | ok — the flow-executor sequence (identification → password → TOTP → PKCE → code exchange) exists once, in `Iverson.LoadTest/Auth/AuthentikFlowExecutorClient.cs`; five native suites would restate it |
| Driver protocol — invocation and `--out` | ok — A14's stdout hazard is real and the file-based output avoids it |
| Driver protocol — reported descriptor shape | → §2.1 |
| Driver protocol — "drivers report, never assert" | ok — consistent with Verification owning assertions |
| Driver protocol — `entity` is the driver's own typed object | ok — this is the only shape that catches a read path that finds the key and drops the value |
| Driver protocol — failed step is data, non-zero exit means the driver broke | ok — clean separation; the report can distinguish them |
| Registration and authorization are separate steps | → §2.1 |
| S1 — crud-roundtrip | → §2.3 (step coverage); rest ok — the six steps map to real client capabilities per A5 |
| S1 — depth-1 belongs to the orchestrator | ok — A6 is correct that only .NET exposes a depth parameter, and the reasoning that the collision lives in the per-type descriptor is sound. But see §2.1: the re-registration step disturbs exactly this |
| S2 — naming-rejected | ok — Go enforces at `registrar.go:110-111`, TypeScript at `core.ts:244-254`, Python equivalently; all raise client-side before any RPC (A10 observed live). The .NET/Java skip rationale holds — their FK is a separate declared field |
| S3 — nav-property-rejected | ok — no client can emit a nav property post-FK-only, so hand-building the `Struct` over raw gRPC is the only way to exercise the server guard |
| S4 — interop | → §2.2 |
| Isolation | → §2.2 |
| Verification — three-way comparison | ok — the localization logic (which pair disagrees names the layer) is correct and is the design's strongest idea |
| Verification — table naming needs no configuration | ok — `SchemaBuilder.cs:30` `TypeName.ToSnakeCase() + "s"` |
| Verification — registration assertions | → §2.1 (the `isArray` assertion has no data behind it) |
| Reporting | ok — matrix, per-failure detail, `--json`, exit code, non-silent skips |
| Expected failures | → §2.3 |
| Lifecycle — assumes stack is running, preflight, no compose management | ok — matches `Iverson.LoadTest`'s existing contract |
| Lifecycle — tenant provisioning duplicated | ok — A3 verified `EnsureTenantProvisionedAsync` is a static local in `Program.cs`; duplicating ~10 lines is the smaller change |
| CI readiness | ok — `AuthentikFlowExecutorClient.cs:146` `LoadCachedTotpSecret()` is the single read point (A4), so the env fallback really is one operator |
| Testing the harness | ok — the mutation requirement is stated concretely (revert Python's relation-property helper; stub Go's slice branch) with evidence going in the report |
| Consequences | ok — five drivers, red first run, "will find more than it was built for" all follow from the design |
| Verified assumptions A1–A15 | see §1 |
| Known issues | ok — both live defects are real, were split into the key-typing spec, and have since been fixed and merged |

### Rules and operands

| Rule | Over-inclusion | Under-inclusion | Disposition |
|---|---|---|---|
| **Registration verification** (`propertyName != foreignKey`; FK among declared properties; `isArray` only for `many_to_many`) | flags a conforming descriptor | passes a broken one | → §2.1 — the third clause has no operand in the reported data |
| **Three-way agreement** | declares a defect where sources legitimately differ | agrees vacuously | ok — checked the vacuous direction: the Postgres leg could silently return zero rows for every entity if RLS bit, which would make "all three agree" meaningless. It does not: `PostgresSchemaManager.cs:138-148` creates the tenant policy and `ENABLE ROW LEVEL SECURITY`, but there is **no `FORCE ROW LEVEL SECURITY`** anywhere in the tree, and the app connects as superuser, switching to `iverson_runtime` only inside scoped transactions (`IRecordStoreRoles.cs:52`). A superuser connection bypasses RLS, so the orchestrator's direct query sees rows. Load-bearing and verified rather than assumed |
| **Exit code** (0 only when every non-skipped, non-xfail cell passed) | red on a known-broken cell | green over a real failure | → §2.3 — the xfail set is under-specified, so the first run reports failures the design says should not occur |
| **S2 applicability** (Go/Python/TS only) | runs where inapplicable | skips where applicable | ok — .NET and Java declare the FK as a separate field, so no client-side member-name rule exists to violate; the server's registration check is the governing gate for them |
| **Isolation identity** (type names stable, row keys prefixed) | two runs collide | runs cannot be told apart | → §2.2 — the key half is unimplementable against the dependency this spec declares |

### Data-flow arrows

| Arrow | Disposition |
|---|---|
| driver → `--out` JSON file → orchestrator | **crosses a serialization boundary** — → §2.1. The persisted shape (relation triples plus a bare list of property *names*) is strictly smaller than what two consuming operations need |
| driver's client → `RegisterSchema` RPC → server | ok — this is the descriptor under test, exactly as intended |
| orchestrator → re-`RegisterSchema` with row permissions | → §2.1 |
| orchestrator → `MappingGet(depth: 1)` | ok — `EntityCoordinator.GetMappedAsync(key, depth)` exists in .NET; the orchestrator can also call the stub directly |
| orchestrator → Postgres `SELECT` | ok — table name derivable (`SchemaBuilder.cs:30`); RLS does not blind it (see the three-way rule above); the connection string is the same one the preflight already needs |
| driver write → row in a UUID key column | → §2.2 |
| orchestrator → `TenantLifecycle` for provisioning | ok — the service is reachable; A3's partial result is handled by duplicating the ten lines |
| `xfail` marks → report cells → exit code | → §2.3 |

## 1. Verified-assumptions cross-check

All fifteen reconfirmed under a fresh read; the three failures (A6, A7, A8) remain failures and the design's responses to them are the ones recorded.

- **A1/A2/A15** — `AuthentikFlowExecutorClient` and `ActingUserTokenProvider` are `public sealed` in `Iverson.LoadTest`; referencing an Exe project is legal; no dependents affected.
- **A3** — ⚠️ PARTIAL stands: `EnsureTenantProvisionedAsync` is a static local method in `Program.cs`.
- **A4** — `AuthentikFlowExecutorClient.cs:146` `LoadCachedTotpSecret()` is the single read point.
- **A5/A9/A13** — client surfaces and toolchains as recorded.
- **A6** — ❌ stands: only .NET takes a depth parameter.
- **A7** — ❌ stands: `RowFieldAuthorizationEvaluator.cs:11-12` returns Denied on null rules, and only .NET's registrar accepts `authorizationByTypeName`.
- **A8** — ❌ as written, but **now historical**: the key-typing work has since landed on `main@b67458d`, giving Go an `iverson_guid:"true"` tag and TypeScript an `@IversonGuid()` decorator. Both clients can now declare `CLR_GUID`. This does not invalidate the assumption as recorded; it changes what the harness will observe on its first run (see §2.3).
- **A10** — client-side naming rejection confirmed in all three clients.
- **A11** — `SchemaBuilder.cs:30`.
- **A12** — idempotent re-registration of an identical *column* shape; note this says nothing about descriptor replacement (see §2.1).
- **A14** — ⚠️ RISK stands; the `--out` file is the mitigation.

### Span check — three uncovered dependencies

1. **No assumption covers whether the orchestrator can obtain a registerable `TypeDescriptor` for a type a driver registered.** A7 establishes *why* re-registration is needed and A12 that it is idempotent, but nothing verifies the orchestrator has the shape to re-register. Verified in-round: it does not. → §2.1.
2. **No assumption covers the key format the harness's row keys must satisfy.** A11 covers table naming; nothing covers what a *key value* may be. Verified in-round: post-key-typing the key column is `UUID`, so the spec's prefixed string keys are not writable. → §2.2.
3. **No assumption covers whether the orchestrator's direct Postgres query can see rows under RLS.** Verified in-round and it can — no `FORCE ROW LEVEL SECURITY` exists and the app's connection is superuser (`PostgresSchemaManager.cs:138-148`, `IRecordStoreRoles.cs:52`). Recorded here so §5 does not read ✅ over an unverified load-bearing fact.

## 2. Literal-wrongness findings

### §2.1 — The orchestrator has no source for the shape it re-registers, and re-registering replaces the descriptor under test

**Description.** The design's central mechanism for working around A7 is: *"The driver registers via its own client — that descriptor is what is under test — and the orchestrator then re-registers the same shape with row permissions attached."* Two things make this impossible as specified.

**(a) The orchestrator is never given the shape.** The driver protocol has it report:

```json
"descriptor": {"relations": [{"propertyName": "…", "foreignKey": "…", "kind": "…"}],
               "properties": ["Id", "Title", "TenantId", "PyAuthorId"]}
```

`properties` is a bare list of *names*. A `RegisterSchema` call needs a full `TypeDescriptor`: per-property `clr_type`, `is_key`, `is_nullable`, `is_array`, plus `tenant_field`, which the server treats as mandatory and rejects when absent (`SchemaRegistrationOrchestrator.cs:61-64`). None of that is in the reported document. `GetSchema` is not an escape hatch either: `SchemaType` carries only `name`, `description`, `fields`, `relations`, and `SchemaField` has no tenant marker, no chunk parameters and no vector dimension — a `TypeDescriptor` cannot be reconstructed from it.

**(b) If the orchestrator registers anything, it overwrites the descriptor under test.** `SchemaRegistry.RegisterAsync` does `_schemas[descriptor.TypeName] = descriptor` and upserts the serialized JSON (`SchemaRegistry.cs:47-56`) — a wholesale replacement, not a merge. So whatever shape the orchestrator invents to attach permissions to *becomes* the registered descriptor. That destroys the object S1's marquee assertion exists to inspect: the depth-1 check is designed to catch a `PropertyName == ForeignKey` collision **in the client's registered descriptor**, and by the time it runs, the descriptor is the orchestrator's. A client that registers a colliding descriptor would be silently corrected before the check that looks for the collision.

The same gap defeats one third of the registration assertion: Verification requires that *"`isArray` is set only for `many_to_many`"*, but `isArray` appears nowhere in the reported descriptor.

**Evidence.**
- `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs:47-56` — `RegisterAsync` upserts and replaces `_schemas[TypeName]`.
- `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:61-64` — `tenant_field` is required; absent means `InvalidArgument`.
- `Iverson.Clients/Common/Proto/object_mapping.proto:124-145` — `SchemaType` / `SchemaField`; no tenant field, no chunk or vector parameters.
- The spec's own driver-protocol JSON block, above.

**Proposed fix.** Have the driver report the **full `TypeDescriptor` it sent**, serialized — every client builds one before calling `RegisterSchema`, so this is a serialization, not new logic. The orchestrator then re-registers *that exact descriptor* with an `authorization` block attached and nothing else changed, which makes the re-registration genuinely shape-preserving, keeps the client's relation descriptor intact for the depth-1 check, and supplies the `isArray` operand the registration assertion needs. State explicitly in the spec that the re-registration must alter only the authorization block.

An alternative worth naming: give the four non-.NET registrars an authorization parameter, deleting the double-registration entirely. That is a larger change to production clients, and this harness is meant to test them rather than reshape them — but it removes a whole class of harness-only machinery, so it deserves to be a considered-and-rejected note rather than an unexamined omission.

### §2.2 — The isolation scheme's row keys cannot be written to a UUID key column

**Description.** Isolation specifies *"row keys are prefixed so runs never collide"*, and S4 makes it concrete: *"Every language writes one row keyed `shared-<lang>-<runid>`"*. This spec declares a dependency on the key-typing design, which has since landed: a key column's SQL type must be `UUID`, enforced at registration. `shared-go-run7a3f` is not a UUID, so the write fails on insert with Postgres `22P02 invalid input syntax for type uuid`. Every scenario is affected, not just S4 — S1's per-language rows use the same prefixing scheme, and any relation foreign key built from such a key additionally fails `RelationValidator`, which requires a well-formed non-empty GUID.

The harness's very first action for every language therefore fails for a reason unrelated to conformance, and the failure is not one of the expected failures the design predicts.

**Evidence.**
- `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs:163` — `KeyColumn = new ColumnDescriptor(keyProp.Name, ClrTypeToSql(keyProp.ClrType, false), false)`; `:236` maps `ClrGuid` → `"UUID"`.
- `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` — the key guard now rejects any non-`UUID` key column outright, so a text-keyed workaround cannot register either.
- `docs/specs/2026-08-09-relation-key-typing-design.md`, Contract — "A key column and a relation foreign-key column are UUID."
- `Iverson.Server/Iverson.Api/Validation/RelationValidator.cs:88,110` — foreign-key values must be well-formed GUIDs.

**Proposed fix.** Keep the run id, move it out of the key. Derive each row key as a deterministic UUIDv5 over `(runId, language, logicalName)` so keys stay reproducible, collision-free across runs, and valid UUIDs; record the mapping from logical name to generated UUID in the report so a failure is still traceable to `shared-go-run7a3f` in human terms. Type names, which are not keys, keep the stable-naming rule the spec already gives them.

### §2.3 — The expected-failure set omits `delete`, so the design's predicted first run is wrong

**Description.** Expected failures says Go and TypeScript are marked *"expected-fail on every read step"* of S1 and S4. Pre-fix, their key columns are `TEXT`, and the uuid cast that breaks reads is not confined to reads: `EntityRepository.DeleteAsync` casts `@Key::uuid` too, and S1's delete step routes through it (`ObjectMappingGrpcService` calls `_entities.DeleteAsync`). So S1's `delete` — explicitly not a read step — also fails for those two clients, unmarked. Consequences compounds the error by restating the prediction as *"Go and TypeScript fail every read step"*.

The effect is that the design's stated first-run outcome does not match what the harness would print: cells the design says should pass come back FAIL with no recorded cause, which is precisely the "known versus new breakage" distinction the xfail mechanism exists to preserve.

Worth noting what is *not* affected, since the asymmetry is the reason this is easy to miss: `Update` does not go through `UpdateAsync` — it routes through `_outboxWriter.UpsertAndEnqueueOutboxAsync` (`ObjectMappingGrpcService.cs:356`), which has no cast. So update genuinely passes while delete fails, which is not a distinction "every read step" captures in either direction.

**Evidence.**
- `Iverson.Server/Iverson.Sql/EntityRepository.cs:39` — `DeleteAsync` uses `WHERE "{key}" = @Key::uuid`.
- `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs:429` — `await _entities.DeleteAsync(...)`.
- `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs:356` — `Update` uses `UpsertAndEnqueueOutboxAsync`, not `UpdateAsync`.

**Proposed fix.** Restate the expected-failure rule by *cause* rather than by step category: for Go and TypeScript pre-fix, every step whose SQL path casts the key to uuid is expected to fail — read-by-key, depth-resolved read, and delete — while write and update are expected to pass. That phrasing stays correct if another cast site is added later.

**Note on current relevance.** The key-typing fix has now landed on `main@b67458d`, so if the harness is built against current `main`, the A8-derived expected failures no longer apply at all and the whole Expected-failures section needs re-basing rather than correcting. The spec's framing — "if the harness is built first, its first green run is the proof that the fix worked" — has been overtaken by the order events actually took.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**

The design's core judgements are sound and several are notably well made: the three-way verification triangle with per-pair localization, moving the depth-1 read to the orchestrator once A6 failed, and the insistence that every assertion be demonstrated to fail. The two hardest-to-see problems are both at the seam between the orchestrator and the drivers — the re-registration step has neither the data it needs nor an awareness that registration replaces what it is meant to preserve (§2.1), and the isolation scheme's key format collides with the very dependency the spec declares (§2.2). Both are fixable without changing the architecture. §2.3 is smaller but should be resolved together with re-basing the Expected-failures section, since the fix it was written to anticipate has already shipped.
