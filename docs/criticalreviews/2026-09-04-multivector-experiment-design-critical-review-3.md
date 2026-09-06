# Critical Design Review: 2026-09-04-multivector-experiment-design (Round 3)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-09-04-multivector-experiment-design.md`
**Verified Assumptions section:** present

Evidence base for this round: `main` at `32cc020` (the spec's current revision, applying round 2's
single finding), read directly. Live, read-only: `docker ps`, TEI `/info` on 8091, Qdrant 1.18.2 REST
on 6333 (collection info, scrolls, point retrieve by derived id, and **300 real named-vector searches
with the spec's exact `query` request body**), `psql` reads of `_iverson_schema`, and an **executed
dry run of §6 step 7's second `report.py` invocation** over the four run files that already exist on
disk. Nothing was started, stopped, recreated or written; the only file created is this review. §10
measurements are ground truth per the skill. Per the skill's iterative rule, the §0 sweep below was
built before either prior review was read.

## 0. Coverage enumeration

### Sections

| Row | Section | Disposition |
|---|---|---|
| S1 | Header / Status / **Depends on** | ok — `c0d08dd` and `8db18dd` are ancestors of `HEAD`; the "planned, needs a CIR round against this revision" status matches the caller's own framing (the plan is stale by design, not this review's surface) |
| S2 | §1 Why | ok — rests on §10 row 5 and `DocumentRanking.CollapseByDocId`; unchanged since round 2 verified both |
| S3 | §2 Scope (In: "scoring against the existing bge-base M1 run (with `rerank-a0` as context)") | ok — after `32cc020` step 7 actually scores both, which is what round 2's §2.1 required; re-checked at R10 and A6 below |
| S4 | §2.1 Decisions, incl. the "Model observation baseline (2026-09-06)" row | ok — `scifact-bge-base-2026-09-04/keymap.json.stats.json` is `chunks 19967, chunk_max_chars 512, chunk_step 448`; the run is on disk and needs no re-run |
| S5 | §3.1 Arms and runs | ok — the gated pair is raw-vs-raw over one collection's vectors; the BUILD MISMATCH parenthetical is now stated in the spec and was confirmed printing in this round's live dry run |
| S6 | §3.2 TEI, incl. the compose change and the two hazards | ok — `docker-compose.yml:173-187` carries no `profiles:`, `restart: unless-stopped`, `command: ["--model-id","${EMBED_MODEL_ID:-BAAI/bge-base-en-v1.5}","--auto-truncate"]`; api `depends_on tei-embed: service_healthy` at `:499-501`, worker at `:568-570`. Live `/info` reports `max_batch_tokens 16384`, so the templated default is behaviour-preserving on the bge arms. The `max_input_length` claim is round 1 span (d) / round 2 span (e); not re-raised |
| S7 | §3.3 `build` | ok — every step re-traced at R1–R5; ids, payload keys and scroll shapes reproduced live this round |
| S8 | §3.3 `query` | ok — R6–R9; the per-chunk request body was executed live for all 300 SciFact queries |
| S9 | §3.3 `stats` | ok — live `GET /collections/benchmark_documents_chunks_tenant_bypass` returns `points_count`/`indexed_vectors_count`/`segments_count` = 19967/16678/2 |
| S10 | §3.4 Reused unchanged | **→ §2.1** — the parenthetical gloss on "Phase 1 plan Task 6 steps 4–6" mis-maps the steps; the range itself is right and does carry the run-dir bootstrap (span (f) below). `ingest.py`'s flags all exist (`:700-747`); `--drop --corpus` drops *then* ingests (`ingest.py:791-801`), it does not exit early |
| S11 | §4 Failure semantics | ok — the sidecar counts are what `update_stats_sidecar` writes; `ChunkBudgetGuard.Evaluate` gives reach 64.9 ≥ 50 at 19,967/5,183 (`ChunkBudgetGuard.cs:28-41`); `stack.py` claims re-verified at R14 |
| S12 | §5 Component contract | ok — the compose row matches §3.2 verbatim; both script paths sit beside `ingest.py`/`report.py`/`test_report.py` in `Iverson.LoadTest/scripts/` |
| S13 | §6 Evaluation protocol, preconditions + steps 1–9 | preconditions ok (live: 5,183 / 19,967 points at 768 Cosine, TEI on `BAAI/bge-base-en-v1.5`, `_iverson_schema.BenchmarkDocument` pinned to bge-base); **step 2 → §2.1**; steps 1, 3–9 ok per R10–R15 and A1–A10 |
| S14 | §7 Gate | ok — `report.py` prints `95% CI [lo, hi]` per measure per compared run, computed run − baseline, so `--baseline gte-chunks-raw` yields the sign the gate names; the few-queries-changed banner exists and fired in this round's dry run |
| S15 | §8 Adoption implications | ok — recorded, not acted on; no protocol step consumes it |
| S16 | §9 Testing | ok — `test_report.py:1-15` documents the same runner; §9's suite touches no ir_measures path, so the PYTHONPATH-free invocation is correct; `sys.path.insert(0, dirname(__file__))` is the sibling-import pattern the new test file copies |
| S17 | §10 Measurements | ok — ground truth; rows 4, 5, 10 and 11 independently reproduced live this round |
| S18 | §11 Verified assumptions | → §1 |
| S19 | §12 Known issues | ok — each is an acknowledged confound with a stated scope; `scifact-512-qdrant-snapshots/` is present, and no protocol step restores it |

