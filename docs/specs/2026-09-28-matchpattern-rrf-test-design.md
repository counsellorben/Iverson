# MatchPattern and RRF: Feasibility and Quality Test Design

**Status:** approved design, 2026-09-28. The work produces tests, harness scripts and a gate document. It changes no production code.

## Goal

Answer "does MATCH_RECOGNIZE (the `MatchPattern` RPC) support reciprocal rank fusion (RRF)?" under all three readings of the question:

1. **RRF inside one request.** Can `define`/`measures` alone compute an RRF score per row?
2. **RRF over its output.** Can a `MatchPattern` result serve as one ranked list, RRF-fused with `SearchChunks`, and does that improve retrieval quality?
3. **Matching over an RRF order.** Can a pattern run over rows ordered by an RRF-fused ranking?

Readings 1 and 3 get feasibility verdicts pinned by tests. Reading 2 gets a feasibility verdict and a quality verdict from a FreshStack-2048 benchmark.

## Expected verdicts (from probes run during design)

| Reading | Expected verdict | Basis |
|---|---|---|
| 1 | **Partial.** The `order_by` leg is expressible: `1.0 / (60 + RUNNING COUNT(*))`, or the reverse rank `FINAL COUNT(*) - RUNNING COUNT(*) + 1`. A rank by `SIMILARITY` is not expressible. Score normalisation (`s / FINAL MAX(s)`) is expressible, but that is score fusion, not RRF. | An engine probe against `PatternQuery.Compile`/`Run`. The correlated count `FINAL SUM(CASE WHEN SIMILARITY(A.Title,'q') > SIMILARITY(Title,'q') THEN 1 ELSE 0 END)` is rejected: `PatternValidationException: All labels and classifiers inside the call to 'SUM' must match.` |
| 2 | **Feasible client-side.** No server-side fusion exists; the row-pattern spec put "a pattern result as a ranking signal inside SearchSimilar/SearchChunks" out of scope. | The RPC returns ordinary rows (spec `2026-09-17-row-pattern-matching-design.md`, Out of scope). |
| 3 | **Not possible today.** `order_by` names stored properties only. An RRF order exists only inside the unmerged `hybrid-rrf-search` branch. | Row-pattern spec §2: `order_by` properties are validated. |

## Part 1: Feasibility tests (readings 1 and 3)

- **`Iverson.Server/Iverson.Patterns.Tests/RrfExpressivenessTests.cs`** (new) pins four behaviours:
  1. `1.0 / (60 + RUNNING COUNT(*))` under ALL ROWS yields `1/61, 1/62, …` in `order_by` order.
  2. `FINAL COUNT(*) - RUNNING COUNT(*) + 1` yields the reverse rank.
  3. `SIMILARITY(...) / FINAL MAX(SIMILARITY(...))` yields scores normalised to 1.
  4. The correlated rank-by-similarity count throws `PatternValidationException` with "must match".
  
  If a later engine change adds ranking, test 4 fails, which is the signal that the reading-1 verdict must be revisited.
- **Reading 3:** an Api-level test in `Iverson.Server/Iverson.Api.Tests/Grpc/MatchPatternGrpcServiceTests.cs` asserts that a TYPE_ROWS request whose `order_by` names a property the type does not have returns `InvalidArgument`. No such end-to-end test exists today. The nearest ones are `Store_exceptions_map_as_the_spec_section_6_table_says` (`:428`), which uses a mocked store exception, and the StarRocks builder test `Every_slot_rejects_an_unknown_hidden_tenant_or_bytes_column`.

## Part 2: Quality benchmark (reading 2)

**Corpus: FreshStack-2048** (6,000 documents, 18,622 chunks, bge-base, 2048/1792 window, 672 judged queries).
- 57.2% of its documents have at least 3 chunks. On SciFact-2048 only 0.5% do (25 of 5,183), which makes any pattern over consecutive chunks degenerate there.
- α-nDCG nugget qrels exist (`qrels.nugget.trec`).

### Phase 0: restore and the two go/no-go probes

