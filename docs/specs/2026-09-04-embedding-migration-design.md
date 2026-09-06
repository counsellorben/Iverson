# Embedding migration to TEI — design (Phase 1: measure, gated)

Status: design, verified against the codebase and the live stack 2026-09-04.
Predecessors: `docs/specs/2026-09-01-helm-embedding-model-configuration-design.md` (per-model configuration),
`docs/specs/2026-09-03-reranker-design.md` (closed; its §7 protocol and `report.py` are reused here).
Motivating measurements: memory `project-tei-vs-ollama-throughput` (2026-09-03).

## 1. Why

Today's production embedding path is Ollama serving `nomic-embed-text` (768 dims) through its native
`/api/embed` route, at a measured 1,130 ms per text under sustained load — 0.88 texts/s, the bottleneck
of every ingest. Text-Embeddings-Inference (TEI) serving `BAAI/bge-base-en-v1.5` measured 1.57× faster at
the same 768 dims, and `bge-small-en-v1.5` (384 dims) 4.5× faster. The published SciFact quality of
bge-base (0.743) exceeds nomic's (0.705), but **published deltas have not transferred on this stack
before** (`project-nomic-embedding-task-prefixes`: a predicted +2.3 points measured +0.0134, n.s.), so
the migration is gated on a measurement here. TEI cannot serve nomic at all (its `config.json` fails to
parse on TEI 1.8.3 and 1.9.3), so the backend change forces a model change, which forces a re-ingest.

Two questions, answered by one protocol: does bge-base hold nomic's quality on this stack, and what
does the 384-dim speed lever cost.

## 2. Scope

