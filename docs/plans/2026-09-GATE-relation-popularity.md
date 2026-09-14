# Relation Popularity Signal — Gate Record

Spec: `docs/specs/2026-09-13-popularity-signal-measurement-design.md` (amended 2026-09-14, which
added Phase 0). Plan:
`.superpowers/sdd/2026-09-14-popularity-measurement-phase0-implementation-plan/`.

## Phase 0 — triage

Recorded 2026-09-14 on branch `popularity-measurement-phase0`, from HEAD `a7e042b`
(`move the semantic scholar citation fetcher into scripts and give it path flags` — Task 1).

**This section reports numbers and draws no conclusion.** Phase 0 is exploratory and selects
nothing (spec, "Phase 0 — triage"). The arm-structure choice among {lifetime arm only, lifetime +
decayed arm, abandon} is the reader's and **has not been made**. Nothing here may alter Phase 2,
which is locked against Phase 0 and Phase 1.

### Method

One script, one invocation, no server time. Every figure below is offline, computed over an
already-captured run file:

```bash
python3 Iverson.Server/Iverson.LoadTest/scripts/popularity_triage.py \
    --run    /home/ben/repositories/iverson-benchmark-corpora/scifact-2048-2026-09-06/runs/sci-2048.similar.trec \
    --qrels  /home/ben/iverson-benchmark-data/scifact-full/qrels/test.tsv \
    --counts scratchpad/popularity/citations.json \
    --out-dir docs/plans
```

| Input | Shape |
|---|---|
| Run | `sci-2048.similar.trec`, 6-column TREC, 15,000 rows, 300 queries × 50, 4,441 distinct documents |
| Qrels | BEIR 3-column header-bearing TSV, 339 judgements, 300 queries, all score 1, 283 distinct relevant documents |
| Counts | Task 1's `citations.json` — Semantic Scholar `citationCount` + `publicationDate` over all 5,183 corpus ids |

