# Transient Projection Failures Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-30-transient-projection-failures-design.md` (commit SHA: `b6635473`)

**Goal:** A dependency outage of any length never loses a projection event: the affected consumer halts, Kafka redelivers, and only genuinely bad messages still reach the DLQ.

**Architecture:**
- **One classifier.** A single classifier, `TransientFailures.IsTransient` in `Iverson.Api`, decides what counts as a dependency outage.
- **The dispatcher halts instead of dead-lettering.** `MessageDispatcher` receives the classifier through `AddKafka`. A transient failure that survives the 3 attempts is rethrown, so `KafkaConsumer` halts without committing and `ConsumerResilience` restarts the loop, which redelivers the message.
- **The swallowing consumers stop swallowing outages.** The three consumers that caught every exception now let transient ones through.
- **StarRocks outages become recognisable.** `EngagementRepository.RunAsync` turns an unrecognised StarRocks failure into `EngagementNotReadyException` when a liveness check shows the backend is down.

**Tech stack:** .NET 10; Confluent.Kafka 2.15.1; Npgsql 10.0.3; MySqlConnector 2.4.0; Qdrant.Client 1.18.1 (Grpc.Core.Api); Polly.Core 8.7.0; xunit, NSubstitute, FluentAssertions.

---

## Global Constraints

- **Code comes from the patches.** Every task's code is one `git apply` patch. It was generated from a prototype that compiled and passed, and the patches were verified to apply in order to `main` at `519ae09d`. Never retype a patch: slice it out of the task brief with the given command, and check the listed md5s afterwards.
- **Cap build memory.** An uncapped build was OOM-killed while the docker stack was running, so every `dotnet` command runs as `systemd-run --user --scope -q -p MemoryMax=4G dotnet … -m:2`.
- **Unit tests only.** The filter `--filter "Category!=Integration"` excludes every container-backed test class, all of which carry `[Trait("Category", "Integration")]`. No task needs Docker.
- **Commits.** A lowercase imperative subject, a blank line, then `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>` (use two `-m` flags). Commit explicit paths only, never `-A`.
- **Shell `grep` skips gitignored paths, including `docs/`.** Use `command grep`.

## Plan decisions beyond the spec (made while prototyping)

1. **The dispatcher is a DI singleton.** `AddKafka` registers `MessageDispatcherOptions` (carrying the predicate) and `MessageDispatcher` as singletons, and the `IEventConsumer` factory resolves the dispatcher from DI. Two tests resolve the real registration and prove a transient failure halts with the predicate and dead-letters without it. Without them, a dropped predicate (mutant M2c) went unnoticed, and the feature would have been silently off. The user approved this at plan-write time.
2. **The liveness check ignores the caller's cancellation token** and uses only its own 5 s bound. If it used the caller's token, a cancelled query would be reported as "backend unavailable"; `MatchRowsIntegrationTests:248` cancels at 1 s.
3. **The rewrap decision is a helper that returns a value.** `EngagementRepository.RewrapIfBackendUnavailableAsync` is `internal static` and returns `EngagementNotReadyException?`, where null means rethrow the original. The exemptions live inside it, so tests can show that no liveness check runs for each exempt case. `RunAsync` gains only a `catch (MySqlException)` that calls it.
4. **The `RunAsync` call site is checked by review, not by a test** (user decision). Only a live StarRocks with its backend stopped can exercise it (mutant M3d). The helper itself is fully unit-tested.
5. **`TransientFailures` is `internal static`.** `Iverson.Api`'s `InternalsVisibleTo` already covers `Iverson.Api.Tests`, and `ConsumerResilience` and `ProjectionTenantResolution` follow the same pattern. The four StarRocks rules are checked before the generic `DbException` rule.
6. **The new `KafkaConsumer` halt test throws through a poison message whose DLQ write fails,** not through the transient path. The consumer's no-commit, rethrow behaviour is the same whatever the cause. Using the transient path there would race `MessageDispatcherTests`' process-wide `MeterListener` assertions, because the two classes run in parallel. For the same reason, the `AddKafka` wiring tests live inside `MessageDispatcherTests`.
7. **The bound-observing rewrap test waits on `Task.Delay(30 s, ct)`, not `Timeout.Infinite`.** That way an unbounded mutant fails within 30 s instead of hanging the test run.
8. **The classifier test matrix adds four cases beyond the spec's list:** `RpcException` ResourceExhausted and Aborted, an `AggregateException` whose transient member is second (the only case that proves every member is checked), and an all-permanent aggregate.

## File Structure

- **Create:**
  - `Iverson.Server/Iverson.Api/Consumers/TransientFailures.cs`: the classifier.
  - `Iverson.Server/Iverson.Api.Tests/Consumers/TransientFailuresTests.cs`: the classifier matrix, 38 cases.
  - `Iverson.Server/Iverson.StarRocks.Tests/EngagementRepositoryLivenessRewrapTests.cs`: the rewrap helper, 10 cases.
- **Modify (Task 2, Events):**
  - `Iverson.Events/MessageDispatcher.cs`: the `IsTransient` option, and halting instead of dead-lettering.
  - `Iverson.Events/Telemetry.cs`: the `consumer.transient_halts` counter.
  - `Iverson.Events/ServiceCollectionExtensions.cs`: the `isTransient` parameter, and the DI-registered options and dispatcher.
  - `Iverson.Events/KafkaConsumer.cs`: halt comment and log wording only.
- **Modify (Task 2, API and LoadTest):**
  - `Iverson.Api/Program.cs`: one line, passing the classifier.
  - `Iverson.LoadTest/Scenarios/BenchmarkIngestScenario.cs`: the contract comment only.
- **Modify (Task 3):** `Iverson.StarRocks/EngagementRepository.cs`, the `RunAsync` catch plus the rewrap helper.
- **Modify (Task 4):**
  - `Iverson.Api/Consumers/EnrichmentConsumer.cs`, `PopularitySignalConsumer.cs` (two sites) and `DocumentRerenderConsumer.cs`: one exception filter each.
- **Tests modified:**
  - `Iverson.Events.Tests/MessageDispatcherTests.cs`: 3 transient-path tests and 2 `AddKafka` wiring tests.
  - `Iverson.Events.Tests/KafkaConsumerTests.cs`: 1 halt test.
  - `Iverson.Events.Tests/Iverson.Events.Tests.csproj`: adds `Microsoft.Extensions.DependencyInjection` 10.0.9 for `BuildServiceProvider`.
  - `Iverson.Api.Tests/Consumers/{Enrichment,PopularitySignal,DocumentRerender}ConsumerTests.cs`: 2 tests flipped and 4 added.

All paths below are relative to the repository root. Run commands from `Iverson.Server/` unless a step says otherwise.

## Inherited from spec

These assumptions were verified by `thorough-brainstorming` and by CDR round 1. They are not re-verified here and are trusted as ground truth. The table is copied verbatim from the spec's "Verified assumptions":

| # | Assumption | Evidence |
|---|---|---|
| 1 | Npgsql 10.0.3 classifies `PostgresException` 57P03, 08006, 08001, 53300 and 40001 as transient, and 42P01, 42601, 23505 and 22P02 as not. | Ran `PostgresException.IsTransient` in a scratch console app. |
| 2 | Npgsql and MySqlConnector 2.4.0 connection-refused errors are transient, and reading `IsTransient` through the `DbException` base type returns the library value. | Ran `OpenAsync` against a closed port: `NpgsqlException > SocketException` IsTransient=True; `MySqlException` UnableToConnectToHost IsTransient=True; `(DbException)` cast gives the same values. |
| 3 | Qdrant.Client 1.18.1 throws `RpcException(Unavailable)` when Qdrant is down. | Ran `ListCollectionsAsync` against a closed port: `RpcException > SocketException`, Unavailable. |
| 4 | HTTP connection refused gives `HttpRequestException` with a null `StatusCode`. `EnsureSuccessStatusCode` sets the status (503, 400). An `HttpClient` timeout is `TaskCanceledException > TimeoutException`. | Ran each against a closed port, synthetic responses, and a silent listener. |
| 5 | `EmbeddingService` (`Iverson.Embeddings/EmbeddingService.cs:106-107`) and `EnrichmentService` (`EnrichmentService.cs:64-65`) call `EnsureSuccessStatusCode()` and rethrow unchanged. Neither has a retry policy. Timeouts are the default 100 s and 2 min respectively. | Read (`ServiceCollectionExtensions.cs:12-19`, `:30-38`; `EnrichmentServiceOptions.cs:8`). |
| 6 | A message consumed without commit, with the consumer then disposed, is redelivered to the restarted consumer of the same group, and processing continues in order. | Ran Confluent.Kafka 2.15.1 against the live broker on a throwaway topic (since deleted): consumer 2 got `m1`, then `m2`. |
| 7 | The halt path exists: `KafkaConsumer.cs:65-66` commits only after dispatch; its catch at `:72-81` rethrows without committing; `ConsumerResilience.cs:25-45` restarts after 10 s. All five consumers wrap `ConsumeAsync` in `RunWithRestartAsync`. | Read (Engagement `:21-26`, Intelligence `:51-56`, Enrichment `:59-64`, Popularity `:182-185`, DocumentRerender `:28-33`). |
| 8 | All five handlers are idempotent under re-execution: StarRocks primary-key upsert and delete-by-key, deterministic Qdrant point IDs, `ON CONFLICT` upserts and inserts, and Popularity recomputing an absolute `COUNT`. | Read the write paths (`EngagementRepository.cs:388-391`, `:407`, `:432-433`; `IntelligenceStoreConsumer.cs:650-670`; `EnrichmentStateRepository.cs:31-33`; `DocumentRerenderQueueRepository.cs:40-54`; `PopularitySignalConsumer.cs:105-140`). |
| 9 | Enrichment, Popularity and DocumentRerender swallow dependency outages today, and tests assert it. Engagement and Intelligence propagate them. | Read the catch sites. Tests: `EnrichmentConsumerTests:476`, `PopularitySignalConsumerTests:418`, `DocumentRerenderConsumerTests:554`, `IntelligenceStoreConsumerTests:633` (`EmbedFailure_Propagates`). |
| 10 | Both producers of `EngagementNotReadyException` mean "temporarily unavailable": circuit open (`EngagementRepository.cs:53`) and readiness gate timeout (`StarRocksReadinessGate.cs:62`). | `command grep -rn "new EngagementNotReadyException"`: two production sites. |
| 11 | `Iverson.Events` references only Confluent.Kafka and Microsoft.Extensions. `Iverson.Api` references Iverson.Sql, StarRocks, Vector and Embeddings plus Grpc.AspNetCore. There is one production `new MessageDispatcher` (`ServiceCollectionExtensions.cs:47`), reached through `AddKafka` from `Program.cs:340`. | Read both csproj files; grep. |
| 12 | Adding a defaulted `IsTransient` to `MessageDispatcherOptions` breaks no caller. The only other construction is `MessageDispatcherTests.cs:31`, using an object initializer. | grep. |
| 13 | Of the other dependents, the DLQ monitor, backlog gauge and admin-console metrics only count what reaches the DLQ; none assumes transient failures land there. `BenchmarkIngestScenario.cs:64-67` documents the old contract (updated in §4). | Read each site. |
| 14 | The Popularity updater's `Failed` outcome is read only by `PopularitySignalReconciliationWorker` (`:72-96`), which catches exceptions from `UpdateAsync` with `outcome` seeded to `Failed`. The sweep tests inject non-transient exceptions. | Read the worker and `PopularitySignalReconciliationWorkerTests.cs`. |
| 15 | Enrichment writes nothing before its dependency calls. DocumentRerender's only pre-failure write is an idempotent enqueue. | Read `EnrichmentConsumer.cs` (first write after generation, `:154`) and `DocumentRerenderConsumer.cs:167-189`. |
| 16 | MySqlConnector 2.4.0 returns `IsTransient == true` for exactly five codes: 1040, 1042, 1205, 1213 and 1614. The circuit breaker uses the same predicate (`StarRocksResiliencePipelineFactory.cs:19`). The cold-start gate latches after its first success (`StarRocksReadinessGate.cs:15,50`). | CDR round 1: `IsTransient` run over every `MySqlErrorCode` member; the factory and gate were read. |
| 17 | StarRocks outage shapes as observed. Frozen server: `CommandTimeoutExpired(-1)` with an inner `SocketException`. Statement outliving its command timeout: `ParseError(1064)`, "Getting syntax error at line 1, column 21. Detail message: Unexpected input '@', the most similar input is {'OUTFILE'}." FE dropped mid-query: `None(0)` with an inner `MySqlEndOfStreamException`. All are `IsTransient=False`. `MySqlEndOfStreamException` is public. | CDR round 1 probes (`probe3`, `probe5`). The `SELECT SLEEP(5)` probe with `CommandTimeout=1` was re-run on 2026-09-30 and gave the quoted 1064. A reflection check found `MySqlEndOfStreamException` public and `CommandTimeoutExpired` = -1. |
| 18 | Backend-not-alive windows happen mid-life on this stack: on 2026-09-30 at 06:40, 09:14–09:23 and 11:15, per `fe.log`. The `IsExpectedMissingResourceError` catches (`EngagementRepository.cs:503,544,604,636`) wrap `RunTenantScopedAsync`, which wraps `RunAsync` (`:164-181`). **UNVERIFIED:** the client-side message for "Backend node not found"; the §5 rewrap covers that shape regardless. | FE log read in-container (CDR round 1). `EngagementRepository.cs` read. |
| 19 | Postgres outage shapes beyond a refused connection or 57P03 classify as transient: 57P01 and 57P02 (crash-terminated sessions), IO and EndOfStream inner exceptions (mid-stream drop), and a frozen server (`NpgsqlException > TimeoutException`). | CDR round 1 probe run over those shapes (`probe4`). |
| 20 | The dispatcher classifies only the exception from the last attempt, so a non-transient first-attempt shape does not decide the outcome there. | Read `MessageDispatcher.cs:65-78`: the attempt check is the only place a classifier call can sit. |
| 21 | At the four in-handler filter sites, the first exception decides. For Postgres (row 19), Qdrant (a mid-call drop gives `RpcException Unavailable`) and HTTP (a server closing mid-request gives `HttpRequestException ResponseEnded` with a null status), that first exception already classifies as transient. StarRocks' first shapes are covered by rows 16–17, §1 and §5. | CDR round 1 probe (`probe7`). |

