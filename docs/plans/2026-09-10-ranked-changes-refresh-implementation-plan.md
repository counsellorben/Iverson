# Ranked-Changes Refresh Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-10-ranked-changes-refresh-design.md` (commit SHA: `10f6849`)

**Goal:** Turn `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` into a durable index whose staleness is mechanically countable, and correct the three claims verification found false.

**Architecture:** Two markdown files edited in place. §0 of the ranked-changes document becomes the durable index carrying an explicit coverage rule; the tiered items become the expiring half, with settled items collapsed to a verdict plus a pointer. Two wrong claims and one met blocker are corrected, two items are added, and two dangling citations in a live spec are repaired.

**Tech stack:** Markdown, `git`, and read-only shell (`ls`, `awk`, `grep`, `podman inspect`). No application code is touched; there is no build, test or lint step to run.

---

## Global Constraints

Copied from the source spec's §4 and §5. Every task holds to these:

- **Only two files may be modified**: `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` and `docs/specs/2026-09-10-multivector-rerun-design.md`. Nothing else.
- **No gate document is edited.** They are the records this index cites.
- **Items 12, 13 and 14 have their *text* corrected, but their *recommendations* are never executed.** Steps 6 and 7 fix false claims inside items 13 and 14; no branch is deleted, no superseded banner is added, no README is fixed. Item 12's three claims were verified still accurate (spec A22) and its text does not change.
- **The ranked-changes file is not renamed.** It is cited by path from at least ten other documents, several of them historical records.
- **Item numbering 1-14 is preserved.** External documents cite items by number. Additions take 15 and 16.
- **Critical-review files are never edited.** They describe the document as it stood at review time; a stale citation inside one is correct.

## File Structure

**Modify**
- `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` — the refresh target (Task 1).
- `docs/specs/2026-09-10-multivector-rerun-design.md` — two dangling citations repaired (Task 2).

Both files are already tracked by git, so `git add` needs no `-f`. (The `.gitignore:51` rule `**/docs/specs/` applies only to *new* files under that directory.)

**Create / Test:** none. A markdown refresh has no test target; Task 1 step 9 is the executable check that replaces one.

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and **not** re-verified here. The full table with evidence is spec §3, A1-A26. The items these tasks rest on directly:

| # | Fact | Evidence (from spec §3) |
|---|---|---|
| A1 | Target path and status-line text are as quoted | 22,933 B; line 3 reads `**Status: decision document, 2026-09-06.** Supersedes` |
| A2 | The target file is **tracked**, not gitignored — only `docs/` subdirectories are ignored | `git check-ignore -v` returns nothing |
| A4 | §0 has 9 data rows | 10 lines matching `^\| ` in §0, one being the header |
| A5 | 8 gate documents exist | `ls docs/plans/*GATE*.md` |
| A6 | The 4 missing gates state citable verdicts | tier1 `:189`; similar-centroid `:3`; chunk-coverage `:15`; phase2 `:17` |
| A9-A12 | Items 1-3's closure facts and the shipped λ values | GATE-tier1-defaults `:189-199`, `:226-236`; `VectorRankingOptions.cs:24-25,30` |
| A16 | Items 1, 2, 3 are all of Tier 1 | `### ` headings between the Tier 1 and Tier 2 headings |
| A17 | **False** — "no shipped default lacks evidence". The fusion triple's A-vs-B is undecidable here | Item 8: "cannot be decided by any corpus this project has" |
| A18-A20 | Items 15/16's factual claims | phase2 gate `:44`, `:68`, `:87`; `scripts/beta_invariant.py`; no λ in any `*.meta.json` |
| A22-A23 | Item 12's three claims hold; item 13's branch table matches git exactly | Verified branch-by-branch |
| A26 | **False** — item 14 names the wrong containers, item 9's blocker was met | `podman inspect`; the Tier 1 campaign swept α-nDCG@10 |

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time:

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | The target exists at exactly `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` | `ls`: 22,933 B |
| P2 | Command | The `docs/plans/*GATE*.md` glob resolves from the repo root, where step 9 runs | `ls docs/plans/*GATE*.md` → 8 files |
| P3 | Command | `awk '/^## 0\./,/^## Tier 1/'` selects §0 | Returns 20 lines beginning `## 0. Where things stand` |
| P4 | Command | `grep -o '2026-09-GATE-[a-z0-9-]*\.md'` matches the Record column's real format | Returns 4 distinct gate filenames today, including the digit-bearing `chunk-coverage-phase2` |
| P5 | Command | `grep -o '^### [0-9]*\.'` matches every item heading, including item 4's rewritten one | Returns `### 1.` … `### 14.`, contiguous |
| P6 | Command | **FALSE as first drafted** — `grep -n 'choice'` is case-sensitive and the document uses both casings (11 lowercase, 12 capitalised). Step 9c uses `grep -in` | `grep -c` on each casing |
| P7 | Command | `git add <path>` works without `-f` on both files | `git ls-files --error-unmatch` succeeds for both |
| P8 | Command | The repo's commit convention is `<area>: <description>`, and `ranked-changes:` is established | `5b5924d ranked-changes: close item 4 (multivector) — …` |
| P9 | Ordering | Step 3's collapse removes nothing step 4's Tier 1 sentence depends on | The sentence cites item 8, which is untouched |
| P10 | Ordering | Step 8's additions do not collide with existing numbering | P5: 1-14 contiguous, so 15 and 16 are free |
| P11 | Ordering | No step depends on an artifact produced outside this plan | All inputs are the two files plus read-only shell |
| P14 | Content | Item 14's container facts are **live system state**, not a static fact | `podman inspect` at spec-write time. Step 6 re-runs it rather than trusting the snapshot |
| P15 | Content | The `## Tier 1` heading text is unchanged by the edit, so step 9a's awk range still terminates correctly | Step 4 adds a sentence under the heading, not to it |
| P16 | Consumer impact | **FALSE** — `docs/specs/2026-09-10-multivector-rerun-design.md:7` and `:138` cite "§4 choice 1", a list deleted in `5b5924d`. Already dangling. Task 2 repairs them | `grep -rn "choice 1"` over `docs/` |
| P17 | Consumer impact | Items 5-14 are unaffected; nothing is renumbered | Additions take 15/16; no existing heading changes number |
| P18 | Consumer impact | **FALSE** — item 13's `chunk-size-512-experiment` row says "delete after item 1 is decided"; item 1 was decided 2026-09-07. Folded into step 7 | `grep -n "item [0-9]"` line 259 |

Three critical-review files also cite `§4 choice 1`. They are historical records of the document as it stood at review time and are deliberately **not** repaired (Global Constraints).

## Tasks

### Task 1: Refresh the ranked-changes document

**Files:**
- Modify: `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md`

**Interfaces:**
- Produces: item 4's collapsed form, which Task 2's replacement citation text refers to.

- [ ] **Step 1: Rewrite the status line (lines 3-7).** Replace `**Status: decision document, 2026-09-06.**` with a status that states the dual role, so a reader knows which half is current:

  > **Status: living index + decision record. Index (§0) reconciled 2026-09-10.** §0 is the durable half and is kept current: one row per gate document under `docs/plans/2026-09-GATE-*.md`, plus any non-gated work that produced a verdict. When a gate lands, its row is added there. The tiered items below are decision content and expire as they are settled; a settled item collapses to its verdict and a pointer to the gate that settled it.

  Keep the existing "Supersedes `docs/2026-08-28-…`" sentence and the paragraph naming the experiments this closes out.

