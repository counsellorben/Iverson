# Coverage-Term Screen Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-10-coverage-term-screen-design.md` (commit SHA: `1d69836`)

**Goal:** Decide, offline against a dump that already exists, whether any candidate statistic carries coverage-direction relevance signal that the raw chunk count does not — and if none does, close ranked-changes item 15 for that direction.

**Architecture:** One new script reads the Phase 1 pool dump, joins it to document-level qrels through `keymap.json`, and runs seven candidates through two screens: degeneracy against the chunk count (label-free), then discriminability conditional on `max_chunk` in two label populations. Two fail-closed controls reproduce known figures before any candidate is interpreted. A gate-style verdict document records the outcome. No ranking term and no β are defined anywhere.

**Tech stack:** Python 3 with numpy 2.5.2 and scipy 1.18.1 from `~/repositories/iverson-benchmark-corpora/python-libs`; `tail_stats.py` for loading/joining/tail extraction; `report.py` for `holm_adjust`, `HOLM_ALPHA`, `PERMUTATION_SEED`, `PERMUTATION_RESAMPLES`; pytest.

---

## Global Constraints

Copied from the source spec. Every task holds to these:

- **No ranking term, no β, no calibration, no new corpus run.** This screen decides whether a term is worth specifying; it does not specify one.
- **The two harness controls are fail-closed.** If either fails to reproduce, the run exits non-zero and reports nothing else (spec §2.6).
- **The decision rule is pre-registered.** It must not be adjusted after seeing output. τ stays 0.95; the 0.90 degeneracy bar stays; the GO conjunction stays as written.
- **Three distinct populations — 172,704 pooled pairs, 33,600 top-50 slots, 92,758 multi-chunk pairs.** Each control is computed on the one it was recorded against; the screen itself runs on the 92,758.
- **Every candidate is oriented so higher = more coverage before any AUC is computed** (spec §2.3). The orientation is fixed from the hypothesis, not chosen after seeing which direction scores better.
- **The advantage CI is not a direction test.** Only the raw-AUC CI bears direction (spec §2.6).
- Read-only against every corpora path. Nothing under `~/repositories/iverson-benchmark-corpora/` is written or modified.

## File Structure

**Create**
- `Iverson.Server/Iverson.LoadTest/scripts/coverage_screen.py` — the screen: candidates, two screens, two controls, both populations.
- `Iverson.Server/Iverson.LoadTest/scripts/test_coverage_screen.py` — pytest over the pure functions.
- `docs/plans/2026-09-GATE-coverage-screen.md` — the verdict document (Task 2).

**Modify:** none. This plan is create-only.

## Inherited from spec

Verified by `thorough-brainstorming` and three rounds of `critical-design-review` at spec-write time; **not** re-verified here. Full table with evidence is spec §3, A1–A25. The facts these tasks rest on directly:

| # | Fact | Evidence (spec §3) |
|---|---|---|
| A1, A2 | The accepted dump has 369,600 data rows over 672 queries; `refused-2026-09-09T1007/` is banner-marked and never read | `# REFUSED RUN — DO NOT SCORE` |
| A3, A9 | 172,704 (query, doc) pairs — the gate's own scope | `resolve_by_doc` yields exactly that |
| A4, A17 | 92,758 multi-chunk / 79,946 single-chunk | matches `beta_invariant`'s check-1 and check-2 counts exactly |
| A5, A6 | `keymap.json` joins parentKey → docId with 0 unresolved | 6,000 entries, injective |
| A7, A8 | `qrels.trec` is the right label file; 10,874 of the multi-chunk pairs are judged (3,168 / 7,706) | 20,209 rows / 672 queries |
| A12, A23 | `report.py` exposes `HOLM_ALPHA`, `holm_adjust`, `PERMUTATION_SEED`, `PERMUTATION_RESAMPLES` | `report.py:113-115`, `:471` |
| A15, A18 | `tail_stats.py` provides the loaders and the tail definition | `load_keymap:42`, `load_hits:49`, `resolve_by_doc:116`, `tail_scores:133`, `scoped_to_run:171`, `TAIL_CAP = 3` at `:38` |
| A19 | The β = 0 run file exists and scopes to exactly 33,600 slots | `fs2048-b0.chunks.trec` |
| A20 | `AUC(count)` is 0.4707 judged / 0.5631 all-multi — **not 0.5** | the fact the whole screen-2 rule guards against |
| A22 | The permutation scheme has adequate cells | 56 (judged) / 60 (all-multi), 99.9 % of pairs in cells with ≥ 2 members and both labels |
| A24 | `mean_tail`'s divisor is never zero on the screen's population | minimum tail length is 1 |

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time:

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1–P3 | File path | None of the three files this plan creates already exists | `coverage_screen.py`, `test_coverage_screen.py`, `2026-09-GATE-coverage-screen.md` all absent |
| P4 | Command | `docs/plans/` is gitignored, so Task 2's commit needs `git add -f` | `.gitignore:49` `**/docs/plans/`. `scripts/` is tracked, so Task 1 needs no `-f` |
| P5 | Signature | `tail_stats.load_keymap(path)` returns a flat `{parentKey: docId}` dict | `tail_stats.py:42-47`, `json.load` of ingest's KeyMap format |
| P6 | Signature | `tail_stats.load_hits(path)` returns a list of `(queryId, parentKey, score)` — three-tuples, not four | `tail_stats.py:49-88`; `rank` is checked against file order, then dropped |
| P7 | Signature | **`tail_stats.resolve_by_doc(hits, keymap)` returns a TUPLE `(dict, unresolved_count)`**, not a bare dict | `tail_stats.py:116-131`. Unpacking it as a dict raises `AttributeError` — this exact mistake was made once during design verification |
| P8 | Signature | `tail_stats.tail_scores(scores)` returns `sorted(scores, reverse=True)[1:1+TAIL_CAP]`, empty for a single-chunk pair | `tail_stats.py:133-138` |
| P9 | Signature | `tail_stats.scoped_to_run(by_doc, run_by_query)` returns a flat dict keyed `(queryId, docId)` | `tail_stats.py:171-180` |
| P10 | Signature | `tail_stats.load_run_doc_ids(path)` returns `{queryId: [(docId, score), …]}` | `tail_stats.py:91-114`, `return dict(by_query)` |
| P12 | Code validity | **Importing `report.py` is side-effect free** — safe for `coverage_screen.py` to import | AST scan: 0 non-declarative top-level nodes; exactly one `if __name__ == "__main__"` guard |
| P13 | Command | `PYTHONPATH=~/repositories/iverson-benchmark-corpora/python-libs python3 -m pytest . -q` from `scripts/` is the project's invocation | 105 tests pass under it today |
| P14 | Command | A new script's commit message is `add <script>.py: <what it does>`, not an `<area>:` prefix | `git log` over `scripts/`: `add tail_stats.py: …`, `add beta_invariant.py: …`, `add multivector.py probe: …` |
| P17 | Code validity | `scipy.stats.mannwhitneyu(a, b).statistic / (len(a)*len(b))` is the AUC | reproduces `AUC(count)` = 0.4707 / 0.5631, independently matched by the design review's own implementation |
| P18 | Code validity | Decile strata from `np.quantile` + `np.digitize` are well defined on `max_chunk` — no empty or degenerate stratum | measured sizes 9275–9276 across all ten, zero empty |
| P19 | Code validity | The `(count, max-decile)` permutation grid matches A22 | `count` ranges 2–8 (7 distinct); 60 cells occupied on all-multi, exactly A22's figure |
| P15, P16 | Ordering | Task 2 consumes only Task 1's committed script; Task 1's controls compute without screen 2 | controls read the dump and the β = 0 run file only |
| P21 | Consumer impact | No name collision in `scripts/` | no existing `coverage*` module |

