# Decay-Weight Sensitivity — Design

**Purpose.** Decide between two fusion weight triples that both reach the measured optimum
`w = 0.500` but differ in decay's share. The question is narrow and bounded: *does the incidental
0.91-percentage-point dilution of decay change search results enough to care?*

This spec does **not** try to find the best decay weight. No corpus available here judges recency,
so no benchmark here can answer that. See "Out of scope".

## Background

`ResultReranker` computes a weighted mean **over the signals present on each candidate**
(`ResultReranker.cs:35-51`): `weightTotal` accumulates only the weights whose signals exist.
The `w` used throughout `docs/centroid-weighting-proposal.md` is therefore the *two-signal* ratio
`WCentroid / (WBase + WCentroid)`, and describes the fusion only when decay is absent.

Every benchmark run in that document was measured with `Decay: null` — `BenchmarkDocument` carries
no timestamp metadata column, so `DecayFieldResolver.ResolveDecayField` returns `null`. Decay is
nonetheless live in production for any schema with a `TIMESTAMPTZ` or `DATETIME` metadata column.

Two triples reach `w = 0.500` and are **bit-identical on the no-decay path**, which is why the sweep
could not distinguish them:

| triple | sum | w | base | centroid | decay |
|---|---|---|---|---|---|
| shipped 0.60 / 0.30 / 0.10 | 1.00 | 0.333 | 60.00% | 30.00% | 10.00% |
| A — 0.50 / 0.50 / 0.10 | 1.10 | 0.500 | 45.45% | 45.45% | 9.09% |
| B — 0.45 / 0.45 / 0.10 | 1.00 | 0.500 | 45.00% | 45.00% | 10.00% |

Their difference has an exact closed form, verified numerically to 1e-12 over 200,000 random draws:

```
A - B = (1/110) * ( mean(base, centroid) - decay )
```

Bounded at **±0.00909** when all signals lie in [0,1]. That bound is not obviously negligible: in the
measured runs the median adjacent top-10 score gap is **0.00173**, and **91.5%** of adjacent top-10
gaps are smaller than the worst-case perturbation. Reordering is therefore possible, and the closed
form shows it is driven by the **spread** of `mean(base,centroid) - decay` across a result set — a
uniform age shift is a constant offset and provably reorders nothing.

## Design

### Capture

Instrument `ResultReranker.Rerank` on a scratch branch — never committed, matching the existing
practice for edited fusion constants — to append one line per candidate:

```
callIndex, candidateId, baseScore, centroidCos
```

`ResultReranker` is registered as a **singleton** (`ServiceCollectionExtensions.cs:50`) and may serve
concurrent requests, so the append must be guarded by a lock.

Decay is deliberately **not** captured: it is `null` for every benchmark candidate and is supplied
synthetically offline.

Queries correlate to capture rows by **call ordering**. `BenchmarkQueryScenario.cs:85-107` iterates
queries with `foreach` and `await`s each call, with no parallelism anywhere in the scenario, issuing
exactly one `SearchSimilar` then one `SearchChunks` per query. Call index `2i` / `2i+1` therefore
maps to query `i`.

**Run procedure:** no other traffic may hit the API during capture, or call ordering interleaves and
the correlation silently breaks.

### One run

A single `benchmark-query` invocation against the currently loaded collection (the 5.66 chunks/doc
arm: 6,000 objects, 33,950 chunks), at any weight triple — the captured components are
weight-independent, so the triple in force during the run does not matter. No re-ingest. ~30 minutes.

### Offline model

For each scenario, assign each document an age, compute `d = 0.5^(ageDays/180)` mirroring
`DecayFieldResolver.ComputeDecay` (`HalfLifeDays = 180.0`, clamped to `<= 1.0`), then score both
triples, re-rank, and compare.

Scenarios vary **age spread**, because the closed form shows spread — not level — drives reordering:

| scenario | ages | role |
|---|---|---|
| uniform | all identical | control; must show exactly zero reordering |
| narrow | within 30 days | typical fresh corpus |
| wide | uniform 0-720 days | typical mixed archive |
| bimodal | half fresh, half 2 years | adversarial; maximises spread |

Reported per scenario: fraction of queries whose top-10 **set** changes, rank displacement, and
Kendall tau between the two orderings.

### Scope of the comparison, and why it is conservative

`Rerank`'s output feeds MMR diversification (`ObjectSearchGrpcService.cs:268` and `:456`), so the
user-visible top-10 is post-MMR. This design compares the **pre-MMR** ordering. Because MMR is a
deterministic function of that ordering plus the diversity vectors, an unchanged pre-MMR ranking is a
**sufficient condition** for unchanged final output. The comparison may therefore overstate change —
a reordering deep in the candidate list can leave the final top-10 identical — which is conservative
in the safe direction.

