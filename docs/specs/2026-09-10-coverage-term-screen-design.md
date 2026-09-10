# Screening for a coverage term that is not a chunk count — design

**Date:** 2026-09-10
**Status:** approved, not executed.
**Parent gate:** `docs/plans/2026-09-GATE-chunk-coverage-phase2.md` — NO β QUALIFIES, 2026-09-09.
**Ranked-changes item:** item 15 (Tier 2), on the `ranked-changes-refresh` branch.

## 1. The question, and why this is a screen rather than a term

Phase 2 rejected `score(doc) = max_chunk + β · Σ(up to the next 3 highest chunk scores)` on
FreshStack-2048: all five arms negative on nDCG@10, monotone in β, top three Holm-significant at
p_adj = 0.0010. But it did not reject *coverage*. Its own diagnosis was that the tail term was a
chunk count in disguise — `corr(Σ tail, tail count) = 0.9970` over all 172,704 pooled pairs, and
because β multiplies the whole term the degeneracy is β-invariant, accounting for 91–92 % of
movement at every arm. What was falsified is **count-weighted promotion of multi-chunk documents**.

The gate names two candidate families that might not degenerate, and then sets the bar for anyone
proposing one (`2026-09-GATE-chunk-coverage-phase2.md:777-784`):

> "This is a scope limit, not an invitation. Nothing in this result suggests such a term would
> succeed… Anyone proposing one owes an argument that it is not the same signal in new clothes —
> and, since every arm here is an offline replay of a dump that still exists, **a cheap measurement
> rather than an argument.**"

This spec is that measurement. It defines **no ranking term and no β**. It asks only whether any
candidate statistic carries relevance signal that the chunk count does not. If none does, **item 15
closes** and the project never pays for another arm. That is the expected outcome, and a clean NO is
the most valuable thing this screen can produce.

The relevance data give it no encouragement to start with: P(relevant | pooled chunk count) runs
8.9 / 8.1 / 7.3 / 7.1 / 9.1 / 11.9 % for counts 1…6+, and mean pooled count is 3.360 for relevant
documents against 3.330 for irrelevant ones.

## 2. Design

Entirely offline against a dump that already exists. No Qdrant, no TEI, no ingest, no benchmark run.

### 2.1 Input and reuse

`~/repositories/iverson-benchmark-corpora/chunk-coverage-phase1-2026-09-09/runs/fs2048-pool.chunks.hits.tsv`
— the **accepted** Phase 1 pool dump (the sibling `refused-2026-09-09T1007/` carries a
`# REFUSED RUN — DO NOT SCORE` banner and is never read). Columns `queryId, parentKey, rank, score`;
369,600 data rows over 672 queries.

Loading, joining and tail extraction **reuse `tail_stats.py`** rather than being reimplemented:
`load_keymap`, `load_hits` (which checks `rank` against file order, so a sorted or concatenated dump
fails loudly), `resolve_by_doc`, `tail_scores` and its `TAIL_CAP = 3`. This matters beyond DRY: it
means the tail definition is inherited from the code that mirrors the shipped
`DocumentRanking.CollapseByDocIdWithTail`, not re-derived here where it could drift.

Labels: `freshstack-2048-2026-09-07/qrels.trec` (document-level, 20,209 rows, 672 queries), joined
through `keymap.json`. `qrels.nugget.trec` is **not** used — it is subtopic-level and this screen
asks about document relevance.

§2.6's control 2 additionally consumes the β = 0 arm's run file,
`chunk-coverage-phase2-arms-2026-09-09/fs2048-b0.chunks.trec`, through `tail_stats.scoped_to_run`.

### 2.2 Population: multi-chunk pairs only

The screen runs over the **92,758 (query, document) pairs holding ≥ 2 pooled chunks**, not all
172,704.

This is not a convenience restriction. `beta_invariant.py`'s check 1 *proves* that a pair with one
pooled chunk is byte-identical at every β — its tail is an empty slice, so `descending[0] + β·0` is
the same score at every arm. A coverage term provably cannot move those 79,946 pairs. Including them
would dilute every statistic with 46.3 % of pairs the term cannot affect, and it would leave two of
the six candidates undefined (§2.3). Restricting makes all six candidates comparable on one
population where the question is meaningful.

### 2.3 Candidate set

