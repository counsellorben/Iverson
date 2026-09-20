# Hybrid RRF Search — Gate Verdict

Recorded 2026-09-20 from branch `hybrid-rrf-search` HEAD `5b1eb89c6a0c9d070436c673998650e8810716e4`
(build composite **`72e8f8c1903197ba`**, same binary for both arms below). Corresponds to
`docs/specs/2026-09-17-hybrid-rrf-search-design.md` and
`docs/plans/2026-09-20-hybrid-rrf-search-implementation-plan.md` (8 tasks, individually reviewed;
final whole-branch review found 5 Important findings, all fixed and independently re-verified before
this run). **The code itself was not merged to `main`** — see "Disposition" below.

Full `report.py` output — the source for every number below — lives at
`~/repositories/iverson-benchmark-corpora/scifact-2048-2026-09-06/report-hybrid-rrf-similar.txt` and
`report-hybrid-rrf-chunks.txt`. Raw run files, meta sidecars, and harness logs live beside them in
`runs/hybrid-rrf-{control,hybrid}.*`.

## Method

One corpus, one binary, two arms, differing only in the `--hybrid-lexical` flag added by the plan's
Task 8. No calibration was attempted — every fusion constant (RRF `K=60`, BM25 `k1=1.2`, `b=0.75`)
is the design spec's own literal default, per spec §9's own accepted-limitation note ("RRF k, BM25
k1 and BM25 b are uncalibrated constants").

**Corpus:** SciFact-2048 (`scifact-2048-2026-09-06`), 5,183 documents, 6,587 chunks, 300 queries
(same corpus and window as the tier-1 retrieval-defaults campaign,
`docs/plans/2026-09-GATE-tier1-defaults.md`).

**Infrastructure:** a fresh, isolated `docker-compose` stack (`Iverson.LoadTest/scripts/stack.py
query`, plus `starrocks`/`starrocks-init`/`authentik-worker`, none of which are in the `query` tier
by default — the `authentik-worker` omission blocks the client-credentials grant entirely, and the
`starrocks` omission blocks the hybrid leg specifically). **Root cause note for future runs against
this box:** the `iverson-api` image already present locally was stale (built 2026-09-11, predating
even the tenant-fingerprinted Qdrant collection-naming scheme this corpus's tooling assumes) — `docker
compose up` silently reuses a stale image rather than rebuilding, which produced an entire run against
orphaned collections from an unrelated prior deployment before this was caught by the harness's own
key-map integrity check (`BenchmarkQueryScenario`'s "N parent key(s) ... absent from the key map"
refusal — it did exactly its job). `docker compose build iverson-api` before `up` is required whenever
the local image predates the worktree's HEAD.

**Data:** both stores were populated fresh and independently for this run — StarRocks via a
purpose-built backfill script (plain parameterized `INSERT`s of the corpus text, keyed by the same
deterministic `uuid5` scheme `ingest.py` already uses for Qdrant, so
`IntelligenceStoreConsumer.KeyToUlong` reproduces the identical id on both sides with no re-ingest
needed), Qdrant via `ingest.py`'s existing fast bypass path. StarRocks row count (5,183) and Qdrant
object point count (5,183) were verified to match exactly before either query run — the precondition
the plan's Task 8 comment documents but deliberately does not enforce in code.

**Commands** (both arms identical except the flag):

```bash
dotnet run --project Iverson.LoadTest -- benchmark-query \
  --corpus-path .../scifact-2048-2026-09-06 --key-map-path .../keymap.json \
  --output-dir .../results --config-label control   [--hybrid-lexical for the second arm]
```

Both arms: 15,000 rows, 300/300 distinct queries, 15,000/15,000 non-zero scores, 300/300 qrels
queries covered, no duplicate doc ids — the harness's own structural checks, both clean.

## Results

### `.similar` (SearchSimilar)

```
[scores] control.similar.trec   nDCG@10 0.7468   R@50 0.9510   AP 0.7064
[scores] hybrid.similar.trec    nDCG@10 0.7208   R@50 0.9499   AP 0.6775

[compare] hybrid vs control   (nDCG@10)
  delta -0.0260   permutation p = 0.0526   95% CI [-0.0520, -0.0000]
  queries changed 105/300 (35.0%)   Holm p_adj = 0.0526   not significant

[compare] hybrid vs control   (R@50)
  delta -0.0011   permutation p = 0.9891   95% CI [-0.0244, +0.0222]
  queries changed 15/300 (5.0%)   Holm p_adj = 0.9891   not significant
  !! FEW QUERIES CHANGED (5.0%) — permutation p is the one to trust, not the paired t.

[compare] hybrid vs control   (AP)
  delta -0.0290   permutation p = 0.0482   95% CI [-0.0570, -0.0009]
  queries changed 121/300 (40.3%)   Holm p_adj = 0.0482   SIGNIFICANT
```

### `.chunks` (SearchChunks)

```
[scores] control.chunks.trec   nDCG@10 0.7476   R@50 0.9393   AP 0.7045   parents@10 9.42   @50 46.94
[scores] hybrid.chunks.trec    nDCG@10 0.7234   R@50 0.9532   AP 0.6790   parents@10 9.33   @50 45.55

[compare] hybrid vs control   (nDCG@10)
  delta -0.0242   permutation p = 0.0634   queries changed 107/300 (35.7%)   not significant

[compare] hybrid vs control   (R@50)
  delta +0.0139   permutation p = 0.3012   queries changed 16/300 (5.3%)   not significant

[compare] hybrid vs control   (AP)
  delta -0.0255   permutation p = 0.0756   queries changed 122/300 (40.7%)   not significant
```

## Verdict — **NO-GO on default constants; not a defect finding**

Every path that could carry a bug is clean: both arms hit identical query/row/coverage counts, one
binary produced both, and the harness's own integrity gates (build identity, key-map coverage,
chunk-budget refusal) all passed without needing to fire. The regression is real, reproducible, and
entirely attributable to fusion math, not implementation.

`hybrid.lexical` is a **small, consistently negative** shift on ranking-order metrics (nDCG@10, AP)
across both `SearchSimilar` and `SearchChunks`, and a **null effect** on recall (R@50 — both deltas
fail to clear even a fifth of paired queries changing, the harness's own "too few queries changed"
guard). One comparison (`.similar` AP) clears Holm-corrected significance; the rest are directionally
consistent but individually short of it — four negative deltas out of five non-recall comparisons is
not noise, but it is a small effect (`|d_z|` ≈ 0.10–0.12 throughout).

The mechanism is legible and matches the design spec's own prediction: SciFact is a strong
semantic-match corpus (control nDCG@10 already 0.75), and unweighted RRF gives the untuned BM25 leg
equal say per rank position regardless of how much worse it is than the vector leg on this corpus —
diluting a strong baseline rather than rescuing weak recall, which is the scenario RRF fusion is
actually good at. This is the accepted, named limitation from spec §9 confirmed empirically, not a
new one.

**What this result does and does not say:**
- Does not indict the implementation — every task's own review, the final whole-branch review, and
  this run's own structural checks all passed clean.
- Does not indict RRF fusion as a mechanism — it indicts *these specific, uncalibrated constants on
  this specific, strongly-semantic corpus*.
- Says nothing about the `rank_query` leg (structured filter+sort boosting) — this run only exercised
  `--hybrid-lexical`; `rank_query` was not benchmarked at all.
- Says nothing about a corpus where lexical/exact-term matching carries more signal than SciFact's
  paraphrase-heavy claims (e.g. code, legal citation, or part-number-style exact-match corpora) — the
  mechanism that hurts here (equal-weight fusion with a weak lexical leg) would need re-evaluating
  against a corpus where the lexical leg is *not* the weak leg.

## Disposition

Per direct instruction: **the code is not merged.** Only this document, the design spec, and the
implementation plan are the artifacts of record on `main`. The design and implementation documents
were already committed to `main` ahead of this run (`9f1e8e5c` spec, `434653b7` plan,
`d606d649` plan-CIR-fixes); this file is the one new addition. The 12 implementation commits, the
whole-branch review, and the final fix-wave remain on the `hybrid-rrf-search` branch only.

If this feature is revisited, the two open threads are: (1) calibrate `K`/`k1`/`b` rather than
re-running on defaults — a naive grid search against this same corpus/qrels pair is the cheapest next
step and the harness already supports the `--hybrid-lexical` A/B shape needed for it; (2) benchmark
`rank_query` and a lexical-favoring corpus before concluding anything about the mechanism generally,
not just this one arm.
