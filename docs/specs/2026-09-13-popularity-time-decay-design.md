# On-the-fly time decay for the relation-popularity signal

**Status:** design, not yet planned or implemented.
**Extends:** `docs/specs/2026-09-13-relation-popularity-signal-design.md` (branch
`relation-popularity-signal`, unmerged).
**Related:** `docs/specs/2026-09-13-popularity-signal-measurement-design.md` (the paused gate).

## What this changes

The shipped popularity signal is a **lifetime count with no time component at any layer**:
`PopularityFor` is `count/(count + SaturationPoint)` reading no clock
(`ObjectSearchGrpcService.cs:1019`), `ResultReranker` documents itself as clock-free, and
`PopularitySignalConsumer.cs:60` aggregates an unfiltered `COUNT(*)` with no date predicate.
`WDecay` is orthogonal and decays the *parent's own age*, never the age of the child rows — so a
three-year-old engagement counts exactly as much as today's.

This design adds a recency term computed **at query time against the current clock**, so the stored
data ages without being rewritten.

**Recency promotes, never demotes.** The naive way to express that — `max(saturate(N),
saturate(D))` — provably never fires, because `D ≤ N` for every document (each child row
contributes exactly 1 to the lifetime count and at most 1 to the decayed count). The floor must
therefore be structural, not enforced.

## The formula

Per candidate, at query time:

```
D          = Σ_buckets  count_b · 0.5 ^ (age_b / RecencyHalfLifeDays)
popularity = (N + β·D) / (N + β·D + SaturationPoint)
```

`N` is the existing lifetime count, unchanged. `β` is `RecencyBoost`. Because `D ≥ 0`, the numerator
never falls below `N`: the lifetime floor holds by construction. A row in the current bucket counts
`1 + β`; an ancient one counts ~1.

**Bucket age is measured from the bucket's start**, not its midpoint. Every bucket is shifted by the
same half-interval, so `D` is scaled by a constant that `β` absorbs entirely — it has no effect on
ranking, making the simpler choice free. Future-dated buckets clamp to 1.0, matching `ComputeDecay`.

### New options on `PopularitySignalOptions`

| Option | Default | Validation |
|---|---|---|
| `RecencyBoost` (β) | **0.0** | finite, ≥ 0 |
| `RecencyHalfLifeDays` | **180.0** | finite, > 0, **and ≤ 300** (see the storage limit below) |

At `β = 0` the formula reduces to `N / (N + SaturationPoint)` — today's expression exactly. The
feature remains inert by default, and enabling buckets alone changes no ranking. The finite-and-
positive checks mirror `AddDecayOptions`, and the 180-day default matches `DecayOptions.HalfLifeDays`.

Both are query-time configuration: half-life and boost retune without recomputing any stored value.
That property is the reason this design stores a series rather than a pre-decayed scalar.

## The `[IversonPopularitySignal]` attribute

A new client attribute marks the single UTC `DateTime` property on a child entity that represents
**when the interaction happened**.

**Why an attribute rather than the existing convention.** `DecayFieldResolver` resolves a timestamp
by convention — exactly one `[IversonMetadata]` `TIMESTAMPTZ`/`DATETIME` scalar column — and it is
the wrong instrument here for two reasons. First, `[IversonMetadata]` means "denormalized onto chunk
points"; requiring an interaction timestamp to wear it conflates unrelated concerns. Second, the
convention refuses to guess when two candidates exist, logging and returning null. A realistic
engagement row carries both an interaction timestamp and an audit `CreatedAt`, so decay would
silently switch off on the common case.

**Contract.**

- Marks one property; the value is interpreted as **UTC**. A local-time value would shift buckets
  silently, since bucket keys are produced server-side by `DATE_FORMAT`.
- **Two marked properties fail `RegisterSchema`** with a named error. Failing at registration beats
  the convention's refuse-and-log: the developer gets an immediate error instead of a signal that
  quietly does nothing.
