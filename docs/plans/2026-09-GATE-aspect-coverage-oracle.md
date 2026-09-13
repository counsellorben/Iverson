# Aspect-Coverage Oracle — Gate Verdict

Measurements recorded 2026-09-13 from the `aspect-coverage-oracle` worktree at HEAD `98ad12a`
(`add aspect_oracle.py: three oracle rankings over the FreshStack-2048 run`), which carries Task 1's
instrument from `docs/plans/2026-09-13-aspect-coverage-oracle-implementation-plan.md`. The design is
`docs/specs/2026-09-13-aspect-coverage-oracle-design.md`. The parent result this builds on is the
coverage-term screen's NO-GO, whose gate document lives on the unmerged `coverage-term-screen` branch;
the prior chunk-coverage phases are `docs/plans/2026-09-GATE-chunk-coverage.md` and
`docs/plans/2026-09-GATE-chunk-coverage-phase2.md`.

This gate asks one question, pre-registered in spec §2.6: **is there enough aspect-coverage headroom on
FreshStack-2048 to be worth designing a term for?** It measures a ceiling — the best any mechanism could
do by reordering the already-retrieved 50 on aspect structure — not a term.

---

## Verdict: GO — G − R = +0.0609 α-nDCG@10, 95 % CI [+0.0541, +0.0678]

Spec §2.6's rule, as carried into the plan's Global Constraints:

> **GO** iff **G − R ≥ +0.02** on the 672-query primary population **and** its 95 % CI lower bound > 0.
> **NO-GO** otherwise, including any point estimate below +0.02 whatever its p-value.

**G − R = +0.0609** on the 672-query primary, which is **≥ +0.02**. Its **95 % CI lower bound is
+0.0541**, which is **> 0**. Both conditions hold, so the rule returns **GO**.

The bar was fixed before the numbers existed and is applied mechanically here. Spec §2.7 predicted NO-GO
was the likelier outcome; it was not the outcome. The ceiling is roughly **three times** the materiality
bar.

**A − R is reported and does not gate** (spec §2.6). **R − B is context only** and is not subject to the
bar (spec §2.3).

### The three quantities, 672-query primary

| Quantity | Δ α-nDCG@10 | 95 % CI | paired *t* | perm. *p* | Holm p_adj (3) | queries changed | Role |
|---|---|---|---|---|---|---|---|
| **G − R** | **+0.0609** | **[+0.0541, +0.0678]** | t = 17.54, p = 0.0000 | 0.0002 | **0.0006** significant | 324 / 672 (48.2 %) | **gates** |
| A − R | +0.0570 | [+0.0503, +0.0636] | t = 16.76, p = 0.0000 | 0.0002 | 0.0006 significant | 293 / 672 (43.6 %) | reported, does not gate |
| R − B | +0.3364 | [+0.3168, +0.3560] | t = 33.72, p = 0.0000 | 0.0002 | 0.0006 significant | 593 / 672 (88.2 %) | context only |

Cohen's d_z: 0.677 (G − R), 0.647 (A − R), 1.301 (R − B). MDE @ 80 % power: 0.0097, 0.0095, 0.0280.

### What the split between G − R and A − R says

A − R = +0.0570 is **93.5 %** of G − R = +0.0609 (derived: 0.056955 / 0.060947). Nearly the whole ceiling
is reachable by ordering on a per-document **aspect count** alone; the α-discounted greedy adds **+0.0040**
beyond it (derived: 0.060947 − 0.056955). Spec §2.6 anticipated
the opposite split ("headroom *but not through a per-document aspect count*") as the interesting case —
this is the other branch: the simpler signal captures almost all of the available headroom. That is a
statement about the ceiling, not about any realised term.

**Both figures are ceilings, and spec §2.6's own precedent is that a realised term captures single-digit
percentages of its oracle** (the reranker gate realised 3.7 % of its +0.2256). Applied here that would
predict a realised gain around +0.002. The GO says the ceiling clears the bar and the term is worth
*designing* — it does not predict that a design will clear anything. Designing the term is explicitly out
of scope (spec §4).

---

## Inputs and provenance

