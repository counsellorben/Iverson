# Critical Implementation Review: 2026-08-31-vector-ranking-configuration-implementation-plan (Round 2)

**Plan:** /home/ben/repositories/Iverson/docs/plans/2026-08-31-vector-ranking-configuration-implementation-plan.md
**Verified plan-level assumptions section:** present

⚠️ 4 commits since plan-write time (SHA `0c14f49`); cited file:line references re-checked under §1. All four are documentation (`258fcd7` design review round 2, `b5a750e` the plan, `825be12` implementation review round 1, `91e565e` the round-1 fixes); no code changed.

Enumeration built before reading round 1 in detail; round 1's fix is a row here, not the search area.

## 0. Coverage enumeration

### Task 1 × surfaces

| Surface | Disposition |
|---|---|
| Step prose — "read the shipped constants" (Step 1) | ok — grep targets still resolve on both branches (`ResultReranker.cs:12` on `main`, `:19` on `decay-share-triple-b`; `ResultDiversifier.cs:12` both) |
| Code block — `VectorRankingOptions` (Step 2) | ok — `public` class, `public` consumers, no CS0051; substitution markers deliberate per Global Constraints |
| Code block — csproj reference (Step 3) | ok — 10.0.9, matching `Iverson.Embeddings.csproj`; no existing Options/Configuration in `Iverson.Vector` to conflict with |
| Code block — `ResultReranker` conversion (Step 4) | ok — `using` called out with its reason; short-circuit left intact |
| Step prose — `ResultDiversifier` conversion (Step 5) | ok — "the same way" carries Step 4's `using` instruction; both `Lambda` sites at `:76-77` named |
| Code block — `AddVectorRanking` (Step 6) | ok — both `using`s named; relocated `AddSingleton` lines match `ServiceCollectionExtensions.cs:50-51` verbatim; **and see the new dynamic row below on `IOptions<>` resolution** |
| Wiring text — `Program.cs` call (Step 7) | ok — insertion point after `:182-186` real; `using Iverson.Vector;` at `Program.cs:13` |
| Step prose — six construction sites (Step 8) | ok — all six re-confirmed including the two target-typed initializers; the `using Microsoft.Extensions.Options;` instruction here is what covers the four `Iverson.Api.Tests` files that lack it today (verified: absent from 4 of 5) |
| Step prose + code — tests (Step 9) | ok — `ConfigurationBuilder`/`AddInMemoryCollection` availability now covered by P25; `new ServiceCollection()` available via `Microsoft.Extensions.DependencyInjection` 10.0.9 in `Iverson.Vector.Tests`; the suggested non-default weights produce a fused score differing from the default on **both** candidate branches |
| Commands (Step 10) | ok — both invocations run this session |
| Commands (Step 11) | ok — `git add` names 12 explicit paths; counted against the task's Files list, which also has exactly 12. No `-A` |

### Task 2 × surfaces

| Surface | Disposition |
|---|---|
| Code block — `DecayOptions` (Step 1) | ok — `public`, file-scoped namespace `Iverson.Api.Grpc` matching the file path; a second class in the same file (Step 4) is valid under a file-scoped namespace |
| Code block + prose — `ComputeDecay` threading (Step 2) | ok — pure append; single application site `DecayFieldResolver.cs:85`; stale XML doc at `:69-75` explicitly flagged for update |
| Code block — `ObjectSearchGrpcService` and `DecayFor` (Step 3) | ok — append lands after `IResultDiversifier diversifier` and before the base clause; both call sites (`:260`, `:447`) checked separately as their own rows |
| Code block + prose — `AddDecayOptions` (Step 4, new this round) | ok — extension method in a non-generic non-nested `public static class`, valid; all three required `using`s named; `Program.cs` call resolves via `using Iverson.Api.Grpc;` at `:7`; `cfg` is `ConfigurationManager`, which implements `IConfiguration` |
| Step prose — call-site updates (Step 5) | ok — 7 `ComputeDecay` sites and 3 construction sites re-confirmed; "the value read in Step 1" resolves to the `HalfLifeDays` literal, keeping existing expectations valid |
| Step prose — tests (Step 6, rewritten this round) | ok — the four validation tests now call the real `AddDecayOptions`; host file `DecayFieldResolverTests.cs` carries `using Iverson.Api.Grpc;` at line 1, and `Iverson.Api.Tests` has both `Microsoft.Extensions.Configuration` and `Microsoft.Extensions.DependencyInjection` at 10.0.9 transitively |
| Commands (Steps 7-8) | ok — `git add` names 8 paths. Checked specifically because the round-1 fix could have introduced a file the list misses: it did not, because `AddDecayOptions` went into the already-listed `DecayOptions.cs` rather than a new file |

