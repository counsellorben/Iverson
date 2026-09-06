# Critical Design Review: 2026-09-04-multivector-experiment-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-09-04-multivector-experiment-design.md`
**Verified Assumptions section:** present

Evidence base for this round: the spec's cited files were read on the `embedding-migration` branch at the worktree `/home/ben/repositories/Iverson/.worktrees/embedding-migration` (`f2706a4`) for everything the migration changed (`ingest.py`, `report.py`, `stack.py`, `docker-compose.yml`, `EmbeddingPrefixes.cs`, `EmbeddingService.cs`, `EmbeddingServiceOptions.cs`, the migration plan), and on `main` for the files unchanged between branches (`DocumentRanking.cs`, `ChunkBudgetGuard.cs`, `BenchmarkQueryScenario.cs`, `MaxPassageAggregator.cs`, `TrecRunWriter.cs`, `IntelligenceStoreConsumer.cs`, `IntelligenceCollectionManager.cs`). Live Qdrant was read (collection info, one-point scrolls) but never written; no TEI container was started. §10 measurements are treated as ground truth.

## 0. Coverage enumeration

### Sections

| Row | Section | Disposition |
|---|---|---|
| S1 | §1 Why | ok — "with a one-row query MaxSim is max-over-rows" rests on §10 row 5 (ground truth); "collapses by max score client-side" matches `DocumentRanking.CollapseByDocId` (`DocumentRanking.cs:28-45`) and `MaxPassageAggregator.Aggregate` (`MaxPassageAggregator.cs:40-57`) |
| S2 | §2 Scope / Out | ok — nothing the design's own steps need is in the Out list; `--pair` exclusion is consistent with A13 (see R11) |
| S3 | §2.1 Decisions | ok — every decision is a picked choice with a stated reason; the window decision's 19,967-chunk figure is the same number the existing baseline sidecar and the bge-base sidecar carry (`scifact-run-2026-08-26/keymap.json.stats.json`, `scifact-bge-base-2026-09-04/keymap.json.stats.json`: `"chunks": 19967`) |
| S4 | §3.1 Arms and runs | ok — the gated pair is raw-vs-raw on one collection's vectors; "default HNSW on both" checked at R13 |
| S5 | §3.2 TEI | ok — compose service and `--auto-truncate` at worktree `docker-compose.yml:171-179`; the one-line command edit is a well-formed compose list item; TEI's own `max_batch_tokens` default is 16384, so the bge arms are unchanged by the templated default; the `max_input_length` cap claim is examined at §1 span check (d) |
| S6 | §3.3 `build` | ok — every step traced at R1–R5 and A1–A4 |
| S7 | §3.3 `query` | ok — every step traced at R6–R10 and A5–A9 |
| S8 | §3.3 `stats` | ok — `GET /collections/<name>` fields `points_count`, `indexed_vectors_count`, `segments_count` all present in the live response; `du -sh` path is §10 row 11 |
| S9 | §3.4 Reused unchanged | ok — `ingest.py` flags exist (`ingest.py:719-745`); `benchmark-query` requires `--corpus-path/--key-map-path/--output-dir/--config-label` (`BenchmarkQueryScenario.cs:59-80`), all supplied by the migration plan's Task 6 step 6 command; `report.py --baseline` exists (`report.py:742-751`); Task 6 steps 4–6 and Task 7 step 5 read in full |
| S10 | §4 Failure semantics | ok — the ingest sidecar counts are what `ingest.py` writes (`update_stats_sidecar`, `ingest.py:660-689`); the `report.py` structural section prints coverage and duplicate counts (`report.py:213-270`) — it does not exit non-zero, and the spec says "must show", i.e. a read check, so this is consistent (candidate dropped, see D6); `stack.py` claim at R14 |
| S11 | §5 Component contract | ok — paths consistent with §3.3/§3.2/§6; `docs/plans/` being gitignored is a commit-mechanics detail, not a design fact |
| S12 | §6 Evaluation protocol | ok — each step traced at A10–A14; step 7's two invocations checked at R11/R12 |
| S13 | §7 Gate | ok — `report.py` prints `95% CI [lo, hi]` per measure per compared run (`report.py:478-479`), computed as run − baseline (`report.py:417`), so `--baseline gte-chunks-raw` yields the sign the gate names; `raw-latency.json` is the script's own artifact; the few-queries-changed banner exists (`report.py:496-502`) |
| S14 | §8 Adoption implications | ok — recorded, not acted on; no design step depends on it |
| S15 | §9 Testing | ok — `test_report.py:1-15` runner, `pytest 9.1.1` importable on this box's Python 3.14.4; the listed pure functions are all things the script must define; no Qdrant/TEI needed |
| S16 | §10 Measurements | ok — ground truth per the skill; the live scroll this round reproduced row 10's shapes exactly (named-vector dict, `chunk_index` as a string, `parent_id` a GUID key) |
| S17 | §11 Verified assumptions | → §1 |
| S18 | §12 Known issues | ok — each is an acknowledged confound with a stated scope; none contradicts a design step |

