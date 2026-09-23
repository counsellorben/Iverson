# Critical Implementation Review: 2026-09-14-popularity-measurement-phase0-implementation-plan (Round 2)

**Plan:** `/home/ben/repositories/Iverson/docs/plans/2026-09-14-popularity-measurement-phase0-implementation-plan.md`
**Artifact HEAD at review:** 0eea1fd2c8f9eb00a69a52befbd5f99766612673
**Verified plan-level assumptions section:** present

⚠️ 3 commits since plan-write time (SHA `80ab176dc2e23b0ee0d8841ef8033f17187e4a75`); cited file:line references re-checked under §1.

**Amendment detection (round-1 anchor `3f8b38a`, SHA form).** Content-identity check run: `git rev-parse 3f8b38a:<plan>` → `a37d40d620cfc2e7c46c4884db2c8b4dd2dd1595`; `git hash-object <plan>` → `492d96b2acc686040451a0e96e5fffcae84fc2cf`. **Unequal**, so an amendment exists; the authoritative hunk set is `git diff 3f8b38a -- <plan>`. Forward window `git log 3f8b38a..HEAD -- <plan>` = exactly one commit, `0eea1fd applied 6 fixes from …-critical-review-1 to …`, which matches the update skill's in-band commit shape. Reverse window `git log HEAD..3f8b38a -- <plan>` is empty. **Row family (c) is therefore empty by evidence, not by silence** — every hunk in the diff is in-band round-1 fix application, already covered at fix-equivalent rigor by row family (a) below. Row families (a) and (b) are carried as marked rows throughout §0.

Spec drift: `git log 80ab176..HEAD -- docs/specs/2026-09-13-popularity-signal-measurement-design.md` is **empty** — the three commits are the plan, the round-1 review, and the round-1 fixes. The spec itself has not moved, so the "Inherited from spec" block is trusted as written.

---

## 0. Coverage enumeration

### Task 1 — Complete the count + date fetch

| Row | Disposition |
|---|---|
| T1 Step 1 prose — move to `scripts/`, `--corpus`/`--cache` flags, "all other behaviour byte-identical" | ok — `[negative]` re-read `scratchpad/popularity/fetch_citations.py` in full (91 lines, `ls -la` → 4,940 B). `CORPUS`/`CACHE` at `:13-14`; readers are `:31-32` (`load_cache`), `:36-38` (`save`, incl. `CACHE + '.tmp'` and `os.replace`), `:41` (`open(CORPUS)`). No other reader; `BATCH_GAP` (`:28`) and `save`'s atomicity are untouched by the conversion |
| T1 Step 1 prose — header shape per `aspect_oracle.py:1-18` | ok — `[existence]` re-read `aspect_oracle.py:1-18`: shebang `#!/usr/bin/env python3`, purpose, "See docs/specs/…", `Run with:` block, "Writes, into --out-dir:" |
| T1 Step 2 command — `mv …bak …superseded` | ok — `[existence]` `ls -la scratchpad/popularity/` → `citations-countonly.json.bak`, `fetch.log`, `fetch_citations.py`. Source exists, destination free, `citations.json` absent so Step 3 is a clean run not a resume |
| T1 Step 2 prose — resume-predicate rationale and "first 27% of the corpus" | ok — `[totality]` `fetch_citations.py:43` is `known = set(cache['counts']) \| set(cache['unresolved'])` verbatim; dumped the real `.bak`: `counts` 1,288, `unresolved` 112 → 1,400 / 5,183 = 27.0% |
| T1 Step 3 command — the fetch invocation | ok — `[existence]` relative `--cache` resolves from repo root; `scratchpad/popularity/` exists (which is what `save` needs) |
| T1 Step 3 prose — "52 batches … roughly 2.6 minutes" | ok — `[totality]` 5,183 ids parsed from `corpus.jsonl` via `json.loads(l)['_id']`, 5,183 distinct → `range(0, 5183, 100)` = 52 batches. `S2_API_KEY` unset in this shell, so `BATCH_GAP = 3.0` |
| T1 Step 4 prose — completeness assertion + the four reported numbers | ok — `[totality]` population is the 5,183 distinct corpus ids; the assertion is a strict both-directions equality over that population and is what converts an exhausted-backoff residue (`:80-81`) or a short-`zip` at `:60` into a hard stop. All four numbers are recoverable from `citations.json` alone |
| T1 Step 5 command — `git add <script>`, then commit | ok — `[negative]` `git check-ignore scratchpad/popularity/citations.json` → no match (rc 1), so naming the one file is what keeps the data out |
| T1 Interfaces — Produces `{counts, years, dates, unresolved}` | ok — `[presence]` `fetch_citations.py:33` is the persisted default shape and `:61-71` are the three separately-guarded writers; round 1 dumped a real persisted record's key set (`['counts','dates','unresolved','years']`) and nothing in the diff touches this path |

### Task 2 — Phase 0's three measurements and the gate doc

