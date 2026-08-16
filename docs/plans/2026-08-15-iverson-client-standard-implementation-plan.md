# Iverson Client Standard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-15-iverson-client-standard-design.md` (commit SHA: `c49a01a`)

**Goal:** Publish a normative standard defining what a conforming Iverson client must satisfy, bound to executable evidence by a coverage gate that fails the build if any requirement is untested.

**Architecture:** A living document at `docs/standards/iverson-client-standard.md` declares requirements as `IVC-<AXIS>-<NNN>` rows across nine axes. A `Requirements.cs` registry mirrors it as consts, orchestrator assertions cite those consts, and a unit test enforces the correspondence in both directions. Five new conformance scenarios extend the harness to the axes nothing currently exercises.

**Tech stack:** .NET 10 (orchestrator, gate test via xunit + FluentAssertions), and the five client languages the drivers exercise — .NET, Java (Maven), Python (pytest), TypeScript (vitest), Go.

---

## Global Constraints

Project-wide rules every task must hold to, copied from the spec's rulings:

- **Untiered.** Every requirement is a MUST. There is no SHOULD tier and no "recommended" requirement.
- **Behaviour and capability only.** Requirements constrain what goes on the wire and what must be reachable through the public API. They never mandate naming or signatures.
- **The server is the enforcement boundary.** Where a rule can be enforced server-side, the requirement is on the server; client-side checks are recommended diagnostics in a non-normative appendix.
- **The gate is strict.** A requirement in the document with no citing assertion fails the build. This forbids authoring requirements ahead of their tests — every axis task lands requirements and citations together.
- **A `skip` is legitimate only for a missing toolchain.** A driver reporting that its client cannot perform a required operation is a FAIL.

## File Structure

**Create**

- `docs/standards/iverson-client-standard.md` — the standard itself; skeleton in T1, filled per-axis in T5–T12.
- `Iverson.Server/Iverson.ClientConformance/Requirements.cs` — one `public const string` per requirement.
- `Iverson.Server/Iverson.ClientConformance.Tests/RequirementsCoverageGateTests.cs` — the three-check gate.
- `Iverson.Server/Iverson.ClientConformance/Scenarios/SchemaCatalogScenario.cs`, `QueryScenario.cs`, `VectorSearchScenario.cs`, `IdentityScenario.cs`, `ErrorContractScenario.cs` — one per new axis (T8–T12).

**Modify**

- `Iverson.Server/Iverson.ClientConformance/Verifier.cs` — `Assertion` gains `RequirementId`; distinctness assertion extends to `many_to_many`; assertions cite consts.
- `Iverson.Server/Iverson.ClientConformance/Report.cs` — `ReportCell` carries assertions; JSON gains the exercised/untouched requirement tally.
- `Iverson.Server/Iverson.ClientConformance/Scenarios/{CrudRoundtrip,NamingRejected,NavPropertyRejected,Interop}Scenario.cs` — `Cell()` paths stop discarding passing assertions; citations added.
- `Iverson.Server/Iverson.ClientConformance/Program.cs` — register the five new scenarios.
- `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` — two new `REG` checks.
- `Iverson.Server/Iverson.Api.Tests/Grpc/RelationValidatorTests.cs` — invert two collision-accepted guards.
- `Iverson.Clients/DotNet/Iverson.Client.Core/SchemaRegistrar.cs` and `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/SchemaRegistrar.java` — navigation-property derivation.
- The five drivers — `Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Program.cs`, `Java/conformance/`, `Python/conformance/driver.py`, `TypeScript/conformance/driver.ts`, `Go/conformance/main.go` — new scenario names and phase branches (T8–T12).

**Test**

- `Iverson.Server/Iverson.ClientConformance.Tests/` — gate tests, scenario tests.
- `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs` — the two new server checks.
- `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/SchemaRegistrarTests.cs`, `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/SchemaRegistrarTest.java` — derivation.

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and **not** re-verified here. Evidence for each is in the spec's `Verified assumptions` table.

