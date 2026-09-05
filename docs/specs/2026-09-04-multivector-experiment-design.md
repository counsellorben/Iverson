# Chunk-level multivector storage — design (experiment, gated go/no-go)

**Status:** verified design, not yet planned.
**Input:** `Qdrant-Multivector-Plan.md` (Ben, 2026-09-04) — one Qdrant point per document holding an
N × 768 multivector, MaxSim scoring, gte-modernbert-base served by TEI.
**Depends on:** the embedding migration (`2026-09-04-embedding-migration-design.md`) having merged to
local main — this experiment reuses its `ingest.py`, its `tei-embed` compose service and its per-arm
procedure, and the box cannot carry two ingests.

## 1. Why

Iverson stores each chunk as its own point in a `_chunks` collection and collapses chunk hits to
documents by max score (client-side in the benchmark harness; per-chunk in `SearchChunks`). The
plan's own comparison table says chunk-level multivector has the *same quality as per-chunk index +
max aggregation* — and with a one-row query, Qdrant's MaxSim **is** max-over-rows (verified live, §10
row 5). So the plan is a storage-layout and query-shape change, not a ranking change, bundled with a
model change (gte-modernbert-base) that is a separate axis.

The question this experiment answers is whether Iverson should replace the `_chunks` collection with
multivector points: does the layout hold ranking quality, and what does it cost in points, storage and
query latency. The model axis is measured alongside because the plan's model was requested, but it is
observed, not gated.

## 2. Scope

**In:** one gte-modernbert-base ingest of SciFact at the 512/448 window; a new script that derives a
multivector collection from the per-chunk collection and queries both directly; three query runs;
scoring against the existing `rerank-a0` baseline; a gate document; one templated argument on the
compose `tei-embed` service.

**Out:** any change to `Iverson.Api`, `Iverson.Vector` or the clients; the plan's C# reference program;
the 2048/1792 window; NFCorpus and FreshStack; quantization; the migration's `--pair` scoring mode.
Adoption, if the gate passes, is a later spec (§8).

### 2.1 Decisions taken during brainstorming (Ben, 2026-09-04)

| Decision | Choice | Why |
|---|---|---|
| Goal | go/no-go on replacing `_chunks` with multivector points | so the experiment must cover quality, cost and the `SearchChunks` contract |
| Model | `Alibaba-NLP/gte-modernbert-base` via TEI, as the plan says | requested; it adds a per-chunk gte control so model and layout separate |
| Window | **512/448 chars**, not the plan's ~2,000 | at 2048/1792 SciFact is 81 % single-chunk (4,180 of 5,183; §10 row 1): a multivector point degenerates to one vector and the layout arm would be a near-null test. At 512/448 it is 99 % multi-chunk, 3.85 rows per point, and matches `rerank-a0`'s window |
| Corpus | SciFact (title-prefixed run-dir corpus) | the only corpus with a nomic baseline at this window; FreshStack's baseline is 384-dim arctic and its whole-body gte embeds would run far past 8 h |
| Locus | Python harness only | smallest build that yields the verdict; `SearchChunks` cannot search a multivector collection |
| Arms | derive the multivector arm from the per-chunk arm's vectors | zero extra embedding; the two arms hold byte-identical vectors so only the layout differs |
| Body embeds | keep them (≈ 4 h of the ingest) | preserves ingest.py's contract; the `.similar` run is reported, not gated |

## 3. Design

### 3.1 Arms and runs

One ingest, three query runs, one baseline already on disk.

| Label | Vectors | Storage | Query path | Purpose |
|---|---|---|---|---|
| `rerank-a0` (exists) | nomic, 512/448 | per-chunk | API | prior baseline; context only |
| `gte-chunks-api` | gte, 512/448 | per-chunk | API (`benchmark-query`) | gte as Iverson ranks it: centroid fusion, decay, MMR |
| `gte-chunks-raw` | same points | per-chunk | `multivector.py query`: Qdrant named-vector search, 250 chunks collapsed to 50 docs | layout control, raw scores |
| `gte-multivector-raw` | same vectors, regrouped | multivector | `multivector.py query`: Qdrant query, MaxSim, 50 docs | the arm under test |