### Rules and operands (both failure directions)

| Row | Rule | Disposition |
|---|---|---|
| R1 | `build` scroll: `with_vector: true` yields `{"body_vector": [...]}` | ok — live scroll of `benchmark_documents_chunks_tenant_bypass` returned `vector` as a dict with one 768-float `body_vector` entry and an integer `next_page_offset` |
| R2 | `build` grouping key `parent_id` → one point per parent (over-merge / under-merge) | ok — `parent_id` is written as the parent's `key` string (`ingest.py:640`), one key per document (`key = uuid5(NAMESPACE, f"{corpus_name}:{doc_id}")`, `ingest.py:846`), and BEIR `_id`s are unique, so groups are 1:1 with documents; under-merge would need two spellings of one key, and the string is written once by one producer |
| R3 | `build` ordering `int(chunk_index)` | ok — `chunk_index` is `str(idx)` (`ingest.py:643`); empty windows are dropped without renumbering (`ingest.py:590`) so gaps can occur and a numeric sort still orders correctly; a wrong order could not change MaxSim in any case (max is order-invariant), so the gate cannot break on it |
| R4 | `build` parent → object point id via `key_to_ulong(parent_id)` | ok — `ingest_document` writes the object point with `id = key_to_ulong(key)` (`ingest.py:585, 616`) and the chunk with `parent_id = key` (`:640`); same function, same string; the point-id derivation is replayed against C# goldens on every run (`_verify_algorithm_goldens`, `ingest.py:463-528`) |
| R5 | `build` reconciliation: rows written = chunks `points_count`; every parent has an object point | ok — producer enumeration for the gte chunks collection: after `--drop` the only writer is `ingest.py`, which writes the object point before its chunks (`ingest.py:614-651`) and only `field: "Body"` chunks; no C# consumer write occurs in the protocol, so no parent can lack an object point and no non-Body field appears |
| R6 | `query` per-chunk: 250 chunks collapse to ≥ 50 documents (under-inclusion → the script exits) | ok — tested on real data: `rerank-a0.chunks.trec`, `m0-control-2026-09-04.chunks.trec` (nomic) and `scifact-bge-base-2026-09-04/runs/bge-base.chunks.trec` (a TEI model) each have exactly 50 rows for all 300 queries, produced from the same collection layout with the same `top_k` 250 (`BenchmarkQueryScenario.cs:369`) and the same collapse; the raw arm drops only the ownerId filter, which is a constant across all points |
| R7 | `query` collapse rule = max, desc, take 50 | ok — matches `DocumentRanking.CollapseByDocId` (`DocumentRanking.cs:34-44`); Qdrant returns hits score-descending so first-seen would coincide with max anyway, and the tests pin max-not-first-seen regardless |
| R8 | `query` parent key → docId map from the object scroll | ok — object payload carries `key` and `docId` (live scroll: `key '274e227f-…'`, `docId 'MED-4113'`); one key per docId (R2); an unresolved parent exits non-zero |
| R9 | `query` multivector: `{query: [q], limit: 50, with_payload: ["docId"]}` | ok — §10 row 5 (ground truth) for the one-row query shape; `docId` is in the multivector payload the same script writes (`{key, docId, chunk_count}`) |
| R10 | `query` dimension mismatch → 400 | ok — §10 row 5; and both TEI outputs are 768 (A20) against a collection sized from the chunks collection's probe (`collection_vector_sizes`, `ingest.py:303-324`, returns `{"body_vector": 768}` for the live collection) |
| R11 | §6 step 7a: `--run <run>/runs --baseline <run>/runs/gte-chunks-raw.chunks.trec` | ok — directory discovery takes `*.trec` only (`report.py:117-136`), so `raw-latency.json`/`storage.json`/`*.log` are ignored; the baseline is excluded from the comparison set by absolute path (`report.py:527-533`); the compare family per measure is {`gte-chunks-api.chunks`, `gte-chunks-api.similar`, `gte-multivector-raw.chunks`} — the extra `.similar` comparison affects only the Holm-adjusted p, not the CI the gate reads (candidate dropped, D5); `gte-chunks-raw.meta.json` is absent → `build: unknown`, printed not fatal (`report.py:175-197`, A13) |
| R12 | §6 step 7b: model observation vs `rerank-a0.chunks.trec` | ok — `rerank-a0` has 300 queries × 50 rows, tag `rerank-a0`, numeric SciFact doc ids; the qrels file is the same SciFact qrels (300 queries, `awk` this round); the BUILD MISMATCH banner will print (different composite) — the migration's same-build control (`m0-control` identical to `rerank-a0`, columns 1–5) is the standing evidence it is benign; not part of the gate |
| R13 | "default HNSW on both" (§3.1) | ok — Qdrant indexes a segment once its vector data exceeds `indexing_threshold_kb` (10 MB); the live objects collection at 2,566 × 2 × 768 × 4 B ≈ 7.9 MB per segment shows `indexed_vectors_count 0`, while the chunks collection's full segments show indexed counts — threshold mechanics confirmed live. The multivector collection holds the same 19,967 × 768 × 4 B ≈ 61 MB of rows as the chunks collection, so both sit far above threshold; `stats` records `indexed_vectors_count` for both, so the gate document can show it |
| R14 | `stack.py` would stop `iverson-tei-embed` | ok — `stop_out_of_tier` stops every running `iverson-*` container not in the tier (`stack.py:124-131`); neither tier lists `tei-embed` (`stack.py:82-85`) |
| R15 | Query-prefix parity between `multivector.py` (`""`) and the API (`_queryPrefix`) for the gte id | ok — `EmbeddingPrefixes.Family` splits on `':'` (`EmbeddingPrefixes.cs:41-45`); `Alibaba-NLP/gte-modernbert-base` has no colon and is not a table key (`:29-39`) → `("", "")` (`:13-14, 47-48`); no `Embeddings__*Prefix` override exists in the worktree compose (grep empty); `ingest.py`'s `document_prefix_for` falls to the contract default (`ingest.py:414-416`) |
| R16 | API routing of the gte default to TEI and the `/info` guard | ok — `BaseUrlFor` is an ordinal name match over `Models` (`EmbeddingServiceOptions.cs:19-21`) fed by `Embeddings__Models__0__Name=${EMBED_MODEL_ID}` / `BaseUrl=http://tei-embed:80` (`docker-compose.yml:405-406`); `VerifyServedModelAsync` compares `model_id` ordinally (`EmbeddingService.cs:79`); §10 row 6 says TEI reports the id exactly |
| R17 | `ApplyCollectionAsync` after the `_iverson_schema` row is deleted, against the gte collections | ok — an existing collection with matching named-vector dimensions takes the "up to date" branch and is not recreated (`IntelligenceCollectionManager.cs:63-97`); only the schema's own collection name is touched (A19); identical to the migration's executed Task 6 step 6 |
| R18 | Run-file format → `ir_measures` | ok — `read_trec_run` does a strict six-way unpack on `line.split()` (`python-libs/ir_measures/util.py:302`); `TrecRunWriter` writes `qid Q0 docid rank score tag` (`TrecRunWriter.cs:30-31`); SciFact doc ids are numeric and the labels are single hyphenated tokens, so the raw runs parse |

