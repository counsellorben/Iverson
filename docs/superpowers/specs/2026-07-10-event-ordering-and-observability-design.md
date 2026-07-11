# Event Ordering Fix + Observability Design

**Date:** 2026-07-10
**Origin:** `/system-architectural-review` — Critical Finding 1 and the two Tactical Findings.

## Problem

### Critical Finding 1: cross-topic consumer race can resurrect deleted entities

`EngagementStoreConsumer` and `IntelligenceStoreConsumer` each run three independent,
concurrently-polling Kafka consumption loops: one on `iverson.entity.created`, one on
`iverson.entity.updated` (both under the same consumer group), and one on
`iverson.entity.deleted` under a *separate* consumer group (`GroupId + ".delete"`). Kafka's
per-partition ordering guarantee does not extend across topics, even for messages sharing the
same key. A rapid create→delete or update→delete on the same entity has no ordering guarantee
between the delete-topic loop and the create/update-topic loop: if the delete is applied first
(a no-op, since the row doesn't exist yet) and the create/update is applied afterward, the entity
is **permanently resurrected** in StarRocks and/or Qdrant, silently — no error, no DLQ entry,
nothing that would surface in logs or (today) metrics. Postgres, the system of record, correctly
shows the entity deleted; the two read projections do not. The only fix mechanism is a manually
triggered, full-table `POST /admin/reconcile/{typeName}` — there is no scheduled sweep.

### Tactical findings: metrics defined but never exported, no backlog visibility

`Program.cs`'s `AddOpenTelemetry()` configures `.WithTracing(...)` only — no `.WithMetrics(...)`.
The two counters that exist (`consumer.retries`, `consumer.dlq_routed` in
`Iverson.Events.Telemetry`) are created but never exported anywhere, and there is no metrics
backend deployed in this stack at all (Jaeger ingests traces only). There is also no
queue-depth/backlog signal for the reconciliation outbox or the DLQ table — the earliest
indicator that opportunistic-publish is failing systematically, or that Finding 1's race is
actively causing drift.

## Design

### Part 1 — Single ordered event topic

**Event envelope.** `EntityEvent` (`Iverson.Events/EntityEvent.cs`) gains an `EventType` field:

```csharp
public enum EntityEventType { Created, Updated, Deleted }

public sealed record EntityEvent(
    EntityEventType EventType,
    string          TypeName,
    string          Key,
    string          PayloadJson,
    string          TraceId,
    string          SchemaVersion,
    DateTimeOffset  OccurredAt,
    StoreTarget     TargetStores = StoreTarget.All);
```

All other fields are unchanged in meaning — for `Deleted`, `PayloadJson` continues to carry the
pre-delete snapshot exactly as today (used for outbox delete-replay and DLQ replay).

**Topic collapse.** `EntityTopics.Created`/`Updated`/`Deleted` (3 topics) become a single topic:

```csharp
public static class EntityTopics
{
    public const string Events = "iverson.entity.events";
    public const string Dlq    = "iverson.entity.dlq";
}
```

Still keyed by entity key (unchanged partitioning), so Kafka's per-partition ordering now
actually covers create/update/delete for the same entity — the race is closed at the source.

**Producers** — every call site that currently produces to `EntityTopics.Created/Updated/Deleted`
switches to `EntityTopics.Events` with the matching `EventType` set:
- `Iverson.Api/Grpc/ObjectMappingGrpcService.cs`: `Post` (Created), `Update` (Updated), `Delete`
  (Deleted).
- `Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs`: both upsert paths (Updated — matches
  existing behavior, these are upsert-style writes that already publish to `Updated` today).
- `Iverson.Api/Reconciliation/ReconciliationService.cs`: `ReconcileTypeAsync`'s full-table replay
  (Updated), `ProcessOneAsync`'s per-row retry (Updated), the delete-replay path (Deleted).

No other change to these call sites — the outbox-write-then-opportunistic-publish pattern, the
`DeleteOutboxRowIfPresentAsync` cleanup, and the warning-log-on-failure behavior are all
unchanged.

**Consumers.** `EngagementStoreConsumer` and `IntelligenceStoreConsumer` each collapse from 2
subscriptions/2 consumer groups down to **1** subscription on `EntityTopics.Events` under the
existing primary `GroupId` (the `".delete"`-suffixed group is retired). Each consumer's
`ExecuteAsync` becomes a single `consumer.ConsumeAsync(EntityTopics.Events, GroupId, HandleAsync, ct)`
(no more `Task.WhenAll` of three loops). A single `HandleAsync` dispatches on `ev.EventType`:

```csharp
internal async Task HandleAsync(string key, string value, CancellationToken ct)
{
    var ev = Deserialize(key, value);
    if (!ev.TargetStores.HasFlag(StoreTarget.Engagement)) return;   // (or .Intelligence)

    switch (ev.EventType)
    {
        case EntityEventType.Created:
        case EntityEventType.Updated:
            await HandleUpsertAsync(ev, ct);   // existing logic, unchanged
            break;
        case EntityEventType.Deleted:
            await HandleDeleteAsync(ev, ct);   // existing logic, unchanged
            break;
    }
}
```

The existing `HandleUpsertAsync`/`HandleDeleteAsync` (Engagement) and `HandleAsync`/
`HandleDeleteAsync` (Intelligence) method *bodies* are untouched — only how they're reached
changes. `MessageDispatcher`/DLQ routing is unaffected (it operates per-message, keyed off
`DispatchContext.SourceTopic`, which will now just read `iverson.entity.events`).

**Cutover.** Coordinated deploy: producers and consumers ship together in one release. The new
topic auto-creates on first produce (`KAFKA_AUTO_CREATE_TOPICS_ENABLE=true`, 12 partitions,
matching current behavior). The three old topics simply stop receiving messages and age out via
normal Kafka retention — no application code deletes them; manual/infra cleanup is a deferred,
out-of-band operational step, not a plan task.

**Cutover gap: drain the old topics before stopping their consumers.** The outbox fast path
(`ObjectMappingGrpcService`/`ObjectPersistenceGrpcService`) deletes its backstop
`IversonOutbox` row on successful **produce**, not successful **consume**. During the rolling
(or Recreate-with-a-drain-gap) deploy, a not-yet-upgraded pod can still produce a message to an
old topic (`iverson.entity.created`/`updated`/`deleted`), succeed, and delete its outbox row —
and once the last old-topic consumer is stopped, that message has no path forward: new consumers
subscribe only to `iverson.entity.events`, and reconciliation can't recover it because the
outbox-row backstop that would normally let it be re-derived is already gone. For a `Deleted`
event this is exactly the transient-resurrection failure class this branch exists to close, so
the operational runbook for this cutover MUST include:
- **Required:** before stopping the old consumer processes, drain all three old topics
  (`iverson.entity.created`/`updated`/`deleted`) to zero consumer lag, confirmed via the same
  `ListConsumerGroupOffsetsAsync` lag check the write-path load test uses. This is the only step
  that closes the gap for a stranded `Deleted` event.
- **Supplementary, not a substitute:** running a full `POST /admin/reconcile/{typeName}` for
  every registered type immediately after cutover is a cheap, idempotent way to re-publish
  every row's *current* Postgres state to the new topic, which repairs any `Created`/`Updated`
  message that was stranded on an old topic. It does **not** cover a stranded `Deleted`: once a
  row is gone from Postgres, `ReconcileTypeAsync`'s full-table replay has nothing left to
  re-publish for that key, so a tombstone that never reached a consumer stays lost. (The
  delete-replay path in `ReconciliationService.ProcessDeleteRowAsync` only fires off the queued
  outbox-failure worker, not this manual endpoint.) Treat the reconcile call as a mop-up for
  upserts, not a substitute for verifying the drain.

### Part 2 — Metrics export + backlog visibility

**Wire up existing counters.** `Program.cs`'s `AddOpenTelemetry()` gains a `.WithMetrics(...)`
builder registering the `Iverson.Events` meter (already emitting `consumer.retries`/
`consumer.dlq_routed`) plus ASP.NET Core/HttpClient instrumentation. Add the
`OpenTelemetry.Exporter.Prometheus.AspNetCore` package and expose `/metrics` on both `api` and
`worker` pods via `app.MapPrometheusScrapingEndpoint()`.

**New backlog gauges:**
- `reconciliation.queue_depth` (`ObservableGauge<int>`) — total pending rows in
  `IversonReconciliationQueue`. Requires a new `IReconciliationQueueRepository.CountPendingAsync()`
  (a plain `SELECT COUNT(*)`, sibling to the existing `CountExhaustedAsync(maxAttempts)`).
- `dlq.unreplayed_count` (`ObservableGauge<int>`) — total unreplayed rows in
  `IversonDlqMessages`. Requires a new `IDlqRepository.CountUnreplayedAsync()`.

Both gauges are backed by a `volatile` field refreshed on a 30-second cadence:
`ReconciliationQueueWorker`'s existing poll loop refreshes `reconciliation.queue_depth` as part of
its existing cycle (no new query pressure beyond one extra `COUNT(*)`); a small new sibling
background loop (same 30s interval, same `ConsumerResilience.RunWithRestartAsync` wrapper pattern)
refreshes `dlq.unreplayed_count`, since `DlqMonitorConsumer` has no periodic loop of its own today.

**New Prometheus subchart** (`deploy/helm/iverson/charts/prometheus/`), matching this repo's
existing hand-rolled pattern (see `charts/jaeger/`: `Deployment` + `Service`, no operator, no
upstream chart dependency):
- `Deployment`: single replica, `prom/prometheus` image, scrape config mounted from a `ConfigMap`
  targeting the `api`/`worker` Services' `/metrics` port.
- `Service`: exposes Prometheus's own built-in query UI (port-forward or ingress — no Grafana).
- A small `PersistentVolumeClaim` for the TSDB, sized similarly to Jaeger's.
- Wired into top-level `values.yaml` the same way `jaeger:` is today (image tag, resources,
  nodeSelector/tolerations).

**Local dev parity.** `docker-compose.yml` gains a `prometheus` service (`prom/prometheus` image,
mounted scrape-config file targeting `iverson-api:8080/metrics`, its own named volume,
`restart: unless-stopped` + healthcheck, matching the existing infra-service pattern). No
`iverson-worker` compose service exists today (a separate, pre-existing gap, out of scope here) —
the local scrape target is `iverson-api` only.

**Explicitly out of scope:** Grafana, Alertmanager, alerting rules/thresholds, dashboards, and any
drift-detection instrumentation beyond the two backlog gauges above. This closes the "data isn't
flowing anywhere" gap; deciding what to alert on is a separate, product-judgment follow-up.

## Testing

- **Part 1:** unit tests for the collapsed consumer dispatch (`HandleAsync` routes each
  `EntityEventType` to the correct existing handler — reuse existing `HandleUpsertAsync`/
  `HandleDeleteAsync` test coverage, just retarget the entry point). An integration test
  demonstrating the fix: produce a Created then a Deleted for the same key *out of order* onto the
  single topic's same partition and assert the consumer applies them in produced order (this is
  the regression test for the bug this plan fixes — it should have been impossible to write
  meaningfully against the old 3-topic layout, which is itself evidence of the gap).
- **Part 2:** unit tests for the two new repository count methods (mirroring existing
  `CountExhaustedAsync`/`ListUnreplayedAsync` test patterns). A live-container smoke check that
  `/metrics` responds and contains the expected metric names is reasonable; deep Prometheus
  scrape-correctness testing is not — the Helm chart itself is infra, not app logic.

## Self-review

- **Placeholder scan:** no TBD/TODO; every call site and file path named explicitly.
- **Internal consistency:** Part 1's consumer collapse and Part 2's gauge-refresh loops don't
  interact — different files, different concerns. `ReconciliationService`'s 3 producer call
  sites (Part 1) are unaffected by its new `CountPendingAsync` consumer (Part 2, read-only).
- **Scope:** two related but separable parts, appropriately sized for one implementation plan
  (not decomposed further) — consistent with this project's existing multi-part-plan precedent.
- **Ambiguity check:** "coordinated cutover" is explicit (no dual-write machinery); "full stack"
  is scoped explicitly to Prometheus only, no Grafana/Alertmanager, matching the hand-rolled
  subchart pattern rather than an operator/kube-prometheus-stack dependency.
