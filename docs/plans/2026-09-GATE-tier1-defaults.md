# Tier 1 Retrieval Defaults — Gate Verdict

Recorded 2026-09-07 from local `main` HEAD `df3cfb2` (`Merge branch 'tier1-retrieval-defaults'`),
which carries Phase A and the harness changes this campaign consumes. Corresponds to Task 8 of
`docs/plans/2026-09-06-tier1-retrieval-defaults-implementation-plan.md`; the gate rules are
`docs/specs/2026-09-06-tier1-retrieval-defaults-design.md` §7.

Every API run in this campaign was produced by one binary, build composite **`9714c660b365fad1`**.
The λ sweeps changed only the two environment variables; the image never changed between arms.

Full `report.py` output — the source for every number below — lives untracked beside the run files
in the three run directories:

| Arm | Run directory | Snapshots |
|---|---|---|
| `sci-2048` | `~/repositories/iverson-benchmark-corpora/scifact-2048-2026-09-06/` | `scifact-2048-qdrant-snapshots/` |
| `fs-2048` | `~/repositories/iverson-benchmark-corpora/freshstack-2048-2026-09-07/` | `freshstack-2048-qdrant-snapshots/` |
| `fs-512` | `~/repositories/iverson-benchmark-corpora/freshstack-512-2026-09-07/` | `freshstack-512-qdrant-snapshots/` |

Each directory holds `report-all.txt`, the per-rule reports named below, `runs/`, `ingest.log`,
`keymap.json`, `keymap.json.stats.json` and `similar-arms.log`. Each snapshot directory holds both
collection snapshots and a `RESTORE.md`. The comparison baseline for rule 7.1(a) is
`scifact-bge-base-2026-09-04/runs/bge-base.chunks.trec` (M1). The restored production baseline is
`scifact-bge-base-qdrant-snapshots/`.

## Method

The protocol is spec §6. Three `ingest.py --drop` ingests, one arm at a time, TEI at
`http://localhost:8091` serving `BAAI/bge-base-en-v1.5` (768 dims, `max_batch_tokens` 16384), each
followed by its query runs over those vectors. Chunk counts are the ingest sidecar's own counts, and
each arm is invalid unless it hits its predicted count exactly — all three did.

Per-corpus chunk budget multiplier: **5** on SciFact (1.27 chunks/doc), **11** on both FreshStack
arms (3.10 and 10.79 chunks/doc). At the default multiplier of 5 the FreshStack arms would have
tripped `ChunkBudgetGuard` and refused every query; no run in this campaign logged a
`REFUSING: chunk budget` line.

One `report.py` invocation per rule, so the Holm family is exactly the rule's own comparisons:

- rule 7.1(a) — `report-rule71a.txt` in `sci-2048`, family of 1 per measure
- rule 7.1(b) — `report-rule71b.txt` in `fs-512`, family of 1 per measure
- rule 7.2 — `report-rule72.txt` in each FreshStack arm, family of **3** (the three λ against 0.70)
- rule 7.3 — `report-rule73.txt` in each of the three arms, family of 1 per measure

`report-all.txt` per arm carries the structural checks, absolute scores and the diversity means. All
runs reported 300/300 (SciFact) or 672/672 (FreshStack) qrels queries covered, no duplicate doc ids,
and all scores non-zero.

### Arms

| Arm | Corpus | Window | Docs | Chunks | Mult | Ingest elapsed | Embed calls (saved) |
|---|---|---|---|---|---|---|---|
| `sci-2048` | SciFact | 2048/1792 | 5,183 | **6,587** | 5 | 5,036 s (1.40 h) | 7,950 (3,820) |
| `fs-2048` | FreshStack 6k slice | 2048/1792 | 6,000 | **18,622** | 11 | 21,332 s (5.93 h) | 24,454 (168) |
| `fs-512` | FreshStack 6k slice | 512/448 | 6,000 | **64,735** | 11 | 23,893 s (6.64 h) | 70,670 (65) |

Ingest totalled 14.0 h against the spec's ≈48 h estimate; the rate was 0.97, 3.56 and 3.98 s/doc
respectively. Each FreshStack arm ran four λ query runs (≈31 min each) plus the raw-arm pass; the
SciFact arm ran one query run plus its raw-arm pass.

The FreshStack nugget qrels were filtered from the five topics' subtopic judgements to the slice by
`freshstack_nugget_qrels.py`: **64,539 rows** kept covering **672 / 672** slice queries (angular
11,929; godot 7,949; langchain 23,143; laravel 15,087; yolo 6,431). This file is what supplies
α-nDCG@10; the same file was used for both FreshStack arms.

## Results

### Rule 7.1 — the chunk window

