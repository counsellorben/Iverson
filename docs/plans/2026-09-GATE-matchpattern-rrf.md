# MatchPattern and RRF — Gate Verdict

Recorded 2026-09-29 from branch `matchpattern-rrf-test` HEAD `a819b2a4d8f62e714d3c9259d8d50e409d4c4e82`
(build composite **`ded69e9492bdc081`**; every pass and every run below is on that one build).
Corresponds to `docs/specs/2026-09-28-matchpattern-rrf-test-design.md` (commit `fa7194ac`) and
`docs/plans/2026-09-28-matchpattern-rrf-test-implementation-plan.md`.

Every number below comes from a file under
`/home/ben/repositories/iverson-benchmark-corpora/matchpattern-rrf-2026-09-28/` (`$OUT`):
`phase0.md`, `pattern/probe-b.json`, `pattern/theta.json`, `pattern/build.json`, `fused/guard.json`,
`fused/inert.json` and `report.txt`. The md5s in section 9 pin them.

**Corpus:** FreshStack-2048 (`freshstack-2048-2026-09-07`): 6,000 documents, 18,622 chunks, bge-base,
2048/1792 window, 672 judged queries, α-nDCG nugget qrels.

## 1. Verdicts

| Reading | Verdict | Basis |
|---|---|---|
| 1. RRF inside one request | **Partial** | `Iverson.Server/Iverson.Patterns.Tests/RrfExpressivenessTests.cs` (commit `72685d0a`) pins four facts: (1) `The_order_by_rrf_term_is_one_over_sixty_plus_the_running_count`: `1.0 / (60 + RUNNING COUNT(*))` under ALL ROWS yields `1/61, 1/62, …` in `order_by` order; (2) `Final_count_minus_running_count_plus_one_is_the_reverse_rank`: `FINAL COUNT(*) - RUNNING COUNT(*) + 1` is the reverse rank; (3) `Similarity_over_its_final_max_normalises_the_best_row_to_one`: `SIMILARITY(...) / FINAL MAX(SIMILARITY(...))` normalises scores to 1, which is score fusion, not RRF; (4) `A_rank_by_similarity_needs_a_correlated_count_which_compile_rejects`: the correlated rank-by-similarity count throws `PatternValidationException` ("must match"). The `order_by` leg of RRF is expressible; a rank by `SIMILARITY` is not. |
| 2. RRF over its output | **NO-GO.** Feasible **client-side only** | The benchmark below. No server-side fusion exists. All six arms are neither DEGENERATE nor INERT, and every one **lowers** nDCG@10 against the re-recorded baseline, significantly after Holm correction (Δ −0.0330 / −0.0148 / −0.0050 at p50 / p75 / p90; Holm-adjusted p 0.0012 / 0.0012 / 0.0480). No arm improves. **Non-default limits:** it ran with `Patterns__Limits__MaxExpressionLength=25000` (default 1,000) and `Patterns__Limits__MaxOutputRows=18623` (default 10,000). On default limits, **437 of 672** queries (65.0%) could not be issued at all (section 3). The whole-corpus shape used here is also unavailable on default limits. Its `limit` 18,623 exceeds the default `MaxOutputRows` 10,000, which is rejected with `InvalidArgument` (`ObjectSearchGrpcService.MatchPattern.cs:294-295`). A whole-corpus calibration call also emits 18,622 rows. A default-configured caller therefore needs per-candidate calls, projected at 12.2 h for calibration and 12.4 h per θ pass. |
| 3. Matching over an RRF order | **Not possible today** | `order_by` names stored properties only; an RRF order exists only on the unmerged `hybrid-rrf-search` branch. The two halves are pinned by existing tests (deviation 1): `MatchRowsQueryBuilderTests.Every_slot_rejects_an_unknown_hidden_tenant_or_bytes_column` (`Iverson.StarRocks.Tests/MatchRowsQueryBuilderTests.cs:86`), where the StarRocks builder rejects an `order_by` column the type does not have, and `MatchPatternGrpcServiceTests.Store_exceptions_map_as_the_spec_section_6_table_says` (`Iverson.Api.Tests/Grpc/MatchPatternGrpcServiceTests.cs:428`), where the service maps that exception to `InvalidArgument`. |

