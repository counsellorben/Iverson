# Critical Implementation Review: 2026-09-13-popularity-sweep-fault-abort-implementation-plan (Round 1)

**Plan:** `/home/ben/repositories/Iverson/docs/plans/2026-09-13-popularity-sweep-fault-abort-implementation-plan.md`
**Artifact HEAD at review:** 95f3b42c8d7badad48eb4d1fa619425ee001c576
**Verified plan-level assumptions section:** present

⚠️ 2 commits since plan-write time (SHA `f18081d`); cited file:line references re-checked under §1.

Both intervening commits are docs-only — `6a219ce` adds the CDR round-2 review file, `95f3b42` adds the plan itself (`git log --oneline f18081d..HEAD`; `git show --stat` on each touches only `docs/`). No code drift.

Round 1: `ls docs/criticalreviews/ | grep popularity-sweep | grep implementation-plan-critical-review` → empty. §4 omitted; the round-N>1 mandatory row families (fix neighbourhoods, intersected fix texts, amendment hunks) do not apply, but the anchor header above is recorded for the next round.

**Probe protocol.** This round ran the plan rather than inspecting it. The plan's code blocks were transcribed verbatim into the four target files, built, tested, mutated, and reverted. Tree restored byte-identical: md5s of all four targets before and after match (`d11de3ea…`, `ad273b4c…`, `b3f98366…`, `458024dc…`), and `git status --porcelain` shows only the two untracked directories (`docs/security/`, `scratchpad/popularity/`) that were present at session start.

---

## 0. Coverage enumeration

### Task 1 × surfaces

| # | Surface | Disposition |
|---|---|---|
| T1.1 | Step 1 prose — enum insertion point ("after the namespace line's following blank line, before the `/// <summary>` block that documents `PopularitySignalUpdater`") | ok — [existence] `PopularitySignalConsumer.cs:23` is `namespace Iverson.Api.Consumers;`, `:24` blank, `:25` opens the `<summary>`. Transcribed there; `dotnet build Iverson.Server/Iverson.Api/Iverson.Api.csproj` → `Build succeeded. 0 Error(s)`. |
| T1.2 | Step 1 code — `internal enum PopularityUpdateOutcome { Updated, Skipped, Failed }` | ok — [negative] name unused repo-wide: `grep -rn "PopularityUpdateOutcome" --include=*.cs .` (worktrees excluded) → 0 hits. Compiles at file scope. |
| T1.3 | Step 1 prose — `internal` accessibility divergence, forward-ref to assumption #10 | ok — [compat] see §1.10; `internal` compiles against all use sites (build + 48-test run green). |
| T1.4 | Step 2 prose — five text anchors, each named by enclosing block rather than line number | ok — [totality] each anchor resolved uniquely on a fresh file. `internal async Task UpdateAsync(` → `:44` only; the aggregate `catch` logging `"AggregateAsync failed for parent=…"` → `:80-83`; `if (result is null)` logging `"AggregateAsync returned null for parent=…"` → `:86-95`; `catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)` → `:134`; method end `:141`. The two bare `return;` edits are disambiguated by their enclosing block, so the three other `return;` statements in the file (`:192`, `:195`, `:198`, in `DispatchAsync`) are not candidates. |
| T1.5 | Step 2 prose — the histogram `catch` gets **no** return (falls through to `Updated`) | ok — [absence] `:113-118` has no return; after transcription the histogram catch falls to the new `return PopularityUpdateOutcome.Updated;`, and `Dispatch_HistogramFails_StillWritesCountWithEmptySeries` stayed green. |
| T1.6 | Step 2 prose — "Do **not** add a catch-all…; the fifth exit path must keep propagating" | ok — [compat] see R3; `UpdateAsync_SetPayloadThrowsNonNotFound_Propagates` passed on the transcribed code, and is the suite's only assertion that a swallowing catch-all would fail. |
| T1.7 | Step 3 code — `BuildUpdater()` helper | ok — [compat] name does not collide (compiles); `PopularitySignalUpdater`'s ctor is `(search, vector, tenantScope, logger)` per `PopularitySignalConsumerTests.cs:61-62`; a `private` member returning an `internal` type is legal (the file's existing `private PopularitySignalConsumer BuildSut(...)` at `:59` is the precedent). Built clean. |
| T1.8 | Step 4 code — the four `[Fact]`s | ok — [compat] all four transcribed verbatim and run: `UpdateAsync_AggregateThrows_ReturnsFailed`, `UpdateAsync_AggregateReturnsNull_ReturnsSkipped`, `UpdateAsync_SetPayloadNotFound_ReturnsSkipped`, `UpdateAsync_SetPayloadThrowsNonNotFound_Propagates` — 4 passed. `act.Should().ThrowAsync<RpcException>().Where(...)` resolves against `Func<Task<PopularityUpdateOutcome>>` (FluentAssertions 7.0.0 generic async overload) — compiles and passes. |
| T1.9 | Step 4 code — `StubCount(3)` is required in tests 3 and 4 to reach the Qdrant write | ok — [existence] `StubCount` filters `a.Kind == AggregationKind.Count` (`:124-131`); the consumer suite's `CommentSchema()` default leaves `PopularitySignalColumn` null (`:90`, `:105`), so no histogram call is issued and the Count stub alone carries the method past `if (result is null)`. This closes CDR round 2's routed residue ("test 6's text omits the aggregate stub"). |
| T1.10 | Step 4 prose — "Spec test 5 needs **no** new test; the existing `Dispatch_*` tests … must stay green" | ok — [totality] all 15 pre-existing `PopularitySignalConsumerTests` methods (including the 3 `Dispatch_AllEventTypes` theory cases) stayed green under the transcribed change; 41→45 with zero pre-existing failures. |
| T1.11 | Step 5 commands — `dotnet build … Iverson.Api.csproj` then `dotnet test … --filter "FullyQualifiedName~PopularitySignal"`, expect 45 | ok on count — [totality] Task-1-only transcription run: `Passed! - Failed: 0, Passed: 45, Skipped: 0, Total: 45`. See §1.22 for the filter's container dependency. |
| T1.12 | Step 6 commands — `git add` two paths + lowercase-imperative commit message | ok — [existence] both paths are tracked (`Iverson.Server/**` is not gitignored, unlike `docs/`), so no `-f` is needed; message style matches `git log --oneline -20` (see §1.24). |