**(a) `sci-2048` vs `bge-base` (M1), `.chunks`** — `sci-2048/report-rule71a.txt`:

```
[compare] sci-2048.chunks.trec  vs  bge-base.chunks.trec        (nDCG@10)
  delta            +0.0024
  permutation      p = 0.7967   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0159, +0.0206]
  Holm (1 tests)   p_adj = 0.7967   not significant

[compare] sci-2048.chunks.trec  vs  bge-base.chunks.trec        (R@50)
  delta            +0.0057
  permutation      p = 0.6095   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0132, +0.0246]
  Holm (1 tests)   p_adj = 0.6095   not significant
```

The `BUILD MISMATCH` warning on this pair is expected and benign (spec §11): M1's API run was made
by an earlier binary, and it is the same control the embedding migration used.

**(b) `fs-2048-l070` vs `fs-512-l070`, `.chunks`** — `fs-512/report-rule71b.txt`:

```
[compare] fs-2048-l070.chunks.trec  vs  fs-512-l070.chunks.trec        (nDCG@10)
  delta            -0.0074
  permutation      p = 0.1216   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0171, +0.0022]
  queries changed  490 / 672  (72.9%)
  Holm (1 tests)   p_adj = 0.1216   not significant

[compare] fs-2048-l070.chunks.trec  vs  fs-512-l070.chunks.trec        (R@50)
  delta            +0.0064
  permutation      p = 0.2980   (10,000 sign flips, seed 20260831)
  95% CI           [-0.0054, +0.0183]
  queries changed  312 / 672  (46.4%)
  Holm (1 tests)   p_adj = 0.2980   not significant
```

### Rule 7.2 — λ per endpoint

Absolute scores per λ, both FreshStack arms (`report-all.txt`):

**`fs-2048`**

| λ | `.similar` nDCG@10 | R@50 | α-nDCG@10 | `.chunks` nDCG@10 | R@50 | α-nDCG@10 | parents@10 | @50 |
|---|---|---|---|---|---|---|---|---|
| 0.50 | 0.2466 | 0.3130 | 0.3167 | 0.2871 | 0.5163 | 0.3525 | 9.760 | 45.485 |
| 0.70 | 0.2793 | 0.4670 | 0.3423 | 0.2933 | 0.5443 | 0.3552 | 8.104 | 34.497 |
| 0.85 | 0.2801 | 0.5104 | 0.3427 | 0.2932 | 0.5446 | 0.3551 | 7.256 | 30.549 |
| 1.00 | 0.2801 | 0.5176 | 0.3427 | 0.2932 | 0.5446 | 0.3551 | 6.665 | 28.815 |

**`fs-512`**

| λ | `.similar` nDCG@10 | R@50 | α-nDCG@10 | `.chunks` nDCG@10 | R@50 | α-nDCG@10 | parents@10 | @50 |
|---|---|---|---|---|---|---|---|---|
| 0.50 | 0.2578 | 0.3249 | 0.3284 | 0.2950 | 0.5252 | 0.3586 | 9.299 | 39.101 |
| 0.70 | 0.2899 | 0.4721 | 0.3510 | 0.3007 | 0.5378 | 0.3621 | 7.176 | 26.854 |
| 0.85 | 0.2908 | 0.5152 | 0.3515 | 0.3016 | 0.5431 | 0.3626 | 6.131 | 22.106 |
| 1.00 | 0.2908 | 0.5238 | 0.3515 | 0.3016 | 0.5439 | 0.3626 | 5.204 | 19.082 |

The decisive α-nDCG@10 comparisons on `.similar`, each within its own family of three:

```
fs-2048  (report-rule72.txt)
[compare] fs-2048-l050.similar.trec  vs  fs-2048-l070.similar.trec        (alpha_nDCG@10)
  delta -0.0257   permutation p = 0.0002   95% CI [-0.0341, -0.0172]   Holm p_adj = 0.0006   significant
[compare] fs-2048-l085.similar.trec  vs  fs-2048-l070.similar.trec        (alpha_nDCG@10)
  delta +0.0004   permutation p = 0.5815   95% CI [-0.0010, +0.0018]   Holm p_adj = 1.0000   not significant
[compare] fs-2048-l100.similar.trec  vs  fs-2048-l070.similar.trec        (alpha_nDCG@10)
  delta +0.0004   permutation p = 0.5815   95% CI [-0.0010, +0.0018]   Holm p_adj = 1.0000   not significant

fs-512   (report-rule72.txt)
[compare] fs-512-l050.similar.trec  vs  fs-512-l070.similar.trec        (alpha_nDCG@10)
  delta -0.0226   permutation p = 0.0002   95% CI [-0.0308, -0.0144]   Holm p_adj = 0.0006   significant
[compare] fs-512-l085.similar.trec  vs  fs-512-l070.similar.trec        (alpha_nDCG@10)
  delta +0.0005   permutation p = 0.4282   95% CI [-0.0007, +0.0017]   Holm p_adj = 0.8563   not significant
[compare] fs-512-l100.similar.trec  vs  fs-512-l070.similar.trec        (alpha_nDCG@10)
  delta +0.0005   permutation p = 0.4282   95% CI [-0.0007, +0.0017]   Holm p_adj = 0.8563   not significant
```

