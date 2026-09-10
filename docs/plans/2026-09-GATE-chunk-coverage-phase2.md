# Chunk-Coverage Signal, Phase 2 — Gate Verdict

Measurements recorded 2026-09-09/10 from the `chunk-coverage-phase2` worktree at HEAD `11f9c3a`
(`guard --max-beta against non-finite and negative values`), which carries Task 2's gate scripts from
`docs/plans/2026-09-09-chunk-coverage-phase2-implementation-plan.md` — Tasks 1 and 3 are operational
and commit no repo file, writing their artefacts outside the tree. The design is
`docs/specs/2026-09-09-chunk-coverage-phase2-design.md`; its parent is
`docs/specs/2026-09-08-chunk-coverage-signal-design.md`. Phase 1's result, whose convention this
document follows and whose `s` = 0.695455 set this phase's ladder, is
`docs/plans/2026-09-GATE-chunk-coverage.md`.

Phase 2 asks the one question Phase 1's measurement was for: **does any β on the ladder beat β = 0 on
nDCG@10 for FreshStack-2048 at λ = 1.00?**

---

## Verdict: NO β QUALIFIES — and the result is a significant NEGATIVE, not a null

Spec §5: *"A β **qualifies** if its nDCG@10 delta against β = 0 is **positive** and Holm
p_adj < 0.05. **A significant negative delta is a qualifying result for the opposite conclusion** and
is recorded as such, not discarded."*

That is the branch this sweep landed on. **All five non-zero arms are negative on nDCG@10, the deltas
are monotone in β, and the top three are Holm-significant at p_adj = 0.0010.**

| Arm | β | nDCG@10 | Δ vs β = 0 | Holm p_adj (5 tests) | |
|---|---|---|---|---|---|
| `fs2048-b0` | 0 | 0.2932 | — | — | baseline |
| `fs2048-b3387` | 0.003387 | 0.2909 | **−0.0022** | 0.2172 | not significant |
| `fs2048-b6107` | 0.006107 | 0.2880 | **−0.0051** | 0.0604 | not significant |
| `fs2048-b11012` | 0.011012 | 0.2807 | **−0.0125** | **0.0010** | **significant** |
| `fs2048-b19855` | 0.019855 | 0.2706 | **−0.0226** | **0.0010** | **significant** |
| `fs2048-b35800` | 0.035800 | 0.2579 | **−0.0353** | **0.0010** | **significant** |

**The coverage signal makes ranking worse.** Not "fails to help" — worse, monotonically, with the
damage growing in β right up to parity, and significant well before parity is reached. The two
non-significant arms are not a counterweight: `fs2048-b3387`'s |Δ| of 0.0022 is below its own MDE of
0.0050 and `fs2048-b6107`'s 0.0051 below its 0.0065, so they are the underpowered low end of one
monotone trend, not evidence of a flat region. Every one of them points the same way.

**Max-passage stands, and it stands on a positive finding against the alternative rather than on the
absence of one.** β is not a tunable this corpus leaves open; the best value of β on the measured
ladder is 0.

### The cost spec §5 said to watch for is the cost that materialised

Spec §5: *"α-nDCG is watched for a *negative*: coverage may concentrate on comprehensive documents at
the cost of subtopic spread, and that cost belongs in the record even though it cannot veto."*

α-nDCG@10 moves negative at every arm — **−0.0020, −0.0039, −0.0100, −0.0175, −0.0312** — significant
from `fs2048-b11012` up (p_adj 0.0258, 0.0016, 0.0010). This is the *specific* mechanism §5 named,
observed rather than hypothesised: rewarding a document for matching in several places promotes
documents that are comprehensive about one thing over a set that spans the query's subtopics. It does
not gate, and it does not need to — nDCG@10 already decided. It is recorded because it says *how* the
signal hurts, not merely that it does.

R@50 and AP are also negative at every arm and significant at **all five** (R@50 p_adj 0.0048 →
0.0010; AP p_adj 0.0464 → 0.0010), i.e. on those two measures even the tie-break arm loses. Neither
gates. All four measures agree in sign at all five arms.

---

## Arms

The ladder is spec §2's construction over Phase 1's measured `s` = 0.695455 — tie-break, three
log-spaced intermediates at ×1.8031, parity, plus β = 0 as the baseline endpoint. **No arm exceeds
parity 0.035800**, per the Phase 1 gate's hard bound.

| Arm | β | label | sidecar `beta` |
|---|---|---|---|
| baseline | 0 | `fs2048-b0` | `0` |
| tie-break | 0.003387 | `fs2048-b3387` | `0.003387` |
| ×1.8031 | 0.006107 | `fs2048-b6107` | `0.006107` |
| ×1.8031 | 0.011012 | `fs2048-b11012` | `0.011012` |
| ×1.8031 | 0.019855 | `fs2048-b19855` | `0.019855` |
| parity | 0.035800 | `fs2048-b35800` | `0.0358` |