### Cross-task interface contracts

| Contract | Disposition |
|---|---|
| Task 2 consumes Task 1's rewritten call at `ObjectSearchGrpcServiceTests.cs:65-69` | ok — one call expression containing both `new ObjectSearchGrpcService(` and `new ResultReranker(), new ResultDiversifier())`; ordering forced and stated |
| Task 2 consumes the same at `DocumentTemplateValidationTests.cs:327-332` | ok — own row, not assumed a copy; same two-part structure confirmed |
| `ObjectSearchVectorIntegrationTests.cs:80` — both tasks edit it | ok — target-typed `new(`; Task 1 rewrites `:89-90`, Task 2 appends the tenth argument; both tasks list the file |
| Task 2 consumes `Program.cs` after Task 1's edit | ok — Task 1 adds `AddVectorRanking(cfg)`, Task 2 adds `AddDecayOptions(cfg)` beside it; distinct statements |
| Task 1 Step 8's `using Microsoft.Extensions.Options;` instruction is consumed by Task 2 Step 5 | ok — traced rather than assumed: `Options.Create` is absent from 4 of the 5 construction-site files today, and Task 1 Step 8 adds it to each; Task 1 runs first on the same files, so Task 2's `Options.Create(new DecayOptions())` compiles |
| `VectorRankingOptions` produced Step 2, consumed Steps 4, 5, 6, 8, 9 | ok — consumers use only the five members Step 2 defines |
| `DecayOptions` produced Step 1, consumed Steps 3, 4, 5, 6 | ok — consumers use only `HalfLifeDays` and `Section` |
| No contract crosses a persistence or serialization boundary | ok — configuration read once into memory at startup; no task writes an artifact a later task reads |

### Rule-like content and dynamic behaviour

| Rule / behaviour | Disposition |
|---|---|
| Finiteness guard (Task 1 Step 6) | over-inclusion: ok. under-inclusion: ok — `double.IsFinite` false for `NaN`/`±Infinity`, all four values named |
| Weight non-negativity | over-inclusion: ok — individual zero admitted and meaningful. under-inclusion: ok |
| All-zero guard | over-inclusion: ok — fires only on all-three-zero. under-inclusion: ok |
| `Lambda is < 0 or > 1` | over-inclusion: ok — endpoints admitted, both meaningful. under-inclusion: ok |
| `HalfLifeDays` finite and `> 0` (Task 2 Step 4) | over-inclusion: ok. under-inclusion: ok — finiteness precedes the range test |
| **Dynamic — `IOptions<T>` resolution when the open generic is already registered** | ok — the load-bearing check this round. `WebApplicationBuilder` calls `AddOptions()`, registering the open generic `IOptions<>` → `UnnamedOptionsManager<>` before either extension runs. Had the open generic won, `IOptions<VectorRankingOptions>.Value` would be a default-constructed instance and **configuration would be silently ignored while serving exactly the shipped defaults** — undetectable by any test the plan specifies. Probed with `AddOptions()` present: `WBase=0.9 Lambda=0.1`, the configured instance wins; and again with `AddOptions()` registered *after*, same result |
| Dynamic — two closed `IOptions<T>` registrations coexisting | ok — `IOptions<VectorRankingOptions>` and `IOptions<DecayOptions>` are distinct closed generics; no shadowing |
| Dynamic — startup validation under the test host | ok — `AuthTestWebApplicationFactory` boots `Program`, so both guards run; with no `VectorRanking` or `Decay` section configured, both bind to defaults and pass |
| Dynamic — options captured once into a readonly field | ok — both components are DI singletons over a singleton `IOptions<T>`; nothing mutates after bind, so no staleness or race |
| *Candidate:* `AddDecayOptions` lives in namespace `Iverson.Api.Grpc` rather than the conventional `Microsoft.Extensions.DependencyInjection` | **dropped** — fails literal-wrongness. `Program.cs:7` already has the using, so the call resolves; and `AddQdrant`/`AddVectorRanking` follow the same assembly-namespace pattern. Discoverability preference, not correctness |