### Task 2 × surfaces

| # | Surface | Disposition |
|---|---|---|
| T2.1 | Step 1 code — `private const int MaxConsecutiveFailures = 5;` beside `PageSize` | ok — [existence] `PopularitySignalReconciliationWorker.cs:26-27` is `SweepInterval` (`private static readonly TimeSpan`) then `PageSize` (`private const int`); the new const matches the latter. Compiles; no CS0414/unused warning (it is read at the `else if`). |
| T2.2 | Step 2 prose — "Declare `var consecutiveFailures = 0;` beside `string? afterKey = null;` — **outside** the paging `while`" | ok — [existence] `afterKey` is declared at `:54`, the paging `while` opens at `:55`; the counter placed at `:55` survives page boundaries. Exercised: `SweepSignalAsync_AllParentsSkipped…` and `…ScatteredNonConsecutiveFailures…` both cross a page boundary in the transcribed run. |
| T2.3 | Step 2 code — seeded `outcome`, try/catch, reset/increment/abort block | ok — [compat] transcribed verbatim; builds and all three new tests pass. The seeded `Failed` makes the pre-existing `catch` feed the counter with no second code path, as the spec requires; `SweepSignalAsync_OneParentUpdateThrows_DoesNotStopSweepForRemainingParents` (which drives exactly that catch) stayed green. |
| T2.4 | Step 2 prose — "The existing per-parent `LogError` is unchanged" | ok — [absence] the transcribed block reproduces `:76-78` byte-for-byte; `git diff` of the probe showed the `logger.LogError(ex, …)` call untouched. |
| T2.5 | Step 2 prose — **ruling** that the `if (row.TenantId is null) continue;` guard neither increments nor resets | ok (non-load-bearing for the outcome) — [compat] the ruling is stated with its cost and its direction (conservative: a streak separated only by null-tenant rows still aborts). No test pins it, and none is required: `UpdateAsync` is never called on that path, so no outcome exists to classify. `SweepSignalAsync_RowWithNullTenantId_IsSkippedWithoutCallingUpdate` stayed green. |
| T2.6 | Step 3 code — `BuildSut` gains a second optional parameter | ok — [totality] declaration at `:70`; all five call sites pass zero arguments — `grep -n "BuildSut(" …WorkerTests.cs` → `:70` (decl), `:102`, `:119`, `:131`, `:149`, `:181`, each literally `BuildSut()`. Transcribed; compiles, and all five pre-existing worker tests stayed green. |
| T2.7 | Step 3 prose — add `using Microsoft.Extensions.Logging;`, keep `.Abstractions` | ok — [compat] see §1.25; added alongside the file's nine existing imports and three aliases; no ambiguity, `Build succeeded. 0 Error(s)`. |
| T2.8 | Step 3 prose — copy `RecordingLogger<T>` verbatim from `DocumentRerenderQueueWorkerTests.cs:304-316` | ok — [existence] the donor class occupies exactly `:304-316` (`private sealed class RecordingLogger<T> : ILogger<T>` at `:304`, closing brace `:316`); no `RecordingLogger` already exists in the target file. Copied; compiles and records. |
| T2.9 | Step 4 code — `StubAggregateFailingFor(params string[])` | ok — [compat] see R2; the `.Returns(ci => …)` lambda form on a `Task<AggregationResult?>`-returning member compiles (NSubstitute 5.3.0 binds `Returns<T>(this T, Func<CallInfo,T>)` with `T = Task<AggregationResult?>`; the `Task<T>`-unwrapping overload is not applicable because the lambda body is not an `AggregationResult?`), and the faulted branch throws where intended — proven by the key-discriminating behaviour of tests 3 and 4, which cannot both pass unless exactly the named keys fault. |
| T2.10 | Step 4 code — `Page(int count, int from = 1)` | ok — [existence] `KeyedTenantRow` is `public sealed record KeyedTenantRow(string Key, string? TenantId)` (`Iverson.Sql/KeyedRow.cs:5`); `Enumerable.Range`/`Select`/`ToArray` come from the SDK's implicit `System.Linq`. Compiles and produces `article-1…article-N`. (`from` is never passed a non-default value — a dead default, not a defect.) |
| T2.11 | Step 4 code — the three `[Fact]`s | ok — [totality] each of the three is non-vacuous; see the mutation column in R1. All three pass on the transcribed code. |
| T2.12 | Step 4 prose — the `StubAggregateFailingFor` doc comment ("Fails the first `failures` parents") names a parameter that does not exist (`failingKeys`) | dropped — stale comment text in a test helper; changes no behaviour and fails the literal-wrongness test. |
| T2.13 | Step 5 commands — expect 48 | ok — [totality] full transcription run: `Total tests: 48 / Passed: 48`. See §1.22 for the filter's container dependency. |
| T2.14 | Step 6 commands — `git add` two paths + commit | ok — [existence] same check as T1.12. |

