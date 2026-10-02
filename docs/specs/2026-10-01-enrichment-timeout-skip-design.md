# Enrichment Timeout Skip — Design

**Date:** 2026-10-01
**Status:** Approved design, assumptions verified
**Follows:** `docs/specs/2026-09-30-transient-projection-failures-design.md` (merged `97881b1e`), whose Known limitations recorded this gap.

## Problem

After the transient-projection-failures change, an Ollama HTTP timeout in `EnrichmentConsumer` is classified as transient, so it escapes Enrichment's catch.

That timeout is a `TaskCanceledException`. The dispatcher's untyped `catch (OperationCanceledException) { throw; }` rethrows it before classification, so:
- `KafkaConsumer` exits its loop without committing;
- `ConsumerResilience` restarts immediately;
- the message is redelivered.

That has two consequences:

1. **Invisible.** There is no retry, no halt log, no `consumer.transient_halts` count, and no restart delay. The only trace is `EnrichmentService`'s generic `GenerateAsync failed` error log.
2. **Blocking.** A document whose generation always times out (for example, one too large for Ollama to finish within the 2-minute client timeout) is redelivered forever at the head of its partition. Every enrichment behind it is stuck.

Before that change, Enrichment's catch-all swallowed the timeout and moved on.

## Goal (user decision)

Enrichment timeouts are both:
- **visible** to operators, with a log naming the object and a counter;
- **never blocking:** a document that always times out cannot stall the consumer.

Genuine outages still halt and redeliver:
- Ollama unreachable, 5xx or 429;
- Postgres down, including Npgsql command timeouts.

## Approach (chosen: A — skip timeouts visibly)

Enrichment treats an Ollama HTTP timeout as best-effort again. The object is skipped with no state row, and the offset commits. The skip is logged with the object's key and the recovery path, and counted.

Rejected alternatives:
- **Retry, then dead-letter timeouts at the dispatcher.**
  - Each pathological message holds the partition for 3 × 2 min, which exceeds Kafka's 300 s `max.poll.interval.ms`.
  - The dispatcher is shared, so Intelligence's TEI timeouts would start dead-lettering vectors. That reopens the loss path the previous change closed, unless the dispatcher grows a per-group policy.
- **Count timeouts per object and skip after N.** This needs a schema change and a new state machine for a best-effort feature, and it adds precision only for the Ollama-overload case, which reconciliation already recovers.

## Design

### 1. Behaviour

In `Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs`, `HandleAsync`, add one catch clause **before** the existing `catch (Exception ex) when (!TransientFailures.IsTransient(ex))` that ends the generate / write-back / republish try block:

```csharp
catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException
                                       || (ex.InnerException is HttpRequestException && !ct.IsCancellationRequested))
{
    ReconciliationTelemetry.EnrichmentTimeoutsSkipped.Add(1);
    logger.LogWarning(
        "[Enrichment] Ollama timed out for {Type}:{Key} — skipped without writing a state row; the next change to the object, or a reconcile of the type (POST /admin/reconcile/<type>), re-enriches it.",
        schema.TypeName.SanitizeForLog(), ev.Key);
}
```

**The rule is deliberately narrow.** It matches `TaskCanceledException` whose *direct* inner exception is `TimeoutException`. That is the exact shape of an `HttpClient` timeout. A timeout that races a connection failure arrives instead as `TaskCanceledException > HttpRequestException`, which a shutdown cancellation racing a connection failure can produce too, so that shape is matched only while the caller's token is live. A plain caller-token cancellation (shutdown) has no `TimeoutException` inside, and an Npgsql command timeout is an `NpgsqlException`, not a `TaskCanceledException`. Within this try block, the only HTTP calls are to Ollama (`EnrichmentService.GenerateAsync` and `GenerateJsonAsync`).

| Failure inside the try block | Handling |
|---|---|
| Ollama HTTP timeout | skipped: logged at Warning (no stack trace — `EnrichmentService` already logged it at Error), counted, no state row written, offset commits |
| Ollama unreachable, 5xx or 429 | halt and redeliver (unchanged from the merged change) |
| Postgres outage, including an Npgsql command timeout | halt and redeliver (unchanged) |
| Shutdown cancellation (caller token) | unchanged: not matched by the new clause, including when it races a connection failure |
| Other non-transient failures (bad LLM output, size limit) | unchanged: best-effort, logged, skipped |

**Recovery for a skipped object.** It has no state row, so its source hash never matches. The next `Created` or `Updated` event re-enriches it. So does `POST /admin/reconcile/{typeName}`, which replays an `Updated` event for every row of the type.

### 2. Visibility

`Iverson.Server/Iverson.Api/Reconciliation/ReconciliationTelemetry.cs` gains:

```csharp
internal static readonly Counter<long> EnrichmentTimeoutsSkipped = Meter.CreateCounter<long>(
    "enrichment.timeouts_skipped",
    description: "Enrichment generations skipped after an Ollama HTTP timeout; recover with the next change or POST /admin/reconcile/{type}");
```

