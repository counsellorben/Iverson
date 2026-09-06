# Critical Design Review: 2026-09-04-embedding-migration-design (Round 1)

**Spec:** docs/specs/2026-09-04-embedding-migration-design.md
**Spec (absolute):** `/home/ben/repositories/Iverson/docs/specs/2026-09-04-embedding-migration-design.md`
**Verified Assumptions section:** present (§11, E1–E6, S1–S12, C1–C3, B1–B11, H1)

Repo state: `main` @ `df45e17`, clean working tree. All evidence below is either a `file:line` read at that
commit or a command run in this session, quoted with its output. Nothing under
`~/repositories/iverson-benchmark-corpora/` was written, moved or deleted; the reads there are `cat`,
`ls` and `grep`. No container was started, stopped or recreated: the only live probes are read-only
`curl`s against the already-running `iverson-ollama`. **TEI was not re-probed** — no TEI container is
running and starting one is out of bounds for this review — so every TEI claim below is checked against
the spec's own §10 rows plus already-recorded artifacts from the reranker trial (`TeiRerankClient.cs`,
`rerank-a1.meta.json`), never against a fresh probe.

---

## 0. Coverage enumeration

### Sections

| # | Section | Disposition |
|---|---|---|
| S1 | §1 Why | ok — the two load-bearing claims are §10/memory-sourced and are ground truth per the VA section. Re-checked only the one that is repo-checkable: Ollama holds no bge-base/bge-small. `curl -s http://127.0.0.1:11434/api/tags` → `['qwen2.5:3b', 'nomic-embed-text:latest', 'bge-m3:latest', 'mxbai-embed-large:latest', 'snowflake-arctic-embed:m', 'snowflake-arctic-embed:s', 'all-minilm:latest']` — bge-m3 is a distinct model, so "the backend change forces a model change" stands |
| S2 | §2 Scope | ok — the five exclusions each checked for a hidden dependency. Clients: no `.cs/.ts/.go/.java/.py` client file names a backend URL (`grep -rn "Embeddings__BaseUrl\|EmbeddingServiceOptions"` returns only server, compose, Helm and tests). Enrichment: separate options + separate named client (`ServiceCollectionExtensions.cs:26-42`). Registration guard: untouched and still fires (`SchemaRegistrationOrchestrator.cs:105-131`). Batching: `EmbedAsync` is single-text (`EmbeddingService.cs:54`). The 2,048 window exclusion → §2.2 |
| S3 | §3.1 One client, per-model base URL | → §2.1 |
| S4 | §3.2 Model identity guard | ok — `EnsureInitializedAsync`'s lock spans `EmbeddingService.cs:38-52` and the probe is inside it at `:42`, so a `/info` call "after the probe and inside the same lock" is a real position. `Dimension` is the only initialisation output (`:17,43`), so one extra request per service lifetime adds no state. TEI's `/info` field name is `model_id` — `TeiRerankClient.cs:27` (`[property: JsonPropertyName("model_id")]`), already exercised against `cpu-1.8` |
| S5 | §3.3 Prefix table | ok — see R3/R6. Two new rows flow through `IngestContractTests.EmitContract` into both `embedding.documentPrefixes` and `golden.documentComposition` (`IngestContractTests.cs:118-133`), which is what `ingest.py`'s strict per-family loop needs. Checked the remaining named identifiers in the block: `EmbeddingPrefixes.Table` (`EmbeddingPrefixes.cs:29-34`), `Family` (`:36-40`), `IngestContractTests.cs:85-93` cited by the spec is inside the emit — real, and `ingest.py:415-416` is `document_prefix_for` — real |
| S6 | §3.4 Compose | ok on shape, → §2.1 on the `Embeddings__ModelId` templating. Shape checks: `8091` is free (`grep -n "80[0-9][0-9]:" docker-compose.yml` → `8030`, `8090` reranker, `8080`/`8081` api only); the `reranker` service at `:157-165` is the template it copies; `volumes:` block at `:539-548` holds `reranker_models:` so a sibling `tei_models:` has a home; `VectorRanking__Lambda=${VECTOR_RANKING_LAMBDA:-0.70}` is at `:388`. The `${BENCH_EMBED_MODEL:-nomic-embed-text}` default preserves the literal the compose comment at `:377-383` says the five conformance fixtures depend on |
| S7 | §3.5 Benchmark ingest script | → §2.2 (window override). The other two bullets ok: `embed()` at `ingest.py:530-556` is the single embed site and takes `OLLAMA_URL` from `:147`; the sidecar writer is `update_stats_sidecar` (`:655-681`) and merges by explicit key, so added keys survive `--resume` |
| S8 | §4 Failure semantics | ok — five bullets traced. Probe failure: `Program.cs:404-415` logs a warning and continues, and `SchemaRegistrationOrchestrator.cs:136-142` turns the retry's failure into `Unavailable`. Identity mismatch and 413: design-side, consistent with §3.2/§3.4. Fallback-to-global: `EmbeddingServiceResolver.cs:27` today passes `options.Value.BaseUrl`, and Ollama 404s an unknown model — re-verified live: `curl -o /dev/null -w "%{http_code}" -X POST http://127.0.0.1:11434/v1/embeddings -d '{"model":"no-such-model","input":"probe"}'` → `404` |
| S9 | §5 Component contract | ok — all seven rows name files that exist; the "Untouched" list checked against the change set (`IEmbeddingService`, `EmbedDocumentAsync`/`EmbedQueryAsync` signatures, `EnrichmentService`, `SchemaRegistrationOrchestrator`, the five clients, Helm) and none of them is reached by the §3 changes |
| S10 | §6.1 Baseline and window | ok — `scifact-run-2026-08-26/runs/rerank-a0.chunks.trec` exists; the quoted scores are the gate doc's own (`docs/plans/2026-09-GATE-reranker-phase1.md:129` "A0 0.6960", `:123` "A0 \| 0.9227", restated at `:203`). The 512/448/50 window and the `ce7bf12` fusion change are B3, ground truth |
| S11 | §6.2 Arms | → §2.3 (N0's window). The rest ok: estimates are explicitly "for scheduling, not evidence"; the M0/N0 run paths exist (`ls scifact-run-2026-08-26/runs`, `ls nfcorpus-run-2026-08-27/runs`) |
| S12 | §6.3 Per-arm procedure | → §2.1 (step 4). Steps 1/2/3/5/6 ok — see A7, R4, S13, A9. Step 4's `--no-deps` rationale is sound and matches the constraint that the running containers came from a vanished checkout. One imprecision checked and dropped: "recreate the API … so it re-registers" — the API does not self-register; `Iverson.LoadTest/Program.cs:88-89,151-163` registers `BenchmarkDocument` on the `benchmark-query` command, i.e. at step 5. The outcome the step asks for still happens, one step later, so this is not literal wrongness |
| S13 | §6.4 Statistics | ok — `--run` is `action="append"` (`report.py:720`), `--baseline` routes to `run_paired_statistics` (`:798-799`), and `check_pool` (`:631`) is called only from the `--pair` path (`:677`). `run_paired_statistics` materialises the baseline once (`:559` `baseline_run = list(ir_measures.read_trec_run(baseline_path))`), so the generator-consumed-once defect is fixed at this commit. Holm m=2 follows from two `--run` args minus the excluded baseline (`:544-552`) |
| S14 | §7 Gate | ok — the predicate is a one-sided CI-lower-bound test on two measures; see R7 |
| S15 | §8 Phase 2 outline | ok — every named identifier exists: `Chart.yaml:24-27` (ollama subchart + `condition: ollama.enabled`), `_helpers.tpl:22-34` (`iverson.embeddingEnv`, emitting `Embeddings__ModelId`/`DocumentPrefix`/`QueryPrefix` today), `values.yaml:45` `global.embeddingModels`, `:54` `global.activeEmbeddingModel`, `stack.py`. No phantoms. Outline-only, gated on a pass |
| S16 | §9 Testing | → §1 span/S4 failure (the fake handler). The other four bullets ok: `EmbeddingServiceResolverTests` exists with a per-model construction path (`:92-130`); `IngestContractTests` is generator-and-gate in one test (`:80-105`) so it does fail until regenerated; the `--limit`/`--object-collection`/`--chunks-collection` flags the smoke ingest needs all exist (`ingest.py:694,704,713-715`) |
| S17 | §10 Measurements | ok — the three Ollama rows re-run live and reconfirmed (see §1 E1/E3/E5). TEI rows not re-probed, by constraint; the `report.py --baseline` row reconfirmed structurally at `report.py:533,720,798`; the ingest row reconfirmed against `scifact-run-2026-08-26/keymap.json.stats.json` (`elapsed_seconds 31070.187319`, `embed_calls 25128`, `chunks 19967`) |
| S18 | §11 Verified assumptions | see §1 |
| S19 | §12 Known issues | ok — all four are scope rulings, not mechanics. The `.similar` truncation asymmetry is real and correctly de-gated |

### Rules and operands

| # | Rule | Disposition |
|---|---|---|
| R1 | Resolver: model id → `BaseUrl` (listed → own, unlisted → global) | → §2.1. Over-inclusion checked: an unlisted id cannot pick up a listed `BaseUrl` because the lookup is by exact `Name`. Under-inclusion is the defect — the *default* model short-circuits at `EmbeddingServiceResolver.cs:17-19` and never reaches the lookup at all |
| R2 | Identity guard: `/info.model_id` == `ModelId` (ordinal) | ok both directions. False accept requires TEI to echo a matching id it isn't serving — it reports the loaded model, not the request's (§10 rows 6–7, ground truth). False reject requires a spelling difference between `--model-id` and `Embeddings__ModelId`; §6.3 step 1 and step 4 both take `<model>` from the same shell variable. Ollama's 404 keeps it inert — re-verified live: `curl -o /dev/null -w "%{http_code}" http://127.0.0.1:11434/info` → `404` |
| R3 | Prefix family lookup, C# side and Python side | ok both sides. `BAAI/bge-base-en-v1.5` carries no `:`, so `EmbeddingPrefixes.Family` (`:36-40`, `IndexOf(':') < 0` → whole id) and `ingest.py:406` (`model_id.split(":", 1)[0]`) both return the full id. The `/` is inert in both: it is not a separator for either rule |
| R4 | Chunk-window override → the run's actual windowing | → §2.2. Both directions fail; see the finding |
| R5 | Reuse gate `body == body.strip() and len(body) <= STEP` (`ingest.py:597`) | → §2.2. This is the exclusion rule that decides whether one embed call fills both the object vector and the single chunk. It reads the module-level `STEP` at call time while `split_into_chunks(body)` (`:583`) reads the def-time default — the two can disagree only once an override exists, which is exactly what §3.5 introduces |
| R6 | `require_known_family` loop over every contract family (`ingest.py:733-735`) | ok — this is the eligibility predicate over *producers* of families. The producers are `EmbeddingPrefixes.Table`'s keys, and `IngestContractTests.cs:123-133` emits one `golden.documentComposition` entry per table key plus `__default__`, so adding two rows adds two golden entries and the strict loop still finds each `fam` in `golden`. Verified the current shape: `python3 -c "…json.load…['golden']['documentComposition']"` → keys `nomic-embed-text`, `snowflake-arctic-embed`, `__default__` |
| R7 | Gate predicate: CI lower bound > −0.02 on nDCG@10 **and** R@50 | ok — `run_paired_statistics` prints delta, 95 % CI and MDE per measure and the measures list is `--measures`-driven, so both are available from one invocation. The conjunction is stated, and the "neither / one / both" branches are exhaustive |
| R8 | Conformance override-model exclusion vs. the widened family table | ok — `ModelRejectedScenarioTests.cs:514-524` asserts `OverrideModelId`'s family is neither the default model's family nor any key of `EmbeddingPrefixes.Table`. `OverrideModelId` is spelled `iverson-conformance-…` (`:509-512` doc comment), which collides with neither new bge key, so the two added rows leave this test green for the right reason |
| R9 | `--baseline` self-exclusion by absolute path (`report.py:544-552`) | ok — the arms pass explicit `--run` files in different directories from `--baseline`, so nothing is silently dropped and the Holm family is 2, as the spec says |

### Data-flow arrows

| # | Arrow (→ ends at an operation) | Disposition |
|---|---|---|
| A1 | compose env → `Configure<EmbeddingServiceOptions>` → **`new HttpRequestMessage(…, absolute URI from BaseUrl)`** in the *default* service | → §2.1. Crosses a process boundary; the parameter that fails to exist at the operation is the default model's TEI base URL |
| A2 | `_iverson_schema` row (persisted JSON) → `SchemaDescriptor.ModelOf` → **`resolver.Get(model).EmbedQueryAsync`** (`ObjectSearchGrpcService.cs:206`, `:387`) | ok as a shape, → §2.1 as a value. Persistence boundary checked: `ModelOf` reads `VectorFields[0].ModelId ?? ChunkFields[0].ModelId` (`SchemaDescriptor.cs:27-28`), both of which are persisted on the descriptor, so the query path does receive a model id. It receives the *default* id, which is what routes it into the broken short-circuit |
| A3 | `EmbeddingPrefixes.Table` → `ingest-contract.json` (persisted) → **`document_prefix_for`** and **`verify_contract`** in `ingest.py` | ok — persistence boundary dumped and checked: the file's `embedding` object is `{documentPrefixes, defaultDocumentPrefix}` and `golden.documentComposition` is keyed by the same families. Both Python consumers read exactly those keys (`:415-416`, `:445-451`) |
| A4 | `ingest.py` probe → `ensure_collection(size=dimension)` → **Qdrant search with the API's query vector** | ok for a 384-dim arm. `ensure_collection` sizes both named vectors from the live probe (`:771-780`), and nothing on the read path hard-codes 768: `grep -rn "\b768\b" --include=*.cs Iverson.Api/ Iverson.Vector/ Iverson.Embeddings/` → one comment only (`IntelligenceVectorService.cs:154`). Registration does not create collections — `EnsureCollectionAsync` is called only from `IntelligenceStoreConsumer` (`:157,175,369`) and a 4-dim startup probe (`Program.cs:319,359`) — so step 4/5's re-registration cannot resize or wipe what step 2 wrote |
| A5 | `ingest.py` stats sidecar (persisted) → **`benchmark-query`'s chunk-budget guard** *and* → **`report.py --stats-path`'s throughput block** | ok — two call sites, both checked. `BenchmarkQueryScenario.cs:57,91-101` deserializes into `record IngestStats(int Documents, int Chunks)` with `PropertyNameCaseInsensitive = true`, so the four new keys are ignored. `report.py:322-365` reads the sidecar with `.get()` per key, so extras are ignored there too. This second consumer is not named by B11 — noted in the span check, no finding |
| A6 | `rerank-a0.chunks.trec` → **`report.py --baseline`** scoring | ok — trec files carry docid/rank/score only, so a dimension change cannot reach this operation; `run_paired_statistics` reads them through `ir_measures.read_trec_run` |
| A7 | TEI container → **`GET /info`** → the operator's step-1 assertion on `model_id`, `max_input_length`, `auto_truncate` | ok — all three parameters exist in TEI `cpu-1.8`'s `/info`: `TeiRerankClient.cs:27-29` binds `model_id`, `max_input_length`, `auto_truncate`, and a real recorded response is preserved in `scifact-run-2026-08-26/runs/rerank-a1.meta.json` (`"maxInputLength": 512, "autoTruncate": true`). Not re-probed against a live TEI, by constraint; the recorded artifact is from the same image tag |
| A8 | N0's run directory artifacts → **`--chunk-max-chars` / `--chunk-step` for arm N1** | → §2.3. The parameter the operation needs does not exist in the artifacts the step enumerates |
| A9 | `benchmark-query` → gRPC `RegisterSchema` → **`defaultService.EnsureInitializedAsync`** (`SchemaRegistrationOrchestrator.cs:81,133-142`) | → §2.1. This is the second caller of the initialisation path (the first is `Program.cs:406` at API startup, which only warns on failure); this one converts the failure into `FailedPrecondition`/`Unavailable` and aborts the arm |

---

## 1. Verified-assumptions cross-check

| # | Verdict |
|---|---|
| E1 | **Still holds.** Re-run live: `POST http://127.0.0.1:11434/v1/embeddings {"model":"nomic-embed-text","input":"probe"}` → keys `['object','data','model','usage']`, `len(data[0].embedding) == 768`; unknown model → `404` |
| E2 | **Still holds** (§10 rows 6–7, ground truth; TEI not re-probed) |
| E3 | **Still holds.** Ollama half re-run live: `GET http://127.0.0.1:11434/info` → `404`. TEI half corroborated without a probe by `TeiRerankClient.cs:27` and the recorded `rerank-a1.meta.json` |
| E4 | **Still holds** (§10 rows 6, 9, 10; TEI not re-probed) |
| E5 | **Still holds.** `GET /api/tags` re-run live: no `bge-base`/`bge-small` (bge-m3 present, a different model) |
| E6 | **Still holds** (§10 row 5, ground truth) |
| S1 | **Still holds.** `EmbeddingService.cs:66` is the only `HttpRequestMessage`/URI construction in the service, and `ServiceCollectionExtensions.cs:18` (`client.BaseAddress = new Uri(opts.BaseUrl)`) is the only base-URL use. The cited range `:15-21` is `:14-19` at this commit — content matches |
| S2 | **Still holds.** `services.Configure<EmbeddingServiceOptions>(config.GetSection(EmbeddingServiceOptions.Section))` is at `ServiceCollectionExtensions.cs:10` (spec cites `:11`); `Section = "Embeddings"` at `EmbeddingServiceOptions.cs:5`. Indexed-list binding is §10 row 11, ground truth |
| S3 | **Holds as scoped, quoted grep output is incomplete.** `grep -rn "new EmbeddingService(" --include=*.cs .` returns five non-worktree hits: `EmbeddingServiceResolver.cs:21` plus `EmbeddingServiceTests.cs:44,57` and `EmbeddingServiceResolverTests.cs:100,124`. The design consequence (the resolver is the only *production* construction site, so the per-model `BaseUrl` lookup has one home) is unaffected; the four test sites just also have to be threaded when the constructor's options gain `Models` |
| S4 | **Failed.** The cited evidence does not support "can … answer `/info`". `EmbeddingServiceTests.cs:14-19`: `FakeHttpMessageHandler(HttpResponseMessage response)` returns that *same instance* for every `SendAsync`, with no branch on `request.Method` or `request.RequestUri`, and records only `LastRequest`/`LastRequestBody`. Neither of the other two fakes routes either: `CountingHttpMessageHandler` (`:183-199`) and `RecordingHttpMessageHandler` (`EmbeddingServiceResolverTests.cs:13-25`) are also single-response. Under §3.2 a single `EnsureInitializedAsync` issues two different requests (`POST /v1/embeddings`, then `GET /info`), so none of the three existing fakes can serve the §9 test list — the probe would be answered with the `/info` body and fail to parse. The gap is small (branch on `request.RequestUri!.AbsolutePath`, and build a fresh `HttpResponseMessage` per call as `CountingHttpMessageHandler` already does at `:191-196`) but it is not the "tests use a fake handler that can … answer `/info`" the assumption asserts |
| S5 | **Still holds.** `grep -rn "api/embed"` over `.cs/.py/.yml/.yaml/.ts/.go/.java` (worktrees and `docs/` excluded) → `EmbeddingService.cs:66,77` and `ingest.py:531,554` only |
| S6 | **Still holds.** `EmbeddingService.cs:38-52`: `await _initLock.WaitAsync(ct)`, double-check at `:41`, probe at `:42`, release in `finally` at `:50` |
| S7 | **Still holds.** `EmbeddingPrefixes.cs:36-40` (`IndexOf(':')`, `< 0` → whole id) and `:30` (`StringComparer.Ordinal`). Spec cites `:33-40`; `Table` is `:29-34` and `Family` is `:36-40` |
| S8 | **Still holds.** `IngestContractTests.cs:80-105` writes the file under `IVERSON_REGENERATE_INGEST_CONTRACT=1` and otherwise diffs it; `ingest.py:142-143` loads it at import, `:415-416` reads `embedding.documentPrefixes` |
| S9 | **Still holds.** `SchemaBuilder.cs:339-352`: every vector name is `$"{c.PropertyName.ToSnakeCase()}_vector"` / `_centroid`; no model id reaches a name |
| S10 | **Still holds.** `SchemaRegistrationOrchestrator.cs:774-781` `ValidateIdentifier` is called only on `typeDesc.TypeName` and property names (`:74-76`), so a `/` in a model id is never tested |
| S11 | **Still holds.** `EnrichmentServiceOptions` has its own `Section`/`BaseUrl`, and `AddEnrichment` (`ServiceCollectionExtensions.cs:26-42`) registers a separate named client |
| S12 | **Still holds.** Confirmed each cite: `docker-compose.yml:386` and `:481` (`Embeddings__BaseUrl=http://ollama:11434`), `charts/api/templates/deployment.yaml:124`, `charts/worker/templates/deployment.yaml:119`, `Iverson.Launcher/Program.cs:28` (`WaitForOllamaAsync("http://localhost:11434/api/tags", "nomic-embed-text", …)`), `EmbeddingServiceOptions.cs:6`. A repo-wide `grep -rn "Embeddings__BaseUrl\|Embeddings:BaseUrl"` finds no sixth surface |
| C1 | **Still holds.** `docker-compose.yml:157-165` is the `reranker` service (profile, templated `--model-id`, `8090:80`, named volume); the worker env block spans `:474-492` |
| C2 | **Still holds.** `docker-compose.yml:388` |
| C3 | **Still holds.** `docker-compose.yml:539-548` declares `postgres_data`, `qdrant_data`, `ollama_data`, `reranker_models`, … as named volumes |
| B1 | **Still holds.** `OLLAMA_URL` at `:147`, window constants at `:208-210`, `embed()` at `:530-556`, `main()` at `:684-853` (the spec's `684-745` is the flag block). See the span check for what this assumption does *not* cover |
| B2 | **Still holds.** `ingest.py:406` `family()`, `:414-416` `document_prefix_for` |
| B3 | **Still holds.** `git show chunk-size-512-experiment:…/ingest-contract.json` → `{'maxChars': 512, 'step': 448, 'wordBoundaryLookback': 50}`; `git show de4b6bf:…/ingest.py` → `153:MAX_CHARS = 2048  154:STEP = 1792`; main's committed contract → `{'maxChars': 2048, 'step': 1792, 'wordBoundaryLookback': 50}`. Note in passing (no finding): `wordBoundaryLookback` is 50 on both sides, so overriding only `maxChars`/`step` is sufficient to reproduce the baseline windowing |
| B4 | **Still holds.** `Entities/BenchmarkDocument.cs` carries bare `[IversonEmbedding]`/`[IversonChunk]` and no model argument, so `DeclaredModel` is null and `nextModel = defaultService.ModelId` (`SchemaRegistrationOrchestrator.cs:83,89`). The `DELETE FROM _iverson_schema` remedy is the guard's own documented one (`:116-131`) and the precedent is recorded in `docs/specs/2026-09-01-helm-embedding-model-configuration-design.md:234` |
| B5 | **Still holds.** `ObjectSearchGrpcService.cs:206` and `:387`, both `resolver.Get(SchemaDescriptor.ModelOf(schema)).EmbedQueryAsync(...)` |
| B6 | **Still holds.** `report.py:533` `run_paired_statistics`; `check_pool` at `:631` is referenced only from the `--pair` path at `:677`; `--baseline` dispatch at `:798-799` |
| B7 | **Still holds** by construction, and by A6 |
| B8 | **Still holds.** `scifact-run-2026-08-26/keymap.json.stats.json` → `elapsed_seconds 31070.187319`, `embed_calls 25128`, `chunks 19967`, `documents 5183`; `nfcorpus-run-2026-08-27/keymap.json.stats.json` → `elapsed_seconds 23334.485313` |
| B9 | **Still holds.** `scifact-512-qdrant-snapshots/` holds `RESTORE.md` plus two `.snapshot` files; `RESTORE.md:12-16` is the upload/restore loop, and its verification line states 5,183 / 19,967 points |
| B10 | **Still holds.** `ls nfcorpus-run-2026-08-27` → `NEXT-STEPS.md beir gate.log ingest-started-at.txt ingest.log keymap.json keymap.json.progress keymap.json.stats.json paired-chunks.txt paired-similar.txt qrels.trec report.txt runs sweep.log`. What the directory does *not* hold is in §2.3 |
| B11 | **Still holds.** `BenchmarkQueryScenario.cs:57` `private sealed record IngestStats(int Documents, int Chunks)`, `:51-53` `PropertyNameCaseInsensitive = true`, read at `:91-101` |
| H1 | **Still holds.** `Chart.yaml:24-27` (`name: ollama` … `condition: ollama.enabled`); `charts/ollama/templates/statefulset.yaml` exists and ranges `global.embeddingModels` at `:59` |

**Span check — design dependencies with no covering assumption:**

- The default `EmbeddingService`'s base URL when `Embeddings__ModelId` is a TEI-only model. S12 enumerates the *surfaces* that carry a base URL and S2 covers the binding; neither states that the default service can reach the backend that serves the default model. → **§2.1**.
- Every consumer of `MAX_CHARS`/`STEP` inside `ingest.py`. B1 covers "the window constants come from the contract" — it says nothing about how many places read them or when they are read. → **§2.2**.
- Whether N0's chunk window is recorded anywhere. B10 enumerates the run directory's *files*; no assumption claims the window is recoverable from them. → **§2.3**.
- TEI `/info`'s `auto_truncate` and `max_input_length` fields, which §6.3 step 1 asserts on. E3 covers only `model_id`. **Verified in-round without probing TEI**: `Iverson.LoadTest/Benchmark/TeiRerankClient.cs:27-29` binds all three, and `scifact-run-2026-08-26/runs/rerank-a1.meta.json` preserves a real `cpu-1.8` response carrying `"maxInputLength": 512, "autoTruncate": true`. Covered — no finding.
- The stats sidecar's second consumer. B11 covers `benchmark-query` only; `report.py:322-365` also reads it (`--stats-path`). **Verified in-round**: every access is `stats.get(<key>)`, so the four added keys are inert there. Covered — no finding.

---

## 2. Literal-wrongness findings

### 2.1 — The arm makes a TEI-only model the API default, but the default embedding service is nailed to the global (Ollama) base URL, so the arm cannot register or query

**Description.** §3.1 states: "The default service (`options.Value.ModelId`) keeps the global base URL." §3.4 then makes `Embeddings__ModelId=${BENCH_EMBED_MODEL:-nomic-embed-text}` precisely "so a benchmark arm can make the candidate the default at registration", and §6.3 step 4 runs `BENCH_EMBED_MODEL=<model> EMBED_MODEL_ID=<model> docker compose up -d --no-deps iverson-api`. Those two statements cannot both hold. With `BENCH_EMBED_MODEL=BAAI/bge-base-en-v1.5`, the DI-registered default `EmbeddingService` has `ModelId = BAAI/bge-base-en-v1.5` and `BaseUrl = http://ollama:11434` (the compose value is not templated, and §5's compose row does not list it). The per-model `Models` lookup never runs for it, because `EmbeddingServiceResolver.Get` short-circuits to `defaultService` whenever the requested id equals `options.Value.ModelId` — and the type's registered id *is* that id, by B4. Ollama does not serve bge-base (E5), so:

- API startup: `Program.cs:406` `EnsureInitializedAsync()` → 404 → the catch at `:407-415` logs "Embedding service not initialized at startup", so §6.3 step 4's own check ("Confirm the API's log shows the embedding service initialised for `<model>` with the expected dimension") reads as a warning, not a confirmation.
- Step 5: `benchmark-query` registers `BenchmarkDocument` (`Iverson.LoadTest/Program.cs:88-89,151-163`). Registration resolves `defaultService = resolver.Get(null)` (`SchemaRegistrationOrchestrator.cs:81`) and awaits `service.EnsureInitializedAsync(ct)` at `:136`; the 404 becomes an `RpcException` at `:139-142` and `Program.cs:166-170` prints "Schema registration failed" (`:168`) and returns 1. The arm never runs.

The `Models` entry the design adds is unreachable for exactly the model the design wants to measure. Every M/N arm is blocked, so the gate cannot be reached at all.

**Evidence.**
- `Iverson.Server/Iverson.Embeddings/EmbeddingServiceResolver.cs:15-19` — `if (string.IsNullOrEmpty(modelId) || string.Equals(modelId, options.Value.ModelId, StringComparison.Ordinal)) return defaultService;`
- `Iverson.Server/Iverson.Embeddings/ServiceCollectionExtensions.cs:12-19,21` — the default `IEmbeddingService` is built from the single global `IOptions<EmbeddingServiceOptions>`; its only base-URL input is `opts.BaseUrl`.
- `Iverson.Server/docker-compose.yml:386` and `:481` — `Embeddings__BaseUrl=http://ollama:11434`, a literal on both api and worker; the spec's §5 compose row changes only `Models__0__*` and `Embeddings__ModelId`.
- `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:81,89,133-142` — `defaultService = resolver.Get(null)`, `nextModel = declared ?? defaultService!.ModelId`, `await service.EnsureInitializedAsync(ct)` inside a try that rethrows as `Unavailable`.
- `Iverson.Server/Iverson.Api/Program.cs:404-415` — startup init warns and continues rather than failing.
- Live: `curl -o /dev/null -w "%{http_code}" -X POST http://127.0.0.1:11434/v1/embeddings -d '{"model":"no-such-model","input":"probe"}'` → `404`; `curl http://127.0.0.1:11434/api/tags` → no bge-base/bge-small.

**Proposed fix (either, not both).**
1. *Minimal, Phase-1-only:* template the base URL alongside the model id — `Embeddings__BaseUrl=${EMBED_BASE_URL:-http://ollama:11434}` on api and worker — and set `EMBED_BASE_URL=http://tei-embed:80` in §6.3 step 4 next to `BENCH_EMBED_MODEL`. Note this makes the `Models`/`ModelEndpoint` machinery unused in Phase 1, which is worth stating explicitly if it is kept for Phase 2's sake.
2. *Keeps §3.1's design:* make the default service resolve its own base URL through `Models` too — i.e. `BaseUrl = Models.FirstOrDefault(m => m.Name == ModelId)?.BaseUrl ?? BaseUrl`, applied where the default `EmbeddingService` is constructed (`ServiceCollectionExtensions.cs:21`) as well as in `EmbeddingServiceResolver.Get`, and delete §3.1's "the default service keeps the global base URL" sentence. This also fixes the named `HttpClient`'s `BaseAddress` being derived from the global URL, which §3.1 already works around by building an absolute URI.

### 2.2 — `--chunk-max-chars` / `--chunk-step` cannot override the window the way §3.5 describes; one implementation silently ingests at 2,048, the other crashes on the reuse gate's assert

**Description.** §3.5 specifies the flags as "overriding `MAX_CHARS` / `STEP` for the run". Two facts in `ingest.py` make that description wrong in both possible readings:

1. `split_into_chunks`'s window comes from *default arguments*, which Python binds once at `def` time (`ingest.py:237`: `def split_into_chunks(text, max_chars=MAX_CHARS, step=STEP, word_boundary_lookback=WORD_BOUNDARY_LOOKBACK)`), and `ingest_document` calls it with no arguments (`:585`: `chunks = list(split_into_chunks(body))`). Rebinding the module globals in `main()` therefore changes nothing about how the corpus is chunked. The run would ingest at main's 2,048/1,792 while printing a 512 window in the new sidecar fields — silently non-comparable to `rerank-a0`, which §6.1 makes the whole point of the arm. Confirmed empirically: `G = 10; def f(x, k=G): return k; G = 99; print(f(0))` → `10`.
2. The reuse gate reads the module global at *call* time (`ingest.py:597`: `reuse = body == body.strip() and len(body) <= STEP`), and it is not one of `split_into_chunks`'s parameters. So if the flags are instead threaded as explicit arguments to `split_into_chunks`, `reuse` still uses 1,792 while chunking uses 448, and the very next line's assertion fires: `assert len(chunks) == 1 and chunks[0][0] == body, "reuse gate fired but split_into_chunks did not yield a single chunk equal to body"` (`:601-603`). Any SciFact abstract between 449 and 1,792 characters with no surrounding whitespace kills the ingest on the first such document.

`STEP` and `MAX_CHARS` are two operands of one window rule, and the spec's §11 B1 covers only where their *values* come from, not who reads them or when.

**Evidence.** `Iverson.Server/Iverson.LoadTest/scripts/ingest.py:208-210` (the constants), `:237` (def-time defaults), `:585` (no-arg call), `:597` (call-time global read), `:601-603` (the assert), `:479` (`split_into_chunks(case["text"])` inside `_verify_algorithm_goldens`, which must keep the contract values).

**Proposed fix.** Thread the effective window explicitly rather than rebinding globals: resolve `(max_chars, step)` from the flags in `main()`, pass the pair into `ingest_document`, and have `ingest_document` use it for **both** `split_into_chunks(body, max_chars=…, step=…)` and the reuse gate's `len(body) <= step`. `_verify_algorithm_goldens` keeps calling `split_into_chunks` with no arguments, which preserves §3.5's stated property that the goldens still run against contract values — that property is only true under this shape, not under a global rebind. Record `word_boundary_lookback` in the sidecar too, or state in the spec that it is not overridable because both windows agree on 50 (verified in B3's cross-check).

### 2.3 — N0's chunk window is not recorded in the artifacts §6.2 tells the operator to read

**Description.** §6.2 says: "N0's window must be read from its stats/`ingest.log` before N1 runs and matched with the override." Neither file carries it. The stats sidecar's schema is fixed by `update_stats_sidecar` (`ingest.py:670-677`) to `documents`, `chunks`, `embed_calls`, `embeds_saved`, `elapsed_seconds`, `started_at`, `finished_at` — the window fields are exactly what §3.5 is *adding*, so they cannot exist in a run from 2026-08-28. And `ingest.py` never prints the window: its `[ingest]` lines cover drop, probe, dimension, collection creation, prefix, progress and totals only. An operator following §6.2 finds nothing, and the natural fallback — main's committed default — is 2,048/1,792, which would make N1 non-comparable to N0 in exactly the way §6.1 rules out for SciFact.

**Evidence.**
- `cat nfcorpus-run-2026-08-27/keymap.json.stats.json` → `{"documents": 3633, "chunks": 14729, "embed_calls": 18327, "embeds_saved": 35, "elapsed_seconds": 23334.485313, "started_at": "2026-08-28T02:02:19.534722+00:00", "finished_at": "2026-08-28T08:31:14.020035+00:00"}` — no window key.
- `grep -in "chunk\|window\|512\|2048" nfcorpus-run-2026-08-27/ingest.log` → five lines, all collection names or the document/chunk totals; no window.
- `Iverson.Server/Iverson.LoadTest/scripts/ingest.py:670-677` — the sidecar's key set is closed.
- The window *is* recoverable, just not from where §6.2 points: `nfcorpus-run-2026-08-27/NEXT-STEPS.md:6` records `Branch: centroid-ablation @ ededce0 (512-char chunks + prefixes + titles)` and `:38` records `4.05 chunks/doc`, and `git show centroid-ablation:Iverson.Server/Iverson.LoadTest/scripts/ingest-contract.json` → `{'maxChars': 512, 'step': 448, 'wordBoundaryLookback': 50}`. 14,729 chunks / 3,633 documents = 4.05 corroborates a 512-character window rather than 2,048.

**Proposed fix.** Replace the stats/`ingest.log` instruction with the source that actually carries it: `nfcorpus-run-2026-08-27/NEXT-STEPS.md:6,38` naming the `centroid-ablation` branch, cross-checked against `git show centroid-ablation:…/ingest-contract.json` (512/448/50) and the 4.05 chunks/doc ratio — and state the resulting N1 flags (`--chunk-max-chars 512 --chunk-step 448`) in §6.2 rather than leaving them to be derived at run time. The forward-looking half is already handled by §3.5's new sidecar fields.

---

## 3. Forced decisions

No forced decisions found.

---

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 has three items, §3 is empty. §2.1 blocks every arm at schema registration and must be resolved before planning; §2.2 and §2.3 each silently produce a run at the wrong chunk window, which is the one variable §6.1 spends its whole argument controlling. §1 reconfirmed 33 of 34 assumptions; S4 failed (no existing test fake can route `/info` separately from `/v1/embeddings`), and the span check surfaced three uncovered dependencies, all of which became the §2 findings, plus two that were verified in-round and need no change.