### Data-flow arrows (persistence boundaries flagged ⧫)

| Row | Arrow → operation | Disposition |
|---|---|---|
| A1 ⧫ | Qdrant chunk points (persisted payload) → `build` grouping/ordering | ok — operation needs `parent_id`, `chunk_index`, `body_vector`; live payload keys are `text, parent_id, field, chunk_index, ownerId` and the vector map has `body_vector` (scroll this round) |
| A2 ⧫ | Qdrant object points → `build` parent resolution (`key`, `docId`) | ok — live object payload keys `key, docId, title, body, ownerId, __TenantId` |
| A3 ⧫ | `build` upsert → multivector collection (`id`, `vector: [[…]]`, payload `{key, docId, chunk_count}`) | ok — shapes are §10 rows 3–4; the id is the object point id (R4), unique per parent |
| A4 | chunks `GET /collections` → `build` reconciliation (`points_count`) and create (`size`) | ok — both fields present in the live info response |
| A5 ⧫ | `<run>/beir/queries.jsonl` → `embed(text, model, "", embed_url)` | ok — rows carry `_id` and `text` (head of the file this round, 300 lines); `embed` signature at `ingest.py:530` |
| A6 | TEI `/v1/embeddings` → Qdrant search/query vector | ok — `embed` returns `data[0].embedding` (`ingest.py:554-557`), 768 floats (A20) |
| A7 ⧫ | Qdrant object scroll (payload only) → `query`'s key→docId map | ok — same keys as A2; `with_payload: true` without vectors is the migration's own scroll shape (§10 row 4) |
| A8 | per-chunk search hits → collapse → TREC rows | ok — needs `score` and `payload.parent_id` (requested via `with_payload: ["parent_id"]`), then docId via A7 |
| A9 | multivector query hits → TREC rows | ok — needs `score` and `payload.docId` (requested) |
| A10 ⧫ | TREC files → `report.py` structural check / scoring / `--baseline` | ok — R11, R12, R18 |
| A11 ⧫ | `raw-latency.json` → gate 3 | ok — the script defines and consumes its own `p95_ms` per mode; no other reader |
| A12 ⧫ | `<run>/keymap.json.stats.json` → `benchmark-query`'s `ChunkBudgetGuard` and `report.py --stats-path` | ok — `ingest.py` writes `documents`/`chunks` (`ingest.py:673-674`), read case-insensitively by the harness (`BenchmarkQueryScenario.cs:51-52, 101-113`); 19,967 / 5,183 → reach 64.9 ≥ 50 (`ChunkBudgetGuard.cs:31-36`) |
| A13 ⧫ | `<run>/keymap.json` → `benchmark-query` `KeyMap` | ok — written on every ingest exit (`ingest.py:864-866`); the API arm's parent keys are the same `uuid5("beir:<docId>")` keys the map holds |
| A14 | §6 step 8 restore: snapshot files → `snapshots/upload` loop → API recreate with defaults | ok — `scifact-512-qdrant-snapshots/` holds both nomic snapshots (`…-6802952876034638-2026-08-28-…`) and the `RESTORE.md` loop; the peer id in the file names is this Qdrant instance's (the bge-base snapshots carry the same id), so the loop's `c="${f%%-6802952876034638*}"` derivation works for the multivector snapshot too |