All six are functions of one pair's chunk-score vector, computable from the dump's four columns.
The first two are **controls**: if they do not reproduce their known values, the harness is wrong and
nothing else in the run is trustworthy (§2.6).

| Candidate | Definition | Role |
|---|---|---|
| `count` | number of pooled chunks | null baseline; the thing every candidate must beat |
| `tail_sum` | `Σ tail_scores` | Phase 2's actual term; must reproduce Pearson ≈ 0.9970 with `tail_count` on all 172,704 pairs (§2.6), and prints Spearman ρ ≈ 0.9292 against `count` under screen 1's own estimator and population |
| `norm_tail_sum` | `Σ (sᵢ / s_max)` over the tail | the gate's family 1, literal reading |
| `n_within_tau` | count of chunks with `sᵢ ≥ τ · s_max`, τ = 0.95 fixed, not swept | "only chunks close to the best one count" |
| `gap_1_2` | **`−(s_max − s_second)`** | peakedness; not count-shaped. Signed negative: the coverage hypothesis predicts a covering document matches in several places, so its tail sits *close* to its max — low raw gap is coverage-positive |
| `dispersion` | **`−`** population standard deviation of the pair's chunk scores | profile spread; not count-shaped. Signed negative for the same reason |

Every candidate is oriented so that **higher = more coverage** before any AUC is computed. The
orientation is fixed here, from the hypothesis, not chosen after seeing which direction scores better.

`gap_1_2` and `dispersion` are undefined for single-chunk pairs, which §2.2 excludes. τ is fixed at
0.95 rather than swept: a swept τ would multiply the comparison count and is exactly the
garden-of-forking-paths this screen exists to avoid.

**τ = 0.95 is close to the boundary that decides `n_within_tau`'s admission**, and the spec does not
argue the value, only the fixing. Measured ρ with `count` on the 92,758: 0.9967 (τ = 0.90), 0.9829
(0.92), 0.9216 (0.94), 0.8474 (0.95), 0.7207 (0.96), 0.3010 (0.98) — screen 1's 0.90 bar is crossed
between 0.94 and 0.95. The value is kept, and the whole curve is published in the verdict document
(§2.7) so the sensitivity is on the record rather than implicit.

**Stated prediction, recorded before the run:** `norm_tail_sum` is expected to fail screen 1. Tail
chunk scores are tight (within-count CV ≈ 5.8 %), so `sᵢ / s_max` is near-constant and its sum is
still a count. `gap_1_2` and `dispersion` are the only two expected to reach screen 2. Recording this
now makes the screen falsifiable against its author's expectations.

### 2.4 Screen 1 — degeneracy

For each candidate, Spearman correlation with `count` over the population. **A candidate with
|ρ| ≥ 0.90 is a chunk count in disguise and is disqualified without further testing.**

This is the gate's "owes an argument that it is not the same signal in new clothes" made mechanical,
and it is the cheaper screen: it needs no labels at all. The threshold is set at 0.90 against the value screen 1 itself
produces for `tail_sum` — Spearman ρ = 0.9292 against `count` on the 92,758 multi-chunk pairs, not
the gate's 0.9970, which is a different estimator on a different operand and population. The margin
over the bar is therefore 0.029, not 0.097; `tail_sum` is still disqualified, but the bar sits closer
to it than the gate's headline number suggests. A candidate must carry meaningful variation
independent of the count to be worth testing for signal.

### 2.5 Screen 2 — discriminability, conditional on `max_chunk`

Survivors only. The ranking already sorts by max-passage, so a term can only change an ordering among
documents whose max scores are close. A statistic that separates relevant from irrelevant documents
only because it proxies "this document matched well at all" would change nothing and must not score
as a win. **Conditioning on `max_chunk` is therefore load-bearing, not a refinement.**

Method: stratify pairs by `max_chunk` decile, compute the candidate's AUC (relevant vs irrelevant)
within each stratum, and pool the strata weighted by size. The reported quantity is the candidate's
**AUC advantage over `count`** on the same pairs, reported beside the candidate's **raw AUC**. Both
are required, because `AUC(count)` is not 0.5: within `max_chunk` deciles it is **0.4707** on the
judged population and **0.5631** on all-multi. Where it sits below chance, an advantage over it is
earned by any candidate that is merely uninformative — a uniform random candidate scores +0.0272 —
so beating the count is necessary but not sufficient, and beating chance is required too.

