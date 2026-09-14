# Popularity Sweep Fault-Abort Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-13-popularity-sweep-fault-abort-design.md` (commit SHA: `f18081d8b90f820d05d004b33c6f0e69fa3633c9`)

**Goal:** Make `PopularitySignalUpdater.UpdateAsync` report an outcome so the reconciliation sweep can detect a persistent engagement-store fault and abandon after five consecutive failures, replacing N error lines and N failing queries per sweep with six and five.

**Architecture:** `UpdateAsync`'s signature becomes `Task<PopularityUpdateOutcome>`; four of its five exits return a value and the fifth keeps propagating by contract. `SweepSignalAsync` counts consecutive `Failed` outcomes — its existing `catch` feeds the same counter via a seeded local — and emits one summary `LogError` before returning. No existing log line changes.

**Tech stack:** C#/.NET 10, xunit + FluentAssertions + NSubstitute, `Iverson.Api` / `Iverson.Api.Tests`.

---

## File Structure

**Modify**
- `Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs` — add the `PopularityUpdateOutcome` enum at file scope; change `UpdateAsync`'s return type and add four `return` statements.
- `Iverson.Server/Iverson.Api/Reconciliation/PopularitySignalReconciliationWorker.cs` — add `MaxConsecutiveFailures`, the consecutive-failure counter, and the abort.

**Test**
- `Iverson.Server/Iverson.Api.Tests/Consumers/PopularitySignalConsumerTests.cs` — spec tests 1, 2 (return-value half), 5, 6.
- `Iverson.Server/Iverson.Api.Tests/Reconciliation/PopularitySignalReconciliationWorkerTests.cs` — spec tests 2 (threshold half), 3, 4; plus a `RecordingLogger<T>` and an optional-logger parameter on `BuildSut`.

