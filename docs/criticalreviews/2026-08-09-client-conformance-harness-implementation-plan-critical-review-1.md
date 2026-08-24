# Critical Implementation Review: 2026-08-09-client-conformance-harness-implementation-plan (Round 1)

**Plan:** `/home/ben/repositories/Iverson/docs/plans/2026-08-09-client-conformance-harness-implementation-plan.md`
**Verified plan-level assumptions section:** present

⚠️ 1 commit since plan-write time (SHA `ece0171`); cited file:line references re-checked under §1. The commit is the plan's own (`ca3202f`), so no source drift.

## 0. Coverage enumeration

### Tasks × surfaces

| Task | Surface | Disposition |
|---|---|---|
| T1 | csproj code block | ok — `net10.0` Exe, `Npgsql 10.0.3` / `Dapper 2.1.79` match `Iverson.LoadTest.csproj`'s pinned versions; both `ProjectReference` paths resolve from the new directory |
| T1 | solution wiring prose | ok — `Iverson.Server.slnx` lists `Iverson.LoadTest`; the root `Iverson.slnx` has an `/Iverson.Server/` folder to add to |
| T1 | TOTP code block | ok — nested conditional is valid C#; `is { Length: > 0 } env` is a legal property pattern on `string?`; the `?? throw` at the sole call site (`:146`) still fires when neither source has a secret |
| T1 | preflight prose | ok — an unauthenticated `MappingGet` still proves transport reachability, which is what the step claims; the harness never manages compose |
| T1 | TokenBroker prose | → §2.4 (tenant identity) |
| T1 | report prose | ok — matrix, reasoned skips, three observed values, `--json`, exit-code rule all trace to the spec's Reporting section |
| T1 | commands | ok — `dotnet build` accepts `.slnx` on .NET 10; `Iverson.Api.Tests.csproj` exists at the cited path |
| T2 | phase-document code block | ok — record shape carries `typeDescriptor`, `keys`, `entity`, `error`; phase set is the post-round-5 five-member enum and partitions the steps |
| T2 | DriverRunner prose | → §2.3 (per-language exec commands are consumed but never defined) |
| T2 | Reregistrar code block | ok — `AuthorizationRules` / `RowPermission` member names match `Iverson.LoadTest/Program.cs:289-306`; authorization-only mutation matches the spec's wholesale-replacement hazard |
| T3 | .NET model code block | ok — `[ManyToOne(typeof(DotNetAuthor))] DotNetAuthor? DotNetAuthor` is legal C# (color-color); FK `DotNetAuthorId` follows `{RelatedTypeName}Id`; nav name `DotNetAuthor` ≠ FK. `DotNetTags` ≠ `DotNetTagIds`, so .NET is the one client the m2m collision does not affect |
| T3 | auth-wiring prose | ok — `EntityCoordinator<T>`'s public 7-arg constructor takes the four generated clients, so an interceptor-carrying channel reaches every CRUD method; `AddIversonClient` genuinely does route `actingUserTokenProvider` only to `SchemaCatalogClient` |
| T3 | capture prose | ok — re-read all four builders; none is public, and the interceptor seam is the only public route in .NET |
| T3 | phase-dispatch block | → §2.2 (no step populates `OwnerId`) |
| T4 | Python model block | → §2.1 — `py_tag_ids` + `many_to_many` yields `PropertyName == ForeignKey == "PyTagIds"` |
| T4 | wiring / capture prose | ok — `IversonClient(credentials=…, acting_user_token=…)` covers both identities in one constructor; `SchemaRegistrar(stub, *classes)` accepts a wrapper stub |
| T4 | commands | ok — `pytest` is configured with `testpaths = ["tests"]`, so the driver is not collected |
| T5 | tsconfig block | ok — `extends` + `outDir` override is valid; `include` spanning `src`/`generated`/`conformance` makes the TS root the inferred `rootDir`, so `conformance/driver.ts`'s `../src/core.js` resolves in the emitted tree |
| T5 | ESM-runnability prose | ok — **checked the emitted output, not the source**: `dist/src/core.js:6-9` carries explicit `.js` specifiers, so `tsc` output runs under Node without a resolver shim. This was the plausible break in a `moduleResolution: bundler` project and it does not bite |
| T5 | model prose | → §2.1 (same m2m shape as T4, per `tests/schema-registrar.test.ts:429-436`) |
| T5 | commands | ok — `npx tsc -p …` (typescript is a devDependency) and `npm test` = `tsc -p tsconfig.test.json && vitest run` |
| T6 | Go model block | → §2.1 — `GoTagIds` + `many_to_many` yields `PropertyName == ForeignKey`, per `registrar.go:318-326` |
| T6 | wiring / capture prose | ok — `MappingClient` is a one-method interface; `WithActingUserToken(ctx, …)` reaches every coordinator method since all take `ctx` |
| T6 | commands | ok — `go build ./...` / `go test ./...` from the module root cover a new `conformance` package |
| T7 | module / pom prose | ok — reactor is `pom`-packaged with a `<modules>` list; `protobuf-java-util` really is `test`-scoped in `client/pom.xml:71-76`, so the compile-scope re-declaration is required; sample's pom is the right template |
| T7 | credentials prose | ok — `CallCredentials` is public and `IversonClient(channel, credentials)` applies it to all four stubs (`IversonClient.java:73-79`) |
| T7 | capture prose | ok — `SchemaRegistrar` reads the package-private `client.mappingStub`, so the channel interceptor is the only public seam; `ManagedChannelBuilder.intercept` is available |
| T7 | commands | ok — matches the CodeQL workflow's own `mvn -B -f Iverson.Clients/Java/pom.xml` invocation |
| T8 | registration-assertion prose | → §2.1 |
| T8 | PostgresProbe prose | ok — `ToSnakeCase(TypeName) + "s"` per `SchemaBuilder.cs:30`; superuser connection is not RLS-blinded (A16) |
| T8 | three-way comparison prose | ok — the localization rule (driver vs gRPC vs Postgres) is stated correctly and each leg has a source |
| T8 | S1 sequencing block | ok — orchestrator rows sit between the `read` and `update` phases, against live rows; matches the spec's post-round-4 phase model |
| T9 | S2 prose | ok — `register`-phase-only; client-side rejection precedes any RPC (A10); .NET/Java skip reason is the separate-FK-field asymmetry, which the models in T3/T7 confirm |
| T9 | S3 prose | ok — `MappingWriteRequest` is the right message (`object_mapping.proto:11`); `RelationValidator.cs:49-60` is what produces the `InvalidArgument` naming both the property and the FK |
| T10 | register-once prose | ok — the hazard is real (`SchemaRegistry.RegisterAsync` replaces wholesale) and the plan states the mitigation as a step, not a note |
| T10 | `--keys` fan-out prose | ok — every driver's `write` phase precedes any `read` phase, so the union is complete before the first cross-read |
| T11 | mutation prose | ok — names the spec's two mutations and extends them to every assertion the plan introduces; the "green mutation is a defect in the assertion" rule is stated |
| T11 | clean-tree commands | ok — all five suite commands verified above |

