# Enrichment Timeout Skip Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-10-01-enrichment-timeout-skip-design.md` (commit SHA: `05eeb823`)

**Goal:** An Ollama HTTP timeout in `EnrichmentConsumer` becomes a visible best-effort skip — logged with the object's key and the recovery path, counted on `enrichment.timeouts_skipped` — instead of an invisible redelivery that can block the partition forever.

**Architecture:** One new catch clause in `EnrichmentConsumer.HandleAsync`, matching only `TaskCanceledException` whose direct inner exception is `TimeoutException` (HttpClient's timeout shape), placed before the existing transient filter. One new counter on the already-exported `Iverson.Api.Reconciliation` meter. Two bullets in the transient-failures spec point at the new behaviour.

**Tech stack:** .NET 10; System.Diagnostics.Metrics (`Counter<long>`, `MeterListener` in tests); Npgsql 10.0.3; xunit, NSubstitute, FluentAssertions.

---

## Global Constraints

- **The code is the patch.** The task's code and docs are ONE `git apply` patch, generated from a prototype that compiled and passed. It was verified to apply to `main` at `05eeb823` and to reproduce every listed md5. Never retype it: slice it from the task brief with the given command and check the md5s.
- **Cap build memory.** An uncapped build was OOM-killed alongside the docker stack, so every `dotnet` command runs as `systemd-run --user --scope -q -p MemoryMax=4G dotnet … -m:2`.
- **Unit tests only:** use `--filter "Category!=Integration"`. No Docker is needed.
- **Commits:** a lowercase imperative subject, a blank line, then `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>` (two `-m` flags). Commit explicit paths only. `docs/` is gitignored: use `git add -f` and `git commit … -- <file>`.
- **Shell `grep` skips gitignored paths, including `docs/`.** Use `command grep`.

## File Structure

- **Modify:** `Iverson.Server/Iverson.Api/Reconciliation/ReconciliationTelemetry.cs`. Adds the `EnrichmentTimeoutsSkipped` counter (`enrichment.timeouts_skipped`), declared after `Meter`, plus one line of class doc.
- **Modify:** `Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs`. Adds `using Iverson.Api.Reconciliation;` and the new `catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)` clause ahead of the transient filter.
- **Test:** `Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs`. Adds `using System.Diagnostics.Metrics;`, `using Npgsql;`, a `TimeoutsSkippedDuring` `MeterListener` helper and three tests:
  - `HandleUpdated_WhenLlmTimesOut_SkipsWithoutWritingAndCountsTheSkip`;
  - `HandleUpdated_WhenCallerCancels_IsNotCountedAsATimeout`, which pins the filter (CDR round 1, D4);
  - `HandleUpdated_WhenWritebackTimesOutInPostgres_Propagates`, which pins that the rule is HTTP-only.
- **Docs:** `docs/specs/2026-09-30-transient-projection-failures-design.md`. Updates two bullets: the §3 `EnrichmentConsumer` bullet, and the Known-limitations bullet "HTTP timeouts reaching the dispatcher directly".

## Inherited from spec

These assumptions were verified by `thorough-brainstorming` and reconfirmed by CDR round 1 (✅). They are not re-verified here. The table is copied verbatim from the spec's "Verified assumptions":

| # | Assumption | Evidence |
|---|---|---|
| 1 | An `HttpClient` timeout is `TaskCanceledException` with `TimeoutException` as its **direct** inner exception, even while the caller's token is live. A caller-token cancellation is `TaskCanceledException > TaskCanceledException`, with no `TimeoutException`. | Ran three `GetAsync` calls against a silent listener on .NET 10: caller cancel gives `[TCE > TCE > IOException > SocketException]`; timeout gives `[TCE > TimeoutException > …]`, both with and without a live caller token. |
| 2 | Nothing between Ollama and Enrichment's catch wraps the exception. `EnrichmentService` logs and rethrows unchanged (`EnrichmentService.cs:82-87`). The consumer's generation helper catches only `InvalidOperationException`, and the write-back step catches only `RpcException` around the size validator. | Read `EnrichmentConsumer.cs:136-232` and `:260-300`. |
| 3 | Inside the try block, the only HTTP calls are `enrichment.GenerateAsync` and `GenerateJsonAsync`, both to Ollama. Write-back, state and outbox go to Postgres; publish goes to Kafka. | Same read. |
| 4 | A frozen-Postgres command timeout is `NpgsqlException > TimeoutException`, not a `TaskCanceledException`. | Transient spec, verified assumption 19 (CDR round 1, `probe4`). |
| 5 | `ReconciliationTelemetry` is `internal static` in `Iverson.Api`, so the consumer can reach it. Its meter `Iverson.Api.Reconciliation` is exported. | `ReconciliationTelemetry.cs:12-17`; `Program.cs:74` `.AddMeter("Iverson.Events", ReconciliationTelemetry.MeterName)`. |
| 6 | Adding a counter breaks no test. `AdminConsoleMetricsSeriesNamesTests` looks up specific named fields by regex, and its counter convention is `Field = Meter.CreateCounter<long>("name"`. | Read `AdminConsoleMetricsSeriesNamesTests.cs:60-100`. |
| 7 | `Iverson.Api.Tests` has no `MeterListener` use yet; the pattern lives in `Iverson.Events.Tests`. Only the new timeout test increments this counter, so a listener on it doesn't race. | `command grep -rln MeterListener` over `Iverson.Api.Tests` finds nothing. |
| 8 | No existing Enrichment test injects a timeout. | `command grep -n 'TimeoutException\|TaskCanceledException'` over `EnrichmentConsumerTests.cs` finds nothing. |
| 9 | A skipped object re-enriches on the next `Created` or `Updated` event, or on a reconcile replay: there is no state row, so the hash gate passes. | `EnrichmentConsumer.cs:66-78` (both event types reach `HandleAsync`), `:121-128` (hash gate); `ReconciliationService.ReconcileTypeAsync` emits `Updated` for every row. |
| 10 | No enrichment counter exists today. | grep of `Iverson.Api` and `Iverson.Embeddings` for `CreateCounter` finds none there. |

## Verified plan-level assumptions

The code was prototyped on the throwaway branch `ets-proto` (`.worktrees/ets-proto`), two commits on `05eeb823`. The patch below is `git diff 05eeb823 ets-proto`.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | Paths | All four modified files exist at the listed paths, and nothing is created. | `+++ b/` list of the patch; each file exists at `05eeb823`. |
| 2 | Signature | Inside Enrichment's catch, `logger`, `schema` and `ev` are in scope (the existing catch uses all three). `ReconciliationTelemetry` (`internal static`, same assembly) needs `using Iverson.Api.Reconciliation;`, which the patch adds. | Read `EnrichmentConsumer.cs:232-238` and the usings at `:1-11`; the prototype compiles. |
| 3 | Code validity | A `static readonly Counter<long>` field initializer can sit alongside `ReconciliationTelemetry`'s static constructor, declared after `Meter` because static initializers run in source order. | The prototype compiles and `enrichment.timeouts_skipped` is observed by the test's `MeterListener`. |
| 4 | Signature | `EnrichmentConsumerTests` stubs `_enrichment.GenerateAsync(...)` and `_txRunner.ExecuteInTransactionAsync(...)` with `.Throws(...)`, and asserts "nothing written" with `DidNotReceiveWithAnyArgs` on `UpdateColumnsAsync`, `UpsertAsync` and `PublishAsync`. The new tests follow this. | Read `EnrichmentConsumerTests.cs:480-528`. |
| 5 | Code validity | `TaskCanceledException(string, Exception)` and `NpgsqlException(string, Exception)` are public. `MeterListener` compiles in `Iverson.Api.Tests` without a package, and `Npgsql` reaches the test project transitively. | The prototype compiles and passes. NpgsqlException's public constructors were listed by a reflection probe. |
| 6 | Command | Expected counts: `EnrichmentConsumerTests` 23 → 26; Api unit suite (`Category!=Integration`) 1267 → 1270; `Iverson.Server.slnx` build 0 errors, 21 warnings (unchanged). | Measured on the prototype. |
| 7 | Consumer impact | Nothing else breaks. `AdminConsoleMetricsSeriesNamesTests` looks up named fields only and passes in the 1270 run. No test expected a timeout to propagate from Enrichment (spec assumption 8). | Api suite 1270/1270 on the prototype. |
| 8 | Mutation | Each new test kills its mutant:<br>• dropping the `when` filter → `HandleUpdated_WhenCallerCancels_IsNotCountedAsATimeout`;<br>• dropping the counter increment, or a clause that never matches → `HandleUpdated_WhenLlmTimesOut_SkipsWithoutWritingAndCountsTheSkip`;<br>• widening the clause to any exception with an inner `TimeoutException` → `HandleUpdated_WhenWritebackTimesOutInPostgres_Propagates`. | Each mutant was applied to the prototype, `EnrichmentConsumerTests` was run (1 failure, the named test), and the file was restored byte for byte. |
| 9 | Docs | The two transient-spec bullets match the text the patch replaces. | The patch applies cleanly to `05eeb823`. |
| 10 | Convention | The commit style is a lowercase imperative subject plus the trailer. | `git log --format=%s -8`. |

## Tasks

### Task 1: Skip Ollama timeouts visibly in Enrichment

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Reconciliation/ReconciliationTelemetry.cs`
- Modify: `Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs`
- Docs: `docs/specs/2026-09-30-transient-projection-failures-design.md`

- [ ] **Step 1: Apply the patch.** `BRIEF` is the path of this task brief. The command slices the brief's only `diff` block byte for byte; never retype it. From the repository root:

```bash
python3 - "$BRIEF" > /tmp/claude-1000/ets-task1.patch <<'PY'
import re, sys
fence = chr(96) * 3
s = open(sys.argv[1]).read()
print(re.search(fence + "diff\n(.*?)" + fence, s, re.S).group(1), end="")
PY
git apply --check /tmp/claude-1000/ets-task1.patch && git apply /tmp/claude-1000/ets-task1.patch
```

If `--check` fails, stop and report. Do not hand-edit. The patch:

```diff
diff --git a/Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs b/Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs
index 460297a2..715e9d98 100644
--- a/Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs
+++ b/Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs
@@ -1,3 +1,4 @@
+using System.Diagnostics.Metrics;
 using System.Net;
 using System.Text.Json;
 using FluentAssertions;
@@ -12,6 +13,7 @@ using Microsoft.Extensions.Configuration;
 using Microsoft.Extensions.DependencyInjection;
 using Microsoft.Extensions.Hosting;
 using Microsoft.Extensions.Logging.Abstractions;
+using Npgsql;
 using NSubstitute;
 using NSubstitute.ExceptionExtensions;
 using Xunit;
@@ -514,6 +516,79 @@ public class EnrichmentConsumerTests
             default, default!, default!, default!, default, default, default, default!, default);
     }
 
