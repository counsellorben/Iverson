# Critical Implementation Review: 2026-09-04-multivector-experiment-implementation-plan (Round 1)

**Plan:** `/home/ben/repositories/Iverson/docs/plans/2026-09-04-multivector-experiment-implementation-plan.md`
**Verified plan-level assumptions section:** present

⚠️ 2 commits since plan-write time (SHA `5563454`); cited file:line references re-checked under §1. (Both commits are documents — the spec's CDR and this plan — no code changed.)

Evidence base: the plan runs on local main after the `embedding-migration` branch merges, so every migration-changed file (`ingest.py`, `report.py`, `docker-compose.yml`, the migration plan) was read on that branch at the worktree `/home/ben/repositories/Iverson/.worktrees/embedding-migration` (`f2706a4`); the harness files the plan cites (`TrecRunWriter.cs`, `DocumentRanking.cs`, `BenchmarkQueryScenario.cs`, `ChunkBudgetGuard.cs`, `IntelligenceStoreConsumer.cs`) are byte-identical on main and the branch (`diff -q`). The live stack was read but never written (collection info, exact counts, payload-only scrolls, one named search, TEI `/info`, `docker exec du`, `docker compose --dry-run`). The plan's Python was assembled exactly as its steps say — Task 2's file, Task 3's three insertions at the named places — into a scratch directory beside a symlink to the branch's `ingest.py`/`ingest-contract.json`, and run under pytest, under mutation, and under stubbed Qdrant/TEI (`build`, `query`, `stats`). Task 1's compose edit was applied to a scratch copy of the branch's compose file and rendered with `docker compose config`. Spec §10/§11 and the plan's "Inherited from spec" list are ground truth.

## 0. Coverage enumeration

### Tasks × surfaces

| Row | Surface | Disposition |
|---|---|---|
| T1-edit | Task 1 step 1: compose line 175 replacement | ok — the branch's line 175 is byte-exactly the plan's "before" text; the scratch copy with the plan's "after" line renders (`--profile tei config`) as a five-item command ending `--max-batch-tokens` / `"16384"`, and `"4096"` with `TEI_MAX_BATCH_TOKENS=4096`; `tei_models` volume and `profiles: [tei]` untouched |
| T1-verify | Task 1 step 2: the three verification commands and their expected outputs | → §2.1 (the first two greps cannot show what the step says they show); third command (`config --services` → 0) ok |
| T1-commit | Task 1 step 3 | ok — `git add` of a tracked file; subject matches the repo's lowercase-imperative `compose:`-prefixed convention (P24) |
| T2-tests | Task 2 step 1 test file (10 tests) | ok — runs green against the Task 2 file alone (`10 passed`); `KEY`→`OID` reproduces live (`ingest.key_to_ulong("29beccc1-…") == 817174487868073`); every asserted dict/list matches what the functions return |
| T2-red | Task 2 step 2 expected `ModuleNotFoundError` | ok — the test module's `import multivector` fails exactly so before step 3 |
| T2-code | Task 2 step 3 `multivector.py` (pure functions, helpers, `build`, `stats`, CLI) | ok — compiles; imports under a socket guard with no network; every `ingest` symbol it uses exists on the branch (`qdrant_request` `:276`, `key_to_ulong` `:226`, `DEFAULT_*_COLLECTION` `:177-182`); `collection_info` maps a live 404 to `None`; `scroll` pages by `next_page_offset` (live: 3,633 objects in 8 pages, 14,729 chunks in 30 pages, every id distinct); `disk_size` parses the live `du` output `68M\t/qdrant/…` |
| T2-build-dyn | `cmd_build` runtime path (stubbed Qdrant) | ok — create → scroll → group → resolve → upsert with `wait=false,false,true` → both reconciliations; refuses an existing target without `--drop`; `--drop` deletes and rebuilds; rows ordered by `int(chunk_index)`; payload `{key, docId, chunk_count}`. (A stub whose keys differed only in the first UUID segment collapsed to one point id and the `points_count != len(points)` check caught it — the reconciliation works as designed) |
| T2-build-mem | "19,967 × 768 floats ≈ 0.5 GB" comment | ok — ~15.3 M Python floats ≈ 0.5 GB; a 500-point page with vectors is ~8 MB JSON; a 100-point upsert batch ≈ 6 MB, under Qdrant's 32 MB request cap |
| T2-green | Task 2 step 4 "10 passed" | ok — reproduced |
| T2-smoke | Task 2 step 5 smoke commands | ok — scratch collection name is throwaway; `curl DELETE` cleanup; `rm -rf /tmp/mv-smoke`; requires "no ingest writing" as the plan says (the live collections were mid-ingest during this review and the plan's guard text is correct: a half-written corpus fails `resolve_parents`) |
| T2-commit | Task 2 step 6 | ok |
| T3-tests | Task 3 step 1 (two `rank_chunk_hits` tests) | ok — assertions match; `SystemExit` on the unknown parent |
| T3-red | Task 3 step 2 expected `AttributeError: rank_chunk_hits` | ok — reproduced against the Task 2 file |
| T3-code | Task 3 step 3: `rank_chunk_hits`, `cmd_query`, subparser, insertion points | ok — inserted "after `collapse_by_doc`", "before the CLI block", "between the build and stats parsers" exactly as written: file compiles, `--help` lists `{build,query,stats}`, `query --help` shows `--run-dir/--model/--embed-url`; search body `{vector:{name,vector}, limit 250, with_payload:[parent_id]}` matches the live-verified shape (a read-only search this round returned a 250-item list, score-descending, `payload.parent_id` present); query body `{query:[vec], limit 50, with_payload:[docId]}` is §10 row 5 |
| T3-query-dyn | `cmd_query` runtime paths (stubbed) | ok — happy path: 3 queries → 150 rows per `.trec`, sidecar has `n/p50/p95/mean` per mode plus model/url/collections/budgets; a 400 on query 3 → `SystemExit` with both `.trec` files holding queries 1–2 (100 rows) and the sidecar written (try/finally); under-fill → rows written, then non-zero exit naming the qids |
| T3-embed | `ingest.embed(text, model, "", url)` for queries | ok — signature `:530`; retries `EMBED_RETRIES=2` with `sys.exit` on exhaustion → propagates through the `finally`; posts `{model, input}` to `/v1/embeddings` (TEI ignores `model`) |
| T3-green | Task 3 step 4 "12 passed" | ok — reproduced |
| T3-smoke | Task 3 step 5 smoke (nomic via Ollama, 3-query slice) | ok — `head -3 queries.jsonl` → 3 rows with `_id`/`text`; Ollama serves `/v1/embeddings` (ingest's own default); the unprefixed nomic query only degrades relevance, and the smoke checks mechanics; wrong-dimension path optional as stated |
| T3-commit | Task 3 step 6 | ok |
| T4-pre | Task 4 step 1 preconditions block | ok — `grep -c TEI_MAX_BATCH_TOKENS` proves Task 1 merged; the `--dry-run … \| grep -o "Container iverson-[a-z-]* *Recreate"` pipeline was run this round and prints the Recreate set (api/authentik/postgres today) as intended; health loop, count/size greps, `/build` composite, snapshot-set count all well-formed |
| T4-tei | Task 4 step 2: run dir, TEI recreate, `/info` check | ok — `TEI_MAX_BATCH_TOKENS=4096 EMBED_MODEL_ID=… --profile tei up -d --no-deps --force-recreate tei-embed` is the migration's command plus the templated var; `/info` exposes `max_batch_tokens` (live: `"max_batch_tokens":16384` on the running bge-base container); expected values are stated as a set (the JSON emits `max_input_length` before `max_batch_tokens` before `auto_truncate` — a comment-order nit, not an error) |
| T4-ingest | Task 4 step 3 ingest command and completion checks | ok — flags exist (`:719-745`); `--drop` removes only the two default collections and the sidecar/progress files (`:789-795`); sidecar path is `<key-map-path>.stats.json` (`:768`); the log's last lines are the `this run:` / `cumulative` prints (`:872-881`); `documents/chunks/model/embed_url/chunk_max_chars/chunk_step/elapsed_seconds` are what `update_stats_sidecar` writes (`:675-687`) |
| T4-snap | Task 4 step 4 snapshot loop + RESTORE.md | ok — verbatim migration loop; two `%s` for two args; no stray `%`/`'` in the format |
| T4-api | Task 4 step 5 schema delete, API recreate, identity check, `benchmark-query` | ok — `EMBED_MODEL_ID` feeds `Embeddings__Models__0__Name` (compose `:405`), `BENCH_EMBED_MODEL` feeds `Embeddings__ModelId` (`:417`); `--no-deps iverson-api` recreates only the API (dry-run this round: only `iverson-api` appears; `tei-embed` is profile-gated and untouched even though its config hash changes); `benchmark-query` flags and outputs per `BenchmarkQueryScenario.cs:220, 284-285`; `.chunks` collapses to `DocumentBudget` (`:389, :422`) → 15,000 rows |
| T4-shell | Task 4 shell state (`K`, `SCI`, `D`, `MV`) | ok — all set within the task before use |
| T5-run | Task 5 step 1: build → query → stats back to back | → §2.2 (index readiness) and → §2.3 (`$MV` undefined in this task) |
| T5-snap | Task 5 step 2 multivector snapshot | ok — same loop; the file name `benchmark_documents_multivector_tenant_bypass-6802952876034638-…` fits the RESTORE loop's `%%-6802952876034638*` derivation |
| T5-score-a | Task 5 step 3 (a) `--run $MV/runs --stats-path …` | ok — reproduced on a scratch `runs/` holding an API pair + meta, two raw runs, `raw-latency.json`, `storage.json`, a `.log`: only `*.trec` discovered, raw runs score `build: unknown`, the stats block prints the sidecar |
| T5-score-b | (b) two `--run` files + `--baseline gte-chunks-raw` | ok — reproduced: `Holm (2 tests)`, `95% CI [lo, hi]` per measure computed run − baseline (`report.py:417`), the `FEW QUERIES CHANGED` banner appears when deltas are mostly zero; a `BUILD UNKNOWN` banner also prints (raw runs have no sidecar) — informational |
| T5-score-c | (c) API run vs `rerank-a0` | ok — `rerank-a0.meta.json` exists so the banner is `BUILD MISMATCH` (composite differs), as the plan says; family of 1 |
| T5-restore | Task 5 step 4 restore loop, API defaults, schema re-register | ok — verbatim migration Task 7 step 5 plus the multivector `DELETE`; `scifact-512-qdrant-snapshots/` holds both nomic snapshots and the loop's peer-id literal matches their names; `K` set in-step; `cd Iverson.LoadTest` is relative to the same block's `cd …/Iverson.Server` |
| T5-gate | Task 5 step 5 gate-document prose | ok — every number it asks for has a producer: CI bounds from (b), p95s from `raw-latency.json`, points/indexed/segments/disk from `storage.json`, ingest `elapsed_seconds`/`embed_calls` from the sidecar, composite from `.meta.json`; `2026-09-GATE-reranker-phase1.md` exists as a shape reference (`2026-09-GATE-embedding-migration.md` does not yet — it is the migration's Task 8 output and will after the merge) |
| T5-commit | Task 5 step 6 `git add -f` | ok — `docs/plans/` is ignored by `.gitignore:49`; 46 files under it are tracked (P23 said 45; the plan itself was committed since) |
| GC | Global Constraints (no `stack.py`, single-service `--no-deps`, fixed window/counts/labels, `--baseline` not `--pair`) | ok — each is honoured by the task commands; `--pair` never appears |

### Cross-task interface contracts (⧫ = persistence boundary)

| Row | Contract | Disposition |
|---|---|---|
| C1 | Task 3 consumes Task 2's `scroll`, `require_collection`, `collapse_by_doc`, `trec_lines`, `summarize_latency`, `add_common_args` | ok — all defined in the Task 2 block with the arities Task 3 calls |
| C2 ⧫ | Task 2 `build` reads chunk points (`parent_id`, `chunk_index`, `vector.body_vector`) and object points (`key`, `docId`) written by `ingest.py` | ok — writer at `ingest.py:626-646` emits exactly those keys; live payload-only scrolls this round returned `{parent_id, chunk_index}` / `{key, docId}`; all 14,729 live chunk parents resolve through `key_to_ulong` to a live object id; every `chunk_index` parses as int |
| C3 ⧫ | Task 3 `query` reads the multivector payload `docId` written by Task 2 `build` | ok — `resolve_parents` writes `{key, docId, chunk_count}`; the query asks `with_payload: ["docId"]` |
| C4 ⧫ | Task 5 consumes Task 3's `runs/gte-chunks-raw.chunks.trec`, `runs/gte-multivector-raw.chunks.trec`, `runs/raw-latency.json` | ok — file names are the module constants + `.chunks.trec`; sidecar keys `chunks.p95_ms` / `multivector.p95_ms` are what the gate reads |
| C5 ⧫ | Task 5 consumes Task 4's run dir, labels and `keymap.json.stats.json` | ok — same `$MV` layout; `--stats-path` reads the ingest sidecar's keys (`report.py:320-333`) |
| C6 ⧫ | `report.py` consumes the raw TREC files | ok — six whitespace-separated tokens; `ir_measures.read_trec_run` six-way unpack (`python-libs/ir_measures/util.py:302`) parses the plan's sample line (ran) |
| C7 ⧫ | `benchmark-query` consumes `$MV/keymap.json` + sidecar | ok — written on every ingest exit (`ingest.py:864-866`); guard reads `documents`/`chunks` case-insensitively |
| C8 | Task 4 consumes Task 1's `TEI_MAX_BATCH_TOKENS` | ok — step 1 asserts it is in the merged compose before any TEI start |
| C9 ⧫ | Task 5 shell state consumes Task 4's `$MV` | → §2.3 |
| C10 | `stats` output consumed by the gate doc | ok — `storage.json` rows carry `collection/points_count/indexed_vectors_count/segments_count/disk` |

### Rule-like content (both failure directions)

| Row | Rule | Disposition |
|---|---|---|
| R1 | `group_rows`: order by `int(chunk_index)` | ok — string-sort mutation fails `test_group_rows_orders_by_integer_index_not_string`; gaps from dropped empty windows still order correctly |
| R2 | `resolve_parents`: `parent_id` → `key_to_ulong` → object id; missing parents reported, not skipped | ok — hash-id mutation and silent-skip mutation each fail a test; live data resolves 100 % |
| R3 | `collapse_by_doc`: max per doc, descending, truncate after | ok — keep-first (2 tests fail), truncate-before (1 fails), ascending (4 fail) all caught; ties keep first-seen by sort stability |
| R4 | `rank_chunk_hits`: unknown parent exits | ok — `continue` mutation caught |
| R5 | `trec_lines`: rank from 1, `.6f`, six tokens | ok — rank-from-0 mutation caught; format equals `TrecRunWriter`'s |
| R6 | `summarize_latency`: nearest-rank on n−1 | ok — floor mutation caught; empty → `ValueError` |
| R7 | Under-fill check `< DOCUMENT_BUDGET` on either arm, after writing | ok — exercised with stubs; the live 250-hit search returned 200 distinct parents |
| R8 | `written != expected_rows` against `points_count` | ok — `points_count` equals `count?exact=true` on both live collections (14,160/14,160 and 3,500/3,500 at read time), so the reconciliation compares like with like on a static collection |
| R9 | Upsert `wait=false` batches then `wait=true` on the last, then read `points_count` | ok — Qdrant applies a shard's updates in order, so the last operation's completion implies the earlier ones; the `points_count != len(points)` check right after is the backstop |
| R10 | Compose interpolation `${TEI_MAX_BATCH_TOKENS:-16384}` / `${EMBED_MODEL_ID:-…}` / `${BENCH_EMBED_MODEL:-nomic-embed-text}` at each `up` | ok — TEI recreate sets both TEI vars; API recreate sets `EMBED_MODEL_ID` + `BENCH_EMBED_MODEL` and, being `--no-deps` single-service, cannot recreate `tei-embed` (dry-run); the final API recreate with nothing set restores nomic/Ollama and the bge-base `Models__0` default, which is inert |
| R11 | Restore loop name parsing `${f%%-6802952876034638*}` | ok — matches every snapshot in the directory; the peer id is this instance's (all nine snapshot dirs carry it) |

### Dropped candidates (failed the literal-wrongness test)

| Row | Candidate | Why dropped |
|---|---|---|
| D1 | `python3 multivector.py build … \| tee` masks `build`'s exit status, so a failed `build` leaves a partial collection that `query` (next line) would run against | The failure text is in the tee'd log and the plan's expected `5183 points, 19967 rows == 19967 chunk points` line would be absent; `stats` prints the multivector `points_count`; a re-run needs `--drop`. Visible, not silent — and it presupposes a failure the reconciliations already make loud |
| D2 | Task 4 step 2's expected `/info` values are listed in a different order than the JSON emits them | The check is a set of values read by a human; the command prints all four |
| D3 | (b) prints `BUILD UNKNOWN` banners the plan does not mention | Informational; the gate reads the CI line, which prints regardless |
| D4 | The per-query latency order is fixed (chunks first, then multivector) rather than alternating | The spec specifies "interleaved per query" and this is that; an order effect is not a mechanic the plan got wrong |
| D5 | `Iverson.LoadTest/scripts` cwd continuity between Task 4 steps 2 and 3 | Same task, same shell; the migration's identical pattern executed |

## 1. Verified-plan-assumptions cross-check

| # | Status | Fresh-read evidence |
|---|---|---|
| P1 | still holds | `ls` on main and the branch: no `multivector.py` / `test_multivector.py` |
| P2/P27 | still holds | branch `docker-compose.yml:175` byte-exact; `--profile tei config` renders the three-item list with the default interpolated |
| P3 | still holds | `import multivector` (which imports `ingest`) under a socket guard: no connect; exports present; defaults `benchmark_documents_tenant_bypass` / `…_chunks_tenant_bypass`; `HTTP_TIMEOUT_SECONDS` 300, `EMBED_RETRIES` 2 |
| P4 | still holds | `test_report.py:14-15` |
| P5 | still holds | `def key_to_ulong(key: str) -> int` `:226`; `def embed(text, model, document_prefix, embed_url)` `:530`, `{model, input}` `:532`, returns `data[0]["embedding"]` `:557`; section header `:528` |
| P6 | still holds | `TrecRunWriter.cs:29-31`; `BenchmarkQueryScenario.cs:284-288` |
| P7 | still holds | `report.py:117-153` (`*.trec` glob, qrels excluded, files and dirs mixed); `:541-546` baseline excluded by abspath — and reproduced by running it |
| P8 | still holds | `report.py:320-334` |
| P9 | still holds | live scroll with `with_payload: [..]` returns exactly those keys; second page follows `next_page_offset` (full paging run this round) |
| P10 | still holds | live `GET` → 404 `doesn't exist` |
| P12 | still holds | spec §10 row 11; live `du` prints `68M\t/qdrant/storage/collections/…` |
| P13 | still holds | migration plan `:812`, `:859-860`; `BenchmarkQueryScenario.cs:220, 285` |
| P14 | still holds | migration plan `:819, :821-823, :826, :839-846, :851-857, :905-921` read; the plan's Task 4/5 blocks are those commands with the model and paths substituted |
| P15 | still holds | `docker compose config` → `name: iversonserver`; the running containers carry that project label |
| P17 | still holds | assembled exactly per the steps: Task 2 file → 10 passed; + Task 3 tests → `AttributeError`; + Task 3 code → 12 passed |
| P18 | still holds | `pytest 9.1.1` on Python 3.14.4 |
| P19 | still holds | `scifact-run-2026-08-26/{beir/{corpus,queries}.jsonl, qrels.trec, runs/}` |
| P20 | still holds | grep over `*.cs *.py *.json *.sh` on the branch → only the two `Iverson.Embeddings.Tests` files |
| P22 | still holds | `read_trec_run` of the sample line → `ScoredDoc('1','15830352',0.812345)` (ran) |
| P23 | still holds | `.gitignore:49`; `git check-ignore -v` → that rule; 46 tracked files under `docs/plans/` (was 45 — the plan's own commit) |
| P24 | still holds | `git log --oneline -8` |
| P26 | still holds | live info has `points_count/indexed_vectors_count/segments_count/config.params.vectors.body_vector.size`; live search returns `result[].{id,score,payload}`; scroll `result.points[]` + `next_page_offset` |

**Span check** — dependencies no listed or inherited assumption covers:

- (a) The multivector collection is HNSW-indexed by the time `query` measures it. No assumption states it; the spec's "default HNSW on both" (§3.1) and the CDR's span item (e) established that the index *will* be built, not *when*. Not verifiable read-only in-round; the plan's ordering makes it false for the first queries → §2.2.
- (b) `points_count` is exact for the `build` reconciliation — verified: equals `points/count?exact=true` on both live collections.
- (c) Task 5's shell has `$MV` — it does not; the plan never sets it in that task → §2.3.
- (d) TEI `/info` exposes `max_batch_tokens` (Task 4 step 2 greps for it) — verified live on the running container.
- (e) The compose project is the same one whether invoked from the branch worktree (where the containers were created) or from main's `Iverson.Server` after the merge — verified: the name derives from the directory basename (`iversonserver`) and the containers carry that label, so single-service `up` adopts them.
- (f) The gate-doc template `2026-09-GATE-embedding-migration.md` — does not exist yet; it is the migration's Task 8 output. The second named template exists, so nothing blocks.

## 2. Literal-wrongness findings

1. **Task 1 step 2's verification greps cannot produce the step's expected output.** `grep -A 6 '^  tei-embed:'` stops at the sixth line after the service key; in the rendered config (`profiles`, `- tei`, `command:`, then the items) that is `- --auto-truncate`, so the two new items are never shown, and `TEI_MAX_BATCH_TOKENS=4096 … | grep -A 6 '^  tei-embed:' | grep -c '"4096"\|^      - 4096$'` prints `0`, not the `1` the plan annotates. Evidence: the plan's edit applied to a scratch copy of the branch's compose file and rendered with `docker compose --project-directory <Iverson.Server> -f <scratch> --profile tei config`: the first pipeline printed only `--model-id / BAAI/bge-base-en-v1.5 / --auto-truncate`; the count printed `0`; `grep -A 10` shows the full five-item list ending `- --max-batch-tokens` / `- "4096"`. The edit itself is correct — only the step's checks are wrong, and a worker following "expect … 1" stops on a phantom failure. Fix: use `-A 9` (or anchor on `command:` with `-A 5`) in both pipelines: `docker compose --profile tei config | grep -A 9 '^  tei-embed:' | grep -A 5 'command:'` and `… | grep -A 9 '^  tei-embed:' | grep -c '"4096"\|^      - 4096$'   # 1`.

2. **Task 5 step 1 runs `query` before Qdrant has indexed the multivector collection, so the gated measurements are taken against an unindexed (plain-search) collection while the index build competes for the CPU, and `stats` — run afterwards — records a state that was not the one measured.** `cmd_build` returns as soon as the last `wait=true` upsert is acknowledged (plan lines 391–406); `cmd_query` checks only that both collections exist (`require_collection`, lines 540–541) and starts its first Qdrant calls within ~2 s (one object scroll, then one ~1.2 s TEI embed per query). Qdrant's `wait=true` waits for the update to be applied, not for the optimizer: HNSW is built asynchronously by the indexing optimizer once a segment's vector data exceeds `indexing_threshold` (live config: 10,000 KB; `flush_interval_sec` 5; `default_segment_number` auto → 2 segments on this 4-CPU box, as both live collections show), with `status: yellow` while it runs. Live evidence that indexing is asynchronous and threshold-gated: the chunks collection reports `status green`, `points_count 14,729`, `indexed_vectors_count 13,341`. For a fresh 5,183-point / 19,967-row collection the build takes tens of seconds on this box, so the first tens of queries run exact MaxSim over ~10k unindexed rows per segment, under contention. Gate 3 is the p95 (the 16th-slowest of 300) of exactly those samples, so ≥ 16 pre-index queries (≈ 20 s) set the ratio the gate reads; and for those queries the quality arm is exact search versus the per-chunk arm's HNSW, which is not the "default HNSW on both" comparison spec §3.1/§12 defines. `stats` then runs after `query` and writes the fully indexed end state into `storage.json`, so the gate document would show a state that did not hold during measurement. Fix: (i) in `cmd_build`, after the last upsert, poll `collection_info(target)` until `status == "green"` (bounded, e.g. 10 min, printing `indexed_vectors_count`), and refuse if `indexed_vectors_count` is still 0 — at two segments × ~30 MB the collection clears the 10 MB threshold, so 0 means it was never indexed and the arm is brute force; (ii) in `cmd_query`, before the loop, require `status == "green"` on both collections and write each collection's `{status, points_count, indexed_vectors_count, segments_count}` into `raw-latency.json` so the gate document reports the index state the latencies were measured under; (iii) in Task 5 step 1's expected output, add the multivector `indexed_vectors_count` (> 0) beside the point/row counts.

3. **Task 5 uses `$MV` in steps 1 and 3 but never sets it; in a fresh shell the task is not executable as written.** Task 4 exports `MV=…/scifact-gte-$D` (line 684); Task 5 is a separate task (the plan requires subagent-driven development, one shell per task — the migration plan's Task 7 step 1 re-establishes `K` and the env for exactly this reason). With `MV` empty, `tee $MV/multivector-build.log` fails on `/multivector-build.log`, `query --run-dir ""` opens `beir/queries.jsonl` relative to the scripts directory (`FileNotFoundError`), `stats --run-dir ""` writes `runs/storage.json` into the scripts directory, and step 3's `--run $MV/runs` becomes `--run /runs`. `$D` cannot be recomputed on a later day, so the value must be recovered from disk. Fix: open Task 5 step 1 with the shell state it needs — `export MV=$(ls -d /home/ben/repositories/iverson-benchmark-corpora/scifact-gte-* | tail -1); ls $MV/runs/gte-chunks-api.meta.json` (the Task 4 run dir, proven by the API run's sidecar) and `K=dev-only-not-for-production-qdrant-key-0123456789` — and keep step 3's `export SCI=…`.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §1 has no failed assumptions; §2 has three findings (one wrong verification command, one measurement-ordering defect that reaches the gate, one missing task-level shell variable); §3 is empty. Address §2 before SDD, then proceed via `update-implementation-plan` or manual edits.

### Dynamic evidence (scratch assembly of the plan's Python, branch `ingest.py` symlinked)

```
python3 -m pytest -q test_multivector.py                       -> 12 passed in 0.07s
Task 2 file + Task 2 tests                                    -> 10 passed
Task 2 file + Task 3 tests (before Task 3 code)               -> AttributeError: module 'multivector' has no attribute 'rank_chunk_hits'
M1 collapse keep-first (not max)                              -> 2 failed, 10 passed
M2 collapse truncate BEFORE collapse                          -> 1 failed, 11 passed
M3 group_rows string sort of chunk_index                      -> 1 failed, 11 passed
M4 collapse ascending                                         -> 4 failed, 8 passed
M5 rank_chunk_hits skips unknown parent                       -> 1 failed, 11 passed
M6 trec rank from 0                                           -> 1 failed, 11 passed
M7 resolve_parents drops missing silently                     -> 1 failed, 11 passed
M8 resolve_parents id from key string hash                    -> 1 failed, 11 passed
M9 percentile floor instead of round                          -> 1 failed, 11 passed
restored                                                      -> 12 passed
```
