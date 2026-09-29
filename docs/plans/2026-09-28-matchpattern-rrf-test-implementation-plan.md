# MatchPattern RRF Feasibility and Quality Test Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task by task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-28-matchpattern-rrf-test-design.md` (commit SHA: `fa7194ac`)

**Goal:** Answer whether MATCH_RECOGNIZE (the `MatchPattern` RPC) supports reciprocal rank fusion, under all three readings of the question.
- **Reading 1:** engine tests pin the "RRF inside one request" verdict.
- **Reading 3:** two existing tests are cited for the "matching over an RRF order" verdict.
- **Reading 2:** a pre-registered FreshStack-2048 benchmark gives the "RRF over its output" verdict. It fuses a MatchPattern chunk-run leg with the shipped `SearchChunks` ranking.

**Architecture:**
- **Tests and scripts.** One C# test file pins the engine facts. Two Python harness scripts do the benchmark work:
  - `pattern_leg.py` drives the live RPC through the Python SDK: probes, calibration and θ passes.
  - `rrf_fuse.py` works offline: RRF fusion, the INERT check and the degeneracy guard.
  
  Both reuse `report.py` rather than copying it.
- **Live run.** Two operational tasks run the benchmark on the shared local compose stack, which is configured with a compose override. The last of them writes and commits the gate document.
- **No production change.**

**Tech stack:**
- .NET 10: xunit, FluentAssertions.
- Python 3.14: pytest, grpcio, and the in-repo `iverson_client` SDK added to `sys.path`.
- scipy 1.18.1, ir_measures 0.4.3 and pyndeval, all under `PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs`.
- Docker Compose (Podman), Qdrant 1.18, TEI with bge-base.

---

## Global Constraints

- **No production code changes** (spec, Out of scope). Every task adds tests, scripts or documents only.
- **Pre-registered constants, never tuned after seeing results:**
  - RRF k = 60;
  - θ as the p50, p75 and p90 of `s` over the 100 lowest query ids' candidates;
  - the degeneracy guard |ρ| ≥ 0.95, with no-run candidates at a floor below every matched score;
  - INERT when fewer than 25% of queries are reordered.
- **Benchmark runs never degrade silently.** Any `MatchPattern` error, a result count equal to the `limit` sent, a NULL similarity, or a `/build` composite mismatch aborts the run.
- **Where artefacts go:** benchmark artefacts live under `/home/ben/repositories/iverson-benchmark-corpora/matchpattern-rrf-<DATE>/`. Nothing goes under `/tmp`, which is not durable on this machine.
- **Shell `grep` skips gitignored paths (including `docs/`).** Use `command grep` for every search.
- **Commits:** a lowercase imperative subject, a blank line, then `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`. Commit explicit paths only. `docs/` is gitignored, so the gate document needs `git add -f`.
- **Tasks 4 and 5 change the shared local compose stack.** They rebuild `iverson-api`, recreate api and worker with non-default limits, and replace two Qdrant collections. The controller asks the user before dispatching Task 4.

## Deviations from the spec (user decisions while planning, 2026-09-28)

1. **Reading 3 has no new Api test.** The spec wanted a `MatchPatternGrpcServiceTests` test asserting that an unknown `order_by` returns `InvalidArgument`. That cannot be written:
   - The service never checks `order_by` against the schema. It reads it only at `ObjectSearchGrpcService.MatchPattern.cs:299` (CHUNKS must have none) and `:304` (TYPE_ROWS needs one), then passes it through at `:402`.
   - The only membership check is the StarRocks builder's `RequireVisible` (`MatchRowsQueryBuilder.cs:29`, `:100-105`). The service maps that check's exception to `InvalidArgument` (`:163-165`).
   - In that test class the store is a mock, so an unknown `order_by` passes straight through.
   
   **User decision:** cite the two existing tests that pin the two halves. They are `Every_slot_rejects_an_unknown_hidden_tenant_or_bytes_column` (`Iverson.StarRocks.Tests/MatchRowsQueryBuilderTests.cs:84-105`) and `Store_exceptions_map_as_the_spec_section_6_table_says` (`Iverson.Api.Tests/Grpc/MatchPatternGrpcServiceTests.cs`). The reading-3 verdict itself is unchanged.
2. **The calibration call is text-free.** The spec's calibration call (`A+`, `A AS TRUE`, ALL ROWS) returns every chunk's full `text`, because ALL ROWS emits every input column (`CompiledPattern.cs:130`). In the whole-corpus shape that is about 30.7 M characters per call.
   
   **User decision:** use pattern `A`, `A AS TRUE`, ONE ROW PER MATCH, with measures `s = SIMILARITY(text, '<q>')` and `ci = chunk_index`. That gives the same values, with one text-free row per chunk; the engine probe below confirms it.
3. **Probe B's rule counts both passes.** The spec says "per-candidate if its projected full run (672 × 50 calls per θ) is ≤ 8 h per θ", and the calibration pass is also 672 × 50 calls. So `pattern_leg.py probe-b` picks per-candidate only when both the calibration projection and the θ projection are ≤ 8 h. This is the conservative reading.
4. **The baseline is recorded before the probes (Task 4).** Both probes need a baseline's candidates, so Phase 1 runs before Phase 0's probes. If Probe B returns STOP, only the baseline's run time is spent.

## File Structure

- **Create:** `Iverson.Server/Iverson.Patterns.Tests/RrfExpressivenessTests.cs`. Four facts that pin reading 1.
- **Create:** `Iverson.Server/Iverson.LoadTest/scripts/rrf_fuse.py`. Offline: RRF fusion, the fused sidecars, INERT and the degeneracy guard.
- **Create:** `Iverson.Server/Iverson.LoadTest/scripts/test_rrf_fuse.py`.
- **Create:** `Iverson.Server/Iverson.LoadTest/scripts/pattern_leg.py`. Live: probes A and B, calibration, and the θ passes. Contains the minimal `BenchmarkDocument` entity and the token refresh.
- **Create:** `Iverson.Server/Iverson.LoadTest/scripts/test_pattern_leg.py`.
- **Create:** `docs/plans/2026-09-GATE-matchpattern-rrf.md`. The gate document (Task 5).
- **Outside the repository** (Tasks 4 and 5): `/home/ben/repositories/iverson-benchmark-corpora/matchpattern-rrf-<DATE>/`, holding `compose.rrf-bench.yml`, `runs/`, `pattern/`, `fused/` and `phase0.md`.

No existing file is modified.

## Inherited from spec

These assumptions were verified by `thorough-brainstorming` and by two rounds of critical design review. They are not re-verified here and are trusted as ground truth. The table is copied verbatim from the spec's "Verified assumptions":

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
| 19 | `MaxOutputRows` accepts any positive value and is bound from `Patterns:Limits`. Only the `limit` check reads it, so raising it has no effect on calls with `limit` ≤ 10,000. | `PatternQueryLimitOptions.cs` `Validate` (`Positive(nameof(MaxOutputRows), …)`); `Iverson.Api/Program.cs:315-325`; `command grep` over `Iverson.Api`, `Iverson.Patterns`, `Iverson.Vector` and `Iverson.StarRocks` finds only the binding, the default, `Validate` and `ObjectSearchGrpcService.MatchPattern.cs:294-295` (CDR round 2). |
| 20 | `/build` is anonymous on listener port 8081 and returns `{composite, assemblies}`. | `Iverson.Api/Program.cs:541-545`; precedent `BenchmarkQueryScenario.cs:170-201` refuses to run without it. |

## Verified plan-level assumptions

Every code block in Tasks 1–3 was prototyped and run at `fa7194ac`, in the throwaway worktrees `.worktrees/rrf-proto-cs` and `.worktrees/rrf-proto-py`. The code below is byte-identical to the prototypes, whose md5s are given in each task.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | Path, signature | `Iverson.Patterns.Tests` exposes `TestRows.Rows` (internal static). The public API is `PatternQuery.Compile(PatternRequest, int)` and `CompiledPattern.Run(rows, Func<int,int,double?>, PatternBudget)`, with `PatternRequest`'s positional constructor as used in Task 1. | `Iverson.Patterns.Tests/TestRows.cs:3-9`; `PatternQuery.cs:17`; `CompiledPattern.cs:67-71`; `PatternRequest.cs`. The Task 1 file compiled and passed 4/4. |
| 2 | Signature | The correlated rank-by-similarity measure throws `Iverson.Patterns.PatternValidationException` at `Compile`, not `Run`, with "must match" in its message. | `PatternExceptions.cs:4`; `Expressions/ExpressionParser.cs:789`; prototype test 4 passes. |
| 3 | Code validity | Task 1's first three facts are exact, with no tolerance: the engine computes in `double`/`long`, so `1.0/61…1.0/65`, `5L…1L` and `s/0.9` compare equal, and the maximum row is exactly `1.0`. | Prototype passed 4/4. Four deliberate breaks (`1/60`, a flipped rank, an expected maximum of `0.9`, an uncorrelated test-4 expression) each failed. |
| 4 | Command | `dotnet test Iverson.Server/Iverson.Patterns.Tests/Iverson.Patterns.Tests.csproj --filter "FullyQualifiedName~RrfExpressivenessTests"` needs no container. The whole project starts a Trino container (`Oracle/TrinoContainerFixture.cs`) and takes about 5 minutes. | Prototype counts: filtered 4/4; whole project 616/616 before and 620/620 after. |
| 5 | Premise (deviation 1) | The service does not validate `order_by` membership; the StarRocks builder does, and the service maps its exception to `InvalidArgument`. | `ObjectSearchGrpcService.MatchPattern.cs:299`, `:304`, `:402`, `:163-165`; `MatchRowsQueryBuilder.cs:29`, `:100-105`; `MatchRowsQueryBuilderTests.cs:84-105`. `MatchPatternGrpcServiceTests` passes 52/52 and is untouched. |
| 6 | Import | The Python SDK is not pip-installed. `pattern_leg.py` puts `Iverson.Clients/Python` on `sys.path`, as `Iverson.Clients/Python/conformance/driver.py:25` does, and grpcio/protobuf come from the user site-packages. | `pip show` finds no `iverson_client`. `test_pattern_leg.py` passes 44/44 with and without `PYTHONPATH`. |
| 7 | Signature | `IversonClient(host, port, use_tls, *, credentials, acting_user_token, allow_insecure_credentials=False)`. The stack's plaintext h2c with an `http://` token endpoint needs `allow_insecure_credentials=True`. The credentials type is `IversonClientCredentials(client_id, client_secret, token_endpoint, scope)`. | `iverson_client/core.py:862-871`, `:874-900`; `auth.py:23-28`; the same opt-in is made at `Iverson.Agents/Python/iverson_agent/__main__.py:75`. |
| 8 | Signature | `coordinator(cls)` needs an `@iverson_entity` class, whose type name is `cls.__name__`. `pattern_leg.py` declares a minimal `BenchmarkDocument` and asserts its name. `EntityCoordinator.with_acting_user(token)` rebinds the token. | `core.py:948-950` (`coordinator`), `:622-634` (`EntityCoordinator` raises without `_iverson_meta`), `:642-646`; `annotations.py:367`. |
| 9 | Signature | `.chunks("Body")` sets `source = CHUNKS` and `chunk_property`, which the server matches case-insensitively. The key column is `Id`. A `where` string is sent as `string_val`, which the server reads as `StringVal`. | `match_pattern.py:47-51`; `ObjectSearchGrpcService.MatchPattern.cs:40-44`; `Iverson.LoadTest/Entities/BenchmarkDocument.cs:8`, `:16-18`; `search.py:43-44`; `ObjectSearchGrpcService.cs:1155-1156`. |
| 10 | Signature | `MatchPatternResult(data, match_number, classifier)`, with `data = dict(Struct)`. Numbers arrive as floats (`chunk_index` as `3.0`), NULL arrives as `None`, and a gRPC failure raises `grpc.RpcError` out of `coordinator.match_pattern`. | `core.py:595-606`, `:471-472`, `:822-830`; checked in Python by the prototype. |
| 11 | Behaviour | The server stops at exactly `limit` rows. `limit` must be in 0..MaxOutputRows, and 0 means 1,000. A CHUNKS parent over MaxPartitionRows (10,000) fails with `ResourceExhausted` before emitting anything. So a per-candidate `limit` of 10,000 keeps "count == limit" meaning possible truncation, and stays valid on a default stack. | `ObjectSearchGrpcService.MatchPattern.cs:144`, `:294`, `:307`, `:159-161`; `QdrantChunkRowSource.cs:34`; `PatternQueryLimitOptions.cs:21`, `:23`. |
| 12 | Code validity (deviation 2) | Pattern `A`, `A AS TRUE`, ONE ROW PER MATCH, with measures `s = SIMILARITY(text, q)` and `ci = chunk_index`, gives one text-free row per chunk carrying its own index and similarity. With `A+`, the parent collapses into one row. | An engine probe (source CHUNKS; `chunk_index` values 10, 11, 13, 14, 20; a distinct similarity per row) printed 5 rows with `ci` equal to its own index and `s` equal to its own score, and no `text`. The `A+` control printed 1 row. |
| 13 | Behaviour | Acting-user tokens last 2 hours. `mint_acting_user_token.py` prints only the token on stdout. `pattern_leg.py` re-mints at 90 minutes and rebinds the coordinator. | `deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml:259`; `mint_acting_user_token.py --help` and `:519`; the `TokenSession` test in `test_pattern_leg.py`. |
| 14 | Environment | `/home/ben/iverson-benchmark-data/bench-env.sh` exports `IVERSON_GRPC_URL`, `IVERSON_CLIENT_ID`, `IVERSON_CLIENT_SECRET`, `IVERSON_TOKEN_ENDPOINT` and `IVERSON_CLIENT_SCOPE`. `benchmark-query` also requires `IVERSON_ACTING_USER_PASSWORD` and `IVERSON_ACTING_USER_BYPASS_PASSWORD`, taken from `.env`'s `IVERSON_SMOKE_TEST_PASSWORD` and `IVERSON_BYPASS_PASSWORD`. | `command grep -o '^export [A-Z_]*='` on bench-env.sh; `Iverson.LoadTest/Program.cs:37-41`, `:57-62`; Plan 3's live run used the same `.env` names. |
| 15 | Signature | `report.sidecar_path_for` strips `.trec` and then `.chunks`, so the fused runs are `fused-<t>-<score>.chunks.trec` with `fused-<t>-<score>.meta.json`. `check_pool` exits the process, so `rrf_fuse.py` checks the document set with `report.ranked_doc_ids`, runs `check_pool` inside `except SystemExit`, and requires agreement with `report.POOL_MIN_REORDERED_FRACTION`. Other scripts already `import report`. | `report.py:167-181`, `:732-766`, `:700`; `multivector.py` and `aspect_vectors.py` import it. A synthetic end-to-end run of `report.py --pair` on `rrf_fuse.py` output exited 0, found every sidecar, reported 0 BUILD warnings and moved R@50 by +0.0000. |
| 16 | Command | scipy 1.18.1 and ir_measures are importable only under `PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs`. `test_rrf_fuse.py` needs that path; `test_pattern_leg.py` does not. The whole scripts suite also needs `QDRANT__SERVICE__API_KEY` set to any value, because `ingest.py:160` reads it at import. | Default `python3 -c "import scipy"` raises `ModuleNotFoundError`. Prototype counts: `test_rrf_fuse.py` 27, `test_pattern_leg.py` 44, whole suite 383 before and 454 after. |
| 17 | Command | A second compose file merges `Patterns__Limits__MaxExpressionLength=25000` and `Patterns__Limits__MaxOutputRows=18623` into `iverson-api`'s environment. `VectorRanking__LambdaChunks` already defaults to 0.70. gRPC is h2c on 8080, `/build` is on 8081 and Qdrant's REST API is on 6333, all bound to loopback. | `docker compose --env-file .env -f docker-compose.yml -f compose.rrf-bench.yml config` printed both keys under `iverson-api`; `docker-compose.yml:466`, `:119`, `:454-455`. |
| 18 | Command | Qdrant's API key is `.env`'s `QDRANT__SERVICE__API_KEY`, and snapshots restore through `POST /collections/<name>/snapshots/upload?priority=snapshot`. **UNVERIFIED live:** restoring into a name different from the snapshot's original. Task 4 Step 2 checks the point counts (6,000 and 18,622) and the `body_vector` config, and stops if they differ. | `docker-compose.yml:116`, `:480`; `scifact-512-qdrant-snapshots/RESTORE.md`. |
| 19 | Command | `benchmark-query` takes `--corpus-path`, `--key-map-path`, `--output-dir`, `--config-label` and `--chunk-budget-multiplier`. It reads only `beir/queries.jsonl`'s `text`, which is the same field `pattern_leg.load_queries` reads. | `docs/plans/2026-09-09-chunk-coverage-phase2-implementation-plan.md` Task 1 Step 5 (a run that completed with those flags); `BenchmarkQueryScenario.cs:491-503`; `Corpus/JsonlCorpusParser.cs:78`; FreshStack-2048 has no `freshstack/queries.jsonl`. |
| 20 | Ordering | Tasks 1, 2 and 3 are independent: neither script imports the other, and both only import `report`. Task 4 needs Task 3. Task 5 needs Tasks 2, 3 and 4. | `command grep "^import\|^from"` over the four script files. |
| 21 | Behaviour | `pattern_leg.fetch_build` reads the live `/build` composite and aborts on a mismatch. | A read-only prototype run against the running stack returned `ded69e9492bdc081` and aborted against `9714c660b365fad1`. |
| 22 | Convention | The commit style is a lowercase imperative subject plus a `Co-Authored-By` trailer. | `git log --oneline -8`. |
| 23 | Behaviour | TEI returns bit-identical query vectors for identical sequential calls, so probe A's exact-equality comparison of `s` between its two calls is sound. | Two identical `POST localhost:8091/embed` calls returned the same md5 (`1a4a0d07…`), re-run in UIP round 1. CIR round 1 S2 checked the shortest and the longest query. |
| 24 | Behaviour | A step lasting from 31 minutes to 8 hours can run as written: a command past the executor's tool timeout is moved to the background and completes, and its `EXIT=` line is captured. | CIR round 1, S3 and row D5 (the reviewer's run of the executor's tool). Not re-run in UIP. |
| 25 | Behaviour | Back-to-back token mints (probe-a, then probe-b) inside one TOTP window succeed. | `mint_acting_user_token.py:77` `MAX_TOTP_ATTEMPTS = 4`, and `:224` `submit_totp_code` waits out a reused window. `~/.cache/iverson/acting-user-totp-secret-compose-iverson-loadtest-bypass-user.txt` exists. |
| 26 | Behaviour | A denied acting user gets an empty stream, not an error. The pattern leg catches that loudly. | `ObjectSearchGrpcService.MatchPattern.cs:58` `if (chunkDecision.Denied) return;`; `pattern_leg.py:590` (probe-a "no rows") and `:670` (calibrate "candidate(s) returned no …"). |
| 27 | Behaviour | `parent_key` values equal the key map's GUID strings. | `ingest.py:684` `"parent_id": key`; `pattern_leg.rows_by_candidate` (`:248`) aborts on a parent that is not in the key map. |
| 28 | Behaviour | Each plan step runs as a separate shell call, so variables, functions and exports do not persist between steps. Sourcing a state file restores them, and the `--pair` list can be rebuilt from `rrf_fuse.log` and checked against `inert.json`. | CIR round 1 §2.1: two consecutive calls lost a variable and a function. UIP round 1: a state file sourced in a fresh shell restored the cwd, `OUT`, `counts`, `K` and both exports, with no secret written to it. The agreement check gave `AGREE_EXIT` 0, 1 and 0 for matching lists, an empty list with arms not INERT, and every arm INERT. `inert.json` keys are `fused-<t>-<score>` (`rrf_fuse.py:82`). |