1. **Stack configuration.** Bring up the stack with two settings:
   - `Patterns__Limits__MaxExpressionLength=25000` (user decision; the longest wrapped query is 21,333 characters);
   - `VectorRanking__LambdaChunks=0.70`, the shipped value.
   
   Build `iverson-api` first, because a stale image is a known trap.
2. **Restore the snapshots under today's names.** Restore `freshstack-2048-qdrant-snapshots` into today's collection names: `benchmark_documents_tenant_bypass_a8j5vpgduxk1bsvma7542yqnk` and `benchmark_documents_chunks_tenant_bypass_a8j5vpgduxk1bsvma7542yqnk` (`scripts/ingest-contract.json`). Do not restore to the snapshot-file names the old restore loop derives, because they predate the tenant fingerprint (`IntelligenceTenantScope.cs:40-44`, commit `09ef5dba`).
3. **Register the schema.** `benchmark-query` registers the `BenchmarkDocument` schema. `tenant_bypass` is seeded by the API at startup (`Tenancy__SeedLegacyTenants=true`).
4. **Probe A (readability).** One `CHUNKS` `MatchPattern` call with `where Id = <one candidate GUID>` returns that parent's chunks with a non-NULL `SIMILARITY`. If it fails, stop.
5. **Probe B (call shape, a rule fixed now by user decision).** On 10 queries, time both shapes, for the calibration call as well as the θ call:
   - **Per-candidate:** one call per (query, candidate), with `where Id = <parent GUID>`.
   - **Whole-corpus:** one call per query with no `where`. This scans all 18,622 chunks, under `MaxRowsScanned` = 100,000, and keeps only the candidates client-side.
   
   Then pick one by this rule:
   - Use **per-candidate** if its projected full run (672 × 50 calls per θ) is ≤ 8 h per θ.
   - Otherwise use **whole-corpus**. `MaxOutputRows` is then raised on the benchmark stack for every call whose output can exceed 10,000 rows. The calibration pass emits one row per chunk (18,622), so it runs with `MaxOutputRows` ≥ 18,623 and `limit` 18,623, which keeps a count at `limit` meaning truncation. A θ pass whose match count exceeds 10,000 is raised the same way. The gate document records the values.
   - If neither shape fits (per-candidate over 8 h, and whole-corpus calls, calibration or θ, hitting the 30 s timeout), stop and return to the user.
   
   The server has no query-embedding cache, so every call embeds the query once.

### Phase 1: re-record the baseline on this build

Run `benchmark-query` on the shipped chunks settings (`--chunk-budget-multiplier 11`, `LambdaChunks` 0.70) to produce `<label>.chunks.trec` plus `<label>.meta.json`. The pattern leg must run on this same build composite, which Phase 2 measures; the 2026-09-07 `fs-2048-l070` run is composite `9714c660b365fad1`. The `.chunks.trec` file is document-level: 672 queries × 50 BEIR doc ids. `keymap.json` maps each Iverson GUID to its BEIR doc id.

### Phase 2: the pattern leg (`Iverson.Server/Iverson.LoadTest/scripts/pattern_leg.py`)

**Client.** Python SDK: `IversonClient(..., acting_user_token=…)`, then `match_pattern(type).chunks(...).where(...).pattern(...).define(...).measure(...).rows_per_match(...).limit(...)`. The acting-user token comes from `deploy/scripts/mint_acting_user_token.py --target compose --username iverson-loadtest-bypass-user`, and is re-minted if a long run outlives it.

**Calibration.** One similarity pass, fixed in advance, runs over all 672 queries' candidates, in the call shape Phase 0 picked:
- **Call:** `A+`, `A AS TRUE`, ALL ROWS, measure `s = SIMILARITY(text, '<query>')`.
- **θ:** the 50th, 75th and 90th percentiles of `s` over the chunks of the 100 lowest query ids' candidates.
- **Guard input:** the same pass gives every candidate's max-chunk similarity, which the degeneracy guard uses.

The pass reads similarity values only, never relevance labels.

