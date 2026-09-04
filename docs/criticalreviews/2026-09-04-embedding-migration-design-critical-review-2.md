# Critical Design Review: 2026-09-04-embedding-migration-design (Round 2)

**Spec:** docs/specs/2026-09-04-embedding-migration-design.md
**Spec (absolute):** `/home/ben/repositories/Iverson/docs/specs/2026-09-04-embedding-migration-design.md`
**Verified Assumptions section:** present (§11, E1–E7, S1–S12, C1–C3, B1–B12, H1)

Repo state: `main` @ `3f0960b` (HEAD), clean working tree. Every claim below is either a `file:line` read at
that commit or a command run in this session, quoted with its output. Nothing under
`~/repositories/iverson-benchmark-corpora/` was written, moved or deleted; the reads there are `cat`, `ls`
and `git show`. No container was started, stopped or recreated — the only live calls are read-only `curl`s
against the already-running `iverson-ollama` plus `docker ps` / `docker inspect`. **TEI was not re-probed**
(`curl --max-time 2 http://127.0.0.1:8091/info` → exit 7, nothing listening; starting a container is out of
bounds), so every TEI claim is checked against the spec's §10 rows plus already-recorded artifacts
(`TeiRerankClient.cs`, `rerank-a1.meta.json`), never a fresh probe.

The §0 enumeration below was built before reading round 1, per the skill's iterative rule.

---

## 0. Coverage enumeration

### Sections

