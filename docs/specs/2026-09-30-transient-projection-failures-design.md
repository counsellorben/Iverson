# Transient Projection Failures: Wait, Don't Dead-Letter — Design

**Date:** 2026-09-30
**Status:** Approved design, assumptions verified
**Trigger:** the 2026-09-30 03:06 UTC Postgres SIGPIPE crash during the MatchPattern dialogue ingest (`docs/plans/2026-09-GATE-matchpattern-dialogue-sequences.md` §7).

## Problem

During that crash, Postgres spent about 23 s in crash recovery (03:06:00 to 03:06:23 UTC). `EngagementStoreConsumer`'s upsert reads the authoritative row from Postgres, via `ProjectionTenantResolution.FetchAuthoritativeRowAsync`, before projecting to StarRocks. That read failed with `57P03: the database system is in recovery mode`.

`MessageDispatcher` retried 3 times, backing off 1 s and 2 s, so it gave up about 3 s after the first failure. That was still inside the recovery window. It then dead-lettered the three in-flight events and committed their offsets. Two StarRocks rows were never written. Only the harness's exact-set readiness check noticed. Recovery needed a manual `POST /admin/dlq/{id}/replay`.

Mapping all five dispatcher-backed consumers showed a second, quieter loss path. Three consumers catch **every** exception, including dependency outages, then log it and return normally, so the offset is committed and the event is gone without even a DLQ row:
- `EnrichmentConsumer`
- `PopularitySignalConsumer`
- `DocumentRerenderConsumer`

## Goal

A dependency outage of **any length** never loses a projection event. The dependencies in scope are Postgres, StarRocks, Qdrant, and TEI or Ollama over HTTP.
- **During an outage,** the affected consumer waits and its lag grows.
- **When the dependency returns,** the consumer catches up on its own, in order.
- **Only genuinely bad messages** still reach the DLQ: poison messages, schema-not-found, bad LLM output, 4xx responses and constraint violations.

## Approach (chosen: B — throw, halt, redeliver)

The consumer loop already implements "halt rather than lose":
1. `KafkaConsumer.ConsumeAsync` commits only after `DispatchAsync` returns.
2. On a throw, it logs "Halting" and rethrows without committing.
3. `ConsumerResilience.RunWithRestartAsync` restarts the loop after 10 s.
4. The new consumer resumes from the last committed offset, so Kafka redelivers the failed message first.

This was probed against the live broker (see Verified assumptions). Until now, the only thing that reaches this path is a failed DLQ write.

This design routes transient dependency failures into the same path. After the dispatcher's existing bounded attempts, a transient failure is **rethrown instead of dead-lettered**. The loop halts and restarts about every 13 s until the dependency is back.

Rejected alternatives:
- **Retry in place forever inside the dispatcher.** A handler blocked past `max.poll.interval.ms` (librdkafka default 300 s; nothing in the repo sets it) is evicted from its group, and its later commit fails into B's path anyway, through an error.
- **A hybrid with a longer in-place backoff before halting.** It only reduces log and rejoin churn during short blips, at the cost of a second backoff schedule.

## Design

### 1. One transient classifier, owned by the API

A new static class `TransientFailures` in `Iverson.Api`, the composition root that references every client library, exposes `static bool IsTransient(Exception ex)`. It walks the whole `InnerException` chain. For an `AggregateException` it checks every `InnerExceptions` member, and it returns true if any link is transient.

| Dependency | Transient when | Library |
|---|---|---|
| Postgres, StarRocks | `DbException` with `IsTransient == true` | Npgsql and MySqlConnector both derive from `System.Data.Common.DbException` and override `IsTransient` |
| StarRocks readiness | `EngagementNotReadyException` | Polly circuit open, or the readiness gate timed out |
| Qdrant | `Grpc.Core.RpcException` with `StatusCode` Unavailable, DeadlineExceeded, ResourceExhausted or Aborted | Qdrant.Client 1.18.1 |
| TEI / Ollama HTTP | `HttpRequestException` whose `StatusCode` is null (connection failure), 5xx or 429 | `EmbeddingService` and `EnrichmentService` call `EnsureSuccessStatusCode()` |
| Any HTTP timeout | `TimeoutException` anywhere in the chain | an `HttpClient` timeout surfaces as `TaskCanceledException` wrapping `TimeoutException` |
| StarRocks, frozen server | `MySqlException` with `ErrorCode == CommandTimeoutExpired` (-1) | shape 2; there is no `TimeoutException` in this chain |
| StarRocks, backend down while the FE is up | `MySqlException` whose message contains "Backend node not found" | shape 1; also covered by the liveness rewrap in §5 |
| StarRocks, FE died mid-query | `MySqlException` with an inner `MySqlEndOfStreamException` | shape 4 |
| StarRocks, statement outlived its command timeout | `MySqlException` with `ErrorCode == ParseError` (1064) and a message containing `Unexpected input '@'` | shape 3: StarRocks rejects MySqlConnector's cancellation cleanup |