### Rules and operands (both failure directions)

| Row | Rule | Disposition |
|---|---|---|
| R1 | `build` grouping key `parent_id` — over-merge (two documents in one group) / under-merge | ok — `parent_id` is the parent's `key` string, written once by one producer; the live object collection holds exactly 5,183 points, so no two of the 5,183 keys collide |
| R2 | `build` parent → point id `key_to_ulong(parent_id)` — id collision, and the > 2^63 case | ok — executed: `key_to_ulong('d2b2694a-25bd-5392-9463-d252fd990100')` = `450788272006036` = the live object point id; `key_to_ulong('323fbe8d-9d43-53a2-9c65-94a8540736aa')` = `12264998695377069468`, and `GET /points` for that id returns the matching object point, so the u64 range above 2^63 round-trips through JSON and Qdrant unharmed; `chunk_point_id(...,'Body',3)` = `330886660006614` = the live chunk id |
| R3 | `build` ordering `int(chunk_index)`, and the operand the spec treats as clean, `field` | ok — the live chunk payload is `{text, parent_id, field: "Body", chunk_index: "3", ownerId}`; a second chunked field would make `(parent_id, chunk_index)` non-unique, and there is none. MaxSim is order-invariant regardless, so the gate cannot break on ordering |
| R4 | `build` reconciliation "rows written = chunks `points_count`" | ok — live `points_count` 19,967, the same number both existing sidecars carry; chunking depends only on text and window, both byte-identical for the gte run |
| R5 | `build` create: `size` probed from the chunks collection; Cosine; no quantization | ok — `collection_vector_sizes` returns `{"body_vector": 768}` for the live chunks collection (config confirmed `{"body_vector":{"size":768,"distance":"Cosine"}}`); `IntelligenceCollectionManager.Metric` is `Distance.Cosine` (`IntelligenceCollectionManager.cs:17`) |
| R6 | `query` per-chunk: 250 chunks must collapse to ≥ 50 documents or the script exits (under-inclusion) | ok — not accepted by assertion: all 300 SciFact queries embedded through the live TEI and searched with the spec's exact body `{"vector":{"name":"body_vector","vector":q},"limit":250,"with_payload":["parent_id"]}`. Every query returned 250 hits; distinct parents ranged **102–204**, median 158; **0 of 300** below 50 |
| R7 | `query` collapse = max, descending, truncate 50 (over-inclusion: first-seen; duplicate doc ids) | ok — `DocumentRanking.CollapseByDocId` keeps `score > existing.Score` then `OrderByDescending(...).Take(limit)`; keyed on docId, so a duplicate doc id inside one query is impossible by construction — which is what §4's "no duplicate doc ids" reads |
| R8 | `query` docId namespace: chunk-arm map vs multivector-arm payload vs qrels | ok — live object payload `docId` values (`21993510`, `16630996`) are BEIR SciFact ids, the same namespace as `bge-base.chunks.trec`'s ids and the qrels; `build` writes `docId` into the multivector payload, so `with_payload:["docId"]` reads a key that exists in the written shape |
| R9 | `query` failure modes: non-200, dimension mismatch, unresolved parent | ok — §10 row 5 for the 400; `ingest.embed` itself `sys.exit`s after retries on any `URLError` (which `HTTPError` subclasses), so a non-200 from TEI is already fatal (`ingest.py:533-561`) |
| R10 | §6 step 7a: `--run <run>/runs --baseline <run>/runs/gte-chunks-raw.chunks.trec` | ok — directory discovery globs `*.trec` only (`report.py:136`), so `raw-latency.json`/`storage.json`/`*.log` are skipped; the baseline is excluded from the comparison set by absolute path (`:541-546`) but still scored by the scoring loop (`:791-793`) |
| R11 | §6 step 7b (the `32cc020` fix): six `--run` files, one of them also the `--baseline` | ok — **executed** this round with the four files that exist on disk (both `bge-base.*` and both `rerank-a0.*`): exit 0, the baseline is dropped from the family with the `[baseline] excluded` line, and one `[compare]` block per remaining run per measure is printed. Adding the two gte files raises the family to 5; Holm adjusts p, not the CI, and the gate reads only 7a's CI |
| R12 | Run-file identity across the six-file invocation: could two files be confused for one run? | ok — `report.py` keys everything on the run path; `sidecar_path_for` strips `.trec` then `.similar`/`.chunks` (`report.py:158-172`), so `bge-base.chunks.trec` and `bge-base.similar.trec` both resolve to `bge-base.meta.json` (composite `7d3a15092f963723`) and the two `rerank-a0.*` files to theirs. Files from three different directories with two shared run tags produce no collision |
| R13 | The six external files are well-formed for `ir_measures` | ok — `awk` over all four on-disk files: 15,000 rows, 300 distinct queries, exactly 50 rows per query, 6 whitespace fields on every line, single-token tags (`bge-base`, `rerank-a0`) |
| R14 | `stack.py` mid-experiment hazard as §4 now states it | ok — `TIERS` list `tei-embed` in both tiers (`stack.py:84-87`), `run_compose_up` is `["docker","compose","up","-d","--no-deps",*services]` (`:102-103`), `stop_out_of_tier` stops every running `iverson-`-prefixed container not in the tier (`:126-131`) |
| R15 | Eligibility: every producer of a chunk point in the gte collections | ok — producer enumeration: after `--drop` the only writer is `ingest.py`, which writes the object point before its chunks and only `field: "Body"`; `IntelligenceStoreConsumer` is Kafka-driven and nothing publishes a `BenchmarkDocument` during the protocol; `multivector.py build` writes only to the new collection |
| R16 | Prefix resolution for the gte id on both sides, and its asymmetry against the bge-base baseline's query prefix | ok — `EmbeddingPrefixes.Table` (`EmbeddingPrefixes.cs:29-39`) carries nomic, arctic and the two bge ids; the gte id has no `':'` and no row → `("", "")`. Executed Python-side: `document_prefix_for('Alibaba-NLP/gte-modernbert-base')` → `''`, `verify_contract` passes. The M1 baseline was embedded with bge's query instruction and the gte arm with none — each model under its own prefix, which is the correct comparison, not a confound |
| R17 | `Embeddings__ModelId=<gte>` must still route to `tei-embed` | ok — `BaseUrlFor` falls through to `BaseUrl` when the id is not in `Models` (`EmbeddingServiceOptions.cs:19-21`), and compose sets no `Embeddings__Models__*` (grep: only `Embeddings__BaseUrl` at `:437, 528` and `Embeddings__ModelId` at `:444, 529`) |

