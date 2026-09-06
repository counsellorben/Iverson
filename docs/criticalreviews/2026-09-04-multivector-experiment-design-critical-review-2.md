# Critical Design Review: 2026-09-04-multivector-experiment-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-09-04-multivector-experiment-design.md`
**Verified Assumptions section:** present

Evidence base for this round: `main` at `8c137d0` (the spec's revision commit), read directly — not
through a worktree, as round 1 had to. Live, read-only: `docker ps`, `docker inspect` on
`iverson-tei-embed`/`iverson-api`/`iverson-worker`, TEI `/info` on 8091, Qdrant 1.18.2 REST on 6333
(collection info, facet, scroll, and 300 real named-vector searches against
`benchmark_documents_chunks_tenant_bypass`), `psql` reads of `_iverson_schema`, `du` inside
`iverson-qdrant`, and the API's `/build`. Nothing was started, stopped, recreated or written. §10
measurements are ground truth per the skill. The §0 sweep below was built before review 1 was read.

## 0. Coverage enumeration

### Sections

| Row | Section | Disposition |
|---|---|---|
| S1 | Header / **Depends on** (revised) | ok — Phase 1 `c0d08dd` and Phase 2 `8db18dd` are both ancestors of `HEAD` (`git log`); "the box carries the bge-base SciFact baseline" and "nomic is no longer deployed anywhere" checked live at A26 below |
| S2 | §1 Why | ok — unchanged by the revision; rests on §10 row 5 and `DocumentRanking.CollapseByDocId`, both re-read this round (`Iverson.LoadTest/Benchmark/DocumentRanking.cs:28-45`) |
| S3 | §2 Scope (revised: "scoring against the existing bge-base M1 run (with `rerank-a0` as context)") | → §2.1 — the In-scope sentence names both runs; step 7 scores only one of them |
| S4 | §2.1 Decisions, incl. the new "Model observation baseline (2026-09-06)" row | ok — the claimed run is on disk at the same window and needs no re-run: `scifact-bge-base-2026-09-04/keymap.json.stats.json` = `chunks 19967, chunk_max_chars 512, chunk_step 448`; the corpus and qrels are byte-identical to `scifact-run-2026-08-26`'s (`md5sum`: `ad1d9c20…` / `f7572e6a…`), so the two run dirs are the same experiment |
| S5 | §3.1 Arms and runs (revised: `bge-base` row, BUILD MISMATCH parenthetical) | ok — the M1 sidecar composite is `7d3a15092f963723` and the live API's `/build` composite is `faf832574f7b14b5`, so the banner will in fact print; the neutrality claim behind it is checked at §1 span (a) |
| S6 | §3.2 TEI (revised: default service, `restart: unless-stopped`, `depends_on` healthy, "Two hazards") | ok — `docker-compose.yml:173-187` has no `profiles:` and carries `restart: unless-stopped`; api `depends_on tei-embed: service_healthy` at `:500-501`, worker at `:569-570`. The hazard is real as stated: with `EMBED_MODEL_ID` unset, `tei-embed`'s resolved command differs from the running gte container's, so compose recreates it on bge-base |
| S7 | §3.2 compose change (`--max-batch-tokens ${TEI_MAX_BATCH_TOKENS:-16384}`) | ok — the live container's `Config.Cmd` is exactly `["--model-id","BAAI/bge-base-en-v1.5","--auto-truncate"]` and `/info` reports `max_batch_tokens 16384`, so the templated default reproduces today's behaviour byte-for-byte on the bge arms |
| S8 | §3.3 `build` | ok — every step re-traced at R1–R5; the collection-name, payload and id mechanics were reproduced live this round |
| S9 | §3.3 `query` | ok — R6–R9; the per-chunk request shape was executed live for all 300 queries (R6) |
| S10 | §3.3 `stats` | ok — `GET /collections/<name>` returns `points_count`/`indexed_vectors_count`/`segments_count` (live: 19967 / 16678 / 2); `docker exec iverson-qdrant du -sh /qdrant/storage/collections/benchmark_documents_chunks_tenant_bypass` → `130M` |
| S11 | §3.4 Reused unchanged (revised: Phase 1 Task 6 steps 4–6 + Phase 2 Task 4 restore; api **and** worker) | ok — both roles carry `Embeddings__ModelId=${BENCH_EMBED_MODEL:-BAAI/bge-base-en-v1.5}` (`docker-compose.yml:444, 529`) and both actually log the guard line (live `docker logs`, see A25). The §3.4 sentence describes Phase 2's Task 4 as ending in a bare `docker compose up -d`, which §4 forbids; §6 step 8 is the operative text and specifies two `--no-deps` commands instead, so this is a description of the source procedure, not an instruction (dropped, D3) |
| S12 | §4 Failure semantics (revised `stack.py` paragraph) | ok — `stack.py`'s tiers now list `tei-embed` (`stack.py:85-86`), `run_compose_up` is `docker compose up -d --no-deps <tier>` (`:102-103`), `stop_out_of_tier` stops every other `iverson-` container (`:126-131`). All three claims verified; the ingest-count and rows-written checks are unchanged from round 1 |
| S13 | §5 Component contract | ok — unchanged by the revision; the compose row matches the §3.2 edit verbatim, the two script paths sit beside `ingest.py`/`report.py`/`test_report.py` in `Iverson.LoadTest/scripts/` |
| S14 | §6 Evaluation protocol, preconditions + steps 1–9 (revised throughout) | preconditions ok (live: 5,183 / 19,967 points at 768 Cosine, `tei-embed` on bge-base, both roles on defaults); steps 1–6 and 8 ok per A10–A16 below; **step 7 → §2.1**; step 9's five-run deliverable is the consumer that breaks |
| S15 | §7 Gate (revised final paragraph) | ok — `report.py` prints `95% CI [lo, hi]` per measure per compared run (`report.py:511`, half-width from `t.ppf(0.975, df)` at `:453-455`), computed as run − baseline, so `--baseline gte-chunks-raw` gives the sign the gate names; the few-queries-changed banner is at `:524-529`. `rerank-a0` "appears in the score table" — see §2.1 |
| S16 | §8 Adoption implications | ok — recorded, not acted on; no protocol step consumes it |
| S17 | §9 Testing | ok — `pytest 9.1.1` runs bare on this box's `python3`; `import ingest` succeeds with no PYTHONPATH (executed this round), and `test_report.py:1-15` only needs PYTHONPATH for the ir_measures-backed sections, which §9's pure-function list excludes |
| S18 | §10 Measurements | ok — ground truth; rows 4, 5, 10 and 11 were independently reproduced live this round |
| S19 | §11 Verified assumptions (A2/A15/A18 re-evidenced, A25–A29 added) | → §1 |
| S20 | §12 Known issues (new nomic bullet) | ok — `scifact-512-qdrant-snapshots/` is present with its `RESTORE.md`; no protocol step references it |

### Rules and operands (both failure directions)

| Row | Rule | Disposition |
|---|---|---|
| R1 | `build` grouping key `parent_id` — over-merge / under-merge | ok — over-merge would need two documents sharing one `parent_id`; `parent_id` is the parent's `key`, and the live object collection holds exactly 5,183 points with distinct keys. Under-merge would need one document's chunks carrying two spellings of the key; one producer writes it once |
| R2 | `build` ordering `int(chunk_index)` — and the operand the spec assumes clean, `field` | ok — the assumed-clean operand tested against real data rather than accepted: `POST /points/facet {"key":"field"}` on the live chunks collection returns a single bucket, `Body` × 19,967. Had any second field been chunked, `(parent_id, chunk_index)` would not be a unique row key and the ordering would be ambiguous; it is not. `ingest.py:644-649` writes the literal `"Body"` as the only field, so the gte ingest reproduces this |
| R3 | `build` parent → multivector point id `key_to_ulong(parent_id)` — collision (over-merge) | ok — executed against live data: `key_to_ulong('d2b2694a-25bd-5392-9463-d252fd990100')` = `450788272006036` and `key_to_ulong('29beccc1-3d1c-577b-a9a2-114337e70200')` = `817174487868073`, both equal to the live object point ids; `chunk_point_id(key_to_ulong('323fbe8d-…'),'Body',3)` = `330886660006614`, the live chunk id. A collision would already have collapsed the object collection below 5,183 points, and it holds exactly 5,183 |
| R4 | `build` reconciliation "rows written = chunks `points_count`" | ok — live `points_count` 19,967; the same 19,967 the existing sidecars carry, and chunking depends only on text and window, both byte-identical for the gte run |
| R5 | `build` refuses an existing collection without `--drop`; multivector collection name is not touched by anything else | ok — `ingest.py --drop` drops exactly its two named collections (`ingest.py:792-793`); `ApplyCollectionAsync` reads `ListCollectionsAsync` but acts only on `schema.CollectionName` and never deletes (`IntelligenceCollectionManager.cs:46-110`) |
| R6 | `query` per-chunk: 250 chunks must collapse to ≥ 50 documents, else the script exits (under-inclusion) | ok — not accepted by assertion: all 300 SciFact queries were embedded through the live TEI (bge-base, with its query prefix) and searched against the live chunks collection with the spec's exact body (`{"vector":{"name":"body_vector","vector":q},"limit":250,"with_payload":["parent_id"]}`). Every query returned 250 hits; distinct parents ranged 96–(median 152.5), minimum **96**, and 0 of 300 fell below 50. The hard-fail cannot fire on this corpus at this window |
| R7 | `query` collapse = max, descending, truncate at 50 (over-inclusion: first-seen instead of max; duplicate doc ids) | ok — `DocumentRanking.CollapseByDocId` keeps `score > existing.Score`, then `OrderByDescending(...).Take(limit)` (`DocumentRanking.cs:32-44`); the collapse is keyed on docId, so a duplicate doc id in one query is impossible by construction, which is what §4's "no duplicate doc ids" check reads |
| R8 | `query` parent key → docId map, and multivector `with_payload: ["docId"]` — same namespace as the qrels | ok — live object payload `docId` values (`21993510`, `15830352`) are BEIR SciFact ids, the same namespace as `bge-base.chunks.trec`'s doc ids (`21456232`, …) and the qrels; one docId per key across 5,183 unique keys |
| R9 | `query` dimension mismatch / non-200 → non-zero exit | ok — §10 row 5 (ground truth); both arms are 768 against a collection sized from the chunks collection's own probe |
| R10 | §6 step 7a: `--run <run>/runs --baseline <run>/runs/gte-chunks-raw.chunks.trec` | ok — directory discovery globs `*.trec` only (`report.py:135`), so `raw-latency.json`/`storage.json`/`*.log` are ignored; the baseline is excluded from the comparison set by absolute path (`:541-546`) but still scored by the scoring loop (`:791-793`); `gte-chunks-raw.meta.json` is absent → `build: unknown`, printed, not fatal |
| R11 | §6 step 7b: `--run gte-chunks-api.chunks --run bge-base.chunks --baseline bge-base.chunks` | ok as an invocation — `run_paired_statistics` leaves exactly one comparison, family size 1; but it is the invocation that drops `rerank-a0` and `*.similar` → §2.1 |
| R12 | Gate criterion 3: multivector p95 ≤ 1.25 × per-chunk p95, from the script's own `raw-latency.json` | ok — the script defines and consumes both numbers; interleaved ordering is specified; §12 accepts that only the ratio transfers |
| R13 | The `_iverson_schema` delete + re-registration must not disturb the just-ingested gte collections | ok — the live row's `vectorFields` is `[{"modelId":"BAAI/bge-base-en-v1.5","dimension":768,"propertyName":"Body"}]`; a gte re-registration is also 768, so `ApplyCollectionAsync` takes the "up to date" branch (`IntelligenceCollectionManager.cs:63-97`). This is the procedure Phase 1 Task 6 step 6 already executed successfully |
| R14 | §6 step 8 restore: uploading the bge-base snapshots over live gte collections of the same names | ok — Phase 2's Task 4 additionally `DELETE`d the collections first (`phase2 plan:514`) and the spec's variant does not, but it cannot differ here: names match, dimensions match (768 vs 768), and the point-id sets are identical (same corpus, same key derivation, same window), so `priority=snapshot` leaves no orphan under either semantics |
| R15 | Eligibility: every producer of a chunk point in the gte collections | ok — producer enumeration: after `--drop` the only writer is `ingest.py` (§6 step 3); the API/worker write path (`IntelligenceStoreConsumer`) is driven by Kafka and nothing publishes a `BenchmarkDocument` during the protocol; `multivector.py build` writes only to the new collection |

### Data-flow arrows (persistence boundaries flagged ⧫)

| Row | Arrow → operation | Disposition |
|---|---|---|
| A1 ⧫ | Qdrant chunk points → `build` grouping/ordering | ok — live scroll returns `{text, parent_id, field, chunk_index, ownerId}` and `next_page_offset`; every parameter the operation needs is in the persisted payload |
| A2 ⧫ | Qdrant object points → `build` parent resolution (`key`, `docId`) | ok — live payload `{key, docId, title, body, ownerId, __TenantId}` |
| A3 ⧫ | `build` upsert → multivector collection; `query`'s `with_payload:["docId"]` reads it back | ok — `docId` is in the payload `build` writes (`{key, docId, chunk_count}`), so the read-back parameter exists in the written shape |
| A4 ⧫ | `<run>/beir/queries.jsonl` → `embed(text, model, "", embed_url)` → Qdrant | ok — 300 rows, keys `_id`/`text`, and the 300 `_id`s are exactly the 300 qrels query ids (set difference empty, both directions). `embed` is `ingest.py:533` |
| A5 ⧫ | Gte run dir `runs/` → `report.py` invocation 7a | ok per R10 |
| A6 ⧫ | **M1 run dir → `report.py` invocation 7b, scored against the gte run dir's qrels** | ok on the qrels question — the two run dirs' `qrels.trec` are byte-identical (`md5sum f7572e6a…`), so scoring an M1 file against the gte dir's qrels is sound. The arrow's *other* end is where it breaks: see §2.1 |
| A7 ⧫ | `rerank-a0.chunks.trec` (a third run dir) → the §6 step 9 score table | → §2.1 — no step-7 invocation reads this file |
| A8 ⧫ | `<run>/keymap.json.stats.json` → `ChunkBudgetGuard` and `report.py --stats-path` | ok — the bge-base sidecar shows the exact keys and values (`documents 5183`, `chunks 19967`) the gte run must reproduce |
| A9 ⧫ | Snapshot files → `RESTORE.md` loop → live collections | ok — `scifact-bge-base-qdrant-snapshots/` holds two `.snapshot` files whose names carry the same `-6802952876034638-` peer token the loop strips on, plus a `RESTORE.md` that points at `scifact-512-qdrant-snapshots/RESTORE.md` for the loop body; the loop is there and executable |
| A10 | Shell env → `docker compose up -d --no-deps iverson-api iverson-worker` (step 4) | ok — `BENCH_EMBED_MODEL` changes both services' resolved `Embeddings__ModelId`, so both config hashes change and compose recreates both; `EMBED_MODEL_ID` is inert for these two services (it appears only in `tei-embed`'s command) and setting it is harmless |