The R@50 side of the same families, which is the price the rule says is measured:

```
fs-2048
[compare] fs-2048-l050.similar.trec  vs  fs-2048-l070.similar.trec        (R@50)
  delta -0.1540   95% CI [-0.1698, -0.1382]   Holm p_adj = 0.0006   significant
[compare] fs-2048-l085.similar.trec  vs  fs-2048-l070.similar.trec        (R@50)
  delta +0.0434   95% CI [+0.0340, +0.0527]   Holm p_adj = 0.0006   significant
[compare] fs-2048-l100.similar.trec  vs  fs-2048-l070.similar.trec        (R@50)
  delta +0.0506   95% CI [+0.0381, +0.0631]   Holm p_adj = 0.0006   significant
```

### Rule 7.3 — object-vector representation

Raw `centroid-raw.similar` vs raw `head-raw.similar`, each arm its own family of one:

```
sci-2048  (report-rule73.txt)
  (nDCG@10)  delta -0.0044   permutation p = 0.4622   95% CI [-0.0163, +0.0074]   Holm p_adj = 0.4622   not significant
  (R@50)     delta -0.0050   permutation p = 0.6131   95% CI [-0.0168, +0.0068]   Holm p_adj = 0.6131   not significant
  (AP)       delta -0.0053   permutation p = 0.4438   95% CI [-0.0187, +0.0082]   not significant

fs-2048   (report-rule73.txt)
  (nDCG@10)  delta +0.0251   permutation p = 0.0004   95% CI [+0.0116, +0.0386]   Holm p_adj = 0.0004   significant
  (R@50)     delta +0.0576   permutation p = 0.0002   95% CI [+0.0400, +0.0751]   Holm p_adj = 0.0002   significant
  (AP)       delta +0.0200   permutation p = 0.0004   95% CI [+0.0097, +0.0303]   significant

fs-512    (report-rule73.txt)
  (nDCG@10)  delta +0.0290   permutation p = 0.0006   95% CI [+0.0144, +0.0436]   Holm p_adj = 0.0006   significant
  (R@50)     delta +0.0598   permutation p = 0.0002   95% CI [+0.0419, +0.0778]   Holm p_adj = 0.0002   significant
  (AP)       delta +0.0257   permutation p = 0.0002   95% CI [+0.0148, +0.0365]   significant
```

A consistency check falls out of these runs. `head-raw.similar` scores **identically** on both
FreshStack arms (nDCG@10 0.2532, R@50 0.4670, AP 0.1789, α-nDCG@10 0.3111) because `body_vector` is
the document head and does not depend on the chunk window, while `centroid-raw` differs between the
arms (0.2783 vs 0.2822 nDCG@10) because the centroid is the mean of chunk vectors and does.

## Verdicts

### Rule 7.1 — **PASS on both halves. The chunk-window default STANDS.**

| Half | nDCG@10 CI lower | R@50 CI lower | > −0.02? |
|---|---|---|---|
| (a) `sci-2048` vs M1 | −0.0159 | −0.0132 | yes |
| (b) `fs-2048` vs `fs-512` | −0.0171 | −0.0054 | yes |

Both CI lower bounds clear −0.02 on both halves, and no delta is significant on either. **Spec §3.4
does NOT execute**: the five client attribute defaults, `SchemaBuilder.cs:161-162`, the Go
`tags_test.go` assertions, `IngestContractTests.cs:75` and `ingest-contract.json` all stay at
512 tokens / 64 overlap. The shipped default was never measured before this campaign; it is now, and
it holds at both the SciFact and FreshStack ends.

### Rule 7.2 — **`LambdaSimilar` = 1.00. `LambdaChunks` = 0.70 (unchanged).**

**`LambdaSimilar` — the none-qualify clause fired, and λ = 1.00 was not itself worse.** No λ beat
0.70 on α-nDCG@10 with Holm p_adj < 0.05 on either arm: λ = 0.50 is significantly *worse* on both
(p_adj 0.0006), and λ = 0.85 and λ = 1.00 are flat (p_adj 1.0000 and 0.8563, deltas +0.0004 and
+0.0005). The rule's fallback is therefore 1.00 — "the R@50 price is measured and the benefit is
not" — and the veto clause does not apply, because λ = 1.00 is not significantly worse than 0.70 on
α-nDCG@10 on either arm.

