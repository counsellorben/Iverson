# Chunk-level Multivector Storage — Gate Verdict

Recorded 2026-09-06, from `.worktrees/multivector-experiment` HEAD `373d48a` (branch
`multivector-experiment`); local `main` is at `01f421d`.
Corresponds to Task 5 of `docs/plans/2026-09-04-multivector-experiment-implementation-plan.md`; the
gate rule is `docs/specs/2026-09-04-multivector-experiment-design.md` §7.

Full `report.py` output (the source for every number below) lives untracked beside the run files in
`~/repositories/iverson-benchmark-corpora/scifact-gte-2026-09-06/`: `report-all.txt` (structural
checks, absolute scores, ingest throughput), `report-layout.txt` (the gated pair), `report-model.txt`
(the model observation). Run files, ingest log, key map and stats sidecar are in the same directory
(`runs/`, `ingest.log`, `keymap.json`, `keymap.json.stats.json`), together with
`multivector-build.log`, `multivector-query.log`, `multivector-stats.txt`, `runs/raw-latency.json`
and `runs/storage.json`.

Qdrant snapshots: the three gte collections in
`~/repositories/iverson-benchmark-corpora/scifact-gte-qdrant-snapshots/`
(`benchmark_documents_tenant_bypass`, `benchmark_documents_chunks_tenant_bypass` and — added by this
task — `benchmark_documents_multivector_tenant_bypass`, 107 MB). The restored production baseline is
`~/repositories/iverson-benchmark-corpora/scifact-bge-base-qdrant-snapshots/`. The comparison runs
come from `scifact-bge-base-2026-09-04/runs/` (bge-base M1) and `scifact-run-2026-08-26/runs/`
(`rerank-a0`, nomic). Per-task execution logs are the SDD reports under
`.superpowers/sdd/2026-09-04-multivector-experiment-implementation-plan/`.

## Method

The protocol is spec §6. **One** gte-modernbert-base ingest of SciFact (`ingest.py --drop`, TEI at
`http://localhost:8091`, `--chunk-max-chars 512 --chunk-step 448`), then three query runs over the
**same** vectors:

- `gte-chunks-api` — Iverson's own path (`benchmark-query` through the gRPC API: centroid fusion,
  decay, MMR), after the `BenchmarkDocument` schema row was cleared and the API recreated under gte.
- `gte-chunks-raw` — `multivector.py query`: a direct Qdrant named-vector search of the per-chunk
  collection, 250 chunk hits collapsed to 50 documents by max score. The layout **control**.
- `gte-multivector-raw` — `multivector.py query` against a collection derived from the per-chunk
  collection by `multivector.py build`: one point per document holding that document's chunk vectors
  as rows, `multivector_config.comparator = max_sim`, queried for 50 documents.

The derived-arm construction is what makes the gated comparison clean: `build` reads the existing
chunk points and regroups their **byte-identical** vectors — no re-embedding — so the two raw arms
differ only in point layout and scoring shape. Both use default HNSW search parameters, as the API
does. `query` interleaves the two modes per query with one shared query embedding, so the latency
figures are measured under the same conditions and against the same embedding cost (which is
excluded from both).

`build` reported the reconciliation the spec asks for: 5,183 points, 19,967 rows == 19,967 chunk
points, collection green with `indexed_vectors_count` 5,183.

### The 512/448 window ruling, and the single-chunk probe that forced it

The window is **512 characters, step 448** — not the plan's ~2,000. Two measurements decided it
(spec §2.1, §10 rows 1–2). At 2048/1792 SciFact is **81 % single-chunk** (4,180 documents of 5,183;
6,219 chunks total): a multivector point would degenerate to a single row for four documents in five,
MaxSim would reduce to an ordinary cosine search, and the layout arm would be a near-null test. At
512/448 the same corpus is 99 % multi-chunk with **3.85 rows per point** (19,967 chunks over 5,183
documents), which is the regime the layout question is actually about. 512/448 is also the window of
every baseline on disk (`rerank-a0`, `bge-base`), so the model observation needs no re-ingest.

The consequence carries into the verdict: **this is a layout verdict at 512/448.** If gte's 8k
context is ever the question, it needs a long-document corpus and its own control.

### `--baseline`, not `--pair`