### Cross-task interface contracts

| # | Contract | Disposition |
|---|---|---|
| C1 | Task 2 consumes the `PopularityUpdateOutcome` enum Task 1 produces | ok — [compat] the worker's `var outcome = PopularityUpdateOutcome.Failed;` resolves because `Iverson.Api.Reconciliation` already imports `Iverson.Api.Consumers` (`PopularitySignalReconciliationWorker.cs:1`), and the enum is file-scope in `Iverson.Api.Consumers`. Same assembly, so `internal` suffices. Compiled in the full transcription. |
| C2 | Task 2 consumes `UpdateAsync`'s new return type | ok — [compat] `outcome = await updater.UpdateAsync(...)` binds `Task<PopularityUpdateOutcome>` → `PopularityUpdateOutcome`. Compiled. |
| C3 | Task 1's **intermediate** state must build and be green before Task 2 exists | ok — [totality] verified in isolation, not assumed: Task-1 production edit alone → `dotnet build Iverson.Server/Iverson.Api/Iverson.Api.csproj` = `Build succeeded. 0 Error(s)` (the worker's un-updated bare `await updater.UpdateAsync(...)` at `:72` discards the new value with no diagnostic); Task-1 production + test edits → `Passed: 45, Failed: 0`. |
| C4a | Call site 1 — `PopularitySignalConsumer.cs:217`, bare `await updater.UpdateAsync(...);` | ok — [compat] bare expression statement; discards the value silently. Compiled clean (Task-1-only build, 0 errors, no new warning). |
| C4b | Call site 2 — `PopularitySignalConsumer.cs:223`, bare `await updater.UpdateAsync(...);` | ok — [compat] separate row per the one-row-per-caller rule. Same shape, same evidence as C4a (the Task-1-only build covers both sites); both are inside `DispatchAsync`'s `foreach`/`try`, neither assigns. |
| C4c | Call site 3 — `PopularitySignalReconciliationWorker.cs:72` | ok — [compat] the only site Task 2 rewrites; verified at T2.3 (transcribed, built, three new tests plus all five pre-existing worker tests green). |
| C5 | DI/consumer population beyond the three call sites | ok — [totality] `grep -rn "PopularitySignalUpdater" --include=*.cs .` (worktrees and `obj/` excluded) → 13 hits: the type declaration + 2 ctor params (`PopularitySignalConsumer.cs:166`, `…Worker.cs:23`), 1 DI registration (`Program.cs:308`, `AddSingleton<…PopularitySignalUpdater>()` — signature-agnostic), 2 test constructions, and 7 doc-comment/comment mentions. No other consumer of `UpdateAsync` exists. |
| C6 | Test-count arithmetic 41 → 45 → 48 matches the `[Fact]`s added | ok — [totality] plan adds 4 `[Fact]`s in Task 1 and 3 in Task 2 = 7; measured 41 baseline → 45 after Task 1 → 48 after Task 2. Exact match. |