### Dropped candidates (failed the literal-wrongness test)

| Row | Candidate | Why dropped |
|---|---|---|
| D1 | `--no-deps` with both `iverson-api` and `iverson-worker` named on one command line discards the `iverson-worker depends_on iverson-api` ordering the compose file deliberately added, reintroducing the concurrent-startup race its comment describes (`docker-compose.yml:571-586`) | The race needs both replicas to *attempt* a `CREATE … IF NOT EXISTS` that has not yet been satisfied. At step 4 the database is fully provisioned (36 schema rows, every table/index/role/policy present), so every statement short-circuits before any catalog insert. A latent-risk observation, not a break of the asked-for behaviour; if it is worth closing at all, it is a plan-level ordering detail |
| D2 | §3.1's justification for the BUILD MISMATCH banner ("the migration established that the route change between builds is ranking-neutral") names a control that covered a *different* build delta — Phase 1's, not `c0d08dd`→`HEAD` | Verified in-round instead of assumed, and the conclusion survives: `git log c0d08dd..HEAD` touches no ranking code, and the only `Iverson.Api`/`Iverson.Embeddings` commits are enrichment work, HTTP-client renaming, and `ed49399`, which normalizes an **empty** `owner_field` — a no-op for `BenchmarkDocument`, whose live descriptor carries `"ownerField": "OwnerId"`. Recorded at §1 span (a) rather than as a finding |
| D3 | §3.4 describes Phase 2's Task 4 restore as ending in `docker compose up -d`, which §4 forbids | §3.4 is describing the source procedure; §6 step 8 is the operative instruction and specifies two single-service `--no-deps` commands. No step tells the operator to run the bare `up` |
| D4 | §6 step 8 leaves `_iverson_schema` without a `BenchmarkDocument` row, so the post-condition no longer matches A26's "verified 2026-09-06" state | The next `benchmark-query`/harness run re-registers it, exactly as Phase 1 Task 7 left the box; nothing in this protocol reads the row afterwards |
| D5 | Between step 1 and step 4, `iverson-api`/`iverson-worker` keep serving with `Embeddings__ModelId=BAAI/bge-base-en-v1.5` against a TEI now serving gte, and their guard does not re-run (`EnsureInitializedAsync` returns early once `_dimension > 0`) | Requires something to embed through the API in that window; §6 preconditions say nothing else runs, the ingest talks to TEI directly, and no `BenchmarkDocument` write is published. Speculation about an input the protocol excludes |
| D6 | `build` holds all 19,967 × 768 float rows in Python memory (~0.5 GB) on a 9.7 GB box already at 5.9 GB used | A resource-headroom concern with no established failure, and streaming is an implementation choice — `critical-implementation-review`'s territory, not a design break |
| D7 | §6 step 5's "≈ 10 min" for `benchmark-query` was estimated for a faster model; gte's measured per-text cost (§10 row 9) suggests longer | An ETA, not a correctness claim; §12 already says the timings are pessimistic |