The R@50 price is not merely unpaid, it is negative: λ = 1.00 is significantly *better* on R@50 than
0.70 on both arms — **+0.0506** on `fs-2048` (95% CI [+0.0381, +0.0631], Holm p_adj 0.0006) and
**+0.0518** on `fs-512` (95% CI [+0.0401, +0.0634], permutation p 0.0002) — at no measurable
α-nDCG@10 cost. MMR on `SearchSimilar` has been suppressing recall without buying diversity that
α-nDCG can detect.

**`LambdaChunks` — 0.70 stays, on the diversity clause, decided on both arms independently.** The
rule sets 1.00 only if λ 1.00 vs 0.70 moves the mean distinct parents in the top-10 chunk hits by
less than 1.0 on both arms:

| Arm | λ 0.70 | λ 1.00 | change | < 1.0? |
|---|---|---|---|---|
| `fs-2048` | 8.104 | 6.665 | **1.439** | no |
| `fs-512` | 7.176 | 5.204 | **1.972** | no |

Either arm alone decides it. This is the campaign's clearest result and it overturns item 2 of
`docs/2026-09-06-ranked-changes-after-retrieval-experiments.md`, which called λ = 1.0 on
`SearchChunks` a free win. On the collapsed `.chunks` metrics λ 0.70 and λ 1.00 are identical to four
decimal places — nDCG@10 0.2933 vs 0.2932, R@50 0.5443 vs 0.5446, α-nDCG@10 0.3552 vs 0.3551 on
`fs-2048` — because the harness's max-passage collapse reduces chunks to documents before scoring and
therefore cannot see chunk-level MMR at all. Production `SearchChunks` returns the chunk list, so the
caller sees the full effect: at λ = 1.00 on `fs-512` a request for ten chunks yields **5.2** distinct
source documents instead of 7.2. The "free win" was an artifact of the measurement, not a property of
the system, and the `<label>.chunks.diversity.json` sidecar added by this spec is what makes it
visible.

### Rule 7.3 — **follow-up spec RECOMMENDED.**

The centroid is better than the head vector on both nDCG@10 and R@50 with Holm p_adj < 0.05 on
**both** FreshStack arms (fs-2048: +0.0251 / +0.0576; fs-512: +0.0290 / +0.0598, all p_adj ≤ 0.0006),
and its CI lower bounds on SciFact (−0.0163 and −0.0168) exceed −0.02. All three conditions hold.

The gate therefore recommends a follow-up spec making `SearchSimilar` search `<property>_centroid`
for chunked properties. The mechanism is legible: SciFact averages 1.27 chunks per document, so the
centroid is nearly the head vector and the two arms tie; FreshStack averages 3.10 and 10.79, where
the centroid summarises the whole document and the head vector sees only its first 512 tokens. This
is item 3 of the ranked document, and it is the campaign's one affirmative finding.

Note what this verdict is *not*: it is a statement about the **raw** `body_centroid` versus the raw
`body_vector`. The API's fused `.similar` path already adds the centroid at weight 0.45, so this does
not say the shipped fusion is wrong — it says the representation `SearchSimilar` searches deserves its
own spec. Changing what `SearchSimilar` searches is explicitly out of scope here (spec §2).

## Consequences applied

1. `LambdaSimilar` default 0.70 → **1.00** in `VectorRankingOptions.cs`, the `docker-compose.yml`
   fallback, and the options-defaults test. `LambdaChunks` stays **0.70**.
2. No window change: rule 7.1 passed, so spec §3.4 is not executed.
3. Rule 7.3's recommendation is recorded here; the follow-up spec is not written by this plan.

**Already-registered types keep their chunk window.** A default change does not migrate existing
registrations, and the registration guard blocks an in-place window change on a registered type. This
matters only if a future gate changes the window; it did not change here.

## Box state at close

Qdrant restored to the SciFact bge-base baseline from
`scifact-bge-base-qdrant-snapshots/`: **19,967** chunk points at 768 dims and **5,183** object
points, verified after restore. The `BenchmarkDocument` schema row was cleared, `iverson-zookeeper`,
`iverson-kafka`, `iverson-starrocks` and `iverson-jaeger` restarted, `iverson-api` and
`iverson-worker` brought up on defaults, and `benchmark-query --help` reported `Schemas registered.`
The API's env shows `VectorRanking__LambdaSimilar=0.70` and `VectorRanking__LambdaChunks=0.70` — the
values in force at the moment of the restore, before the consequence in §1 above was applied.
