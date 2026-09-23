# Popularity Measurement — Phase 0 and Phase 1 Prerequisites Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-13-popularity-signal-measurement-design.md` (commit SHA: `80ab176dc2e23b0ee0d8841ef8033f17187e4a75`)

**Goal:** Produce the cheap evidence Phase 0 exists to buy — a complete citation count + date fetch, the two AUC estimators of measurement 1 (the pool-matched one and the retrieved-corpus one, which are different comparisons and are labelled as such), and the age control's descriptive half — plus the two Phase 1 parts whose shape does not depend on the arm structure Phase 0 selects.

**Architecture:** Four sequential scripts-and-data tasks — **run them 1 → 2 → 3 → 4**. Task 1 completes the fetch; Task 2 measures on it and writes the gate doc; Task 3 checks the Phase 1 identity's divisor precondition against restored Qdrant snapshots and appends to that gate doc; Task 4 implements the piecewise identity as a reusable re-ranker, whose divisor Task 3's result determines. No server code changes, no api, no arms. The ordering is recorded in assumption 22 — under one-subagent-per-task dispatch it is the only thing that establishes it.

**Tech stack:** Python 3 (the `Iverson.LoadTest/scripts/` convention), Semantic Scholar graph API, Qdrant (Task 3 only), TREC run files scored by the existing `report.py`.

---

## Scope

This plan covers **Phase 0 in full** plus the **two arm-independent parts of Phase 1** (the divisor prerequisite and the piecewise re-ranker).

It deliberately excludes Phase 1's measurements 2, 3, 4c and 4d (the ceiling and both nulls), because the spec states they "belong to whichever arm structure Phase 0's read selects" — that selection has not been made. It also excludes all of Phase 2, which runs only if Phase 1 clears its gate.

## File Structure

**Create**
- `Iverson.Server/Iverson.LoadTest/scripts/fetch_citations.py` — the Semantic Scholar fetcher, relocated from untracked scratch and converted to argparse
- `Iverson.Server/Iverson.LoadTest/scripts/popularity_triage.py` — Phase 0's three measurements
- `Iverson.Server/Iverson.LoadTest/scripts/popularity_rerank.py` — the piecewise identity, arm-independent
- `docs/plans/2026-09-GATE-relation-popularity.md` — the gate doc (`docs/plans` is gitignored; every commit of it needs `git add -f`)

**Data — untracked, never committed**
- `scratchpad/popularity/citations.json` — the fetch output
- `scratchpad/popularity/divisor-exceptions.txt` — Task 3's docIds lacking `body_centroid`, one per line; empty when the count is zero

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and across two CDR rounds. **Not re-verified here.** In particular: A32 (the run file and qrels exist and match their stated shapes across two different roots), A30/A31 (the shipped transform and the decay mechanism), A39 (a candidate with no popularity value keeps `fused_new = fused_old`), A40 (the truncated pool overstates the ceiling), A34 (fetch cost arithmetic), and the §3.1 stratum rule fixed in measurement 4c.