### Data-flow arrows (persistence boundaries flagged ⧫)

| Row | Arrow → operation | Disposition |
|---|---|---|
| A1 ⧫ | Qdrant chunk points → `build` grouping/ordering/upsert | ok — the operation needs `parent_id`, `chunk_index`, `body_vector`; the live scroll returns all three plus an integer `next_page_offset`, with the vector as a `{"body_vector": [...]}` map |
| A2 ⧫ | Qdrant object points → `build` parent resolution (`key`, `docId`) | ok — live payload keys `{__TenantId, body, docId, key, ownerId, title}` |
| A3 ⧫ | `build` upsert → multivector collection → `query`'s `with_payload:["docId"]` | ok — `docId` is in the payload `build` writes; the read-back parameter exists in the written shape |
| A4 ⧫ | `<run>/beir/queries.jsonl` → `embed(text, model, "", embed_url)` → Qdrant | ok — 300 rows keyed `_id`/`text`; `embed(text, model, document_prefix, embed_url)` at `ingest.py:533` |
| A5 ⧫ | `<run>/runs/` → `report.py` invocation 7a | ok per R10 |
| A6 ⧫ | Three run dirs → `report.py` invocation 7b, scored against **one** qrels file | ok — the new claim added at `32cc020` was tested, not accepted: `md5sum` of `scifact-run-2026-08-26/qrels.trec`, `scifact-bge-base-2026-09-04/qrels.trec` and `scifact-bge-small-2026-09-04/qrels.trec` is `f7572e6aef242267104d7ad5cda8f729` for all three. The invocation was then run end-to-end (R11) |
| A7 ⧫ | `<run>/keymap.json.stats.json` → `ChunkBudgetGuard` and `report.py --stats-path` | ok — `ingest.py` writes lowercase `documents`/`chunks`; the harness reads case-insensitively; 5,183 / 19,967 → reach 64.9 |
| A8 ⧫ | `_iverson_schema` row → step 4 delete → re-registration by `benchmark-query` | ok — live row's `vectorFields[0]` is `{"modelId":"BAAI/bge-base-en-v1.5","dimension":768}`; `benchmark-query` is in `needsTenantAndSchema` (`Iverson.LoadTest/Program.cs:88-89`) and calls `SchemaRegistrar.RegisterAllAsync` (`:163`), so the gte row is re-created; 768 → 768 keeps `ApplyCollectionAsync` on the "up to date" branch |
| A9 ⧫ | Snapshot files → the restore loop → live collections (step 8) | ok — `scifact-bge-base-qdrant-snapshots/` holds two `.snapshot` files carrying the `-6802952876034638-` peer token the loop strips on, plus a `RESTORE.md` that points at `../scifact-512-qdrant-snapshots/RESTORE.md` for the loop body |
| A10 ⧫ | **Snapshot-creation loop → `scifact-gte-qdrant-snapshots/` (steps 3, 6) and `scifact-bge-base-qdrant-snapshots/` (step 2)** | **→ §2.1** — the operation these three steps perform exists only in the Phase 1 plan, and the spec's only pointer to it names the wrong step |
| A11 | Shell env → `docker compose up -d --no-deps iverson-api iverson-worker` (step 4) | ok — `BENCH_EMBED_MODEL` changes both services' resolved `Embeddings__ModelId` (`:444, 529`), so both config hashes change and compose recreates both; `EMBED_MODEL_ID` appears only in `tei-embed`'s command and is inert here |