### Rule-like content (both failure directions)

**R1 — the counter rule `if (outcome is not Failed) reset; else if (++n >= 5) abort;`**

- over: ok — [totality] a non-fault must never count. Mutation probe: rewrote the guard to `if (outcome is Updated)` (i.e. `Skipped` counts) → `SweepSignalAsync_AllParentsSkipped_RunsToCompletionWithoutAbandoning` **failed** (1 failed / 7 passed). The reset arm also fires on `Updated`: mutation to cumulative counting (`if (outcome is Failed && ++n >= 5)`, reset removed) → `SweepSignalAsync_ScatteredNonConsecutiveFailures_DoesNotAbandon` **failed**. Both directions of "what must not count" are pinned by a test that actually falsifies.
- under: ok — [totality] producers of a counted event, enumerated: (a) the aggregate `catch` → `Failed`; (b) any exception escaping `UpdateAsync` → caught by the worker, `outcome` stays at its `Failed` seed. No third producer exists (the only other exits return `Skipped` or `Updated`). Mutation probe: `MaxConsecutiveFailures = 500` → `SweepSignalAsync_FiveConsecutiveFailures_AbandonsWithExactlyOneSummary` **failed** with `Expected logs.Entries to contain a single item matching …Contains("Abandoning sweep"), but the collection is empty` — i.e. it fails for the right reason, and the unstubbed page-2 continuation returned an empty sequence (loop broke, 223 ms) rather than null-dereferencing or hanging. `OperationCanceledException` cannot reach the seeded-`Failed` path from a sweep cancellation: neither `UpdateAsync` (6 params, `:44-46`), nor `IEngagementStoreSearchService.AggregateAsync` (7 params, `IEngagementStoreRoles.cs:36-43`), nor `IVectorWriteService.SetPayloadAsync` (3 params) takes a `CancellationToken`, so no ct is plumbed in.

**R2 — `StubAggregateFailingFor`'s key-identity rule `q?.Clauses.FirstOrDefault()?.Value?.StringVal`**

- over (a non-failing parent faults anyway): ok — [totality] `failingKeys.Contains(key)` is exact ordinal equality over `article-1…article-9`, so no prefix collision. If the chain resolved to `null` or a constant, *every* key would take the success branch and test 3 could not abort — test 3 passed. If it resolved to a constant matching a failing key, *every* key would fault and test 4 would abort — test 4 passed. The two tests are jointly falsifying on this rule.
- under (a named failing parent does not fault): ok — [compat] the value the stub reads is the one `UpdateAsync` writes: `PopularitySignalConsumer.cs:48-60` builds exactly one `SearchClause` with `Value = new SearchValue { StringVal = parentKey }`, and `parentKey` is `row.Key` at the worker call site (`:72`). `FirstOrDefault()` over a one-element `Clauses` is that clause. Confirmed at runtime by test 3 aborting at exactly the fifth parent (`after 5 consecutive parent-update failures`).