### Cross-task interface contracts

| Contract | Disposition |
|---|---|
| T2 `PhaseDocument` → T3–T7 emit it | ok — every field the orchestrator reads (`typeDescriptor`, `keys`, `entity`) has a producing step named in each driver task |
| T3–T7 `keys` map → T8 orchestrator reads/deletes, T10 cross-reads | ok — logical names (`author`, `tag`, `article`) are fixed in T3 and mirrored by T4–T7 |
| T1 `TokenBroker.GetOwnerIdAsync()` → drivers' `--owner-id` → **written rows** | → §2.2 — the parameter is produced and passed, and no consuming step writes it into a payload |
| T1 `TokenBroker` tenant → drivers' `--tenant` → server-side tenant scoping | → §2.4 |
| T2 `DriverRunner` build/exec commands ← T3–T7 | → §2.3 |
| T3 register-phase `typeDescriptor` → T2 `Reregistrar` (crosses a serialization boundary) | ok — canonical proto3 JSON in all five languages; the orchestrator parses back into the same message type before mutating `Authorization` |
| T8 `Verifier` → reused by T9 and T10 | ok — the registration assertions and three-way comparison are the only shared surface, and both are defined in T8 before either consumer |

### Rule-like content, both directions

| Rule | Over-inclusion | Under-inclusion | Disposition |
|---|---|---|---|
| Registration assertion `propertyName != foreignKey` | flags a conforming descriptor | passes a broken one | → §2.1 — over-inclusion fires on every conforming ManyToMany |
| `isArray` set only for `many_to_many` | flags a conforming array FK | misses a wrongly-arrayed FK | ok — Go `registrar.go:135`, Python `core.py:262`, TS test at `:439` all set `isArray` exactly on m2m |
| Key identity (driver-chosen UUID, reported by logical name) | two runs collide | orchestrator addresses an unwritten key | ok — run id in every key; the reported map is authoritative |
| Toolchain-absent → `skip` | skips a language whose toolchain is present | fails a language for an absent toolchain | ok — the rule keys on the build/exec command's own failure, and the row carries its reason |
| Three-way agreement | declares a defect where sources differ legitimately | agrees vacuously | ok — the deleted-row vacuity was closed by the spec's phase model; all three observations are taken between `read` and `update` |