All six are offline `benchmark-aggregate` replays of Phase 1's accepted hit dump
(`chunk-coverage-phase1-2026-09-09/runs/fs2048-pool.chunks.hits.tsv`, 369,600 rows). No query run, no
Qdrant restore, no exposure to the SIGPIPE crash. That is what Phase 1's byte-identity check bought
and this is the phase that spent it.

| | |
|---|---|
| Corpus | FreshStack 6k slice, `freshstack-2048-2026-09-07` |
| Window | 2048/1792 |
| `--chunk-budget-multiplier` | **11** → 50 × 11 = **550-chunk pool per query** |
| `VectorRanking__LambdaChunks` on the source dump | **1.00** — MMR off |
| Fusion triple | shipped `WBase`/`WCentroid`/`WDecay` = 0.45/0.45/0.10, untouched |
| Server build composite | **`3ffafcd26416ed30`** (all six sidecars) |
| Aggregator composite | `c5f976dc8d6b9497` (all six sidecars) |
| Queries | 672/672 in every arm |

Artefacts live untracked in `~/repositories/iverson-benchmark-corpora/`:
`chunk-coverage-phase2-arms-2026-09-09/` (18 arm files + `invariant.txt` + `report.txt`) and
`chunk-coverage-phase2-l070-2026-09-09/` (the λ = 0.70 capture + `capture-certification.txt`).

Six `WARNING:` banners were printed, one per replay, all of them the expected "`fs2048-pool.meta.json`
has no `queryCount`" banner — the Phase 1 sidecar predates that field. Spec §3 records the answer:
that run *was* verified complete by hand, 672/672 queries, zero failed RPCs, no unhandled exception.
No other warning, and no `REFUSING` / `Exception` / `BUILD MISMATCH` / `BUILD UNKNOWN` text, appeared
anywhere.

---

## Every check's result

Spec §6 requires this section to exist: *"A check whose result reaches no durable artefact reproduces
§4's own failure mode one level up: it looks exactly like a check that passed."*

### 1. Spec §4's precondition invariant — PASS (all three counts non-zero)

`beta_invariant.py` (Task 2, commit `11f9c3a`), exit 0. Verbatim from
`chunk-coverage-phase2-arms-2026-09-09/invariant.txt`:

```
[beta_invariant] check 1 PASSED: 79946 single-chunk pairs identical at beta=0 and beta=parity
[beta_invariant] check 2 PASSED: 92758 multi-chunk pairs differ between beta=0 and beta=parity
[beta_invariant] check 3 PASSED: 6 sidecar beta values match --ladder, none exceeding 0.0358
```

| Check | Count | Rule |
|---|---|---|
| 1 — single-chunk pairs identical at β = 0 and β = parity | **79,946** | zero would be a failure, not a pass |
| 2 — multi-chunk pairs that differ (positive control) | **92,758** | zero would be a failure, not a pass |
| 3 — sidecar β values checked against the ladder, none over `--max-beta` 0.035800 | **6** | |

**The partition closes: 79,946 + 92,758 = 172,704**, the dump's full (query, parent) pair census,
independently recomputed here from the hit dump and `keymap.json`. **What this proves, precisely:** the
census is complete, no pair fell out of either side of the check, and β *applied* to every pair that
has a tail. **What it does not prove:** anything about β's *magnitude*. The partition closes
arithmetically for any non-zero β, since `Skip(1).Take(3).Sum()` is 0 on a single-chunk pair and
non-zero on a multi-chunk one whatever β is. Magnitude rests on check 3 alone.

**And check 3 is not a swap guard.** It compares a `collections.Counter` *multiset* of the six
sidecars' β values against `--ladder`. A label↔β swap — `fs2048-b3387` carrying 0.0358 and
`fs2048-b35800` carrying 0.003387 — presents the same multiset and passes it unchanged. Check 3
catches a mistyped or out-of-bound β; it cannot see a permutation. That gap is closed separately, by
recomputation, in §3 below.

### 2. Spec §1.1's `/build` certification for the λ = 0.70 capture — PASS

Verbatim from `chunk-coverage-phase2-l070-2026-09-09/capture-certification.txt`:

```
composite at capture: 3ffafcd26416ed30
lambda at capture:    VectorRanking__LambdaChunks=0.70
failed RPCs / exceptions (must be 0): 0
distinct queries (must be 672): 672
sidecar queryCount:   672
SIGPIPE crash during run: none observed (0 failed RPCs)
```

`iverson-api` was recreated with `VECTOR_RANKING_LAMBDA_CHUNKS=0.70` via
`docker compose up -d --no-deps iverson-api` — **no `--build`**, no tier-wide `docker compose up`, and
no "Building" line in the output. `/build` was then re-read *before anything ran* and returned
`3ffafcd26416ed30`, the composite the Phase 1 dump was taken on. That is the check spec §1.1 step 3
calls "the whole point of the sequence", and it passed. The run recorded at 2026-09-10T02:11:09Z and
completed ~02:57Z — a 46-minute pass, zero failed RPCs, exit 0.