**The pattern, per θ:**
- `A+`, `A AS SIMILARITY(text, '<query>') > θ`, ONE ROW PER MATCH;
- measures `run_len = COUNT(*)` and `run_sum = SUM(SIMILARITY(A.text, '<query>'))`;
- the query is escaped with `''`;
- the partition is by `parent_key`, in `chunk_index` order, both implicit for `CHUNKS`.

**Scoring.**
- A document's score is its best run, under `run_len` or under `run_sum`. Ties break by the other score, then by the baseline rank.
- A candidate with no run gets no pattern rank.
- Each (θ, score) pair produces one TREC run: 6 runs in all.

**Failure.** Any `MatchPattern` error aborts the whole run: timeout, `InvalidArgument`, a stream ended early, or a result count at `limit` (possible truncation). A failed query is never read as "no runs". This follows the benchmark rule that runs never silently degrade.

**Build identity.** `pattern_leg.py` GETs `/build` (anonymous, listener port 8081) before and after every pass (calibration and each θ). It aborts if the `composite` differs from the baseline sidecar's (user decision, CDR round 1 §3.2).

### Phase 3: fusion and scoring (`Iverson.Server/Iverson.LoadTest/scripts/rrf_fuse.py`)

- **Fusion.** RRF with k = 60, fixed in advance and not tuned: `score = 1/(60 + rank_baseline) + 1/(60 + rank_pattern)`. The second term is omitted for a document with no pattern rank. Fusion covers only the baseline's 50 candidates, so every fused run has the baseline's document set (`report.py` `check_pool` requires this). The fused order is RRF score descending, with ties broken by baseline rank (unique per query), so it is strict. Swapped ranks tie exactly (`1/63 + 1/67 == 1/67 + 1/63`), and ir_measures breaks score ties by doc id rather than file order. So `rrf_fuse.py` writes a strictly decreasing score in the fused order (`51 − fused_rank`) and records the RRF values in a sidecar file, which makes the scored order equal the file order `check_pool` reads.
- **Scoring.** `report.py --run … --qrels qrels.trec --nugget-qrels qrels.nugget.trec --pair <fused>=<baseline>` (repeated per arm) produces nDCG@10, R@50, AP and α-nDCG@10, with a paired permutation test and Holm correction across the declared pairs.
- **INERT arms.** An arm that reorders fewer than 25% of queries fails `check_pool`. Such an arm is reported as **INERT** and left out of the `--pair` set, so the run does not abort.

### Degeneracy guard (fixed in advance, run before any quality number is read)

**Measurement.** For each arm, compute Spearman ρ, over all (query, candidate) pairs, between the arm's primary score (`run_len` or `run_sum`) and the two inputs below. A candidate with no run is included at a floor score strictly below every matched score in the arm (user decision, CDR round 1 §3.1):
- (a) the document's chunk count;
- (b) the document's max-chunk similarity, from the calibration pass.

**Rule.** |ρ| ≥ 0.95 against either one marks the arm **DEGENERATE**. It still reports its numbers, but it cannot produce a GO.

**Why.** Chunk-coverage phase 2 measured a count in disguise (ρ = 0.9970 with the tail count).

**Accepted consequence.** Including no-run candidates counts the threshold split as part of the arm. Having a run is by construction the same as a max-chunk similarity above θ, so an arm whose only effect is that split correlates with (b) by construction and can be marked DEGENERATE. This is accepted (user decision, CDR round 1 §3.1).

### Gate

- **GO:** at least one arm that is neither DEGENERATE nor INERT improves nDCG@10 over the re-recorded baseline, with Holm-adjusted p < 0.05.
- **NO-GO:** every such arm is null or negative.
- **VOID:** every arm is DEGENERATE or INERT; the benchmark measured nothing new.
- **Context only:** α-nDCG@10 and AP.
- **Integrity:** R@50 must equal the baseline's, because fusion only re-ranks. A change is an integrity failure. `rrf_fuse.py` writes the composite `pattern_leg.py` measured into each fused run's `.meta.json`, so `report.py`'s build check compares measured values (user decision, CDR round 1 §3.2).

## Deliverables