## 1. Verified-plan-assumptions cross-check

All 26 listed assumptions still hold under a fresh read. Re-confirmed this round rather than carried forward: P5, P7, P12, P19, P20, P23, P24 (all cited lines unmoved — the drift's four commits touch only `docs/`), and the two rows added last round, P25 (`Iverson.Vector.Tests` still shows no Configuration package of its own) and P26 (all three files still carry `using Iverson.Api.Grpc;`).

### Span check — uncovered dependencies

Three dependencies the plan needs that no listed assumption states as scoped — two of them created by round 1's fix, which is what makes them new rather than re-raises. All verified in-round; all hold.

**1.a — Task 2 Step 6's four validation tests need `ConfigurationBuilder().AddInMemoryCollection` and `new ServiceCollection()` in `Iverson.Api.Tests`, and P25 is scoped to `Iverson.Vector.Tests` only.** Round 1's fix moved these tests from asserting a duplicated predicate to calling the real `AddDecayOptions`, which is what introduced the requirement in a second project. Verified in-round: `Iverson.Api.Tests` carries `Microsoft.Extensions.Configuration` 10.0.9 and `Microsoft.Extensions.DependencyInjection` 10.0.9 transitively. Holds.

**1.b — Step 6's tests are hosted in `DecayFieldResolverTests.cs`, which must see namespace `Iverson.Api.Grpc` for both `AddDecayOptions` and `DecayOptions`; P26 covers only the three `ObjectSearchGrpcService` construction-site files.** Verified in-round: `using Iverson.Api.Grpc;` at `DecayFieldResolverTests.cs:1`. Holds.

**1.c — The plan depends on a closed-generic `IOptions<T>` singleton taking precedence over the open-generic `IOptions<>` that `AddOptions()` registers.** P6 covers `AddSingleton(Options.Create(..))` registering the interface, but its evidence came from a bare `ServiceCollection` with no `AddOptions()` — which is not the collection either extension actually runs against. This is the gap between two correct assumptions rather than a defect in either. Verified in-round by probe, both registration orders: the closed generic wins, configured values are served. Holds.

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §3.1** (the `HalfLifeDays` guard was unreachable from a test, and Step 6's fallback silently picked a predicate-duplicating test). Resolved: Step 4 now builds `AddDecayOptions` as a callable extension in the already-created `DecayOptions.cs`, and Step 6's four validation tests invoke the real guard. The Architecture paragraph, both File Structure entries and Task 2's Files list were updated to match, so the plan no longer contradicts itself on where the guard lives. Task 1 and Task 2 are now symmetric on validation testing.
- **Round 1 §1 span 1.a** (no assumption covered `Program.cs` having `using Microsoft.Extensions.Options;` for `Options.Create`). Dissolved rather than covered: `Options.Create` moved out of `Program.cs` into the new extension, whose usings Step 4 specifies.
- **Round 1 §1 span 1.b** (`ConfigurationBuilder`/`AddInMemoryCollection` availability in `Iverson.Vector.Tests`). Resolved: P25 added with the transitive-package evidence.
- **Round 1 §1 span 1.c** (the three construction-site files seeing `Iverson.Api.Grpc`). Resolved: P26 added.

## 5. Recommendation

✅ **Approve as-is** — §1 has no failed assumptions, §2 and §3 are both empty. Every §0 row has a disposition, the one candidate generated this round failed the literal-wrongness test and was dropped rather than promoted, and all three span-check dependencies were verified in-round and hold. Plan is ready for `subagent-driven-development`.