**R3 — the exit-path → outcome mapping (all five producers of an exit from `UpdateAsync`)**

- over: ok — [totality] each of the four returned exits is reachable and distinct. `Failed` ← aggregate catch (`:78-84`) — pinned by `UpdateAsync_AggregateThrows_ReturnsFailed`. `Skipped` ← null result (`:86-95`) — pinned by `UpdateAsync_AggregateReturnsNull_ReturnsSkipped`. `Skipped` ← Qdrant `NotFound` (`:134-140`) — pinned by `UpdateAsync_SetPayloadNotFound_ReturnsSkipped`, the test the plan adds beyond the spec's six; without Step 2's `return Skipped;` inside that catch it would fall to the trailing `return Updated;`, which is exactly the mislabelling spec §2 warns about. `Updated` ← fall-through, including the histogram catch (`:113-118`, no return) — pinned by `Dispatch_HistogramFails_…` staying green.
- under: ok — [totality] the fifth exit (any non-`NotFound` exception on the Qdrant write, or anything thrown outside the two guarded awaits) must **not** become an outcome. Both inner catches are `when (ex is not OperationCanceledException)` (`:78`, `:113`) and the Qdrant catch is `RpcException` filtered to `NotFound` (`:134`), so the propagating class survives the change; `UpdateAsync_SetPayloadThrowsNonNotFound_Propagates` passed on the transcribed code and is the only assertion in either suite that a swallowing catch-all would fail.

**R4 — the test-selection rule `--filter "FullyQualifiedName~PopularitySignal"` vs the population of tests it matches**

- over: → §1.22 — [totality] the filter matches **five** test classes, not the two the plan edits. Full population, dispositioned: `Grpc.PopularitySignalOptionsTests` (17 — pure unit, unaffected, green), `Consumers.PopularitySignalConsumerTests` (14 baseline — edited by Task 1), `Reconciliation.PopularitySignalReconciliationWorkerTests` (5 — edited by Task 2), `Schema.SchemaBuilderTests` (4 — pure unit, unaffected, green), `Grpc.ObjectSearchVectorIntegrationTests` (1: `SearchChunks_ReflectsPopularitySignal_ForConfiguredType` — **Testcontainers**, starts a Ryuk reaper plus a Qdrant container on 6334). The fifth cell is what assumption #22 denies exists.
- under: ok — [totality] nothing the plan edits escapes the filter: both edited classes' names contain `PopularitySignal`, so every method the plan adds is selected. Confirmed by the measured deltas (+4, +3).

---

## 1. Verified-plan-assumptions cross-check

Fresh read of every cited reference. 24 of 25 still hold; **#22 fails**.

