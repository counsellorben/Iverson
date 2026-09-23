# Refreshing the ranked-changes document into a durable index — design

**Date:** 2026-09-10
**Status:** approved, not executed.
**Target:** `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` (tracked; `docs/` root is not gitignored).

## 1. The problem

The target document is used as an index. Memory records the rule as *"this doc is the index of what has
already been measured — grep it before proposing any retrieval experiment"*, and a false claim in it has
already cost real work: a four-arm NFCorpus λ sweep was specced on the premise that NFCorpus had never been
swept, which the document itself would have disproved.

But it was **built** as something else. Its own header says *"Status: decision document, 2026-09-06"*, and it
supersedes a predecessor the same way. A decision document is meant to expire — you read it once and act. An
index has to stay current. Used as the second while built as the first, it went stale within four days: of
its fourteen items, three of the four Tier 1/2 experiment items were settled by gates that landed on
2026-09-07 and 2026-09-08, and the document still presents all three as open.

Two of its claims are now not merely stale but wrong, and one of those is a safety hazard (§2.6).

## 2. Design

One file changes. Nothing else in the repository is touched.

### 2.1 Identity and header

The path stays. The date in the filename becomes date-of-origin, not date-of-currency, because the file is
cited by path from at least ten other documents — three gate documents, four critical reviews, two
implementation plans and its own superseded predecessor. Several of those are historical records that must
not be rewritten to chase a rename.

The status line changes to state three things the current one does not: that §0 is the durable index and is
current; that the tiered items are decision content which expires; and the maintenance rule in §2.2.

### 2.2 §0 becomes the index

`## 0. Where things stand` keeps its four columns (Experiment | Verdict | What shipped | Record) and gains an
explicit coverage rule:

> One row per gate document under `docs/plans/2026-09-GATE-*.md`, plus any non-gated work that produced a
> verdict. When a gate lands, its row is added here.

The rule makes staleness mechanically detectable: count the gate files, then count the **distinct** gate
files cited by rows. Today that is 8 gate files against 9 existing rows, of which 5 cite a gate — covering 4
distinct files, since `embedding-migration` is cited twice, by the chunk-window row and by the Phase 1 row —
and 4 are non-gated pre-gate results recorded in `docs/centroid-weighting-proposal.md`. The four rows added
below bring distinct gate files cited to 8, matching the file count.

Four rows are added, for the gates that landed after this document was written:

| Experiment | Verdict | What shipped | Record |
|---|---|---|---|
| Tier 1 retrieval defaults | Rule 7.1 **PASS**, chunk-window default STANDS; Rule 7.2 λ split per endpoint; Rule 7.3 FIRES → follow-up | `LambdaSimilar` 1.00 / `LambdaChunks` 0.70; the `.chunks.diversity.json` sidecar. Chunk-window defaults unchanged by decision | `2026-09-GATE-tier1-defaults.md` |
| `SearchSimilar` centroid retrieval (rule 7.3 follow-up) | **FAIL** — head retrieval stands, `SimilarRetrievalVector` deleted; reconciled in the 2026-09-08 amendment | nothing; Task 1's wiring reverted | `2026-09-GATE-similar-centroid.md` |
| Chunk-coverage signal, Phase 1 | **Phase 2 warranted** — 85.0 % of top-50 slots carry a tail; β calibrated | harness only (`beta_invariant.py` and the β ladder) | `2026-09-GATE-chunk-coverage.md` |
| Chunk-coverage signal, Phase 2 | **NO β QUALIFIES** — significant negative on all five arms, not a null | nothing | `2026-09-GATE-chunk-coverage-phase2.md` |

### 2.3 Settled items collapse

Items 1-4 collapse to a verdict line plus a record pointer. The detail already lives in the gate documents;
duplicating it here makes the document grow as work gets *done*, which is backwards for a lookup target.

Each settled item keeps a second line **only** where the item's own reasoning turned out wrong. That is the
document's error record and exists nowhere else — a gate document records what was measured, not which prior
argument the measurement demolished.

| Item | Collapses to | Premise-error line? |
|---|---|---|
| 1. Chunk window never measured | CLOSED 2026-09-07 — rule 7.1 PASS on both halves, default STANDS, spec §3.4 not executed | no |
| 2. λ is one knob for two RPCs | CLOSED 2026-09-07 — `LambdaSimilar` 1.00, `LambdaChunks` 0.70 shipped | **yes** — "λ = 1.0 on `SearchChunks` is a free win" was an artifact of the harness's max-passage collapse; production returns the chunk list, where λ 1.00 costs 7.2 → 5.2 distinct parents on `fs-512` |
| 3. `SearchSimilar` embeds only the first 512 tokens | RESOLVED 2026-09-08 — `SimilarViaChunksTypes` shipped; rule 7.3 follow-up gated FAIL and reconciled | no |
| 4. Multivector asymmetries | CLOSED 2026-09-10 — NO-GO, layout is the same function as the control | **yes** — the item asserted both asymmetries biased the control upward; after index equalisation the **arm** held the larger corpus fraction (1.93 % vs 1.25 %) |