### Dropped candidates (failed the literal-wrongness test)

| Row | Candidate | Why dropped |
|---|---|---|
| D1 | Step 7b compares two `.similar` runs against a `.chunks` baseline, mixing RPCs inside one Holm family | The model observation carries no threshold (§7), and Holm adjusts p, not the CI the gate reads. Round 1 dropped the same shape at its D5; the executed dry run confirms every block still prints its own delta and CI |
| D2 | §6 step 9's "score table (five runs, **both RPC files**)" is impossible for `gte-chunks-raw`/`gte-multivector-raw`, which are chunk-only by construction | A deliverable description, not an instruction that can be executed wrongly; §3.1 already states the raw arms have one query path each |
| D3 | The identity guard at `Program.cs:406` is wrapped in a `try/catch` that swallows the exception, so a TEI/API model mismatch does not kill the container as §3.2's "the API's identity guard fails" suggests | The guard still blocks the arm — `EmbeddingService` leaves `_dimension` unset, and schema registration turns the re-thrown mismatch into `Unavailable`, which is the step-5 `benchmark-query` the protocol runs next. The stated outcome (the arm does not proceed on a mismatch) holds |
| D4 | §3.3 `query` step 2 scrolls the object collection without stating that it pages, while `build` step 1 states it explicitly | Paging is an implementation obligation the script must meet either way; `critical-implementation-review`'s surface, not a design break |
| D5 | `embed`'s `sys.exit` on a TEI failure contradicts §3.3 step 5's "non-zero, **after writing what it has**" | Partial-write-before-exit is an implementation choice inside the new script; the failure is loud and non-zero either way |
| D6 | The multivector collection survives step 8's restore, leaving an extra collection on the box | Round 1 dropped this at D3; the stated post-condition is the bge-base baseline plus API defaults, and an unreferenced collection breaks neither |

