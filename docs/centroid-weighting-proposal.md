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

**It should not be exposed, because there is no stable trade-off to expose.** A second corpus
(NFCorpus, 2026-08-28) with 35x SciFact's judgment density reverses the asymmetry's direction: there
the centroid *helps* precision and is flat-to-negative on recall. Neither corpus resolves the effect
as significant. A knob whose sign flips between corpora and whose magnitude never clears noise is not
a trade-off a caller could set correctly.

**The same experiment found something that is not about the centroid at all, and matters more:**
MMR diversification at the shipped `Lambda = 0.70` costs `SearchSimilar` a measurable amount of
retrieval quality — R@50 -0.0216 (t = 4.46) and AP -0.0050 (t = 7.59) against `Lambda = 1.00`. On
SciFact that cost measured as exactly zero. See "The MMR finding" below.

## Experimental record — SciFact

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

> **⚠️ Retracted 2026-08-28.** The R@50 row does not survive an assumption-free test. **Only 5 of 300
> queries changed at all**; the other 295 deltas are exactly zero, and a paired *t*-test on a
> distribution that is 98.3% zeros badly violates its own normality assumption. A sign-flip
> permutation test over the same per-query deltas gives **p = 0.063**, against the *t*-test's
> p = 0.025. The observed −0.0167 also sits below this study's minimum detectable effect at 80%
> power (0.0188).
>
> This was the only evidence that the centroid buys recall, and it is the observation the design
> below was built on. It should not be treated as a finding. The honest summary across both corpora
> is that **the centroid has no demonstrated effect on ranking quality in either direction** — and
> NFCorpus, which appeared to contradict SciFact, in fact agrees with it.

### MMR λ

λ 0.70 → 1.00 (diversification off) leaves chunks R@50 and nDCG@10 **identical to four decimals**
(t = 0.00), while only 3 of 300 queries retain an identical full ordering. MMR reshuffles the tail
heavily and the reshuffling is metric-neutral. Expected: MMR optimises *diversity*; BEIR measures
*relevance*.

**The conclusion drawn from this — "λ's cost is zero" — was wrong, and only a denser corpus could
show it.** SciFact has 1.1 relevant documents per query, so there is almost nothing for
diversification to displace: the reshuffling moves non-relevant documents past each other. NFCorpus,
at 38.2 relevant per query, measures a real and highly significant cost. See "The MMR finding".

## NFCorpus results (2026-08-28)

BEIR NFCorpus test split: 3,633 documents, 323 queries, **12,334 judgments — 38.2 relevant
documents per query against SciFact's 1.1**, graded (11,758 at rel=1, 576 at rel=2). Ingested
through the same path and configuration as the SciFact reference run (512-char chunks, document and
query prefixes, titles composed): 14,729 chunks, 18,327 embed calls, 6.4 s/document.

Run files in `iverson-benchmark-corpora/nfcorpus-run-2026-08-27/runs/`.

Documents are **not** longer than SciFact's (1,591.8 vs 1,500.4 composed chars, 4.05 vs 3.96
chunks/document). This corpus tests *resolution*, not the long-document hypothesis.

### Fusion weight sweep — `SearchChunks`

| w = WCentroid/(WBase+WCentroid) | nDCG@10 | R@50 | AP |
|---|---|---|---|
| 0.000 | 0.3477 | **0.2515** | 0.1552 |
| 0.167 | 0.3487 | 0.2503 | **0.1562** |
| **0.333 — shipped** | **0.3522** | 0.2489 | 0.1556 |
| 0.500 | 0.3517 | 0.2479 | 0.1544 |
| 0.667 | 0.3446 | 0.2395 | 0.1502 |
| 1.000 | 0.3271 | 0.2260 | 0.1385 |

Paired per-query t against w = 0.333, Bonferroni |t| > 2.50 over four comparisons. The curve is
again cleanly unimodal, and w = 0.667 (R@50 t = -2.75) and w = 1.000 (t = -4.63) are significantly
worse. **The shipped constants survive a second corpus.**

### The centroid's direction reverses between corpora

| ablation w = 0.333 → 0 | SciFact | NFCorpus |
|---|---|---|
| nDCG@10 | +0.0021 (t = +0.36) | **-0.0045 (t = -1.58)** |
| R@50 | **-0.0167 (t = -2.25)** | +0.0026 (t = +1.08) |
| AP | +0.0052 (t = +0.76) | -0.0004 (t = -0.40) |

