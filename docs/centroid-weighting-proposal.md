# Request-Scaled Centroid Weighting — Proposal and Experimental Record

**Status: not proposed for implementation.** The design below is recorded because the question will
recur, and because the evidence that would justify it is specific and cheap to gather. Today's
measurements say it should not be built. This document exists so that conclusion can be revisited
against data rather than re-derived from intuition.

## Summary

`ResultReranker` fuses a base similarity score with a document centroid — the mean of a document's
chunk vectors — as a weighted mean:

```
fused = (WBase·base + WCentroid·centroidCos + WDecay·decay) / (sum of weights present)
```

Measured on BEIR SciFact (2026-08-28), the centroid **improves recall and is neutral for precision**.
That asymmetry suggested exposing the trade-off to callers, since a caller fetching 10 results for
display wants different behaviour from one fetching 200 to feed a reranker.

**It should not be exposed, because there is no trade-off to expose.** The centroid costs nothing
measurable on precision, so a caller offered the choice could only opt out of a free benefit.

## Experimental record

All runs: BEIR SciFact standard test split (5,183 documents, 300 queries, 339 judgments), scored with
`ir_measures`. Run files are preserved in `iverson-benchmark-corpora/scifact-run-2026-08-26/runs/`.

### Ingest-side configurations

| run | config | chunks nDCG@10 / R@50 / AP | validity |
|---|---|---|---|
| `direct-ingest-baseline` | 2048-char chunks, no prefixes, no titles | 0.6820 / 0.9160 / 0.6377 | valid — symmetric (neither side prefixed) |
| `prefixed-titled` | + `search_document:`, + titles composed | 0.6954 / 0.9099 / 0.6545 | **prefix result invalid** (see below) |
| `chunked-512` | + 512-char chunks | 0.7072 / 0.9210 / 0.6625 | **prefix result invalid** |
| `chunked-512-apifixed` | as above, correct query prefix | 0.7042 / 0.9293 / 0.6642 | valid — the reference configuration |

**Two runs measured a configuration they were not labelled as.** The `iverson-api` container was
serving an image built 2026-08-24 from a checkout that no longer exists, two days before the
query-prefix code landed. Documents were prefixed (`ingest.py` is invoked directly); queries were
not. That is the asymmetric case the design explicitly warned against. A full SDD pipeline, four task
reviews, two design reviews, an implementation review and a whole-branch review all verified the
*code* and none verified that the code under review was the code running. **Any conclusion drawn from
a live stack must establish that the deployed artifact contains the change** — see "Open risk" below.

### Query-side configurations

All on the same 512-char collection with correct prefixes; only constants differ. No re-ingest.

| w = WCentroid/(WBase+WCentroid) | chunks R@50 | chunks nDCG@10 | chunks AP |
|---|---|---|---|
| 0.000 | 0.9127 | 0.7063 | 0.6694 |
| 0.167 | 0.9227 | 0.7093 | 0.6702 |
| **0.333 — shipped** | **0.9293** | 0.7042 | 0.6642 |
| 0.500 | 0.9227 | 0.6960 | 0.6561 |
| 0.667 | 0.9137 | 0.6836 | 0.6395 |
| 1.000 | 0.8953 | 0.6572 | 0.6104 |

**The fusion is scale-invariant** — a weighted mean normalised by the weight total, so only the ratio
matters. `(0.60, 0.30)` and `(0.20, 0.10)` are the same configuration. This is a one-parameter sweep,
not a grid.

Primary endpoint declared before running: R@50 on `SearchChunks`, Bonferroni |t| > 2.50 over four
comparisons against w = 0.333. Only w = 1.000 is significant (−0.0340, t = −3.45). The curve is
cleanly unimodal and **peaks at the shipped value**, with symmetric fall-off (w = 0.167 and w = 0.500
are both −0.0067). The constants were chosen, never measured; the guess was optimal.

### Centroid ablation (w = 0.333 → 0)

| metric | delta | t | direction |
|---|---|---|---|
| chunks nDCG@10 | +0.0021 | +0.36 | nothing |
| chunks AP | +0.0052 | +0.76 | nothing |
| **chunks R@50** | **−0.0167** | **−2.25** | **5 queries worse, 0 better** |
| similar R@50 | −0.0143 | −1.60 | not significant |

One-directional on chunks: no query gained. Mechanically coherent — a whole-document mean over
passages cannot sharpen which of the top 10 is best, but it keeps documents whose relevance is spread
thinly across several passages from falling out of the candidate set.