| # | Section | Disposition |
|---|---|---|
| S1 | §1 Why | ok — re-ran the one repo/live-checkable claim: `curl http://127.0.0.1:11434/api/tags` → `['qwen2.5:3b','nomic-embed-text:latest','bge-m3:latest','mxbai-embed-large:latest','snowflake-arctic-embed:m','snowflake-arctic-embed:s','all-minilm:latest']`. No bge-base/bge-small, so "the backend change forces a model change" stands. The throughput and published-quality numbers are §10/memory ground truth |
| S2 | §2 Scope | ok — checked the exclusions against the code: client-model-as-string (5 clients), the registration guard (`SchemaRegistrationOrchestrator.cs:116-131`), `EnrichmentService` (own options + own named client, `ServiceCollectionExtensions.cs:26-42`), main's 2,048 window (`ingest-contract.json` → `{'maxChars': 2048, 'step': 1792, …}`). All genuinely untouched by §3 |
| S3 | §3.1 One client, per-model base URL | → §0 rules table (R1, R2); the CDR-1 default-service fix re-read at `ServiceCollectionExtensions.cs:12-21` and `EmbeddingServiceResolver.cs:15-19`. See §4.1 |
| S4 | §3.2 Model identity guard | → **§2.1** |
| S5 | §3.3 Prefix table | ok — `EmbeddingPrefixes.cs:29-34` is `Table` (spec cites `:26-31`; content matches), `Family` at `:36-40` splits on the first `':'`. Neither `BAAI/bge-base-en-v1.5` nor `BAAI/bge-small-en-v1.5` contains `':'`, so each is its own family and the strict tag loop in `ingest.py:732-733` (`verify_contract(f"{fam}:probe-tag", require_known_family=True)`) still round-trips. `IngestContractTests.cs:117-127` emits one `documentPrefixes` entry AND one `golden.documentComposition` case per `Table` row, so both new rows are covered by the regenerated contract; `'/'` is not escaped by `System.Text.Json`'s default encoder and is a legal JSON key |
| S6 | §3.4 Compose | ok — `docker-compose.yml:153-165` is the `reranker` template the new service copies (profile, templated `--model-id`, `8090:80`, named volume `reranker_models` declared at `:548`). `Embeddings__ModelId` literals at `:398` (api) / `:481` (worker); `VectorRanking__Lambda=${VECTOR_RANKING_LAMBDA:-0.70}` at `:388` is the templating precedent. The api-side comment at `:391-397` warns the value must stay `nomic-embed-text` for the conformance fixtures — `${BENCH_EMBED_MODEL:-nomic-embed-text}` preserves the default, so the warning is honoured |
| S7 | §3.5 Benchmark ingest script | ok — all four cited anchors re-read: `embed()` at `ingest.py:530`, `split_into_chunks(body)` at `:585`, `reuse = … len(body) <= STEP` at `:597`, the assert at `:601-603`, `_verify_algorithm_goldens`'s no-arg call at `:479`. `OLLAMA_URL` (`:147`) is read inside `embed()`'s body at call time, so `--embed-url` needs no def-time care (unlike `MAX_CHARS`/`STEP`) |
| S8 | §4 Failure semantics | → **§2.1** (the "Identity mismatch: throws" bullet) |
| S9 | §5 Component contract | ok — every row maps to a file that exists and to a change §3 describes; the CDR-1-added `ServiceCollectionExtensions.cs` row is present. "Untouched: … all five clients" checked by `grep -rn "api/embed"` → production hits only in `EmbeddingService.cs:66,77` and `ingest.py:531,554` |
| S10 | §6.1 Baseline and window | ok — `scifact-run-2026-08-26/runs/rerank-a0.chunks.trec` and `.meta.json` exist; composite `31583db5aea49136`, matching `rerank-a0prime`/`rerank-a0-2026-09-04`/`rerank-a1`. `git show chunk-size-512-experiment:…/ingest-contract.json` → `{'maxChars': 512, 'step': 448, 'wordBoundaryLookback': 50}`; `git log --oneline -1 ce7bf12` → "ship triple B (0.45/0.45/0.10) for the fusion weights". The post-change build-composite consequence is a dropped candidate — see the rules table, R5 |
| S11 | §6.2 Arms | ok — the CDR-1 rewrite verified end to end: `nfcorpus-run-2026-08-27/NEXT-STEPS.md:6` = "Branch: centroid-ablation @ ededce0 (512-char chunks + prefixes + titles)", `:38` = "4.05 chunks/doc, 14,729 chunks, 18,327 embed calls"; `git show centroid-ablation:…/ingest-contract.json` → `{'maxChars': 512, 'step': 448, 'wordBoundaryLookback': 50}`; the sidecar gives 14,729 / 3,633 = 4.054. SciFact ingest figures match `keymap.json.stats.json` (`elapsed_seconds 31070.187319`, `embed_calls 25128`) |
| S12 | §6.3 Per-arm procedure | ok — each step checked; see arrows A4–A7. Step 4's `--no-deps` note re-verified live: `docker inspect` shows all six running containers carry `com.docker.compose.project=iversonserver` and sit on `iversonserver_default`, even though their `project.working_dir` labels name three different checkouts (`.worktrees/reranker-phase1`, `Iverson-tenant`, `Iverson.Server`), so a recreate from this checkout rejoins the same network. `DELETE FROM _iverson_schema WHERE type_name = …` matches the real DDL (`SchemaRegistryRepository.cs:9,17-19`) |
| S13 | §6.4 Statistics | ok — `report.py:533` `run_paired_statistics`; `check_pool` (`:637`) is reachable only from the `--pair` path (`:677`), and `--pair`/`--baseline` are mutually exclusive (`:770-771`); `print_compare_block` prints delta (`:504`), 95 % CI (`:511`), MDE (`:513`) and Holm (`:523`). The baseline-exclusion-by-abspath at `:544-551` is a no-op here (baseline and runs live in different directories) |
| S14 | §7 Gate | ok — the two quantities the gate reads (95 % CI lower bound on nDCG@10 and on R@50) are both printed per measure, and `measures = [nDCG@10, R@50, AP]` at `report.py:763` |
| S15 | §8 Phase 2 outline | → rules table R6 (dropped) |
| S16 | §9 Testing | → **§2.2** |
| S17 | §10 Measurements | ok — the two Ollama rows re-run live this round (`/v1/embeddings` → keys `['object','data','model','usage']`, 768 dims; unknown model → 404; `GET /info` → 404). TEI rows carried as ground truth, not re-probed |
| S18 | §11 Verified assumptions | → §1 |
| S19 | §12 Known issues | ok — all four bullets restate rulings already made (§6.1 window, §3.4 truncation, the published-delta caveat, the throughput-by-product caveat); none introduces a new claim |

### Rules and operands

