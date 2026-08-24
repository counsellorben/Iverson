# Critical Implementation Review: 2026-08-09-client-conformance-harness-implementation-plan (Round 3)

**Plan:** `/home/ben/repositories/Iverson/docs/plans/2026-08-09-client-conformance-harness-implementation-plan.md`
**Verified plan-level assumptions section:** present

⚠️ 5 commits since plan-write time (SHA `ece0171`); cited file:line references re-checked under §1. All five are this plan's own write/review/update cycle — no source drift.

Coverage re-derived against the current plan before consulting rounds 1–2. This round's sweep put the **negative and shared-type scenarios** — S3 and S4, the two whose preconditions are established by something other than their own steps — under the contract discipline for the first time. Rounds 1 and 2 worked over S1's plumbing and the verification mechanics respectively; neither asked what has to be true *before* S3 and S4 can execute at all.

## 0. Coverage enumeration

### Tasks × surfaces

| Task | Surface | Disposition |
|---|---|---|
| T1 | csproj block | ok — both `ProjectReference` paths and the two package versions re-resolved against `Iverson.LoadTest.csproj` |
| T1 | TOTP block | ok — env precedence, sole call site, `?? throw` reachability |
| T1 | preflight prose | ok — three named checks; failure messages name the down service |
| T1 | TokenBroker prose | ok — `IVERSON_LOADTEST_TENANT_ID` read and default match `Program.cs:44`; ensure-exists shape matches `ListTenants`-then-`CreateTenant` |
| T1 | report prose | ok — cell vocabulary and exit-code rule trace to the spec's Reporting section |
| T1 | `--keep` in the CLI surface | dropped — no step implements row deletion, but keys are run-scoped so accumulation changes no verdict; the spec's outcome does not fail |
| T1 | commands | ok — `.slnx` build; Api test project path exists |
| T2 | phase-document block | ok — every consumed field present; five-phase enum partitions the steps |
| T2 | build/exec table | ok — each command's tool matches its language; `-pl conformance -am` builds the client first; `dotnet run --no-build` consistent with build-once |
| T2 | `--keys` shape prose | ok — language-qualified map; the driver reports the inner map and the orchestrator adds the language it already knows |
| T2 | toolchain-skip prose | ok — keyed on build-command failure; degrades per language |
| T2 | Reregistrar block | ok — member names match `Program.cs:289-306`; **and the call site**: `RegisterSchema` is invoked per type, so S1's re-registration covers exactly the types its driver registered |
| T3 | model block | ok — FK convention, distinct nav name, `Guid[]` m2m, `OwnerId` present |
| T3 | auth-wiring prose | ok — public coordinator ctor over interceptor-carrying stubs |
| T3 | capture prose | ok — four builders re-checked as non-public |
| T3 | phase-dispatch block | ok — `OwnerId` stamped on write and update; failed-step-is-data; exec form delegated to T2 |
| T4–T7 | model / wiring / capture prose | ok — `many_to_many` exported in Python; TS `@IversonGuid()`; Go `MappingClient` interface; Java `CallCredentials` on all four stubs and the channel interceptor seam |
| T4–T7 | commands | ok — pytest scoping, `npm test`'s two stages, Go module-root build, the CodeQL-matching mvn invocation |
| T8 | registration-assertion prose | ok — kind-scoped; m2m exemption matches `RelationValidator.cs:20-24` |
| T8 | PostgresProbe prose | ok — `row_to_json` over property-named columns |
| T8 | three-way comparison prose | ok — named value set, separator-insensitive field resolution, parsed UUID comparison |
| T8 | S1 sequencing block | ok — **re-checked the precondition**, not just the order: S1's own table carries the orchestrator re-registration row between `register` and `write`, so the schema has rules before the first write |
| T9 | S2 prose | ok — client-side rejection precedes any RPC, `--scenario`-selected so it cannot abort S1's registration, .NET/Java skip reason correct |
| T9 | S3 prose | → §2.2 |
| T9 | commands | ok — commit scope covers both new scenario files and the three modified drivers |
| T10 | Step 1 prose (shared type declarations) | ok — same type names and shapes in five languages is what makes the cross-read meaningful |
| T10 | Step 2 prose (register once) | → §2.1 |
| T10 | Step 3 prose (write, cross-read) | ok — language-qualified iteration now yields twenty-five reads |
| T10 | commands | ok |
| T11 | mutation prose | ok — **checked that each named mutation actually reaches the driver**: Python and TypeScript drivers import client source, and Go/Java/.NET drivers are rebuilt per run by T2's table, so a mutation in client source is present in the next phase invocation |
| T11 | clean-tree commands | ok — five suites, all verified |

### Cross-task interface contracts