+    /// <summary>Runs <paramref name="act"/> and returns how many enrichment.timeouts_skipped it recorded.</summary>
+    private static async Task<long> TimeoutsSkippedDuring(Func<Task> act)
+    {
+        long skipped = 0;
+        using var listener = new MeterListener();
+        listener.InstrumentPublished = (inst, l) =>
+        {
+            if (inst.Meter.Name == "Iverson.Api.Reconciliation" && inst.Name == "enrichment.timeouts_skipped")
+                l.EnableMeasurementEvents(inst);
+        };
+        listener.SetMeasurementEventCallback<long>((_, val, _, _) => Interlocked.Add(ref skipped, val));
+        listener.Start();
+
+        await act();
+        return Interlocked.Read(ref skipped);
+    }
+
+    // An Ollama HTTP timeout (HttpClient's shape: TaskCanceledException wrapping TimeoutException) is skipped,
+    // not redelivered: a document that always times out must not block the partition. Nothing is written, so
+    // the next change or a reconcile re-enriches it, and the skip is counted.
+    [Fact]
+    public async Task HandleUpdated_WhenLlmTimesOut_SkipsWithoutWritingAndCountsTheSkip()
+    {
+        await _registry.RegisterAsync(EnrichedArticle());
+        _enrichment.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
+                   .Throws(new TaskCanceledException("ollama timed out", new TimeoutException()));
+
+        var sut = BuildSut();
+        var skipped = await TimeoutsSkippedDuring(async () =>
+        {
+            var act = async () => await sut.HandleAsync(Key, Event(EntityEventType.Updated), CancellationToken.None);
+            await act.Should().NotThrowAsync();
+        });
+
+        skipped.Should().Be(1);
+        await _entities.DidNotReceiveWithAnyArgs().UpdateColumnsAsync(default!, default!, default!, default!, default);
+        await _state.DidNotReceiveWithAnyArgs().UpsertAsync(
+            default!, default!, default!, default!, default!, default);
+        await _outboxPublisher.DidNotReceiveWithAnyArgs().PublishAsync(
+            default, default!, default!, default!, default, default, default, default!, default);
+    }
+
+    // A caller-token cancellation is a TaskCanceledException with NO TimeoutException inside: it is not an
+    // Ollama timeout, so it must not be counted or logged as one (it falls to the existing best-effort catch).
+    [Fact]
+    public async Task HandleUpdated_WhenCallerCancels_IsNotCountedAsATimeout()
+    {
+        await _registry.RegisterAsync(EnrichedArticle());
+        _enrichment.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
+                   .Throws(new TaskCanceledException("caller cancelled"));
+
+        var sut = BuildSut();
+        var skipped = await TimeoutsSkippedDuring(() =>
+            sut.HandleAsync(Key, Event(EntityEventType.Updated), CancellationToken.None));
+
+        skipped.Should().Be(0);
+    }
+
+    // The timeout rule is HTTP-only: a Postgres command timeout during the write-back (NpgsqlException wrapping
+    // TimeoutException) is an outage, so it still propagates for redelivery instead of being skipped.
+    [Fact]
+    public async Task HandleUpdated_WhenWritebackTimesOutInPostgres_Propagates()
+    {
+        await _registry.RegisterAsync(EnrichedArticle());
+        _txRunner.ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>())
+                 .Throws(new NpgsqlException("command timeout", new TimeoutException()));
+
+        var sut = BuildSut();
+        var act = async () => await sut.HandleAsync(Key, Event(EntityEventType.Updated), CancellationToken.None);
+
+        await act.Should().ThrowAsync<NpgsqlException>();
+    }
+
     [Fact]
     public async Task HandleUpdated_WhenWritebackFails_DoesNotThrowPoisonMessageException()
     {
diff --git a/Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs b/Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs
index ba35a1a9..3a867ef9 100644
--- a/Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs
+++ b/Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs
@@ -5,6 +5,7 @@ using System.Text.Json;
 using Google.Protobuf.WellKnownTypes;
 using Grpc.Core;
 using Iverson.Api.Grpc;
+using Iverson.Api.Reconciliation;
 using Iverson.Api.Schema;
 using Iverson.Embeddings;
 using Iverson.Events;
@@ -229,6 +230,18 @@ public sealed class EnrichmentConsumer(
                 "[Enrichment] Enriched {Count} column(s) for {Type}:{Key}",
                 columns.Count, schema.TypeName.SanitizeForLog(), ev.Key);
         }
