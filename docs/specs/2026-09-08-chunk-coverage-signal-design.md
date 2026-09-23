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

- **β = 0 must reproduce today's ranking bit-for-bit.** `max + 0.0 · tail` is exactly `max` in IEEE
  arithmetic for finite tails, but the implementation must not rely on that incidentally — it
  short-circuits, matching the discipline already in `ResultReranker` (which short-circuits its
  weighted mean to preserve ordering bit-for-bit) and `ResultDiversifier` (which reduces to
  `Take(topK)` at λ = 1.00). This is a **requirement on code that does not yet exist**, not a verified
  fact, so it is stated here rather than in §8. It is discharged after implementation by the β = 0 identity check
  specified in §3's Phase 1: an offline reconstruction at `--beta 0`, written to a separate output
  directory under the pool run's own label, must equal the in-run `.chunks.trec` byte for byte. That identity is the whole reason §3 puts the aggregator in one shared C# function rather
  than a second implementation.
- **The tail is capped at 3 chunks.** Uncapped, the term is dominated by document length rather than
  coverage. The term multiplies *fused* chunk scores (`ResultReranker.cs:36-52`), not raw cosines.

**β is not fixed in this spec.** It is derived in Phase 1 from the measured pool, because the quantity
it multiplies — the score of a document's 2nd–4th *pooled* chunks — appears in no artefact that exists
today. `*.chunks.trec` is the collapsed document ranking (one maximum per document, truncated at rank
50, `DocumentRanking.cs:40-44`), so it holds no tail chunk scores at all; earlier drafts of this spec
twice mistook its rank-50 truncation edge (0.5770) for a floor on chunk scores.

Phase 1 reports, from the dump: the **in-pool tail-depth histogram** (how many top-50 documents have
2, 3, 4 or more pooled chunks) and the **tail-score level `s`** — the mean score of the 2nd–4th pooled
chunks **of the documents that reach the top 50 of the β = 0 ranking**. That scoping is not arbitrary:
both numerators below are properties of the top-50 ranking, so the denominator must come from the same
population or the ratio is not a ratio of comparable quantities. Phase 1 also reports the **pool-wide**
figure beside it — the same mean over every parent in the 550-chunk pool, which is strictly lower — so
the gap between the two is on the record and a later reader can see which population the ladder used. Phase 2 sets the ladder from the ranking's own decision scale, measured on the same
arm:

- **tie-break** β = median top-10 adjacent gap ÷ `s`
- **parity** β = (rank-1 → rank-50 span) ÷ (3 · `s`)

with three intermediate values, log-spaced, and **β = 0 as the baseline endpoint**. The ladder spans
tie-break to parity inclusive and does **not** extend past parity: beyond it the term degenerates into
a count of tail chunks, and §6's "opposite conclusion" rule would misread a loss from count-ranking as
evidence about coverage. Using the document-level constants (span 0.0747, median gap 0.00236) with a
placeholder `s` = 0.72 gives tie-break ≈ 0.003 and parity ≈ 0.035 — **indicative only, to be
recomputed from the measured `s`.**

## 3. Where it is computed

**No server change.** `SearchChunks` returns chunk-level rows and does not dedup by parent
(`ObjectSearchGrpcService.cs:577-593`); the harness owns the collapse. The work is:

1. A new aggregator function beside `DocumentRanking.CollapseByDocId`. **It must be a new function,
   not a modification.** `CollapseByDocId` serves two distinct callers — chunk collapse *and*
   same-`DocId` entity dedup on the `SearchSimilar` path — and changing it would silently alter
   `SearchSimilar` run files, which have no chunks and no tail.
2. **A raw-chunk-hit dump in `BenchmarkQueryScenario`.** One record per hit:
   **`(QueryId, ParentKey, Score)`, in per-query rank order.** The `chunks` list at `:396` is rebuilt
   per query inside `RunChunksAsync`, so query identity exists in memory and only needs naming.
   `QueryId` is not optional — `TrecRunWriter.WriteAsync` requires it per ranked list
   (`TrecRunWriter.cs:12-16`), and aggregation is per-query, so an unpartitioned dump would fuse one
   document's chunks across different queries, which is the specified rule at no β. The existing
   `.chunks.diversity.json` sidecar records parent keys and counts but not scores; raw hits are not
   persisted today.