A1 `docs/standards/` free · A2 no existing standard · A3 `Assertion` tolerates an optional trailing field · A4 path convention (partial) · A5 assertions also built in the test project · A6 `ReportCell` carries no assertions (failed) · A7 the four scenario names · A8 const reflection works · A9 FK synthesis per relation kind · A10 FK naming, server checks neither · A11 m2m collision legitimate today (failed) · A12 `isArray` for m2m only · A13 nav properties rejected with `InvalidArgument` · A14 FK readable at every depth · A15 multi-valued FKs sent as a list · A16 `one_to_many` reverse lookup · A17 all five expose a depth-resolved read (failed) · A18 Go alone requires the authorization map · A19 .NET explicit FK, server checks no name · A20 search/aggregate/vector/GetSchema exist in all five · A21 no assertion discharges QRY/VEC/SCH/IDN/ERR (failed) · A22 `6c18080` clean base · A23 nothing consumes the report JSON externally · A24 only Python/TS/Go derive nav names (failed) · A25 `Iverson.slnx` at the root · A26 `docs/standards/` not gitignored

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | `docs/standards/` and `Requirements.cs` do not exist; T1 creates both | existence check — both ABSENT; all other cited paths EXIST |
| 2 | File path | The four scenario files, `Report.cs`, `Verifier.cs`, `DriverRunner.cs`, `DriverProtocol.cs`, `SchemaRegistrationOrchestrator.cs`, both `SchemaRegistrar` files exist as cited | existence check, all EXIST |
| 3 | File path | All five driver entry points exist | `Program.cs`, `main.go`, `driver.py`, `driver.ts`, `Java/conformance/` all present |
| 4 | Signature | `Assertion` is built only via `Pass`/`Fail`/`From`; no positional `new Assertion(`, no deconstruction | grep returned zero hits for all three patterns |
| 5 | Signature | `Phase` is a fixed 5-value enum with no query/schema member | `DriverProtocol.cs:10-17` — `Register, Write, Read, Update, Delete` |
| 6 | Signature | Drivers dispatch on `(phase, scenario)` pairs, so a new scenario needs no new phase | `driver.py:378` `phase == "register" and scenario == "naming-rejected"`; `:419`, `:454` for interop write/read |
| 7 | Signature | Each driver validates the scenario name against a set constant | `driver.py:43` `SCENARIOS = {"crud-roundtrip", "naming-rejected", "interop"}` |
| 8 | Signature | `ReportCell` is `(Language, Scenario, Status, Reason, Detail)` with four factories | `Report.cs:24-40` |
| 9 | Command | TypeScript tests run via `npm test` = `npm run typecheck && vitest run` | `TypeScript/package.json:16` |
| 10 | Command | Python tests run via `pytest` | `pyproject.toml` declares `pytest>=8.0` and `[tool.pytest.ini_options]` |
| 11 | Command | Both conformance projects are in the solution, so `dotnet test` targets them by path | `Iverson.slnx:15-16` |
| 12 | Command | Commit messages are plain imperative sentences; prefixes (`spec:`, `fix(server):`) appear but are not required | `git log --oneline -12` |
| 13 | Ordering | T1's gate passes on an empty registry and a requirement-free skeleton — check 1 compares two empty sets, checks 2 and 3 iterate nothing | derived from the spec's check definitions; no code dependency |
| 14 | Ordering | T3 and T4 touch no file T1 or T2 touches, so they may run in parallel | file-structure disjointness: clients + `Iverson.Api/` vs `Iverson.ClientConformance/` |
| 15 | Ordering | T7 depends on T4 — the `REG` axis cites assertions T4 introduces | plan-internal |
| 16 | Code validity | The gate test project has xunit + FluentAssertions and `Nullable enable`, so the reflection and the nullable `RequirementId` compile | `Iverson.ClientConformance.Tests.csproj` |
| 17 | Consumer (Cat 6) | **T3 is a no-op for every existing .NET model** — no nav member name carries an `Id`/`Ids` suffix | `Sample/Models/Article.cs:26,29` (`Author`, `Tags`), `UserArticle.cs:18` (`User`), `SchemaRegistrarTests.cs:96-100` (`Author`, `Tags`), `LoadTest/Entities/BenchmarkArticle.cs:18` |
| 18 | Consumer (Cat 6) | The golden fixtures are query contracts, not descriptors, so T3 does not touch them | `Common/testdata/` holds only `groupby-contract-1.json`, `pipeline-contract-1.json` |
| 19 | Consumer (Cat 6) | **T4 reverses two existing regression guards that assert a collision is accepted** | `RelationValidatorTests.cs:124` `PropertyNameEqualsForeignKey_KeyNotStripped`, `:144` `ManyToMany_PropertyNameEqualsForeignKey_NoConflictError`, both building `RelationDescriptor("TagIds", ManyToMany, "Tag", "TagIds")` at `:130,150` |
| 20 | Consumer (Cat 6) | No other test or fixture registers a colliding descriptor, so T4 breaks nothing else | `SchemaFixtures.cs:100` `("Tags", ManyToMany, "Tag", "TagIds")`; `ObjectMappingGrpcServiceTests.cs:428` `("Author", ManyToOne, "author", "AuthorId")`; `AuthorizationFieldMaskingTests.cs:162`, `StoreTargetingTests.cs:18` — all distinct |
| 21 | Consumer (Cat 6) | `ERR` partly discharges against existing assertions, so T12 is the smallest scenario task | `NamingRejectedScenario.cs:120-130` and `NavPropertyRejectedScenario.cs:142-163` already assert status codes and message content |
| 22 | Consumer (Cat 6) | A driver-registered type is `Denied` to every authorized operation until re-registered with an authorization block | `RowFieldAuthorizationEvaluator.cs:10-12` returns `Denied = true` when `schema.Authorization is null`; `ObjectMappingGrpcService.cs:78-81` skips denied schemas in `GetSchema`; `CrudRoundtripScenario.cs:63-89` re-registers for exactly this reason |
| 23 | Consumer (Cat 6) | The depth-resolved-read Capability — authored in `LIFE`, superseding the retired `IVC-REL-009` — has no existing assertion to cite | all five drivers read at depth 0 only: `Program.cs:325-335`, `driver.py:521`, `driver.ts:481`, `main.go:597`, `Driver.java:218`; the only depth-1 read is the orchestrator's own gRPC call |