| Input | Path | Provenance |
|---|---|---|
| Run B | `~/repositories/iverson-benchmark-corpora/chunk-coverage-phase2-arms-2026-09-09/fs2048-b0.chunks.trec` | the chunk-coverage Phase 2 β = 0 arm; `fs2048-b0.meta.json` records composite `3ffafcd26416ed30`, aggregator `c5f976dc8d6b9497`, recorded 2026-09-10T03:01:01Z. 33,600 rows = 672 queries × 50 |
| Relevance qrels | `~/repositories/iverson-benchmark-corpora/freshstack-2048-2026-09-07/qrels.trec` | FreshStack-2048 ingest, 2026-09-07 |
| Nugget qrels | `~/repositories/iverson-benchmark-corpora/freshstack-2048-2026-09-07/qrels.nugget.trec` | same |
| Instrument | `Iverson.Server/Iverson.LoadTest/scripts/aspect_oracle.py` @ `98ad12a` | stdlib only; 27 pytest tests pass |
| Statistics | `Iverson.Server/Iverson.LoadTest/scripts/report.py --pair` | invoked verbatim; **no new statistics code was written** (spec §2.5) |

Nothing was re-retrieved. No container was started. No server code was touched.

**Outputs**, all written to `~/repositories/iverson-benchmark-corpora/aspect-oracle-2026-09-13/`:
`oracle-R.trec`, `oracle-A.trec`, `oracle-G.trec` (3,173,071 bytes each, 33,600 lines each),
`qrels.610.trec`, `qrels.nugget.610.trec`, `aspect-summary.tsv`, plus the two scoring transcripts
`report-primary.txt` and `report-610.txt`.

> **Every figure in this document is transcribed from `report-primary.txt` and `report-610.txt` in that
> corpora directory. That directory is _not_ a git repository** — it is un-versioned bulk data. The
> transcripts are the only record of these numbers; they are not reproducible from anything inside this
> repository without re-running Steps 1, 2 and 4 of the implementation plan's Task 2 against the same
> inputs. The oracle run files carry md5 `b980ec1da3eaf04e686e7dfa961a9a7a` (R),
> `d08d46ce751d79edb59907acd38f1130` (A), `b5e677499d6251482dccc71dff74d047` (G).

### Metric parameters, pre-registered in spec §2.4

α-nDCG@10 via `pyndeval` through `report.py --nugget-qrels`, at pyndeval's defaults: **α = 0.5**,
**rel = 1**, **judged_only = False**. Permutation: 10,000 sign flips at seed 20260831. Holm across a
declared family of 3 pairs (`G=R`, `A=R`, `R=B`).

---

## The four `[scores]` blocks

### 672-query primary — `report-primary.txt`

| Run | nDCG@10 | R@50 | AP | α-nDCG@10 |
|---|---|---|---|---|
| `fs2048-b0.chunks.trec` (B) | 0.2932 | 0.5446 | 0.2147 | 0.3551 |
| `oracle-R.trec` | 0.6750 | 0.5446 | 0.5446 | 0.6916 |
| `oracle-A.trec` | 0.6750 | 0.5446 | 0.5446 | 0.7485 |
| `oracle-G.trec` | 0.6750 | 0.5446 | 0.5446 | **0.7525** |

B's build is `3ffafcd26416ed30`; the three oracle runs report build `unknown`.

### 610-query sensitivity — `report-610.txt`

| Run | nDCG@10 | R@50 | AP | α-nDCG@10 |
|---|---|---|---|---|
| `fs2048-b0.chunks.trec` (B) | 0.3229 | 0.6000 | 0.2366 | 0.3912 |
| `oracle-R.trec` | 0.7436 | 0.6000 | 0.6000 | 0.7619 |
| `oracle-A.trec` | 0.7436 | 0.6000 | 0.6000 | 0.8246 |
| `oracle-G.trec` | 0.7436 | 0.6000 | 0.6000 | **0.8290** |

---

## The three `[pool]` lines

Identical in both transcripts, because **the population restriction filters the qrels, never the run
files** (spec §2.5, A23) and `check_pool` reads the run files:

```
[pool] oracle-G.trec  vs  oracle-R.trec: 672 queries, set changed on 0, sequence differs on 324 (48.2%)
[pool] oracle-A.trec  vs  oracle-R.trec: 672 queries, set changed on 0, sequence differs on 294 (43.8%)
[pool] oracle-R.trec  vs  fs2048-b0.chunks.trec: 672 queries, set changed on 0, sequence differs on 593 (88.2%)
```

`set changed on 0` in all three is the construction holding: every oracle is a **permutation of B's own
50**, so no document ever enters or leaves a pool. All three clear `POOL_MIN_REORDERED_FRACTION` (25 %)
with margin. These three figures match the design's A13 exactly (324 / 294 / 593 of 672), reproduced
independently before scoring began.

---

## 610-query sensitivity