### 3. The λ = 0.70 capture, evidenced from inside the artefacts

**This is a gap in the artefact set, not in the run, and it is recorded because it is the failure mode
spec §4 names.** `benchmark-query`'s sidecar has **no `lambda` field** — the l070 sidecar's keys are
`assemblies`, `chunkBudgetMultiplier`, `composite`, `configLabel`, `queryCount`, `recordedAtUtc`,
`reranker`, and nothing else; `benchmark-aggregate`'s arm sidecars likewise carry no λ. So λ — the
parameter this phase spent a 46-minute perishable-build run to preserve — is attested durably only by
a line of operator-written text in `capture-certification.txt` and by a running container that will
not survive. That reproduces spec §4's own warning at the level above it: *a check that reaches no
durable artefact looks like a check that passed.* Phase 1 hit the same gap and answered it the same
way (its gate's "λ = 1.00 is corroborated in-artifact, not only by the env check").

Four independent in-artefact checks were therefore run for this document, against
`fs2048-pool-l070.*` and Phase 1's `fs2048-pool.*`. All four agree that the l070 dump is a λ = 0.70
arm of the same build and index.

**(a) MMR reordering — λ < 1's fingerprint.** At λ = 1.00 the returned stream is score-descending; at
λ < 1 MMR reorders it. Counting per-query adjacent pairs where the next score is *higher* than the
previous:

| Dump | Queries | Adjacent pairs | Descending-order violations | Queries with ≥ 1 violation |
|---|---|---|---|---|
| Phase 1 `fs2048-pool` | 672 | 368,928 | **0** | **0** |
| λ = 0.70 `fs2048-pool-l070` | 672 | 368,928 | **183,792** (49.82 %) | **672 / 672** |

Zero against 183,792 on identically-shaped dumps. The Phase 1 figure reproduces spec §8 assumption 22
exactly (`0 of 369,600 rows`, i.e. 0 of the 368,928 adjacent pairs). Diversification is off in one
dump and on in the other; nothing else in the pipeline reorders a stream.

**(b) Distinct parents, against parent spec §4's recorded table.** Read from each run's own
`.chunks.diversity.json`:

| | mean distinct parents @10 | @50 |
|---|---|---|
| Parent §4 recorded, `fs-2048` **λ 0.70** | 8.10 | 34.50 |
| **Measured, `fs2048-pool-l070`** | **8.104166666666666** | **34.49702380952381** |
| Parent §4 recorded, `fs-2048` **λ 1.00** | 6.67 | 28.82 |
| **Measured, Phase 1 `fs2048-pool`** | **6.665178571428571** | **28.81547619047619** |

Each lands on its own row to the recorded precision and is nowhere near the other's. λ diversifies
among chunks — more distinct parents per slot — and the l070 dump shows exactly the recorded lift.

**(c) Rank-1 identity — same index, same vectors, λ the only difference.** MMR's first selection is
always the argmax, so rank 1 is invariant to λ while everything below it is not:

- rank-1 score **bit-identical on 672 / 672 queries**; rank-1 parent key identical on 672 / 672.
- and the dumps are otherwise emphatically *not* the same file: only **1,959 of 369,600** rows
  (0.53 %) carry the same (parent, score) at the same rank, of which 672 are the pinned rank-1 rows —
  **1,287 of 368,928** coincidences (0.35 %) below rank 1.

A copied file would score 100 %; a different index or different query vectors would break rank 1. Only
a reordering of one identically-scored candidate set produces this pattern.

**(d) Assembly MVIDs — same binary, independent of the certification text.** All seven module version
IDs in the l070 sidecar equal Phase 1's, as does the `composite`:

```
Iverson.Api               f6395fff-2305-45d3-bfb0-0b83f986dadc
Iverson.Client.Contracts  809c1418-8dd9-40a2-b114-f0c8e460ee7e
Iverson.Embeddings        3aeecca3-0e78-4b61-9ebd-37aadc3a010b
Iverson.Events            a3639474-e6b5-4e74-a4bd-02c5cbff68f2
Iverson.Sql               ff808231-8155-472d-a586-664885df96f0
Iverson.StarRocks         7b0958b5-fbb8-41b9-a598-bc37585c669c
Iverson.Vector            aa74a8f0-fb6b-4611-8012-3cdfd640c617
composite                 3ffafcd26416ed30
```

MVIDs are per-compilation. Seven-for-seven equality means the λ = 0.70 recreate did not rebuild, which
is the fact `/build` asserted and this confirms from the artefact rather than from the operator's
transcript.

**What (a)–(d) jointly establish:** the l070 dump came off the *same binary* and the *same index* as
Phase 1's, differing by a parameter that reorders the returned stream and raises distinct-parent
counts to parent §4's recorded λ = 0.70 row. That is λ = 0.70. It is not a proof that the env var read
`0.70` rather than, say, `0.71` — no artefact carries the value — but it pins the arm to its recorded
row and excludes every failure the certification text alone could hide: a silent rebuild, a swapped
index, a restart that dropped the setting, or a re-run of the λ = 1.00 arm under a new label.

**Recommended follow-up (not done here, not blocking):** add `lambdaChunks`/`lambdaSimilar` to
`benchmark-query`'s sidecar so the next arm attests its own λ instead of needing this section.

### 4. Label↔β binding, by recomputation — PASS (this is what excludes a swap)

Check 3 above cannot see a permutation. So each arm's `--scores-path` output was recomputed
independently, from Phase 1's hit dump and `keymap.json`, under `DocumentRanking.cs`'s rule
(`descending[0] + β · descending.Skip(1).Take(3).Sum()`) at the arm's **labelled** β, and β was
additionally *solved* from each arm's own scores by least squares over the 92,758 multi-chunk pairs:

| label | sidecar `beta` | scores reproduced at labelled β | max abs err | β solved from the data alone |
|---|---|---|---|---|
| `fs2048-b0` | 0 | 172,704 / 172,704 | 0.000e+00 | 0.000000000 |
| `fs2048-b3387` | 0.003387 | 172,704 / 172,704 | 0.000e+00 | 0.003387000 |
| `fs2048-b6107` | 0.006107 | 172,704 / 172,704 | 0.000e+00 | 0.006107000 |
| `fs2048-b11012` | 0.011012 | 172,704 / 172,704 | 0.000e+00 | 0.011012000 |
| `fs2048-b19855` | 0.019855 | 172,704 / 172,704 | 0.000e+00 | 0.019855000 |
| `fs2048-b35800` | 0.0358 | 172,704 / 172,704 | 0.000e+00 | 0.035800000 |

Bit-exact on every one of the 1,036,224 scores, and the β recovered from the numbers matches the label
to nine decimals at every arm. A label↔β swap is excluded, as is any mistyped β, at every arm rather
than only at the two the invariant script compares.

### 5. Aggregator drift — spec §8 assumption 21's free confirmation — PASS

Assumption 21 notes that `composite` tracks the *server*, so it cannot certify that Phase 2's
*harness* replays the dump the way Phase 1's certified one did, and offers a check that costs nothing:
compare columns 1–5 of Phase 2's β = 0 run against Phase 1's, ignoring column 6 (the config label,
which necessarily differs).

```
sha256 (cols 1-5)  a4d6cb15864c67477c09385b8775dc9f090d4865f6e2889b64c2054e5115cf2d
  phase2-arms/fs2048-b0.chunks.trec
  phase1/identity-beta0/fs2048-pool.chunks.trec
  phase1/runs/fs2048-pool.chunks.trec
```

Identical across all three, 33,600 rows, **0 differing lines** either way. Phase 2's β = 0 arm
reproduces both Phase 1's certified identity replay *and* the original in-run ranking exactly. The
aggregator has not drifted, and the `NumberStyles.Float` parse change assumption 21 flags is confirmed
output-inert.

Note what this does and does not establish, on Phase 1's own terms: `DocumentRanking.cs:61`
short-circuits at β = 0, so this exercises dump, key-map, reader and writer fidelity — **not** the
tail arithmetic, which never executes on this path. The β ≠ 0 arithmetic rests on Task 2's unit tests,
§4's invariant, and §4 above.

### 6. Holm family size — PASS

Spec §5's stated invariant is that the printed family equals the number of non-zero β arms compared.

- `[baseline] excluded …` appears **exactly once** — β = 0 was removed from the comparison set once,
  not zero or twice.
- Every one of the 20 `[compare]` blocks prints **`Holm (5 tests)`**; `sort -u` over the grep returns
  that single line. A family of 6 would have meant the baseline was not excluded and every correction
  in the run was wrong.
- 20 blocks = 5 arms × 4 measures. Holm corrects within each measure, never pooled across them.
- `--baseline` was used, never `--pair`: β changes which documents reach the top 50, so `check_pool`
  would exit `ARM INVALID: pool changed`.

---

## The sweep: full `report.py` output

Verbatim from `chunk-coverage-phase2-arms-2026-09-09/report.txt` (exit 0). Invocation per spec §5,
with all six runs passed to `--run` and β = 0 additionally as `--baseline`.

```

[structural] /home/ben/repositories/iverson-benchmark-corpora/chunk-coverage-phase2-arms-2026-09-09/fs2048-b0.chunks.trec
  rows                 33,600
  distinct queries     672
  non-zero scores      33,600 / 33,600
  qrels queries        672
  covered by this run  672 / 672
  duplicate doc ids    none

[structural] /home/ben/repositories/iverson-benchmark-corpora/chunk-coverage-phase2-arms-2026-09-09/fs2048-b3387.chunks.trec
  rows                 33,600
  distinct queries     672
  non-zero scores      33,600 / 33,600
  qrels queries        672
  covered by this run  672 / 672
  duplicate doc ids    none

[structural] /home/ben/repositories/iverson-benchmark-corpora/chunk-coverage-phase2-arms-2026-09-09/fs2048-b6107.chunks.trec
  rows                 33,600
  distinct queries     672
  non-zero scores      33,600 / 33,600
  qrels queries        672
  covered by this run  672 / 672
  duplicate doc ids    none

[structural] /home/ben/repositories/iverson-benchmark-corpora/chunk-coverage-phase2-arms-2026-09-09/fs2048-b11012.chunks.trec
  rows                 33,600
  distinct queries     672
  non-zero scores      33,600 / 33,600
  qrels queries        672
  covered by this run  672 / 672
  duplicate doc ids    none

[structural] /home/ben/repositories/iverson-benchmark-corpora/chunk-coverage-phase2-arms-2026-09-09/fs2048-b19855.chunks.trec
  rows                 33,600
  distinct queries     672
  non-zero scores      33,600 / 33,600
  qrels queries        672
  covered by this run  672 / 672
  duplicate doc ids    none

[structural] /home/ben/repositories/iverson-benchmark-corpora/chunk-coverage-phase2-arms-2026-09-09/fs2048-b35800.chunks.trec
  rows                 33,600
  distinct queries     672
  non-zero scores      33,600 / 33,600
  qrels queries        672
  covered by this run  672 / 672
  duplicate doc ids    none

[scores] /home/ben/repositories/iverson-benchmark-corpora/chunk-coverage-phase2-arms-2026-09-09/fs2048-b0.chunks.trec
  build      3ffafcd26416ed30
  nDCG@10    0.2932
  R@50       0.5446
  AP         0.2147
  alpha_nDCG@10 0.3551

[scores] /home/ben/repositories/iverson-benchmark-corpora/chunk-coverage-phase2-arms-2026-09-09/fs2048-b3387.chunks.trec
  build      3ffafcd26416ed30
  nDCG@10    0.2909
  R@50       0.5386
  AP         0.2119
  alpha_nDCG@10 0.3531

[scores] /home/ben/repositories/iverson-benchmark-corpora/chunk-coverage-phase2-arms-2026-09-09/fs2048-b6107.chunks.trec
  build      3ffafcd26416ed30
  nDCG@10    0.2880
  R@50       0.5373
  AP         0.2089
  alpha_nDCG@10 0.3513

[scores] /home/ben/repositories/iverson-benchmark-corpora/chunk-coverage-phase2-arms-2026-09-09/fs2048-b11012.chunks.trec
  build      3ffafcd26416ed30
  nDCG@10    0.2807
  R@50       0.5285
  AP         0.2035
  alpha_nDCG@10 0.3452

[scores] /home/ben/repositories/iverson-benchmark-corpora/chunk-coverage-phase2-arms-2026-09-09/fs2048-b19855.chunks.trec
  build      3ffafcd26416ed30
  nDCG@10    0.2706
  R@50       0.5095
  AP         0.1938
  alpha_nDCG@10 0.3376

[scores] /home/ben/repositories/iverson-benchmark-corpora/chunk-coverage-phase2-arms-2026-09-09/fs2048-b35800.chunks.trec
  build      3ffafcd26416ed30
  nDCG@10    0.2579
  R@50       0.4733
  AP         0.1804
  alpha_nDCG@10 0.3240
[baseline] excluded /home/ben/repositories/iverson-benchmark-corpora/chunk-coverage-phase2-arms-2026-09-09/fs2048-b0.chunks.trec (it is the --baseline file)

[compare] fs2048-b3387.chunks.trec  vs  fs2048-b0.chunks.trec        (nDCG@10)
  delta            -0.0022
  paired t         t = -1.25   p = 0.2129
  permutation      p = 0.2172   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0057, +0.0013]
  Cohen's d_z      -0.048
  MDE @ 80% power  0.0050
  queries changed  284 / 672  (42.3%)
  Holm (5 tests)   p_adj = 0.2172   not significant

[compare] fs2048-b6107.chunks.trec  vs  fs2048-b0.chunks.trec        (nDCG@10)
  delta            -0.0051
  paired t         t = -2.20   p = 0.0280
  permutation      p = 0.0302   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0097, -0.0006]
  Cohen's d_z      -0.085
  MDE @ 80% power  0.0065
  queries changed  341 / 672  (50.7%)
  Holm (5 tests)   p_adj = 0.0604   not significant

[compare] fs2048-b11012.chunks.trec  vs  fs2048-b0.chunks.trec        (nDCG@10)
  delta            -0.0125
  paired t         t = -3.93   p = 0.0001
  permutation      p = 0.0002   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0187, -0.0062]
  Cohen's d_z      -0.152
  MDE @ 80% power  0.0089
  queries changed  382 / 672  (56.8%)
  Holm (5 tests)   p_adj = 0.0010   significant

[compare] fs2048-b19855.chunks.trec  vs  fs2048-b0.chunks.trec        (nDCG@10)
  delta            -0.0226
  paired t         t = -5.37   p = 0.0000
  permutation      p = 0.0002   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0309, -0.0143]
  Cohen's d_z      -0.207
  MDE @ 80% power  0.0118
  queries changed  427 / 672  (63.5%)
  Holm (5 tests)   p_adj = 0.0010   significant

[compare] fs2048-b35800.chunks.trec  vs  fs2048-b0.chunks.trec        (nDCG@10)
  delta            -0.0353
  paired t         t = -6.57   p = 0.0000
  permutation      p = 0.0002   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0458, -0.0247]
  Cohen's d_z      -0.254
  MDE @ 80% power  0.0150
  queries changed  451 / 672  (67.1%)
  Holm (5 tests)   p_adj = 0.0010   significant

[compare] fs2048-b3387.chunks.trec  vs  fs2048-b0.chunks.trec        (R@50)
  delta            -0.0061
  paired t         t = -3.05   p = 0.0024
  permutation      p = 0.0024   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0099, -0.0022]
  Cohen's d_z      -0.118
  MDE @ 80% power  0.0056
  queries changed  98 / 672  (14.6%)
  Holm (5 tests)   p_adj = 0.0048   significant

[compare] fs2048-b6107.chunks.trec  vs  fs2048-b0.chunks.trec        (R@50)
  delta            -0.0074
  paired t         t = -2.50   p = 0.0128
  permutation      p = 0.0118   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0131, -0.0016]
  Cohen's d_z      -0.096
  MDE @ 80% power  0.0083
  queries changed  138 / 672  (20.5%)
  Holm (5 tests)   p_adj = 0.0118   significant

[compare] fs2048-b11012.chunks.trec  vs  fs2048-b0.chunks.trec        (R@50)
  delta            -0.0161
  paired t         t = -4.22   p = 0.0000
  permutation      p = 0.0002   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0236, -0.0086]
  Cohen's d_z      -0.163
  MDE @ 80% power  0.0107
  queries changed  200 / 672  (29.8%)
  Holm (5 tests)   p_adj = 0.0010   significant

[compare] fs2048-b19855.chunks.trec  vs  fs2048-b0.chunks.trec        (R@50)
  delta            -0.0351
  paired t         t = -6.82   p = 0.0000
  permutation      p = 0.0002   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0452, -0.0250]
  Cohen's d_z      -0.263
  MDE @ 80% power  0.0144
  queries changed  269 / 672  (40.0%)
  Holm (5 tests)   p_adj = 0.0010   significant

[compare] fs2048-b35800.chunks.trec  vs  fs2048-b0.chunks.trec        (R@50)
  delta            -0.0714
  paired t         t = -10.32   p = 0.0000
  permutation      p = 0.0002   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0849, -0.0578]
  Cohen's d_z      -0.398
  MDE @ 80% power  0.0194
  queries changed  350 / 672  (52.1%)
  Holm (5 tests)   p_adj = 0.0010   significant

[compare] fs2048-b3387.chunks.trec  vs  fs2048-b0.chunks.trec        (AP)
  delta            -0.0028
  paired t         t = -2.01   p = 0.0443
  permutation      p = 0.0464   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0056, -0.0001]
  Cohen's d_z      -0.078
  MDE @ 80% power  0.0039
  queries changed  534 / 672  (79.5%)
  Holm (5 tests)   p_adj = 0.0464   significant

[compare] fs2048-b6107.chunks.trec  vs  fs2048-b0.chunks.trec        (AP)
  delta            -0.0058
  paired t         t = -3.44   p = 0.0006
  permutation      p = 0.0008   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0092, -0.0025]
  Cohen's d_z      -0.133
  MDE @ 80% power  0.0048
  queries changed  549 / 672  (81.7%)
  Holm (5 tests)   p_adj = 0.0016   significant

[compare] fs2048-b11012.chunks.trec  vs  fs2048-b0.chunks.trec        (AP)
  delta            -0.0112
  paired t         t = -4.84   p = 0.0000
  permutation      p = 0.0002   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0158, -0.0067]
  Cohen's d_z      -0.187
  MDE @ 80% power  0.0065
  queries changed  565 / 672  (84.1%)
  Holm (5 tests)   p_adj = 0.0010   significant

[compare] fs2048-b19855.chunks.trec  vs  fs2048-b0.chunks.trec        (AP)
  delta            -0.0209
  paired t         t = -6.94   p = 0.0000
  permutation      p = 0.0002   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0268, -0.0150]
  Cohen's d_z      -0.268
  MDE @ 80% power  0.0084
  queries changed  584 / 672  (86.9%)
  Holm (5 tests)   p_adj = 0.0010   significant

[compare] fs2048-b35800.chunks.trec  vs  fs2048-b0.chunks.trec        (AP)
  delta            -0.0344
  paired t         t = -8.55   p = 0.0000
  permutation      p = 0.0002   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0423, -0.0265]
  Cohen's d_z      -0.330
  MDE @ 80% power  0.0113
  queries changed  594 / 672  (88.4%)
  Holm (5 tests)   p_adj = 0.0010   significant

[compare] fs2048-b3387.chunks.trec  vs  fs2048-b0.chunks.trec        (alpha_nDCG@10)
  delta            -0.0020
  paired t         t = -0.94   p = 0.3480
  permutation      p = 0.3528   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0062, +0.0022]
  Cohen's d_z      -0.036
  MDE @ 80% power  0.0060
  queries changed  295 / 672  (43.9%)
  Holm (5 tests)   p_adj = 0.3528   not significant

[compare] fs2048-b6107.chunks.trec  vs  fs2048-b0.chunks.trec        (alpha_nDCG@10)
  delta            -0.0039
  paired t         t = -1.38   p = 0.1694
  permutation      p = 0.1720   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0094, +0.0017]
  Cohen's d_z      -0.053
  MDE @ 80% power  0.0079
  queries changed  349 / 672  (51.9%)
  Holm (5 tests)   p_adj = 0.3440   not significant

[compare] fs2048-b11012.chunks.trec  vs  fs2048-b0.chunks.trec        (alpha_nDCG@10)
  delta            -0.0100
  paired t         t = -2.64   p = 0.0084
  permutation      p = 0.0086   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0174, -0.0026]
  Cohen's d_z      -0.102
  MDE @ 80% power  0.0106
  queries changed  387 / 672  (57.6%)
  Holm (5 tests)   p_adj = 0.0258   significant

[compare] fs2048-b19855.chunks.trec  vs  fs2048-b0.chunks.trec        (alpha_nDCG@10)
  delta            -0.0175
  paired t         t = -3.59   p = 0.0004
  permutation      p = 0.0004   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0272, -0.0079]
  Cohen's d_z      -0.138
  MDE @ 80% power  0.0137
  queries changed  430 / 672  (64.0%)
  Holm (5 tests)   p_adj = 0.0016   significant

[compare] fs2048-b35800.chunks.trec  vs  fs2048-b0.chunks.trec        (alpha_nDCG@10)
  delta            -0.0312
  paired t         t = -4.91   p = 0.0000
  permutation      p = 0.0002   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0436, -0.0187]
  Cohen's d_z      -0.189
  MDE @ 80% power  0.0178
  queries changed  455 / 672  (67.7%)
  Holm (5 tests)   p_adj = 0.0010   significant
```

---

## What this does and does not license

**Licensed.** Max-passage (β = 0) stands as the shipped aggregation rule, and the chunk-coverage
signal as specified — a β-weighted sum of a document's next three pooled chunk scores — is **rejected
on FreshStack-2048 at the production window**, on evidence rather than on a failure to find evidence.
The Phase 1 gate's "not licensed" clause is now answered: tail depth was available (85.0 % of top-50
slots have a tail), the term did move the ranking, and it moved it the wrong way.

