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
the distribution is wide, and 44.4 % of FreshStack-2048 documents carry four or more chunks.

## 2. The signal

```
score(doc) = max_chunk + β · Σ(up to the next 3 highest chunk scores of that doc)
```

Sweep **β ∈ {0, 0.05, 0.10, 0.20, 0.35}**.

- **β = 0 must reproduce today's ranking bit-for-bit.** `max + 0.0 · tail` is exactly `max` in IEEE
  arithmetic for finite tails, but the implementation must not rely on that incidentally — it
  short-circuits, matching the discipline already in `ResultReranker` (which short-circuits its
  weighted mean to preserve ordering bit-for-bit) and `ResultDiversifier` (which reduces to
  `Take(topK)` at λ = 1.00). The baseline arm's validity depends on this.
- **The tail is capped at 3 chunks.** Uncapped, the term is dominated by document length: measured
  chunk cosines sit in 0.5553–0.8813 (FreshStack head-raw, n = 33,600), so a 20-chunk document sums
  to ~14 against a max of ~0.88, and β would be measuring length rather than coverage. Capping bounds
  the term at ~3 × 0.88 regardless of document size.
- **Calibration.** At β = 0.35 the two terms are roughly equal; at β = 0.05 the tail only breaks ties
  between documents whose best chunks are near-identical. That lower end isolates the mechanism.

## 3. Where it is computed

**No server change.** `SearchChunks` returns chunk-level rows and does not dedup by parent
(`BenchmarkQueryScenario.cs:36-41`); the harness owns the collapse. The work is:

1. A new aggregator function beside `DocumentRanking.CollapseByDocId`. **It must be a new function,
   not a modification.** `CollapseByDocId` serves two distinct callers — chunk collapse *and*
   same-`DocId` entity dedup on the `SearchSimilar` path — and changing it would silently alter
   `SearchSimilar` run files, which have no chunks and no tail.
2. A raw-chunk-hit dump in `BenchmarkQueryScenario`. The `chunks` list at `:396` is local and only
   its aggregated output is written; the existing `.chunks.diversity.json` sidecar records parent
   keys and counts but not scores. Raw `(ParentKey, Score)` hits in rank order are not persisted
   today, and every β is reconstructible from them.

**One query run per corpus serves every β.** The sweep is offline over the dumped hits, so β costs
nothing after the run that produced the pool.

Because the routed `SearchSimilar` path was proven byte-identical to collapsed chunks
(`2026-09-08-similar-via-chunks-design.md` §4), one measurement covers both RPCs. Shipping a winning
β is a separate spec against `ObjectSearchGrpcService.cs:364`.

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

| Corpus | mean chunks/doc | 1-chunk docs | ≥4-chunk docs | Role |
|---|---|---|---|---|
| FreshStack-2048 | 2.99 | 29.0 % | **44.4 %** | primary; production window; nugget qrels |
| FreshStack-512 | 10.66 | 11.2 % | **77.0 %** | deepest tail; nugget qrels |
| NFCorpus | 3.91 | **1.5 %** | **66.9 %** | densest by ≥4-chunk share; graded qrels |
| SciFact-2048 | 1.13 | **87.6 %** | 0.1 % | **null control** |

SciFact is a correctness check, not evidence: with 87.6 % of documents holding a single chunk there
is no tail to sum, so β must leave it essentially unmoved. **A material SciFact shift means the
aggregator is wrong**, and the sweep must not be interpreted until that is explained.

Snapshots, keymaps and qrels are on disk for all four — restores plus one query run each, no
re-ingest.

The chunks/doc figures above are from a reimplementation of the windowing rule
(`max_chars`/`step` = 2048/1792, 512/448) over each `beir/corpus.jsonl`, so they differ slightly from
the ingest-recorded means (2.99 vs 3.10; 10.66 vs 10.79; 3.91 vs 4.05) — `split_into_chunks` strips and drops empties.
They are indicative of the distribution's shape, which is what the design rests on, not exact counts.

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

## 8. Verified assumptions

Verified 2026-09-08 against the working tree and the corpora on disk.

| # | Assumption | Evidence |
|---|---|---|
| B1 | `Aggregate` is pure over `(ParentKey, Score)` | `MaxPassageAggregator.cs` — resolves keys, delegates to `DocumentRanking.CollapseByDocId` |
| B2 | The harness receives several chunks per parent | Diversity sidecars: 28.82 distinct parents per 50 chunks (`fs-2048` λ 1.00) = 1.74 per parent |
| B3 | Chunk scores are server-fused and cross-document comparable | `BenchmarkQueryScenario.cs:402` stores `r.Score` as returned by `SearchChunks` |
| B4 | Raw hits are discarded today | `chunks` at `:396` is local; only aggregated output reaches `TrecRunWriter` at `:293` |
| B5 | Raw hits suffice to reconstruct any β | Aggregation reads only `(ParentKey, Score)` in rank order |
| B6 | No server change is needed | `BenchmarkQueryScenario.cs:36-41` — `top_k` counts chunks, server does not dedup by parent |
| B7 | A `.chunks` result transfers to routed `.similar` | Reproduction check: byte-identical run files, `2026-09-08-similar-via-chunks-design.md` §4 |
| B8 | `LambdaChunks` is settable by env + restart | `docker-compose.yml:446`; line 444 documents the restart command |
| B9 | λ = 1.00 reduces MMR to `Take(topK)` | `ResultDiversifier.cs:72-75` — `Mmr(i) = 1.0·score − 0·maxSim` |
| B10 | The MMR confound is real | Measured distinct-parent table in §4 |
| B11 | Snapshots exist for all four corpora | 2 `.snapshot` files in each `*-qdrant-snapshots/` |
| B12 | Density figures | Computed distribution, §5 (with the reimplementation caveat stated there) |
| B13 | Only FreshStack has nugget qrels | `qrels.nugget.trec` present for both FreshStack arms, absent for NFCorpus and SciFact |
| B14 | SciFact is a valid null control | 87.6 % single-chunk, 0.1 % with ≥4 chunks |
| B15 | `--baseline` Holm-corrects within each measure | `report.py:471` `holm_adjust`, `:620` prints family size |
| B16 | `--pair` would reject these arms | `report.py:732` `check_pool` exits on any document-set change |
| B17 | α-nDCG computes on FreshStack | `report.py` `--nugget-qrels`; nugget files present |
| B18 | The chunk budget is 250 | `DocumentBudget` = 50 (`:42`), `--chunk-budget-multiplier` default 5 (`:39-41`) |
| B19 | Adding a variant breaks no existing caller | Two production call sites (`:418`, `:424`) plus 7 tests; **`CollapseByDocId` is shared with the `SearchSimilar` dedup path**, hence the new-function requirement in §3 |
| B20 | Every β member is handled, and β = 0 is bit-identical | Design requirement, §2; enforced by short-circuit |
| B21 | All four corpora have keymap **and** qrels **and** snapshots | Checked together per corpus, not severally |

## 9. Known issues, accepted

- **The tail cap of 3 rarely binds on FreshStack-2048.** At a mean of 2.99 chunks per document, the
  typical tail is 1–2 chunks; the cap binds for the 44.4 % with four or more. This is a property of
  the production window, not a defect — but it means the primary corpus exercises a shallower tail
  than FreshStack-512 or NFCorpus, and a null on it is weaker evidence than a null on those.
- **Chunk-level MMR still runs on the primary arms** at λ = 1.00 only in the sense that
  diversification is off; the chunk pool is still whatever Qdrant returned under the shipped fusion
  triple. This experiment does not disentangle the fusion weights from the aggregation rule.
