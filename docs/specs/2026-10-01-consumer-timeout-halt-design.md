# Consumer Timeout Halt — Design

**Date:** 2026-10-01
**Status:** Approved design, assumptions verified
**Follows:** `docs/specs/2026-09-30-transient-projection-failures-design.md` (merged `97881b1e`) and `docs/specs/2026-10-01-enrichment-timeout-skip-design.md` (merged `cdbd7511`). The first spec's Known limitations recorded this gap.

## Problem

A handler's HTTP timeout surfaces as a `TaskCanceledException`, which is an `OperationCanceledException`. Both `MessageDispatcher.DispatchAsync` (`:74`) and `KafkaConsumer.ConsumeAsync` (`:69`, `:85`) catch `OperationCanceledException` without looking at the token, so a timeout is handled exactly like a shutdown:
- the dispatcher rethrows it with no retry, no halt log and no `consumer.transient_halts` count;
- `KafkaConsumer` turns it into a `break`, closes the consumer and returns normally;
- `ConsumerResilience` sees a normal return and restarts immediately, with no 10 s delay and no log.

The message is redelivered, so nothing is lost. But the outage is invisible, and the consumer re-hits a struggling dependency at once, rejoining its Kafka group on every cycle.

**Who is affected.** Only `IntelligenceStoreConsumer`. Its embedding calls (`EmbedDocumentAsync`) go over HTTP on a client with .NET's default 100 s timeout.
- Engagement makes no HTTP calls. It reaches StarRocks through MySqlConnector, whose timeouts are `MySqlException`s that already take the classified path. The transient-failures spec was wrong to list it.
- Enrichment catches its own Ollama timeouts before they reach the dispatcher (the enrichment-timeout-skip design).
- Intelligence's contextual-prefix call to Ollama swallows every failure and embeds the chunk unprefixed, so it is unaffected.
- Popularity and DocumentRerender make no HTTP calls.

## Goal (user decision)

An Intelligence embedding timeout takes the same visible halt-and-backoff path as every other transient outage:
- a Critical halt log;
- a `consumer.transient_halts` count;
- the 10 s restart delay;
- redelivery, with nothing lost.

Skipping is not an option, because a skipped object would be left with no vectors.

## Approach (chosen: B — halt at once)

A cancellation that is not a shutdown skips the dispatcher's in-place retries and halts immediately. It is never dead-lettered, whatever its inner exception.

Rejected: **A — timeouts enter the normal retry loop** (3 attempts, then the classifier decides).
- With a 100 s HTTP timeout and 1 s + 2 s backoff, a persistently unresponsive dependency costs 303 s before the halt. That exceeds Kafka's 300 s `max.poll.interval.ms`, so the consumer would be evicted from its group mid-message.
- A cancellation the classifier calls non-transient (a plain `TaskCanceledException` with no `TimeoutException`) would be dead-lettered after three attempts. That loses the object's vectors, contradicting the transient-failures goal of no loss.
- A capped variant (two attempts, at most 201 s) avoids the eviction but needs a separate attempt limit for timeouts, and it still dead-letters plain cancellations.

The cost of B: a single slow request costs a halt, which is the 10 s restart delay and a group rejoin, where A would have retried after 1 s. The halt path closes the Kafka consumer before rethrowing, so the restarted consumer rejoins at once instead of waiting out `session.timeout.ms`; see §1.

## Design

### 1. Behaviour

In `Iverson.Server/Iverson.Events/MessageDispatcher.cs`, `DispatchAsync`, the cancellation catch splits in two:

```csharp
catch (OperationCanceledException) when (ct.IsCancellationRequested)
{
    throw;
}
catch (OperationCanceledException ex)
{
    // Cancelled while the consumer's token is live: a dependency timeout (HttpClient surfaces its
    // timeout as a TaskCanceledException). Not a bad message, so never dead-lettered; not retried in
    // place, because one attempt can take HttpClient's 100 s and three would pass Kafka's 300 s
    // max.poll.interval.ms.
    logger.LogCritical(ex,
        "[Dispatch] Handler cancelled while the consumer is running (a dependency timeout) topic={Topic} key={Key} — not dead-lettering; halting for redelivery",
        ctx.SourceTopic, ctx.Key);
    Telemetry.ConsumerTransientHalts.Add(1);
    throw;
}
```

The class doc's delivery-contract list gains one bullet for this case.

In `Iverson.Server/Iverson.Events/KafkaConsumer.cs`, `ConsumeAsync`, both cancellation catches gain `when (cancellationToken.IsCancellationRequested)`:
- the inner `catch (OperationCanceledException) { throw; }` around the dispatch (`:69`);
- the outer `catch (OperationCanceledException) { break; }` (`:85`).

A timeout then reaches the existing inner `catch (Exception)`. That catch logs the halt, does not commit, and rethrows. The exception propagates out of `ConsumeAsync`, because neither outer catch matches it. `ConsumerResilience` already logs any non-shutdown exception as Critical, waits 10 s and restarts, and Kafka redelivers the message.

That halt catch now also closes the consumer before rethrowing, guarded so a failing `Close()` logs a Warning and never replaces the original exception. A consumer that is only disposed holds its partitions until the old member's `session.timeout.ms` (45 s by default) expires. This applies to every halt (timeouts, classified transient outages and DLQ-write failures). `Close()` commits nothing, because auto-commit is off.