## 1. Verified-plan-assumptions cross-check

Assumptions 1–36 re-read against the cited evidence. **All 36 still hold.** Spot-notes where the fresh read added something:

- **7** — confirmed the two-hit grep; the plan is right that `:93` is the definition and `:146` the sole caller, which the spec's own A4 states less precisely.
- **17/18** — re-read all four builders and all four seams. `MappingClient` (Go) is genuinely an interface with one method; `IversonClient.java:73-79` applies `CallCredentials` to all four stubs.
- **19** — `RowFieldAuthorizationEvaluator.cs:11-16`: `rules is null` and `actingUser is null` both return `new AuthorizationDecision(true, …)`, and the record's first member is `Denied` (`IRowFieldAuthorizationEvaluator.cs:16-17`). The assumption's reading is correct.
- **25/26** — `protobuf-java-util` is `<scope>test</scope>` at `client/pom.xml:74`; `google.golang.org/protobuf v1.36.11` at `go.mod:7`.
- **30** — verified against emitted output rather than source: `dist/src/core.js` preserves `.js` specifiers.

### Span check — one uncovered dependency

**Nothing covers which tenant the harness runs in, or that the acting user's `tenant_id` claim matches it.** Assumptions 8, 19, 23 and 24 cover the acting-user token, the `sub`, the env var names and the rules shape; none covers the tenant. Verified in-round: reads are tenant-scoped by the claim (`ObjectRetrievalGrpcService.cs:34-38`), and `Evaluate` requires a non-empty `tenant_id` claim before any non-denied path (`RowFieldAuthorizationEvaluator.cs:18-22`). → §2.4.

## 2. Literal-wrongness findings

### §2.1 — The registration assertion fails every conforming `many_to_many`, in four of the five clients

**Description.** Task 8, Step 1 asserts, for each non-`OneToMany` relation, that `propertyName != foreignKey`. That is inherited verbatim from the spec's Verification section, and it is wrong for `ManyToMany`. In the FK-on-the-member clients, a many-to-many member named `{Related}Ids` derives its navigation-property name by returning the member name unchanged — the `Id` strip applies only to `many_to_one` and `one_to_one` — while the foreign key is inferred as `{Related}Ids`. The two are therefore *equal by construction*, and the server treats that as correct, not as a defect:

> `// When PropertyName and ForeignKey collide — Python, TypeScript and Java can all produce that for ManyToMany — the "nav property" and the foreign key are the SAME payload key. There is nothing to reject: the payload key IS the foreign key.`

The plan's own models walk straight into it: `py_tag_ids = many_to_many("PyTag")` (Task 4), `regAuthorIds`-shaped `@ManyToMany` (Task 5), `GoTagIds []string` (Task 6). All three are the shape the clients' own tests assert as correct. So S1 — the harness's central scenario — would report `FAIL` for Python, TypeScript, Go and Java on a fully conforming stack, and only .NET would pass, because .NET alone declares the m2m nav property separately (`DotNetTags`) from the FK (`DotNetTagIds`).

