# Aspect-Coverage Oracle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-13-aspect-coverage-oracle-design.md` (commit SHA: `79bff76`)

**Goal:** Measure, by oracle, whether coverage over distinct query aspects has enough headroom on FreshStack-2048 to justify designing a ranking term — and record a GO/NO-GO against a pre-registered bar.

**Architecture:** One new stdlib-only harness script reorders an already-captured run into three oracle rankings (relevance-only, per-document aspect count, greedy α-discounted coverage) and writes them as TREC run files alongside two query-filtered qrels copies. The existing `report.py` then scores all four rankings and runs three declared pairs; the gated quantity is G − R. Nothing is re-retrieved and no server code is touched.

**Tech stack:** Python 3 stdlib for the instrument; `report.py` with `ir_measures` + `pyndeval` (from `~/repositories/iverson-benchmark-corpora/python-libs`) for scoring; pytest for the suite.

---

## Global Constraints

Copied verbatim from the spec. Every task holds to these.

- **α = 0.5**, **rel = 1**, **judged_only = False** — pyndeval's defaults, pre-registered in spec §2.4 so the greedy's optimum is not read against a different α afterwards. α is a module constant, **not** a CLI flag.
- **The materiality bar is 0.02 α-nDCG@10**, fixed before the measurement runs. **GO** iff **G − R ≥ +0.02** on the 672-query primary population **and** its 95 % CI lower bound > 0; **NO-GO** otherwise, including any point estimate below +0.02 whatever its p-value.
- **A − R is reported but does not gate.** **R − B is context only** and is not subject to the bar.
- **Every oracle run file carries a synthetic score strictly decreasing in its new rank order** — never B's original per-document score. The score column, not file order and not the rank column, determines the ranking that gets scored (spec §2.8, A19).
- **The population restriction filters the qrels, never the run files** (spec §2.5, A23).
- **No new statistics code is written.** Statistics are `report.py --pair`, invoked verbatim.
- **Nothing is re-retrieved**, no container is started, no collection is touched, no server code changes.

---

## File Structure

**Create**
- `Iverson.Server/Iverson.LoadTest/scripts/aspect_oracle.py` — builds the three oracle rankings, the two filtered qrels files, and the per-query summary. Stdlib only.
- `Iverson.Server/Iverson.LoadTest/scripts/test_aspect_oracle.py` — pytest suite over the module's pure functions.
- `docs/plans/2026-09-GATE-aspect-coverage-oracle.md` — the verdict document (Task 2).

**Modify**
- `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` — one row added to the §0 table (Task 2).

**Outputs (not in the repo)** — written to `~/repositories/iverson-benchmark-corpora/aspect-oracle-2026-09-13/`, following chunk-coverage Phase 2's convention: `oracle-R.trec`, `oracle-A.trec`, `oracle-G.trec`, `qrels.610.trec`, `qrels.nugget.610.trec`, `aspect-summary.tsv`, `report-primary.txt`, `report-610.txt`.

---

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and by three `critical-design-review` rounds. **Not re-verified here.** The spec's §3 table is authoritative; this is the working copy for subagents who read only the plan.