`consumer.transient_halts` carries two tags: `consumer.group` (the consumer group) and `reason`, which is `timeout` for a cancellation while the token is live and `transient` for a classified outage that exhausted its attempts.

| Failure in a handler | Today | After |
|---|---|---|
| HTTP timeout (Intelligence embedding) | immediate silent restart: no retry, no delay, no count | 1 attempt, Critical halt log, `consumer.transient_halts` +1, 10 s delay, redelivery |
| Any other cancellation while the token is live | as above | as above; never dead-lettered |
| Shutdown cancellation | quiet exit | unchanged |
| Transient outage (classified) | 3 attempts, then halt | unchanged |
| Ordinary failure | 3 attempts, then dead-letter | unchanged |
| Enrichment's Ollama timeout | skipped by its own catch | unchanged: never reaches the dispatcher |

Unchanged:
- `ConsumeRawAsync`, used only by the DLQ monitor, whose handler makes no HTTP calls;
- `ConsumerResilience`;
- the embedding client's 100 s timeout.

### 2. Docs

`docs/specs/2026-09-30-transient-projection-failures-design.md`:
- §3's dispatcher bullet: "`OperationCanceledException` still rethrows immediately" now says it rethrows immediately on shutdown, and that a cancellation while the token is live halts (pointing here).
- Known limitations, the bullet "HTTP timeouts reaching the dispatcher directly": replaced. Engagement makes no HTTP calls, and Intelligence's embedding timeouts now halt (pointing here).

`docs/specs/2026-10-01-enrichment-timeout-skip-design.md`:
- Known limitations, "Engagement and Intelligence timeouts are unchanged": now points here. Engagement has no HTTP timeouts, and Intelligence's now halt, are counted and back off.

## Testing

Unit tests only, following each file's existing setup.

`Iverson.Server/Iverson.Events.Tests/MessageDispatcherTests.cs`:
1. **A timeout halts at once.** The handler throws `new TaskCanceledException("timed out", new TimeoutException())` with a live token. `DispatchAsync` throws; the handler ran once; nothing was produced to the DLQ; `consumer.transient_halts` is 1.
2. **A plain cancellation is never dead-lettered.** The same, with `new TaskCanceledException("cancelled")` (no inner exception): it throws, and nothing was produced to the DLQ.
3. **Shutdown is unchanged.** The handler cancels the token and throws. `DispatchAsync` rethrows, the handler ran once, and `consumer.transient_halts` is 0.
4. **End to end through `KafkaConsumer`.** A fake consumer delivers one message and the handler times out with a live token. `ConsumeAsync` throws, and `Commit` is never called.

All four live in `MessageDispatcherTests`, not `KafkaConsumerTests`. Tests that touch the dispatcher's counters already share this class so they never run in parallel with its `MeterListener` assertions (`MessageDispatcherTests.cs:289-291`), and each of the four increments `consumer.transient_halts` or asserts on it.

`Iverson.Server/Iverson.Events.Tests/KafkaConsumerTests.cs`:
5. **A shutdown during the handler** makes `ConsumeAsync` return normally, without a commit. This path increments no counter.
6. **The shared fake now behaves like the real client.** `CreateConsumer`'s fake `Consume` throws `OperationCanceledException` to end the loop. It must now cancel a real `CancellationTokenSource` first, and the tests that pass `CancellationToken.None` (`ConsumeAsync_UsesInjectedConsumerFactory_NotAConcreteConfluentClient`, `ConsumeAsync_UsesInjectedAdminClientFactory_ToEnsureTopicExists`) pass that source's token instead. Otherwise the filtered outer catch lets the exception escape and both tests fail (probed: 35 passed, 2 failed).

## Known limitations (accepted)

- **A one-off slow request costs a restart.** One embedding call that exceeds the embedding timeout halts the consumer, waits 10 s and rejoins the group, where an in-place retry might have succeeded after 1 s.
- **Three Critical lines per halt.** The dispatcher, `KafkaConsumer` and `ConsumerResilience` each log the halt, as they already do for every other transient halt.
- **A shutdown racing a timeout looks like a shutdown.** If the token is cancelled after the timeout fires but before the dispatcher's filter runs, the exit is quiet. Nothing is committed, so the message is redelivered on the next start.
- **A document whose embedding always times out halts on every redelivery.** Raising `Embeddings:Timeout` (`Embeddings__Timeout`; default 100 s, the `HttpClient` default) is the operator's lever. Until it is raised, that partition makes no progress, and the object's chunk points (already deleted before re-embedding) stay missing.

## Out of scope

- Changing the embedding client's 100 s default. It is now configurable as `Embeddings:Timeout`, mirroring `Enrichment:Timeout`.
- `ConsumeRawAsync` and the DLQ monitor.
- Retrying timeouts in place.

## Verified assumptions

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
| 16 | A consumer that only disposes holds its partitions until `session.timeout.ms` expires; `Close()` releases them at once. | Probe, Confluent.Kafka 2.15.1 against a throwaway `cp-kafka:7.6.0` broker: after consumer A consumed without committing, a new consumer in the same group got its first message 190 ms after A's `Close()`, and 44,995 ms after A's `Dispose()` alone. |
| 17 | `Close()` is bounded when the broker is down. | Same probe: `Close()` returned after 5,006 ms with the broker `docker pause`d, and after 2 ms with the broker never reachable; neither threw. |