- **Zero marked properties** means no buckets are written, `D = 0`, and popularity is the lifetime
  count — today's behavior. There is deliberately **no fallback** to the `[IversonMetadata]`
  convention; an implicit fallback would reintroduce the ambiguity this removes.

**It marks the timestamp only.** `PopularitySignal.Signals` still names which `ParentType`/`Relation`
participates. The client declares what only the developer knows — which timestamp means
"interaction" — and the operator decides what only they know, whether to rank on it. This preserves
the original spec's server-config-only choice rather than overturning it.

**Plumbing.** `bool is_popularity_signal = 23` on `PropertyDescriptor` (fields 1–22 are in use, so
this is purely additive and old clients simply omit it). It surfaces on `SchemaDescriptor` as a
nullable `string? PopularitySignalColumn` — defaulted, not required, matching the documented
"legacy `_iverson_schema` rows predate this layer" pattern. A single nullable string deliberately
avoids the `HashSet` case-comparer trap that `MetadataColumns` documents: that member re-applies
`StringComparer.OrdinalIgnoreCase` in its init accessor because `SchemaRegistry.LoadAsync`
deserializes from Postgres JSON with the default case-sensitive comparer, and without it lookups
succeed in the registering process and fail in every other one.

Per-language cost is one registrar source file each — `SchemaRegistrar.cs`, `core.ts`, `core.py`,
`SchemaRegistrar.java`, `registrar.go` — plus the .NET attribute class and one test file each.
Stub regeneration is scripted (`generate_protos.sh` per client). This is the same shape as the
declarative tenant marker that already shipped.

## Write path

`PopularitySignalUpdater.UpdateAsync` keeps everything it does today and gains one conditional step.
The reconciliation worker calls the same method, so both paths change together.

**When the child schema has a `PopularitySignalColumn`**, issue a second aggregation alongside the
existing `Count`:

```
AggregationDescriptor("buckets", AggregationKind.DateHistogram,
                      Field: <the marked column>, CalendarInterval: "month")
```

reusing the **existing FK-only `SearchQuery`, unchanged**. The result is
`IReadOnlyList<AggregationBucket>` of `(string Key, long DocCount)`, keyed `%Y-%m` and already
ordered by key.

**No SQL window clause.** The histogram is already filtered to a single parent by the FK predicate,
so the rows returned are that parent's distinct active months — a handful typically, ~120 for a
decade of engagement. Windowing in SQL would require comparing a `DATETIME` column against a string
bound, because `SearchValue` has no timestamp type and `GetScalarValue` passes strings through
verbatim; that coercion has no precedent anywhere in the codebase and no live instance to verify it
against. Truncating in the consumer instead removes the dependency entirely.

**The consumer truncates to the most recent 60 monthly buckets** — a fixed constant, *not* derived
from the half-life. Deriving it would couple a write-time decision to query-time configuration:
raising the half-life would leave stored series too short until reconciliation rewrote them. A fixed
store has nothing to heal, which is why `RecencyHalfLifeDays` is capped at 300: six half-lives is
where a bucket's contribution falls below ~1.6%, and 60 months covers half-lives to roughly 300 days.
Beyond that the tail would be silently lost, so the validator rejects it rather than degrading
quietly.

**One write, two keys.** `SetPayloadAsync` already takes a dictionary:

```csharp
new Dictionary<string, object> {
    [fieldName]             = count,                          // unchanged
    [fieldName + "Buckets"] = "2026-07:3;2026-08:11;2026-09:2"
}
```

The series is a **string this code encodes and parses itself**, not a Qdrant list:
`ToCanonicalString` returns `StringValue` verbatim but falls through to protobuf's `ToString()` for
lists, which is not a contract worth depending on. Bucket keys arrive already in `%Y-%m` form, so
encoding is a join, and the format stays readable for debugging.

**Cost:** two StarRocks queries per popularity update instead of one.

