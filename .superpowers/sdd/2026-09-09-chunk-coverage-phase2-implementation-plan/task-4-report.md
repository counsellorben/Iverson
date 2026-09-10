# Task 4 report: the gate document

**Status:** DONE
**Created:** `docs/plans/2026-09-GATE-chunk-coverage-phase2.md` (716 lines), committed with
`git add -f` (`.gitignore:49` is `**/docs/plans/`).

## Verdict recorded

**NO β QUALIFIES — and the result is a significant NEGATIVE, not a null.** All five non-zero arms are
negative on nDCG@10, monotone in β; the top three Holm-significant at p_adj = 0.0010. Written as a
finding under spec §5's opposite-conclusion rule, not as an absence of one. α-nDCG@10 negative at
every arm (−0.0020 → −0.0312), which realises the specific cost §5 said to watch. R@50 and AP
negative and significant at all five. Max-passage stands on positive evidence against the alternative.

## Every check recorded (spec §6)

1. **§4 invariant** — the three verbatim `invariant.txt` lines: 79,946 / 92,758 / 6.
2. **§1.1 `/build` certification** — verbatim `capture-certification.txt`.
3. **λ = 0.70 in-artefact evidence** — the review finding, addressed; see below.
4. **Label↔β binding by recomputation** — new; see below.
5. **Aggregator drift** (§8 assumption 21's free confirmation) — new; see below.
6. **Holm family size** — `[baseline] excluded` once, `Holm (5 tests)` on all 20 blocks.
7. Full `report.py` output spliced in verbatim from `report.txt`.

## Checks I ran myself for this document (all four λ checks verified, not copied)

Neither sidecar has a `lambda` field (l070 keys: `assemblies`, `chunkBudgetMultiplier`, `composite`,
`configLabel`, `queryCount`, `recordedAtUtc`, `reranker`), so λ was attested only by operator text
plus a container that will not survive. In-artefact evidence now recorded:

| Check | Result |
|---|---|
| (a) descending-order violations | l070 **183,792 / 368,928** (49.82 %), 672/672 queries; Phase 1 **0**, 0 queries |
| (b) mean distinct parents | l070 8.104166666666666 / 34.49702380952381 vs parent §4 λ0.70 row 8.10 / 34.50; Phase 1 6.665178571428571 / 28.81547619047619 vs λ1.00 row 6.67 / 28.82 |
| (c) rank-1 identity | score **bit-identical 672/672**, parent key 672/672; yet only 1,959/369,600 (0.53 %) rows match overall, 1,287/368,928 (0.35 %) below rank 1 — not a copied file |
| (d) assembly MVIDs | all **7/7** equal to Phase 1's, `composite` equal — same binary, independent of the certification text |

Two further checks, added because they close gaps the brief flagged:

- **Recomputation excludes a label↔β swap.** Each arm's `scores.tsv` reproduced bit-exactly
  (172,704/172,704, max err 0.000e+00) from the Phase 1 dump + keymap under
  `descending[0] + β·Skip(1).Take(3).Sum()` at its *labelled* β; β solved from each arm's own numbers
  by least squares over the 92,758 multi-chunk pairs recovers the label to 9 dp. 1,036,224 scores.
- **Assumption 21's free confirmation.** Columns 1–5 of `fs2048-b0.chunks.trec` sha256-identical to
  *both* Phase 1's certified `identity-beta0` replay and its original run (`a4d6cb15…cf2d`), 0
  differing lines. Aggregator has not drifted.

## Overclaim guards written in explicitly

- The **79,946 + 92,758 = 172,704** partition proves the census and that β applied; it closes
  arithmetically for *any* non-zero β and says **nothing** about magnitude. Stated as such.
- **Check 3 is not a swap guard** — it compares a `Counter` multiset, so a permutation passes it
  unchanged. Stated as such, with the swap excluded by the recomputation above instead.
- **The λ = 0.70 capture was not consumed.** Spec §7 routes the confirmation arm to a β *qualifying*,
  which §1 fixes as a positive; this outcome does not un-defer it. Recorded plainly as insurance taken
  under uncertainty that did not pay out, with what it leaves banked (both λ items are now offline
  replays costing minutes, never 46 minutes again).
- The λ evidence is scoped: it pins the arm to parent §4's λ = 0.70 row and excludes rebuild, swapped
  index, dropped setting and re-labelled λ = 1.00 run — **not** that the env var read exactly `0.70`.

## Scope limits carried (spec §9)

Negative scoped to the **550-chunk budget** *and* the **shipped 0.45/0.45/0.10 fusion triple**; λ =
1.00 removes diversification, not the weights. **Tail cap of 3 binds on 50.5 % of ranked slots — the
majority, not rarely** (parent §9's contrary claim stays retracted), noted as cutting *for* the
finding. Deep-tail evidence 512-window only; `SearchSimilar` transfer unmeasured.

## Numbers audit

All 27 four-decimal figures quoted in the document's prose verified present verbatim in `report.txt`
(or are Phase-1/spec-external constants: `s`, ladder βs, ×1.8031, parent §4's table). All derived
percentages recomputed. Nothing rounded from the artefacts except where the artefact itself is
rounded; the diversity means are at full recorded precision.

## Scope notes

Did not run `benchmark-query`, `benchmark-aggregate` or `report.py`. Docker touched read-only
(`docker ps -a`, `docker inspect`) to record that the currently-running `iverson-api` is the λ = 0.70
recreate (`VectorRanking__LambdaChunks=0.70`, `RestartCount=0`, created 2026-09-09 21:55:28 EDT) —
i.e. the λ = 1.00 container is gone, as spec §1.1 predicted. Postgres untouched.

## Concerns

1. **`task-1-report.md` is stale** — it still reads `STATUS: IN PROGRESS` with Steps 5 and 6 marked
   pending, although the run completed and `capture-certification.txt` exists. Not my task to edit,
   but it will misread on any later review of this branch.
2. **`benchmark-query`'s sidecar records no λ.** The gate document recommends adding
   `lambdaChunks`/`lambdaSimilar` to it. Until then every λ arm needs §3's four-check reconstruction.
