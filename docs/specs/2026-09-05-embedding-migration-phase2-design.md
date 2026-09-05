# Embedding migration Phase 2 — TEI everywhere, TGI for enrichment, Ollama removed (design)

Status: design, verified against the codebase, the live compose stack and the two serving images 2026-09-05.
Predecessor: `docs/specs/2026-09-04-embedding-migration-design.md` (Phase 1; gate passed for bge-base — see
`docs/plans/2026-09-GATE-embedding-migration.md`). Its §8 outline is superseded by this document.
Related: `docs/specs/2026-09-01-helm-embedding-model-configuration-design.md` (the `global.embeddingModels`
invariant this spec rewrites).

## 1. Why

Phase 1 measured `BAAI/bge-base-en-v1.5` on TEI as non-inferior and superior to nomic on Ollama (SciFact chunks
nDCG@10 +0.0492, 95 % CI [+0.0242, +0.0741]; NFCorpus +0.0185, significant), 3.4–3.6× faster to ingest. Spec §7
of Phase 1 says: one candidate passed → Phase 2 proceeds with it. This spec makes bge-base on TEI the default on
every deployment surface — Helm, compose, the Launcher's `dotnet run` path, the benchmark scripts and the
conformance fixtures — and, by Ben's decision 2026-09-05, removes Ollama entirely rather than keeping it for the
one consumer the embedding switch would have left it: chunk enrichment (`EnrichmentService`, `qwen2.5:3b`).

Removing Ollama needs a new generative backend. Ben chose HuggingFace text-generation-inference (TGI) and a
measured choice of model. Only `Qwen/Qwen2.5-1.5B-Instruct` can be measured on this box (§6); the 3B variant
waits for a cloud node. Because the measurement can fail, the removal is gated (§6.4) and the spec defines the
end state for both outcomes (§7).

There are no registered types on any deployment, so no migration procedure exists in this spec.

## 2. Scope

**In:** a `tei` subchart and a `tgi` subchart replacing `ollama`; `global.embeddingModels` entries all TEI-served;
Terraform node pools and storage classes to match; compose `tei-embed` as a default service plus a `tgi` service,
`ollama`/`ollama-init` removed; `EmbeddingServiceOptions` and `EnrichmentServiceOptions` defaults switched;
`EnrichmentService` ported from Ollama's `/api/generate` to the OpenAI-compatible `/v1/chat/completions`;
Launcher, `stack.py`, `ingest.py` defaults; the five conformance drivers' declared model; every remaining Ollama
reference in code, chart, compose, Terraform and scripts; a measurement script and gate for the enrichment
backend; the verdict document.

**Out:** `bge-small` (failed the Phase 1 gate); any migration tooling (no registered types exist); the
`docs/` history (untouched); Qwen2.5-3B on TGI (cannot be measured here — §6.1); the `EmbeddingPrefixes`
nomic/arctic rows (model ids, not a backend; the ingest contract pins them and nothing is served by them by
default).

One spec, not two: Ben declined the decomposition into "Phase 2 with Ollama kept for enrichment" and "generative
backend replacement" (2026-09-05). §7 orders the work so the embedding switch lands regardless of the enrichment
gate.

## 3. Design

### 3.1 Helm values

```yaml
global:
  # Every entry is served by the tei subchart (one StatefulSet per entry). `slug` is the DNS-safe suffix of
  # that entry's StatefulSet/Service: <release>-tei-<slug>. There is no Ollama-served entry any more.
  embeddingModels:
    - name: BAAI/bge-base-en-v1.5
      slug: bge-base
      # prefixes omitted -> EmbeddingPrefixes derives them from the model family (rows added in Phase 1)
  activeEmbeddingModel: BAAI/bge-base-en-v1.5
  # Hub id of the generative model the tgi subchart serves and api/worker request through Enrichment__ModelId.
  generativeModel: "Qwen/Qwen2.5-1.5B-Instruct"
  # Drives the tgi subchart's condition AND Enrichment__Enabled on api/worker (one flag, same reasoning as
  # engagementEnabled). false on profiles that cannot afford TGI (laptop).
  enrichmentEnabled: true

tei:
  enabled: true
  imageTag: "cpu-1.8"
  replicas: 2
  storageSize: 2Gi            # bge-base weights are 0.44 GB
  storageClassName: ""
  resources:
    requests: { cpu: "2", memory: "2Gi", ephemeral-storage: "1Gi" }
    limits:   { cpu: "4", memory: "4Gi", ephemeral-storage: "2Gi" }
  nodeSelector: {}
  tolerations: []

tgi:
  enabled: true               # rendered only when global.enrichmentEnabled is also true (Chart.yaml condition)
  imageTag: "3.3.4-intel-cpu"
  replicas: 2
  storageSize: 8Gi            # Qwen2.5-1.5B-Instruct weights are 3.09 GB; 3B is ~6 GB
  storageClassName: ""
  resources:                  # the former ollama sizing; bf16 1.5B measured 4.2–5.3 GB RSS
    requests: { cpu: "8", memory: "16Gi", ephemeral-storage: "2Gi" }
    limits:   { cpu: "8", memory: "16Gi", ephemeral-storage: "4Gi" }
  nodeSelector: {}
  tolerations: []
```