There is no β on this ladder worth shipping, and none off it either: past parity 0.035800 the term
degenerates into a count of tail chunks, which spec §2 forbids and §5's opposite-conclusion rule would
misread; below tie-break 0.003387 the term cannot break a median top-10 gap, so it is inert by
construction. The ladder spans the entire interval in which the signal can do anything at all, and it
is negative across the whole of it.

**Not licensed.** The scope limits below are load-bearing, and the two that constrain *this* verdict
hardest are the fusion triple and the chunk budget.

### Scope limits (spec §9, inherited in full)

- **The null-or-negative is scoped to the shipped 0.45/0.45/0.10 fusion triple.** λ = 1.00 removes
  **diversification**, not the fusion weights. The chunk pool these arms aggregate over is still
  whatever Qdrant returned under `WBase`/`WCentroid`/`WDecay` = 0.45/0.45/0.10. **This phase does not
  disentangle the fusion weights from the aggregation rule** — it is the one scope limit that survives
  exactly the configuration this phase ran. What λ = 1.00 does buy is real and worth stating: stream
  order equals score order, so nothing about this negative is confounded *by MMR*. That is a narrower
  claim than "unconfounded".
- **Scoped to the 550-chunk budget.** The dump was taken at `--chunk-budget-multiplier` 11, which
  determines how many of a document's 2nd–4th chunks are in the pool to be read at all. Phase 1
  measured the budget truncating available tail on **13.8 %** of top-50 slots. This phase cannot show
  whether coverage helps at other chunk budgets.