Spec test 2 deliberately spans both files: "`Skipped` is returned on a null aggregate result" is a `UpdateAsync` return-value assertion (Task 1), while "does not count toward the threshold" is sweep behaviour (Task 2).

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and re-confirmed across two CDR rounds. **Not re-verified here.** A1, A1b, A1c (five exit paths; `NotFound` shares a terminal point with success), A2 (small enums inline in the owning file), A3, A4 (the sweep's `foreach` + per-row try/catch shape; the catch logs and continues), A5 (the consumer can ignore the return value — no `TreatWarningsAsErrors` anywhere in `Iverson.Server/`), A6, A12/A13 (no interface or delegate constrains the signature), A8, A10, A11, A14 (the targeted fault produces `Failed`, not `Skipped` — for both `IEngagementStoreSearchService` implementations), A15, A16, A17, A18, A19.

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | All four edited files exist at the paths above | `wc -l`: consumer 300, worker 85, consumer-tests 499, worker-tests 188 |
| 2 | File path | `DocumentRerenderQueueWorkerTests.cs` exists as the `RecordingLogger<T>` donor | 317 lines, same `Reconciliation/` test folder |
| 3 | Signature | `UpdateAsync` is `internal async Task` with six parameters | `PopularitySignalConsumer.cs:44-46` |
| 4 | Signature | The four return sites are the aggregate `catch`, the null-result branch, the `NotFound` catch, and the method's end | `:83`, `:94`, `:134-140`, method closes `:141` |
| 5 | Code validity | Both inner catches are `when (ex is not OperationCanceledException)` — so an `OperationCanceledException` from either await is part of the **propagating** class, not a returned outcome | `:78`, `:113` |
| 6 | Consumer impact | **Exactly three call sites of `UpdateAsync` exist, all production, zero in tests** | `grep -rn "UpdateAsync(" --include=*.cs Iverson.Server/ \| grep -i popularit` → decl `:44` + consumer `:217`, `:223` + worker `:72`, nothing else |
| 7 | Consumer impact | No other `.UpdateAsync(` exists anywhere in `Iverson.Server/` — no name collision | same grep, inverted filter → zero rows |
| 8 | Consumer impact | Both consumer call sites are bare expression statements, so discarding the new return value compiles unchanged | `:217`, `:223` — `await updater.UpdateAsync(...);` |
| 9 | Code validity | `namespace Iverson.Api.Consumers;` is file-scoped, so a file-scope enum sits beside the classes without nesting | `PopularitySignalConsumer.cs:23` |
| 10 | Code validity | **Accessibility divergence, deliberate:** all four file-scope enums in `Iverson.Api` are `public`; the spec mandates `internal`. Following the spec — `PopularityUpdateOutcome` is the return type of an `internal` method on an `internal sealed` class, so `internal` is the tightest legal accessibility and adding a `public` type reachable from no public member would widen the assembly surface for nothing | `AuthorizationAction` (`IRowFieldAuthorizationEvaluator.cs:6`), `EnrichmentKind`/`RelationKind` (`SchemaDescriptor.cs:107,137`), `DocumentSegmentKind` (`DocumentTemplate.cs:7`) — 4/4 `public`, 0 `internal` |
| 11 | Signature | `SanitizeForLog()` is an `internal static` extension already in scope in the worker | `LoggingExtensions.cs:5`; used at `PopularitySignalReconciliationWorker.cs:78` |
| 12 | Signature | `SweepInterval` is `static readonly TimeSpan`, `PageSize` a `private const int` — the new const matches the latter | `PopularitySignalReconciliationWorker.cs:26-27` |
| 13 | Signature | Worker `BuildSut` already takes one optional parameter and all five call sites pass none, so a second optional parameter breaks nothing | decl `:70`; calls `:102`, `:119`, `:131`, `:149`, `:181` |
| 14 | Signature | `RecordingLogger<T>` exposes `Entries` as `List<(LogLevel Level, string Message)>` and formats via `formatter(state, exception)` | `DocumentRerenderQueueWorkerTests.cs:304-316` |
| 15 | Signature | `StubCount(long)` in the consumer suite filters on `a.Kind == AggregationKind.Count`, so it stubs the count call without also stubbing the histogram | `PopularitySignalConsumerTests.cs:124-131` |
| 16 | Signature | `StubAggregate(long)` in the worker suite matches `Arg.Any<AggregationDescriptor>()` — it stubs both aggregate calls | `PopularitySignalReconciliationWorkerTests.cs:79-85` |
| 17 | Code validity | The null-aggregate idiom is `.Returns((EngagementAggResult?)null)` | `PopularitySignalConsumerTests.cs:374` |
| 18 | Code validity | The throw-stub idiom is `.Returns(Task.FromException(...))` with `RpcException`/`Status`/`StatusCode` already imported | `PopularitySignalConsumerTests.cs:420`; `using Grpc.Core;` at `:3` |
| 19 | Code validity | Consumer-test fixtures the new tests call all exist: `ArticleSchema()`, `CommentSchema()`, `TenantA`, `ArticleId`, and `Relations[0]` as a `SchemaRelationDescriptor` | `:73`, `:90`, `:35`, `:37`, `:83` |
| 20 | Code validity | `PopularitySignalUpdater`'s constructor is `(search, vector, tenantScope, logger)` | `PopularitySignalConsumerTests.cs:61-62`; `PopularitySignalReconciliationWorkerTests.cs:72-73` |
| 21 | Command | The narrow filter `--filter "FullyQualifiedName~PopularitySignalConsumerTests\|FullyQualifiedName~PopularitySignalReconciliationWorkerTests"` selects exactly the two edited classes | executed at CIR round 1 close: **19 passed, 0 failed, 821 ms**, no container startup. VSTest's `\|` (OR) syntax confirmed working |
| 22 | Command | The **broad** filter `~PopularitySignal` is a substring match reaching **five** classes, one of which is a Testcontainers test — so the broad command requires a running container runtime. The two edited suites are pure NSubstitute, but that does not discharge the broad command's requirement | `--list-tests` bucketed by class: `PopularitySignalOptionsTests` 17, `PopularitySignalConsumerTests` 14, `PopularitySignalReconciliationWorkerTests` 5, `SchemaBuilderTests` 4, `ObjectSearchVectorIntegrationTests` 1 (= 41). `ObjectSearchVectorIntegrationTests.cs:25` declares `QdrantGrpcContainerFixture`, `:29` constructs `new ContainerBuilder()`; a broad run starts a reaper + Qdrant on 6334. **Corrected at CIR round 1** — the original row claimed "no Testcontainers", inferring it from a 6 s run time rather than from what the filter matches |
| 23 | Ordering | Task 2 consumes Task 1's enum and its new return type; Task 1 depends on nothing Task 2 introduces | Task 1 touches only `PopularitySignalConsumer.cs` + its test file; the worker's `outcome` local cannot compile before the enum exists |
| 24 | Command | Commit messages are lowercase imperative with no Conventional-Commits prefix | `git log --oneline -20`: "add three missing CSP directives…", "close csr round-3 finding #15…", "fuse the decayed recency count…" |
| 25 | Code validity | The worker test file imports `Microsoft.Extensions.Logging.Abstractions` but **not** `Microsoft.Extensions.Logging`, and `ImplicitUsings` on `Microsoft.NET.Sdk` supplies only `System.*` — so `RecordingLogger<T>` needs the import added (Task 2 Step 3) | target usings `:1-13`; donor `DocumentRerenderQueueWorkerTests.cs:6` carries it; `Iverson.Api.Tests.csproj:1,6` |
| 26 | Ordering | Task 1 alone leaves a compiling, green tree — Step 5's "Expect 23" depends on it | verified at CIR round 1 by transcribing Task 1 in isolation: the production edit alone → `Build succeeded. 0 Error(s)`; production + tests → 45 green on the broad filter, i.e. **23** on the narrow filter both Step 5s pin |
| 27 | Code validity | None of the four new helpers or seven new `[Fact]` names collides with an existing member of either test class | `BuildUpdater`, `StubAggregateFailingFor`, `Page(`, `RecordingLogger` and all seven test-method names → **0 hits** in both test files |
| 28 | Code validity | Adding `using Microsoft.Extensions.Logging;` introduces no ambiguity against the file's imports and three aliases | the aliases are `EngagementAggResult`, `SchemaRelationDescriptor`, `SchemaRelationKind` (`…WorkerTests.cs:17-19`) — none is a type in `Microsoft.Extensions.Logging`; CIR round 1's transcription built clean with no CS0104 |
| 29 | Code validity | NSubstitute accepts `StubAggregateFailingFor`'s `.Returns(Func<CallInfo, Task<T>>)` lambda, and the faulted branch actually throws | NSubstitute **5.3.0** (`Iverson.Api.Tests.csproj:16`); verified at CIR round 1 by execution — tests 3 and 4 discriminate correctly by key, which is impossible if either branch mis-bound |
| 30 | Code validity | Test 3's unstubbed page-2 continuation fails loudly, not silently or fatally | verified at CIR round 1 by mutation probe (`MaxConsecutiveFailures = 500`): the unstubbed call returned an **empty sequence**, the paging loop broke, and the test failed in 223 ms on "the collection is empty" — no null-dereference, no hang |

**One test beyond the spec's list of six.** Task 1 Step 4 adds `UpdateAsync_SetPayloadNotFound_ReturnsSkipped`, which the spec's §4 does not enumerate. It is included because spec §2 and assumption A1c warn explicitly that the `NotFound` catch shares a terminal point with success and that "a single `return Updated;` at the end would therefore mislabel it" — the exit-path table mandates `NotFound → Skipped`, and no other test in the plan or the existing suite pins that mapping. Without it the specific bug the design warns about ships untested.

**Baseline:** **19** tests pass across the two edited classes under the narrow filter both Step 5s use (measured: 19 passed, 821 ms, no containers). Expect **23** after Task 1 and **26** after Task 2. Any failure of *those* counts after Task 1 or Task 2 is introduced by this plan.

The broader `~PopularitySignal` filter matches **41** tests across five classes, one of them a Testcontainers integration test — see assumption #22. Use it only as an optional final whole-feature check, and only on a host with a working container runtime; a red result there is not by itself attributable to this plan.

**Drift note:** the spec's last commit (`f18081d`) is one commit behind HEAD (`6a219ce`); the only intervening commit adds the round-2 review file. No code drift.

---

## Tasks

### Task 1: `UpdateAsync` reports an outcome

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/PopularitySignalConsumerTests.cs`

**Interfaces:**
- Produces: the `PopularityUpdateOutcome` enum and `UpdateAsync`'s new return type — both consumed by Task 2.

- [ ] **Step 1: Declare the enum at file scope.** Insert immediately after the `namespace Iverson.Api.Consumers;` line's following blank line and before the `/// <summary>` block that documents `PopularitySignalUpdater`:

```csharp
/// <summary>
/// What <see cref="PopularitySignalUpdater.UpdateAsync"/> did, so the reconciliation sweep can
/// distinguish a real engagement-store failure from a designed no-op. <c>Skipped</c> covers the
/// documented degrade cases (unprovisioned tenant, Qdrant point not yet created) — folding them
/// into <c>Failed</c> would make the sweep abandon itself during normal operation.
/// </summary>
internal enum PopularityUpdateOutcome { Updated, Skipped, Failed }
```

Accessibility is `internal` per the spec, diverging from the 4/4 `public` file-scope enums elsewhere in `Iverson.Api` — see plan assumption #10 for why.

- [ ] **Step 2: Change the signature and add four returns.** Anchor on text, not line numbers — earlier edits shift them.

  1. `internal async Task UpdateAsync(` → `internal async Task<PopularityUpdateOutcome> UpdateAsync(`
  2. In the aggregate `catch` (the one logging `"AggregateAsync failed for parent=…"`), `return;` → `return PopularityUpdateOutcome.Failed;`
  3. In the `if (result is null)` branch (logging `"AggregateAsync returned null for parent=…"`), `return;` → `return PopularityUpdateOutcome.Skipped;`
  4. Inside the `catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)` block, after its `LogInformation`, add `return PopularityUpdateOutcome.Skipped;`
  5. As the method's new final statement, after that `catch` block closes, add `return PopularityUpdateOutcome.Updated;`

  The histogram `catch` (logging `"histogram failed for parent=…"`) gets **no** return — it falls through to `Updated`, which is correct: the count was still written. Do **not** add a catch-all around anything; the fifth exit path must keep propagating (spec §2, and Step 3's test 6 now asserts it).

- [ ] **Step 3: Add a direct-call helper to the test class.** Beside the existing `BuildSut`:

```csharp
// UpdateAsync's outcome is not observable through DispatchAsync (which discards it), so the
// outcome tests drive the updater directly. Same construction as BuildSut's.
private PopularitySignalUpdater BuildUpdater() =>
    new(_search, _vector, _tenantScope, NullLogger<PopularitySignalUpdater>.Instance);
```

  The four tests pass `UpdateAsync`'s six arguments inline, as shown in Step 4.

- [ ] **Step 4: Write the four tests.**

```csharp
// ── Outcome mapping: the aggregate's failure is swallowed internally (the log line stays),
//    but it must now be REPORTED so the reconciliation sweep can count it. ──────────────
[Fact]
public async Task UpdateAsync_AggregateThrows_ReturnsFailed()
{
    _search.AggregateAsync(
            Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
            Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
            Arg.Any<Func<string, EngagementQuerySchema?>?>(),
            Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
        .Returns(Task.FromException<EngagementAggResult?>(new InvalidOperationException("starrocks down")));

    var outcome = await BuildUpdater().UpdateAsync(
        ArticleSchema(), new PopularitySignalEntry("Article", "Comments"), CommentSchema(),
        ArticleSchema().Relations[0], ArticleId, TenantA);

    outcome.Should().Be(PopularityUpdateOutcome.Failed);
}

// A null aggregate result is the DESIGNED outcome of the unprovisioned-tenant race — Skipped,
// never Failed, or a sweep over an unprovisioned tenant would abandon itself.
[Fact]
public async Task UpdateAsync_AggregateReturnsNull_ReturnsSkipped()
{
    _search.AggregateAsync(
            Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
            Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
            Arg.Any<Func<string, EngagementQuerySchema?>?>(),
            Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
        .Returns((EngagementAggResult?)null);

    var outcome = await BuildUpdater().UpdateAsync(
        ArticleSchema(), new PopularitySignalEntry("Article", "Comments"), CommentSchema(),
        ArticleSchema().Relations[0], ArticleId, TenantA);

    outcome.Should().Be(PopularityUpdateOutcome.Skipped);
}

// The Qdrant point not existing yet is the other documented degrade case — also Skipped, and it
// shares a terminal point with success, so a single trailing `return Updated` would mislabel it.
[Fact]
public async Task UpdateAsync_SetPayloadNotFound_ReturnsSkipped()
{
    StubCount(3);
    _vector.SetPayloadAsync(
            Arg.Any<string>(), Arg.Any<ulong>(), Arg.Any<IReadOnlyDictionary<string, object>>())
        .Returns(Task.FromException(new RpcException(new Status(StatusCode.NotFound, "no point"))));

    var outcome = await BuildUpdater().UpdateAsync(
        ArticleSchema(), new PopularitySignalEntry("Article", "Comments"), CommentSchema(),
        ArticleSchema().Relations[0], ArticleId, TenantA);

    outcome.Should().Be(PopularityUpdateOutcome.Skipped);
}

// ── Spec test 6: the fifth exit path. This is the ONLY assertion in the suite that a catch-all
//    converting the propagating class into an outcome would fail — the two pre-existing tests
//    that document the contract in comments assert only NotThrowAsync, which a swallowing
//    catch-all also satisfies. StubCount is REQUIRED: without it the aggregate returns null and
//    UpdateAsync returns Skipped before ever reaching the Qdrant write.
[Fact]
public async Task UpdateAsync_SetPayloadThrowsNonNotFound_Propagates()
{
    StubCount(3);
    _vector.SetPayloadAsync(
            Arg.Any<string>(), Arg.Any<ulong>(), Arg.Any<IReadOnlyDictionary<string, object>>())
        .Returns(Task.FromException(new RpcException(new Status(StatusCode.Unavailable, "down"))));

    var act = () => BuildUpdater().UpdateAsync(
        ArticleSchema(), new PopularitySignalEntry("Article", "Comments"), CommentSchema(),
        ArticleSchema().Relations[0], ArticleId, TenantA);

    await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.Unavailable);
}
```

  Spec test 5 (the consumer is unchanged when it ignores the return value) needs **no new test**: the existing `Dispatch_*` tests exercise both call sites and must stay green. Confirm that in Step 5 rather than duplicating coverage.

- [ ] **Step 5: Build and test.**
```bash
dotnet build Iverson.Server/Iverson.Api/Iverson.Api.csproj
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~PopularitySignalConsumerTests|FullyQualifiedName~PopularitySignalReconciliationWorkerTests"
```
  Expect 23 passed (19 baseline + 4 new), 0 failed. Every pre-existing `Dispatch_*` test staying green **is** spec test 5 — they live in `PopularitySignalConsumerTests`, which this filter still selects.

- [ ] **Step 6: Commit.**
```bash
git add Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs Iverson.Server/Iverson.Api.Tests/Consumers/PopularitySignalConsumerTests.cs
git commit -m "make UpdateAsync report an outcome so the sweep can see engagement-store faults"
```

---

### Task 2: The sweep abandons on consecutive failures

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Reconciliation/PopularitySignalReconciliationWorker.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Reconciliation/PopularitySignalReconciliationWorkerTests.cs`

**Interfaces:**
- Consumes: `PopularityUpdateOutcome` and `UpdateAsync`'s new return type from Task 1.

- [ ] **Step 1: Add the threshold constant** beside `PageSize`:

```csharp
private const int PageSize = 500;
private const int MaxConsecutiveFailures = 5;
```

- [ ] **Step 2: Count consecutive failures and abandon.** Declare `var consecutiveFailures = 0;` beside `string? afterKey = null;` — **outside** the paging `while`, so the count carries across page boundaries — then replace the per-row `try`/`catch` body with:

```csharp
var outcome = PopularityUpdateOutcome.Failed;
try
{
    outcome = await updater.UpdateAsync(parentSchema, signal, childSchema, relation, row.Key, row.TenantId);
}
catch (Exception ex)
{
    logger.LogError(ex,
        "[PopularitySignalReconciliation] Failed to update parent={Parent} for signal={Signal} — skipping.",
        row.Key.SanitizeForLog(), signal.Relation.SanitizeForLog());
}

if (outcome is not PopularityUpdateOutcome.Failed)
{
    consecutiveFailures = 0;
}
else if (++consecutiveFailures >= MaxConsecutiveFailures)
{
    logger.LogError(
        "[PopularitySignalReconciliation] Abandoning sweep for signal={Signal} after {Count} " +
        "consecutive parent-update failures — see the preceding per-parent errors for the cause. " +
        "Retrying at the next sweep in {Minutes} minutes.",
        signal.Relation.SanitizeForLog(), consecutiveFailures, SweepInterval.TotalMinutes);
    return;
}
```

  Seeding `outcome` to `Failed` is what makes the existing `catch` feed the same counter, as the spec requires, without a second code path. The existing per-parent `LogError` is unchanged — the spec's out-of-scope list forbids touching it.

  **Ruling on a case the spec does not cover:** the `if (row.TenantId is null) continue;` guard above stays untouched, so a null-tenant row neither increments nor resets the counter. It is not an outcome — `UpdateAsync` is never called — and leaving it inert preserves consecutive-ness across interleaved null-tenant rows. Cost if wrong: a sweep whose failures are separated only by null-tenant rows aborts where a resetting counter would not, which is the conservative direction.

- [ ] **Step 3: Make the logger injectable in tests.** Change `BuildSut` to take an optional logger; all five existing call sites pass none and keep compiling:

```csharp
private PopularitySignalReconciliationWorker BuildSut(
    PopularitySignalOptions? options = null,
    ILogger<PopularitySignalReconciliationWorker>? logger = null)
{
    var updater = new PopularitySignalUpdater(
        _search, _vector, _tenantScope, NullLogger<PopularitySignalUpdater>.Instance);
    return new PopularitySignalReconciliationWorker(
        Options.Create(options ?? new PopularitySignalOptions { Signals = [Signal()] }),
        _registry, _entities, updater,
        logger ?? NullLogger<PopularitySignalReconciliationWorker>.Instance);
}
```

  **Add `using Microsoft.Extensions.Logging;`** — the file currently imports only `Microsoft.Extensions.Logging.Abstractions` (`:10`), which supplies `NullLogger` but not `ILogger<T>` or `LogLevel`, and this test project's `ImplicitUsings` is plain `Microsoft.NET.Sdk` (System.* only). Keep the existing `.Abstractions` import — `BuildSut`'s fallback still needs it. Then copy `RecordingLogger<T>` verbatim from `DocumentRerenderQueueWorkerTests.cs:304-316` as a private nested class at the end of the test class.

- [ ] **Step 4: Add a helper that fails the first N parents**, then the three tests:

```csharp
// Fails the first `failures` parents by key, succeeds for the rest — drives UpdateAsync's
// aggregate catch, which is the Failed outcome the counter reacts to.
private void StubAggregateFailingFor(params string[] failingKeys)
{
    _search.AggregateAsync(
            Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
            Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
            Arg.Any<Func<string, EngagementQuerySchema?>?>(),
            Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
        .Returns(ci =>
        {
            var q = ci.ArgAt<SearchQuery?>(1);
            var key = q?.Clauses.FirstOrDefault()?.Value?.StringVal;
            return key is not null && failingKeys.Contains(key)
                ? Task.FromException<EngagementAggResult?>(new InvalidOperationException("starrocks down"))
                : Task.FromResult<EngagementAggResult?>(
                    new EngagementAggResult("count", AggregationKind.Count, MetricValue: 1));
        });
}

private static KeyedTenantRow[] Page(int count, int from = 1) =>
    Enumerable.Range(from, count).Select(i => new KeyedTenantRow($"article-{i}", TenantA)).ToArray();

// ── Spec test 2, threshold half: an all-Skipped sweep must run to COMPLETION. Skipped is a
//    designed no-op (unprovisioned tenant), and it can affect every parent at once — if it
//    counted toward the threshold the sweep would abandon itself during normal operation. ──
[Fact]
public async Task SweepSignalAsync_AllParentsSkipped_RunsToCompletionWithoutAbandoning()
{
    await _registry.RegisterAsync(ArticleSchema());
    await _registry.RegisterAsync(CommentSchema());
    _search.AggregateAsync(
            Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
            Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
            Arg.Any<Func<string, EngagementQuerySchema?>?>(),
            Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
        .Returns((EngagementAggResult?)null);

    var page = Page(8);
    _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), null, 500, Arg.Any<EntityAccess>()).Returns(page);
    _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), "article-8", 500, Arg.Any<EntityAccess>()).Returns([]);

    var logs = new RecordingLogger<PopularitySignalReconciliationWorker>();
    await BuildSut(logger: logs).SweepSignalAsync(Signal(), CancellationToken.None);

    // Paged to exhaustion rather than abandoning at 5.
    await _entities.Received(1).FetchKeysAndTenantsPagedAsync(
        Arg.Any<TableSchema>(), "article-8", 500, Arg.Any<EntityAccess>());
    logs.Entries.Should().NotContain(e => e.Message.Contains("Abandoning sweep"));
}