One inherited item does **not** reach as far as this plan needs. Spec A8 argues the archived run's score column is the fused score from `LambdaSimilar = 1.00`, but that λ was set after the run was recorded, so the argument does not cover this build. Plan-level assumption 30 carries the replacement mechanism, which holds at every λ.

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | `Iverson.Server/Iverson.LoadTest/scripts/` is where experiment analysis scripts live | directory exists and holds `aspect_oracle.py`, `beta_invariant.py`, `multivector.py`, `sample_corpus.py`, `report.py`, `ingest.py` |
| 2 | File path | `scratchpad/popularity/fetch_citations.py` exists and is the file being moved | exists; 4,940 bytes |
| 3 | File path | `scratchpad/popularity/citations-countonly.json.bak` exists and must NOT be reused | exists; top-level keys are `['counts','unresolved']` only — no `years`/`dates` |
| 4 | File path | `docs/plans/2026-09-GATE-relation-popularity.md` does not exist; Task 2 creates it | absent from `docs/plans/` |
| 5 | Command | `docs/plans` is gitignored **for files inside it**, so every gate-doc commit needs `git add -f` | `.gitignore:49` is `**/docs/plans/` — note the trailing slash: `git check-ignore docs/plans` (bare dir) does **not** match, while `git check-ignore docs/plans/2026-09-GATE-relation-popularity.md` matches `.gitignore:49`. Checking the bare directory is misleading |
| 6 | File path | `scratchpad/` is **not** gitignored, only untracked | `git check-ignore scratchpad/popularity/citations.json` → no match. So the data file must be kept out of commits by never `git add`-ing it, not by relying on ignore rules |
| 7 | File path | Phase 1's inputs exist at two different roots | `iverson-benchmark-corpora/scifact-2048-2026-09-06/runs/sci-2048.similar.trec`, `iverson-benchmark-data/scifact-full/qrels/test.tsv`, `iverson-benchmark-data/scifact-full/corpus.jsonl` — all present |
| 8 | File path | The Qdrant snapshots and their restore procedure exist | `scifact-2048-qdrant-snapshots/RESTORE.md` present, plus both `.snapshot` files; it delegates the restore loop to `scifact-512-qdrant-snapshots/RESTORE.md`, which also exists |
| 9 | Code validity | `body_centroid` is a **named vector**, not a payload field — Task 3's check queries vectors | `ingest.py:656` `object_vectors["body_centroid"] = centroid`; `:838` declares it in the collection's vector config alongside `body_vector` |
| 10 | Code validity | The fetcher's cache shape is `{counts, years, dates, unresolved}`, with `years` and `dates` separately guarded | `fetch_citations.py:33` default, and `:61-71` — `counts` is set when `citationCount is not None`, `years` only when `year is not None`, `dates` only when `publicationDate` is truthy |
| 11 | Consumer impact | Moving the fetcher requires changing it: `CORPUS` and `CACHE` are hardcoded absolute `/home/ben/...` paths | `fetch_citations.py:13-14`. A committed script with a personal absolute path is broken in any other checkout, and every sibling in `scripts/` is argparse-driven — so the move converts both to flags |
| 12 | Consumer impact | Nothing outside the fetcher references its scratchpad path | `grep -rn "scratchpad/popularity"` across `*.py`/`*.md`/`*.cs` (worktrees and review files excluded) returns exactly one hit: the fetcher's own `CACHE` constant at `:14` |
| 13 | File path | The two new script names are free | no `popularity_triage.py` or `popularity_rerank.py` in `scripts/` |
| 14 | Signature | Script convention: shebang, docstring naming the spec, a `Run with:` usage block, and the outputs listed | `aspect_oracle.py:1-18` — `#!/usr/bin/env python3`, purpose, "See docs/specs/…", `Run with:` block, "Writes, into --out-dir:" |
| 15 | Signature | Failure convention: `sys.exit`, not printed warnings, with mandatory non-zero assertions | `beta_invariant.py:1-20` — "fails loudly -- sys.exit, not a printed warning"; "Zero pairs asserted is a failure of the precondition, not a pass" |
| 16 | Signature | There is no existing AUC helper to reuse; the triage script introduces one | `grep -rlniE "auc" scripts/*.py` → no files. `report.py` exposes `holm_adjust` (`:471`) and `per_query_values` (`:489`) only |
| 17 | Signature | `report.py` parses TREC generically, so Task 4's output is scoreable without changes to it | `report.py:267` `fields = line.split()` is the **structural check**, not the scorer; the scorer is `ir_measures.read_trec_run`. Verified at run tier instead: a 15,000-row run in Task 4's output shape scored clean through `report.py`, exit 0. See assumption 29 for the invocation it requires |
| 18 | Code validity | The fetcher already requests the fields Phase 0 needs | `fetch_citations.py:21` `'?fields=citationCount,year,publicationDate'` |
| 19 | Command | A clean fetch is 52 batches | 5,183 corpus ids (`wc -l corpus.jsonl`) at 100 per batch (`:48` `range(0, len(todo), 100)`) → 52; `BATCH_GAP` is 3.0 s anonymous / 1.2 s keyed (`:28`) |
| 20 | Command | `S2_API_KEY` is not set in this environment, so the anonymous pace applies unless supplied | `S2_API_KEY` unset in the shell; no reference in shell profiles |
| 21 | Command | Commit messages are lowercase imperative with no Conventional-Commits prefix | `git log --oneline -20`: "add three missing CSP directives…", "close csr round-3 finding #15…", "fuse the decayed recency count…" |
| 22 | Ordering | **Order is 1 → 2 → 3 → 4.** Task 2 consumes Task 1's `citations.json`; Task 3 appends to the gate doc Task 2 Step 3 creates; Task 4's 0.90 divisor is conditional on Task 3's count being zero | Task 2's measurements all read counts/dates. Task 3's *measurement* reads Qdrant only, but its **Files** block is `Modify:` the gate doc, which assumption 4 verifies is absent until Task 2 creates it. Task 4 reads a run file and a counts file — and `ResultReranker.cs:37-38` seeds `weightTotal = WBase = 0.45`, with `:40-45` adding `WCentroid` only under `hasCentroid`, so a candidate missing `body_centroid` fuses at 0.45 and Task 3 is what establishes whether any exists |
| 23 | Ordering | No task introduces a symbol another task imports | all four produce standalone scripts or documents; none imports another |
| 24 | Code validity | The piecewise identity matches both the spec and the shipped reranker | spec's identity section (both branches); `ResultReranker.cs:53-57` adds `WPopularity` to `weightedSum` **and** `weightTotal` under one `hasPopularity` guard |
| 25 | Code validity | The 5-year stratum rule is total over what the fetch emits | four classes are possible per id — resolved with `publicationDate`, resolved with `year` only, resolved with neither, unresolved. The rule assigns the first two to calendar strata, the third to the no-date stratum, and the fourth has no count so never enters the AUC population |
| 26 | Consumer impact | Adding files to `scripts/` collides with nothing and changes no build | the directory holds loose Python scripts with no `__init__.py` packaging and no csproj reference; `__pycache__` is the only non-`.py`/`.json` entry |
| 27 | Code validity | Measurement 1's population: **275 of 300** queries are eligible, and 253 of those carry exactly one in-pool relevant document | in-pool positives per query over the real run × qrels = `{0: 25, 1: 253, 2: 13, 3: 4, 4: 5}`. Restricted to the 275 eligible queries: `n_pos = 311, n_neg = 13,439`. **The two estimators pair differently** — the cross-product `311 × 13,439` = 4,179,529 pairs is estimator (i)'s population (99.64% of those pairs compare across queries), while the genuinely within-pool pairing is **15,129** pairs. (i)'s 4,325 distinct non-relevant documents span 83.4% of the corpus, which is why it is the retrieved-corpus form and why its Hanley–McNeil CI is anti-conservative. (Over all 300 queries the figures are 14,689 / 4,402; the eligible-restricted ones are the estimators' actual population) |
| 28 | Code validity | Only **283 distinct relevant documents** exist across the 5,183-document corpus, so degenerate 4b strata are likely rather than exotic | `scifact-full/qrels/test.tsv`: 339 judgments over 300 queries, all `score = 1`, 283 distinct relevant docs, 0 absent from `corpus.jsonl` |
| 29 | Command | `report.py` needs `PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs` **and** a 4-column TREC qrels | run tier, both directions observed. With the PYTHONPATH and `scifact-2048-2026-09-06/qrels.trec` → exit 0 (`nDCG@10 0.7450`, `R@50 0.9137`). Without the PYTHONPATH → exit 1, `could not import ir_measures`. With the PYTHONPATH but against `scifact-full/qrels/test.tsv` → exit 1, `ValueError: not enough values to unpack (expected 4, got 3)` in `ir_measures/util.py:284`. `report.py` is the one non-stdlib script in the directory (`report.py:71-78`) |
| 30 | Code validity | The archived run's score column is the **fused** score whatever λ was live for the build that produced it | `sci-2048.meta.json` records `recordedAtUtc 2026-09-07T04:46:40Z`, which predates `c27eb98` (2026-09-07 22:26 UTC) — so spec A8's `LambdaSimilar = 1.00` argument does **not** cover this build. The claim holds by a different mechanism: `ResultDiversifier.cs:72-75` computes the MMR objective but `:80` emits `new RerankedResult(ranked[index].Id, ranked[index].Score)` — the fused score — at every λ. Confirmed empirically: 0 non-monotone queries across all 15,000 rows of `sci-2048.similar.trec` and of `sci-2048.chunks.trec` |
| 31 | File path | `scifact-full/qrels/test.tsv` is a 3-column **header-bearing** BEIR TSV (`query-id\tcorpus-id\tscore`), which constrains Task 2's parser | parsed directly: header row present, 339 judgments, 300 queries, all `score = 1`, 283 distinct relevant docs. It is *not* the 4-column TREC shape `ir_measures` requires — which is why Task 4 scores against `scifact-2048-2026-09-06/qrels.trec`, whose relevant-doc set is set-equal to this file's 283 |

