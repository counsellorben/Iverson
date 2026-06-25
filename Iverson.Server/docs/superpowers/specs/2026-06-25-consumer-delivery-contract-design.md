# Consumer Delivery Contract — Design

**Date:** 2026-06-25
**Status:** Approved (pending implementation plan)
**Scope:** `Iverson.Events` (shared Kafka consumer loop) + the three projection consumers in `Iverson.Api/Consumers`.

## Problem

The event-projection pipeline loses data silently, and does so inconsistently across consumers.

The shared loop commits the Kafka offset immediately after the handler returns
([KafkaConsumer.cs:52-53](../../../Iverson.Events/KafkaConsumer.cs#L52-L53)):

- `RecordStoreConsumer` and `IntelligenceStoreConsumer` catch their own projection
  exceptions, log, and return normally → the offset is committed even though the
  write failed → the event is **permanently skipped** (at-most-once, silent loss).
- `EngagementStoreConsumer` does **not** catch its store exceptions → they
  propagate, fault the consumer task, and halt the engagement projection;
  on restart the poison message head-of-line-blocks the partition.

The `iverson.entity.dlq` topic is declared ([EntityEvent.cs:28](../../../Iverson.Events/EntityEvent.cs#L28))
but never produced to. There is no retry, no dead-lettering, and no metric that
would reveal any of this.

## Goals

1. No silent loss: a projection failure is either retried to success, or
   preserved in the DLQ — never dropped by an offset commit.
2. One uniform contract for all three consumers.
3. A poison message must not halt a partition forever.
4. Make the failure path observable.

Non-goals: cross-store reconciliation/backfill (separate finding), true
consumer-lag metrics, producer idempotence hardening, authn/authz.

## Design

### 1. The contract lives in the shared `KafkaConsumer` loop

The retry → DLQ → commit policy moves into `KafkaConsumer.ConsumeAsync` — the
single point every consumer routes through. Projection handlers return to doing
one thing (project) and signal failure by **throwing**. This removes the
per-consumer divergence by construction.

### 2. Two failure classes

- **Transient** — any ordinary exception from a store/embedding write. Retried
  in-process: **3 attempts, exponential backoff (1s, 2s, 4s)**, then DLQ + commit.
- **Poison** — deterministic failure. A new `PoisonMessageException` (defined in
  `Iverson.Events`) **bypasses retry** and goes straight to DLQ + commit.
  Handlers throw it for deserialize failures and for a `null` deserialized event.

`schema-not-found` remains a **logged drop that commits** (unchanged) — it is a
transient registration/ordering condition, not a projection failure. The loop
cannot distinguish an intentional drop from a success, and does not need to:
both commit.

### 3. Commit semantics (the core fix)

```
consume → handler attempt
  success             → commit
  PoisonMessageEx     → DLQ → commit
  transient, n < 3    → backoff(n) → retry
  transient, n == 3   → DLQ → commit
  DLQ produce fails   → DO NOT commit; log critical; surface (halt rather than lose)
```

The offset is committed **only** after a successful projection or a successful
DLQ hand-off. If the DLQ write itself fails (e.g. Kafka unavailable), nothing is
committed; the message remains for reprocessing. This is the deliberate inversion
of today's "commit regardless of outcome."

### 4. DLQ message shape

Produced to the existing `iverson.entity.dlq` topic:

- **Key**: original message key (entity id).
- **Value**: the original event JSON, **verbatim** — so a DLQ message can be
  replayed onto its source topic without unwrapping.
- **Headers**: `dlq.source_topic`, `dlq.consumer_group`, `dlq.exception_type`,
  `dlq.exception_message` (truncated), `dlq.attempts`, `dlq.failed_at`, and the
  propagated `traceparent` (so the DLQ hop stays on the original trace).

To attach headers, `KafkaConsumer` uses the already-registered
`IProducer<string,string>` singleton directly (thread-safe; designed for
concurrent use). `KafkaProducer` is left untouched.

### 5. Consumer edits

| Consumer | Change |
|----------|--------|
| `RecordStoreConsumer` | Remove the try/catch that swallows the upsert/delete exception (let it throw). Deserialize failure → throw `PoisonMessageException`. Keep schema-not-found as a logged drop. |
| `IntelligenceStoreConsumer` | Remove the swallowing catches around embedding/vector writes (let them throw). Per-field embedding failures now **propagate** (no silent partial vector writes). Deserialize failure → throw `PoisonMessageException`. |
| `EngagementStoreConsumer` | Already throws on store failure — only the deserialize path changes to throw `PoisonMessageException`. |

### 6. Observability

- A `Meter "Iverson.Events"` exposing `Counter<long>` `consumer.retries` and
  `consumer.dlq_routed`.
- Span tags on the consume activity: `messaging.retry_count`, `messaging.dlq`
  (bool), `messaging.dlq.reason`.
- Structured logs: WARN on each retry, CRITICAL on DLQ routing and on DLQ-write
  failure.

**Infra caveat (accepted):** the stack has no metrics backend — Jaeger is
traces-only — so the `Meter`/counters are added for a future metrics scraper and
for unit assertions, but **no OTLP metrics exporter is wired** (it would be
discarded by Jaeger). Visibility *today* comes from span tags + logs. True
consumer-lag is deferred.

### 7. Testing

The per-message decision is extracted into an internal, testable unit
(e.g. `ProcessMessageAsync` returning a `Committed` / `DeadLettered` outcome).
The infinite `Consume()` loop stays a thin shell around it.

Unit tests (in `Iverson.Events.Tests`, with a substituted `IProducer`/handler and
**injectable backoff** so tests run instantly) cover:

- success → commit, no DLQ;
- transient failure recovered on attempt 2 → commit, no DLQ, `retries` counted;
- transient failure exhausting 3 attempts → DLQ once + commit, `dlq_routed` counted;
- `PoisonMessageException` → DLQ immediately, **no** retries;
- DLQ-produce failure → not committed, surfaces;
- DLQ headers carry source topic, attempts, exception type, and traceparent.

This gives `Iverson.Events` its first coverage of the delivery contract.

## Risks / Trade-offs

- Bounded in-process retry briefly **head-of-line-blocks** the partition during
  backoff. Acceptable: with single-partition topics today there is no parallelism
  to lose, and the cap is ~7s before dead-lettering.
- Reusing the singleton producer for DLQ writes couples the consumer to a producer
  instance; mitigated because `IProducer` is thread-safe and already a singleton.
- Intelligence's per-field propagation means one bad field now fails the whole
  event (retried, then DLQ'd) rather than writing a partial vector set — chosen
  deliberately to avoid silent corruption.