| Row | Disposition |
|---|---|
| **(a) fix neighborhood** — T2 Step 1 measurement 1, the two-estimator emission (`:143-147`, new in `0eea1fd`) | → §2.1 |
| **(a) fix neighborhood** — T2 Step 1 measurement 4b, the stratum-pooled Mann–Whitney + per-stratum table (`:149-153`, new) | over: ok — `[totality]` the statistic is defined for every stratum because a stratum with 0 relevant or 0 non-relevant members contributes 0 pairs, never an undefined ratio; it cannot mis-fire on any stratum composition. under: ok — `[totality]` the construction cannot silently report nothing: the paired `sys.exit` on total pooled pairs (row R2) is exactly the vacuity it admits, and it fires under the pathological binning (one bin per document) that would otherwise yield a defined-but-empty statistic |
| **(a) fix neighborhood** — T2 Step 1, "Bins are **enumerated across the observed calendar range**" (`:149`, new) | over: ok — `[totality]` an empty bin is now ordinary rather than fatal; the replaced `sys.exit` no longer keys on emptiness, so enumeration over a wide observed range cannot abort the run. under: ok — `[totality]` enumeration is what makes an empty bin *visible*, which is the stated purpose; a bins-where-documents-land construction would make the per-stratum table's degeneracy column vacuous, and the plan's Note paragraph (`:157`) names that trade explicitly |
| **(a) fix neighborhood** — T2 Step 1, the replaced `sys.exit` condition list (`:155-157`, new) | over: ok — `[totality]` measured against the real inputs, none of the three can mis-fire: 275 eligible queries (probe over run × qrels), 1,288 resolved counts on the `.bak` alone, and the within-stratum pooled pair count is bounded below by 283 relevant documents against ~4,900 non-relevant ones. under: ok — `[totality]` each of the three names a population whose emptiness makes a *reported* number vacuous; see the span check for the one population they do not cover (4a's resolved-*date* set) and why it is not a finding |
| T2 Step 1 prose — measurement 4a (AUC of publication date alone) | ok — `[presence]` the population is well-defined from `citations.json` (`dates` ∪ `years`, restricted to `counts`); no claim about it is falsifiable before the fetch |
| T2 Step 1 prose — "emits the corpus median citation count, which is what fixes `SaturationPoint`" | ok — `[existence]` matches the spec's Pre-registration ("`S` is pre-registered as a *rule* … computed once from the completed fetch"); executing the rule, not revising it |
| T2 Step 2 command — flags and paths | ok — `[existence]` all three inputs present and parsed in-round: `sci-2048.similar.trec` (15,000 rows / 300 queries / depth exactly 50 on all 300), `scifact-full/qrels/test.tsv` (339 judgments), `scratchpad/popularity/` exists; `--out-dir docs/plans` exists |
| T2 Step 2 command — the script's `--out-dir` products are never named | dropped — the convention (assumption 14) requires the script's docstring to list them, and whatever lands there is gitignored and uncommitted by Step 4's explicit two-path `git add`. No stated outcome turns on it |
| **(a) fix neighborhood** — T2 Step 3 gate-doc contents (`:173`, amended) | ok — `[existence]` a strict superset of the spec's Phase 0 "Output" list, and it now names both measurement-1 estimators, the 4b pooled statistic **and** the per-stratum table, which is what §3.1/§3.2 of round 1 resolved to. The label the (i) row will carry is §2.1's subject, not this row's |
| T2 Step 4 commands — `git add` + `git add -f` | ok — `[existence]` `git check-ignore -v docs/plans/2026-09-GATE-relation-popularity.md` → `.gitignore:49:**/docs/plans/` (rc 0); `git check-ignore docs/plans` (bare dir) → rc 1. The script path is not ignored, so the mixed add is correct |
| T2 Interfaces — Consumes Task 1's `citations.json` | ok — `[presence]` see contract C1 |

### Task 3 — Phase 1 prerequisite (divisor uniformity)

| Row | Disposition |
|---|---|
| **(a) fix neighborhood** — T3 **Interfaces** block (`:194-196`, new) | → §2.2 |
| T3 Step 1 prose — restore both snapshots via `RESTORE.md` | ok — `[compat]` run-tier, not existence-tier: executed the delegated loop's name derivation (`c="${f%%-6802952876034638*}"` from `scifact-512-qdrant-snapshots/RESTORE.md`) against the **2048** snapshot filenames → `benchmark_documents_chunks_tenant_bypass` and `benchmark_documents_tenant_bypass`, exactly the two collections Step 1 names. The 512 loop is not 512-specific; the snapshot-id component is shared |
| T3 Step 2 prose — `body_centroid` is a named vector, check the vector set | ok — `[existence]` `ingest.py:656` `object_vectors["body_centroid"] = centroid`; `:838` declares `body_centroid` in `ensure_collection`'s vector config beside `body_vector` |
| T3 Step 2 prose — "`ingest.py` writes it only when at least one chunk vector has non-zero magnitude" | ok — `[existence]` `ingest.py:651-656`: `centroid_input = [v for v in chunk_vectors if not is_zero_magnitude(v)]`; `centroid = compute_centroid(centroid_input) if centroid_input else None`; key set only under `if centroid is not None` |
| T3 Step 2 — the query mechanism is unnamed | dropped — `ingest.py:293-313` already carries a stdlib `qdrant_request(method, path, body)` helper against `QDRANT_URL = "http://localhost:6333"` (`:154`, `urllib` only per `:138-139`), so the scroll is reachable with no new dependency. Round 1 dispositioned the running-Qdrant precondition; not re-raised |
| **(a) fix neighborhood** — T3 Step 3 stop-guard (`:210`, new) | over: ok — `[existence]` the guard keys on the same literal path Task 2 Step 3 creates (`docs/plans/2026-09-GATE-relation-popularity.md`, byte-identical in both steps), so it cannot fire after Task 2 has run. under: ok — `[absence]` `ls docs/plans/ \| grep -i GATE-relation-popularity` → empty today, so the guard's trigger state is the real current state and the guard is live, not decorative |
| **(a) fix neighborhood** — T3 Step 3 docId change (`:214`, new) | ok — `[presence]` `ingest.py:660` is `"docId": doc_id,` inside the `object_payload` literal opened at `:658` — the exact line the plan now cites (round 1 cited the range `:658-660`; the narrowing is correct). The identity space is right; whether the list reaches Task 4 is §2.2 |
| T3 Step 4 commands — `git add -f` + commit | ok — `[existence]` same ignore evidence as T2 Step 4; appending to an already-committed gate doc and re-adding it is correct |

### Task 4 — the piecewise re-ranker

| Row | Disposition |
|---|---|
| **(a) fix neighborhood** — T4 **Interfaces**, new `Consumes: Task 3's divisor-exception list` (`:232`) | → §2.2 |
| **(a) fix neighborhood** — T4 Step 1 flag list, `--divisor-exceptions <file>` (`:237`, new) | → §2.2 |
| **(a) fix neighborhood** — T4 Step 1, the 0.45 exception branch (`:250-257`, new) | present: ok — `[existence]` re-derived against the shipped code rather than the plan's summary: for a candidate with `hasCentroid == false`, `ResultReranker.cs:37-38` seeds `weightedSum = WBase·base`, `weightTotal = WBase`, `:40-45` is skipped, `:53-57` adds `WPopularity` to both, `:59` divides → `(0.45·base + W·pop)/(0.45 + W)`, and `VectorRankingOptions.cs:14` gives `WBase = 0.45`. absent: ok — `[existence]` with all three of `hasCentroid`/`hasDecay`/`hasPopularity` false the **short-circuit at `:26-33`** returns `candidate.BaseScore` unchanged, so `fused_old = base` for exception documents and `fused_new = fused_old` holds exactly — the plan's `0.45·fused_old` substitution is arithmetically right in both branches |
| T4 Step 1 prose/code — the 0.90 identity, both branches | ok — `[existence]` `hasCentroid` true → `weightedSum = 0.45·base + 0.45·centroid + W·pop`, `weightTotal = 0.90 + W` (`:37-45`, `:53-57`, `VectorRankingOptions.cs:14-15` `WBase = WCentroid = 0.45`), which is `(0.90·fused_old + W·pop)/(0.90 + W)` |
| T4 Step 1 prose — "the grid that sweeps (W, S) is not here" | ok — `[existence]` matches the spec's Phase 0 scope (measurements 2, 3, 4c, 4d belong to the arm structure Phase 0 selects) |
| T4 Step 2 prose — assertion 1 (some query's order differs) | over: ok — `[totality]` cannot fire spuriously; measured by building a real Task-4-shaped rerank over all 300 queries at `W = 0.30, S = 235` with the cached counts → **300 of 300** queries reordered. under: ok — `[totality]` the input run is fused-descending on all 15,000 rows (monotonicity probe, row R3), so a re-sort at `W → 0` reproduces it exactly and the assertion tests popularity's effect, not the sort |
| T4 Step 2 prose — assertion 2 (absent set non-empty, scores unchanged) | over: ok — `[totality]` the absent set is non-empty on the real inputs: the same probe left **11,235 of 15,000** rows in the absent branch against the partial cache, and the spec's measured unresolved rate is 8.0%. under: ok — `[totality]` all 11,235 carried byte-identical scores through the probe, so the assertion is satisfiable and non-vacuous as written. (Its blind spot — a truthiness implementation misreading `count = 0` — was dropped by round 1 and is not re-raised) |
| T4 Interfaces — "can be smoke-tested against the superseded partial cache" | dropped — the `.superseded` file is **not** of Task 1's shape (assumption 3: keys `['counts','unresolved']`, no `years`/`dates`), but Task 4 reads only `counts` and Step 3 directs the smoke test at Task 1's own output. No execution path fails |
| **(a) fix neighborhood** — T4 Step 3 `report.py` command (`:272-277`, new) | ok — `[compat]` run tier, the exact command re-executed in-round against a freshly built 15,000-row Task-4-shaped run → exit 0, `rows 15,000 / distinct queries 300 / qrels queries 300 / covered by this run 300 / duplicate doc ids none / build unknown / nDCG@10 0.6443 / R@50 0.9137`. No `.meta.json` sidecar is required. The falsifying results were both produced: without `PYTHONPATH` → exit 1 `could not import ir_measures`; with it but against `scifact-full/qrels/test.tsv` → exit 1 `ValueError: not enough values to unpack (expected 4, got 3)` at `ir_measures/util.py:284` |
| T4 Step 3 prose — "any finite positive `W` exercises both branches" | ok — `[totality]` confirmed by the probe above at `W = 0.30`: present branch 3,765 rows rescored, absent branch 11,235 rows unchanged, order changed on all 300 queries |
| T4 Step 4 command — commit | ok — `[existence]` `Iverson.Server/Iverson.LoadTest/scripts/` holds loose scripts, `ingest-contract.json` and `__pycache__` only; no packaging, no csproj reference |

### Cross-task interface contracts

| # | Contract | Disposition |
|---|---|---|
| C1 | **Persistence boundary.** Task 1 writes `citations.json`; Task 2's three measurements read it | ok — `[presence]` the consuming operations need `counts` (m1, 4b), `dates` + `years` (4a, 4b strata) and `unresolved` (Step 3's split). All four are top-level keys of the persisted artifact — the key set of a **real persisted record** was dumped in round 1 as `['counts','dates','unresolved','years']` (see the T1 Interfaces row), with `fetch_citations.py:33` default + `:61-71` as the producing writers; keys reload as `str`, matching the run file's docid space |
| C2 | **Persistence boundary, second call site of "read a counts file".** Task 4 reads the same artifact via `--counts` | ok — `[compat]` this caller additionally joins counts keys to TREC docids, which C1's caller does not. Probe over the real files: 4,441 distinct run docids, **0** absent from `corpus.jsonl`'s 5,183 `_id`s; the `.bak`'s `counts` keys join to run docids directly (the rerank probe above resolved 3,765 of 15,000 rows through exactly this join) |
| C3 | Task 1 Step 4's four numbers → Task 2 Step 3's gate doc | ok — `[presence]` all four are recomputable from `citations.json` alone (`len(counts)`, `len(unresolved)`, `len(dates ∩ counts)`, `len((years − dates) ∩ counts)`), which Task 2's script already opens; no out-of-band handoff is load-bearing |
| C4 | **Persistence boundary.** Task 2 creates the gate doc; Task 3 appends to it | ok — `[existence]` both steps name the same literal path; Task 3's new Interfaces line and Step 3 stop-guard now record and enforce the direction. Round 1's §2.1 resolved here |
| C5 | **Persistence boundary, new in `0eea1fd`.** Task 3 *Produces* the divisor-exception list; Task 4 *Consumes* it via `--divisor-exceptions <file>` | → §2.2 |
| C6 | Task 4 produces a run file; `report.py` consumes it | ok — `[compat]` run tier; see the T4 Step 3 row — a 15,000-row Task-4-shaped run scored clean, exit 0, no sidecar needed |
| C7 | The 1 → 2 → 3 → 4 order asserted in three places — Architecture (`:9`), assumption 22 (`:63`), the four Interfaces blocks | ok — `[bidirectional]` checked both directions. Forward: every dependency the Interfaces blocks declare (1→2 counts, 2→3 gate doc, 3→4 exception list, 1→4 counts) is consistent with the linear order and with assumption 22's evidence column. Reverse: no Interfaces line declares a dependency the order forbids, and no task's steps read an artifact a later task writes. The one loose statement — the Architecture line's "whose divisor Task 3's result determines" and assumption 22's "Task 4's 0.90 divisor is conditional on Task 3's count being zero" — overstates a now data-level dependency (`--divisor-exceptions` defaults to empty, so Task 4 is writable and smoke-testable before Task 3), but it overstates the constraint in the safe direction and breaks no stated outcome. dropped as a finding |

### Rule-like content — both failure directions

| # | Rule | Disposition |
|---|---|---|
| R1 | Measurement 1's estimator (i): "all in-pool (relevant, non-relevant) pairs from the eligible queries" = 4,179,529 pairs | over: → §2.1 (the stated pair count admits 4,164,400 cross-query pairs the spec's pool-matching excludes). under: → §2.1 (under the within-query reading only 15,129 pairs exist, and the Hanley–McNeil SE the same sentence mandates is not defined for that statistic) |
| R2 | The three `sys.exit` conditions in Task 2 Step 1 | over: ok — `[totality]` measured against the real inputs; none can mis-fire (275 eligible queries; 1,288 resolved counts before the fetch even runs; within-stratum pairs bounded below by 283 relevant × ~4,900 non-relevant documents). under: ok — `[totality]` the replaced 4b clause is the correct one: it is the only condition that catches a binning that yields zero within-stratum pairs, which is the pooled statistic's sole vacuity mode |
| R3 | The archived run's score column is the fused score (assumption 30's mechanism) | over: ok — `[existence]` `ResultDiversifier.cs:72-75` computes the MMR objective but `:80` emits `new RerankedResult(ranked[index].Id, ranked[index].Score)` — never the objective — at every λ, so no λ can substitute a non-fused score. under: ok — `[totality]` probe over all 15,000 rows of `sci-2048.similar.trec` **and** all 15,000 of `sci-2048.chunks.trec` → 0 non-monotone queries, so the recorded 50 are the fused top-50 in fused order. λ history confirmed independently: `c27eb98` (2026-09-07 18:26 -0400 = 22:26 UTC) set `LambdaSimilar = 1.00`; its parent had `0.70`; the run's `recordedAtUtc` is `2026-09-07T04:46:40Z` — so spec A8's argument genuinely does not cover this build and the plan's replacement mechanism is the load-bearing one |
| R4 | The 5-year stratum rule, `publicationDate` → `year` → no-date stratum | over: ok — `[totality]` the four input classes are exhaustive over what `fetch_citations.py:61-71` can emit (`counts` is set when `citationCount is not None`; `years` only when `year is not None`; `dates` only when `publicationDate` is truthy), so no resolved id escapes a stratum. under: ok — `[totality]` a fifth class exists (ids in neither map, from `:80-81` or a short `zip` at `:60`) but it carries no count, so it cannot enter the AUC population, and Task 1 Step 4's equality assertion is what stops the run before it matters |
| R5 | The absent-popularity predicate | over: dropped by round 1 (the `count = 0` truthiness case); not re-raised. under: ok — `[totality]` measured non-empty on the real inputs — 11,235 of 15,000 rows in the probe against the partial cache, and 91 of the 4,441 run docids are already in the cached `unresolved` list |
| R6 | The piecewise identity's branches, both divisors | 0.90 present/absent: ok — `[existence]` `ResultReranker.cs:37-45,53-59`. 0.45 present/absent: ok — `[existence]` same lines with `:40-45` skipped, plus the `:26-33` short-circuit which makes `fused_old = base` for exception documents. All four cells traced against the shipped code |
| R7 | Task 3's stop-guard predicate (file exists) | over: ok — `[existence]` see the T3 Step 3 row: the guard keys on the same literal path Task 2 Step 3 creates (`docs/plans/2026-09-GATE-relation-popularity.md`, byte-identical in both steps), so it cannot fire after Task 2 has run. under: ok — `[absence]` see the T3 Step 3 row: `ls docs/plans/ \| grep -i GATE-relation-popularity` → empty today, sampled at the populating state (Task 2 Step 3 has not run, §1 row 4), so the guard's trigger state is the real current state and the guard is live, not decorative |