## Verified plan-level assumptions

All code was prototyped on the throwaway branch `tpf-proto` (`.worktrees/tpf-proto`, five commits on `519ae09d`). The four patches below are diffs of that branch. They were verified to apply in order to a clean `519ae09d` checkout and to reproduce every listed md5.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | Paths | `TransientFailures.cs`, `TransientFailuresTests.cs` and `EngagementRepositoryLivenessRewrapTests.cs` do not exist yet. Every modified file exists at its path. | `ls` at `519ae09d`. All 12 modified paths exist. |
| 2 | Signature | `Iverson.Api.csproj` grants `InternalsVisibleTo` to `Iverson.Api.Tests`, so `internal static TransientFailures` is visible to its tests, to `Program.cs` and to the consumers. | `Iverson.Api.csproj:8-15`. |
| 3 | Signature | `EngagementNotReadyException(string, Exception?)` is public. `IsExpectedMissingResourceError(Exception)` is private static in `EngagementRepository` (`:147`). `CheckBackendAliveAsync(string, CancellationToken)` is at `:667`. | Read. |
| 4 | Signature | `MySqlException` and `MySqlEndOfStreamException` have only internal constructors. Tests build them by reflection, the convention of `StarRocksResiliencePipelineFactoryTests.cs:21-30` and `StarRocksHealthCheckerTests.cs:8-12`. The `(MySqlErrorCode, string, Exception)` and `(int, int)` overloads exist. `PostgresException`, `NpgsqlException` and `RpcException` have public constructors. | Reflection probe over the constructors. |
| 5 | Signature | `KafkaConsumer`'s injected consumer factory lets a test make dispatch throw and assert `Commit` is never received. `MessageDispatcherTests` observes counters with a `MeterListener`. | Read `KafkaConsumerTests.cs:25-38` and `MessageDispatcherTests.cs` (Metrics test). The prototype tests pass. |
| 6 | Signature | `Iverson.Events.Tests` lacks `BuildServiceProvider` until `Microsoft.Extensions.DependencyInjection` is added, at the version `Iverson.StarRocks.Tests.csproj:17` uses (10.0.9). | Compile error CS1061 before the reference, green after. |
| 7 | Command | `dotnet test <project> --filter "Category!=Integration"` runs every unit test and no container test. Every container-backed class carries `[Trait("Category", "Integration")]` (8 in Api.Tests, 4 in StarRocks.Tests), and Events.Tests has none. | `command grep -rn 'Trait("Category"'` cross-checked against `command grep -rlE "Testcontainers\|ContainerBuilder\|PostgreSqlBuilder\|KafkaBuilder\|QdrantBuilder"`. |
| 8 | Command | Unit counts, base → after each task, all passing: Events 29 → 35 (Task 2); StarRocks 430 → 440 (Task 3); Api 1221 → 1259 (Task 1) → 1263 (Task 4). | Measured on the prototype. The tip counts were re-run independently: 35, 440, 1263. |
| 9 | Command | `dotnet build Iverson.Server.slnx` has 0 errors and the same 21 warnings before and after: 19 CS0618 (Testcontainers `ContainerBuilder()`) and 2 CS8604 (`StarRocksQueryBuilder.cs:379,409`). | Base and tip warning lists, sorted and path-normalised, diff as identical. |
| 10 | Ordering | Task 1 needs nothing. Task 2 needs Task 1 (`Program.cs` names `TransientFailures`). Task 3 needs nothing. Task 4 needs Task 1. Each patch touches files no other patch touches. | `+++ b/` path lists of the four patches are disjoint. They were applied in order to `519ae09d` without conflict. |
| 11 | Code validity | All four patches compile and their tests pass against the library versions above. | Prototype runs per task; tip re-run. |
| 12 | Code validity | The `when` filters compose with the existing catches without changing their bodies. Popularity `:86` keeps `ex is not OperationCanceledException`. | Patch 4 changes only the `catch` lines; the bodies are unchanged. |
| 13 | Consumer impact | `AddKafka` has no caller other than `Program.cs:340`. The new parameter is last and optional. Registering `MessageDispatcher` and its options as DI singletons changes no other resolution, and the Api unit suite still passes 1263/1263. | `command grep -rn "AddKafka("` over the repo (bin and obj excluded). |
| 14 | Consumer impact | No test, alert or dashboard asserts the old halt wording ("The DLQ write itself failed", "Halting topic"). `dlq_routed` readers (admin console endpoint, series-name test, AdminUI widget) count DLQ'd events only. | `command grep -rn "DLQ write itself\|offset not committed\|Halting topic"`: no test hits. |
| 15 | Consumer impact | The rewrap changes no existing test's expectation:<br>• Unit tests don't reach the new catch: the port-1 test cancels at the readiness gate, the search test throws before I/O, and the factory tests bypass `RunAsync`.<br>• `MatchRowsIntegrationTests:248`: the check runs, the backend is alive, and the original is rethrown under `ThrowAsync<Exception>`.<br>• `MatchRowsIntegrationTests:301`: same, under `ThrowAsync<MySqlException>`, while the backend is alive.<br>The other integration assertions involve no non-transient, non-missing-resource `MySqlException`. | Code read of each path. All 430 existing StarRocks unit tests still pass. Docker tests not run. |
| 16 | Consumer impact | In the three consumer test files, only two tests inject a transient-classified exception and assert `NotThrow`, and both are flipped. `UpdateAsync_SetPayloadThrowsNonNotFound_Propagates` already asserts propagation and is unchanged. The reconciliation-worker tests inject only non-transient exceptions. | `command grep` over the exception types in the three test files and every `*Tests.cs` that references `PopularitySignalReconciliationWorker`. |
| 17 | Convention | The commit style is a lowercase imperative subject plus the trailer. | `git log --format=%s -8`. |
| 18 | Mutation | 17 of 19 mutants are killed (table below). M3d survives, by user decision (decision 4). The original M2c survivor is now killed (decision 1). | Each mutant was applied, the project's unit suite run, and the change reverted. |

**Mutants:**
- **Classifier:**
  - M1a: drop the `Unexpected input '@'` rule. Killed by `IsTransient_DependencyOutageShape_ReturnsTrue("MySql ParseError Unexpected input '@'")`.
  - M1b: check only an aggregate's first member. Killed by `…("Aggregate, transient second member")`.
  - M1c: drop the HTTP 5xx rule. Killed by `…("Http 503")`.
- **Dispatcher:**
  - M2a: invert the branch. Killed by `TransientFailure_ExhaustsAttempts_WithPredicate_RethrowsOriginal_NoDlq_CountsHalt` and `NonTransientFailure_WithPredicate_…StillRoutesToDlq`.
  - M2b: drop the halt counter. Killed by the same `TransientFailure_…_CountsHalt` test.
  - M2c: `AddKafka` ignores `isTransient`. Killed by `AddKafka_WithPredicate_DispatcherHaltsOnTransientFailure_InsteadOfDeadLettering`.
  - M2d: commit before rethrowing, and M2e: swallow the halt. Both killed by `ConsumeAsync_DispatchThrows_DoesNotCommit_AndRethrows`.
- **Rewrap helper:**
  - M3a: no missing-resource exemption. Killed by `Exempt_IsNotRewrapped_AndMakesNoLivenessCheck` ×3.
  - M3b: a check that throws or times out counts as alive. Killed by `NonTransient_CheckThrows_…` and `NonTransient_CheckExceedsBound_…`.
  - M3c: no bound. Killed by `NonTransient_CheckExceedsBound_…` and `NonTransient_CheckObservesTokenAtBound_…`.
  - M3d: `RunAsync` never calls the helper. **Survives**; review-checked.
- **Consumer filters:**
  - M4a: no Enrichment filter. Killed by `HandleUpdated_WhenLlmUnreachable_PropagatesForRedeliveryAndWritesNothing`.
  - M4b: Enrichment filter inverted. Killed by `HandleUpdated_WhenLlmRejectsRequest_…DoesNotThrow`.
  - M4c: no Popularity per-relation filter. Killed by `Dispatch_SetPayloadAsyncThrowsUnavailable_PropagatesForRedelivery`.
  - M4d: no Popularity aggregate filter. Killed by `UpdateAsync_AggregateThrowsTransient_Propagates`.
  - M4e: no DocumentRerender filter, and M4f: DocumentRerender filter inverted. Both killed by `Dispatch_DependentReadHitsTransientPostgresFailure_PropagatesForRedelivery`; M4f also by `Dispatch_OneDependentThrows_OtherDependentsAreStillEnqueued`.

## Tasks

### Task 1: The transient-failure classifier