| # | Rule | Disposition |
|---|---|---|
| R1 | Per-model base-URL lookup: `Models.FirstOrDefault(m => m.Name == ModelId)?.BaseUrl ?? BaseUrl` | ok both directions. Over-match: `Name` is compared with C# `string ==` (ordinal), matching `EmbeddingServiceResolver.cs:13`'s `StringComparer.Ordinal` dictionary and `:18`'s `StringComparison.Ordinal` short-circuit — one consistent identity rule, no case-folding seam. Under-match: an entry absent → `?? BaseUrl` → Ollama → 404 at the first embed, which is §4's stated loud path (`curl -X POST …/v1/embeddings -d '{"model":"no-such-model",…}'` → 404, re-run live) |
| R2 | `EmbeddingServiceResolver.Get` short-circuit for the default model id | ok — `:17-19` returns `defaultService` for null/empty/equal-to-default. This is the exact operand CDR-1's §2.1 was about; the applied §3.1 text now names it and routes the default service through `Models` too. See §4.1 |
| R3 | Prefix family key (identity/exclusion rule) | ok — over-merge: `Family` returns the whole id when there is no `':'`, so the two bge ids cannot collide with each other or with `nomic-embed-text` / `snowflake-arctic-embed`. Under-merge: `BAAI/bge-base-en-v1.5:some-tag` would still resolve to the bge family. The two new rows share a *value* with the arctic row but not a key, which the table's own `Dictionary` shape permits |
| R4 | Identity guard equality: `/info.model_id` vs configured `ModelId`, ordinal | mechanics are sound (TEI reports `BAAI/bge-base-en-v1.5` for `--model-id BAAI/bge-base-en-v1.5`, §10 row 6), but **when** it can fire is not → **§2.1** |
| R5 | Build-identity comparison in `report.py` | dropped — candidate was "every gate compare block will print `!! BUILD MISMATCH`, because Phase 1 changes `Iverson.Embeddings`, one of the seven assemblies in the composite (`rerank-a0.meta.json`), while §6.1 asserts the baseline was 'produced on today's server build'". Read `report.py:486-497`: the mismatch path only `print`s two lines; there is no `sys.exit` on it anywhere (`grep -n "sys.exit" report.py` → 16 hits, none in `print_compare_block` or `run_paired_statistics` except the "excludes every discovered run" guard at `:551`). Delta/CI/MDE/Holm all still compute, the gate's two quantities are unaffected, and the scoring path (`Iverson.Api`/`Iverson.Vector`) is untouched by §5. Fails the literal-wrongness test — the asked-for verdict is still produced |
| R6 | Phase-2 reachability of a TEI service under the chart | dropped — the chart's api and worker egress NetworkPolicies enumerate allowed destinations explicitly and include ollama:11434 only (`deploy/helm/iverson/templates/networkpolicies.yaml:54-55` api, `:85-86` worker, plus the ollama ingress policy at `:394-402`), so a `tei` subchart would also need egress/ingress rules — which §8 does not list and S12 does not enumerate. Dropped: §2 and §8 both state Phase 2 is an outline "planned only if the gate passes", i.e. a later design round owns it; nothing in Phase 1's asked-for behavior (compose only) breaks |
| R7 | Chunk-budget refusal operand pair (`documents`, `chunks` from the sidecar) | ok — `BenchmarkQueryScenario.cs:101-140`: `ChunkBudgetGuard.Evaluate` at `:113` — `ChunkBudgetGuard.Evaluate(stats.Documents, stats.Chunks, 50, 5)`. Chunking is model-independent and every arm reuses the 512/448 window, so M1/M2 reproduce SciFact's 19,967/5,183 = 3.85 chunks/doc (250/3.85 ≈ 65 reachable ≥ 50) and N1 reproduces NFCorpus's 4.05 (≈ 62 ≥ 50). No arm trips the refusal |
| R8 | `verify_contract`'s permissive/strict family predicate against the two new producers | ok — `ingest.py:732-733` iterates `CONTRACT["embedding"]["documentPrefixes"]` (not a hardcoded list), so both bge families are exercised with `require_known_family=True` automatically once the contract is regenerated; `golden.documentComposition` gains a case per family from the same `Table` loop (`IngestContractTests.cs:121-127`), so `golden.get(fam, golden["__default__"])` at `ingest.py:452` finds a real case rather than falling back |

### Data-flow arrows

