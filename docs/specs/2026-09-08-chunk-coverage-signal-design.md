# Chunk-coverage signal — does a document's second-best chunk carry information?

**Status:** specified, not executed.
**Date:** 2026-09-08.

## 1. Why

Max-passage collapse keeps each document's best chunk and discards the rest.
`DocumentRanking.CollapseByDocId` holds one `maxByDoc` entry per document, so a document matching
the query in five places ranks identically to one that matched in a single lucky place. How many
chunks matched, and how well, is thrown away.

`MaxPassageAggregator`'s own docstring names the road not taken — the parent's score is the maximum
"not the first chunk seen or the sum of its chunks" — and no alternative to max-passage appears
anywhere in `docs/`. The multivector experiment (`2026-09-GATE-multivector.md`) is not a prior test
of this: Qdrant's `multivector_config.comparator` was `max_sim`, which is also max-aggregation.

**The signal is available.** Chunks-per-document is not the flat mean the corpus summaries suggest;
the distribution is wide, and 46.0 % of FreshStack-2048 documents carry four or more chunks.

## 2. The signal

```
score(doc) = max_chunk + β · Σ(up to the next 3 highest chunk scores of that doc)
```

Sweep **β ∈ {0, 0.003, 0.008, 0.02, 0.05}**.

- **β = 0 must reproduce today's ranking bit-for-bit.** `max + 0.0 · tail` is exactly `max` in IEEE
  arithmetic for finite tails, but the implementation must not rely on that incidentally — it
  short-circuits, matching the discipline already in `ResultReranker` (which short-circuits its
  weighted mean to preserve ordering bit-for-bit) and `ResultDiversifier` (which reduces to
  `Take(topK)` at λ = 1.00). The baseline arm's validity depends on this.
- **The tail is capped at 3 chunks.** Uncapped, the term is dominated by document length. Fused
  chunk scores on the primary arm span 0.5770–0.8552 (`fs-2048-l100.chunks.trec`), so a 20-chunk
  document sums to ~14 against a max of ~0.85, and β would be measuring length rather than coverage.
- **Calibration is against the score *spread*, not the score level.** What decides rank is the gap
  between documents, and that is two orders of magnitude below the level: on the primary arm the whole
  rank-1 → rank-50 span is 0.0747 (mean; 0.0701 median) and the median top-10 adjacent gap is 0.00236.
  Because pooled chunk scores are bounded well away from zero (min 0.5770), each tail chunk
  contributes a near-constant β·s ≈ β·0.72 — so β must be set from the gap scale or the term
  degenerates into a count of tail chunks. **β ≈ 0.003** makes one tail chunk worth about one median
  top-10 gap: a genuine tie-break arm. **β ≈ 0.035** makes a full 3-chunk tail worth about the entire
  top-50 span: the parity arm this section previously believed β = 0.35 to be. The swept range spans
  tie-break to parity. The term multiplies *fused* chunk scores (`ResultReranker.cs:36-52`), not raw
  cosines.

## 3. Where it is computed

**No server change.** `SearchChunks` returns chunk-level rows and does not dedup by parent
(`ObjectSearchGrpcService.cs:577-593`); the harness owns the collapse. The work is:

1. A new aggregator function beside `DocumentRanking.CollapseByDocId`. **It must be a new function,
   not a modification.** `CollapseByDocId` serves two distinct callers — chunk collapse *and*
   same-`DocId` entity dedup on the `SearchSimilar` path — and changing it would silently alter
   `SearchSimilar` run files, which have no chunks and no tail.
2. A raw-chunk-hit dump in `BenchmarkQueryScenario`. The `chunks` list at `:396` is local and only
   its aggregated output is written; the existing `.chunks.diversity.json` sidecar records parent
   keys and counts but not scores. Raw `(ParentKey, Score)` hits in rank order are not persisted
   today, and every β is reconstructible from them.

3. **A `benchmark-aggregate` command** (`Program.cs` dispatch, beside `benchmark-query`). Reads the
   dump plus `keymap.json`, applies the new aggregator at a `--beta` flag, and writes
   `<label>.chunks.trec` plus `<label>.meta.json` — without the sidecar every `report.py` compare
   block prints `BUILD UNKNOWN` (`report.py:589-591`). Sharing one aggregator with the in-run path is
   what makes the β = 0 identity check a test of the shipping code rather than of a second
   implementation; a Python reimplementation would additionally have to reproduce `keyMap` resolution
   and unresolved-parent exclusion (`MaxPassageAggregator.cs:50-53`), truncation *after* collapse
   (`DocumentRanking.cs:40-44`) and the first-seen tie rule (`:36`).

**One query run per corpus serves every β.** The sweep is offline over the dumped hits, so β costs
nothing after the run that produced the pool.

