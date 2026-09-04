# Reranker Phase 1 — Gate Verdict

Recorded 2026-09-04, from `.worktrees/reranker-phase1` HEAD `cf9cbb8` (branch `reranker-phase1`).
Corresponds to Task 7 of `2026-09-03-reranker-phase1-implementation-plan.md`. Full execution log, per-arm logs and the SDD ledger are preserved beside the run files:
`~/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26/rerank-2026-09-task7-report.md`,
`rerank-2026-09-task7-logs/`, `rerank-2026-09-sdd-ledger.md`.

## Method

Both corpora ran as **sequential families with a Qdrant snapshot swap between them, not
interleaved**. `benchmark_documents_tenant_bypass` / `benchmark_documents_chunks_tenant_bypass` are
the only two collections either corpus uses, so NFCorpus could not run concurrently with SciFact —
running both required a full delete-then-restore of both collections between families (§7.6.4's
per-corpus interleaving guidance does not apply across corpora; the verdict says so explicitly).
This is justified as methodologically sound (not merely operationally necessary) because retrieval
is bit-deterministic in this stack:

- `reference` vs `reference-repeat` (NFCorpus, `nfcorpus-run-2026-08-27/runs/`) — two independent
  invocations of the same retrieval config — differ on **0 of 323** query sequences (`diff <(cut
  -d' ' -f1-5 reference.similar.trec) <(cut -d' ' -f1-5 reference-repeat.similar.trec)` is empty).
  Independently re-verified in this session; also recorded as design assumption A30 in
  `docs/specs/2026-09-03-reranker-design.md`.
