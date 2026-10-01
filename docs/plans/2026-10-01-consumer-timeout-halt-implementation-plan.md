# Consumer Timeout Halt Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-10-01-consumer-timeout-halt-design.md` (commit SHA: `664599e2`)

**Goal:** A handler cancelled while the consumer's token is live (a dependency timeout, in practice Intelligence's embedding HTTP timeout) halts visibly: a Critical log, `consumer.transient_halts` +1, the 10 s restart delay and redelivery. Today it restarts silently and at once.

**Architecture:** `MessageDispatcher.DispatchAsync` splits its cancellation catch. A cancellation on shutdown is rethrown as before. Any other cancellation is logged, counted and rethrown at once, never retried or dead-lettered. `KafkaConsumer.ConsumeAsync` filters both of its cancellation catches on the consumer token, so a timeout leaves the loop as a halt and `ConsumerResilience` backs off. The counter's description and two older specs are updated to match.

**Tech stack:** .NET 10; Confluent.Kafka 2.15.1; System.Diagnostics.Metrics (`MeterListener` in tests); xunit, NSubstitute, FluentAssertions.

---

## Global Constraints

- **The code is the patch.** The task's code and docs are ONE `git apply` patch, generated from a prototype that compiled and passed. It was verified to apply to `main` at `664599e2` and to reproduce every listed md5. Never retype it: slice it from the task brief with the given command and check the md5s.
- **Cap build memory.** An uncapped build was OOM-killed alongside the docker stack, so every `dotnet` command runs as `systemd-run --user --scope -q -p MemoryMax=4G dotnet … -m:2`. The one exception is the solution build, which uses 5G.
- **Unit tests only:** no Docker is needed. The Api suite uses `--filter "Category!=Integration"`; the Events suite has no integration tests.
- **Commits:** a lowercase imperative subject, a blank line, then `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>` (two `-m` flags). Commit explicit paths only (`git commit … -- <paths>`). `docs/` is gitignored: use `git add -f`.
- **Shell `grep` skips gitignored paths, including `docs/`.** Use `command grep`.

## File Structure

- **Modify:** `Iverson.Server/Iverson.Events/MessageDispatcher.cs`.
  - The cancellation catch splits into a shutdown rethrow (`when (ct.IsCancellationRequested)`) and a timeout halt (Critical log, `Telemetry.ConsumerTransientHalts.Add(1)`, rethrow).
  - The class doc gains one delivery-contract bullet.
- **Modify:** `Iverson.Server/Iverson.Events/KafkaConsumer.cs`. `ConsumeAsync`'s inner `throw;` catch and outer `break;` catch gain `when (cancellationToken.IsCancellationRequested)`. `ConsumeRawAsync` is untouched.
- **Modify:** `Iverson.Server/Iverson.Events/Telemetry.cs`. The `consumer.transient_halts` description now also names a dependency timeout; the instrument name is unchanged.
- **Test:** `Iverson.Server/Iverson.Events.Tests/MessageDispatcherTests.cs`. Adds `using Confluent.Kafka.Admin;` and four tests in a new section:
  - `Timeout_WhileConsumerRuns_HaltsAtOnce_NoRetry_NoDlq_CountsHalt`;
  - `PlainCancellation_WhileConsumerRuns_IsNeverDeadLettered`;
  - `Shutdown_RethrowsWithoutCountingAHalt`;
  - `KafkaConsumer_HandlerTimeout_ThrowsOutOfConsumeAsync_WithoutCommitting`, which also asserts the consume loop's own Critical halt log.

  They live here because they touch the dispatcher's counters (spec, Testing).
- **Test:** `Iverson.Server/Iverson.Events.Tests/KafkaConsumerTests.cs`.
  - `CreateConsumer` returns a fourth element, a token its fake `Consume` cancels before throwing.
  - The three tests that destructure it pass that token instead of `CancellationToken.None`.
  - Adds `ConsumeAsync_ShutdownDuringDispatch_ReturnsNormally_WithoutCommitting`.
- **Docs:** `docs/specs/2026-09-30-transient-projection-failures-design.md` has two passages updated:
  - the dispatcher bullet's "`OperationCanceledException` still rethrows immediately";
  - the Known-limitations bullet "HTTP timeouts reaching the dispatcher directly".
- **Docs:** `docs/specs/2026-10-01-enrichment-timeout-skip-design.md` updates its "Engagement and Intelligence timeouts" limitation.

## Inherited from spec

These assumptions were verified by `thorough-brainstorming` and reconfirmed by CDR round 1 (✅). They are not re-verified here. The table is copied verbatim from the spec's "Verified assumptions":