**Files:**
- Create: `Iverson.Server/Iverson.Api/Consumers/TransientFailures.cs`
- Create: `Iverson.Server/Iverson.Api.Tests/Consumers/TransientFailuresTests.cs`

**Interfaces:**
- Produces, for Tasks 2 and 4: `internal static class Iverson.Api.Consumers.TransientFailures` with `public static bool IsTransient(Exception ex)`.

- [ ] **Step 1: Apply the patch.** `BRIEF` is the path of this task brief. The command slices the brief's only `diff` block byte for byte; never retype it. From the repository root:

```bash
python3 - "$BRIEF" > /tmp/claude-1000/tpf-task1.patch <<'PY'
import re, sys
fence = chr(96) * 3
s = open(sys.argv[1]).read()
print(re.search(fence + "diff\n(.*?)" + fence, s, re.S).group(1), end="")
PY
git apply --check /tmp/claude-1000/tpf-task1.patch && git apply /tmp/claude-1000/tpf-task1.patch
```

If `--check` fails, stop and report. Do not hand-edit. The patch:

```diff
diff --git a/Iverson.Server/Iverson.Api.Tests/Consumers/TransientFailuresTests.cs b/Iverson.Server/Iverson.Api.Tests/Consumers/TransientFailuresTests.cs
new file mode 100644
index 00000000..3d5cb895
--- /dev/null
+++ b/Iverson.Server/Iverson.Api.Tests/Consumers/TransientFailuresTests.cs
@@ -0,0 +1,110 @@
+using System.Net;
+using System.Net.Sockets;
+using System.Reflection;
+using System.Text.Json;
+using FluentAssertions;
+using Grpc.Core;
+using Iverson.Api.Consumers;
+using Iverson.Events;
+using Iverson.StarRocks;
+using MySqlConnector;
+using Npgsql;
+using Xunit;
+
+namespace Iverson.Api.Tests.Consumers;
+
+public sealed class TransientFailuresTests
+{
+    // MySqlException's and MySqlEndOfStreamException's constructors are internal to MySqlConnector
+    // with no InternalsVisibleTo grant, so instances are built via reflection against the exact
+    // overload (the StarRocksResiliencePipelineFactoryTests convention), naming every parameter
+    // type to avoid AmbiguousMatchException against sibling overloads.
+    private static MySqlException MySql(MySqlErrorCode errorCode, string message, Exception? inner = null) =>
+        (MySqlException)typeof(MySqlException)
+            .GetConstructor(
+                BindingFlags.NonPublic | BindingFlags.Instance,
+                binder: null,
+                types: [typeof(MySqlErrorCode), typeof(string), typeof(Exception)],
+                modifiers: null)!
+            .Invoke([errorCode, message, inner]);
+
+    private static MySqlEndOfStreamException EndOfStream() =>
+        (MySqlEndOfStreamException)typeof(MySqlEndOfStreamException)
+            .GetConstructor(
+                BindingFlags.NonPublic | BindingFlags.Instance,
+                binder: null,
+                types: [typeof(int), typeof(int)],
+                modifiers: null)!
+            .Invoke([4, 0]);
+
+    private static PostgresException Pg(string sqlState) =>
+        new("synthetic", "ERROR", "ERROR", sqlState);
+
+    private static HttpRequestException Http(HttpStatusCode? status) =>
+        new("synthetic", inner: null, statusCode: status);
+
+    private static RpcException Rpc(StatusCode code) => new(new Status(code, "synthetic"));
+
+    // Keyed by a readable name so each case is its own discovered test (Exception is not
+    // xunit-serializable, so passing instances through MemberData would fold them into one).
+    private static readonly Dictionary<string, Func<Exception>> Transient = new()
+    {
+        ["Postgres 57P03 recovery mode"]           = () => Pg("57P03"),
+        ["Postgres 08006 connection failure"]      = () => Pg("08006"),
+        ["Postgres 53300 too many connections"]    = () => Pg("53300"),
+        ["Postgres 40001 serialization failure"]   = () => Pg("40001"),
+        ["Npgsql wrapping SocketException"]        = () => new NpgsqlException("refused", new SocketException((int)SocketError.ConnectionRefused)),
+        ["MySql UnableToConnectToHost"]            = () => MySql(MySqlErrorCode.UnableToConnectToHost, "Unable to connect to any of the specified MySQL hosts."),
+        ["EngagementNotReadyException"]            = () => new EngagementNotReadyException("circuit open"),
+        ["Rpc Unavailable"]                        = () => Rpc(StatusCode.Unavailable),
+        ["Rpc DeadlineExceeded"]                   = () => Rpc(StatusCode.DeadlineExceeded),
+        ["Rpc ResourceExhausted"]                  = () => Rpc(StatusCode.ResourceExhausted),
+        ["Rpc Aborted"]                            = () => Rpc(StatusCode.Aborted),
+        ["Http null status (connection failure)"]  = () => Http(null),
+        ["Http 503"]                               = () => Http(HttpStatusCode.ServiceUnavailable),
+        ["Http 429"]                               = () => Http(HttpStatusCode.TooManyRequests),
+        ["TaskCanceled wrapping TimeoutException"] = () => new TaskCanceledException("timed out", new TimeoutException()),
+        ["InvalidOperation wrapping transient"]    = () => new InvalidOperationException("outer", Rpc(StatusCode.Unavailable)),
+        ["Aggregate, transient second member"]     = () => new AggregateException(new InvalidOperationException("permanent"), Pg("57P03")),
+
+        // StarRocks shapes MySqlConnector's own IsTransient does not cover (spec §1).
+        ["MySql CommandTimeoutExpired"]            = () => MySql(MySqlErrorCode.CommandTimeoutExpired, "The Command Timeout expired before the operation completed.", new SocketException((int)SocketError.TimedOut)),
+        ["MySql Backend node not found"]           = () => MySql(MySqlErrorCode.ParseError, "Backend node not found. Check if any backend node is down."),
+        ["MySql wrapping MySqlEndOfStream"]        = () => MySql(MySqlErrorCode.None, "Failed to read the result set.", EndOfStream()),
+        ["MySql ParseError Unexpected input '@'"]  = () => MySql(MySqlErrorCode.ParseError, "Getting syntax error at line 1, column 21. Detail message: Unexpected input '@', the most similar input is {'OUTFILE'}."),
+    };
+
+    private static readonly Dictionary<string, Func<Exception>> NotTransient = new()
+    {
+        ["MySql ParseError cannot find role"]       = () => MySql(MySqlErrorCode.ParseError, "cannot find role t_abc"),
+        ["MySql ParseError is not granted to"]      = () => MySql(MySqlErrorCode.ParseError, "Role t_abc is not granted to 'iverson'@'%'"),
+        ["MySql ParseError ordinary syntax"]        = () => MySql(MySqlErrorCode.ParseError, "Getting syntax error at line 1, column 7. Detail message: Unexpected input 'FORM'."),
+        ["MySql 5502 unknown table"]                = () => MySql((MySqlErrorCode)5502, "Unknown table 'iverson.articles'"),
+        ["Postgres 42P01 undefined table"]          = () => Pg("42P01"),
+        ["Postgres 42601 syntax error"]             = () => Pg("42601"),
+        ["Postgres 23505 unique violation"]         = () => Pg("23505"),
+        ["Postgres 22P02 invalid text"]             = () => Pg("22P02"),
+        ["Rpc InvalidArgument"]                     = () => Rpc(StatusCode.InvalidArgument),
+        ["Rpc NotFound"]                            = () => Rpc(StatusCode.NotFound),
+        ["Http 400"]                                = () => Http(HttpStatusCode.BadRequest),
+        ["Http 413"]                                = () => Http(HttpStatusCode.RequestEntityTooLarge),
+        ["PoisonMessageException"]                  = () => new PoisonMessageException("bad json"),
+        ["InvalidOperationException"]               = () => new InvalidOperationException("boom"),
+        ["JsonException"]                           = () => new JsonException("bad output"),
+        ["TaskCanceled without TimeoutException"]   = () => new TaskCanceledException("cancelled"),
+        ["Aggregate, every member permanent"]       = () => new AggregateException(new InvalidOperationException("a"), Pg("23505")),
+    };
+
+    public static TheoryData<string> TransientCases() => new(Transient.Keys);
+    public static TheoryData<string> NotTransientCases() => new(NotTransient.Keys);
+
+    [Theory]
+    [MemberData(nameof(TransientCases))]
+    public void IsTransient_DependencyOutageShape_ReturnsTrue(string name) =>
+        TransientFailures.IsTransient(Transient[name]()).Should().BeTrue(name);
+
+    [Theory]
+    [MemberData(nameof(NotTransientCases))]
+    public void IsTransient_BadMessageShape_ReturnsFalse(string name) =>
+        TransientFailures.IsTransient(NotTransient[name]()).Should().BeFalse(name);
+}
diff --git a/Iverson.Server/Iverson.Api/Consumers/TransientFailures.cs b/Iverson.Server/Iverson.Api/Consumers/TransientFailures.cs
new file mode 100644
index 00000000..9b812b87
--- /dev/null
+++ b/Iverson.Server/Iverson.Api/Consumers/TransientFailures.cs
@@ -0,0 +1,66 @@
+using System.Data.Common;
+using System.Net;
+using Grpc.Core;
+using Iverson.StarRocks;
+using MySqlConnector;
+
+namespace Iverson.Api.Consumers;
+
+/// <summary>
+/// The one place that decides whether a projection failure is a dependency outage (wait and
+/// redeliver) or a genuinely bad message (dead-letter). It lives in <c>Iverson.Api</c> because
+/// this is the composition root that references every client library; <c>Iverson.Events</c>
+/// receives it only as a <c>Func&lt;Exception, bool&gt;</c>.
+///
+/// The whole <see cref="Exception.InnerException"/> chain is walked, and every member of an
+/// <see cref="AggregateException"/> is checked: a failure is transient when ANY link is.
+///
+/// Misclassification costs in both directions. A transient failure classified as permanent is
+/// dead-lettered; a permanent failure classified as transient stalls its consumer until someone
+/// intervenes. <c>TransientFailuresTests</c> pins both directions.
+/// </summary>
+internal static class TransientFailures
+{
+    public static bool IsTransient(Exception ex) => ex switch
+    {
+        AggregateException agg => agg.InnerExceptions.Any(IsTransient),
+        _ => IsTransientLink(ex) || (ex.InnerException is { } inner && IsTransient(inner)),
+    };
+
+    private static bool IsTransientLink(Exception ex) => ex switch
+    {
+        // StarRocks shapes MySqlConnector's IsTransient does not cover (it marks only 1040, 1042,
+        // 1205, 1213 and 1614). Checked before the DbException rule, which they would otherwise
+        // fall through as non-transient.
+        MySqlException { ErrorCode: MySqlErrorCode.CommandTimeoutExpired } => true,      // frozen server
+        MySqlException my when my.Message.Contains("Backend node not found",
+                                                    StringComparison.Ordinal) => true,  // BE down, FE up
+        MySqlException { InnerException: MySqlEndOfStreamException } => true,           // FE died mid-query
+        MySqlException { ErrorCode: MySqlErrorCode.ParseError } my
+            when my.Message.Contains("Unexpected input '@'", StringComparison.Ordinal) => true,
+                                                    // statement outlived its command timeout: StarRocks
+                                                    // rejects MySqlConnector's cancellation cleanup
+
+        // Postgres (Npgsql) and StarRocks (MySqlConnector) both override DbException.IsTransient.
+        DbException db => db.IsTransient,
+
+        // StarRocks circuit open, or the readiness gate timed out.
+        EngagementNotReadyException => true,
+
+        // Qdrant.
+        RpcException rpc => rpc.StatusCode is StatusCode.Unavailable
+                                           or StatusCode.DeadlineExceeded
+                                           or StatusCode.ResourceExhausted
+                                           or StatusCode.Aborted,
+
+        // TEI / Ollama via EnsureSuccessStatusCode(); a null status is a connection failure.
+        HttpRequestException http => http.StatusCode is null
+                                     || (int)http.StatusCode.Value >= 500
+                                     || http.StatusCode == HttpStatusCode.TooManyRequests,
+
+        // An HttpClient timeout surfaces as TaskCanceledException wrapping TimeoutException.
+        TimeoutException => true,
+
+        _ => false,
+    };
+}
```