**Baseline:** none of the four artifacts exists yet, so there is no test count to hold. Each task's own assertions are its gate.

---

## Tasks

### Task 1: Complete the count + date fetch

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/fetch_citations.py`
- Data (never committed): `scratchpad/popularity/citations.json`

**Interfaces:**
- Produces: `citations.json` with `{counts, years, dates, unresolved}` — consumed by Task 2, and optionally by Task 4 for a smoke run.

- [ ] **Step 1: Move the fetcher into `scripts/` and convert its two hardcoded paths to flags.**

`fetch_citations.py:13-14` currently hardcodes `CORPUS = '/home/ben/iverson-benchmark-data/scifact-full/corpus.jsonl'` and `CACHE = '/home/ben/repositories/Iverson/scratchpad/popularity/citations.json'`. A committed script cannot carry a personal absolute path, and every sibling in `scripts/` is argparse-driven. Replace both with required flags — `--corpus` and `--cache` — keeping all other behaviour byte-identical, including the atomic `save()` and the `BATCH_GAP` pacing.

Give it the header shape the directory uses (`aspect_oracle.py:1-18`): shebang, a docstring stating what it fetches and why, a reference to `docs/specs/2026-09-13-popularity-signal-measurement-design.md`, a `Run with:` block, and the output shape.

- [ ] **Step 2: Move the stale count-only cache aside.**

```bash
mv scratchpad/popularity/citations-countonly.json.bak scratchpad/popularity/citations-countonly.json.superseded
```

Its top-level keys are `counts` and `unresolved` only — it carries no dates. It must not become `citations.json`: the fetcher's resume predicate is `known = set(cache['counts']) | set(cache['unresolved'])`, so every id in it would be treated as done and never acquire a date, silently hollowing out the age control for the first 27% of the corpus in corpus order. Renaming rather than deleting keeps the 1,288 counts available for cross-checking the new fetch.

- [ ] **Step 3: Run the fetch to completion.**

```bash
python3 Iverson.Server/Iverson.LoadTest/scripts/fetch_citations.py \
    --corpus /home/ben/iverson-benchmark-data/scifact-full/corpus.jsonl \
    --cache  scratchpad/popularity/citations.json