### Row family (b) — intersected round-1 fix texts and probe results

| Item | Disposition |
|---|---|
| Round-1 §3.2 option (a)'s characterisation of the pooled form, now adopted verbatim as estimator (i) and as assumption 27's evidence column | → §2.1. Round 1 presented (a) as "One pooled AUC over all in-pool (relevant, non-relevant) pairs" and carried the same 4,179,529 figure; it never asked whether that estimator is pool-matched. The question is new, the locus is a round-1 fix text, and the finding is against the fix, not a re-raise of the choice |
| Assumption 27's transcribed probe numbers (authored during the fix round) | ok — `[totality]` every figure reproduced independently over the real run × qrels: in-pool positives per query `{0: 25, 1: 253, 2: 13, 3: 4, 4: 5}`; 275 eligible; `n_pos = 311`, `n_neg = 13,439`; 311 × 13,439 = 4,179,529; **4,325** distinct non-relevant documents; all-300 figures 14,689 / 4,402. All true as stated |
| Assumption 28's transcribed probe numbers | ok — `[totality]` parsed `qrels/test.tsv` directly: 339 judgments, 300 queries, all `score = 1`, 283 distinct relevant docs, 0 absent from `corpus.jsonl` |
| Assumption 29's transcribed run-tier results | ok — `[compat]` all three invocations re-executed; outputs byte-match the assumption (`nDCG@10 0.7450`, `R@50 0.9137`, exit 0 / `could not import ir_measures`, exit 1 / `ValueError … (expected 4, got 3)` at `ir_measures/util.py:284`, exit 1) |
| Assumption 30's altered cites — round 1 wrote `ResultDiversifier.cs:74-75` and `c27eb98` at `18:26 -0400`; the plan writes `:72-75` / `:80` and `22:26 UTC` | ok — `[existence]` both alterations are correct, not drift: `:72-75` is the `Mmr(int i)` body and `:80` is the emitting line; `git log -1 --date=iso c27eb98` → `2026-09-07 18:26:13 -0400`, which is `22:26 UTC`. The conversion is right and the ordering against `recordedAtUtc 2026-09-07T04:46:40Z` still holds |
| Round-1 §2.2 fix (b)'s cite `ingest.py:658-660`, narrowed by the plan to `:660` | ok — `[existence]` `:658` opens `object_payload = {`, `:660` is `"docId": doc_id,`. The narrowing is the more precise cite |
| Round-1 §2.3's probe `nDCG@10 0.2591` vs assumption 29's `nDCG@10 0.7450` | ok — `[compat]` not a contradiction: 0.2591 was round 1's re-ranked probe run, 0.7450 is the control run, which is what assumption 29 now describes. I reproduced 0.7450/0.9137 on the control and 0.6443/0.9137 on my own re-ranked probe |