Two quantities are produced for that advantage, mirroring the project's existing gate convention of
reporting a CI bound beside a permutation p:

- **p-value** by permutation of the **candidate** within `(count, max-decile)` cells, leaving the
  relevance labels fixed, reusing `report.py`'s `PERMUTATION_SEED` and resample count. This tests the
  null actually of interest — candidate ⫫ relevance given `count` and `max_chunk` — by holding
  `count`'s own signal fixed by construction, which is what "signal the count does not carry" means.
  Shuffling labels instead drives both AUCs to 0.5 and centres the null on 0, which a null candidate
  does not satisfy.
- **95 % CI** by cluster bootstrap resampling **queries** (not pairs) — queries are the independent
  unit here, and a pair-level bootstrap would understate the interval by treating a query's pairs as
  independent draws. Same seed.

Both label populations are run, per §2.2's forced decision:

| Population | n | Relevant | Bias |
|---|---|---|---|
| Judged only | 10,874 | 3,168 (29.1 %) | pooling — these pairs already surfaced in some run |
| All multi-chunk, unjudged = irrelevant | 92,758 | 3,168 (3.4 %) | 88.3 % of the negative class unassessed |

### 2.6 Decision rule, pre-specified

**Harness validation, fail-closed.** Before any candidate is interpreted, two known values must
reproduce, each on its own recorded scope:

1. Pearson `corr(tail_sum, tail_count) ≈ 0.9970` over all **172,704** pooled pairs (the gate's scope
   — not the multi-chunk subset), where `tail_count = len(tail_scores(scores)) = min(count − 1, 3)`.
   The operand is the **tail** count, which saturates at 3, not the pooled count: measured Pearson
   against pooled `count` is 0.9795 and Spearman 0.9881, neither of which is the gate's figure.
2. `P(relevant | count)` = 8.9 / 8.1 / 7.3 / 7.1 / 9.1 / 11.9 % over the β = 0 arm's **33,600**
   top-50 slots (672 × 50), using `tail_stats.scoped_to_run` and the β = 0 run file, with **unjudged
   slots scored irrelevant** and `count` = pooled chunk count capped at 6+. The convention must be
   stated because only 8,689 of the 33,600 slots carry a `qrels.trec` row: judged-only scores
   32.5 / 36.0 / 32.8 / 29.9 / 28.7 / 35.2 %, roughly four times the gate's figures, and this control
   is fail-closed.

These are three different populations — 172,704, 33,600 and 92,758 — and each control must be
computed on the one it was recorded against. If either fails to reproduce, the run exits non-zero and
reports nothing else.

**GO for a candidate** requires all of:
- screen 1: |ρ with `count`| < 0.90; and
- screen 2, **in both label populations**, all three of:
  - raw AUC with a 95 % CI lower bound above **0.5** — the candidate beats chance;
  - AUC advantage over `count` with a 95 % CI lower bound above **0** — it also beats the count;
  - Holm-adjusted permutation `p_adj < HOLM_ALPHA` (0.05). The CI bounds are reported as magnitudes,
    not as tests; `report.py`'s `holm_adjust` consumes p-values only. The family size **m is the
    number of screen-1 survivors**, not the six candidates: `count` has no testable null against
    itself, and screen-1 casualties never reach screen 2. Conditioning m on screen 1 does not peek at
    the outcome variable, because screen 1 is label-free (§2.4).

**Overall NO-GO** — no candidate passes — closes item 15. A candidate passing in one population but
not the other is neither a GO nor a silent discard: it is reported as a population-dependent result,
naming which bias it rides.

### 2.7 Outputs

- `Iverson.Server/Iverson.LoadTest/scripts/coverage_screen.py`, with `test_coverage_screen.py`
  alongside, following the existing script + `test_*.py` idiom.
- A gate-style verdict document, `docs/plans/2026-09-GATE-coverage-screen.md`, in the shape of
  `2026-09-GATE-chunk-coverage-phase2.md`: every candidate's degeneracy correlation, its conditional
  AUC advantage in both populations, **each candidate's raw AUC and `AUC(count)` per `max_chunk`
  decile**, the **full ρ(τ) curve for `n_within_tau`** (§2.3), the harness-validation reproductions,
  and the GO/NO-GO. `AUC(count)` is what determines whether an advantage means anything and must be
  on the record.

