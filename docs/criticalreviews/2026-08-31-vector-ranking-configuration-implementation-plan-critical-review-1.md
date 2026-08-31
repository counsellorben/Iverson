# Critical Implementation Review: 2026-08-31-vector-ranking-configuration-implementation-plan (Round 1)

**Plan:** `/home/ben/repositories/Iverson/docs/plans/2026-08-31-vector-ranking-configuration-implementation-plan.md`
**Verified plan-level assumptions section:** present

⚠️ 2 commits since plan-write time (SHA `0c14f49`); cited file:line references re-checked under §1. Both are documentation (`258fcd7` the round-2 design review, `b5a750e` the plan itself); no code changed.

## 0. Coverage enumeration

### Task 1 × surfaces

| Surface | Disposition |
|---|---|
| Step prose — "read the shipped constants" derivation rule (Step 1) | ok — this is executable prose, not narration: it is what makes the plan branch-correct. `grep` targets confirmed to match on both branches (`ResultReranker.cs:12` on `main`, `:19` on `decay-share-triple-b`; `ResultDiversifier.cs:12` on both) |
| Code block — `VectorRankingOptions` (Step 2) | ok — `public`, `Section` const, four `{ get; set; }` defaults; mirrors `EmbeddingServiceOptions.cs`. Substitution markers are deliberate per the Global Constraint, not placeholders |
| Code block — csproj `PackageReference` (Step 3) | ok — `Microsoft.Extensions.Options.ConfigurationExtensions` 10.0.9, identical to `Iverson.Embeddings.csproj`; `dotnet list package --include-transitive` shows no existing Options/Configuration in `Iverson.Vector` to conflict with |
| Code block — `ResultReranker` conversion (Step 4) | ok — `public` class taking `public` options type, so no CS0051; the `using` is called out explicitly with its reason (P17); short-circuit correctly left intact |
| Step prose — `ResultDiversifier` conversion (Step 5) | ok — "the same way" inherits Step 4's `using` instruction; both `Lambda` references at `ResultDiversifier.cs:76-77` are named |
| Code block — `AddVectorRanking` (Step 6) | ok — signature, both `using`s named, `Options.Create` registration empirically confirmed to resolve as `IOptions<T>`; the two relocated `AddSingleton` lines match `ServiceCollectionExtensions.cs:50-51` verbatim |
| Wiring text — `Program.cs` call (Step 7) | ok — insertion point after `:182-186` is real; `using Iverson.Vector;` confirmed at `Program.cs:13` |
| Step prose — six construction sites (Step 8) | ok — all six re-confirmed, including the two target-typed `= new()` initializers a `new ResultReranker` grep misses; the "do not adjust any expected value" instruction is correct given defaults equal shipped constants |
| Step prose + code — validation and non-default tests (Step 9) | **→ §1 span 2** — requires `ConfigurationBuilder().AddInMemoryCollection`, which `Iverson.Vector.Tests` has no package for today. Verified in-round; resolved |
| Commands (Step 10) | ok — both `dotnet test <csproj>` invocations run in this session (123 and 830 passing) |
| Commands (Step 11) | ok — `git add` names 12 explicit paths, no `-A`; all 12 correspond to files the task actually touches; message style matches `git log --format=%s -6` |

### Task 2 × surfaces

| Surface | Disposition |
|---|---|
| Code block — `DecayOptions` (Step 1) | ok — `public` with the CS0051 reason and the ruled-out alternative both stated; namespace `Iverson.Api.Grpc` matches the file path |
| Code block + prose — `ComputeDecay` threading (Step 2) | ok — signature change is a pure append; the single application site is `DecayFieldResolver.cs:85` (`Math.Pow(0.5, ageDays / HalfLifeDays)`); the stale "fixed 180-day half-life" XML doc at `:69-75` is explicitly called out for update |
| Code block — `ObjectSearchGrpcService` parameter and `DecayFor` (Step 3) | ok — append lands after `IResultDiversifier diversifier` and before the `: ObjectSearchService.ObjectSearchServiceBase` base clause; both call sites (`:260`, `:447`) confirmed inside instance methods, checked separately rather than assumed copies |
| Code block — `Program.cs` bind and validate (Step 4) | ok — `Options.Create` resolves: `using Microsoft.Extensions.Options;` confirmed at `Program.cs:21` (see §1 span 1); `Bind` resolves via `Microsoft.NET.Sdk.Web`'s implicit `Microsoft.Extensions.Configuration` |
| Step prose — call-site updates (Step 5) | ok — 7 `ComputeDecay` sites and 3 construction sites re-confirmed by grep; all three construction-site files carry `using Iverson.Api.Grpc;`, so `DecayOptions` resolves unqualified (see §1 span 3) |
| Step prose — validation tests (Step 6) | **→ §3.1** — the guard lives in top-level statements and is not reachable from a test; the plan's fallback silently picks a predicate-duplicating test |
| Commands (Steps 7-8) | ok — same verified invocations; `git add` names 8 explicit paths matching the task's file list |

