# Abandoning the popularity reconciliation sweep under a persistent engagement-store fault

**Status:** design, not yet planned or implemented.
**Fixes:** the follow-up carried as production-gating since the relation-popularity signal shipped
(merged `b32f870`). Verified against `41a6269`.

## The problem

`PopularitySignalUpdater.UpdateAsync` (`PopularitySignalConsumer.cs:44`) returns `Task` — void — and
swallows the engagement-store failure internally: the aggregate's `catch` logs and returns at `:83`.
**The reconciliation worker is therefore structurally blind to the fault.** Every call succeeds from
its point of view, so its own `catch` (`PopularitySignalReconciliationWorker.cs:74`) is never reached
for this failure class, and `ConsumerResilience.RunWithRestartAsync` never fires either — it only
catches exceptions escaping the run loop, and none escape.

The consequence is not a logging bug. Under a persistent StarRocks fault the sweep does exactly what
it is told: enumerate **every** parent of the configured type, 500 per page, issuing one query that
fails and one `LogError` for each, then repeat ten minutes later, indefinitely. Two symptoms, one
cause:

- **Log flood** — N error lines per sweep. At the LoadTest's 400,000-parent scale that is 400,000
  lines every ten minutes.
- **Load amplification** — N failing queries per sweep against a store that is already unhealthy.

A fix that only deduplicated the log lines would leave the second symptom untouched.

## The design

### 1. `UpdateAsync` reports an outcome

Its signature becomes `Task<PopularityUpdateOutcome>`, with the enum declared **inline in
`PopularitySignalConsumer.cs`** — this codebase's convention for small enums is a one-liner in the
file that owns them (`AuthorizationAction` in `IRowFieldAuthorizationEvaluator.cs:6`; `EnrichmentKind`
and `RelationKind` in `SchemaDescriptor.cs:107,137`; `DocumentSegmentKind` in `DocumentTemplate.cs:7`),
not a file of its own:

```csharp
internal enum PopularityUpdateOutcome { Updated, Skipped, Failed }
```

**No logging changes.** Every existing log line stays exactly as it is. This adds a return value and
nothing else.

**Why an enum rather than a `bool`.** A bool would serve the sweep, which only needs failed-or-not.
But the `Skipped` cases are *designed* no-ops — an unprovisioned tenant is the documented outcome of
the consumer/store startup race, and it can legitimately affect many parents at once. If a later
maintainer folds `Skipped` into "failure", the sweep abandons itself during entirely normal
operation. The enum names the distinction at the point the logic depends on it.

### 2. Exit-path mapping — all five

`UpdateAsync` has **five** exits, not four. Four return an outcome; the fifth propagates.

| Exit | Location | Outcome |
|---|---|---|
| Aggregate threw | `catch` at `:78`, returns `:83` | `Failed` |
| Aggregate returned null | returns `:94` | `Skipped` |
| Histogram threw | `catch` at `:113`, no return — falls through | `Updated` (the count was still written) |
| Qdrant `NotFound` | `catch` at `:134`, no return — falls through | `Skipped` |
| **Any other exception** | **propagates to the caller** | **not an outcome — see below** |

**The NotFound catch falls through to the same terminal point as success** (`:134-141`, the method
simply ends). A single `return Updated;` at the end would therefore mislabel it. The implementation
must set an explicit outcome variable, or `return Skipped;` from inside that catch.

**The fifth path must keep propagating.** This is a deliberate, tested contract, not an oversight:

> `PopularitySignalConsumerTests.cs:411` — "must propagate to DispatchAsync's own per-signal catch …
> but it must not be silently swallowed by UpdateAsync itself."
>
> `PopularitySignalReconciliationWorkerTests.cs:173` — "the one exception UpdateAsync does NOT
> swallow itself … so it propagates out to the worker's own per-row try/catch."

A Qdrant `Unavailable` is the worked example. **Do not add a catch-all that converts these into
`Failed`** — it would break both tests and the contract they encode. The worker's existing `catch`
is where they are handled, and it counts them as failures (below).

### 3. The sweep abandons on consecutive failures

`SweepSignalAsync` tracks consecutive failures across the paging loop. A `Failed` outcome increments;
**the worker's existing `catch` at `:74` increments the same counter**, since a propagating exception
is the same class of event. Any other outcome resets it to zero. On reaching the threshold the sweep
abandons with one summary line:

```csharp
if (++consecutiveFailures >= MaxConsecutiveFailures)
{
    logger.LogError(
        "[PopularitySignalReconciliation] Abandoning sweep for signal={Signal} after {Count} " +
        "consecutive failures — the engagement store appears unavailable. " +
        "Retrying at the next sweep in {Minutes} minutes.",
        signal.Relation.SanitizeForLog(), consecutiveFailures, SweepInterval.TotalMinutes);
    return;
}
```

`MaxConsecutiveFailures` is a `private const int` beside `PageSize` (`:27`), matching the file's
existing convention for tuning constants.

**Consecutive, not cumulative.** A systemic fault fails every parent; one poisoned row fails one.
Cumulative counting would abandon a 400,000-parent sweep over five scattered, unrelated failures —
the wrong behaviour. Consecutive separates the two cases.

**Threshold 5 — a judgment call, stated as one.** It is not derived. It is large enough that a few
unrelated failures do not trip it, small enough to bound the damage tightly. Under a persistent fault
the sweep now emits **six log lines** (five per-parent errors plus one summary) and issues **five**
queries, against N of each today.

**Scope is per-signal**, matching the method that owns it. Under a fault affecting everything that is
5 × the number of configured signals, and `PopularitySignalOptions.Signals` is typically one or two
entries — so tightening it to a whole-sweep abort is not worth the plumbing.

**Recovery is automatic.** Abandoning affects only the current sweep; the next one runs on schedule
ten minutes later and proceeds normally once the store recovers.

### 4. Tests

1. `Failed` is returned when the aggregate throws.
2. `Skipped` is returned on a null aggregate result, **and does not count toward the threshold** —
   a sweep of all-skipped parents must run to completion.
3. The sweep abandons after five consecutive failures, emitting **exactly one** summary line.
4. The sweep does **not** abandon on scattered non-consecutive failures.
5. The consumer's behaviour is unchanged when it ignores the new return value.

## Out of scope

- **A pre-sweep health probe.** `IEngagementStoreHealthCheck.IsHealthyAsync()` exists
  (`IEngagementStoreRoles.cs:11-15`) and would let the sweep skip entirely when the store is already
  down. Deliberately excluded: once the abort fires at five, the sweep costs five queries rather than
  N, so the probe buys a marginal saving in exchange for a second mechanism, a second failure mode,
  and a second thing to test. It remains a clean addition later if wanted.
- **Backing off the sweep interval during a fault.** Considered and rejected as a new state machine
  for a problem the abort already bounds.
- **Changing any existing log line.** The per-parent `LogError` stays; the abort bounds it at five.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| A1 | `UpdateAsync` returns bare `Task` and swallows the aggregate failure | `PopularitySignalConsumer.cs:44`; `catch` at `:78` returning `:83`; null-result return at `:94` |
| A1b | It has **five** exit paths, not four | the four in the table above, plus a propagating class documented by `PopularitySignalConsumerTests.cs:411` and `PopularitySignalReconciliationWorkerTests.cs:173`. Found during verification; the original design listed four |
| A1c | The `NotFound` catch shares a terminal point with success | `:134-141` — the catch logs and the method ends, no distinct return |
| A2 | Small enums live inline in the owning file | `IRowFieldAuthorizationEvaluator.cs:6`, `SchemaDescriptor.cs:107`, `:137`, `DocumentTemplate.cs:7` — all one-liners, none in a dedicated file |
| A3 | The sweep's `foreach` + per-row try/catch has the shape the design edits | `PopularitySignalReconciliationWorker.cs:65-80` |
| A4 | The worker's `catch` logs and continues, not rethrows | `:74-79` |
| A5 | The consumer can ignore the return value without a warning | no `Directory.Build.props`, no `TreatWarningsAsErrors`, no `AnalysisMode` anywhere in `Iverson.Server/` |
| A6 | Exactly three call sites | consumer `:217`, `:223`; worker `:72` |
| A12/A13 | No interface or delegate constrains the signature | `PopularitySignalUpdater` is declared `internal sealed class` at `:38` with no base type or interface; no delegate references `UpdateAsync` |
| A8 | The worker's tests drive `SweepSignalAsync` directly | 10 references in `PopularitySignalReconciliationWorkerTests.cs` |
| A10 | The suite can already simulate the failures the tests need | `StubAggregate` at `:79`; `Task.FromException` used for throw simulation |
| A11 | `private const` matches the file's convention | `PageSize` at `:27`, beside `SweepInterval` at `:26` |
| — | `ConsumerResilience` does not mitigate this today | `ConsumerResilience.cs` catches only exceptions escaping the run loop; the updater swallows, so none escape |
| — | The codebase has an established anti-spam pattern | `ReconciliationService.cs:65-71` counts failures and emits one line with the count, rather than one per row |