**Sibling-set sweep** — "every identifier the plan names resolves to a definition at its point of use", run over the full set the plan references: `Assertion`, `ReportCell`, `Verifier.VerifyRegistration`, `DriverRunner`, `DriverProtocol`/`Phase`/`PhaseNames`, `Report.RenderJson`/`RenderText`, both `SchemaRegistrar`s, `SchemaRegistrationOrchestrator`, `RelationValidator`, the four scenario classes, `Iverson.slnx`, the five driver entry points. All resolve; findings are rows 5, 6, 7 and 19 above.

## Tasks

### Task 1: Gate foundation

**Files:**
- Create: `docs/standards/iverson-client-standard.md`
- Create: `Iverson.Server/Iverson.ClientConformance/Requirements.cs`
- Create: `Iverson.Server/Iverson.ClientConformance.Tests/RequirementsCoverageGateTests.cs`
- Modify: `Iverson.Server/Iverson.ClientConformance/Verifier.cs:12-16`

**Interfaces:**
- Produces: `Requirements` (empty registry), `Assertion.RequirementId`, the gate test. Every later task consumes all three.

- [ ] **Step 1: Write the standard's skeleton.** Front matter explaining what the document is; the nine-axis table; the Behaviour/Capability definitions; the entry format (ID, status, kind, statement, rationale, evidence); the non-normative *Recommended diagnostics* appendix seeded with the client-side foreign-key naming check. One empty requirement table per axis, each with the header `| ID | Status | Kind | Statement |`. No requirements yet — the gate must be green before any exist.

- [ ] **Step 2: Create the empty registry.** `Requirements.cs` with the class declaration, a comment stating that a const exists only for an `Active` requirement, and no members.

- [ ] **Step 3: Add `RequirementId` to `Assertion`.** Extend the record and the three factories with an optional trailing `string? requirementId = null`. Existing call sites must compile untouched.

- [ ] **Step 4: Write the gate test, tests-first against the empty state.** Three checks: (1) the set of `Active` IDs parsed from requirement-table rows equals the consts reflected off `Requirements` via `IsLiteral && !IsInitOnly`, with `Retired` rows parsed for well-formedness and uniqueness only and taking no const; (2) each const's identifier appears at least once under `Iverson.ClientConformance/`, excluding `Requirements.cs` and the test project; (3) every ID matches `IVC-[A-Z]+-\d{3}` with an axis from the known set. Locate the repository root by walking up from `AppContext.BaseDirectory` to the directory containing `Iverson.slnx`.