// ── Spec test 3: five CONSECUTIVE failures abandon the sweep with EXACTLY ONE summary line. ──
[Fact]
public async Task SweepSignalAsync_FiveConsecutiveFailures_AbandonsWithExactlyOneSummary()
{
    await _registry.RegisterAsync(ArticleSchema());
    await _registry.RegisterAsync(CommentSchema());
    StubAggregateFailingFor("article-1", "article-2", "article-3", "article-4", "article-5");

    _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), null, 500, Arg.Any<EntityAccess>())
        .Returns(Page(8));

    var logs = new RecordingLogger<PopularitySignalReconciliationWorker>();
    await BuildSut(logger: logs).SweepSignalAsync(Signal(), CancellationToken.None);

    logs.Entries.Should().ContainSingle(e => e.Message.Contains("Abandoning sweep"))
        .Which.Message.Should().Contain("after 5 consecutive parent-update failures");

    // Abandoned mid-page: parents 6-8 were never attempted, and no second page was requested.
    await _vector.DidNotReceive().SetPayloadAsync(
        Arg.Any<string>(), IntelligenceStoreConsumer.KeyToUlong("article-6"),
        Arg.Any<IReadOnlyDictionary<string, object>>());
    await _entities.DidNotReceive().FetchKeysAndTenantsPagedAsync(
        Arg.Any<TableSchema>(), "article-8", 500, Arg.Any<EntityAccess>());
}