Statistics are `report.py --baseline` (`run_paired_statistics`). `--pair` is not used and could not
be: it enforces pool invariance — identical per-query candidate sets — which was built for reranking
arms and which a layout change cannot satisfy, so `--pair` would declare the multivector arm invalid
(spec §11 A13). `PERMUTATION_SEED` (20260831), 10,000 resamples and `HOLM_ALPHA` were left untouched.
The gated pair and the API-vs-raw control were scored in **one** invocation, so Holm corrects at
**m = 2** there; the model observation invocation carries five non-baseline runs, so **m = 5**.
All three invocations exited 0. No script was edited during this task.

### `BUILD UNKNOWN` and `BUILD MISMATCH`

Both raw arms are produced by `multivector.py`, which writes no build sidecar, so every compare block
in the gated pair prints `!! BUILD UNKNOWN`. That is expected and inert **for the gated comparison**:
both arms were produced by the same Python script in the same interleaved loop against the same
Qdrant server, so there is no binary difference to confound them. It is a real limitation only for
the `gte-chunks-api` vs `gte-chunks-raw` block, which genuinely crosses a C# binary and a Python
script — that block is context, not a gated criterion.

`!! BUILD MISMATCH` in the model observation is expected and benign for the reason the migration
established: `bge-base` carries composite `7d3a15092f963723`, `rerank-a0` carries
`31583db5aea49136`, and this branch's build is `faf832574f7b14b5`. The migration's Task 6 same-build
control showed the route change between the first two was ranking-neutral
(`2026-09-GATE-embedding-migration.md`). `report.py` was not edited to suppress either warning.

## Arms

| Arm | Model | Layout | Query path | Rows | Ingest `elapsed_seconds` / `embed_calls` | Query wall clock |
|---|---|---|---|---|---|---|
| `bge-base` (M1, exists) | BAAI/bge-base-en-v1.5 | per-chunk | API (`benchmark-query`) | 15,000 (`.chunks`), 15,000 (`.similar`) | 9,166.55 / 25,128 (22 saved) | 3 m 05 s (2026-09-04) |
| `rerank-a0` (context, exists) | nomic-embed-text | per-chunk | API (`benchmark-query`) | 15,000 (`.chunks`), 15,000 (`.similar`) | 31,070.19 / 25,128 (22 saved) | — (2026-08-26) |
| `gte-chunks-api` | Alibaba-NLP/gte-modernbert-base | per-chunk | API (`benchmark-query`) | 15,000 (`.chunks`), 15,000 (`.similar`) | 11,952.84 / 25,128 (22 saved) | 5 m 12 s |
| `gte-chunks-raw` | same points as above | per-chunk | `multivector.py query` (Qdrant named-vector, 250 chunks → 50 docs) | 15,000 | — (same ingest) | ≈ 43 s (both raw arms, interleaved) |
| `gte-multivector-raw` | same vectors, regrouped | multivector (`max_sim`) | `multivector.py query` (Qdrant query, MaxSim, 50 docs) | 15,000 | — (derived, no embedding) | ≈ 43 s (both raw arms, interleaved) |

The gte ingest: **5,183 documents, 19,967 chunks, 25,128 embed calls (22 saved by the reuse gate),
`elapsed_seconds` 11,952.84** (3 h 19 m 13 s; 2026-09-06 17:27:40Z → 20:46:53Z), 512/448 window,
768 dims, no prefixes. Those chunk and embed-call totals match the nomic and bge-base sidecars
exactly, which corroborates that all three arms chunked the corpus identically. `report.py
--stats-path` puts it at **2.306 s/document, 0.476 s/embed, 1,561.0 docs/hour**. That is slower than
bge-base's 1.769 s/document on the same box — gte-modernbert-base is the larger model, and TEI ran at
`--max-batch-tokens 4096` to avoid the OOM the spec recorded (§10 row 7) — but it is still 2.60×
faster than nomic's 31,070.19 s. It is a by-product of the arm, not a controlled throughput
measurement.

The two raw arms completed far faster than the plan's ≈ 5 min estimate: `build` and `query` together
took under two minutes of wall clock, because SciFact queries are short and TEI embeds 300 of them in
well under a minute.

## Results — absolute scores

From `report-all.txt` and `report-model.txt` (`[scores]` blocks). Every run: 15,000 rows, 300
distinct queries, 300/300 qrels queries covered, no duplicate doc ids, all scores non-zero.