```

52 batches at 3.0 s apart is roughly 2.6 minutes. Export `S2_API_KEY` first if one is available — the script drops to 1.2 s per batch when it sees one.

- [ ] **Step 4: Assert the fetch is complete, and report the split.**

Every one of the 5,183 corpus ids must appear in `counts` or in `unresolved` — neither more nor fewer. Report four numbers for Task 2 to record: resolved count, unresolved count, how many resolved ids carry a `publicationDate`, and how many carry only a `year`. A resolved id with neither is the no-date stratum's membership and must be counted, not silently dropped.

- [ ] **Step 5: Commit the script only.**

```bash
git add Iverson.Server/Iverson.LoadTest/scripts/fetch_citations.py
git commit -m "move the semantic scholar citation fetcher into scripts and give it path flags"
```

`scratchpad/` is untracked but **not** gitignored, so `citations.json` is kept out of the commit by naming the script explicitly — never `git add -A`.

---

### Task 2: Phase 0's three measurements and the gate doc

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/popularity_triage.py`
- Create: `docs/plans/2026-09-GATE-relation-popularity.md`

**Interfaces:**
- Consumes: Task 1's `citations.json`.

- [ ] **Step 1: Write `popularity_triage.py`.**

Flags, matching the directory's convention: `--run`, `--qrels`, `--counts`, `--out-dir`.