On SciFact, removing the centroid **cost recall** and was neutral for precision. On NFCorpus,
removing it **gains** recall and **costs** precision. Both nDCG@10 deltas and both NFCorpus deltas
are non-significant; only SciFact's R@50 result reached |t| = 2.25, and that does not survive
correction either.

**This is the finding that settles the proposed design** — and the re-analysis above settles it
harder. The design assumes a fixed direction: the centroid buys recall at the price of precision,
scaled by `top_k`. Two things now falsify that premise. On NFCorpus the trade runs the *other* way,
so a `top_k` schedule fitted to SciFact would be pointed backwards. And SciFact's recall benefit —
the observation that motivated the design at all — **does not survive a permutation test** (p = 0.063
on 5 changed queries out of 300). There is no stable sign to condition on because there is no
demonstrated effect to condition on.

### Statistical re-analysis (2026-08-28)

Every run file was re-analysed with exact paired *t*-tests, **sign-flip permutation tests** (no
normality assumption), 95% confidence intervals, Cohen's *d_z*, Holm-Bonferroni correction, and
minimum detectable effect at 80% power. No experiment was re-run: significance is analysis over the
preserved run files.

| check | outcome |
|---|---|
| *t*-test vs permutation, NFCorpus | agree to 3 decimals — normality was fine where deltas are dense |
| *t*-test vs permutation, SciFact R@50 | **disagree** (0.025 vs 0.063) — deltas are 98.3% zeros |
| Bonferroni vs Holm | no verdict changes on any comparison |
| bpref as a fourth measure | **useless here** — see below |
| MDE at n = 323 (NFCorpus) | 0.0080 nDCG@10 / 0.0069 R@50 / 0.0029 AP |
| MDE at n = 300 (SciFact) | 0.0152 nDCG@10 / 0.0188 R@50 / 0.0176 AP |

**bpref cannot help on BEIR.** It was added to guard against shallow judging, but bpref is defined
over *judged non-relevant* documents — and neither corpus has a single `rel=0` row (SciFact: 339
rows, all rel=1; NFCorpus: 12,334 rows, all rel≥1). With no judged negatives the formula reduces
algebraically to recall, and it reproduced R@50 to six decimal places. Any measure robust to
incomplete judgments needs qrels that record negatives; BEIR's do not.

**The *t*-test was the right tool except where the deltas are degenerate.** Its failure mode here is
specific and identifiable in advance: when a change moves only a handful of queries, the per-query
delta distribution is a spike at zero with a few outliers, and the *t*-test overstates significance.
Report a permutation test alongside whenever fewer than ~10% of queries change.

### Underpowered, or genuinely null?

This was the question NFCorpus was chosen to answer, and it answers it cleanly. **The instrument
works**: in the same run, on the same queries, it resolved the MMR effect at t = 7.59. A corpus that
can detect one effect at that confidence and still reports the centroid at |t| < 1.6 is not failing
to see a large centroid effect. **The centroid's effect on ranking quality is genuinely small**, in
both directions, on both corpora tested.

## The MMR finding

λ 0.70 → 1.00 (diversification off), same collection, same weights, no re-ingest:

| | nDCG@10 | R@50 | AP |
|---|---|---|---|
| `SearchChunks` | +0.0006 (t = +1.21) | **-0.0000 (t = 0.00)** | +0.0007 (t = +3.11) |
| `SearchSimilar` | +0.0021 (t = +2.34) | **+0.0216 (t = +4.46)** | **+0.0050 (t = +7.59)** |

On `SearchSimilar`, turning diversification off improves R@50 on **123 queries and harms 30**. This
comfortably survives Bonferroni correction and is the largest, most confidently measured effect this
project has produced.

**The split between the two endpoints is mechanically coherent, which is why it is believable.**
`SearchChunks` fetches 250 chunks and collapses them to documents by max-passage. Diversifying
*among chunks* mostly reorders chunks belonging to the same document, and the collapse then discards
that reordering — so the measured cost is exactly zero. `SearchSimilar` ranks one vector per
document, so MMR displaces whole documents directly, and every displaced relevant document is a
recall loss.