| Arm | Build | nDCG@10 | R@50 | AP |
|---|---|---|---|---|
| `gte-chunks-api` `.chunks` | `faf832574f7b14b5` | 0.7323 | 0.9510 | 0.6999 |
| `gte-chunks-api` `.similar` | `faf832574f7b14b5` | 0.7506 | 0.9533 | 0.7077 |
| `gte-chunks-raw` `.chunks` | unknown | **0.7137** | **0.9467** | 0.6799 |
| `gte-multivector-raw` `.chunks` | unknown | **0.6998** | **0.9167** | 0.6664 |
| `bge-base` `.chunks` | `7d3a15092f963723` | 0.7452 | 0.9337 | 0.7018 |
| `bge-base` `.similar` | `7d3a15092f963723` | 0.7391 | 0.9153 | 0.6941 |
| `rerank-a0` `.chunks` | `31583db5aea49136` | 0.6960 | 0.9227 | 0.6561 |
| `rerank-a0` `.similar` | `31583db5aea49136` | 0.6597 | 0.8527 | 0.6179 |

The two raw runs report `build: unknown` as expected; `gte-chunks-api` carries this branch's
composite `faf832574f7b14b5`.

## Results — the compare blocks, verbatim

### THE GATE: `gte-multivector-raw` vs `gte-chunks-raw`, plus `gte-chunks-api` vs the same control (Holm m = 2)

From `report-layout.txt`.

```
[compare] gte-multivector-raw.chunks.trec  vs  gte-chunks-raw.chunks.trec        (nDCG@10)
  !! BUILD UNKNOWN: at least one of these runs has no build sidecar.
     Build agreement cannot be confirmed.
  delta            -0.0140
  paired t         t = -1.60   p = 0.1107
  permutation      p = 0.1210   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0312, +0.0032]
  Cohen's d_z      -0.092
  MDE @ 80% power  0.0245
  queries changed  18 / 300  (6.0%)
  Holm (2 tests)   p_adj = 0.1210   not significant
  !! FEW QUERIES CHANGED: only 6.0% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] gte-chunks-api.chunks.trec  vs  gte-chunks-raw.chunks.trec        (nDCG@10)
  !! BUILD UNKNOWN: at least one of these runs has no build sidecar.
     Build agreement cannot be confirmed.
  delta            +0.0186
  paired t         t = 2.64   p = 0.0087
  permutation      p = 0.0096   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0047, +0.0324]
  Cohen's d_z      0.152
  MDE @ 80% power  0.0197
  queries changed  70 / 300  (23.3%)
  Holm (2 tests)   p_adj = 0.0192   significant

[compare] gte-multivector-raw.chunks.trec  vs  gte-chunks-raw.chunks.trec        (R@50)
  !! BUILD UNKNOWN: at least one of these runs has no build sidecar.
     Build agreement cannot be confirmed.
  delta            -0.0300
  paired t         t = -2.74   p = 0.0065
  permutation      p = 0.0108   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0515, -0.0085]
  Cohen's d_z      -0.158
  MDE @ 80% power  0.0306
  queries changed  11 / 300  (3.7%)
  Holm (2 tests)   p_adj = 0.0216   significant
  !! FEW QUERIES CHANGED: only 3.7% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] gte-chunks-api.chunks.trec  vs  gte-chunks-raw.chunks.trec        (R@50)
  !! BUILD UNKNOWN: at least one of these runs has no build sidecar.
     Build agreement cannot be confirmed.
  delta            +0.0043
  paired t         t = 0.48   p = 0.6310
  permutation      p = 0.7405   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0134, +0.0221]
  Cohen's d_z      0.028
  MDE @ 80% power  0.0252
  queries changed  9 / 300  (3.0%)
  Holm (2 tests)   p_adj = 0.7405   not significant
  !! FEW QUERIES CHANGED: only 3.0% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] gte-multivector-raw.chunks.trec  vs  gte-chunks-raw.chunks.trec        (AP)
  !! BUILD UNKNOWN: at least one of these runs has no build sidecar.
     Build agreement cannot be confirmed.
  delta            -0.0135
  paired t         t = -1.53   p = 0.1283
  permutation      p = 0.1352   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0309, +0.0039]
  Cohen's d_z      -0.088
  MDE @ 80% power  0.0248
  queries changed  45 / 300  (15.0%)
  Holm (2 tests)   p_adj = 0.1352   not significant

[compare] gte-chunks-api.chunks.trec  vs  gte-chunks-raw.chunks.trec        (AP)
  !! BUILD UNKNOWN: at least one of these runs has no build sidecar.
     Build agreement cannot be confirmed.
  delta            +0.0199
  paired t         t = 2.39   p = 0.0175
  permutation      p = 0.0188   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0035, +0.0363]
  Cohen's d_z      0.138
  MDE @ 80% power  0.0234
  queries changed  102 / 300  (34.0%)
  Holm (2 tests)   p_adj = 0.0376   significant
```