1. **still holds** — [existence] `wc -l` → 300 / 85 / 499 / 188, matching the plan exactly.
2. **still holds** — [existence] `DocumentRerenderQueueWorkerTests.cs` = 317 lines, in `Iverson.Api.Tests/Reconciliation/` alongside the target.
3. **still holds** — [existence] `PopularitySignalConsumer.cs:44-46` is `internal async Task UpdateAsync(` with six parameters (`parentSchema, signal, childSchema, relation, parentKey, tenantId`).
4. **still holds** — [existence] `return;` at `:83` and `:94`; `catch (RpcException …NotFound)` at `:134` closing `:140`; method closes `:141`.
5. **still holds** — [existence] `catch (Exception ex) when (ex is not OperationCanceledException)` at `:78` and `:113`.
6. **still holds** — [totality] re-ran the population unfiltered rather than trusting the `grep -i popularit` narrowing: `grep -rn "UpdateAsync" --include=*.cs .` (worktrees excluded) → within `Iverson.Server/`, exactly the declaration `:44` plus call sites `:217`, `:223`, `:72`. Zero calls in any test file (the four test-file hits are comment text at `…ConsumerTests.cs:71,411` and `…WorkerTests.cs:87,137,159,173`). Three call sites, all production.
7. **still holds** — [negative] the same unfiltered grep returns no other `.UpdateAsync(` anywhere under `Iverson.Server/`. The only other matches in the repo are `EntityCoordinator.cs:121,169,172` and `EntityCoordinatorMappedWriteTests.cs:48,53`, both under `Iverson.Clients/DotNet/` — a different solution tree with no reference to `Iverson.Api`.
8. **still holds** — [compat] `:217` and `:223` are bare `await updater.UpdateAsync(...);` statements. Verified by execution, not inspection: the Task-1-only production edit builds `Iverson.Api` with `0 Error(s)` and emits no new diagnostic at either site.
9. **still holds** — [existence] `namespace Iverson.Api.Consumers;` is file-scoped at `:23`.
10. **still holds**, and the divergence is safe — [totality] all four file-scope enums in `Iverson.Api` are `public` and none is `internal`: `SchemaDescriptor.cs:107` (`EnrichmentKind`), `:137` (`RelationKind`), `IRowFieldAuthorizationEvaluator.cs:6` (`AuthorizationAction`), `DocumentTemplate.cs:7` (`DocumentSegmentKind`) — a repo-wide enum grep over `Iverson.Api/` returns exactly these four, so the 4/4 count is the whole population, not a sample. `internal` compiles against every use site: the consumer and worker are both in `Iverson.Api`, and `Iverson.Api.csproj:10-12` grants `InternalsVisibleTo("Iverson.Api.Tests")`, so the test assembly can name `PopularityUpdateOutcome` in `outcome.Should().Be(...)`. Proven by the full transcription building and running 48 green.
11. **still holds** — [existence] `LoggingExtensions.cs:5` is `internal static string SanitizeForLog(this string value)` in `namespace Iverson.Api`; already applied at `…Worker.cs:78`.
12. **still holds** — [existence] `:26` `private static readonly TimeSpan SweepInterval`, `:27` `private const int PageSize = 500;`.
13. **still holds** — [totality] declaration `:70`; call sites `:102`, `:119`, `:131`, `:149`, `:181`, all `BuildSut()` with zero arguments. Exhaustive: `grep -n "BuildSut(" …WorkerTests.cs` returns exactly those six lines.
14. **still holds** — [compat] `DocumentRerenderQueueWorkerTests.cs:306` declares `public List<(LogLevel Level, string Message)> Entries { get; } = [];` and `:315` is `Entries.Add((logLevel, formatter(state, exception)));`. The `.ContainSingle(predicate).Which.Message` chain the plan's test 3 builds on this compiles and passes.
15. **still holds** — [existence] `…ConsumerTests.cs:124-131`, `Arg.Is<AggregationDescriptor>(a => a.Kind == AggregationKind.Count)`.
16. **still holds** — [existence] `…WorkerTests.cs:79-85`, `Arg.Any<AggregationDescriptor>()`.
17. **still holds** — [existence] `…ConsumerTests.cs:374` is `.Returns((EngagementAggResult?)null);`.
18. **still holds** — [existence] `…ConsumerTests.cs:420` is `.Returns(Task.FromException(new RpcException(new Status(StatusCode.Unavailable, "down"))));`; `using Grpc.Core;` at `:3`.
19. **still holds** — [compat] `ArticleSchema()` `:73`, `CommentSchema()` `:90`, `TenantA` `:35`, `ArticleId` `:37`, `Relations = [new SchemaRelationDescriptor(...)]` `:83`. All four new tests construct successfully against them.
20. **still holds** — [existence] `…ConsumerTests.cs:61-62` and `…WorkerTests.cs:72-73` both construct `new PopularitySignalUpdater(_search, _vector, _tenantScope, NullLogger<PopularitySignalUpdater>.Instance)`.
21. **still holds on its load-bearing half** — [totality] re-ran the exact command: `Passed! - Failed: 0, Passed: 41, Skipped: 0, Total: 41, Duration: 6 s`. The baseline count and duration reproduce precisely. The sub-clause "scopes to both suites" understates the selection — the filter reaches five classes (R4) — but the 41 figure and the green baseline are correct, so the assumption's operative content holds. The container consequence of the extra classes is #22's failure below, not this row's.
22. **FAILED** — the cited evidence is contradicted by a fresh run of the same command. The assumption states "Both suites are pure NSubstitute unit tests — **no Testcontainers**, so no Ryuk/container prefix is needed", with the evidence "the 41-test run completed in 6 s with **no container startup**". A fresh baseline run of `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~PopularitySignal"` emits:

    ```
    [testcontainers.org 00:00:01.44] Connected to Docker:
    [testcontainers.org 00:00:02.21] Docker container 69d2c6130e40 created
    [testcontainers.org 00:00:03.94] Docker container fdc187490218 created
    [testcontainers.org 00:00:04.13] Execute "… nc -vz -w 1 localhost 6334 …" at Docker container fdc187490218
      Passed Iverson.Api.Tests.Grpc.ObjectSearchVectorIntegrationTests.SearchChunks_ReflectsPopularitySignal_ForConfiguredType [1 s]
    ```

    Two containers start — a reaper and a Qdrant instance — because `ObjectSearchVectorIntegrationTests.SearchChunks_ReflectsPopularitySignal_ForConfiguredType` matches `~PopularitySignal` and is a Testcontainers integration test (`ObjectSearchVectorIntegrationTests.cs:29` constructs a `ContainerBuilder`). The two *edited* suites are indeed pure NSubstitute, but that is not what discharges the command's container requirement, and the stated evidence ("no container startup") is false.

    This is load-bearing because the plan converts it into an execution gate: line 63 asserts "**Any failure after Task 1 or Task 2 is introduced by this plan**", and Steps T1.11/T2.13 instruct the implementer to expect `0 failed`. On a machine where the container runtime is down, unreachable, or configured with an inherited `TESTCONTAINERS_RYUK_DISABLED` mismatch, Step 5 reports a red suite from a test the plan never touches, and the plan's own rule directs the implementer to attribute it to their edit. The change itself remains correct — the 48-test transcription run is green — so this is a defect in the plan's verification instructions, not in the code it prescribes.

    **Proposed fix.** Amend #22 to state what is true, and narrow the two Step 5 commands so the gate cannot be tripped by an untouched integration test. Replace the filter in Task 1 Step 5 and Task 2 Step 5 with the class-scoped form, and restate the expected counts against it:
    `--filter "FullyQualifiedName~PopularitySignalConsumerTests|FullyQualifiedName~PopularitySignalReconciliationWorkerTests"` → baseline **19**, **23** after Task 1, **26** after Task 2. Keep the broader `~PopularitySignal` run as a final whole-feature check with a note that it requires a running container runtime.
    `Evidence:` [totality] measured, not derived, and the proposed filter string itself was executed. Baseline per-class counts from `dotnet test … --filter "FullyQualifiedName~PopularitySignal" --logger "console;verbosity=normal"`, test names bucketed by class: `PopularitySignalOptionsTests` 17, `PopularitySignalConsumerTests` 14, `PopularitySignalReconciliationWorkerTests` 5, `SchemaBuilderTests` 4, `ObjectSearchVectorIntegrationTests` 1 (= 41). The two edited classes total 19; the plan adds 4 then 3 `[Fact]`s (transcription-measured deltas 41→45→48), giving 19→23→26. The two-clause form was then run end-to-end by the controller at review close — `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~PopularitySignalConsumerTests|FullyQualifiedName~PopularitySignalReconciliationWorkerTests"` → `Passed! - Failed: 0, Passed: 19, Skipped: 0, Total: 19, Duration: 821 ms`, with no container startup. VSTest's `|` (OR) syntax works, and 19 matches the class-bucket arithmetic exactly, so the 19→23→26 sequence rests on a measured baseline rather than a derived one. No unverified residue remains in this fix.