The defect the harness exists to catch is the *many-to-one* collision, where `PropertyName == ForeignKey` genuinely means the resolver overwrites the FK with the hydrated entity. Scoping the assertion to m2m as well inverts it from a defect detector into a false-positive generator.

**Evidence.**
- `Iverson.Server/Iverson.Api/Grpc/RelationValidator.cs:20-24` — the collision is explicitly accommodated for `ManyToMany`.
- `Iverson.Clients/Go/iverson/registrar.go:318-326` — `relationPropertyName` strips `Id` only for `ManyToOne`/`OneToOne`; `:122-127` uses it as `PropertyName` alongside the `{Related}Ids` foreign key.
- `Iverson.Clients/Python/iverson_client/core.py:100-121` — the same split between `_relation_property_name` and `_infer_fk`.
- `Iverson.Clients/TypeScript/tests/schema-registrar.test.ts:429-440` — `regAuthorIds` with `@ManyToMany` is asserted to produce `RegAuthorIds` with `isArray: true`; that is the client's own definition of correct.
- Plan, Task 4 Step 1 / Task 5 Step 2 / Task 6 Step 1 — the models the plan tells the implementer to write.

**Proposed fix.** Scope the assertion by kind in Task 8, Step 1: require `propertyName != foreignKey` for `ManyToOne` and `OneToOne` only, and for `ManyToMany` assert instead that the foreign key is declared among the properties with `isArray` set — which is what actually distinguishes a conforming m2m descriptor from a broken one. Add one sentence recording why m2m is exempt, citing `RelationValidator.cs:20-24`, so a later round doesn't "restore" the stricter form.

### §2.2 — `--owner-id` is produced, passed, and never written into a row

**Description.** The plan's Global Constraints fix the authorization rules as `OwnerField = "OwnerId"` plus the bypass `RowPermission`, and Task 1 Step 4 produces the value via `GetOwnerIdAsync()`. Every driver takes `--owner-id <sub>`, and Task 3 Step 1's model carries an `OwnerId` property. But no step in any driver task says to populate it on write: Task 3 Step 4's write phase is "persist author, tag, article (both FKs)", and Tasks 4–7 mirror Task 3.

With ownership rules in force and the group bypass unmatched, the server compares the row's owner field against the token's `sub` and treats a mismatch as an ownership violation — an empty `OwnerId` is a mismatch. The write phase then fails for every language, and because a failed step is data rather than a crash, it surfaces as a conformance `FAIL` attributed to the client rather than to the harness's own omission. If the group bypass *does* match, the same rows pass — which makes the failure mode intermittent across environments rather than absent.

**Evidence.**
- `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs:258` and `:401` — `StructFieldAccess.GetFieldString(entityStruct, decision.OwnerFieldName!) != decision.OwnerValue` is the ownership check, on the write and read paths respectively.
- `Iverson.Server/Iverson.Api/Authorization/RowFieldAuthorizationEvaluator.cs:36-47` — ownership is required whenever the caller matched no bypass role and `OwnerField` is set; `OwnerValue` is the `sub` claim.
- Plan, Task 3 Step 4 — the write phase's step list, which never assigns `OwnerId`.

**Proposed fix.** In Task 3 Step 4, state that every entity written in the `write` and `update` phases sets `OwnerId` to the `--owner-id` value, and note in Tasks 4–7 that this is part of the shape they mirror. One clause; it closes the produced-but-never-consumed contract.

### §2.3 — `DriverRunner` consumes a per-language build and exec command that four of the five driver tasks never define

**Description.** Task 2 Step 2 specifies `DriverRunner` as "one build per driver per run, then one exec per phase", and defers the commands: "the build command and the exec command are per-language (Task 3–7 each supply theirs)." Tasks 3–7 do not supply them. Their command blocks are build-and-test gates for the implementer (`dotnet build`, `python3 -m pytest`, `npm test`, `go test ./...`, `mvn … test`) — none of which is the command `DriverRunner` should exec to run a phase. Only Task 7 names an artifact shape at all, and only indirectly, via the shade-plugin rationale ("so `DriverRunner` can exec `java -jar`").