## Tasks

### Task 1: Engine tests pinning reading 1

**Files:**
- Create: `Iverson.Server/Iverson.Patterns.Tests/RrfExpressivenessTests.cs` (prototype md5 `a0a8882505ea0b188a0c3735b13b4caf`)

- [ ] **Step 1: Write the test file**

```csharp
using FluentAssertions;
using Xunit;
using static Iverson.Patterns.Tests.TestRows;

namespace Iverson.Patterns.Tests;

/// <summary>
/// Pins the reading-1 verdict of docs/specs/2026-09-28-matchpattern-rrf-test-design.md ("RRF inside one
/// request"): <c>measures</c> can compute the <c>order_by</c> leg of RRF (<c>1 / (60 + rank)</c>), the reverse rank
/// and a normalised similarity, but not a rank by <c>SIMILARITY</c>. If the last fact starts failing, the engine
/// has gained a correlated count and the verdict must be revisited.
/// </summary>
public sealed class RrfExpressivenessTests
{
    // Five rows, already in order_by order: the engine numbers rows in the order the row source delivers them.
    private static readonly List<IDictionary<string, object?>> D = Rows(["id", "title"],
        [1L, "a"], [2L, "b"], [3L, "c"], [4L, "d"], [5L, "e"]);

    // SIMILARITY(title, 'q') per row; row 2 (index 1) is the maximum.
    private static readonly double[] Scores = [0.3, 0.9, 0.45, 0.6, 0.1];

    /// <summary>One match over every row (<c>A+</c>, <c>A AS TRUE</c>), ALL ROWS PER MATCH, one measure <c>m</c>.</summary>
    private static PatternRequest Req(string measure) =>
        new(PatternSource.TypeRows, [], "A+", [], [new NamedExpression("A", "TRUE")],
            [new NamedExpression("m", measure)], RowsPerMatch.AllRowsShowEmpty, AfterMatchSkipKind.PastLastRow, "",
            "TenantId");

    private static List<object?> Run(string measure, Func<int, int, double?>? similarity = null) =>
        PatternQuery.Compile(Req(measure), 5000)
            .Run(D, similarity ?? ((_, _) => null), new PatternBudget(10_000, 10_000_000))
            .Select(r => r.Data["m"])
            .ToList();

    [Fact]
    public void The_order_by_rrf_term_is_one_over_sixty_plus_the_running_count()
    {
        Run("1.0 / (60 + RUNNING COUNT(*))").Should().Equal(1.0 / 61, 1.0 / 62, 1.0 / 63, 1.0 / 64, 1.0 / 65);
    }

    [Fact]
    public void Final_count_minus_running_count_plus_one_is_the_reverse_rank()
    {
        Run("FINAL COUNT(*) - RUNNING COUNT(*) + 1").Should().Equal(5L, 4L, 3L, 2L, 1L);
    }

    [Fact]
    public void Similarity_over_its_final_max_normalises_the_best_row_to_one()
    {
        var normalised = Run("SIMILARITY(Title, 'q') / FINAL MAX(SIMILARITY(Title, 'q'))", (row, _) => Scores[row]);

        normalised.Should().Equal(Scores.Select(s => (object?)(s / 0.9)));
        normalised[1].Should().Be(1.0);
    }

    [Fact]
    public void A_rank_by_similarity_needs_a_correlated_count_which_compile_rejects()
    {
        var act = () => PatternQuery.Compile(
            Req("FINAL SUM(CASE WHEN SIMILARITY(A.Title,'q') > SIMILARITY(Title,'q') THEN 1 ELSE 0 END) + 1"), 5000);

        act.Should().Throw<PatternValidationException>().WithMessage("*must match*");
    }
}
```

- [ ] **Step 2: Run the new tests**

Run: `dotnet test Iverson.Server/Iverson.Patterns.Tests/Iverson.Patterns.Tests.csproj --filter "FullyQualifiedName~RrfExpressivenessTests"`
Expected: `Passed: 4, Failed: 0`. No container is needed.

- [ ] **Step 3: Run the whole project**

Run: `dotnet test Iverson.Server/Iverson.Patterns.Tests/Iverson.Patterns.Tests.csproj`
Expected: `Passed: 620, Failed: 0` (616 before). The project's Trino oracle tests start a container, so this takes about 5 minutes and needs Docker.

- [ ] **Step 4: Commit**
```bash
git add Iverson.Server/Iverson.Patterns.Tests/RrfExpressivenessTests.cs
git commit -m "pin what a matchpattern measure can and cannot compute toward rrf" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 2: `rrf_fuse.py`, the offline fusion, INERT check and degeneracy guard

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/rrf_fuse.py` (prototype md5 `c7582b1174addf57a7bbc34ef254502f`)
- Create: `Iverson.Server/Iverson.LoadTest/scripts/test_rrf_fuse.py` (prototype md5 `3bb06fa9cb658ca467a72e9a55948e51`)

**Interfaces:**
- Consumes these files, whose formats Task 3's writers produce:
  - `OUT/pattern/calibration.tsv`: `query_id doc_id chunk_index s`
  - `OUT/pattern/scores-<t>.tsv`: `query_id doc_id run_len run_sum run_sum_of_best_len run_len_of_best_sum`, read by column name
  - `OUT/pattern/mp-<t>-<score>.trec`
  - `OUT/pattern/build.json`: `{"baseline_composite", "checks": [{"pass","before","after"}]}`
  
  It also reads the baseline `<label>.chunks.trec` and its `<label>.meta.json`.
- Produces, for Task 5:
  - `OUT/runs/fused-<t>-<score>.chunks.trec` and its `.meta.json`
  - `OUT/fused/fused-<t>-<score>.rrf.tsv`
  - `OUT/fused/inert.json` and `OUT/fused/guard.json`
  - the printed `--pair` arguments for the arms that are not INERT

- [ ] **Step 1: Write the tests**

```python
"""pytest suite for rrf_fuse.py (spec docs/specs/2026-09-28-matchpattern-rrf-test-design.md, Phase 3
and the degeneracy guard). Run with:

    PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs \\
        python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_rrf_fuse.py -q

PYTHONPATH is needed: the INERT tests run report.check_pool (ir_measures) and the guard tests run
scipy.stats.spearmanr. Every fixture is hand-computable."""
import json
import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rrf_fuse  # noqa: E402


def write_run(path, rows, tag="t"):
    """rows: [(qid, [docid, ...])] -- ranks 1..n in list order, scores n..1."""
    with open(path, "w", encoding="utf-8") as f:
        for qid, docids in rows:
            for i, docid in enumerate(docids):
                f.write(f"{qid} Q0 {docid} {i + 1} {float(len(docids) - i):.6f} {tag}\n")


# ── RRF arithmetic ──────────────────────────────────────────────────────────────────────

def test_rrf_score_sums_both_reciprocal_ranks_at_k_60():
    assert rrf_fuse.rrf_score(1, 3) == 1 / 61 + 1 / 63


def test_rrf_score_omits_the_pattern_term_when_the_candidate_has_no_pattern_rank():
    """Absent, not zero-ranked and not floored: exactly 1/(60 + rank_b)."""
    assert rrf_fuse.rrf_score(4, None) == 1 / 64


def test_fuse_query_orders_by_rrf_and_uses_the_missing_branch_for_unmatched_candidates():
    # baseline a,b,c,d; pattern ranks d first, b second; a and c have no run.
    fused = rrf_fuse.fuse_query(["a", "b", "c", "d"], ["d", "b"])
    expected = {"a": 1 / 61, "b": 1 / 62 + 1 / 62, "c": 1 / 63, "d": 1 / 64 + 1 / 61}
    assert dict(fused) == expected
    assert [d for d, _ in fused] == ["b", "d", "a", "c"]


def test_fuse_query_refuses_a_pattern_document_that_is_not_a_baseline_candidate():
    with pytest.raises(SystemExit):
        rrf_fuse.fuse_query(["a", "b"], ["z"])


# ── swapped ranks: strict order, strictly decreasing written score ──────────────────────

def test_swapped_ranks_tie_exactly_and_the_baseline_rank_breaks_the_tie(tmp_path):
    """Baseline rank 3 / pattern rank 7 versus baseline rank 7 / pattern rank 3: the RRF values are
    bit-identical, so only the baseline-rank tie-break makes the order strict. Doc ids run AGAINST
    baseline rank ("z3" is rank 3, "a7" rank 7), so a doc-id tie-break -- what ir_measures does --
    would put a7 first and fail here."""
    baseline = ["z1", "y2", "z3", "x4", "w5", "v6", "a7", "u8", "t9", "s10"]
    pattern = ["u8", "t9", "a7", "s10", "z1", "y2", "z3"]  # a7 at pattern rank 3, z3 at pattern rank 7
    fused = rrf_fuse.fuse_query(baseline, pattern)
    score = dict(fused)
    assert score["z3"] == score["a7"]                      # 1/63 + 1/67 == 1/67 + 1/63
    order = [d for d, _ in fused]
    assert order.index("z3") == order.index("a7") - 1      # adjacent, baseline rank 3 first

    path = tmp_path / "fused.chunks.trec"
    rrf_fuse.write_fused_run(str(path), {"q1": fused}, ["q1"], "tag")
    rows = [line.split() for line in path.read_text().splitlines()]
    written = [float(r[4]) for r in rows]
    assert [int(r[3]) for r in rows] == list(range(1, 11))
    assert written == [51.0 - rank for rank in range(1, 11)]
    assert all(a > b for a, b in zip(written, written[1:]))


# ── document set equals the baseline's ──────────────────────────────────────────────────

def test_fuse_run_keeps_every_baseline_document_and_no_other(tmp_path):
    baseline = {"q1": ["a", "b", "c"], "q2": ["d", "e", "f"]}
    fused = rrf_fuse.fuse_run(baseline, ["q1", "q2"], {"q1": ["c"]})
    assert {q: sorted(d for d, _ in docs) for q, docs in fused.items()} == \
        {"q1": ["a", "b", "c"], "q2": ["d", "e", "f"]}
    # q2 has no pattern row: every second term is absent, so the baseline order survives.
    assert [d for d, _ in fused["q2"]] == ["d", "e", "f"]


def test_fuse_run_refuses_a_pattern_query_absent_from_the_baseline():
    with pytest.raises(SystemExit):
        rrf_fuse.fuse_run({"q1": ["a"]}, ["q1"], {"q9": ["a"]})


def test_load_ranked_run_refuses_a_rank_column_that_disagrees_with_file_order(tmp_path):
    p = tmp_path / "r.trec"
    p.write_text("q1 Q0 a 2 1.0 t\nq1 Q0 b 1 0.5 t\n", encoding="utf-8")
    with pytest.raises(SystemExit):
        rrf_fuse.load_ranked_run(str(p))


# ── build composite ─────────────────────────────────────────────────────────────────────

PASSES = ("calibration", "p50", "p75", "p90")


def build_json(composite="c1", baseline="c1", overrides=None):
    checks = [{"pass": p, "before": composite, "after": composite} for p in PASSES]
    for index, side, value in overrides or []:
        checks[index][side] = value
    return {"baseline_composite": baseline, "checks": checks}


def test_measured_composite_returns_the_composite_when_every_check_agrees():
    assert rrf_fuse.measured_composite(build_json(), "c1", PASSES) == "c1"


@pytest.mark.parametrize("build, sidecar", [
    (build_json(baseline="c0"), "c1"),                                   # build.json baseline != sidecar
    (build_json(overrides=[(2, "after", "c2")]), "c1"),                  # one check disagrees
    (build_json(overrides=[(0, "before", "c2")]), "c1"),                 # calibration before disagrees
    ({"baseline_composite": "c1", "checks": build_json()["checks"][:3]}, "c1"),  # p90 never checked
    (build_json(), None),                                                # baseline sidecar has no composite
])
def test_measured_composite_aborts_on_any_mismatch_or_missing_check(build, sidecar):
    with pytest.raises(SystemExit):
        rrf_fuse.measured_composite(build, sidecar, PASSES)


def test_write_sidecar_is_found_by_report_and_carries_the_measured_composite(tmp_path):
    run = tmp_path / "fused-p50-run_len.chunks.trec"
    run.write_text("", encoding="utf-8")
    path = rrf_fuse.write_sidecar(str(run), {"configLabel": "base", "composite": "old", "x": 1}, "c9",
                                  "fused-p50-run_len")
    assert path == str(tmp_path / "fused-p50-run_len.meta.json")
    with open(path, encoding="utf-8") as f:
        assert json.load(f) == {"configLabel": "fused-p50-run_len", "composite": "c9", "x": 1}


# ── INERT: both sides of the 25% rule ───────────────────────────────────────────────────

BASE8 = [(f"q{i}", ["a", "b", "c"]) for i in range(8)]


def reorder_first(n):
    return [(q, list(reversed(d)) if i < n else d) for i, (q, d) in enumerate(BASE8)]


def test_an_arm_reordering_exactly_25_percent_is_not_inert(tmp_path):
    base, arm = tmp_path / "b.chunks.trec", tmp_path / "a.chunks.trec"
    write_run(base, BASE8)
    write_run(arm, reorder_first(2))       # 2 of 8 = 25%
    assert rrf_fuse.classify_inert(str(arm), str(base)) == {"reordered_fraction": 0.25, "inert": False}


def test_an_arm_reordering_under_25_percent_is_inert_and_does_not_end_the_run(tmp_path):
    base, arm = tmp_path / "b.chunks.trec", tmp_path / "a.chunks.trec"
    write_run(base, BASE8)
    write_run(arm, reorder_first(1))       # 1 of 8 = 12.5%
    assert rrf_fuse.classify_inert(str(arm), str(base)) == {"reordered_fraction": 0.125, "inert": True}


def test_classify_inert_aborts_when_the_document_set_changed(tmp_path):
    base, arm = tmp_path / "b.chunks.trec", tmp_path / "a.chunks.trec"
    write_run(base, BASE8)
    write_run(arm, [("q0", ["a", "b", "z"])] + reorder_first(8)[1:])
    with pytest.raises(SystemExit):
        rrf_fuse.classify_inert(str(arm), str(base))


# ── Degeneracy guard: the floor, and both sides of the 0.95 threshold ───────────────────

def test_guard_vectors_gives_no_run_candidates_a_floor_strictly_below_every_matched_score():
    baseline = {"q1": ["a", "b", "c"], "q2": ["d", "e"]}
    calibration = {("q1", "a"): (1, 0.5), ("q1", "b"): (2, 0.6), ("q1", "c"): (3, 0.7),
                   ("q2", "d"): (4, 0.8), ("q2", "e"): (5, 0.9)}
    primary, counts, sims = rrf_fuse.guard_vectors(
        baseline, ["q1", "q2"], {("q1", "b"): 2.0, ("q2", "e"): 3.0}, calibration)
    assert primary == [1.0, 2.0, 1.0, 1.0, 3.0]            # floor = min(2, 3) - 1 = 1 < 2
    assert counts == [1, 2, 3, 4, 5]
    assert sims == [0.5, 0.6, 0.7, 0.8, 0.9]


def test_guard_vectors_aborts_on_a_candidate_missing_from_calibration():
    with pytest.raises(SystemExit):
        rrf_fuse.guard_vectors({"q1": ["a", "b"]}, ["q1"], {("q1", "a"): 1.0}, {("q1", "a"): (1, 0.5)})


def test_guard_vectors_aborts_when_the_arm_matched_nothing():
    with pytest.raises(SystemExit):
        rrf_fuse.guard_vectors({"q1": ["a"]}, ["q1"], {}, {("q1", "a"): (1, 0.5)})


def test_rho_at_the_threshold_is_degenerate():
    # 20 points with one adjacent swap: rho = 1 - 6*2/(20*399) = 0.99849...; a count-in-disguise.
    counts = list(range(20))
    primary = list(range(20))
    primary[0], primary[1] = primary[1], primary[0]
    sims = [(i * 7) % 20 for i in range(20)]          # a permutation unrelated to primary
    result = rrf_fuse.degeneracy(primary, counts, sims)
    assert result["rho_chunk_count"] >= 0.95
    assert result["degenerate"] is True


def test_rho_just_above_the_threshold_is_degenerate():
    # n = 20, sum(d^2) = 64: rho = 1 - 384/7980 = 0.95188. Swaps 0<->4 (32), 5<->7 (8), 10<->12 (8),
    # 14<->16 (8), 17<->19 (8): total 64. (An exact 0.95 is not reachable: scipy returns
    # 0.9500000000000001 for the n = 9 construction, so the bracket 0.94887 / 0.95188 pins the rule.)
    counts = list(range(20))
    primary = list(range(20))
    for a, b in ((0, 4), (5, 7), (10, 12), (14, 16), (17, 19)):
        primary[a], primary[b] = primary[b], primary[a]
    result = rrf_fuse.degeneracy(primary, counts, [(i * 7) % 20 for i in range(20)])
    assert result["rho_chunk_count"] == pytest.approx(1 - 384 / 7980)
    assert result["degenerate"] is True


def test_rho_just_below_the_threshold_is_not_degenerate():
    # 1 - 6*sum(d^2)/(n(n^2-1)) with n = 20: sum(d^2) = 64 gives 1 - 384/7980 = 0.95188 (>= 0.95),
    # sum(d^2) = 68 gives 1 - 408/7980 = 0.94887 (< 0.95). Build a permutation with sum(d^2) = 68.
    counts = list(range(20))
    primary = list(range(20))
    # swap 0<->4 (d^2 16+16=32), 5<->8 (9+9=18), 10<->13 (9+9=18): total 68
    for a, b in ((0, 4), (5, 8), (10, 13)):
        primary[a], primary[b] = primary[b], primary[a]
    sims = [(i * 7) % 20 for i in range(20)]
    result = rrf_fuse.degeneracy(primary, counts, sims)
    assert result["rho_chunk_count"] == pytest.approx(1 - 408 / 7980)
    assert result["rho_chunk_count"] < 0.95
    assert abs(result["rho_max_sim"]) < 0.95
    assert result["degenerate"] is False


def test_a_negative_rho_beyond_the_threshold_is_degenerate_too():
    counts = list(range(20))
    primary = list(reversed(range(20)))
    sims = [(i * 7) % 20 for i in range(20)]
    result = rrf_fuse.degeneracy(primary, counts, sims)
    assert result["rho_chunk_count"] == pytest.approx(-1.0)
    assert result["degenerate"] is True


def test_the_max_sim_input_alone_can_mark_an_arm_degenerate():
    primary = list(range(20))
    counts = [(i * 7) % 20 for i in range(20)]
    sims = [i / 20 for i in range(20)]
    result = rrf_fuse.degeneracy(primary, counts, sims)
    assert abs(result["rho_chunk_count"]) < 0.95
    assert result["rho_max_sim"] == pytest.approx(1.0)
    assert result["degenerate"] is True


def test_a_constant_input_aborts_rather_than_reading_as_not_degenerate():
    with pytest.warns(Warning):
        with pytest.raises(SystemExit):
            rrf_fuse.degeneracy([1.0] * 5, [1, 2, 3, 4, 5], [0.1, 0.2, 0.3, 0.4, 0.5])
```