- [ ] **Step 2: §0 — coverage rule, chunk-window correction, four new rows.**
  1. Add the coverage rule as a sentence directly under the `## 0. Where things stand` heading (same wording as the status line's rule).
  2. **Correct the existing chunk-window row.** Its Verdict/What-shipped cells assert the shipped 2048/1792 default is the one configuration with no measurement behind it. Rule 7.1 measured it and it passed. Rewrite those cells to say: best of the ablation at 512/448, **and the shipped 2048/1792 default has since been measured and passed** (rule 7.1, 2026-09-07); nothing shipped, by decision.
  3. Append these four rows, preserving the four-column shape:

  | Experiment | Verdict | What shipped | Record |
  |---|---|---|---|
  | Tier 1 retrieval defaults | Rule 7.1 **PASS**, chunk-window default STANDS; Rule 7.2 λ split per endpoint; Rule 7.3 FIRES → follow-up | `LambdaSimilar` 1.00 / `LambdaChunks` 0.70; the `.chunks.diversity.json` sidecar. Chunk-window defaults unchanged by decision | `2026-09-GATE-tier1-defaults.md` |
  | `SearchSimilar` centroid retrieval (rule 7.3 follow-up) | **FAIL** — head retrieval stands, `SimilarRetrievalVector` deleted; reconciled in the 2026-09-08 amendment | nothing; Task 1's wiring reverted | `2026-09-GATE-similar-centroid.md` |
  | Chunk-coverage signal, Phase 1 | **Phase 2 warranted** — 85.0 % of top-50 slots carry a tail; β calibrated | harness only (`beta_invariant.py` and the β ladder) | `2026-09-GATE-chunk-coverage.md` |
  | Chunk-coverage signal, Phase 2 | **NO β QUALIFIES** — significant negative on all five arms, not a null | nothing | `2026-09-GATE-chunk-coverage-phase2.md` |

- [ ] **Step 3: Collapse items 1-4.** Each becomes its heading, a verdict line, and a record pointer. Delete each item's "Choices, ranked" list and its "What the gate recorded" / "What is wrong" prose — the gate documents hold that. Add a premise-error line **only** to items 2 and 4.

  | Item | Heading suffix | Verdict line | Premise-error line |
  |---|---|---|---|
  | 1 | `— CLOSED 2026-09-07` | Rule 7.1 PASS on both halves; the chunk-window default STANDS. Spec §3.4 was not executed: the five client attribute defaults stay at 512 tokens / 64 overlap. Record: `docs/plans/2026-09-GATE-tier1-defaults.md` | none |
  | 2 | `— CLOSED 2026-09-07` | `LambdaSimilar` 1.00, `LambdaChunks` 0.70 shipped (`VectorRankingOptions.cs:24-25`). Record: `docs/plans/2026-09-GATE-tier1-defaults.md` | ⚠ This item called λ = 1.00 on `SearchChunks` a free win. It was an artifact of the harness's max-passage collapse, which reduces chunks to documents before scoring and so cannot see chunk-level MMR. Production `SearchChunks` returns the chunk list: at λ = 1.00 a request for ten chunks yields 5.2 distinct source documents instead of 7.2 on `fs-512`. |
  | 3 | `— RESOLVED 2026-09-08` | `VectorRanking:SimilarViaChunksTypes` ships the operator-configurable alternative (`VectorRankingOptions.cs:30`); the rule 7.3 follow-up gated FAIL and was reconciled. Records: `docs/plans/2026-09-GATE-tier1-defaults.md`, `docs/plans/2026-09-GATE-similar-centroid.md` | none |
  | 4 | `— CLOSED 2026-09-10` | NO-GO. The re-run removed both asymmetries and the layout is the same function as the control: at `params.exact` the arm ties it exactly. Record: `docs/plans/2026-09-GATE-multivector.md` and its two 2026-09-10 amendments | ⚠ This item asserted both asymmetries biased the control upward. After index equalisation the **arm** held the larger corpus fraction (1.93 % vs 1.25 %), so the retrieval-budget asymmetry favoured the arm. The index-state asymmetry was real and inert — equalising it reproduced the original gate to four decimal places. |

  Item 4 currently carries a ~30-line narrative from commit `5b5924d`; this step trims it to the two-line form.

- [ ] **Step 4: Tier 1 — keep the heading, add one sentence.** Under `## Tier 1 — production defaults that the evidence does not yet cover`, with all three items now collapsed, add:

  > **Empty.** Every production default this campaign identified as unmeasured has now been measured. The fusion triple's A-vs-B choice (item 8) is a separate case — not unmeasured but *undecidable* here, since no corpus this project has can judge recency.

  Do not delete the heading. An empty Tier 1 is the document's most useful single statement and is only visible if the heading survives to carry it.

- [ ] **Step 5: Item 9 — replace the met blocker.** Its closing sentence reads `**Choice:** stays deferred behind item 2's diversity measurement; when it is picked up it needs its own spec.` That measurement ran in the Tier 1 campaign (α-nDCG@10 swept on both FreshStack arms). Replace with a deferral resting on the grounds that still hold — the proto change across five clients, the authorization fork, and the second uncalibrated fusion — and note that the previously stated blocker was met on 2026-09-07.

- [ ] **Step 6: Item 14 — correct the container hazard.** First re-run the check, because this is live state rather than a static fact (P14):

  ```bash
  podman inspect iverson-postgres iverson-authentik-server iverson-api \
    --format '{{.Name}} :: {{index .Config.Labels "com.docker.compose.project.working_dir"}}'
  ```

  Rewrite the second bullet against what that prints. At spec-write time `postgres` and `authentik-server` were both created from main's path (`/home/ben/repositories/Iverson/Iverson.Server`) and the container created from a vanished worktree was **`iverson-api`**, from `.worktrees/chunk-coverage-phase2/Iverson.Server`. Keep the general rule — only single-service `--no-deps` actions are safe until the stack is recreated from main deliberately — and add why it matters for this container specifically: `iverson-api`'s composite is not reproducible from a deleted worktree. If the command's output disagrees with the above, write what it prints and note the change.

- [ ] **Step 7: Item 13 — one row added, one stale cell corrected.**
  1. Add a row for `.worktrees/tier1-retrieval-defaults`, still present although the work merged on 2026-09-07. Do not add `csr-remediation`, `embedding-model-configuration` or `admin-console-landing-page` — they are outside this item's "left by the experiments" scope.
  2. **Correct the `chunk-size-512-experiment` row (P18).** Its recommendation says "delete after item 1 is decided". Item 1 was decided on 2026-09-07, so the condition is met; restate it as such.

- [ ] **Step 8: Append items 15 and 16.**

  **15. A coverage term that is not a chunk count in disguise** — Tier 2. Phase 2 rejected `max_chunk + β·Σ(next 3)`, but its tail term correlates with tail *count* at 0.9970 over all 172,704 pooled pairs, and because β multiplies the whole term the degeneracy is β-invariant. What was falsified is count-weighted promotion of multi-chunk documents; a coverage term whose value does not collapse onto the count is untested. FreshStack-2048 and its snapshots are on disk and `beta_invariant.py` is built, so the open work is a term, not an instrument. Record this explicitly so the Phase 2 result is not mis-cited as "coverage was tried and failed" — a misreading `docs/plans/2026-09-GATE-chunk-coverage-phase2.md` itself warns against.

  **16. `benchmark-query`'s run sidecar records no λ** — Tier 4. Attesting which λ a past run used needs a four-part reconstruction: descending-order violation counts, distinct-parent means against the spec's table, rank-1 identity across queries, and MVID equality. A λ field in the sidecar retires all of it.

- [ ] **Step 9: Verify.** Run all three from the repo root; all three must pass before committing.

  ```bash
  D=docs/2026-09-06-ranked-changes-after-retrieval-experiments.md

  # (a) Coverage rule holds: gate files == DISTINCT gate files cited in section 0.
  ls docs/plans/*GATE*.md | wc -l                                    # expect 8
  awk '/^## 0\./,/^## Tier 1/' "$D" \
    | grep -o '2026-09-GATE-[a-z0-9-]*\.md' | sort -u | wc -l        # expect 8

  # (b) Item numbering contiguous 1..16, no duplicates, nothing renumbered.
  grep -o '^### [0-9]*\.' "$D" | grep -o '[0-9]*' | tr '\n' ' '      # expect 1..16

  # (c) No dangling reference to a choices list step 3 deleted.
  #     -i is required: the document uses both "choice" and "Choice" (P6).
  grep -in 'choice' "$D"
  ```

  For (c), inspect every hit: any surviving reference to a *numbered* choice ("choice 1", "choice 2 above", "Choices, ranked") inside or pointing at items 1-4 is a dangling reference and must be rewritten. Hits inside items 5-11 are expected and correct — those items keep their choice lists.

- [ ] **Step 10: Commit.**
  ```bash
  git add docs/2026-09-06-ranked-changes-after-retrieval-experiments.md
  git commit -m "ranked-changes: refresh into a durable index; correct item 14's container hazard"
  ```

### Task 2: Repair the dangling choice citations

**Scope note.** This task edits a file outside the source spec's §2 design body. It was added during plan-level verification, when P16 found the citations already broken, and approved by the user before this plan was written. The spec's §4 out-of-scope list does not cover it either way.

Item 4's ranked-choices list was deleted in `5b5924d`, leaving two live citations pointing at nothing (P16). Task 1's collapse makes the same shape permanent, so the citations are repaired to name the closed item and state the fact directly rather than by reference.

**Files:**
- Modify: `docs/specs/2026-09-10-multivector-rerun-design.md` (lines 7 and 138)

**Interfaces:**
- Consumes: item 4's collapsed form from Task 1 — the replacement text names its closure date.

- [ ] **Step 1: Repair the header citation (line 7).** Currently:

  ```
  **Ranked-changes item:** `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` §4 (Tier 2), choice 1.
  ```

  Replace the trailing `§4 (Tier 2), choice 1.` with `§4 — closed 2026-09-10 by this experiment.` The item no longer carries a numbered choice list, so the reference must be to the item.

- [ ] **Step 2: Repair the in-body citation (line 138).** Currently reads `Matching the control's *absolute* beam (`hnsw_ef` 250, ranked-changes §4 choice 1) was rejected: …`. Replace the parenthetical's `§4 choice 1` with `§4's original first choice` and keep the rest of the sentence unchanged — the rejection and its arithmetic are still correct, only the pointer is stale.

- [ ] **Step 3: Verify no dangling choice citation remains outside the historical records.**
  ```bash
  grep -rn "choice 1" --include='*.md' docs/ | grep -v criticalreviews
  ```
  Expect no hit that points at ranked-changes §4. Hits inside `docs/criticalreviews/` are historical records and are deliberately left alone.

- [ ] **Step 4: Commit.**
  ```bash
  git add docs/specs/2026-09-10-multivector-rerun-design.md
  git commit -m "multivector spec: repoint two citations at ranked-changes §4, whose choice list is closed"
  ```

## Tasks NOT in this plan

Inherited from the source spec's §4:

- **Re-ranking the survivors.** With Tier 1 empty and roughly two live experiment items left, the document's strength × impact ÷ cost ordering has almost nothing to order.
- **Editing any gate document.** They are the records this one indexes.
- **Acting on items 12, 13 or 14** — deleting branches, adding superseded banners, fixing the README's Ollama/nomic references. All stay recorded, not executed.
- **Updating the memory files** that cite this document.
- **Renaming the file.**

## Known issues inherited from spec

- **The maintenance rule in §2.2 is a convention, not a mechanism.** Nothing enforces that a future gate adds its row; the rule only makes the omission countable after the fact. A pre-commit check comparing gate-file count to gate-citing row count would enforce it and is not proposed here.
- **Item 10's documentation debt is recorded, not fixed.** `README.md:64` still describes embeddings as "Local text embeddings via Ollama (`nomic-embed-text`, 768 dims)"; the shipped default has been bge-base via TEI since Phase 2. Verified still true, left for item 10.