The gated comparison is `gte-multivector-raw` vs `gte-chunks-raw`: identical vectors, identical
query embeddings, default HNSW on both; only point layout and scoring differ. `gte-chunks-api` vs
`rerank-a0` is the model observation (same window, model differs, the API's ranking stack on top).

### 3.2 TEI

The migration's `tei-embed` compose service with `EMBED_MODEL_ID=Alibaba-NLP/gte-modernbert-base`.
Verified (§10 rows 6–9): TEI cpu-1.8 (1.8.3) loads the model on CPU through its ONNX backend, serves
768-dim unit-length CLS embeddings, and needs no prefix on either side — the id is absent from the
prefix table, and both `EmbeddingPrefixes.For` and `ingest.py`'s `document_prefix_for` fall through to
the empty default (§11 A5, A6).

**Compose change (the one repo edit outside the scripts).** With TEI's default `max_batch_tokens`
16384 the warm-up batch was OOM-killed at 6.9 GB resident while the live stack ran (§10 row 7). The
service's command gains one templated argument:

```yaml
command: ["--model-id", "${EMBED_MODEL_ID:-BAAI/bge-base-en-v1.5}", "--auto-truncate",
          "--max-batch-tokens", "${TEI_MAX_BATCH_TOKENS:-16384}"]
```

The default is TEI's own default, so the bge arms are unchanged; the gte arm starts with
`TEI_MAX_BATCH_TOKENS=4096` (ready in ≈ 60 s, 3.5 GB resident). `max_batch_tokens` also caps
`max_input_length` (4096 tokens at that setting), which still covers the longest SciFact body
(10,128 chars ≈ 2,500 tokens); `--auto-truncate` remains the safety net. The plan's suggestion to add
`--max-input-length` is wrong for this image: TEI 1.8 rejects the flag (§10 row 8).

### 3.3 `multivector.py` — one new script, three subcommands

Lives in `Iverson.Server/Iverson.LoadTest/scripts/` beside `ingest.py`; stdlib-only; imports
`ingest.py`'s `qdrant_request(method, path, body)` (Qdrant URL + api-key, returns
`(status, parsed)`) and `embed(text, model, document_prefix, embed_url)` rather than copying them
(§11 A3, A4).

**`build`** — derives the multivector collection from the per-chunk one.

1. Scrolls the chunks collection (`POST /points/scroll`, `with_vector: true`, `with_payload: true`,
   paging on `next_page_offset`). Each point's vector arrives as `{"body_vector": [...]}` (named
   vectors scroll as a map, §11 A9); its payload carries `parent_id` (the parent's GUID **key**, not
   its point id) and `chunk_index` as a **string**.
2. Groups rows by `parent_id`, orders each group by `int(chunk_index)` (a string sort would place
   `"10"` before `"2"`).
3. Resolves each parent to its object point: id = `key_to_ulong(parent_id)` (verified equal to the
   live object point id, §11 A10); reads `key` and `docId` from that point's payload.
4. Creates `benchmark_documents_multivector_tenant_bypass` with one unnamed vector config
   `{size: <probed from the chunks collection>, distance: Cosine, multivector_config:
   {comparator: max_sim}}` — Cosine to match `IntelligenceCollectionManager.Metric`, so an adoption
   measures the same thing; no quantization, because the per-chunk collection has none and adding it
   to one arm would confound the comparison.
5. Upserts one point per parent: `id` = the object point id, `vector` = the ordered rows
   (`[[...], [...]]`), payload `{key, docId, chunk_count}`. Batches of 100 with `wait=true` on the
   last.
6. Refuses to run if the collection already exists unless `--drop`; exits non-zero if any parent lacks
   an object point or if rows written ≠ the chunks collection's `points_count`. Prints both counts.

**`query`** — writes TREC runs from raw Qdrant queries.

1. Reads `<run>/beir/queries.jsonl` (`_id`, `text`; 300 rows for SciFact, §11 A14). Embeds each
   query once through TEI with `embed(text, model, "", embed_url)` — the same route and empty prefix
   the API uses (§11 A23).
2. Builds `parent key → docId` once by scrolling the object collection (payload only). No key-map file.
3. Per query, in **interleaved** order so latency is comparable:
   - per-chunk: `POST /collections/<chunks>/points/search` with
     `{vector: {name: "body_vector", vector: q}, limit: 250, with_payload: ["parent_id"]}`;
     collapse to documents by max score, descending, truncate to 50 — the same rule as
     `DocumentRanking.CollapseByDocId` and the same 50 × 5 budget as `benchmark-query` (§11 A11);
   - multivector: `POST /collections/<multivector>/points/query` with
     `{query: [q], limit: 50, with_payload: ["docId"]}` (a one-row multivector query; §11 A8).
4. Writes `runs/gte-chunks-raw.chunks.trec` and `runs/gte-multivector-raw.chunks.trec` in
   `TrecRunWriter`'s space-separated six-column format (`qid Q0 docid rank score label`), and
   `runs/raw-latency.json`: per mode, `n`, `p50_ms`, `p95_ms`, `mean_ms` around the Qdrant call only
   (embedding excluded), plus the model id, embed URL and collection names.