Three measurements, all named by the spec:

1. **Two AUCs of citation count over the retrieved pools**, relevant vs non-relevant. **Two estimators are emitted**, because the aggregation and the standard error are not independent choices and the data makes them structurally different — and they are not two ways of computing one quantity, they are two different comparisons:
   - **(i) one pooled two-sample AUC** over `n_pos = 311, n_neg = 13,439` = 4,179,529 pairs from the eligible queries, with the Hanley–McNeil SE and a 95% CI. **This is not the pool-matched statistic.** It pairs each relevant document against *every* eligible query's non-relevant documents, not only its own pool's — 4,179,529 pairs against **15,129** within-pool ones, so 99.64% of them compare across queries. Its negative sample spans 4,325 of the corpus's 5,183 documents, which is what makes it the **retrieved-corpus comparison** and what makes it directly comparable to the 0.6201 already on record. Its CI is additionally **anti-conservative** and must be reported as such: those 4,325 distinct documents are reused across overlapping pools.
   - **(ii) the mean of the per-query AUCs**, with a CI from the between-query spread — queries, not pairs, as the independent units. **Estimator (ii) is the spec's measurement 1 — the pool-matched one**, comparing each relevant paper only against other papers in its own retrieved pool. Honest about the dependence, but 253 of the 275 eligible queries have exactly one in-pool relevant document, so most of its terms are single-positive rank statistics.

   The gate doc states which of the two the arm-structure read is taken on, and labels (i) as the retrieved-corpus form so it cannot be read as pool-matched. The 0.6201 on record is against *random corpus documents* — a weaker claim than (ii), and close in kind to (i).
2. **4a — AUC of publication date alone**, over the resolved-date population.
3. **4b — within-stratum AUC of citation count**, using measurement 4c's fixed strata: 5-year calendar bins on `publicationDate`, falling back to `year`; resolved ids with neither form their own stratum. Bins are **enumerated across the observed calendar range**, so an empty bin is visible in the output rather than absent from it.

   The reported 4b number is one **stratum-pooled Mann–Whitney statistic** — concordant pairs summed over within-stratum (relevant, non-relevant) pairs only, across all strata. This is defined whatever any single stratum contains: a stratum with no relevant or no non-relevant member contributes zero pairs rather than an undefined ratio. Alongside it the script emits a **per-stratum table** — size, relevant count, non-relevant count, and the stratum's own AUC where it has one — with every degenerate stratum marked as such.

   The pooled statistic is 4b; the table is what makes its composition auditable, and it is what measurement 4c's within-stratum permutation will need. Degeneracy is expected rather than exotic: only **283 distinct relevant documents** exist across the whole 5,183-document corpus, spread over however many bins the publication dates span.

Per `beta_invariant.py`'s discipline, the script fails loudly rather than scoring nothing. At minimum these are `sys.exit` conditions, not warnings: zero queries with at least one relevant *and* one non-relevant document in-pool (AUC is undefined); **zero within-stratum (relevant, non-relevant) pairs summed across all strata** (4b would be computed over nothing); and a resolved-count population of zero. A check that silently asserts nothing over its data is indistinguishable from one that passed.

Note which assertion this is *not*. "Any stratum that ends up empty" is the wrong condition under both halves of the construction above: with bins enumerated across the calendar range an empty bin is ordinary and would abort Phase 0 outright, and under the pooled statistic an empty stratum simply contributes no pairs. The load-bearing assertion is that the pooled pair count is non-zero.

It also emits the **corpus median citation count**, which is what fixes `SaturationPoint` — the spec pre-registers `S` as a rule, and this is the run that executes it.

- [ ] **Step 2: Run it.**

```bash
python3 Iverson.Server/Iverson.LoadTest/scripts/popularity_triage.py \
    --run    /home/ben/repositories/iverson-benchmark-corpora/scifact-2048-2026-09-06/runs/sci-2048.similar.trec \
    --qrels  /home/ben/iverson-benchmark-data/scifact-full/qrels/test.tsv \
    --counts scratchpad/popularity/citations.json \
    --out-dir docs/plans
```

- [ ] **Step 3: Write the gate doc's Phase 0 section.**