| # | Assumption | Evidence |
|---|---|---|
| A1 | The run is 672 queries × 50 documents | 33,600 lines; 672 distinct query ids |
| A2 | Depth is exactly 50 for every query | 33,600 / 672 = 50; no short lists |
| A3 | Run and nugget-qrels document ids share one id space | both are chunk ids `path_start_end` |
| A4 | `fs2048-b0.chunks.trec` is the β = 0 control | the coverage-screen gate names it "β = 0 run file (control 2)"; `fs2048-b0.meta.json` carries `beta = 0` |
| A5 | `report.py --nugget-qrels` adds α-nDCG@10 | `report.py:833`; measure appended at `:911` |
| A6 | `pyndeval` and `ir_measures` import under the python-libs PYTHONPATH | `alpha_nDCG@10` constructs |
| A7 | `freshstack_nugget_qrels.py` emits the file `--nugget-qrels` consumes | `freshstack-2048-2026-09-07/qrels.nugget.trec`, 64,539 rows |
| A8 | **Document relevance is exactly the nugget union** — R, A and G promote an identical set | 5,445 / 5,445, zero difference either direction |
| A9 | Judged coverage is partial, worst where it matters | 25.9 % overall; **51.4 %** within the top 10 |
| A10 | `alpha_nDCG` scores an unjudged document as zero gain, not as unknown | `judged_only` default `False`; `alpha` 0.5; `rel` 1 |
| A11 | `report.py` scores a TREC run file offline | `score_run` at `report.py:358` |
| A12 | The reranker precedent is an oracle-reorder ceiling of the same construction | ceiling 0.9216, A0 0.6960, A1 0.7043 → +0.0083 = 3.7 % of ceiling |
| A13 | All three declared pairs clear the 25 % reorder threshold | R vs B 88.2 %; A vs R 43.8 %; G vs R 48.2 % |
| A14 | `--pair` Holm-corrects across exactly the declared pairs | `run_pair_statistics` at `report.py:769`; `--pair` at `:852` |
| A15 | The pool check requires identical per-query document sets | `check_pool` at `report.py:732`; `POOL_MIN_REORDERED_FRACTION` at `:700` |
| A16 | 62 queries (9.2 %) have no relevant document in their 50 and contribute 0 to every delta | counted over `qrels.trec` ∩ the run's per-query lists |
| A17 | Aspect counts are near-binary | 57.1 % / 27.2 % / 3.8 % at 1 / 2 / 4+; 17.9 % of judged top-10 at 2+ |
| A18 | Nothing in the ranked-changes doc already answers this question | §15 is the mechanical candidate screen (closed NO-GO) |
| A19 | The **score column** determines the scored ranking, while `check_pool` reads file order | `read_trec_run` discards rank (`util.py:298-303`); re-sorts by score (`:229-241`); `ranked_doc_ids` reads file order (`report.py:721-730`) |
| A20 | Every run query is present in both qrels files | run ∖ each qrels = 0 over all 672; `pyndeval/__init__.py:93` scores only `if qid in self.qrels` |
| A21 | B's file order is score-descending; the scorer's tie-break diverges only where §2.3 does not gate | 672/672 descending; 99 tied-score queries; differs on 58/672, 7/672 in top 10 — touches R − B only |
| A22 | Under the α-discounted objective the prefix restriction is a no-op | greedy over all 50 ≡ greedy-in-prefix + B-order tail, 672/672 |
| A23 | The averaged population is fixed by the **qrels**, not the run | `providers/base.py:22-27` back-fills `measure.DEFAULT = 0.0`; observed 3-query/2-scored case gives 0.6667 vs 1.0 when the qrels are restricted instead |

