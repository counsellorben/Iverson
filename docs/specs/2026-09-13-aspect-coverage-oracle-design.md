# Is there headroom in coverage over distinct query aspects? — an oracle measurement — design

**Date:** 2026-09-13
**Parent:** `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` §15 ("A coverage term that is
not a chunk count in disguise"), and the NO-GO that closed the mechanical screen of that item
(`docs/plans/2026-09-GATE-coverage-screen.md`, on branch `coverage-term-screen`).
**Status:** design, approved after one revision. No implementation plan yet.

---

## 1. The question

Item 15 left "a coverage term whose value does not collapse onto the count" untested. The coverage-term
screen then tested seven such candidates over the chunk dump and returned NO-GO — every candidate that
was not a count in disguise failed to discriminate. That closed the *candidates*, not the *idea*. The
idea that survived is a different one: coverage not over a document's own chunks, but over the **distinct
aspects of the query** that a result list collectively answers — the thing FreshStack's nugget qrels
actually label and the thing α-nDCG actually scores.

Before designing any such term, this spec asks the cheaper prior question:

> **Is there enough headroom for aspect coverage, at all, to be worth a term?**

The answer is an oracle: give a ranker perfect knowledge of which aspects each retrieved document covers,
let it reorder the documents already retrieved, and measure how much α-nDCG@10 it can gain. If a
*cheating* ranker cannot gain much, no honest term can. This is a measurement, not a mechanism — the
output is a number and a GO/NO-GO, not a candidate to ship.

### 1.1 Why the naive oracle would have answered the wrong question

The first version of this design compared an aspect-aware oracle against the shipped run. Verification
killed it. Of the 33,600 retrieved (query, document) pairs only 25.9 % are judged at all, and **in the
top 10 specifically only 51.4 % are judged**. `alpha_nDCG`'s `judged_only` parameter defaults to `False`
(verified, A10), so an unjudged document is scored as contributing zero gain — not as unknown.

An oracle that reorders against the shipped run therefore gets paid twice: once for knowing **which
documents are relevant at all** (it drags every judged document above every unjudged one), and once for
knowing **which aspects each covers**. The first payment would dominate and has nothing to do with
coverage. This is the same defect that let uniform noise pass the coverage screen — an
apparently-neutral baseline that is not in fact neutral.

The fix is to make the baseline an oracle too.

---

## 2. Design

### 2.1 Input, reused as-is

| | |
|---|---|
| Run (the shipped ranking, **B**) | `~/repositories/iverson-benchmark-corpora/chunk-coverage-phase2-arms-2026-09-09/fs2048-b0.chunks.trec` |
| Document qrels | `~/repositories/iverson-benchmark-corpora/freshstack-2048-2026-09-07/qrels.trec` |
| Nugget (aspect) qrels | `~/repositories/iverson-benchmark-corpora/freshstack-2048-2026-09-07/qrels.nugget.trec` |
| Scale | 672 queries × 50 documents = 33,600 rows |

`fs2048-b0.chunks.trec` is the β = 0 arm of chunk-coverage Phase 2 — that is, the shipped ranking with
no coverage term — and is the same control that gate and the coverage-term screen both used. Nothing is
re-retrieved, no container is started, and no server code is touched.

### 2.2 Four rankings over one pool

Every ranking below is a **permutation of B's own 50 documents for that query**. No document enters or
leaves any list. `report.py`'s pool check (`check_pool`, `report.py:732`) enforces set identity and will
exit non-zero if a document ever enters or leaves a list. It reads file order, not the score column, so it
cannot detect a run file whose score column still encodes B's ordering — that property is the writer's
responsibility (§2.8).

| | Ranking | What it knows |
|---|---|---|
| **B** | the shipped run, unmodified | nothing |
| **R** | judged-relevant documents first, in B's order; the rest after, in B's order | relevance only |
| **A** | judged-relevant documents ordered by **descending count of distinct nuggets that document covers for this query** (ties in B's order); the rest after, in B's order | relevance + per-document aspect count |
| **G** | **greedy α-discounted marginal gain within the relevant prefix** — among the judged-relevant documents, repeatedly append the one whose gain, Σ over the nuggets it covers of (1 − α)^(number of already-appended documents covering that nugget), is largest at the same **α = 0.5** §2.4 pre-registers (ties in B's order), until the relevant prefix is exhausted. The non-relevant documents follow after, in B's order | relevance + full aspect structure |

Restricting G's greedy to the relevant prefix is stated for clarity, not as a constraint that changes the result: under the α-discounted objective every relevant document has strictly positive gain and every unjudged document has exactly zero, so a greedy ranging over all 50 produces the identical ranking (672/672 queries, A22). The restriction earns its place by making §2.2's identical-prefix property true by construction rather than by argument.

**R, A and G promote the identical set of documents.** Document relevance on this corpus *is* the nugget
union — 5,445 relevant (query, document) pairs in `qrels.trec`, 5,445 in the nugget qrels, with zero
difference in either direction (verified, A8). A document is relevant exactly when it covers at least one
nugget, so "relevant first" and "nugget-covering first" select the same prefix. The three rankings differ
**only in the order within that prefix**. That is what makes the subtraction below clean.

### 2.3 The three quantities

| Quantity | Reads as |
|---|---|
| **R − B** | the value of knowing relevance. Reported for context; **not** the question, and not subject to the bar. |
| **A − R** | the ceiling for ordering on a per-document **aspect** count — how many of the query's nuggets one document covers — with relevance held fixed. This is **not** the parent gate's term: that one counted a document's own pooled **chunks**, an operand these inputs do not carry. |
| **G − R** | **the ceiling for aspect coverage as an idea** — the most any mechanism could win by ordering on aspects. This is what the bar applies to. |

### 2.4 Metric

α-nDCG@10 via `pyndeval`, through the already-built `report.py --nugget-qrels` path
(`report.py:833`, `:906-911`). Parameters are pyndeval's defaults, unchanged and pre-registered here so
the greedy oracle's optimum is not read against a different α afterwards:

- **α = 0.5** (redundancy intolerance)
- **rel = 1** (relevance > 0 counts)
- **judged_only = False**

nDCG@10 and R@50 are scored alongside, as they always are, but do not decide anything: R@50 is invariant
by construction (identical pools) and is a self-check, not a result.

### 2.5 Statistics

`report.py --pair`, declaring exactly three pairs — `G=R`, `A=R`, `R=B` — so Holm corrects across a
family of 3 (`report.py:769`). This is the project's existing paired vehicle: paired *t*, sign-flip
permutation over `PERMUTATION_RESAMPLES = 10_000` at `PERMUTATION_SEED = 20260831`, Holm at
`HOLM_ALPHA = 0.05`, and a 95 % CI on the mean per-query delta. No new statistics code is written.

The pool check's second condition — ranked order must differ from the control on ≥ 25 % of queries
(`POOL_MIN_REORDERED_FRACTION`, `report.py:700`) — is satisfied with margin: R differs from B on 88.2 %
of queries, A differs from R on 43.8 %, and G differs from R on 48.2 % (verified, A13).

**Population.** The primary figure is over all 672 queries, because that is the population the shipped
metric is reported on. 62 queries (9.2 %) have no relevant document anywhere in their 50 and therefore
score α-nDCG 0 under all four rankings, contributing exactly 0 to every delta; the 610-query
restriction is reported as a sensitivity, and per the parent gate's convention the **per-population
primary decides** while the other is context.

### 2.6 Decision rule, pre-specified

The materiality bar is **0.02 α-nDCG@10**, fixed before the measurement runs.

> **GO** — headroom exists, and an aspect-coverage term is worth designing — iff
> **G − R ≥ +0.02** on the 672-query primary population **and** its 95 % CI lower bound > 0.
>
> **NO-GO** otherwise. In particular a point estimate below +0.02 is NO-GO whatever its p-value: a
> ceiling this measurement cannot clear is not made interesting by being measured precisely.

**A − R is reported but does not gate.** If G − R clears the bar while A − R does not, the finding is that
aspect coverage has headroom *but not through a per-document aspect count* — which is a different and more useful
result than either number alone.

#### Why 0.02, and what the precedent actually says

This project's one prior oracle-ceiling measurement is the reranker gate's, and it is a precedent of
exactly this construction: the ideal reordering of the same retrieved 50.

| | nDCG@10 |
|---|---|
| Oracle@50 ceiling, `chunked-512` (§7.1.3 / A36) | **0.9216** |
| Shipped baseline A0 | **0.6960** |
| Headroom the oracle showed | **+0.2256** |
| Best realised arm (A1) | 0.7043 |
| Realised gain | **+0.0083 — 3.7 % of the ceiling** |

Two things follow, and both are load-bearing:

1. **A realised term captures single-digit percentages of its oracle.** A ceiling of +0.02 would, at that
   conversion rate, predict a realised gain under +0.001 — below this project's own measurement floor. So
   +0.02 is not a conservative bar; it is close to the smallest ceiling that could possibly matter.
2. **0.9216 is itself a relevance-only oracle** — perfect reordering by relevance, which is precisely
   Oracle R. The precedent is therefore a measurement of R − B on a different corpus, and it is the very
   quantity §2.3 subtracts off. Reading it as a precedent for an *aspect* ceiling would repeat the error
   §1.1 describes.

The precedent is SciFact at the 512/448 window scored on plain nDCG@10; this measurement is FreshStack-2048
scored on α-nDCG@10. It sets the conversion-rate expectation, not the ceiling's scale.

### 2.7 What the numbers already suggest

Two facts verified while checking this design's assumptions, recorded so the result is not read as a
surprise either way:

- **57.1 %** of relevant documents cover exactly one aspect, 27.2 % cover two, and only **3.8 %** cover
  four or more. A per-document aspect count is nearly binary on this corpus.
- Only **17.9 %** of judged top-10 documents cover two or more aspects.

G − R is bounded by how much reordering a minority of documents can move a top-10 metric. A NO-GO is the
likelier outcome, and that is a reason to run the measurement cheaply rather than a reason to skip it.

### 2.8 Instrument and outputs

One new script, `Iverson.Server/Iverson.LoadTest/scripts/aspect_oracle.py`, alongside the other harness
scripts. It reads the run and both qrels files and writes, into one output directory:

- `oracle-R.trec`, `oracle-A.trec`, `oracle-G.trec` — TREC run files, each a permutation of B. **Each
  carries a synthetic score strictly decreasing in its new rank order** (e.g. `50 − i`), never B's original
  per-document score. `ir_measures.read_trec_run` discards the rank column and every scorer re-sorts each
  query by score descending, so the score column — not the rank column and not file order — is what
  determines the ranking that actually gets scored.
- `aspect-summary.tsv` — one row per query: relevant count in 50, distinct nuggets, nuggets reachable in
  the top 10 under each of the four rankings

One self-check before any result is read: R − B must be non-zero on this corpus, so an R − B of exactly
`+0.0000` is a writer bug — the score column still encoding B — and not a result.

`report.py` then scores all four run files and runs the three declared pairs. The verdict is recorded in
`docs/plans/2026-09-GATE-aspect-coverage-oracle.md`, following the gate-document convention, and gets its
row in the ranked-changes doc's §0 table.

---

## 3. Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| A1 | The run is 672 queries × 50 documents | `fs2048-b0.chunks.trec` is 33,600 lines; 672 distinct query ids |
| A2 | Depth is exactly 50 for every query, so every ranking is a permutation of the same set | 33,600 / 672 = 50 exactly; no short lists |
| A3 | The run's document ids and the nugget qrels' document ids share one id space | both are chunk ids of the form `path_start_end`, e.g. `docs/authorization.md_2493_11872` |
| A4 | `fs2048-b0.chunks.trec` is the β = 0 control, i.e. the shipped ranking with no coverage term | `chunk-coverage-phase2-arms-2026-09-09/` holds `fs2048-b0` plus the five β arms; the coverage-screen gate names it "β = 0 run file (control 2)" |
| A5 | `report.py` has a `--nugget-qrels` flag that adds α-nDCG@10 | `report.py:833`; measure appended at `:911` |
| A6 | `pyndeval` and `ir_measures` import in this environment | `PYTHONPATH=~/repositories/iverson-benchmark-corpora/python-libs`; `alpha_nDCG@10` constructs |
| A7 | `freshstack_nugget_qrels.py` emits the file `--nugget-qrels` consumes, at `<run>/qrels.nugget.trec` | script docstring; `freshstack-2048-2026-09-07/qrels.nugget.trec` exists, 64,539 rows |
| A8 | **Document relevance is exactly the nugget union** — so R, A and G promote an identical document set | 5,445 rel > 0 pairs in `qrels.trec`, 5,445 in the nugget qrels, 0 in either difference |
| A9 | Judged coverage is partial, and worst where it matters | 25.9 % (8,689/33,600) of the retrieved pairs are judged; **51.4 %** within the top 10 |
| A10 | `alpha_nDCG` scores an unjudged document as zero gain rather than discarding it | `judged_only` default `False`; `alpha` default `0.5`; `rel` default `1` (`ir_measures` `SUPPORTED_PARAMS`) |
| A11 | `report.py` scores a TREC run file offline — no live retrieval needed to score a reordering | `score_run` at `report.py:358` takes `run_path` and calls `ir_measures.read_trec_run` |
| A12 | The reranker precedent is an oracle-reorder ceiling of the same construction, with the figures quoted in §2.6 | `2026-09-GATE-reranker-phase1.md:129-130`, `:203`; `2026-09-03-reranker-phase1-implementation-plan.md:89` |
| A13 | All three declared pairs clear `report.py`'s 25 % reorder threshold | R differs from B on 593/672 (88.2 %); A differs from R on 294/672 (43.8 %); G differs from R on 324/672 (48.2 %) |
| A14 | `--pair` Holm-corrects across exactly the declared pairs, so a family of 3 is expressible | `run_pair_statistics` at `report.py:769`; `--pair RUN=BASELINE` at `:852` |
| A15 | The pool check requires identical per-query document sets — satisfied by construction, and it will catch any bug that breaks that | `check_pool` at `report.py:732`, `POOL_MIN_REORDERED_FRACTION` at `:700` |
| A16 | 62 queries (9.2 %) have no relevant document in their 50 and contribute 0 to every delta | counted over `qrels.trec` ∩ the run's per-query lists |
| A17 | Aspect counts are near-binary on this corpus, per §2.7 | 57.1 % / 27.2 % / 3.8 % of relevant documents at 1 / 2 / 4+ aspects; 17.9 % of judged top-10 at 2+ |
| A18 | Nothing in the ranked-changes doc already answers this question | §15 is the mechanical candidate screen (closed NO-GO); no item asks whether aspect coverage has headroom |
| A19 | The **score column** — not file order, not the rank column — determines the ranking a scorer sees, while `check_pool` reads file order | `read_trec_run` yields `ScoredDoc(query_id, doc_id, score)` and never reads rank (`python-libs/ir_measures/util.py:298-303`); `as_sorted_namedtuple_iter` re-sorts per query by score descending (`:229-241`); `ranked_doc_ids` appends in file order (`report.py:721-730`). The two guards disagree, which is why §2.8 pins the score column |
| A20 | Every run query is present in both qrels files, so none is silently dropped from the denominator | run ∖ `qrels.trec` = 0 and run ∖ `qrels.nugget.trec` = 0 over all 672 queries; `pyndeval/__init__.py:93` scores only `if qid in self.qrels`, with no else-branch — a missing query would shrink the denominator invisibly rather than score 0 |
| A21 | B's file order is score-descending, and the scorer's tie-break diverges from it only where §2.3 does not gate | score-descending on 672/672; 99 queries carry tied scores; the scorer's `(-score, doc_id)` order (`pyndeval/__init__.py:153`, `:160`) differs from file order on 58/672 queries and on 7/672 within the top 10. The divergence touches R − B only — A − R and G − R are computed between files all written under §2.8's score contract |
| A22 | Under the α-discounted objective the prefix restriction is a no-op | greedy over all 50 gives the ranking identical to greedy within the prefix plus a B-order tail, on 672/672 queries: every relevant document has strictly positive gain and every unjudged document exactly zero |

---

## 4. Out of scope

- **Designing the term itself.** This spec measures a ceiling. If the ceiling clears the bar, the term is
  a separate design.
- **Any server change.** No `VectorRankingOptions` constant, no ranking code, no configuration.
- **Re-retrieval.** No container is started, no collection is touched, nothing is re-embedded. Every input
  is already on disk.
- **The 512 corpus and every other corpus.** FreshStack is the only corpus in this project with nugget
  qrels, and FreshStack-2048 is the window both chunk-coverage phases and the coverage-term screen ran on.
- **Improving judgment coverage.** The 48.6 % unjudged share in the top 10 is a property of FreshStack,
  not something this measurement fixes.

## 5. Known issues accepted as out of scope

- **The ceiling is a ceiling on what α-nDCG can reward, not on true aspect coverage.** 48.6 % of top-10
  documents are unjudged and are scored as covering nothing. A real term might surface a document that
  genuinely covers a new aspect and be given no credit. The measurement is nonetheless the right one: a
  term shipped into this project would be evaluated by exactly this metric, so what it *could be rewarded
  for* is the operative bound.
- **The greedy oracle is greedy, not optimal.** Maximising α-nDCG@10 exactly is a set-selection problem;
  the α-discounted marginal-gain greedy of §2.2 is the standard approximation and is the construction
  α-nDCG's own ideal ranking is built with, so it optimises the same quantity the metric scores. A true
  optimum could still be marginally higher, which makes G − R a slight under-estimate of the ceiling —
  biased toward NO-GO, and stated so the result is not over-read.
- **One corpus, one window.** The result binds FreshStack-2048. Whether aspect-coverage headroom exists
  elsewhere is unmeasurable here, because no other corpus in this project carries subtopic labels.
- **The coverage-screen gate document lives on an unmerged branch** (`coverage-term-screen`), so the
  ranked-changes doc's §0 table has no row for it yet. That is that branch's business, not this spec's,
  but a reader following §0 alone will not find the NO-GO this spec builds on.