### MMR λ

λ 0.70 → 1.00 (diversification off) leaves chunks R@50 and nDCG@10 **identical to four decimals**
(t = 0.00), while only 3 of 300 queries retain an identical full ordering. MMR reshuffles the tail
heavily and the reshuffling is metric-neutral. Expected: MMR optimises *diversity*; BEIR measures
*relevance*. **This benchmark can measure λ's cost (zero) but structurally cannot measure its
benefit.** λ must not be tuned from it.

## The proposed design

Scale the centroid's share of the fused score by the requested result-set size:

```
w = f(top_k)   — low for small top_k (precision-oriented), higher for large top_k (pool-building)
```

**No request-shape change is required.** `top_k` already carries the caller's intent: a request for
10 is a display list, a request for 200 is a candidate pool for downstream reranking. The server can
condition on it directly.

This is strictly preferable to a new request field. `SearchSimilarRequest` and `SearchChunksRequest`
regenerate across five client languages plus their conformance drivers — a full spec → plan → SDD
cycle by this repo's history. A new field also converts a decision the system currently makes
correctly by construction into one every caller can get wrong silently, since a bad choice produces
no error, only worse ranking.

## Why this is not justified today

The design trades precision for recall. **On SciFact there is nothing to trade.**

- Removing the centroid moves nDCG@10 by **+0.0021 (t = +0.36)** — the centroid is *neutral* for
  precision, not harmful.
- The nominal precision optimum (w = 0.167) versus the recall optimum (w = 0.333) differs by
  **+0.0051 (t = +1.30)** — not significant. Within resolution they are the same optimum.

A knob whose two settings measure the same is API surface with no payload.

## The evidence bar

Build this only when a corpus shows a **significant negative nDCG@10 delta at the w that maximises
R@50**. On SciFact that delta is +0.0021 — pointing the wrong way for the argument.

Where that evidence plausibly exists: long, heterogeneous documents. SciFact documents are a title
plus a single-topic abstract (mean 1,500 characters), so the centroid is a mean over passages that
mostly say the same thing. A document covering several distinct topics should produce a centroid that
represents none of them well, and *that* is where a whole-document signal should begin costing
precision.

## Next experiments: a long-document corpus

SciFact is exhausted as an instrument. Two properties limit it, and the second matters more than
document length:

1. **Documents are short.** 87% were a single chunk at the 2048-char default, making the centroid a
   degenerate copy of the object vector — it could not be evaluated at all until the 512-char run.
2. **1.1 relevant documents per query.** With one relevant document and 50 slots, R@50 is nearly
   binary per query: it saturates at 0.93 and resolves almost nothing. This is why every effect in
   this project has been ~0.005–0.017 with t < 2.5.

### Recommendation: NFCorpus

| | SciFact | **NFCorpus** | TREC-COVID | FiQA | SCIDOCS |
|---|---|---|---|---|---|
| test queries | 300 | **323** | 50 | 648 | 1,000 |
| corpus | 5.2K | **3.6K** | 171K | 57K | 25K |
| **relevant docs / query** | 1.1 | **38.2** | 493.5 | 2.6 | 4.9 |

NFCorpus wins on the axis that actually binds. **38.2 relevant documents per query against SciFact's
1.1** turns R@50 from a near-binary indicator into a graded measure with real headroom — roughly a
35-fold increase in relevance signal per query, at comparable query count. Its corpus is also
*smaller* than SciFact's, so a full ingest is cheaper than the runs already completed, with no
sampling and therefore full comparability to published numbers.

TREC-COVID and Touché-2020 have the longest documents but only ~50 test queries, which is worse
statistical power than the setup that already failed to resolve most effects. They should not be
used for measuring small deltas.

**Verify before committing:** NFCorpus's document-length distribution is assumed longer than
SciFact's but has not been measured here. The pre-flight check is the chunk-count model already used
in this project — run the corpus through the chunk window and confirm the mean chunks/document is
comfortably above 1 and that `250 / mean-chunks-per-doc` stays above the 50-document budget. Note
also that NFCorpus qrels are graded (multi-level), unlike SciFact's binary judgments, which changes
how nDCG behaves.

## Open risk: deployed-artifact drift

The stale-image incident above is unaddressed and will recur. Nothing in the harness establishes that
the running API contains the code under test. A cheap guard: have `benchmark-query` log the API's
build identity, or assert a known symbol, and record it in the run's metadata alongside the config
label — so a run file carries evidence of what actually produced it.