## 1. Verified-assumptions cross-check

| # | Status | Fresh-read evidence (this round) |
|---|---|---|
| A1 | still holds | §10 rows 6–9 (ground truth) |
| A2 | still holds | `docker-compose.yml:173-187`: no `profiles:`, `restart: unless-stopped`, `command: ["--model-id","${EMBED_MODEL_ID:-BAAI/bge-base-en-v1.5}","--auto-truncate"]`, `8091:80` |
| A3 | still holds (cite drift: `embed` is at `:533`, not `:530`) | `--corpus :700`, `--key-map-path :704`, `--drop :713`, `--model :723`, `--embed-url :736`, `--chunk-max-chars :741`, `--chunk-step :747`; `def embed(text, model, document_prefix, embed_url)` at `:533` |
| A4 | still holds (cite drift: `:148-149` and `:279-300`) | `QDRANT_URL`/`QDRANT_API_KEY` at `ingest.py:148-149`; `qdrant_request` at `:279` returns `(status, parsed)` and never raises on an HTTP status |
| A5 | still holds | executed: `document_prefix_for('Alibaba-NLP/gte-modernbert-base')` → `''`; `verify_contract('Alibaba-NLP/gte-modernbert-base')` returns without exiting |
| A6 | still holds | `EmbeddingPrefixes.cs:13-14` (empty defaults), `:47` (`For` falls through); `EmbeddingService.cs:76-82` is the ordinal `model_id` compare in `VerifyServedModelAsync` |
| A7 / A17 | still holds | §10 row 3 |
| A8 | still holds | §10 row 5 |
| A9 | still holds | §10 row 4, reproduced live (named-vector scroll returns a `{"body_vector": …}` map) |
| A10 | still holds | reproduced exactly this round for two keys, including one whose id exceeds 2^63 (R2); payload keys as stated |
| A11 | still holds (cite drift: `:39-40`) | `DocumentBudget = 50`, `ChunkBudgetMultiplier = 5` at `BenchmarkQueryScenario.cs:39-40`; `DocumentRanking.CollapseByDocId` |
| A12 | still holds | live chunks `points_count` 19,967; `ChunkBudgetGuard.cs:28-41` gives reach 64.9 ≥ 50 with the finiteness guard first |
| A13 | still holds | `--pair`/`--baseline` mutually exclusive (`report.py:748-749`); `check_pool` exits on any per-query set difference (`:640-666`); `load_build_composite` returns `None` for a missing sidecar. The baseline-generator defect is still fixed (`baseline_run = list(...)`, `:558`) |
| A14 | still holds | 300 rows in `beir/queries.jsonl`, keyed `_id`/`text`; 300 qrels query ids |
| A15 | still holds **as a line range**, but see §2.1 for the step labels | Phase 1 plan Task 6 headers: step 4 `:816`, step 5 `:837`, step 6 `:849`, step 7 `:866`; Phase 2 plan Task 4 `:516` is the snapshot-upload loop and `:525` the `docker compose up -d` — that cite is exact |
| A16 | still holds | live `points_count`/`indexed_vectors_count`/`segments_count` = 19967/16678/2 |
| A18 | still holds | `stack.py:84-87` tiers, `:102-103` `up`, `:126-131` out-of-tier stop |
| A19 | still holds (cite drift: `:792-793`) | `ingest.py:792-793` drops exactly the two named collections; `IntelligenceCollectionManager` acts only on `schema.CollectionName` and never deletes |
| A20 | still holds | §10 row 6 |
| A21 | still holds | §10 row 9 |
| A22 | still holds | `test_report.py:1-15` documents the same runner and the same `sys.path.insert` sibling-import pattern |
| A23 | still holds | `EmbeddingService.cs:28` builds `_queryPrefix` from `EmbeddingPrefixes.For`, `:101` posts to `EndpointUri("/v1/embeddings")`; `ingest.embed` posts to `{embed_url}/v1/embeddings` |
| A24 | still holds | ground truth, plus this round's 300 live named-vector searches and a live `GET /points` retrieve on 1.18.2 |
| A25 | still holds | `Program.cs:30` reads `WORKLOAD_ROLE`; `EnsureInitializedAsync` at `:406` sits outside the `workloadRole == "api"` guard; `depends_on tei-embed: service_healthy` at `:499-501` (api) and `:568-570` (worker); both roles carry `Embeddings__ModelId=${BENCH_EMBED_MODEL:-BAAI/bge-base-en-v1.5}` |
| A26 | still holds | live: 5,183 object / 19,967 chunk points at 768 Cosine; TEI `/info` `model_id` `BAAI/bge-base-en-v1.5`; `scifact-bge-base-qdrant-snapshots/` holds two `.snapshot` files + `RESTORE.md`; `_iverson_schema.BenchmarkDocument` `vectorFields[0].modelId` and `chunkFields[0].modelId` are both bge-base; `scifact-512-qdrant-snapshots/` still present |
| A27 | still holds | `bge-base.chunks.trec`: 15,000 rows, 300 queries, exactly 50 each, 6 fields per line; sidecar `chunks 19967, chunk_max_chars 512, chunk_step 448`; `bge-base.meta.json` composite `7d3a15092f963723` |
| A28 | still holds | `EmbeddingPrefixes.Table` (`:29-39`) has nomic, arctic and the two bge rows and no gte row; `Family` splits on `':'`, absent from the gte id → `("", "")`; Python side executed above |
| A29 | still holds | repo-wide grep for `tei-embed` / `EMBED_MODEL_ID` / `max-batch-tokens` (excluding `.git` and worktrees) hits only `docker-compose.yml`, `stack.py`, `ingest.py`, `Iverson.Launcher/Program.cs:20`, and two `Iverson.Embeddings.Tests` files; the Helm chart passes its own `args` (`charts/tei/templates/statefulset.yaml:51`). Nothing parses the compose `command` list |