### Row family (c) — amendment hunks

Empty **by evidence**: the sole forward-window commit is `0eea1fd applied 6 fixes from …-critical-review-1 to …`, which matches the update skill's in-band shape; the reverse window is empty and the content-identity check attributes the whole diff to it. No out-of-band amendment exists to review.

---

## 1. Verified-plan-assumptions cross-check

| # | Verdict |
|---|---|
| 1 | still holds — `[existence]` `ls Iverson.Server/Iverson.LoadTest/scripts/` contains all six named files (and 16 more, incl. eight `test_*.py`) |
| 2 | still holds — `[existence]` `ls -la scratchpad/popularity/fetch_citations.py` → 4,940 bytes |
| 3 | still holds — `[presence]` dumped the real file with `json.load`: top-level keys `['counts','unresolved']`, 1,288 counts, 112 unresolved; no `years`/`dates` |
| 4 | still holds — `[absence]` `ls docs/plans/ \| grep -i GATE` lists ten gate docs, none of them `GATE-relation-popularity`. Sampled at the populating state: Task 2 Step 3 has not run |
| 5 | still holds — `[existence]` `.gitignore:49` is `**/docs/plans/`; `git check-ignore -v docs/plans/2026-09-GATE-relation-popularity.md` → `.gitignore:49` (rc 0); `git check-ignore docs/plans` → rc 1, exactly the trailing-slash distinction the row warns about |
| 6 | still holds — `[negative]` `git check-ignore scratchpad/popularity/citations.json` → no match, rc 1 |
| 7 | still holds — `[existence]` all three paths present at the two stated roots; all three parsed in-round |
| 8 | still holds, evidence upgraded — `[compat]` beyond existence: executed the delegated restore loop's collection-name derivation against the 2048 snapshot filenames → the two collections Task 3 Step 1 names |
| 9 | still holds — `[existence]` `ingest.py:656` and `:838` exactly as cited |
| 10 | still holds — `[presence]` round 1's dump of a **real persisted record** gives exactly this key set, `['counts','dates','unresolved','years']` (cited at the T1 Interfaces row); the producing code matches — `fetch_citations.py:33` is the persisted default `{"counts": {}, "years": {}, "dates": {}, "unresolved": []}` and `:61-71` are the three separate guards (`citationCount is not None`, `year is not None`, truthy `publicationDate`) |
| 11 | still holds — `[existence]` `fetch_citations.py:13-14` are the two `/home/ben/...` absolutes; every sibling analysis script in `scripts/` is argparse-driven |
| 12 | still holds — `[negative]` `grep -rn "scratchpad/popularity" --include=*.py --include=*.md --include=*.cs .` (review files and worktrees excluded) → exactly one hit, `scratchpad/popularity/fetch_citations.py:14` |
| 13 | still holds — `[absence]` `ls scripts/ \| grep -E "popularity_(triage\|rerank)"` → empty, rc 1 |
| 14 | still holds — `[existence]` `aspect_oracle.py:1-18`; all four elements present as described |
| 15 | still holds — `[existence]` `beta_invariant.py:1-20`; both quoted sentences verbatim |
| 16 | still holds — `[negative]` `grep -rlniE "auc" scripts/*.py` → zero files, rc 1. `report.py:471` `holm_adjust`, `:489` `per_query_values` confirmed |
| 17 | still holds — `[compat]` the run-tier claim the row now rests on was reproduced independently: a 15,000-row run in Task 4's output shape scored clean through `report.py`, exit 0. `report.py:267` is `fields = line.split()` inside the structural check, as the corrected evidence says |
| 18 | still holds — `[existence]` `fetch_citations.py:20-21`, `'?fields=citationCount,year,publicationDate'` |
| 19 | still holds — `[totality]` 5,183 ids, 5,183 distinct, over the full `corpus.jsonl`; `:48` `range(0, len(todo), 100)` → 52; `:28` `BATCH_GAP = 1.2 if API_KEY else 3.0` |
| 20 | still holds — `[negative]` `S2_API_KEY` empty in this shell |
| 21 | still holds — `[existence]` `git log --oneline` shows lowercase imperative subjects with no Conventional-Commits prefix |
| 22 | **still holds** (was FAILED in round 1; the row was rewritten by `0eea1fd`) — `[existence]` every clause of the new evidence column re-verified: Task 3's Files block is `Modify:` the gate doc (`:192`), assumption 4's absence confirmed above, and `ResultReranker.cs:37-38` / `:40-45` are exactly the seeding and the `hasCentroid` guard. The "0.90 divisor is conditional" phrasing overstates a data-level dependency — noted at C7, not a failure |
| 23 | still holds — `[negative]` all four tasks produce standalone scripts or documents; none names an import of another. Task 4 Step 3 invokes `report.py` as a subprocess, not an import |
| 24 | still holds — `[existence]` `ResultReranker.cs:53-57` is one `hasPopularity` guard over both accumulators; `VectorRankingOptions.cs:14-15` gives `WBase = WCentroid = 0.45` |
| 25 | still holds — `[totality]` the four classes are exhaustive over `fetch_citations.py:61-71`'s emissions; see rule row R4 for the fifth (count-less) class and why totality is unaffected |
| 26 | still holds — `[negative]` `ls scripts/` yields only `*.py`, `ingest-contract.json` and `__pycache__`; no `__init__.py`, no csproj reference |
| 27 | still holds — `[totality]` every figure reproduced over the real run × qrels (see row family (b)). The row's *numbers* are true; what the numbers describe is §2.1 |
| 28 | still holds — `[totality]` `qrels/test.tsv` parsed directly: 339 judgments, 300 queries, all `score = 1`, 283 distinct relevant docs, 0 absent from `corpus.jsonl` |
| 29 | still holds — `[compat]` all three invocations re-executed in-round; outputs match the row exactly, including the `ir_measures/util.py:284` line number |
| 30 | still holds — `[totality]` mechanism and empirics both re-verified: `ResultDiversifier.cs:72-75` / `:80`, `recordedAtUtc 2026-09-07T04:46:40Z`, `c27eb98` at 22:26 UTC with parent `LambdaSimilar = 0.70`, and 0 non-monotone queries across all 15,000 rows of both run files |
| 31 | still holds — `[presence]` parsed the real file: header row `query-id\tcorpus-id\tscore` present, 339 judgments, 300 queries, all `score = 1`, 283 distinct relevant docs. Set-equality with `scifact-2048-2026-09-06/qrels.trec` confirmed by direct set comparison (`283 == 283`, sets equal, query sets equal), and `read_trec_qrels` fails on it at run tier as the row states |