- The two A0 controls in Step 2 of this task — one run from the main checkout at `ad6ba1c` (whose
  `Iverson.LoadTest` is byte-identical to the plan's base commit `39610d4` — Ruling R5), one from this branch's HEAD (`df4f635` at the time) — were byte-identical on
  columns 1-5 of both `.chunks.trec` and `.similar.trec` (see Step 3 of the task-7 report).

Given bit-determinism, running families sequentially with a data swap between them introduces no
measurement noise that concurrent execution would have avoided — the only thing sequential
execution costs is wall-clock time, not validity.

## SciFact (the gate)

Retrieval config: `chunked-512-apifixed` (512-char chunks, `chunk-size-512-experiment` branch,
document + query prefixes, titles composed). 300 queries, 50 candidates/query, 15,000 rows/arm.
`report.py` build identity `31583db5aea49136` across every arm (same server build for the whole
family). Full `report.py` output: `~/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26/runs/report-rerank-2026-09.txt`.

### Arms

| Arm | Config | λ | Reranker | Queries | Duration | Exit |
|---|---|---|---|---|---|---|
| A0 | rerank-a0 | 0.70 | none | 300/300 | ~11 min | clean |
| A0-preplan | rerank-a0-preplan | 0.70 | none | 300/300 | ~10 min | clean (base-commit control) |
| A1 | rerank-a1 | 0.70 | ms-marco-MiniLM-L-6-v2 | 300/300 | ~73 min | clean |
| A2 | rerank-a2 | 0.70 | bge-reranker-base | 300/300 | ~1h55m (attempt 3) | clean |
| A0′ | rerank-a0prime | 1.00 | none | 300/300 | ~6m29s | clean |
| A3 | rerank-a3 | 1.00 | ms-marco-MiniLM-L-6-v2 | 300/300 | ~26m23s | clean |

**A2 attempt history:** attempt 1 crashed at 125/300 (~2h elapsed) on an `AuthentikFlowExecutorClient`
CSRF bug — the harness's flow-executor POSTs omitted `X-authentik-CSRF` on a forced re-authentication
mid-run (Authentik's 2h `access_token_validity` boundary), causing `DriveAuthenticationFlowAsync` to
loop to `MaxFlowStages` (20) and throw `InvalidOperationException`. Root-caused, fixed, and proven by
live reproduction (unfixed code crashed under a forced re-mint; fixed code completed 300/300 with
clean re-logins), committed as `cf9cbb8`. Attempt 2 (on the fixed harness) reached 100/300 cleanly
before the host machine rebooted for unrelated reasons, interrupting it. Attempt 3, run entirely in
this session on `cf9cbb8`, completed 300/300 cleanly in ~1h55m23s with no auth errors (its total
duration never even crossed the 2h token boundary that triggered attempt 1's bug).

### λ evidence

- **Positive** (env var reflects the intended value): `VectorRanking__Lambda=1.00` confirmed via
  `docker inspect iverson-api` immediately after Step 6's restart; `VectorRanking__Lambda=0.70`
  confirmed again after Step 7's restore.
- **Negative** (the env var actually changed retrieval, not just its own reflection): scoring
  `rerank-a0prime.chunks.trec` against `rerank-a0.chunks.trec` (λ=1.00 vs λ=0.70, otherwise identical
  config) **must** fail the pool check, and did:
  ```
  [pool] rerank-a0prime.chunks.trec vs rerank-a0.chunks.trec: 300 queries, set changed on 293, sequence differs on 300 (100.0%)
  [pool] ARM INVALID: pool changed -- the document set differs from rerank-a0.chunks.trec on 293 of 300 queries (first: 1). Reranking cannot change which documents are in the pool (spec §7.1.2); this arm was produced by a different pool and must not be scored.
  EXIT CODE: 1
  ```
  This confirms λ genuinely reached MMR's diversification objective, validating that A0′/A3 are
  correctly labeled (not accidentally still at λ=0.70).

### Retrieval identity (`.similar` diffs — reranking must never touch retrieval)

```
diff <(cut -d' ' -f1-5 rerank-a0.similar.trec) <(cut -d' ' -f1-5 rerank-a1.similar.trec)        -> empty
diff <(cut -d' ' -f1-5 rerank-a0.similar.trec) <(cut -d' ' -f1-5 rerank-a2.similar.trec)        -> empty
diff <(cut -d' ' -f1-5 rerank-a0prime.similar.trec) <(cut -d' ' -f1-5 rerank-a3.similar.trec)  -> empty
```
All three pairs identical on the retrieval-identifying columns. PASS.

### Score-collapse check (raw logits, R8)

- A1 (ms-marco): 0 duplicate `(qid, score)` pairs. rows=15,000, non-zero=15,000.
- A2 (bge-reranker-base): **2 duplicate pairs** —
  `1303 Q0 19752008 11 -5.750728` / `1303 Q0 43566999 12 -5.750728` (ranks 11,12) and
  `502 Q0 24575065 45 -10.196537` / `502 Q0 37248570 46 -10.196537` (ranks 45,46). Both entirely
  below rank 10. **Ruling R16 (controller, mid-task):** A2 is ACCEPTED — the score-collapse check
  exists to catch systematic sigmoid/F6 saturation (many rows at 0.000000 or long identical-score
  runs), and two isolated within-query ties between adjacent documents at ranks 11/12 and 45/46 of
  a 15,000-row file are float coincidence that cannot change nDCG@10 (both ties are below rank 10;
  `trec_eval` breaks ties by doc id) or R@50. R16 additionally set a refined stop rule for the
  remaining arms in this family: stop only if within-query duplicates exceed 0.5% of rows, any run
  of 3+ identical scores exists, or any tie straddles rank 10 (ranks 10 and 11) — 2/15,000 = 0.0133%
  is far under threshold, no run reaches 3, and neither tie touches ranks 10/11.
- A3 (ms-marco, λ=1.00): 0 duplicate pairs. rows=15,000, non-zero=15,000.

### auto_truncate

Every reranked-arm startup banner (A1, A2, A3) printed `auto_truncate=True` — confirmed against the
compose command's `--auto-truncate` flag, no wrong-container risk triggered.

### Reranker per-batch timeout (R11: 120s)

No timeout message, 120s reference, or related text appears in any of the four run logs
(`step4-a1.log`, `step5-a2-attempt3.log`, `step6-a0prime.log`, `step7-a3.log`) — the 120s per-batch
budget was never approached in any arm.

### Results

| Arm pair | Metric | Delta | Paired t p | Permutation p | 95% CI | d_z | MDE@80% | Queries changed | Holm p_adj (m=3) |
|---|---|---|---|---|---|---|---|---|---|
| A1 vs A0 | nDCG@10 | **+0.0082** | 0.5993 | 0.6161 | [-0.0226, +0.0391] | 0.030 | 0.0439 | 121/300 (40.3%) | **1.0000 — not significant** |
| A2 vs A0 | nDCG@10 | -0.1126 | 0.0000 | 0.0002 | [-0.1502, -0.0750] | -0.340 | 0.0535 | 148/300 (49.3%) | 0.0006 — significant (negative) |
| A3 vs A0′ | nDCG@10 | +0.0057 | 0.7119 | 0.7271 | [-0.0246, +0.0359] | 0.021 | 0.0431 | 118/300 (39.3%) | 1.0000 — not significant |

R@50 by arm (must match its own-λ control to 4 decimals — confirmed):

| Arm | R@50 |
|---|---|
| A0 | 0.9227 |
| A1 | 0.9227 |
| A2 | 0.9227 |
| A0′ | 0.9193 |
| A3 | 0.9193 |

nDCG@10 by arm: A0 0.6960, A1 0.7043, A2 0.5834, A0′ 0.6980, A3 0.7036. None approaches the oracle
ceiling (§7.1.3 / A36, 0.9216) — max observed is 0.7043. **PASS — no arm exceeds the ceiling.**

### GATE VERDICT: **GATE FAILED**

Per §3.4, the gate requires A1 vs A0 Holm p_adj < 0.05 **with a positive delta**. The delta IS
positive (+0.0082, ms-marco improves SciFact nDCG@10 on point estimate), but Holm p_adj = 1.0000 —
nowhere near significant at m=3. The 95% CI [-0.0226, +0.0391] straddles zero. **No Phase 2 plan is
written** as a result (per the plan's own instruction on a failed gate).

Secondary observation: A2 (bge-reranker-base) is significantly **worse** than its control
(delta -0.1126, Holm p_adj=0.0006) — bge-reranker-base measurably harms SciFact ranking quality in
this configuration, a negative result worth carrying into any future reranker-selection decision
even though it doesn't bear on the A1-vs-A0 gate itself.

## NFCorpus (the §7.1.6 counter-case, secondary — not part of the gate)

Retrieval config: same reference configuration as SciFact restore-wise (512-char chunks, document +
query prefixes, titles composed), 323 queries, 50 candidates/query, 16,150 rows/arm. Per Ruling R13,
only A0 and A1 ran (A2 not run — time budget prioritized completing the full SciFact family plus
this counter-case within the session). Family size m=1. Full output:
`~/repositories/iverson-benchmark-corpora/nfcorpus-run-2026-08-27/runs/report-rerank-2026-09.txt`.

| Arm | nDCG@10 | R@50 | AP |
|---|---|---|---|
| A0 | 0.3517 | 0.2479 | 0.1544 |
| A1 | 0.3463 | 0.2479 | 0.1555 |

Pool check: `set changed on 0` (PASS). `.similar` diff a1 vs a0: empty (PASS).

Score-collapse check on A1: 12 duplicate `(qid,score)` pairs (24/16,150 rows = 0.149%, under the
0.5% R16 threshold; max group size 2, no run of 3+; no pair straddles ranks 10/11 — the closest is
ranks 9,10 for query PLAIN-2770, entirely within the nDCG@10 cutoff). Accepted per R16's refined
rule, same reasoning as SciFact's A2.

**A1 vs A0 (nDCG@10):** delta **-0.0054**, paired t p=0.4154, permutation p=0.4080, 95% CI
[-0.0185, +0.0077], d_z=-0.045, MDE@80%=0.0186, 199/323 (61.6%) queries changed, **Holm (m=1)
p_adj = 0.4080 — not significant.**

R@50: delta +0.0000, 0/323 queries changed, permutation p=1.0000 — fully invariant, as expected
(reranking cannot change the candidate pool).

**P8 prediction check:** P8 predicted that reranking underperforms on NFCorpus, a recall-bound
corpus (R@50 = 0.2479 vs SciFact's 0.9227 — the retrieval pool itself is far weaker, so reordering
within it has less room to help). The observed nDCG@10 delta is **negative** (-0.0054), matching
P8's predicted direction, though the effect is not statistically significant at this sample size
(the study's MDE@80% for this family, 0.0186, exceeds the observed |delta|). **P8's directional
prediction held; its magnitude cannot be confirmed as a real effect at this power.**

## Method note

The two corpora ran as **sequential families with a snapshot swap between them, not interleaved**
(justification above). All box-idle (§7.6.1) and timing (§7.6.7) requirements were observed
throughout — nothing but the query-tier compose stack and (during reranked arms) the reranker
container ran concurrently with any benchmark-query process.

## Document-input trial (A4, 2026-09-04)

Follow-up trial from `docs/specs/2026-09-04-reranker-document-input-trial-design.md`: identical to A1
except the cross-encoder scores each candidate's **full corpus document text** instead of its winning
chunk (`benchmark-query --rerank-input document`). Same corpus, same retrieval config, same reranker
(`cross-encoder/ms-marco-MiniLM-L-6-v2`, `max_input_length=512`, `auto_truncate=true`), same server
build identity `31583db5aea49136`, 300 queries, 50 candidates/query, 15,000 rows. Full `report.py`
output: `~/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26/runs/report-rerank-a4-2026-09.txt`.

**A1 reuse.** A1 was reused rather than re-run. A same-session A0 control (`rerank-a0-2026-09-04`) was
run first and its columns 1–5 are byte-identical to the preserved Phase 1 `rerank-a0` for both
`.chunks` and `.similar` (`CHUNKS-IDENTICAL` / `SIMILAR-IDENTICAL`), so neither the index nor the
server drifted between the Phase 1 family and this trial, and the Phase 1 arms remain comparable.

| Arm | Config | λ | Reranker | Input | Queries | Duration | Exit |
|---|---|---|---|---|---|---|---|
| A4 | rerank-a4 | 0.70 | ms-marco-MiniLM-L-6-v2 | full document | 300/300 | ~1h03m | clean |

Macro scores: A0 nDCG@10 0.6960 / R@50 0.9227 / AP 0.6561; A1 0.7043 / 0.9227 / 0.6644;
**A4 0.6993 / 0.9227 / 0.6584.**

Pool checks (both invocations `exit=0`, neither `ARM INVALID`):

```
[pool] rerank-a4.chunks.trec  vs  rerank-a0.chunks.trec: 300 queries, set changed on 0, sequence differs on 300 (100.0%)
[pool] rerank-a4.chunks.trec  vs  rerank-a1.chunks.trec: 300 queries, set changed on 0, sequence differs on 300 (100.0%)
```

`set changed on 0` on both (reranking never touched the candidate pool) and 100.0 % of sequences
reordered on both, well above the 25 % floor.

### The gate comparison: A4 vs A0 (nDCG@10)

```
[compare] rerank-a4.chunks.trec  vs  rerank-a0.chunks.trec        (nDCG@10)
  delta            +0.0032
  paired t         t = 0.23   p = 0.8214
  permutation      p = 0.8193   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0251, +0.0316]
  Cohen's d_z      0.013
  MDE @ 80% power  0.0403
  queries changed  118 / 300  (39.3%)
  Holm (1 tests)   p_adj = 0.8193   not significant
```

| Pair | Metric | Delta | Paired t p | Permutation p (= Holm p_adj, m=1) | 95% CI | d_z | MDE@80% | Queries changed |
|---|---|---|---|---|---|---|---|---|
| A4 vs A0 | nDCG@10 | +0.0032 | 0.8214 | 0.8193 | [-0.0251, +0.0316] | 0.013 | 0.0403 | 118/300 (39.3%) |

R@50 for A4 is **0.9227**, equal to A0's 0.9227 to 4 decimals (delta +0.0000, 0/300 queries changed,
permutation p = 1.0000) — retrieval invariant, as required.

### The format comparison: A4 vs A1 (nDCG@10)

```
[compare] rerank-a4.chunks.trec  vs  rerank-a1.chunks.trec        (nDCG@10)
  delta            -0.0050
  paired t         t = -0.41   p = 0.6821
  permutation      p = 0.6947   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0289, +0.0189]
  Cohen's d_z      -0.024
  MDE @ 80% power  0.0341
  queries changed  93 / 300  (31.0%)
  Holm (1 tests)   p_adj = 0.6947   not significant
```

| Pair | Metric | Delta | Paired t p | Permutation p (= Holm p_adj, m=1) | 95% CI | d_z | MDE@80% | Queries changed |
|---|---|---|---|---|---|---|---|---|
| A4 vs A1 | nDCG@10 | -0.0050 | 0.6821 | 0.6947 | [-0.0289, +0.0189] | -0.024 | 0.0341 | 93/300 (31.0%) |

This invocation did **not** exit `ARM INVALID`: `set changed on 0` held and 100.0 % of sequences
differ. A4's AP is also lower than A1's (-0.0060, permutation p = 0.6725, n.s.), and R@50 is
identical (+0.0000, p = 1.0000). Feeding the full document is, on point estimate, slightly *worse*
than feeding the winning chunk, and the difference is not distinguishable from noise.

### Checks

- **Oracle ceiling (§7.1.3 / A36, 0.9216):** A4's nDCG@10 is 0.6993 — does **not** exceed the
  ceiling. **PASS.**
- **Score collapse (R16):** zero duplicate `(qid, score)` groups in `rerank-a4.chunks.trec`
  (0 rows, 0.00 % of 15,000; no group of size ≥ 3; no group straddling rank 10). **PASS.**
- **Run integrity:** 300/300 queries, exit 0, no `ARM INVALID`, no per-batch timeout, and no
  `Authentication flow did not complete` error — the `cf9cbb8` CSRF re-mint fix held (though at
  ~1h03m the run did not cross the 2 h token boundary).
- **Provenance:** banner `input=document` printed once; sidecar `rerank-a4.meta.json` carries
  `"input": "document"`. A4 wall time **~1 h 03 min** (12:32:05 → 13:35:28 local).

### TRIAL VERDICT: **TRIAL FAILED**

Per spec §5 step 5 the trial passes only on a positive A4-vs-A0 nDCG@10 delta with p < 0.05 on both
the paired *t* and the permutation test. The delta is positive (+0.0032) but both p-values are far
from significant (t p = 0.8214, permutation p = 0.8193 = Holm p_adj at m = 1), the 95 % CI
[-0.0251, +0.0316] straddles zero, and the observed |delta| is an order of magnitude below the
family's MDE@80% of 0.0403. **Document input does not rescue the failed Phase 1 gate.** It is also
not better than chunk input: A4 trails A1 by -0.0050 nDCG@10 (n.s.). The GATE FAILED verdict above
stands unchanged, and no Phase 2 plan follows from this trial either.