### Dropped candidates (failed the literal-wrongness test)

| Row | Candidate | Why dropped |
|---|---|---|
| D1 | "Byte-identical vectors" (§2.1) is not strictly true: the scroll→JSON→upsert round trip passes f32 through Python's f64 repr | Cannot change the gate's validity: shortest-repr round-tripping reproduces the f32 in all but boundary cases, Cosine re-normalises on insert, and a 1-ulp perturbation cannot flip a document's rank in a way the comparison is asked to detect |
| D2 | The t-based 95 % CI is questionable under a zero-inflated delta distribution | The spec picks the CI knowingly (§7 last paragraph) and names the permutation p as the sanity read; a statistics preference, not a mechanic failure |
| D3 | The multivector collection is left in Qdrant after step 8 | The stated post-condition is "box on the nomic baseline, API defaults"; an extra unreferenced collection breaks nothing the API or `benchmark-query` touches |
| D4 | The model-observation report will print `!! BUILD MISMATCH` and the spec does not say so | Informational banner; the gate does not read it and the migration's control run already explains it |
| D5 | `--run <run>/runs` folds `gte-chunks-api.similar.trec` into the Holm family | Holm adjusts p, not the CI the gate uses |
| D6 | `report.py`'s structural checks print rather than exit non-zero, while §4 says "mismatches exit non-zero" | §4 phrases the report.py check as "must show", i.e. a read check the operator applies; the counts the spec says exit non-zero are the script's own, and those do |

## 1. Verified-assumptions cross-check