---

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | `scripts/aspect_oracle.py` does not yet exist | `[ -e ]` → absent |
| P2 | File path | `scripts/test_aspect_oracle.py` does not yet exist | `[ -e ]` → absent |
| P3 | File path | `docs/plans/2026-09-GATE-aspect-coverage-oracle.md` does not yet exist | `[ -e ]` → absent |
| P4 | File path | The ranked-changes doc exists and carries the §0 table Task 2 appends to | 22 table rows present |
| P5 | File path | The output directory does not yet exist | `[ -e ]` → absent |
| P6 | File path | `report.py` is at `Iverson.Server/Iverson.LoadTest/scripts/report.py` | present, 47,023 b |
| P7 | Convention | The repo's existing TREC-writer score convention is `float(len(docids) - i)` | `test_report.py:30` writes `{qid} Q0 {docid} {i + 1} {float(len(docids) - i):.6f} {tag}` |
| P8 | Convention | Harness test files are stdlib-only and import the module via `sys.path.insert` | `test_beta_invariant.py:6` "No non-stdlib imports -- nothing needs PYTHONPATH"; `:13` `sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))` |
| P9 | Format | A TREC run line is 6 whitespace-separated columns `qid Q0 docid rank score tag` | `read_trec_run` unpacks exactly `query_id, iteration, doc_id, rank, score, tag = line.split()` |
| P10 | Format | Both qrels files are 4-column `qid iteration docid rel`, so the 610 filter is a line-level query-id filter on each | `qrels.trec` and `qrels.nugget.trec` rows both split to 4 fields |
| P11 | Command | `python3 -m pytest <path> -q` is the suite convention and pytest is installed | pytest 9.1.1; docstring convention in `test_beta_invariant.py:3` |
| P12 | Command | `report.py` takes repeatable `--run` and `--pair RUN=BASELINE`, and `--pair` excludes `--baseline` | `--pair` at `report.py:852`; `report.py:885-886` exits if both given |
| P13 | Command | `report.py` needs `PYTHONPATH=~/repositories/iverson-benchmark-corpora/python-libs` | `ir_measures` is not importable without it |
| P14 | Convention | Commit messages are lowercase imperative with no Conventional-Commits prefix | `git log --oneline -8`: "add …", "applied N fixes from …" |
| P15 | Ordering | Task 2 depends only on Task 1's script; Task 1 depends on nothing later | Task 1 creates two files that nothing else in the plan modifies |
| P16 | Ordering | `check_pool` runs for **every** declared pair before **any** statistic is computed | `report.py:783-784` — the `for run_path, baseline_path in pairs: check_pool(...)` loop completes before `baseline_runs` is built |
| P17 | Code validity | The 610-filtered nugget qrels still passes `check_nugget_qrels_structure` | the guard exits only when `len(iterations) <= 1` (`report.py:336`); the filtered file carries **1,950** distinct nugget ids over 59,051 rows |
| P18 | Code validity | "≥ 1 relevant document in their 50" selects exactly 610 queries | counted over the real run and qrels: 610 (= 672 − 62, agreeing with A16) |
| P19 | Code validity | `report.py` emits one `[scores]` block per `--run` given, so passing all four yields four | `report.py:915-917` loops `for path in run_paths: score_run(...)` |
| P20 | Consumer impact | Adding a §0 row breaks no consumer — no code reads the ranked-changes doc | `grep -rln "2026-09-06-ranked-changes" --include=*.py --include=*.cs --include=*.sh` → no hits |
| P21 | Command | `docs/plans/` is gitignored so the gate doc needs `git add -f`; the scripts directory and the `docs/`-root ranked-changes doc are **not** ignored | `.gitignore:49` is `**/docs/plans/`; `git check-ignore -v docs/plans/<new>.md` → exit 0 matching that rule, while `Iverson.Server/Iverson.LoadTest/scripts/` matches no rule |

---

## Tasks