The byte-identity established for the routed `SearchSimilar` path
(`2026-09-08-similar-via-chunks-design.md` §4) is a **max-passage** result and does not extend here.
The two paths collapse different pools — the harness sees the `DocumentBudget × multiplier` chunks
the server returns, while the routed path collapses `topK × ceil(chunkPoints / objectPoints) × 4`
(`ObjectSearchGrpcService.cs:358-362`; `OverFetchFactor` = 4 at `:900`). Max is invariant to the
extra chunks — the companion spec's own agreement condition is that the paths match "wherever the
first 200 fused chunks hold at least 50 distinct parents" — but a sum over the next three is not,
because those chunks include a document's 2nd–4th at fused ranks the harness's dump never contained.
**This experiment measures `SearchChunks` only.** Shipping a winning β to the routed path needs its
own arm at matched budget, in the shipping spec `ObjectSearchGrpcService.cs:364` already points at.

## 4. Arms and the MMR confound

`LambdaChunks` diversifies *among chunks*, which pushes toward one chunk per parent and starves the
tail this signal reads. That is measured, not hypothetical:

| Arm | distinct parents @10 | @50 | chunks per parent @50 |
|---|---|---|---|
| `fs-2048` λ 0.70 (shipped) | 8.10 | 34.50 | 1.45 |
| `fs-2048` λ 1.00 | 6.67 | 28.82 | 1.74 |
| `fs-512` λ 0.70 | 7.18 | 26.85 | 1.86 |
| `fs-512` λ 1.00 | 5.20 | 19.08 | 2.62 |

So the primary arms run at **`LambdaChunks` = 1.00** — MMR off, the unconfounded test of whether
coverage carries signal at all — plus **one confirmation arm at the shipped 0.70 on FreshStack-2048**,
answering whether it survives the configuration that actually ships. Without this split, a null at
0.70 cannot distinguish "coverage is worthless" from "MMR starved it".

`LambdaSimilar` and the fusion triple stay at their shipped values on every arm.

## 5. Corpora

| Corpus | Window | mean chunks/doc | 1-chunk docs | ≥4-chunk docs | Role |
|---|---|---|---|---|---|
| FreshStack-2048 | 2048/1792 | 3.10 | 26.6 % | **46.0 %** | primary; **the only production-window arm**; nugget qrels |
| FreshStack-512 | 512/448 | 10.79 | 10.2 % | **77.7 %** | deepest tail; 512-window; nugget qrels |
| NFCorpus | **512/448** | 4.05 | **1.0 %** | **71.0 %** | second 512-window density arm; graded qrels |
| SciFact-2048 | 2048/1792 | 1.27 | **73.7 %** | 0.1 % | **null control** (invariant below) |

Every figure reproduces the ingest-recorded chunk totals exactly (18,622 / 64,735 / 14,729 / 6,587)
when `split_into_chunks` (`ingest.py:240-258`, `word_boundary_lookback` 50) is applied at each
corpus's own recorded window from its `keymap.json.stats.json`.

SciFact is a correctness check, not evidence. The check is an **invariant on the aggregator, not a
threshold on the corpus**: *a document contributing exactly one chunk to the pool must score
identically at every β.* That is checkable per document, holds regardless of how many documents carry
tails (26.3 % of SciFact documents do), and fails loudly on the real bug classes — an off-by-one in
the tail slice, self-inclusion of the max in its own tail, or taking the tail in stream order rather
than by score. **A violation means the aggregator is wrong**, and the sweep must not be interpreted
until it is explained. SciFact earns its place because 73.7 % single-chunk documents make violations
most visible there, not because β is expected to leave the corpus unmoved.

Snapshots, keymaps and qrels are on disk for all four — restores plus one query run each, no
re-ingest.

## 6. Gate

**Primary: nDCG@10 on FreshStack-2048 at λ = 1.00**, baselined at β = 0, Holm-corrected across the
four non-zero β values (family size 4). A β **qualifies** if its nDCG@10 delta against β = 0 is **positive** and Holm p_adj < 0.05. A
significant negative delta is a qualifying result for the opposite conclusion and is recorded as
such, not discarded.

Coverage is a precision claim — a document matching in several places is more likely genuinely
relevant — so ordering is the endpoint.

- **If a β qualifies**, re-check it on the λ = 0.70 confirmation arm. It must not be significantly
  worse there, or the finding does not survive the shipped configuration.
- **If none qualifies**, max-passage stands, and the null is interpretable because it was taken with
  MMR off.

α-nDCG@10 (FreshStack only) and R@50 are reported and do not gate. α-nDCG is watched for a
**negative**: coverage may concentrate on comprehensive documents at the cost of subtopic spread, and
that cost belongs in the record even though it cannot veto.

`--pair` cannot be used — β changes which documents reach the top 50, so the document set differs and
`check_pool` exits with `ARM INVALID: pool changed`. Use `--baseline`.

## 7. What this cannot show

Whether coverage helps at chunk budgets other than 250 (`DocumentBudget` 50 ×
`--chunk-budget-multiplier` 5), and whether it interacts with the fusion weights. Both are held fixed.
A null is therefore scoped to this budget and this triple.

Whether a winning β transfers to the routed `SearchSimilar` path. That path collapses a larger pool
which scales with the caller's `top_k` (`ObjectSearchGrpcService.cs:358-362`), so the available tail
depth varies per request — no fixed-budget harness arm measures it.

## 8. Verified assumptions

Verified 2026-09-08 against the working tree and the corpora on disk.

