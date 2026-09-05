# Embedding Migration to TEI — Phase 1 Gate Verdict

Recorded 2026-09-04, from `.worktrees/embedding-migration` HEAD `f2706a4` (branch `embedding-migration`).
Corresponds to Task 8 of `docs/plans/2026-09-04-embedding-migration-implementation-plan.md`; the gate
rule is `docs/specs/2026-09-04-embedding-migration-design.md` §7.

Full `report.py` output (the source for every number below):
`~/repositories/iverson-benchmark-corpora/report-embedding-migration-2026-09-04.txt`.
Run files, ingest logs, key maps and stats sidecars live untracked beside it in
`~/repositories/iverson-benchmark-corpora/`:
`scifact-bge-base-2026-09-04/`, `scifact-bge-small-2026-09-04/`, `nfcorpus-bge-base-2026-09-04/`,
`scifact-run-2026-08-26/runs/m0-control-2026-09-04.*` (the same-build control), and the per-arm Qdrant
snapshots in `scifact-bge-base-qdrant-snapshots/`, `scifact-bge-small-qdrant-snapshots/`,
`nfcorpus-bge-base-qdrant-snapshots/`. Per-task execution logs are the SDD reports
`.superpowers/sdd/2026-09-04-embedding-migration-implementation-plan/task-6-report.md` and
`task-7-report.md`.

## Method