| # | Arrow (ends at an operation) | Disposition |
|---|---|---|
| A1 | compose env → `EmbeddingServiceOptions` binding → `EmbeddingService.EmbedAsync`'s URI construction | ok — `services.Configure<…>(config.GetSection("Embeddings"))` at `ServiceCollectionExtensions.cs:10`, `Section = "Embeddings"` at `EmbeddingServiceOptions.cs:5`; indexed-list binding is §10 row 11 ground truth. The absolute request URI overrides the named client's `BaseAddress` (`:18`), which stays the global URL — consistent with §3.1's "the named client remains for handler and telemetry configuration" |
| A2 | `EnsureInitializedAsync` → `/info` guard → `SchemaBuilder.BuildDescriptor(typeDesc, service)`'s `service.Dimension` read | → **§2.1**. The parameter the consuming operation needs (`Dimension`) is committed at `EmbeddingService.cs:43`, *before* the guard the spec places "after the probe" |
| A3 | `EmbeddingPrefixes.Table` → `IngestContractTests` emit → **file** `ingest-contract.json` → `ingest.py` import-time load (persistence boundary) | ok — dumped the committed file's real key set: `chunkWindow`, `distance`, `collectionNaming`, `embedding.{documentPrefixes,defaultDocumentPrefix}`, `golden.{chunking,pointIds,centroid,documentComposition}`. `ingest.py:142-143` loads it, `:415-416` reads `embedding.documentPrefixes`, `:208-210` reads `chunkWindow`. Query prefixes are deliberately not emitted (`IngestContractTests.cs` class doc), and the bge query instruction is only ever needed C#-side, so no Python consumer is left without a parameter |
| A4 | `ingest.py` args → **file** `<key-map>.stats.json` → `benchmark-query`'s `ChunkBudgetGuard.Evaluate` (call site 1) | ok — `BenchmarkQueryScenario.cs:57` `record IngestStats(int Documents, int Chunks)` with `PropertyNameCaseInsensitive = true` (`:51-53`); `JsonSerializer` ignores the four new keys. Guard arithmetic covered by R7 |
| A5 | same file → `report.py`'s `print_stats` (call site 2 — different consumer, different parameter list) | ok — read `report.py:319-365`: every field is `stats.get(<key>[, default])`; the four added keys are never enumerated and never break the `elapsed_seconds`-vs-span fallback |
| A6 | `ingest.py` `--embed-url`/`--model` → `embed()` → Qdrant `ensure_collection` vector size | ok — `main()` derives `dimension = len(embed("dimension probe", args.model, ""))` at `:754` *before* `--drop` acts, then builds both collections at that size (`:769-778`). For M2 this is 384 and `--drop` recreates both collections, so the 768→384 transition needs no extra step |
| A7 | Qdrant points (written by `ingest.py`) → API query path → `resolver.Get(SchemaDescriptor.ModelOf(schema)).EmbedQueryAsync` | ok — `ObjectSearchGrpcService.cs:206` and `:387`. Under a benchmark arm the registered id equals `Embeddings__ModelId`, so `Get` returns the default service (R2) and the query is embedded by the same service/URL/prefix pair the identity guard covers. Vector names (`body_vector`, `body_centroid`) carry no model id (`SchemaBuilder.cs:341-351`), so a model change never renames a field |
| A8 | Existing test fakes → `EnsureInitializedAsync`'s request stream | → **§2.2**. S4 covers routing; the *count* of requests is a separate parameter three committed assertions read |

---

## 1. Verified-assumptions cross-check