### Cross-task interface contracts

| Contract | Disposition |
|---|---|
| Task 2 consumes Task 1's rewritten construction expression at `ObjectSearchGrpcServiceTests.cs:65-69` | ok — traced to the real artifact: lines 65-69 are **one** call expression containing both `new ObjectSearchGrpcService(` and `new ResultReranker(), new ResultDiversifier())`. Task 1 rewrites the inner arguments; Task 2 appends a tenth. Ordering is therefore forced and the plan states it |
| Task 2 consumes the same at `DocumentTemplateValidationTests.cs:327-332` | ok — checked as its own row, not assumed a copy of the first: same two-part structure, `new ObjectSearchGrpcService(` at `:327` and `new ResultReranker(), new ResultDiversifier());` at `:332` |
| `ObjectSearchVectorIntegrationTests.cs:80` — Task 1 and Task 2 both edit it | ok — third call site, target-typed `new(`; Task 1 rewrites `:89-90`, Task 2 appends the tenth argument to the same call. Both tasks list this file |
| Task 2 consumes `Program.cs` after Task 1's edit | ok — Task 1 adds `AddVectorRanking(cfg)` after `:182-186`; Task 2 adds its block "beside" it. Different statements, no conflict; sequential execution |
| `VectorRankingOptions` produced by Task 1 Step 2, consumed by Steps 4, 5, 6, 8, 9 | ok — every consumer uses only `WBase`/`WCentroid`/`WDecay`/`Lambda`/`Section`, all defined in Step 2's block |
| `DecayOptions` produced by Task 2 Step 1, consumed by Steps 3, 4, 5, 6 | ok — consumers use only `HalfLifeDays` and `Section`, both defined |
| No contract crosses a persistence or serialization boundary | ok — configuration is read once into memory at startup; no task writes an artifact a later task reads |

### Rule-like content (both failure directions)

| Rule | Disposition |
|---|---|
| Finiteness guard, Task 1 Step 6 | over-inclusion: ok — rejects no finite value. under-inclusion: ok — `double.IsFinite` is false for `NaN`/`±Infinity` and all four values are named individually |
| Weight non-negativity | over-inclusion: ok — admits an individual zero, which is meaningful (disable one signal). under-inclusion: ok — non-finite values are caught by the preceding guard |
| All-zero guard | over-inclusion: ok — fires only when all three are zero. under-inclusion: ok |
| `Lambda is < 0 or > 1` | over-inclusion: ok — both endpoints admitted and both meaningful. under-inclusion: ok |
| `HalfLifeDays` finite and `> 0`, Task 2 Step 4 | over-inclusion: ok. under-inclusion: ok — finiteness precedes the range test, closing the `Infinity`-passes-`> 0` case the plan names |
| "Defaults equal the shipped constants" derivation, Global Constraints + Task 1 Step 1 | ok — both branches' real values confirmed; the rule is expressed as "read them", which cannot go stale, rather than as literals |
| *Candidate:* Task 1 Step 9's suggested non-default weights `WBase = 0.9, WCentroid = 0.1, WDecay = 0.0` include a zero weight | **dropped** — fails literal-wrongness. `WDecay = 0.0` passes validation (sum is 1.0) and produces a well-defined fused score that differs from the default; the test does what it is there to do |

## 1. Verified-plan-assumptions cross-check

All 24 listed assumptions still hold under a fresh read. Re-confirmed this round rather than carried forward: P1/P2 (both new files still absent), P5 (`ResultDiversifier.cs:14`, `IResultReranker.cs:9`, `IResultDiversifier.cs:8`), P7 (`ObjectSearchGrpcService.cs:39-40` still ends with `IResultDiversifier diversifier)`), P12 (the shared call expression at `ObjectSearchGrpcServiceTests.cs:65-69`), P19 (`ComputeDecay` still 7 / 1 / 1 by file), P20 (all three construction sites), P23 (`Program.cs:7` and `:13`), P24 (`DecayFieldResolver.cs:85`).

The drift note's two commits touch only `docs/`, so no cited `file:line` moved.

### Span check — uncovered dependencies