3. **A `benchmark-aggregate` command** (`Program.cs` dispatch, beside `benchmark-query`). Reads the
   dump plus `keymap.json`, applies the new aggregator at a `--beta` flag, and writes
   `<label>.chunks.trec` plus `<label>.meta.json` — without the sidecar every `report.py` compare
   block prints `BUILD UNKNOWN` (`report.py:589-591`). Sharing one aggregator with the in-run path is
   what makes the β = 0 identity check a test of the shipping code rather than of a second
   implementation; a Python reimplementation would additionally have to reproduce `keyMap` resolution
   and unresolved-parent exclusion (`MaxPassageAggregator.cs:50-53`), truncation *after* collapse
   (`DocumentRanking.cs:40-44`) and the first-seen tie rule (`:36`). **Build attribution:** the
   command cannot perform `benchmark-query`'s `GET /build` (`BenchmarkQueryScenario.cs:169-201`) and
   stay offline, so it copies the pool run's `composite` into the sidecar — all β arms then agree and
   no `BUILD UNKNOWN` / `BUILD MISMATCH` banner fires — and records the aggregator's own build under a
   separate key. `report.py` reads only `composite` (`:184-202`), so that second key is documentation,
   not an enforced check; it exists so a re-aggregation under a changed aggregator is distinguishable.

**The work runs in two phases, because β cannot be calibrated before the pool is measured.**

- **Phase 1 — measure.** Land items 1–3, then one FreshStack-2048 query run at multiplier 11. From its
  dump, report the in-pool tail-depth histogram and the tail-score level `s` (§2). This is a run the
  experiment needs regardless; it is ordering, not extra cost. It also discharges the one design
  dependency no assumption covered — how deep the tail actually is *in the pool*, as opposed to in the
  corpus.

  **β = 0 identity check, before any β arm is scored.** Run `benchmark-aggregate --beta 0` with
  `--config-label` equal to the pool run's and a **different `--output-dir`**, then require the emitted
  `.chunks.trec` to be byte-identical to the in-run one. The distinct output directory is what makes
  this possible: `TrecRunWriter` writes the config label as column 6 (`TrecRunWriter.cs:31`) and derives
  the output path from it, so an equal label in the same directory would overwrite the comparand rather
  than reproduce it. This form also exercises the `.meta.json` write without clobbering the pool run's
  sidecar. **A failure here blocks Phase 2** — it means the shared aggregator does not reduce to today's
  behaviour at β = 0.
- **Phase 2 — calibrate and sweep.** Fix the β ladder from Phase 1's numbers per §2, then run the
  remaining arms. **One query run per corpus serves every β**, since the sweep is offline over the
  dumped hits.

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
tails (26.3 % of SciFact documents do), and fails loudly on the real bug classes reachable there — an
off-by-one in the tail slice, or self-inclusion of the max in its own tail. **It cannot check the
tail's ordering rule**: a one-chunk document has exactly one ordering, and at λ = 1.00 stream order
equals fused-descending order (`ResultReranker.cs:59`, `ResultDiversifier.cs:71-75`), so a tail taken
in stream order is byte-identical to a correct one on every primary arm. §6 carries the check that
covers it. **A violation means the aggregator is wrong**, and the sweep must not be interpreted
until it is explained. SciFact earns its place because 73.7 % single-chunk documents make violations
most visible there, not because β is expected to leave the corpus unmoved.

Snapshots, keymaps and qrels are on disk for all four — restores plus one query run each, no
re-ingest.

## 6. Gate

**Ordering check, on the λ = 0.70 arm.** Because stream order and score order coincide at λ = 1.00
(`ResultReranker.cs:59`, `ResultDiversifier.cs:71-75`), the tail's ordering rule is only falsifiable
where MMR reorders. On the FreshStack-2048 λ = 0.70 arm — which therefore dumps its pool like Phase 1's
and reports its own tail-depth histogram — assert, **for every document in the β-arm's top 50 that
contributes ≥ 4 chunks to the pool**, that the tail term equals `β · Σ` of the 2nd–4th **largest** chunk
scores recomputed independently from the dump. The tail term is obtained by differencing a β = 0 and a
β = parity aggregate of the same dump: `score_β − score_0` is exactly `β · Σ(tail)`.