The `ollama:` block and the "must contain every model any registered type names" comment are deleted from all
six values files. Profiles: `values-local.yaml` and `values-laptop.yaml` set `tei.replicas: 1`,
`tgi.replicas: 1`, `storageClassName: standard`; laptop sets `global.enrichmentEnabled: false` (its 1.45-CPU
budget cannot hold a 4–5 GB, 8-thread generative server) and smaller `tei.resources` (`500m`/`1Gi`).
`values-{aws,azure,gcp}.yaml` replace their `ollama:` block with `tei:` (`storageClassName: iverson-tei`,
`nodeSelector: {iverson.io/node-pool: tei}`, matching toleration) and `tgi:` (`iverson-tgi`, pool `tgi`).

### 3.2 The `tei` subchart

`charts/tei/` mirrors `charts/ollama/` (`Chart.yaml`, `values.yaml`, `templates/{statefulset,service,pdb}.yaml`),
listed in the parent `Chart.yaml` with `condition: tei.enabled`. Each template ranges over
`.Values.global.embeddingModels` and renders, per entry:

- a ServiceAccount and a StatefulSet `{{ .Release.Name }}-tei-{{ .slug }}` with pod labels
  `app: <release>-tei-<slug>` and `iverson.io/component: tei`; container
  `ghcr.io/huggingface/text-embeddings-inference:{{ .Values.imageTag }}`, args
  `["--model-id", <name>, "--auto-truncate", "--port", "8080"]` (TEI cannot bind 80 as uid 1000 — §9 row 6),
  `containerPort: 8080`, ollama's securityContext (`runAsNonRoot`, uid/gid/fsGroup 1000,
  `readOnlyRootFilesystem`, drop ALL), volumes: the `tei-data` PVC at `/data` (the image's
  `HUGGINGFACE_HUB_CACHE`) from `volumeClaimTemplates` and an `emptyDir` at `/tmp`; a startupProbe
  `GET /health` on 8080 (`periodSeconds 10`, `failureThreshold 30` — the first start downloads the weights) and
  a readinessProbe on the same path; the pod anti-affinity, `nodeSelector`, `tolerations` and `resources` blocks
  as ollama's;
- a headless Service `<release>-tei-<slug>` with `port: 80`, `targetPort: 8080`, selector `app`;
- a PodDisruptionBudget as ollama's.

`--auto-truncate` is mandatory (Phase 1 §3.4: 413 above 512 tokens without it).

### 3.3 The `tgi` subchart

`charts/tgi/`, same file shape, `condition: global.enrichmentEnabled` (the `tgi.enabled` value is read by the
templates as a second guard so `--set tgi.enabled=false` also works). One StatefulSet `<release>-tgi` (labels
`app: <release>-tgi`, `iverson.io/component: tgi`), container
`ghcr.io/huggingface/text-generation-inference:{{ .Values.imageTag }}` with args

```
--model-id {{ .Values.global.generativeModel }} --port 8080 --dtype bfloat16
--max-input-tokens 3072 --max-total-tokens 3584 --max-batch-prefill-tokens 3072
```

env `HF_HUB_DISABLE_XET=1` (§9 row 9: the Hub's Xet download path failed repeatedly from this box; the classic
path completed), `workingDir: /tmp` (§9 row 12: under a read-only root the router writes a file named `out` in
its cwd and otherwise degrades to legacy tokenization), volumes: PVC `tgi-data` at `/data`, `emptyDir` at
`/tmp`, `emptyDir` (`medium: Memory`, `sizeLimit: 1Gi`) at `/dev/shm`; ollama's securityContext; a startupProbe
`GET /health` on 8080 with `periodSeconds 15`, `failureThreshold 120` (the CPU warm-up measured ~10 minutes
after a ~19 s shard load; the first start adds the download), then a readinessProbe on the same path; a
headless Service `<release>-tgi` (`80 → 8080`); a PDB.

The token limits are load-bearing: at the image defaults (`max_total_tokens` 32,768) warm-up took twenty
minutes and pre-allocated for a 4,096-token prefill; 3,072 input tokens is above the 8,000-character prompt cap
in §3.5.

### 3.4 Helpers, deployments, policies

`_helpers.tpl`:

- `iverson.activeEmbeddingModel` is unchanged (it resolves prefixes by name).
- `iverson.embeddingEnv` additionally emits, for each entry at index `i` of `global.embeddingModels`,
  `Embeddings__Models__<i>__Name: <name>` and `Embeddings__Models__<i>__BaseUrl: http://<release>-tei-<slug>:80`.
- new `iverson.embeddingBaseUrl`: `http://<release>-tei-<slug of the first entry>:80` — the global fallback,
  now a TEI service. A deployment with a single entry never consults `Models`.

`charts/api/templates/deployment.yaml` and `charts/worker/templates/deployment.yaml` replace
`Embeddings__BaseUrl: http://<release>-ollama:11434` with the helper, `Enrichment__BaseUrl` with
`http://<release>-tgi:80`, and add `Enrichment__Enabled: {{ .Values.global.enrichmentEnabled }}`.

`templates/networkpolicies.yaml`: the `ollama-ingress`/`ollama-egress` policies are deleted; `tei-ingress`
(from api and worker, TCP 8080, selector `iverson.io/component: tei`) and `tei-egress` (DNS + TCP 443 to any —
the Hub download, the same reasoning as the deleted registry rule) are added, and likewise `tgi-ingress`/
`tgi-egress` (selector `iverson.io/component: tgi`); the api-egress and worker-egress rules replace the
`-ollama:11434` target with `iverson.io/component: tei` on 8080 and `iverson.io/component: tgi` on 8080.