Everything else is non-transient, including:
- `PostgresException` 42P01, 42601, 23505 and 22P02;
- any other `MySqlException` 1064: the role errors "cannot find role" and "is not granted to", and ordinary syntax errors;
- `MySqlException` 5502 (unknown table);
- `RpcException` InvalidArgument and NotFound;
- `HttpRequestException` 4xx other than 429;
- `PoisonMessageException`, `InvalidOperationException` and `JsonException`.

### 2. Dispatcher and consumer loop

- **`MessageDispatcherOptions`** (`Iverson.Events/MessageDispatcher.cs:17`) gains `public Func<Exception, bool> IsTransient { get; init; } = _ => false;`. The default keeps every existing caller and test behaving exactly as today, and `Iverson.Events` needs no client-library references.
- **`MessageDispatcher.DispatchAsync`.** When an ordinary exception exhausts `MaxAttempts` (still 3), the dispatcher checks `_options.IsTransient(ex)`. If it is true:
  - log Critical: `[Dispatch] Transient failure persisted after {Max} attempts topic={Topic} key={Key} — not dead-lettering; halting for redelivery`;
  - increment a new counter, `Telemetry.ConsumerTransientHalts` (`consumer.transient_halts`), next to the existing `consumer.retries` and `consumer.dlq_routed` counters at `Telemetry.cs:13-17`;
  - rethrow the original exception, with no DLQ write.

  Otherwise it dead-letters as today. Poison messages still dead-letter immediately. `OperationCanceledException` still rethrows immediately, and a failed DLQ write still throws.
- **`AddKafka`** (`Iverson.Events/ServiceCollectionExtensions.cs:11`) gains an optional `Func<Exception, bool>? isTransient = null` parameter. The single production construction site (`:47`) passes it into `new MessageDispatcherOptions { IsTransient = isTransient ?? (_ => false) }`. `Program.cs:340` changes to `builder.Services.AddKafka(cfg, isTransient: TransientFailures.IsTransient);`.
- **`KafkaConsumer.ConsumeAsync`** has no behavior change. Only the comment and log wording at the halt site change: from "the DLQ write itself failed" to "the DLQ write failed or a dependency is transiently unavailable".
- **`ConsumerResilience`** is unchanged. Each of the five consumers runs its own group and loop, so an outage halts only the consumers that need the missing dependency.

### 3. The three consumers that swallow failures

Each broad catch gains the filter `when (!TransientFailures.IsTransient(ex))`. Transient failures then escape to the dispatcher, and everything else is handled exactly as today.

- **`EnrichmentConsumer`** (`:232-238`, the catch around generation, write-back and republish). Non-transient failures stay best-effort: logged, no state row written, offset committed. Transient failures now halt, including Ollama being unreachable or returning 5xx, and Postgres being down during the write-back transaction. An Ollama timeout is skipped, logged and counted instead (see `docs/specs/2026-10-01-enrichment-timeout-skip-design.md`): no state row, offset commits, `enrichment.timeouts_skipped` increments. **Behavior change:** a deployment that enables enrichment while Ollama is down accumulates enrichment lag instead of skipping objects.
- **`PopularitySignalConsumer`**, at two sites:
  - The per-relation catch in `DispatchAsync` (`:259-264`).
  - The catch in `PopularitySignalUpdater.UpdateAsync` that converts a StarRocks `AggregateAsync` failure to `Failed` (`:86-92`). It becomes `catch (Exception ex) when (ex is not OperationCanceledException && !TransientFailures.IsTransient(ex))`.

  The updater's other caller, `PopularitySignalReconciliationWorker.SweepSignalAsync` (`:72-82`), already wraps `UpdateAsync` in its own catch-all with `outcome` seeded to `Failed`. A transient failure that now escapes the updater therefore still counts as `Failed` there, and the sweep's 5-consecutive-failure abandon rule is unchanged.