| Contract | Disposition |
|---|---|
| T2 `PhaseDocument` → T3–T7 | ok — each consumed field has a producing step |
| T2 build/exec table → T3–T7 | ok — all five languages present; drivers defer to it |
| T1 `--owner-id` → written rows | ok — stamped on write and update |
| T1 tenant → `--tenant` → server scoping | ok — pinned to the acting user's own tenant |
| T3–T7 `keys` → T8 (S1, one language) | ok — logical names unique within a document |
| T3–T7 `keys` → T2 union → T10 (S4, five languages) | ok — key space is language-qualified |
| T3 `typeDescriptor` → T2 `Reregistrar` (serialization boundary) | ok — proto3 JSON both ways from the same shared proto |
| T2 `Reregistrar` → **S1's caller** (T8) | ok — the re-registration row is in S1's step table |
| T2 `Reregistrar` → **S4's caller** (T10) | → §2.1 — second call site; the operation is never invoked for S4's shared types |
| T9 S3 → a registered type with a relation | → §2.2 — the scenario consumes a precondition no step in it establishes |
| T8 `Verifier` → T9, T10 | ok — defined before both consumers |

### Rule-like content, both directions

| Rule | Over-inclusion | Under-inclusion | Disposition |
|---|---|---|---|
| Registration assertion (kind-scoped) | flags a conforming descriptor | passes a broken one | ok — m2o/o2o strict, m2m FK-with-`isArray` |
| Three-way agreement (normalized) | disagrees on spelling | agrees vacuously | ok — named value set with separator-insensitive resolution |
| Key identity, within and across languages | collision | unaddressable row | ok — run-scoped UUIDs, language-qualified map |
| Toolchain-absent → skip | skips a present toolchain | fails on an absent one | ok — keyed on build failure |
| S3's expected-error predicate (`InvalidArgument` naming property and FK) | passes on a different `InvalidArgument` | misses the real rejection because a different status arrives first | → §2.2 (the under-inclusion direction) |
| S4's register-once rule | four drivers overwrite the descriptor | no one registers, or no one authorizes | → §2.1 (the second direction) |

## 1. Verified-plan-assumptions cross-check

Assumptions 1–38 re-read against cited evidence. **All 38 still hold.** Notes:

- **38** (added by the round-2 update) — reconfirmed independently: `EntityRepository.cs:7-9` is `row_to_json(t)` over quoted column names, and `SchemaBuilder.cs:52-54` builds each column as `new ColumnDescriptor(prop.Name, …)`.
- **19, 24** — the evaluator's `Denied`-on-null-rules and the `AuthorizationRules` member names are unchanged.
- **11** — six RPCs; `MappingWriteRequest` is `Post`'s and `Update`'s request type.

### Span check — one uncovered dependency

**Nothing covers the order in which the write path applies authorization and relation validation.** Assumption 19 covers *whether* a caller is denied; nothing covers *when* that check runs relative to the relation rejection S3 asserts on. Verified in-round: `ObjectMappingGrpcService.Post` calls `RequireSchema` (`:292`), then `EnforceWriteAuthorization` (`:294`), and only then `ValidateAndNormalizeRelations` (`:298`). → §2.2.

## 2. Literal-wrongness findings

### §2.1 — S4's shared types are never re-registered with authorization, so every interop write is denied

**Description.** The whole reason the harness registers twice is that only .NET's registrar accepts authorization rules, and a schema whose rules are null is writable by nobody. S1 handles this with an explicit orchestrator row between the `register` and `write` phases. S4 does not: Task 10, Step 2 covers *who registers* ("only the .NET driver runs a `register` phase for S4") and says nothing about the re-registration, and Step 3 goes straight from registration to "every language writes one row".

`SharedAuthor` and `SharedArticle` are therefore registered by the .NET driver's own `SchemaRegistrar` call — which, being a driver, passes no `authorizationByTypeName` — and are left with `Authorization` null. `EnforceWriteAuthorization` runs on every `Post` before anything else touches the payload, and the evaluator returns `Denied` for null rules. Every one of the five S4 writes fails, the twenty-five cross-reads have nothing to read, and the scenario reports `FAIL` for all five languages on a conforming stack.

This is `Reregistrar`'s **second call site**. Round 1 and round 2 both traced it at S1's caller, where the re-registration row is explicit in the step table; S4's caller sources the same operation from a different place — a single driver's register phase rather than each driver's own — and the step that would invoke it was never written.

Note the interaction with Step 2's register-once rule, which is correct and must be preserved: the re-registration has to happen **once**, after the .NET driver's register phase and before any driver's write phase, not once per language.

**Evidence.**
- Plan, Task 10 Steps 2–3 — no orchestrator re-registration between them.
- Plan, Task 8 S1 sequencing block — the contrasting row S1 does have: `driver register → orchestrator re-register with row permissions`.
- `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs:292-298` — `RequireSchema`, then `EnforceWriteAuthorization`, then relation validation.
- `Iverson.Server/Iverson.Api/Authorization/RowFieldAuthorizationEvaluator.cs:11-13` — `rules is null` returns `Denied`.
- Plan, "Registration and authorization are separate steps" is inherited from the spec as the reason the orchestrator re-registers at all.