// ── Spec test 4: scattered, non-consecutive failures must NOT abandon. A systemic fault fails
//    every parent; a poisoned row fails one. Cumulative counting would abandon a 400k-parent
//    sweep over five unrelated failures — this is the test that pins the difference. ──
[Fact]
public async Task SweepSignalAsync_ScatteredNonConsecutiveFailures_DoesNotAbandon()
{
    await _registry.RegisterAsync(ArticleSchema());
    await _registry.RegisterAsync(CommentSchema());
    // Six failures, never more than two in a row.
    StubAggregateFailingFor("article-1", "article-2", "article-4", "article-5", "article-7", "article-8");

    var page = Page(9);
    _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), null, 500, Arg.Any<EntityAccess>()).Returns(page);
    _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), "article-9", 500, Arg.Any<EntityAccess>()).Returns([]);

    var logs = new RecordingLogger<PopularitySignalReconciliationWorker>();
    await BuildSut(logger: logs).SweepSignalAsync(Signal(), CancellationToken.None);

    logs.Entries.Should().NotContain(e => e.Message.Contains("Abandoning sweep"));
    await _entities.Received(1).FetchKeysAndTenantsPagedAsync(
        Arg.Any<TableSchema>(), "article-9", 500, Arg.Any<EntityAccess>());
}
```

- [ ] **Step 5: Build and test.**
```bash
dotnet build Iverson.Server/Iverson.Api/Iverson.Api.csproj
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~PopularitySignalConsumerTests|FullyQualifiedName~PopularitySignalReconciliationWorkerTests"
```
  Expect 26 passed (23 after Task 1 + 3 new), 0 failed.

  Optionally, as a final whole-feature check on a host with a working container runtime, run the broad filter — `--filter "FullyQualifiedName~PopularitySignal"`, expect 48 — which additionally exercises `ObjectSearchVectorIntegrationTests` against a live Qdrant. Per assumption #22 this is not part of the gate: a failure there is not by itself attributable to this plan.

- [ ] **Step 6: Commit.**
```bash
git add Iverson.Server/Iverson.Api/Reconciliation/PopularitySignalReconciliationWorker.cs Iverson.Server/Iverson.Api.Tests/Reconciliation/PopularitySignalReconciliationWorkerTests.cs
git commit -m "abandon the popularity sweep after five consecutive parent-update failures"
```

---

## Tasks NOT in this plan

Inherited verbatim from the spec's "Out of scope". A new spec → plan cycle is required to add any of these.

- **A pre-sweep health probe.** `IEngagementStoreHealthCheck.IsHealthyAsync()` exists
  (`IEngagementStoreRoles.cs:11-15`) and would let the sweep skip entirely when the store is already
  down. Deliberately excluded: once the abort fires at five, the sweep costs five queries rather than
  N, so the probe buys a marginal saving in exchange for a second mechanism, a second failure mode,
  and a second thing to test. It remains a clean addition later if wanted.
- **Backing off the sweep interval during a fault.** Considered and rejected as a new state machine
  for a problem the abort already bounds.
- **Changing any existing log line.** The per-parent `LogError` stays; the abort bounds it at five.