Same three declared pairs, the same **unfiltered** run files, and the two **filtered** qrels files. All
`[compare]` denominators read `/ 610`, confirming the restriction took.

| Quantity | Δ α-nDCG@10 | 95 % CI | paired *t* | perm. *p* | Holm p_adj (3) | queries changed |
|---|---|---|---|---|---|---|
| **G − R** | **+0.0671** | **[+0.0598, +0.0745]** | t = 17.96, p = 0.0000 | 0.0002 | 0.0006 significant | 324 / 610 (53.1 %) |
| A − R | +0.0627 | [+0.0556, +0.0699] | t = 17.13, p = 0.0000 | 0.0002 | 0.0006 significant | 293 / 610 (48.0 %) |
| R − B | +0.3706 | [+0.3510, +0.3903] | t = 37.06, p = 0.0000 | 0.0002 | 0.0006 significant | 593 / 610 (97.2 %) |

Cohen's d_z: 0.727, 0.694, 1.500. MDE @ 80 % power: 0.0105, 0.0103, 0.0280.

**Per spec §2.5 the 672-query primary decides and this is context.** The sensitivity agrees in direction
and magnitude and would return the same GO on its own (+0.0671 ≥ +0.02, CI lower bound +0.0598 > 0). The
point estimates rescale by roughly 672/610 = 1.102, as expected: the 62 dropped queries have no relevant
document anywhere in their 50 and contribute exactly 0 to every delta under all four rankings. The CIs are
not derivable arithmetically from the primary's and were measured, not scaled.

---

## Validity checks

### Spec §2.8's writer self-check — PASSED

**R − B on α-nDCG@10 is +0.3364 with 593 / 672 queries changed.** It is non-zero, so the score column in
the oracle run files encodes the new ranking and not B's original order. This is the check that matters:
A19 records that `ir_measures` re-sorts by the **score column** and never reads the rank column, while
`check_pool` reads **file order** — so a non-zero `[pool] sequence differs` line cannot clear it, and did
not have to here.

### Spec §2.4's R@50 invariance check — PASSED

**R@50 is 0.5446 in all four `[scores]` blocks** of the primary, and 0.6000 in all four of the 610
sensitivity. The pools are identical by construction, so any difference would have meant a run file lost
or gained a document. None did. R@50 is a self-check, not a result.

### The seven expected-zero `[compare]` blocks — as predicted

`report.py` scores four measures per pair, so twelve `[compare]` blocks are printed per population. Seven
correctly read `delta +0.0000` with `0 /` queries changed:

- **R@50 for all three pairs** — identical pools at depth 50.
- **nDCG@10 and AP for `G vs R` and `A vs R`** — R, A and G place the identical relevant set in the
  identical positions and differ only in the order *within* that prefix, which binary-relevance measures
  cannot see.

Exactly seven zeros appeared, in exactly those seven blocks. **Only α-nDCG@10 separates the three
oracles**, which is the design's whole point. The remaining two non-gating blocks are informative for
context: `R − B` is +0.3818 nDCG@10 (CI [+0.3639, +0.3998]) and +0.3299 AP (CI [+0.3130, +0.3468]) on the
primary; +0.4207 and +0.3634 on the 610.

### Pool count 294 vs compare count 293 on `A vs R` — resolved, not a defect

The `[pool]` line reports A's sequence differing from R's on **294** queries while the α-nDCG@10
`[compare]` block reports **293** queries changed. The two count different things: the pool line counts
*order* differences, the compare block counts *score* differences. The gap is exactly one query,
**`78409544`**, whose top 10 reorders but whose α-nDCG@10 is numerically identical under both rankings —
A and R swap ranks 3 and 4 between a document covering `{_2}` and one covering `{_0, _3}`, and at α = 0.5
against a rank-1 document already covering all four nuggets the two orders earn the same discounted gain.
A genuine metric tie. Verified directly against the nugget qrels; no other query in the 294 has an
identical top 10.

### `BUILD UNKNOWN` on all twelve compare blocks — expected, and it fails open

Every `[compare]` block prints `!! BUILD UNKNOWN: at least one of these runs has no build sidecar.` The
three oracle runs are **synthesised from B by permutation**, not retrieved, so they have no `.meta.json`
and no composite to compare. This is not a build mismatch being tolerated: all four runs derive from the
one retrieval whose composite is `3ffafcd26416ed30`, by construction. It is recorded because this
project's convention is that a build-identity check **fails open** and a silent open failure is the thing
that has bitten before.

### The `FEW QUERIES CHANGED` warnings