- [ ] **Step 2: Check the md5s.** From the repository root:

```bash
md5sum Iverson.Server/Iverson.Api.Tests/Consumers/TransientFailuresTests.cs Iverson.Server/Iverson.Api/Consumers/TransientFailures.cs
```

Expected:
```
e873401f32004e0c421931469a09e395  Iverson.Server/Iverson.Api.Tests/Consumers/TransientFailuresTests.cs
5527ff4449a920c9857d9aedb68fe798  Iverson.Server/Iverson.Api/Consumers/TransientFailures.cs
```

- [ ] **Step 3: Run the tests.** From `Iverson.Server/`:

```bash
systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.Api.Tests -m:2 --filter "FullyQualifiedName~Iverson.Api.Tests.Consumers.TransientFailuresTests"
systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.Api.Tests -m:2 --filter "Category!=Integration"
```

Expected: `Passed: 38` for the first command (21 transient and 17 not-transient cases), then `Passed: 1259`, up from 1221.

- [ ] **Step 4: Commit.** From the repository root:

```bash
git add Iverson.Server/Iverson.Api/Consumers/TransientFailures.cs Iverson.Server/Iverson.Api.Tests/Consumers/TransientFailuresTests.cs
git commit -m "add the transient failure classifier for projection consumers" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 2: Halt for redelivery instead of dead-lettering

**Files:**
- Modify: `Iverson.Server/Iverson.Events/MessageDispatcher.cs`, `Telemetry.cs`, `ServiceCollectionExtensions.cs` and `KafkaConsumer.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs` (`:340`) and `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkIngestScenario.cs` (`:64-67`, comment only)
- Test: `Iverson.Server/Iverson.Events.Tests/MessageDispatcherTests.cs`, `KafkaConsumerTests.cs` and `Iverson.Events.Tests.csproj`

**Interfaces:**
- Consumes `TransientFailures.IsTransient` from Task 1, in `Program.cs` only.
- Produces `MessageDispatcherOptions.IsTransient`, `AddKafka(…, Func<Exception, bool>? isTransient = null)`, and DI-registered `MessageDispatcherOptions` and `MessageDispatcher` singletons.

**Review focus:**
- **Program.cs:** the one line that passes `TransientFailures.IsTransient` into `AddKafka`. It is not covered by a test.
- **AddKafka:** the `IEventConsumer` factory must resolve `MessageDispatcher` from DI rather than build its own. The wiring tests resolve the dispatcher, not the consumer.

- [ ] **Step 1: Apply the patch.** `BRIEF` is the path of this task brief. The command slices the brief's only `diff` block byte for byte; never retype it. From the repository root:

```bash
python3 - "$BRIEF" > /tmp/claude-1000/tpf-task2.patch <<'PY'
import re, sys
fence = chr(96) * 3
s = open(sys.argv[1]).read()
print(re.search(fence + "diff\n(.*?)" + fence, s, re.S).group(1), end="")
PY
git apply --check /tmp/claude-1000/tpf-task2.patch && git apply /tmp/claude-1000/tpf-task2.patch
```

If `--check` fails, stop and report. Do not hand-edit. The patch:

```diff
diff --git a/Iverson.Server/Iverson.Api/Program.cs b/Iverson.Server/Iverson.Api/Program.cs
index a97f96a7..f3a8e08a 100644
--- a/Iverson.Server/Iverson.Api/Program.cs
+++ b/Iverson.Server/Iverson.Api/Program.cs
@@ -337,7 +337,7 @@ builder.Services.AddVectorRanking(cfg);
 builder.Services.AddDecayOptions(cfg);
 builder.Services.AddPopularitySignalOptions(cfg);
 
-builder.Services.AddKafka(cfg);
+builder.Services.AddKafka(cfg, isTransient: TransientFailures.IsTransient);
 
 builder.Services.AddSingleton<SchemaRegistry>();
 builder.Services.AddSingleton<DocumentRenderer>();
diff --git a/Iverson.Server/Iverson.Events.Tests/Iverson.Events.Tests.csproj b/Iverson.Server/Iverson.Events.Tests/Iverson.Events.Tests.csproj
index b4b368e0..bca908bc 100644
--- a/Iverson.Server/Iverson.Events.Tests/Iverson.Events.Tests.csproj
+++ b/Iverson.Server/Iverson.Events.Tests/Iverson.Events.Tests.csproj
@@ -20,6 +20,7 @@
     </PackageReference>
     <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.9" />
     <PackageReference Include="Confluent.Kafka" Version="2.15.1" />
+    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.9" />
   </ItemGroup>
   <ItemGroup>
     <ProjectReference Include="../Iverson.Events/Iverson.Events.csproj" />
diff --git a/Iverson.Server/Iverson.Events.Tests/KafkaConsumerTests.cs b/Iverson.Server/Iverson.Events.Tests/KafkaConsumerTests.cs
index c3996a6c..9d888017 100644
--- a/Iverson.Server/Iverson.Events.Tests/KafkaConsumerTests.cs
+++ b/Iverson.Server/Iverson.Events.Tests/KafkaConsumerTests.cs
@@ -4,6 +4,7 @@ using FluentAssertions;
 using Iverson.Events;
 using Microsoft.Extensions.Logging.Abstractions;
 using NSubstitute;
+using NSubstitute.ExceptionExtensions;
 using Xunit;
 
 namespace Iverson.Events.Tests;
@@ -76,4 +77,47 @@ public sealed class KafkaConsumerTests
 
         fakeConsumer.Received(1).Subscribe("topic");
     }
+
+    [Fact]
+    public async Task ConsumeAsync_DispatchThrows_DoesNotCommit_AndRethrows()
+    {
+        // The halt path: a message whose dispatch throws must never have its offset committed,
+        // and ConsumeAsync must rethrow so ConsumerResilience restarts the loop and the broker
+        // redelivers from the last committed offset. Dispatch is made to throw by a poison message
+        // whose DLQ write fails — that path increments none of the dispatcher's counters, so this
+        // test cannot perturb MessageDispatcherTests' MeterListener assertions running in parallel.
+        var result = new ConsumeResult<string, string>
+        {
+            Message              = new Message<string, string> { Key = "key-1", Value = "{}", Headers = new Headers() },
+            TopicPartitionOffset = new TopicPartitionOffset("topic", new Partition(0), new Offset(7)),
+        };
+        var fakeConsumer = Substitute.For<IConsumer<string, string>>();
+        fakeConsumer.Consume(Arg.Any<CancellationToken>()).Returns(result);
+
+        var fakeAdmin = Substitute.For<IAdminClient>();
+        fakeAdmin.CreateTopicsAsync(Arg.Any<IEnumerable<TopicSpecification>>()).Returns(Task.CompletedTask);
+
+        var producer = Substitute.For<IProducer<string, string>>();
+        producer
+            .ProduceAsync(Arg.Any<string>(), Arg.Any<Message<string, string>>(), Arg.Any<CancellationToken>())
+            .ThrowsAsync(new KafkaException(ErrorCode.Local_Transport));
+        var dispatcher = new MessageDispatcher(producer, NullLogger<MessageDispatcher>.Instance);
+
+        var consumer = new KafkaConsumer(
+            new KafkaOptions { BootstrapServers = "localhost:9092" },
+            NullLogger<KafkaConsumer>.Instance,
+            dispatcher,
+            _ => fakeConsumer,
+            _ => fakeAdmin);
+
+        var act = () => consumer.ConsumeAsync(
+            "topic",
+            "group",
+            (_, _, _) => throw new PoisonMessageException("bad json"),
+            CancellationToken.None);
+
+        await act.Should().ThrowAsync<KafkaException>();
+        fakeConsumer.DidNotReceive().Commit(Arg.Any<ConsumeResult<string, string>>());
+        fakeConsumer.Received(1).Consume(Arg.Any<CancellationToken>());
+    }
 }
diff --git a/Iverson.Server/Iverson.Events.Tests/MessageDispatcherTests.cs b/Iverson.Server/Iverson.Events.Tests/MessageDispatcherTests.cs
index 7c506089..30a248cf 100644
--- a/Iverson.Server/Iverson.Events.Tests/MessageDispatcherTests.cs
+++ b/Iverson.Server/Iverson.Events.Tests/MessageDispatcherTests.cs
@@ -3,6 +3,9 @@ using System.Text;
 using Confluent.Kafka;
 using FluentAssertions;
 using Iverson.Events;
+using Microsoft.Extensions.Configuration;
+using Microsoft.Extensions.DependencyInjection;
+using Microsoft.Extensions.Logging;
 using Microsoft.Extensions.Logging.Abstractions;
 using NSubstitute;
 using NSubstitute.ExceptionExtensions;
@@ -30,6 +33,20 @@ public sealed class MessageDispatcherTests
             NullLogger<MessageDispatcher>.Instance,
             new MessageDispatcherOptions { MaxAttempts = maxAttempts, Backoff = _ => TimeSpan.Zero });
 
+    private MessageDispatcher BuildSut(Func<Exception, bool> isTransient, int maxAttempts = 3) =>
+        new(
+            _producer,
+            NullLogger<MessageDispatcher>.Instance,
+            new MessageDispatcherOptions
+            {
+                MaxAttempts = maxAttempts,
+                Backoff     = _ => TimeSpan.Zero,
+                IsTransient = isTransient,
+            });
+
+    /// <summary>The predicate the transient-path tests inject: only a TimeoutException is transient.</summary>
+    private static bool OnlyTimeoutsAreTransient(Exception ex) => ex is TimeoutException;
+
     private static DispatchContext Ctx(string value = """{"ok":true}""") =>
         new(
             "iverson.entity.created",
@@ -188,4 +205,121 @@ public sealed class MessageDispatcherTests
         measurements.GetValueOrDefault("consumer.retries").Should().Be(2);
         measurements.GetValueOrDefault("consumer.dlq_routed").Should().Be(1);
     }