Create `docs/plans/2026-09-GATE-relation-popularity.md` with a Phase 0 section recording: the three measurements with their CIs; for measurement 1, **both** estimators with an explicit statement of which one the arm-structure read is taken on; for 4b, the pooled statistic **and** the per-stratum table with degenerate strata marked; the corpus median that fixes `S`; the resolved / unresolved split; the `publicationDate` / `year`-only / neither split; and the no-date stratum's size.

It reports numbers and draws no conclusion. The arm-structure choice is the reader's, and it has not been made.

- [ ] **Step 4: Commit.**

```bash
git add Iverson.Server/Iverson.LoadTest/scripts/popularity_triage.py
git add -f docs/plans/2026-09-GATE-relation-popularity.md
git commit -m "add the phase 0 popularity triage measurements and gate doc"
```

`git add -f` is required for the gate doc: `.gitignore:49` is `**/docs/plans/`.

---

### Task 3: Phase 1 prerequisite — is the divisor uniform?

**Files:**
- Modify: `docs/plans/2026-09-GATE-relation-popularity.md` (append a prerequisite subsection)
- Create (data, untracked): `scratchpad/popularity/divisor-exceptions.txt` — one docId per line, empty when the count is zero

**Interfaces:**
- Consumes: the gate doc created by Task 2 Step 3.
- Produces: the divisor-exception list — consumed by Task 4.

- [ ] **Step 1: Restore the snapshots.**

Follow `iverson-benchmark-corpora/scifact-2048-qdrant-snapshots/RESTORE.md`, which delegates its restore loop to `../scifact-512-qdrant-snapshots/RESTORE.md`. Both collections are needed: `benchmark_documents_tenant_bypass` and `benchmark_documents_chunks_tenant_bypass`. Qdrant only — the api is not involved.

- [ ] **Step 2: Count object points lacking `body_centroid`.**