- **The tail cap of 3 binds on the MAJORITY of ranked slots, not rarely.** Phase 1 measured **50.5 %**
  of top-50 slots contributing ≥ 4 pooled chunks. The parent spec §9 claim to the contrary was
  retracted in the Phase 1 gate and **must not be carried forward into how this result is read**. This
  cuts *for* the finding rather than against it: "the cap barely binds" would license "we never really
  tested a 3-chunk tail", and the measurement refuses that escape. The term evaluated here is a full
  3-chunk sum for most of the slots the ranking actually decides between, so this is a result about
  the signal, not about an inactive cap.
- **The deep-tail evidence is 512-window only.** FreshStack-2048 is the sole production-window arm; the
  density arms are 512/448 ingests and were not run. This result cannot be triangulated against any
  deep-tail arm **at the window that ships** — the gap
  `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` item 1 already names. Deferring
  those arms did not widen it; it was there either way.
- **Nor does this say whether the result transfers to the routed `SearchSimilar` path**, which
  collapses a larger pool scaling with the caller's `top_k` (`ObjectSearchGrpcService.cs:358-362`), so
  available tail depth varies per request and no fixed-budget harness arm measures it.

One thing the negative result makes *easier* to carry than a null would have: a null at a single
corpus and a single budget invites "try another budget". A monotone, significant, four-measure-agreeing
negative that grows with β is a statement about the direction of the term's effect on this corpus, and
the budget and triple qualify how far it generalises rather than whether it happened.