### Span check — plan dependencies with no covering assumption

- **S1. The new scripts must be stdlib-only.** No assumption states it; assumption 29 states only that `report.py` is the one non-stdlib script *already* in the directory. Verified in-round and **holds**: `[negative]` `python3 -c "import numpy"` and `"import scipy"` both → `ModuleNotFoundError` in site-packages, while `PYTHONPATH=…/python-libs python3 -c "import numpy"` → 2.5.2 — so a `python-libs`-free script must be stdlib-only, and the siblings are (`aspect_oracle.py` imports `argparse`, `collections`, `os`; `multivector.py` imports `argparse`, `json`, `os`, `subprocess`, `sys`, `time`). Everything measurement 1, 4a and 4b need — rank statistics, the Hanley–McNeil SE, a normal-approximation CI, a between-query spread — is `math`/`statistics`-computable, so no plan text is contradicted.
- **S2. Task 3's Qdrant query mechanism.** No assumption covers how the point scan is performed. Verified in-round and **holds**: `[existence]` `ingest.py:293-313` defines `qdrant_request(method, path, body=None)` over `urllib.request` (`:138-139`) against `QDRANT_URL = "http://localhost:6333"` (`:154`), so the scroll needs no new dependency. (The service is not running here — `urlopen('http://localhost:6333/collections')` → `Connection refused` — but Step 1 is the restore step and round 1 already dispositioned that precondition.)
- **S3. Measurement 4a's population can be empty independently of the three `sys.exit` conditions.** The conditions cover eligible queries, within-stratum pairs and the resolved-*count* population; 4a runs over the resolved-*date* population, a strict subset none of them bounds. Verified in-round and **not a finding**: `[presence]` the spec's own 40-id probe resolved `publicationDate` 38/38, and `fetch_citations.py:20-21` requests the field in the same call as the count, so an empty date population requires every one of ~4,800 resolved papers to lack a date. No concrete failure path — fails the literal-wrongness gate, recorded here rather than promoted.
- **S4. The `--divisor-exceptions` file has no producer and no stated format.** Uncovered by assumptions 22–24 and by Task 3's Files block; → §2.2.
- **S5. Measurement 1's estimand.** Uncovered by assumption 27, which verifies the population's *counts* but not which pairing the estimator forms over them; → §2.1.