| # | Assumption | Evidence |
|---|---|---|
| 1 | Today a handler timeout is indistinguishable from a shutdown: `ConsumeAsync` returns normally after one attempt, with no commit and no halt count. | Probe through the real `KafkaConsumer` and `MessageDispatcher` at `cdbd7511` (fake `IConsumer`, handler throwing `TaskCanceledException > TimeoutException`, live token): `returned normally; handlerCalls=1; commits=0; transient_halts=0`. A shutdown gave the identical line. |
| 2 | With this design, a timeout halts after one attempt, is counted, and is not committed, while shutdown is unchanged. | Same probe on a prototype of §1: `threw TaskCanceledException; handlerCalls=1; commits=0; transient_halts=1`; shutdown `returned normally; handlerCalls=1; commits=0; transient_halts=0`. The retry-loop alternative (A) gave `handlerCalls=3; transient_halts=1`. |
| 3 | The dispatcher's `ct` is the consumer's token, and Intelligence passes it to `HttpClient`. | `KafkaConsumer.cs:65` passes `cancellationToken`; `IntelligenceStoreConsumer.DispatchAsync` → `HandleAsync(key, value, ct)` (`:65`) → `EmbedDocumentAsync(…, ct)` (`:145`, `:242`) → `EmbeddingService.EmbedAsync` → `client.SendAsync(request, ct)`. |
| 4 | An embedding timeout reaches the dispatcher unwrapped. | `EmbeddingService`'s `catch (Exception ex)` logs at Error and rethrows (`throw;`). The awaited `Task.WhenAll` (`IntelligenceStoreConsumer.cs:149`, `:247`) rethrows the first faulted task's exception. |
| 5 | The embedding client's timeout is 100 s. | It is registered without setting `Timeout` (`Iverson.Embeddings/ServiceCollectionExtensions.cs:12-19`); a probe printed `new HttpClient().Timeout = 00:01:40`. |
| 6 | Kafka's `max.poll.interval.ms` is 300 s here. | Nothing in the repo sets it (`command grep` for `MaxPollInterval` / `max.poll` finds nothing); librdkafka 2.15.1 `CONFIGURATION.md` gives the default `300000`. |
| 7 | Confluent's `Consume(CancellationToken)` throws `OperationCanceledException` only on cancellation, so filtering the outer catch is safe. | Confluent.Kafka 2.15.1 XML doc for `IConsumer.Consume(CancellationToken)`: `OperationCanceledException` "Thrown on cancellation"; every other failure is a `ConsumeException`. |
| 8 | `ConsumerResilience` treats a non-shutdown cancellation as a fault: Critical log, 10 s delay, restart. | `ConsumerResilience.cs`: `catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }`, then `catch (Exception ex)` logs Critical and awaits `Task.Delay(delay, ct)`. Each consumer passes the same `ct` to `RunWithRestartAsync` and `ConsumeAsync`. |
| 9 | Only Intelligence can raise a non-shutdown cancellation into the dispatcher. | Enrichment: its own catch handles Ollama timeouts first, and its catch-all swallows other cancellations. Engagement: MySqlConnector and Polly (retry + circuit breaker, no timeout strategy); its liveness probe cancels its own token internally and never rethrows (`EngagementRepository.cs:88-104`). Popularity and DocumentRerender: no HTTP calls; their Qdrant calls are gRPC (`RpcException`). |
| 10 | Engagement makes no HTTP calls on its handler path. | `EngagementStoreConsumer` calls only `registry`, `entities` and `sr` (`EngagementRepository`, MySqlConnector); `command grep` for `HttpClient` in `Iverson.StarRocks` finds none. |
| 11 | `ConsumeRawAsync` is used only by the DLQ monitor, which makes no HTTP calls. | `command grep` for `ConsumeRawAsync`: `DlqMonitorConsumer.cs:21` only; that file references no HTTP or embedding client. |
| 12 | Plain `TaskCanceledException` is classified non-transient, so approach A would dead-letter it. | `TransientFailuresTests.cs:97` pins `TaskCanceledException` without an inner `TimeoutException` as non-transient. |
| 13 | Only the two `KafkaConsumerTests` that rely on the fake `Consume` break. No other test pins the old behaviour. | Events suite on the prototype: 35 passed, 2 failed, both those tests. The only other `KafkaConsumer` construction in tests is `EngagementStoreConsumerKafkaOrderingTests` (`Category=Integration`), which stops by cancelling. |
| 14 | `MessageDispatcherTests` observes the dispatcher's counters with a `MeterListener` on meter `Iverson.Events`, and tests touching them share that class to avoid parallel runs. | `MessageDispatcherTests.cs:185-241` (listener; `transient_halts` asserted `1`); `:289-291` (comment on keeping counter-touching tests in the class); `KafkaConsumerTests.cs:84-88` (deliberately avoids the counters for the same reason). |
| 15 | The spec text to update exists where §2 says. | Transient spec `:75` ("`OperationCanceledException` still rethrows immediately") and `:152` (the HTTP-timeouts limitation); enrichment spec `:116` ("Engagement and Intelligence timeouts are unchanged"). |

## Verified plan-level assumptions

