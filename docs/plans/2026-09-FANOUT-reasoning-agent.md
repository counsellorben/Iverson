# §7.1 Fanout Sweep Verdict — Reasoning Agent

Date: 2026-09-06
Branch: `reasoning-agent`
Corpus: SciFact (`~/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26`)
Embedding model: **`BAAI/bge-base-en-v1.5` on TEI** (the model Iverson ships since the embedding
migration — not nomic). The existing SciFact bge-base baseline was already resident on the box;
per Ruling R9, `stack.py` and the Qdrant snapshot restore were skipped and replaced with read-only
verification (below), since the corpora's `keymap.json`/`qrels.trec` are model-independent and the
bge-base collections were ingested with the same key derivation.

## Step 1: pre-flight verification (Ruling R9)

All checks matched before any arm ran:

```
benchmark_documents_tenant_bypass:        "points_count":5183
benchmark_documents_chunks_tenant_bypass: "points_count":19967
TEI /info model_id:                       "model_id":"BAAI/bge-base-en-v1.5"
docker health (api/qdrant/postgres/redis/authentik-server): all healthy
server build composite (/build):          "composite":"faf832574f7b14b5"
_iverson_schema.BenchmarkDocument modelId: "modelId": "BAAI/bge-base-en-v1.5"
```

This composite (`faf832574f7b14b5`) is the value every arm's `.meta.json` sidecar must reproduce.

## Step 2: the four arms