This trims item 4 from the ~30-line narrative it was given earlier on 2026-09-10.

### 2.4 Tier 1 stays, empty, with one sentence

The heading is kept rather than deleted, because an empty Tier 1 is itself the document's most useful
statement. The sentence is narrowed from the obvious phrasing, which verification falsified (§3, A17):

> Empty. Every production default this campaign identified as unmeasured has now been measured. The fusion
> triple's A-vs-B choice (item 8) is a separate case — not unmeasured but *undecidable* here, since no corpus
> this project has can judge recency.

### 2.5 New items

**15. A coverage term that is not a chunk count in disguise** (Tier 2). Phase 2 rejected
`max_chunk + β·Σ(next 3)`, but its tail term correlates with tail *count* at 0.9970 over all 172,704 pooled
pairs, and because β multiplies the whole term the degeneracy is β-invariant. What was falsified is
count-weighted promotion of multi-chunk documents; a coverage term whose value does not collapse onto the
count is untested. FreshStack-2048 and its snapshots are on disk and `beta_invariant.py` is built, so the
open work is a term, not an instrument. Recorded so the Phase 2 result is not mis-cited as "coverage was
tried and failed" — a misreading the gate document itself warns against.

**16. `benchmark-query`'s run sidecar records no λ** (Tier 4). Attesting which λ a past run used currently
needs a four-part reconstruction (descending-order violation counts, distinct-parent means, rank-1 identity,
MVID equality). A λ field in the sidecar retires it.

### 2.6 Corrections that verification forced

**Item 14's compose hazard names the wrong containers, and the error is unsafe.** It states that the live
containers were created from worktree paths that no longer exist and that a tier-wide `up` would recreate
`postgres` and `authentik-server`. Both of those were in fact created from main's path
(`/home/ben/repositories/Iverson/Iverson.Server`). The container created from a vanished worktree is
**`iverson-api`**, from `.worktrees/chunk-coverage-phase2/Iverson.Server`. As written the hazard points a
reader at two safe containers and away from the one that matters. A critical review recorded this on
2026-09-09 and the document was never corrected. The bullet is rewritten against the observed state, with the
general rule kept: only single-service `--no-deps` actions are safe.

**Item 9's stated blocker has been met.** It defers multi-property vector search "behind item 2's diversity
measurement"; that measurement ran in the Tier 1 campaign, which swept α-nDCG@10 on both FreshStack arms. The
deferral stands on its other grounds — a proto change across five clients, an authorization fork, a second
uncalibrated fusion — but the gating sentence is false and is replaced.

**§0's existing chunk-window row is stale.** It asserts the shipped 2048/1792 default is "the one
configuration with no measurement behind it". Rule 7.1 measured it and it passed. The row is corrected rather
than left to be contradicted by the Tier 1 row added in §2.2.

### 2.7 Items 12 and 13 refreshed

Both are lists of stale things, so a stale entry in either is the worst case for this document. Verification
found item 12's three claims all still accurate and item 13's branch table exact — every "N ahead" count
matches `git rev-list`, and all eleven branches still exist. Neither table's content changes.

Item 13 gains the one worktree that has entered its scope since: `.worktrees/tier1-retrieval-defaults`,
still present although the work merged on 2026-09-07. The three worktrees outside its scope
(`csr-remediation`, `embedding-model-configuration`, `admin-console-landing-page`) are not added.

No action is taken on either item. They are recorded, not executed.