## 1. Verified-assumptions cross-check

| # | Status | Fresh-read evidence (this round) |
|---|---|---|
| A1 | still holds | §10 rows 6–9 (ground truth) |
| A2 | still holds (re-evidence confirmed) | `docker-compose.yml:173-187` is exactly the `tei-embed` block: no `profiles:` key, `restart: unless-stopped` on line 176, `command: ["--model-id","${EMBED_MODEL_ID:-BAAI/bge-base-en-v1.5}","--auto-truncate"]` on 177, `start_period: 5m` on 187 |
| A3 | still holds | `--model/--embed-url/--chunk-max-chars/--chunk-step` present; `def embed(text, model, document_prefix, embed_url)` is at `ingest.py:533` (the cite says 530 — a three-line drift, the signature is unchanged) |
| A4 | still holds | `qdrant_request` at `ingest.py:279-303` returns `(status, parsed)` and adds the `api-key` header; `QDRANT_URL`/`QDRANT_API_KEY` are at `:148-149` (cite says 145–146 — same three-line drift) |
| A5 | still holds | executed: `ingest.document_prefix_for('Alibaba-NLP/gte-modernbert-base')` → `''`; `ingest.verify_contract('Alibaba-NLP/gte-modernbert-base')` returns without exiting |
| A6 | still holds | `EmbeddingPrefixes.cs:13-14` (empty defaults), `:47-48` (`For` falls through); `EmbeddingService.cs` ordinal `model_id` compare in `VerifyServedModelAsync` (the block the cite names) |
| A7 / A17 | still holds | §10 row 3 |
| A8 | still holds | §10 row 5 |
| A9 | still holds | §10 row 4, reproduced live (named-vector scroll returns a `{"body_vector": …}` map plus an integer `next_page_offset`) |
| A10 | still holds | reproduced exactly: `key_to_ulong(key) == ` the live object point id for two sampled points, and `chunk_point_id` reproduces a live chunk id; payload keys as stated |
| A11 | still holds | `DocumentBudget = 50` and `ChunkBudgetMultiplier = 5` at `BenchmarkQueryScenario.cs:39-40` (cite says 37–38); `DocumentRanking.cs:28-45` |
| A12 | still holds | live chunks `points_count` 19,967; `ChunkBudgetGuard` reach 64.9 ≥ 50 |
| A13 | still holds | `--pair` and `--baseline` are mutually exclusive (`report.py:748-749`); `load_build_composite` returns `None` for a missing sidecar → `unknown` (`:175-192`). Also re-confirmed that the baseline-generator defect noted in earlier work is fixed: `baseline_run` is materialised with `list(...)` at `:558` |
| A14 | still holds | `wc -l beir/queries.jsonl` = 300; the 300 `_id`s and the 300 qrels query ids are the same set |
| A15 | still holds (Phase 1 span is 816–865, not the cited 819–859) | Phase 1 plan Task 6 step 4 at `:816`, step 5 at `:837`, step 6 at `:849`, step 7 at `:866`; Phase 2 plan `:516` is the snapshot-upload loop and `:525` is the `docker compose up -d` — the cited `:516-525` is exact |
| A16 | still holds | live: `points_count`/`indexed_vectors_count`/`segments_count` = 19967/16678/2; `du -sh` inside `iverson-qdrant` → `130M` |
| A18 | still holds (re-evidence confirmed) | `stack.py:85-86` tiers both list `tei-embed`; `run_compose_up` builds `["docker","compose","up","-d","--no-deps",*services]` at `:102-103`; `stop_out_of_tier` at `:126-131` stops every running `iverson-`-prefixed container not in the tier |
| A19 | still holds | `ingest.py:792-793` drops exactly the two named collections (cite says 789–790); `ApplyCollectionAsync` never deletes and acts only on `schema.CollectionName` |
| A20 | still holds | §10 row 6 |
| A21 | still holds | §10 row 9 |
| A22 | still holds | `test_report.py:1-15`; `python3 -m pytest --version` → `pytest 9.1.1`, and §9's suite needs no PYTHONPATH because it touches no ir_measures path |
| A23 | still holds | `EmbeddingService.cs` composes `_queryPrefix` from `EmbeddingPrefixes.For` and posts to `EndpointUri("/v1/embeddings")`; `ingest.embed` posts to `{embed_url}/v1/embeddings` with `document_prefix + text` |
| A24 | still holds | ground truth, plus this round's 300 live named-vector searches on 1.18.2 |
| A25 | still holds | `Program.cs:30-32` reads `WORKLOAD_ROLE`; `EnsureInitializedAsync` at `:406` sits outside the `workloadRole == "api"` guard; `depends_on tei-embed: service_healthy` at `docker-compose.yml:500-501` (api) and `:569-570` (worker); both live containers carry `Embeddings__ModelId=BAAI/bge-base-en-v1.5`, and **both** logs actually contain `EmbeddingService initialized: model=BAAI/bge-base-en-v1.5 dimension=768 …` |
| A26 | still holds | live: 5,183 object / 19,967 chunk points, both 768 Cosine; `iverson-tei-embed` `Config.Cmd` = `--model-id BAAI/bge-base-en-v1.5 --auto-truncate` and `/info` agrees; `scifact-bge-base-qdrant-snapshots/` holds two `.snapshot` files + `RESTORE.md`; `_iverson_schema.BenchmarkDocument` `vectorFields[0].modelId` = `BAAI/bge-base-en-v1.5`; `scifact-512-qdrant-snapshots/` still present |
| A27 | still holds | `bge-base.chunks.trec`: 15,000 rows, 300 distinct queries, exactly 50 per query; sidecar `chunks 19967, chunk_max_chars 512, chunk_step 448`; `bge-base.meta.json` composite `7d3a15092f963723` |
| A28 | still holds | `EmbeddingPrefixes.Table` carries `nomic-embed-text`, `snowflake-arctic-embed`, `BAAI/bge-base-en-v1.5`, `BAAI/bge-small-en-v1.5` and no gte row (`EmbeddingPrefixes.cs:32-38`), and `Family` splits on `':'`, which the gte id does not contain → `("", "")`; Python side executed above |
| A29 | still holds | grep for `auto-truncate` / `max-batch-tokens` / `EMBED_MODEL_ID` / `tei-embed` across the repo (excluding `.git`, worktrees, build output) returns only `docker-compose.yml`, the Helm `tei` chart's own `args` (`charts/tei/templates/statefulset.yaml:51`), `stack.py`, `ingest.py`'s `--embed-url` help text, `Iverson.Launcher/Program.cs:20`, and two `Iverson.Embeddings.Tests` files. No test or tool parses the compose `command` list. `git log 5563454..HEAD -- report.py Iverson.Vector Iverson.LoadTest/Benchmark` is empty |