The code was prototyped on the throwaway branch `cth-proto` (`.worktrees/cth-proto`), two commits on `664599e2`. The patch below is `git diff 664599e2 cth-proto`.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | Paths | All seven modified files exist at the listed paths, and nothing is created. | The patch's `+++ b/` list; each file exists at `664599e2`. |
| 2 | Signature | Inside `DispatchAsync`'s loop, `logger`, `ctx` and `ct` are in scope, and `Telemetry.ConsumerTransientHalts` (`internal`, same assembly) is reachable. | `MessageDispatcher.cs:51-54` (parameters), `:91` (existing `Telemetry.ConsumerTransientHalts.Add(1)`); the prototype compiles. |
| 3 | Signature | `ConsumeAsync`'s token parameter is `cancellationToken`. The patch touches only `ConsumeAsync`'s two catches (`:69`, `:85`), not `ConsumeRawAsync`'s at `:130`, because filtering that one breaks its test (CDR round 1). | `KafkaConsumer.cs:20`; the patch's `KafkaConsumer.cs` hunks touch only two lines. |
| 4 | Signature | The test helpers the new tests use exist with these shapes: <br>• `BuildSut(int maxAttempts = 3)` and `BuildSut(Func<Exception, bool>, int)`, both with zero backoff; <br>• `Ctx()`; <br>• `_producer`, stubbed to return a `DeliveryResult`; <br>• the inline `MeterListener` on meter `Iverson.Events`. <br>`CreateConsumer`'s three destructuring callers are all updated to the 4-tuple. | `MessageDispatcherTests.cs:20-56`, `:183-242`; `KafkaConsumerTests.cs:46`, `:61`, `:75` on the prototype. |
| 5 | Code validity | NSubstitute's `When(…).Do(…)` throwing from `Do`, and `ReceivedCalls()` with `GetMethodInfo()`/`GetArguments()`, compile and behave as used. | The prototype compiles; the Events suite passes 40/40. |
| 6 | Command | Expected counts: <br>• Events suite 35 → 40; <br>• Api unit suite (`Category!=Integration`) 1274, unchanged; <br>• `Iverson.Server.slnx` build 0 errors, 21 warnings, unchanged. | `git grep -c '[Fact]\|[Theory]'` over `Iverson.Events.Tests` at `664599e2` sums to 35 (no `InlineData`); measured on the prototype: 40/40, 1274/1274, 0 errors / 21 warnings. |
| 7 | Consumer impact | Nothing reads the counter's description, and the instrument name is unchanged. The only other `KafkaConsumer` in tests (`EngagementStoreConsumerKafkaOrderingTests`, `Category=Integration`) stops by cancelling, so the filters don't change it. | `command grep -rn 'transient_halts\|persisted past the dispatcher'` outside `docs/` finds only `Telemetry.cs` and the dispatcher tests; Api unit suite 1274/1274 (includes `AdminConsoleMetricsSeriesNamesTests`); spec assumption 13. |
| 8 | Mutation | Each new assertion kills its mutant. Applied one at a time to the prototype, running the Events suite each time and restoring byte for byte: <br>• dropping the halt counter fails `Timeout_WhileConsumerRuns_…`; <br>• narrowing the halt catch to `when (ex.InnerException is TimeoutException)` fails `PlainCancellation_…`; <br>• dropping `KafkaConsumer`'s inner filter fails `KafkaConsumer_HandlerTimeout_…` (the halt-log assertion); <br>• dropping its outer filter fails `KafkaConsumer_HandlerTimeout_…`; <br>• deleting the dispatcher's shutdown catch fails `Shutdown_RethrowsWithoutCountingAHalt`; <br>• inverting the outer filter fails `ConsumeAsync_ShutdownDuringDispatch_…` and three others. <br>Unfiltering the shutdown catch does not compile (CS0160). | Each mutant run: 1 failure, the named test (the inverted filter: 4); files restored and compared with `cmp`. |
| 9 | Docs | The three replaced doc passages match the patch's context. | The patch applies cleanly to `664599e2`. |
| 10 | Convention | The commit style is a lowercase imperative subject plus the trailer. | `git log --format=%s -8`. |

## Tasks

### Task 1: Halt on a cancellation while the consumer is running

**Files:**
- Modify: `Iverson.Server/Iverson.Events/MessageDispatcher.cs`
- Modify: `Iverson.Server/Iverson.Events/KafkaConsumer.cs`
- Modify: `Iverson.Server/Iverson.Events/Telemetry.cs`
- Test: `Iverson.Server/Iverson.Events.Tests/MessageDispatcherTests.cs`
- Test: `Iverson.Server/Iverson.Events.Tests/KafkaConsumerTests.cs`
- Docs: `docs/specs/2026-09-30-transient-projection-failures-design.md`
- Docs: `docs/specs/2026-10-01-enrichment-timeout-skip-design.md`

- [ ] **Step 1: Apply the patch.** `BRIEF` is the path of this task brief. The command slices the brief's only `diff` block byte for byte; never retype it. From the repository root:

```bash
python3 - "$BRIEF" > /tmp/claude-1000/cth-task1.patch <<'PY'
import re, sys
fence = chr(96) * 3
s = open(sys.argv[1]).read()
print(re.search(fence + "diff\n(.*?)" + fence, s, re.S).group(1), end="")
PY
git apply --check /tmp/claude-1000/cth-task1.patch && git apply /tmp/claude-1000/cth-task1.patch
```

If `--check` fails, stop and report. Do not hand-edit. The patch:

```diff
diff --git a/Iverson.Server/Iverson.Events.Tests/KafkaConsumerTests.cs b/Iverson.Server/Iverson.Events.Tests/KafkaConsumerTests.cs
index 9d888017..1f421955 100644
--- a/Iverson.Server/Iverson.Events.Tests/KafkaConsumerTests.cs
+++ b/Iverson.Server/Iverson.Events.Tests/KafkaConsumerTests.cs
@@ -11,13 +11,18 @@ namespace Iverson.Events.Tests;
 
 public sealed class KafkaConsumerTests
 {
-    private static (KafkaConsumer consumer, IConsumer<string, string> fakeConsumer, IAdminClient fakeAdmin) CreateConsumer()
+    private static (KafkaConsumer consumer, IConsumer<string, string> fakeConsumer, IAdminClient fakeAdmin, CancellationToken ct) CreateConsumer()
     {
         var fakeConsumer = Substitute.For<IConsumer<string, string>>();
-        // Throwing OperationCanceledException from the first Consume() call causes
-        // ConsumeAsync's loop to hit its `catch (OperationCanceledException) { break; }`
-        // handler immediately, so the test doesn't hang in an infinite polling loop.
-        fakeConsumer.When(x => x.Consume(Arg.Any<CancellationToken>())).Throw(new OperationCanceledException());
+        // The first Consume() call cancels the token and throws, as the real client does on shutdown,
+        // so ConsumeAsync's loop hits its `catch (OperationCanceledException) when (...) { break; }`
+        // handler immediately and the test doesn't hang in an infinite polling loop.
+        var cts = new CancellationTokenSource();
+        fakeConsumer.When(x => x.Consume(Arg.Any<CancellationToken>())).Do(_ =>
+        {
+            cts.Cancel();
+            throw new OperationCanceledException(cts.Token);
+        });
 
         var fakeAdmin = Substitute.For<IAdminClient>();
         fakeAdmin.CreateTopicsAsync(Arg.Any<IEnumerable<TopicSpecification>>()).Returns(Task.CompletedTask);
@@ -32,19 +37,19 @@ public sealed class KafkaConsumerTests
             _ => fakeConsumer,
             _ => fakeAdmin);
 
-        return (consumer, fakeConsumer, fakeAdmin);
+        return (consumer, fakeConsumer, fakeAdmin, cts.Token);
     }
 
     [Fact]
     public async Task ConsumeAsync_UsesInjectedConsumerFactory_NotAConcreteConfluentClient()
     {
-        var (consumer, fakeConsumer, _) = CreateConsumer();
+        var (consumer, fakeConsumer, _, ct) = CreateConsumer();
 
         await consumer.ConsumeAsync(
             "topic",
             "group",
             (_, _, _) => Task.CompletedTask,
-            CancellationToken.None);
+            ct);
 
         fakeConsumer.Received(1).Subscribe("topic");
         fakeConsumer.Received(1).Close();
@@ -53,13 +58,13 @@ public sealed class KafkaConsumerTests
     [Fact]
     public async Task ConsumeAsync_UsesInjectedAdminClientFactory_ToEnsureTopicExists()
     {
-        var (consumer, _, fakeAdmin) = CreateConsumer();
+        var (consumer, _, fakeAdmin, ct) = CreateConsumer();
 
         await consumer.ConsumeAsync(
             "topic",
             "group",
             (_, _, _) => Task.CompletedTask,
-            CancellationToken.None);
+            ct);
 
         await fakeAdmin.Received(1).CreateTopicsAsync(Arg.Any<IEnumerable<TopicSpecification>>());
     }
@@ -67,13 +72,13 @@ public sealed class KafkaConsumerTests
     [Fact]
     public async Task ConsumeRawAsync_UsesInjectedConsumerFactory_NotAConcreteConfluentClient()
     {
-        var (consumer, fakeConsumer, _) = CreateConsumer();
+        var (consumer, fakeConsumer, _, ct) = CreateConsumer();
 
         await consumer.ConsumeRawAsync(
             "topic",
             "group",
             (_, _, _, _) => Task.CompletedTask,
-            CancellationToken.None);
+            ct);
 
         fakeConsumer.Received(1).Subscribe("topic");
     }
@@ -120,4 +125,43 @@ public sealed class KafkaConsumerTests
         fakeConsumer.DidNotReceive().Commit(Arg.Any<ConsumeResult<string, string>>());
         fakeConsumer.Received(1).Consume(Arg.Any<CancellationToken>());
     }
+
+    [Fact]
+    public async Task ConsumeAsync_ShutdownDuringDispatch_ReturnsNormally_WithoutCommitting()
+    {
+        // Only a cancelled consumer token is shutdown: the loop exits quietly and the uncommitted message is
+        // redelivered on the next start. (A cancellation while the token is live halts instead — see
+        // MessageDispatcherTests, which owns the tests that touch the dispatcher's counters.)
+        var result = new ConsumeResult<string, string>
+        {
+            Message              = new Message<string, string> { Key = "key-1", Value = "{}", Headers = new Headers() },
+            TopicPartitionOffset = new TopicPartitionOffset("topic", new Partition(0), new Offset(7)),
+        };
+        var fakeConsumer = Substitute.For<IConsumer<string, string>>();
+        fakeConsumer.Consume(Arg.Any<CancellationToken>()).Returns(result);
+        var fakeAdmin = Substitute.For<IAdminClient>();
+        fakeAdmin.CreateTopicsAsync(Arg.Any<IEnumerable<TopicSpecification>>()).Returns(Task.CompletedTask);
+
+        var consumer = new KafkaConsumer(
+            new KafkaOptions { BootstrapServers = "localhost:9092" },
+            NullLogger<KafkaConsumer>.Instance,
+            new MessageDispatcher(Substitute.For<IProducer<string, string>>(), NullLogger<MessageDispatcher>.Instance),
+            _ => fakeConsumer,
+            _ => fakeAdmin);
+
+        using var cts = new CancellationTokenSource();
+        var act = () => consumer.ConsumeAsync(
+            "topic",
+            "group",
+            (_, _, _) =>
+            {
+                cts.Cancel();
+                throw new OperationCanceledException(cts.Token);
+            },
+            cts.Token);
+
+        await act.Should().NotThrowAsync();
+        fakeConsumer.DidNotReceive().Commit(Arg.Any<ConsumeResult<string, string>>());
+        fakeConsumer.Received(1).Close();
+    }
 }
diff --git a/Iverson.Server/Iverson.Events.Tests/MessageDispatcherTests.cs b/Iverson.Server/Iverson.Events.Tests/MessageDispatcherTests.cs
index 30a248cf..bcf0c0ae 100644
--- a/Iverson.Server/Iverson.Events.Tests/MessageDispatcherTests.cs
+++ b/Iverson.Server/Iverson.Events.Tests/MessageDispatcherTests.cs
@@ -1,6 +1,7 @@
 using System.Diagnostics.Metrics;
 using System.Text;
 using Confluent.Kafka;
+using Confluent.Kafka.Admin;
 using FluentAssertions;
 using Iverson.Events;
 using Microsoft.Extensions.Configuration;
@@ -283,6 +284,131 @@ public sealed class MessageDispatcherTests
                 Arg.Any<CancellationToken>());
     }
 
+    // ── Cancellation while the consumer is running (a dependency timeout) ───────
+    // HttpClient surfaces its timeout as a TaskCanceledException, an OperationCanceledException. Only a
+    // cancelled consumer token means shutdown; any other cancellation halts at once — no in-place retry
+    // (three 100 s attempts would pass Kafka's 300 s max.poll.interval.ms) and never a dead-letter. These
+    // count consumer.transient_halts, so they live in this class with the other MeterListener assertions.
+
+    [Fact]
+    public async Task Timeout_WhileConsumerRuns_HaltsAtOnce_NoRetry_NoDlq_CountsHalt()
+    {
+        var measurements = new Dictionary<string, long>();
+        using var listener = new MeterListener();
+        listener.InstrumentPublished = (inst, l) =>
+        {
+            if (inst.Meter.Name == "Iverson.Events") l.EnableMeasurementEvents(inst);
+        };
+        listener.SetMeasurementEventCallback<long>((inst, val, _, _) =>
+        {
+            measurements.TryGetValue(inst.Name, out var cur);
+            measurements[inst.Name] = cur + val;
+        });
+        listener.Start();
+
+        var calls = 0;
+        Task Handler(string k, string v, CancellationToken c)
+        {
+            calls++;
+            throw new TaskCanceledException("timed out", new TimeoutException());
+        }
+
+        var act = async () => await BuildSut(maxAttempts: 3).DispatchAsync(Ctx(), Handler, CancellationToken.None);
+
+        await act.Should().ThrowAsync<TaskCanceledException>();
+        listener.Dispose();
+
+        calls.Should().Be(1);
+        await _producer.DidNotReceive()
+            .ProduceAsync(Arg.Any<string>(), Arg.Any<Message<string, string>>(), Arg.Any<CancellationToken>());
+        measurements.GetValueOrDefault("consumer.transient_halts").Should().Be(1);
+        measurements.GetValueOrDefault("consumer.retries").Should().Be(0);
+    }
+
+    [Fact]
+    public async Task PlainCancellation_WhileConsumerRuns_IsNeverDeadLettered()
+    {
+        // No inner TimeoutException, so the transient classifier would call it permanent: it must still halt.
+        Task Handler(string k, string v, CancellationToken c) => throw new TaskCanceledException("cancelled");
+
+        var act = async () => await BuildSut(_ => false, maxAttempts: 3).DispatchAsync(Ctx(), Handler, CancellationToken.None);
+
+        await act.Should().ThrowAsync<TaskCanceledException>();
+        await _producer.DidNotReceive()
+            .ProduceAsync(Arg.Any<string>(), Arg.Any<Message<string, string>>(), Arg.Any<CancellationToken>());
+    }
+
+    [Fact]
+    public async Task Shutdown_RethrowsWithoutCountingAHalt()
+    {
+        var measurements = new Dictionary<string, long>();
+        using var listener = new MeterListener();
+        listener.InstrumentPublished = (inst, l) =>
+        {
+            if (inst.Meter.Name == "Iverson.Events") l.EnableMeasurementEvents(inst);
+        };
+        listener.SetMeasurementEventCallback<long>((inst, val, _, _) =>
+        {
+            measurements.TryGetValue(inst.Name, out var cur);
+            measurements[inst.Name] = cur + val;
+        });
+        listener.Start();
+
+        using var cts = new CancellationTokenSource();
+        var calls = 0;
+        Task Handler(string k, string v, CancellationToken c)
+        {
+            calls++;
+            cts.Cancel();
+            throw new OperationCanceledException(cts.Token);
+        }
+
+        var act = async () => await BuildSut(maxAttempts: 3).DispatchAsync(Ctx(), Handler, cts.Token);
+
+        await act.Should().ThrowAsync<OperationCanceledException>();
+        listener.Dispose();
+
+        calls.Should().Be(1);
+        measurements.GetValueOrDefault("consumer.transient_halts").Should().Be(0);
+    }
+
+    [Fact]
+    public async Task KafkaConsumer_HandlerTimeout_ThrowsOutOfConsumeAsync_WithoutCommitting()
+    {
+        // End to end through the consume loop: before this change a timeout became a quiet `break` and
+        // ConsumeAsync returned normally, so ConsumerResilience restarted at once with no delay or log.
+        var result = new ConsumeResult<string, string>
+        {
+            Message              = new Message<string, string> { Key = "key-1", Value = "{}", Headers = new Headers() },
+            TopicPartitionOffset = new TopicPartitionOffset("topic", new Partition(0), new Offset(7)),
+        };
+        var fakeConsumer = Substitute.For<IConsumer<string, string>>();
+        fakeConsumer.Consume(Arg.Any<CancellationToken>()).Returns(result);
+        var fakeAdmin = Substitute.For<IAdminClient>();
+        fakeAdmin.CreateTopicsAsync(Arg.Any<IEnumerable<TopicSpecification>>()).Returns(Task.CompletedTask);
+
+        var consumerLogger = Substitute.For<ILogger<KafkaConsumer>>();
+        var consumer = new KafkaConsumer(
+            new KafkaOptions { BootstrapServers = "localhost:9092" },
+            consumerLogger,
+            BuildSut(),
+            _ => fakeConsumer,
+            _ => fakeAdmin);
+
+        var act = () => consumer.ConsumeAsync(
+            "topic",
+            "group",
+            (_, _, _) => throw new TaskCanceledException("timed out", new TimeoutException()),
+            CancellationToken.None);
+
+        await act.Should().ThrowAsync<TaskCanceledException>();
+        fakeConsumer.DidNotReceive().Commit(Arg.Any<ConsumeResult<string, string>>());
+        // The consume loop's own halt log, not just the dispatcher's: the timeout reached its catch-all.
+        consumerLogger.ReceivedCalls()
+            .Count(c => c.GetMethodInfo().Name == nameof(ILogger.Log) && (LogLevel)c.GetArguments()[0]! == LogLevel.Critical)
+            .Should().Be(1);
+    }
+
     // ── AddKafka wiring ─────────────────────────────────────────────────────────
     // AddKafka is the only path by which the API's transient classifier reaches the dispatcher the
     // consumers run under: if it dropped the predicate, every transient dependency failure would
diff --git a/Iverson.Server/Iverson.Events/KafkaConsumer.cs b/Iverson.Server/Iverson.Events/KafkaConsumer.cs
index 840c16e7..0337e513 100644
--- a/Iverson.Server/Iverson.Events/KafkaConsumer.cs
+++ b/Iverson.Server/Iverson.Events/KafkaConsumer.cs
@@ -66,7 +66,7 @@ public class KafkaConsumer(
                     consumer.Commit(result);
                     activity?.SetStatus(ActivityStatusCode.Ok);
                 }
-                catch (OperationCanceledException)
+                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                 {
                     throw;
                 }
@@ -82,7 +82,7 @@ public class KafkaConsumer(
                     throw;
                 }
             }
-            catch (OperationCanceledException)
+            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
             {
                 break;
             }
diff --git a/Iverson.Server/Iverson.Events/MessageDispatcher.cs b/Iverson.Server/Iverson.Events/MessageDispatcher.cs
index e49aabfb..f469182c 100644
--- a/Iverson.Server/Iverson.Events/MessageDispatcher.cs
+++ b/Iverson.Server/Iverson.Events/MessageDispatcher.cs
@@ -38,6 +38,9 @@ public sealed class MessageDispatcherOptions
 ///     failing after the bounded attempts → throw, no dead-letter (caller must NOT commit — the
 ///     consumer halts and the message is redelivered once the dependency is back);
 ///   - PoisonMessageException → dead-letter immediately (no retry);
+///   - OperationCanceledException while <c>ct</c> is live (a dependency timeout: HttpClient surfaces
+///     its timeout as a TaskCanceledException) → throw at once, no retry and no dead-letter (caller
+///     must NOT commit); on shutdown it is rethrown as cancellation;
 ///   - success or successful dead-letter → return normally (caller commits the offset);
 ///   - DLQ write itself fails → throw (caller must NOT commit — halt rather than lose).
 /// </summary>
@@ -71,10 +74,22 @@ public sealed class MessageDispatcher(
                 await DeadLetterAsync(ctx, ex, attempt + 1, ct);
                 return;
             }
-            catch (OperationCanceledException)
+            catch (OperationCanceledException) when (ct.IsCancellationRequested)
             {
                 throw;
             }
+            catch (OperationCanceledException ex)
+            {
+                // Cancelled while the consumer's token is live: a dependency timeout (HttpClient surfaces its
+                // timeout as a TaskCanceledException). Not a bad message, so never dead-lettered; not retried in
+                // place, because one attempt can take HttpClient's 100 s and three would pass Kafka's 300 s
+                // max.poll.interval.ms.
+                logger.LogCritical(ex,
+                    "[Dispatch] Handler cancelled while the consumer is running (a dependency timeout) topic={Topic} key={Key} — not dead-lettering; halting for redelivery",
+                    ctx.SourceTopic, ctx.Key);
+                Telemetry.ConsumerTransientHalts.Add(1);
+                throw;
+            }
             catch (Exception ex)
             {
                 attempt++;
diff --git a/Iverson.Server/Iverson.Events/Telemetry.cs b/Iverson.Server/Iverson.Events/Telemetry.cs
index e0935d47..75601019 100644
--- a/Iverson.Server/Iverson.Events/Telemetry.cs
+++ b/Iverson.Server/Iverson.Events/Telemetry.cs
@@ -17,5 +17,5 @@ internal static class Telemetry
         Meter.CreateCounter<long>("consumer.dlq_routed", description: "Messages routed to the dead-letter queue");
 
     internal static readonly Counter<long> ConsumerTransientHalts =
-        Meter.CreateCounter<long>("consumer.transient_halts", description: "Consumer halts on a transient failure that persisted past the dispatcher's attempts");
+        Meter.CreateCounter<long>("consumer.transient_halts", description: "Consumer halts for redelivery: a transient failure that persisted past the dispatcher's attempts, or a dependency timeout");
 }
diff --git a/docs/specs/2026-09-30-transient-projection-failures-design.md b/docs/specs/2026-09-30-transient-projection-failures-design.md
index 0e64748a..7a4fbb45 100644
--- a/docs/specs/2026-09-30-transient-projection-failures-design.md
+++ b/docs/specs/2026-09-30-transient-projection-failures-design.md
@@ -72,7 +72,7 @@ Everything else is non-transient, including:
   - increment a new counter, `Telemetry.ConsumerTransientHalts` (`consumer.transient_halts`), next to the existing `consumer.retries` and `consumer.dlq_routed` counters at `Telemetry.cs:13-17`;
   - rethrow the original exception, with no DLQ write.
 
-  Otherwise it dead-letters as today. Poison messages still dead-letter immediately. `OperationCanceledException` still rethrows immediately, and a failed DLQ write still throws.
+  Otherwise it dead-letters as today. Poison messages still dead-letter immediately. `OperationCanceledException` still rethrows immediately on shutdown; a cancellation while the consumer's token is live (a dependency timeout) halts at once instead (`docs/specs/2026-10-01-consumer-timeout-halt-design.md`). A failed DLQ write still throws.
 - **`AddKafka`** (`Iverson.Events/ServiceCollectionExtensions.cs:11`) gains an optional `Func<Exception, bool>? isTransient = null` parameter. The single production construction site (`:47`) passes it into `new MessageDispatcherOptions { IsTransient = isTransient ?? (_ => false) }`. `Program.cs:340` changes to `builder.Services.AddKafka(cfg, isTransient: TransientFailures.IsTransient);`.
 - **`KafkaConsumer.ConsumeAsync`** has no behavior change. Only the comment and log wording at the halt site change: from "the DLQ write itself failed" to "the DLQ write failed or a dependency is transiently unavailable".
 - **`ConsumerResilience`** is unchanged. Each of the five consumers runs its own group and loop, so an outage halts only the consumers that need the missing dependency.
@@ -149,7 +149,7 @@ All unit tests use each project's existing fakes. No containers are needed.
 - **Extra round-trip:** one FE liveness check per StarRocks failure that is neither transient nor an expected missing resource.
 - **gRPC behavior change:** reads return `Unavailable` during a StarRocks backend outage (§5).
 - **Misclassification has a cost in both directions.** A transient failure classified as permanent is still dead-lettered. A permanent failure classified as transient stalls its consumer until someone intervenes. The classifier test matrix is the guard.
-- **HTTP timeouts reaching the dispatcher directly (Engagement, Intelligence: existing, unchanged):** an HTTP timeout is a `TaskCanceledException`, which is an `OperationCanceledException`. The dispatcher and `KafkaConsumer` already treat it as cancellation: the loop exits, `ConsumeAsync` returns, and `ConsumerResilience` restarts immediately with no 10 s delay. It is redelivered either way, with no halt log or `consumer.transient_halts` count. Enrichment no longer takes this path: its Ollama timeouts are skipped, logged and counted (`docs/specs/2026-10-01-enrichment-timeout-skip-design.md`).
+- **HTTP timeouts reaching the dispatcher directly (resolved):** Engagement makes no HTTP calls (it reaches StarRocks through MySqlConnector), so only Intelligence's embedding timeouts reach the dispatcher, as a `TaskCanceledException`. They now halt like any transient outage: a Critical log, `consumer.transient_halts`, the 10 s restart delay and redelivery, instead of a silent immediate restart (`docs/specs/2026-10-01-consumer-timeout-halt-design.md`). Enrichment's Ollama timeouts are skipped, logged and counted before they reach the dispatcher (`docs/specs/2026-10-01-enrichment-timeout-skip-design.md`).
 
 ## Out of scope
 
diff --git a/docs/specs/2026-10-01-enrichment-timeout-skip-design.md b/docs/specs/2026-10-01-enrichment-timeout-skip-design.md
index bb50f97a..da091036 100644
--- a/docs/specs/2026-10-01-enrichment-timeout-skip-design.md
+++ b/docs/specs/2026-10-01-enrichment-timeout-skip-design.md
@@ -113,7 +113,7 @@ All tests go in `Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTe
 
 - **During an Ollama overload, objects stay unenriched until recovered.** While Ollama is slow enough to time out, every object it times out on is skipped and stays unenriched until its next change or a reconcile. That is the best-effort contract Enrichment had before the transient change, now with a per-object Warning and a counter.
 - **A skip is not a lasting fix.** A document that always times out is skipped on every event for it, and is never enriched until its source text changes.