---

## What was captured but not read

**The λ = 0.70 confirmation arm and parent §6's ordering check remain unread, and this outcome does
not un-defer them.**

Spec §7 routes both to *"a β qualifying"*, and spec §1 fixes what that means operationally: *"pay for
everything that only matters if the answer is positive only once it is."* The confirmation arm exists
to answer whether a winning β survives the configuration that ships. There is no winning β. **The
capture was therefore not consumed by this phase, and this document does not claim it was.**

It was taken anyway, and the reason should be recorded honestly rather than retrofitted: at capture
time the outcome was unknown and the *capability* was perishable. The λ = 1.00 `iverson-api` container
carried a build whose source worktree (`.worktrees/chunk-coverage-phase1/Iverson.Server`) no longer
exists, so the build survived only as a local image; λ is fixed at container-create time, so reaching
0.70 required a recreate, and a recreate is one flag from a rebuild that would have produced a
different composite and destroyed the λ-only comparison permanently. Spending 46 minutes to convert a
perishable capability into a permanent artefact was the right call under uncertainty **and** it turned
out not to be needed. Both halves of that are true, and the second does not retroactively make it
waste — it makes it insurance that did not pay out.

What the capture leaves behind, banked:

- `chunk-coverage-phase2-l070-2026-09-09/runs/fs2048-pool-l070.chunks.hits.tsv` — 369,600 rows, 672
  queries, certified on composite `3ffafcd26416ed30`, and evidenced as λ = 0.70 by §3 above.