**Proposed fix.** Add a step between Task 10's Steps 2 and 3: after the .NET driver's `register` phase for S4 and before any driver's `write` phase, the orchestrator re-registers `SharedAuthor` and `SharedArticle` from the .NET driver's reported `TypeDescriptor` with the authorization block set, exactly as S1 does — once, not per language.

### §2.2 — S3 asserts a relation-validation error it cannot reach: the scenario registers no type and authenticates no caller

**Description.** Task 9, Step 2 has the orchestrator hand-build a `Struct` with a navigation-property key, post it as a `MappingWriteRequest`, and assert `InvalidArgument` naming both the property and the foreign key. Two preconditions stand between the post and that assertion, and the step establishes neither.

First, the type must be registered *with a relation*, or `RequireSchema` throws before the payload is examined at all — and S3 is described as "orchestrator only, no driver", with no type of its own and no registration step. When the whole suite runs, a driver's type happens to be registered by an earlier scenario, so the assertion passes by accident of ordering; when S3 runs alone — which the `--scenarios` flag exists to allow, and which is exactly how someone would iterate on it — nothing has registered anything and the error is a missing-schema failure.

Second, `EnforceWriteAuthorization` runs *before* `ValidateAndNormalizeRelations`. The orchestrator's raw-gRPC post is the one call in the plan that bypasses the client libraries, and Step 2 says nothing about attaching the service bearer or the acting-user header. Without the acting-user token the evaluator denies the write on any rules-carrying schema, so the response is a permission failure rather than the `InvalidArgument` the assertion looks for — and the scenario reports `FAIL` while the behaviour it exists to verify is working correctly.

The under-inclusion direction is what makes this worth fixing rather than tolerating: the assertion looks for one specific rejection, and two *different* rejections arrive first, both of which look like S3 failing.

**Evidence.**
- Plan, Task 9 Step 2 — "orchestrator only, no driver"; no named type, no registration, no metadata.
- Plan, Task 1 Step 1 — `--scenarios` is a CLI selector, so a single-scenario run is a supported invocation.
- `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs:292` — `RequireSchema(request.TypeName)` precedes everything.
- `ObjectMappingGrpcService.cs:294-298` — `EnforceWriteAuthorization` at `:294`, `ValidateAndNormalizeRelations` at `:298`.
- `RowFieldAuthorizationEvaluator.cs:15-16` — a null acting user is `Denied` on a rules-carrying schema.

**Proposed fix.** State both preconditions in Task 9, Step 2: S3 registers its own single-type fixture (a type carrying one `many_to_one`) through the orchestrator's own registrar with the authorization block set, so the scenario is self-contained and runnable alone; and the raw-gRPC post carries the same two headers the drivers use — the service bearer and `x-acting-user-authorization` — so the request reaches relation validation rather than stopping at the authorization gate. Asserting the *status code* alongside the message text is what makes the distinction visible if either precondition regresses.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1** — the registration assertion failed every conforming `many_to_many`. Resolved: kind-scoped in Task 8 Step 1 with the `RelationValidator.cs:20-24` rationale recorded.
- **Round 1 §2.2** — `--owner-id` was produced and never written. Resolved: stamped on the write and update phases, propagated to all five driver tasks.
- **Round 1 §2.3** — `DriverRunner` consumed undefined build/exec commands. Resolved: the five-language table in Task 2 Step 2.
- **Round 1 §2.4** — the tenant was never pinned. Resolved: the acting user's own tenant via `IVERSON_LOADTEST_TENANT_ID`.
- **Round 2 §2.1** — the three-way comparison compared differently-spelled documents. Resolved: a named value set with separator-insensitive field resolution and parsed UUID comparison.
- **Round 2 §2.2** — S4's `--keys` union collapsed five rows into one. Resolved: language-qualified key space plus the iteration rule in Task 10 Step 3.
- **Round 1 and 2 span checks** — the tenant claim and the three legs' key spelling. Resolved: assumptions 37 and 38.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**

Two findings, and they share a shape that neither prior round was positioned to see: both are **preconditions established outside the step that depends on them**. S1 carries its re-registration row in its own table, so rounds 1 and 2 saw that contract satisfied and moved on; S4 sources the same operation differently and never invokes it, and S3 depends on a registered type and an authenticated caller that no step of its own provides. Both scenarios would report `FAIL` on a fully conforming stack — S4 for all five languages, S3 either always or only when run alone, which is worse because it passes in the suite and fails when someone iterates on it.

Both fixes are additive and local: one re-registration step in Task 10, and two sentences of precondition in Task 9 Step 2.

Everything rounds 1 and 2 fixed holds on a fresh read, and the two fixes most at risk of having been applied too narrowly were not: the `--keys` qualification reached both the shape in Task 2 and the iteration rule in Task 10, and the comparison normalization is stated as a named value set rather than as a string-munging rule.