- [ ] **Step 2: Run them and watch them fail**

Run: `PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_rrf_fuse.py -q`
Expected: a collection error (`No module named 'rrf_fuse'`).

- [ ] **Step 3: Write the script**

```python
#!/usr/bin/env python3
"""Reciprocal rank fusion of the MatchPattern leg with the SearchChunks baseline, plus the INERT and
degeneracy checks that decide which arms report.py may score (spec
docs/specs/2026-09-28-matchpattern-rrf-test-design.md, "Phase 3" and "Degeneracy guard").

Fully offline. Reads the baseline `<label>.chunks.trec` and its `<label>.meta.json` sidecar, and the
files pattern_leg.py wrote under OUT/pattern/ (calibration.tsv, scores-<t>.tsv, mp-<t>-<score>.trec,
build.json). Writes, per arm (t in p50/p75/p90, score in run_len/run_sum -- six arms):

    OUT/runs/fused-<t>-<score>.chunks.trec   the fused run
    OUT/runs/fused-<t>-<score>.meta.json     its build sidecar
    OUT/fused/fused-<t>-<score>.rrf.tsv      query_id, doc_id, fused_rank, rrf

and, across the arms, OUT/fused/inert.json and OUT/fused/guard.json. Finally it prints the
`--pair RUN=BASELINE` arguments for every non-INERT arm, ready for report.py.

Fusion. k = 60, fixed in advance and never tuned:

    rrf = 1/(60 + rank_baseline) + 1/(60 + rank_pattern)

The second term is omitted, not zeroed or floored, for a candidate with no pattern rank (no run
above theta). Only the baseline's 50 candidates are fused, so every fused run has the baseline's
document set -- report.check_pool requires exactly that, and it is asserted here before writing.

Why the written score is `51 - fused_rank` and not the RRF value: swapped ranks tie EXACTLY
(1/63 + 1/67 == 1/67 + 1/63), and ir_measures breaks score ties by doc id, not by file order (spec
verified assumption 18). The fused order breaks RRF ties by baseline rank, which is unique per
query, so the order is strict -- but only a strictly decreasing written score makes ir_measures
score that order rather than a doc-id order. The RRF values themselves go to the .rrf.tsv sidecar.
`51 - rank` is teacher_rerank.py's convention: ranks 1..50 map to 50..1, never 0.000000.

Build identity. The fused sidecar is the baseline sidecar with `composite` replaced by the
composite pattern_leg.py MEASURED (build.json), so report.py's build check compares measured values
rather than a copied constant. Every before/after check in build.json must agree with each other,
with build.json's baseline_composite and with the baseline sidecar, and the calibration pass plus
every theta pass must be present, or this script refuses to write anything. The sidecar is named by
report.py's own rule (report.sidecar_path_for) and read back through report.load_build_composite,
so a naming drift fails here rather than as a quiet "BUILD UNKNOWN" in the report.

INERT. report.check_pool is the authority: an arm whose ranked sequence differs from the baseline
on fewer than 25% of queries fails it. check_pool reports that by sys.exit, which would end this
run too, so it is called inside `except SystemExit` -- report.py is not modified. The document-set
half of check_pool is asserted by this script before check_pool runs, so a SystemExit from it can
only be the reorder-fraction half; the fraction is recomputed with report.ranked_doc_ids and
report.POOL_MIN_REORDERED_FRACTION, and if the two disagree about the arm this script aborts.

Degeneracy guard. Spearman rho (scipy.stats.spearmanr) over every (query, candidate) pair of the
baseline, between the arm's primary score and (a) the candidate's chunk count and (b) its
max-chunk similarity, both from calibration.tsv. A candidate with no run takes a floor strictly
below every matched score in the arm, min(matched) - 1 (spec: user decision, CDR round 1 §3.1).
|rho| >= 0.95 against either marks the arm DEGENERATE. A non-finite rho (a constant input) aborts:
per this directory's finiteness-first convention it must not read as "not degenerate".

Not stdlib-only: scipy (the guard) and ir_measures (report.check_pool) are reached through
PYTHONPATH, exactly as report.py's are:

    PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs python3 \\
        Iverson.Server/Iverson.LoadTest/scripts/rrf_fuse.py \\
        --out /home/ben/repositories/iverson-benchmark-corpora/matchpattern-rrf-<date> \\
        --baseline /home/ben/repositories/iverson-benchmark-corpora/matchpattern-rrf-<date>/runs/<label>.chunks.trec
"""
import argparse
import json
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import report  # noqa: E402  (sidecar naming, check_pool, ranked_doc_ids -- reused, never copied)

RRF_K = 60                      # spec Phase 3: fixed in advance, not tuned
WRITTEN_SCORE_BASE = 51         # written score = 51 - fused_rank (teacher_rerank.py's convention)
MAX_CANDIDATES = WRITTEN_SCORE_BASE - 1   # more than 50 would drive the written score to <= 0
DEGENERATE_ABS_RHO = 0.95       # spec "Degeneracy guard": |rho| >= 0.95 against either input
THETAS = ("p50", "p75", "p90")
SCORES = ("run_len", "run_sum")
CALIBRATION_PASS = "calibration"
RUN_SUFFIX = report.CHUNKS_RUN_SUFFIX + ".trec"


def arm_label(theta, score):
    return f"fused-{theta}-{score}"


# ── Readers ─────────────────────────────────────────────────────────────────────────────

def load_ranked_run(path):
    """{query_id: [doc_id, ...]} in file order, and the query order of the file. The rank column
    must equal the 1-based position within its query: file order IS the ranked order for
    report.check_pool (report.ranked_doc_ids reads positionally), so a file whose rank column
    disagrees means something upstream sorted, filtered or concatenated it. A doc id repeated
    within a query is refused for the same reason report.structural_check counts it."""
    runs = {}
    order = []
    with open(path, encoding="utf-8") as f:
        for lineno, line in enumerate(f, start=1):
            if not line.strip():
                continue
            fields = line.split()
            if len(fields) != 6:
                sys.exit(f"{path}:{lineno}: expected 6 whitespace-separated fields, got {len(fields)}: {line!r}")
            query_id, _iter, doc_id, rank, _score, _tag = fields
            docs = runs.get(query_id)
            if docs is None:
                docs = runs[query_id] = []
                order.append(query_id)
            if doc_id in docs:
                sys.exit(f"{path}:{lineno}: document {doc_id!r} appears twice in query {query_id!r}")
            if rank != str(len(docs) + 1):
                sys.exit(f"{path}:{lineno}: rank {rank} is not position {len(docs) + 1} in query {query_id!r}; "
                         "file order must be rank order")
            docs.append(doc_id)
    if not runs:
        sys.exit(f"{path}: no run rows")
    return runs, order


def read_tsv(path, required_columns):
    """Rows of a header-first, tab-separated file as dicts. Missing file or missing column exits."""
    if not os.path.exists(path):
        sys.exit(f"{path}: not found")
    with open(path, encoding="utf-8") as f:
        header = f.readline().rstrip("\n").split("\t")
        missing = [c for c in required_columns if c not in header]
        if missing:
            sys.exit(f"{path}: header {header} lacks column(s) {missing}")
        rows = []
        for lineno, line in enumerate(f, start=2):
            if not line.strip():
                continue
            values = line.rstrip("\n").split("\t")
            if len(values) != len(header):
                sys.exit(f"{path}:{lineno}: {len(values)} fields, header has {len(header)}")
            rows.append(dict(zip(header, values)))
    return rows


def parse_float(value, where):
    try:
        number = float(value)
    except ValueError:
        sys.exit(f"{where}: {value!r} is not a number")
    if not math.isfinite(number):
        sys.exit(f"{where}: {value!r} is not finite")
    return number


def load_calibration(path):
    """{(query_id, doc_id): (chunk_count, max_s)} from calibration.tsv. An empty `s` is refused:
    pattern_leg.py never writes one (a NULL similarity aborts it), so one here means a hand-edited
    or foreign file."""
    stats = {}
    for i, row in enumerate(read_tsv(path, ("query_id", "doc_id", "chunk_index", "s")), start=2):
        s = parse_float(row["s"], f"{path}:{i} s")
        key = (row["query_id"], row["doc_id"])
        count, best = stats.get(key, (0, -math.inf))
        stats[key] = (count + 1, max(best, s))
    return stats


def load_scores(path):
    """{(query_id, doc_id): {"run_len": float, "run_sum": float}} from scores-<t>.tsv -- each column
    is that arm's PRIMARY score for the candidate (see pattern_leg.write_scores)."""
    scores = {}
    for i, row in enumerate(read_tsv(path, ("query_id", "doc_id") + SCORES), start=2):
        key = (row["query_id"], row["doc_id"])
        if key in scores:
            sys.exit(f"{path}:{i}: ({key[0]}, {key[1]}) appears twice")
        scores[key] = {name: parse_float(row[name], f"{path}:{i} {name}") for name in SCORES}
    return scores


# ── Build identity ──────────────────────────────────────────────────────────────────────

def measured_composite(build, baseline_composite, required_passes):
    """The one composite pattern_leg.py measured, or exit. Every before and after in every check,
    and build.json's own baseline_composite, must equal the baseline sidecar's composite; and every
    pass in `required_passes` (calibration plus each theta fused here) must have been checked."""
    if not baseline_composite:
        sys.exit("the baseline sidecar has no composite; a fused run cannot be attributed to a build")
    if build.get("baseline_composite") != baseline_composite:
        sys.exit(f"build.json baseline_composite {build.get('baseline_composite')!r} != baseline sidecar "
                 f"composite {baseline_composite!r}")
    checks = build.get("checks") or []
    seen = {c.get("pass") for c in checks}
    missing = [p for p in required_passes if p not in seen]
    if missing:
        sys.exit(f"build.json has no /build check for pass(es) {missing}; refusing to fuse unverified output")
    for check in checks:
        for side in ("before", "after"):
            if check.get(side) != baseline_composite:
                sys.exit(f"build.json: pass {check.get('pass')!r} {side} composite {check.get(side)!r} != "
                         f"baseline {baseline_composite!r} -- the pattern leg ran on a different build")
    return baseline_composite


def write_sidecar(run_path, baseline_sidecar, composite, label):
    """The baseline sidecar's JSON with composite replaced by the MEASURED one and configLabel set
    to the fused label, at report.sidecar_path_for(run_path) -- then read back through
    report.load_build_composite, so a sidecar report.py cannot find fails here."""
    sidecar = dict(baseline_sidecar)
    sidecar["composite"] = composite
    sidecar["configLabel"] = label
    path = report.sidecar_path_for(run_path)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(sidecar, f, indent=2)
        f.write("\n")
    if report.load_build_composite(run_path) != composite:
        sys.exit(f"{path}: report.load_build_composite({run_path}) does not read back {composite!r}")
    return path


# ── Fusion ─────────────────────────────────────────────────────────────────────────────

def rrf_score(baseline_rank, pattern_rank):
    """1/(k + rank_b) + 1/(k + rank_p); the second term is absent when pattern_rank is None."""
    score = 1.0 / (RRF_K + baseline_rank)
    if pattern_rank is not None:
        score += 1.0 / (RRF_K + pattern_rank)
    return score


def fuse_query(baseline_docs, pattern_docs):
    """[(doc_id, rrf), ...] in fused order: rrf descending, ties by baseline rank ascending (unique
    per query, so the order is strict). `pattern_docs` is the pattern leg's ranked list for this
    query (matched candidates only); every one must be a baseline candidate."""
    baseline_rank = {doc: i for i, doc in enumerate(baseline_docs, start=1)}
    stray = [d for d in pattern_docs if d not in baseline_rank]
    if stray:
        sys.exit(f"pattern leg ranks {len(stray)} document(s) that are not baseline candidates, e.g. {stray[:3]}")
    pattern_rank = {doc: i for i, doc in enumerate(pattern_docs, start=1)}
    fused = [(doc, rrf_score(baseline_rank[doc], pattern_rank.get(doc))) for doc in baseline_docs]
    fused.sort(key=lambda pair: (-pair[1], baseline_rank[pair[0]]))
    if {d for d, _ in fused} != set(baseline_docs) or len(fused) != len(baseline_docs):
        sys.exit("fused document set differs from the baseline's")  # unreachable by construction; kept loud
    return fused


def fuse_run(baseline, baseline_order, pattern):
    """{query_id: [(doc_id, rrf), ...]} for every baseline query. A query the pattern leg has no row
    for fuses with every second term absent (no candidate had a run)."""
    stray = sorted(set(pattern) - set(baseline))
    if stray:
        sys.exit(f"pattern leg has {len(stray)} query id(s) absent from the baseline, e.g. {stray[:3]}")
    return {q: fuse_query(baseline[q], pattern.get(q, [])) for q in baseline_order}


def write_fused_run(path, fused, query_order, tag):
    """6-column TREC, written score 51 - fused_rank (strictly decreasing within a query)."""
    with open(path, "w", encoding="utf-8") as f:
        for query_id in query_order:
            for rank, (doc_id, _rrf) in enumerate(fused[query_id], start=1):
                f.write(f"{query_id} Q0 {doc_id} {rank} {WRITTEN_SCORE_BASE - rank:.6f} {tag}\n")


def write_rrf_tsv(path, fused, query_order):
    with open(path, "w", encoding="utf-8") as f:
        f.write("query_id\tdoc_id\tfused_rank\trrf\n")
        for query_id in query_order:
            for rank, (doc_id, rrf) in enumerate(fused[query_id], start=1):
                f.write(f"{query_id}\t{doc_id}\t{rank}\t{rrf!r}\n")


# ── INERT (report.check_pool, without its sys.exit ending this run) ─────────────────────

def classify_inert(run_path, baseline_path):
    """{"reordered_fraction", "inert"} for one fused arm, decided by report.check_pool itself.

    check_pool exits on either half of its rule. The document-set half is asserted here first, so a
    SystemExit afterwards can only be the reorder-fraction half, which is exactly INERT. The
    fraction is recomputed from report.ranked_doc_ids for the record, and the two verdicts must
    agree or the run aborts -- a disagreement means report.py's rule moved under this script."""
    run_seq = report.ranked_doc_ids(run_path)
    base_seq = report.ranked_doc_ids(baseline_path)
    common = sorted(set(run_seq) & set(base_seq))
    if not common:
        sys.exit(f"{run_path}: no query in common with {baseline_path}")
    set_changed = [q for q in common if set(run_seq[q]) != set(base_seq[q])]
    if set_changed:
        sys.exit(f"{run_path}: document set differs from the baseline on {len(set_changed)} queries "
                 f"(first: {set_changed[0]}) -- fusion must only reorder")
    fraction = sum(run_seq[q] != base_seq[q] for q in common) / len(common)
    try:
        report.check_pool(run_path, baseline_path)
        failed = False
    except SystemExit as exit_:
        print(f"  (check_pool exited: {exit_.code})")
        failed = True
    expected = fraction < report.POOL_MIN_REORDERED_FRACTION
    if failed != expected:
        sys.exit(f"{run_path}: report.check_pool {'failed' if failed else 'passed'} but the reorder fraction "
                 f"{fraction:.4f} says inert={expected}; report.py's pool rule has changed under this script")
    return {"reordered_fraction": fraction, "inert": failed}


# ── Degeneracy guard ────────────────────────────────────────────────────────────────────

def guard_vectors(baseline, baseline_order, arm_scores, calibration):
    """(primary, chunk_count, max_sim) as three parallel lists over every baseline (query, candidate)
    pair. `arm_scores` is {(query_id, doc_id): primary} for matched candidates only; the rest take
    the floor min(matched) - 1, strictly below every matched score. A candidate absent from
    calibration aborts: its chunk count and max similarity are the guard's inputs."""
    stray = [k for k in arm_scores if k[0] not in baseline or k[1] not in baseline[k[0]]]
    if stray:
        sys.exit(f"{len(stray)} scored (query, doc) pair(s) are not baseline candidates, e.g. {stray[:3]}")
    if not arm_scores:
        sys.exit("the arm has no matched candidate at all; the guard is undefined (every score is the floor)")
    floor = min(arm_scores.values()) - 1
    primary, counts, max_sims = [], [], []
    missing = []
    for query_id in baseline_order:
        for doc_id in baseline[query_id]:
            key = (query_id, doc_id)
            if key not in calibration:
                missing.append(key)
                continue
            count, best = calibration[key]
            primary.append(arm_scores.get(key, floor))
            counts.append(count)
            max_sims.append(best)
    if missing:
        sys.exit(f"{len(missing)} baseline candidate(s) have no calibration row, e.g. {missing[:3]}")
    return primary, counts, max_sims


def spearman(x, y, what):
    try:
        from scipy.stats import spearmanr
    except ImportError as e:
        sys.exit(f"could not import scipy ({e}); set PYTHONPATH to the corpora repo's python-libs "
                 "(see this script's docstring)")
    rho = float(spearmanr(x, y).statistic)
    if not math.isfinite(rho):
        sys.exit(f"guard: Spearman rho against {what} is {rho!r} (a constant input); a non-finite rho must "
                 "not read as 'not degenerate'")
    return rho


def degeneracy(primary, counts, max_sims):
    rho_count = spearman(primary, counts, "chunk count")
    rho_sim = spearman(primary, max_sims, "max-chunk similarity")
    return {
        "rho_chunk_count": rho_count,
        "rho_max_sim": rho_sim,
        "degenerate": abs(rho_count) >= DEGENERATE_ABS_RHO or abs(rho_sim) >= DEGENERATE_ABS_RHO,
    }


# ── CLI ─────────────────────────────────────────────────────────────────────────────────

def build_arg_parser():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", required=True, help="the benchmark's OUT directory (holds runs/ and pattern/)")
    ap.add_argument("--baseline", required=True,
                    help="the re-recorded baseline <label>.chunks.trec (its <label>.meta.json must sit beside it)")
    return ap


def main(argv=None):
    args = build_arg_parser().parse_args(argv)
    out = args.out
    pattern_dir = os.path.join(out, "pattern")
    runs_dir = os.path.join(out, "runs")
    fused_dir = os.path.join(out, "fused")

    if not args.baseline.endswith(RUN_SUFFIX):
        sys.exit(f"--baseline {args.baseline}: expected a <label>{RUN_SUFFIX} run")
    baseline_sidecar_path = report.sidecar_path_for(args.baseline)
    if not os.path.exists(baseline_sidecar_path):
        sys.exit(f"{baseline_sidecar_path}: the baseline's sidecar is missing")
    with open(baseline_sidecar_path, encoding="utf-8") as f:
        baseline_sidecar = json.load(f)
    with open(os.path.join(pattern_dir, "build.json"), encoding="utf-8") as f:
        build = json.load(f)
    composite = measured_composite(build, report.load_build_composite(args.baseline),
                                   (CALIBRATION_PASS,) + THETAS)

    baseline, baseline_order = load_ranked_run(args.baseline)
    oversized = [q for q in baseline_order if len(baseline[q]) > MAX_CANDIDATES]
    if oversized:
        sys.exit(f"{len(oversized)} baseline queries hold more than {MAX_CANDIDATES} candidates, e.g. {oversized[:3]}")
    calibration = load_calibration(os.path.join(pattern_dir, "calibration.tsv"))

    os.makedirs(runs_dir, exist_ok=True)
    os.makedirs(fused_dir, exist_ok=True)
    inert, guard, pairs = {}, {}, []
    for theta in THETAS:
        scores = load_scores(os.path.join(pattern_dir, f"scores-{theta}.tsv"))
        for score in SCORES:
            label = arm_label(theta, score)
            mp_path = os.path.join(pattern_dir, f"mp-{theta}-{score}.trec")
            pattern, _ = load_ranked_run(mp_path) if os.path.getsize(mp_path) else ({}, [])
            mp_pairs = {(q, d) for q, docs in pattern.items() for d in docs}
            if mp_pairs != set(scores):
                sys.exit(f"{mp_path}: its (query, doc) pairs differ from scores-{theta}.tsv's "
                         f"({len(mp_pairs)} vs {len(scores)})")

            fused = fuse_run(baseline, baseline_order, pattern)
            run_path = os.path.join(runs_dir, label + RUN_SUFFIX)
            write_fused_run(run_path, fused, baseline_order, label)
            write_rrf_tsv(os.path.join(fused_dir, label + ".rrf.tsv"), fused, baseline_order)
            write_sidecar(run_path, baseline_sidecar, composite, label)

            print(f"\n[rrf_fuse] {label}")
            inert[label] = classify_inert(run_path, args.baseline)
            primary = {key: values[score] for key, values in scores.items()}
            guard[label] = degeneracy(*guard_vectors(baseline, baseline_order, primary, calibration))
            print(f"  reordered {inert[label]['reordered_fraction'] * 100:.1f}%  "
                  f"{'INERT' if inert[label]['inert'] else 'active'}   "
                  f"rho(count) {guard[label]['rho_chunk_count']:+.4f}  rho(max_sim) {guard[label]['rho_max_sim']:+.4f}  "
                  f"{'DEGENERATE' if guard[label]['degenerate'] else 'ok'}")
            if not inert[label]["inert"]:
                pairs.append(f"{run_path}={args.baseline}")

    for name, data in (("inert.json", inert), ("guard.json", guard)):
        with open(os.path.join(fused_dir, name), "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
            f.write("\n")

    print(f"\n[rrf_fuse] composite {composite}; {len(pairs)} of {len(inert)} arms are not INERT")
    print("[rrf_fuse] report.py --pair arguments:")
    for pair in pairs:
        print(f"  --pair {pair}")


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: Run the tests**

Run: `PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_rrf_fuse.py -q`
Expected: `27 passed`. Without the `PYTHONPATH` the scipy and ir_measures tests fail. That is expected, and it is why every `rrf_fuse.py` command in this plan sets the path.

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.LoadTest/scripts/rrf_fuse.py Iverson.Server/Iverson.LoadTest/scripts/test_rrf_fuse.py
git commit -m "add the offline rrf fusion, inert check and degeneracy guard for the matchpattern leg" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 3: `pattern_leg.py`, the live MatchPattern leg

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/pattern_leg.py` (prototype md5 `6d6695fcab6c22cb4be6c0be86eed9cf`)
- Create: `Iverson.Server/Iverson.LoadTest/scripts/test_pattern_leg.py` (prototype md5 `03ef1b2a1f5e78b7a5abec7a381d53c9`)