## 3. Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| A1 | Path and status-line text are as quoted | `ls`: 22,933 B; line 3 reads `**Status: decision document, 2026-09-06.** Supersedes` |
| A2 | **FALSE** — the file is gitignored | `git check-ignore -v` returns nothing. `docs/` root is tracked; only its subdirectories are ignored. No `-f` needed for this file |
| A3 | §0 is a 4-column table | Header row `\| Experiment \| Verdict \| What shipped \| Record \|` |
| A4 | §0 has 9 data rows | 10 lines matching `^\| ` in §0, of which 1 is the header |
| A5 | 8 gate documents exist | `ls docs/plans/*GATE*.md` — 8 files, named in §2.2 |
| A6 | The 4 missing gates state citable verdicts | tier1 `:189` PASS/STANDS; similar-centroid `:3` FAIL; chunk-coverage `:15` "Phase 2 is warranted"; phase2 `:17` "NO β QUALIFIES" |
| A7 | Existing rows' Record paths resolve | 4 cite gate files that exist; 5 cite `centroid-weighting-proposal.md`, present |
| A8 | 9 existing + 4 new leaves no gate unrepresented | 8 gates ↔ 8 gate-citing rows after the addition |
| A9 | Item 1 closed by rule 7.1, spec §3.4 not executed | `2026-09-GATE-tier1-defaults.md:189-199` — both CI lower bounds clear −0.02; "**Spec §3.4 does NOT execute**" |
| A10 | Shipped λ values are 1.00 / 0.70 | `VectorRankingOptions.cs:24-25` |
| A11 | The "free win" premise is contradicted by the gate | `2026-09-GATE-tier1-defaults.md:226-236` names item 2 of this document and calls the free win "an artifact of the measurement" |
| A12 | Item 3 resolved by `SimilarViaChunksTypes` + the 7.3 follow-up | `VectorRankingOptions.cs:30`; `2026-09-GATE-similar-centroid.md:3` |
| A13 | Item 3's "Update 2026-09-08" holds no unique fact | Its content (byte-identical routed run, 672 queries, artifact paths) is in `freshstack-2048-2026-09-07/` and the similar-via-chunks plan |
| A14 | Item 4's text carries the premise-error note | Present in the 2026-09-10 rewrite, commit `5b5924d` |
| A15 | No document cites items 1-4 in a way a collapse breaks | Citations are by number (`§3`, `§4`, `item 1`, `item 2`); numbers are preserved |
| A16 | Items 1, 2, 3 are all of Tier 1 | `### ` headings between `## Tier 1` and `## Tier 2` |
| A17 | **FALSE** — "no shipped default lacks evidence" | Item 8: the fusion triple's "A vs B on real timestamps cannot be decided by any corpus this project has". Sentence narrowed in §2.4 |
| A18 | Phase 2 records the count degeneracy and does not test a non-degenerate term | `2026-09-GATE-chunk-coverage-phase2.md:44` (count-weighted promotion), `:68` (corr 0.9970), `:87` (β-invariance) |
| A19 | fs-2048 corpus, snapshots and `beta_invariant.py` are on disk | `freshstack-2048-qdrant-snapshots/` 3 files; `scripts/beta_invariant.py` 13,116 B |
| A20 | The run sidecar records no λ | `grep -l 'lambda\|Lambda'` over `freshstack-2048-2026-09-07/runs/*.meta.json` — no match |
| A21 | Numbers 15 and 16 are free | 14 `### N.` headings |
| A22 | Item 12's three claims still hold | Empty-chunk-guard spec has no banner (`head -5`); the 2026-08-28 doc's status line points here; `centroid-weighting-proposal.md:795` still says "still unaddressed in the harness" |
| A23 | Item 13's branch table matches git | All 11 branches exist; ahead-counts 9/6/2/5/1 and five at 0 match the table exactly |
| A24 | Nothing parses the document programmatically | No `.py`/`.sh`/`.cs` file references it |
| A25 | Items are cited by number, not title | Retitling settled items is safe |
| A26 | **FALSE** — untouched items 5-11, 14 carry no falsified claim | Item 14 names the wrong containers (`podman inspect`: postgres and authentik-server both from main's path; `iverson-api` from the deleted `.worktrees/chunk-coverage-phase2/`); item 9's stated blocker was met by the Tier 1 campaign. Both corrected in §2.6 |

## 4. Out of scope

- **Re-ranking the survivors.** With Tier 1 empty and roughly two live experiment items left, the document's
  strength × impact ÷ cost ordering has almost nothing to order.
- **Editing any gate document.** They are the records this one indexes.
- **Acting on items 12, 13 or 14** — deleting branches, adding superseded banners, fixing the README's
  Ollama/nomic references. All stay recorded, not executed.
- **Updating the memory files** that cite this document.
- **Renaming the file.** §2.1.

## 5. Known issues accepted as out of scope

- **The maintenance rule in §2.2 is a convention, not a mechanism.** Nothing enforces that a future gate adds
  its row; the rule only makes the omission countable after the fact. A pre-commit check comparing gate-file
  count to gate-citing row count would enforce it and is not proposed here.
- **Item 10's documentation debt is recorded, not fixed.** `README.md:64` still describes embeddings as
  "Local text embeddings via Ollama (`nomic-embed-text`, 768 dims)"; the shipped default has been bge-base
  via TEI since Phase 2. Verified still true, left for item 10.