## 2. Phase 0 (from `phase0.md`)

**Build composite:** `ded69e9492bdc081`. The live `/build` and the baseline sidecar
(`runs/mp-baseline.meta.json`) are equal, checked at Step 3 and again at the end of Task 4.

**Stack environment** (`docker exec iverson-api env`):

| Setting | Value | Default | Note |
|---|---|---|---|
| `Patterns__Limits__MaxExpressionLength` | `25000` | 1,000 | **non-default**; see the reading-2 verdict and section 3 |
| `Patterns__Limits__MaxOutputRows` | `18623` | 10,000 | **non-default**; exercised, since the whole-corpus shape was chosen |
| `VectorRanking__LambdaChunks` | `0.70` | 0.70 | the shipped value |

**Restore counts** (`counts`, after restore and again at the end of Task 4):
`benchmark_documents_tenant_bypass_a8j5vpgduxk1bsvma7542yqnk` had **6000** points, with vectors
`['body_centroid', 'body_vector']`. `benchmark_documents_chunks_tenant_bypass_a8j5vpgduxk1bsvma7542yqnk`
had **18622** points, with vectors `['body_vector']`. The counts never changed during the task.

**Baseline** (`mp-baseline`, `benchmark-query --chunk-budget-multiplier 11`, `LambdaChunks` 0.70):
`DOTNET_EXIT=0`, 33,600 rows (672 × 50).

**Probe A (PASS):** query `47128903`, candidate `docs/authorization.md_2493_11872`
(`4626c17b-61e4-5283-9607-5a0bf891867d`). The ALL ROWS running-sum call returned 6 rows, and every
`s` was non-NULL (0.7607254981994629 … 0.7576436400413513). `SUM(SIMILARITY(A.text, q))` equals the
running sum of `s` on every chunk. The text-free calibration call gave the same `(chunk index, s)`
for every chunk and carried no `text` column. `EXIT=0`. The first attempt failed with
`UNAUTHENTICATED` 401; that was fix round 3 (section 10).

**Probe B** (`pattern/probe-b.json`; 10 queries, placeholder θ 0.6):

| Shape / pass | Calls | Mean (s) | Max (s) | Timeouts |
|---|---|---|---|---|
| per-candidate / calibration | 500 | 1.3076938338239852 | 9.77188541799842 | 0 |
| per-candidate / theta | 500 | 1.3290015042937593 | 9.929607187004876 | 0 |
| whole-corpus / calibration | 10 | 10.277627585301525 | 14.84286457899725 | 0 |
| whole-corpus / theta | 10 | 6.189448394201463 | 8.648566600997583 | 0 |

Projected per-candidate full passes (672 × 50 calls): calibration **12.20514244902386 h**, θ
**12.404014040075086 h**. Both are over the 8 h budget (deviation 3). No whole-corpus call reached
the 30 s timeout. **Chosen shape: `whole-corpus`**, `limit` 18,623 on every call (per-candidate
would have used 10,000).

**Actual pass times, whole-corpus** (file modification times of `calibration.tsv` and
`mp-<θ>-run_len.trec`; the 07:44 calibration start comes from the Task 5 session record):
calibration about 1 h 25 m, ending 09:09:50;
p50 ending 10:18:44; p75 ending 11:25:49; p90 ending 12:33:26. Each θ pass took about 67–69 minutes.

## 3. Query length (recomputed from the strings `pattern_leg.py` sends)

The recompute imports `pattern_leg` and calls its request builders: `calibration_request` and
`theta_request` at each of the three measured θ values, in the whole-corpus shape. It runs over
`beir/queries.jsonl` for the 672 baseline query ids, and measures every `define` and `measure`
expression in the built `MatchPatternRequest` protobufs.

| Expression | Queries over 1,000 characters |
|---|---|
| calibration `measure s` = `SIMILARITY(text, '<q>')` | 432 |
| θ `define A` = `SIMILARITY(text, '<q>') > <θ>` (each of p50, p75, p90) | 437 |
| θ `measure run_sum` = `SUM(SIMILARITY(A.text, '<q>'))` (each θ) | 432 |
| **Any expression** | **437 of 672 (65.0%)** |