**Interfaces:**
- Produces, for Tasks 4 and 5:
  - the subcommands `probe-a`, `probe-b`, `calibrate --shape` and `run --shape --theta`;
  - the `OUT/pattern/` files listed in Task 2's Interfaces;
  - `OUT/pattern/probe-b.json`.

- [ ] **Step 1: Write the tests**

```python
"""pytest suite for pattern_leg.py (spec docs/specs/2026-09-28-matchpattern-rrf-test-design.md, Phase 0
and Phase 2). Run with:

    python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_pattern_leg.py -q

No network: the coordinator, the token minter, the clock and the /build fetch are fakes. The request
builders run through the real SDK (Iverson.Clients/Python, put on sys.path by pattern_leg itself), so
the field assertions below pin the exact protos the live run sends. grpcio and protobuf come from the
user site-packages; nothing needs PYTHONPATH (setting it to python-libs is harmless)."""
import os
import sys

import grpc
import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pattern_leg as pl  # noqa: E402
from iverson_client import MatchPatternResult  # noqa: E402  (on sys.path via pattern_leg)

pb = pl.pb


# ── fakes ───────────────────────────────────────────────────────────────────────────────

class FakeRpcError(grpc.RpcError):
    def __init__(self, code=grpc.StatusCode.DEADLINE_EXCEEDED, details="MatchPattern exceeded its time limit."):
        self._code, self._details = code, details

    def code(self):
        return self._code

    def details(self):
        return self._details


class FakeCoordinator:
    """Records every request and the token it was bound to; `respond(request)` returns rows or raises."""

    def __init__(self, respond, token=None, log=None):
        self.respond, self.token = respond, token
        self.log = log if log is not None else []

    def with_acting_user(self, token):
        return FakeCoordinator(self.respond, token, self.log)

    def match_pattern(self, request):
        self.log.append((self.token, request))
        return self.respond(request)


def session_for(respond, clock=lambda: 0.0):
    tokens = iter(f"token-{i}" for i in range(1, 100))
    coordinator = FakeCoordinator(respond)
    return pl.TokenSession(coordinator, lambda: next(tokens), clock=clock), coordinator.log


def row(**data):
    return MatchPatternResult(data=data, match_number=1, classifier="A")


# ── escaping and expressions ────────────────────────────────────────────────────────────

def test_escape_literal_doubles_every_single_quote():
    assert pl.escape_literal("it's") == "it''s"
    assert pl.escape_literal("''") == "''''"
    assert pl.escape_literal('no "quotes" here') == 'no "quotes" here'


def test_similarity_wraps_the_escaped_query_in_a_string_literal():
    assert pl.similarity("A.text", "Bob's `x`") == "SIMILARITY(A.text, 'Bob''s `x`')"


def test_format_theta_round_trips_and_refuses_non_finite():
    assert float(pl.format_theta(0.1 + 0.2)) == 0.1 + 0.2
    with pytest.raises(SystemExit):
        pl.format_theta(float("nan"))


# ── request fields: both shapes x both call kinds ───────────────────────────────────────

GUID = "7c5a8e9b-82ef-5c17-91d6-7e55ac356209"
QUERY = "why won't it's work?"
ESCAPED = "why won''t it''s work?"


def common_fields(request, pattern="A+"):
    assert request.type_name == "BenchmarkDocument"
    assert request.source == pb.CHUNKS
    assert request.chunk_property == "Body"
    assert list(request.partition_by) == [] and list(request.order_by) == []
    assert request.pattern == pattern
    assert not request.HasField("after_match")        # server default: SKIP PAST LAST ROW


def assert_per_candidate_where(request):
    assert len(request.where) == 1
    clause = request.where[0]
    assert (clause.property, clause.operator, clause.clause_type) == ("Id", pb.EQUALS, pb.FILTER)
    assert clause.value.WhichOneof("kind") == "string_val" and clause.value.string_val == GUID


@pytest.mark.parametrize("shape, guid", [("per-candidate", GUID), ("whole-corpus", None)])
def test_calibration_request_fields(shape, guid):
    """The text-free calibration call: pattern A (one single-row match per chunk), ONE ROW, s and ci."""
    request = pl.calibration_request(QUERY, guid, pl.shape_limit(shape))
    common_fields(request, pattern="A")
    if guid:
        assert_per_candidate_where(request)
        assert request.limit == 10_000
    else:
        assert len(request.where) == 0
        assert request.limit == 18_623
    assert [(d.name, d.expr) for d in request.define] == [("A", "TRUE")]
    assert [(m.name, m.expr) for m in request.measures] == [
        ("s", f"SIMILARITY(text, '{ESCAPED}')"),
        ("ci", "chunk_index"),
    ]
    assert request.rows_per_match == pb.ONE_ROW


@pytest.mark.parametrize("shape, guid", [("per-candidate", GUID), ("whole-corpus", None)])
def test_theta_request_fields(shape, guid):
    request = pl.theta_request(QUERY, 0.61, guid, pl.shape_limit(shape))
    common_fields(request)
    if guid:
        assert_per_candidate_where(request)
        assert request.limit == 10_000
    else:
        assert len(request.where) == 0
        assert request.limit == 18_623
    assert [(d.name, d.expr) for d in request.define] == [("A", f"SIMILARITY(text, '{ESCAPED}') > 0.61")]
    assert [(m.name, m.expr) for m in request.measures] == [
        ("run_len", "COUNT(*)"),
        ("run_sum", f"SUM(SIMILARITY(A.text, '{ESCAPED}'))"),
    ]
    assert request.rows_per_match == pb.ONE_ROW


def test_probe_a_request_is_the_row_carrying_running_sum_call():
    request = pl.probe_a_request(QUERY, GUID, pl.PER_CANDIDATE_LIMIT)
    common_fields(request)
    assert_per_candidate_where(request)
    assert [(d.name, d.expr) for d in request.define] == [("A", "TRUE")]
    assert [(m.name, m.expr) for m in request.measures] == [
        ("s", f"SIMILARITY(text, '{ESCAPED}')"),
        ("run_sum", f"SUM(SIMILARITY(A.text, '{ESCAPED}'))"),
    ]
    assert request.rows_per_match == pb.ALL_ROWS_SHOW_EMPTY


# ── best-run selection and every tie-break ──────────────────────────────────────────────

def test_best_run_takes_the_highest_primary():
    runs = [(2, 1.9), (3, 1.2), (1, 0.8)]
    assert pl.best_run(runs, "run_len") == (3, 1.2)
    assert pl.best_run(runs, "run_sum") == (1.9, 2)


def test_best_run_breaks_a_primary_tie_by_the_other_score():
    assert pl.best_run([(3, 1.2), (3, 1.5), (2, 1.9)], "run_len") == (3, 1.5)
    assert pl.best_run([(2, 1.5), (4, 1.5)], "run_sum") == (1.5, 4)


def test_pattern_order_sorts_by_primary_descending():
    runs = {"a": [(1, 0.9)], "b": [(3, 0.7)], "c": [(2, 0.8)]}
    assert pl.pattern_order(runs, "run_len", ["a", "b", "c"]) == ["b", "c", "a"]
    assert pl.pattern_order(runs, "run_sum", ["a", "b", "c"]) == ["a", "c", "b"]


def test_pattern_order_breaks_a_primary_tie_by_the_other_score():
    runs = {"a": [(2, 1.1)], "b": [(2, 1.4)]}
    assert pl.pattern_order(runs, "run_len", ["a", "b"]) == ["b", "a"]
    runs = {"a": [(1, 0.9)], "b": [(2, 0.9)]}
    assert pl.pattern_order(runs, "run_sum", ["a", "b"]) == ["b", "a"]


def test_pattern_order_breaks_a_full_tie_by_baseline_rank():
    runs = {"a": [(2, 1.4)], "b": [(2, 1.4)], "c": [(2, 1.4)]}
    assert pl.pattern_order(runs, "run_len", ["c", "a", "b"]) == ["c", "a", "b"]
    assert pl.pattern_order(runs, "run_sum", ["b", "c", "a"]) == ["b", "c", "a"]


def test_pattern_order_uses_the_other_score_of_the_best_run_not_the_best_other_score():
    # a's longest run has run_sum 1.0; its best run_sum (2.0) belongs to a shorter run.
    runs = {"a": [(3, 1.0), (1, 2.0)], "b": [(3, 1.5)]}
    assert pl.pattern_order(runs, "run_len", ["a", "b"]) == ["b", "a"]


# ── calls: count at limit, gRPC errors, NULLs ───────────────────────────────────────────

def test_execute_aborts_when_the_count_reaches_limit():
    request = pl.calibration_request(QUERY, GUID, 3)
    session, _ = session_for(lambda r: [row(parent_key=GUID, ci=float(i), s=0.5) for i in range(3)])
    with pytest.raises(SystemExit, match="truncated"):
        pl.execute(session, request, "t")


def test_execute_accepts_a_count_one_below_limit():
    request = pl.calibration_request(QUERY, GUID, 3)
    session, _ = session_for(lambda r: [row(parent_key=GUID, ci=float(i), s=0.5) for i in range(2)])
    assert len(pl.execute(session, request, "t")) == 2


@pytest.mark.parametrize("code", [grpc.StatusCode.DEADLINE_EXCEEDED, grpc.StatusCode.INVALID_ARGUMENT,
                                  grpc.StatusCode.UNAVAILABLE])
def test_a_grpc_error_aborts(code):
    def raise_(request):
        raise FakeRpcError(code, "boom")
    session, _ = session_for(raise_)
    with pytest.raises(SystemExit, match=str(code)):
        pl.execute(session, pl.theta_request(QUERY, 0.5, GUID, 10), "t")


def test_a_null_similarity_aborts_calibration():
    with pytest.raises(SystemExit, match="NULL"):
        pl.calibration_chunks([row(parent_key=GUID, ci=0.0, s=None)], "t")


def test_a_null_run_sum_aborts_a_theta_pass():
    with pytest.raises(SystemExit, match="NULL"):
        pl.theta_runs([row(parent_key=GUID, run_len=2.0, run_sum=None)], "t")


def test_calibration_chunks_reads_ci_and_s_in_chunk_order():
    """The engine emits ci as a long and the server a proto number, so ci arrives as a float."""
    rows = [row(parent_key=GUID, s=0.9, ci=2.0), row(parent_key=GUID, s=0.3, ci=0.0),
            row(parent_key=GUID, s=0.6, ci=1.0)]
    assert pl.calibration_chunks(rows, "t") == [(0, 0.3), (1, 0.6), (2, 0.9)]


def test_calibration_chunks_refuses_a_row_without_ci():
    """A row carrying chunk_index but no ci is the old ALL ROWS shape, not the calibration call's."""
    with pytest.raises(SystemExit, match="'ci'"):
        pl.calibration_chunks([row(parent_key=GUID, chunk_index=0.0, s=0.1)], "t")


def test_probe_a_parses_its_all_rows_call_by_chunk_index():
    rows = [row(parent_key=GUID, chunk_index=1.0, text="b", s=0.4), row(parent_key=GUID, chunk_index=0.0, text="a", s=0.2)]
    assert pl.calibration_chunks(rows, "t", index_column="chunk_index") == [(0, 0.2), (1, 0.4)]


def test_calibration_chunks_refuses_a_duplicate_or_fractional_chunk_index():
    with pytest.raises(SystemExit):
        pl.calibration_chunks([row(ci=0.0, s=0.1), row(ci=0.0, s=0.2)], "t")
    with pytest.raises(SystemExit):
        pl.calibration_chunks([row(ci=0.5, s=0.1)], "t")


def respond_calibration(request):
    """One text-free row per chunk, as the engine probe showed: g1 has chunks 0..2, g3 one chunk,
    g4 is a non-candidate only the whole corpus returns."""
    chunks = {"g1": [(0, 0.1), (1, 0.7), (2, 0.4)], "g2": [(0, 0.5)], "g3": [(0, 0.2)], "g4": [(0, 0.9)]}
    rows = {g: [row(parent_key=g, s=s, ci=float(i)) for i, s in cs] for g, cs in chunks.items()}
    if request.where:
        return rows[request.where[0].value.string_val]
    return [r for rs in rows.values() for r in rs]


@pytest.mark.parametrize("shape, calls", [("per-candidate", 3), ("whole-corpus", 2)])
def test_a_calibration_pass_gives_the_same_chunks_in_both_shapes(shape, calls):
    session, log = session_for(respond_calibration)
    result = pl.run_pass(session, shape, lambda q, guid: pl.calibration_request("q", guid, pl.shape_limit(shape)),
                         pl.calibration_chunks, BASELINE, ["q1", "q2"], MAPS)
    assert result == {"q1": {"d1": [(0, 0.1), (1, 0.7), (2, 0.4)], "d2": [(0, 0.5)]}, "q2": {"d3": [(0, 0.2)]}}
    assert len(log) == calls
    assert all(r.pattern == "A" and r.rows_per_match == pb.ONE_ROW for _, r in log)


def test_rows_by_candidate_refuses_a_foreign_parent_in_the_per_candidate_shape():
    with pytest.raises(SystemExit):
        pl.rows_by_candidate([row(parent_key="other")], {"other": "d2", GUID: "d1"}, {"d1"}, GUID, "t")


def test_rows_by_candidate_refuses_an_unknown_parent_and_drops_non_candidates_whole_corpus():
    keymap = {"g1": "d1", "g2": "d2"}
    grouped = pl.rows_by_candidate([row(parent_key="g1"), row(parent_key="g2")], keymap, {"d1"}, None, "t")
    assert list(grouped) == ["d1"]
    with pytest.raises(SystemExit):
        pl.rows_by_candidate([row(parent_key="g9")], keymap, {"d1"}, None, "t")


# ── a whole pass through the fake coordinator ───────────────────────────────────────────

BASELINE = {"q1": ["d1", "d2"], "q2": ["d3"]}
MAPS = ({"d1": "g1", "d2": "g2", "d3": "g3", "d4": "g4"},
        {"g1": "d1", "g2": "d2", "g3": "d3", "g4": "d4"})


def respond_theta(request):
    """g1 has two runs, g3 one, g2 none; g4 is a non-candidate that only the whole corpus returns."""
    rows = {"g1": [row(parent_key="g1", run_len=2.0, run_sum=1.3), row(parent_key="g1", run_len=1.0, run_sum=0.9)],
            "g3": [row(parent_key="g3", run_len=1.0, run_sum=0.7)],
            "g4": [row(parent_key="g4", run_len=5.0, run_sum=3.0)]}
    if request.where:
        return rows.get(request.where[0].value.string_val, [])
    return [r for rs in rows.values() for r in rs]


@pytest.mark.parametrize("shape, calls", [("per-candidate", 3), ("whole-corpus", 2)])
def test_run_pass_gives_the_same_runs_in_both_shapes(shape, calls):
    session, log = session_for(respond_theta)
    runs = pl.run_pass(session, shape, lambda q, guid: pl.theta_request("q", 0.5, guid, pl.shape_limit(shape)),
                       pl.theta_runs, BASELINE, ["q1", "q2"], MAPS)
    assert runs == {"q1": {"d1": [(2, 1.3), (1, 0.9)]}, "q2": {"d3": [(1, 0.7)]}}
    assert len(log) == calls


def test_a_pass_that_outlives_the_refresh_age_mints_again():
    now = [0.0]

    def respond(request):
        now[0] += 40 * 60          # every call takes 40 minutes
        return []
    session, log = session_for(respond, clock=lambda: now[0])
    pl.run_pass(session, "per-candidate", lambda q, guid: pl.theta_request("q", 0.5, guid, 10),
                pl.theta_runs, BASELINE, ["q1", "q2"], MAPS)
    # calls start at 0, 40 and 80 minutes (token-1), then... the third starts at 80 < 90: still token-1.
    assert [t for t, _ in log] == ["token-1", "token-1", "token-1"]
    pl.run_pass(session, "per-candidate", lambda q, guid: pl.theta_request("q", 0.5, guid, 10),
                pl.theta_runs, BASELINE, ["q1"], MAPS)
    # the fourth call starts at 120 minutes >= 90: re-minted.
    assert [t for t, _ in log][3:] == ["token-2", "token-2"]
    assert session.mints == 2


def test_the_token_is_not_reminted_before_the_refresh_age():
    now = [0.0]
    session, _ = session_for(lambda r: [], clock=lambda: now[0])
    first = session.coordinator()
    now[0] = pl.TOKEN_REFRESH_SECONDS - 1
    assert session.coordinator() is first
    now[0] = pl.TOKEN_REFRESH_SECONDS
    assert session.coordinator().token == "token-2"


# ── /build ──────────────────────────────────────────────────────────────────────────────

def test_run_checked_pass_records_both_checks_when_the_build_matches():
    result, check = pl.run_checked_pass(lambda url: "c1", "u", "c1", "p50", lambda: 42)
    assert result == 42 and check == {"pass": "p50", "before": "c1", "after": "c1"}


def test_a_build_mismatch_before_the_pass_aborts_without_running_it():
    ran = []
    with pytest.raises(SystemExit, match="BUILD MISMATCH"):
        pl.run_checked_pass(lambda url: "c2", "u", "c1", "p50", lambda: ran.append(1))
    assert ran == []


def test_a_build_mismatch_after_the_pass_aborts():
    answers = iter(["c1", "c2"])
    with pytest.raises(SystemExit, match="BUILD MISMATCH"):
        pl.run_checked_pass(lambda url: next(answers), "u", "c1", "calibration", lambda: None)


# ── theta percentiles over the 100 lowest query ids ─────────────────────────────────────

def test_sample_is_the_100_lowest_query_ids_in_numeric_order():
    ids = [str(i) for i in range(1, 106)]                  # "10" < "9" as strings, not as numbers
    assert pl.sample_query_ids(ids) == [str(i) for i in range(1, 101)]


def test_theta_uses_only_the_sample_queries_chunks():
    # query i has one candidate with chunks s = i/1000 and i/1000 + 0.0005; queries 101..105 carry
    # huge values that would move every percentile if they leaked in.
    calibration = {str(i): {"d": [(0, i / 1000), (1, i / 1000 + 0.0005)]} for i in range(1, 101)}
    calibration.update({str(i): {"d": [(0, 99.0)]} for i in range(101, 106)})
    theta = pl.theta_from_calibration(calibration, list(calibration))
    values = sorted(s for i in range(1, 101) for s in (i / 1000, i / 1000 + 0.0005))
    assert theta["rows_in_sample"] == 200
    assert theta["sample_query_ids"] == [str(i) for i in range(1, 101)]
    # inclusive (linear) method: position (n - 1) * p over the sorted 200 values
    for name, p in (("p50", 0.50), ("p75", 0.75), ("p90", 0.90)):
        pos = (len(values) - 1) * p
        lo = int(pos)
        expected = values[lo] + (values[lo + 1] - values[lo]) * (pos - lo)
        assert theta[name] == pytest.approx(expected)


def test_percentiles_on_a_hand_computable_set():
    assert pl.percentiles([float(i) for i in range(101)]) == {"p50": 50.0, "p75": 75.0, "p90": 90.0}


def test_fewer_than_100_queries_refuses_theta():
    with pytest.raises(SystemExit):
        pl.sample_query_ids([str(i) for i in range(99)])


# ── writers: TREC order equals rank order ───────────────────────────────────────────────

def test_pattern_trec_file_order_is_rank_order(tmp_path):
    runs = {"q1": {"d1": [(1, 0.4)], "d2": [(3, 1.8)], "d3": [(3, 1.8)], "d4": [(2, 2.5)]}}
    baseline = {"q1": ["d1", "d2", "d3", "d4", "d5"], "q2": ["d6"]}
    path = tmp_path / "mp-p50-run_len.trec"
    pl.write_pattern_trec(str(path), runs, ["q1", "q2"], baseline, "run_len", "mp-p50-run_len")
    rows = [line.split() for line in path.read_text().splitlines()]
    assert [r[2] for r in rows] == ["d2", "d3", "d4", "d1"] == pl.pattern_order(runs["q1"], "run_len", baseline["q1"])
    assert [int(r[3]) for r in rows] == [1, 2, 3, 4]
    scores = [float(r[4]) for r in rows]
    assert all(a > b for a, b in zip(scores, scores[1:]))
    assert {r[0] for r in rows} == {"q1"}                   # q2 matched nothing: no rows


def test_write_scores_holds_each_arms_primary_and_its_tiebreak(tmp_path):
    runs = {"q1": {"d1": [(3, 1.0), (1, 2.0)]}}
    path = tmp_path / "scores-p50.tsv"
    pl.write_scores(str(path), runs, ["q1"], {"q1": ["d1", "d2"]})
    lines = path.read_text().splitlines()
    assert lines[0] == "query_id\tdoc_id\trun_len\trun_sum\trun_sum_of_best_len\trun_len_of_best_sum"
    assert lines[1:] == ["q1\td1\t3\t2.0\t1.0\t1"]


# ── probe B's decision rule ─────────────────────────────────────────────────────────────

def test_probe_decision_prefers_per_candidate_within_8_hours():
    # 8 h = 28,800 s over 33,600 calls = 0.857 s per call
    assert pl.probe_decision({"calibration": 0.85, "theta": 0.80}, [(40.0, True)]) == "per-candidate"


def test_probe_decision_falls_back_to_whole_corpus_then_stops():
    slow = {"calibration": 0.9, "theta": 0.5}
    assert pl.probe_decision(slow, [(12.0, False), (29.9, False)]) == "whole-corpus"
    assert pl.probe_decision(slow, [(12.0, False), (30.0, True)]) == "STOP"
```