### Decision rule — fixed before the run

If the top-10 set is unchanged for **>= 99% of queries** under **both** the wide and bimodal
scenarios, the 0.91pp dilution is immaterial: ship **triple B (0.45 / 0.45 / 0.10)**, which holds
decay's share at exactly today's 10.00%, and close the hold recorded in
`docs/centroid-weighting-proposal.md`.

Otherwise the dilution is material, and the choice becomes a product decision about decay's intended
share — not something this or any relevance benchmark should settle.

### Reported but explicitly not decisive

nDCG@10 between the triples is recorded for the record and **must not drive the choice**. With
synthetic timestamps uncorrelated to relevance, decay is noise with respect to the qrels, so the
measure will favour whichever triple carries less decay by construction. This is the same trap the
source document already documented for MMR: "A benchmark that scores no credit for diversity will
always prefer diversification off; that is a property of the measure." Recording the number without
letting it decide is the guard against repeating that error.

## Out of scope

- **The optimal decay weight.** No corpus here judges recency; nothing available can answer it.
- The MMR / `Lambda` question.
- The 180-day half-life in `ComputeDecay`.
- Any change to the centroid weight itself, which remains held pending this result.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| A1 | Centroid cosine is computed in exactly one place | `ResultReranker.cs:40` is the only `CosineSimilarity` against a centroid; `ResultDiversifier.cs:98` computes a different quantity (candidate-to-candidate, for MMR) |
| A2 | `Rerank` is called once per search request, not in a loop | Two call sites only: `ObjectSearchGrpcService.cs:268` and `:456`, one per endpoint |
| A3 | Harness issues one SearchSimilar then one SearchChunks per query, sequentially | `BenchmarkQueryScenario.cs:95-96` inside a `foreach` with `await` on each; no `Parallel` / `WhenAll` / `Task.Run` in the file |
| A4 | No other API traffic during capture | Not code-verifiable; enforced as a run procedure (stated above) |
| A5 | `BaseScore` is the raw Qdrant similarity | `ObjectSearchGrpcService.cs:258` — `BaseScore: r.Score` |
| A6 | Every benchmark candidate has `Decay: null` | `BenchmarkDocument.cs` declares no `DateTime`/`DateTimeOffset` property; `DecayFieldResolver.cs:41-67` requires a `TIMESTAMPTZ`/`DATETIME` column present in `MetadataColumns` |
| A7 | Decay is exactly `0.5^(age/180)` | `DecayFieldResolver.cs:14` `HalfLifeDays = 180.0`; `:85` `Math.Min(1.0, Math.Pow(0.5, ageDays / HalfLifeDays))` |
| A8 | Decay lies in (0,1] | Same line — `Math.Min(1.0, ...)` caps above; `0.5^x > 0` for all finite x |
| A9 | Base and centroid lie in [0,1], so the ±1/110 bound holds | 2,291,100 scored rows across every preserved run: min fused 0.443912, max 0.934603, **zero negative**. Inferred from fused scores; the capture run observes components directly and will confirm it |
| A10 | Captured components are weight-independent | `ResultReranker.cs:35-51` — base comes from the candidate, centroid from `CosineSimilarity`; neither reads `WBase`/`WCentroid` |
| A11 | A queryable collection is loaded | Live: `benchmark_documents_tenant_bypass` = 6,000, `benchmark_documents_chunks_tenant_bypass` = 33,950 (the 5.66 chunks/doc arm) |
| A12 | A query run costs ~30 min | Sweep logs, `freshstack-chunk256-2026-08-30/sweep.log`: five arms at 04:01 -> 06:39, ~32 min each |
| A13 | The reranker is a singleton and may serve concurrent requests | `ServiceCollectionExtensions.cs:50` — `AddSingleton<IResultReranker, ResultReranker>()`. Forces the capture write to be locked |
| A15 | No test asserts `ResultReranker` performs no I/O | No purity assertion against `ResultReranker` in `Iverson.Vector.Tests`; the claim lives only in the class doc comment, which the scratch branch does not ship |
| A17 | Every query returns >= 10 results, so a top-10 set comparison is defined | `freshstack-chunk256-2026-08-30/runs/w0500.{chunks,similar}.trec`: 672 queries each, min = max = 50 results |
| A18 | Benchmark candidates carry a non-null centroid | `BenchmarkDocument.cs:16-18` marks `Body` with both `[IversonEmbedding]` and `[IversonChunk]`, which is what makes the centroid non-degenerate |