### What this does and does not license

It does **not** license setting `Lambda = 1.00`. The original reasoning still holds: MMR optimises
*diversity*, and BEIR measures *relevance* only. A benchmark that scores no credit for diversity
will always prefer diversification off; that is a property of the measure, not evidence that
diversity is worthless to callers.

What changed is the price tag. λ's cost was previously believed to be **zero**, on SciFact evidence,
and a free knob needs no justification. Its cost on `SearchSimilar` is now known to be real,
significant, and roughly **9% of R@50**. Whether that buys enough diversity to be worth it is a
product question this benchmark cannot answer — but it is now a question that has to be asked, and
answered with a diversity measure (α-nDCG or subtopic recall) rather than assumed.

The cheapest honest next step is not a λ change. It is to make the cost visible where it is paid:
`SearchChunks` demonstrably pays nothing, so if λ is ever tuned, it should be tuned per endpoint.

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

## Why this is not justified

The design trades precision for recall. **On SciFact there is nothing to trade; on NFCorpus the
trade runs backwards.**

- On SciFact, removing the centroid moves nDCG@10 by **+0.0021 (t = +0.36)** — the centroid is
  *neutral* for precision, not harmful.
- On SciFact, the nominal precision optimum (w = 0.167) versus the recall optimum (w = 0.333)
  differs by **+0.0051 (t = +1.30)** — not significant. Within resolution they are the same optimum.
- On NFCorpus, the w that **maximises R@50 is w = 0.000** — the centroid buys no recall at all — and
  the centroid instead contributes **+0.0045 nDCG@10 (t = +1.58)**, the opposite face of the trade.

A knob whose two settings measure the same on one corpus, and whose sign inverts on the next, is API
surface with no payload — and a `top_k` schedule fitted to either corpus would be actively wrong on
the other.

## The evidence bar

Build this only when a corpus shows a **significant negative nDCG@10 delta at the w that maximises
R@50**, *and* that direction replicates on a second corpus. SciFact gives +0.0021 at its R@50
optimum; NFCorpus gives -0.0045 at its own R@50 optimum (w = 0), which is the right sign but
**non-significant (t = -1.58)** and, more damagingly, arises from an R@50 curve that peaks where
SciFact's does not. Two corpora now disagree about the sign. The bar is further away than it looked
after SciFact alone, not closer.

Where that evidence plausibly exists: long, heterogeneous documents. SciFact documents are a title
plus a single-topic abstract (mean 1,500 characters), so the centroid is a mean over passages that
mostly say the same thing. A document covering several distinct topics should produce a centroid that
represents none of them well, and *that* is where a whole-document signal should begin costing
precision.

## Next experiments

**Question 1 (resolution) is answered — see "NFCorpus results" above.** The section below is kept
because it records why NFCorpus was chosen and how the corpora were measured; the FreshStack half is
still outstanding.

SciFact is exhausted as an instrument, for two reasons — and **measurement showed the second one
matters more, and that the first is not fixable within BEIR.**

1. **1.1 relevant documents per query.** With one relevant document and 50 slots, R@50 is nearly
   binary per query: it saturates at 0.93 and resolves almost nothing. This is why every effect in
   this project landed at ~0.005-0.017 with t < 2.5.
2. **Documents are short**, so the centroid was a degenerate copy of the object vector for 87% of
   documents at the 2048-char default.

### Measured document lengths — BEIR is a SHORT-document benchmark

Downloaded and measured 2026-08-28. **SciFact already has among the longest documents in BEIR**,
which inverts the original premise of this section:

| corpus | docs | mean chars | median | p90 |
|---|---|---|---|---|
| NFCorpus | 3,633 | 1,591.8 | 1,616 | 2,124 |
| SciFact (current) | 5,183 | 1,500.4 | ~1,400 | — |
| TREC-COVID | 171,332 | 1,118.7 | 1,175 | 2,052 |
| FiQA | 57,638 | 767.2 | 522 | 1,551 |
| **FreshStack (godot)** | **25,458** | **3,899.7** | **3,732** | **7,563** |

BEIR corpora are abstracts, forum posts and passages. **No BEIR corpus tests the long-document
hypothesis.** NFCorpus at 2048-char chunks is 86% single-chunk — reproducing the exact degeneracy
that made the centroid unmeasurable on SciFact.