+
+    [Fact]
+    public async Task TransientFailure_ExhaustsAttempts_WithPredicate_RethrowsOriginal_NoDlq_CountsHalt()
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
+        var outage = new TimeoutException("dependency down");
+        var calls = 0;
+        Task Handler(string k, string v, CancellationToken c) { calls++; throw outage; }
+
+        var act = async () => await BuildSut(OnlyTimeoutsAreTransient, maxAttempts: 3)
+            .DispatchAsync(Ctx(), Handler, CancellationToken.None);
+
+        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(outage);
+        listener.Dispose();
+
+        calls.Should().Be(3);
+        await _producer.DidNotReceive()
+            .ProduceAsync(
+                Arg.Any<string>(),
+                Arg.Any<Message<string, string>>(),
+                Arg.Any<CancellationToken>());
+        measurements.GetValueOrDefault("consumer.transient_halts").Should().Be(1);
+        measurements.GetValueOrDefault("consumer.dlq_routed").Should().Be(0);
+    }
+
+    [Fact]
+    public async Task TransientFailure_WithPredicate_RecoversOnSecondAttempt_DoesNotThrow_NoDlq()
+    {
+        var calls = 0;
+        Task Handler(string k, string v, CancellationToken c)
+        {
+            calls++;
+            if (calls == 1) throw new TimeoutException("blip");
+            return Task.CompletedTask;
+        }
+
+        var act = async () => await BuildSut(OnlyTimeoutsAreTransient)
+            .DispatchAsync(Ctx(), Handler, CancellationToken.None);
+
+        await act.Should().NotThrowAsync();
+        calls.Should().Be(2);
+        await _producer.DidNotReceive()
+            .ProduceAsync(
+                Arg.Any<string>(),
+                Arg.Any<Message<string, string>>(),
+                Arg.Any<CancellationToken>());
+    }
+
+    [Fact]
+    public async Task NonTransientFailure_WithPredicate_ExhaustsAttempts_StillRoutesToDlqAndReturns()
+    {
+        var calls = 0;
+        Task Handler(string k, string v, CancellationToken c) { calls++; throw new InvalidOperationException("bad message"); }
+
+        var act = async () => await BuildSut(OnlyTimeoutsAreTransient, maxAttempts: 3)
+            .DispatchAsync(Ctx(), Handler, CancellationToken.None);
+
+        await act.Should().NotThrowAsync();
+        calls.Should().Be(3);
+        await _producer.Received(1)
+            .ProduceAsync(
+                EntityTopics.Dlq,
+                Arg.Is<Message<string, string>>(m => m.Key == "key-1"),
+                Arg.Any<CancellationToken>());
+    }
+
+    // ── AddKafka wiring ─────────────────────────────────────────────────────────
+    // AddKafka is the only path by which the API's transient classifier reaches the dispatcher the
+    // consumers run under: if it dropped the predicate, every transient dependency failure would
+    // dead-letter again and every test above would still pass. These resolve the real registration
+    // (production backoff, so each takes ~3 s) and live in this class rather than their own so they
+    // never run in parallel with the MeterListener assertions above.
+
+    private MessageDispatcher ResolveFromAddKafka(Func<Exception, bool>? isTransient)
+    {
+        var services = new ServiceCollection();
+        services.AddKafka(Substitute.For<IConfiguration>(), isTransient: isTransient);
+        services.AddSingleton(_producer);
+        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
+        return services.BuildServiceProvider().GetRequiredService<MessageDispatcher>();
+    }
+
+    [Fact]
+    public async Task AddKafka_WithPredicate_DispatcherHaltsOnTransientFailure_InsteadOfDeadLettering()
+    {
+        var act = () => ResolveFromAddKafka(OnlyTimeoutsAreTransient)
+            .DispatchAsync(Ctx(), (_, _, _) => throw new TimeoutException("dependency down"), CancellationToken.None);
+
+        await act.Should().ThrowAsync<TimeoutException>();
+        await _producer.DidNotReceive()
+            .ProduceAsync(Arg.Any<string>(), Arg.Any<Message<string, string>>(), Arg.Any<CancellationToken>());
+    }
+
+    [Fact]
+    public async Task AddKafka_WithoutPredicate_DispatcherStillDeadLetters()
+    {
+        await ResolveFromAddKafka(isTransient: null)
+            .DispatchAsync(Ctx(), (_, _, _) => throw new TimeoutException("dependency down"), CancellationToken.None);
+
+        await _producer.Received(1)
+            .ProduceAsync(
+                EntityTopics.Dlq,
+                Arg.Is<Message<string, string>>(m => m.Key == "key-1"),
+                Arg.Any<CancellationToken>());
+    }
 }
diff --git a/Iverson.Server/Iverson.Events/KafkaConsumer.cs b/Iverson.Server/Iverson.Events/KafkaConsumer.cs
index ddf31f3a..840c16e7 100644
--- a/Iverson.Server/Iverson.Events/KafkaConsumer.cs
+++ b/Iverson.Server/Iverson.Events/KafkaConsumer.cs
@@ -72,12 +72,13 @@ public class KafkaConsumer(
                 }
                 catch (Exception ex)
                 {
-                    // The DLQ write itself failed — do not commit. Halt this consumer
-                    // so we never advance past an uncommitted message (block rather than lose).
+                    // The DLQ write failed or a dependency is transiently unavailable — do not
+                    // commit. Halt this consumer so we never advance past an uncommitted message
+                    // (block rather than lose); the restarted consumer is redelivered this message.
                     activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                     activity?.RecordException(ex);
                     logger.LogCritical(ex,
-                        "[Consumer] Halting topic={Topic} group={Group} — offset not committed", topic, groupId);
+                        "[Consumer] Halting topic={Topic} group={Group} — the DLQ write failed or a dependency is transiently unavailable; offset not committed", topic, groupId);
                     throw;
                 }
             }
diff --git a/Iverson.Server/Iverson.Events/MessageDispatcher.cs b/Iverson.Server/Iverson.Events/MessageDispatcher.cs
index fd887237..e49aabfb 100644
--- a/Iverson.Server/Iverson.Events/MessageDispatcher.cs
+++ b/Iverson.Server/Iverson.Events/MessageDispatcher.cs
@@ -19,11 +19,24 @@ public sealed class MessageDispatcherOptions
     public int MaxAttempts { get; init; } = 3;
     public Func<int, TimeSpan> Backoff { get; init; } =
         attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
+
+    /// <summary>
+    /// Classifies the exception that exhausted <see cref="MaxAttempts"/>. True means a dependency
+    /// is transiently unavailable: the dispatcher rethrows instead of dead-lettering, so the
+    /// consumer halts uncommitted and the message is redelivered once the dependency is back.
+    /// The default classifies nothing as transient (every exhausted failure is dead-lettered);
+    /// the composition root supplies the real classifier, so this library needs no client-library
+    /// references.
+    /// </summary>
+    public Func<Exception, bool> IsTransient { get; init; } = _ => false;
 }
 
 /// <summary>
 /// Runs a projection handler under the delivery contract:
 ///   - ordinary exception  → retry (bounded, with backoff), then dead-letter;
+///   - transient exception (per <see cref="MessageDispatcherOptions.IsTransient"/>) that is still
+///     failing after the bounded attempts → throw, no dead-letter (caller must NOT commit — the
+///     consumer halts and the message is redelivered once the dependency is back);
 ///   - PoisonMessageException → dead-letter immediately (no retry);
 ///   - success or successful dead-letter → return normally (caller commits the offset);
 ///   - DLQ write itself fails → throw (caller must NOT commit — halt rather than lose).