`mean_tail` makes the candidate table **seven** rows. Spec §2.2, §2.3 and §2.6 each say "six" in prose — stale wording left when `mean_tail` was added, which changes no rule (`m` is the number of screen-1 survivors, not the table's length). This plan implements the table.

## Tasks

### Task 1: `coverage_screen.py` and its tests

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/coverage_screen.py`
- Create: `Iverson.Server/Iverson.LoadTest/scripts/test_coverage_screen.py`

**Interfaces:**
- Produces: the script Task 2 runs, and its printed/JSON output, which is the verdict document's source.

- [ ] **Step 1: Candidate statistics as pure functions.** One function per candidate over a pair's chunk-score list, each returning the value **already oriented so higher = more coverage** (spec §2.3), so no later stage re-applies a sign:

  | name | value |
  |---|---|
  | `count` | `len(scores)` |
  | `tail_sum` | `sum(tail_scores(scores))` |
  | `mean_tail` | `sum(t)/len(t)` for `t = tail_scores(scores)` |
  | `norm_tail_sum` | `sum(s/max(scores) for s in tail_scores(scores))` |
  | `n_within_tau` | `sum(1 for s in scores if s >= 0.95*max(scores))` |
  | `gap_1_2` | `−(max − second)` |
  | `dispersion` | `−` population standard deviation of `scores` |

  Use `tail_stats.tail_scores` (P8) rather than re-deriving the slice. `TAU = 0.95` and the `0.90` degeneracy bar are module constants, not literals at their use sites.

- [ ] **Step 2: Screen 1 — degeneracy.** Spearman ρ of each candidate against `count` over the 92,758 multi-chunk pairs; disqualify at `|ρ| ≥ 0.90`. Also compute the ρ(τ) curve for `n_within_tau` at τ ∈ {0.90, 0.92, 0.94, 0.95, 0.96, 0.98} — spec §2.7 requires it in the verdict document. The curve is reported only; τ = 0.95 remains the operating value.

- [ ] **Step 3: Stratified AUC and advantage.** Stratify by `max_chunk` decile (`np.quantile` + `np.digitize`, P18), compute each candidate's AUC of relevant vs irrelevant within each stratum via `mannwhitneyu` (P17), pool weighted by stratum size, and report both the **raw AUC** and the **advantage over `count`** on the same pairs.

- [ ] **Step 4: Permutation and bootstrap.** One-sided permutation `p = P(advantage_perm ≥ advantage_obs)`, permuting the **candidate** within `(count, max-decile)` cells with labels fixed (P19), using `report.PERMUTATION_SEED` and `report.PERMUTATION_RESAMPLES`. 95 % CI by cluster bootstrap resampling **queries**, not pairs, same seed. The CI is computed for **both** the raw AUC and the advantage, since the GO rule tests a bound on each; the permutation p is computed for the **advantage** only, which is the quantity §2.6's Holm clause consumes.

- [ ] **Step 5: The two fail-closed controls.** Compute on their own populations (Global Constraints):
  1. Pearson `corr(tail_sum, tail_count)` over all **172,704** pooled pairs, where `tail_count = len(tail_scores(scores))`. Target ≈ 0.9970.
  2. `P(relevant | count)` over the β = 0 arm's **33,600** top-50 slots via `tail_stats.scoped_to_run` + `load_run_doc_ids` (P9, P10), unjudged scored irrelevant, `count` capped at 6+. Target 8.9 / 8.1 / 7.3 / 7.1 / 9.1 / 11.9 %.

  Both run before any candidate is interpreted; on mismatch `sys.exit` non-zero reporting nothing else.

- [ ] **Step 6: CLI and both populations.** Arguments for the dump, keymap, qrels and β = 0 run file. One pass computes both label populations — judged-only (10,874) and all-multi with unjudged scored irrelevant (92,758) — and applies the GO conjunction from spec §2.6, with Holm across the screen-1 survivors via `report.holm_adjust` and `report.HOLM_ALPHA`. Print every quantity spec §2.7 requires in the verdict document, so Task 2 has one source to transcribe from.

- [ ] **Step 7: pytest over the pure parts.** Hand-built score lists, no fixtures and no mocks, matching the `test_multivector.py` idiom. Cover: each candidate's value and orientation on a worked example; `mean_tail` on a two-chunk pair (tail length 1); `gap_1_2` and `dispersion` sign; stratified pooling weighting strata by size; the AUC identity on a tiny labelled set with a known answer. Do not test scipy itself.

- [ ] **Step 8: Run the script and confirm the two controls reproduce.** The controls gate every run by design (spec §2.6), so a normal invocation exercises them first; no separate flag is needed. Paste the actual control output into the task report. This is the step that proves the harness before any verdict exists.
  ```bash
  cd Iverson.Server/Iverson.LoadTest/scripts
  PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs \
    python3 coverage_screen.py --dump <path> --keymap <path> --qrels <path> --beta0-run <path>
  ```

- [ ] **Step 9: Run the suite and commit.**
  ```bash
  cd Iverson.Server/Iverson.LoadTest/scripts
  PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs python3 -m pytest . -q
  git add Iverson.Server/Iverson.LoadTest/scripts/coverage_screen.py \
          Iverson.Server/Iverson.LoadTest/scripts/test_coverage_screen.py
  git commit -m "add coverage_screen.py: does any candidate carry signal the chunk count does not"
  ```

### Task 2: Run the screen and write the verdict document

**Files:**
- Create: `docs/plans/2026-09-GATE-coverage-screen.md`

**Interfaces:**
- Consumes: `coverage_screen.py` from Task 1 and its output.

- [ ] **Step 1: Run the screen over both populations.** Capture the full output; it is the verdict document's only source. If either control fails, stop — the run reports nothing else by design, and that is itself the finding.

- [ ] **Step 2: Write the verdict document** at `docs/plans/2026-09-GATE-coverage-screen.md`, in the shape of `docs/plans/2026-09-GATE-chunk-coverage-phase2.md`. Spec §2.7 mandates all of: every candidate's degeneracy ρ; its raw AUC **and** advantage in both populations; **`AUC(count)` per `max_chunk` decile**; the **full ρ(τ) curve** for `n_within_tau`; and both control reproductions.

- [ ] **Step 3: Record the verdict and the two reporting classes.** GO/NO-GO per spec §2.6, remembering that a NO-GO closes item 15 **for the coverage direction** only. Then name, rather than folding into "no candidate passes":
  - **population-dependent** candidates — passing in one population but not the other, naming which bias each rides;
  - **anti-coverage** candidates — raw-AUC 95 % CI wholly below 0.5. Use the raw-AUC CI alone for this; the advantage CI is not a direction test.

  Also record whether the spec's stated prediction held (spec §2.3 already scores the screen-1 half; screen 2's half is decided by this run).

- [ ] **Step 4: Commit.**
  ```bash
  git add -f docs/plans/2026-09-GATE-coverage-screen.md
  git commit -m "add coverage-screen gate verdict"
  ```

## Tasks NOT in this plan

Inherited from the source spec's §4:

- **Family 2, coverage over distinct query aspects.** The dump records a parent key and a score per
  hit, with no chunk identity and no chunk vector, so aspect coverage cannot be reconstructed from
  it. Screening it needs a Qdrant read for chunk vectors or a fresh capture recording chunk ids, and
  a definition of "distinct aspect" that nobody has pinned down.
- **Any ranking term, any β, any calibration, any new corpus run.** This screen decides whether a
  term is worth specifying; it does not specify one.
- **A `matched / total chunks` density candidate.** It needs each document's total chunk count, which
  the dump does not carry.

## Known issues inherited from spec

- **Pooling bias is present in both label populations and cannot be removed by this screen.** The
  judged-only population contains exactly the pairs some earlier run surfaced; the all-pairs
  population treats 88.3 % unassessed pairs as negatives. Requiring agreement across both is a
  mitigation, not a fix — a candidate that rides a bias common to both would still pass.
- **A NO-GO closes item 15 for the coverage direction on FreshStack-2048 evidence alone**, at the 2048/1792 window with the
  shipped fusion triple. Phase 2's negative carries the same limitation; this screen inherits it and
  does not widen it.