**Span check** — design dependencies no listed assumption states, each verified in-round:

- (a) **The `c0d08dd`→`HEAD` build delta is ranking-neutral.** §3.1 leans on the migration's same-build control, which covered Phase 1's route change — not the delta that will actually separate the M1 run from `gte-chunks-api`. Verified directly instead: `git log c0d08dd..HEAD` touches `Iverson.Vector`, `Iverson.LoadTest/Benchmark` and `report.py` not at all; the `Iverson.Api`/`Iverson.Embeddings` commits are enrichment (`ae5c08d`, `44d4e84`), HTTP-client naming/defaults (`6207965`), comment sweeps, and `ed49399`, whose effect is confined to descriptors with an **empty** `owner_field` — `BenchmarkDocument`'s live descriptor has `"ownerField": "OwnerId"`, and the read path already treated `""` and `null` alike. Neutral. No finding.
- (b) **`report.py` scores the `--baseline` file itself, not only the comparison set** — step 9's table needs `gte-chunks-raw`'s own numbers, and `gte-chunks-raw` is the baseline. Verified: `resolve_run_paths` keeps it, the scoring loop covers every resolved path, and only `run_paired_statistics` excludes it (`report.py:791-793`, `:541-546`). Verified.
- (c) **The step-8 restore is safe against live collections of the same name and dimension** — R14. Verified.
- (d) `import ingest` from a sibling script has no side effect beyond reading `ingest-contract.json`, and `main()` is guarded. Executed this round. Verified.
- (e) §3.2's `max_batch_tokens`-caps-`max_input_length` claim remains uncovered by any assumption, as round 1 noted at its span (d); the situation is unchanged and the mitigation is unchanged (chunk inputs are ≈ 128 tokens, `--auto-truncate` bounds the failure to truncation of the ungated body embeds, and §6 step 1 makes the value observable before any ingest cost). Not re-raised.