5. Fails loud (non-zero, after writing what it has): any non-200 from Qdrant or TEI; a dimension
   mismatch (Qdrant returns 400 "Vector dimension error", §10 row 5); an unresolved `parent_id`; fewer
   than 50 documents for any query (would understate R@50 silently).

**`stats`** — read-only cost table: `GET /collections/<name>` for both collections (`points_count`,
`indexed_vectors_count`, `segments_count`) and
`docker exec iverson-qdrant du -sh /qdrant/storage/collections/<name>` (§11 A16). Prints one table
and writes `runs/storage.json`.

### 3.4 Reused unchanged

`ingest.py` (gte ingest: `--model Alibaba-NLP/gte-modernbert-base --embed-url http://localhost:8091
--chunk-max-chars 512 --chunk-step 448`), `benchmark-query` (API control run), `report.py
--baseline` (scoring + paired statistics), the migration plan's Task 6 steps 4–6 and Task 7's restore
(snapshot loop, `_iverson_schema` delete, API recreate, `RESTORE.md` loop; §11 A15).

## 4. Failure semantics — fail loud

Every count that can be checked is checked and mismatches exit non-zero: ingest sidecar chunks must
equal 19,967 and documents 5,183 (§11 A12); `build` rows written must equal chunk points; `query`
must resolve every parent and fill every query's 50 slots; `report.py`'s structural checks must show
300 queries covered and no duplicate doc ids. A run that fails any of these is not scored.

`stack.py` is never called after TEI is up: its out-of-tier stop halts every `iverson-` container not
in the tier, `iverson-tei-embed` included (§11 A18). Every compose action is single-service
`--no-deps`, as in the migration.

## 5. Component contract

| Component | Change |
|---|---|
| `Iverson.Server/Iverson.LoadTest/scripts/multivector.py` | new: `build`, `query`, `stats` (§3.3) |
| `Iverson.Server/Iverson.LoadTest/scripts/test_multivector.py` | new: pytest, same runner as `test_report.py` (§9) |
| `Iverson.Server/docker-compose.yml` | `tei-embed` command gains `"--max-batch-tokens", "${TEI_MAX_BATCH_TOKENS:-16384}"` (§3.2) |
| `docs/plans/2026-09-GATE-multivector.md` | the verdict (§7) |
| `~/repositories/iverson-benchmark-corpora/scifact-gte-<date>/` (untracked) | `beir/`, `qrels.trec`, `keymap.json*`, `ingest.log`, `runs/` |
| `~/repositories/iverson-benchmark-corpora/scifact-gte-qdrant-snapshots/` (untracked) | three `.snapshot` files + `RESTORE.md` |

## 6. Evaluation protocol

Preconditions: the embedding migration has merged to local main; the box is on the SciFact nomic
baseline (5,183 / 19,967 points, API defaults); nothing else runs during the ingest.

1. `TEI_MAX_BATCH_TOKENS=4096 EMBED_MODEL_ID=Alibaba-NLP/gte-modernbert-base docker compose --profile
   tei up -d --no-deps --force-recreate tei-embed`; wait for `/info` to report the gte model id and
   `max_input_length` 4096.
2. Skip this step if `scifact-512-qdrant-snapshots/` already holds the nomic baseline (it does as of
   2026-09-04); otherwise snapshot the live nomic collections with the migration's Task 6 step 4 loop.
3. `ingest.py --drop --corpus <run>/beir/corpus.jsonl --key-map-path <run>/keymap.json --model
   Alibaba-NLP/gte-modernbert-base --embed-url http://localhost:8091 --chunk-max-chars 512
   --chunk-step 448` in the background; poll the log. Expect sidecar `documents 5183, chunks 19967`,
   vector size 768. Snapshot both gte collections.
4. `docker exec iverson-postgres psql -U iverson -d iverson -c "DELETE FROM _iverson_schema WHERE
   type_name = 'BenchmarkDocument';"`, then `BENCH_EMBED_MODEL=Alibaba-NLP/gte-modernbert-base
   EMBED_MODEL_ID=Alibaba-NLP/gte-modernbert-base docker compose up -d --no-deps iverson-api`;
   confirm the API log shows `dimension=768` and the gte model.
5. `benchmark-query … --config-label gte-chunks-api` (≈ 10 min).
6. `multivector.py build`, `multivector.py query`, `multivector.py stats`; snapshot the multivector
   collection into the same snapshot directory.