- [ ] **Step 5: Prove each check can fail.** Temporarily add a requirement row with no const, a const with no row, an uncited const, and a malformed ID; confirm each turns the corresponding check red; revert. Record the four failure messages in the task report. A gate that cannot fail is the exact defect this standard exists to prevent.

- [ ] **Step 6: Run and commit.**
```bash
dotnet test Iverson.Server/Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj
git add docs/standards/iverson-client-standard.md Iverson.Server/Iverson.ClientConformance/Requirements.cs Iverson.Server/Iverson.ClientConformance/Verifier.cs Iverson.Server/Iverson.ClientConformance.Tests/RequirementsCoverageGateTests.cs
git commit -m "add the client-standard skeleton and its coverage gate"
```

### Task 2: ReportCell carries assertions; runtime tally

**Files:**
- Modify: `Iverson.Server/Iverson.ClientConformance/Report.cs:17-40, 105-116`
- Modify: the four `Scenarios/*.cs` `Cell()` paths
- Test: `Iverson.Server/Iverson.ClientConformance.Tests/`

**Interfaces:**
- Consumes: `Assertion.RequirementId` (T1).
- Produces: the exercised/untouched tally that T5–T12 populate as they add citations.

- [ ] **Step 1: Extend `ReportCell` to carry its assertions.** Add the full list — passing and failing — alongside the existing fields, and thread it through the four factories. `Detail` keeps its current failure-text role so `RenderText` is unchanged.

- [ ] **Step 2: Stop discarding passes.** Each scenario's `Cell()` currently keeps only `Assertions.Where(a => !a.Passed)`. Pass the whole list to the cell. Terminal skip/fail paths that bypass `Cell()` carry whatever assertions accumulated before they fired.

- [ ] **Step 3: Emit the tally.** `RenderJson` gains, per cell, the distinct requirement IDs its assertions cited, and at the top level the set of registry IDs no cell exercised. Print a one-line untouched count in `RenderText` so a green matrix cannot hide an unexercised requirement.

- [ ] **Step 4: Test, including the tally's failure direction.** Assert that a cell whose assertions cite nothing yields an empty exercised set and that its requirements appear in the untouched set. Mutate the tally to always report every ID as exercised and confirm the test goes red.

- [ ] **Step 5: Run and commit.**
```bash
dotnet test Iverson.Server/Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj
git add Iverson.Server/Iverson.ClientConformance/Report.cs Iverson.Server/Iverson.ClientConformance/Scenarios Iverson.Server/Iverson.ClientConformance.Tests
git commit -m "carry every assertion into the report and tally exercised requirements"
```

### Task 3: Navigation-property derivation in .NET and Java

Independent of T1 and T2 — may run in parallel.

**Files:**
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/SchemaRegistrar.cs:85`
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/SchemaRegistrar.java:296`
- Test: `Iverson.Client.Core.Tests/SchemaRegistrarTests.cs`, `SchemaRegistrarTest.java`

- [ ] **Step 1: Write the failing tests first.** In each language, a model whose relation is declared on the foreign-key member (`[ManyToMany(typeof(Tag))] public Guid[] TagIds`, and the Java equivalent) currently produces `PropertyName == ForeignKey`. Assert the derived name is distinct. Both tests must fail before the change.

- [ ] **Step 2: Derive in .NET.** `PropertyName = relation.Property.Name` becomes a derivation that strips a trailing `Id` or `Ids`, case-sensitively, with the same length guards Python, TypeScript and Go use (`core.py:112-115`, `core.ts:99-101`) so a member named exactly `Id` is not truncated.

- [ ] **Step 3: Derive in Java.** Same rule applied to `StructConverter.toPascalCase(field.getName())`.

- [ ] **Step 4: Confirm no existing model changes.** Assumption 17 verified that every current .NET nav member is `Author`, `Tags`, `User` or `Article` — no `Id`/`Ids` suffix — so the strip is a no-op for the Sample, LoadTest, drivers and existing tests. Re-run both suites and confirm zero pre-existing tests changed behaviour. If any did, stop and report: the no-op claim was wrong.