Longest expression: **21,346** characters (query `78003643`, the p50 `define`).

This differs from the spec's "433 of 672" and "21,333". The whole difference is the θ literal:
`format_theta` writes θ at full `repr` precision, 18 characters (`0.6597611904144287`,
`0.7002342939376831`, `0.7339564561843872`). Five queries (`77354096`, `78052517`, `76168470`,
`77883233`, `76388280`) have a `define` of 1,002–1,013 characters. Without the θ literal those
defines are 984–995 characters, and their calibration `s` measures are 981–992 characters. The
spec's figure predates the measured θ values, so its `define` could not have carried this 18-character
spelling. The 432 figure for the other two expressions matches the spec's per-expression 432 / 432
exactly. With the strings actually sent, the figure is **437**.

The spec's two figures are not consistent with each other. 21,333 corresponds to a 5-character θ
literal (432 queries over 1,000), while 433 needs a 6–9-character literal (maximum 21,334–21,337).
The conclusion is unaffected.

## 4. θ values (`pattern/theta.json`)

| θ | Value |
|---|---|
| p50 | `0.6597611904144287` |
| p75 | `0.7002342939376831` |
| p90 | `0.7339564561843872` |

The sample is the 100 lowest query ids, `47128903` through `76452844`: 100 distinct ids and
`rows_in_sample` **19061** chunks. The values satisfy p50 < p75 < p90. Candidates with a run: p50
30,212, p75 20,283, p90 10,381, out of 33,600 (672 × 50). Longest best run: 7 / 7 / 6 chunks. Mean
best `run_len`: 2.132 / 1.561 / 1.277.

## 5. Degeneracy guard (`fused/guard.json`)

Spearman ρ is taken over all 33,600 (query, candidate) pairs, with no-run candidates at
floor = min(matched) − 1. A pair is DEGENERATE when |ρ| ≥ 0.95.

| Arm | ρ vs chunk count | ρ vs max-chunk similarity | DEGENERATE |
|---|---|---|---|
| fused-p50-run_len | 0.3057458733380149 | 0.5688489389420801 | false |
| fused-p50-run_sum | 0.22467359328415049 | 0.7362642729560462 | false |
| fused-p75-run_len | 0.05072291240333951 | 0.8472738460239962 | false |
| fused-p75-run_sum | 0.013310831780053134 | 0.9197297456406506 | false |
| fused-p90-run_len | -0.02737001867418074 | 0.7996206028519995 | false |
| fused-p90-run_sum | -0.036327674787347324 | 0.8136004512214037 | false |

No arm is DEGENERATE. The largest |ρ| is 0.9197297456406506 (p75 `run_sum` against max-chunk
similarity).

## 6. INERT (`fused/inert.json`)

| Arm | Reordered fraction | INERT |
|---|---|---|
| fused-p50-run_len | 0.9940476190476191 (668 / 672) | false |
| fused-p50-run_sum | 0.9940476190476191 (668 / 672) | false |
| fused-p75-run_len | 0.9151785714285714 (615 / 672) | false |
| fused-p75-run_sum | 0.9151785714285714 (615 / 672) | false |
| fused-p90-run_len | 0.6875 (462 / 672) | false |
| fused-p90-run_sum | 0.6875 (462 / 672) | false |

No arm is INERT, so all six are in the `--pair` set. The Step 4 agreement check printed
`AGREE_EXIT=0`.

## 7. Scored table (`report.txt`)

The baseline is `mp-baseline.chunks.trec`: nDCG@10 **0.2933**, R@50 **0.5443**, AP **0.2143**,
α-nDCG@10 **0.3552**.

Each Δ is the fused run minus the baseline, from paired tests. p is the permutation p (10,000 sign
flips, seed 20260831). "Holm" is the Holm-adjusted p over the 6 declared pairs.