| # | Verdict |
|---|---|
| E1 | **Still holds.** Re-run live this round: `POST http://127.0.0.1:11434/v1/embeddings {"model":"nomic-embed-text","input":"probe"}` → keys `['object','data','model','usage']`, `len(data[0].embedding) == 768`; `{"model":"no-such-model"}` → `404` |
| E2 | **Still holds** (§10 rows 6–7, ground truth; TEI not re-probed — nothing listening on 8091) |
| E3 | **Still holds.** Ollama half re-run live: `GET http://127.0.0.1:11434/info` → `404`. TEI half corroborated without a probe by `Iverson.LoadTest/Benchmark/TeiRerankClient.cs:27-29` and `rerank-a1.meta.json` |
| E4 | **Still holds** (§10 rows 6, 9, 10; TEI not re-probed) |
| E5 | **Still holds.** `GET /api/tags` re-run live: seven models, none of them bge-base or bge-small (bge-m3 is a different model) |
| E6 | **Still holds** (§10 row 5, ground truth) |
| E7 | **Still holds** (added by CDR-1). Re-read the cited evidence: `rerank-a1.meta.json` preserves a real `cpu-1.8` response as `"reranker": {"baseUrl": "http://127.0.0.1:8090", "modelId": "cross-encoder/ms-marco-MiniLM-L-6-v2", "maxInputLength": 512, "autoTruncate": true}`, so `/info` carries all three fields §6.3 step 1 asserts on |
| S1 | **Still holds.** `EmbeddingService.cs:66` is the only `HttpRequestMessage` construction in the service; `ServiceCollectionExtensions.cs:18` is the only `BaseAddress` assignment for the embedding client. Cited `:15-21` is `:14-19` at this commit — content matches |
| S2 | **Still holds.** `ServiceCollectionExtensions.cs:10` (spec cites `:11`); `EmbeddingServiceOptions.cs:5` `Section = "Embeddings"`. Indexed-list binding is §10 row 11 |
| S3 | **Still holds as scoped.** `grep -rn "new EmbeddingService(" --include=*.cs` (worktrees excluded) → `EmbeddingServiceResolver.cs:21`, plus four test sites (`EmbeddingServiceTests.cs:44,57`, `EmbeddingServiceResolverTests.cs:100,124`). Production-wise the resolver is still the only site. Note for the plan, not a finding: §3.1's fix says the default service's lookup is "applied where the default `EmbeddingService` is constructed (`ServiceCollectionExtensions.cs:21`)", and `:21` is today `services.AddSingleton<IEmbeddingService, EmbeddingService>();` — a plain DI registration with no factory lambda, so applying it there necessarily introduces a second production construction site |
| S4 | **Still holds** (rewritten by CDR-1). Re-read all three fakes: `FakeHttpMessageHandler` (`EmbeddingServiceTests.cs:14-28`) returns the *same* `HttpResponseMessage` instance for every `SendAsync` and branches on nothing; `CountingHttpMessageHandler` (`:183-199`) builds a fresh response per call but still ignores the URI; `RecordingHttpMessageHandler` (`EmbeddingServiceResolverTests.cs:13-25`) is single-response and records only `LastRequestBody`. The assumption's claim is now accurate. What it does not cover is in §2.2 |
| S5 | **Still holds.** `grep -rn "api/embed"` over `.cs/.py/.yml/.yaml/.ts/.go/.java` (worktrees and `docs/` excluded) → `EmbeddingService.cs:66,77` and `ingest.py:531,554` only. Observation, not a failure: the Ollama *response shape* is additionally hard-coded in two test fixtures (`EmbeddingServiceTests.cs:67`, `EmbeddingServiceResolverTests.cs:32`, both `{"embeddings":[[…]]}`) — `grep -rn '"embeddings"'` finds no third. Both live in files §9 already puts in scope |
| S6 | **Still holds.** `EmbeddingService.cs:38` `WaitAsync`, `:41` double-check, `:42` probe, `:50` release in `finally`. The guard *can* follow the probe inside the lock, as stated. What follows from *where* exactly it lands is §2.1 |
| S7 | **Still holds.** `EmbeddingPrefixes.cs:36-40` (`IndexOf(':')`, `< 0` → whole id), `:30` `StringComparer.Ordinal`. Spec cites `:33-40`; `Table` is `:29-34`, `Family` `:36-40` |
| S8 | **Still holds.** `IngestContractTests.cs:80-105` writes under `IVERSON_REGENERATE_INGEST_CONTRACT=1` and otherwise diffs; `ingest.py:142-143` loads at import, `:415-416` reads `embedding.documentPrefixes` |
| S9 | **Still holds.** `SchemaBuilder.cs:339-352` — vector names are `$"{PropertyName.ToSnakeCase()}_vector"`/`_centroid`; no model id reaches a name |
| S10 | **Still holds.** `SchemaRegistrationOrchestrator.cs:774-781` `ValidateIdentifier`, called only at `:74-76` on the type name and property names |
| S11 | **Still holds.** `EnrichmentServiceOptions.cs:6-7` has its own `Section`/`BaseUrl`; `AddEnrichment` (`ServiceCollectionExtensions.cs:26-42`) registers a separate named client `iverson.ollama.enrichment` (`Telemetry.cs:9`) |
| S12 | **Still holds as scoped.** Re-confirmed each cite: `docker-compose.yml:386`/`:481` (`Embeddings__BaseUrl=http://ollama:11434`), `charts/api/templates/deployment.yaml:124`, `charts/worker/templates/deployment.yaml:119`, `Iverson.Launcher/Program.cs:28` (`WaitForOllamaAsync("http://localhost:11434/api/tags", "nomic-embed-text", …)`), `EmbeddingServiceOptions.cs:6`. The charts live under `Iverson.Server/deploy/helm/iverson/`, not repo-root `deploy/` — paths in S12/H1/§8 are chart-relative. See the span check for the reachability surface this URL enumeration does not cover |
| C1 | **Still holds.** `docker-compose.yml:153-165` is the `reranker` service; the worker env block spans `:474-492` and is a plain list that accepts new entries |
| C2 | **Still holds.** `docker-compose.yml:388` |
| C3 | **Still holds.** `docker-compose.yml:539-548` declares `postgres_data`, `qdrant_data`, `ollama_data`, `reranker_models` and the rest as named volumes |
| B1 | **Still holds.** `OLLAMA_URL` at `:147` (read inside `embed()`'s body, `:531`), window constants at `:208-210`, `embed()` at `:530-556`, `--model`/`--drop` at `:709-720`, sidecar writer at `:656-682` |
| B2 | **Still holds.** `ingest.py:406` `family()`, `:414-416` `document_prefix_for` — same first-`':'` split and same case-sensitive lookup as C# |
| B3 | **Still holds.** `git show chunk-size-512-experiment:…/ingest-contract.json` → `{'maxChars': 512, 'step': 448, 'wordBoundaryLookback': 50}`; `git show de4b6bf:…/ingest.py` → `MAX_CHARS = 2048` / `STEP = 1792`; committed contract on main → `{'maxChars': 2048, 'step': 1792, 'wordBoundaryLookback': 50}`; `ce7bf12` = "ship triple B (0.45/0.45/0.10) for the fusion weights" |
| B4 | **Still holds.** `Entities/BenchmarkDocument.cs` carries bare `[IversonEmbedding]`/`[IversonChunk]`; the `DELETE FROM _iverson_schema` remedy is the guard's own (`SchemaRegistrationOrchestrator.cs:116-131`), and `type_name` is the real column (`SchemaRegistryRepository.cs:9,17-19`) |
| B5 | **Still holds.** `ObjectSearchGrpcService.cs:206` and `:387`, both `resolver.Get(SchemaDescriptor.ModelOf(schema)).EmbedQueryAsync(...)` |
| B6 | **Still holds.** `report.py:533` `run_paired_statistics`; `check_pool` at `:637` reachable only from `--pair` (`:677`), and the two flags are mutually exclusive (`:770-771`) |
| B7 | **Still holds** by construction — `--run`/`--baseline` take trec paths and `ir_measures.read_trec_run` never sees a vector |
| B8 | **Still holds.** SciFact sidecar → `elapsed_seconds 31070.187319`, `embed_calls 25128`, `chunks 19967`, `documents 5183`; NFCorpus sidecar → `elapsed_seconds 23334.485313`, `documents 3633`, `chunks 14729` |
| B9 | **Still holds.** `scifact-512-qdrant-snapshots/` holds `RESTORE.md` plus two `.snapshot` files (135 MB / 98 MB); `RESTORE.md:12-16` is the upload loop and its verification line states 5,183 / 19,967 |
| B10 | **Still holds.** `ls nfcorpus-run-2026-08-27` → `NEXT-STEPS.md beir gate.log ingest-started-at.txt ingest.log keymap.json keymap.json.progress keymap.json.stats.json paired-chunks.txt paired-similar.txt qrels.trec report.txt runs sweep.log` |
| B11 | **Still holds.** `BenchmarkQueryScenario.cs:57` `record IngestStats(int Documents, int Chunks)`, `:51-53` `PropertyNameCaseInsensitive = true`, read at `:99-113` |
| B12 | **Still holds** (added by CDR-1). `report.py:319-365` — every sidecar access is `stats.get(<key>)`; the four new keys are inert |
| H1 | **Still holds.** `Chart.yaml:24-27` (`name: ollama` … `condition: ollama.enabled`); `charts/ollama/templates/{statefulset,service}.yaml` both exist |

**Span check — design dependencies with no covering assumption:**

- **What `EnsureInitializedAsync` has already committed by the time the guard runs, and what the guard's throw meets at its only two call sites.** S6 covers only that the probe is inside the lock and the guard can follow it there. → **§2.1**.
- **The number of HTTP requests `EnsureInitializedAsync` issues.** S4 covers the fakes' inability to *route* two paths; nothing covers the three committed assertions that pin the *count*. → **§2.2**.
- **The build composite's survival across the Phase 1 change**, which §6.1's "produced on today's server build" leans on. **Verified in-round**: `report.py:486-497` prints `!! BUILD MISMATCH` and continues — no `sys.exit` on that path, delta/CI/MDE/Holm unaffected, and §5 leaves the scoring assemblies untouched. Covered — no finding (recorded as R5).
- **Reachability of a non-Ollama embedding backend under the Helm chart's default-deny egress**, which S12's URL enumeration does not reach. **Verified in-round**: `networkpolicies.yaml:54-55,85-86,394-402`. Phase 2 only, and §2/§8 both defer Phase 2 to a later design round. Covered — no Phase 1 finding (recorded as R6).
- **Whether the `--no-deps` recreate in §6.3 step 4 lands on the same Docker network as the running dependencies.** **Verified in-round**: `docker inspect` → all six containers share `com.docker.compose.project=iversonserver` and network `iversonserver_default` despite three different `working_dir` labels, because Compose derives the project name from the `Iverson.Server` directory name. Covered — no finding.

---

## 2. Literal-wrongness findings

### 2.1 — The identity guard placed "after the probe" fires at most once and is then permanently bypassed, so a misrouted TEI model survives into the arm

**Description.** §3.2 places the guard "after the probe and inside the same lock"; §5's contract row repeats "`/info` identity guard after the probe"; S6 says the guard "can follow it there". But the probe block does not end at the HTTP call — `EnsureInitializedAsync` assigns `_dimension = probe.Length` at `EmbeddingService.cs:43` and logs "EmbeddingService initialized" at `:44-46`, and the method's first statement is `if (_dimension > 0) return;` (`:36`). A guard placed after that block therefore throws *from an already-initialised service*, and the throw is not repeatable.

That interacts with the only two production callers, neither of which the spec accounts for:

- **Startup** (`Iverson.Api/Program.cs:404-415`) wraps `EnsureInitializedAsync()` in a `try`/`catch (Exception)` that logs a warning — "Embedding service not initialized at startup; will initialize on first schema registration" — and continues, deliberately, so a still-pulling Ollama does not crash-loop the pod. The identity mismatch becomes that warning.
- **Schema registration** (`SchemaRegistrationOrchestrator.cs:133-142`) is the retry the warning promises, and it is the automatic gate: its `catch` turns an init failure into `RpcException(Unavailable)`, which is what stops an arm. But with `_dimension` already `768`, `EnsureInitializedAsync` returns at `:36` before reaching the guard, so registration succeeds, `SchemaBuilder.BuildDescriptor(typeDesc, service)` reads `service.Dimension` and writes a schema row naming the *configured* model, and every subsequent `EmbedQueryAsync` (`ObjectSearchGrpcService.cs:206,387`) runs against the *served* one.

So §4's "Identity mismatch: `EnsureInitializedAsync` throws naming the configured and served ids" is true exactly once, into a swallowed warning, and the design's own fail-loud contract does not hold. The failure this leaves open is the one §3.2 exists for: with TEI ignoring the `model` field (E2/§10 row 7), the M1→M2 transition — where §6.3 step 1 re-creates `tei-embed` with `EMBED_MODEL_ID=BAAI/bge-small-en-v1.5` and step 4 re-creates the API with `BENCH_EMBED_MODEL=BAAI/bge-small-en-v1.5` — silently produces a full 768-dim bge-base arm labelled bge-small if step 1 did not take, and the dimension probe cannot object because it has no expected value to compare against. §6.3 step 4's "Confirm the API's log shows … no identity mismatch" is a human backstop, not the stated mechanism, and it is reading a log that will *also* say "EmbeddingService initialized: model=BAAI/bge-small-en-v1.5 dimension=768".

**Evidence.**
- `Iverson.Server/Iverson.Embeddings/EmbeddingService.cs:36` — `if (_dimension > 0) return;`
- `Iverson.Server/Iverson.Embeddings/EmbeddingService.cs:42-46` — `var probe = await EmbedAsync("probe", ct); _dimension = probe.Length;` then the "EmbeddingService initialized" log; the lock releases in `finally` at `:48-51`.
- `Iverson.Server/Iverson.Api/Program.cs:404-415` — `try { await …EnsureInitializedAsync(); } catch (Exception ex) { … app.Logger.LogWarning(ex, "Embedding service not initialized at startup; will initialize on first schema registration."); }`
- `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:132-142` — `var service = defaultService ?? resolver.Get(declared); try { await service.EnsureInitializedAsync(ct); } catch (Exception ex) { throw new RpcException(new Status(StatusCode.Unavailable, …)); }`
- `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:206,387` — the query path calls `EmbedQueryAsync` without any initialisation call of its own.
- §10 row 7 (ground truth): TEI accepts `model: nomic-embed-text` against a bge-base container and returns bge-base vectors.

**Proposed fix.** State the ordering explicitly rather than leaving "after the probe" to be read: run `/info` between the probe call and the `_dimension` assignment, so a mismatch leaves the service uninitialised and *every* later `EnsureInitializedAsync` re-throws — which turns the startup warning into a registration-time `Unavailable` and blocks the arm automatically. (Equivalently, record a sticky mismatch state that `:36` checks before its early return.) Amend §3.2, §5's contract row and S6 to say "before the dimension is recorded" instead of "after the probe", and add the corresponding §9 test: a mismatching `/info` must make a *second* `EnsureInitializedAsync` throw too, not just the first — that is the assertion that separates the two placements.

### 2.2 — §9's test-side enumeration omits three committed assertions that count `EnsureInitializedAsync`'s HTTP requests; adding the `/info` call turns them red

**Description.** §9 states the whole test-side change as: `FakeHttpMessageHandler` "returns one response for every request today; it gains a branch on `request.RequestUri.AbsolutePath` … and builds a fresh `HttpResponseMessage` per call". S4 backs that up for *routing*. Neither covers request *count*: §3.2 makes a single `EnsureInitializedAsync` issue two HTTP requests instead of one, and three committed tests assert exact counts over handlers that count every request regardless of path.

- `EnsureInitializedAsync_CalledTwice_ProbesOnlyOnce` asserts `handler.CallCount.Should().Be(1)` (`:210`) → becomes 2.
- `EnsureInitializedAsync_ConcurrentCallers_ProbeOnlyOnce` asserts `handler.CallCount.Should().Be(1)` (`:222`) → becomes 2.
- `EnsureInitializedAsync_FailingProbe_ThrowsButLeavesServiceUsableForLaterSuccess` asserts `handler.CallCount.Should().Be(2)` (`:256`) → becomes 3: `FlakyThenSuccessHandler` 500s on call 1, so the sequence is failed probe → probe → `/info`.

Both handlers reply with the embeddings body to `/info` as well, so the guard itself no-ops (200 without `model_id`, per §3.2) — the only thing that changes is the count, and the count is what these three assertions pin. `Iverson.Embeddings.Tests` is in `Iverson.slnx`, so the suite is red until they are updated, and §9's list says the change is confined to one fake.

**Evidence.**
- `Iverson.Server/Iverson.Embeddings.Tests/EmbeddingServiceTests.cs:183-199` — `CountingHttpMessageHandler`: `Interlocked.Increment(ref CallCount);` at `:191` with no branch on `request.RequestUri`.
- Same file `:202-212`, `:214-224` — the two `CallCount.Should().Be(1)` assertions at `:210` and `:222`.
- Same file `:226-259` — `FlakyThenSuccessHandler` (`Interlocked.Increment` at `:234`, 500 on call 1 at `:235-236`) and `handler.CallCount.Should().Be(2)` at `:256`.
- `Iverson.Server/Iverson.Embeddings/EmbeddingService.cs:34-52` — today's `EnsureInitializedAsync` issues exactly one request (`:42`).

**Proposed fix.** Extend §9's bullet: the two counting handlers count only `/v1/embeddings` (branch on `request.RequestUri!.AbsolutePath`, as the same bullet already prescribes for `FakeHttpMessageHandler`), which keeps all three assertions at their current values and keeps them meaningful as "probe once per lifetime" rather than "one HTTP call per lifetime". Note that raising the literals to 2/2/3 instead would make `ProbesOnlyOnce` stop falsifying a double-probe, so the branch is the version that survives the §9 mutation requirement. `EmbeddingServiceResolverTests`' `SuccessResponse` (`:27-34`) also needs the `data[0].embedding` shape — §9's resolver bullet currently mentions only base-URL routing.

---

## 3. Forced decisions

No forced decisions found.

---

## 4. Previously addressed

- **CDR-1 §2.1** (the arm makes a TEI-only model the API default while the default service is nailed to the global Ollama URL) — **resolved.** §3.1 now reads "The default service resolves its base URL the same way — `Models.FirstOrDefault(m => m.Name == ModelId)?.BaseUrl ?? BaseUrl` — applied where the default `EmbeddingService` is constructed (`ServiceCollectionExtensions.cs:21`) as well as in `EmbeddingServiceResolver.Get`", and names the short-circuit at `EmbeddingServiceResolver.cs:15-19` as the reason. §5 gained the matching `ServiceCollectionExtensions.cs` contract row. Option 2 of the two CDR-1 offered, applied whole; the `Models`/`ModelEndpoint` machinery is now load-bearing in Phase 1 rather than dead. One consequence noted in §1 S3 (`:21` is a plain `AddSingleton`, so this adds a second production construction site) — a plan-level detail, not a residual finding.
- **CDR-1 §2.2** (`--chunk-max-chars`/`--chunk-step` could not override the window as described) — **resolved.** §3.5 now specifies threading the pair through `main()` → `ingest_document` → *both* `split_into_chunks(body, max_chars=…, step=…)` (`:585`) and the reuse gate `len(body) <= step` (`:597`), explicitly rejects rebinding `MAX_CHARS`/`STEP` with the def-time-default reason (`:237`) and the assert-at-`:601` reason, keeps `_verify_algorithm_goldens` (`:479`) on the contract values, and records that `word_boundary_lookback` is not overridable because both windows use 50. Re-read all five anchors at `3f0960b`; each is where the spec says it is.
- **CDR-1 §2.3** (N0's chunk window is not in the artifacts §6.2 pointed at) — **resolved.** §6.2 now states the window is absent from the sidecar and `ingest.log`, sources it from `NEXT-STEPS.md:6,38` → `centroid-ablation @ ededce0` → `git show centroid-ablation:…/ingest-contract.json` (512/448/50), corroborates with 14,729 / 3,633 = 4.05, and fixes N0/N1's window column at 512 with the explicit `--chunk-max-chars 512 --chunk-step 448` flags. All three legs re-verified this round.
- **CDR-1 §1, S4 failed** — **resolved.** S4 was rewritten from "Tests use a fake handler that can assert path/body and answer `/info`" to an accurate statement that all three existing fakes are single-response and cannot route the two paths, with the three fake declarations cited. §9 gained the matching branch-and-fresh-response bullet. What the rewrite does not reach is §2.2 above.
- **CDR-1 span check** — the two dependencies it verified in-round were promoted into the assumptions table as **E7** (`/info` also carries `max_input_length`/`auto_truncate`) and **B12** (`report.py` reads the stats sidecar through `stats.get`). Both re-read this round and both hold.

---

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 has two items, §3 is empty. §2.1 is the more serious: as placed, the identity guard cannot stop an arm, and the arm it cannot stop (a mislabelled bge-small/bge-base run) is undetectable by the dimension probe and would cost a full ingest plus a wrong gate number. §2.2 is a one-line correction to §9 that keeps the existing suite green and its probe-once assertions falsifiable. §1 reconfirmed all 35 assumptions on a fresh read, including the five CDR-1 touched; the span check surfaced five uncovered dependencies, two of which became the §2 findings and three of which were verified in-round and need no change.