**Failure behavior** follows the existing pattern: the histogram call sits in the same `try`, and any
failure leaves the count written and the series absent — degrading to today's ranking, never to a
wrong one.

## Read path

**`ResultReranker` does not change.** `RerankCandidate.Popularity` stays a pre-computed `double?`,
and the reranker keeps its "pure and I/O-free, reads no clock" contract. The entire change is
contained in the two helpers that produce that value: `PopularityFor` (object path) and
`RetrievePopularityOrDegradeAsync` (chunk path). Both gain `now`, the half-life, and β.

**One ordering edit:** on the chunk path `now` is computed at `ObjectSearchGrpcService.cs:656`,
*after* `RetrievePopularityOrDegradeAsync` is called at :650. It must be hoisted above that call.

**One shared parse helper**, living beside `ComputeDecay` and mirroring its curve and clamping. The
computation (split, parse `%Y-%m`, age in days, `0.5^(age/HL)`, sum) is non-trivial enough that
duplicating it verbatim would be worse than sharing it — unlike the three small private helpers the
consumers already duplicate by plan mandate.

| Condition | Result |
|---|---|
| No bucket key in payload | `D = 0` → `saturate(N)`, today's ranking |
| Bucket string malformed anywhere | **Whole series absent**, `D = 0` |
| Child has no `[IversonPopularitySignal]` | Consumer wrote no buckets → `D = 0` |
| `N` missing but buckets present | Popularity **absent** entirely, as today — the floor needs `N` |
| Future-dated bucket (clock skew) | Clamped to 1.0, matching `ComputeDecay` |
| `β = 0` (default) | Reduces to `N/(N+S)` exactly |

The malformed case is deliberately all-or-nothing rather than skip-the-bad-entry: a partially parsed
series yields a confidently wrong `D`, which is worse than no recency signal, and it matches
`ComputeDecay`'s "unparseable returns null, never a neutral value" stance.

**One cost, named not optimised.** The object path parses up to 60 buckets for each of up to 200
candidates per search — roughly 12,000 small parses per request. Almost certainly sub-millisecond,
but it is new hot-path work; if it ever matters the fix is caching parsed series by point id, not
changing the format.

## Rejected alternatives, with evidence

**A SQL-side exponential decayed sum.** Not expressible. `StarRocksQueryBuilder.cs:210-217` applies
`DeriveWhitelist` to `spec.Expression`; the list is `SUM, AVG, MIN, MAX, COUNT, OVER, PARTITION, BY,
ORDER, ASC, DESC, COALESCE, NULLIF, ROUND, ABS, AND, OR, NOT, NULL`. `EXP`, `POW`, `UNIX_TIMESTAMP`
and `DATEDIFF` all throw `Unknown aggregation field`. Widening that list is not a local change:
`StarRocksQueryBuilder.cs:292-298` records that `spec.Expression` "IS reachable by any caller with
read access to this type via the Aggregate RPC" and explicitly says not to weaken the checks.
Baking the half-life into a stored scalar would also forfeit retunability.

**Count plus mean age.** Cheap — `Sum` over the timestamp column needs no expression at all — but
mathematically inadequate: `Σ exp(-λ·age_i)` is not a function of `(n, Σage_i)`. Measured, at a
365-day half-life: 50 rows at age 0 plus 50 at age 2000 versus 100 rows all at age 1000 have
identical count and identical mean age, true decayed counts of 51.12 and 14.97 (a **3.4× gap**), and
**identical** scores under the approximation. It cannot distinguish a recent burst from a dead
history, which is the entire case decay exists to detect.

**Periodic recomputation in the consumer.** No allowlist change and O(1) storage, but the value is
only as fresh as the last sweep — not an on-the-fly calculation — and every parent's score drifts
silently between sweeps.

**Iverson's own search results as an intrinsic popularity signal.** Rejected on measurement, and
recorded here so it is not re-proposed. The proposal: since `SearchSimilar`/`SearchChunks` return
ranked results, aggregate how often and how highly a document surfaces across queries, giving a
popularity signal needing no external data. Measured on `sci-2048.similar.trec` — 300 SciFact
queries × 50 ranked documents — with leave-one-out so a document's own query cannot score it:

| Intrinsic signal | Pooled AUC | Per-query mean (n=275) |
|---|---|---|
| Rank-weighted `Σ 1/log₂(1+r)` | 0.4822 | **0.5012** |
| Plain appearance count | 0.4266 | **0.4353** |

Rank-weighting lands exactly on the no-information line; plain frequency is **below** it, meaning
documents appearing in more result lists are *less* likely to be relevant. That is the hubness
prediction confirmed directly — in high-dimensional embedding spaces a minority of points appear in
disproportionately many neighbour lists, and those hubs sit near the data centroid, i.e. are
generic. Observed hubness was mild (mean 3.38 appearances, max 21 of 300), so this is the signal
being uninformative rather than the space being pathological.

Feeding it back would also be self-reinforcing with no human anywhere in the loop, amplifying
something measured at or below chance.

*Caveat on how far this generalises:* SciFact has ~1.13 relevant documents per query, so a relevant
document is relevant to essentially one query and has no reason to appear in others, which
structurally caps how much cross-query centrality could help. With 300 queries each document appears
in ~3.4 pools — a thin basis for estimating centrality. This is strong evidence against the idea on
this shape of corpus and direct confirmation of the mechanism; it is not a universal refutation.
Reopening it requires a corpus where documents are genuinely relevant to many queries.

## Known hazard: the engagement feedback loop

No impression, click, or feedback write path exists anywhere in the server — popularity only gains
rows if a deployment wires its clients to write them. For deployments that do, `popularity ↑ →
rank ↑ → interactions ↑ → popularity ↑` is unmitigated rich-get-richer.

**The recency decay in this design partly damps it**: old popularity fades, so a document must keep
earning interactions to keep its boost. That is the cheapest available mitigation and a genuine
side-benefit of this change.

Two notes for deployments wiring search-driven engagement:

- **Record rank at interaction time.** It cannot be reconstructed later.
- **Rank means stream order, never score order.** `object_search.proto` returns `score`, not
  position, and both RPCs document that MMR diversification makes the streamed order "not simply
  fused-score-descending". Sorting by score yields the wrong rank.

Position-bias correction (inverse propensity weighting) is deliberately **not** in this design. It
would add an uncalibrated propensity model to a feature whose single existing weight has never been
measured, and it is unfalsifiable with what exists here — no click logs, and the SciFact gate has
neither ranks nor clicks.

## Out of scope

- **A dedicated engagement ingestion endpoint.** Decided: its own spec, started next, independent of
  what the measurement gate returns. Nothing is blocked meanwhile — a deployment populates popularity
  today by `Post`-ing a child entity carrying `[IversonPopularitySignal]`. Note the throughput
  argument for such an endpoint is currently **unmeasured in both directions**: the only write-path
  data on disk is `count=32, concurrency=16` runs whose p95 is cold-start dominated (even `Tag`, with
  no embedding field, reports 1 ops/sec at p95 7.1s).
- **Weighted or typed interactions.** Buckets carry **counts**; a view weighs the same as a save.
  `DateHistogram` hardcodes `COUNT(*)` (`StarRocksQueryBuilder.cs:286-287`), so a bucket can never
  carry a weighted sum. Adding weights later needs a new aggregation kind or a `GroupBy`+`Sum` path
  in the StarRocks layer — a door this design closes, knowingly.
- **Calibrating β or the half-life.** Both ship chosen-not-measured, like `WPopularity` and `WDecay`
  before them. The measurement spec is the instrument that could calibrate them.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| A1 | A free, additive proto field exists | `PropertyDescriptor` uses 1–22; `is_popularity_signal = 23` is additive and old clients omit it |