`body_centroid` is a **named vector**, not a payload field (`ingest.py:656` writes it into `object_vectors`; `:838` declares it in the collection's vector config beside `body_vector`), so the check is on the point's vector set, not on payload keys.

The spec's identity assumes every candidate divides by 0.90, which holds only if every object point has this vector; `ingest.py` writes it only when at least one chunk vector has non-zero magnitude, and a document failing that is scored at `weightTotal = 0.45`.

- [ ] **Step 3: Record the result.**

Append a prerequisite subsection to the gate doc. If the gate doc does not yet exist, Task 2 has not run — **stop rather than creating it here**, so the Phase 0 section is not lost.

Zero means the identity is uniform and exact, and Task 4's re-ranker may assume the 0.90 divisor throughout. Non-zero means those specific documents take the 0.45 divisor and must be listed, so Phase 1 can handle them separately rather than mis-scoring them.

Record the exception set **as docIds, read from each point's `docId` payload key** (`ingest.py:660` writes `"docId": doc_id` into `object_payload`) — not as Qdrant point ids. Task 4 keys on TREC docids, and recovering a docid from a point id needs `key_to_ulong(uuid5(...))` plus `keymap.json`, which this plan's scope excludes. A docId-keyed list joins to Task 4's inputs directly.

Write those same docIds to `scratchpad/popularity/divisor-exceptions.txt`, **one per line**, and create the file empty when the count is zero. That file is the artifact Task 4's `--divisor-exceptions` consumes; the gate-doc subsection is the human-readable record of it. Task 4 runs in a separate subagent with no sight of this one's transcript, so the file is the only channel between them. It stays untracked — the same treatment `citations.json` gets, and the reason Step 4 below adds only the gate doc.

- [ ] **Step 4: Commit.**

```bash
git add -f docs/plans/2026-09-GATE-relation-popularity.md
git commit -m "record the phase 1 centroid-uniformity prerequisite result"
```

---

### Task 4: Phase 1 arm-independent — the piecewise re-ranker

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/popularity_rerank.py`

**Interfaces:**
- Consumes: a counts file of Task 1's shape (can be smoke-tested against the superseded partial cache).
- Consumes: Task 3's divisor-exception list, docId-keyed (empty when Task 3 reports zero).
- Produces: a TREC run file scoreable by the existing `report.py` — verified at run tier, see assumption 29 for the invocation.

- [ ] **Step 1: Write `popularity_rerank.py`.**

Flags: `--run`, `--counts`, `--w`, `--saturation`, `--out`, and `--divisor-exceptions <file>` (optional, default empty) — a text file of docIds, **one per line**, as written by Task 3 Step 3 to `scratchpad/popularity/divisor-exceptions.txt`; these are the documents Task 3 found to lack `body_centroid`.

It applies the spec's identity to one recorded run at one `(W, SaturationPoint)` cell, **both branches**:

```
pop = count / (count + SaturationPoint)

pop present:  fused_new = (0.90·fused_old + W·pop) / (0.90 + W)
pop absent:   fused_new = fused_old
```

The absent branch is not a rounding detail. `ResultReranker.cs:53-57` adds `WPopularity` to the weighted sum *and* to the weight total under a single `hasPopularity` guard, so a candidate with no resolved count keeps the unchanged divisor. Substituting `pop = 0` would score it 0.0 and punish it hard; substituting a median would pull it toward the pool. Both model a server that does not exist.

The 0.90 divisor is likewise not universal. `ResultReranker.cs:37-38` seeds `weightTotal = WBase = 0.45` and `:40-45` adds `WCentroid` only under `hasCentroid`, so a document lacking `body_centroid` fuses at 0.45. Documents named in `--divisor-exceptions` therefore take:

```
pop present:  fused_new = (0.45·fused_old + W·pop) / (0.45 + W)
pop absent:   fused_new = fused_old
```

Task 3 is what establishes whether that set is empty. With the default empty list this is inert, but it must be an input rather than an assertion: an exception document sits in the *present* branch, where neither of Step 2's assertions can see it.

The grid that sweeps `(W, S)` is **not** here — it belongs to whichever arm structure Phase 0 selects.

- [ ] **Step 2: Add the two mandatory non-zero assertions.**

Same discipline as Task 2, and for the same reason — a re-ranker that silently changed nothing looks exactly like one that worked:

1. At least one query's document order must differ from the input run. Zero is a failure, not a pass.
2. The absent-pop set must be non-empty **and** every one of its scores must be unchanged from the input. This is the branch most likely to be silently wrong, and it is the one with no natural signal that it ran.

- [ ] **Step 3: Smoke-test it.**

Run against the control run and Task 1's counts at any single cell — the spec's pre-registered `W` rule (`0.90 × σ_fused / σ_popularity`) is a reasonable choice but any finite positive `W` exercises both branches. Confirm both assertions pass, then score the output:

```bash
PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs python3 \
    Iverson.Server/Iverson.LoadTest/scripts/report.py \
    --run  <the --out file> \
    --qrels /home/ben/repositories/iverson-benchmark-corpora/scifact-2048-2026-09-06/qrels.trec
```

`report.py` is the only non-stdlib script in this directory; its third-party imports live under the corpora repo's `python-libs`, not site-packages, and a plain invocation exits 1 before scoring anything. Score against `scifact-2048-2026-09-06/qrels.trec` (4-column TREC) — `ir_measures.read_trec_qrels` cannot read `scifact-full/qrels/test.tsv`, which is the 3-column header-bearing BEIR TSV Task 2 uses.

- [ ] **Step 4: Commit.**

```bash
git add Iverson.Server/Iverson.LoadTest/scripts/popularity_rerank.py
git commit -m "add the arm-independent piecewise popularity re-ranker"
```

---

## Tasks NOT in this plan

From the spec's "Out of scope", inherited verbatim:

- **Whether the shipped consumer, StarRocks aggregate, and reconciliation worker function against live infrastructure.** That is a separate question, deliberately excluded.
- **A decayed-popularity arm.** Per-citation dates exist and the cost is now measured at ~8,797 requests (≈2.9 h keyed), but the arm belongs to whichever structure Phase 0 selects.

Additionally excluded by this plan's own scope, and not by the spec:

- **Phase 1 measurements 2, 3, 4c and 4d** — the ceiling and both nulls. They depend on the arm structure Phase 0's read selects.
- **All of Phase 2** — the live gate, its schema work, the compose allowlist keys, the `/build` extension and Step 1b's registration. Phase 2 runs only if Phase 1 clears its gate.
