# Critical Implementation Review: 2026-09-06-tier1-retrieval-defaults-implementation-plan (Round 1)

**Plan:** /home/ben/repositories/Iverson/docs/plans/2026-09-06-tier1-retrieval-defaults-implementation-plan.md
**Verified plan-level assumptions section:** present

⚠️ 1 commit since plan-write time (SHA `62f1567`); cited file:line references re-checked under §1. (The one commit is `e1de1e4`, the plan's own addition; no source file moved.)

Method note: every self-contained code block was executed, not just read. Task 1 and Task 2 were applied verbatim in a scratch worktree at `e1de1e4` (removed afterwards): `Iverson.Vector.Tests` 134/134 green; `Iverson.Api.Tests` (filtered to the two substitute-only classes, 142 tests) green; `Iverson.LoadTest.Tests` 54/54 green; `dotnet build Iverson.slnx` 0 errors; `docker compose config` grep = 2. Mutations: hard-coding `0.70` at the SearchSimilar site fails exactly `SearchSimilar_UsesConfiguredLambdaSimilar_NotAHardCodedOne` (141/142); at the SearchChunks site exactly `SearchChunks_UsesConfiguredLambdaChunks_NotAHardCodedOne`; the three `ChunkDiversity` mutations (distinct-before-take, at50→Take(10), no-distinct) each fail its tests. The plan's Python (`similar_arms.py`, its tests, the nugget filter from docstring + prose, the two new `test_report.py` tests) was assembled beside symlinks to the real scripts and run under pytest: `test_similar_arms` 3/3, `test_freshstack_nugget_qrels` 2/2, the two `test_report.py` tests **failed** (§2.1). The `similar_arms.py` smoke ran read-only against the live baseline (150 rows per arm, correct line format, the arms differ on 139/150 rows, point counts unchanged); the nugget filter ran on the real slice (672/672 queries, all with a rel>0 row, 0 doc ids outside the slice). pyndeval is absent (no gcc), as the plan itself states; nothing below depends on it.

## 0. Coverage enumeration

**Task 1 — Phase A**
- Options code (`LambdaSimilar`/`LambdaChunks`, both 0.70): ok — applied verbatim; `AddVectorRanking_Defaults_AreSeventyPercentOnBothEndpoints` resolves 0.70/0.70 from a `ServiceCollection`.
- Validation code, obsolete-key check placed before `Bind`, `section["Lambda"]` semantics: ok — `Bind` ignores the unknown `Lambda` key (default binder), so the pre-Bind indexer check is the only guard; `AddVectorRanking_ObsoleteLambdaKey_Throws_NamingBothNewKeys` throws with both names; the exact-key indexer does not fire on `LambdaSimilar`/`LambdaChunks` (over-inclusion checked), and an empty `VectorRanking__Lambda=` still throws (under-inclusion checked).
- Diversifier signature / `_o` removal: ok — compiles; `_o.Lambda` has 3 references at `:77-78`, not the "both occurrences" the prose says; a replace-all is what any executor does, no execution impact.
- Service ctor + both call sites; DI resolution of `IOptions<VectorRankingOptions>`: ok — `Iverson.Api/Program.cs:188` `AddVectorRanking(cfg)` registers `AddSingleton(Options.Create(opts))` under the static type `IOptions<VectorRankingOptions>`, the registration `ResultReranker` already resolves through; no other host constructs the service (grep).
- Compose lines: ok — rendered form `VectorRanking__LambdaSimilar: "0.70"` / `LambdaChunks: "0.70"`; the plan's step-4 grep counts 2; the old `VectorRanking__Lambda:` line count 0.
- `VectorRankingOptionsTests` edits (rename entries, split range test, two new tests, rewritten non-default test): ok — green.
- `ResultDiversifierTests` 13-site rename: ok — the count is **12** (`grep -c 'Diversify('` = 12); the compiler finds every site, no execution impact. λ=1.00 both-branches test green.
- The 6 construction sites and 3 service-ctor sites: ok — all rewritten as instructed; the parameter order (ranking before decay) compiles.
- Wiring tests' comment-only bodies: ok — writable without discovery by copying `:2680` and `:3075` verbatim and changing the options object and expected ids; both discriminate (mutation evidence above), and a cross-wired site (`LambdaChunks` at the Similar site or vice versa) is also caught by construction of the swapped assertions.
- Step 2/4/5 commands: ok — commands run; commit paths exist.
- Global Constraint "no behaviour change": ok — every pre-existing ranking test passes with only the enumerated mechanical edits.

**Task 2 — harness flag + sidecar**
- `ChunkDiversity.cs` / tests: ok — compiles under `Iverson.LoadTest.Benchmark` with implicit usings; FluentAssertions accepts the tuple `Be((0, 0))`; all three mutations caught.
- `CommandFlags` + `IntFlag` parse failure mode: ok — `--chunk-budget-multiplier 11` → 11, default 5; a malformed value silently falls back to 5, which the guard then REFUSES at 10.79 chunks/doc — exactly the loud stop Task 6 step 3 names.
- Help text: ok — one added line; `grep -c` = 1.
- Scenario threading (`:113`, `:369`, REFUSING text `:121,123,130`): ok — compiles; the guard reads the flag.
- Meta sidecar `["chunkBudgetMultiplier"]` and Task 5's grep: ok — `JsonObject` int with `WriteIndented` serialises `"chunkBudgetMultiplier": 5`, matched by `"chunkBudgetMultiplier":[[:space:]]*5`.
- Diversity sidecar (JSON shape, doubles, write ordering, partial-output on failure): ok — `Average()` doubles serialise as `7.83` or `8` (Python reads both); written after the two `TrecRunWriter` calls and before the failures/unresolved throws, so it exists whenever the run files do; a failed RPC yields (0, 0) for that query.
- Raw hits taken before the collapse: ok — `chunks` is the streamed list at `:372-378`, post-MMR from the API at chunk granularity, pre-`MaxPassageAggregator` — the caller-visible list the spec asks for.
- Step 3 smoke / step 4 commit: ok — `--help` needs the live stack (it registers schemas, multivector plan `:781`), which Task 2 has.

**Task 3 — report.py, nugget qrels, pyndeval**
- Step 1 pyndeval gate: ok — `which gcc` is empty on this box, the plan gates on it, and nothing else in the task is committed until `ok` prints.
- Step 2 tests: → §2.1 (row shape). The α-nDCG `"1.0000"` assertion does discriminate: with the 4-query `qrels_path` fixture the run's nDCG@10/R@50/AP print `0.2500` (verified with the real `report.py`), so only α-nDCG can print `1.0000`.
- Step 3 routing by measure at `:285` (`score_run`): → §2.2.
- Step 3 routing at `:402` / `:559-566`: ok — `per_query_values` re-reads the run per call; `baseline_run` is already a materialised list; Holm family stays per measure (3 for the rule 7.2 invocation).
- Diversity sidecar path derivation: ok — `sidecar_path_for("…/fs-2048-l070.chunks.trec")` → `…/fs-2048-l070.meta.json` → `…/fs-2048-l070.chunks.diversity.json`, the name Task 2 writes; `.similar.trec` and raw runs are excluded (both directions checked). Print format `  distinct parents @10  7.50` matches the test's substrings.
- `qrels_for` predicate: ok — `str(alpha_nDCG@10)` = `'alpha_nDCG@10'`; `nDCG@10` does not match.
- Step 4 nugget filter (docstring + prose + tests): ok — a body written from the prose alone passes both tests; on the real slice 672/672 queries covered, all 672 have a rel>0 row (so rel-0-only queries — the one over-inclusion the "keeps rel as-is" rule could admit — do not occur), 0 doc ids outside the slice.
- Step 5/6 commands: ok.

**Task 4 — `similar_arms.py`**
- Code block, imports (`multivector.trec_lines/require_collection`, `ingest.embed/qdrant_request/DEFAULT_OBJECT_COLLECTION`): ok — all exist with the used signatures; `ingest.py`'s import-time contract load resolves relative to its own directory.
- `rank_hits` fail-loud rules (missing `docId`, `< limit`), partial output in `finally`: ok — tests pass; the `finally` writes what it has.
- Step 3 smoke on the live baseline: ok — the three SciFact queries embed on bge-base and search both named vectors; 150 rows per arm; first line `1 Q0 14103509 1 0.585929 centroid-raw`.

**Task 5 — `sci-2048`**
- Step 1 preconditions (dry-run service names, container list, point counts, TEI model, snapshot set, rebuild, `docker inspect` grep, composite, pyndeval): ok — six service names exist; live box is 19,967/768, 5,183, `BAAI/bge-base-en-v1.5`, both named vectors present, 2 snapshots; the rebuild + `--no-deps` recreate picks up the compose change (config-hash differs) and the inspect grep matches only the two new keys.
- Step 2 ingest block + counts: ok — copied from the multivector plan with the window substituted; `$K` from step 1 is used in the same shell; a fresh shell makes the curl print nothing, which is a visible non-match against `# 6587`.
- Step 3 snapshot / RESTORE.md: ok — convention as `:760-770`.
- Step 4 run + raw arms + three reports: ok — `report.py` argument shapes match `main()`; `$M1/runs/bge-base.chunks.trec` exists; `similar_arms.py --run-dir $A` reads `$A/beir/queries.jsonl` copied in step 2.

**Task 6 — `fs-2048`**
- Step 1 dirs, `qrels.trec` copy, nugget filter, `awk … 672`: ok — verified on the real files.
- Step 2 ingest at 2048/1792, 18,622: ok — inherited counts (A12).
- Step 3 base run at multiplier 11, 33,600 rows, REFUSING stop: ok.
- Step 4 sweep (labels `l050/l085/l100`, env values bind as doubles, `docker inspect` check), restore-to-defaults from a var-less shell: ok — compose recreates on any config-hash change, so `up -d --no-deps` with the vars absent recreates at 0.70; the inline-env form never exports into the shell.
- Step 5 raw arms and the three reports; the four `distinct parents @10` lines: ok — four `.chunks` runs, each with a sidecar.
- Cross-shell env (`$B`, `$D`, `$R`, `$K`) across a ≈ 22 h task: dropped — an unset variable fails loud at every use (`cp /qrels…`, `--corpus-path` required, `--run /runs/…: not a file`), never a wrong outcome.

**Task 7 — `fs-512`, window comparison, restore**
- Steps 1–5 by substitution, `cp $B/qrels.nugget.trec`, rule 7.1(b) command: ok — same loud-failure analysis for `$B`.
- Step 6 restore block: ok — the `%%-6802952876034638*` pattern matches both snapshot filenames; upload with `priority=snapshot` as before; the TEI recreate is correctly omitted (bge-base throughout).

**Task 8 — gate document and consequences**
- Step 1 gate document shape: ok — template exists.
- Step 2 rule 7.2 default move + "suites green": → §2.3.
- Step 3 commit: ok — `git add -f` for the gitignored-but-tracked path.
- Step 4 gated change set (every cited line still holds the default; test commands; contract regeneration; commit paths): ok — all nine cites verified; `IVERSON_REGENERATE_INGEST_CONTRACT=1` path at `IngestContractTests.cs:85`; toolchains present.

**Cross-task contracts**
- Task 1 env names → compose → Tasks 6–7 `docker inspect` grep: ok.
- Task 2 sidecar → Task 3 reader (path + key names `meanDistinctParentsAt10/50`): ok.
- Task 2 meta key → Task 5 grep: ok.
- Task 3 nugget file → Tasks 6–7 → `report.py --nugget-qrels` (4-column, nugget in the iteration column, same doc-id space as the runs): ok.
- Task 4 outputs → §6 invocations (`<run>/runs/{head,centroid}-raw.similar.trec`): ok.
- Task 7 `report-rule71b.txt` → Task 8: ok.
- Regenerated contract → `ingest.py` default window: ok — `CONTRACT["chunkWindow"]` is read at import.
- Api MMR fixture tests → the options default (an implicit contract Task 8 changes): → §2.3.

## 1. Verified-plan-assumptions cross-check

- P1 still holds. P2 still holds. P3 still holds.
- P4 still holds, except the `ResultDiversifierTests` site count is 12, not 13 (`grep -c 'Diversify('`); no execution impact.
- P5 still holds. P6 still holds. P7 still holds (6 + 3 sites by grep). P8 still holds (executed). P9 still holds. P10 still holds. P11 still holds. P12 still holds. P13 still holds.
- P14 still holds as stated — `write_run(path, rows, tag)` with rows `[(qid, [docid, …])]` — which is exactly why the plan's own test blocks fail (§2.1).
- P15 still holds (`str(alpha_nDCG@10)` = `alpha_nDCG@10`). P16 still holds (tab-separated 4-column, rel ∈ {0,1}; 20,209 rows; 672 queries). P17 still holds. P18 still holds. P19 still holds (ran live). P20 still holds. P21 still holds. P22 still holds. P23 still holds. P24 still holds. P25 still holds (`cf9cbb8` on main). P26/P27 still hold. P28 still holds. P30 still holds. P32 still holds. P31 still holds. P33 still holds. P34 still holds. P35 still holds (mvn, java, go, node, dotnet on PATH). P36 still holds.

Span check:
- `ir_measures.read_trec_run` returns a generator (`report.py:553-558` documents the trap for the baseline; nothing covers `score_run`). Verified in-round: a second `calc_aggregate` on the same generator returns 0.0 → §2.2.
- The Api MMR fixture tests (`:2680`, `:3075`, via the shared `_sut` at `:78`) depend on `new VectorRankingOptions()` being 0.70 — no assumption scopes what Task 8's default move does to them. Verified in-round → §2.3.
- `ir_measures.calc_aggregate` averages over the qrels' query set (so the α-nDCG test's `1.0000` cannot come from nDCG@10 on the 4-query fixture): verified in-round (`nDCG@10 0.2500`); no finding.
- The API host resolves `IOptions<VectorRankingOptions>` for the new ctor parameter: verified in-round (`Program.cs:188` + the `Defaults` test resolving it from a `ServiceCollection`); no finding.