`charts/ollama/` is deleted, its `Chart.yaml` dependency and `Chart.lock` entry removed; `helm dependency
build` regenerates `charts/*.tgz` (the existing `ollama-0.1.0.tgz` is deleted with it — a stale tarball would
otherwise keep rendering the old subchart, the trap recorded in memory `feedback-helm-subchart-tgz-cache`).

### 3.5 Server code

**`EmbeddingServiceOptions`** defaults: `BaseUrl = "http://localhost:8091"`, `ModelId = "BAAI/bge-base-en-v1.5"`.

**`EnrichmentServiceOptions`** defaults: `BaseUrl = "http://localhost:8092"`, `ModelId =
"Qwen/Qwen2.5-1.5B-Instruct"`. `Enabled`, `Timeout`, `MaxConcurrentChunkPrefixes` unchanged.

**`EnrichmentService`** (`Iverson.Embeddings/EnrichmentService.cs`): `GenerateInternalAsync` posts to
`{BaseUrl}/v1/chat/completions` (absolute URI from `BaseUrl`, the Phase 1 pattern) with

```json
{"model": "<ModelId>", "messages": [{"role": "user", "content": "<prompt>"}],
 "max_tokens": 256, "temperature": 0, "stream": false}
```

and reads `choices[0].message.content`. Two constants replace behaviour Ollama gave for free:

- `MaxPromptChars = 8_000`: the prompt is cut to this length before sending. TGI rejects inputs above
  `--max-input-tokens` with a 422 where Ollama silently truncated at its 2,048-token context; 8,000 characters
  is ~2,000 tokens, under the 3,072 limit with room for the chat template. Parity, not a regression.
- `MaxGeneratedTokens = 256`: the chat API requires an explicit `max_tokens`; the enrichment prompts ask for
  2–3 sentences, 5–10 keywords, a 1–2 sentence description, or a JSON object.