23. **still holds**, and was verified in isolation rather than argued — [compat] Task 1 touches only `PopularitySignalConsumer.cs` and its test file; Task-1-only state builds (`0 Error(s)`) and runs 45 green with the worker untouched. The reverse direction also holds: the worker's `var outcome = PopularityUpdateOutcome.Failed;` cannot compile before Task 1 declares the enum, so the stated order is forced.
24. **still holds** — [existence] `git log --oneline -20`: "add popularity sweep fault-abort implementation plan", "add three missing CSP directives and Permissions-Policy header to admin console", "close csr round-3 finding #15…", "fuse the decayed recency count into the popularity signal". Lowercase imperative, no Conventional-Commits prefix.
25. **still holds** — [absence] `…WorkerTests.cs:1-13` imports `Microsoft.Extensions.Logging.Abstractions` (`:10`) and not `Microsoft.Extensions.Logging`; `Iverson.Api.Tests.csproj:1` is `Microsoft.NET.Sdk` with `ImplicitUsings` enabled (`:6`), which supplies only the `System.*` set — so `ILogger<T>`, `LogLevel` and `EventId` are unresolvable without the added import. The donor file carries it at `:6`. Confirmed by execution in both directions: the transcription with the import added builds clean; the claim that it is *needed* is what `RecordingLogger<T>`'s three unresolvable type references establish.

