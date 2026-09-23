# Critical Implementation Review: 2026-09-14-popularity-measurement-phase0-implementation-plan (Round 1)

**Plan:** `/home/ben/repositories/Iverson/docs/plans/2026-09-14-popularity-measurement-phase0-implementation-plan.md`
**Artifact HEAD at review:** 3f8b38a37c065e3fc9a78f5319a05de36b4af6b2
**Verified plan-level assumptions section:** present

⚠️ 1 commits since plan-write time (SHA `80ab176dc2e23b0ee0d8841ef8033f17187e4a75`); cited file:line references re-checked under §1.

No prior review files exist for this plan basename (`ls docs/criticalreviews/ | grep -i phase0` → empty), so §4 is omitted and round-N>1 row families do not apply.

---

## 0. Coverage enumeration

### Task 1 — Complete the count + date fetch

| Row | Disposition |
|---|---|
| T1 Step 1 prose — move to `scripts/`, convert `CORPUS`/`CACHE` to `--corpus`/`--cache`, keep the rest "byte-identical" | ok — `[negative]` read `scratchpad/popularity/fetch_citations.py` in full (91 lines, 4,940 B). `CORPUS`/`CACHE` are module constants at `:13-14`; `grep -n 'CORPUS\|CACHE' ` finds readers only at `:31-32` (`load_cache`), `:36-38` (`save`) and `:41` (`open(CORPUS)`), and `main()` is called unconditionally at `:91`. No other reader exists, so the conversion is mechanical and does not touch `BATCH_GAP` (`:28`) or `save`'s `os.replace` (`:38`) |
| T1 Step 1 prose — header shape per `aspect_oracle.py:1-18` | ok — `[existence]` read `aspect_oracle.py:1-18`: shebang, purpose, "See docs/specs/…", `Run with:` block, "Writes, into --out-dir:". The plan's description matches the file |
| T1 Step 2 command — `mv …bak …superseded` | ok — `[existence]` `scratchpad/popularity/` holds exactly `citations-countonly.json.bak`, `fetch.log`, `fetch_citations.py`; the source path exists and the destination is free |
| T1 Step 2 prose — resume-predicate rationale | ok — `[existence]` `fetch_citations.py:43` is `known = set(cache['counts']) \| set(cache['unresolved'])`, verbatim as quoted. See rule row R1 |
| T1 Step 3 command — the fetch invocation | ok — `[existence]` relative `--cache scratchpad/popularity/citations.json` resolves from repo root, and `scratchpad/popularity/` exists (`save`'s `CACHE + '.tmp'` needs the directory, not the file). `citations.json` is absent, so `load_cache` (`:31`) returns the fresh four-key dict and the run is clean, not a resume |
| T1 Step 3 prose — "52 batches … roughly 2.6 minutes" | ok — `[totality]` see assumption 19 in §1 (5,183 **distinct** corpus ids → `:48` `range(0, len(todo), 100)` → `ceil(5183/100) = 52`, measured over the full file). The wall-clock estimate omits per-request latency, but an optimistic duration breaks no stated outcome. dropped as a finding |
| T1 Step 4 prose — the completeness assertion and the four reported numbers | ok — `[totality]` the assertion's population is the 5,183 corpus ids; probe: `json.loads(l)['_id']` over `corpus.jsonl` → 5,183 ids, 5,183 distinct, 0 duplicates, all `str`. Every id is therefore eligible to land in exactly one of `counts`/`unresolved`, and a duplicate id cannot inflate `unresolved` (which is an append-only list at `:73`). The four reported numbers are all recoverable from `citations.json` itself (`counts`, `unresolved`, `dates`, `years`), so no out-of-band handoff is required — see contract C3 |
| T1 Step 5 command — `git add <script>` then commit | ok — `[negative]` `grep -n scratchpad .gitignore` → no match, so `scratchpad/` is untracked-but-not-ignored exactly as assumption 6 states; naming the one file is what keeps `citations.json` out |
| T1 Interfaces — Produces `citations.json` `{counts, years, dates, unresolved}` | ok — `[presence]` probe at run tier, not schema tier: loaded the fetcher's own `load_cache`/`save` with `CACHE` repointed at a temp path, ran the classification block at `:60-73` over a canned 4-row batch, and dumped the **persisted** file's key set → `['counts','dates','unresolved','years']`; content `{"counts":{"A":7,"B":3,"C":0},"years":{"A":1998,"B":1975},"dates":{"A":"1998-10-01"},"unresolved":["D"]}`. All three dict key types reload as `str`. The four keys are present on a real persisted record even when `dates`/`years` are sparse |

### Task 2 — Phase 0's three measurements and the gate doc

| Row | Disposition |
|---|---|
| T2 Step 1 prose — measurement 1 (pool-matched AUC), its aggregation and its SE | → §3.2 |
| T2 Step 1 prose — measurement 4a (AUC of publication date alone, resolved-date population) | ok — `[presence]` the population is well-defined from `citations.json` (`dates` ∪ `years` restricted to `counts`); no plan claim about it is falsifiable in-round without the fetch |
| T2 Step 1 prose — measurement 4b (within-stratum AUC) and the 5-year stratum rule | → §3.1 (see rule row R3) |
| T2 Step 1 prose — the four `sys.exit` conditions | → §3.1 for the empty-stratum clause; the other two clauses `ok` — `[totality]` see rule row R8 (both measured on the real inputs: 275 eligible queries, 1,288 resolved counts) |
| T2 Step 1 prose — "emits the corpus median citation count, which is what fixes `SaturationPoint`" | ok — `[existence]` matches the spec's Pre-registration ("`S` is pre-registered as a *rule*… computed once from the completed fetch"). Executing a pre-registered rule, not revising it |
| T2 Step 2 command — flags and paths | ok — `[existence]` all three inputs present: `sci-2048.similar.trec` (15,000 lines), `scifact-full/qrels/test.tsv` (340 lines incl. header), `scratchpad/popularity/citations.json` (produced by Task 1). `--out-dir docs/plans` exists |
| T2 Step 2 command — the qrels file's actual format | ok — `[presence]` see span-check S3. It is a 3-column, header-bearing BEIR TSV (`query-id\tcorpus-id\tscore`), not 4-column TREC; the plan writes the parser, so nothing in the plan is contradicted, but no assumption records the format |
| T2 Step 3 prose — gate-doc contents | ok — `[existence]` a superset of the spec's "Output" list (numbers, corpus median, resolved/unresolved split); the plan adds the date splits and the no-date stratum size, which the spec's §3.1 requires be "reported alongside the ceilings" |
| T2 Step 4 command — `git add` + `git add -f` | ok — `[existence]` `git check-ignore -v docs/plans/2026-09-GATE-relation-popularity.md` → `.gitignore:49:**/docs/plans/`; the script path is not ignored, so the mixed add is correct |
| T2 Interfaces — Consumes Task 1's `citations.json` | ok — `[presence]` see contract C1 (all four top-level keys dumped from a real persisted record, not from the in-memory dict) |

### Task 3 — Phase 1 prerequisite (divisor uniformity)

| Row | Disposition |
|---|---|
| T3 Files — "Modify: `docs/plans/2026-09-GATE-relation-popularity.md`" | → §2.1 |
| T3 Step 1 prose — restore both snapshots via `RESTORE.md` | ok — `[existence]` `scifact-2048-qdrant-snapshots/` holds `RESTORE.md` + both `.snapshot` files; it delegates to `../scifact-512-qdrant-snapshots/RESTORE.md`, which exists and carries the concrete `curl … /snapshots/upload?priority=snapshot` loop and the api-key. A running Qdrant on `localhost:6333` is implied by "Qdrant only — the api is not involved" |
| T3 Step 2 prose — `body_centroid` is a named vector, so check the vector set not payload keys | ok — `[existence]` `grep -n body_centroid ingest.py` → `:17` (docstring), `:656` `object_vectors["body_centroid"] = centroid`, `:838` in `ensure_collection`'s vector config beside `body_vector`. The plan's line cites are correct (and correct the spec's stale `:620-625`) |
| T3 Step 2 prose — "ingest.py writes it only when at least one chunk vector has non-zero magnitude" | ok — `[existence]` `ingest.py:651-656`: `centroid_input = [v for v in chunk_vectors if not is_zero_magnitude(v)]`; `centroid = compute_centroid(centroid_input) if centroid_input else None`; the key is set only under `if centroid is not None` |
| T3 Step 3 prose — zero vs non-zero branches, and the 0.45 divisor | → §2.2 |
| T3 Step 4 command — `git add -f` + commit | ok — `[existence]` same ignore evidence as T2 Step 4 |

### Task 4 — the piecewise re-ranker

| Row | Disposition |
|---|---|
| T4 Step 1 prose/code — the identity, both branches | ok — `[existence]` see rule row R6 (both branches read at `ResultReranker.cs:37-57`, `VectorRankingOptions.cs:14-15`). The 0.90 divisor's provenance is §2.2's subject, not the algebra's |
| T4 Step 1 prose — the `ResultReranker.cs:53-57` justification for the absent branch | ok — `[existence]` `ResultReranker.cs`: `:53` `if (hasPopularity)`, `:55` `weightedSum += _o.WPopularity * …`, `:56` `weightTotal += _o.WPopularity`, `:57` `}`. One guard, both accumulators. `VectorRankingOptions.cs:14-15` `WBase = 0.45`, `WCentroid = 0.45` |
| T4 Step 1 prose — "the grid that sweeps (W, S) is not here" | ok — `[existence]` matches the spec's Phase 0 scope ("measurements 2, 3, 4c and 4d … belong to whichever arm structure Phase 0's read selects") |
| T4 Step 2 prose — assertion 1 (some query's order differs) | ok — `[totality]` see rule row R7 (both directions measured; the monotonicity probe covers all 15,000 rows of the input run) |
| T4 Step 2 prose — assertion 2 (absent set non-empty, scores unchanged) | ok — `[totality]` as a plan statement: rule row R5's under direction measures the absent set non-empty on the real inputs (91 of 4,441 run docids already in the cached `unresolved` list). Its blind spot is R5's over direction, dropped there |
| T4 Step 3 prose — the smoke test, and "report.py scores the output without complaint" | → §2.3 |
| T4 Step 3 prose — "any finite positive W exercises both branches" | ok — `[totality]` with `pop` varying across present candidates, any `W > 0` produces a different fused ordering; the absent branch is exercised whenever the absent set is non-empty, which rule row R5 confirms it is |
| T4 Step 4 command — commit | ok — `[negative]` `Iverson.Server/Iverson.LoadTest/scripts/` is not ignored and not referenced by any csproj/slnx/CI file (`grep -rn "LoadTest/scripts" --include=*.csproj --include=*.yml --include=*.yaml --include=*.slnx` → zero hits) |
| T4 Interfaces — Produces a TREC run scoreable by `report.py` | ok — `[compat]` run-tier probe, not the cited-line read: wrote a 15,000-row re-ranked run in Task 4's shape (6 columns, re-sorted, ranks renumbered, 6-dp scores) to the scratchpad and pushed it through `report.py --run <file> --qrels scifact-2048-2026-09-06/qrels.trec` → `rows 15,000 / distinct queries 300 / duplicate doc ids none / build unknown / nDCG@10 0.2591 / R@50 0.9137`, exit 0. No sidecar is required (`load_build_composite` returns `None` → "unknown" and the run is excluded from the mismatch check). R@50 is byte-equal to the control's 0.9137, confirming the probe preserved the doc set |

### Cross-task interface contracts

| # | Contract | Disposition |
|---|---|---|
| C1 | **Persistence boundary.** Task 1 writes `citations.json`; Task 2's three measurements read it | ok — `[presence]` Task 2 needs `counts` (measurement 1, 4b), `dates` + `years` (4a, 4b strata), `unresolved` (the split in Step 3). All four are top-level keys of the persisted artifact — dumped in the T1-Interfaces row above, not inferred from the in-memory dict. Key type on reload is `str`, matching the run file's docid space (see C2) |
| C2 | **Persistence boundary, second call site of "read a counts file".** Task 4 reads the same artifact via `--counts` | ok — `[compat]` this caller additionally joins counts keys to TREC docids, which C1's caller does not. Probe over the real files: 4,441 distinct docids in `sci-2048.similar.trec`, **0** absent from `corpus.jsonl`'s `_id` set; corpus `_id`s are `str`, and the persisted `counts`/`years`/`dates` keys reload as `str`. Join space is identical in both directions |
| C3 | Task 1 Step 4's four reported numbers → Task 2 Step 3's gate doc (no named artifact carries them) | ok — `[presence]` every one of the four is recomputable from `citations.json` alone (`len(counts)`, `len(unresolved)`, `len(dates ∩ counts)`, `len((years − dates) ∩ counts)`), which Task 2's script already opens. No out-of-band handoff is load-bearing |
| C4 | **Persistence boundary.** Task 2 creates `docs/plans/2026-09-GATE-relation-popularity.md`; Task 3 appends to it | → §2.1 |
| C5 | Task 3 produces the list of object points lacking `body_centroid`; Task 4's divisor depends on it | → §2.2 |
| C6 | Task 4 produces a run file; `report.py` consumes it | ok for the file — `[compat]` see the T4-Interfaces row (a 15,000-row run in Task 4's output shape pushed through `report.py`, exit 0); → §2.3 for the invocation |

### Rule-like content — both failure directions

| # | Rule | Disposition |
|---|---|---|
| R1 | Resume predicate `known = set(counts) \| set(unresolved)` (`fetch_citations.py:43`) | over: ok — `[negative]` no id can be in `known` without having been classified, because `counts[k]` and `unresolved.append(k)` are the only two writers (`:62`, `:73`) and they are the two arms of one `if`. under: ok — `[totality]` an id skipped by an exhausted-backoff batch (`:80-81`) enters neither map and is therefore **re-fetched** on the next run, which is the intended resume behaviour; Task 1 Step 4's completeness assertion is what turns a residual into a hard stop |
| R2 | The fetch's per-id classification (assumption 25's four classes) | over: ok — `[totality]` ran `:60-73` verbatim over a canned batch covering all four: `{7,1998,'1998-10-01'}`→`counts`+`years`+`dates`; `{3,1975,None}`→`counts`+`years`; `{0,None,None}`→`counts` only (a genuine zero is **resolved**, not unresolved); `None`→`unresolved`. under: ok — one class the assumption does not name exists (ids in **neither** map, from `:80-81` or from `zip(batch, rows)` at `:60` truncating a short response), and it is gated by Task 1 Step 4; it carries no count, so it cannot enter the AUC population and totality of the stratum rule is unaffected |
| R3 | The 5-year calendar stratum rule, `publicationDate` → `year` → no-date stratum | over: → §3.1 (bins enumerated across a calendar range can be empty → the plan's own `sys.exit`). under: → §3.1 (a non-empty stratum with no relevant, or no non-relevant, member yields an undefined within-stratum AUC and is not named by any of the plan's four exit conditions) |
| R4 | The pool-matched AUC's eligibility predicate (which queries enter measurement 1) | over: ok — `[totality]` ran the predicate over the real run × qrels: in-pool positives per query = `{0: 25, 1: 253, 2: 13, 3: 4, 4: 5}`; 275 of 300 queries have ≥1 relevant **and** ≥1 non-relevant in-pool. The plan's `sys.exit` fires only at zero, so it cannot misfire. under: → §3.2 (the 25 ineligible queries and the ~8% count-less pool members change the estimator's meaning, and the plan names two incompatible aggregations) |
| R5 | The absent-popularity predicate ("a candidate with no resolved count") | over: dropped — a truthiness-based implementation would misclassify a genuine `count = 0` as absent, and the real corpus does contain one (`CorpusId 4810810`, count 0 in the 1,288-id cache, **and** present in the run pool), but the plan's own text is correct as written and the population is ~1/1,288 ≈ 4 documents corpus-wide — far below the instrument's 0.0152 MDE, so no stated outcome is broken. under: ok — `[totality]` the absent set is non-empty on the real inputs: 91 of the 4,441 run docids are already in the cached `unresolved` list, and the spec's measured unresolved rate is 8.0% |
| R6 | The piecewise identity's two branches | present: ok — `[existence]` `(0.90·f + W·p)/(0.90+W)` is exactly `ResultReranker.cs:37-57` with `WBase+WCentroid = 0.45+0.45` (`VectorRankingOptions.cs:14-15`) and `hasDecay` false. absent: ok — `[existence]` `:53-57` is a single guard over both accumulators, so `weightedSum/weightTotal` is unchanged, i.e. `fused_new = fused_old` |
| R7 | Task 4's assertion 1 — "at least one query's order must differ" | over: ok — `[totality]` cannot fire spuriously; with any `W>0` and varying `pop`, order changes. under: ok — `[totality]` the input run is already fused-descending on all 300 queries (span-check S1's monotonicity probe over 15,000 rows), so a re-sort at `W→0` reproduces the input order exactly; the assertion therefore tests popularity's effect and not the re-sort |
| R8 | Task 2's `sys.exit` conditions | over: ok for two of three — `[totality]` "zero eligible queries" is 275 away from firing (measured, R4), and "resolved-count population of zero" is 1,288 away on the existing cache alone (measured). under: ok for those two — `[totality]` both are lower bounds on populations that only grow with the completed fetch. The third ("any stratum that ends up empty") → §3.1 in both directions |

---

## 1. Verified-plan-assumptions cross-check

| # | Verdict |
|---|---|
| 1 | still holds — `[existence]` `Iverson.Server/Iverson.LoadTest/scripts/` exists and contains all six named files (and 13 more, including six `test_*.py` companions) |
| 2 | still holds — `[existence]` `wc -c scratchpad/popularity/fetch_citations.py` → `4940` |
| 3 | still holds — `[presence]` dumped the real file: top-level keys `['counts','unresolved']`, 1,288 counts, 112 unresolved; no `years`/`dates` |
| 4 | still holds — `[absence]` `ls docs/plans/ \| grep -i GATE-relation-popularity` → empty. The populating state is "Task 2 Step 3 has run", which it has not |
| 5 | still holds — `[existence]` `.gitignore:49` is `**/docs/plans/`; `git check-ignore -v docs/plans/2026-09-GATE-relation-popularity.md` → `.gitignore:49` |
| 6 | still holds — `[negative]` `grep -n scratchpad .gitignore` → no match; `git check-ignore scratchpad/popularity/citations.json` → no match |
| 7 | still holds — `[existence]` all three paths present at the two stated roots |
| 8 | still holds — `[existence]` both `.snapshot` files + `RESTORE.md` present; the delegated `scifact-512-qdrant-snapshots/RESTORE.md` exists and carries the concrete restore loop |
| 9 | still holds — `[existence]` `ingest.py:656` and `:838` are exactly as cited (a correction of the spec's stale `:620-625`) |
| 10 | still holds, evidence upgraded — `[presence]` the cited lines are source defaults (`:33`) and guards (`:61-71`), which is the tier the ladder calls never-sufficient for a persisted-artifact claim. Upgraded in-round by round-tripping the fetcher's own `load_cache`/`save` on a temp path: persisted key set `['counts','dates','unresolved','years']` |
| 11 | still holds — `[existence]` `fetch_citations.py:13-14`; every sibling in `scripts/` is argparse-driven |
| 12 | still holds — `[negative]` `grep -rn "scratchpad/popularity" --include=*.py --include=*.md --include=*.cs .` (review files excluded) → exactly one hit, `scratchpad/popularity/fetch_citations.py:14` |
| 13 | still holds — `[absence]` no `popularity_triage.py` / `popularity_rerank.py` in `scripts/` (`ls scripts/ \| grep -E "popularity_(triage\|rerank)"` → empty) |
| 14 | still holds — `[existence]` read `aspect_oracle.py:1-18`; all four elements present as described |
| 15 | still holds — `[existence]` read `beta_invariant.py:1-20`; "fails loudly -- sys.exit, not a printed warning" and "Zero pairs asserted is a failure of the precondition, not a pass" are verbatim |
| 16 | still holds — `[negative]` `grep -rlniE "auc" scripts/*.py` → zero files |
| 17 | still holds, evidence corrected — `report.py:267` `fields = line.split()` is inside `structural_check`, and `:268-270` discards any row with `< 6` fields as malformed; the **scorer** is `ir_measures.read_trec_run` at `:365`, which the cited line does not evidence. Upgraded to a `[compat]` run-tier probe: a re-ranked 15,000-row run in Task 4's shape scored clean through `report.py`, exit 0 (see the T4-Interfaces §0 row) |
| 18 | still holds — `[existence]` `fetch_citations.py:20-21`, `'?fields=citationCount,year,publicationDate'` |
| 19 | still holds — `[totality]` 5,183 lines in `corpus.jsonl`, 5,183 **distinct** ids (so `todo` is 5,183 on a clean run), `:48` `range(0, len(todo), 100)` → `ceil(5183/100) = 52`; `:28` `BATCH_GAP = 1.2 if API_KEY else 3.0` |
| 20 | still holds — `[negative]` `S2_API_KEY` empty in the shell; `grep -rn S2_API_KEY ~/.bashrc ~/.profile ~/.bash_profile` → no hits |
| 21 | still holds — `[existence]` `git log --oneline -20`: "add the popularity measurement phase 0 implementation plan", "applied 3 fixes from …", "add critical design review round 2 …" |
| 22 | **FAILED.** `[presence]` The assumption's evidence line reads "Task 3 reads Qdrant only", but Task 3's own **Files** block is `Modify: docs/plans/2026-09-GATE-relation-popularity.md` and its Step 3 is "Append a prerequisite subsection to the gate doc" — a file assumption 4 verifies does not exist and Task 2 Step 3 creates. Task 3 therefore depends on Task 2. New evidence: plan `docs/plans/2026-09-14-popularity-measurement-phase0-implementation-plan.md:175` (Task 3 Files) and `:188` (Step 3) against `:43` (assumption 4) and `:154-156` (Task 2 Step 3). → §2.1 |
| 23 | still holds — `[negative]` all four tasks produce standalone scripts or documents, and none of the four names an import of another. (The directory does contain a cross-import precedent — `similar_arms.py:15-17` does `sys.path.insert` then `import ingest, multivector` — but no task in this plan uses it) |
| 24 | still holds — `[existence]` `ResultReranker.cs:53-57` is one `hasPopularity` guard over both `weightedSum` and `weightTotal`; `VectorRankingOptions.cs:14-15` gives the 0.45/0.45 that makes the divisor 0.90 |
| 25 | still holds, qualified — `[totality]` all four named classes reproduced by running `:60-73` over a canned batch (see §0 rule row R2). A fifth class the assumption does not name exists (ids in neither map, via `:80-81` or a short-`zip` at `:60`); it is gated by Task 1 Step 4 and, carrying no count, cannot enter the AUC population — so the stratum rule's totality is unaffected. This row does **not** discharge the rule's behaviour over the real date distribution, which is unobtainable pre-fetch (see §3.1) |
| 26 | still holds — `[negative]` `ls scripts/` yields only `*.py`, `ingest-contract.json`, and `__pycache__`; `grep -rn "LoadTest/scripts" --include=*.csproj --include=*.yml --include=*.yaml --include=*.slnx .` → zero hits |

### Span check — plan dependencies with no covering assumption

- **S1. The archived run's score column is the fused score *for the build that produced it*.** Task 4's entire identity rests on this; the plan defers to spec A8, which argues from `LambdaSimilar = 1.00` and `ResultDiversifier.cs:74-75`. But `sci-2048.meta.json` records `recordedAtUtc 2026-09-07T04:46:40Z`, and `c27eb98` ("set LambdaSimilar/LambdaChunks per rule 7.2") is dated `2026-09-07 18:26 -0400` — 14 hours **after** the run; at run time the default was `db774c5`'s "both 0.70". Verified in-round by a different mechanism and **holds**: `ResultDiversifier.cs:80` emits `new RerankedResult(ranked[index].Id, ranked[index].Score)` — the **fused** score, never the MMR objective — at every λ. Confirmed empirically: all 300 queries in `sci-2048.similar.trec` are score-monotone non-increasing by rank (so the recorded 50 are the fused top-50 in fused order, whichever λ was live). `[totality]` probe over all 15,000 rows; the same check on `sci-2048.chunks.trec`, `sc-head.similar.trec`, `sc-centroid.similar.trec`, `head-raw.similar.trec` also returns 0 non-monotone queries.
- **S2. `report.py`'s third-party dependencies are reachable.** No assumption covers this, and Task 4 Step 3 requires it. Verified in-round and **fails**: `[compat]` run tier, not read tier — `env -u PYTHONPATH python3 report.py --run … --qrels …` → `could not import ir_measures (No module named 'ir_measures')`, exit 1. → §2.3
- **S3. `scifact-full/qrels/test.tsv`'s format.** Assumption 7 covers existence only. Verified in-round and **holds** (no plan text is contradicted): `[presence]` dumped the real file — it is a 3-column, tab-separated, **header-bearing** BEIR file (`query-id\tcorpus-id\tscore`; 339 judgments over 300 queries, all `score = 1`, 283 distinct relevant docs, 0 absent from `corpus.jsonl`). Note it is *not* the 4-column TREC shape `report.py`/`ir_measures` require — a TREC equivalent with a byte-identical relevant-doc set sits at `scifact-2048-2026-09-06/qrels.trec`.
- **S4. The pool-matched AUC population is non-degenerate.** No assumption covers it. Verified in-round and **holds**: `[totality]` the predicate run over all 300 queries × the full qrels — 275 carry ≥1 relevant and ≥1 non-relevant document inside their own recorded 50; 311 of 339 positives are in-pool. Its estimator ambiguity is §3.2, not a coverage gap.
- **S5. Task 3's gate-doc precondition.** Uncovered by assumptions 22 and 23; → §2.1.
- **S6. Task 3's result gates Task 4's divisor.** Stated by Task 3 Step 3, uncovered by assumptions 22–24 and absent from Task 4's Interfaces; → §2.2.

---

## 2. Literal-wrongness findings

### 2.1 — Task 3 appends to a gate doc only Task 2 creates, and the plan's ordering assumption denies the dependency

**Description.** Task 3's **Files** block is `Modify: docs/plans/2026-09-GATE-relation-popularity.md`, and Step 3 is "Append a prerequisite subsection to the gate doc". Assumption 4 verifies that file does not exist, and Task 2 Step 3 is what creates it. Assumption 22 nevertheless asserts "Tasks 3 and 4 depend on neither", evidenced as "Task 3 reads Qdrant only" — which is false of Task 3 as written. The plan's header mandates `superpowers:subagent-driven-development` ("one subagent per task"), so nothing but this assumption establishes the ordering. A Task 3 subagent dispatched before Task 2 has no file to append to; Step 4's `git add -f` then commits a gate doc containing only the prerequisite subsection, and Task 2 Step 3's "Create" either overwrites it or collides.

**Evidence.** Plan `:175` (Task 3 Files: `Modify:` the gate doc), `:187-189` (Step 3: "Append a prerequisite subsection to the gate doc"), `:194` (Step 4 commits it) versus `:43` (assumption 4: "does not exist; Task 2 creates it"), `:154-156` (Task 2 Step 3: "Create `docs/plans/2026-09-GATE-relation-popularity.md`"), and `:61` (assumption 22's "Task 3 reads Qdrant only"). Filesystem: `ls docs/plans/ | grep -i GATE-relation-popularity` → empty.

**Proposed fix.** Correct assumption 22's row to read: "Task 2 consumes Task 1's `citations.json`; **Task 3 appends to the gate doc Task 2 Step 3 creates**; Task 4 depends on neither", with evidence "Task 3's Files block is `Modify:` the gate doc, which assumption 4 verifies is absent until Task 2 creates it; Task 3's *measurement* reads Qdrant only". Add to Task 3 an **Interfaces** block reading `Consumes: the gate doc created by Task 2 Step 3`, and add to Task 3 Step 3 the sentence: "If the gate doc does not yet exist, Task 2 has not run — stop rather than creating it here, so the Phase 0 section is not lost."

**Evidence:** `[existence]` `ls docs/plans/ | grep -i GATE-relation-popularity` → no output, so the file genuinely does not exist today and the ordering is real rather than hypothetical. `[presence]` the plan text quoted above is verbatim from `docs/plans/2026-09-14-popularity-measurement-phase0-implementation-plan.md` at the lines cited. The fix introduces no new symbol, path, or command.

### 2.2 — Task 4 hardcodes the 0.90 divisor with no channel for Task 3's exception set, and the plan records no 3→4 precondition

**Description.** Task 3 Step 3 states the dependency explicitly: "Zero means the identity is uniform and exact, and **Task 4's re-ranker may assume the 0.90 divisor throughout**. Non-zero means those specific point ids take the 0.45 divisor and must be listed, so Phase 1 can handle them separately rather than mis-scoring them." Task 4's flag set is `--run --counts --w --saturation --out` — there is no input for that list, its code block fixes `0.90` in both places, and its **Interfaces** block records only `Consumes: a counts file`. Neither of Task 4's two mandatory assertions can detect the case: a 0.45-divisor candidate is in the *present* branch, so assertion 2 (absent-set scores unchanged) never sees it, and assertion 1 only requires that *some* order changed. The plan also names no ordering constraint 3→4 (assumptions 22 and 23 both omit it), so under the mandated one-subagent-per-task dispatch Task 4 can be written, smoke-tested and committed before Task 3's number exists. Separately, the artefact Task 3 is told to produce is in the **wrong identity space** for Task 4 to consume even if a flag existed: Task 3 Step 3 says to list "point ids" (Qdrant ulongs), while Task 4 keys on TREC docids; recovering the mapping needs `key_to_ulong(uuid5(...))` + `keymap.json`, which this plan's scope explicitly excludes (it belongs to the spec's Phase 2 Step 2).

**Evidence.** Plan `:189` (Task 3 Step 3, quoted above), `:211` (Task 4's flag list), `:215-220` (the code block's two literal `0.90`s), `:206` (Task 4 Interfaces: `Consumes: a counts file of Task 1's shape`), `:226-231` (the two assertions), `:61-62` (assumptions 22 and 23). Server side: `ResultReranker.cs:37-57` — a candidate with `hasCentroid == false` and `hasPopularity == true` fuses at `weightTotal = WBase = 0.45`, not 0.90; `ingest.py:651-656` is the writer whose absence produces that candidate. The object payload does carry `docId` (`ingest.py:658-660`), so a docid-space listing is obtainable, but the plan asks for point ids.

**Proposed fix.** (a) Add an ordering line to assumption 22's row — "Task 4's 0.90 divisor is conditional on Task 3's count being zero; Task 3 runs first" — and add `Consumes: Task 3's divisor-exception list (empty when Task 3 reports zero)` to Task 4's Interfaces block. (b) Change Task 3 Step 3 to record the exception set **as docIds read from each point's `docId` payload key**, not as Qdrant point ids, so it lands in Task 4's join space with no keymap. (c) Add to Task 4 Step 1: "`--divisor-exceptions <file>` (optional, default empty): docIds that Task 3 found to lack `body_centroid`; these fuse as `(0.45·fused_old + W·pop)/(0.45 + W)` in the present branch and are unchanged in the absent branch."

**Evidence:** `[presence]` `ingest.py:658-660` writes `"docId": doc_id` into `object_payload`, so a scroll can emit docIds directly — the fix's (b) needs no new mechanism. `[compat]` the run file's docid space and `corpus.jsonl`'s `_id` space are identical (4,441 distinct run docids, 0 absent from the corpus), so a docId-keyed exception list joins to both Task 4's inputs. `UNVERIFIED:` whether Task 3's count is actually non-zero — Qdrant is not running here and I did not restore the snapshots. The trigger is unlikely (all 5,183 corpus documents have non-blank body text; none is under 50 characters, so `split_into_chunks` cannot yield an all-zero-magnitude chunk set from an empty body), but the plan itself declines to assume it: the spec calls this check "not optional", and the fix is the interface, not a prediction of the answer.

### 2.3 — Task 4 Step 3's `report.py` confirmation exits 1 as written

**Description.** Task 4 Step 3 requires "that `report.py` scores the output without complaint". `report.py` is the one script in this directory that is not stdlib-only; its module docstring states its dependencies "are reached through PYTHONPATH, never a site-packages install -- this box is PEP 668 externally-managed with no working venv". The plan names no `PYTHONPATH`, no libs directory, and gives no command for the step; a plain invocation terminates before scoring anything, so the step's confirmation cannot be obtained. No plan-level assumption covers the dependency (assumption 17 covers the parser, not the invocation).

**Evidence.** Run tier, not read tier: `env -u PYTHONPATH python3 Iverson.Server/Iverson.LoadTest/scripts/report.py --run <probe>.trec --qrels …/qrels.trec` → `could not import ir_measures (No module named 'ir_measures'). It must be reached via PYTHONPATH, not site-packages -- see this script's module docstring for the install command.`, exit code 1. `env -u PYTHONPATH python3 -c "import ir_measures"` → `ModuleNotFoundError`. Source: `report.py:71-78` (the "reached through PYTHONPATH, never a site-packages install -- this box is PEP 668 externally-managed with no working venv" note and its `python3 -m pip install --target` command) and `:870-875` (the `ImportError` guard's `sys.exit`).

**Proposed fix.** Replace Task 4 Step 3's prose confirmation with the concrete command:

```bash
PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs python3 \
    Iverson.Server/Iverson.LoadTest/scripts/report.py \
    --run  <the --out file> \
    --qrels /home/ben/repositories/iverson-benchmark-corpora/scifact-2048-2026-09-06/qrels.trec
```

and add a sentence: "`report.py` is the only non-stdlib script in this directory; its third-party imports live under the corpora repo's `python-libs`, not site-packages. Score against `scifact-2048-2026-09-06/qrels.trec` (4-column TREC) — `ir_measures.read_trec_qrels` cannot read `scifact-full/qrels/test.tsv`, which is a 3-column header-bearing BEIR TSV."

**Evidence:** `[compat]` the exact command above was run in-round against a 15,000-row probe run in Task 4's output shape and exited 0, printing `rows 15,000 / distinct queries 300 / qrels queries 300 / covered by this run 300 / duplicate doc ids none / build unknown / nDCG@10 0.2591 / R@50 0.9137`. The falsifying result was available and did not occur: the same invocation without `PYTHONPATH` exits 1 (evidence above). `[presence]` `scifact-2048-2026-09-06/qrels.trec` exists, has 339 rows all of 4 fields (`1 0 31715818 1`), and its relevant-doc set is set-equal to `scifact-full/qrels/test.tsv`'s 283 docs — verified by set comparison, so substituting it changes no judgment.

---

## 3. Forced decisions

### 3.1 — What measurement 4b does with a stratum that cannot yield an AUC

**The choice.** The plan makes "any stratum that ends up empty" a hard `sys.exit`, and names no handling for the far more likely case: a stratum that is *non-empty* but contains no relevant document, or no non-relevant document, for which the within-stratum AUC is undefined. It also does not say how the 5-year calendar bins are constructed, and the two constructions give opposite behaviours for the stated exit condition.

**Why it's forced.** The strata are pre-registered: the spec fixes them "before the ceilings are computed (chosen 2026-09-14)" and binds the Phase 1 gate on them, so they cannot be re-binned once the numbers are seen. The corpus makes degenerate strata likely rather than exotic — there are only **283 distinct relevant documents** across the entire 5,183-document corpus (measured against `qrels/test.tsv`), spread over however many 5-year bins the publication dates span. And the construction matters in both directions: bins enumerated across the observed calendar range can be empty (firing the plan's `sys.exit` and producing no Phase 0 numbers at all), while bins created only where documents land can never be empty (making the plan's stated assertion vacuous — precisely the "check that silently asserts nothing" the plan's own Task 2 prose warns against). The script must do something, and what it does changes the 4b number the arm-structure read is taken on.

**Options.**

- **(a) Abort on any stratum that cannot yield an AUC**, reporting which one and its size. Consistent with the `beta_invariant.py` discipline the plan invokes; costs a re-run cycle if it fires, and offers no path forward because the strata are pre-registered.
  **Evidence:** `[presence]` `beta_invariant.py:6-8` — "A check that silently asserts nothing over its data is indistinguishable from a check that passed… fails loudly -- sys.exit, not a printed warning". `UNVERIFIED:` how often this would fire — publication years are not obtainable before the fetch (see the closing note).
- **(b) Skip degenerate strata from the 4b aggregate and report each skipped stratum's size and composition alongside the number**, mirroring the treatment the spec already mandates for the no-date stratum ("its size is reported alongside the ceilings, not folded in silently"). Keeps 4b computable; changes its population, which must then be stated.
  **Evidence:** `[presence]` spec `docs/specs/2026-09-13-popularity-signal-measurement-design.md` §4c, "The no-date stratum preserves no age structure by construction… so its size is reported alongside the ceilings, not folded in silently" — the precedent for reporting rather than folding already exists in the pre-registration.
- **(c) Compute 4b as one stratum-pooled Mann-Whitney statistic** — sum concordant pairs over within-stratum (relevant, non-relevant) pairs only, across all strata — so a stratum contributing zero pairs simply contributes nothing and no stratum is ever "undefined". Keeps the full population in one number; loses the per-stratum breakdown the spec's "between papers of the same vintage" framing implies.
  **Evidence:** `[totality]` the pairing is well-defined for every stratum because a stratum with 0 relevant or 0 non-relevant members contributes 0 pairs rather than an undefined ratio; no stratum count is needed. `UNVERIFIED:` whether the spec's 4c age-preserving null (which permutes *within* strata) would still be comparable to a pooled 4b — 4c is out of this plan's scope, so the compatibility is untested here.

No listed option dominates: (a) and (b) preserve the per-stratum reporting the spec's §4c null needs and differ only in whether a degenerate stratum stops the run; (c) removes the failure mode entirely but changes what "within-stratum AUC" reports. A hybrid of (b) and (c) — pooled statistic **plus** a per-stratum table with degenerate strata marked — is the closest thing to a dominating variant and is listed here so the choice is made on a complete menu; it costs the extra table and nothing else.

**Why this cannot be settled in-round.** `[totality]` the stratum × relevance cross-tab requires publication years, and none exist on disk: `corpus.jsonl`'s `metadata` field is `{}` for all 5,183 records (checked exhaustively — one distinct key set, `('_id','metadata','text','title')`, count 5,183), and `citations-countonly.json.bak` has top-level keys `['counts','unresolved']` only. The only source is the Semantic Scholar fetch Task 1 performs, which this review does not run.

### 3.2 — How measurement 1's per-query AUCs are aggregated, and which standard error that admits

**The choice.** Task 2 Step 1 specifies measurement 1 as "within each query's own retrieved pool, the AUC of relevant vs non-relevant documents by citation count, **aggregated across queries**, with the **Hanley–McNeil** standard error and a 95% CI". Those two clauses select different estimators. Hanley–McNeil is defined for a single two-sample AUC from one `(n_pos, n_neg)`; it has no definition for a mean of per-query AUCs. The plan does not say which is computed.

**Why it's forced.** The data makes the two estimators numerically and structurally different, and the difference is not a rounding detail. Measured over the real run × qrels: 275 of 300 queries are eligible (25 have no in-pool positive at all), and of those 275, **253 have exactly one** in-pool relevant document (`{1: 253, 2: 13, 3: 4, 4: 5}`). A per-query AUC is therefore, for 92% of queries, a single-positive rank statistic over ~49 negatives, and a mean over 275 such values is a different quantity from one pooled AUC over `n_pos = 311, n_neg = 13,439`. The Hanley–McNeil SE on the pooled form would treat 4,179,529 pairs as independent when they are drawn from only **4,325 distinct** non-relevant documents reused across overlapping pools, understating the SE. The plan has to emit one number and one CI, and the spec's Phase 0 "Output" says this number "may inform the choice among {lifetime arm only, lifetime + decayed arm, abandon}".

**Options.**

- **(a) One pooled AUC over all in-pool (relevant, non-relevant) pairs, with the Hanley–McNeil SE as written.** Directly comparable to the 0.6201 already on record, which the spec reports with a Hanley–McNeil SE on `n = 174 vs 370`. The CI is anti-conservative because the same non-relevant document recurs across pools.
  **Evidence:** `[presence]` spec Phase 1 measurement 1 — "Against *random* corpus documents this is already 0.6201 (n = 174 vs. 370; Hanley–McNeil SE 0.0264; 95% CI [0.569, 0.672]; z ≈ 4.55)" — the on-record precedent is the pooled two-sample form. `[totality]` the pooled counts on the real inputs are `n_pos = 311`, `n_neg = 13,439`, drawn from 4,325 distinct non-relevant documents (probe over `sci-2048.similar.trec` × `qrels/test.tsv`).
- **(b) Mean of the 275 per-query AUCs, with a CI from the between-query spread** (the queries, not the pairs, are the independent units). Honest about the dependence structure; not comparable to the recorded 0.6201, and built on 253 single-positive statistics.
  **Evidence:** `[totality]` in-pool positives per query measured as `{0: 25, 1: 253, 2: 13, 3: 4, 4: 5}` over all 300 queries; 275 eligible. `UNVERIFIED:` the resulting CI width — it depends on counts the fetch has not yet produced.
- **(c) Report both, and say which the arm-structure read is taken on.** Costs one extra line in the gate doc; removes the ambiguity from the record permanently. Consistent with the gate doc's stated job ("It reports numbers and draws no conclusion").
  **Evidence:** `[presence]` plan Task 2 Step 3 already requires the gate doc to record "the three measurements with their CIs", so a second estimator is an added row, not a restructure.

No combination dominates: (a) alone keeps comparability with the recorded 0.6201 but ships a CI that is known to be too narrow; (b) alone fixes the CI but breaks comparability with the one prior number Phase 0 exists to correct; (c) is their union and is listed precisely so it is not discovered after the choice.

---

## 5. Recommendation

🛑 **Surface forced decisions to user** — §3 is non-empty (two items), and §1 carries one failed assumption (#22) with three §2 literal-wrongness findings attached. §3.1 and §3.2 both need a decision before Task 2's script is written; §2.1–§2.3 are mechanical and can be applied via `update-implementation-plan` (or by hand) once those are settled.