The domain is stated as the top 50 rather than the whole pool because that is what is observable —
`<label>.chunks.trec` holds one row per document truncated to the top 50 per query
(`DocumentRanking.cs:40-44`), so a pool document ranking 51st or lower has no persisted score to assert
against.

**The check must report the number of (query, document) pairs it asserted over, and a count of zero is
a failure of the precondition, not a pass.** The λ = 0.70 tail-depth histogram gives the expected count
before the check runs, so vacuity is detected against a prior expectation rather than inferred from the
check's own silence. **This must pass before the confirmation arm is read as a gate result** — otherwise
a stream-order bug presents exactly as "the finding does not survive the shipped configuration".

**Primary: nDCG@10 on FreshStack-2048 at λ = 1.00**, baselined at β = 0, Holm-corrected across the
ladder's non-zero β values — **five**, under §2's tie-break + three intermediates + parity construction.
`report.py` derives the family from the `--run` count (`:678-681`), so the invariant the run must satisfy
is that **the printed family size equals the number of non-zero β arms passed as `--run`**; stating the
count that way means a later change to the ladder's length cannot re-stale this section. A β
**qualifies** if its nDCG@10 delta against β = 0 is **positive** and Holm p_adj < 0.05. A
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

Whether coverage helps at chunk budgets other than **550** (`DocumentBudget` 50 ×
`--chunk-budget-multiplier` 11), and whether it interacts with the fusion weights. Both are held
fixed. A null is therefore scoped to this budget and this triple. The budget is not a nuisance
parameter here: it determines how many of a document's 2nd–4th chunks are in the pool at all, which
is the quantity the tail term reads.

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
| B5 | The dump's field set covers all three of its consumers | Aggregation reads `(ParentKey, Score)` in rank order; **run-file emission additionally requires `QueryId`** (`TrecRunWriter.cs:12-16`); Phase 1's histogram and tail-score level need per-query partitioning. `(QueryId, ParentKey, Score)` covers all three |
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
| B18 | The chunk budget is **550** on every arm | `DocumentBudget` = 50 (`BenchmarkQueryScenario.cs:42`); multiplier 11, stated in §3's Phase 1 bullet and §7, matching the pool `fs-2048-l100.meta.json` was produced at (`chunkBudgetMultiplier: 11`). This is a deliberate **deviation** from the Tier 1 policy at `2026-09-GATE-tier1-defaults.md:33-36`, which is per-corpus — "**5** on SciFact (1.27 chunks/doc), **11** on both FreshStack arms" — adopted here so tail depth is comparable across arms. B24 confirms 11 admits all four. The harness default of 5 (`Program.cs:423`) is **not** what these arms use |
| B19 | Adding a variant breaks no existing caller | `CollapseByDocId` has four production call sites (`MaxPassageAggregator.cs:56,75`; `BenchmarkQueryScenario.cs:383,451`) and 8 tests in `DocumentRankingTests.cs`. **`:383` is the `SearchSimilar` dedup path** — the shared use that forces the new-function requirement in §3 |
| B21 | All four corpora have keymap **and** qrels **and** snapshots | Checked together per corpus, not severally |
| B22 | The scale the ranking is decided at, on the primary arm | `fs-2048-l100.chunks.trec` (**document-level** maxima): rank-1 → rank-50 span 0.0747 mean / 0.0701 median, median top-10 adjacent gap 0.00236. Sound as ranking properties; says nothing about chunk scores |
| B23 | **FAILED and replaced.** The tail-score level is **unmeasured** — no artefact holds raw chunk-hit scores | `fs-2048-l100.chunks.trec` holds one *maximum* per document, truncated at rank 50 (`DocumentRanking.cs:40-44`); its 0.5770 minimum is that truncation edge, not a chunk-score floor. Phase 1 measures `s` directly (§2, §3) |
| B24 | `ChunkBudgetGuard` admits all four arms at multiplier 11, and would refuse FreshStack-512 at 5 | `ChunkBudgetGuard.cs:31-40` refuses when `topK / chunksPerDoc < DocumentBudget`. At multiplier 5: SciFact 196.7 ✓, FreshStack-2048 80.5 ✓, NFCorpus 61.7 ✓, FreshStack-512 **23.2 ✗** (`ChunkBudgetGuardTests.cs:14` asserts this case) |

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