- **Declaration order:** the field is declared *after* the `Meter` field, because static field initializers run in source order.
- **Naming:** it follows the `Field = Meter.CreateCounter<long>("name"` shape that `AdminConsoleMetricsSeriesNamesTests` parses.
- **Class doc:** the summary gains one line naming this signal.
- **Export:** the meter `Iverson.Api.Reconciliation` is already exported (`Program.cs:74`), so no wiring changes. Prometheus sees `enrichment_timeouts_skipped_total`.

### 3. Docs

Two bullets in `docs/specs/2026-09-30-transient-projection-failures-design.md` are updated to point here:
- **§3, the `EnrichmentConsumer` bullet:** "An Ollama timeout … takes the cancellation path described under Known limitations" becomes "An Ollama timeout is skipped, logged and counted (see the enrichment-timeout-skip design)".
- **Known limitations, the "HTTP timeouts reaching the dispatcher directly" bullet:** drop Enrichment from it. It stays true for Engagement and Intelligence.

## Testing

All tests go in `Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs`, following that file's existing setup.

1. **An Ollama timeout is skipped.**
   - `GenerateAsync` throws `new TaskCanceledException("timeout", new TimeoutException())`.
   - The handler must not throw.
   - Nothing is written: no state upsert, no `UpdateColumns`, no outbox row, no publish.
2. **The counter increments.** The same test observes `enrichment.timeouts_skipped` with a `MeterListener` and expects exactly 1. This is the pattern `Iverson.Events.Tests/MessageDispatcherTests.cs` uses; it is new to `Iverson.Api.Tests`, and `MeterListener` is part of the base class library.
3. **An Npgsql command timeout still propagates.**
   - The write-back transaction throws `new NpgsqlException("timeout", new TimeoutException())`.
   - The handler throws.
   - This pins that the rule is HTTP-only.
4. **Unchanged:** `HandleUpdated_WhenLlmUnreachable_PropagatesForRedeliveryAndWritesNothing`, which pins that a refused Ollama connection still halts.
5. **An extraction timeout is skipped.** `HandleUpdated_WhenExtractionTimesOut_SkipsWithoutWritingAndCountsTheSkip`: `GenerateJsonAsync` throws the timeout shape; nothing is written and the skip is counted once.
6. **A timeout that races a connection failure is skipped.** `HandleUpdated_WhenTimeoutRacesAConnectionFailure_SkipsWithoutWritingAndCountsTheSkip`: `GenerateAsync` throws `TaskCanceledException > HttpRequestException` with a live caller token; the handler does not throw, the skip is counted once, nothing is written.
7. **A shutdown that races a connection failure propagates uncounted.** `HandleUpdated_WhenShutdownRacesAConnectionFailure_PropagatesUncounted`: the same exception shape, thrown after the stub cancels the caller's token; the handler throws and the counter stays 0.
8. **One Warning per skip, with the key and no exception.** `HandleUpdated_WhenLlmTimesOut_LogsOneWarningWithTheKeyAndNoException`: a logger spy receives exactly one Warning containing the key, with a null exception.

## Known limitations (accepted)

- **During an Ollama overload, objects stay unenriched until recovered.** While Ollama is slow enough to time out, every object it times out on is skipped and stays unenriched until its next change or a reconcile. That is the best-effort contract Enrichment had before the transient change, now with a per-object Warning and a counter.
- **A skip is not a lasting fix.** A document that always times out is skipped on every event for it, and is never enriched until its source text changes.
- **Engagement and Intelligence timeouts are not skipped.** Engagement makes no HTTP calls, and Intelligence's embedding timeouts halt, are counted and back off (`docs/specs/2026-10-01-consumer-timeout-halt-design.md`). "Never block" doesn't apply to them, because skipping would drop vectors or projections, which are not best-effort.

## Out of scope

- Routing any consumer's HTTP timeout through the dispatcher's classified halt path.
- Changing the Ollama `HttpClient` timeout (`EnrichmentServiceOptions.Timeout`, 2 min).
- A dedicated enrichment backfill sweep.

## Verified assumptions

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
| 11 | When the handler fails with an `HttpRequestException` after the request's cancellation fired, `HttpClient` surfaces `TaskCanceledException > HttpRequestException` with no `TimeoutException`, for a timeout (caller token live) and for a caller cancel alike. | Probe on .NET 10.0.112: an `HttpMessageHandler` that waits on its token and then throws `HttpRequestException` gives "`TaskCanceledException > HttpRequestException`" with `Timeout` = 200 ms and a live caller token, and the same chain with a caller token cancelled at 200 ms. |
| 12 | `EnrichmentService` logs every generation failure with its exception at Error before rethrowing, so the consumer's Warning need not carry the exception. | `EnrichmentService.cs`, the `catch (Exception ex)` → `logger.LogError(ex, "GenerateAsync failed for model {Model}", ModelId); throw;` (`GenerateJsonAsync` goes through the same method). |