**Span check** — design dependencies no listed assumption states, each verified in-round:

- (a) **The `32cc020` invocation actually runs.** Executed with the four on-disk run files: exit 0, `[baseline] excluded …/bge-base.chunks.trec`, and one `[compare]` block per remaining run per measure — exactly the rows step 9's table names. Verified.
- (b) **One qrels file legitimately scores all three run dirs**, the claim `32cc020` added. `md5sum` identical across all three. Verified.
- (c) **`sidecar_path_for` cannot cross-attribute build composites** when six files from three directories share two run tags. Verified at R12.
- (d) **`--drop --corpus X` ingests rather than exiting after the drop** — `ingest.py`'s documented drop-only form returns early only when `args.corpus is None` (`:798-800`), so §6 step 3's single command performs the gte ingest. Verified.
- (e) **The gte ingest reuses the baseline's point ids**, which is what makes `--drop` mandatory at step 3 and what makes step 8's snapshot restore land on the same id space: `corpus_name` is `basename(dirname(--corpus))` = `beir` for every run dir, so the `uuid5` keys are a pure function of the BEIR doc id. Verified by reading `ingest.py:816` and by the live keys matching `scifact-run-2026-08-26`'s. 
- (f) **The `<run>` directory's `beir/` and `qrels.trec` have a producer.** Nothing in §6 creates them, and §5 lists them as run-dir contents. They come from `mkdir -p $M1/runs && cp -r $SCI/beir $M1/ && cp $SCI/qrels.trec $M1/` inside Phase 1 plan Task 6 **step 4** — which is inside the "steps 4–6" range §3.4 incorporates, so the dependency is covered by reference. Verified (and it is the same step 4 that §2.1 is about).
- (g) §3.2's `max_batch_tokens`-caps-`max_input_length` claim remains uncovered by any assumption. Raised at round 1 span (d) and round 2 span (e); nothing has changed and the mitigation is unchanged. Not re-raised.