**Phase 1 (this spec's build):** the smallest server, compose and harness change that lets the benchmark
run a TEI-served model end to end, then three arms (SciFact bge-base, SciFact bge-small, NFCorpus
bge-base) against the existing nomic runs, and a gate verdict.

**Phase 2 (outlined in §8, planned only if the gate passes):** Helm subchart, default-model switch,
readiness gates, and the migration procedure for already-registered types.

Not in scope: client-library changes (a type declares a model id as a string in all five clients
already), the schema-registration guard, the enrichment path (stays on Ollama), batching, main's
2,048-character chunk window (see §6.1).

## 3. Design

### 3.1 One client, per-model base URL

`EmbeddingService.EmbedAsync` (`EmbeddingService.cs:56-95`) posts to the OpenAI-compatible route
`/v1/embeddings` with `{"model": ModelId, "input": text}` and reads `data[0].embedding`, instead of
Ollama's `/api/embed` and `embeddings[0]`. Both backends serve that route: Ollama's returns vectors
identical to its native route (cosine 1.0, both L2-normalised) and 404s an unknown model; TEI's returns
vectors identical to its native `/embed` (cosine 1.0). The service stays single-text.

The request URI is built absolute from the service's own `BaseUrl` rather than from the named
`HttpClient`'s `BaseAddress` (`ServiceCollectionExtensions.cs:15-21`), so services for different models
can target different backends through the same factory. The named client remains for handler and
telemetry configuration.

`EmbeddingServiceOptions` gains `List<ModelEndpoint> Models` where `ModelEndpoint` is `{Name, BaseUrl}`,
bound from indexed configuration — `Embeddings__Models__0__Name`, `Embeddings__Models__0__BaseUrl` —
mirroring the Helm `global.embeddingModels` list. `EmbeddingServiceResolver.Get` (`:16-28`) looks the
requested model up in that list and uses its `BaseUrl`, falling back to `options.Value.BaseUrl`. A
deployment with no entries behaves exactly as today. The default service resolves its base URL the same
way — `Models.FirstOrDefault(m => m.Name == ModelId)?.BaseUrl ?? BaseUrl` — applied where the default
`EmbeddingService` is constructed (`ServiceCollectionExtensions.cs:21`) as well as in
`EmbeddingServiceResolver.Get`: `Get` short-circuits to the default service for the default model id
(`EmbeddingServiceResolver.cs:15-19`) and never consults the list for it, so without this a benchmark
arm that makes a TEI-only model the default (§3.4) would send that model to the global Ollama URL and
fail at registration.

### 3.2 Model identity guard

TEI serves one model per container and **ignores the `model` field** — verified: a request naming
`nomic-embed-text` against a bge-base container returned bge-base vectors. bge-base and nomic are both
768-dim, so the existing dimension probe cannot catch a misrouted model. In `EnsureInitializedAsync`
(`:37-52`), inside the same lock, between the probe call and the `_dimension` assignment (`:43`), the
service requests `GET {BaseUrl}/info`. If the answer is 200 and carries `model_id`, it must equal
`ModelId` (ordinal) or initialisation throws naming both ids. The placement is load-bearing: a mismatch
leaves the service uninitialised, so every later `EnsureInitializedAsync` re-throws rather than
returning early at `:36` — the startup call's swallowed warning (`Program.cs:404-415`) becomes a
registration-time `Unavailable` (`SchemaRegistrationOrchestrator.cs:133-142`) and blocks the arm
automatically. Ollama answers 404 there (verified), so the guard is a no-op on Ollama. One request per
service lifetime.

### 3.3 Prefix table

`EmbeddingPrefixes.Table` (`EmbeddingPrefixes.cs:26-31`) gains two rows, both `("", "Represent this
sentence for searching relevant passages: ")` — no document prefix, the bge query instruction:
`BAAI/bge-base-en-v1.5` and `BAAI/bge-small-en-v1.5`. The family rule splits on the first `:`; these ids
carry none, so each is its own family. `IngestContractTests` regenerates
`Iverson.LoadTest/scripts/ingest-contract.json` from the table (`IngestContractTests.cs:85-93`), and
`ingest.py` resolves prefixes from that file (`ingest.py:415-416`), so the Python side follows without a
second table.

### 3.4 Compose

A `tei-embed` service: image `ghcr.io/huggingface/text-embeddings-inference:cpu-1.8`, `profiles:
["tei"]`, `command: ["--model-id", "${EMBED_MODEL_ID:-BAAI/bge-base-en-v1.5}", "--auto-truncate"]`, port
`8091:80`, volume `tei_models:/data` — never under `/tmp`, which is a 4.9 GB tmpfs on this box. Same
shape as the `reranker` service (`docker-compose.yml:153-165`).

`--auto-truncate` is required, not optional: `BenchmarkDocument.Body` carries `[IversonEmbedding]` on the
whole body as well as `[IversonChunk]`, and SciFact abstracts reach ~1,900 tokens; TEI returns **413** above
512 tokens without the flag (verified: "must have less than 512 tokens. Given: 2002"). This is a real
behavioural difference from nomic on Ollama, whose context is 2,048 tokens (`ollama show`), so the
whole-document vector sees the full abstract under nomic and the first 512 tokens under bge. The
`.similar` metrics will reflect it; §6.4 reports them and does not gate on them.

`iverson-api` and `iverson-worker` gain `Embeddings__Models__0__Name=${EMBED_MODEL_ID:-BAAI/bge-base-en-v1.5}`
and `Embeddings__Models__0__BaseUrl=http://tei-embed:80`. The resolver is lazy, so the entry costs
nothing until a type declares that model. `Embeddings__ModelId` on both becomes
`${BENCH_EMBED_MODEL:-nomic-embed-text}` so a benchmark arm can make the candidate the default at
registration, following the `VectorRanking__Lambda=${VECTOR_RANKING_LAMBDA:-0.70}` pattern
(`docker-compose.yml:~392`).

### 3.5 Benchmark ingest script

`ingest.py` gains:

- `--embed-url` (default `http://localhost:11434`, today's `OLLAMA_URL`), and `embed()` (`:530-553`)
  posts to `{embed_url}/v1/embeddings` reading `data[0].embedding` — the same shape as the server.
- `--chunk-max-chars` / `--chunk-step` (defaults: the contract's `chunkWindow` values, 2,048 / 1,792).
  `main()` resolves the effective `(max_chars, step)` from the flags and passes the pair into
  `ingest_document`, which uses it for **both** `split_into_chunks(body, max_chars=…, step=…)` (`:585`)
  and the reuse gate `len(body) <= step` (`:597`) — the two operands of one window rule. The module
  globals `MAX_CHARS` / `STEP` are not rebound: `split_into_chunks`'s defaults bind at `def` time
  (`:237`), so a rebind would silently chunk at 2,048, and a chunker fed 448 while the gate still read
  1,792 would trip the assert at `:601`. `_verify_algorithm_goldens` (`:479`) keeps calling
  `split_into_chunks` with no arguments, so the golden chunking cases still run against the contract
  values and the override cannot mask a chunker regression. `word_boundary_lookback` is not overridable:
  both windows use 50.
- The stats sidecar (`<key-map-path>.stats.json`, `:740`) gains `model`, `embed_url`, `chunk_max_chars`,
  `chunk_step` — today it records counts and timing but not which model or window produced the vectors,
  and every arm here differs only in those. `benchmark-query` reads only `documents`/`chunks` from it
  (case-insensitive, unknown fields ignored), so nothing breaks.

## 4. Failure semantics — fail loud

- Probe failure at initialisation: unchanged — the service stays uninitialised and searches on that
  type return `Unavailable`.
- Identity mismatch: `EnsureInitializedAsync` throws naming the configured and served ids.
- TEI 413 (only possible if `--auto-truncate` is dropped): surfaces as a failed embed; never a truncated
  vector produced by the client.
- `ingest.py` against a TEI container serving the wrong model: the sidecar records `model` and
  `embed_url`, and the arm's `.meta.json` records the server's model id; the protocol (§6.3) checks
  `/info` before each arm.
- A model absent from `Models` falls back to the global base URL — which is Ollama, which 404s an
  unknown model at the first embed. Loud, as today.

## 5. Component contract

| Component | Change |
|---|---|
| `Iverson.Embeddings/EmbeddingService.cs` | `/v1/embeddings` request + `data[0].embedding` parse; absolute URI from `BaseUrl`; `/info` identity guard before the dimension is recorded; base URL resolved through `Models` via `BaseUrlFor(ModelId)`, so the DI-built default service reaches a listed backend |
| `Iverson.Embeddings/EmbeddingServiceOptions.cs` | `+ List<ModelEndpoint> Models`; `ModelEndpoint(Name, BaseUrl)` |
| `Iverson.Embeddings/EmbeddingServiceResolver.cs` | per-model `BaseUrl` lookup with global fallback |
| `Iverson.Embeddings/EmbeddingPrefixes.cs` | two bge rows |
| `Iverson.LoadTest/scripts/ingest-contract.json` | regenerated by `IngestContractTests` |
| `Iverson.LoadTest/scripts/ingest.py` | `--embed-url`, `/v1/embeddings`, chunk-window override, sidecar fields |
| `Iverson.Server/docker-compose.yml` | `tei-embed` service + volume; api/worker `Models__0__*`; templated `Embeddings__ModelId` |

Untouched: `IEmbeddingService`, `EmbedDocumentAsync`/`EmbedQueryAsync`, `EnrichmentService`, the schema
registration guard, all five clients, Helm (Phase 2), `ServiceCollectionExtensions.cs`.

## 6. Evaluation protocol

### 6.1 Baseline and window

The candidate arms are compared against `scifact-run-2026-08-26/runs/rerank-a0.chunks.trec` (nomic,
nDCG@10 0.6960, R@50 0.9227), the same control every reranker trial used, produced on today's server
build and ranking configuration. That collection was ingested with a **512-character chunk window**
(`maxChars 512, step 448, wordBoundaryLookback 50`, from the unmerged `chunk-size-512-experiment` branch's
contract), not main's 2,048. The candidates therefore ingest with `--chunk-max-chars 512 --chunk-step 448`.
The older 2,048-window nomic run (`prefixed-titled`, 2026-08-27) is **not** a valid baseline: the fusion
weights changed on 2026-08-31 (`ce7bf12`, triple B), after it was produced. Ruled by Ben 2026-09-04;
recorded as a known issue in §11.

### 6.2 Arms

| Arm | Corpus | Model | dims | window | est. ingest |
|---|---|---|---|---|---|
| M0 (existing) | SciFact | nomic-embed-text (Ollama) | 768 | 512 | — (`rerank-a0`) |
| M1 | SciFact | BAAI/bge-base-en-v1.5 (TEI) | 768 | 512 | ≈ 5.5 h |
| M2 | SciFact | BAAI/bge-small-en-v1.5 (TEI) | 384 | 512 | ≈ 1.9 h |
| N0 (existing) | NFCorpus | nomic-embed-text (Ollama) | 768 | 512 | — (`nfcorpus-run-2026-08-27/runs/rerank-a0`) |
| N1 | NFCorpus | BAAI/bge-base-en-v1.5 (TEI) | 768 | 512 | ≈ 4.1 h |

Estimates scale the recorded nomic ingests (SciFact 31,070 s for 25,128 embed calls; NFCorpus 23,334 s)
by the measured per-text ratios (bge-base 720 ms, bge-small 253 ms, nomic 1,130 ms). Sustained figures
on this box have run 4–5× slower than cold ones before; the estimates are for scheduling, not evidence.
N0's window is not recorded in its stats sidecar or `ingest.log` (both predate §3.5's fields). It comes
from `nfcorpus-run-2026-08-27/NEXT-STEPS.md:6,38`: the run was ingested from `centroid-ablation @
ededce0`, whose contract (`git show centroid-ablation:Iverson.Server/Iverson.LoadTest/scripts/ingest-contract.json`)
is 512 / 448 / 50, corroborated by 14,729 / 3,633 = 4.05 chunks per document. N1 therefore ingests with
`--chunk-max-chars 512 --chunk-step 448`, the same override as M1/M2.

### 6.3 Per-arm procedure

Nothing else runs on the box during an arm. `$RUN` is a **new** run directory per arm
(`scifact-bge-base-<date>` etc.) holding a copy of `beir/`, `qrels.trec`; the corpus file is the same
title-composed `corpus.jsonl` (5,183/5,183 texts begin with their title).

1. `EMBED_MODEL_ID=<model> docker compose --profile tei up -d --force-recreate tei-embed`; wait for
   `/info`; it must report the model id, `max_input_length` 512, `auto_truncate` true.
2. Ingest: `python3 ingest.py --corpus $RUN/beir/corpus.jsonl --key-map-path $RUN/keymap.json --drop
   --model <model> --embed-url http://localhost:8091 --chunk-max-chars 512 --chunk-step 448 | tee
   $RUN/ingest.log`. Verify the sidecar's `model`, `embed_url`, window, and the point counts.
3. Snapshot both collections (per `scifact-512-qdrant-snapshots/RESTORE.md`'s API) into
   `<corpus>-<model>-qdrant-snapshots/`.
4. Clear the benchmark type's schema row (`DELETE FROM _iverson_schema WHERE type_name =
   'BenchmarkDocument'`, the Phase 1 precedent) so it re-registers under the candidate — the
   registration guard refuses an in-place model change — and recreate the API with
   `BENCH_EMBED_MODEL=<model> EMBED_MODEL_ID=<model> docker compose up -d --no-deps iverson-api`
   (`--no-deps`, so Compose recreates only the API: containers created from another checkout carry
   different config hashes and would otherwise be recreated under it — the trap the reranker trial's
   plan review found). Confirm the
   API's log shows the embedding service initialised for `<model>` with the expected dimension and no
   identity mismatch.
5. `benchmark-query --config-label <label>` from `Iverson.LoadTest`; its sidecar records the build
   composite as today.
6. Ingest wall time and embed count from the sidecar are the arm's throughput evidence, quoted with the
   idle-box caveat.

After the last arm: restore the SciFact nomic snapshot (`scifact-512-qdrant-snapshots`), clear the schema
row, recreate the API with the defaults, and re-verify 5,183 / 19,967 points, so the box is left on the
baseline.

### 6.4 Statistics

`report.py --baseline` (not `--pair`: `check_pool` requires identical per-query candidate sets, which a
model change cannot satisfy — `--baseline` runs `run_paired_statistics`, `report.py:533`, which has no
pool check and prints delta, 95 % CI, MDE and Holm per measure, verified on the preserved runs):

```
report.py --qrels $SCIFACT/qrels.trec --baseline $SCIFACT/runs/rerank-a0.chunks.trec \
  --run $M1/runs/<m1>.chunks.trec --run $M2/runs/<m2>.chunks.trec        # Holm m = 2
report.py --qrels $NF/qrels.trec --baseline $NF/runs/rerank-a0.chunks.trec --run $N1/runs/<n1>.chunks.trec
```

`.similar` runs are scored the same way and reported. `PERMUTATION_SEED`, resamples and `HOLM_ALPHA`
unchanged.

## 7. Gate

A candidate **passes** if, on SciFact `chunks`, the 95 % CI lower bound of its nDCG@10 delta against M0
is above **−0.02** and the 95 % CI lower bound of its R@50 delta is above **−0.02** (non-inferiority; the
margin is the size of the largest configuration effect measured on this corpus, the +0.0134 prefix
effect). Superiority (positive delta, Holm p_adj < 0.05) and the `.similar` deltas are reported, not
required. N1 vs N0 is reported, not gated.

- Neither candidate passes: the migration does not happen; the throughput lever is closed for these
  models, and the verdict says so.
- One passes: Phase 2 proceeds with it.
- Both pass: Ben chooses at the gate between 768 dims (bge-base, 1.57×, no collection-shape change) and
  384 dims (bge-small, 4.5×) on the measured quality cost.

The verdict is recorded in `docs/plans/2026-09-GATE-embedding-migration.md` with the compare blocks
quoted verbatim, the throughput rows, and the `.similar` deltas.

## 8. Phase 2 — outline, planned only on a pass

- A `tei` subchart under `deploy/helm/iverson/charts/`, gated by `condition: tei.enabled` like the
  ollama subchart (`Chart.yaml:27`), one Deployment per served model with `--auto-truncate` and a
  model-cache PVC.
- `global.embeddingModels[]` entries gain `baseUrl`; `iverson.embeddingEnv` (`_helpers.tpl:22-34`)
  additionally renders `Embeddings__Models__N__Name/BaseUrl` for every entry that has one.
- `global.activeEmbeddingModel` switched to the passing model; nomic stays listed and pulled while any
  registered type still names it (the values-file invariant already documented there).
- Readiness: `stack.py`'s `ingest`/`query` tiers and the Launcher's wait gain the TEI service.
- Migration procedure for registered types, documented: clear the type's schema row and collections,
  re-register, re-ingest. There is no automatic re-embed and the guard blocks an in-place change.

## 9. Testing

- `EmbeddingServiceTests` (`FakeHttpMessageHandler`, `:14`): request path is `/v1/embeddings` on the
  configured base URL, body carries `model` and `input`, `data[0].embedding` is parsed; identity guard —
  `/info` 200 with matching id initialises, mismatching id throws naming both, 404 initialises; a
  mismatching `/info` makes a *second* `EnsureInitializedAsync` throw too, not just the first — the
  assertion that separates guard-before-dimension from guard-after.
  `FakeHttpMessageHandler` returns one response for every request today; it gains a branch on
  `request.RequestUri.AbsolutePath` (`/v1/embeddings` vs `/info`) and builds a fresh `HttpResponseMessage`
  per call, as `CountingHttpMessageHandler` already does (`:191-196`). `CountingHttpMessageHandler` and
  `FlakyThenSuccessHandler` count only `/v1/embeddings` (the same path branch), so the three `CallCount`
  assertions at `:210`, `:222`, `:256` keep their values and keep meaning "probe once per lifetime";
  raising the literals instead would stop `ProbesOnlyOnce` falsifying a double probe.
- `EmbeddingServiceResolverTests`: a listed model gets its own base URL; an unlisted one gets the global.
  Its `SuccessResponse` (`:27-34`) takes the `data[0].embedding` shape.
- `IngestContractTests`: the regenerated contract carries the two bge rows (the test fails until the
  JSON is regenerated, by design).
- `ingest.py`: a `--limit 5` smoke ingest into `--object-collection scratch_objects --chunks-collection
  scratch_chunks` against the TEI container with the 512 override, checking dims, chunk counts and the
  sidecar fields, before any real arm. There is no Python unit suite for `ingest.py`; the golden chunking
  cases in the contract are its regression net.
- Tests are written to fail against a named mutation (a `/api/embed` regression; a resolver that
  ignores `Models`; a guard that compares nothing).

## 10. Measurements taken during design (2026-09-04, live)

| What | Value | How |
|---|---|---|
| Ollama `/v1/embeddings` vs `/api/embed`, nomic, same input | cosine 1.0, 768 dims, both norm 1.0 | two calls, 127.0.0.1:11434 |
| Ollama `/v1/embeddings` unknown model | HTTP 404 | `model: no-such-model` |
| Ollama `GET /info` | HTTP 404 | — |
| Ollama models present | nomic, bge-m3, mxbai, arctic s/m, all-minilm, qwen2.5 — **no bge-base/small** | `ollama list` |
| nomic context length on Ollama | 2,048 tokens | `ollama show nomic-embed-text` |
| TEI cpu-1.8 + bge-base `/info` | `BAAI/bge-base-en-v1.5`, max 512, pooling cls | temporary container |
| TEI `/v1/embeddings` with `model: nomic-embed-text` against bge-base | **accepted**, 768 dims (field ignored) | — |
| TEI `/v1/embeddings` vs native `/embed` | cosine 1.0, norm 1.0 | — |
| TEI 2,002-token input without `--auto-truncate` | HTTP 413 | — |
| TEI + bge-small with `--auto-truncate`, same input | 384 dims | — |
| TEI `GET /v1/models` | HTTP 404 (so the guard uses `/info`) | — |
| .NET indexed list binding | `Embeddings__Models__{0,1}__{Name,BaseUrl}` → 2 entries | scratch console + `ConfigurationBinder` |
| `report.py --baseline` on `rerank-a1` vs `rerank-a0` | compare blocks with delta, 95 % CI, MDE, Holm; no `[pool]` line; exit 0 | read-only run |
| Nomic SciFact ingest (2026-08-27) | 31,070 s, 25,128 embed calls, 19,967 chunks | `keymap.json.stats.json` |

## 11. Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| E1 | Ollama serves `/v1/embeddings` with the OpenAI shape, identical vectors, 404 on unknown model | §10 rows 1–2 |
| E2 | TEI serves `/v1/embeddings`, ignores `model` | §10 rows 6–7 |
| E3 | TEI `/info` carries `model_id`; Ollama `/info` is 404 | §10 rows 3, 6 |
| E4 | TEI loads bge-base and bge-small on cpu-1.8; 413 above 512 tokens without `--auto-truncate` | §10 rows 6, 9, 10 |
| E5 | Ollama does not carry bge-base/small, so the query side cannot stay on Ollama | §10 row 4 |
| E6 | nomic's Ollama context is 2,048 tokens (design said 8k — corrected) | §10 row 5 |
| E7 | TEI `/info` also carries `max_input_length` and `auto_truncate`, which §6.3 step 1 asserts on | `TeiRerankClient.cs:27-29` binds all three; `scifact-run-2026-08-26/runs/rerank-a1.meta.json` records `maxInputLength 512, autoTruncate true` from cpu-1.8 (CDR-1) |
| S1 | `EmbedAsync` is the only request/URI site; the named client's `BaseAddress` is the only base-URL use | `EmbeddingService.cs:56-95`; `ServiceCollectionExtensions.cs:15-21` |
| S2 | Options bind from section `Embeddings`; a `List<T>` binds from indexed env vars | `ServiceCollectionExtensions.cs:11`; §10 row 11 |
| S3 | The resolver is the only non-DI construction site of `EmbeddingService` | `grep "new EmbeddingService("` → `EmbeddingServiceResolver.cs:21` only |
| S4 | The existing fakes (`FakeHttpMessageHandler`, `CountingHttpMessageHandler`, `RecordingHttpMessageHandler`) each return one response for every request and cannot route `/v1/embeddings` and `/info` separately; the §9 tests need a fake that branches on `request.RequestUri.AbsolutePath` and builds a fresh response per call | `EmbeddingServiceTests.cs:14-19,183-199`, `EmbeddingServiceResolverTests.cs:13-25` (CDR-1) |
| S5 | Nothing else calls `/api/embed` or depends on Ollama's response shape | repo grep → `EmbeddingService.cs:66,77` and `ingest.py:531` only |
| S6 | The probe runs inside `EnsureInitializedAsync`'s lock; the guard runs there before `_dimension` is assigned (`:43`) — the early return at `:36` is what makes the ordering load-bearing | `EmbeddingService.cs:36-52` (CDR-2) |
| S7 | Family = id before the first `:`; ordinal keys | `EmbeddingPrefixes.cs:33-40` |
| S8 | `IngestContractTests` writes/pins `ingest-contract.json`; `ingest.py` reads it | `IngestContractTests.cs:15,85-93`; `ingest.py:142-143,415-416` |
| S9 | Model ids never become Qdrant collection/vector names | `SchemaBuilder.cs:341-351` (names from property names); `CollectionSchema.cs:8` |
| S10 | `ValidateIdentifier` covers type and property names only, so `/` in a model id is accepted | `SchemaRegistrationOrchestrator.cs:74-76,774` |
| S11 | Enrichment has its own options/client and is untouched | `EnrichmentServiceOptions.cs:6-7`; `ServiceCollectionExtensions.cs:27-42` |
| S12 | Every backend-URL surface is enumerated: compose api/worker, Helm api/worker deployments, Launcher, options default — Phase 1 touches compose only | `docker-compose.yml:386,481`; `charts/api/templates/deployment.yaml:124`; `charts/worker/templates/deployment.yaml:119`; `Launcher/Program.cs:28`; `EmbeddingServiceOptions.cs:6` |
| C1 | The reranker service is the compose template; worker env block accepts new entries | `docker-compose.yml:153-165,476-492` |
| C2 | `${VAR:-default}` env templating is an existing pattern | `VectorRanking__Lambda=${VECTOR_RANKING_LAMBDA:-0.70}` |
| C3 | Qdrant and Postgres data live in named volumes, surviving container recreation | `docker-compose.yml:539-548` |
| C4 | The `--no-deps` recreate in §6.3 step 4 lands on the running dependencies' network: all six containers share project `iversonserver` and network `iversonserver_default`, because Compose derives the project name from the `Iverson.Server` directory | `docker inspect` labels (CDR-2) |
| B1 | `ingest.py`: `OLLAMA_URL` + `embed()` are the only embed site; `--model`, `--drop`, sidecar writer exist; window constants come from the contract | `ingest.py:147,208-210,530-553,684-745` |
| B2 | `ingest.py` resolves prefixes by family from the contract, same rule as C# | `ingest.py:406,415-416` |
| B3 | The baseline collection's window is 512/448/50 from the experiment branch; main's script is 2,048/1,792 now and was at the baseline commit `de4b6bf`; the fusion weights changed 2026-08-31 | `git show chunk-size-512-experiment:…ingest-contract.json`; `git show de4b6bf:…ingest.py:153-154`; `git log` → `ce7bf12` |
| B4 | `BenchmarkDocument` declares no model, so the API default decides at registration; clearing the schema row is the precedent | `Entities/BenchmarkDocument.cs` (only `[IversonEmbedding]`/`[IversonChunk]`); Phase 1 report step 2 |
| B5 | Queries embed with the type's registered model through the resolver | `ObjectSearchGrpcService.cs:206,387` |
| B6 | `--baseline` has no pool check and prints CI/MDE/Holm | `report.py:533-600`; §10 row 12 |
| B7 | `report.py` reads only trec files (dimension-agnostic) | by construction of `--run`/`--baseline` |
| B8 | Nomic ingest timings and TEI per-text ratios | §10 row 13; memory `project-tei-vs-ollama-throughput` |
| B9 | Snapshot/restore procedure and files exist | `scifact-512-qdrant-snapshots/RESTORE.md:12-16` + two `.snapshot` files |
| B10 | NFCorpus baseline run dir holds `beir/`, `qrels.trec`, `keymap.json*`, `ingest.log`, `runs/` | `ls nfcorpus-run-2026-08-27` |
| B11 | `benchmark-query` reads only `documents`/`chunks` from the stats sidecar, case-insensitive, extras ignored | `BenchmarkQueryScenario.cs:51-53,91-100` |
| B12 | `report.py` also reads the stats sidecar (`--stats-path`); every access is `stats.get(<key>)`, so the four added keys are inert there | `report.py:322-365` (CDR-1) |
| B13 | `report.py` prints `!! BUILD MISMATCH` and continues; delta/CI/MDE/Holm are unaffected, and §5 leaves the scoring assemblies untouched | `report.py:486-497` (CDR-2) |
| H1 | The ollama subchart + `condition:` pattern exists for Phase 2 | `Chart.yaml:27`; `charts/ollama/templates/{statefulset,service}.yaml` |
| H2 | The Helm chart's default-deny egress does not reach a non-Ollama backend; Phase 2 must add TEI to it (Phase 2 only) | `networkpolicies.yaml:54-55,85-86,394-402` (CDR-2) |

## 12. Known issues, accepted as out of scope

- **Measured on the unmerged 512-character chunk configuration**, not main's 2,048 default, because that
  is the only nomic baseline on today's server (§6.1). Ruled by Ben 2026-09-04. A pass says the model
  holds quality under that window; the window itself is a separate, unmerged experiment.
- **Whole-document embeddings differ in coverage** (nomic 2,048 tokens vs TEI 512 with truncation); the
  `.similar` metric is reported, not gated.
- **The quality gain is published-only until M1 runs.** The gate exists because published deltas have
  not transferred here before.
- **Throughput evidence is a by-product of the arms**, not a controlled measurement; the controlled
  numbers are the 2026-09-03 interleaved sweep.