-- **Engagement and Intelligence timeouts are unchanged.** Their HTTP timeouts keep the existing immediate-restart cancellation path: redelivered, with no halt log or counter. "Never block" doesn't apply to them, because skipping would drop vectors or projections, which are not best-effort.
+- **Engagement and Intelligence timeouts are not skipped.** Engagement makes no HTTP calls, and Intelligence's embedding timeouts halt, are counted and back off (`docs/specs/2026-10-01-consumer-timeout-halt-design.md`). "Never block" doesn't apply to them, because skipping would drop vectors or projections, which are not best-effort.
 
 ## Out of scope
 
```

- [ ] **Step 2: Check the md5s.** From the repository root:

```bash
md5sum Iverson.Server/Iverson.Events/MessageDispatcher.cs Iverson.Server/Iverson.Events/KafkaConsumer.cs Iverson.Server/Iverson.Events/Telemetry.cs Iverson.Server/Iverson.Events.Tests/MessageDispatcherTests.cs Iverson.Server/Iverson.Events.Tests/KafkaConsumerTests.cs docs/specs/2026-09-30-transient-projection-failures-design.md docs/specs/2026-10-01-enrichment-timeout-skip-design.md
```

Expected:
```
43b68a2f5828e45a2ca9a60d3eeeb494  Iverson.Server/Iverson.Events/MessageDispatcher.cs
6642ca4c5547255bbe24352ae4e241e8  Iverson.Server/Iverson.Events/KafkaConsumer.cs
a95db44dba29040904f7f483e18b072c  Iverson.Server/Iverson.Events/Telemetry.cs
17e93f2b35299e1426326f9e659c21fb  Iverson.Server/Iverson.Events.Tests/MessageDispatcherTests.cs
c3e8ce6b0b05b34aedc07120b70e5e7a  Iverson.Server/Iverson.Events.Tests/KafkaConsumerTests.cs
e8bd467ea180e4ec410381316be7bc3c  docs/specs/2026-09-30-transient-projection-failures-design.md
8116db668d81cf94f5a327f152bda4a6  docs/specs/2026-10-01-enrichment-timeout-skip-design.md
```

- [ ] **Step 3: Run the tests and build.** From `Iverson.Server/`:

```bash
systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.Events.Tests -m:2
systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.Api.Tests -m:2 --filter "Category!=Integration"
systemd-run --user --scope -q -p MemoryMax=5G dotnet build Iverson.Server.slnx -m:2 --no-incremental
```

Expected:
- The first command reports `Passed: 40`, up from 35.
- The second reports `Passed: 1274`, unchanged.
- The build reports 0 errors and 21 warnings, unchanged.

- [ ] **Step 4: Commit the code and tests.** From the repository root:

```bash
git add Iverson.Server/Iverson.Events/MessageDispatcher.cs Iverson.Server/Iverson.Events/KafkaConsumer.cs Iverson.Server/Iverson.Events/Telemetry.cs Iverson.Server/Iverson.Events.Tests/MessageDispatcherTests.cs Iverson.Server/Iverson.Events.Tests/KafkaConsumerTests.cs
git commit -m "halt for redelivery when a handler is cancelled while the consumer is running" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>" -- Iverson.Server/Iverson.Events/MessageDispatcher.cs Iverson.Server/Iverson.Events/KafkaConsumer.cs Iverson.Server/Iverson.Events/Telemetry.cs Iverson.Server/Iverson.Events.Tests/MessageDispatcherTests.cs Iverson.Server/Iverson.Events.Tests/KafkaConsumerTests.cs
```

- [ ] **Step 5: Commit the spec updates.** From the repository root:

```bash
git add -f docs/specs/2026-09-30-transient-projection-failures-design.md docs/specs/2026-10-01-enrichment-timeout-skip-design.md
git commit -m "point the transient and enrichment specs at the consumer timeout halt" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>" -- docs/specs/2026-09-30-transient-projection-failures-design.md docs/specs/2026-10-01-enrichment-timeout-skip-design.md
```

## Tasks NOT in this plan

- Changing any `HttpClient` timeout, including the embedding client's 100 s default.
- `ConsumeRawAsync` and the DLQ monitor.
- Retrying timeouts in place.

## Known issues inherited from spec

- **A one-off slow request costs a restart.** One embedding call that exceeds 100 s halts the consumer, waits 10 s and rejoins the group, where an in-place retry might have succeeded after 1 s.
- **Three Critical lines per halt.** The dispatcher, `KafkaConsumer` and `ConsumerResilience` each log the halt, as they already do for every other transient halt.
- **A shutdown racing a timeout looks like a shutdown.** If the token is cancelled after the timeout fires but before the dispatcher's filter runs, the exit is quiet. Nothing is committed, so the message is redelivered on the next start.