@@ -67,6 +80,18 @@ public sealed class MessageDispatcher(
                 attempt++;
                 if (attempt >= _options.MaxAttempts)
                 {
+                    if (_options.IsTransient(ex))
+                    {
+                        logger.LogCritical(
+                            ex,
+                            "[Dispatch] Transient failure persisted after {Max} attempts topic={Topic} key={Key} — not dead-lettering; halting for redelivery",
+                            _options.MaxAttempts,
+                            ctx.SourceTopic,
+                            ctx.Key);
+                        Telemetry.ConsumerTransientHalts.Add(1);
+                        throw;
+                    }
+
                     logger.LogCritical(
                         ex,
                         "[Dispatch] Exhausted {Max} attempts topic={Topic} key={Key} — routing to DLQ",
diff --git a/Iverson.Server/Iverson.Events/ServiceCollectionExtensions.cs b/Iverson.Server/Iverson.Events/ServiceCollectionExtensions.cs
index 0cc00fe1..17e14334 100644
--- a/Iverson.Server/Iverson.Events/ServiceCollectionExtensions.cs
+++ b/Iverson.Server/Iverson.Events/ServiceCollectionExtensions.cs
@@ -11,7 +11,8 @@ public static class ServiceCollectionExtensions
     public static IServiceCollection AddKafka(
         this IServiceCollection services,
         IConfiguration config,
-        int numPartitions = 12)
+        int numPartitions = 12,
+        Func<Exception, bool>? isTransient = null)
     {
         services.Configure<KafkaOptions>(config.GetSection(KafkaOptions.Section));
 
@@ -41,16 +42,20 @@ public static class ServiceCollectionExtensions
 
         services.AddSingleton<IEventBrokerHealthCheck, KafkaBrokerHealthCheck>();
 
+        services.AddSingleton(new MessageDispatcherOptions { IsTransient = isTransient ?? (_ => false) });
+
+        services.AddSingleton(sp => new MessageDispatcher(
+            sp.GetRequiredService<IProducer<string, string>>(),
+            sp.GetRequiredService<ILogger<MessageDispatcher>>(),
+            sp.GetRequiredService<MessageDispatcherOptions>()));
+
         services.AddSingleton<IEventConsumer>(sp =>
         {
             var options = sp.GetRequiredService<IOptions<KafkaOptions>>().Value;
-            var dispatcher = new MessageDispatcher(
-                sp.GetRequiredService<IProducer<string, string>>(),
-                sp.GetRequiredService<ILogger<MessageDispatcher>>());
             return new KafkaConsumer(
                 options,
                 sp.GetRequiredService<ILogger<KafkaConsumer>>(),
-                dispatcher,
+                sp.GetRequiredService<MessageDispatcher>(),
                 cfg => new ConsumerBuilder<string, string>(cfg).Build(),
                 cfg => new AdminClientBuilder(cfg).Build(),
                 numPartitions);
diff --git a/Iverson.Server/Iverson.Events/Telemetry.cs b/Iverson.Server/Iverson.Events/Telemetry.cs
index 2712b70b..e0935d47 100644
--- a/Iverson.Server/Iverson.Events/Telemetry.cs
+++ b/Iverson.Server/Iverson.Events/Telemetry.cs
@@ -15,4 +15,7 @@ internal static class Telemetry
 
     internal static readonly Counter<long> ConsumerDlqRouted =
         Meter.CreateCounter<long>("consumer.dlq_routed", description: "Messages routed to the dead-letter queue");
+
+    internal static readonly Counter<long> ConsumerTransientHalts =
+        Meter.CreateCounter<long>("consumer.transient_halts", description: "Consumer halts on a transient failure that persisted past the dispatcher's attempts");
 }
diff --git a/Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkIngestScenario.cs b/Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkIngestScenario.cs
index b038060d..12720921 100644
--- a/Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkIngestScenario.cs
+++ b/Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkIngestScenario.cs
@@ -61,10 +61,15 @@ public sealed class BenchmarkIngestScenario(
             throw new InvalidOperationException("No corpus found at the given --corpus-path.");
         }
 
-        // Baseline the DLQ before a single document is posted. MessageDispatcher retries 3x and then
-        // routes to the DLQ and *returns normally*, so KafkaConsumer commits the offset and a
-        // dead-lettered event drives consumer lag to zero exactly like a successful one. Lag alone
-        // therefore cannot tell "corpus is searchable" from "some of it was dropped".
+        // Baseline the DLQ before a single document is posted. A transient dependency failure
+        // (Postgres, StarRocks, Qdrant or the embedding server unavailable) no longer dead-letters:
+        // MessageDispatcher rethrows it, so the consumer halts uncommitted and is redelivered the
+        // event once the dependency is back. The drain loop below therefore waits out an outage, and
+        // its lack of a deadline is intended. A NON-transient failure is still retried 3x, then
+        // routed to the DLQ, and the dispatcher *returns normally*, so KafkaConsumer commits the
+        // offset and that dead-lettered event drives consumer lag to zero exactly like a successful
+        // one. Lag alone therefore cannot tell "corpus is searchable" from "some of it was dropped";
+        // only those non-transient failures grow the DLQ high watermark checked here.
         var dlqBaseline = QueryDlqHighWatermark();
         Console.WriteLine($"[benchmark-ingest] DLQ high watermark before ingest: {dlqBaseline:N0}");
 
```

- [ ] **Step 2: Check the md5s.** From the repository root:

```bash
md5sum Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Events.Tests/Iverson.Events.Tests.csproj Iverson.Server/Iverson.Events.Tests/KafkaConsumerTests.cs Iverson.Server/Iverson.Events.Tests/MessageDispatcherTests.cs Iverson.Server/Iverson.Events/KafkaConsumer.cs Iverson.Server/Iverson.Events/MessageDispatcher.cs Iverson.Server/Iverson.Events/ServiceCollectionExtensions.cs Iverson.Server/Iverson.Events/Telemetry.cs Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkIngestScenario.cs
```

Expected:
```
63b5d0bbd94de8220ec0b66595b22187  Iverson.Server/Iverson.Api/Program.cs
90ff42c9a1d0ef25dfdaeff9ef9200a7  Iverson.Server/Iverson.Events.Tests/Iverson.Events.Tests.csproj
06ce46d0f4f45f34b4c0fc2551691737  Iverson.Server/Iverson.Events.Tests/KafkaConsumerTests.cs
4e2ec6ea19998e2ec9766c5b6f44c0e8  Iverson.Server/Iverson.Events.Tests/MessageDispatcherTests.cs
31bb854b590795aa0ebcd8f5b06929a0  Iverson.Server/Iverson.Events/KafkaConsumer.cs
7eac5383d039e9402d825ee7163b115a  Iverson.Server/Iverson.Events/MessageDispatcher.cs
1b66f2a1b6d54ac91bca307db311a066  Iverson.Server/Iverson.Events/ServiceCollectionExtensions.cs
e944165314d25dacf7fb24545555ff10  Iverson.Server/Iverson.Events/Telemetry.cs
2d3fbf19b302ad33d867ba7bcb0323fa  Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkIngestScenario.cs
```

- [ ] **Step 3: Run the tests.** From `Iverson.Server/`:

```bash
systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.Events.Tests -m:2 --filter "Category!=Integration"
systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.Api.Tests -m:2 --filter "Category!=Integration"
systemd-run --user --scope -q -p MemoryMax=4G dotnet build Iverson.LoadTest -m:2
```

Expected:
- Events: `Passed: 35`, up from 29. That includes 3 transient-path dispatcher tests, 2 `AddKafka` wiring tests (about 3 s each, because they use the production backoff) and 1 halt test.
- Api: `Passed: 1259`, unchanged.
- LoadTest: builds with 0 warnings and 0 errors.

- [ ] **Step 4: Commit.** From the repository root:

```bash
git add Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Events.Tests/Iverson.Events.Tests.csproj Iverson.Server/Iverson.Events.Tests/KafkaConsumerTests.cs Iverson.Server/Iverson.Events.Tests/MessageDispatcherTests.cs Iverson.Server/Iverson.Events/KafkaConsumer.cs Iverson.Server/Iverson.Events/MessageDispatcher.cs Iverson.Server/Iverson.Events/ServiceCollectionExtensions.cs Iverson.Server/Iverson.Events/Telemetry.cs Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkIngestScenario.cs
git commit -m "halt for redelivery instead of dead-lettering transient dispatch failures" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 3: StarRocks backend-liveness rewrap

**Files:**
- Modify: `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs` (`RunAsync`, plus the new helper and its bound)
- Create: `Iverson.Server/Iverson.StarRocks.Tests/EngagementRepositoryLivenessRewrapTests.cs`

**Interfaces:**
- Produces `internal static Task<EngagementNotReadyException?> RewrapIfBackendUnavailableAsync(Exception, Func<CancellationToken, Task<bool>>, TimeSpan)` and `internal static readonly TimeSpan LivenessCheckBound` (5 s). Nothing outside `Iverson.StarRocks` uses them.

**Review focus:** the new `catch (MySqlException ex)` in `RunAsync` is the call site no unit test reaches (mutant M3d, accepted by the user). Check three things:
- it sits after the `BrokenCircuitException` catch;
- it passes `probeCt => CheckBackendAliveAsync(connectionString, probeCt)` and `LivenessCheckBound`;
- it rethrows the original with `throw;` when the helper returns null.

The helper deliberately never sees the caller's token (decision 2).

- [ ] **Step 1: Apply the patch.** `BRIEF` is the path of this task brief. The command slices the brief's only `diff` block byte for byte; never retype it. From the repository root:

```bash
python3 - "$BRIEF" > /tmp/claude-1000/tpf-task3.patch <<'PY'
import re, sys
fence = chr(96) * 3
s = open(sys.argv[1]).read()
print(re.search(fence + "diff\n(.*?)" + fence, s, re.S).group(1), end="")
PY
git apply --check /tmp/claude-1000/tpf-task3.patch && git apply /tmp/claude-1000/tpf-task3.patch
```

If `--check` fails, stop and report. Do not hand-edit. The patch:

```diff
diff --git a/Iverson.Server/Iverson.StarRocks.Tests/EngagementRepositoryLivenessRewrapTests.cs b/Iverson.Server/Iverson.StarRocks.Tests/EngagementRepositoryLivenessRewrapTests.cs
new file mode 100644
index 00000000..63a7044f
--- /dev/null
+++ b/Iverson.Server/Iverson.StarRocks.Tests/EngagementRepositoryLivenessRewrapTests.cs
@@ -0,0 +1,133 @@
+using System.Reflection;
+using FluentAssertions;
+using Iverson.StarRocks;
+using MySqlConnector;
+using Xunit;
+
+namespace Iverson.StarRocks.Tests;
+
+/// <summary>
+/// Spec §5: <see cref="EngagementRepository.RewrapIfBackendUnavailableAsync"/> turns a non-transient,
+/// non-missing-resource StarRocks failure into <see cref="EngagementNotReadyException"/> when the backend cannot
+/// be shown alive, driven here by a fake liveness check so no server is needed.
+/// </summary>
+public class EngagementRepositoryLivenessRewrapTests
+{
+    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
+
+    // MySqlException's constructors are internal to MySqlConnector with no InternalsVisibleTo grant to this
+    // assembly; built via reflection against the exact overload (StarRocksResiliencePipelineFactoryTests).
+    private static MySqlException CreateMySqlException(MySqlErrorCode errorCode, string message) =>
+        (MySqlException)typeof(MySqlException)
+            .GetConstructor(
+                BindingFlags.NonPublic | BindingFlags.Instance,
+                binder: null,
+                types: [typeof(MySqlErrorCode), typeof(string), typeof(string), typeof(Exception)],
+                modifiers: null)!
+            .Invoke([errorCode, null, message, null]);
+
+    /// <summary>Shape 2 (frozen server): IsTransient is false, and it is not a missing resource.</summary>
+    private static MySqlException NonTransient() =>
+        CreateMySqlException(MySqlErrorCode.CommandTimeoutExpired, "The Command Timeout expired before the operation completed.");
+
+    /// <summary>A liveness check that records whether it ran.</summary>
+    private sealed class FakeCheck(Func<CancellationToken, Task<bool>> behaviour)
+    {
+        public int Calls { get; private set; }
+
+        public Task<bool> Invoke(CancellationToken ct)
+        {
+            Calls++;
+            return behaviour(ct);
+        }
+    }
+
+    [Fact]
+    public async Task NonTransient_BackendReportsDead_RewrapsAsNotReady()
+    {
+        var original = NonTransient();
+        var check = new FakeCheck(_ => Task.FromResult(false));
+
+        var result = await EngagementRepository.RewrapIfBackendUnavailableAsync(original, check.Invoke, Bound);
+
+        result.Should().NotBeNull();
+        result!.InnerException.Should().BeSameAs(original);
+        check.Calls.Should().Be(1);
+    }
+
+    [Fact]
+    public async Task NonTransient_CheckThrows_RewrapsAsNotReady()
+    {
+        var original = NonTransient();
+        var check = new FakeCheck(_ => Task.FromException<bool>(new ProbeFailedException()));
+
+        var result = await EngagementRepository.RewrapIfBackendUnavailableAsync(original, check.Invoke, Bound);
+
+        result.Should().NotBeNull();
+        result!.InnerException.Should().BeSameAs(original);
+        check.Calls.Should().Be(1);
+    }
+
+    [Fact]
+    public async Task NonTransient_CheckExceedsBound_RewrapsAsNotReady()
+    {
+        var original = NonTransient();
+        // Ignores its token, so only the bound can end the wait.
+        var check = new FakeCheck(async _ => { await Task.Delay(TimeSpan.FromSeconds(30)); return true; });
+
+        var result = await EngagementRepository.RewrapIfBackendUnavailableAsync(
+            original, check.Invoke, TimeSpan.FromMilliseconds(50));
+
+        result.Should().NotBeNull();
+        result!.InnerException.Should().BeSameAs(original);
+    }
+
+    [Fact]
+    public async Task NonTransient_CheckObservesTokenAtBound_RewrapsAsNotReady()
+    {
+        var original = NonTransient();
+        var check = new FakeCheck(async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return true; });
+
+        var result = await EngagementRepository.RewrapIfBackendUnavailableAsync(
+            original, check.Invoke, TimeSpan.FromMilliseconds(50));
+
+        result.Should().NotBeNull();
+        result!.InnerException.Should().BeSameAs(original);
+    }
+
+    [Fact]
+    public async Task NonTransient_BackendReportsAlive_ReturnsNullSoTheOriginalIsRethrown()
+    {
+        var check = new FakeCheck(_ => Task.FromResult(true));
+
+        var result = await EngagementRepository.RewrapIfBackendUnavailableAsync(NonTransient(), check.Invoke, Bound);
+
+        result.Should().BeNull();
+        check.Calls.Should().Be(1);
+    }
+
+    public static TheoryData<string> ExemptCases() => new(Exempt.Keys);
+
+    private static readonly Dictionary<string, Func<Exception>> Exempt = new()
+    {
+        ["missing resource: cannot find role"]  = () => CreateMySqlException(MySqlErrorCode.ParseError, "cannot find role role_tenant_x"),
+        ["missing resource: is not granted to"] = () => CreateMySqlException(MySqlErrorCode.ParseError, "Role role_tenant_x is not granted to 'iverson_app'@'%'"),
+        ["missing resource: unknown table"]     = () => CreateMySqlException((MySqlErrorCode)5502, "Unknown table 'iverson.articles'"),
+        ["library-transient MySqlException"]    = () => CreateMySqlException(MySqlErrorCode.UnableToConnectToHost, "Unable to connect to any of the specified MySQL hosts."),
+        ["not a MySqlException"]                = () => new InvalidOperationException("boom"),
+    };
+
+    [Theory]
+    [MemberData(nameof(ExemptCases))]
+    public async Task Exempt_IsNotRewrapped_AndMakesNoLivenessCheck(string name)
+    {
+        var check = new FakeCheck(_ => Task.FromResult(false));   // would rewrap, if it were consulted
+
+        var result = await EngagementRepository.RewrapIfBackendUnavailableAsync(Exempt[name](), check.Invoke, Bound);
+
+        result.Should().BeNull(name);
+        check.Calls.Should().Be(0, name);
+    }
+
+    private sealed class ProbeFailedException() : Exception("connection refused");
+}
diff --git a/Iverson.Server/Iverson.StarRocks/EngagementRepository.cs b/Iverson.Server/Iverson.StarRocks/EngagementRepository.cs
index c21b66cb..ebb207bf 100644
--- a/Iverson.Server/Iverson.StarRocks/EngagementRepository.cs
+++ b/Iverson.Server/Iverson.StarRocks/EngagementRepository.cs
@@ -53,6 +53,50 @@ public sealed class EngagementRepository(
             throw new EngagementNotReadyException(
                 "StarRocks is currently unavailable (circuit breaker open).", ex);
         }
+        catch (MySqlException ex)
+        {
+            if (await RewrapIfBackendUnavailableAsync(
+                    ex, probeCt => CheckBackendAliveAsync(connectionString, probeCt), LivenessCheckBound)
+                .ConfigureAwait(false) is { } unavailable)
+                throw unavailable;
+            throw;
+        }
+    }
+
+    /// <summary>How long the post-failure backend liveness check may take before it counts as dead.</summary>
+    internal static readonly TimeSpan LivenessCheckBound = TimeSpan.FromSeconds(5);
+
+    /// <summary>
+    /// Decides whether a StarRocks failure is really a backend outage (spec §5). MySqlConnector marks only five
+    /// error codes transient, and the circuit breaker counts failures with that same predicate, so an outage that
+    /// is not a refused connection (a frozen server, a BE down behind a live FE) neither classifies as transient
+    /// nor opens the circuit. For a <see cref="MySqlException"/> that is neither <see cref="MySqlException.IsTransient"/>
+    /// nor <see cref="IsExpectedMissingResourceError"/>, this runs <paramref name="checkBackendAlive"/> bounded to
+    /// <paramref name="bound"/>; a false result, a throw, or a timeout returns an
+    /// <see cref="EngagementNotReadyException"/> wrapping <paramref name="ex"/>, which the projection consumers'
+    /// classifier treats as transient and the gRPC read paths map to <c>Unavailable</c>. Null means rethrow the
+    /// original. Expected missing-resource errors are exempt so that a read of an unwritten type or an
+    /// unprovisioned tenant never pays a liveness round-trip.
+    /// </summary>
+    internal static async Task<EngagementNotReadyException?> RewrapIfBackendUnavailableAsync(
+        Exception ex, Func<CancellationToken, Task<bool>> checkBackendAlive, TimeSpan bound)
+    {
+        if (ex is not MySqlException mex || mex.IsTransient || IsExpectedMissingResourceError(mex))
+            return null;
+
+        bool alive;
+        try
+        {
+            using var cts = new CancellationTokenSource(bound);
+            // WaitAsync bounds a check that ignores its token; the token lets a cooperative one stop early.
+            alive = await checkBackendAlive(cts.Token).WaitAsync(bound).ConfigureAwait(false);
+        }
+        catch (Exception)
+        {
+            alive = false;   // the check threw or timed out: the backend cannot be shown alive
+        }
+
+        return alive ? null : new EngagementNotReadyException("StarRocks backend unavailable", ex);
     }
 
     public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null)
```

- [ ] **Step 2: Check the md5s.** From the repository root:

```bash
md5sum Iverson.Server/Iverson.StarRocks.Tests/EngagementRepositoryLivenessRewrapTests.cs Iverson.Server/Iverson.StarRocks/EngagementRepository.cs
```

Expected:
```
eb2b52b52efaaed5926f9d91cef854c0  Iverson.Server/Iverson.StarRocks.Tests/EngagementRepositoryLivenessRewrapTests.cs
4362d1a12a36b9b79fe9c80a5aef5cf1  Iverson.Server/Iverson.StarRocks/EngagementRepository.cs
```

- [ ] **Step 3: Run the tests.** From `Iverson.Server/`:

```bash
systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.StarRocks.Tests -m:2 --filter "FullyQualifiedName~Iverson.StarRocks.Tests.EngagementRepositoryLivenessRewrapTests"
systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.StarRocks.Tests -m:2 --filter "Category!=Integration"
```

Expected: `Passed: 10` (5 facts and 5 theory cases), then `Passed: 440`, up from 430.

- [ ] **Step 4: Commit.** From the repository root:

```bash
git add Iverson.Server/Iverson.StarRocks.Tests/EngagementRepositoryLivenessRewrapTests.cs Iverson.Server/Iverson.StarRocks/EngagementRepository.cs
git commit -m "rewrap starrocks failures as not-ready when the backend is not alive" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 4: Let transient failures escape the swallowing consumer catches

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs` (`:232`)
- Modify: `Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs` (`:86` and `:259`)
- Modify: `Iverson.Server/Iverson.Api/Consumers/DocumentRerenderConsumer.cs` (`:120`)
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs`, `PopularitySignalConsumerTests.cs` and `DocumentRerenderConsumerTests.cs`

**Interfaces:**
- Consumes `TransientFailures.IsTransient` from Task 1.

**Test changes:**
- **Flipped to assert propagation:**
  - `HandleUpdated_WhenLlmFails_LeavesObjectIntactAndDoesNotThrow` becomes `HandleUpdated_WhenLlmUnreachable_PropagatesForRedeliveryAndWritesNothing`;
  - `Dispatch_SetPayloadAsyncThrowsOtherRpcException_IsCaughtBySignalLevelHandler` becomes `Dispatch_SetPayloadAsyncThrowsUnavailable_PropagatesForRedelivery`.
- **Added:**
  - `HandleUpdated_WhenLlmRejectsRequest_LeavesObjectIntactAndDoesNotThrow` (HTTP 400);
  - `Dispatch_SetPayloadAsyncThrowsNonTransientRpcException_IsCaughtBySignalLevelHandler` (InvalidArgument);
  - `UpdateAsync_AggregateThrowsTransient_Propagates`;
  - `Dispatch_DependentReadHitsTransientPostgresFailure_PropagatesForRedelivery`.
- **Unchanged:** the existing non-transient tests.

- [ ] **Step 1: Apply the patch.** `BRIEF` is the path of this task brief. The command slices the brief's only `diff` block byte for byte; never retype it. From the repository root:

```bash
python3 - "$BRIEF" > /tmp/claude-1000/tpf-task4.patch <<'PY'
import re, sys
fence = chr(96) * 3
s = open(sys.argv[1]).read()
print(re.search(fence + "diff\n(.*?)" + fence, s, re.S).group(1), end="")
PY
git apply --check /tmp/claude-1000/tpf-task4.patch && git apply /tmp/claude-1000/tpf-task4.patch
```

If `--check` fails, stop and report. Do not hand-edit. The patch:

```diff
diff --git a/Iverson.Server/Iverson.Api.Tests/Consumers/DocumentRerenderConsumerTests.cs b/Iverson.Server/Iverson.Api.Tests/Consumers/DocumentRerenderConsumerTests.cs
index 99dc504f..16489d54 100644
--- a/Iverson.Server/Iverson.Api.Tests/Consumers/DocumentRerenderConsumerTests.cs
+++ b/Iverson.Server/Iverson.Api.Tests/Consumers/DocumentRerenderConsumerTests.cs
@@ -1,5 +1,6 @@
 using System.Text.Json;
 using FluentAssertions;
+using Npgsql;
 using Iverson.Api.Consumers;
 using Iverson.Api.Schema;
 using Iverson.Events;
@@ -584,4 +585,26 @@ public class DocumentRerenderConsumerTests
         await _queue.Received(1).EnqueueEntityAsync(TenantA, "Badge", BadgeId);
         await _queue.DidNotReceive().EnqueueEntityAsync(TenantA, "Widget", WidgetId);
     }
+
+    // The per-dependent catch isolates a bad dependent, but a Postgres outage is not one: a transient
+    // failure escapes to the dispatcher, which halts the consumer for redelivery instead of skipping.
+    [Fact]
+    public async Task Dispatch_DependentReadHitsTransientPostgresFailure_PropagatesForRedelivery()
+    {
+        await _registry.RegisterAsync(WidgetSchema());
+        await _registry.RegisterAsync(BadgeSchema());
+        await _registry.RegisterAsync(AuthorSchema());
+
+        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), AuthorId, Arg.Any<EntityAccess>())
+            .Returns($$"""{"Id":"{{AuthorId}}","Name":"Ada","TenantId":"{{TenantA}}"}""");
+        _entities.FetchByColumnAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), AuthorId, EntityAccess.ForTenant(TenantA))
+            .Returns<IEnumerable<string>>(_ => throw new PostgresException(
+                "the database system is in recovery mode", "FATAL", "FATAL", "57P03"));
+
+        var ev = MakeEvent(EntityEventType.Updated, "Author", AuthorId,
+            $$"""{"Id":"{{AuthorId}}","Name":"Ada","TenantId":"{{TenantA}}"}""");
+
+        var act = () => BuildSut().DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);
+        await act.Should().ThrowAsync<PostgresException>().Where(e => e.SqlState == "57P03");
+    }
 }