7. Score:
   - `report.py --run <run>/runs --qrels <run>/qrels.trec --stats-path <run>/keymap.json.stats.json
     --baseline <run>/runs/gte-chunks-raw.chunks.trec` — the gated pair plus the API run against the
     raw control;
   - `report.py --run <run>/runs/gte-chunks-api.chunks.trec --run
     …/scifact-run-2026-08-26/runs/rerank-a0.chunks.trec --qrels <run>/qrels.trec --baseline
     …/rerank-a0.chunks.trec` — the model observation.
   `--pair` is **not** used: it enforces pool invariance (identical per-query document sets, built for
   reranking arms) and would declare the multivector arm invalid (§11 A13).
8. Restore the nomic baseline collections and recreate the API with defaults (migration Task 7 step 5);
   stop `tei-embed`.
9. Write `docs/plans/2026-09-GATE-multivector.md`: score table (four runs, both RPC files), paired
   statistics for the gated pair, latency and storage table, verdict per §7.

**Cost.** Measured under Task 6 load (§10 row 9): 1.16 s per 512-char chunk, ≈ 3 s per whole SciFact
body, so ≈ 6.4 h of chunk embeds + ≈ 4.3 h of body embeds ≈ 11 h loaded; an idle box should be
faster. `build` and `query` are minutes.

## 7. Gate

**Go** on the layout question requires all three:

1. 95 % CI lower bound of the nDCG@10 delta, `gte-multivector-raw` − `gte-chunks-raw`, above −0.02;
2. the same for R@50;
3. multivector p95 query latency ≤ 1.25 × per-chunk p95 (from `raw-latency.json`).

Points, vectors and on-disk size are reported, not gated: fewer points is the expected direction, not
the question. Anything short of all three is **no-go**, and the gate document names the failing
criterion. Because the arms share vectors, many exact-zero per-query deltas are expected; report.py
already prints the few-queries-changed banner and names the sign-flip permutation p as the one to
trust in that case.

The model observation (`gte-chunks-api` vs `rerank-a0`) is reported with the same statistics and no
threshold.

## 8. Adoption implications — recorded, not acted on