## 2. Literal-wrongness findings

1. **Task 3 step 2's two `test_report.py` tests call `write_run` with the wrong row shape and crash at setup.** `write_run(path, rows, tag)` takes rows as `[(qid, [docid, …])]` (`test_report.py:25-30`, and P14 says so); the plan passes `[("q1", "d1", 1, 0.9), ("q1", "d2", 2, 0.8)]` and `[("q1", "d1", 1, 0.9)]`, while claiming "`write_run`'s row tuples follow its existing signature". Evidence (pytest, both tests): `test_report.py:28: ValueError: too many values to unpack (expected 2, got 4)` — raised before argparse, so this is not the missing pyndeval. Fix: `write_run(run, [("q1", ["d1", "d2"])])` and `write_run(run, [("q1", ["d1"])])` (ranks and descending scores are generated by the helper).

2. **Task 3 step 3's "route by measure at every `calc_aggregate` call site" zeroes every measure after the first in `score_run` — including without `--nugget-qrels` — and no test pins it.** `score_run` (`report.py:282-286`) binds `run = ir_measures.read_trec_run(run_path)`, a generator, and calls `calc_aggregate(measures, qrels, run)` once with the whole list. Routing "by measure" at that site means more than one `calc_aggregate` call on the one generator; the first consumes it and every later call scores an empty run as 0.0 without error — the A34 shape the file documents at `:553-558` for the baseline. Runtime trace of the plan's instruction applied literally to the fixture run (no nugget file): `{'nDCG@10': 0.25, 'R@50': 0.0, 'AP': 0.0}`. This corrupts the `[scores]` block of every invocation (the reported-only scores the gate document's tables quote) while the paired compare blocks stay correct, so the two sections silently disagree; the only existing `main()`-driving test (`test_report.py:172-179`) exits before scoring, and the new α-nDCG test asserts only `"1.0000"`, which one ordering of the calls (α first) satisfies while printing `R@50 0.0000 / AP 0.0000`. Fix: in `score_run`, `run = list(ir_measures.read_trec_run(run_path))` and merge the results of one call per qrels group (`calc_aggregate(base_measures, qrels, run)` ∪ `calc_aggregate([alpha_nDCG@10], nugget_qrels, run)`); and make the new test assert the whole block — `nDCG@10    0.2500`, `R@50       0.2500`, `AP         0.2500` and `alpha_nDCG@10 1.0000` — so either ordering is caught.

3. **Task 8 step 2's change set cannot leave `Iverson.Api.Tests` green when rule 7.2 moves a default.** The step names `VectorRankingOptions.cs`, the compose fallbacks and `AddVectorRanking_Defaults_AreSeventyPercentOnBothEndpoints`, then says both suites are green. The Api MMR fixtures build `_sut` (`ObjectSearchGrpcServiceTests.cs:74-79`) with `Options.Create(new VectorRankingOptions())` and hand-compute their expectations at λ = 0.70. Verified in the scratch worktree with both defaults at 1.00 — the rule's own "none qualifies → 1.00" branch: `SearchSimilar_PromotesDissimilarCandidate_OverNearDuplicate_DespiteLowerFusedScore` and `SearchChunks_SuppressesNearDuplicatePassage_ButNotDissimilarPassage_EvenSharingOneParent` fail (140/142). Fix: in Task 1, construct the shared `_sut` at `:78` (and the sites at `:2612`, `ObjectSearchVectorIntegrationTests.cs:93`, `DocumentTemplateValidationTests.cs:337` for consistency) with `Options.Create(new VectorRankingOptions { LambdaSimilar = 0.70, LambdaChunks = 0.70 })` so the arithmetic fixtures pin the λ their comments assume and no default move can touch them; Task 8 step 2 then correctly changes only the Vector default test. (Alternatively, list those two tests under Task 8 step 2's "only where the rule moves a value" and re-derive their expected ids at the new value.)

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ Approve with literal-wrongness fixes — §1 has no failed assumptions; §2 has three findings (one static test-setup crash, one runtime scoring corruption the plan's tests cannot see, one impossible-as-written "green" step in Task 8); §3 is empty.