| # | Assumption | Evidence |
|---|---|---|
| B1 | `Aggregate` is pure over `(ParentKey, Score)` | `MaxPassageAggregator.cs` — resolves keys, delegates to `DocumentRanking.CollapseByDocId` |
| B2 | The harness receives several chunks per parent | Diversity sidecars: 28.82 distinct parents per 50 chunks (`fs-2048` λ 1.00) = 1.74 per parent |
| B3 | Chunk scores are server-fused and cross-document comparable | `BenchmarkQueryScenario.cs:402` stores `r.Score` as returned by `SearchChunks` |
| B4 | Raw hits are discarded today | `chunks` at `:396` is local; only aggregated output reaches `TrecRunWriter` at `:293` |
| B5 | Raw hits suffice to reconstruct any β | Aggregation reads only `(ParentKey, Score)` in rank order |
| B6 | No server change is needed | `ObjectSearchGrpcService.cs:577-593` streams one `ChunkSearchResponse` per diversifier-selected chunk, with no parent collapse |
| B7 | **FAILED.** The routed `.similar` byte-identity is a **max-passage** result and does **not** extend to a tail-sensitive aggregator | `2026-09-08-similar-via-chunks-design.md:241-244` — the paths agree "wherever the first 200 fused chunks hold at least 50 distinct parents", a max-only condition; routed pool sizing at `ObjectSearchGrpcService.cs:358-362`. Scope is now `SearchChunks` only (§3, §7) |
| B8 | `LambdaChunks` is settable by env + restart | `docker-compose.yml:446`; line 444 documents the restart command |
| B9 | λ = 1.00 reduces MMR to `Take(topK)` | `ResultDiversifier.cs:72-75` — `Mmr(i) = 1.0·score − 0·maxSim` |
| B10 | The MMR confound is real | Measured distinct-parent table in §4 |
| B11 | Snapshots exist for all four corpora | 2 `.snapshot` files in each `*-qdrant-snapshots/` |
| B12 | Density figures reproduce the ingest records exactly | Faithful `split_into_chunks` port at each corpus's own recorded window: 18,622 / 64,735 / 14,729 / 6,587, each matching its `keymap.json.stats.json` |
| B13 | Only FreshStack has nugget qrels | `qrels.nugget.trec` present for both FreshStack arms, absent for NFCorpus and SciFact |
| B14 | SciFact's chunk distribution, and the control it supports | 73.7 % single-chunk, 0.1 % with ≥4 chunks (recomputed, §5). **The control is the per-document invariant in §5, not this statistic** — the statistic alone cannot distinguish a correct aggregator from a broken one |
| B15 | `--baseline` Holm-corrects within each measure | `report.py:471` `holm_adjust`, `:620` prints family size |
| B16 | `--pair` would reject these arms | `report.py:732` `check_pool` exits on any document-set change |
| B17 | α-nDCG computes on FreshStack | `report.py` `--nugget-qrels`; nugget files present |
| B18 | The chunk budget is 250 | `DocumentBudget` = 50 (`BenchmarkQueryScenario.cs:42`); `--chunk-budget-multiplier` default 5 (`Program.cs:423`) |
| B19 | Adding a variant breaks no existing caller | `CollapseByDocId` has four production call sites (`MaxPassageAggregator.cs:56,75`; `BenchmarkQueryScenario.cs:383,451`) and 8 tests in `DocumentRankingTests.cs`. **`:383` is the `SearchSimilar` dedup path** — the shared use that forces the new-function requirement in §3 |
| B20 | Every β member is handled, and β = 0 is bit-identical | Design requirement, §2; enforced by short-circuit |
| B21 | All four corpora have keymap **and** qrels **and** snapshots | Checked together per corpus, not severally |
| B22 | The tail term's scale relative to the scale the ranking is decided at | `fs-2048-l100.chunks.trec`: rank-1 → rank-50 span 0.0747 mean / 0.0701 median, median top-10 adjacent gap 0.00236, against β·0.72 per tail chunk |
| B23 | Tail chunk scores are bounded away from zero, making each tail chunk a near-constant offset | Same file: min 0.5770, mean 0.7197, max 0.8552 |

## 9. Known issues, accepted

- **The deep-tail evidence is 512-window only.** FreshStack-2048 is the sole production-window arm;
  both density arms — FreshStack-512 (77.7 % of documents at ≥4 chunks) and NFCorpus (71.0 %) — are
  512/448 ingests, and §5 forbids re-ingest. At the production window NFCorpus would be 1.33
  chunks/doc with 0.2 % at ≥4, a second null control rather than an instrument. So a null on
  FreshStack-2048 cannot be triangulated against any deep-tail arm **at the window that ships**. This
  is the gap `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` item 1 already names.
- **The tail cap of 3 rarely binds on FreshStack-2048.** At a mean of 3.10 chunks per document the
  typical tail is 1–2 chunks; the cap binds for the 46.0 % with four or more.
- **Chunk-level MMR still runs on the primary arms** at λ = 1.00 only in the sense that
  diversification is off; the chunk pool is still whatever Qdrant returned under the shipped fusion
  triple. This experiment does not disentangle the fusion weights from the aggregation rule.