Three dependencies the plan needs that no listed assumption states as scoped. All three were verifiable in-round, and all three hold — none becomes a §3.

**1.a — Task 2 Step 4 calls `Options.Create` in `Program.cs`, but no assumption covers `Program.cs` having `using Microsoft.Extensions.Options;`.** P23 covers only the `Iverson.Api.Grpc` and `Iverson.Vector` usings, and `Microsoft.NET.Sdk.Web`'s implicit-usings set does **not** include `Microsoft.Extensions.Options`. Verified in-round: present at `Program.cs:21`. Holds.

**1.b — Task 1 Step 9 requires `ConfigurationBuilder().AddInMemoryCollection` in `Iverson.Vector.Tests`, and no assumption covers its availability.** This is the sharpest of the three: `dotnet list package --include-transitive` on `Iverson.Vector.Tests` today returns **no** `Microsoft.Extensions.Configuration` package at any version, so the availability is entirely contingent on what Task 1 Step 3's new package reference drags in transitively. Verified in-round by building a probe that replicates the real package sets — a library carrying only `Microsoft.Extensions.Options.ConfigurationExtensions` 10.0.9 plus `DependencyInjection.Abstractions`, and a consumer project carrying `Logging.Abstractions` + `DependencyInjection` and a `ProjectReference` to it — compiling exactly the call the plan specifies. Build succeeded. Holds.

**1.c — Task 2 Step 5 writes `Options.Create(new DecayOptions())` unqualified in three test files, and no assumption covers those files seeing namespace `Iverson.Api.Grpc`.** Verified in-round: `ObjectSearchGrpcServiceTests.cs`, `DocumentTemplateValidationTests.cs` and `ObjectSearchVectorIntegrationTests.cs` all carry `using Iverson.Api.Grpc;`. Holds.

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

### 3.1 — The `DecayOptions` guard is unreachable from a test, and the plan's fallback silently picks a test that cannot fail

**The choice.** Where the `HalfLifeDays` validation lives, given that it must be both (a) inline in `Program.cs` per the spec and (b) covered by a validation test per the spec.

**Why it's forced.** `Program.cs` is top-level statements — `grep -c "^class \|^internal class \|^public class "` returns **0**, and the file opens `var builder = WebApplication.CreateBuilder(args);` at `:23`. There is no type a test can call, so the guard Task 2 Step 4 places there is not reachable from a unit test. `Iverson.Api.Tests` has no existing pattern for asserting startup failure either; `AuthTestWebApplicationFactory` sets environment variables to make the host *succeed*, and no test in `Helpers/` asserts a throw.

The spec requires both things and they conflict. The plan resolved the conflict without naming it: Task 2 Step 6 says *"If the guard is not reachable from a test, assert the same predicate over a bound `DecayOptions` built from an in-memory configuration."* That instruction produces a test that re-implements the guard and then asserts its own re-implementation. It passes whether `Program.cs`'s guard is correct, wrong, or deleted entirely — so it does not test the thing the spec asked to be tested. Note the asymmetry with Task 1, where the equivalent guard lives in `AddVectorRanking` and *is* directly callable, so Task 1 Step 9's four validation tests exercise the real code.

**The options.**

- **(a) Move the `DecayOptions` bind-and-validate into a testable seam** — e.g. `AddDecayOptions(this IServiceCollection, IConfiguration)` in `Iverson.Api`, mirroring `AddVectorRanking` exactly. The tests then call the real guard, and Task 1 and Task 2 become symmetric. Costs a small departure from the spec's "bound and validated inline in `Program.cs`" wording, which was justified on the grounds that `Iverson.Api` is the composition root — a reason about tidiness, not about testability.
- **(b) Keep it inline and test it through the host** — assert that `WebApplicationFactory` throws when `Decay__HalfLifeDays` is set to `-1` / `NaN` / `Infinity`. Tests the real guard end-to-end, but introduces a startup-failure test pattern the repo does not currently have, in a factory whose existing job is to make the host start.
- **(c) Keep it inline and accept that this guard has no test** — drop Task 2 Step 6's validation tests rather than write predicate-duplicating ones, and record in the plan that `HalfLifeDays` validation is verified by inspection only. Honest, and cheaper than either alternative, but leaves one of the spec's five required validation tests undelivered.

## 5. Recommendation

🛑 **Surface forced decisions to user** — §3 non-empty. §1 has no failed assumptions and §2 is empty; the sole blocker is 3.1, which needs a pick before `subagent-driven-development`. Note that the three span-check dependencies were all verified in-round and hold, so nothing else is outstanding.