- [ ] **Step 2: Run them and watch them fail**

Run: `python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_pattern_leg.py -q`
Expected: a collection error (`No module named 'pattern_leg'`).

- [ ] **Step 3: Write the script**

```python
#!/usr/bin/env python3
"""The MatchPattern leg of the MatchPattern-RRF benchmark (spec
docs/specs/2026-09-28-matchpattern-rrf-test-design.md, "Phase 0" probes and "Phase 2").

Talks to the live MatchPattern RPC through the Python SDK (Iverson.Clients/Python). Four subcommands,
all taking --out (the benchmark's OUT directory) and --baseline (a <label>.chunks.trec whose 50
candidates per query are the documents the pattern leg scores):

    probe-a    Two CHUNKS calls over one candidate's chunks. (1) A+, ALL ROWS, s = SIMILARITY(text, q)
               plus a RUNNING SUM(SIMILARITY(A.text, q)), so the aggregate-over-SIMILARITY path -- which
               no server test covers, spec assumption 9 -- is exercised live. (2) The text-free
               calibration call. Prints the rows, and fails on no rows, a NULL s, a running sum that is
               not the cumulative sum of s, or a calibration (ci, s) that differs from call (1)'s
               (chunk_index, s) for any chunk.
    probe-b    Times the calibration call and the theta call (at a placeholder theta) in both call
               shapes over the 10 lowest query ids; projects the per-candidate pass as
               mean seconds per call x 672 x 50; applies the spec's rule (per-candidate if <= 8 h per
               pass, else whole-corpus if no whole-corpus call hit the 30 s timeout, else STOP); prints
               and writes OUT/pattern/probe-b.json.
    calibrate  One similarity pass over every candidate of every query: A, A AS TRUE, ONE ROW PER
               MATCH, s = SIMILARITY(text, q), ci = chunk_index -- one text-free row per chunk. Writes calibration.tsv and theta.json (p50/p75/p90 of s over
               the chunks of the 100 lowest query ids' candidates) and starts build.json.
    run        One theta pass: A+, A AS SIMILARITY(text, q) > theta, ONE ROW PER MATCH, run_len =
               COUNT(*), run_sum = SUM(SIMILARITY(A.text, q)). Writes scores-<t>.tsv and
               mp-<t>-run_len.trec / mp-<t>-run_sum.trec, and appends to build.json.

The two call shapes (spec Phase 0 step 5):
    per-candidate  one call per (query, candidate), `where Id = <parent GUID>`, limit
                   PER_CANDIDATE_LIMIT (10,000). A CHUNKS partition larger than MaxPartitionRows
                   (10,000 by default) is refused by the server before any row is emitted
                   (QdrantChunkRowSource: PatternBudgetExceededException("MaxPartitionRows"), mapped to
                   ResourceExhausted), and a call emits at most one row per chunk (calibration) or one
                   per match (theta, matches do not overlap), so a per-candidate call can reach 10,000
                   rows only for a parent of exactly 10,000 chunks. 10,000 is also the default
                   MaxOutputRows, so the call is valid on a default stack.
    whole-corpus   one call per query with no where, limit WHOLE_CORPUS_LIMIT (18,623, which the stack
                   must allow via Patterns__Limits__MaxOutputRows=18623). The corpus holds 18,622
                   chunks, so no call can emit 18,623 rows; the candidates are kept client-side.
In both shapes the server stops writing at exactly `limit` rows (ObjectSearchGrpcService.MatchPattern
.cs: `if (++written == outputLimit) return;`), so every truncated result has count == limit; a count
at limit therefore aborts as possible truncation. It can only over-abort, never under-abort.

Failure is loud (spec "Failure"): any gRPC error (timeout, InvalidArgument, ...), a count at limit,
a NULL similarity, a row for an unexpected parent, or a /build composite that differs from the
baseline sidecar's (GET before and after every pass) exits non-zero before any output of that pass is
written. A failed query is never read as "no runs".

Identity. The service credentials come from the same env vars as Iverson.Agents and the LoadTest
(IVERSON_GRPC_URL, IVERSON_CLIENT_ID, IVERSON_CLIENT_SECRET, IVERSON_TOKEN_ENDPOINT,
IVERSON_CLIENT_SCOPE -- /home/ben/iverson-benchmark-data/bench-env.sh exports all five). The
acting-user token is minted here, not read once: Authentik issues it with access_token_validity
hours=2 (compose-only/service-clients.yaml, iverson-loadtest-human provider) and a per-candidate pass
may run for up to 8 hours. So this script runs deploy/scripts/mint_acting_user_token.py --target
compose --username iverson-loadtest-bypass-user --password $IVERSON_ACTING_USER_BYPASS_PASSWORD as
a subprocess, and re-mints once the token is TOKEN_REFRESH_SECONDS (90 minutes) old, rebinding the
coordinator with EntityCoordinator.with_acting_user.

SDK import: the SDK is not pip-installed on this box; like Iverson.Clients/Python/conformance/
driver.py:25, the SDK root is put on sys.path. grpcio and protobuf come from the user site-packages.
No PYTHONPATH is needed. Run with:

    . /home/ben/iverson-benchmark-data/bench-env.sh
    export IVERSON_ACTING_USER_BYPASS_PASSWORD=...
    python3 Iverson.Server/Iverson.LoadTest/scripts/pattern_leg.py probe-a --out OUT --baseline OUT/runs/<label>.chunks.trec
    python3 Iverson.Server/Iverson.LoadTest/scripts/pattern_leg.py probe-b --out OUT --baseline ...
    python3 Iverson.Server/Iverson.LoadTest/scripts/pattern_leg.py calibrate --shape <shape> --out OUT --baseline ...
    python3 Iverson.Server/Iverson.LoadTest/scripts/pattern_leg.py run --shape <shape> --theta p50 --out OUT --baseline ...
"""
import argparse
import json
import math
import os
import statistics
import subprocess
import sys
import time
import urllib.request
from urllib.parse import urlsplit

SCRIPTS_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(SCRIPTS_DIR)))
SDK_ROOT = os.path.join(REPO_ROOT, "Iverson.Clients", "Python")
MINT_SCRIPT = os.path.join(REPO_ROOT, "Iverson.Server", "deploy", "scripts", "mint_acting_user_token.py")

sys.path.insert(0, SCRIPTS_DIR)
sys.path.insert(0, SDK_ROOT)
import grpc  # noqa: E402
import report  # noqa: E402  (sidecar_path_for / load_build_composite -- stdlib-only at import)
from iverson_client import iverson_entity, iverson_key, match_pattern  # noqa: E402
from iverson_client.generated import object_search_pb2 as pb  # noqa: E402

TYPE_NAME = "BenchmarkDocument"      # Iverson.LoadTest/Entities/BenchmarkDocument.cs
CHUNK_PROPERTY = "Body"              # its [IversonChunk] property; matched case-insensitively
KEY_COLUMN = "Id"                    # its [IversonKey]; BuildChunksFilter accepts EQUALS on the key
PATTERN = "A+"                       # the theta call (and probe A's running-sum call)
CALIBRATION_PATTERN = "A"            # one single-row match per chunk
PER_CANDIDATE_LIMIT = 10_000
WHOLE_CORPUS_LIMIT = 18_623
SHAPES = ("per-candidate", "whole-corpus")
THETAS = ("p50", "p75", "p90")
SCORES = ("run_len", "run_sum")
THETA_SAMPLE_QUERIES = 100
PROBE_QUERIES = 10
PROBE_PLACEHOLDER_THETA = 0.6
PROJECTION_QUERIES = 672
PROJECTION_CANDIDATES = 50
PER_CANDIDATE_BUDGET_HOURS = 8.0
WHOLE_CORPUS_TIMEOUT_SECONDS = 30.0
TOKEN_REFRESH_SECONDS = 90 * 60
BYPASS_USERNAME = "iverson-loadtest-bypass-user"
BYPASS_PASSWORD_ENV = "IVERSON_ACTING_USER_BYPASS_PASSWORD"
CORPORA = "/home/ben/repositories/iverson-benchmark-corpora"
DEFAULT_KEYMAP = f"{CORPORA}/freshstack-2048-2026-09-07/keymap.json"
DEFAULT_QUERIES = f"{CORPORA}/freshstack-2048-2026-09-07/beir/queries.jsonl"


@iverson_entity
class BenchmarkDocument:
    """Only its name matters: EntityCoordinator needs an @iverson_entity class, and the SDK derives
    the type name from cls.__name__ (annotations.py `"type_name": cls.__name__`). The request's own
    type_name comes from match_pattern(TYPE_NAME)."""
    id: str = iverson_key()


assert BenchmarkDocument._iverson_meta["type_name"] == TYPE_NAME


# ── Expressions and requests (pure) ─────────────────────────────────────────────────────

def escape_literal(text):
    """A single quote inside an expression string literal is written twice (ExpressionLexer.ReadString)."""
    return text.replace("'", "''")


def similarity(column, query):
    return f"SIMILARITY({column}, '{escape_literal(query)}')"


def format_theta(theta):
    """repr() is the shortest round-trip spelling; the lexer parses it with double.Parse (exponent
    included), so the server compares against exactly this double."""
    theta = float(theta)
    if not math.isfinite(theta):
        sys.exit(f"theta {theta!r} is not finite")
    return repr(theta)


def shape_limit(shape):
    return {"per-candidate": PER_CANDIDATE_LIMIT, "whole-corpus": WHOLE_CORPUS_LIMIT}[shape]


def _chunks_builder(guid):
    builder = match_pattern(TYPE_NAME).chunks(CHUNK_PROPERTY)
    if guid is not None:
        builder = builder.where(KEY_COLUMN, pb.EQUALS, guid)
    return builder


def calibration_request(query, guid, limit):
    """A, A AS TRUE, ONE ROW PER MATCH, s = SIMILARITY(text, q), ci = chunk_index. Every chunk is its
    own one-row match (SKIP PAST LAST ROW), so the call emits one row per chunk carrying only
    parent_key, s and ci -- no text, which ALL ROWS would stream for every chunk (CompiledPattern.cs:
    130 fixes the ALL ROWS columns at parent_key, chunk_index, text). The engine probe
    (.superpowers/rrf-proto/engine-probe) showed bare chunk_index yields each chunk's own index and
    s its own similarity. `guid` None is the whole-corpus shape."""
    return (_chunks_builder(guid).pattern(CALIBRATION_PATTERN).define("A", "TRUE")
            .measure("s", similarity("text", query))
            .measure("ci", "chunk_index")
            .rows_per_match(pb.ONE_ROW).limit(limit).build())


def probe_a_request(query, guid, limit):
    """Probe A's row-carrying call: A+, A AS TRUE, ALL ROWS, s and a RUNNING SUM(SIMILARITY(A.text, q))."""
    return (_chunks_builder(guid).pattern(PATTERN).define("A", "TRUE")
            .measure("s", similarity("text", query))
            .measure("run_sum", f"SUM({similarity('A.text', query)})")
            .rows_per_match(pb.ALL_ROWS_SHOW_EMPTY).limit(limit).build())


def theta_request(query, theta, guid, limit):
    """A+, A AS SIMILARITY(text, q) > theta, ONE ROW PER MATCH, run_len and run_sum."""
    return (_chunks_builder(guid).pattern(PATTERN)
            .define("A", f"{similarity('text', query)} > {format_theta(theta)}")
            .measure("run_len", "COUNT(*)")
            .measure("run_sum", f"SUM({similarity('A.text', query)})")
            .rows_per_match(pb.ONE_ROW).limit(limit).build())


# ── Calls ───────────────────────────────────────────────────────────────────────────────

class TokenSession:
    """Hands out a coordinator bound to a fresh-enough acting-user token, minting a new one once the
    current one is `refresh_seconds` old. `mint` and `clock` are injected so tests need no Authentik."""

    def __init__(self, base_coordinator, mint, clock=time.monotonic, refresh_seconds=TOKEN_REFRESH_SECONDS):
        self._base = base_coordinator
        self._mint = mint
        self._clock = clock
        self._refresh = refresh_seconds
        self._bound = None
        self._minted_at = None
        self.mints = 0

    def coordinator(self):
        now = self._clock()
        if self._bound is None or now - self._minted_at >= self._refresh:
            self._bound = self._base.with_acting_user(self._mint())
            self._minted_at = now
            self.mints += 1
        return self._bound


def execute(session, request, what):
    """Run one MatchPattern call to completion. A gRPC error or a count at `limit` aborts."""
    try:
        rows = session.coordinator().match_pattern(request)
    except grpc.RpcError as e:
        code = e.code() if hasattr(e, "code") else None
        details = e.details() if hasattr(e, "details") else str(e)
        sys.exit(f"[pattern_leg] {what}: MatchPattern failed with {code}: {details} -- aborting; a failed "
                 "call is never read as 'no runs'")
    if len(rows) == request.limit:
        sys.exit(f"[pattern_leg] {what}: {len(rows)} rows == limit {request.limit}: the result may be "
                 "truncated -- aborting")
    return rows


def field(row, name, what):
    if name not in row.data:
        sys.exit(f"[pattern_leg] {what}: a result row has no {name!r} column (columns: {sorted(row.data)})")
    return row.data[name]


def whole_number(value, name, what, minimum):
    if not isinstance(value, float) or not value.is_integer() or value < minimum:
        sys.exit(f"[pattern_leg] {what}: {name} {value!r} is not a whole number >= {minimum}")
    return int(value)


def finite_number(value, name, what):
    if value is None:
        sys.exit(f"[pattern_leg] {what}: {name} is NULL -- the chunk's similarity could not be computed")
    if not isinstance(value, float) or not math.isfinite(value):
        sys.exit(f"[pattern_leg] {what}: {name} {value!r} is not a finite number")
    return value


def rows_by_candidate(rows, guid_to_doc, candidates, expected_guid, what):
    """{doc_id: [row, ...]} for the query's candidates. In the per-candidate shape every row must be
    `expected_guid`'s; in the whole-corpus shape (expected_guid None) every parent must be a known
    corpus document, and non-candidates are dropped."""
    grouped = {}
    for row in rows:
        parent = field(row, "parent_key", what)
        if expected_guid is not None and parent != expected_guid:
            sys.exit(f"[pattern_leg] {what}: a row for parent {parent!r} came back from a call filtered to "
                     f"{expected_guid!r}")
        doc_id = guid_to_doc.get(parent)
        if doc_id is None:
            sys.exit(f"[pattern_leg] {what}: parent {parent!r} is not in the key map -- wrong collection?")
        if doc_id in candidates:
            grouped.setdefault(doc_id, []).append(row)
    return grouped


def calibration_chunks(rows, what, index_column="ci"):
    """[(chunk_index, s), ...] for one candidate, in chunk order, from the calibration call's `ci` and
    `s` measures (probe A's ALL ROWS call passes index_column="chunk_index"); NULL s and duplicate
    chunks abort."""
    chunks = {}
    for row in rows:
        index = whole_number(field(row, index_column, what), index_column, what, 0)
        if index in chunks:
            sys.exit(f"[pattern_leg] {what}: chunk {index} appears twice")
        chunks[index] = finite_number(field(row, "s", what), "s", what)
    return sorted(chunks.items())


def theta_runs(rows, what):
    """[(run_len, run_sum), ...] for one candidate; a NULL run_sum aborts."""
    return [(whole_number(field(row, "run_len", what), "run_len", what, 1),
             finite_number(field(row, "run_sum", what), "run_sum", what)) for row in rows]


def query_calls(shape, candidates, doc_to_guid):
    """[guid, ...] -- one call per candidate -- or [None] for the one whole-corpus call."""
    if shape == "whole-corpus":
        return [None]
    return [doc_to_guid[doc] for doc in candidates]


def run_pass(session, shape, build_request, per_candidate, baseline, baseline_order, maps, progress="pass"):
    """Every query's calls in `shape`; returns {query_id: {doc_id: parsed}} where parsed is
    `per_candidate(rows, what)` over that candidate's rows (candidates with no rows are absent)."""
    doc_to_guid, guid_to_doc = maps
    result = {}
    for n, query_id in enumerate(baseline_order, start=1):
        candidates = set(baseline[query_id])
        grouped = {}
        for guid in query_calls(shape, baseline[query_id], doc_to_guid):
            what = f"{progress} query {query_id}" + (f" parent {guid}" if guid else "")
            rows = execute(session, build_request(query_id, guid), what)
            for doc_id, doc_rows in rows_by_candidate(rows, guid_to_doc, candidates, guid, what).items():
                grouped.setdefault(doc_id, []).extend(doc_rows)
        result[query_id] = {doc: per_candidate(doc_rows, f"{progress} query {query_id} doc {doc}")
                            for doc, doc_rows in grouped.items()}
        if n % 50 == 0 or n == len(baseline_order):
            print(f"[pattern_leg] {progress}: {n}/{len(baseline_order)} queries", flush=True)
    return result


# ── Scoring (pure) ──────────────────────────────────────────────────────────────────────

def best_run(runs, primary):
    """(primary value, other value) of the candidate's best run under `primary`: the highest primary,
    ties broken by the higher other score."""
    index = SCORES.index(primary)          # runs are (run_len, run_sum) tuples, in SCORES order
    return max((run[index], run[1 - index]) for run in runs)


def pattern_order(runs_by_doc, primary, baseline_docs):
    """Matched candidates in pattern-rank order: best primary descending, then the best run's other
    score descending, then baseline rank ascending (unique, so the order is strict)."""
    baseline_rank = {doc: i for i, doc in enumerate(baseline_docs, start=1)}
    keyed = []
    for doc, runs in runs_by_doc.items():
        value, other = best_run(runs, primary)
        keyed.append((-value, -other, baseline_rank[doc], doc))
    return [doc for *_, doc in sorted(keyed)]


def ordered_query_ids(query_ids):
    """Numeric order when every id is all digits (FreshStack's are), string order otherwise."""
    ids = list(query_ids)
    if all(q.isdigit() for q in ids):
        return sorted(ids, key=int)
    return sorted(ids)


def sample_query_ids(query_ids, n=THETA_SAMPLE_QUERIES):
    ordered = ordered_query_ids(query_ids)
    if len(ordered) < n:
        sys.exit(f"theta needs the {n} lowest query ids, but only {len(ordered)} queries exist")
    return ordered[:n]


def percentiles(values):
    """p50/p75/p90 with linear interpolation between order statistics (statistics.quantiles'
    "inclusive" method, the same as numpy.percentile's default)."""
    if len(values) < 2:
        sys.exit(f"theta needs at least 2 similarity values, got {len(values)}")
    cuts = statistics.quantiles(values, n=100, method="inclusive")
    return {"p50": cuts[49], "p75": cuts[74], "p90": cuts[89]}


def theta_from_calibration(calibration, query_ids):
    sample = sample_query_ids(query_ids)
    values = [s for q in sample for chunks in calibration[q].values() for _, s in chunks]
    return {**percentiles(values), "sample_query_ids": sample, "rows_in_sample": len(values)}


def projection_hours(mean_seconds):
    return mean_seconds * PROJECTION_QUERIES * PROJECTION_CANDIDATES / 3600.0


def probe_decision(per_candidate_means, whole_corpus_calls):
    """The spec's rule, fixed by user decision. `per_candidate_means` is {kind: mean seconds per call};
    `whole_corpus_calls` is [(seconds, timed_out), ...]. Every per-candidate pass, calibration as
    well as theta, is 672 x 50 calls, so both must fit the budget (the conservative reading)."""
    worst_hours = max(projection_hours(m) for m in per_candidate_means.values())
    if worst_hours <= PER_CANDIDATE_BUDGET_HOURS:
        return "per-candidate"
    if all(not timed_out and seconds < WHOLE_CORPUS_TIMEOUT_SECONDS for seconds, timed_out in whole_corpus_calls):
        return "whole-corpus"
    return "STOP"


# ── Build identity ──────────────────────────────────────────────────────────────────────

def fetch_build(url):
    """GET /build (anonymous, listener 8081) -> composite; any failure aborts, like benchmark-query."""
    try:
        with urllib.request.urlopen(url, timeout=30) as response:
            body = json.load(response)
    except Exception as e:  # noqa: BLE001 -- every failure mode means "cannot attribute this pass"
        sys.exit(f"[pattern_leg] could not GET {url}: {e}")
    composite = body.get("composite")
    if not composite:
        sys.exit(f"[pattern_leg] {url} returned no composite")
    return composite


def check_build(fetch, url, expected, label):
    composite = fetch(url)
    if composite != expected:
        sys.exit(f"[pattern_leg] BUILD MISMATCH at {label}: {url} reports {composite!r}, the baseline sidecar "
                 f"{expected!r} -- the pattern leg must run on the baseline's build")
    return composite


def run_checked_pass(fetch, url, expected, pass_name, body):
    """/build before, the pass, /build after; returns (result, check record)."""
    before = check_build(fetch, url, expected, f"{pass_name} (before)")
    result = body()
    after = check_build(fetch, url, expected, f"{pass_name} (after)")
    return result, {"pass": pass_name, "before": before, "after": after}


# ── Inputs ──────────────────────────────────────────────────────────────────────────────

def load_baseline(path):
    """{query_id: [doc_id, ...]} in rank order and the file's query order (rank must equal position)."""
    baseline, order = {}, []
    with open(path, encoding="utf-8") as f:
        for lineno, line in enumerate(f, start=1):
            if not line.strip():
                continue
            fields = line.split()
            if len(fields) != 6:
                sys.exit(f"{path}:{lineno}: expected 6 fields, got {len(fields)}")
            query_id, _iter, doc_id, rank, _score, _tag = fields
            if query_id not in baseline:
                baseline[query_id] = []
                order.append(query_id)
            docs = baseline[query_id]
            if doc_id in docs or rank != str(len(docs) + 1):
                sys.exit(f"{path}:{lineno}: duplicate document or rank {rank} out of order in query {query_id}")
            docs.append(doc_id)
    if not baseline:
        sys.exit(f"{path}: no rows")
    return baseline, order


def load_keymap(path, baseline):
    """(doc_to_guid, guid_to_doc); the map must be one-to-one and cover every candidate."""
    with open(path, encoding="utf-8") as f:
        guid_to_doc = json.load(f)
    doc_to_guid = {doc: guid for guid, doc in guid_to_doc.items()}
    if len(doc_to_guid) != len(guid_to_doc):
        sys.exit(f"{path}: two GUIDs map to one doc id")
    missing = sorted({d for docs in baseline.values() for d in docs} - set(doc_to_guid))
    if missing:
        sys.exit(f"{path}: {len(missing)} baseline candidate(s) have no GUID, e.g. {missing[:3]}")
    return doc_to_guid, guid_to_doc


def load_queries(path, query_ids):
    texts = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            if line.strip():
                row = json.loads(line)
                texts[row["_id"]] = row.get("text") or ""
    missing = [q for q in query_ids if not texts.get(q)]
    if missing:
        sys.exit(f"{path}: {len(missing)} baseline query id(s) have no text, e.g. {missing[:3]}")
    return texts


# ── Writers (pure over their inputs) ────────────────────────────────────────────────────

def write_calibration(path, calibration, query_order, baseline):
    with open(path, "w", encoding="utf-8") as f:
        f.write("query_id\tdoc_id\tchunk_index\ts\n")
        for query_id in query_order:
            for doc_id in baseline[query_id]:
                for index, s in calibration[query_id][doc_id]:
                    f.write(f"{query_id}\t{doc_id}\t{index}\t{s!r}\n")


def write_scores(path, runs, query_order, baseline):
    """One row per matched candidate. `run_len` is the arm-run_len primary score (the candidate's
    longest run) and `run_sum` the arm-run_sum primary (its highest run_sum): the best run under
    each score, which may be two different runs. The last two columns are each best run's other
    score, the tie-break pattern_order applied."""
    with open(path, "w", encoding="utf-8") as f:
        f.write("query_id\tdoc_id\trun_len\trun_sum\trun_sum_of_best_len\trun_len_of_best_sum\n")
        for query_id in query_order:
            for doc_id in baseline[query_id]:
                if doc_id in runs.get(query_id, {}):
                    best_len, sum_of_len = best_run(runs[query_id][doc_id], "run_len")
                    best_sum, len_of_sum = best_run(runs[query_id][doc_id], "run_sum")
                    f.write(f"{query_id}\t{doc_id}\t{best_len}\t{best_sum!r}\t{sum_of_len!r}\t{len_of_sum}\n")


def write_pattern_trec(path, runs, query_order, baseline, primary, tag):
    """Matched candidates only, in pattern-rank order; rank from 1, file order == rank order. The
    score column is (matched + 1 - rank), strictly decreasing, so the file scores in its own order
    even under a scorer that breaks score ties by doc id."""
    with open(path, "w", encoding="utf-8") as f:
        for query_id in query_order:
            ordered = pattern_order(runs.get(query_id, {}), primary, baseline[query_id])
            for rank, doc_id in enumerate(ordered, start=1):
                f.write(f"{query_id} Q0 {doc_id} {rank} {len(ordered) + 1 - rank:.6f} {tag}\n")


def write_json(path, data):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)
        f.write("\n")


# ── Wiring (live) ───────────────────────────────────────────────────────────────────────

def require_env(name):
    value = os.environ.get(name)
    if not value:
        sys.exit(f"[pattern_leg] {name} is not set (source /home/ben/iverson-benchmark-data/bench-env.sh)")
    return value


def connect():
    """(client, base coordinator) from the IVERSON_* env vars, as Iverson.Agents/Python/iverson_agent/
    __main__.py:21-36 reads them. The compose stack's gRPC is plaintext h2c and its token endpoint
    http://, so allow_insecure_credentials=True is the same explicit opt-in that CLI makes."""
    from iverson_client import IversonClient, IversonClientCredentials

    url = urlsplit(os.environ.get("IVERSON_GRPC_URL", "http://localhost:8080"))
    if url.scheme not in ("http", "https"):
        sys.exit(f"IVERSON_GRPC_URL must start with http:// or https://, got {url.geturl()!r}")
    credentials = IversonClientCredentials(
        client_id=require_env("IVERSON_CLIENT_ID"), client_secret=require_env("IVERSON_CLIENT_SECRET"),
        token_endpoint=require_env("IVERSON_TOKEN_ENDPOINT"), scope=os.environ.get("IVERSON_CLIENT_SCOPE"))
    client = IversonClient(url.hostname or "localhost", url.port or (443 if url.scheme == "https" else 8080),
                           use_tls=url.scheme == "https", credentials=credentials,
                           allow_insecure_credentials=True)
    return client, client.coordinator(BenchmarkDocument)


def mint_acting_user_token(username):
    """stdout of mint_acting_user_token.py (everything else it prints goes to stderr)."""
    password = require_env(BYPASS_PASSWORD_ENV)
    result = subprocess.run(
        [sys.executable, MINT_SCRIPT, "--target", "compose", "--username", username, "--password", password],
        capture_output=True, text=True)
    token = result.stdout.strip()
    if result.returncode != 0 or not token:
        sys.exit(f"[pattern_leg] minting the acting-user token failed (exit {result.returncode}):\n"
                 f"{result.stderr[-2000:]}")
    print(f"[pattern_leg] minted an acting-user token for {username}", flush=True)
    return token


class Context:
    """Everything a subcommand needs, loaded once from the CLI arguments."""

    def __init__(self, args):
        self.args = args
        self.pattern_dir = os.path.join(args.out, "pattern")
        os.makedirs(self.pattern_dir, exist_ok=True)
        self.baseline, self.order = load_baseline(args.baseline)
        self.maps = load_keymap(args.keymap, self.baseline)
        self.texts = load_queries(args.queries, self.order)
        self.expected = report.load_build_composite(args.baseline)
        if not self.expected:
            sys.exit(f"{report.sidecar_path_for(args.baseline)}: missing, or has no composite")
        self.client, base = connect()
        self.session = TokenSession(base, lambda: mint_acting_user_token(args.username))

    def path(self, name):
        return os.path.join(self.pattern_dir, name)


def calibration_pass(ctx, shape, order):
    limit = shape_limit(shape)
    return run_pass(ctx.session, shape, lambda q, guid: calibration_request(ctx.texts[q], guid, limit),
                    calibration_chunks, ctx.baseline, order, ctx.maps, progress="calibration")


def theta_pass(ctx, shape, theta, order, label):
    limit = shape_limit(shape)
    return run_pass(ctx.session, shape, lambda q, guid: theta_request(ctx.texts[q], theta, guid, limit),
                    theta_runs, ctx.baseline, order, ctx.maps, progress=label)


def cmd_probe_a(ctx):
    query_id = ctx.args.query_id or ordered_query_ids(ctx.order)[0]
    doc_id = ctx.args.doc_id or ctx.baseline[query_id][0]
    guid = ctx.maps[0][doc_id]
    rows = execute(ctx.session, probe_a_request(ctx.texts[query_id], guid, PER_CANDIDATE_LIMIT),
                   f"probe A query {query_id} doc {doc_id}")
    calibration_rows = execute(ctx.session, calibration_request(ctx.texts[query_id], guid, PER_CANDIDATE_LIMIT),
                               f"probe A calibration query {query_id} doc {doc_id}")
    for name, got in (("ALL ROWS running-sum call", rows), ("text-free calibration call", calibration_rows)):
        print(f"[probe-a] query {query_id}, candidate {doc_id} ({guid}), {name}: {len(got)} row(s)")
        for row in got:
            shown = {k: (v[:60] + "..." if isinstance(v, str) and len(v) > 60 else v) for k, v in row.data.items()}
            print(f"  match {row.match_number} classifier {row.classifier!r} {shown}")
    if not rows:
        sys.exit("[probe-a] FAIL: no rows -- the candidate's chunks are not readable")
    chunks = calibration_chunks(rows, "probe A", index_column="chunk_index")
    calibrated = calibration_chunks(calibration_rows, "probe A calibration")
    if calibrated != chunks:
        sys.exit(f"[probe-a] FAIL: the calibration call's (ci, s) {calibrated} differ from the ALL ROWS call's "
                 f"(chunk_index, s) {chunks}")
    if any("text" in row.data for row in calibration_rows):
        sys.exit("[probe-a] FAIL: the calibration call returned a text column")
    running = 0.0
    for (index, s), row in zip(chunks, sorted(rows, key=lambda r: r.data["chunk_index"])):
        running += s
        got = finite_number(row.data.get("run_sum"), "run_sum", "probe A")
        if not math.isclose(got, running, rel_tol=1e-9, abs_tol=1e-12):
            sys.exit(f"[probe-a] FAIL: chunk {index}: RUNNING SUM(SIMILARITY) {got!r} != cumulative s {running!r}")
    print(f"[probe-a] PASS: {len(chunks)} chunk(s), every s non-NULL, SUM(SIMILARITY(A.text, q)) is the running "
          "sum of s, and the text-free calibration call gives the same (chunk index, s) per chunk")


def cmd_probe_b(ctx):
    queries = ordered_query_ids(ctx.order)[:PROBE_QUERIES]
    theta = ctx.args.theta
    composite = fetch_build(ctx.args.build_url)
    timings = {}
    for shape in SHAPES:
        limit = shape_limit(shape)
        for kind in ("calibration", "theta"):
            calls = []
            for query_id in queries:
                for guid in query_calls(shape, ctx.baseline[query_id], ctx.maps[0]):
                    request = (calibration_request(ctx.texts[query_id], guid, limit) if kind == "calibration"
                               else theta_request(ctx.texts[query_id], theta, guid, limit))
                    what = f"probe B {shape} {kind} query {query_id}"
                    start = time.perf_counter()
                    timed_out = False
                    try:
                        rows = ctx.session.coordinator().match_pattern(request)
                    except grpc.RpcError as e:
                        if shape == "whole-corpus" and e.code() == grpc.StatusCode.DEADLINE_EXCEEDED:
                            timed_out = True
                            rows = []
                        else:
                            sys.exit(f"[probe-b] {what}: {e.code()}: {e.details()}")
                    seconds = time.perf_counter() - start
                    if len(rows) == limit:
                        sys.exit(f"[probe-b] {what}: {len(rows)} rows == limit {limit}: possible truncation")
                    calls.append((seconds, timed_out))
            timings[(shape, kind)] = calls
            mean = statistics.fmean(s for s, _ in calls)
            print(f"[probe-b] {shape:13s} {kind:11s} {len(calls):4d} calls  mean {mean:.3f}s  "
                  f"max {max(s for s, _ in calls):.3f}s  timeouts {sum(t for _, t in calls)}", flush=True)
    if fetch_build(ctx.args.build_url) != composite:
        sys.exit("[probe-b] the /build composite changed during the probe")
    means = {kind: statistics.fmean(s for s, _ in timings[("per-candidate", kind)]) for kind in ("calibration", "theta")}
    whole = timings[("whole-corpus", "calibration")] + timings[("whole-corpus", "theta")]
    decision = probe_decision(means, whole)
    result = {
        "query_ids": queries, "placeholder_theta": theta, "composite": composite,
        "limits": {"per-candidate": PER_CANDIDATE_LIMIT, "whole-corpus": WHOLE_CORPUS_LIMIT},
        "calls": {f"{shape}/{kind}": {"n": len(c), "mean_s": statistics.fmean(s for s, _ in c),
                                       "max_s": max(s for s, _ in c), "timeouts": sum(t for _, t in c)}
                  for (shape, kind), c in timings.items()},
        "per_candidate_projection_hours": {k: projection_hours(m) for k, m in means.items()},
        "decision": decision,
    }
    write_json(ctx.path("probe-b.json"), result)
    print(f"[probe-b] projected per-candidate pass: calibration "
          f"{result['per_candidate_projection_hours']['calibration']:.2f} h, theta "
          f"{result['per_candidate_projection_hours']['theta']:.2f} h; decision: {decision}")
    if decision == "STOP":
        sys.exit("[probe-b] STOP: per-candidate exceeds 8 h per pass and a whole-corpus call hit the 30 s "
                 "timeout -- return to the user")


def cmd_calibrate(ctx):
    calibration, check = run_checked_pass(
        fetch_build, ctx.args.build_url, ctx.expected, "calibration",
        lambda: calibration_pass(ctx, ctx.args.shape, ctx.order))
    for query_id in ctx.order:
        absent = [d for d in ctx.baseline[query_id] if d not in calibration[query_id]]
        if absent:
            sys.exit(f"[pattern_leg] calibration: query {query_id}: {len(absent)} candidate(s) returned no "
                     f"chunk, e.g. {absent[:3]}")
    theta = theta_from_calibration(calibration, ctx.order)
    write_calibration(ctx.path("calibration.tsv"), calibration, ctx.order, ctx.baseline)
    write_json(ctx.path("theta.json"), theta)
    write_json(ctx.path("build.json"), {"baseline_composite": ctx.expected, "checks": [check]})
    print(f"[pattern_leg] theta p50 {theta['p50']!r}  p75 {theta['p75']!r}  p90 {theta['p90']!r} "
          f"over {theta['rows_in_sample']} chunks of the {THETA_SAMPLE_QUERIES} lowest query ids")


def cmd_run(ctx):
    label = ctx.args.theta
    with open(ctx.path("theta.json"), encoding="utf-8") as f:
        theta = json.load(f)[label]
    with open(ctx.path("build.json"), encoding="utf-8") as f:
        build = json.load(f)
    if build.get("baseline_composite") != ctx.expected:
        sys.exit(f"build.json baseline_composite {build.get('baseline_composite')!r} != {ctx.expected!r}")
    runs, check = run_checked_pass(fetch_build, ctx.args.build_url, ctx.expected, label,
                                   lambda: theta_pass(ctx, ctx.args.shape, theta, ctx.order, label))
    matched = sum(len(docs) for docs in runs.values())
    if matched == 0:
        sys.exit(f"[pattern_leg] {label}: no candidate of any query has a run above theta {theta!r} -- this is what "
                 "missing vectors look like (NULL > theta never matches); aborting")
    write_scores(ctx.path(f"scores-{label}.tsv"), runs, ctx.order, ctx.baseline)
    for score in SCORES:
        write_pattern_trec(ctx.path(f"mp-{label}-{score}.trec"), runs, ctx.order, ctx.baseline, score,
                           f"mp-{label}-{score}")
    build["checks"].append(check)
    write_json(ctx.path("build.json"), build)
    print(f"[pattern_leg] {label} (theta {theta!r}): {matched} matched candidate(s) over {len(runs)} queries")


def build_arg_parser():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    for name in ("probe-a", "probe-b", "calibrate", "run"):
        p = sub.add_parser(name)
        p.add_argument("--out", required=True, help="the benchmark's OUT directory; files go to OUT/pattern/")
        p.add_argument("--baseline", required=True, help="<label>.chunks.trec whose candidates are scored")
        p.add_argument("--keymap", default=DEFAULT_KEYMAP, help="Iverson GUID -> BEIR doc id")
        p.add_argument("--queries", default=DEFAULT_QUERIES, help="BEIR queries.jsonl (the text benchmark-query sends)")
        p.add_argument("--build-url", default=os.environ.get("IVERSON_HTTP_URL", "http://localhost:8081") + "/build")
        p.add_argument("--username", default=BYPASS_USERNAME, help="the acting user to mint a token for")
        if name == "probe-a":
            p.add_argument("--query-id", default=None, help="default: the lowest query id")
            p.add_argument("--doc-id", default=None, help="default: that query's rank-1 candidate")
        if name == "probe-b":
            p.add_argument("--theta", type=float, default=PROBE_PLACEHOLDER_THETA, help="placeholder theta")
        if name in ("calibrate", "run"):
            p.add_argument("--shape", required=True, choices=SHAPES, help="the shape probe-b decided")
        if name == "run":
            p.add_argument("--theta", required=True, choices=THETAS)
    return ap


def main(argv=None):
    args = build_arg_parser().parse_args(argv)
    ctx = Context(args)
    try:
        {"probe-a": cmd_probe_a, "probe-b": cmd_probe_b, "calibrate": cmd_calibrate, "run": cmd_run}[args.cmd](ctx)
    finally:
        ctx.client.close()


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: Run the tests, then the whole scripts suite**

Run: `python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_pattern_leg.py -q`
Expected: `44 passed`. No network is used, because the tests inject a fake coordinator, a fake minter and a fake build fetch.

Run: `QDRANT__SERVICE__API_KEY=unused-by-tests PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts -q`
Expected: `454 passed` once Task 2 has landed (383 before either task, 427 with only Task 3). The dummy key is needed because `ingest.py:160` reads it at import.

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.LoadTest/scripts/pattern_leg.py Iverson.Server/Iverson.LoadTest/scripts/test_pattern_leg.py
git commit -m "add the matchpattern chunk-run leg for the rrf benchmark" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 4: Live run, part 1: stack, restore, baseline, Probes A and B

This task changes the shared local compose stack. It rebuilds `iverson-api`, recreates api and worker with two non-default limits, and replaces two Qdrant collections. **The controller asks the user before dispatching it.**

**Files:**
- Create, outside the repository: the run directory, `compose.rrf-bench.yml` and `phase0.md` under `/home/ben/repositories/iverson-benchmark-corpora/matchpattern-rrf-<DATE>/`.
- Nothing is committed.

**Interfaces:**
- Consumes Task 3's `pattern_leg.py`.
- Produces, for Task 5:
  - `$OUT/runs/mp-baseline.chunks.trec` and `$OUT/runs/mp-baseline.meta.json`;
  - `$OUT/pattern/probe-b.json`, including the chosen shape;
  - `$OUT/phase0.md`.

Each step runs in a fresh shell, so nothing a step sets survives to the next one (plan assumption 28). This setup therefore writes every shared variable, function and credential export to one state file. **Every bash block in Tasks 4 and 5 starts by sourcing it.** The file holds commands, never secret values: `K` and the two passwords are re-read from `.env` each time it is sourced.

Run the setup once, from the root of the worktree the tasks execute in:
```bash
STATE=/home/ben/repositories/iverson-benchmark-corpora/matchpattern-rrf-shell.sh
printf 'cd %q\nDATE=%q\n' "$(pwd)" "$(date +%F)" > "$STATE"
cat >> "$STATE" <<'EOF'
OUT=/home/ben/repositories/iverson-benchmark-corpora/matchpattern-rrf-$DATE
C=/home/ben/repositories/iverson-benchmark-corpora/freshstack-2048-2026-09-07
SNAP=/home/ben/repositories/iverson-benchmark-corpora/freshstack-2048-qdrant-snapshots
ENVF=/home/ben/repositories/Iverson/Iverson.Server/.env
LEG=Iverson.Server/Iverson.LoadTest/scripts/pattern_leg.py
PL=/home/ben/repositories/iverson-benchmark-corpora/python-libs
K=$(command grep '^QDRANT__SERVICE__API_KEY=' "$ENVF" | cut -d= -f2-)
counts() { for c in benchmark_documents_tenant_bypass_a8j5vpgduxk1bsvma7542yqnk benchmark_documents_chunks_tenant_bypass_a8j5vpgduxk1bsvma7542yqnk; do
  curl -sf -H "api-key: $K" "http://localhost:6333/collections/$c" | python3 -c 'import sys,json; r=json.load(sys.stdin)["result"]; print(r["points_count"], sorted(r["config"]["params"]["vectors"]))'; done; }