## 3. Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| A1 | The dump has `queryId, parentKey, rank, score` | 369,600 data rows, 672 queries |
| A2 | `runs/` is the accepted dump | `refused-2026-09-09T1007/README.md` line 1: `# REFUSED RUN — DO NOT SCORE` |
| A3, A9 | The dump is the same population Phase 2 measured | **172,704** (query, doc) pairs — exactly the gate's "all 172,704 pooled pairs" |
| A4, A17 | Multi-chunk pairs are numerous; two candidates are undefined without them | 92,758 pairs ≥ 2 chunks (53.7 %); 79,946 single-chunk (46.3 %). Both counts match `beta_invariant`'s recorded check-1 and check-2 figures exactly |
| A5, A6 | `keymap.json` joins parentKey → docId and covers the dump | dict of 6,000 entries; **172,704 pairs resolved, 0 rows unresolved** |
| A7 | Document-level `qrels.trec` is the right label file | 20,209 rows / 672 queries, rel ∈ {0,1}; `qrels.nugget.trec` is subtopic-level (64,539 rows) |
| A8 | Both classes exist, but most pooled pairs are unjudged | multi-chunk: 10,874 judged (3,168 rel / 7,706 irrel), 81,884 unjudged (88.3 %) — this forced §2.5's dual-population rule |
| A11 | numpy/scipy are importable for AUC and correlation | numpy 2.5.2, scipy 1.18.1, `scipy.stats.mannwhitneyu` present |
| A12 | `report.py` exposes reusable Holm machinery | `report.py:115` `HOLM_ALPHA = 0.05`; `:471` `def holm_adjust(pvalues)` |
| A14 | The script + test idiom exists in `scripts/` | 6 script + `test_*.py` pairs (`beta_invariant`, `freshstack_nugget_qrels`, `multivector`, `report`, `similar_arms`, `tail_stats`); 5 scripts have none. The idiom is established, which is all §2.7 relies on |
| A15 | `tail_stats.py` already provides the loaders — reuse, do not reimplement | `load_keymap:42`, `load_hits:49`, `resolve_by_doc:116`, `tail_scores:133` with `TAIL_CAP = 3`, `scoped_to_run:171` |
| A18 | The tail definition is `descending.Skip(1).Take(3)` | `tail_stats.py:133-140`; `beta_invariant.py` docstring cites `DocumentRanking` for the same slice |
| A19 | The β = 0 run file control 2 consumes exists and matches the dump | `chunk-coverage-phase2-arms-2026-09-09/fs2048-b0.chunks.trec`, 33,600 rows; `scoped_to_run` over the Phase 1 dump recovers all 33,600 |
| A20 | **`AUC(count)` within `max_chunk` deciles is not 0.5** — the quantity §2.5's advantage is measured against | 0.4707 on the judged population, 0.5631 on all-multi. This is the mechanism §2.5 now guards against |
| A21 | `tail_sum` under screen 1's own estimator and population | Spearman ρ = 0.9292 on the 92,758 multi-chunk pairs — not the gate's Pearson 0.9970, which is a different operand, estimator and population |
| A22 | The permutation scheme has adequate cells to permute | verified in review §0 |

## 4. Out of scope

- **Family 2, coverage over distinct query aspects.** The dump records a parent key and a score per
  hit, with no chunk identity and no chunk vector, so aspect coverage cannot be reconstructed from
  it. Screening it needs a Qdrant read for chunk vectors or a fresh capture recording chunk ids, and
  a definition of "distinct aspect" that nobody has pinned down.
- **Any ranking term, any β, any calibration, any new corpus run.** This screen decides whether a
  term is worth specifying; it does not specify one.
- **A `matched / total chunks` density candidate.** It needs each document's total chunk count, which
  the dump does not carry.

## 5. Known issues accepted as out of scope

- **Pooling bias is present in both label populations and cannot be removed by this screen.** The
  judged-only population contains exactly the pairs some earlier run surfaced; the all-pairs
  population treats 88.3 % unassessed pairs as negatives. Requiring agreement across both is a
  mitigation, not a fix — a candidate that rides a bias common to both would still pass.
- **A NO-GO closes item 15 on FreshStack-2048 evidence alone**, at the 2048/1792 window with the
  shipped fusion triple. Phase 2's negative carries the same limitation; this screen inherits it and
  does not widen it.