- [ ] **Step 5: Run and commit.**
```bash
dotnet test Iverson.Clients/DotNet/Iverson.Client.Core.Tests/
mvn -f Iverson.Clients/Java/pom.xml test
git add Iverson.Clients/DotNet/Iverson.Client.Core Iverson.Clients/DotNet/Iverson.Client.Core.Tests Iverson.Clients/Java/client
git commit -m "derive navigation property names distinctly in the .NET and Java registrars"
```

### Task 4: The two server REG checks

Independent of T1 and T2 — may run in parallel.

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:83-109`
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/RelationValidatorTests.cs:124-160`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`

**Interfaces:**
- Produces: the two rejections T7 cites.

- [ ] **Step 1: Write the failing tests.** Registration must be rejected with `InvalidArgument` when (a) a relation's foreign key is not named `{RelatedTypeName}Id` / `{RelatedTypeName}Ids`, compared with `StringComparison.OrdinalIgnoreCase` to match the foreign-key membership check immediately above it (`SchemaRegistrationOrchestrator.cs:85-86`) and the registry's own keying, and (b) a relation's `PropertyName` equals its `ForeignKey`, for every relation kind. Both messages name the offending relation and what was expected. Add a test for the case the scope split in Step 2 protects: a one-to-many relation whose foreign key is `{ThisTypeName}Id` must register cleanly.

- [ ] **Step 2: Add the two checks at their correct scopes.** The **naming** check goes inside the existing per-relation loop, which already excludes `OneToMany` — a one-to-many foreign key is named `{ThisTypeName}Id` and lives on the related type's row, so the `{RelatedTypeName}Id` rule does not apply to it. The **collision** check goes in a separate pass over `descriptor.Relations` with no kind filter, so it covers every relation kind including `OneToMany`, per the spec's ruling.

- [ ] **Step 3: Invert the two regression guards.** `RelationValidatorTests.cs:124` `PropertyNameEqualsForeignKey_KeyNotStripped` and `:144` `ManyToMany_PropertyNameEqualsForeignKey_NoConflictError` currently assert a collision **is accepted** — that behaviour is what this task reverses. Rewrite both to assert rejection, keeping their descriptor fixtures. **Do not weaken the new check to keep them green**; the guards, not the check, are what changed.

- [ ] **Step 4: Confirm nothing else registers a colliding descriptor.** Assumption 20 verified the other four inline descriptors and the shared fixture are all distinct. Run the full API suite and confirm only the two inverted tests moved.

- [ ] **Step 5: Run and commit.**
```bash
dotnet test Iverson.Server/Iverson.Api.Tests/
git add Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs Iverson.Server/Iverson.Api.Tests/Grpc
git commit -m "reject misnamed foreign keys and nav-property/foreign-key collisions at registration"
```

### Task 5: The REL axis

The worked exemplar — it lands before the other axes because it proves the authoring pattern the rest follow.

**Files:**
- Modify: `docs/standards/iverson-client-standard.md` (REL table)
- Modify: `Iverson.Server/Iverson.ClientConformance/Requirements.cs`
- Modify: `Iverson.Server/Iverson.ClientConformance/Verifier.cs`, `Scenarios/CrudRoundtripScenario.cs`

**Interfaces:**
- Consumes: T1's registry and gate; T3's derivation and T4's rejection, both of which `IVC-REL-003` depends on.

- [ ] **Step 1: Author the ten requirements** from the spec's table, each with kind, statement, a rationale naming the spec or defect behind it, and its conformance evidence. Nine are `Active`. `IVC-REL-009` is authored as `Retired`, rationale "superseded by the `LIFE` depth capability" — its ID is never reused, it takes no const, and it is not subject to the gate. This is the retirement path's first real exercise, so it also proves check 1 parses a `Retired` row and excludes it from the `Active` set.

- [ ] **Step 2: Add the ten consts** to `Requirements.cs`, named for what each asserts.

- [ ] **Step 3: Extend the distinctness assertion to `many_to_many`.** `Verifier.VerifyRegistration` currently applies it to `ManyToOne` and `OneToOne` only, exempting m2m by design. The ruling reverses that exemption; remove it and update the explanatory comment, which currently states the old rationale.

- [ ] **Step 4: Cite the consts** on the assertions that discharge each requirement, in `Verifier.cs` and `CrudRoundtripScenario.cs`.

- [ ] **Step 5: Verify the gate now binds.** Delete one citation and confirm check 2 goes red naming that ID; restore. Add a requirement row with no const and confirm check 1 goes red; remove.

- [ ] **Step 6: Run the suite and a live matrix.**
```bash
dotnet test Iverson.Server/Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj
dotnet run --project Iverson.Server/Iverson.ClientConformance -- --scenarios crud-roundtrip
git add docs/standards Iverson.Server/Iverson.ClientConformance
git commit -m "author the REL axis and bind it to the harness assertions"
```

### Task 6: The DECL and LIFE axes

**This task carries a five-driver change**, not only document and citation work: `LIFE`'s depth-resolved-read Capability has no existing assertion to cite (Step 3).

**Files:**
- Modify: `docs/standards/iverson-client-standard.md` (DECL and LIFE tables)
- Modify: `Iverson.Server/Iverson.ClientConformance/Requirements.cs`
- Modify: `Iverson.Server/Iverson.ClientConformance/Verifier.cs`, `Scenarios/CrudRoundtripScenario.cs`
- Modify: all five drivers — `DotNet/Iverson.Client.Conformance.Driver/Program.cs`, `Java/conformance/`, `Python/conformance/driver.py`, `TypeScript/conformance/driver.ts`, `Go/conformance/main.go`

- [ ] **Step 1: Author `DECL`** — key field, tenant field, scalar and array type mapping, UUID key typing — citing the registration assertions that already check them.
- [ ] **Step 2: Author `LIFE`** — mapped create/read/update/delete, server-assigned keys, and the depth-resolved read as a Capability — citing the round-trip assertions. The depth capability supersedes the retired `IVC-REL-009`; it is the axis the spec's own table assigns `depth` to.
- [ ] **Step 3: Add a driver-side depth-resolved read** to discharge that Capability. Extend each driver's `read` phase with a second read of the article through its own client at depth 1 — `GetMappedAsync(key, depth: 1)`, `get_mapped(id, depth=1)`, `getMapped(id, 1)` (TypeScript and Java), and `GetMapped(ctx, id, 1)` (`coordinator.go:236`) — reported as its own step carrying the returned entity. This is what makes it a Capability the harness can grade: the orchestrator's own depth-1 read proves the server hydrates, not that a client can ask it to. Then assert, orchestrator-side, that every driver reported a hydrated entity, citing the `LIFE` depth const.
- [ ] **Step 4: For any other requirement with no existing assertion, add the assertion rather than dropping the requirement.** The gate will not let the requirement land otherwise; that is the intended pressure.
- [ ] **Step 5: Run and commit.**
```bash
dotnet test Iverson.Server/Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj
dotnet run --project Iverson.Server/Iverson.ClientConformance -- --scenarios crud-roundtrip
git add docs/standards Iverson.Server/Iverson.ClientConformance
git commit -m "author the DECL and LIFE axes"
```

### Task 7: The REG axis

**Files:**
- Modify: `docs/standards/iverson-client-standard.md` (REG table)
- Modify: `Iverson.Server/Iverson.ClientConformance/Requirements.cs`
- Modify: `Iverson.Server/Iverson.ClientConformance/Verifier.cs`, `Scenarios/NamingRejectedScenario.cs`, `Scenarios/NavPropertyRejectedScenario.cs`

**Interfaces:**
- Consumes: T4's two server rejections.

- [ ] **Step 1: Author `REG`** — descriptor contents, authorization rules, drift, plus the two rules T4 added: foreign keys are named `{RelatedTypeName}Id`, and a nav-property/foreign-key collision is rejected at registration.
- [ ] **Step 2: Cite the existing registration assertions** and add orchestrator-side assertions for T4's two rejections, exercised by attempting each offending registration over gRPC.
- [ ] **Step 3: Run and commit.**
```bash
dotnet test Iverson.Server/Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj
dotnet run --project Iverson.Server/Iverson.ClientConformance -- --scenarios naming-rejected,nav-property-rejected
git add docs/standards Iverson.Server/Iverson.ClientConformance
git commit -m "author the REG axis including the two new registration rejections"
```

### Task 8: The SCH axis and its scenario

The first new scenario — it establishes the pattern T9–T12 follow, and is the simplest because `GetSchema` needs no seeded rows.

**Files:**
- Create: `Iverson.Server/Iverson.ClientConformance/Scenarios/SchemaCatalogScenario.cs`
- Modify: `Program.cs`, `docs/standards/iverson-client-standard.md`, `Requirements.cs`
- Modify: all five drivers

**Interfaces:**
- Produces: the new-scenario pattern — scenario-name constant, driver set-constant entry, `(phase, scenario)` branch — that T9–T12 reuse.

- [ ] **Step 1: Add the scenario to each driver.** Two edits per driver: add the name to the validated scenario set (`driver.py:43` and its four equivalents), and add a `read`-phase branch that calls the client's `GetSchema` and reports the returned catalogue. **The `Phase` enum is not extended** — `Register, Write, Read, Update, Delete` is fixed, and drivers already branch on `(phase, scenario)` pairs.

- [ ] **Step 2: Re-register each reported descriptor with row permissions.** Take each driver's reported `TypeDescriptor` and re-register it through `Reregistrar.ReregisterAsync(descriptor.Json, actingToken)` with the authorization block added and nothing else changed. A schema with no authorization block is `Denied` for every action including `Read` (`RowFieldAuthorizationEvaluator.cs:10-12`), and `GetSchema` skips denied schemas outright (`ObjectMappingGrpcService.cs:78-81`) — so without this the driver's own types are invisible to the catalogue and the scenario fails for all five languages. Where a descriptor is missing, report a failed assertion naming the consequence; never skip in silence, matching `CrudRoundtripScenario.cs:70-84`. **This step is part of the pattern T9–T12 copy.**

- [ ] **Step 3: Write the orchestrator scenario.** Register a type per language, then have each driver read the catalogue and report it. Assert every language sees the type it registered, with the same field set the descriptor declared.

- [ ] **Step 4: Author the `SCH` requirements and cite them.**

- [ ] **Step 5: Prove the assertions can fail.** Break one driver's report and confirm only that language's cell goes red.

- [ ] **Step 6: Run and commit.**
```bash
dotnet test Iverson.Server/Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj
dotnet run --project Iverson.Server/Iverson.ClientConformance -- --scenarios schema-catalog
git add docs/standards Iverson.Server/Iverson.ClientConformance Iverson.Clients
git commit -m "add the schema-catalog conformance scenario and the SCH axis"
```

### Task 9: The QRY axis and its scenario

Follows T8's pattern.

- [ ] **Step 1: Add the scenario to all five drivers** — set constant plus a `write` branch seeding rows and a `read` branch issuing a search and an aggregate through each client's own builder API.
- [ ] **Step 2: Re-register each reported descriptor with row permissions**, per T8 Step 2. Without it every seeded write is denied.
- [ ] **Step 3: Handle the projection delay.** `Search` and `Aggregate` are served from StarRocks, but a mapped write commits to Postgres and enqueues an outbox row whose projection is asynchronous — so the read phase polls with a bounded retry rather than querying once. Match whatever wait convention T10 establishes; a bounded poll with an explicit timeout that reports as a failed step, never an indefinite wait, and never a fixed sleep presented as determinism.
- [ ] **Step 4: Write the orchestrator scenario**, asserting all five clients agree on the result set and the aggregate value for the same query over the same seeded rows. Require a positive expected row count, so five empty result sets fail rather than agreeing.
- [ ] **Step 5: Author the `QRY` requirements and cite them.**
- [ ] **Step 6: Prove the assertions can fail** — perturb one client's filter and confirm its cell alone goes red.
- [ ] **Step 7: Run and commit.**
```bash
dotnet test Iverson.Server/Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj
dotnet run --project Iverson.Server/Iverson.ClientConformance -- --scenarios query
git add docs/standards Iverson.Server/Iverson.ClientConformance Iverson.Clients
git commit -m "add the query conformance scenario and the QRY axis"
```

### Task 10: The VEC axis and its scenario

- [ ] **Step 1: Add the scenario to all five drivers** — `write` seeds rows carrying an embedded field, `read` issues `SearchSimilar` and `SearchChunks`.
- [ ] **Step 2: Re-register each reported descriptor with row permissions**, per T8 Step 2. Without it every seeded write is denied.
- [ ] **Step 3: Handle the projection delay.** Embeddings reach Qdrant asynchronously, so the read phase polls with a bounded retry rather than reading once. Match whatever wait convention the harness already uses; if none exists, a bounded poll with an explicit timeout that reports as a failed step — never an indefinite wait, and never a fixed sleep presented as determinism.
- [ ] **Step 4: Write the orchestrator scenario**, asserting all five clients retrieve the same object for the same query vector.
- [ ] **Step 5: Author the `VEC` requirements and cite them.**
- [ ] **Step 6: Prove the assertions can fail.**
- [ ] **Step 7: Run and commit.**
```bash
dotnet test Iverson.Server/Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj
dotnet run --project Iverson.Server/Iverson.ClientConformance -- --scenarios vector-search
git add docs/standards Iverson.Server/Iverson.ClientConformance Iverson.Clients
git commit -m "add the vector-search conformance scenario and the VEC axis"
```

### Task 11: The IDN axis and its scenario

- [ ] **Step 1: Add the scenario to all five drivers.** The orchestrator passes a deliberately wrong acting-user token for the negative leg; each driver attempts a write and reports the status code it received rather than judging it.
- [ ] **Step 2: Re-register each reported descriptor with row permissions**, per T8 Step 2. Without it the positive leg is denied for the same reason the negative leg is, and the scenario cannot tell the two apart.
- [ ] **Step 3: Write the orchestrator scenario** — a positive leg (the correct acting user writes and reads back) and a negative leg (the wrong acting user is denied). Assert every client surfaces the same status code on the negative leg.
- [ ] **Step 4: Author the `IDN` requirements and cite them** — service token, acting-user propagation, tenancy enforcement.
- [ ] **Step 5: Prove the assertions can fail.** Drop the acting-user header in one driver and confirm its cell goes red. If a denial appears, read `docker logs iverson-api | grep Audit.Denied` — the audit line names actor, tenant and reason, and is the fastest route to the cause.
- [ ] **Step 6: Run and commit.**
```bash
dotnet test Iverson.Server/Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj
dotnet run --project Iverson.Server/Iverson.ClientConformance -- --scenarios identity
git add docs/standards Iverson.Server/Iverson.ClientConformance Iverson.Clients
git commit -m "add the identity conformance scenario and the IDN axis"
```

### Task 12: The ERR axis and its scenario

The smallest of the five: `naming-rejected` and `nav-property-rejected` already assert status codes and message content, so several `ERR` requirements cite existing assertions.

- [ ] **Step 1: Author the `ERR` requirements**, citing the existing assertions in `NamingRejectedScenario.cs:120-130` and `NavPropertyRejectedScenario.cs:142-163` wherever they already discharge a rule.
- [ ] **Step 2: Add a scenario only for the error classes nothing covers** — a not-found read, and a write rejected for a schema-validation reason. If existing assertions cover a requirement, cite them rather than duplicating the check.
- [ ] **Step 3: Prove any new assertion can fail.**
- [ ] **Step 4: Run the full matrix.** Every scenario, every language, no `--scenarios` filter — the default run must exercise all of them.
```bash
dotnet test Iverson.Server/Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj
dotnet run --project Iverson.Server/Iverson.ClientConformance
```
- [ ] **Step 5: Confirm the tally is empty.** The report's untouched-requirement set must be empty — every requirement in the standard was exercised by at least one language. A non-empty set here means a requirement is cited but never reached at runtime.
- [ ] **Step 6: Commit.**
```bash
git add docs/standards Iverson.Server/Iverson.ClientConformance Iverson.Clients
git commit -m "add the error-contract conformance scenario and the ERR axis"
```

## Known issues inherited from spec

Accepted by Ben during brainstorming. These exist in the implementation by design.

**The gate proves citation, not falsifiability.** A cited assertion may still be incapable of failing. That branch's own history is the argument — a committed mutation marker made eight assertions unfailable while green, and a final review found an extracted helper the production path never called whose tests looked meaningful. The runtime tally narrows this and mutation testing remains the real answer; neither is replaced by the gate.

*This plan's response:* every task carries an explicit prove-it-can-fail step. That is a mitigation, not a repeal.

**CI execution is not addressed**, inheriting the harness's position.