## 2. Literal-wrongness findings

### 2.1 — The snapshot loop is Phase 1 plan Task 6 **step 5**; the spec points every snapshot step at **step 4**, which is a 5.5-hour bge-base re-ingest that would destroy the gte collections

**Description.** Three steps of §6 perform a snapshot-creation: step 2 (snapshot the bge-base baseline,
if it is missing), step 3 ("Snapshot both gte collections") and step 6 ("snapshot the multivector
collection into the same snapshot directory"). Steps 3 and 6 cite nothing, so the only place the spec
tells the operator where that loop lives is §6 step 2 — and it names **"the Phase 1 plan's Task 6 step
4 loop"**. Phase 1 plan Task 6 step 4 is *"M1 — TEI up with bge-base, ingest"*: it recreates
`tei-embed` on `BAAI/bge-base-en-v1.5`, copies the run dir, and runs
`ingest.py … --drop --model BAAI/bge-base-en-v1.5 …` against the default collection names for ~5.5 h.
It contains no snapshot loop at all. The snapshot loop is **step 5** ("Snapshot M1").

The consequence is not confined to step 2's skippable branch. Steps 3 and 6 sit on the main path,
immediately after the ~11-hour gte ingest, and inherit step 2's pointer. Following it there does not
merely fail to take a snapshot — it drops and re-ingests
`benchmark_documents_tenant_bypass` / `benchmark_documents_chunks_tenant_bypass` under bge-base,
destroying the gte vectors the entire experiment is built on, and leaving `multivector.py build`/`query`
with nothing to read. §3.4's parenthetical compounds it: "the Phase 1 plan's Task 6 steps 4–6
(snapshot loop, `_iverson_schema` delete, API recreate)" is a three-item gloss on a three-step range,
which reads as 4 = snapshot, 5 = delete, 6 = recreate. The true mapping is 4 = TEI-up + run-dir copy +
ingest, 5 = snapshot, 6 = schema delete + API recreate + `benchmark-query`.

**Evidence.**
- Spec §6 step 2: *"otherwise snapshot the live bge-base collections with the Phase 1 plan's Task 6 step 4 loop"*; §6 steps 3 and 6 say "Snapshot …" with no cite; §3.4's parenthetical.
- `docs/plans/2026-09-04-embedding-migration-implementation-plan.md:816` — `**Step 4: M1 — TEI up with bge-base, ingest.**`; `:819` is `mkdir -p $M1/runs && cp -r $SCI/beir $M1/ …`; `:826` is `ingest.py … --drop --model BAAI/bge-base-en-v1.5 …` (annotated "≈ 5.5 h").
- Same file `:837` — `**Step 5: Snapshot M1.**`, whose body is the `for c in benchmark_documents_tenant_bypass benchmark_documents_chunks_tenant_bypass; do … /snapshots?wait=true … done` loop plus the `RESTORE.md` write. `:849` — `**Step 6: Register under bge-base and query M1.**` (the `_iverson_schema` DELETE and the API recreate).
- `ingest.py:792-793` — `--drop` drops exactly `args.object_collection` and `args.chunks_collection`, which are the same defaults the gte ingest writes to (`ingest.py:182-190`); so a step-4 re-run at steps 3/6 is destructive, not merely useless.
- The spec's own `scifact-bge-base-qdrant-snapshots/RESTORE.md` (read this round) documents only the *restore* loop, not the snapshot-creation loop — so there is no second, correct pointer anywhere in the spec's reach.

**Proposed fix.** Change §6 step 2's cite from "Task 6 step 4 loop" to **"Task 6 step 5 loop"**, and
give §6 steps 3 and 6 the same explicit cite so neither depends on step 2 (which the spec expects to be
skipped) being read at all. Correct §3.4's parenthetical to match the real mapping, e.g.
"the Phase 1 plan's Task 6 step 5 (snapshot loop) and step 6 (`_iverson_schema` delete, API recreate)" —
noting that step 4 is reused only for its run-dir bootstrap (`mkdir`/`cp` of `beir/` and `qrels.trec`),
never for its ingest, which §6 step 3 replaces.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- Round 2's §2.1 (step 7 no longer scored `rerank-a0` or either `.similar` file, while step 9's table
  named them) is **resolved** at `32cc020`. The second `report.py` invocation now lists all six run
  files. Verified beyond a read: the invocation was executed this round against the four files that
  already exist on disk and returned exit 0 with a `[compare]` block for every one of them, per
  measure, plus the `[baseline] excluded` line for the run that is also the baseline. The claim the
  fix added — that one qrels file scores all three run dirs — was tested by `md5sum` and holds.
- Round 1's span note **(b)** and round 2's **R6** (that 250 chunks really reach 50 documents) were
  re-tested independently this round with a third model's worth of live traffic: 300 queries, minimum
  102 distinct parents, none below 50. The hard-fail cannot fire on this corpus at this window.
- Round 1's **A2** (recorded as `profiles: ["tei"]` from the pre-merge worktree) stays superseded by
  the merged default-service shape at `docker-compose.yml:173-187`, re-read here.
- Round 1's **R14** (`stack.py` would *stop* `iverson-tei-embed`) stays superseded by §4's revised
  text: `tei-embed` is in both tiers, so the hazard is a silent recreate on bge-base, verified again
  at `stack.py:84-87, 102-103`.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 has one item, §3 is empty. Fix the Phase 1 Task 6
step reference in §6 step 2 (and give steps 3 and 6 their own cite, and correct §3.4's parenthetical),
then proceed. Everything else at `32cc020` — the six-file step 7b invocation, the qrels-identity claim
it rests on, the `build`/`query`/`stats` mechanics, the TEI and compose changes, the api+worker
recreate, and A1–A29 — checks out against `main` and the live box.