- Both λ = 0.70 items are now **offline replays**, like the primary. Reading the confirmation arm is
  six `benchmark-aggregate` invocations against that dump plus one `report.py` — minutes, no Docker,
  no Qdrant restore, no SIGPIPE exposure. It never again costs 46 minutes.
- Parent §6's ordering check additionally needs a `--scores-path` output from those replays, which
  does not yet exist. It is one flag on each replay.

Anyone re-opening chunk coverage — at another budget, another fusion triple, or another corpus —
starts from a certified λ = 0.70 dump they will not have to re-capture, and cannot re-capture, since
the build that produced it is gone from source.

### Still deferred, and what would un-defer it

Unchanged from spec §7.

| Deferred | Un-deferred by |
|---|---|
| FreshStack-512, NFCorpus (density arms) | a decision to spend ~2 h; they cannot triangulate at the production window anyway |
| SciFact-2048 (parent §5's invariant site) | a decision to re-site the §4 invariant |
| λ = 0.70 confirmation arm + parent §6 ordering check | a β qualifying — which, per this verdict, none does |

---

## Provenance

| | |
|---|---|
| Branch / HEAD | `chunk-coverage-phase2` @ `11f9c3a` |
| Spec | `docs/specs/2026-09-09-chunk-coverage-phase2-design.md` |
| Plan | `docs/plans/2026-09-09-chunk-coverage-phase2-implementation-plan.md` |
| Phase 1 gate | `docs/plans/2026-09-GATE-chunk-coverage.md` |
| Gate scripts | `Iverson.Server/Iverson.LoadTest/scripts/beta_invariant.py` (26 tests), `report.py` |
| Arm artefacts | `~/repositories/iverson-benchmark-corpora/chunk-coverage-phase2-arms-2026-09-09/` |
| λ = 0.70 capture | `~/repositories/iverson-benchmark-corpora/chunk-coverage-phase2-l070-2026-09-09/` |
| Source dump | `~/repositories/iverson-benchmark-corpora/chunk-coverage-phase1-2026-09-09/runs/` |
| Permutation seed | 20260831, 10,000 sign flips |

`docs/plans/` is git-ignored (`.gitignore:49`); this document is committed with `git add -f`, as the
Phase 1 gate was.