The protocol is spec §6. Each candidate arm is a **full re-ingest** of the corpus under a TEI-served
model (`--drop`, new collections at the model's dimensionality), a Qdrant snapshot, a schema-row clear
so `BenchmarkDocument` re-registers under the candidate model, an API recreate
(`up -d --no-deps iverson-api`, never a tier-wide `up`), and a `benchmark-query` run. Statistics are
`report.py --baseline` — **not** `--pair`: `check_pool` requires identical per-query candidate sets,
which a model change cannot satisfy, so `--baseline` (`run_paired_statistics`) is the only valid mode.
`PERMUTATION_SEED` (20260831), 10,000 resamples and `HOLM_ALPHA` were left untouched. SciFact was
scored in **one** invocation carrying both candidates, so Holm corrects at **m = 2**; NFCorpus at
**m = 1**. All seven invocations exited 0.

### The 512/448 window ruling

The baseline is `scifact-run-2026-08-26/runs/rerank-a0.*` (nomic-embed-text via Ollama, 300 queries),
which was ingested with a **512-character** chunk window (`maxChars 512, step 448,
wordBoundaryLookback 50`) from the unmerged `chunk-size-512-experiment` contract — not main's 2,048.
The candidates therefore ingested with `--chunk-max-chars 512 --chunk-step 448`, and reproduced the
baseline chunking exactly: **19,967 chunks** and **25,128 embed calls (22 saved)** on both SciFact
arms, matching the nomic sidecar's 19,967 / 25,128 / 22; and **14,729 chunks** with **18,327 embed
calls (35 saved)** on NFCorpus, matching N0's sidecar exactly — so the chunk *boundaries*, not merely
the count, reproduced.

The older 2,048-window nomic run (`prefixed-titled`, 2026-08-27) is **not** a valid baseline: the
fusion weights changed on 2026-08-31 (`ce7bf12`, triple B) after it was produced, so it was generated
by a different ranking configuration. Ruled by Ben 2026-09-04 (spec §6.1). A pass therefore says the
model holds quality **under the 512-character window**; the window itself remains a separate, unmerged
experiment.

### Tokens are not characters

TEI's `/info` reported `max_input_length: 512` and `auto_truncate: true` for both models. That 512 is
**512 tokens**, while `--chunk-max-chars 512` is **512 characters**. A 512-character chunk is well
under 512 tokens, so `auto_truncate` **never engaged on any chunk embed**. The only embeds that can
have been truncated are the whole-document ones behind the `.similar` runs. This is why `.similar` is
reported and not gated (spec §11): nomic's whole-document coverage (2,048-token context) and TEI's
(512 tokens with truncation) genuinely differ, so a `.similar` delta mixes model quality with document
coverage. The gate reads `.chunks` only, where coverage is identical by construction.

### Same-build control, and why `!! BUILD MISMATCH` is benign here

Every compare block in this document prints:

```
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
```

That warning is **expected and benign**. The 2026-08 nomic baselines carry build composite
`31583db5aea49136`; every arm here carries `7d3a15092f963723`, the composite of this branch's build
(the `/v1/embeddings` route change of spec §3.1). `report.py` compares composites and cannot know
whether the difference touched ranking, so it warns unconditionally.

Task 6 step 3 measured the answer directly. Before any TEI arm ran, the **new** build was pointed at
the **unchanged** nomic index and re-run as `m0-control-2026-09-04`. Its output is byte-identical to
`rerank-a0` on the retrieval-identifying columns (fields 1–5: query id, doc id, rank) for both run
files:

```
=== CHUNKS DIFF ===
CHUNKS-IDENTICAL
=== SIMILAR DIFF ===
SIMILAR-IDENTICAL
```

```
OLD: "composite":"31583db5aea49136"     (rerank-a0, 2026-08 nomic baselines)
NEW: "composite":"7d3a15092f963723"     (m0-control-2026-09-04 and all three TEI arms)
```

15,000 rows each, exit 0, no `Authentication flow` error, control wall clock 3 m 56 s. The new build
reproduces the old build's ranking bit-for-bit on the same index, so the route change is
ranking-neutral and the composite difference confounds nothing. `report.py` was not edited to suppress
the warning.

## Arms

| Arm | Corpus | Model | Backend | Dims | Window | Docs / chunks | `embed_calls` | `elapsed_seconds` | Ingest wall clock | Query duration | Exit |
|---|---|---|---|---|---|---|---|---|---|---|---|
| M0 (baseline) | SciFact | nomic-embed-text | Ollama | 768 | 512 / 448 | 5,183 / 19,967 | 25,128 (22 saved) | 31,070.19 | 8 h 37 m 50 s (2026-08-27) | — (`rerank-a0`) | — |
| M0 control | SciFact | nomic-embed-text | Ollama | 768 | 512 / 448 | 5,183 / 19,967 (re-used index) | — | — | — (no re-ingest) | 3 m 56 s | 0 |
| M1 | SciFact | BAAI/bge-base-en-v1.5 | TEI | 768 | 512 / 448 | 5,183 / 19,967 | 25,128 (22 saved) | 9,166.55 | 2 h 32 m 49 s | 3 m 05 s | 0 |
| M2 | SciFact | BAAI/bge-small-en-v1.5 | TEI | 384 | 512 / 448 | 5,183 / 19,967 | 25,128 (22 saved) | 3,565.40 | 59 m 27 s | 2 m 13 s | 0 |
| N0 (baseline) | NFCorpus | nomic-embed-text | Ollama | 768 | 512 / 448 | 3,633 / 14,729 | 18,327 (35 saved) | 23,334.49 | 6 h 28 m 54 s (2026-08-28) | — (`rerank-a0`) | — |
| N1 | NFCorpus | BAAI/bge-base-en-v1.5 | TEI | 768 | 512 / 448 | 3,633 / 14,729 | 18,327 (35 saved) | 6,509.41 | 1 h 48 m 30 s | 5 m 02 s | 0 |

Rows per run file: SciFact 15,000 (300 queries × 50), NFCorpus 16,150 (323 × 50) — for `.chunks` and
`.similar` alike, on every arm.

**Throughput** (`elapsed_seconds` ratios against the same corpus's nomic sidecar):

| Comparison | Ratio |
|---|---|
| M1 (bge-base, 768) vs M0 (nomic, 768), SciFact | **3.39× faster** (31,070.19 → 9,166.55 s) |
| M2 (bge-small, 384) vs M0 (nomic, 768), SciFact | **8.71× faster** (31,070.19 → 3,565.40 s) |
| M2 vs M1 (same backend, half the dimensions) | **2.57× faster** (9,166.55 → 3,565.40 s) |
| N1 (bge-base, 768) vs N0 (nomic, 768), NFCorpus | **3.58× faster** (23,334.49 → 6,509.41 s) |

`report.py --stats-path` puts the same numbers per document: M1 1.769 s/doc, 0.365 s/embed;
M2 0.688 s/doc, 0.142 s/embed; N1 1.792 s/doc, 0.355 s/embed.

Caveat, unchanged from the spec: **this throughput evidence is a by-product of the arms, not a
controlled measurement.** The box was idle apart from the ingest, the two nomic baselines ran under
different conditions weeks earlier, and the controlled per-text figures remain the 2026-09-03
interleaved sweep (bge-base 1.57×, bge-small 4.5×). The ratios above run *ahead* of those, which is
consistent with the earlier finding that dimension, not backend, dominates — but they are not the
controlled number.

## Results — the compare blocks, verbatim

### SciFact `.chunks` — M1, M2 vs M0 (Holm m = 2): THE GATE

Absolute scores (`[scores]`, build `7d3a15092f963723`):

| Arm | nDCG@10 | R@50 | AP |
|---|---|---|---|
| M1 (bge-base) | 0.7452 | 0.9337 | 0.7018 |
| M2 (bge-small) | 0.7030 | 0.9253 | 0.6607 |

M0's own macro scores are not re-printed by a `--baseline` invocation; they are the recorded
`rerank-a0` values 0.6960 / 0.9227 / 0.6561 (spec §6.1 and `2026-09-GATE-reranker-phase1.md`), which
the deltas below reproduce exactly (0.7452 − 0.0492 = 0.6960; 0.9337 − 0.0110 = 0.9227).

```
[compare] bge-base.chunks.trec  vs  rerank-a0.chunks.trec        (nDCG@10)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            +0.0492
  paired t         t = 3.88   p = 0.0001
  permutation      p = 0.0002   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0242, +0.0741]
  Cohen's d_z      0.224
  MDE @ 80% power  0.0355
  queries changed  99 / 300  (33.0%)
  Holm (2 tests)   p_adj = 0.0004   significant

[compare] bge-small.chunks.trec  vs  rerank-a0.chunks.trec        (nDCG@10)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            +0.0070
  paired t         t = 0.60   p = 0.5460
  permutation      p = 0.5531   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0157, +0.0296]
  Cohen's d_z      0.035
  MDE @ 80% power  0.0323
  queries changed  89 / 300  (29.7%)
  Holm (2 tests)   p_adj = 0.5531   not significant

[compare] bge-base.chunks.trec  vs  rerank-a0.chunks.trec        (R@50)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            +0.0110
  paired t         t = 0.82   p = 0.4145
  permutation      p = 0.4504   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0155, +0.0375]
  Cohen's d_z      0.047
  MDE @ 80% power  0.0377
  queries changed  18 / 300  (6.0%)
  Holm (2 tests)   p_adj = 0.9007   not significant
  !! FEW QUERIES CHANGED: only 6.0% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] bge-small.chunks.trec  vs  rerank-a0.chunks.trec        (R@50)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            +0.0027
  paired t         t = 0.21   p = 0.8370
  permutation      p = 1.0000   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0228, +0.0281]
  Cohen's d_z      0.012
  MDE @ 80% power  0.0363
  queries changed  16 / 300  (5.3%)
  Holm (2 tests)   p_adj = 1.0000   not significant
  !! FEW QUERIES CHANGED: only 5.3% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] bge-base.chunks.trec  vs  rerank-a0.chunks.trec        (AP)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            +0.0457
  paired t         t = 3.53   p = 0.0005
  permutation      p = 0.0004   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0202, +0.0712]
  Cohen's d_z      0.204
  MDE @ 80% power  0.0363
  queries changed  114 / 300  (38.0%)
  Holm (2 tests)   p_adj = 0.0008   significant

[compare] bge-small.chunks.trec  vs  rerank-a0.chunks.trec        (AP)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            +0.0046
  paired t         t = 0.37   p = 0.7087
  permutation      p = 0.7135   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0197, +0.0290]
  Cohen's d_z      0.022
  MDE @ 80% power  0.0346
  queries changed  112 / 300  (37.3%)
  Holm (2 tests)   p_adj = 0.7135   not significant
```

### SciFact `.similar` — reported, not gated

| Arm | nDCG@10 | R@50 | AP |
|---|---|---|---|
| M1 (bge-base) | 0.7391 | 0.9153 | 0.6941 |
| M2 (bge-small) | 0.6952 | 0.9030 | 0.6537 |

```
[compare] bge-base.similar.trec  vs  rerank-a0.similar.trec        (nDCG@10)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            +0.0794
  paired t         t = 5.40   p = 0.0000
  permutation      p = 0.0002   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0505, +0.1083]
  Cohen's d_z      0.312
  MDE @ 80% power  0.0412
  queries changed  110 / 300  (36.7%)
  Holm (2 tests)   p_adj = 0.0004   significant

[compare] bge-small.similar.trec  vs  rerank-a0.similar.trec        (nDCG@10)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            +0.0356
  paired t         t = 2.53   p = 0.0119
  permutation      p = 0.0098   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0079, +0.0632]
  Cohen's d_z      0.146
  MDE @ 80% power  0.0393
  queries changed  104 / 300  (34.7%)
  Holm (2 tests)   p_adj = 0.0098   significant

[compare] bge-base.similar.trec  vs  rerank-a0.similar.trec        (R@50)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            +0.0626
  paired t         t = 3.48   p = 0.0006
  permutation      p = 0.0008   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0272, +0.0980]
  Cohen's d_z      0.201
  MDE @ 80% power  0.0504
  queries changed  36 / 300  (12.0%)
  Holm (2 tests)   p_adj = 0.0016   significant

[compare] bge-small.similar.trec  vs  rerank-a0.similar.trec        (R@50)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            +0.0503
  paired t         t = 2.81   p = 0.0052
  permutation      p = 0.0046   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0151, +0.0855]
  Cohen's d_z      0.162
  MDE @ 80% power  0.0501
  queries changed  35 / 300  (11.7%)
  Holm (2 tests)   p_adj = 0.0046   significant

[compare] bge-base.similar.trec  vs  rerank-a0.similar.trec        (AP)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            +0.0762
  paired t         t = 5.16   p = 0.0000
  permutation      p = 0.0002   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0471, +0.1053]
  Cohen's d_z      0.298
  MDE @ 80% power  0.0414
  queries changed  121 / 300  (40.3%)
  Holm (2 tests)   p_adj = 0.0004   significant

[compare] bge-small.similar.trec  vs  rerank-a0.similar.trec        (AP)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            +0.0358
  paired t         t = 2.50   p = 0.0129
  permutation      p = 0.0112   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0077, +0.0640]
  Cohen's d_z      0.144
  MDE @ 80% power  0.0401
  queries changed  123 / 300  (41.0%)
  Holm (2 tests)   p_adj = 0.0112   significant
```

Both candidates improve `.similar` significantly on all three measures. Note that these are the runs
where TEI's 512-**token** limit can bite (see "Tokens are not characters" above): the whole-document
embeds are the truncatable ones, and bge-base still beats nomic's full 2,048-token coverage by
+0.0794 nDCG@10. The metric stays reported, not gated, because the coverage difference means it is not
a clean model-vs-model comparison in either direction.

### NFCorpus `.chunks` — N1 vs N0 (Holm m = 1): reported, not gated

| Arm | nDCG@10 | R@50 | AP |
|---|---|---|---|
| N1 (bge-base) | 0.3701 | 0.2816 | 0.1712 |

N0's recorded values are 0.3517 / 0.2479 / 0.1544 (`2026-09-GATE-reranker-phase1.md`); the deltas
below reproduce them to rounding (0.3701 − 0.0185 = 0.3516; 0.2816 − 0.0336 = 0.2480).

```
[compare] bge-base.chunks.trec  vs  rerank-a0.chunks.trec        (nDCG@10)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            +0.0185
  paired t         t = 2.70   p = 0.0073
  permutation      p = 0.0054   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0050, +0.0319]
  Cohen's d_z      0.150
  MDE @ 80% power  0.0192
  queries changed  200 / 323  (61.9%)
  Holm (1 tests)   p_adj = 0.0054   significant

[compare] bge-base.chunks.trec  vs  rerank-a0.chunks.trec        (R@50)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            +0.0336
  paired t         t = 4.64   p = 0.0000
  permutation      p = 0.0002   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0194, +0.0479]
  Cohen's d_z      0.258
  MDE @ 80% power  0.0203
  queries changed  172 / 323  (53.3%)
  Holm (1 tests)   p_adj = 0.0002   significant

[compare] bge-base.chunks.trec  vs  rerank-a0.chunks.trec        (AP)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            +0.0168
  paired t         t = 3.14   p = 0.0018
  permutation      p = 0.0008   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0063, +0.0273]
  Cohen's d_z      0.175
  MDE @ 80% power  0.0150
  queries changed  256 / 323  (79.3%)
  Holm (1 tests)   p_adj = 0.0008   significant
```

### NFCorpus `.similar` — reported

| Arm | nDCG@10 | R@50 | AP |
|---|---|---|---|
| N1 (bge-base) | 0.3663 | 0.2626 | 0.1654 |

```
[compare] bge-base.similar.trec  vs  rerank-a0.similar.trec        (nDCG@10)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            +0.0262
  paired t         t = 3.58   p = 0.0004
  permutation      p = 0.0004   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0118, +0.0405]
  Cohen's d_z      0.199
  MDE @ 80% power  0.0205
  queries changed  206 / 323  (63.8%)
  Holm (1 tests)   p_adj = 0.0004   significant

[compare] bge-base.similar.trec  vs  rerank-a0.similar.trec        (R@50)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            +0.0361
  paired t         t = 4.71   p = 0.0000
  permutation      p = 0.0002   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0210, +0.0512]
  Cohen's d_z      0.262
  MDE @ 80% power  0.0215
  queries changed  180 / 323  (55.7%)
  Holm (1 tests)   p_adj = 0.0002   significant

[compare] bge-base.similar.trec  vs  rerank-a0.similar.trec        (AP)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            +0.0183
  paired t         t = 3.28   p = 0.0011
  permutation      p = 0.0008   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0073, +0.0292]
  Cohen's d_z      0.183
  MDE @ 80% power  0.0156
  queries changed  255 / 323  (78.9%)
  Holm (1 tests)   p_adj = 0.0008   significant
```

## Gate

The rule (spec §7), applied mechanically to the SciFact `.chunks` block only: a candidate passes if
the 95 % CI lower bound of its **nDCG@10** delta against M0 is **> −0.02** *and* the 95 % CI lower
bound of its **R@50** delta is **> −0.02**. The margin is the size of the largest configuration effect
measured on this corpus (the +0.0134 prefix effect). Superiority and `.similar` are reported, not
required.

One property of the deciding statistic, stated so it is not discovered later: `report.py` builds the
95 % CI **parametrically from the paired t distribution** (`st.t.ppf(0.975, df)` scaled by the standard
error, `report.py:453-455`) — not from the permutation test. Both R@50 comparisons
below therefore carry the tool's own `!! FEW QUERIES CHANGED` advisory ("The paired t-test's
assumptions are likely violated") on the very bound the gate reads: M1's R@50 at 6.0 % of queries
changed, M2's at 5.3 %. Both deciding R@50 bounds are close to the margin — M1's −0.0155 clears it by
0.0045, M2's −0.0228 misses it by 0.0028 — so the advisory is not academic. This is recorded, not
acted on: the gate rule names the 95 % CI and nothing else, and the verdicts below apply it as
written.

### M1 (BAAI/bge-base-en-v1.5, 768 dims): **PASS**

| Criterion | Delta | 95 % CI | CI lower bound | vs −0.02 | |
|---|---|---|---|---|---|
| nDCG@10 | +0.0492 | [+0.0242, +0.0741] | **+0.0242** | +0.0242 > −0.02 | PASS |
| R@50 | +0.0110 | [-0.0155, +0.0375] | **−0.0155** | −0.0155 > −0.02 | PASS |

Both bounds clear the margin, so **M1 passes**.

**Superiority also holds on nDCG@10.** M1's delta is positive (+0.0492) with Holm p_adj = 0.0004 at
m = 2 — significant. bge-base is not merely non-inferior to nomic on SciFact chunk retrieval; it is
measurably better, and by roughly 3.7× the size of the largest previously measured configuration
effect. AP is likewise superior (+0.0457, p_adj = 0.0008). R@50 is not significant (+0.0110,
p_adj = 0.9007), which is expected: only 6.0 % of queries changed on that measure, and `report.py`
flags the paired-t assumption as violated there — the permutation p (0.4504) is the one to read.

This is the first time in this project's benchmark history that a published quality delta has
transferred to this stack. The gate existed precisely because it had not before
(`project-nomic-embedding-task-prefixes`: a predicted +2.3 points measured +0.0134, n.s.). Here the
published gap (bge-base 0.743 vs nomic 0.705, i.e. +0.038) transferred as +0.0492 — the same
direction and a slightly larger magnitude.

### M2 (BAAI/bge-small-en-v1.5, 384 dims): **FAIL**

| Criterion | Delta | 95 % CI | CI lower bound | vs −0.02 | |
|---|---|---|---|---|---|
| nDCG@10 | +0.0070 | [-0.0157, +0.0296] | **−0.0157** | −0.0157 > −0.02 | PASS |
| R@50 | +0.0027 | [-0.0228, +0.0281] | **−0.0228** | −0.0228 **is not** > −0.02 | **FAIL** |

**M2 fails the R@50 criterion by 0.0028.** The rule is applied as written and the verdict is FAIL.

Recorded honestly, because it is a close call and the reason matters: the failure is a **precision**
failure, not evidence of harm. M2's R@50 point estimate is *positive* (+0.0027), only 16 of 300
queries changed on that measure, and the permutation p is 1.0000 — the data show no detectable R@50
difference from nomic in either direction. The CI is simply 0.0028 too wide at the bottom for a
−0.02 non-inferiority margin at n = 300. Nothing here licenses overriding the rule — the margin was
chosen before the numbers were seen, and a gate that bends when a candidate lands just outside it is
not a gate. But if bge-small is ever revisited, the question to answer is whether a wider sample
(or a wider corpus) narrows that bound, not whether the model damaged recall; the measurement did not
detect harm. Its nDCG@10 is also not significantly different from nomic (+0.0070, p_adj = 0.5531).

### Reported, not gated

- **SciFact `.similar`:** M1 +0.0794 nDCG@10 (p_adj = 0.0004), +0.0626 R@50 (p_adj = 0.0016);
  M2 +0.0356 nDCG@10 (p_adj = 0.0098), +0.0503 R@50 (p_adj = 0.0046). Both significantly better;
  see the coverage caveat above.
- **N1 vs N0 (NFCorpus `.chunks`):** +0.0185 nDCG@10, 95 % CI [+0.0050, +0.0319], p_adj = 0.0054 —
  significant; +0.0336 R@50, CI [+0.0194, +0.0479], p_adj = 0.0002 — significant; +0.0168 AP,
  p_adj = 0.0008. bge-base improves a **second, structurally different corpus** — NFCorpus is
  recall-bound (R@50 0.2479 at baseline vs SciFact's 0.9227), and it is the corpus where the reranker
  trial's improvement went negative. The gain generalising across both corpora is the strongest
  available evidence that the SciFact result is the model, not a SciFact artefact. It is nonetheless
  reported, not gated: the gate is SciFact `.chunks` by spec §7.

## Verdict: **GATE PASSED — one candidate (M1, bge-base)**

Per spec §7's consequence table, exactly one candidate passed, so **Phase 2 proceeds with
`BAAI/bge-base-en-v1.5`**. There is no choice for Ben to make between 768 and 384 dims at this gate:
bge-small did not pass, so the "both pass" branch (choose between the 1.57× 768-dim lever and the
4.5× 384-dim lever on the measured quality cost) does not apply. bge-small's 8.71× measured ingest
speed-up on this box is the cost of that outcome, and is recorded here should the question be reopened
with a wider sample.

What Phase 2 inherits (spec §8, still to be planned): a `tei` subchart under
`deploy/helm/iverson/charts/` gated by `condition: tei.enabled`; `baseUrl` on
`global.embeddingModels[]` entries with `iverson.embeddingEnv` rendering
`Embeddings__Models__N__Name/BaseUrl`; `global.activeEmbeddingModel` switched to bge-base with nomic
still listed and pulled while any registered type names it; the TEI service added to `stack.py`'s
`ingest`/`query` tiers and the Launcher's wait; and the documented migration procedure for
already-registered types (clear the type's schema row and collections, re-register, re-ingest — there
is no automatic re-embed and the registration guard blocks an in-place model change).

Two scope limits carry forward with the pass:

1. **The pass is measured under the 512-character chunk window**, not main's 2,048 default, because
   that is the only nomic baseline on today's server (§6.1). The result says the model holds — and
   improves — quality under that window; main's window is a separate, unmerged experiment and Phase 2
   does not inherit a measurement for it.
2. **The `.similar` path's coverage differs** between nomic (2,048-token whole-document context) and
   TEI (512 tokens with `auto_truncate`). Chunk embeds are unaffected. Any Phase 2 decision that
   leans on whole-document embeddings should re-measure with that in mind.

## Box state at close

The box was left on the SciFact nomic baseline, exactly as §6.3 requires:

```
API model:  "Embeddings__ModelId=nomic-embed-text"
API log:    EmbeddingService initialized: model=nomic-embed-text dimension=768 documentPrefix=search_document:  queryPrefix=search_query:
benchmark_documents_tenant_bypass:         "points_count":5183   "size":768
benchmark_documents_chunks_tenant_bypass:  "points_count":19967  "size":768
```

`iverson-tei-embed` is stopped (`Exited (0)`). The six query-tier services (`redis`, `qdrant`,
`ollama`, `postgres`, `authentik-server`, `api`) are healthy; `worker`, `starrocks`, `kafka`,
`zookeeper` and `jaeger` remain stopped from the arms. `BenchmarkDocument` is re-registered under
nomic and `benchmark-query --help` prints `Schemas registered.`. Qdrant holds 7 server-side snapshots
per collection — the pre-existing set, untouched; every snapshot either arm created was downloaded and
then deleted server-side. Every compose action across Tasks 6 and 7 was a single-service `--no-deps`
form; no tier-wide `up`, no `stack.py`, no `down`. Task 8 ran nothing on the box but `report.py`.
