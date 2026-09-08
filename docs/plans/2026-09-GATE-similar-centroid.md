# SearchSimilar Centroid Retrieval — Gate Verdict

**Verdict: FAIL. Head retrieval stands; `SimilarRetrievalVector` is deleted and Task 1's wiring
reverted.**

Recorded 2026-09-08 against local `main` HEAD `3fce6f6`, the commit branch `similar-centroid` was
created from. Corresponds to Task 5 of
`docs/plans/2026-09-07-similar-centroid-retrieval-implementation-plan.md`; the gate rule is
`docs/specs/2026-09-07-similar-centroid-retrieval-design.md` §5 and its consequences are §6.

Every API run in this campaign was produced by one binary, build composite **`e849cb6aa24340d5`**,
built once from the worktree at Task 1's commit `39688db`. The image never changed between arms;
only `VectorRanking__SimilarRetrievalVector` did.

Full `report.py` output — the source for every number below — lives untracked beside the run files
in the three run directories:

| Arm | Run directory | Report | Snapshots |
|---|---|---|---|
| `sci-2048` | `~/repositories/iverson-benchmark-corpora/scifact-2048-2026-09-06/` | `report-sci.txt` | `scifact-2048-qdrant-snapshots/` |
| `fs-2048` | `~/repositories/iverson-benchmark-corpora/freshstack-2048-2026-09-07/` | `report-fs2048.txt` | `freshstack-2048-qdrant-snapshots/` |
| `fs-512` | `~/repositories/iverson-benchmark-corpora/freshstack-512-2026-09-07/` | `report-fs512.txt` | `freshstack-512-qdrant-snapshots/` |

These are the same three directories the Tier 1 campaign wrote
(`docs/plans/2026-09-GATE-tier1-defaults.md`); this campaign added six `.trec` runs, six
`meta.json`, six `.log` files and the three reports named above to them. No re-ingest occurred —
each arm restored both collections from its snapshot directory, per spec §4.

## Method

Six query runs from one binary: three corpora × two settings (`head`, `centroid`). Per arm:
restore `benchmark_documents_tenant_bypass` and `benchmark_documents_chunks_tenant_bypass` from the
arm's snapshots, clear the `BenchmarkDocument` row from `_iverson_schema`, recreate `iverson-api`
with `VECTOR_RANKING_SIMILAR_RETRIEVAL=<setting>`, then run `benchmark-query`, which reaches
`SearchSimilar` at `BenchmarkQueryScenario.cs:353`.

`LambdaSimilar` stayed at its shipped default of **1.00** throughout and `LambdaChunks` at 0.70, so
the λ-gated second retrieve in Task 1's wiring was never issued and both arms ran at one Qdrant
round trip. Only `SimilarRetrievalVector` varied.

Per-corpus chunk budget multiplier: **5** on SciFact, **11** on both FreshStack arms — the same
values the Tier 1 campaign used. No run in this campaign logged a `REFUSING: chunk budget` line.

One `report.py --baseline <head run> --run <centroid run>` invocation per arm, so the Holm family is
exactly **one comparison per measure per arm** — the same shape rule 7.3 used, which is what makes
the two results directly comparable. Every `Holm (1 tests)` line below is that family of one.

All six runs' `meta.json` recorded composite `e849cb6aa24340d5`, matching the live `/build`
endpoint, and no report printed `BUILD MISMATCH`. All runs reported 300/300 (SciFact) or 672/672
(FreshStack) qrels queries covered, no duplicate doc ids, and all scores non-zero.

### Arms

| Arm | Corpus | Window | Objects | Chunks | chunks/doc | Mult | Queries | Rows per run |
|---|---|---|---|---|---|---|---|---|
| `sci-2048` | SciFact | 2048/1792 | 5,183 | 6,587 | 1.27 | 5 | 300 | 15,000 |
| `fs-2048` | FreshStack 6k slice | 2048/1792 | 6,000 | 18,622 | 3.10 | 11 | 672 | 33,600 |
| `fs-512` | FreshStack 6k slice | 512/448 | 6,000 | 64,735 | 10.79 | 11 | 672 | 33,600 |

Every restore hit its predicted point counts exactly, and every run produced its predicted row
count exactly.

## Results

### `sci-2048` — `report-sci.txt`

Absolute `sc-centroid.similar`: nDCG@10 **0.7468**, R@50 **0.9510**, AP **0.7064**.