- `Iverson.Server/Iverson.Patterns.Tests/RrfExpressivenessTests.cs`
- The reading-3 test in `Iverson.Server/Iverson.Api.Tests/Grpc/MatchPatternGrpcServiceTests.cs`
- `scripts/pattern_leg.py` and `scripts/rrf_fuse.py`, each with a `test_*.py` in the pytest style of the existing scripts. The tests cover:
  - RRF arithmetic, including the missing-pattern-rank branch;
  - best-run selection and tie-breaks;
  - quote escaping;
  - the abort on error, truncation, or a build-composite mismatch;
  - a swapped-rank pair gives a strict fused order and strictly decreasing written scores.
- `docs/plans/2026-09-GATE-matchpattern-rrf.md`, containing:
  - the three verdicts;
  - the Phase 0 probe results, including the call shape picked and its timings;
  - the θ values;
  - ρ per arm;
  - the scored table;
  - the md5 of every run file;
  - the non-default limits used.
- Benchmark artefacts under `/home/ben/repositories/iverson-benchmark-corpora/matchpattern-rrf-<date>/`, never under `/tmp`.

## Out of scope

- **Any production change**, including server-side fusion, an `IN` filter on the `CHUNKS` source, and a query-embedding cache.
- **Tuning k, or weighted RRF.** k = 60 is fixed.
- **A quality benchmark for readings 1 and 3.** Neither is feasible.
- **SciFact and other corpora.**

## Known issues accepted