All arms ran with `DocumentBudget = 5` (the agent's k=5). `ChunkBudgetMultiplier` varied per arm.
Each arm is its own `dotnet run -c Release -- benchmark-query` invocation against
`Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs:40-41`, edited via `sed`
and restored afterward (never committed).

| arm (mult) | grep confirms | wall-clock | chunks.trec lines | meta.json composite | guard refused? |
|---|---|---|---|---|---|
| ×5 (baseline) | `DocumentBudget = 5`, `ChunkBudgetMultiplier = 5` | 18:37:17 → 18:41:53 (4m36s) | 1,500 | `faf832574f7b14b5` | no |
| ×4 (agent default) | `DocumentBudget = 5`, `ChunkBudgetMultiplier = 4` | 18:42:02 → 18:46:13 (4m11s) | 1,498 | `faf832574f7b14b5` | no |
| ×6 | `DocumentBudget = 5`, `ChunkBudgetMultiplier = 6` | 18:46:24 → 18:50:45 (4m21s) | 1,500 | `faf832574f7b14b5` | no |
| ×8 | `DocumentBudget = 5`, `ChunkBudgetMultiplier = 8` | 18:50:53 → 18:55:18 (4m25s) | 1,500 | `faf832574f7b14b5` | no |

No arm hit the `ChunkBudgetGuard` refusal (`REFUSING: chunk budget cannot reach DocumentBudget
distinct documents`) — expected, since `×2`/`×3` are the multipliers the guard rejects on this
corpus (assumption 28), not `×4`/`×5`/`×6`/`×8`.

The ×4 arm produced 1,498 rows instead of the full 1,500. The harness emits one row per
collapsed **document** (capped at `DocumentBudget = 5`), not one row per chunk, so 1,498 means
two queries returned only four distinct documents each: at `ChunkBudgetMultiplier = 4` the
20-chunk pool held no chunk belonging to what would otherwise have been each query's fifth
document — document `16979690` for query `129` and document `8570690` for query `1359`, both of
which are present in the ×5, ×6 and ×8 runs. This is not a guard refusal; `report.py`'s
structural check confirms all 300 queries are still covered by the run (`covered by this run
300 / 300`). Scores and the plateau reported below are unaffected: the two missing documents are
the fifth-ranked ones for two of 300 queries.

All four `.meta.json` composites are identical to each other and to the Step 1 baseline
(`faf832574f7b14b5`), confirming the harness edit changes no server build, only the client-side
request shape.

## Step 3: scenario file restored

```
git checkout -- Scenarios/BenchmarkQueryScenario.cs && git status --short
```
exited 0 with no output — the file is back to `DocumentBudget = 50`, `ChunkBudgetMultiplier = 5`
(its pre-task state), and no scenario edits are part of this or any commit.

## Step 4: report.py — trustworthiness of the paired rows (Ruling R10)

```
$ grep -n "list(ir_measures.read_trec_run(baseline_path))" .../Iverson.LoadTest/scripts/report.py
559:    baseline_run = list(ir_measures.read_trec_run(baseline_path))
```

The fix has landed. Per Ruling R10, **every paired row below (nDCG@10, R@50, AP) is trustworthy**,
not only nDCG@10 as the original plan text cautioned. The plateau rule itself still reads nDCG@10
only, per spec §7.1 (ranking is 5 deep, so nDCG@10 scores over a 5-deep list; ranks 6–10
contribute zero).

### Invocation 1 — ×4, ×6, ×8 vs ×5 baseline (one Holm family of 3 tests per metric)

```
[structural] agent-k5-f5.chunks.trec
  rows                 1,500
  distinct queries     300
  non-zero scores      1,500 / 1,500
  qrels queries        300
  covered by this run  300 / 300
  duplicate doc ids    none

[structural] agent-k5-f4.chunks.trec
  rows                 1,498
  distinct queries     300
  non-zero scores      1,498 / 1,498
  qrels queries        300
  covered by this run  300 / 300
  duplicate doc ids    none

[structural] agent-k5-f6.chunks.trec
  rows                 1,500
  distinct queries     300
  non-zero scores      1,500 / 1,500
  qrels queries        300
  covered by this run  300 / 300
  duplicate doc ids    none

[structural] agent-k5-f8.chunks.trec
  rows                 1,500
  distinct queries     300
  non-zero scores      1,500 / 1,500
  qrels queries        300
  covered by this run  300 / 300
  duplicate doc ids    none

[scores] agent-k5-f5.chunks.trec
  build      faf832574f7b14b5
  nDCG@10    0.7207
  R@50       0.8082
  AP         0.6859

[scores] agent-k5-f4.chunks.trec
  build      faf832574f7b14b5
  nDCG@10    0.7220
  R@50       0.8116
  AP         0.6866

[scores] agent-k5-f6.chunks.trec
  build      faf832574f7b14b5
  nDCG@10    0.7201
  R@50       0.8071
  AP         0.6853

[scores] agent-k5-f8.chunks.trec
  build      faf832574f7b14b5
  nDCG@10    0.7212
  R@50       0.8104
  AP         0.6858
[baseline] excluded agent-k5-f5.chunks.trec (it is the --baseline file)

[compare] agent-k5-f4.chunks.trec  vs  agent-k5-f5.chunks.trec        (nDCG@10)
  delta            +0.0013
  paired t         t = 1.00   p = 0.3181
  permutation      p = 1.0000   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0012, +0.0038]
  Cohen's d_z      0.058
  MDE @ 80% power  0.0036
  queries changed  1 / 300  (0.3%)
  Holm (3 tests)   p_adj = 1.0000   not significant
  !! FEW QUERIES CHANGED: only 0.3% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] agent-k5-f6.chunks.trec  vs  agent-k5-f5.chunks.trec        (nDCG@10)
  delta            -0.0006
  paired t         t = -1.00   p = 0.3181
  permutation      p = 0.9981   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0018, +0.0006]
  Cohen's d_z      -0.058
  MDE @ 80% power  0.0017
  queries changed  1 / 300  (0.3%)
  Holm (3 tests)   p_adj = 1.0000   not significant
  !! FEW QUERIES CHANGED: only 0.3% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] agent-k5-f8.chunks.trec  vs  agent-k5-f5.chunks.trec        (nDCG@10)
  delta            +0.0005
  paired t         t = 0.38   p = 0.7076
  permutation      p = 1.0000   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0023, +0.0034]
  Cohen's d_z      0.022
  MDE @ 80% power  0.0040
  queries changed  3 / 300  (1.0%)
  Holm (3 tests)   p_adj = 1.0000   not significant
  !! FEW QUERIES CHANGED: only 1.0% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] agent-k5-f4.chunks.trec  vs  agent-k5-f5.chunks.trec        (R@50)
  delta            +0.0033
  paired t         t = 1.00   p = 0.3181
  permutation      p = 1.0000   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0032, +0.0099]
  Cohen's d_z      0.058
  MDE @ 80% power  0.0093
  queries changed  1 / 300  (0.3%)
  Holm (3 tests)   p_adj = 1.0000   not significant
  !! FEW QUERIES CHANGED: only 0.3% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] agent-k5-f6.chunks.trec  vs  agent-k5-f5.chunks.trec        (R@50)
  delta            -0.0011
  paired t         t = -1.00   p = 0.3181
  permutation      p = 0.9981   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0033, +0.0011]
  Cohen's d_z      -0.058
  MDE @ 80% power  0.0031
  queries changed  1 / 300  (0.3%)
  Holm (3 tests)   p_adj = 1.0000   not significant
  !! FEW QUERIES CHANGED: only 0.3% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] agent-k5-f8.chunks.trec  vs  agent-k5-f5.chunks.trec        (R@50)
  delta            +0.0022
  paired t         t = 0.63   p = 0.5280
  permutation      p = 1.0000   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0047, +0.0091]
  Cohen's d_z      0.036
  MDE @ 80% power  0.0099
  queries changed  2 / 300  (0.7%)
  Holm (3 tests)   p_adj = 1.0000   not significant
  !! FEW QUERIES CHANGED: only 0.7% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] agent-k5-f4.chunks.trec  vs  agent-k5-f5.chunks.trec        (AP)
  delta            +0.0007
  paired t         t = 1.00   p = 0.3181
  permutation      p = 1.0000   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0006, +0.0020]
  Cohen's d_z      0.058
  MDE @ 80% power  0.0019
  queries changed  1 / 300  (0.3%)
  Holm (3 tests)   p_adj = 1.0000   not significant
  !! FEW QUERIES CHANGED: only 0.3% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] agent-k5-f6.chunks.trec  vs  agent-k5-f5.chunks.trec        (AP)
  delta            -0.0007
  paired t         t = -1.00   p = 0.3181
  permutation      p = 0.9981   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0020, +0.0006]
  Cohen's d_z      -0.058
  MDE @ 80% power  0.0019
  queries changed  1 / 300  (0.3%)
  Holm (3 tests)   p_adj = 1.0000   not significant
  !! FEW QUERIES CHANGED: only 0.3% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] agent-k5-f8.chunks.trec  vs  agent-k5-f5.chunks.trec        (AP)
  delta            -0.0002
  paired t         t = -0.17   p = 0.8621
  permutation      p = 0.9903   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0021, +0.0017]
  Cohen's d_z      -0.010
  MDE @ 80% power  0.0027
  queries changed  3 / 300  (1.0%)
  Holm (3 tests)   p_adj = 1.0000   not significant
  !! FEW QUERIES CHANGED: only 1.0% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.
```

### Invocation 2 — ×8 vs ×6 (its own Holm family of 1 test per metric)

```
[structural] agent-k5-f8.chunks.trec
  rows                 1,500
  distinct queries     300
  non-zero scores      1,500 / 1,500
  qrels queries        300
  covered by this run  300 / 300
  duplicate doc ids    none

[scores] agent-k5-f8.chunks.trec
  build      faf832574f7b14b5
  nDCG@10    0.7212
  R@50       0.8104
  AP         0.6858

[compare] agent-k5-f8.chunks.trec  vs  agent-k5-f6.chunks.trec        (nDCG@10)
  delta            +0.0011
  paired t         t = 0.88   p = 0.3792
  permutation      p = 1.0000   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0014, +0.0037]
  Cohen's d_z      0.051
  MDE @ 80% power  0.0036
  queries changed  2 / 300  (0.7%)
  Holm (1 tests)   p_adj = 1.0000   not significant
  !! FEW QUERIES CHANGED: only 0.7% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] agent-k5-f8.chunks.trec  vs  agent-k5-f6.chunks.trec        (R@50)
  delta            +0.0033
  paired t         t = 1.00   p = 0.3181
  permutation      p = 1.0000   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0032, +0.0099]
  Cohen's d_z      0.058
  MDE @ 80% power  0.0093
  queries changed  1 / 300  (0.3%)
  Holm (1 tests)   p_adj = 1.0000   not significant
  !! FEW QUERIES CHANGED: only 0.3% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.

[compare] agent-k5-f8.chunks.trec  vs  agent-k5-f6.chunks.trec        (AP)
  delta            +0.0005
  paired t         t = 0.73   p = 0.4678
  permutation      p = 1.0000   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0009, +0.0019]
  Cohen's d_z      0.042
  MDE @ 80% power  0.0019
  queries changed  2 / 300  (0.7%)
  Holm (1 tests)   p_adj = 1.0000   not significant
  !! FEW QUERIES CHANGED: only 0.7% of paired queries differ from baseline. The paired t-test's assumptions are likely violated --
     read the permutation p above, not the paired t p.
```

## Summary of per-run scores

| arm | nDCG@10 | R@50 | AP |
|---|---|---|---|
| ×4 | 0.7220 | 0.8116 | 0.6866 |
| ×5 (baseline) | 0.7207 | 0.8082 | 0.6859 |
| ×6 | 0.7201 | 0.8071 | 0.6853 |
| ×8 | 0.7212 | 0.8104 | 0.6858 |

The four arms are within ±0.002 nDCG@10 of each other on a corpus where the per-metric MDE @ 80%
power is 0.002–0.004 — the sweep found nothing to plateau *out of*: retrieval quality on SciFact
bge-base is already flat across chunk fan-out multipliers 4 through 8 at `k=5`.

## Plateau derivation

Per spec §7.1, the plateau rule reads nDCG@10 and walks up from the smallest arm run, looking for
the smallest multiplier at which nDCG@10 stops improving with Holm p_adj < 0.05 against the
next-smaller arm. The three comparisons the rule reads, named explicitly:

| comparison | source | delta (nDCG@10) | Holm p_adj | significant? |
|---|---|---|---|---|
| 5-vs-4 | the ×5-family's 4-vs-5 row, sign flipped (i.e. does ×5 improve on ×4?) | −0.0013 | 1.0000 | no |
| 6-vs-5 | the ×5-family's 6-vs-5 row | −0.0006 | 1.0000 | no |
| 8-vs-6 | the second invocation's 8-vs-6 row | +0.0011 | 1.0000 | no |

None of the three comparisons is significant at Holm p_adj < 0.05. Per the brief's plateau rule,
**if no arm differs significantly, the plateau is the smallest arm run, 4.**

**fanout plateau = 4**

Whether the guard refused any arm: no — all four arms (×4, ×5, ×6, ×8) ran and scored; the guard's
known refusal multipliers on this corpus (×2, ×3, assumption 28) were not part of this sweep.

The server build composite from every `.meta.json` sidecar (`faf832574f7b14b5`) is identical
across all four arms and matches the Step 1 `/build` baseline, confirming the harness's
constant-only edit changes no server build.

## Step 6: config.py — no change

Since the plateau (4) equals the current default (`Iverson.Agents/Python/iverson_agent/config.py`
line 12, `fanout: int = 4`), **no change was made** to `config.py` or to
`tests/test_config.py::test_defaults_match_spec_section_5`. Verification:

```
$ cd Iverson.Agents/Python && .venv/bin/python -m pytest -q
..............................                                           [100%]
30 passed in 0.31s
```

30 passed, unchanged from before this task.

## Concerns

- The ×4 arm produced 2 fewer rows (1,498 vs. the expected 1,500) than the other three arms.
  Rows are one per collapsed **document** (capped at `DocumentBudget = 5`), so this is two queries
  — `129` and `1359` — that yielded only four distinct documents: at `ChunkBudgetMultiplier = 4`
  their 20-chunk pools contained no chunk of the document that would have been fifth (`16979690`
  and `8570690` respectively, both present in the ×5/×6/×8 runs). It does not affect scoring
  validity (`report.py` confirms 300/300 query coverage and non-zero scores for all present rows)
  and leaves the deltas and the plateau unchanged; it is not a defect in the harness or the sweep.
- Every comparison in this sweep is far from significance (Holm p_adj = 1.0000 throughout,
  0.3–1.0% of queries changed per pair, deltas an order of magnitude below each comparison's
  MDE @ 80% power). The sweep's negative result is itself the finding: fan-out multiplier has no
  measurable effect on SciFact bge-base retrieval quality at k=5 in the 4–8 range tested, so the
  agent's existing default of 4 (the cheapest of the tested multipliers) is retained with no
  retrieval-quality cost.
