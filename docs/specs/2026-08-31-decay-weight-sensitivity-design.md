# Decay-Weight Sensitivity — Design

**Purpose.** Decide between two fusion weight triples that both reach the measured optimum
`w = 0.500` but differ in decay's share. The question is narrow and bounded: *does the incidental
0.91-percentage-point dilution of decay change search results enough to care?*

**Scoped to the centroid-present fusion branch.** The reranker's weighted mean runs over signals
present, so a candidate without a centroid fuses differently and carries a larger perturbation (see
Background). The measured corpus cannot produce that branch (A18), so this experiment speaks for the
centroid-present branch only; the centroid-absent branch is decided analytically in "Scope of the
comparison" and is not measured.

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

Their difference has an exact closed form — but the reranker accumulates `weightTotal` only over
signals **present**, so the form is branch-dependent. Each branch verified numerically to 1e-12 over
200,000 draws:

| branch | closed form | bound |
|---|---|---|
| base + centroid + decay | `(1/110) * ( mean(base,centroid) - decay )` | ±0.00909 |
| base + decay (centroid absent) | `(1/66) * ( base - decay )` | ±0.01515 |
| base + centroid (no decay), or base alone | triples agree exactly | 0 |

The measured corpus exercises only the first branch (A18), and that is the branch this experiment
speaks for; the centroid-absent branch is decided analytically in "Scope of the comparison" rather
than measured. Against the measured branch the bound (signals in [0,1]) is not obviously negligible: in the
measured runs the median adjacent top-10 score gap is **0.00173**, and **91.5%** of adjacent top-10
gaps are smaller than the worst-case perturbation. Reordering is therefore possible, and the closed
form shows it is driven by the **spread** of `mean(base,centroid) - decay` across a result set — a
uniform age shift is a constant offset and provably reorders nothing.

## Design

### Capture

Instrument `ResultReranker.Rerank` on a scratch branch — never committed, matching the existing
practice for edited fusion constants — to append one line per candidate:

```
callIndex, candidateId, parentId, hasCentroid, baseScore, centroidCos
```

`parentId` is the document the candidate belongs to: `r.Payload["parent_id"]` on the `SearchChunks`
path (`ObjectSearchGrpcService.cs:445-446`), and `candidateId` itself on the `SearchSimilar` path,
where candidates are objects. `hasCentroid` records whether the centroid term was included, which
selects the fusion branch offline.

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

**Validation gate — before any analysis.** Both endpoints can throw
`RpcException(StatusCode.Unavailable)` on the embedding path *before* reaching `Rerank`, and the
harness counts the failure and continues (`BenchmarkQueryScenario.cs:97`), so one such throw shifts
the parity of every later row and silently joins components to the wrong queries. Before trusting the
join, assert that the number of distinct `callIndex` values is exactly `2 x <queries>` (1,344 for
this corpus) **and** that the harness reported `failures == 0`. Refuse to proceed otherwise.

### One run

A single `benchmark-query` invocation against the currently loaded collection (the 5.66 chunks/doc
arm: 6,000 objects, 33,950 chunks), at any weight triple — the captured components are
weight-independent, so the triple in force during the run does not matter. No re-ingest. ~30 minutes.

### Offline model

For each scenario, assign each **document** an age keyed by `parentId` — never per candidate, since
production denormalises a single parent timestamp onto every chunk (A20) — compute
`d = 0.5^(ageDays/180)` mirroring `DecayFieldResolver.ComputeDecay` (`HalfLifeDays = 180.0`, clamped
to `<= 1.0`), then score both triples **using the branch indicated by `hasCentroid`**, re-rank, and
compare.

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

**The centroid-absent branch, decided analytically.** Decay's share is not a fixed 10% today: it is
10.00% when the centroid is present and **14.29%** when it is absent, because `weightTotal` omits
`WCentroid`. No triple at `w = 0.500` preserves both — branch 1 requires `WBase = 4.5 * WDecay`,
branch 2 requires `WBase = 6 * WDecay`, and together those force `WDecay = 0`. The drift is
structural, not a tuning failure, and the branches favour opposite triples:

| triple | branch 1 decay share | branch 2 decay share |
|---|---|---|
| shipped 0.60 / 0.30 / 0.10 | 10.00% | 14.29% |
| A — 0.50 / 0.50 / 0.10 | 9.09% (-0.91pp) | 16.67% (**+2.38pp**) |
| B — 0.45 / 0.45 / 0.10 | 10.00% (+0.00pp) | 18.18% (**+3.90pp**) |

**Decision: accept the branch-2 drift, do not optimise for it.** It is bounded at +2.38 to +3.90pp,
no triple removes it, and preserving it would require retuning `WDecay` itself, which is out of
scope. This does not overturn a preference for B; it retires the premise that B is *universally*
share-preserving — that property holds in branch 1 only.

### Decision rule — fixed before the run

If the top-10 set is unchanged for **>= 99% of queries** under **both** the wide and bimodal
scenarios, the 0.91pp dilution is immaterial: ship **triple B (0.45 / 0.45 / 0.10)**, which holds
decay's share at exactly today's 10.00% **in the measured branch**, and close the hold recorded in
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
| A14 | The scratch build deploys through the existing compose path | `scratchpad/crossover.sh` rebuilt and redeployed the API this way once per sweep arm across both crossover arms, 10 times, without failure |
| A16 | Offline analysis tooling is available | `iverson-benchmark-corpora/python-libs/ir_measures` imports and scored all three arms this session; `numpy`/`scipy` back `scratchpad/stats.py` |
| A15 | No test asserts `ResultReranker` performs no I/O | No purity assertion against `ResultReranker` in `Iverson.Vector.Tests`; the claim lives only in the class doc comment, which the scratch branch does not ship |
| A17 | Every query returns >= 10 results, so a top-10 set comparison is defined | `freshstack-chunk256-2026-08-30/runs/w0500.{chunks,similar}.trec`: 672 queries each, min = max = 50 results |
| A19 | Chunk payloads carry the parent's metadata columns, so decay is non-null on `SearchChunks` | `IntelligenceStoreConsumer.cs:302-318` copies every `schema.MetadataColumns` entry onto each chunk payload |
| A20 | All chunks of one document share a single decay value | Same loop — each chunk's metadata is extracted from the parent's `payload`, so every chunk carries the identical timestamp |
| A18 | Benchmark candidates carry a non-null centroid | `BenchmarkDocument.cs:16-18` marks `Body` with both `[IversonEmbedding]` and `[IversonChunk]`, which is what makes the centroid non-degenerate |