```
[compare] sc-centroid.similar.trec  vs  sc-head.similar.trec        (nDCG@10)
  delta            +0.0000
  paired t         t = nan   p = nan
  permutation      p = 1.0000   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0000, +0.0000]
  Cohen's d_z      nan
  MDE @ 80% power  0.0000
  queries changed  0 / 300  (0.0%)
  Holm (1 tests)   p_adj = 1.0000   not significant
  !! FEW QUERIES CHANGED: only 0.0% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] sc-centroid.similar.trec  vs  sc-head.similar.trec        (R@50)
  delta            +0.0000
  paired t         t = nan   p = nan
  permutation      p = 1.0000   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0000, +0.0000]
  Cohen's d_z      nan
  MDE @ 80% power  0.0000
  queries changed  0 / 300  (0.0%)
  Holm (1 tests)   p_adj = 1.0000   not significant
  !! FEW QUERIES CHANGED: only 0.0% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] sc-centroid.similar.trec  vs  sc-head.similar.trec        (AP)
  delta            +0.0000
  paired t         t = nan   p = nan
  permutation      p = 1.0000   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0000, +0.0000]
  Cohen's d_z      nan
  MDE @ 80% power  0.0000
  queries changed  0 / 300  (0.0%)
  Holm (1 tests)   p_adj = 1.0000   not significant
  !! FEW QUERIES CHANGED: only 0.0% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.
```

An exact null across all 15,000 rows. That is the same signature the plan warned would appear if the
image had been built from the wrong checkout, so it was investigated rather than accepted; see
**Why the nulls are real** below.

### `fs-2048` — `report-fs2048.txt`

Absolute `fs2048-centroid.similar`: nDCG@10 **0.2802**, R@50 **0.5213**, AP **0.2029**.

```
[compare] fs2048-centroid.similar.trec  vs  fs2048-head.similar.trec        (nDCG@10)
  delta            +0.0001
  paired t         t = 1.00   p = 0.3177
  permutation      p = 0.9893   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0001, +0.0003]
  Cohen's d_z      0.039
  MDE @ 80% power  0.0003
  queries changed  1 / 672  (0.1%)
  Holm (1 tests)   p_adj = 0.9893   not significant
  !! FEW QUERIES CHANGED: only 0.1% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] fs2048-centroid.similar.trec  vs  fs2048-head.similar.trec        (R@50)
  delta            +0.0037
  paired t         t = 3.04   p = 0.0025
  permutation      p = 0.0026   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0013, +0.0062]
  Cohen's d_z      0.117
  MDE @ 80% power  0.0035
  queries changed  42 / 672  (6.2%)
  Holm (1 tests)   p_adj = 0.0026   significant
  !! FEW QUERIES CHANGED: only 6.2% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] fs2048-centroid.similar.trec  vs  fs2048-head.similar.trec        (AP)
  delta            +0.0004
  paired t         t = 2.82   p = 0.0050
  permutation      p = 0.0060   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0001, +0.0007]
  Cohen's d_z      0.109
  MDE @ 80% power  0.0004
  queries changed  106 / 672  (15.8%)
  Holm (1 tests)   p_adj = 0.0060   significant
```

### `fs-512` — `report-fs512.txt`

Absolute `fs512-centroid.similar`: nDCG@10 **0.2908**, R@50 **0.5283**, AP **0.2109**.

```
[compare] fs512-centroid.similar.trec  vs  fs512-head.similar.trec        (nDCG@10)
  delta            +0.0000
  paired t         t = nan   p = nan
  permutation      p = 1.0000   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0000, +0.0000]
  Cohen's d_z      nan
  MDE @ 80% power  0.0000
  queries changed  0 / 672  (0.0%)
  Holm (1 tests)   p_adj = 1.0000   not significant
  !! FEW QUERIES CHANGED: only 0.0% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] fs512-centroid.similar.trec  vs  fs512-head.similar.trec        (R@50)
  delta            +0.0045
  paired t         t = 2.36   p = 0.0184
  permutation      p = 0.0124   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0008, +0.0082]
  Cohen's d_z      0.091
  MDE @ 80% power  0.0053
  queries changed  69 / 672  (10.3%)
  Holm (1 tests)   p_adj = 0.0124   significant

[compare] fs512-centroid.similar.trec  vs  fs512-head.similar.trec        (AP)
  delta            +0.0004
  paired t         t = 2.12   p = 0.0347
  permutation      p = 0.0370   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0000, +0.0009]
  Cohen's d_z      0.082
  MDE @ 80% power  0.0006
  queries changed  151 / 672  (22.5%)
  Holm (1 tests)   p_adj = 0.0370   significant
```

## Validity checks