---

## 2. Literal-wrongness findings

### 2.1 — Measurement 1's estimator (i) specifies a pair count that only a cross-query pairing produces, under a heading that says pool-matched

**Description.** Task 2 Step 1 (`:143-144`) names the measurement "**Pool-matched AUC** — within each query's own retrieved pool", then specifies estimator (i) as "one pooled AUC over all in-pool (relevant, non-relevant) pairs from the eligible queries, with the Hanley–McNeil SE and a 95% CI … the pooled population is `n_pos = 311, n_neg = 13,439` = 4,179,529 pairs". Those two sentences select different estimators and an implementer must pick one:

- **Within-query pairing** (what "pool-matched" means, and what the spec's measurement 1 asks for) yields **15,129** pairs on the real inputs, not 4,179,529. The Hanley–McNeil SE the same sentence mandates is derived for a single two-sample AUC over all `n_pos × n_neg` pairs and has no definition for this statistic, so the CI would be attached to a statistic it was not derived for.
- **Cross-query pairing** yields exactly 4,179,529 and admits the Hanley–McNeil SE — but 4,164,400 of those pairs (99.64%) compare a relevant document against a non-relevant document drawn from a *different query's* pool. That is not "comparing each relevant paper against other papers on the same topic"; its negative sample spans **4,325 of the corpus's 5,183 documents (83.4%)**, which is approximately the corpus-level comparison the spec calls "a different and weaker claim". The plan nevertheless asserts at `:147` that the 0.6201 on record is "a different, weaker claim than either".

The plan's own measurement 4b, rewritten in the same fix round, shows the distinction is available to it: `:151` says "concordant pairs summed over within-stratum (relevant, non-relevant) pairs **only**, across all strata". Measurement 1's estimator (i) omits the "only" and supplies the cross-product arithmetic instead. Phase 0's stated job is to produce this number for the arm-structure read, which the spec says "may inform the choice among {lifetime arm only, lifetime + decayed arm, abandon}"; the gate doc will record whichever estimator was built under the label "pool-matched AUC".

**Evidence.** Plan `:143` (heading and the within-pool definition), `:144` (estimator (i), the HM SE, and `= 4,179,529 pairs`), `:147` ("a different, weaker claim than either"), `:151` (4b's contrasting "within-stratum … pairs only"), and assumption 27 at `:68` (the same arithmetic in the evidence column). Run tier over the real inputs (`sci-2048.similar.trec` × `scifact-full/qrels/test.tsv`), not read tier:

```
eligible queries 275   npos 311   nneg 13439
within-query pairs          15,129
cross-product npos*nneg  4,179,529
distinct non-relevant docs (eligible queries) 4,325   of 5,183 corpus docs
```

Computed over the 1,288 counts in `citations-countonly.json.bak` (the only counts on disk; the completed fetch has not been run), the two estimators are numerically distinct and the cross-query one sits nearer the corpus-level figure than the pool-matched one: cross-product pooled **0.5726** (Hanley–McNeil SE 0.0334, 95% CI [0.5071, 0.6381]); within-query pooled Mann–Whitney **0.5654**; mean of per-query AUCs **0.5731**; corpus-level relevant-vs-rest **0.5537**. `[totality]` over that subset; the full-fetch values will differ, which is precisely why the estimand must be fixed before the number is read.

**Proposed fix.** In `:144`, state the estimand explicitly and make the SE match it. Two internally consistent resolutions exist; the plan should name one:

- **(A) Keep (i) as the cross-query pooled two-sample AUC over `n_pos = 311, n_neg = 13,439` with the Hanley–McNeil SE** — internally consistent as written — but relabel it so the gate doc cannot read it as pool-matched. Replace "This is the form directly comparable to the 0.6201 already on record" with: "This pairs relevant documents against **every** eligible query's non-relevant documents, not only their own pool's — 4,179,529 pairs against 15,129 within-pool ones — so it is **not** the pool-matched statistic; it is the retrieved-corpus comparison, and it is what makes it directly comparable to the 0.6201 on record. Estimator (ii) is the pool-matched one." Delete or requalify `:147`'s claim that the 0.6201 is "a different, weaker claim than either": (i)'s negative sample covers 4,325 of the corpus's 5,183 documents.
- **(B) Make (i) the within-pool pooled Mann–Whitney statistic** — concordant pairs summed over within-query (relevant, non-relevant) pairs **only**, across all eligible queries, 15,129 pairs on the real inputs — mirroring the construction `:151` already fixes for 4b. Then the Hanley–McNeil SE must be replaced, because it is not defined for this statistic; `UNVERIFIED:` the replacement (a query-clustered or query-level bootstrap CI) is a statistical construction this review did not implement or validate, so the plan must name it rather than inherit it.

Under either resolution, add one sentence at `:146`: "Estimator (ii) is the spec's measurement 1 — the pool-matched one."

**Evidence:** `[totality]` the pair counts, eligibility counts and distinct-document counts above were computed by running the predicate over the complete real run × qrels (all 300 queries, all 15,000 rows, all 339 judgments), not sampled — both the covered set (275 eligible queries, 311 in-pool positives) and the residual (25 queries with zero in-pool positives, 1,250 negatives they contribute) were inspected. `[presence]` the quoted plan text at `:143`, `:144`, `:147` and `:151` is verbatim from the file at `0eea1fd`. `[existence]` the spec's measurement 1 wording ("Within each query's own retrieved pool… it compares each relevant paper against other papers on the same topic") and its contrast with the 0.6201 ("against *random* corpus documents… a different and weaker claim") are verbatim from `docs/specs/2026-09-13-popularity-signal-measurement-design.md`, which this review trusts as ground truth. `UNVERIFIED:` which resolution the author intends, and the SE that resolution (B) would need.

### 2.2 — Task 3 *Produces* the divisor-exception list into markdown prose; Task 4 *Consumes* it as `--divisor-exceptions <file>`, and no task writes that file

**Description.** `0eea1fd` added the channel round 1 asked for, but only at one end. Task 3's new Interfaces block says "Produces: the divisor-exception list — consumed by Task 4" (`:196`) and Task 4's says "Consumes: Task 3's divisor-exception list, docId-keyed" (`:232`), with the flag `--divisor-exceptions <file>` at `:237`. Task 3's **Files** block, however, still lists exactly one artifact — `Modify: docs/plans/2026-09-GATE-relation-popularity.md` (`:192`) — and Step 3 instructs "Append a prerequisite subsection to the gate doc … Record the exception set **as docIds**" (`:210`, `:214`). A markdown prose subsection is not a file `--divisor-exceptions` can be pointed at, and no step anywhere in the plan writes one. Neither end specifies a format either: Task 3 is told what identity space to use but not what serialisation; Task 4 is told to read a `<file>` of unspecified shape.

The plan's header mandates `superpowers:subagent-driven-development` ("one subagent per task"), and the Architecture line at `:9` says the ordering "is the only thing that establishes it" under that dispatch. So Task 3's subagent finishes having written prose into a gitignored markdown document, and Task 4's subagent — running later, with no sight of Task 3's transcript — implements a parser for a format nobody defined, against a path that does not exist. The contract the two Interfaces blocks declare cannot be satisfied by the two tasks as written.

This is inert when Task 3's count is zero (`--divisor-exceptions` defaults to empty) and bites when it is not — which the plan itself declines to predict: `:257` states "it must be an input rather than an assertion: an exception document sits in the *present* branch, where neither of Step 2's assertions can see it", and the spec calls this check "not optional".

**Evidence.** Plan `:192` (Task 3 Files — one entry, the gate doc), `:196` (Produces the list), `:210` and `:214` (Step 3 records it as prose in the gate doc), `:232` (Task 4 Consumes it), `:237` (`--divisor-exceptions <file>`), `:257` (why it must be an input). Task 3 has no Step that opens a file for writing other than the gate doc, and the plan's File Structure section (`:22-31`) names four created artifacts, none of them an exception list. `[absence]` `ls docs/plans/ | grep -i GATE-relation-popularity` → empty, so nothing on disk supplies the missing artifact today either. `UNVERIFIED:` whether Task 3's count is non-zero — Qdrant is not running here (`urlopen('http://localhost:6333/collections')` → `Connection refused`) and this review did not restore the snapshots; the finding is about the interface, not a prediction of the answer.

**Proposed fix.** Add the artifact to Task 3 so the flag has a producer, and fix the format at one end. In Task 3's **Files** block, add a second line:

```
- Create (data, untracked): `scratchpad/popularity/divisor-exceptions.txt` — one docId per line, empty when the count is zero
```

and append to Task 3 Step 3: "Write the same docIds to `scratchpad/popularity/divisor-exceptions.txt`, one per line, and create the file empty when the count is zero — this is the artifact Task 4's `--divisor-exceptions` consumes; the gate-doc subsection is the human-readable record of it." Then amend Task 4's flag line at `:237` to name the shape: "`--divisor-exceptions <file>` (optional, default empty) — a text file of docIds, one per line, as written by Task 3 Step 3". Leaving the file untracked matches the plan's existing treatment of `citations.json`, so Task 3 Step 4's commit (`git add -f` on the gate doc only) needs no change.

**Evidence:** `[negative]` `git check-ignore scratchpad/popularity/citations.json` → no match, rc 1 — `scratchpad/` is untracked but not ignored, so a new file there is kept out of commits by the same mechanism Task 1 Step 5 already relies on, and the proposed path introduces no ignore-rule change. `[presence]` `ingest.py:660` writes `"docId": doc_id` into `object_payload`, so Task 3's scroll can emit the docIds directly into the file with no keymap — the fix adds a write, not a new mechanism. `[compat]` the docId space joins to Task 4's other input: 4,441 distinct docids in `sci-2048.similar.trec`, **0** absent from `corpus.jsonl`'s 5,183 `_id`s, verified by set difference over the real files. `[existence]` `scratchpad/popularity/` exists today (`ls -la`), so the write needs no directory creation.

---

## 3. Forced decisions

No forced decisions found. Round 1's two items (§3.1 degenerate strata, §3.2 measurement-1 aggregation) were both decided and applied in `0eea1fd`; neither §2 finding above presents a choice a codebase or product constraint forces — §2.1 requires the plan to state which estimand it already implies, and §2.2 requires one artifact the plan already declares it produces.

---

## 4. Previously addressed

- **Round 1 §1, assumption 22 FAILED** — resolved. The row was rewritten to "**Order is 1 → 2 → 3 → 4**" with the Task 3 gate-doc dependency stated in both the assumption and its evidence column; re-verified this round (`ResultReranker.cs:37-38` and `:40-45` are exactly as cited, and assumption 4's absence still holds).
- **Round 1 §2.1 — Task 3 appends to a gate doc only Task 2 creates, and assumption 22 denied the dependency** — resolved. Task 3 gained an Interfaces block (`Consumes: the gate doc created by Task 2 Step 3`), Step 3 gained the stop-guard ("If the gate doc does not yet exist, Task 2 has not run — **stop rather than creating it here**"), and the Architecture line was rewritten from "Four independent-ish tasks" to "Four sequential … **run them 1 → 2 → 3 → 4**". Both guard directions re-checked this round (contract C4, rule row R7).
- **Round 1 §2.2 — no channel for Task 3's exception set, and the wrong identity space** — partially resolved. The identity space is fixed: Task 3 Step 3 now records docIds read from the `docId` payload key, and `ingest.py:660` confirms the cite. The 0.45 divisor branch was added to Task 4 Step 1 and traced against `ResultReranker.cs:26-59` this round — including the `:26-33` short-circuit the plan does not mention, which is what makes `fused_old = base` for exception documents and the substitution exact. The residue — the list has no producing file — is §2.2 above.
- **Round 1 §2.3 — Task 4 Step 3's `report.py` confirmation exits 1 as written** — resolved. The concrete `PYTHONPATH=…/python-libs` command and the qrels-shape sentence were added to Step 3, and assumption 29 records all three directions. Every one re-executed in-round with byte-matching output, including the `ir_measures/util.py:284` failure line.
- **Round 1 §3.1 — what 4b does with a stratum that cannot yield an AUC** — resolved, by the hybrid round 1 flagged as the dominant variant: 4b is now one stratum-pooled Mann–Whitney statistic *plus* a per-stratum table with degenerate strata marked, the bin construction is named ("enumerated across the observed calendar range"), and the "any stratum that ends up empty" `sys.exit` was replaced with "zero within-stratum … pairs summed across all strata". Both directions re-checked this round (rule rows R2, R4).
- **Round 1 §3.2 — measurement 1's aggregation and SE** — decided (option (c): report both, and say which the read is taken on), and the gate-doc contents at `:173` were extended accordingly. The realisation of option (a) inside that fix is §2.1 above; the *choice* is not re-raised.
- **Round 1 span items S1–S4** — all four closed into the assumptions table as rows 30, 29, 31 and 27 respectively. Each re-verified independently this round at the tier its claim class demands: 30 at run tier (0 non-monotone queries over 30,000 rows across two run files, plus the λ history from `git log`), 29 at run tier (three invocations), 31 by direct parse plus a set-equality comparison against `qrels.trec`, 27 by running the eligibility predicate over the complete run × qrels.

---

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §1 reconfirms all 31 assumptions (including #22, which round 1 failed) and the span check found no uncovered dependency that survives verification; §3 is empty. The two §2 findings are text-level and mechanical: §2.1 must be settled before `popularity_triage.py` is written, because it fixes what measurement 1 computes and what the gate doc's headline number means; §2.2 must be settled before Task 3 runs, because it is Task 3 that must write the artifact. Both can be applied via `update-implementation-plan` (or by hand), after which the plan is ready for `subagent-driven-development`.