| A2 | `SchemaDescriptor` can carry the marker | `ColumnDescriptor(Name, SqlType, IsNullable)` has no per-column flags; markers live as descriptor members. A nullable string avoids `MetadataColumns`' documented comparer trap |
| A3 | `SchemaBuilder` is the home for mapping and rejection | consumes `prop.IsMetadata` at `SchemaBuilder.cs:96`, sets `MetadataColumns` at :226; already throws for reserved-key collisions |
| A4 | One registrar source file per client | `SchemaRegistrar.cs`, `core.ts`, `core.py`, `SchemaRegistrar.java`, `registrar.go`, each with one test file |
| A5 | Go expresses markers via struct-tag metadata | `registrar.go:142` — `IsMetadata: fm.Metadata` from a parsed `fieldMeta` |
| A6 | .NET attributes are one file each | 16 files in `Iverson.Client.Attributes/` |
| A7 | Stub regeneration is scripted | `generate_protos.sh` in Python, TypeScript, Go clients and AdminUI |
| A10 | `DateHistogram` + `CalendarInterval: "month"` is supported | `StarRocksQueryBuilder.cs:285-289` emits `DATE_FORMAT(col,'%Y-%m')`, `GROUP BY bucket_key ORDER BY bucket_key`; intervals minute/hour/day/week/month/year/quarter |
| A11 | The result type exposes buckets | `AggregationResult(Name, Kind, Buckets?, MetricValue?)`, `AggregationBucket(string Key, long DocCount)`, aliased `EngagementAggResult` in the consumer |
| A12 | A SQL window bound would be unverifiable | `SearchValue` has no timestamp kind; `GetScalarValue` (`:945-951`) passes `StringVal` verbatim; no timestamp-comparison precedent anywhere in the repo. **Design changed to avoid it** |
| A15 | `SetPayloadAsync` takes multiple keys | signature is `IReadOnlyDictionary<string, object>` |
| A16 | A string round-trips verbatim | `ToCanonicalString`: `StringValue => v.StringValue`; lists fall through to protobuf `ToString()` |
| A17 | Reconciliation reuses the same writer | `PopularitySignalReconciliationWorker.cs:68` calls `updater.UpdateAsync(...)` |
| A18 | `%Y-%m` keys sort chronologically | lexicographic order on zero-padded `%Y-%m` is chronological; SQL already emits `ORDER BY bucket_key` |
| A21 | The chunk path computes `now` too late | `ObjectSearchGrpcService.cs:650` calls the popularity helper; `:656` computes `now`. Must hoist |
| A22 | `RetrievePayloadAsync` returns all keys | `:222` maps the whole payload dictionary |
| A23 | No reranker signature change | `RerankCandidate.Popularity` is already `double?`; the value is pre-computed by the caller |
| A25 | `PopularitySignalOptions` is the right home | already holds `SaturationPoint`, validated in `AddPopularitySignalOptions` |
| A26 | Env binding works for new doubles | executed earlier this session: `PopularitySignal__SaturationPoint=186` bound, alongside `Signals__0__*` into the positional record |
| A28 | An existing test breaks | `PopularitySignalConsumerTests.cs:163` asserts `p.Count == 1` on the payload dictionary — fails once buckets are added |
| — | The `max()` formulation is degenerate | `D ≤ N` by construction, so `max(sat(N), sat(D)) = sat(N)` always; the floor must be structural |
| — | No feedback write path exists | grep for impression/click/feedback across `Iverson.Api` returns nothing |

Carried as risk, not verified: A8 (old-server/new-client forward compatibility for the new field),
A13/A14 (the marked column's casing as `AggregationDescriptor.Field` expects it, and that
`CheckFieldAllowed`/`ResolveStrict` accept it), A19 (nothing rate-limits the doubled StarRocks call
rate), A29/A30 (`DecayFieldResolver` remains correct for parent decay; the client conformance matrix
survives the new field). A13/A14 are the sharpest of these — they are a plan-time check against
`ResolveStrict`, and getting the casing wrong throws rather than failing silently.