diff --git a/Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs b/Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs
index d80456bf..460297a2 100644
--- a/Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs
+++ b/Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs
@@ -1,3 +1,4 @@
+using System.Net;
 using System.Text.Json;
 using FluentAssertions;
 using Iverson.Api.Consumers;
@@ -472,8 +473,11 @@ public class EnrichmentConsumerTests
 
     // ── Failure handling ──────────────────────────────────────────────────────
 
+    // An unreachable LLM (HttpRequestException with no status: a connection failure) is a transient
+    // dependency outage. It must escape to the dispatcher, which halts the consumer for redelivery,
+    // rather than be swallowed and the object skipped — and nothing may be written before it does.
     [Fact]
-    public async Task HandleUpdated_WhenLlmFails_LeavesObjectIntactAndDoesNotThrow()
+    public async Task HandleUpdated_WhenLlmUnreachable_PropagatesForRedeliveryAndWritesNothing()
     {
         await _registry.RegisterAsync(EnrichedArticle());
         _enrichment.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
@@ -482,6 +486,26 @@ public class EnrichmentConsumerTests
         var sut = BuildSut();
         var act = async () => await sut.HandleAsync(Key, Event(EntityEventType.Updated), CancellationToken.None);
 
+        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("ollama down");
+        await _entities.DidNotReceiveWithAnyArgs().UpdateColumnsAsync(default!, default!, default!, default!, default);
+        await _state.DidNotReceiveWithAnyArgs().UpsertAsync(
+            default!, default!, default!, default!, default!, default);
+        await _outboxPublisher.DidNotReceiveWithAnyArgs().PublishAsync(
+            default, default!, default!, default!, default, default, default, default!, default);
+    }
+
+    // The non-transient sibling: the LLM answering 400 is a bad request, not an outage, so the
+    // best-effort catch still logs it, leaves the object intact and returns normally.
+    [Fact]
+    public async Task HandleUpdated_WhenLlmRejectsRequest_LeavesObjectIntactAndDoesNotThrow()
+    {
+        await _registry.RegisterAsync(EnrichedArticle());
+        _enrichment.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
+                   .Throws(new HttpRequestException("bad request", inner: null, statusCode: HttpStatusCode.BadRequest));
+
+        var sut = BuildSut();
+        var act = async () => await sut.HandleAsync(Key, Event(EntityEventType.Updated), CancellationToken.None);
+
         await act.Should().NotThrowAsync();
         await _entities.DidNotReceiveWithAnyArgs().UpdateColumnsAsync(default!, default!, default!, default!, default);
         await _state.DidNotReceiveWithAnyArgs().UpsertAsync(
diff --git a/Iverson.Server/Iverson.Api.Tests/Consumers/PopularitySignalConsumerTests.cs b/Iverson.Server/Iverson.Api.Tests/Consumers/PopularitySignalConsumerTests.cs
index e870a5eb..ad4efef1 100644
--- a/Iverson.Server/Iverson.Api.Tests/Consumers/PopularitySignalConsumerTests.cs
+++ b/Iverson.Server/Iverson.Api.Tests/Consumers/PopularitySignalConsumerTests.cs
@@ -411,11 +411,10 @@ public class PopularitySignalConsumerTests
         await act.Should().NotThrowAsync();
     }
 
-    // A non-NotFound RpcException is NOT one of the two documented degrade cases and must
-    // propagate to DispatchAsync's own per-signal catch, which logs and moves on rather than
-    // crashing the whole dispatch — but it must not be silently swallowed by UpdateAsync itself.
+    // Qdrant Unavailable is a transient dependency outage: it must escape DispatchAsync's per-signal
+    // catch to the dispatcher, which halts the consumer for redelivery instead of skipping the signal.
     [Fact]
-    public async Task Dispatch_SetPayloadAsyncThrowsOtherRpcException_IsCaughtBySignalLevelHandler()
+    public async Task Dispatch_SetPayloadAsyncThrowsUnavailable_PropagatesForRedelivery()
     {
         await _registry.RegisterAsync(ArticleSchema());
         await _registry.RegisterAsync(CommentSchema());
@@ -430,6 +429,29 @@ public class PopularitySignalConsumerTests
         var ev = MakeEvent(EntityEventType.Created, "Comment", CommentId, payload);
         var sut = BuildSut(OptionsWith("Article", "Comments"));
 
+        var act = () => sut.DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);
+        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.Unavailable);
+    }
+
+    // A non-transient, non-NotFound RpcException is NOT one of the two documented degrade cases and
+    // must propagate to DispatchAsync's own per-signal catch, which logs and moves on rather than
+    // crashing the whole dispatch — but it must not be silently swallowed by UpdateAsync itself.
+    [Fact]
+    public async Task Dispatch_SetPayloadAsyncThrowsNonTransientRpcException_IsCaughtBySignalLevelHandler()
+    {
+        await _registry.RegisterAsync(ArticleSchema());
+        await _registry.RegisterAsync(CommentSchema());
+        StubCount(1);
+        _vector.SetPayloadAsync(
+                Arg.Any<string>(), Arg.Any<ulong>(), Arg.Any<IReadOnlyDictionary<string, object>>())
+            .Returns(Task.FromException(new RpcException(new Status(StatusCode.InvalidArgument, "bad payload"))));
+
+        var payload = $$"""{"Id":"{{CommentId}}","Body":"hi","ArticleId":"{{ArticleId}}","TenantId":"{{TenantA}}"}""";
+        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), CommentId, Arg.Any<EntityAccess>()).Returns(payload);
+
+        var ev = MakeEvent(EntityEventType.Created, "Comment", CommentId, payload);
+        var sut = BuildSut(OptionsWith("Article", "Comments"));
+
         var act = () => sut.DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);
         await act.Should().NotThrowAsync("DispatchAsync isolates per-signal failures the same way DocumentRerenderConsumer isolates per-dependent failures");
     }