+        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
+        {
+            // An Ollama HTTP timeout: HttpClient's timeout is a TaskCanceledException whose DIRECT inner
+            // exception is TimeoutException (a caller-token cancellation has none, and an Npgsql command
+            // timeout is an NpgsqlException), and the only HTTP calls in this block go to Ollama. Skipped
+            // rather than redelivered so a document that always times out cannot block the partition; no
+            // state row, so the next change to the object or a reconcile of the type re-enriches it.
+            ReconciliationTelemetry.EnrichmentTimeoutsSkipped.Add(1);
+            logger.LogWarning(ex,
+                "[Enrichment] Ollama timed out for {Type}:{Key} — skipped with no state row; the next change to the object, or a reconcile of the type (POST /admin/reconcile/<type>), re-enriches it.",
+                schema.TypeName.SanitizeForLog(), ev.Key);
+        }
         catch (Exception ex) when (!TransientFailures.IsTransient(ex))
         {
             // Leaves no state row, so the next event for this object retries. A transient dependency
diff --git a/Iverson.Server/Iverson.Api/Reconciliation/ReconciliationTelemetry.cs b/Iverson.Server/Iverson.Api/Reconciliation/ReconciliationTelemetry.cs
index 2fb03f91..54de4504 100644
--- a/Iverson.Server/Iverson.Api/Reconciliation/ReconciliationTelemetry.cs
+++ b/Iverson.Server/Iverson.Api/Reconciliation/ReconciliationTelemetry.cs
@@ -9,6 +9,8 @@ namespace Iverson.Api.Reconciliation;
 /// <see cref="ReconciliationQueueWorker"/>, <see cref="DlqBacklogGaugeWorker"/>, and
 /// <see cref="DocumentRerenderQueueWorker"/> respectively — ObservableGauge reads whatever value
 /// is currently here whenever the OTel SDK collects, so no locking is needed beyond `volatile`.
+/// <see cref="EnrichmentTimeoutsSkipped"/> counts the other silent shortfall: enrichments skipped
+/// because Ollama timed out, which only a later change or a reconcile of the type recovers.
 /// </summary>
 internal static class ReconciliationTelemetry
 {
@@ -16,6 +18,11 @@ internal static class ReconciliationTelemetry
 
     private static readonly Meter Meter = new(MeterName, "1.0.0");
 
+    // Declared after Meter: static field initializers run in source order.
+    internal static readonly Counter<long> EnrichmentTimeoutsSkipped = Meter.CreateCounter<long>(
+        "enrichment.timeouts_skipped",
+        description: "Enrichment generations skipped after an Ollama HTTP timeout; recover with the next change or POST /admin/reconcile/{type}");
+
     internal static volatile int ReconciliationQueueDepth;
     internal static volatile int DlqUnreplayedCount;
     internal static volatile int DocumentRerenderQueueDepth;
diff --git a/docs/specs/2026-09-30-transient-projection-failures-design.md b/docs/specs/2026-09-30-transient-projection-failures-design.md
index 2bf76082..dc66b2c1 100644
--- a/docs/specs/2026-09-30-transient-projection-failures-design.md
+++ b/docs/specs/2026-09-30-transient-projection-failures-design.md
@@ -81,7 +81,7 @@ Everything else is non-transient, including:
 
 Each broad catch gains the filter `when (!TransientFailures.IsTransient(ex))`. Transient failures then escape to the dispatcher, and everything else is handled exactly as today.
 
-- **`EnrichmentConsumer`** (`:232-238`, the catch around generation, write-back and republish). Non-transient failures stay best-effort: logged, no state row written, offset committed. Transient failures now halt, including Ollama being unreachable or returning 5xx, and Postgres being down during the write-back transaction. An Ollama timeout is classified transient but does not halt through the dispatcher: it takes the cancellation path described under Known limitations. **Behavior change:** a deployment that enables enrichment while Ollama is down accumulates enrichment lag instead of skipping objects.
+- **`EnrichmentConsumer`** (`:232-238`, the catch around generation, write-back and republish). Non-transient failures stay best-effort: logged, no state row written, offset committed. Transient failures now halt, including Ollama being unreachable or returning 5xx, and Postgres being down during the write-back transaction. An Ollama timeout is skipped, logged and counted instead (see `docs/specs/2026-10-01-enrichment-timeout-skip-design.md`): no state row, offset commits, `enrichment.timeouts_skipped` increments. **Behavior change:** a deployment that enables enrichment while Ollama is down accumulates enrichment lag instead of skipping objects.
 - **`PopularitySignalConsumer`**, at two sites:
   - The per-relation catch in `DispatchAsync` (`:259-264`).
   - The catch in `PopularitySignalUpdater.UpdateAsync` that converts a StarRocks `AggregateAsync` failure to `Failed` (`:86-92`). It becomes `catch (Exception ex) when (ex is not OperationCanceledException && !TransientFailures.IsTransient(ex))`.
@@ -149,7 +149,7 @@ All unit tests use each project's existing fakes. No containers are needed.
 - **Extra round-trip:** one FE liveness check per StarRocks failure that is neither transient nor an expected missing resource.
 - **gRPC behavior change:** reads return `Unavailable` during a StarRocks backend outage (§5).
 - **Misclassification has a cost in both directions.** A transient failure classified as permanent is still dead-lettered. A permanent failure classified as transient stalls its consumer until someone intervenes. The classifier test matrix is the guard.
-- **HTTP timeouts reaching the dispatcher directly (Engagement, Intelligence: existing, unchanged; Enrichment: new on this branch):** an HTTP timeout is a `TaskCanceledException`, which is an `OperationCanceledException`. The dispatcher and `KafkaConsumer` already treat it as cancellation: the loop exits, `ConsumeAsync` returns, and `ConsumerResilience` restarts immediately with no 10 s delay. It is redelivered either way. For Enrichment this is new on this branch: the old catch-all swallowed an Ollama timeout (2 min `HttpClient` timeout), whereas the classifier now calls it transient, so it escapes Enrichment's catch and reaches the dispatcher's cancellation catch before classification. Observability gap: no retries, no "Transient failure persisted" log, no `consumer.transient_halts` count, and an immediate restart; the only signal is `EnrichmentService`'s own error log. No event is lost (nothing is committed), but a document whose generation always times out loops about every 2 min at the head of its partition. A code fix (classify before the cancellation catch) is deliberately deferred.
+- **HTTP timeouts reaching the dispatcher directly (Engagement, Intelligence: existing, unchanged):** an HTTP timeout is a `TaskCanceledException`, which is an `OperationCanceledException`. The dispatcher and `KafkaConsumer` already treat it as cancellation: the loop exits, `ConsumeAsync` returns, and `ConsumerResilience` restarts immediately with no 10 s delay. It is redelivered either way, with no halt log or `consumer.transient_halts` count. Enrichment no longer takes this path: its Ollama timeouts are skipped, logged and counted (`docs/specs/2026-10-01-enrichment-timeout-skip-design.md`).
 
 ## Out of scope
 
```

- [ ] **Step 2: Check the md5s.** From the repository root:

```bash
md5sum Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs Iverson.Server/Iverson.Api/Reconciliation/ReconciliationTelemetry.cs docs/specs/2026-09-30-transient-projection-failures-design.md
```

Expected:
```
b23926b460ba989af1fdbc27cfb25ff9  Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs
ded97faa3afcdfe91e7d86682610ba78  Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs
e400760ebdb65d6cc2f6461c6c5bcab2  Iverson.Server/Iverson.Api/Reconciliation/ReconciliationTelemetry.cs
47350b9d0d54b47cc180da102b497e80  docs/specs/2026-09-30-transient-projection-failures-design.md
```

- [ ] **Step 3: Run the tests and build.** From `Iverson.Server/`:

```bash
systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.Api.Tests -m:2 --filter "FullyQualifiedName~Iverson.Api.Tests.Consumers.EnrichmentConsumerTests"
systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.Api.Tests -m:2 --filter "Category!=Integration"
systemd-run --user --scope -q -p MemoryMax=5G dotnet build Iverson.Server.slnx -m:2 --no-incremental
```

Expected:
- The first command reports `Passed: 26`, up from 23.
- The second reports `Passed: 1270`, up from 1267.
- The build reports 0 errors and 21 warnings, unchanged.

- [ ] **Step 4: Commit the code and tests.** From the repository root:

```bash
git add Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs Iverson.Server/Iverson.Api/Reconciliation/ReconciliationTelemetry.cs Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs
git commit -m "skip ollama timeouts in enrichment visibly instead of redelivering them" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 5: Commit the spec update.** From the repository root:

```bash
git add -f docs/specs/2026-09-30-transient-projection-failures-design.md
git commit -m "point the transient failures spec at the enrichment timeout skip" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>" -- docs/specs/2026-09-30-transient-projection-failures-design.md
```

## Tasks NOT in this plan

- Routing any consumer's HTTP timeout through the dispatcher's classified halt path.
- Changing the Ollama `HttpClient` timeout (`EnrichmentServiceOptions.Timeout`, 2 min).
- A dedicated enrichment backfill sweep.

## Known issues inherited from spec

- **During an Ollama overload, objects stay unenriched until recovered.** While Ollama is slow enough to time out, every object it times out on is skipped and stays unenriched until its next change or a reconcile. That is the best-effort contract Enrichment had before the transient change, now with a per-object Warning and a counter.
- **A skip is not a lasting fix.** A document that always times out is skipped on every event for it, and is never enriched until its source text changes.
- **Engagement and Intelligence timeouts are unchanged.** Their HTTP timeouts keep the existing immediate-restart cancellation path: redelivered, with no halt log or counter. "Never block" doesn't apply to them, because skipping would drop vectors or projections, which are not best-effort.