The spec predicted the `FEW QUERIES CHANGED` banner on this pair (§7: "because the arms share
vectors, many exact-zero per-query deltas are expected"), and it appeared on both gated measures —
6.0 % of queries changed on nDCG@10, 3.7 % on R@50. As the banner and the spec both direct, the
permutation p is the one read below.

The `gte-chunks-api` blocks are the API-vs-raw control, not a gate criterion: Iverson's ranking stack
(centroid fusion, decay, MMR) is worth **+0.0186 nDCG@10** (p_adj = 0.0192) and **+0.0199 AP**
(p_adj = 0.0376) over the raw per-chunk max-collapse on the same vectors, with R@50 unchanged
(+0.0043, n.s.). That block crosses a C# binary and a Python script, so it is read as a direction,
not a calibrated number.

### The model observation: `gte-chunks-api` vs `bge-base` (Holm m = 5)

From `report-model.txt`. This is reported with statistics and **no threshold** (spec §7).

Note on how to read this block: `report.py --baseline` compares every non-baseline run against the
one baseline file, so the `.similar` runs are also compared against `bge-base.chunks.trec`. Those
cross-RPC rows are not like-for-like comparisons; the `.similar` numbers that matter are the absolute
scores in the table above. The rows that answer the model question are
`gte-chunks-api.chunks.trec vs bge-base.chunks.trec`.

```
[compare] gte-chunks-api.chunks.trec  vs  bge-base.chunks.trec        (nDCG@10)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            -0.0129
  paired t         t = -1.07   p = 0.2843
  permutation      p = 0.2818   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0366, +0.0108]
  Cohen's d_z      -0.062
  MDE @ 80% power  0.0337
  queries changed  93 / 300  (31.0%)
  Holm (5 tests)   p_adj = 0.8453   not significant

[compare] gte-chunks-api.similar.trec  vs  bge-base.chunks.trec        (nDCG@10)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            +0.0054
  paired t         t = 0.44   p = 0.6623
  permutation      p = 0.6681   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0190, +0.0298]
  Cohen's d_z      0.025
  MDE @ 80% power  0.0347
  queries changed  106 / 300  (35.3%)
  Holm (5 tests)   p_adj = 0.8453   not significant

[compare] bge-base.similar.trec  vs  bge-base.chunks.trec        (nDCG@10)
  delta            -0.0062
  paired t         t = -0.89   p = 0.3734
  permutation      p = 0.3848   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0198, +0.0075]
  Cohen's d_z      -0.051
  MDE @ 80% power  0.0194
  queries changed  70 / 300  (23.3%)
  Holm (5 tests)   p_adj = 0.8453   not significant

[compare] rerank-a0.chunks.trec  vs  bge-base.chunks.trec        (nDCG@10)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            -0.0492
  paired t         t = -3.88   p = 0.0001
  permutation      p = 0.0002   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0741, -0.0242]
  Cohen's d_z      -0.224
  MDE @ 80% power  0.0355
  queries changed  99 / 300  (33.0%)
  Holm (5 tests)   p_adj = 0.0010   significant

[compare] rerank-a0.similar.trec  vs  bge-base.chunks.trec        (nDCG@10)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            -0.0856
  paired t         t = -5.74   p = 0.0000
  permutation      p = 0.0002   (10,000 sign flips, seed 20260831)
  95% CI           [-0.1149, -0.0563]
  Cohen's d_z      -0.332
  MDE @ 80% power  0.0417
  queries changed  103 / 300  (34.3%)
  Holm (5 tests)   p_adj = 0.0010   significant

[compare] gte-chunks-api.chunks.trec  vs  bge-base.chunks.trec        (R@50)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            +0.0173
  paired t         t = 1.26   p = 0.2083
  permutation      p = 0.2356   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0097, +0.0444]
  Cohen's d_z      0.073
  MDE @ 80% power  0.0385
  queries changed  18 / 300  (6.0%)
  Holm (5 tests)   p_adj = 0.4712   not significant
  !! FEW QUERIES CHANGED: only 6.0% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] gte-chunks-api.similar.trec  vs  bge-base.chunks.trec        (R@50)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            +0.0197
  paired t         t = 1.62   p = 0.1062
  permutation      p = 0.1146   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0042, +0.0436]
  Cohen's d_z      0.094
  MDE @ 80% power  0.0340
  queries changed  16 / 300  (5.3%)
  Holm (5 tests)   p_adj = 0.3438   not significant
  !! FEW QUERIES CHANGED: only 5.3% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] bge-base.similar.trec  vs  bge-base.chunks.trec        (R@50)
  delta            -0.0183
  paired t         t = -2.42   p = 0.0161
  permutation      p = 0.0356   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0332, -0.0034]
  Cohen's d_z      -0.140
  MDE @ 80% power  0.0212
  queries changed  6 / 300  (2.0%)
  Holm (5 tests)   p_adj = 0.1424   not significant
  !! FEW QUERIES CHANGED: only 2.0% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] rerank-a0.chunks.trec  vs  bge-base.chunks.trec        (R@50)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            -0.0110
  paired t         t = -0.82   p = 0.4145
  permutation      p = 0.4504   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0375, +0.0155]
  Cohen's d_z      -0.047
  MDE @ 80% power  0.0377
  queries changed  18 / 300  (6.0%)
  Holm (5 tests)   p_adj = 0.4712   not significant
  !! FEW QUERIES CHANGED: only 6.0% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] rerank-a0.similar.trec  vs  bge-base.chunks.trec        (R@50)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            -0.0809
  paired t         t = -4.43   p = 0.0000
  permutation      p = 0.0002   (10,000 sign flips, seed 20260831)
  95% CI           [-0.1169, -0.0450]
  Cohen's d_z      -0.256
  MDE @ 80% power  0.0512
  queries changed  37 / 300  (12.3%)
  Holm (5 tests)   p_adj = 0.0010   significant

[compare] gte-chunks-api.chunks.trec  vs  bge-base.chunks.trec        (AP)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            -0.0019
  paired t         t = -0.15   p = 0.8818
  permutation      p = 0.8765   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0276, +0.0238]
  Cohen's d_z      -0.009
  MDE @ 80% power  0.0366
  queries changed  116 / 300  (38.7%)
  Holm (5 tests)   p_adj = 1.0000   not significant

[compare] gte-chunks-api.similar.trec  vs  bge-base.chunks.trec        (AP)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            +0.0059
  paired t         t = 0.42   p = 0.6735
  permutation      p = 0.6673   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0215, +0.0333]
  Cohen's d_z      0.024
  MDE @ 80% power  0.0390
  queries changed  120 / 300  (40.0%)
  Holm (5 tests)   p_adj = 1.0000   not significant

[compare] bge-base.similar.trec  vs  bge-base.chunks.trec        (AP)
  delta            -0.0077
  paired t         t = -1.01   p = 0.3156
  permutation      p = 0.3172   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0227, +0.0074]
  Cohen's d_z      -0.058
  MDE @ 80% power  0.0214
  queries changed  81 / 300  (27.0%)
  Holm (5 tests)   p_adj = 0.9515   not significant

[compare] rerank-a0.chunks.trec  vs  bge-base.chunks.trec        (AP)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            -0.0457
  paired t         t = -3.53   p = 0.0005
  permutation      p = 0.0004   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0712, -0.0202]
  Cohen's d_z      -0.204
  MDE @ 80% power  0.0363
  queries changed  114 / 300  (38.0%)
  Holm (5 tests)   p_adj = 0.0016   significant

[compare] rerank-a0.similar.trec  vs  bge-base.chunks.trec        (AP)
  !! BUILD MISMATCH: these runs came from different binaries.
     Any comparison between them is confounded.
  delta            -0.0839
  paired t         t = -5.66   p = 0.0000
  permutation      p = 0.0002   (10,000 sign flips, seed 20260831)
  95% CI           [-0.1131, -0.0547]
  Cohen's d_z      -0.327
  MDE @ 80% power  0.0416
  queries changed  116 / 300  (38.7%)
  Holm (5 tests)   p_adj = 0.0010   significant
```

## Latency and storage

From `runs/raw-latency.json` (300 queries, both modes interleaved per query, embedding excluded,
default HNSW search parameters on both) and `runs/storage.json` / `multivector-stats.txt`.

| Mode | n | p50 | p95 | mean |
|---|---|---|---|---|
| per-chunk (`gte-chunks-raw`, top-250 → 50) | 300 | 10.84 ms | **26.46 ms** | 13.39 ms |
| multivector (`gte-multivector-raw`, MaxSim, 50) | 300 | 6.30 ms | **16.12 ms** | 8.01 ms |
| **ratio (multivector ÷ per-chunk)** | | 0.58× | **0.61×** | 0.60× |

| Collection | Points | Indexed vectors | Segments | Disk |
|---|---|---|---|---|
| `benchmark_documents_chunks_tenant_bypass` | 19,967 | 16,678 | 2 | 130 M |
| `benchmark_documents_multivector_tenant_bypass` | 5,183 | 5,183 | 2 | 103 M |

The multivector layout holds the same 19,967 vectors in **26 % of the points** and **79 % of the
disk**, and is **1.64× faster at p95**. Both collections reported `status: green` at query time.
Absolute latency numbers are this box only; the ratio is what the gate reads.

The measured window is what `time.perf_counter()` brackets around `ingest.qdrant_request` in
`cmd_query` — the full client-side round trip, including HTTP setup and `json.loads` of the response,
not server-side search time alone. The two calls also return different response sizes: the chunk
search asks for 250 hits with `parent_id` payload, the multivector query for 50 hits with `docId`
payload, so the client-side `json.loads` is ~5× larger for the control arm. Criterion 3 passes at
0.61× against a 1.25× ceiling, so this has no effect on the verdict even in the (implausible) worst
case that the entire measured difference were this client-side parse rather than server-side search —
but a reader taking "1.64× faster" as an adoption number should know the window is round-trip, not
search-only.

Arm order within each query was also fixed, not alternated: the chunk search always ran first,
immediately after the shared query embedding call, and the multivector query always second, so any
warm-up or cache effect across the pair is charged entirely to one arm. The spec asked for the two
modes to be interleaved *across* queries sharing one embedding, which is what was delivered; a fully
alternating per-query order (swapping which arm goes first) would have removed this last ordering
confound as well.

## Gate

The rule (spec §7): **Go** requires all three of —

1. 95 % CI lower bound of the nDCG@10 delta, `gte-multivector-raw` − `gte-chunks-raw`, above −0.02;
2. the same for R@50;
3. multivector p95 query latency ≤ 1.25 × per-chunk p95.

Anything short of all three is no-go, and this document names the failing criterion.

| # | Criterion | Deciding number | Threshold | |
|---|---|---|---|---|
| 1 | nDCG@10 delta 95 % CI lower bound | **−0.0312** (delta −0.0140, CI [−0.0312, +0.0032]) | must be > −0.02 | **FAIL** |
| 2 | R@50 delta 95 % CI lower bound | **−0.0515** (delta −0.0300, CI [−0.0515, −0.0085]) | must be > −0.02 | **FAIL** |
| 3 | p95 latency ratio | **0.61×** (16.12 ms ÷ 26.46 ms) | must be ≤ 1.25× | **PASS** |

### Verdict: **NO-GO**

Two of the three criteria fail: **criterion 1 (nDCG@10 CI lower bound −0.0312) and criterion 2 (R@50
CI lower bound −0.0515)**, both below the −0.02 non-inferiority margin. Criterion 3 passes
comfortably. Per §7 the layout is **not** adopted; `_chunks` stays as it is.

Two properties of the failure, recorded so they are not rediscovered later:

- **The R@50 failure is a measured loss, not a precision failure.** Unlike the migration's bge-small
  near-miss, this delta is *negative at the point estimate* (−0.0300) and *significant*: permutation
  p = 0.0108, Holm p_adj = 0.0216 at m = 2. Only 11 of 300 queries changed on R@50, so the
  `FEW QUERIES CHANGED` banner fires on the deciding statistic — but the permutation test the banner
  directs us to agrees with the parametric one. Regrouping the same vectors into MaxSim points
  measurably loses recall on this corpus.
- **The nDCG@10 failure is a precision failure on top of a negative point estimate.** The delta is
  −0.0140 and not significant (permutation p = 0.1210, p_adj = 0.1210, MDE at 80 % power 0.0245), so
  the data do not establish a real nDCG@10 difference; the CI is simply wide enough at the bottom
  (−0.0312) to miss a −0.02 margin at n = 300. A wider sample could plausibly move this bound. It
  could not rescue the verdict on its own, because criterion 2 fails on the point estimate.

The most likely mechanism is the one the spec flagged as inside the comparison (plan "Known issues"):
**HNSW approximation differs between the two shapes.** The per-chunk arm retrieves 250 chunk hits and
collapses them to 50 documents — an effective over-fetch of 5× that gives HNSW many chances to surface
a document through any of its chunks. The multivector arm asks HNSW for 50 documents directly, with
one graph node per document. That is exactly the recall difference an adoption would experience, so it
belongs inside the gate rather than being controlled away — but it means the result is a verdict on
*this* query shape at *this* budget, not a claim that MaxSim scoring is intrinsically worse. An
adoption spec that wanted to revisit it would have to over-fetch on the multivector side (and pay the
latency, of which there is a large margin: 0.61× against a 1.25× ceiling).

A second asymmetry sits alongside the over-fetch one: the two arms were not equally indexed at query
time. `runs/storage.json` and the `index_state` sidecar in `runs/raw-latency.json` record the chunks
collection (`benchmark_documents_chunks_tenant_bypass`) as `status: green` with `indexed_vectors_count`
16,678 of 19,967 points (2 segments), while the multivector collection
(`benchmark_documents_multivector_tenant_bypass`) was 5,183 of 5,183 (also 2 segments) — fully indexed.
In Qdrant, a segment below `indexing_threshold` is searched exactly rather than through HNSW; 19,967 −
16,678 = 3,289 vectors, consistent with one sub-threshold segment on the chunks side. So roughly 16 % of
the control arm's corpus was brute-force (perfect-recall) searched while the arm under test was 100 %
approximate. This biases both deciding numbers in the direction observed: it inflates the control's
R@50 (criterion 2, which already fails on the point estimate with only 11/300 queries changed) and it
inflates the control's latency, since exact search over a small segment is not necessarily cheaper than
approximate search but removes any approximation error from that slice (criterion 3, which passes). The
verdict stands regardless — criterion 1 (nDCG@10) fails independently of this asymmetry, and the bias
is bounded to the ~16 % sub-threshold slice — so this is recorded as a documentation gap, not a
verdict correction. `multivector.py build`'s `wait_for_index` (see script, `wait_for_index`) was applied
only to the collection it had just built (the multivector collection); the pre-existing chunks
collection was never re-checked. `cmd_query`'s precondition (`multivector.py`, `cmd_query`) checks
`info["status"] != "green"` but not `indexed_vectors_count`, so a collection sitting below its indexing
threshold still passes the gate that is supposed to stop the measurement. This is a gap in the spec
(§7/§12 never required equal index state between the two arms, only that HNSW approximation itself sits
inside the comparison) and in the plan (Task 5/P37 verified the indexed state of the newly built
collection only, not the pre-existing one) as much as it is a gap in the harness. A re-attempt should
equalise index state before measuring — raise `indexing_threshold` on both collections (or set it to 0
to force always-indexed segments) or force-optimise both collections so neither has a sub-threshold
segment at query time.

### The model observation (reported, no threshold)

`gte-chunks-api` vs `bge-base` `.chunks`, the same window, the same API ranking stack, Holm m = 5:

| Measure | Delta | 95 % CI | Permutation p | Holm p_adj |
|---|---|---|---|---|
| nDCG@10 | −0.0129 | [−0.0366, +0.0108] | 0.2818 | 0.8453 — n.s. |
| R@50 | +0.0173 | [−0.0097, +0.0444] | 0.2356 | 0.4712 — n.s. |
| AP | −0.0019 | [−0.0276, +0.0238] | 0.8765 | 1.0000 — n.s. |

**gte-modernbert-base is not distinguishable from bge-base on SciFact at this window.** All three
measures are non-significant, the nDCG@10 and AP point estimates are slightly negative, R@50 slightly
positive, and every CI spans zero. There is no measured reason to move Iverson off bge-base for this
model, and gte costs 1.30× more ingest time on this box (11,952.84 s vs 9,166.55 s) for it. The
comparison crosses builds (`faf832574f7b14b5` vs `7d3a15092f963723`), which the migration's same-build
control established as ranking-neutral.

**Context — `rerank-a0` (nomic):** −0.0492 nDCG@10 (p_adj = 0.0010), −0.0110 R@50 (n.s.), −0.0457 AP
(p_adj = 0.0016) against `bge-base`. This is the migration's own result re-printed from the other
direction and it reproduces exactly. It sets the scale: bge-base beat nomic by 0.0492 nDCG@10, while
gte differs from bge-base by −0.0129 n.s. — i.e. the model axis of this experiment moved nothing
comparable to the migration's own step.

## Adoption implications (spec §8) — inputs to a follow-up spec, if one is ever written

The gate is NO-GO, so none of this is acted on. It is restated because it is what an adoption spec
would have had to solve, and the cost is unmeasured here:

- `SearchChunks` returns the matched chunk's **text and index**, and the reranker harness consumed the
  winning chunk text. **MaxSim returns the document and its score but not which row won.** An adopting
  design must recover the winning row itself — retrieve the point's rows and argmax the dot product
  locally — and carry chunk texts in the point payload.
- Neither the extra retrieval round-trip nor the payload growth was measured in this experiment. Both
  would eat into the 0.61× p95 margin, which is the one criterion that passed.
- Any re-attempt should first settle the over-fetch question above: a multivector arm asking for more
  than 50 documents and truncating, so the two arms have comparable HNSW recall budgets.
- The multivector arm writes TREC rows straight from each point's `payload.docId` with no dedupe,
  unlike the chunk arm's `collapse_by_doc`; two multivector points sharing a `docId` would produce a
  malformed run. Here `report.py`'s duplicate-doc-id structural check covered it and reported none, but
  an adoption path should not rely on the scorer to catch that — it should dedupe at write time.

## Scope limits carried forward

1. **This is a layout verdict at the 512/448 character window on SciFact.** At 2048/1792 the corpus is
   81 % single-chunk and the arm degenerates; the long-context question needs a long-document corpus
   (FreshStack at 2048/1792 is 73 % multi-chunk) and its own control.
2. **HNSW approximation is inside the comparison**, deliberately (see the mechanism note above).
3. **Latency is this box, interleaved, embedding excluded.** Absolute numbers do not transfer; the
   ratio is the gate.
4. The `.similar` runs are reported, never gated, and their compare blocks in `report-model.txt` are
   scored against a `.chunks` baseline — read the absolute scores, not those deltas.

## Box state at close

The box was restored to the bge-base baseline per spec §6 step 8:

```
benchmark_documents_tenant_bypass:         "points_count":5183
benchmark_documents_chunks_tenant_bypass:  "points_count":19967   "size":768
tei-embed /info:  "model_id":"BAAI/bge-base-en-v1.5"  "max_input_length":512  "max_batch_tokens":16384
iverson-api log:     EmbeddingService initialized: model=BAAI/bge-base-en-v1.5 dimension=768
iverson-worker log:  EmbeddingService initialized: model=BAAI/bge-base-en-v1.5 dimension=768
benchmark-query --help:  Schemas registered.
```

The three gte collections were deleted after being snapshotted (including
`benchmark_documents_multivector_tenant_bypass`), the bge-base snapshots were re-uploaded, and the
gte `BenchmarkDocument` schema row was deleted so the type re-registers under bge-base. `tei-embed`
was recreated on bge-base at TEI's own default `max_batch_tokens` (16384) from a shell with neither
`BENCH_EMBED_MODEL` nor `TEI_MAX_BATCH_TOKENS` set; `iverson-zookeeper`, `iverson-kafka`,
`iverson-starrocks` and `iverson-jaeger` were restarted, and `iverson-api` / `iverson-worker`
recreated on defaults. Qdrant now holds only the pre-existing collections
(`benchmark_documents_tenant_bypass`, `benchmark_documents_chunks_tenant_bypass`, `iverson-probe`,
`vector_docs_tenant_bypass`, `vector_docs_chunks_tenant_bypass`). Every snapshot this task created was
downloaded and then deleted server-side. Every compose action was a single-service `--no-deps` form;
no tier-wide `up`, no `stack.py`, no `down`.