At execution time the Task 2 subagent has nothing to write into the runner, and the Task 4–6 subagents have no instruction to produce it, so the runner's command table is either invented per-task or left empty. The toolchain-absent skip rule depends on the same commands, so it is unimplementable for the same reason.

**Evidence.**
- Plan, Task 2 Step 2 — defers the commands to Tasks 3–7.
- Plan, Task 4 Step 5 / Task 5 Step 5 / Task 6 Step 4 — the only command blocks in those tasks are regression gates.
- Plan, Task 7 Step 1 — the sole (indirect) statement of an exec form.

**Proposed fix.** Put the table in Task 2 Step 2, where its consumer lives, with one row per language naming the build command and the exec command — e.g. `dotnet run --project … -- <flags>`; `python3 conformance/driver.py <flags>`; `npx tsc -p tsconfig.conformance.json` then `node dist-conformance/conformance/driver.js <flags>`; `go run ./conformance <flags>`; `mvn -B -f … -pl conformance -am package` then `java -jar conformance/target/<artifact>.jar <flags>`. Tasks 3–7 then state only "the exec form Task 2's table names for this language."

### §2.4 — The plan never pins the tenant, and a freshly provisioned one would not match the acting user's claim

**Description.** Task 1 Step 4 has `TokenBroker` "provision the tenant" without naming which tenant, and the spec's illustrative invocation uses `--tenant tenant-bypass`. Both reads and the authorization evaluator are driven by the acting user's `tenant_id` **claim**, not by the `--tenant` flag: retrieval scopes the row fetch to the claim's tenant, and `Evaluate` bails to `Denied` when the claim is empty. The acting-user identities the plan reuses are LoadTest's, whose tenant is `IVERSON_LOADTEST_TENANT_ID` (default `iverson-loadtest-dynamic`).

So if the harness provisions a new tenant of its own and stamps it on driver rows, the acting user's token still carries the old tenant: writes land under one tenant and every read scopes to another, so every read returns not-found. S1's delete step treats not-found as its *success* condition, which means the run can report a passing delete while every verification step fails for a reason the report attributes to the client — the same failure signature §2.1 produces, from a different cause.

**Evidence.**
- `Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs:34-38` — `tenantId: actingUserAccessor.ActingUser?.FindFirst("tenant_id")?.Value` scopes the fetch.
- `Iverson.Server/Iverson.Api/Authorization/RowFieldAuthorizationEvaluator.cs:18-22` — an empty `tenant_id` claim short-circuits to `Denied`.
- `Iverson.Server/Iverson.LoadTest/Program.cs:44` — the acting identities belong to `IVERSON_LOADTEST_TENANT_ID`.
- Plan, Task 1 Step 4 and Global Constraints — neither names a tenant.

**Proposed fix.** State in Task 1 Step 4 that the harness runs in the acting user's own tenant — read `IVERSON_LOADTEST_TENANT_ID` (same default as LoadTest), ensure it exists rather than creating a harness-specific one, and pass that value as every driver's `--tenant`. If a dedicated conformance tenant is wanted instead, the acting-user identity has to be provisioned into it first, which is a second, larger change; the plan should say which of the two it means.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**

Four findings, all in the same class: values that the plan produces and passes but never lands (`OwnerId`, the driver exec commands), and identity that the plan assumes rather than pins (the m2m naming rule, the tenant). None of them requires re-shaping a task — the fixes are one clause in Task 3, one table moved into Task 2, one kind-scoped assertion in Task 8, and one named env var in Task 1.

§2.1 is the one worth prioritising: it makes the harness report `FAIL` for four of five languages against a fully conforming stack, and it would be read as a client defect. It is also the finding least likely to be caught during execution, because it is inherited verbatim from an approved spec and looks like a settled requirement rather than a decision.

The parts of the plan that were most likely to be wrong came back clean on a real check rather than an assumed one: the compiled TypeScript really does carry `.js` specifiers and runs under Node, the four descriptor-capture seams are all genuinely public, and the .NET model's color-color property and FK-inference convention line up.