- **Non-default limits.** The benchmark runs with a non-default `MaxExpressionLength` (25,000), and possibly `MaxOutputRows`. A caller on default limits could not issue 64% of these queries (433 of 672 have at least one expression over 1,000 characters once wrapped and quote-escaped). The gate document recomputes this figure from the exact strings `pattern_leg.py` sends and states it next to the verdict.
- **θ is fixed, not tuned.** The ladder is the similarity percentiles above. A NO-GO speaks for these θ values and k = 60 only.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| 1 | The engine computes an `order_by` rank, a reverse rank and a normalised similarity. It rejects a correlated rank by similarity. | A probe program against `Iverson.Patterns` (P1–P7) printed `r=1..5`, `rrf1=0.01639…`, `rdesc=5..1` and `norm=…/0.9`. P4 threw "All labels and classifiers inside the call to 'SUM' must match". P5 (PREV inside an aggregate) was rejected too. |
| 2 | The snapshots need today's fingerprinted names. | `Iverson.Vector/IntelligenceTenantScope.cs:40-44`; `scripts/ingest-contract.json:15-18`; `ingest.py:192-201` notes that the old names are unreachable. |
| 3 | `benchmark-query` registers the `BenchmarkDocument` schema, and the API seeds `tenant_bypass`. | `Iverson.LoadTest/Program.cs:34-35`, `167-187`; `Iverson.Api/Program.cs:723-727`; `docker-compose.yml:498`, `:599`. |
| 4 | Chunk rows are `{parent_key, chunk_index, text}`. `SIMILARITY` is legal only on `text` and uses each chunk's own `body_vector`. | `ObjectSearchGrpcService.MatchPattern.cs:412-419`, `:113`; `CompiledPattern.cs:189-206`; `SimilarityResolver.ScoreVectors`; `ingest.py:681`. |
| 5 | A `CHUNKS` `where` accepts only EQUALS on the key column or an `[IversonMetadata]` column. It has no `IN`, and `OR` is rejected. | `ObjectSearchGrpcService.cs:1138-1181` (`BuildChunksFilter`); `MatchPattern.cs:301-302`. |
| 6 | `CHUNKS` requires `partition_by` and `order_by` to be empty. It partitions by `parent_key` and orders by `chunk_index`. | `MatchPattern.cs:297-300`, `:118`; `CompiledPattern.cs:122-128`; `QdrantChunkRowSource.cs:50-56`. |
| 7 | `SIMILARITY` and `SearchChunks` both embed in query mode, with the same prefix. | `SimilarityResolver.cs:29` and `ObjectSearchGrpcService.cs:561` both call `EmbedQueryAsync`; `EmbeddingService.cs:143-147`. |
| 8 | `''` escapes a quote. `MaxExpressionLength` applies to each expression and is configurable. 433 of 672 FreshStack queries have at least one expression (the θ define, the `run_sum` measure or the calibration measure) over 1,000 characters once wrapped and quote-escaped; the maximum wrapped length is 21,333. | `ExpressionLexer.cs:225-229`; `MatchPattern.cs:287-290`; `Program.cs:320`; measured on `beir/queries.jsonl` (recomputed in CDR round 1: 433 / 432 / 432 per expression). |
| 9 | A repeated `SIMILARITY` term counts once, and `SUM(SIMILARITY(A.text,'q'))` parses. No test covers an aggregate over `SIMILARITY`, so Phase 0 probe A exercises it live. | `SimilarityTermTable.cs:11-19`; `ExpressionParser.cs:595-630`, `:731-736`. |
| 10 | The Python SDK builder has `chunks()`, `where()`, `define()`, `measure()`, `rows_per_match()` and `limit()`. The client takes `acting_user_token`. | `iverson_client/match_pattern.py:47-53`; `core.py:822`, `:862-880`; example at `Iverson.Agents/Python/iverson_agent/__main__.py:69-79`. |
| 11 | `fs-2048-l070.chunks.trec` is document-level, 672 queries × 50. It was recorded with `chunkBudgetMultiplier` 11 and `LambdaChunks` 0.70, composite `9714c660b365fad1`. `keymap.json` maps GUID → BEIR id. | Head of the file; `runs/fs-2048-l070.meta.json`; `chunk-coverage-phase2-l070-2026-09-09/capture-certification.txt`. |
| 12 | `report.py` accepts repeated `--pair` with Holm correction and `--nugget-qrels`. It enforces `check_pool`: same document set, and at least 25% of queries reordered. A missing `.meta.json` only prints `BUILD UNKNOWN`. | `report.py:703-718`, `769-821`, `346-353`, `732-766`, `184-202`, `589-591`. |
| 13 | No existing artefact holds raw per-chunk similarity for all candidates, so a calibration pass is needed. | The `chunks.hits.tsv` scores are fused scores that include the centroid, and lack `chunk_index` (`ResultReranker.cs:37-59`). |
| 14 | The server has no query-embedding cache. | `EmbeddingService.cs` has no cache. `EmbeddingServiceResolver.cs:13` caches services per model, not vectors. |
| 15 | Harness script tests are pytest `test_*.py` files, run from the repo root. The ones that need ir_measures take `PYTHONPATH=…/iverson-benchmark-corpora/python-libs`. | `scripts/test_tail_stats.py:1-3`; `test_report.py:1-4`. |
| 16 | Chunk counts per document: FreshStack-2048 has 57.2% of documents at 3 or more chunks; SciFact-2048 has 0.5%. | Estimated from `beir/corpus.jsonl` text lengths with the 2048/1792 window. The estimate is within 4% of the recorded 18,622 chunks. |
| 17 | The snapshot's chunk collection matches what `QdrantChunkRowSource` reads: vector `body_vector` (768, Cosine), keyword indexes on `parent_id`, `field` and `ownerId`. | CDR round 1, row D10: the snapshot's `config.json` and `payload_index.json`; `ingest.py`'s chunk payload lines are unchanged since `2dd58236`. |
| 18 | Swapped RRF ranks tie exactly, and ir_measures breaks score ties by doc id, not file order. Strictly decreasing scores make it follow file order. | `1/63+1/67 == 1/67+1/63` is `True`; an ir_measures probe scored two tied docs at nDCG@10 0.6309 in either file order (CDR round 1, re-run in update). |
| 19 | `MaxOutputRows` accepts any positive value and is bound from `Patterns:Limits`. | `PatternQueryLimitOptions.cs` `Validate` (`Positive(nameof(MaxOutputRows), …)`); `Iverson.Api/Program.cs:315-325`. |
| 20 | `/build` is anonymous on listener port 8081 and returns `{composite, assemblies}`. | `Iverson.Api/Program.cs:541-545`; precedent `BenchmarkQueryScenario.cs:170-201` refuses to run without it. |