- **`DocumentRerenderConsumer`** (`:120-127`, the per-dependent catch). One bad dependent still cannot starve the others, but a Postgres outage now halts the consumer.

**Redelivery is safe** because every handler is idempotent. A partial DocumentRerender run, with a new-parent enqueue done before a later Postgres read failed, re-enqueues harmlessly: `INSERT … ON CONFLICT DO NOTHING` on `ux_document_rerender_queue_entity`. Enrichment writes nothing before its dependency calls.

### 4. Stale documentation in a dependent

`Iverson.LoadTest/Scenarios/BenchmarkIngestScenario.cs:64-67` documents the old contract: the dispatcher dead-letters and returns normally, so lag reaches zero. Rewrite that comment to say:
- a transient dependency failure now halts the consumer rather than dead-lettering, so the drain loop waits out an outage;
- only non-transient failures still grow the DLQ high-watermark that the scenario checks.

No code change. The drain loop's lack of a deadline is now intended: waiting is the new behavior.

### 5. StarRocks backend-liveness rewrap

MySqlConnector marks only five error codes as transient, and the StarRocks circuit breaker counts failures with that same predicate (`StarRocksResiliencePipelineFactory.cs:19`). So a StarRocks outage that is not a refused connection neither classifies as transient nor opens the circuit. The classifier's text and code rules (§1) cover the known shapes. This rewrap covers shapes 1 and 2 without depending on message text.

In `EngagementRepository.RunAsync` (`EngagementRepository.cs:41-56`), when the pipeline throws a `MySqlException` for which both `IsTransient` and `IsExpectedMissingResourceError` are false:
1. run the existing `CheckBackendAliveAsync` (`:667-673`), bounded to 5 s;
2. if it returns false, throws or times out, throw `new EngagementNotReadyException("StarRocks backend unavailable", ex)`, which the classifier already treats as transient;
3. otherwise rethrow the original exception.

`IsExpectedMissingResourceError` is excluded because its catches (`:503`, `:544`, `:604`, `:636`) wrap `RunTenantScopedAsync`, which wraps `RunAsync`. Without the exclusion, every read of an unwritten type or unprovisioned tenant would pay a liveness round-trip.

**Behavior change:** during a backend outage, gRPC read paths now return `Unavailable` through their existing `EngagementNotReadyException` mappings (`ObjectSearchGrpcService.cs:114,788,836,914`, `ObjectSearchGrpcService.MatchPattern.cs:171`), instead of surfacing the raw StarRocks error.

## Testing

All unit tests use each project's existing fakes. No containers are needed.

- **`TransientFailuresTests` (new, `Iverson.Api.Tests`)** covers both directions.
  - **Transient:** `PostgresException` 57P03, 08006, 53300 and 40001; `NpgsqlException` wrapping a `SocketException`; `MySqlException` `UnableToConnectToHost`; `EngagementNotReadyException`; `RpcException` Unavailable and DeadlineExceeded; `HttpRequestException` with a null status, 503 or 429; `TaskCanceledException` wrapping `TimeoutException`; a transient exception as an `InnerException` of an `InvalidOperationException`; a transient exception inside an `AggregateException`.
  - **Transient, StarRocks shapes (§1):** `MySqlException` `CommandTimeoutExpired`; `MySqlException` whose message contains "Backend node not found"; `MySqlException` wrapping `MySqlEndOfStreamException`; `MySqlException` `ParseError` whose message contains `Unexpected input '@'`.
  - **Not transient:** `MySqlException` `ParseError` with "cannot find role", with "is not granted to", and with an ordinary syntax message; `MySqlException` 5502; `PostgresException` 42P01, 42601, 23505 and 22P02; `RpcException` InvalidArgument and NotFound; `HttpRequestException` 400 and 413; `PoisonMessageException`; `InvalidOperationException`; `JsonException`; `TaskCanceledException` without an inner `TimeoutException`.