`SearchChunks` returns the matched chunk's text and index, and the reranker harness consumed the
winning chunk text. MaxSim returns the document and its score but not which row won, so an adopting
design must recover the winning row itself (retrieve the point's rows, argmax dot product locally) and
carry chunk texts in the point payload. Neither cost is measured here; both belong to the adoption spec.

## 9. Testing

`test_multivector.py`, run with `python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_multivector.py -q`
(the `test_report.py` runner, §11 A22). Pure functions only, no Qdrant or TEI:

- regrouping: rows ordered by `int(chunk_index)` (`"10"` after `"9"`), a parent missing from the object
  map is reported, rows-written reconciliation fails on a mismatch;
- max-passage collapse: max not first-seen, sort descending, truncate after collapse (mutation-checked
  as `DocumentRanking`'s tests were);
- TREC row formatting matches `TrecRunWriter`'s six columns;
- latency summary: p50/p95 over a known sample.

The Qdrant request bodies are pinned by §10 rows 3–5, verified against the live 1.18.2 server; the
tests assert the exact dicts the script builds.

## 10. Measurements taken during design (2026-09-04/05, live)

| # | What | Result | How |
|---|---|---|---|
| 1 | SciFact chunk-count distribution at 2048/1792 | 4,180 single-chunk of 5,183 (81 %); 979 two-chunk; 6,219 chunks | `ingest.py`'s `split_into_chunks` over `scifact-full/corpus.jsonl` |
| 2 | Run-dir corpus at 512/448 | 5,183 docs, **19,967** chunks, 3.852/doc, budget reach 64.9 ≥ 50 | same chunker over `scifact-run-2026-08-26/beir/corpus.jsonl` |
| 3 | Qdrant 1.18.2 multivector create | `PUT /collections/c {vectors:{size,distance:Cosine,multivector_config:{comparator:max_sim}}}` → 200 | scratch collection, deleted after |
| 4 | Upsert / scroll / retrieve shapes | `vector: [[…],[…]]` accepted; scroll returns rows as a list (unnamed) or `{"body_vector": […]}` (named), `next_page_offset` paging | same probe + live chunks collection |
| 5 | MaxSim semantics | 1-row query → scores 1.0 / 0.7071 = max cosine over rows; 2-row query sums (2.0 / 1.4142); wrong dim → 400 `Vector dimension error` | same probe |
| 6 | TEI cpu-1.8 + gte-modernbert-base | loads (ONNX backend, 1.8.3); `/info` model_id exact, pooling cls; `/v1/embeddings` 768 dims, L2 norm 1.0; identical to `/embed normalize=true` | temporary container on 8092, removed after |
| 7 | TEI at compose defaults | **OOM-killed during warm-up**, 6.9 GB RSS (dmesg, exit 137) with the live stack running | same |
| 8 | `--max-input-length` | rejected by TEI 1.8 (`unexpected argument`, exit 2) | same |
| 9 | gte per-text cost (under Task 6 load) | 512 chars 1,162 ms median (955–1,751); 2,048 chars 4,195 ms; 10,128-char body 20,632 ms; 3.5 GB RSS at `--max-batch-tokens 4096` | same |
| 10 | Chunk payload / object payload / ids | chunk: `text, parent_id (GUID key), field, chunk_index (string), ownerId`; object: `key, docId, title, body, ownerId, __TenantId`; `key_to_ulong(key) == point id` | live scroll |
| 11 | Storage inspection | `du -sh /qdrant/storage/collections/<name>` works via `docker exec iverson-qdrant` | live |

## 11. Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| A1 | TEI cpu-1.8 serves gte-modernbert-base on CPU | §10 rows 6–9 |
| A2 | `tei-embed` takes `EMBED_MODEL_ID`, passes `--auto-truncate` | worktree `docker-compose.yml:171-179` |
| A3 | `ingest.py` has `--model/--embed-url/--chunk-max-chars/--chunk-step`; `embed(text, model, document_prefix, embed_url)` | `--help`; `ingest.py:530` |
| A4 | `qdrant_request(method, path, body)` → `(status, parsed)`, api-key header | `ingest.py:145-146, 276-300` |
| A5 | unknown family → empty prefix; `verify_contract` passes | ran `verify_contract("Alibaba-NLP/gte-modernbert-base")` |
| A6 | C# unknown family → `("", "")`; `/info` guard is an ordinal `model_id` compare | `EmbeddingPrefixes.cs:13-14, 47`; `EmbeddingService.cs:76-82` |
| A7 / A17 | multivector + Cosine create and upsert | §10 row 3 |
| A8 | one-row MaxSim = max-over-rows | §10 row 5 |
| A9 | scroll shapes | §10 row 4 |
| A10 | payload keys, `parent_id` is the GUID key, `key_to_ulong(key) == id` | §10 row 10; `IntelligenceStoreConsumer.cs:307-312` |
| A11 | budget 50 × 5 = 250; collapse = max, desc, take | `BenchmarkQueryScenario.cs:37-38`; `DocumentRanking.cs:29-46` |
| A12 | 19,967 chunks; guard passes | §10 row 2; `ChunkBudgetGuard.cs:26-40` |
| A13 | `--pair` enforces pool invariance → use `--baseline`; missing `.meta.json` scores as `build: unknown` | `report.py:631-661, 175-197` |
| A14 | 300 queries / 300 qrels queries | `wc -l`, `awk` over the run dir |
| A15 | migration per-arm procedure reusable | plan Task 6 steps 4–6 (`:821-859`), Task 7, `RESTORE.md` |
| A16 | collection info + on-disk size | §10 rows 3, 11 |
| A18 | `stack.py` would stop `iverson-tei-embed` | `stack.py:82-84, 124-131` |
| A19 | `--drop` touches only its two named collections; `ApplyCollectionAsync` only its schema's collection | `ingest.py:789-790`; `IntelligenceCollectionManager.cs:44-100` |
| A20 | 768 dims, unit length | §10 row 6 |
| A21 | per-text cost | §10 row 9 |
| A22 | pytest runner | `test_report.py:1-15` |
| A23 | API and ingest.py share `/v1/embeddings` and the empty query prefix | `EmbeddingService.cs:28, 101` |
| A24 | every Qdrant REST shape used (create, upsert, scroll, named search, `query` with `using`, multivector query, info, delete) | live on 1.18.2 |

## 12. Known issues, accepted as out of scope

- **The layout verdict is at 512/448, not the plan's long window.** Chosen deliberately (§2.1). If
  gte's 8k context is ever the question, it needs a long-document corpus (FreshStack at 2048/1792 is
  73 % multi-chunk) and its own control.
- **HNSW approximation is inside the comparison.** Both raw modes use default search parameters, as the
  API does, so the delta includes any recall difference between per-chunk HNSW with a 250 budget and
  multivector HNSW at 50. That is the difference adoption would experience.
- **Latency is measured on this box, interleaved, embedding excluded.** Absolute numbers do not
  transfer; the ratio is the gate.
- **Timing was measured under load** (Task 6 running). The ETA is pessimistic; the per-text ratios
  are what the plan should trust.
- The whole-body embeds cost ≈ 4 h for a run that is not gated (Ben, 2026-09-04: keep them).