### Task 1: `aspect_oracle.py` and its pytest suite

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/aspect_oracle.py`
- Create: `Iverson.Server/Iverson.LoadTest/scripts/test_aspect_oracle.py`

**Interfaces:**
- Produces: the three oracle `.trec` files, the two filtered qrels files, and `aspect-summary.tsv` that Task 2 consumes.

- [ ] **Step 1: Write `aspect_oracle.py`.** Stdlib only (P8). Module docstring in the house style: what it does, the run command, and a pointer to `docs/specs/2026-09-13-aspect-coverage-oracle-design.md`.

  Module constant, with the reason attached — this is pre-registered, not tunable:

  ```python
  ALPHA = 0.5   # spec §2.4: pyndeval's default, pre-registered so the greedy's objective
                # and the metric's objective are the same constant by construction.
                # Deliberately NOT a CLI flag.
  ```

  Loaders: `load_run(path)` → `OrderedDict[qid, [docid, ...]]` in file order; `load_rel(path)` → `{qid: set(docid)}` over `rel > 0` rows; `load_nuggets(path)` → `{qid: {docid: set(nugget_id)}}` over `rel > 0` rows. All three read the 4-/6-column formats of P9/P10.

  One shared tie-break helper, used by all three rankings so "in B's order" means one thing:

  ```python
  def b_index(docs):
      """Position in B, the tie-break every ranking uses for "in B's order"."""
      return {d: i for i, d in enumerate(docs)}
  ```

  The three rankings, each taking `(docs, rel, nug)` for one query and returning a permutation of `docs`:

  - `rank_R` — relevant documents in B's order, then the rest in B's order.
  - `rank_A` — relevant documents by descending `len(nug[d])`, ties by `b_index`, then the rest in B's order.
  - `rank_G` — the α-discounted greedy. Write it exactly as below; the objective is where round 2 of the design review found the defect, and the count-greedy that looks equivalent is not:

  ```python
  def rank_G(docs, rel, nug):
      """Spec §2.2. Gain is the alpha-DISCOUNTED marginal gain, NOT the count of
      not-yet-covered nuggets: a nugget already covered by k appended documents is
      still worth (1 - ALPHA) ** k, and the two objectives coincide only at alpha = 1.
      Runs until the relevant prefix is exhausted -- there is no termination clause."""
      order = b_index(docs)
      remaining = [d for d in docs if d in rel]
      seen = collections.Counter()
      out = []
      while remaining:
          best = max(remaining, key=lambda d: (
              sum((1 - ALPHA) ** seen[n] for n in nug.get(d, ())),
              -order[d],
          ))
          out.append(best)
          for n in nug.get(best, ()):
              seen[n] += 1
          remaining.remove(best)
      return out + [d for d in docs if d not in rel]
  ```

  The TREC writer, reusing the repo's existing score convention (P7) rather than inventing one:

  ```python
  def write_trec(path, ranking, tag):
      """Score is strictly decreasing in the NEW rank order (spec §2.8 / A19): the scorer
      re-sorts by score and ignores the rank column, so a file carrying B's original scores
      would be silently re-sorted back into B. Same shape as test_report.py:30."""
      with open(path, "w", encoding="utf-8") as f:
          for qid, docs in ranking.items():
              for i, docid in enumerate(docs):
                  f.write(f"{qid} Q0 {docid} {i + 1} {float(len(docs) - i):.6f} {tag}\n")
  ```

  `filter_qrels(src, dst, keep)` — line-level copy keeping rows whose first field is in `keep` (P10). `select_610(run, rel)` — the query ids with at least one relevant document **in their 50** (P18).

  `summary_rows(...)` → one row per query: relevant count in 50, distinct nuggets, and nuggets reachable in the top 10 under each of B, R, A, G.

  `main()` with argparse: `--run`, `--qrels`, `--nugget-qrels`, `--out-dir`, all required. Create `--out-dir` if absent. Write the three run files (tags `oracle-R`, `oracle-A`, `oracle-G`), the two filtered qrels files, and the summary TSV. Print the counts written.

- [ ] **Step 2: Write `test_aspect_oracle.py`.** Pytest, stdlib only, module imported via the `sys.path.insert` shape at `test_beta_invariant.py:13`. Build one small hand-written fixture — a handful of queries covering: a query with **no** relevant documents; **tied** aspect counts resolved by B's order; and a document whose nuggets are **already covered** by an earlier pick. Cover:

  - `rank_R`, `rank_A`, `rank_G` each return a permutation of the input (same multiset, same length).
  - All three place exactly the relevant documents in the prefix, and the identical set — spec §2.2's load-bearing property.
  - `rank_G`'s first pick maximises α-discounted gain, and on the already-covered fixture it differs from what a not-yet-covered **count** greedy would pick. Assert the specific ordering, not just that one exists.
  - `rank_G` on a query whose nuggets are all covered early still orders the remaining relevant documents by discounted gain rather than stopping.
  - `write_trec` emits strictly decreasing scores within every query, and 6 columns per line.
  - `filter_qrels` keeps exactly the requested query ids and drops every other row.
  - `select_610` returns only queries with a relevant document inside the run's list, not merely somewhere in the qrels.

- [ ] **Step 3: Run the suite.**
  ```bash
  python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_aspect_oracle.py -q
  ```
  All tests must pass before committing.

- [ ] **Step 4: Commit.**
  ```bash
  git add Iverson.Server/Iverson.LoadTest/scripts/aspect_oracle.py Iverson.Server/Iverson.LoadTest/scripts/test_aspect_oracle.py
  git commit -m "add aspect_oracle.py: three oracle rankings over the FreshStack-2048 run"
  ```

### Task 2: Run the measurement and record the verdict

**Files:**
- Create: `docs/plans/2026-09-GATE-aspect-coverage-oracle.md`
- Modify: `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` (one row in the §0 table)

**Interfaces:**
- Consumes: Task 1's `aspect_oracle.py` and the six files it writes.

- [ ] **Step 1: Build the rankings.**
  ```bash
  python3 Iverson.Server/Iverson.LoadTest/scripts/aspect_oracle.py \
    --run ~/repositories/iverson-benchmark-corpora/chunk-coverage-phase2-arms-2026-09-09/fs2048-b0.chunks.trec \
    --qrels ~/repositories/iverson-benchmark-corpora/freshstack-2048-2026-09-07/qrels.trec \
    --nugget-qrels ~/repositories/iverson-benchmark-corpora/freshstack-2048-2026-09-07/qrels.nugget.trec \
    --out-dir ~/repositories/iverson-benchmark-corpora/aspect-oracle-2026-09-13
  ```
  Confirm 610 queries in the filtered qrels (P18) before continuing.

- [ ] **Step 2: Score the primary population (672) and run the three declared pairs.**
  ```bash
  export PYTHONPATH=~/repositories/iverson-benchmark-corpora/python-libs
  OUT=~/repositories/iverson-benchmark-corpora/aspect-oracle-2026-09-13
  ARMS=~/repositories/iverson-benchmark-corpora/chunk-coverage-phase2-arms-2026-09-09
  QRELS=~/repositories/iverson-benchmark-corpora/freshstack-2048-2026-09-07
  python3 Iverson.Server/Iverson.LoadTest/scripts/report.py \
    --run "$ARMS/fs2048-b0.chunks.trec" --run "$OUT/oracle-R.trec" \
    --run "$OUT/oracle-A.trec" --run "$OUT/oracle-G.trec" \
    --qrels "$QRELS/qrels.trec" --nugget-qrels "$QRELS/qrels.nugget.trec" \
    --pair "$OUT/oracle-G.trec=$OUT/oracle-R.trec" \
    --pair "$OUT/oracle-A.trec=$OUT/oracle-R.trec" \
    --pair "$OUT/oracle-R.trec=$ARMS/fs2048-b0.chunks.trec" \
    | tee "$OUT/report-primary.txt"
  ```

- [ ] **Step 3: Apply the writer's self-check BEFORE reading any other figure.** Spec §2.8: R − B must be non-zero on this corpus. If the `R vs B` block reports `delta +0.0000` with `0 / 672` queries changed, the score column still encodes B — that is a writer bug in Task 1, **not** a result. Fix Task 1 and re-run; do not proceed to the verdict. A non-zero `[pool] sequence differs` line does **not** clear this check: the pool check reads file order and cannot see the score column (A19).

  Second self-check, from spec §2.4: **R@50 must be identical across all four `[scores]` blocks.** The pools are identical by construction, so any difference means a run file lost or gained a document — a writer bug, not a result.

- [ ] **Step 4: Score the 610-query sensitivity.** Same three pairs, the same **unfiltered** run files, and the two **filtered** qrels files — filtering the run files would silently reproduce the 672 figure (A23).
  ```bash
  python3 Iverson.Server/Iverson.LoadTest/scripts/report.py \
    --run "$ARMS/fs2048-b0.chunks.trec" --run "$OUT/oracle-R.trec" \
    --run "$OUT/oracle-A.trec" --run "$OUT/oracle-G.trec" \
    --qrels "$OUT/qrels.610.trec" --nugget-qrels "$OUT/qrels.nugget.610.trec" \
    --pair "$OUT/oracle-G.trec=$OUT/oracle-R.trec" \
    --pair "$OUT/oracle-A.trec=$OUT/oracle-R.trec" \
    --pair "$OUT/oracle-R.trec=$ARMS/fs2048-b0.chunks.trec" \
    | tee "$OUT/report-610.txt"
  ```
  Confirm the `[compare]` blocks print `/ 610`, not `/ 672`. A `/ 672` denominator means the restriction did not take.

- [ ] **Step 5: Write the verdict document** at `docs/plans/2026-09-GATE-aspect-coverage-oracle.md`, following the gate-document convention. It records: the inputs and their provenance; the four `[scores]` blocks; all three `[pool]` lines; **G − R, A − R and R − B** each with delta, 95 % CI, paired *t* p, permutation p, Holm-adjusted p and queries-changed; the 610-query sensitivity for the same three; and one line **GO** or **NO-GO** applying the Global Constraints' rule verbatim — GO iff G − R ≥ +0.02 on the 672 primary with CI lower bound > 0. State explicitly that A − R is reported and does not gate, and that R − B is context. State that the figures are transcribed from `report-primary.txt` / `report-610.txt` in the corpora directory, which is not a git repository.

- [ ] **Step 6: Add the §0 row** to `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` — Experiment / Verdict / What shipped / Record, matching the 14 existing rows' shape. "What shipped" is `harness only (aspect_oracle.py)` on a NO-GO.

- [ ] **Step 7: Commit.** `-f` is required because `docs/plans/` is gitignored (`.gitignore:49`, P21); it is a no-op on the ranked-changes doc, which sits at the `docs/` root and is not ignored.
  ```bash
  git add -f docs/plans/2026-09-GATE-aspect-coverage-oracle.md docs/2026-09-06-ranked-changes-after-retrieval-experiments.md
  git commit -m "record the aspect-coverage oracle verdict and its ranked-changes row"
  ```

---

## Tasks NOT in this plan

Inherited verbatim from the spec's "Out of scope". A new spec → plan cycle is required to add any of these.

- **Designing the term itself.** This spec measures a ceiling. If the ceiling clears the bar, the term is
  a separate design.
- **Any server change.** No `VectorRankingOptions` constant, no ranking code, no configuration.
- **Re-retrieval.** No container is started, no collection is touched, nothing is re-embedded. Every input
  is already on disk.
- **The 512 corpus and every other corpus.** FreshStack is the only corpus in this project with nugget
  qrels, and FreshStack-2048 is the window both chunk-coverage phases and the coverage-term screen ran on.
- **Improving judgment coverage.** The 48.6 % unjudged share in the top 10 is a property of FreshStack,
  not something this measurement fixes.

## Known issues inherited from spec

These exist in the measurement by design — accepted by the user during brainstorming.

- **The ceiling is a ceiling on what α-nDCG can reward, not on true aspect coverage.** 48.6 % of top-10
  documents are unjudged and are scored as covering nothing. A real term might surface a document that
  genuinely covers a new aspect and be given no credit. The measurement is nonetheless the right one: a
  term shipped into this project would be evaluated by exactly this metric, so what it *could be rewarded
  for* is the operative bound.
- **The greedy oracle is greedy, not optimal.** Maximising α-nDCG@10 exactly is a set-selection problem;
  the α-discounted marginal-gain greedy of §2.2 is the standard approximation and is the construction
  α-nDCG's own ideal ranking is built with, so it optimises the same quantity the metric scores. A true
  optimum could still be marginally higher, which makes G − R a slight under-estimate of the ceiling —
  biased toward NO-GO, and stated so the result is not over-read.
- **One corpus, one window.** The result binds FreshStack-2048. Whether aspect-coverage headroom exists
  elsewhere is unmeasurable here, because no other corpus in this project carries subtopic labels.
- **The coverage-screen gate document lives on an unmerged branch** (`coverage-term-screen`), so the
  ranked-changes doc's §0 table has no row for it yet. That is that branch's business, not this spec's,
  but a reader following §0 alone will not find the NO-GO this spec builds on.