Spec §4 declares these a record, not a gate condition, and non-diagnostic on mismatch — `meta.json`
carries the build composite and `chunkBudgetMultiplier` but not λ, so a mismatch cannot separate a
leaky setting from a λ difference in the recorded runs. Both outcomes are recorded here.

| Arm | New binary's `head` run vs the recorded λ=1.00 run (build `9714c660b365fad1`) | Outcome |
|---|---|---|
| `fs-2048` | `fs2048-head` vs `fs-2048-l100.similar.trec` — **identical ordering** | **Confirms.** The setting is provably inert on the control path, and Task 1's refactor did not change head-retrieval behaviour. |
| `fs-512` | `fs512-head` vs `fs-512-l100.similar.trec` — **362 of 67,200 positions differ (0.54 %)**, with a slightly different document set | **Non-diagnostic**, as pre-declared. |

Weigh the second against the first: a setting that leaked into the control path would have shown on
`fs-2048` too, and it did not. A 0.54 % positional difference across two different builds is far
more consistent with ANN/float variation than with the setting bleeding. SciFact has no recorded
λ=1.00 `.similar` run of its own to check against, so it contributes no validity check.

A second, independent cross-check falls out of the absolute scores. `fs512-centroid.similar` scores
nDCG@10 **0.2908**, exactly the Tier 1 campaign's `fs-512-l100.similar` figure, and
`fs2048-centroid.similar` scores 0.2802 against that campaign's 0.2801 — consistent with 0 and 1
query changed respectively.

## Why the nulls are real

Two of the three arms return an exact `+0.0000` on nDCG@10 with zero queries changed. The mechanism
is a property of the fusion, and it was established from two directions before the numbers were
accepted.

**The fusion is symmetric in the two vectors.** `WBase = WCentroid = 0.45`, and
`ResultReranker.Rerank` computes `(WBase·BaseScore + WCentroid·secondaryCosine) / (WBase + WCentroid)`.
Retrieval always fetches the *other* named vector as the fused secondary signal
(`ObjectSearchGrpcService.cs:265-275`), so under **both** settings the fused score is
`0.45·cos(q, head) + 0.45·cos(q, centroid)` over the same two cosines. For any document present in
both arms' retrieved pool, its final score is provably identical. Confirmed empirically: query 1's
top SciFact document scores **0.581890** in both arms.

**The vectors themselves genuinely differ.** A real 4-chunk document in
`benchmark_documents_tenant_bypass` has `cosine(body_vector, body_centroid) = 0.923`, not 1.0. So
the named vectors are distinct in Qdrant and the null is not a restore fault, a wrong-checkout
build, or an inert setting. Build provenance was checked independently: one image, one composite in
all six `meta.json` and on the live `/build`, and `AddVectorRanking` rejects any value other than
`head`/`centroid` at startup, so both containers demonstrably received their setting.

**The consequence.** The swap cannot reorder any document both arms retrieve. It can only change
which documents enter the 4×-over-fetched candidate pool. Every difference this campaign can
measure is therefore a **recall** difference — and the density table shows exactly that:

| Arm | chunks/doc | nDCG@10 Δ (queries changed) | R@50 Δ | ordering divergence |
|---|---|---|---|---|
| `sci-2048` | 1.27 | +0.0000 (0 / 300) | +0.0000 | 2.2 % |
| `fs-2048` | 3.10 | +0.0001 (1 / 672) | **+0.0037** significant | 12.6 % |
| `fs-512` | 10.79 | +0.0000 (0 / 672) | **+0.0045** significant | 17.1 % |

R@50 grows monotonically with chunk density — 0.0000 → +0.0037 → +0.0045 — exactly as widening pool
divergence predicts. **nDCG@10 stays pinned at zero regardless**, even on `fs-512` where 17.1 % of
all ranked positions move. nDCG@10 reads the top 10, which is almost entirely shared candidates, so
it cannot move at any density. Only the pool boundary at depth 50 can, and it does.

SciFact's null is additionally structural: 3,820 of its 5,183 documents (73.7 %) are single-chunk,
and a single-chunk document's centroid equals its unit-normalized head vector, so it cannot move at
all. The spec recorded this before the run (Known issues); the arm was kept as the near-single-chunk
guard, and it discharges that role — it demonstrates no harm.

## Verdict

Spec §5:

> **PASS** if centroid-retrieval beats head-retrieval on **both** nDCG@10 and R@50 with Holm
> p_adj < 0.05 on **both** FreshStack arms, **and** SciFact's 95 % CI lower bounds on both measures
> exceed −0.02.
>
> Anything else is a **FAIL**.