### Span check — plan dependencies with no covering assumption

Five dependencies the plan relies on that no listed item (and no "Inherited from spec" item) states. All five verified in-round; none escalates to §3.

- **SP1 — the intermediate Task-1-only state must build and be green.** #23 covers the *ordering* (Task 2 cannot precede Task 1); nothing covers whether Task 1 alone leaves a compiling, passing tree, which Step 5's "Expect 45 passed" depends on. **Verified, holds:** [totality] Task-1 production edit alone → `Build succeeded. 0 Error(s)`; Task-1 production + test edits → `Passed: 45, Failed: 0, Total: 45`.
- **SP2 — the seven new member names must not collide.** No listed item covers `BuildUpdater`, `StubAggregateFailingFor`, `Page`, `RecordingLogger<T>`, or the seven `[Fact]` method names against existing members of the two test classes. **Verified, holds:** [compat] full transcription of both tasks compiles with `0 Error(s)`; a collision would be CS0111/CS0102 at build.
- **SP3 — adding `using Microsoft.Extensions.Logging;` must be *safe*, not merely necessary.** #25 establishes the import is required; nothing establishes it introduces no ambiguity against the file's nine imports and three type aliases (`EngagementAggResult`, `SchemaRelationDescriptor`, `SchemaRelationKind`). **Verified, holds:** [compat] transcribed file builds clean; no CS0104.
- **SP4 — NSubstitute must accept the `.Returns(Func<CallInfo, Task<T>>)` lambda form.** #18 covers only the *value* form `.Returns(Task.FromException(...))`; `StubAggregateFailingFor` uses a lambda whose branches return `Task.FromException<EngagementAggResult?>` and `Task.FromResult<EngagementAggResult?>` against a member declared `Task<AggregationResult?>` — a documented NSubstitute overload hazard. **Verified, holds:** [compat] compiles under NSubstitute 5.3.0 (`Iverson.Api.Tests.csproj:16`) and the faulted branch actually throws where intended — tests 3 and 4 discriminate correctly by key, which is impossible if either branch is mis-bound.
- **SP5 — test 3's unstubbed page-2 continuation must fail loudly, not silently or fatally.** Test 3 stubs `FetchKeysAndTenantsPagedAsync` only for `afterKey == null`; if the abort regresses, the loop requests a second page that no stub covers. Nothing states what an unstubbed `Task<IEnumerable<KeyedTenantRow>>` returns. **Verified, holds:** [compat] mutation probe (`MaxConsecutiveFailures = 500`) → the unstubbed call returned an empty sequence, the paging loop broke, and the test failed in 223 ms with `Expected logs.Entries to contain a single item matching …Contains("Abandoning sweep"), but the collection is empty` — the right reason, no null-dereference, no hang.

---

## 2. Literal-wrongness findings

No literal-wrongness findings.

The plan's code is correct as written. It was transcribed verbatim into a clean tree, compiled, and executed: 48 tests pass, matching the plan's stated arithmetic exactly (41 baseline → 45 after Task 1 → 48 after Task 2). Three mutation probes confirm the new worker tests are non-vacuous — disabling the abort fails test 3, switching to cumulative counting fails test 4, and making `Skipped` count toward the threshold fails test 2. The one defect found lives in the plan's verification instructions and is recorded as the §1.22 assumption failure; it is not re-raised here.

---

## 3. Forced decisions

No forced decisions found.

---

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §1 has one failed assumption (#22); §2 and §3 are both empty. Address #22's proposed fix (or accept the container dependency explicitly and say so in the plan) via `update-implementation-plan` or a manual edit, then proceed to `subagent-driven-development`. No change to any task's code is required: the prescribed edits build and pass as written.