source /home/ben/iverson-benchmark-data/bench-env.sh
export IVERSON_CLIENT_SECRET="$(command grep '^IVERSON_ADMIN_AUTOMATION_CLIENT_SECRET=' "$ENVF" | cut -d= -f2-)"
export IVERSON_ACTING_USER_PASSWORD="$(command grep '^IVERSON_SMOKE_TEST_PASSWORD=' "$ENVF" | cut -d= -f2-)"
export IVERSON_ACTING_USER_BYPASS_PASSWORD="$(command grep '^IVERSON_BYPASS_PASSWORD=' "$ENVF" | cut -d= -f2-)"
EOF
. "$STATE"
mkdir -p "$OUT/runs" "$OUT/pattern" "$OUT/fused"
```

- [ ] **Step 1: Write the compose override and bring the stack up on a fresh image**
```bash
. /home/ben/repositories/iverson-benchmark-corpora/matchpattern-rrf-shell.sh
cat > "$OUT/compose.rrf-bench.yml" <<'EOF'
services:
  iverson-api:
    environment:
      - Patterns__Limits__MaxExpressionLength=25000
      - Patterns__Limits__MaxOutputRows=18623
EOF
cd Iverson.Server
docker compose --env-file "$ENVF" -f docker-compose.yml -f "$OUT/compose.rrf-bench.yml" build iverson-api
docker compose --env-file "$ENVF" -f docker-compose.yml -f "$OUT/compose.rrf-bench.yml" up -d
cd ..
until curl -sf http://localhost:8081/build >/dev/null; do sleep 2; done
docker exec iverson-api env | command grep -E 'Patterns__Limits__Max(ExpressionLength|OutputRows)|VectorRanking__LambdaChunks'
```
Expected: `MaxExpressionLength=25000`, `MaxOutputRows=18623` and `LambdaChunks=0.70`. The last is compose's own default (`docker-compose.yml:466`).

The worktree has no `.env`, which is why `--env-file` points at the main checkout's. On a freshly initialised Postgres, Authentik may reject logins for a few minutes ("Invalid password" / "did not complete after 20 stages") while its blueprint applies. If a later step fails that way, wait until `docker logs iverson-authentik-worker 2>&1 | command grep -c service-clients` shows the blueprint applied, then retry.

- [ ] **Step 2: Restore the FreshStack-2048 snapshots into today's collection names**
```bash
. /home/ben/repositories/iverson-benchmark-corpora/matchpattern-rrf-shell.sh
for pair in \
  "benchmark_documents_tenant_bypass-6802952876034638:benchmark_documents_tenant_bypass_a8j5vpgduxk1bsvma7542yqnk" \
  "benchmark_documents_chunks_tenant_bypass-6802952876034638:benchmark_documents_chunks_tenant_bypass_a8j5vpgduxk1bsvma7542yqnk"; do
  f=$(ls "$SNAP"/"${pair%%:*}"*.snapshot); c=${pair##*:}
  curl -sf -X POST -H "api-key: $K" "http://localhost:6333/collections/$c/snapshots/upload?priority=snapshot" -F "snapshot=@$f" | head -c 200; echo
done
counts
```
Expected:
- the object collection has `6000` points;
- the chunks collection has `18622` points, and its vectors include `body_vector`.

If either count differs, stop: the restore did not land under the name the server reads (plan assumption 18).

- [ ] **Step 3: Re-record the baseline (this also registers the `BenchmarkDocument` schema)**
```bash
. /home/ben/repositories/iverson-benchmark-corpora/matchpattern-rrf-shell.sh
dotnet run -c Release --project Iverson.Server/Iverson.LoadTest -- benchmark-query \
  --corpus-path "$C" --key-map-path "$C/keymap.json" \
  --output-dir "$OUT/runs" --config-label mp-baseline \
  --chunk-budget-multiplier 11 > "$OUT/runs/mp-baseline.log" 2>&1; RC=$?
tail -3 "$OUT/runs/mp-baseline.log"; echo "DOTNET_EXIT=$RC"
wc -l < "$OUT/runs/mp-baseline.chunks.trec"
counts
python3 -c "import json;print(json.load(open('$OUT/runs/mp-baseline.meta.json'))['composite'])"; curl -s http://localhost:8081/build | python3 -c 'import sys,json;print(json.load(sys.stdin)["composite"])'
```
Expected:
- `DOTNET_EXIT=0` and `33600` rows (672 × 50).
- The counts are still `6000` and `18622`, because registering the schema must not replace the restored collections.
- The sidecar composite equals the live `/build` composite.

Do not use `tail`'s exit status as the result; `RC` is the result.

- [ ] **Step 4: Probe A**
```bash
. /home/ben/repositories/iverson-benchmark-corpora/matchpattern-rrf-shell.sh
python3 "$LEG" probe-a --out "$OUT" --baseline "$OUT/runs/mp-baseline.chunks.trec" 2>&1 | tee "$OUT/pattern/probe-a.log"; echo "EXIT=${PIPESTATUS[0]}"
```
Expected: `EXIT=0`, with chunk rows printed for the lowest query's rank-1 candidate. On any error, stop and report it. Probe A checks all of the following, and fails if any one fails:
- The rows have a non-NULL `s`.
- The running `SUM(SIMILARITY(A.text, q))` is the cumulative sum of `s`. This is spec assumption 9, exercised live.
- The text-free calibration call's `(ci, s)` equals the first call's `(chunk_index, s)` for every chunk, and carries no `text` column (deviation 2).

- [ ] **Step 5: Probe B and the shape decision**
```bash
. /home/ben/repositories/iverson-benchmark-corpora/matchpattern-rrf-shell.sh
python3 "$LEG" probe-b --out "$OUT" --baseline "$OUT/runs/mp-baseline.chunks.trec" 2>&1 | tee "$OUT/pattern/probe-b.log"; echo "EXIT=${PIPESTATUS[0]}"
python3 -c "import json;d=json.load(open('$OUT/pattern/probe-b.json'));print(d['decision'])"
```
Expected: `EXIT=0` and a decision of `per-candidate` or `whole-corpus` (deviation 3 gives the rule). A decision of `STOP` exits non-zero. In that case end the task and report `probe-b.json` to the controller, who returns to the user with the timings. Do not raise any limit or timeout to make a shape fit.

- [ ] **Step 6: Record Phase 0**

Source the state file first. Write `$OUT/phase0.md` with:
- the date;
- the `/build` composite, and the baseline sidecar composite (these must be equal);
- the three env values from Step 1;
- the Step 2 and Step 3 point counts;
- the baseline's row count and `DOTNET_EXIT`;
- Probe A's output;
- `probe-b.json` verbatim, with the chosen shape.

No commit: everything here lives outside the repository.

### Task 5: Live run, part 2: calibration, θ passes, fusion, scoring and the gate document

**Files:**
- Create: `docs/plans/2026-09-GATE-matchpattern-rrf.md`.
- Outside the repository: `$OUT/pattern/*`, `$OUT/runs/fused-*`, `$OUT/fused/*` and `$OUT/report.txt`.

**Interfaces:**
- Consumes Task 1's tests, Task 2's `rrf_fuse.py` and Task 3's `pattern_leg.py`.
- Consumes Task 4's baseline, `probe-b.json` and `phase0.md`.

Task 4's state file already carries `DATE`, so `$OUT` is Task 4's directory. Append Task 5's two variables to it once:
```bash
cat >> /home/ben/repositories/iverson-benchmark-corpora/matchpattern-rrf-shell.sh <<'EOF'
B="$OUT/runs/mp-baseline.chunks.trec"
SHAPE=$(python3 -c "import json;print(json.load(open('$OUT/pattern/probe-b.json'))['decision'])")
EOF
. /home/ben/repositories/iverson-benchmark-corpora/matchpattern-rrf-shell.sh
echo "OUT=$OUT SHAPE=$SHAPE"
```

- [ ] **Step 1: Calibration pass**
```bash
. /home/ben/repositories/iverson-benchmark-corpora/matchpattern-rrf-shell.sh
python3 "$LEG" calibrate --out "$OUT" --baseline "$B" --shape "$SHAPE" > "$OUT/pattern/calibrate.log" 2>&1; echo "EXIT=$?"; tail -3 "$OUT/pattern/calibrate.log"
cat "$OUT/pattern/theta.json"
```
Expected: `EXIT=0`, and `theta.json` holding `p50 < p75 < p90`, 100 sample query ids and `rows_in_sample` > 0. On a non-zero exit, stop and report the log. Every abort in `pattern_leg.py` is deliberate (Global Constraints). Never rerun with a relaxed check.

- [ ] **Step 2: The three θ passes**
```bash
. /home/ben/repositories/iverson-benchmark-corpora/matchpattern-rrf-shell.sh
for t in p50 p75 p90; do
  python3 "$LEG" run --out "$OUT" --baseline "$B" --shape "$SHAPE" --theta "$t" > "$OUT/pattern/run-$t.log" 2>&1; echo "$t EXIT=$?"; tail -2 "$OUT/pattern/run-$t.log"
done
ls "$OUT/pattern"
```
Expected: three `EXIT=0` lines. `OUT/pattern` should hold `scores-p50.tsv`, `scores-p75.tsv`, `scores-p90.tsv`, six `mp-<t>-<score>.trec` files and `build.json` with 4 checks (the calibration pass and each θ pass, each recording a before and an after composite). Stop on any non-zero exit.

- [ ] **Step 3: Fuse, INERT and guard**
```bash
. /home/ben/repositories/iverson-benchmark-corpora/matchpattern-rrf-shell.sh
PYTHONPATH=$PL python3 Iverson.Server/Iverson.LoadTest/scripts/rrf_fuse.py --out "$OUT" --baseline "$B" 2>&1 | tee "$OUT/fused/rrf_fuse.log"; echo "EXIT=${PIPESTATUS[0]}"
cat "$OUT/fused/inert.json" "$OUT/fused/guard.json"
```
Expected: `EXIT=0`, six fused runs with their sidecars, and `inert.json` and `guard.json` covering all six arms. Stop on any non-zero exit: Step 4 reads this step's saved log, and a log from a failed run would give Step 4 a wrong `--pair` list.

- [ ] **Step 4: Score**

`PAIRS` is rebuilt from Step 3's saved log. It must name exactly the arms `inert.json` marks as not INERT; if it doesn't (`AGREE_EXIT=1`), stop and report both. If every arm is INERT, both lists are empty. The gate is then VOID, and `report.py` runs with no `--pair`, giving its structural output only.
```bash
. /home/ben/repositories/iverson-benchmark-corpora/matchpattern-rrf-shell.sh
PAIRS=$(sed -n 's/^  --pair /--pair /p' "$OUT/fused/rrf_fuse.log" | tr '\n' ' ')
python3 - "$OUT/fused/inert.json" $PAIRS <<'PY'
import json, sys
inert = json.load(open(sys.argv[1]))
live = sorted(k for k, v in inert.items() if not v["inert"])
paired = sorted(a.split("=", 1)[0].rsplit("/", 1)[-1].removesuffix(".chunks.trec") for a in sys.argv[2:] if a != "--pair")
print(f"non-INERT arms {live}; --pair arms {paired}")
sys.exit(0 if live == paired else 1)
PY
echo "AGREE_EXIT=$?"
PYTHONPATH=$PL python3 Iverson.Server/Iverson.LoadTest/scripts/report.py --run "$OUT/runs" --qrels "$C/qrels.trec" --nugget-qrels "$C/qrels.nugget.trec" $PAIRS > "$OUT/report.txt" 2>&1; echo "EXIT=$?"
command grep -n "BUILD\|\[pool\]\|nDCG@10\|R@50\|alpha_nDCG@10\|Holm" "$OUT/report.txt" | head -60
md5sum "$OUT"/runs/*.trec "$OUT"/pattern/*.trec
```
Expected: `EXIT=0` and no `BUILD MISMATCH` or `BUILD UNKNOWN` line. R@50 must equal the baseline's for every fused run, because fusion only re-ranks. Any change in R@50 is an integrity failure: stop and report it.

- [ ] **Step 5: Decide the gate**

Apply the spec's gate exactly. Take DEGENERATE from `guard.json` and INERT from `inert.json`, and use the Holm-adjusted p for nDCG@10 from `report.txt`.
- **GO:** at least one arm that is neither DEGENERATE nor INERT improves nDCG@10 with Holm-adjusted p < 0.05.
- **NO-GO:** every such arm is null or negative.
- **VOID:** every arm is DEGENERATE or INERT.

α-nDCG@10 and AP are context only.

- [ ] **Step 6: Write the gate document**

Source the state file first. Create `docs/plans/2026-09-GATE-matchpattern-rrf.md` with these sections, filled from the files named:
1. **Verdicts.**
   - **Reading 1 (partial):** cite `RrfExpressivenessTests` and its four facts.
   - **Reading 2:** give the GO, NO-GO or VOID from Step 5, and say it is feasible client-side only.
   - **Reading 3 (not possible today):** cite `MatchRowsQueryBuilderTests.Every_slot_rejects_an_unknown_hidden_tenant_or_bytes_column` and `MatchPatternGrpcServiceTests.Store_exceptions_map_as_the_spec_section_6_table_says` (deviation 1).
2. **Phase 0,** from `phase0.md`:
   - the composite;
   - the three env values, with the non-default limits stated beside the verdict;
   - the restore counts;
   - Probe A;
   - Probe B's timings, projections and chosen shape.
3. **Query length.** Recompute the spec's "433 of 672" figure from the exact expression strings `pattern_leg.py` built. Use its request builders over `beir/queries.jsonl`, counting queries with any expression over 1,000 characters.
4. **θ values,** from `theta.json`.
5. **Guard:** ρ against chunk count and against max similarity, and DEGENERATE, per arm, from `guard.json`.
6. **INERT:** the reordered fraction per arm, from `inert.json`.
7. **Scored table,** from `report.txt`. For every arm give nDCG@10, R@50, AP and α-nDCG@10 against the baseline, with the paired Δ and the Holm-adjusted p.
8. **Integrity:**
   - R@50 equal to the baseline's;
   - every before and after composite in `build.json`'s 4 checks equal to the baseline composite;
   - `report.py` build lines.
9. **md5s:** every run file, from Step 4's `md5sum`.
10. **Deviations from the spec:** deviations 1 to 4 of this plan.
11. **Known limits:**
    - the non-default `MaxExpressionLength` and `MaxOutputRows`;
    - a NO-GO speaks only for these θ values and k = 60;
    - the guard's accepted consequence (spec, CDR round 1 §3.1).

- [ ] **Step 7: Commit the gate document**
```bash
. /home/ben/repositories/iverson-benchmark-corpora/matchpattern-rrf-shell.sh
git add -f docs/plans/2026-09-GATE-matchpattern-rrf.md
git commit -m "record the matchpattern rrf gate verdict" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>" -- docs/plans/2026-09-GATE-matchpattern-rrf.md
```

## Tasks NOT in this plan

- **Any production change**, including server-side fusion, an `IN` filter on the `CHUNKS` source, and a query-embedding cache.
- **Tuning k, or weighted RRF.** k = 60 is fixed.
- **A quality benchmark for readings 1 and 3.** Neither is feasible.
- **SciFact and other corpora.**

## Known issues inherited from spec

- **Non-default limits.** The benchmark runs with a non-default `MaxExpressionLength` (25,000), and `MaxOutputRows` (18,623; set on the stack, exercised only under whole-corpus). A caller on default limits could not issue 64% of these queries (433 of 672 have at least one expression over 1,000 characters once wrapped and quote-escaped). The gate document recomputes this figure from the exact strings `pattern_leg.py` sends and states it next to the verdict.
- **θ is fixed, not tuned.** The ladder is the similarity percentiles above. A NO-GO speaks for these θ values and k = 60 only.