### Question 1 — resolution: NFCorpus ✅ DONE 2026-08-28

| | SciFact | **NFCorpus** |
|---|---|---|
| test queries | 300 | 323 |
| corpus | 5,183 | **3,633** (cheaper than runs already done) |
| **relevant docs / query** | 1.1 | **38.2** |
| relevance levels | binary | **graded (1 and 2)** |

38.2 relevant documents per query turns R@50 from a near-binary indicator into a graded measure with
real headroom. This does **not** test long documents; it tests whether our null results are genuinely
null or merely underpowered — which is the cheaper and arguably more urgent question, since every
conclusion in this document rests on non-significant deltas.

**Outcome:** genuinely null for the centroid, and the added resolution immediately paid for itself by
exposing the MMR cost that SciFact measured as exactly zero.

Note the graded qrels (11,758 judgments at level 1, 576 at level 2) change how nDCG behaves relative
to SciFact's binary judgments.

### Question 2 — long documents: FreshStack (godot), sampled — STILL OUTSTANDING

Already converted and on disk at `iverson-benchmark-corpora/freshstack/`. Mean 3,899.7 chars, **2.6x
SciFact**, and only **32% single-chunk at the 2048-char default** versus SciFact's 87%. Its documents
are technical documentation — heterogeneous and multi-topic, which is precisely the case where a
whole-document centroid should represent no single topic well and begin costing precision.

Measured properties: 99 queries; **585 relevant (qid, docid) pairs after a `rel = max` collapse over
nuggets — 5.9 per query**, 5.4x SciFact's density; only **449 distinct relevant documents**, so
`sample_corpus.py --target-size` can cut the corpus to ~3,000 documents while keeping every judged
one. At 2.62 chunks/document that is roughly 9,900 embed calls, about 5 hours — affordable, unlike
the full 25,458-document corpus (~43 hours).

Two caveats, both known:

- **The qrels are nugget-scoped and must be collapsed before use.** One `(qid, docid)` pair appears
  once per nugget, and `ir_measures` resolves duplicates last-wins by file order, so a query-level
  reader silently misreads relevance. The fix is the documented `rel = max` collapse (~6 lines,
  mirroring upstream's own `qrels_query`). **Emit that file before any FreshStack scoring run.**
- 99 queries is fewer than SciFact's 300, so the paired test has less power per unit effect. That is
  acceptable only if the effect is larger here — which is the hypothesis being tested. If FreshStack
  also returns null, the honest reading is that the centroid's precision cost is small everywhere,
  not that the corpus was wrong.

α-nDCG, the measure FreshStack's subtopic structure actually calls for, remains uncomputable on this
machine (`pyndeval` has no Python 3.14 wheel and there is no C toolchain). R@50 and nDCG@10 over
collapsed qrels are valid and sufficient for this question.

## Open risk: deployed-artifact drift

The stale-image incident above is **still unaddressed in the harness** and will recur. Nothing in
the tooling establishes that the running API contains the code under test. A cheap guard: have
`benchmark-query` log the API's build identity, or assert a known symbol, and record it in the run's
metadata alongside the config label — so a run file carries evidence of what actually produced it.

The NFCorpus run worked around it by hand, and the workaround is worth recording as the interim
procedure: before the run, `Iverson.Embeddings.dll` was extracted from the live container with
`docker cp` and searched for the UTF-16 literals `search_document: ` and `search_query: `. Both were
present. This is a manual check that must be repeated after every rebuild until the guard exists.

## Reproducibility check

The NFCorpus reference configuration was re-run after the sweep, on a fully settled collection, as a
control. It reproduced **bit-identically** — same documents, same ranks, all 323 queries, on both
`SearchChunks` and `SearchSimilar` (delta 0.0000, t = 0.00, 323 ties on every measure).

This matters because the sweep's first run executed seconds after the ingest finished, while Qdrant
was still building indexes on the chunks collection (13,341 of 14,729 vectors indexed), and the
headline MMR result compares that first run against the last. The control rules out an indexing
artifact. Note also that the object collection reports `indexed_vectors: 0` — below Qdrant's
20,000-vector HNSW threshold it serves exact brute-force search, so `SearchSimilar` results are
exact rather than approximate.