Seven blocks also carry `!! FEW QUERIES CHANGED: only 0.0% of paired queries differ`. Those are exactly
the seven structurally-zero blocks above. No gating block carries the warning: G − R changed 48.2 % of
queries, A − R 43.6 %, R − B 88.2 %.

---

## What this result does and does not license

**Licensed.** Designing an aspect-coverage term for FreshStack-2048 is worth a spec. The ceiling exists,
it is roughly 3× the materiality bar, and it is not an artifact of the relevance oracle underneath it —
G − R and A − R are measured with R held fixed, so the +0.3364 that relevance alone buys is subtracted
off, which is precisely the error spec §1.1 was written to avoid.

**Not licensed.** No claim that a realised term will gain anything. Spec §2.6's precedent puts realised
conversion at single-digit percentages of an oracle; at 3.7 % this ceiling predicts about +0.002, below
this project's measurement floor. The GO is a licence to *design and then measure*, not a prediction.

### Known limits, carried forward from spec §5 and §2.7

- **The ceiling is a ceiling on what α-nDCG can reward, not on true aspect coverage.** 48.6 % of top-10
  documents are unjudged and score as covering nothing. The measurement is still the right one: a term
  shipped here would be evaluated by exactly this metric.
- **The greedy oracle is greedy, not optimal.** G is the standard α-discounted greedy approximation — the
  same construction α-nDCG's own ideal ranking uses. A true optimum could be marginally higher, so G − R
  slightly **under**-estimates the ceiling. That bias points toward NO-GO, and the result was GO anyway.
- **One corpus, one window.** This binds FreshStack-2048. No other corpus in this project carries subtopic
  labels, so whether the headroom generalises is unmeasurable here.
- **Aspect counts are near-binary on this corpus** (spec §2.7): 57.1 % of relevant documents cover exactly
  one aspect, 3.8 % cover four or more, and only 17.9 % of judged top-10 documents cover two or more. The
  ceiling was reached by reordering that minority.
- **The coverage-screen gate document lives on the unmerged `coverage-term-screen` branch**, so §0 of
  `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` has no row for it. A reader following §0
  alone will not find the NO-GO this gate builds on.

## What shipped

**Harness only.** `Iverson.Server/Iverson.LoadTest/scripts/aspect_oracle.py` and its 27-test suite. No
`VectorRankingOptions` constant, no ranking code, no configuration, no server change.

---

## Amendment, 2026-09-13: no offline-testable signal reaches the ceiling, and a term that did would be undetectable

The GO above licensed *designing* an aspect-coverage term. A design session ran four probes before
proposing one. All four are offline, over artefacts already on disk. They do **not** establish that no
signal exists — one candidate family was never screened, and §"What was NOT tested" below says why. What
they establish is narrower and still decisive: every signal testable offline fails, and the one family
that is not testable offline would produce an effect below this corpus's measurement floor even if it
worked.

**1. No *score-derived* signal proxies the oracle's per-document aspect count.** This covers the
signals reconstructible from the chunk-hit dump — which records a parent key and a score per hit, and
no chunk identity or chunk vector. Over all **2,733** relevant
(query, document) pairs with chunk hits — joined from `chunk-coverage-phase1-2026-09-09/runs/fs2048-pool.chunks.hits.tsv`
through `keymap.json`, 0 unresolved keys — Spearman ρ against the aspect count `rank_A` orders by:

| signal | ρ |
|---|---|
| `spread` (max − min chunk score) | +0.0790 |
| `mean_tail` | +0.0761 |
| `dispersion` | +0.0722 |
| `n_chunks` | +0.0714 |
| `max_score` | +0.0572 |
| `n_within_095` | +0.0428 |

All six cluster, which is the signature of one weak size effect rather than six independent signals.
The reason is structural, not incidental: a **scalar score per chunk destroys the aspect information**.
One query embedding yields one number per chunk, and nothing in that number records *which part* of the
query the chunk answered. Aspect count is exactly "how many different parts", so no function of those
scalars can recover it.

**2. There is no nugget text, so a query-decomposition term cannot be validated offline.** FreshStack
ships nugget **ids** only — `queries.jsonl` and `corpus.jsonl` carry text, `qrels.tsv` carries
`queryId nuggetId corpusId rel`, and no file anywhere in the topic tree carries nugget text. A term that
decomposed the query into aspects could only ever be measured end-to-end; there is no intermediate check
that it found the right aspects.

**3. MMR neither occupies the headroom nor can reach it.** The λ sweep arms in
`freshstack-2048-2026-09-07/runs/` scored on α-nDCG@10:

| λ | α-nDCG@10 |
|---|---|
| 0.50 | 0.3525 |
| 0.70 | 0.3552 |
| 0.85 | 0.3551 |
| 1.00 | 0.3551 |

Whole-range spread **0.0027**, about 4 % of the +0.0609 ceiling and well under the 0.02 bar — and
λ = 1.00, which is MMR **off**, ties λ = 0.70 exactly. This is worth recording because λ was tuned on
nDCG@10, a binary-relevance measure that structurally cannot see diversity (§"The seven expected-zero
`[compare]` blocks" above is the same fact in the other direction). Retuning λ on the diversity metric
was the obvious cheap route; it is dead. Relatedly, no artefact records the λ that produced B (ranked-changes
item 16), but the question is moot: B's α-nDCG@10 of 0.3551 is identical to the λ = 1.00 and λ = 0.85 arms.

**4. The conversion premise reproduces, and the population to test it further does not exist.** Applying
the reranker gate's own quantity — realised ÷ (ideal reordering of the control's pool − control) — to
`rerank-a0`/`rerank-a1` on SciFact gives A0 0.6960, A1 0.7043, ceiling 0.9229, **conversion 3.63 %**,
against the 3.7 % that gate records. (The ceiling differs from its recorded 0.9216 because that figure
was computed on `chunked-512`'s pool; this one uses the control's own pool, which is what a ratio requires.)
Widening that from n = 1 was attempted and failed on eligibility: a conversion ratio needs an arm that
**reorders a fixed pool**, and only rerankers do. SciFact `a1`/`a2`/`a4` qualify at 300/300 identical
pools; `a3` and `a0′` fail at 7/300 (different λ, so different selection); the centroid-weight sweep has
no control on disk (`sweep-w0333` does not exist); and **all five coverage β arms fail** — 29, 2, 0, 0 and
0 of 672 — because the β term reweights the chunk→document collapse and so changes which documents
survive. NFCorpus `a1` qualifies at 323/323 but its qrels are graded (rel ∈ {1,2}), so relevant-first is
not its nDCG ideal. Net population: four arms, three from one experiment on one corpus, two of them
known-negative.

### What was NOT tested

**Family 2 — chunk-vector-derived aspect coverage — has never been screened.** The coverage-term screen
spec deferred it explicitly (`2026-09-10-coverage-term-screen-design.md` §4: the dump has "no chunk
identity and no chunk vector, so aspect coverage cannot be reconstructed from it"), and probe 1 above
inherits exactly that limitation — its six signals are all score-derived. Probe 1 is therefore evidence
about the dump, not about vectors.

Screening it was attempted in this session and is not a probe. Chunk vectors do extract from
`freshstack-2048-qdrant-snapshots` — `vector_storage-body_vector/vectors/chunk_*.mmap`, raw f32,
`{"dim":768,"chunk_size_vectors":10922}`, 18,622 points. Two things block going further: grouping those
vectors by document requires parsing Qdrant's page-based `payload_storage` (LZ4-framed JSON) plus
`id_tracker.mappings`, and the **query** vectors exist nowhere on disk — the collections hold chunk and
object vectors only. There is no local route to make them (no `torch`, `transformers`,
`sentence_transformers` or `onnxruntime`, and no cached weights), though `beir/corpus.jsonl` and
`beir/queries.jsonl` carry the text. The real quantity — how far a document's matched chunks spread
*relative to the query* — needs a snapshot restore into Qdrant plus TEI for 672 query embeddings: an
experiment with its own spec, not a screen.

### What this changes

**The GO stands as measured.** Nothing above touches the +0.0609 or how it was obtained.

**The licence it granted is spent on the measurability ground, not on an exhaustive signal search.**
Probes 1-3 remove every offline-testable route. Family 2 remains unscreened — but probe 4 applies to it
as much as to any other construction: the gate's own conversion figure puts a realised term at ≈ +0.002
against a measured MDE of 0.0097, so a family-2 experiment's *best case* is undetectable on the only
corpus that can score it. Building it to learn the conversion rate is a defensible reason to run it; the
term paying for itself is not, on current evidence.

**What would reopen it:** nugget text for FreshStack (making probe 2's validation possible), a corpus
with subtopic labels large enough to lift the MDE below the predicted effect, or a decision to run the
family-2 screen as a funded experiment — snapshot restore, TEI, and a definition of "distinct aspect"
that nobody has yet pinned down. A query-side multi-vector representation would need its own gate; the
document-side multivector question is separately closed (`2026-09-GATE-multivector.md`).