The full machine-readable record is `docs/plans/popularity-triage.json`
(md5 `ffa04fea7a75591c541b0a6faaebd62d`), untracked beside this file per the convention the tier-1
gate doc already uses. `Iverson.Server/Iverson.LoadTest/scripts/test_popularity_triage.py` pins the
arithmetic: **44 tests, all passing**, and five mutants of the core statistics (tie credit, the
eligibility order, 4b's pooling rule, the year-fallback midpoint, presence-by-truthiness) were each
confirmed to fail the suite.

Ties are handled the standard way throughout — a tied pair contributes 0.5. Citation counts tie
constantly, so this is load-bearing rather than a formality.

### The fetch: resolved / unresolved and the date split

| | Count |
|---|---|
| Corpus ids | 5,183 |
| **Resolved** (carry a citation count) | **4,879** |
| **Unresolved** (Semantic Scholar returned nothing) | **304** |
| Resolved, carrying `publicationDate` | **4,567** |
| Resolved, carrying `year` only | **292** |
| Resolved, carrying **neither** — the no-date stratum | **20** |

The 20 documents with neither date form are real, so the no-date stratum is non-empty by
construction rather than by accident. Its *in-pool* footprint — the size that matters to
measurement 4b — is **46 observations**, listed in the stratum table below.

### The corpus median that fixes `S`

**Corpus median citation count = 182**, over all 4,879 resolved counts. Range 0 … 75,298.

This executes the spec's pre-registered rule (`SaturationPoint` = the corpus median), it does not
revise it. It **supersedes the 235** quoted in the spec's "Parameters" section, which was computed
over the 1,288 ids in the partial count-only cache; 182 is the completed-fetch figure the rule
actually names. Two of the 4,879 resolved documents have a genuine count of **0** — a resolved zero,
not a missing value, and it is ranked as a zero everywhere below.

### The analysis population, and the 803 observations that are not in it

The unit of observation is the **(query, document) pair**, not the document: relevance is a property
of the pair, and a document retrieved for many queries is a separate observation in each.

An AUC over citation count cannot rank a document that has no count. The 304 unresolved ids are
therefore **excluded**, not zero-filled — and the exclusion is applied **before** the eligibility
predicate, not after. That order is forced: judged on the raw pool, a query whose only in-pool
relevant document is unresolved would count as eligible while having no positive left to rank, and
the per-query AUC term for it would be undefined.

| | Queries | n_pos | n_neg |
|---|---|---|---|
| Queries in run | 300 | — | — |
| Eligible on the **raw** pool | **275** | **311** | **13,439** |
| … in-pool observations with no resolved count | −803 | | |
| Eligible on the **count-bearing** pool — **the analysis population** | **259** | **293** | **11,928** |

**This is a divergence from the plan's pre-registered figures and it is recorded rather than
smoothed over.** The plan expected 275 eligible queries and a 311 × 13,439 = 4,179,529-pair
cross-product. Those are the **raw-pool** figures, and this run reproduces them exactly (275 / 311 /
13,439, with 15,129 within-pool pairs) — they were computed before accounting for in-pool documents
that the fetch could not resolve. 803 in-pool observations across the 275 raw-eligible pools, spread
over **260 distinct documents, every one of them in the cache's `unresolved` list**, carry no count.
Of the 16 queries lost, **every one had exactly one in-pool relevant document and that document was
unresolved**, so the whole pool drops. The analysed cross-product is therefore 293 × 11,928 =
**3,494,904** pairs, of which **13,450** are within-pool.

Both populations are printed by the script so the two are always reconcilable.

### Measurement 1 — AUC of citation count, relevant vs non-relevant

Two estimators are emitted. **They are not two ways of computing one quantity — they are two
different comparisons**, and their standard errors come from different places. Neither is a
refinement of the other and neither should be quoted without its label.

#### (i) Pooled — the **retrieved-corpus** form. NOT pool-matched.

| | |
|---|---|
| **AUC** | **0.5944** |
| n_pos × n_neg | 293 × 11,928 = **3,494,904** pairs |
| of which within-pool | **13,450** — so **99.62%** of pairs compare **across** queries |
| Hanley–McNeil SE | 0.017681 |
| **95% CI** | **[0.5598, 0.6291]** |

This pairs each relevant document against *every* eligible query's non-relevant documents, not only
its own pool's. That is what makes it the **retrieved-corpus** comparison, and what makes it the one
directly comparable to the **0.6201 already on record** — that figure is against *random corpus
documents*, close in kind to (i) and a weaker claim than (ii). The Hanley–McNeil SE is defined only
for this form.

**Its CI is anti-conservative and must be read as such.** Hanley–McNeil models n_pos × n_neg
independent pairs; the negative sample here spans 4,005 distinct documents reused across overlapping
pools, so the pairs are nowhere near independent and the true interval is wider than [0.5598,
0.6291].

#### (ii) Mean of the per-query AUCs — **this is the spec's pool-matched measurement 1**

| | |
|---|---|
| **Mean AUC** | **0.5890** |
| Queries (the independent units) | **259** |
| Between-query sd | 0.2146 |
| **95% CI** | **[0.5628, 0.6151]** |
| Queries with exactly **one** in-pool relevant document | **237 of 259** |

One AUC inside each eligible query's own pool, then averaged; the CI comes from the between-query
spread, **not** from Hanley–McNeil. This compares each relevant paper only against other papers in
its own retrieved pool, which is the honest version of the claim. The qualification travels with it:
**237 of the 259 terms are single-positive rank statistics**, not well-populated AUCs.

**Which one the arm-structure read should be taken on: estimator (ii), 0.5890 [0.5628, 0.6151].**
It is the pool-matched statistic the spec's measurement 1 names, and the pools it scores are the
pools `SearchSimilar` actually returns. Estimator (i) is recorded beside it because it — not (ii) —
is the like-for-like successor to the 0.6201 on record, and quoting (ii) against that 0.6201 would
be a cross-form comparison. The reader takes the arm-structure decision; this doc records which
estimator answers the question, not what the answer implies.

### Measurement 4a — AUC of publication date alone

The descriptive half of the age control, over the resolved-date population: **258 eligible queries**
after excluding **852** in-pool observations that carry no count or no date of either kind. Date is
taken as `publicationDate` falling back to `year` (a year-only document is placed at 1 July, its
year's midpoint). Reported in **both** forms, so it is never read across forms against measurement 1.

| Form | AUC | n_pos × n_neg | 95% CI |
|---|---|---|---|
| (i) pooled / retrieved-corpus | **0.5471** | 292 × 11,835 = 3,455,820 | [0.5128, 0.5815] — anti-conservative, same caveat as above |
| (ii) pool-matched mean | **0.5643** | 258 queries, sd 0.2710 | [0.5312, 0.5974] |

AUC > 0.5 means relevant documents are **newer** than non-relevant ones. Both forms sit above 0.5
with CIs excluding it, so date alone carries some separation on its own.

### Measurement 4b — within-stratum AUC of citation count

Measurement 4c's fixed strata: **5-year calendar bins** on `publicationDate` falling back to `year`,
with resolved ids carrying neither forming their own `no-date` stratum. Bins are **enumerated across
the observed calendar range**, so a bin with nothing in it appears as an empty row rather than
vanishing from the table. Same (query, document) observation population as estimator (i).

**The reported 4b number is one stratum-pooled Mann–Whitney statistic** — concordant pairs summed
over **within-stratum (relevant, non-relevant) pairs only**, across all strata, divided by the
number of such pairs. It is not a mean of per-stratum AUCs, and it is defined whatever any single
stratum contains.

| | |
|---|---|
| **Stratum-pooled AUC** | **0.6214** |
| Within-stratum pairs | **798,584** (vs 3,494,904 unstratified) |
| Strata enumerated | 14 — **5 degenerate**, contributing zero pairs |

The 2,696,320-pair difference is the cross-stratum comparison that stratifying removes: that
difference *is* the age confound.

#### Per-stratum table

| Stratum | Size | n_pos | n_neg | Pairs | AUC | |
|---|---|---|---|---|---|---|
| 1960–1964 | 6 | 0 | 6 | 0 | — | **degenerate** |
| 1965–1969 | 14 | 0 | 14 | 0 | — | **degenerate** |
| 1970–1974 | 12 | 0 | 12 | 0 | — | **degenerate** |
| 1975–1979 | 32 | 0 | 32 | 0 | — | **degenerate** |
| 1980–1984 | 67 | 1 | 66 | 66 | 0.2955 | |
| 1985–1989 | 145 | 4 | 141 | 564 | 0.5541 | |
| 1990–1994 | 362 | 5 | 357 | 1,785 | 0.4364 | |
| 1995–1999 | 869 | 14 | 855 | 11,970 | 0.4446 | |
| 2000–2004 | 1,952 | 32 | 1,920 | 61,440 | 0.5130 | |
| 2005–2009 | 3,135 | 86 | 3,049 | 262,214 | 0.6163 | |
| 2010–2014 | 3,888 | 100 | 3,788 | 378,800 | 0.6352 | |
| 2015–2019 | 1,684 | 50 | 1,634 | 81,700 | 0.6863 | |
| 2020–2024 | 9 | 0 | 9 | 0 | — | **degenerate** |
| **no-date** | **46** | **1** | **45** | **45** | 0.2556 | |

Degeneracy is expected here, not exotic: only 283 distinct relevant documents exist across the whole
5,183-document corpus, spread over the bins the publication dates span. A degenerate stratum
contributes **zero pairs**, never an undefined ratio — which is why "some stratum came out empty" is
not an abort condition. The load-bearing assertion is that the **pooled** pair count is non-zero; it
is 798,584.

The table is what makes the pooled number's composition auditable, and it is what measurement 4c's
within-stratum permutation will need. Note that **722,714 of the 798,584 pairs — 90.5% — sit in the
three bins from 2005 to 2019**, whose own AUCs are 0.6163, 0.6352 and 0.6863. The pooled statistic is
dominated by them; the five pre-2005 bins that hold any pairs, whose AUCs all sit at or below 0.5541,
contribute 75,825 pairs — 9.5% — between them. The stratum sizes sum to 12,221 = 293 + 11,928, i.e. the table partitions the
analysis population exactly.

### Summary of the numbers

| Measurement | Form | Value | 95% CI |
|---|---|---|---|
| 1 | (i) pooled, retrieved-corpus | 0.5944 | [0.5598, 0.6291] (anti-conservative) |
| 1 | **(ii) pool-matched mean** | **0.5890** | **[0.5628, 0.6151]** |
| 4a | (i) pooled, retrieved-corpus | 0.5471 | [0.5128, 0.5815] (anti-conservative) |
| 4a | (ii) pool-matched mean | 0.5643 | [0.5312, 0.5974] |
| 4b | stratum-pooled Mann–Whitney | 0.6214 | — (no SE defined for this form) |
| — | corpus median count (fixes `S`) | 182 | — |

No conclusion is drawn. In particular, this document does not claim what the relationship between
4b (0.6214) and measurement 1 (0.5890 / 0.5944) implies, does not rule on the age confound, and does
not select an arm structure.

### Open items this section hands forward

1. **The pre-registered pair counts do not survive contact with the fetch.** The plan's 275 / 311 /
   13,439 are raw-pool figures; the analysed population is 259 / 293 / 11,928. Anything downstream
   quoting 4,179,529 pairs is quoting the wrong cross-product.
2. **`S` = 182, not 235.** The spec's "Parameters" section still names 235 from the partial cache.
   The rule is unchanged; the number it produces is not.
3. **4b has no interval.** A stratum-pooled Mann–Whitney statistic over dependent, unequally-sized
   strata has no closed-form SE, and none is invented here. Measurement 4c's within-stratum
   permutation is what would supply one.
4. **16 queries were lost to unresolved relevant documents**, each of them a single-positive query.
   Whether that loss is ignorable or should be chased with a second fetch pass is not decided here.