- **`MessageDispatcherTests` (extended).** With the predicate:
  - a transient failure that exhausts its attempts rethrows the original exception, makes no DLQ produce call, and increments `consumer.transient_halts`;
  - a transient failure that succeeds on attempt 2 does not throw;
  - a non-transient failure still dead-letters.

  Without the predicate, the existing tests stay green unchanged.
- **`EngagementRepository` liveness rewrap (§5).** Against a fake liveness check:
  - a non-transient `MySqlException` with the check reporting dead, throwing, or exceeding the bound → `EngagementNotReadyException` wrapping the original;
  - the same exception with the check reporting alive → the original rethrown;
  - an `IsExpectedMissingResourceError` exception → rethrown, with no check made.
- **`KafkaConsumerTests` (one new test).** Using the injected fake consumer factory, a handler whose dispatch throws means `Commit` is never called and `ConsumeAsync` rethrows. No existing test covers this halt path.
- **Consumer tests.** Tests that inject an outage-shaped exception and assert `NotThrow` flip to asserting propagation:
  - `EnrichmentConsumerTests.HandleUpdated_WhenLlmFails_LeavesObjectIntactAndDoesNotThrow`, which injects `HttpRequestException("ollama down")`;
  - `PopularitySignalConsumerTests.Dispatch_SetPayloadAsyncThrowsOtherRpcException_IsCaughtBySignalLevelHandler`, which injects `RpcException(Unavailable)`.

  Each gains a non-transient sibling that still asserts `NotThrow`, so both directions are pinned per catch site. The existing non-transient tests (`…WhenWritebackFails…`, `UpdateAsync_AggregateThrows_ReturnsFailed`, `Dispatch_OneDependentThrows_OtherDependentsAreStillEnqueued`, all injecting `InvalidOperationException`) stay unchanged. The DocumentRerender and Popularity-updater sites gain one transient-propagates test each.
- **Redelivery** is not re-tested in the suite. The chain (dispatcher throws, no commit, loop restarts, broker redelivers) is covered piecewise above, and the broker leg was probed live.

## Known limitations (accepted)

- **Optional-input catches are deliberately untouched.** In these three places an outage degrades the output but loses no event:
  - `IntelligenceStoreConsumer.FetchSummaryAsync` (`:559-567`): the vector is written without the context summary.
  - `IntelligenceStoreConsumer.PrefixWithContextAsync` (`:594-600`): the vector is written without the context prefix.
  - `PopularitySignalUpdater`'s histogram catch (`:121`): the count is written with an empty series.
- **Log and rejoin churn during an outage.** Each ~13 s cycle, per affected consumer, logs three Critical lines, each carrying the full exception (the dispatcher's "Transient failure persisted", `KafkaConsumer`'s "Halting", and `ConsumerResilience`'s "Consumer loop faulted"), plus two retry Warnings, and causes one consumer-group rejoin.
- **Two StarRocks rules match message text:** "Backend node not found" and `Unexpected input '@'`. If StarRocks or MySqlConnector change that wording, the shape silently reverts to non-transient. The §5 liveness rewrap still covers shapes 1 and 2, but shape 3 would be lost.
- **Extra round-trip:** one FE liveness check per StarRocks failure that is neither transient nor an expected missing resource.
- **gRPC behavior change:** reads return `Unavailable` during a StarRocks backend outage (§5).
- **Misclassification has a cost in both directions.** A transient failure classified as permanent is still dead-lettered. A permanent failure classified as transient stalls its consumer until someone intervenes. The classifier test matrix is the guard.
- **HTTP timeouts reaching the dispatcher directly (Engagement, Intelligence: existing, unchanged):** an HTTP timeout is a `TaskCanceledException`, which is an `OperationCanceledException`. The dispatcher and `KafkaConsumer` already treat it as cancellation: the loop exits, `ConsumeAsync` returns, and `ConsumerResilience` restarts immediately with no 10 s delay. It is redelivered either way, with no halt log or `consumer.transient_halts` count. Enrichment no longer takes this path: its Ollama timeouts are skipped, logged and counted (`docs/specs/2026-10-01-enrichment-timeout-skip-design.md`).

## Out of scope

- The Postgres SIGPIPE crash's root cause.
- Automatic replay of DLQ rows that already exist.
- A Polly pipeline for Postgres, Qdrant or HTTP.
- A deadline for the benchmark drain loop.

## Verified assumptions

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