@@ -583,6 +605,26 @@ public class PopularitySignalConsumerTests
         outcome.Should().Be(PopularityUpdateOutcome.Failed);
     }
 
+    // A transient StarRocks failure (circuit open / backend down) is not converted to Failed: it
+    // propagates, so DispatchAsync's caller halts for redelivery. The reconciliation sweep still
+    // counts it as Failed through its own catch, whose outcome is seeded to Failed.
+    [Fact]
+    public async Task UpdateAsync_AggregateThrowsTransient_Propagates()
+    {
+        _search.AggregateAsync(
+                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
+                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
+                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
+                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
+            .Returns(Task.FromException<EngagementAggResult?>(new EngagementNotReadyException("StarRocks backend unavailable")));
+
+        var act = () => BuildUpdater().UpdateAsync(
+            ArticleSchema(), new PopularitySignalEntry("Article", "Comments"), CommentSchema(),
+            ArticleSchema().Relations[0], ArticleId, TenantA);
+
+        await act.Should().ThrowAsync<EngagementNotReadyException>();
+    }
+
     // A null aggregate result is the DESIGNED outcome of the unprovisioned-tenant race — Skipped,
     // never Failed, or a sweep over an unprovisioned tenant would abandon itself.
     [Fact]
diff --git a/Iverson.Server/Iverson.Api/Consumers/DocumentRerenderConsumer.cs b/Iverson.Server/Iverson.Api/Consumers/DocumentRerenderConsumer.cs
index f75bcbe8..5c08bb0a 100644
--- a/Iverson.Server/Iverson.Api/Consumers/DocumentRerenderConsumer.cs
+++ b/Iverson.Server/Iverson.Api/Consumers/DocumentRerenderConsumer.cs
@@ -117,7 +117,7 @@ public sealed class DocumentRerenderConsumer(
                             $"Unhandled {nameof(RelationKind)} value — add a case above.");
                 }
             }
-            catch (Exception ex)
+            catch (Exception ex) when (!TransientFailures.IsTransient(ex))
             {
                 logger.LogError(ex,
                     "[DocumentRerender] Failed to enqueue re-render for dependent type={DeclaringType} " +
diff --git a/Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs b/Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs
index 0d8991d2..ba35a1a9 100644
--- a/Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs
+++ b/Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs
@@ -229,9 +229,11 @@ public sealed class EnrichmentConsumer(
                 "[Enrichment] Enriched {Count} column(s) for {Type}:{Key}",
                 columns.Count, schema.TypeName.SanitizeForLog(), ev.Key);
         }
-        catch (Exception ex)
+        catch (Exception ex) when (!TransientFailures.IsTransient(ex))
         {
-            // Leaves no state row, so the next event for this object retries.
+            // Leaves no state row, so the next event for this object retries. A transient dependency
+            // failure (Ollama/Postgres unavailable) is not caught: it reaches the dispatcher, which halts
+            // the consumer for redelivery instead of skipping the object.
             logger.LogError(ex,
                 "[Enrichment] Failed for {Type}:{Key} — object left intact and unenriched.",
                 schema.TypeName.SanitizeForLog(), ev.Key);
diff --git a/Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs b/Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs
index b940be27..9f9bb3b8 100644
--- a/Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs
+++ b/Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs
@@ -83,7 +83,7 @@ internal sealed class PopularitySignalUpdater(
                 spec,
                 authz: authzConstraints);
         }
-        catch (Exception ex) when (ex is not OperationCanceledException)
+        catch (Exception ex) when (ex is not OperationCanceledException && !TransientFailures.IsTransient(ex))
         {
             logger.LogError(ex,
                 "[PopularitySignal] AggregateAsync failed for parent={Parent} type={Type} relation={Relation}; skipping.",
@@ -256,7 +256,7 @@ internal sealed class PopularitySignalConsumer(
                         await updater.UpdateAsync(parentSchema, signal, childSchema, relation, oldParentKey, tenantId);
                 }
             }
-            catch (Exception ex)
+            catch (Exception ex) when (!TransientFailures.IsTransient(ex))
             {
                 logger.LogError(ex,
                     "[PopularitySignal] Failed to update parent for signal={Signal} type={Type} key={Key} — skipping.",
```

- [ ] **Step 2: Check the md5s.** From the repository root:

```bash
md5sum Iverson.Server/Iverson.Api.Tests/Consumers/DocumentRerenderConsumerTests.cs Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs Iverson.Server/Iverson.Api.Tests/Consumers/PopularitySignalConsumerTests.cs Iverson.Server/Iverson.Api/Consumers/DocumentRerenderConsumer.cs Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs
```

Expected:
```
19c0c9837cb7a5c4dfe40f7573aaeeac  Iverson.Server/Iverson.Api.Tests/Consumers/DocumentRerenderConsumerTests.cs
e3f67477b2e6d1bec9f70f47ee322315  Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs
812f042d7ae4858ff432afe09c8e7be1  Iverson.Server/Iverson.Api.Tests/Consumers/PopularitySignalConsumerTests.cs
7576212a537ce3aee1023b0bcaffcbc1  Iverson.Server/Iverson.Api/Consumers/DocumentRerenderConsumer.cs
40f31d98ac3c6a52337848bdbeedef8d  Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs
712211c46ba4c699677347562b4e6b63  Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs
```

- [ ] **Step 3: Run the tests.** From `Iverson.Server/`:

```bash
systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.Api.Tests -m:2 --filter "FullyQualifiedName~Iverson.Api.Tests.Consumers.EnrichmentConsumerTests|FullyQualifiedName~Iverson.Api.Tests.Consumers.PopularitySignalConsumerTests|FullyQualifiedName~Iverson.Api.Tests.Consumers.DocumentRerenderConsumerTests|FullyQualifiedName~Iverson.Api.Tests.Consumers.TransientFailuresTests"
systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.Api.Tests -m:2 --filter "Category!=Integration"
systemd-run --user --scope -q -p MemoryMax=4G dotnet build Iverson.Server.slnx -m:2 --no-incremental
```

Expected:
- First command: `Passed: 104` (19 + 23 + 24 + 38). `--list-tests` confirms this substring filter matches only those four classes.
- Second command: `Passed: 1263`, up from 1259.
- Build: 0 errors and 21 warnings, the same as at `519ae09d`.

- [ ] **Step 4: Commit.** From the repository root:

```bash
git add Iverson.Server/Iverson.Api.Tests/Consumers/DocumentRerenderConsumerTests.cs Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs Iverson.Server/Iverson.Api.Tests/Consumers/PopularitySignalConsumerTests.cs Iverson.Server/Iverson.Api/Consumers/DocumentRerenderConsumer.cs Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs
git commit -m "let transient failures escape the swallowing consumer catches" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

## Tasks NOT in this plan

- The Postgres SIGPIPE crash's root cause.
- Automatic replay of DLQ rows that already exist.
- A Polly pipeline for Postgres, Qdrant or HTTP.
- A deadline for the benchmark drain loop.

## Known issues inherited from spec

- **Optional-input catches are deliberately untouched.** In these three places an outage degrades the output but loses no event:
  - `IntelligenceStoreConsumer.FetchSummaryAsync` (`:559-567`): the vector is written without the context summary.
  - `IntelligenceStoreConsumer.PrefixWithContextAsync` (`:594-600`): the vector is written without the context prefix.
  - `PopularitySignalUpdater`'s histogram catch (`:121`): the count is written with an empty series.
- **Log and rejoin churn during an outage.** One Critical "Halting" log line and one consumer-group rejoin per ~13 s cycle, per affected consumer.
- **Two StarRocks rules match message text:** "Backend node not found" and `Unexpected input '@'`. If StarRocks or MySqlConnector change that wording, the shape silently reverts to non-transient. The §5 liveness rewrap still covers shapes 1 and 2, but shape 3 would be lost.
- **Extra round-trip:** one FE liveness check per StarRocks failure that is neither transient nor an expected missing resource.
- **gRPC behavior change:** reads return `Unavailable` during a StarRocks backend outage (§5).
- **Misclassification has a cost in both directions.** A transient failure classified as permanent is still dead-lettered. A permanent failure classified as transient stalls its consumer until someone intervenes. The classifier test matrix is the guard.
- **Existing, unchanged behavior:** an HTTP timeout reaching the dispatcher directly (Engagement, Intelligence) is a `TaskCanceledException`, which is an `OperationCanceledException`. The dispatcher and `KafkaConsumer` already treat it as cancellation: the loop exits, `ConsumeAsync` returns, and `ConsumerResilience` restarts immediately with no 10 s delay. It is redelivered either way.