`GenerateJsonAsync` sends the same request with no grammar (ruled by Ben 2026-09-05, option (a) of §9 row 11:
TGI grammars require a fixed `properties` set and a permissive schema constrains the model to `{ }`; the
extraction target's hint is free text and its result is stored verbatim). It strips a leading ```` ``` ```` or
```` ```json ```` fence and a trailing fence, validates the remainder with `JsonDocument.Parse`, and throws
`InvalidOperationException` naming the first 200 characters when it does not parse. Ollama also serves
`/v1/chat/completions` (§9 row 14), so this port is backend-neutral: the fail branch of §7 keeps it.

**`Telemetry`**: `HttpClientName = "iverson.embeddings"`, `EnrichmentHttpClientName = "iverson.enrichment"`
(named-client keys only; no dashboard or alert references them — §10 S3).

**Strings and comments**: `SchemaRegistrationOrchestrator.cs:128` "must remain pulled in this deployment's
Ollama" → "must remain served by this deployment's embedding backend"; `Program.cs:410-412` startup comment;
`EnrichmentConsumer.cs:16`; `EmbeddingService.cs:66,112`; `EnrichmentServiceOptions.cs:14`;
`EmbeddingPrefixes.cs:16` ("Ollama ids carry tags" stays as a description of the id grammar);
`BenchmarkIngestScenario.cs:240`; `ModelRejectedScenario.cs:79`; `VectorSearchScenario.cs:124`;
`Iverson.Clients/Python/iverson_client/annotations.py:89-90,170-180,230` docstrings.

### 3.6 Launcher

`Iverson.Launcher/Program.cs`: the compose `up` list becomes `postgres starrocks qdrant kafka zookeeper jaeger
tei-embed tgi`; `WaitForOllamaAsync` is replaced by `WaitForHttp200Async` called for
`http://localhost:8091/health` ("TEI") and `http://localhost:8092/health` ("TGI"), with a 30-minute ceiling
for TGI's first start.

### 3.7 Compose

`Iverson.Server/docker-compose.yml`:

- `ollama` (`:124-136`), `ollama-init` (`:138-149`) and the `ollama_data` volume (`:571`) are deleted; the two
  `depends_on: ollama-init: service_completed_successfully` entries on api (`:473`) and worker (`:545`) go with
  them.
- `tei-embed` loses `profiles: ["tei"]` and gains a healthcheck
  `["CMD", "curl", "-sf", "http://localhost:80/health"]` (`interval 15s`, `timeout 5s`, `retries 20`,
  `start_period 5m`); the image carries `curl` (§9 row 7).
- new service `tgi`: image `ghcr.io/huggingface/text-generation-inference:3.3.4-intel-cpu`, `container_name:
  iverson-tgi`, `command: ["--model-id", "${GEN_MODEL_ID:-Qwen/Qwen2.5-1.5B-Instruct}", "--dtype", "bfloat16",
  "--max-input-tokens", "3072", "--max-total-tokens", "3584", "--max-batch-prefill-tokens", "3072"]`,
  `environment: [HF_HUB_DISABLE_XET=1]`, `shm_size: 1g`, `ports: ["8092:80"]`, `volumes: [tgi_models:/data]`,
  healthcheck `curl -sf http://localhost:80/health` with `start_period 25m` (download plus warm-up on this
  box); `tgi_models` added under `volumes:`. Compose containers run as root, so both keep the image's port 80.
- api env: `Embeddings__BaseUrl=http://tei-embed:80`, `Embeddings__ModelId=${BENCH_EMBED_MODEL:-BAAI/bge-base-en-v1.5}`,
  the `Embeddings__Models__0__*` pair removed (redundant with a TEI fallback and one container),
  `Enrichment__BaseUrl=http://tgi:80`, `Enrichment__ModelId=${GEN_MODEL_ID:-Qwen/Qwen2.5-1.5B-Instruct}`; the
  comment block at `:410-416` rewritten (the default must agree with `tei-embed`'s served model and the
  conformance fixtures now declare it); worker env the same. api `depends_on` gains `tei-embed:
  condition: service_healthy`; worker gains `tei-embed` and `tgi`, both `service_healthy`.
- The Phase 1 `EMBED_MODEL_ID` templating of `tei-embed`'s `--model-id` stays (the benchmark arms use it).

### 3.8 Scripts and kind

- `stack.py`: `TIERS = {"ingest": ["qdrant", "tei-embed"], "query": ["qdrant", "tei-embed", "postgres", "redis",
  "authentik-server", "iverson-api"]}` (enrichment is worker-only — §10 S6 — so the query tier does not need
  `tgi`); `CONTAINER["tei-embed"] = "iverson-tei-embed"`; `READY_CHECKS["tei-embed"] =
  wait_http_200("http://127.0.0.1:8091/health")`; the docstring's tier lists and the `ollama-init` note updated.
- `ingest.py`: `--model` default `BAAI/bge-base-en-v1.5`; `--embed-url` default `http://localhost:8091`
  (`OLLAMA_URL` renamed `DEFAULT_EMBED_URL`); the docstring and help text drop the Ollama-only wording that
  remains at `:26,64,121-124,153,725`. `benchmark-query` registers under the API default, and a nomic-ingested
  index queried with bge-base is the same 768 dims, so the defaults must agree.
- `deploy/kind/setup.sh:98` and `setup.ps1:95`: the immutable-`volumeClaimTemplates` note names
  `tei.storageSize`/`tgi.storageSize` and the `iverson-tei-bge-base`/`iverson-tgi` StatefulSets.

### 3.9 Terraform

In `modules/cluster-{aws,gcp,azure}`: the `ollama` pool entry becomes `tgi` (variables `tgi_instance_type`/
`tgi_machine_type`/`tgi_vm_size` and `tgi_node_count`, same defaults as the ollama ones) and a `tei` entry is
added (`tei_*` variables; one size below: `c7i.xlarge`, `c2-standard-4`, `Standard_F4s_v2`; count 2). The pool
key is the `iverson.io/node-pool` label the values files select on. In `modules/operators`:
`kubernetes_storage_class.ollama` (`iverson-ollama`) becomes `tgi` (`iverson-tgi`), `tei` (`iverson-tei`) is
added, and `outputs.tf:7` follows. The root modules pass no ollama variable (§10 F4), so nothing else moves.
Existing clusters: a pool rename is a replace; this spec does not plan the cut-over of a live cloud cluster
(none is running).

### 3.10 Conformance fixtures

`S11Model{Dotnet,Java,Python,Typescript,Go}` and `S12Declared{…}` declare `BAAI/bge-base-en-v1.5`
(`Iverson.Clients/DotNet/…/Models/S11ModelDotnet.cs:24`, `S12ModelDotnet.cs:11`; `Java/…/models/S11ModelJava.java:30`,
`S12DeclaredJava.java:11`; `Python/conformance/models.py:202,231`; `TypeScript/conformance/models.ts:303,327`;
`Go/conformance/models.go:208,218`), their doc comments and the three driver comments follow, and
`Iverson.ClientConformance/Scenarios/InheritedModelScenario.cs:63` `ExpectedModelId` matches. These fixtures
exist to declare the deployment default explicitly. `ModelRejectedScenario.OverrideModelId` is a never-deployed
id and stays. The client SDKs' unit tests keep their `nomic-embed-text` literals: they test annotation
propagation and never contact a backend.

## 4. Failure semantics — fail loud

- Embedding: unchanged from Phase 1 (probe failure → `Unavailable`; `/info` identity mismatch throws on every
  initialisation; a TEI 413 is a failed embed).
- Enrichment backend unreachable or non-2xx: `HttpRequestException` from `EnsureSuccessStatusCode`, as today.
- Enrichment JSON extraction that does not parse after fence stripping: `InvalidOperationException` naming the
  head of the text; nothing is stored for that column.
- A prompt over 8,000 characters is cut, never rejected; a generation over 256 tokens is cut by the server.
- `global.enrichmentEnabled: false` disables the consumer (`Enrichment__Enabled=false`) and renders no `tgi`
  objects; api/worker still render `Enrichment__BaseUrl`, which nothing calls.
- TGI's first start is minutes long: compose `start_period` and the k8s startupProbe budgets are sized to it;
  a pod that never becomes healthy fails its startupProbe and restarts, which is visible.

## 5. Component contract

| Component | Change |
|---|---|
| `deploy/helm/iverson/Chart.yaml`, `Chart.lock`, `charts/*.tgz` | `ollama` dependency removed; `tei` (`condition: tei.enabled`) and `tgi` (`condition: global.enrichmentEnabled`) added; `helm dependency build` |
| `charts/tei/**`, `charts/tgi/**` | new subcharts (§3.2, §3.3) |
| `charts/ollama/**` | deleted |
| `templates/_helpers.tpl` | `Models__N` emission; `iverson.embeddingBaseUrl` |
| `charts/api/templates/deployment.yaml`, `charts/worker/templates/deployment.yaml` | `Embeddings__BaseUrl` via helper; `Enrichment__BaseUrl` → tgi; `Enrichment__Enabled` |
| `templates/networkpolicies.yaml` | ollama policies deleted; tei/tgi ingress+egress; api/worker egress targets |
| `values.yaml` + 5 profiles | §3.1 |
| `deploy/terraform/modules/{cluster-aws,cluster-gcp,cluster-azure,operators}` | §3.9 |
| `deploy/kind/setup.{sh,ps1}` | note text |
| `Iverson.Embeddings/EmbeddingServiceOptions.cs`, `EnrichmentServiceOptions.cs` | defaults |
| `Iverson.Embeddings/EnrichmentService.cs` | chat-completions port, prompt cap, JSON validation |
| `Iverson.Embeddings/Telemetry.cs` | client names |
| `Iverson.Embeddings.Tests/EnrichmentServiceTests.cs` | request/response shape, cap, fence, invalid-JSON tests |
| `Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`, `Iverson.Api/Program.cs`, `Iverson.Api/Consumers/EnrichmentConsumer.cs`, `EmbeddingService.cs`, `EmbeddingPrefixes.cs`, `BenchmarkIngestScenario.cs`, `ModelRejectedScenario.cs`, `VectorSearchScenario.cs`, `Python/iverson_client/annotations.py` | wording only |
| `Iverson.Launcher/Program.cs` | §3.6 |
| `Iverson.Server/docker-compose.yml` | §3.7 |
| `Iverson.LoadTest/scripts/stack.py`, `ingest.py` | §3.8 |
| `Iverson.LoadTest/scripts/enrich_bench.py` | new (§6.2) |
| five conformance fixture sets + `InheritedModelScenario.cs` | §3.10 |
| `docs/plans/2026-09-GATE-enrichment-backend.md` | verdict (§6.4) |

Untouched: `IEmbeddingService`, `IEnrichmentService`, `EmbeddingService`'s request path and guard,
`EnrichmentConsumer`'s pipeline, the schema-registration guard, the five client SDK APIs, `EmbeddingPrefixes.Table`,
the ingest contract, `report.py`.

## 6. Enrichment backend measurement

### 6.1 Candidates and where they can run

| Candidate | Weights | RSS measured here | Runs on this box? |
|---|---|---|---|
| Ollama `qwen2.5:3b` (baseline, Q4 GGUF) | ~2 GB | — | yes (6.9 tok/s native) |
| TGI `Qwen/Qwen2.5-1.5B-Instruct` bf16 | 3.09 GB | 4.2–5.3 GB | yes (~1.6 tok/s, 32 tokens in 19.5 s) |
| TGI `Qwen/Qwen2.5-3B-Instruct` bf16 | ~6 GB | not measured | no — 9.9 GB RAM, 2 GB free with the stack up |

This box is an i7-7500U (AVX2, no AVX-512/AMX, 4 threads); TGI's Intel CPU image computes bf16 without the
AMX fast path it is built for. Ben ruled 2026-09-05: measure the 1.5B candidate here; 3B is deferred to a
cloud (`c7i`, AMX) node and is not part of this gate.

### 6.2 `enrich_bench.py`

`Iverson.LoadTest/scripts/enrich_bench.py` builds a fixed prompt set from the first documents of
`scifact-run-2026-08-26/beir/corpus.jsonl` (read-only): 50 `ChunkContext` prompts (context = the first 1,500
characters of the abstract, excerpt = its first 512 characters), 10 `Summary`, 10 `Keywords`, 5 `Extraction`
(hint "the main finding and the population studied"), using the prompt strings from `EnrichmentPrompts.cs`
copied verbatim (the script is Python; the duplication is the same accepted shape as the ingest contract). It
runs the set at temperature 0, `max_tokens 256`, sequentially against each backend — Ollama `qwen2.5:3b` via
`/v1/chat/completions` on 11434 (the same route the ported client uses), then TGI on 8092 — recording per
prompt: wall seconds, completion tokens (`usage.completion_tokens`), tokens/s, whether the output is empty,
and for `Extraction` whether it parses after fence stripping; and per backend: peak container RSS sampled from
`docker stats --no-stream` every 5 s, and the box's `MemAvailable` minimum. Output: `enrichment-bench-<date>/
results.json` and `side-by-side.md` (every prompt with both outputs) under the corpora directory. Backends are
never run concurrently.

### 6.3 Ordering of the measurement

The script runs against the compose stack **before** Ollama is removed: the `tgi` service is added first (§7,
Phase B), `ollama` is still present, the worker is stopped, and nothing else runs on the box.

### 6.4 Gate

TGI-1.5B **passes** when all of: (1) mean `ChunkContext` wall ≤ Ollama-3B's mean; (2) p95 wall over all 75
prompts < 120 s (`EnrichmentServiceOptions.Timeout`); (3) zero failed or empty generations; (4) all five
`Extraction` outputs parse; (5) with the query tier and `tgi` up, the box's minimum `MemAvailable` stays above
500 MB and no container is OOM-killed. The side-by-side table is Ben's quality call and can veto a numeric pass.
The verdict is recorded in `docs/plans/2026-09-GATE-enrichment-backend.md` with the per-kind means, p95, tokens/s,
peak RSS, the parse results and the table, in the shape of the Phase 1 gate document.

Expectation, stated so the outcome is not a surprise: Ollama-3B measured 6.9 tok/s and TGI-1.5B ~1.6 tok/s in
the design probes (§9), so criterion (1) is likely to fail on this box. That is the point of measuring.

## 7. Ordering and the two end states

- **Phase A — embedding switch (no gate):** §3.1–§3.2 minus the ollama deletion, §3.4's `Models__N` and
  `embeddingBaseUrl` helpers, `EmbeddingServiceOptions` defaults, compose `tei-embed` default + healthcheck +
  `Embeddings__*` env, `stack.py`, `ingest.py`, Launcher's TEI wait, the conformance fixtures, the Phase 1
  `Embeddings__Models__0__*` removal. Ollama is still present and still serves enrichment.
- **Phase B — enrichment port and measurement:** `EnrichmentService` port (backend-neutral), compose `tgi`
  service, `enrich_bench.py`, the measurement, the verdict.
- **Phase C — on a pass:** the Ollama removal (§3.3 tgi subchart, §3.4 policies and env, §3.5 `Enrichment`
  defaults and wording, §3.6 Launcher's TGI wait and `up` list, §3.7 deletions and `tgi` dependencies, §3.9,
  kind notes) and the kind smoke.
- **Phase C′ — on a fail:** Ollama stays for enrichment only. `charts/ollama` remains with its pull loop
  reduced to `global.generativeModel` (an Ollama id again, `qwen2.5:3b`), `Enrichment__BaseUrl` stays
  `http://<release>-ollama:11434`, compose keeps `ollama` and an `ollama-init` that pulls only `qwen2.5:3b`,
  `EnrichmentServiceOptions` defaults stay Ollama's, the `tgi` compose service and the Terraform `tei` pool
  addition land but the `ollama → tgi` rename does not. The port in Phase B works unchanged against Ollama
  (§9 row 14). Ben decides at the gate whether to pursue the 3B candidate on a cloud node.

## 8. Testing

- `Iverson.Embeddings.Tests`: `EnrichmentServiceTests` rewritten for the chat shape — path `/v1/chat/completions`
  on the configured base URL, body carries `model`, one user message, `max_tokens` 256, `temperature` 0,
  `stream` false; `choices[0].message.content` returned; prompt longer than 8,000 characters is sent cut to
  8,000; `GenerateJsonAsync` sends no `response_format`, strips a ```` ```json ```` fence, throws on non-JSON;
  non-2xx throws `HttpRequestException`. Tests fail against named mutations (an `/api/generate` regression; a
  `response_format` sent; no cap; no fence strip). `EmbeddingServiceTests` unchanged except defaults.
- `helm lint`, `helm template | kubeconform`, `kube-score` on all five profiles (the `deploy-validate` workflow's
  three jobs, run locally with `helm dependency build` first) — asserting one `tei` StatefulSet/Service per
  entry, the `tgi` StatefulSet present when `enrichmentEnabled`, absent on laptop, no object named `-ollama`,
  the api/worker env, the policies.
- `terraform fmt -check` and `init -backend=false && validate` for the three cloud roots (the workflow's matrix).
- `docker compose config` renders; the conformance harness (five drivers) against compose after Phase A; the
  Embeddings, Api and LoadTest suites.
- kind deployment on `values-local.yaml`: TEI pod ready, API log `EmbeddingService initialized:
  model=BAAI/bge-base-en-v1.5 dimension=768`, and (Phase C) the tgi pod ready and one worker enrichment logged.
- The Phase 1 benchmark harness still runs: `stack.py query` then `benchmark-query` against the restored SciFact
  index (now registered under bge-base by default) produces run files.

## 9. Measurements taken during design (2026-09-05, live)

| # | What | Value | How |
|---|---|---|---|
| 1 | This box | i7-7500U, AVX2/FMA, no AVX-512/AMX, 4 threads, 9.9 GB RAM (2 GB available with the query tier up) | `lscpu`, `free` |
| 2 | TGI image tags | `3.3.4-intel-cpu` exists (amd64), `3.3.5-intel-cpu` does not; 6.87 GB on disk | `docker manifest inspect`, `docker images` |
| 3 | TGI image | `ubuntu:22.04`, PyTorch 2.7 CPU + IPEX, no AVX-512 check, `HUGGINGFACE_HUB_CACHE=/data`, `PORT=80`, entrypoint `text-generation-launcher`, root, has `curl`, `wget`, `python3` | `Dockerfile_intel`, image probe |
| 4 | TGI API | paths incl. `/health` (200 / 503), `/info` (`model_id`, `max_input_tokens`, `max_total_tokens`, `version`), `/generate`, `/v1/chat/completions`; `ChatRequest.response_format` is `GrammarType` = `json` \| `regex` \| `json_schema`, each with `value` | OpenAPI document |
| 5 | TEI API | `/health` (200 / 503), `/info` incl. `model_dtype`; image Debian bookworm, root, has `curl`, no `wget`, `HUGGINGFACE_HUB_CACHE=/data`, `PORT=80` | OpenAPI document, image probe |
| 6 | TEI as uid 1000, read-only root, `/tmp` tmpfs, cached volume | port 80: `Could not bind TCP Listener … Permission denied`; `--port 8080`: healthy in 45 s, `model_dtype float32`, embeds | two probes |
| 7 | TEI/TGI healthcheck binary | `curl` present in both images | image probes |
| 8 | Hub weights | bge-base 0.44 GB; Qwen2.5-1.5B-Instruct 3.09 GB single file; Qwen2.5-3B first shard 3.97 GB (two shards); all anonymous 200 | `curl -I` |
| 9 | TGI download via Xet | `CAS service error … UnexpectedEof` on retries 1–2 after 3–5 min each; `HF_HUB_DISABLE_XET=1` downloaded 3.09 GB in 11 min | probe logs |
| 10 | TGI 1.5B bf16 on this CPU | shard ready in 18.7 s; warm-up to `/health` 200 ≈ 20 min at default limits, ≈ 10 min at 3072/3584; RSS 5.25 GB (defaults) / 4.24 GB (limits) | probes |
| 11 | TGI chat | `Hello there!` 4 tokens in 6.0 s; 32 tokens in 19.5 s (~1.6 tok/s) | `/v1/chat/completions` |
| 12 | TGI JSON | `{"type":"json","value":{"type":"object"}}` → 422 "Grammar must have a 'properties' field"; with `properties` or `json_schema` → valid JSON; `properties: {}` (± `additionalProperties`) → the model emits `{ }`; `json_object` → 422; prompt-only → ```` ```json ```` fenced object that parses after stripping | probes |
| 13 | TGI as uid 1000, read-only, `--port 8080` | healthy; `ERROR Failed to import python tokenizer OSError: [Errno 30] Read-only file system: 'out'` then "falling back on legacy tokenization"; generation works | probe |
| 14 | Ollama `/v1/chat/completions` with `qwen2.5:3b` | 31 tokens, `choices[0].message.content` present; native `eval` 6.94 tok/s | live Ollama |
| 15 | Enrichment consumer | `Extracted` stores the generated text verbatim as the target column; `GenerateJsonAsync` has one caller | `EnrichmentConsumer.cs:258-284` |

## 10. Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| T1 | TGI `3.3.4-intel-cpu` starts on this AVX2 CPU with Qwen2.5-1.5B in bf16 and serves `/health`, `/info`, chat | §9 rows 10–11, 13 |
| T2 | Chat completions take model/messages/max_tokens/temperature/stream and return `choices[0].message.content`; JSON grammar needs `properties`; `json_object` does not exist | §9 rows 4, 12 |
| T3 | Cache at `/data`, port 80, anonymous Hub download; Xet path unreliable here, classic path works | §9 rows 3, 8, 9 |
| T4 | No privileged caps needed on CPU; runs as uid 1000 read-only with `/tmp` tmpfs and `--port 8080`; cwd write of `out` under read-only root | §9 row 13 (probes ran with no `--privileged`/`--device`) |
| T5 | Both Qwen repos are public safetensors; sizes | §9 row 8 |
| T6 | 1.5B bf16 fits beside the query tier only barely | §9 rows 1, 10 (gate criterion 5 decides) |
| K1 | TEI `/health` is 200/503; readiness-suitable | §9 row 5 |
| K2 | TEI runs as uid 1000 read-only with `/data` + `/tmp` on port 8080; not on 80 | §9 row 6 |
| K3 | TEI cache env `/data` | §9 row 5 |
| K4 | bge-base 0.44 GB | §9 row 8 |
| H1 | `Chart.lock` lists `ollama`; `charts/*.tgz` exist for every subchart (`ollama-0.1.0.tgz` included); `helm dependency build` is the workflow's first step | `Chart.lock:14-16`; `ls charts/*.tgz`; `deploy-validate.yml:25-26,52-53,80` |
| H2 | api/worker templates carry `Embeddings__BaseUrl` (`:124-125` / `:119-120`), the helper include (`:126` / `:121`), `Enrichment__BaseUrl` (`:127-128` / `:122-123`), `Enrichment__ModelId` (`:129-130` / `:124-125`); no `Enrichment__Enabled` today | `grep` |
| H3 | `global.embeddingModels` is read only by `_helpers.tpl:9` and the ollama statefulset pull loop; `generativeModel` by the two deployments and that loop | `grep` over `templates`, `charts` |
| H4 | Every `-ollama` reference in templates: the two deployment URLs, `networkpolicies.yaml:54,85` (api/worker egress) and `:393-418` (ollama ingress/egress) | `grep` |
| H5 | Chart checks: `helm lint` ×5 overlays, `helm template \| kubeconform` ×5, `kube-score` ×5 with documented exemptions (uid 1000 convention), `tfsec`, `terraform fmt`/`init -backend=false`/`validate` per cloud | `.github/workflows/deploy-validate.yml` |
| H6 | Six values files: all carry `ollama:`; `values.yaml`, `-local`, `-laptop` carry `embeddingModels`/`activeEmbeddingModel`; the three cloud files carry only `ollama:` (storage class + pool) | `grep -c` per file |
| H7 | Release name `iverson` (`kind/setup.sh:97`); `iverson-tei-bge-base` is 20 characters | `grep` |
| F1 | Pool maps: `cluster-aws/main.tf:511`, `cluster-gcp/main.tf:239`, `cluster-azure/main.tf:208`; variables `ollama_instance_type`/`ollama_machine_type`/`ollama_vm_size` + `ollama_node_count` (`c7i.2xlarge`, `c2-standard-8`, `Standard_F8s_v2`, count 2); label `iverson.io/node-pool` = pool key (`aws:572`, `gcp:266`, `azure:221`) | `grep` |
| F2 | `operators/main.tf:177` `iverson-ollama`; `outputs.tf:7` references `kubernetes_storage_class.ollama` | `grep` |
| F3 | Terraform 1.9.8 installed; the workflow validates with `init -backend=false` | `terraform version`; workflow `:36-45` |
| F4 | Root modules (`aws/`, `azure/`, `gcp/`, `bootstrap/`) reference no ollama variable | `grep -rln ollama` → none |
| C1 | compose: `ollama :124-136`, `ollama-init :138-149`, `tei-embed :171-179`, api `:387` (env `:401,410-419`, `depends_on … ollama-init :473`), worker `:483` (env `:501,508-509`, `depends_on … ollama-init :545`), volumes `:564-574` (`ollama_data :571`) | `grep -n` |
| C2 | TEI and TGI images carry `curl` | §9 rows 3, 5 |
| C3 | api/worker `depends_on` use `service_healthy`/`service_completed_successfully`; api does not depend on `ollama` itself | compose `:72-86`, `:473`, `:545` |
| C4 | Launcher: `up` list `:20`, `WaitForOllamaAsync` `:28,72-` are its only Ollama references | `grep` |
| S1 | `EnrichmentService`: `GenerateAsync`/`GenerateJsonAsync` → `GenerateInternalAsync(prompt, jsonFormat)` (`:19-53`), the only HTTP site; `format = "json"` only for `Extracted` (`EnrichmentConsumer.cs:274`); `IEnrichmentService` unchanged | read |
| S2 | `EnrichmentServiceTests`: 7 facts; three pin Ollama specifics (`/api/generate` path `:85`, `response` field, `format: json`) | `grep` |
| S3 | `Telemetry.HttpClientName`/`EnrichmentHttpClientName` are used only by `ServiceCollectionExtensions.cs:13,31`, `EmbeddingService.cs:69,97`, `EnrichmentService.cs:34`; no dashboard/alert names `iverson.ollama` | `grep` (prometheus chart: none) |
| S4 | No test relies on the option defaults for a URL: Embeddings tests set `BaseUrl` where they assert one; `EnrichmentServiceTests` pass `modelId` explicitly; `ModelRejectedScenarioTests:518` compares the default family against a never-deployed id | `grep` |
| S5 | Non-test Ollama-naming strings: `Program.cs:410-412`, `EnrichmentConsumer.cs:16`, `SchemaRegistrationOrchestrator.cs:128`, `EmbeddingPrefixes.cs:16`, `EnrichmentServiceOptions.cs:14`, `EmbeddingService.cs:66,112`, `Telemetry.cs:8-9`, `BenchmarkIngestScenario.cs:240`, `ModelRejectedScenario.cs:79`, `VectorSearchScenario.cs:124`, `Launcher/Program.cs:20,28,72-74` | `grep` |
| S6 | Enrichment consumer is worker-only, gated on `Enrichment:Enabled` | `EnrichmentConsumer.cs:376` |
| P1 | `stack.py` Ollama sites `:10,13,27,43,83-84,89,92,171`; `ingest.py` `:26,64,121-124,150,153,531,725,736-737`; `report.py` none | `grep` |
| P2 | kind scripts mention ollama only in the storageSize note (`setup.sh:98`, `setup.ps1:95`); the install command is the `Next:` echo at `:97` | `grep` |
| Q1 | Fixture sites as listed in §3.10; `Preflight.cs` and the harness `Program.cs` probe no Ollama endpoint | `grep` |
| Q2 | SDK unit tests asserting `nomic-embed-text` are annotation tests with no backend | file reads |
| M1 | `qwen2.5:3b` is pulled on the live Ollama | `/api/tags` |
| M2 | SciFact corpus readable | Phase 1 |
| M3 | cgroup v2; `docker stats` samples container memory (podman-backed `docker` here) | `cgroup.controllers`; `docker stats` |
| D1 | Nothing besides the Phase 1 harness docs reads `Embeddings__Models__*`; `EnrichmentConsumerTests:427` sets only `Enrichment:Enabled` in-memory; `StartupNoOpFakes.cs:11` is a comment | `grep` |
| D2 | Prometheus chart and dashboards name no Ollama | `grep` |
| D3 | 48 files reference Ollama outside `docs/`; every one is in §3.5, §3.7, §3.8, §3.9, §3.10 or the deleted subchart, except `Iverson.Server/docs/security/tma.md` and `README.md` (documentation, left to a doc pass) | `grep -rli` |

## 11. Known issues, accepted as out of scope

- **The enrichment gate is expected to fail on this box** (§6.4). The design ships both end states; the 3B
  candidate on an AMX node is a separate measurement Ben may schedule.
- **TGI's Xet download path** is disabled by env; if the Hub retires the classic path the pods need a
  pre-populated cache.
- **Laptop profile runs without enrichment** (`enrichmentEnabled: false`); the kind smoke on that profile does
  not exercise the worker's enrichment path.
- **The TGI legacy-tokenization fallback** is what the read-only probe exercised; `workingDir: /tmp` is the
  designed fix and is verified by the kind smoke's absence of the `Errno 30` line, not by a design probe.
- **`README.md` and `Iverson.Server/docs/security/tma.md`** still describe Ollama; a documentation pass is not
  part of this spec.