| Arm | nDCG@10 | Δ | perm p | Holm | R@50 | Δ | Holm | AP | Δ | Holm | α-nDCG@10 | Δ | Holm |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| fused-p50-run_len | 0.2604 | −0.0330 | 0.0002 | **0.0012** | 0.5443 | +0.0000 | 1.0000 | 0.1929 | −0.0214 | 0.0012 | 0.3214 | −0.0339 | 0.0012 |
| fused-p50-run_sum | 0.2604 | −0.0330 | 0.0002 | **0.0012** | 0.5443 | +0.0000 | 1.0000 | 0.1929 | −0.0214 | 0.0012 | 0.3214 | −0.0339 | 0.0012 |
| fused-p75-run_len | 0.2785 | −0.0148 | 0.0002 | **0.0012** | 0.5443 | +0.0000 | 1.0000 | 0.2054 | −0.0090 | 0.0012 | 0.3377 | −0.0175 | 0.0012 |
| fused-p75-run_sum | 0.2785 | −0.0148 | 0.0002 | **0.0012** | 0.5443 | +0.0000 | 1.0000 | 0.2054 | −0.0090 | 0.0012 | 0.3377 | −0.0175 | 0.0012 |
| fused-p90-run_len | 0.2883 | −0.0050 | 0.0240 | **0.0480** | 0.5443 | +0.0000 | 1.0000 | 0.2114 | −0.0030 | 0.1404 | 0.3486 | −0.0067 | 0.0540 |
| fused-p90-run_sum | 0.2883 | −0.0050 | 0.0240 | **0.0480** | 0.5443 | +0.0000 | 1.0000 | 0.2114 | −0.0030 | 0.1404 | 0.3486 | −0.0067 | 0.0540 |

nDCG@10 95% CIs: p50 [−0.0425, −0.0234]; p75 [−0.0214, −0.0081]; p90 [−0.0093, −0.0006]. Queries
changed at nDCG@10: 465 / 382 / 270 of 672.

For reference, `report.txt` also scores `mp-baseline.similar.trec` (SearchSimilar, not paired):
nDCG@10 0.2801, R@50 0.5176, AP 0.2025, α-nDCG@10 0.3427.

**Gate decision (spec § Gate, applied exactly):** all six arms are neither DEGENERATE (section 5)
nor INERT (section 6). None improves nDCG@10; all six are negative, and all six are significant
after Holm correction. **The verdict is NO-GO.** It is not VOID, because no arm is DEGENERATE or
INERT. α-nDCG@10 and AP are context only, and they agree in direction.

**The two score arms are the same ranking at each θ.** At every θ, `fused-<θ>-run_len` and
`fused-<θ>-run_sum` differ only in their TREC tag column. With the tag stripped, the files are
byte-identical: md5 of `(qid, doc, rank)` p50 `8d56902b82f9…`, p75 `45caa3f83d15…`, p90
`53caf55f1a23…`, and the same holds for the unfused `mp-<θ>-*.trec` legs. In `scores-<θ>.tsv` the
best-length run and the best-sum run are the same run for all 30,212 / 20,283 / 10,381 matched
candidates. Across candidates the two orders agree because the per-length `run_sum` bands do not
overlap. Every chunk in a run has θ < s ≤ 0.8813 (the largest `s` in `calibration.tsv`). So a
length-k run sums to at most 0.8813·k, and a length-(k+1) run to more than θ·(k+1). That guarantees
the length order only up to k ≤ θ/(0.8813 − θ): k ≤ 2 at p50, k ≤ 3 at p75, k ≤ 4 at p90. Beyond
that, the separation is a property of this data. At p50, for example, the largest length-3 sum is
2.4459 and the smallest length-4 sum is 2.6567. On another corpus or θ the two scores could diverge.
The tag-stripped md5s are `awk '{print $1,$3,$4}' <file> | md5sum`. The benchmark therefore measured three distinct rankings, not six. Holm over 6 tests is
more conservative than over 3, and it cannot change a result whose sign is negative. The guard's ρ
values differ between a θ's two arms because `run_len` is an integer score with many ties, which
changes Spearman's tied ranks even though the induced order is identical.

The harm shrinks as θ rises: −0.0330 at p50, −0.0148 at p75, −0.0050 at p90. It tracks how many
candidates the pattern leg touches (30,212 → 20,283 → 10,381).

## 8. Integrity

- **R@50** is 0.5443 on every fused run, equal to the baseline's 0.5443. Δ is +0.0000, with 0 of 672
  queries changed, for all six pairs. `[pool]` reports "set changed on 0" for all six.