| Clause | Required | Measured | Holds? |
|---|---|---|---|
| `fs-2048` nDCG@10 | Holm p_adj < 0.05 | +0.0001, **p_adj 0.9893** | **NO** |
| `fs-2048` R@50 | Holm p_adj < 0.05 | +0.0037, p_adj 0.0026 | yes |
| `fs-512` nDCG@10 | Holm p_adj < 0.05 | +0.0000, **p_adj 1.0000** | **NO** |
| `fs-512` R@50 | Holm p_adj < 0.05 | +0.0045, p_adj 0.0124 | yes |
| `sci-2048` nDCG@10 | CI lower > −0.02 | +0.0000 | yes |
| `sci-2048` R@50 | CI lower > −0.02 | +0.0000 | yes |

The rule is a conjunction over all six clauses. **Two fail — nDCG@10 on both FreshStack arms — so
the verdict is FAIL.** `fs-2048` alone settles it; `fs-512` was run anyway (SDD Ruling 5) because a
FAIL's whole value is the argument it licenses, and the 10.79 chunks/doc regime is what shows
whether the masking worsens as pool divergence grows. It does not worsen; it is total at every
density.

### What the FAIL means

Spec §5 anticipated this reading: "a FAIL would mean the 45 % head-vector term in the fusion masks a
gain the raw vectors show clearly, which is an argument about the weights, not about the centroid."
That is what happened, and the size of the masking is now measured. Against Tier 1 rule 7.3's raw
head-vs-centroid measurement on these same two corpora:

| Arm | raw nDCG@10 Δ (rule 7.3) | fused nDCG@10 Δ (here) | raw R@50 Δ | fused R@50 Δ |
|---|---|---|---|---|
| `fs-2048` | **+0.0251** (p_adj 0.0004) | +0.0001 | **+0.0576** (p_adj 0.0002) | +0.0037 |
| `fs-512` | **+0.0290** (p_adj 0.0006) | +0.0000 | **+0.0598** (p_adj 0.0002) | +0.0045 |

The fused path delivers roughly **0.4 % and 0 %** of the raw nDCG@10 effect and **6 % and 8 %** of
the raw R@50 effect. The symmetric 0.45/0.45 fusion does not merely dilute the centroid's advantage
— on ordering it erases it exactly, by construction, and no choice of retrieval vector can restore
it while the weights stay equal. Rule 7.3's finding stands as a fact about the representations; this
gate shows the current architecture cannot convert it, and that the lever is **WBase / WCentroid**,
which spec §2 put out of scope. Any future attempt at rule 7.3's gain should start there, not at the
retrieval vector.

## Consequences applied

Spec §6, FAIL branch: "delete the setting and its compose entry, keep head retrieval, record the
verdict."

1. `SimilarRetrievalVector` removed from `VectorRankingOptions.cs`, its rejection check removed from
   `AddVectorRanking` (`ServiceCollectionExtensions.cs`), and its `docker-compose.yml` entry
   removed.
2. Task 1's `SearchSimilar` wiring reverted: `ObjectSearchGrpcService.cs` retrieves by
   `<property>_vector` unconditionally and fetches `<property>_centroid` as the secondary and
   diversity vector, as it did before this branch. The λ-gated second retrieve is gone with it.
3. `RerankCandidate.Centroid` is **not** renamed — that was the PASS branch only, and the field once
   again holds literally a centroid.
4. Tests removed: the two `SimilarRetrievalVector` cases in `VectorRankingOptionsTests.cs` and the
   four vector-selection / diversity-vector cases in `ObjectSearchGrpcServiceTests.cs`. They assert
   a setting and a code path that no longer exist. Suite counts return to their pre-branch values,
   Vector 134 and Api 878.

`git diff 3fce6f6 HEAD -- Iverson.Server` is empty: the production and test code is byte-identical
to the commit this branch started from. This gate document is the branch's only surviving artifact.

## Box state at close

Qdrant still holds the **`fs-512`** arm — `benchmark_documents_tenant_bypass` **6,000** object
points and `benchmark_documents_chunks_tenant_bypass` **64,735** chunk points, verified after the
last restore. `iverson-api` was recreated on defaults after the final run and its environment shows
`VectorRanking__LambdaSimilar=1.00` and `VectorRanking__LambdaChunks=0.70`.

The running `iverson-api` image is build `e849cb6aa24340d5`, which was built from Task 1's now-
reverted code. **It no longer corresponds to any commit.** Rebuild before any further benchmarking;
a rebuild from `main` will produce a different composite and `report.py` will correctly flag a
`BUILD MISMATCH` against this campaign's six runs.