## 2. Literal-wrongness findings

### 2.1 — Step 7 no longer scores `rerank-a0` or either `.similar` file, but step 9's deliverable requires them

**Description.** The revision swapped the model-observation baseline from `rerank-a0` to the bge-base
M1 run and, in the same edit, raised step 9's deliverable from "score table (four runs, both RPC
files)" to "score table (**five** runs, both RPC files)". But it changed step 7b from
`--run gte-chunks-api --run rerank-a0 --baseline rerank-a0` to
`--run gte-chunks-api --run bge-base.chunks --baseline bge-base.chunks`, which *removes* `rerank-a0`
from every invocation in the protocol. §7 nonetheless states "`rerank-a0` appears in the score table
as context only", and §2 lists `rerank-a0` as in-scope context. After step 7 the operator holds
scores for five files — `gte-chunks-api.chunks`, `gte-chunks-api.similar`, `gte-chunks-raw.chunks`,
`gte-multivector-raw.chunks` (all from 7a's directory sweep) and `bge-base.chunks` (7b) — and none for
`rerank-a0.chunks`, `rerank-a0.similar`, or `bge-base.similar`. Step 9 as written cannot be produced
from step 7's output.

**Evidence.**
- `git show 8c137d0` — step 7b's `--run …/rerank-a0.chunks.trec` is deleted; step 9's "four runs" becomes "five runs"; §7's `rerank-a0` sentence is added.
- Spec §6 step 7 (both invocations), §6 step 9, §7 final paragraph, §2 "In".
- `report.py` scores exactly the files `resolve_run_paths` resolves from the `--run` values plus the `--baseline` file (`report.py:135, 785-793`); a directory expands to its own `*.trec` only (`:135`), so 7a cannot reach `scifact-run-2026-08-26/runs/` or `scifact-bge-base-2026-09-04/runs/`.
- The files exist and are scorable against this qrels: `rerank-a0.chunks.trec` is 15,000 rows in `~/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26/runs/`, `bge-base.similar.trec` is in the M1 run dir, and all three run dirs' `qrels.trec` are byte-identical (`md5sum f7572e6aef242267104d7ad5cda8f729`).

**Proposed fix.** Extend step 7's second invocation to cover every file the table names, rather than
leaving the operator to reconstruct the missing rows from older reports:

```
report.py --run <run>/runs/gte-chunks-api.chunks.trec
          --run <run>/runs/gte-chunks-api.similar.trec
          --run …/scifact-bge-base-2026-09-04/runs/bge-base.chunks.trec
          --run …/scifact-bge-base-2026-09-04/runs/bge-base.similar.trec
          --run …/scifact-run-2026-08-26/runs/rerank-a0.chunks.trec
          --run …/scifact-run-2026-08-26/runs/rerank-a0.similar.trec
          --qrels <run>/qrels.trec
          --baseline …/scifact-bge-base-2026-09-04/runs/bge-base.chunks.trec
```

Alternatively, if the intent was that `rerank-a0` and the `.similar` rows be transcribed from the
migration's own report rather than re-scored, say so in step 9 and drop the implication that step 7
produces them. Either resolution is fine; the current text implies the first and specifies neither.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- Round 1's dropped candidate **D4** ("the model-observation report will print `!! BUILD MISMATCH` and the spec does not say so") is now stated explicitly in §3.1, with the M1 composite named. Confirmed live: the M1 sidecar is `7d3a15092f963723` and the running API's `/build` composite is `faf832574f7b14b5`, so the banner will print.
- Round 1 read `docker-compose.yml`, `ingest.py`, `report.py`, `stack.py` and the prefix/embedding sources from the `embedding-migration` worktree at `f2706a4`, because they had not yet merged. A2, A15 and A18 are now re-evidenced against `main`, and this round re-read all of them there; the profile-gated shape round 1 recorded for A2 (`profiles: ["tei"]`, `docker-compose.yml:171-179`) is correctly superseded by the default-service shape at `:173-187`.
- Round 1's span note **(b)** (that the 250-chunk budget really reaches 50 documents) rested on three existing run files all having 50 rows per query. This round tested the raw path itself — 300 live searches with the spec's own request body — and found a minimum of 96 distinct parents per query. The dependency is now verified on the operation the script will actually perform, not only on a downstream artifact of a different code path.
- Round 1's **R14** (`stack.py` would stop `iverson-tei-embed`) is superseded by the merged tier list: `tei-embed` is now *in* both tiers, so §4's revised text — that `stack.py up` would silently recreate it on bge-base rather than stop it — is the correct hazard, and it is verified.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 has one item, §3 is empty. Address §2.1 (one
invocation in §6 step 7, or one sentence in §6 step 9), then proceed. Everything else in the
revision — the bge-base baseline, the default-service `tei-embed` hazards, the api+worker recreate,
the `stack.py` re-characterisation, and A25–A29 — checks out against the live box and `main` at
`8c137d0`.