| # | Status | Fresh-read evidence |
|---|---|---|
| A1 | still holds | §10 rows 6–9 (ground truth) |
| A2 | still holds | worktree `docker-compose.yml:171-179`: `profiles: ["tei"]`, `command: ["--model-id", "${EMBED_MODEL_ID:-BAAI/bge-base-en-v1.5}", "--auto-truncate"]`, port 8091 |
| A3 | still holds | `--model`/`--embed-url`/`--chunk-max-chars`/`--chunk-step` at `ingest.py:719-745`; `def embed(text, model, document_prefix, embed_url)` at `ingest.py:530` |
| A4 | still holds | `QDRANT_URL`/`QDRANT_API_KEY` at `ingest.py:145-146`; `qdrant_request` at `:276-300` returns `(status, parsed)` and never raises on an HTTP status |
| A5 | still holds | `document_prefix_for` falls to `defaultDocumentPrefix` (`ingest.py:414-416`); `verify_contract` falls to the `__default__` golden for an unknown family (`:437`) |
| A6 | still holds | `EmbeddingPrefixes.cs:13-14` (`""` defaults), `:47-48` (`For`); `EmbeddingService.cs:76-82` ordinal `model_id` compare |
| A7/A17 | still holds | §10 row 3 |
| A8 | still holds | §10 row 5 |
| A9 | still holds | §10 row 4, reproduced by this round's live scroll |
| A10 | still holds | §10 row 10 reproduced live; `IntelligenceStoreConsumer.cs:305-311` writes `parent_id = ev.Key`, `chunk_index = chunkIndex.ToString()` |
| A11 | still holds | `BenchmarkQueryScenario.cs:40-41` (50 × 5) and `:369` (`TopK(DocumentBudget * ChunkBudgetMultiplier)`); `DocumentRanking.cs:28-45` |
| A12 | still holds | §10 row 2; `ChunkBudgetGuard.cs:28-41`; the existing sidecars carry 19,967 |
| A13 | still holds | `check_pool` at `report.py:631-666` exits on any per-query set difference; `load_build_composite` at `:175-197` returns `None` → printed `unknown` |
| A14 | still holds | `wc -l queries.jsonl` = 300; 300 distinct qrels query ids |
| A15 | still holds | migration plan Task 6 steps 4–6 and Task 7 step 5 read in full; `RESTORE.md` loop present |
| A16 | still holds | §10 rows 3, 11 |
| A18 | still holds | `stack.py:82-85, 124-131` |
| A19 | still holds | `ingest.py:789-790` drops exactly the two named collections; `IntelligenceCollectionManager.cs:46-110` touches only `schema.CollectionName` |
| A20 | still holds | §10 row 6 |
| A21 | still holds | §10 row 9 |
| A22 | still holds | `test_report.py:1-15`; `pytest 9.1.1` on Python 3.14.4 |
| A23 | still holds | `EmbeddingService.cs:27-28` (`_queryPrefix` from `EmbeddingPrefixes.For`), `:101` (`/v1/embeddings`); no compose prefix override |
| A24 | still holds | live on 1.18.2 (ground truth) |

**Span check** — dependencies the design needs that no listed assumption states, each verified in-round:

- (a) `ir_measures.read_trec_run` unpacks exactly six whitespace-separated tokens (`python-libs/ir_measures/util.py:302`); the raw runs' doc ids (numeric SciFact ids) and tags (`gte-chunks-raw`, `gte-multivector-raw`) are single tokens. Verified.
- (b) The 250-chunk budget actually reaches 50 documents on this corpus at this window — the `query` subcommand exits if it does not. Verified on real data: three 300-query runs on this layout (`rerank-a0`, `m0-control-2026-09-04`, `bge-base`) all have exactly 50 rows per query.
- (c) `EmbeddingServiceOptions.BaseUrlFor` resolves the gte default to `tei-embed` by ordinal name match (`EmbeddingServiceOptions.cs:19-21`). Verified.
- (d) §3.2's claim that `--max-batch-tokens 4096` caps `max_input_length` at 4096 is not covered by any assumption and could not be verified this round (no TEI start permitted). It is not load-bearing for the gated comparison: chunk inputs are ≤ 512 chars (≈ 128 tokens) and only the ungated whole-body embeds could be truncated; §6 step 1 makes the value observable (`/info` must report 4096) before any ingest cost is paid, and `--auto-truncate` bounds the failure to truncation rather than a 413. No forced decision arises.
- (e) Both raw arms are served by an HNSW index rather than one falling to exact search below Qdrant's indexing threshold — the premise of "default HNSW on both". Verified by live threshold mechanics (R13) and observable through `stats`' `indexed_vectors_count`.
- (f) `import ingest` from `multivector.py` has no side effects beyond reading `ingest-contract.json` and asserting the default collection names (`ingest.py:141-143, 183-190`); `main()` is guarded (`:886-887`). Verified.

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

✅ **Approve as-is** — §2 and §3 are both empty. Spec is ready for implementation planning.