- **`pattern/build.json`**: `baseline_composite` `ded69e9492bdc081` and 4 checks. `calibration`,
  `p50`, `p75` and `p90` each have `before` and `after` equal to `ded69e9492bdc081`.
- **`report.py` build lines**: all 8 scored runs (6 fused, `mp-baseline.chunks`,
  `mp-baseline.similar`) print `build ded69e9492bdc081`. There are **0** `BUILD MISMATCH` or
  `BUILD UNKNOWN` lines. All six fused `.meta.json` sidecars carry composite `ded69e9492bdc081`.
- **Structural**: every run has 33,600 rows, 672 distinct queries, 33,600 of 33,600 non-zero
  scores, 672 of 672 qrels queries covered, and no duplicate doc ids.
- **Exit codes**: calibrate `EXIT=0`; run p50, p75 and p90 each `EXIT=0`; `rrf_fuse.py` `EXIT=0`;
  agreement check `AGREE_EXIT=0`; `report.py` `EXIT=0`.

## 9. md5s

Run files (Step 4's `md5sum`):

```
36c70b4b583899ea5fc7aa0b63c8d9ad  runs/fused-p50-run_len.chunks.trec
1a0e9b7316fe96677174d319c23b5fbb  runs/fused-p50-run_sum.chunks.trec
a7de91058679af88f7e02b05fba581da  runs/fused-p75-run_len.chunks.trec
ca480b8ca8161ebae50f6699ac18e452  runs/fused-p75-run_sum.chunks.trec
81001157dc1da401ce4d1d683d21fa72  runs/fused-p90-run_len.chunks.trec
a57cf2db9decde7546a0fa875ed697eb  runs/fused-p90-run_sum.chunks.trec
999ab2cfcf93fd849a9634fea16d8ec8  runs/mp-baseline.chunks.trec
e16aed44501fc9da9bde2d6f632f9b40  runs/mp-baseline.similar.trec
a6c0bcccf8187654f420bfc06c14f36b  pattern/mp-p50-run_len.trec
454578c0dd8b42ed364d315f380bda94  pattern/mp-p50-run_sum.trec
efd179343cdd1c4b7fb049c9d1e19577  pattern/mp-p75-run_len.trec
2bad672710d41a840b78ee3958f45a5e  pattern/mp-p75-run_sum.trec
768a8fe657d56844a24d6c49546bf3a9  pattern/mp-p90-run_len.trec
eaa332f2f03e95389030bf279ca3576d  pattern/mp-p90-run_sum.trec
```

Supporting files:

```
027bebcb19d55e0550abb231f341ec9d  pattern/calibration.tsv
f0fd2d449200f7ff2675f202314f6433  pattern/scores-p50.tsv
48061e59e687152d82febc006c6d4c2c  pattern/scores-p75.tsv
2efe1b66a8a207cd921d084a12559f0e  pattern/scores-p90.tsv
74fd8b40a4ce433327395fafa4fd1bdc  pattern/theta.json
faa8224986b97a525c638f4669edcfce  pattern/build.json
c22c2c325b74412c924f70f98524d0cb  fused/inert.json
0829fd738da9d7c81514b33e96865670  fused/guard.json
bf1602dea236f9d2b54bf541cb3fc078  runs/mp-baseline.meta.json
fa811f4b15b6c94552f06f6ad92c6b92  report.txt
6ce335da5c88c0701bb3127fb3d2206e  phase0.md
6f12cf93f93cb40283ad4e31774be454  pattern/probe-b.json
```

## 10. Deviations from the spec

The plan's four deviations (user decisions while planning, 2026-09-28):

1. **Reading 3 has no new Api test.** The service never checks `order_by` against the schema
   (`ObjectSearchGrpcService.MatchPattern.cs:299`, `:304`, `:402`). The only membership check is the
   StarRocks builder's `RequireVisible` (`MatchRowsQueryBuilder.cs:29`, `:100-105`), and in
   `MatchPatternGrpcServiceTests` the store is a mock. The two existing tests that pin the two halves
   are cited instead (section 1).
2. **The calibration call is text-free.** It uses pattern `A`, `A AS TRUE`, ONE ROW PER MATCH, with
   measures `s = SIMILARITY(text, '<q>')` and `ci = chunk_index`, instead of `A+` ALL ROWS. The
   values are the same, with one row per chunk and no `text`. Probe A confirmed this live.
3. **Probe B's rule counts both passes.** Per-candidate is chosen only if both the calibration and
   the θ projections are ≤ 8 h. Here both exceeded it (12.21 h and 12.40 h).
4. **The baseline is recorded before the probes** (Task 4), because both probes need its candidates.

Changes made during execution (recorded in `phase0.md` and in the SDD ledger,
`.superpowers/sdd/2026-09-28-matchpattern-rrf-test-implementation-plan/progress.md`):

5. **Admin-automation client secret (fix round 1).** `bench-env.sh`'s hard-coded
   `IVERSON_CLIENT_SECRET` was stale against the stack's regenerated
   `IVERSON_ADMIN_AUTOMATION_CLIENT_SECRET`, so the baseline failed with `invalid_grant`. The state
   file `matchpattern-rrf-shell.sh` gained one line after it sources `bench-env.sh`. The line re-reads
   the secret from `Iverson.Server/.env` at each source and never stores the value. `bench-env.sh`
   itself is untouched.
6. **Tenant-admin password reset (fix round 2).** `benchmark-query` logs in as
   `iverson-loadtest-tenant-admin` with the dev-default password (`Iverson.LoadTest/Program.cs:66`).
   On the freshly wiped stack that user had a different password, so every query failed with
   "Authentication flow did not complete after 20 stages". With the user's approval, the password
   was reset to the dev default on the local stack only. There was no repository change. This is a
   pre-existing harness bug that surfaces only on a fresh stack.
7. **The pattern leg mints its own service token (fix round 3, commit `a819b2a4`).** The Python
   SDK's `IversonClientCredentials` minted without the `Host: authentik-server:9000` override, so the
   token's `iss` was `http://localhost:9000/`. That matches neither of the API's authorities, and
   Probe A's first call failed with `UNAUTHENTICATED` 401. `pattern_leg.py` now mints a
   client-credentials service token with the Host header and refreshes it at `expires_in` − 300 s.
   It attaches the token as a static bearer, following the conformance driver's pattern. The plan's
   embedded Task 3 code is superseded by the branch.

## 11. Known limits

- **Non-default limits.** `MaxExpressionLength` was 25,000 (default 1,000) and `MaxOutputRows`
  18,623 (default 10,000; exercised, because the whole-corpus shape was chosen). A caller on default
  limits could not issue 437 of these 672 queries (section 3). The whole-corpus shape used here is
  also unavailable on default limits. Its `limit` 18,623 exceeds the default `MaxOutputRows` 10,000,
  which is rejected with `InvalidArgument` (`ObjectSearchGrpcService.MatchPattern.cs:294-295`). A
  whole-corpus calibration call also emits 18,622 rows. A default-configured caller therefore needs
  per-candidate calls, projected at 12.2 h for calibration and 12.4 h per θ pass. The client-side
  fusion measured here is therefore unavailable, as written, to a default-configured deployment for
  most FreshStack queries.
- **Scope of the NO-GO.** It covers only these θ values (p50 `0.6597611904144287`, p75
  `0.7002342939376831`, p90 `0.7339564561843872`) and k = 60, unweighted RRF, on FreshStack-2048
  against the shipped `SearchChunks` ranking (`LambdaChunks` 0.70, multiplier 11). k, weights and θ
  were fixed in advance and not tuned. As measured, `run_len` and `run_sum` induce the same ranking at
  every θ, so the benchmark tested three distinct rankings.
- **The guard's accepted consequence** (spec, "Degeneracy guard"; user decision, CDR round 1 §3.1).
  No-run candidates are included at a floor score, so the threshold split (having any run above θ,
  which is by construction a max-chunk similarity above θ) counts as part of each arm. An arm whose
  only effect is that split correlates with max-chunk similarity by construction, and could be marked
  DEGENERATE. Here no arm reached |ρ| ≥ 0.95 (largest 0.9197297456406506), so the consequence did not
  decide any classification.
