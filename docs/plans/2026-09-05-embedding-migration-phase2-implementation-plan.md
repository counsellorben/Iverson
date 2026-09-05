# Embedding Migration Phase 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-05-embedding-migration-phase2-design.md` (commit SHA: `107eba5`)

**Goal:** Make bge-base on TEI the default embedding backend on every deployment surface, replace Ollama's enrichment role with TGI behind a measured gate, and remove Ollama from chart, compose, Terraform, Launcher, scripts and code on a pass — or leave it serving enrichment only on a fail.

**Architecture:** Phase A switches embeddings everywhere (a `tei` subchart rendered per `global.embeddingModels` entry, compose `tei-embed` as a default service, code defaults, scripts, five conformance fixtures) after clearing the compose stack's 36 nomic-pinned schema rows and restoring Phase 1's bge-base SciFact index. Phase B ports `EnrichmentService` to the OpenAI-compatible `/v1/chat/completions` (backend-neutral, with fenced-block/balanced-brace JSON extraction and a source-text cap in the consumer), adds a compose `tgi` service, and measures TGI-1.5B against Ollama-3B with `enrich_bench.py`. The verdict selects Phase C (a `tgi` subchart, Ollama deleted everywhere, the Terraform pool rename) or Phase C′ (nothing further: what landed is already the Ollama-for-enrichment-only shape). A kind smoke on the laptop profile closes either branch.

**Tech stack:** .NET 10 / C# (`Iverson.Embeddings`, `Iverson.Api`; xunit 2.9.3, FluentAssertions 7.0.0, NSubstitute 5.3.0), Helm 3.16 charts checked by `helm lint` / `kubeconform` (k8s 1.30 schemas) / `kube-score` 1.19.0, Terraform 1.9.8 (CI pins 1.7.5), Docker Compose 2.40.3 (podman-backed `docker`), TEI `cpu-1.8`, TGI `3.3.4-intel-cpu`, Python 3.14 (stdlib only), kind 0.24.

---

## Global Constraints

Copied from the spec; every task must hold to them.

- **Ports.** In the chart TEI and TGI listen on `8080` (`--port 8080`; neither binds 80 as uid 1000); their Services are headless on `port: 8080` with no `targetPort` (a headless Service does no remapping, and kube-score marks a StatefulSet governed by a non-headless Service CRITICAL); every in-cluster URL is `:8080`. Compose containers run as root and keep the images' port 80 (`tei-embed` 8091→80, `tgi` 8092→80).
- **TGI runtime (spec §3.3, §3.7):** `--dtype bfloat16 --max-input-tokens 3072 --max-total-tokens 3584 --max-batch-prefill-tokens 3072`, env `HF_HUB_DISABLE_XET=1`, `workingDir: /tmp` in the chart, 1 GiB shm, a first start of ~25 minutes on this box (download + warm-up).
- **Values shape (spec §3.1):** `global.embeddingModels: [{name: BAAI/bge-base-en-v1.5, slug: bge-base}]`, `global.activeEmbeddingModel: BAAI/bge-base-en-v1.5`, `global.generativeModel: "Qwen/Qwen2.5-1.5B-Instruct"`, `global.enrichmentEnabled` (Phase C). `values-local.yaml` and `values-laptop.yaml` replace their `embeddingModels` override with that single entry and inherit `activeEmbeddingModel`. On Azure the Terraform pool map's `label` attribute is set with the key.
- **Enrichment client (spec §3.5):** `POST {BaseUrl}/v1/chat/completions` with `model`, one user message, `max_tokens` 256, `temperature` 0, `stream` false; read `choices[0].message.content`; `GenerateJsonAsync` sends no `response_format`, extracts the first fenced block else the first balanced `{ … }` span, parses it, throws `InvalidOperationException` naming the first 200 characters otherwise. `MaxSourceChars = 8_000` cuts `BuildSourceText`'s result in `EnrichmentConsumer` before prompt assembly, never the assembled prompt.
- **Gate (spec §6.4):** TGI-1.5B passes when (1) mean `ChunkContext` wall ≤ Ollama-3B's, (2) p95 wall over all 75 prompts < 120 s, (3) zero failed or empty generations, (4) all five `Extraction` outputs parse, (5) with the query tier and `tgi` up, minimum `MemAvailable` > 500 MB and no OOM kill. Ben's side-by-side reading can veto a numeric pass. Verdict in `docs/plans/2026-09-GATE-enrichment-backend.md`. Expected outcome on this box: fail (6.9 vs ~1.6 tok/s).
- **Ordering (spec §7):** Phase A before Phase B; Phase B's measurement before any Ollama deletion; exactly one of Phase C / Phase C′ executes, chosen by Task 8's verdict.
- **Box discipline:** `/home/ben/repositories/iverson-benchmark-corpora/` is read-only except the new `enrichment-bench-<date>/` directory; benchmark snapshots are restored, never deleted; the kind smoke runs on `values-laptop.yaml` with the compose stack stopped first.
- Tests are written to fail against a named mutation; `docs/plans/` and `docs/criticalreviews/` are gitignored (`git add -f`).

## File Structure

**Create**
- `Iverson.Server/deploy/helm/iverson/charts/tei/{Chart.yaml,values.yaml,templates/statefulset.yaml,templates/service.yaml,templates/pdb.yaml}` — one StatefulSet/Service/PDB per `embeddingModels` entry (Task 2).
- `Iverson.Server/deploy/helm/iverson/charts/tgi/{Chart.yaml,values.yaml,templates/statefulset.yaml,templates/service.yaml,templates/pdb.yaml}` — the generative server (Task 9, Phase C only).
- `Iverson.Server/Iverson.LoadTest/scripts/enrich_bench.py` — the enrichment measurement (Task 8).
- `docs/plans/2026-09-GATE-enrichment-backend.md` — the verdict (Task 8).
- (outside this repo) `~/repositories/iverson-benchmark-corpora/enrichment-bench-<date>/{results.json,side-by-side.md}`.

**Modify**
- `Iverson.Server/Iverson.Embeddings/{EmbeddingServiceOptions,EnrichmentServiceOptions,EnrichmentService,Telemetry,EmbeddingService,EmbeddingPrefixes}.cs`; `Iverson.Server/Iverson.Api/{Program.cs,Grpc/SchemaRegistrationOrchestrator.cs,Consumers/EnrichmentConsumer.cs}`; `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkIngestScenario.cs`; `Iverson.Server/Iverson.ClientConformance/Scenarios/{ModelRejectedScenario,VectorSearchScenario,InheritedModelScenario}.cs`; `Iverson.Server/Iverson.Launcher/Program.cs`.
- `Iverson.Server/deploy/helm/iverson/{Chart.yaml,Chart.lock,charts/*.tgz,values.yaml,values-local.yaml,values-laptop.yaml,values-aws.yaml,values-azure.yaml,values-gcp.yaml,templates/_helpers.tpl,templates/networkpolicies.yaml,charts/api/templates/deployment.yaml,charts/worker/templates/deployment.yaml,charts/ollama/templates/statefulset.yaml}`; `charts/ollama/**` deleted in Phase C.
- `Iverson.Server/deploy/terraform/modules/{cluster-aws,cluster-gcp,cluster-azure}/{main.tf,variables.tf}`, `modules/operators/{main.tf,outputs.tf}`.
- `Iverson.Server/docker-compose.yml`; `Iverson.Server/Iverson.LoadTest/scripts/{stack.py,ingest.py}`; `Iverson.Server/deploy/kind/{setup.sh,setup.ps1}`.
- Conformance fixtures: `Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/{Models/S11ModelDotnet.cs,Models/S12ModelDotnet.cs,Program.cs}`, `Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance/{models/S11ModelJava.java,models/S12DeclaredJava.java,models/S12InheritedJava.java,Driver.java}`, `Iverson.Clients/Python/conformance/{models.py,driver.py}`, `Iverson.Clients/TypeScript/conformance/{models.ts,driver.ts}`, `Iverson.Clients/Go/conformance/{models.go,main.go}`; doc comments in `Iverson.Clients/Python/iverson_client/annotations.py`, `Iverson.Clients/Java/client/src/main/java/io/iverson/client/annotations/{IversonSummary,IversonKeywords,IversonExtracted}.java`, `Iverson.Clients/TypeScript/src/annotations.ts`.

**Test**
- `Iverson.Server/Iverson.Embeddings.Tests/EnrichmentServiceTests.cs` — rewritten for the chat shape (Task 7).
- `Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs` — the source-text cap case (Task 7).

## Inherited from spec

Verified by `thorough-brainstorming` and CDR rounds 1–2 (spec §10) and NOT re-verified here:

- T1–T6 TGI `3.3.4-intel-cpu` runs 1.5B bf16 on this AVX2 box, serves `/health`, `/info`, chat completions; JSON grammars need `properties`; cache `/data`, port 80, Xet download unreliable, classic path works; no privileged caps; runs as uid 1000 read-only on `--port 8080`; the two Qwen repos are public; 1.5B fits only barely beside the stack.
- K1–K6 TEI `/health` 200/503; runs as uid 1000 read-only on 8080; cache `/data`; bge-base 0.44 GB; headless Services do no port remapping; kube-score requires a headless governing Service.
- H1–H7 `Chart.lock` and `charts/*.tgz` exist for every subchart; api/worker template lines for the env; only the helper and the ollama statefulset read `global.embeddingModels`; every `-ollama` reference in templates; the workflow's three chart checks; the six values files' contents; release name `iverson`.
- F1–F4 pool maps, variables, labels per cloud (Azure via `label`); `iverson-ollama` storage class and `outputs.tf:7`; Terraform validate works offline; root modules pass no ollama variable.
- C1–C6 compose line ranges; both images carry `curl`; api/worker `depends_on` shapes; Launcher's Ollama sites; 36 nomic-pinned schema rows; the conformance fixtures own no Qdrant collections.
- S1–S9 `EnrichmentService`'s single HTTP site and `GenerateJsonAsync`'s one caller; the six enrichment facts; the telemetry names' consumers; no test relies on option-default URLs; the Ollama-naming strings; enrichment is worker-only; Ollama's prose-wrapped chat JSON and head-first 4,096-token truncation; the cap sits before the loop-prevention hash.
- P1–P3, Q1–Q2, M1–M4, D1–D3 script Ollama sites; kind notes; fixture sites and the never-deployed override id; SDK unit tests are backend-free; `qwen2.5:3b` is pulled; the SciFact corpus is readable; `docker stats` samples RSS; `benchmark-query` re-registers `BenchmarkDocument`; the two SciFact key maps are byte-identical; nothing else reads `Embeddings__Models__*`; the Ollama file sweep is complete.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time (2026-09-05, `main` @ `107eba5`, compose stack up):

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | `charts/tei/`, `charts/tgi/`, `scripts/enrich_bench.py`, `docs/plans/2026-09-GATE-enrichment-backend.md` do not exist; `charts/ollama/templates/{pdb,service,statefulset}.yaml` exist as the templates to mirror | `ls` |
| P2 | File path | `setup.sh:97` names the app install as `helm upgrade --install iverson . -f values-local.yaml -n iverson`; `deploy/kind/build-and-load-image.sh` builds `iverson-api:0.1.0`, re-tags it `docker.io/library/iverson-api:0.1.0` (the podman fix) and `kind load`s it into cluster `iverson`; `kind-config.yaml` is a one-node cluster with the default CNI disabled (Calico installed by `setup.sh`); `setup.sh` installs the five operators + metrics-server and does not create the cluster itself | file reads; `setup.sh:1-12` podman note; `build-and-load-image.sh:10-14` |
| P3 | Signature | `IEnrichmentService` = `ModelId`, `GenerateAsync(string, ct)`, `GenerateJsonAsync(string, ct)`; only `EnrichmentServiceTests.cs:37` constructs `EnrichmentService`; `EnrichmentConsumer`'s constructor is as `BuildSut()` calls it (`EnrichmentConsumerTests.cs:135-137`) and gains no parameter | `grep "new EnrichmentService("`; test file read |
| P4 | Signature | `EnrichmentConsumerTests` configures `_enrichment.GenerateJsonAsync(...).Returns(...)` in its constructor (`:75`), drives `HandleAsync(Key, Event(EntityEventType.Updated), ct)`, feeds the row with `_entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Key).Returns(RowJson(body))`, and `EnrichedArticle()` carries an `Extracted` target with hint `"the author's stated conclusion"` | `EnrichmentConsumerTests.cs:74-75,81-104,119,258` |
| P5 | Signature | The extraction prompt is `string.Format(EnrichmentPrompts.Extraction, sourceText) + "\n\nExtract specifically: {hint}"`; `EnrichmentPrompts.Extraction` starts `"Extract structured information"`; `sourceText` is assigned at `EnrichmentConsumer.cs:129` and hashed at `:130` | `EnrichmentConsumer.cs:129-130,273-276`; `EnrichmentPrompts.cs:12-13` |
| P6 | Signature | `EnrichmentService` today: `GenerateInternalAsync(prompt, jsonFormat, ct)` at `:25-66`, `_jsonOpts` camelCase, `Telemetry.EnrichmentHttpClientName` client, activity tags `enrichment.model/input_chars/json_format/output_chars` | file read |
| P7 | Signature | Launcher: `WaitForOllamaAsync(url, modelName, ct)` at `:72-103` is an `HttpClient` GET loop with `Task.Delay(3000)`; `WaitForPortAsync(host, port, name, ct)` at `:105`; compose `up` list at `:20` | file read |
| P8 | Signature | `stack.py`: `TIERS` `:83-84`, `CONTAINER` `:91-97`, `READY_CHECKS` `:169-172`, `wait_http_200(url, timeout)`; `ingest.py`: `OLLAMA_URL` at `:150` and its only use at `:736-737` | `grep` |
| P9 | Code validity | Helm globals reach subcharts (the ollama statefulset reads `.Values.global.embeddingModels`); `condition: global.<flag>` has the starrocks precedent (`Chart.yaml` `condition: global.engagementEnabled`); the ollama subchart's `values.yaml` carries exactly `replicas, storageSize, storageClassName, imageTag, resources, nodeSelector, tolerations`, the keys its templates read | `charts/ollama/templates/statefulset.yaml:56-62`; `Chart.yaml:17`; `charts/ollama/values.yaml` |
| P10 | Command | Local `helm` 3.16.4, `kubeconform`, `kube-score` 1.19.0, `terraform` 1.9.8, `kind` 0.24, `kubectl` present; the workflow's exact invocations: `helm lint <chart> -f <profile>`; `helm template iverson <chart> -f <profile> \| kubeconform -kubernetes-version 1.30.0 -summary -ignore-missing-schemas`; `kube-score score --ignore-test pod-networkpolicy --ignore-test container-image-pull-policy --ignore-test container-security-context-user-group-id -`; `terraform -chdir=<cloud> fmt -check`, `init -backend=false`, `validate` | `deploy-validate.yml:28-33,55-62,80-96,110-126`; `command -v` |
| P11 | Command | On a scratch copy of the current chart `helm lint -f values-laptop.yaml` passes, kubeconform reports 63 valid / 0 invalid, and kube-score **already exits 1** on pre-existing objects (e.g. `iverson-starrocks-create-user-egress`) while `iverson-ollama` passes — so this plan asserts kube-score cleanliness on the new `-tei-`/`-tgi` objects only, never exit 0. `helm dependency build` succeeds ONLY while `Chart.yaml`'s dependency list is unchanged; once a dependency is added or removed it exits 1 with `the lock file (Chart.lock) is out of sync with the dependencies file (Chart.yaml)`, whereas `helm dependency update` regenerates `Chart.lock`, packages the new subchart and prunes a removed one's tarball — so this plan runs `update`, commits the regenerated lock, and CI's `build` (`deploy-validate.yml:25,52,80`) stays in sync | scratch runs 2026-09-05 (plan-write, CIR-1) |
| P12 | Command | `dotnet test Iverson.Server/Iverson.Embeddings.Tests/Iverson.Embeddings.Tests.csproj`; `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~Iverson.Api.Tests.Consumers.EnrichmentConsumerTests"` (substitutes only, no containers); `dotnet test Iverson.Server/Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj` | csproj paths; `EnrichmentConsumerTests.cs:17-34` |
| P13 | Command | The conformance harness: `cd Iverson.Server/Iverson.ClientConformance && dotnet run` with `IVERSON_CLIENT_ID=dev-iverson-loadtest-client-id`, `IVERSON_CLIENT_SECRET=dev-only-not-for-production-loadtest-secret-0123456789`, `IVERSON_TOKEN_ENDPOINT=http://localhost:9000/application/o/token/`, `IVERSON_CLIENT_SCOPE="schema_admin tenant_id_loadtest"`; a full matrix's exit code is the coverage claim (`-- --languages dotnet,python` runs a subset, runbook `:44`); toolchains present: node 24, go 1.22, java 21 + maven 3.9, python 3.14, dotnet 10 | `docs/runbooks/client-conformance-matrix.md:14-46`; `command -v` |
| P14 | Command | The compose stack today: 36 `_iverson_schema` rows; Qdrant collections `benchmark_documents_tenant_bypass`, `benchmark_documents_chunks_tenant_bypass`, `vector_docs_tenant_bypass`, `vector_docs_chunks_tenant_bypass`, `iverson-probe` (the API's own probe collection, left alone); ports 8091/8092 free, 11434 listening | `psql`, Qdrant `GET /collections`, `ss` |
| P15 | Command | `scifact-bge-base-qdrant-snapshots/` holds two `.snapshot` files named with peer `6802952876034638`, so `scifact-512-qdrant-snapshots/RESTORE.md`'s name-derivation loop restores them unchanged | `ls`; `RESTORE.md` |
| P16 | Code validity | `JsonSerializer.Serialize(new { model, messages = new[]{ new { role, content } }, max_tokens = 256, temperature = 0, stream = false }, camelCase)` yields `{"model":"m","messages":[{"role":"user","content":"hi"}],"max_tokens":256,"temperature":0,"stream":false}` — the underscore names survive the camelCase policy | scratch console |
| P17 | Code validity | The `ExtractJson` function in Task 7 (fenced-block regex, else a string-aware brace-depth scan) returns the object from Ollama's prose-wrapped fenced reply, from a bare object with `}` inside a string and trailing prose, and from a bare object, and throws on text with no object | scratch console, four inputs |
| P18 | Code validity | Compose accepts `start_period: 25m` (renders `25m0s`); podman's `docker stats --no-stream --format '{{.Name}} {{.MemUsage}}'` prints `name 5.291GB / 7.516GB`; `/proc/meminfo` `MemAvailable` in kB | scratch compose `config`; earlier probe output; `python3 -c` |
| P19 | Consumer impact | `values-laptop.yaml`'s CPU requests total 1,450m (postgres/kafka/qdrant/ollama 250m each, api/worker/others) against the 4-CPU host that the kind node inherits, with ~2.1 CPU of system/operator overhead measured at 2026-07-27; a TEI request of 500m would overcommit (4.05), 250m fits (3.8) in Phase C′ and 3.55 in Phase C — so the laptop `tei.resources.requests` is `250m`/`1Gi`, not the spec's `500m` | `values-laptop.yaml:9-11,38-100`; `nproc` = 4 |
| P20 | Consumer impact | Renaming `Telemetry.HttpClientName`/`EnrichmentHttpClientName` touches only `ServiceCollectionExtensions.cs:13,31`, `EmbeddingService.cs:69,97`, `EnrichmentService.cs:34`; `IdpAdminClient.HttpClientName` is a different constant | `grep HttpClientName` |
| P21 | Consumer impact | podman's `pids_limit = -1` is already set in `~/.config/containers/containers.conf`, the prerequisite `setup.sh` documents for kind under podman | `grep pids_limit` |
| P22 | Ordering | Task 2 needs nothing from Task 1; Task 4 rebuilds the API image so Task 1's defaults ride along but the compose env overrides them; Task 5's harness needs Task 4's row clear; Task 6's `benchmark-query` needs Task 4's restored index; Task 7 is code-only; Task 8 needs Task 4's compose state and, for Phase C, Task 7; Task 9 needs Tasks 2–3; Task 10 needs Tasks 7–8; Task 11 needs Task 9 (pass) or Task 2 (fail) | task text |
| P24 | Code validity | `Iverson.Embeddings` grants `InternalsVisibleTo` to nothing (`EmbeddingPrefixes.cs:9` says so; no attribute in the csproj), so `EnrichmentServiceTests` asserts the literal `256` and drives `ExtractJson` through `GenerateJsonAsync`; `Iverson.Api` grants it to `Iverson.Api.Tests` (`Iverson.Api.csproj:10-11`), so the consumer test reads `EnrichmentConsumer.MaxSourceChars` | csproj reads |
| P25 | Command | The API runtime image (`Iverson.Api/Dockerfile:16`, `mcr.microsoft.com/dotnet/aspnet:10.0`) ships `bash` but neither `wget` nor `curl` (`docker exec iverson-api sh -c 'command -v wget; command -v curl; command -v bash'` prints only `/usr/bin/bash`; `docker-compose.yml:453-456` records the same for the healthcheck) — the kind smoke's in-cluster probe uses the bash `/dev/tcp` idiom | CIR-1 |
| P26 | Consumer impact | No registerable type in the repo declares an enrichment target: `[IversonSummary]`/`[IversonKeywords]`/`[IversonExtracted]` appear in none of the five conformance drivers, the `Iverson.LoadTest` entities or any sample; `VectorDoc.cs:10-12` is deliberately annotation-free — an end-to-end enrichment check needs a throwaway type of its own | CIR-1 |
| P27 | Ordering | Ollama keeps a model resident for 5 minutes after its last request by default (`keep_alive`), so a backend measured right after Ollama starts its window with Ollama's model still loaded — the measurement runs TGI first | CIR-1 |
| P28 | Consumer impact | With the TEI pod added, the laptop profile's rendered container memory requests rise from ~4.4 Gi to ~5.44 Gi (plus ~1 Gi of CNPG/Strimzi-owned pods the chart does not render) on a 10.19 GB box — tight, not disqualifying; Task 11 records any Pending pod with its reason | CIR-1, rendered profile |
| P29 | Code validity | `charts/ollama/templates/statefulset.yaml:53-62` is one `sh -c` block scalar: `ollama serve &`, `sleep 5`, the `range .Values.global.embeddingModels` pull loop (`:59-61`), then the generative pull; `ollama pull` is a client command that fails without the background server, so only the loop is deleted | CIR-2 |
| P30 | File path | `values-laptop.yaml:14` is `tracingEnabled: false      # no jaeger`; the `embeddingModels` comment + list the plan replaces starts at `:15` — a `:14-26` replacement would turn jaeger on at `values.yaml`'s production sizing | CIR-2 |
| P31 | Command | kube-score 1.19.0's `networkpolicy-targets-pod` marks a NetworkPolicy whose `podSelector` matches no rendered pod CRITICAL — the parent chart's `tgi-*` policies must be guarded by the same `global.enrichmentEnabled` flag that gates the subchart | CIR-2, laptop render |
| P32 | Sibling sweep | Per-file `grep -n -i ollama` inventory of the files Tasks 1 and 6 edit by line: `IversonExtracted.java:9,17`, `annotations.ts:71,181,197,219`, `stack.py:89` are the complete sets; `IversonSummary.java`/`IversonKeywords.java` have `:9` only; `annotations.py`'s five sites are all listed | CIR-2 |
| P33 | Command | A double-quoted `python3 -c "…"` argument containing backticks is command-substituted by bash; the plan's verification one-liners use single quotes around the Python | CIR-2, executed |
| P34 | Code validity | The named mutation "no parse check" is falsified only by a reply whose first fenced block / balanced span does not parse; a reply with no `{` at all throws on the null-candidate path with or without `JsonDocument.Parse` | CIR-2, scratch suite |
| P35 | File path | `.gitignore:47` ignores `docs/criticalreviews/` and `:49` ignores `docs/plans/`, so every commit of those files uses `git add -f` | CIR-2 |
| P36 | Consumer impact | `ModelRejectedScenario.cs:263-297` asserts four other substrings of the orchestrator's guard message and never its Ollama tail, so Task 1's rewording cannot fail the live matrix | CIR-2 |
| P23 | Sibling sweep | Every `.Values.tei.*` / `.Values.tgi.*` key the new templates read is defaulted in the subchart's `values.yaml` (the same seven keys as ollama's); every `$.Release.Name` inside a `range` uses the `$` root; the instance sizes `c7i.xlarge`, `c2-standard-4`, `Standard_F4s_v2` are one step below the ollama defaults in each cloud's catalogue | Task 2/9 templates; `variables.tf` |

## Tasks

### Task 1: Embedding-side defaults, telemetry names, wording (Phase A)

**Files:**
- Modify: `Iverson.Server/Iverson.Embeddings/EmbeddingServiceOptions.cs:6-7`; `Iverson.Server/Iverson.Embeddings/Telemetry.cs:8-9`; `Iverson.Server/Iverson.Embeddings/EmbeddingService.cs:66,112`; `Iverson.Server/Iverson.Embeddings/EmbeddingPrefixes.cs:16`; `Iverson.Server/Iverson.Api/Program.cs:410-412`; `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:128`; `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkIngestScenario.cs:240`; `Iverson.Server/Iverson.ClientConformance/Scenarios/ModelRejectedScenario.cs:79`; `Iverson.Server/Iverson.ClientConformance/Scenarios/VectorSearchScenario.cs:124`; `Iverson.Clients/Python/iverson_client/annotations.py:89-90,170-180,230`; `Iverson.Clients/Java/client/src/main/java/io/iverson/client/annotations/{IversonSummary,IversonKeywords,IversonExtracted}.java:9`; `Iverson.Clients/TypeScript/src/annotations.ts:71`

- [ ] **Step 1: Defaults.** `EmbeddingServiceOptions.cs`:
```csharp
    public string  BaseUrl        { get; set; } = "http://localhost:8091";
    public string  ModelId        { get; set; } = "BAAI/bge-base-en-v1.5";
```
`Telemetry.cs`:
```csharp
    internal const string HttpClientName = "iverson.embeddings";
    internal const string EnrichmentHttpClientName = "iverson.enrichment";
```

- [ ] **Step 2: Wording.** Each site loses its Ollama-specific claim without changing behaviour:
  - `SchemaRegistrationOrchestrator.cs:128`: `"'{priorModel}' must remain pulled in this deployment's Ollama — every other type still registered under it needs it to stay reachable."` → `"'{priorModel}' must remain served by this deployment's embedding backend — every other type still registered under it needs it to stay reachable."`
  - `Program.cs:410-412`: the three comment lines → `// The embedding backend is commonly still downloading its model on a first install. Dying here crash-loops both roles; CrashLoopBackOff then delays recovery by up to five minutes AFTER the backend is healthy. Initialization retries lazily at the one place that needs the dimension (schema registration), so continue.`
  - `EmbeddingService.cs:66`: `// model_id; Ollama answers 404 there, which makes this a no-op on Ollama.` → `// model_id; a backend without /info (Ollama answered 404) makes this a no-op.` and `:112`: `on both Ollama and TEI` → `on TEI (and on Ollama's OpenAI-compatible route)`.
  - `EmbeddingPrefixes.cs:16`: keep "Ollama ids carry tags" (it describes the id grammar) — no change.
  - `BenchmarkIngestScenario.cs:240`: `through CPU Ollama` → `through a CPU embedding backend`.
  - `ModelRejectedScenario.cs:79`: `a name no Ollama…` → `a name no embedding backend serves`; `VectorSearchScenario.cs:124`: `the same Ollama instance` → `the same embedding backend`.
  - `annotations.py:89-90,170,175,180,230`, the three Java annotation Javadocs at `:9` plus `IversonExtracted.java:17` ("the Ollama prompt" → "the extraction prompt"), and `annotations.ts:71,181,197,219`: `Ollama enrichment targets` → `enrichment targets`; `Ollama-driven`/`Ollama-generated` → `model-generated`; `the Ollama model this type's …` → `the embedding model this type's …`.

- [ ] **Step 3: Build and test.**
```bash
cd /home/ben/repositories/Iverson
dotnet build Iverson.slnx 2>&1 | grep -E 'error|Build succeeded'
dotnet test Iverson.Server/Iverson.Embeddings.Tests/Iverson.Embeddings.Tests.csproj 2>&1 | grep -E 'Passed!|Failed!'          # 48/48
dotnet test Iverson.Server/Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj 2>&1 | grep -E 'Passed!|Failed!'
grep -rn 'iverson\.ollama' --include=*.cs Iverson.Server | grep -v '/bin/\|/obj/' | wc -l    # 0
grep -li ollama Iverson.Clients/Python/iverson_client/annotations.py Iverson.Clients/Java/client/src/main/java/io/iverson/client/annotations/Iverson{Summary,Keywords,Extracted}.java Iverson.Clients/TypeScript/src/annotations.ts    # prints nothing (P32)
```
(The ClientConformance.Tests suite pins the scenario messages; it is the guard for the wording edits.)

- [ ] **Step 4: Commit.**
```bash
git add Iverson.Server/Iverson.Embeddings/EmbeddingServiceOptions.cs Iverson.Server/Iverson.Embeddings/Telemetry.cs Iverson.Server/Iverson.Embeddings/EmbeddingService.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkIngestScenario.cs Iverson.Server/Iverson.ClientConformance/Scenarios/ModelRejectedScenario.cs Iverson.Server/Iverson.ClientConformance/Scenarios/VectorSearchScenario.cs Iverson.Clients/Python/iverson_client/annotations.py Iverson.Clients/Java/client/src/main/java/io/iverson/client/annotations/IversonSummary.java Iverson.Clients/Java/client/src/main/java/io/iverson/client/annotations/IversonKeywords.java Iverson.Clients/Java/client/src/main/java/io/iverson/client/annotations/IversonExtracted.java Iverson.Clients/TypeScript/src/annotations.ts
git commit -m "default the embedding client to bge-base on TEI, rename the embedding and enrichment HTTP clients, and drop Ollama from the comments that described them"
```

### Task 2: Helm — the `tei` subchart and the Phase A chart shape

**Files:**
- Create: `Iverson.Server/deploy/helm/iverson/charts/tei/Chart.yaml`, `charts/tei/values.yaml`, `charts/tei/templates/statefulset.yaml`, `charts/tei/templates/service.yaml`, `charts/tei/templates/pdb.yaml`
- Modify: `Chart.yaml:24-27` (+ dependency), `Chart.lock`, `charts/*.tgz` (rebuilt), `values.yaml:29-54,118-129`, `values-local.yaml:10-22,67-77`, `values-laptop.yaml:15-26,57-68`, `values-aws.yaml:59-67`, `values-azure.yaml:59-67`, `values-gcp.yaml:60-68`, `templates/_helpers.tpl:22-34`, `charts/api/templates/deployment.yaml:124-126`, `charts/worker/templates/deployment.yaml:119-121`, `templates/networkpolicies.yaml:54-55,85-86` (+ two policies), `charts/ollama/templates/statefulset.yaml:59-61`

**Interfaces**
- Produces: the `tei` subchart, `iverson.embeddingBaseUrl`, `Embeddings__Models__N__*` rendering, the values shape Tasks 9/11 build on.

- [ ] **Step 1: The subchart.** `charts/tei/Chart.yaml`:
```yaml
apiVersion: v2
name: tei
description: Text Embeddings Inference — one server per global.embeddingModels entry
type: application
version: 0.1.0
appVersion: "1.0.0"
```
`charts/tei/values.yaml`:
```yaml
replicas: 2
storageSize: 2Gi
storageClassName: ""
imageTag: "cpu-1.8"
resources:
  requests: { cpu: "2", memory: "2Gi", ephemeral-storage: "1Gi" }
  limits: { cpu: "4", memory: "4Gi", ephemeral-storage: "2Gi" }
nodeSelector: {}
tolerations: []
```
`charts/tei/templates/statefulset.yaml` (ranges over the global list; `$` is the chart root inside the range):
```yaml
{{- range .Values.global.embeddingModels }}
apiVersion: v1
kind: ServiceAccount
metadata:
  name: {{ $.Release.Name }}-tei-{{ .slug }}
automountServiceAccountToken: false
---
apiVersion: apps/v1
kind: StatefulSet
metadata:
  name: {{ $.Release.Name }}-tei-{{ .slug }}
spec:
  serviceName: {{ $.Release.Name }}-tei-{{ .slug }}
  replicas: {{ $.Values.replicas }}
  selector:
    matchLabels:
      app: {{ $.Release.Name }}-tei-{{ .slug }}
  template:
    metadata:
      labels:
        app: {{ $.Release.Name }}-tei-{{ .slug }}
        iverson.io/component: tei
    spec:
      serviceAccountName: {{ $.Release.Name }}-tei-{{ .slug }}
      automountServiceAccountToken: false
      nodeSelector:
        {{- toYaml $.Values.nodeSelector | nindent 8 }}
      tolerations:
        {{- toYaml $.Values.tolerations | nindent 8 }}
      affinity:
        podAntiAffinity:
          preferredDuringSchedulingIgnoredDuringExecution:
            - weight: 100
              podAffinityTerm:
                labelSelector:
                  matchLabels:
                    app: {{ $.Release.Name }}-tei-{{ .slug }}
                topologyKey: kubernetes.io/hostname
      securityContext:
        runAsNonRoot: true
        runAsUser: 1000
        runAsGroup: 1000
        fsGroup: 1000
        seccompProfile: { type: RuntimeDefault }
      containers:
        - name: tei
          image: "ghcr.io/huggingface/text-embeddings-inference:{{ $.Values.imageTag }}"
          imagePullPolicy: IfNotPresent
          # --port 8080: the image listens on 80, which uid 1000 cannot bind. --auto-truncate: without it
          # TEI answers 413 above 512 tokens (Phase 1 spec §3.4).
          args: ["--model-id", {{ .name | quote }}, "--auto-truncate", "--port", "8080"]
          securityContext:
            allowPrivilegeEscalation: false
            readOnlyRootFilesystem: true
            capabilities: { drop: ["ALL"] }
          ports:
            - containerPort: 8080
          resources:
            requests:
              cpu: {{ $.Values.resources.requests.cpu | quote }}
              memory: {{ $.Values.resources.requests.memory | quote }}
              ephemeral-storage: {{ index $.Values.resources.requests "ephemeral-storage" | quote }}
            limits:
              cpu: {{ $.Values.resources.limits.cpu | quote }}
              memory: {{ $.Values.resources.limits.memory | quote }}
              ephemeral-storage: {{ index $.Values.resources.limits "ephemeral-storage" | quote }}
          # The first start downloads the weights from the Hub into /data; every later start loads
          # them from the PVC in under a minute.
          startupProbe:
            httpGet: { path: /health, port: 8080 }
            periodSeconds: 10
            failureThreshold: 30
          readinessProbe:
            httpGet: { path: /health, port: 8080 }
            periodSeconds: 10
          volumeMounts:
            - name: tei-data
              mountPath: /data
            - name: tmp
              mountPath: /tmp
      volumes:
        - name: tmp
          emptyDir: {}
  volumeClaimTemplates:
    - metadata:
        name: tei-data
      spec:
        accessModes: ["ReadWriteOnce"]
        storageClassName: {{ $.Values.storageClassName | quote }}
        resources:
          requests:
            storage: {{ $.Values.storageSize }}
---
{{- end }}
```
`charts/tei/templates/service.yaml` (headless, Service port equal to the container port — kube-score requires a headless governing Service and a headless Service does no port remapping):
```yaml
{{- range .Values.global.embeddingModels }}
apiVersion: v1
kind: Service
metadata:
  name: {{ $.Release.Name }}-tei-{{ .slug }}
spec:
  clusterIP: None
  selector:
    app: {{ $.Release.Name }}-tei-{{ .slug }}
  ports:
    - port: 8080
---
{{- end }}
```
`charts/tei/templates/pdb.yaml`:
```yaml
{{- if gt (.Values.replicas | int) 1 }}
{{- range .Values.global.embeddingModels }}
apiVersion: policy/v1
kind: PodDisruptionBudget
metadata:
  name: {{ $.Release.Name }}-tei-{{ .slug }}
spec:
  maxUnavailable: 1
  selector:
    matchLabels:
      app: {{ $.Release.Name }}-tei-{{ .slug }}
---
{{- end }}
{{- end }}
```
Parent `Chart.yaml`: add after the `ollama` dependency
```yaml
  - name: tei
    version: "0.1.0"
    repository: "file://charts/tei"
    condition: tei.enabled
```

- [ ] **Step 2: Values.** `values.yaml`: replace the `embeddingModels` block and its comment (`:29-50`) with
```yaml
  # Every entry is served by the tei subchart (one StatefulSet + headless Service per entry). `slug`
  # is the DNS-safe suffix of that entry's objects: <release>-tei-<slug>. The prefix keys stay
  # three-state (omitted = derive from the model family; "" = deliberately none).
  embeddingModels:
    - name: BAAI/bge-base-en-v1.5
      slug: bge-base
```
`activeEmbeddingModel: BAAI/bge-base-en-v1.5` with its comment's "404 from Ollama" → "an identity mismatch from the backend's /info". Add after the `ollama:` block:
```yaml
tei:
  enabled: true
  replicas: 2
  storageSize: 2Gi            # bge-base weights are 0.44 GB
  storageClassName: ""
  imageTag: "cpu-1.8"
  resources:
    requests: { cpu: "2", memory: "2Gi", ephemeral-storage: "1Gi" }
    limits: { cpu: "4", memory: "4Gi", ephemeral-storage: "2Gi" }
  nodeSelector: {}
  tolerations: []
```
`values-local.yaml:10-22` and `values-laptop.yaml:15-26` (NOT `:14`, which is `tracingEnabled: false`): replace the comment + list with
```yaml
  # One entry, served by the tei subchart. A profile list override REPLACES rather than merges, and
  # activeEmbeddingModel (inherited from values.yaml) must name an entry here.
  embeddingModels:
    - name: BAAI/bge-base-en-v1.5
      slug: bge-base
```
and add a `tei:` block after `ollama:` — local: `replicas: 1`, `storageSize: 2Gi`, `storageClassName: "standard"`, `resources: {requests: {cpu: "250m", memory: "1Gi"}, limits: {cpu: "1", memory: "2Gi"}}`; laptop the same (P19: 250m keeps the profile inside its 4-CPU budget; update the capacity comment at `:9-11` to `250+250+250+250+250+100+100+50+200 = 1.7 CPU of requests; system + operators measured ~2.0; metrics-server ~0.1. Total ~3.8 of 4.0 (~95%). Margin is thin — roughly 200m.`). `values-{aws,azure,gcp}.yaml`: after the `ollama:` block add
```yaml
tei:
  storageClassName: "iverson-tei"
  nodeSelector:
    iverson.io/node-pool: tei
  tolerations:
    - key: "iverson.io/node-pool"
      operator: "Equal"
      value: "tei"
      effect: "NoSchedule"
```

- [ ] **Step 3: Helpers and deployments.** Append to `_helpers.tpl`'s `iverson.embeddingEnv` body, before its final `{{- end -}}`:
```yaml
{{- range $i, $m := .Values.global.embeddingModels }}
- name: Embeddings__Models__{{ $i }}__Name
  value: {{ $m.name | quote }}
- name: Embeddings__Models__{{ $i }}__BaseUrl
  value: "http://{{ $.Release.Name }}-tei-{{ $m.slug }}:8080"
{{- end }}
```
and add a helper:
```yaml
{{/*
The global embedding fallback: the first tei entry's headless Service, on the container port.
*/}}
{{- define "iverson.embeddingBaseUrl" -}}
{{- $first := index .Values.global.embeddingModels 0 -}}
http://{{ .Release.Name }}-tei-{{ $first.slug }}:8080
{{- end -}}
```
In both deployments replace `value: "http://{{ .Release.Name }}-ollama:11434"` under `Embeddings__BaseUrl` with `value: {{ include "iverson.embeddingBaseUrl" . | quote }}` (the `Enrichment__BaseUrl` line two entries below stays on ollama in Phase A).

- [ ] **Step 4: Policies and the ollama pull loop.** In `networkpolicies.yaml` replace the api-egress and worker-egress ollama targets (`:54-55`, `:85-86`) with
```yaml
    - to: [{ podSelector: { matchLabels: { iverson.io/component: tei } } }]
      ports: [{ protocol: TCP, port: 8080 }]
    - to: [{ podSelector: { matchLabels: { app: {{ .Release.Name }}-ollama } } }]
      ports: [{ protocol: TCP, port: 11434 }]
```
(the ollama line stays for enrichment until Phase C) and add before the `ollama-ingress` policy:
```yaml
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: {{ .Release.Name }}-tei-ingress
spec:
  podSelector:
    matchLabels: { iverson.io/component: tei }
  policyTypes: ["Ingress"]
  ingress:
    - from:
        - podSelector: { matchLabels: { app: {{ .Release.Name }}-api } }
        - podSelector: { matchLabels: { app: {{ .Release.Name }}-worker } }
      ports: [{ protocol: TCP, port: 8080 }]
---
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: {{ .Release.Name }}-tei-egress
spec:
  podSelector:
    matchLabels: { iverson.io/component: tei }
  policyTypes: ["Egress"]
  egress:
    - to: []   # DNS
      ports: [{ protocol: UDP, port: 53 }, { protocol: TCP, port: 53 }]
    - to: []   # huggingface.co: the first start downloads the model weights into the PVC. No
               # portable pod/namespaceSelector target exists for an external host.
      ports: [{ protocol: TCP, port: 443 }]
---
```
In `charts/ollama/templates/statefulset.yaml` delete ONLY the three-line embedding pull loop at `:59-61` (`{{- range .Values.global.embeddingModels }}` / `ollama pull {{ .name }}` / `{{- end }}`), leaving `ollama serve &`, `sleep 5` and `ollama pull {{ .Values.global.generativeModel }}` intact — `ollama pull` is a client command that needs the background server, and an `ollama pull` of a Hub id would fail the init container; embeddings are no longer Ollama's job; the `ollama-egress` policy's registry comment says `the generative model` where it says `nomic-embed-text`.

- [ ] **Step 5: Rebuild the dependencies and run the three chart checks on all five profiles.**
```bash
cd /home/ben/repositories/Iverson/Iverson.Server/deploy/helm/iverson
helm dependency update .                                    # NOT build: build refuses once Chart.yaml's list changed (P11). Regenerates Chart.lock and charts/*.tgz (adds tei-0.1.0.tgz)
for v in values-local values-laptop values-aws values-azure values-gcp; do
  helm lint . -f $v.yaml | tail -1
  helm template iverson . -f $v.yaml | kubeconform -kubernetes-version 1.30.0 -summary -ignore-missing-schemas | tail -1     # Invalid: 0, Errors: 0
  helm template iverson . -f $v.yaml | kube-score score --ignore-test pod-networkpolicy --ignore-test container-image-pull-policy --ignore-test container-security-context-user-group-id - 2>/dev/null | grep -E 'tei-' | grep -c '💥'   # 0 (P11: the baseline is red elsewhere)
done
helm template iverson . -f values-local.yaml > /tmp/claude-1000/-home-ben-repositories-Iverson/690ec42d-1faa-4cc8-9c9a-e8965b19eebe/scratchpad/render-local.yaml
grep -c '^  name: iverson-tei-bge-base$' /tmp/claude-1000/-home-ben-repositories-Iverson/690ec42d-1faa-4cc8-9c9a-e8965b19eebe/scratchpad/render-local.yaml    # 3 metadata names (ServiceAccount, StatefulSet, Service; no PDB at 1 replica) — anchored so serviceAccountName: does not count
grep -A1 'Embeddings__Models__0__BaseUrl\|Embeddings__BaseUrl' /tmp/claude-1000/-home-ben-repositories-Iverson/690ec42d-1faa-4cc8-9c9a-e8965b19eebe/scratchpad/render-local.yaml | grep -c 'http://iverson-tei-bge-base:8080'   # 4 (api + worker, both keys)
grep -c 'ollama pull' /tmp/claude-1000/-home-ben-repositories-Iverson/690ec42d-1faa-4cc8-9c9a-e8965b19eebe/scratchpad/render-local.yaml    # 1 (the generative model only)
grep -c 'ollama serve' /tmp/claude-1000/-home-ben-repositories-Iverson/690ec42d-1faa-4cc8-9c9a-e8965b19eebe/scratchpad/render-local.yaml   # 1 — the server the pull needs survived the edit
helm template iverson . -f values-laptop.yaml | grep -c 'iverson-jaeger'    # 0 — tracingEnabled: false at values-laptop.yaml:14 survived the list replacement
grep -c 'clusterIP: None' /tmp/claude-1000/-home-ben-repositories-Iverson/690ec42d-1faa-4cc8-9c9a-e8965b19eebe/scratchpad/render-local.yaml
```

- [ ] **Step 6: Commit** (the tarballs and the regenerated `Chart.lock` are tracked; CI runs `helm dependency build`, which only stays in sync because the lock is committed).
```bash
cd /home/ben/repositories/Iverson
git add Iverson.Server/deploy/helm/iverson
git commit -m "helm: add the tei subchart, serve every embeddingModels entry through it, and point api/worker embeddings at TEI"
```

### Task 3: Terraform — the `tei` node pool and storage class

**Files:**
- Modify: `Iverson.Server/deploy/terraform/modules/cluster-aws/{main.tf:511,variables.tf:79-87}`, `modules/cluster-gcp/{main.tf:239,variables.tf:73-81}`, `modules/cluster-azure/{main.tf:208,variables.tf:84-92}`, `modules/operators/{main.tf:176-183,outputs.tf:7}`

- [ ] **Step 1: Pools.** Add after each `ollama` map entry: aws `tei = { instance_type = var.tei_instance_type, count = var.tei_node_count }`; gcp `tei = { machine_type = var.tei_machine_type, count = var.tei_node_count }`; azure `tei = { vm_size = var.tei_vm_size, count = var.tei_node_count, label = "tei" }` (Azure's label comes from this attribute, not the key). Variables, after the ollama ones: `tei_instance_type` default `"c7i.xlarge"`, `tei_machine_type` default `"c2-standard-4"`, `tei_vm_size` default `"Standard_F4s_v2"`, and `tei_node_count` default `2` in each module.

- [ ] **Step 2: Storage class.** `operators/main.tf`, after `kubernetes_storage_class.ollama`:
```hcl
resource "kubernetes_storage_class" "tei" {
  metadata {
    name = "iverson-tei"
  }
  storage_provisioner = var.storage_class_config.provisioner
  parameters          = var.storage_class_config.parameters
  volume_binding_mode = "WaitForFirstConsumer"
}
```
`outputs.tf`: add `tei = kubernetes_storage_class.tei.metadata[0].name` after the ollama line.

- [ ] **Step 3: Validate** (the workflow's matrix, on a scratch copy so `init` writes nothing into the repo):
```bash
SC=/tmp/claude-1000/-home-ben-repositories-Iverson/690ec42d-1faa-4cc8-9c9a-e8965b19eebe/scratchpad/plan-tf; rm -rf $SC; mkdir -p $SC; cp -r /home/ben/repositories/Iverson/Iverson.Server/deploy/terraform $SC/
for c in aws azure gcp; do terraform -chdir=$SC/terraform/$c fmt -check && terraform -chdir=$SC/terraform/$c init -backend=false -input=false >/dev/null && terraform -chdir=$SC/terraform/$c validate | tail -1; done
terraform -chdir=/home/ben/repositories/Iverson/Iverson.Server/deploy/terraform/modules/operators fmt -check
```

- [ ] **Step 4: Commit.**
```bash
git add Iverson.Server/deploy/terraform/modules
git commit -m "terraform: add the tei node pool and iverson-tei storage class in all three clouds"
```

### Task 4: Compose Phase A and the box

**Files:**
- Modify: `Iverson.Server/docker-compose.yml` (`ollama-init` `:138-149`; `tei-embed` `:171-179`; api env `:401-419`; api `depends_on` `:473`; worker env `:501-509`; worker `depends_on` `:545`)

**Interfaces**
- Produces: the compose stack Tasks 5, 6 and 8 run against.

- [ ] **Step 1: Compose edits.**
  - `ollama-init` `command` keeps only the `qwen2.5:3b` pull (drop the `nomic-embed-text` line and its `&&`).
  - `tei-embed`: delete `profiles: ["tei"]`; add
```yaml
    healthcheck:
      test: ["CMD", "curl", "-sf", "http://localhost:80/health"]
      interval: 15s
      timeout: 5s
      retries: 20
      start_period: 5m
```
  - api env: `- Embeddings__BaseUrl=http://tei-embed:80`; delete the `Embeddings__Models__0__Name`/`__BaseUrl` pair and their three comment lines; replace the comment block above `Embeddings__ModelId` with `# Must agree with tei-embed's served model (its --model-id, EMBED_MODEL_ID) and with the five conformance fixtures' declared model: this is the deployment default types register under. A mismatch fails loudly at first initialisation — the API's /info identity guard.`; `- Embeddings__ModelId=${BENCH_EMBED_MODEL:-BAAI/bge-base-en-v1.5}`. Same on the worker (its comment block has no eight-line version; just the env lines).
  - api `depends_on`: replace `ollama-init: condition: service_completed_successfully` with `tei-embed: condition: service_healthy`; worker: the same replacement (its `ollama-init` entry at `:545`).
```bash
cd /home/ben/repositories/Iverson/Iverson.Server
docker compose config --services | grep -c '^tei-embed$'                     # 1 — no profile
docker compose config | grep -cE 'Embeddings__BaseUrl: http://tei-embed:80'  # 2
docker compose config | grep -cE 'Embeddings__ModelId: BAAI/bge-base-en-v1.5'  # 2
docker compose config | grep -c 'Embeddings__Models__0'                      # 0
docker compose config | grep -c 'nomic-embed-text'                           # 0
```

- [ ] **Step 2: Clear the nomic-pinned state and restore the bge-base index.** (Read-only checks first; the row count must be 36 and the four collections present — P14.)
```bash
K=dev-only-not-for-production-qdrant-key-0123456789
docker exec iverson-postgres psql -U iverson -d iverson -Atc "select count(*) from public._iverson_schema"     # 36
docker exec iverson-postgres psql -U iverson -d iverson -c "DELETE FROM public._iverson_schema;"            # DELETE 36
for c in benchmark_documents_tenant_bypass benchmark_documents_chunks_tenant_bypass vector_docs_tenant_bypass vector_docs_chunks_tenant_bypass; do curl -s -o /dev/null -w "$c %{http_code}\n" -X DELETE -H "api-key: $K" 127.0.0.1:6333/collections/$c; done
cd /home/ben/repositories/iverson-benchmark-corpora/scifact-bge-base-qdrant-snapshots
for f in *.snapshot; do c="${f%%-6802952876034638*}"; curl -s -o /dev/null -w "$c %{http_code}\n" -X POST -H "api-key: $K" "http://localhost:6333/collections/$c/snapshots/upload?priority=snapshot" -F "snapshot=@$f"; done
curl -s -H "api-key: $K" 127.0.0.1:6333/collections/benchmark_documents_chunks_tenant_bypass | grep -o '"points_count":[0-9]*\|"size":[0-9]*' | head -2   # 19967, 768
curl -s -H "api-key: $K" 127.0.0.1:6333/collections/benchmark_documents_tenant_bypass | grep -o '"points_count":[0-9]*'   # 5183
```

- [ ] **Step 3: Rebuild the image and bring the whole stack up from this checkout.** This recreates `iverson-postgres` and `iverson-authentik-server` (their config hashes still point at the deleted `reranker-phase1` worktree); their data is in named volumes and authentik's bind mounts resolve to the same files, so nothing is lost. `authentik-migrate` reruns as a no-op.
```bash
cd /home/ben/repositories/Iverson/Iverson.Server
docker compose build iverson-api 2>&1 | tail -2
docker compose up -d 2>&1 | tail -5
sleep 60; docker ps --format '{{.Names}} {{.Status}}' | sort
until curl -sf 127.0.0.1:8081/build >/dev/null; do sleep 5; done
docker logs iverson-api 2>&1 | grep "EmbeddingService initialized\|not initialized\|serves model"   # exactly one line: model=BAAI/bge-base-en-v1.5 dimension=768
docker logs iverson-worker 2>&1 | grep "EmbeddingService initialized"                            # same model
docker exec iverson-postgres psql -U iverson -d iverson -Atc "select count(*) from public._iverson_schema"   # 0 — the harness and benchmark-query re-register their own types
```
If any container besides `iverson-tei-embed`, `iverson-api`, `iverson-worker`, `iverson-postgres`, `iverson-authentik-server`, `iverson-authentik-migrate` was recreated, note it in the report; a recreate is not a stop condition.

- [ ] **Step 4: Commit.**
```bash
cd /home/ben/repositories/Iverson
git add Iverson.Server/docker-compose.yml
git commit -m "compose: make tei-embed a default service the api and worker depend on, default the embedding model to bge-base, and pull only the generative model into ollama"
```

### Task 5: Conformance fixtures declare bge-base; run the matrix

**Files:**
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Models/S11ModelDotnet.cs:15,24`, `Models/S12ModelDotnet.cs:7,11,18`, `Program.cs:54,62,902`; `Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance/models/S11ModelJava.java:21,30`, `models/S12DeclaredJava.java:8,11`, `models/S12InheritedJava.java:13`, `Driver.java:89,96,394`; `Iverson.Clients/Python/conformance/models.py:202,214,231,234,246`, `driver.py:57,63,915`; `Iverson.Clients/TypeScript/conformance/models.ts:295,303,322,327,333`, `driver.ts:49,55,520`; `Iverson.Clients/Go/conformance/models.go:192,208,218`, `main.go:65,72`; `Iverson.Server/Iverson.ClientConformance/Scenarios/InheritedModelScenario.cs:63`

**Interfaces**
- Consumes: Task 4's stack (0 schema rows, TEI serving bge-base).

- [ ] **Step 1: Replace every `nomic-embed-text` literal at the sites above with `BAAI/bge-base-en-v1.5`** — the declarations (`[IversonEmbeddingModel("…")]`, `@IversonEmbeddingModel("…")`, `embedding_model="…"`, `@IversonEmbeddingModel('…')`, `return "…"`) and the doc/driver comments that quote them — and `InheritedModelScenario.ExpectedModelId = "BAAI/bge-base-en-v1.5"`. The client SDK unit tests (`SchemaRegistrarTests.cs`, `SchemaRegistrarTest.java`, `test_schema_registrar.py`, `core.test.ts`, `registrar_test.go`) keep their literals: they never contact a backend.
```bash
cd /home/ben/repositories/Iverson
grep -rn 'nomic-embed-text' Iverson.Clients/*/conformance Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver Iverson.Server/Iverson.ClientConformance/Scenarios | wc -l    # 0
```

- [ ] **Step 2: Unit suites, then the full matrix** (a full run is the coverage claim; the four exports are the runbook's compose values):
```bash
dotnet test Iverson.Server/Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj 2>&1 | grep -E 'Passed!|Failed!'
export IVERSON_CLIENT_ID=dev-iverson-loadtest-client-id IVERSON_CLIENT_SECRET=dev-only-not-for-production-loadtest-secret-0123456789 IVERSON_TOKEN_ENDPOINT=http://localhost:9000/application/o/token/ IVERSON_CLIENT_SCOPE="schema_admin tenant_id_loadtest"
cd Iverson.Server/Iverson.ClientConformance && dotnet run 2>&1 | tee /tmp/claude-1000/-home-ben-repositories-Iverson/690ec42d-1faa-4cc8-9c9a-e8965b19eebe/scratchpad/conformance-phaseA.log | tail -25; echo "exit=${PIPESTATUS[0]}"
docker exec iverson-postgres psql -U iverson -d iverson -Atc "select type_name, schema_json::text like '%BAAI/bge-base-en-v1.5%' from public._iverson_schema where type_name like 'S11Model%'"   # five rows, all t
```
The matrix must exit 0 with every cell `ok`. A cell failing on `model-rejected` or `model-inherited` is this task's defect; anything else is reported, not worked around.

- [ ] **Step 3: Commit.**
```bash
cd /home/ben/repositories/Iverson
git add Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance Iverson.Clients/Python/conformance Iverson.Clients/TypeScript/conformance Iverson.Clients/Go/conformance Iverson.Server/Iverson.ClientConformance/Scenarios/InheritedModelScenario.cs
git commit -m "conformance fixtures declare the deployment default explicitly as bge-base"
```

### Task 6: Scripts and the Launcher's TEI half

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/stack.py:10-13,27,43,83-84,91-97,169-172`; `Iverson.Server/Iverson.LoadTest/scripts/ingest.py:26,64,121-124,150,153,723-737`; `Iverson.Server/Iverson.Launcher/Program.cs:20,28,72-103`; `Iverson.Server/deploy/kind/setup.sh:98`, `setup.ps1:95`

**Interfaces**
- Consumes: Task 4's stack and restored index.

- [ ] **Step 1: `stack.py`.**
```python
TIERS = {
    "ingest": ["qdrant", "tei-embed"],
    "query": ["qdrant", "tei-embed", "postgres", "redis", "authentik-server", "iverson-api"],
}
```
`CONTAINER` gains `"tei-embed": "iverson-tei-embed"` and loses `"ollama"`; `READY_CHECKS` gains `"tei-embed": lambda timeout: wait_http_200("http://127.0.0.1:8091/health", timeout)` and loses `"ollama"`; the docstring's tier lists (`:10-13`), the `--no-deps` note (`:26-30`: its quoted `depends_on` list swaps `ollama-init (service_completed_successfully)` for `tei-embed (service_healthy)`, which the tiers DO include, so the sentence becomes: the other three are what --no-deps skips) the readiness sentence (`:43`, "Ollama's `GET /api/tags`" → "TEI's `GET /health`"), and the `CONTAINER` comment at `:89` ("… ollama and iverson-api." → "… for every entry but qdrant and iverson-api.") follow.

- [ ] **Step 2: `ingest.py`.** `OLLAMA_URL = "http://localhost:11434"` → `DEFAULT_EMBED_URL = "http://localhost:8091"` (both uses); `--model` default `"BAAI/bge-base-en-v1.5"` and its help's first sentence → `"embedding model id, e.g. 'BAAI/bge-base-en-v1.5' (the deployment default). Against Ollama it must already be pulled -- the dimension probe runs before --drop acts."` (keep the rest); `--embed-url` help → `f"embedding backend base URL, POSTed at /v1/embeddings (default {DEFAULT_EMBED_URL}, the compose tei-embed service; Ollama is http://localhost:11434)"`; the docstring lines `:26,64,121-124` and the comment at `:153` say "the embedding backend" where they say Ollama, keeping the TEI-ignores-`--model` warning.
```bash
cd /home/ben/repositories/Iverson/Iverson.Server/Iverson.LoadTest/scripts
python3 -m py_compile ingest.py stack.py && echo compiled
grep -li ollama stack.py    # prints nothing (P32); ingest.py keeps its two deliberate mentions
SCR=$(mktemp -d); python3 ingest.py --corpus /home/ben/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26/beir/corpus.jsonl --key-map-path $SCR/keymap.json --drop --limit 5 --object-collection scratch_objects --chunks-collection scratch_chunks 2>&1 | grep 'probed embedding dimension'   # 768 for model 'BAAI/bge-base-en-v1.5' at http://localhost:8091
grep -o '"model": "[^"]*"\|"embed_url": "[^"]*"' $SCR/keymap.json.stats.json
K=dev-only-not-for-production-qdrant-key-0123456789; for c in scratch_objects scratch_chunks; do curl -s -o /dev/null -w "$c del=%{http_code}\n" -X DELETE -H "api-key: $K" 127.0.0.1:6333/collections/$c; done; rm -rf $SCR
```

- [ ] **Step 3: Launcher.** `:20` → `"compose up -d postgres starrocks qdrant kafka zookeeper jaeger ollama ollama-init tei-embed"`; `:28` → `await WaitForHttp200Async("http://localhost:8091/health", "TEI", TimeSpan.FromMinutes(10), cts.Token);` and keep an Ollama wait for the generative model: `await WaitForOllamaAsync("http://localhost:11434/api/tags", "qwen2.5:3b", cts.Token);`. Add beside `WaitForOllamaAsync`:
```csharp
static async Task WaitForHttp200Async(string url, string serviceName, TimeSpan ceiling, CancellationToken ct)
{
    Console.Write($"[Launcher] Waiting for {serviceName} at {url}");
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    var deadline = DateTime.UtcNow + ceiling;
    while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
    {
        try
        {
            var response = await client.GetAsync(url, ct);
            if (response.IsSuccessStatusCode) { Console.WriteLine(" ready."); return; }
        }
        catch (OperationCanceledException) { return; }
        catch { }
        Console.Write(".");
        try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { return; }
    }
    throw new TimeoutException($"{serviceName} at {url} did not answer 200 within {ceiling}.");
}
```
`dotnet build Iverson.Server/Iverson.Launcher/Iverson.Launcher.csproj`.

- [ ] **Step 4: kind notes.** `setup.sh:98` / `setup.ps1:95`: `ollama.storageSize` → `tei.storageSize (or ollama.storageSize while ollama is deployed)`; `iverson-ollama` → `iverson-tei-bge-base (or iverson-ollama)`.

- [ ] **Step 5: The Phase 1 harness still runs** (spec §8's last bullet), against Task 4's restored bge-base index:
```bash
cd /home/ben/repositories/Iverson/Iverson.Server
python3 Iverson.LoadTest/scripts/stack.py query --timeout 300 | tail -3      # the tier is already up from Task 4; this stops the out-of-tier containers (worker, kafka, starrocks, …) — the benchmark discipline — and exercises the new tei-embed readiness check
export SCI=/home/ben/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26
source /home/ben/iverson-benchmark-data/bench-env.sh
cd Iverson.LoadTest
dotnet run -c Release -- benchmark-query --corpus-path $SCI --key-map-path $SCI/keymap.json --output-dir $SCI/runs --config-label phase2-a-$(date +%F) 2>&1 | tail -3
wc -l $SCI/runs/phase2-a-$(date +%F).chunks.trec     # 15000 (300 queries × 50)
diff <(cut -d' ' -f1-5 $SCI/runs/phase2-a-$(date +%F).chunks.trec) <(cut -d' ' -f1-5 /home/ben/repositories/iverson-benchmark-corpora/scifact-bge-base-2026-09-04/runs/bge-base.chunks.trec) && echo IDENTICAL-TO-M1
docker exec iverson-postgres psql -U iverson -d iverson -Atc "select schema_json::text like '%BAAI/bge-base-en-v1.5%' from public._iverson_schema where type_name='BenchmarkDocument'"   # t
```
`IDENTICAL-TO-M1` is expected (same index, same model, same build of the ranking code); a difference is reported, not a stop. Afterwards `docker compose up -d` from `Iverson.Server` restarts the out-of-tier containers `stack.py` stopped (the worker is needed by later tasks).

- [ ] **Step 6: Commit.**
```bash
cd /home/ben/repositories/Iverson
git add Iverson.Server/Iverson.LoadTest/scripts/stack.py Iverson.Server/Iverson.LoadTest/scripts/ingest.py Iverson.Server/Iverson.Launcher/Program.cs Iverson.Server/deploy/kind/setup.sh Iverson.Server/deploy/kind/setup.ps1
git commit -m "scripts and Launcher: tei-embed joins the tiers and the wait list, ingest.py defaults to bge-base on TEI"
```

### Task 7: `EnrichmentService` chat-completions port and the source-text cap (Phase B)

**Files:**
- Modify: `Iverson.Server/Iverson.Embeddings/EnrichmentService.cs` (whole class body); `Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs:43,129`
- Test: `Iverson.Server/Iverson.Embeddings.Tests/EnrichmentServiceTests.cs` (rewritten); `Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs` (+1)

**Interfaces**
- Produces: the backend-neutral client Tasks 8 and 10 rely on; `EnrichmentConsumer.MaxSourceChars` (internal, visible to Api.Tests). `EnrichmentService`'s constants and `ExtractJson` stay internal and are exercised only through the public methods: Iverson.Embeddings grants InternalsVisibleTo to nothing (P24).

- [ ] **Step 1: Rewrite `EnrichmentServiceTests.cs`** (ten facts; tests first; the suite is red until step 3):
```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Iverson.Embeddings.Tests;

public sealed class EnrichmentServiceTests
{
    private sealed class FakeHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest     { get; private set; }
        public string?             LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            LastRequest     = request;
            LastRequestBody = request.Content is not null
                ? await request.Content.ReadAsStringAsync(ct)
                : null;
            return response;
        }
    }

    // The HttpClient's BaseAddress is deliberately NOT the options' BaseUrl: the service must build
    // an absolute URI from its own BaseUrl, never a relative path on the client.
    private static EnrichmentService CreateService(FakeHttpMessageHandler handler, EnrichmentServiceOptions? options = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory
            .CreateClient(Arg.Any<string>())
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") });
        return new EnrichmentService(
            factory,
            Options.Create(options ?? new EnrichmentServiceOptions { ModelId = "Qwen/Qwen2.5-1.5B-Instruct", BaseUrl = "http://tgi:8092" }),
            NullLogger<EnrichmentService>.Instance);
    }

    // The OpenAI-compatible shape TGI and Ollama both serve at /v1/chat/completions.
    private static HttpResponseMessage ChatResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { choices = new[] { new { index = 0, message = new { role = "assistant", content } } } }),
                Encoding.UTF8,
                "application/json")
        };

    [Fact]
    public async Task GenerateAsync_PostsToV1ChatCompletions_OnTheConfiguredBaseUrl_NotTheClientBaseAddress()
    {
        var handler = new FakeHttpMessageHandler(ChatResponse("ok"));
        var svc = CreateService(handler);

        await svc.GenerateAsync("some prompt");

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri.Should().Be(new Uri("http://tgi:8092/v1/chat/completions"));
    }

    [Fact]
    public async Task GenerateAsync_SendsModel_OneUserMessage_MaxTokens_TemperatureZero_StreamFalse()
    {
        var handler = new FakeHttpMessageHandler(ChatResponse("ok"));
        var svc = CreateService(handler);

        await svc.GenerateAsync("summarize this");

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var root = doc.RootElement;
        root.GetProperty("model").GetString().Should().Be("Qwen/Qwen2.5-1.5B-Instruct");
        var messages = root.GetProperty("messages");
        messages.GetArrayLength().Should().Be(1);
        messages[0].GetProperty("role").GetString().Should().Be("user");
        messages[0].GetProperty("content").GetString().Should().Be("summarize this");
        root.GetProperty("max_tokens").GetInt32().Should().Be(256);   // Iverson.Embeddings grants InternalsVisibleTo to nothing (P24)
        root.GetProperty("temperature").GetDouble().Should().Be(0);
        root.GetProperty("stream").GetBoolean().Should().BeFalse();
        root.TryGetProperty("format", out _).Should().BeFalse();
        root.TryGetProperty("prompt", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GenerateAsync_ReturnsTheFirstChoicesMessageContent()
    {
        var svc = CreateService(new FakeHttpMessageHandler(ChatResponse("hello world")));

        var result = await svc.GenerateAsync("summarize this");

        result.Should().Be("hello world");
    }

    [Fact]
    public async Task GenerateJsonAsync_SendsNoResponseFormat()
    {
        // TGI's grammars need a fixed property set the extraction hint cannot supply (spec §3.5);
        // a response_format here would 422 on TGI.
        var handler = new FakeHttpMessageHandler(ChatResponse("""{"key":"value"}"""));
        var svc = CreateService(handler);

        await svc.GenerateJsonAsync("extract this");

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.TryGetProperty("response_format", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("format", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GenerateJsonAsync_ExtractsTheFencedBlock_FromAProseWrappedReply()
    {
        // Ollama's shape without a format directive (spec §9 row 17): prose, a fenced object, a note.
        var reply = "Here is the extracted information formatted as JSON:\n\n```json\n{\n  \"revenue\": {\"figure\": 4.2},\n  \"year\": 2024\n}\n```\n\nNote: the percentage is not included.";
        var svc = CreateService(new FakeHttpMessageHandler(ChatResponse(reply)));

        var result = await svc.GenerateJsonAsync("extract this");

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("year").GetInt32().Should().Be(2024);
        result.Should().NotContain("```").And.NotContain("Note:");
    }

    [Fact]
    public async Task GenerateJsonAsync_ExtractsTheFirstBalancedObject_WhenThereIsNoFence()
    {
        // A closing brace inside a string must not end the span; trailing prose must be dropped.
        var reply = "Sure: {\"a\": \"x } y\", \"b\": {\"c\": 1}} and that is all }";
        var svc = CreateService(new FakeHttpMessageHandler(ChatResponse(reply)));

        var result = await svc.GenerateJsonAsync("extract this");

        result.Should().Be("{\"a\": \"x } y\", \"b\": {\"c\": 1}}");
    }

    [Fact]
    public async Task GenerateJsonAsync_Throws_WhenTheReplyHoldsNoJsonObject()
    {
        var svc = CreateService(new FakeHttpMessageHandler(ChatResponse("I could not find any structured information.")));

        await svc.Invoking(s => s.GenerateJsonAsync("extract this"))
                 .Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*I could not find any structured information.*");
    }

    [Fact]
    public async Task GenerateJsonAsync_Throws_WhenTheOnlyObjectDoesNotParse()
    {
        // The balanced span is found but JsonDocument.Parse rejects it (trailing comma); without the
        // parse guard this malformed text would be stored verbatim in the target column (spec §4).
        var svc = CreateService(new FakeHttpMessageHandler(ChatResponse("Result: {\"a\": 1,} — that's all")));

        await svc.Invoking(s => s.GenerateJsonAsync("extract this"))
                 .Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*Result: {*");
    }

    [Fact]
    public async Task GenerateAsync_ThrowsHttpRequestException_OnNonSuccessStatusCode()
    {
        var svc = CreateService(new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await svc.Invoking(s => s.GenerateAsync("hello"))
                 .Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GenerateAsync_Throws_OnMalformedResponseJson()
    {
        var malformed = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"unexpected":"format"}""", Encoding.UTF8, "application/json")
        };
        var svc = CreateService(new FakeHttpMessageHandler(malformed));

        await svc.Invoking(s => s.GenerateAsync("hello"))
                 .Should().ThrowAsync<Exception>();
    }
}
```
Named mutations: an `/api/generate` regression (test 1 and 2); a `response_format` sent (test 4); reading `response` instead of `choices[0].message.content` (test 3); a leading/trailing strip instead of extraction (tests 5, 6); no parse check (test 8, the malformed-object reply — test 7 has no `{` and throws on the null-candidate path with or without the guard, P34).

- [ ] **Step 2: The consumer cap test.** In `EnrichmentConsumerTests.cs`, after `HandleUpdated_PublishesPostCommitRefetch_NotThePreGenerationSnapshot`:
```csharp
    // The cap sits on the SOURCE TEXT, not the assembled prompt: three prompts lead with their
    // instruction and the extraction prompt trails with its hint, so a cut on the prompt would drop
    // one of them (spec §3.5). A cap on the assembled prompt fails the EndWith assertion.
    [Fact]
    public async Task HandleUpdated_CutsTheSourceTextTo8000Chars_KeepingTheInstructionAndTheHint()
    {
        await _registry.RegisterAsync(EnrichedArticle());
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Key).Returns(RowJson(new string('x', 20_000)));
        string? extractionPrompt = null;
        _enrichment.GenerateJsonAsync(Arg.Do<string>(p => extractionPrompt = p), Arg.Any<CancellationToken>())
                   .Returns("""{"a":1}""");

        await BuildSut().HandleAsync(Key, Event(EntityEventType.Updated), CancellationToken.None);

        extractionPrompt.Should().NotBeNull();
        extractionPrompt.Should().StartWith("Extract structured information");
        extractionPrompt.Should().EndWith("Extract specifically: the author's stated conclusion");
        extractionPrompt.Should().Contain(new string('x', EnrichmentConsumer.MaxSourceChars));
        extractionPrompt.Should().NotContain(new string('x', EnrichmentConsumer.MaxSourceChars + 1));
    }
```

- [ ] **Step 3: `EnrichmentService.cs`** — replace the class body:
```csharp
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iverson.Embeddings;

public sealed class EnrichmentService(
    IHttpClientFactory httpClientFactory,
    IOptions<EnrichmentServiceOptions> options,
    ILogger<EnrichmentService> logger) : IEnrichmentService
{
    // The chat API needs an explicit completion budget. The enrichment prompts ask for 2-3
    // sentences, 5-10 keywords, a 1-2 sentence description, or one JSON object.
    internal const int MaxGeneratedTokens = 256;

    private static readonly JsonSerializerOptions _jsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly Regex FencedBlock =
        new("```(?:json)?\\s*\\n(.*?)\\n\\s*```", RegexOptions.Singleline | RegexOptions.Compiled);

    // Absolute, from this service's own BaseUrl: the named HttpClient's BaseAddress is not the
    // request base (the same rule EmbeddingService follows).
    private readonly string _baseUrl = options.Value.BaseUrl;

    public string ModelId => options.Value.ModelId;

    public Task<string> GenerateAsync(string prompt, CancellationToken ct = default) =>
        GenerateInternalAsync(prompt, jsonFormat: false, ct);

    public async Task<string> GenerateJsonAsync(string prompt, CancellationToken ct = default) =>
        ExtractJson(await GenerateInternalAsync(prompt, jsonFormat: true, ct));

    private async Task<string> GenerateInternalAsync(string prompt, bool jsonFormat, CancellationToken ct)
    {
        using var activity = Telemetry.Source.StartActivity("enrichment.generate", ActivityKind.Client);
        activity?.SetTag("enrichment.model", ModelId);
        activity?.SetTag("enrichment.input_chars", prompt.Length);
        activity?.SetTag("enrichment.json_format", jsonFormat);

        try
        {
            using var client = httpClientFactory.CreateClient(Telemetry.EnrichmentHttpClientName);

            // The OpenAI-compatible chat route, served by TGI and by Ollama alike. No grammar for the
            // JSON case: TGI's grammars need a fixed property set and the extraction hint is free
            // text (spec §3.5); ExtractJson isolates the object from the reply instead.
            var body = JsonSerializer.Serialize(new
            {
                model       = ModelId,
                messages    = new[] { new { role = "user", content = prompt } },
                max_tokens  = MaxGeneratedTokens,
                temperature = 0,
                stream      = false,
            }, _jsonOpts);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_baseUrl), "/v1/chat/completions"))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
            using var doc                  = await JsonDocument.ParseAsync(responseStream, default, ct);

            // { "choices": [ { "message": { "role": "assistant", "content": "..." } } ], ... }
            var text = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            activity?.SetTag("enrichment.output_chars", text.Length);
            activity?.SetStatus(ActivityStatusCode.Ok);

            return text;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogError(ex, "GenerateAsync failed for model {Model}", ModelId);
            throw;
        }
    }

    // The first fenced code block if the reply carries one, else the first balanced { ... } span
    // (string-aware), parsed to prove it is JSON. Without a format directive Ollama wraps the object
    // in prose on both sides and TGI fences it (spec §9 rows 12, 17); a leading/trailing fence strip
    // isolates neither.
    internal static string ExtractJson(string text)
    {
        var fence     = FencedBlock.Match(text);
        var candidate = fence.Success ? fence.Groups[1].Value.Trim() : BalancedObject(text);
        if (candidate is not null)
        {
            try
            {
                using var _ = JsonDocument.Parse(candidate);
                return candidate;
            }
            catch (JsonException) { }
        }

        var head = text.Length <= 200 ? text : text[..200];
        throw new InvalidOperationException(
            $"Enrichment backend returned no parseable JSON object for a JSON extraction: '{head}'");
    }

    private static string? BalancedObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return text.Substring(start, i - start + 1);
        }
        return null;
    }
}
```

- [ ] **Step 4: The consumer cap.** `EnrichmentConsumer.cs`: after the `GroupId` constant (`:43`):
```csharp
    // Cut the source text, not the assembled prompt: three prompts lead with their instruction and
    // the extraction prompt trails with its hint, so a cut on the prompt would drop one of them.
    // 8,000 characters is ~2,000 tokens, under TGI's --max-input-tokens 3072 with room for the
    // instruction and the chat template (spec §3.5). Ollama silently truncated from the head at
    // 4,096 tokens; this is a deliberate reduction, not parity. Applied before ComputeHash so the
    // loop-prevention hash covers what was actually sent.
    internal const int MaxSourceChars = 8_000;
```
and at `:129`:
```csharp
        var sourceText = BuildSourceText(schema, row);
        if (sourceText.Length > MaxSourceChars) sourceText = sourceText[..MaxSourceChars];
```
(`IntelligenceStoreConsumer`'s chunk-context prompt is already bounded: `ParentTextContextChars = 2000` plus one chunk.)

- [ ] **Step 5: Run.**
```bash
cd /home/ben/repositories/Iverson
dotnet test Iverson.Server/Iverson.Embeddings.Tests/Iverson.Embeddings.Tests.csproj 2>&1 | grep -E 'Passed!|Failed!'     # 52/52 (48 + 10 − 6)
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~Iverson.Api.Tests.Consumers.EnrichmentConsumerTests" 2>&1 | grep -E 'Passed!|Failed!'
dotnet build Iverson.slnx 2>&1 | grep -E 'error|Build succeeded'
```
Then prove two mutations by hand and revert: move the cap onto the assembled prompt in `GenerateAsync` (the consumer test's `EndWith` fails); replace `ExtractJson` with a fence strip (tests 5 and 6 fail).

- [ ] **Step 6: Commit.**
```bash
git add Iverson.Server/Iverson.Embeddings/EnrichmentService.cs Iverson.Server/Iverson.Embeddings.Tests/EnrichmentServiceTests.cs Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs
git commit -m "enrichment: talk OpenAI-compatible chat completions, extract the JSON object from the reply, and cap the source text before the prompt is built"
```

### Task 8: Compose `tgi`, `enrich_bench.py`, the measurement, the verdict

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/enrich_bench.py`; `docs/plans/2026-09-GATE-enrichment-backend.md`; (outside the repo) `~/repositories/iverson-benchmark-corpora/enrichment-bench-<date>/{results.json,side-by-side.md}`
- Modify: `Iverson.Server/docker-compose.yml` (a `tgi` service after `tei-embed`; `tgi_models` under `volumes:`)

**Interfaces**
- Consumes: Task 4's stack (Ollama still serving `qwen2.5:3b`). The port from Task 7 is not on the measurement path (the script talks HTTP directly) but the verdict decides Phase C, which depends on it.
- Produces: the verdict that selects Task 9/10 (pass) or Task 9′ (fail).

This task runs ~2.5 h: TGI's first start ~25 min, then 75 prompts on Ollama (~20 min) and on TGI (~75 min at ~1.6 tok/s). Nothing else on the box; the worker is stopped for the measurement and restarted after.

- [ ] **Step 1: The `tgi` service**, after `tei-embed`:
```yaml
  # Generative backend under evaluation (spec docs/specs/2026-09-05-embedding-migration-phase2-design.md
  # §3.7). Intel CPU image; bf16 weights; the token limits keep warm-up to ~10 minutes (the image
  # defaults pre-allocate a 32k context and take twenty). HF_HUB_DISABLE_XET: the Hub's Xet download
  # path fails from this box, the classic path works. First start: ~11 min download + ~10 min warm-up.
  tgi:
    image: ghcr.io/huggingface/text-generation-inference:3.3.4-intel-cpu
    container_name: iverson-tgi
    command: ["--model-id", "${GEN_MODEL_ID:-Qwen/Qwen2.5-1.5B-Instruct}", "--dtype", "bfloat16", "--max-input-tokens", "3072", "--max-total-tokens", "3584", "--max-batch-prefill-tokens", "3072"]
    environment:
      - HF_HUB_DISABLE_XET=1
    shm_size: 1g
    ports:
      - "8092:80"
    volumes:
      - tgi_models:/data
    healthcheck:
      test: ["CMD", "curl", "-sf", "http://localhost:80/health"]
      interval: 30s
      timeout: 10s
      retries: 10
      start_period: 25m
```
and `tgi_models:` under `volumes:`. `docker compose config --services | grep -c '^tgi$'` → 1.

- [ ] **Step 2: `enrich_bench.py`** (stdlib only; the prompt strings are `EnrichmentPrompts.cs`'s, copied verbatim):
```python
#!/usr/bin/env python3
"""Measure enrichment backends on a fixed prompt set (spec 2026-09-05-embedding-migration-phase2 §6.2).

Builds 75 prompts from the first SciFact abstracts (read-only): 50 ChunkContext, 10 Summary,
10 Keywords, 5 Extraction. Runs them sequentially, one backend at a time, through the
OpenAI-compatible /v1/chat/completions route the ported EnrichmentService uses (temperature 0,
max_tokens 256), recording per prompt: wall seconds, completion tokens, tokens/s, empty output,
and for Extraction whether the reply parses after the same extraction rule as the service; per
backend: peak container RSS (docker stats, sampled every 5 s) and the box's minimum MemAvailable.

    python3 enrich_bench.py --corpus .../scifact-run-2026-08-26/beir/corpus.jsonl \\
        --out .../enrichment-bench-2026-09-05 \\
        --backend ollama=http://localhost:11434=qwen2.5:3b=iverson-ollama \\
        --backend tgi=http://localhost:8092=Qwen/Qwen2.5-1.5B-Instruct=iverson-tgi

Each --backend is name=base_url=model=container. Outputs results.json and side-by-side.md.
"""
import argparse, json, os, re, statistics, subprocess, threading, time, urllib.request

# EnrichmentPrompts.cs, verbatim.
SUMMARY = "Summarize the following text in 2-3 concise sentences:\n\n{0}"
KEYWORDS = ("Extract the 5-10 most important keywords or key phrases from the following text. "
            "Return them as a comma-separated list:\n\n{0}")
EXTRACTION = "Extract structured information from the following text and return it as JSON:\n\n{0}"
CHUNK_CONTEXT = ("Here is the context of a document:\n\n{0}\n\n"
                 "Here is an excerpt from that same document:\n\n{1}\n\n"
                 "Write a brief 1-2 sentence description of what this excerpt is about and how it "
                 "relates to the surrounding document. Respond with the description only.")
EXTRACT_HINT = "the main finding and the population studied"
MAX_TOKENS = 256
FENCE = re.compile(r"```(?:json)?\s*\n(.*?)\n\s*```", re.S)


def build_prompts(corpus_path):
    docs = []
    with open(corpus_path, encoding="utf-8") as f:
        for line in f:
            d = json.loads(line)
            if d.get("text", "").strip():
                docs.append(d["text"])
            if len(docs) >= 75:
                break
    prompts = []
    for i, text in enumerate(docs[:50]):
        prompts.append(("ChunkContext", i, CHUNK_CONTEXT.format(text[:1500], text[:512])))
    for i, text in enumerate(docs[50:60]):
        prompts.append(("Summary", i, SUMMARY.format(text[:8000])))
    for i, text in enumerate(docs[60:70]):
        prompts.append(("Keywords", i, KEYWORDS.format(text[:8000])))
    for i, text in enumerate(docs[70:75]):
        prompts.append(("Extraction", i, EXTRACTION.format(text[:8000]) + f"\n\nExtract specifically: {EXTRACT_HINT}"))
    assert len(prompts) == 75, len(prompts)
    return prompts


def chat(base_url, model, prompt):
    body = json.dumps({"model": model, "messages": [{"role": "user", "content": prompt}],
                       "max_tokens": MAX_TOKENS, "temperature": 0, "stream": False}).encode()
    req = urllib.request.Request(f"{base_url}/v1/chat/completions", data=body, method="POST")
    req.add_header("Content-Type", "application/json")
    t0 = time.monotonic()
    with urllib.request.urlopen(req, timeout=600) as resp:
        parsed = json.loads(resp.read())
    wall = time.monotonic() - t0
    content = parsed["choices"][0]["message"]["content"]
    tokens = parsed.get("usage", {}).get("completion_tokens")
    return content, wall, tokens


def extract_json(text):
    """The service's rule: first fenced block, else first balanced {...} span (string-aware)."""
    m = FENCE.search(text)
    cand = m.group(1).strip() if m else None
    if cand is None:
        start = text.find("{")
        if start >= 0:
            depth, in_str, esc = 0, False, False
            for i in range(start, len(text)):
                c = text[i]
                if in_str:
                    if esc: esc = False
                    elif c == "\\": esc = True
                    elif c == '"': in_str = False
                    continue
                if c == '"': in_str = True
                elif c == "{": depth += 1
                elif c == "}":
                    depth -= 1
                    if depth == 0:
                        cand = text[start:i + 1]
                        break
    if cand is None:
        return False
    try:
        json.loads(cand)
        return True
    except json.JSONDecodeError:
        return False


class MemorySampler(threading.Thread):
    def __init__(self, container):
        super().__init__(daemon=True)
        self.container, self.peak_rss, self.min_avail, self.stop = container, 0, None, threading.Event()

    def run(self):
        while not self.stop.is_set():
            try:
                out = subprocess.run(["docker", "stats", "--no-stream", "--format", "{{.MemUsage}}", self.container],
                                     capture_output=True, text=True, timeout=20).stdout
                m = re.search(r"([0-9.]+)([KMG]i?B)", out)
                if m:
                    unit = {"KB": 1e3, "MB": 1e6, "GB": 1e9, "KiB": 2**10, "MiB": 2**20, "GiB": 2**30}[m.group(2)]
                    self.peak_rss = max(self.peak_rss, float(m.group(1)) * unit)
                with open("/proc/meminfo") as f:
                    avail = int([l for l in f if l.startswith("MemAvailable")][0].split()[1]) * 1024
                self.min_avail = avail if self.min_avail is None else min(self.min_avail, avail)
            except Exception:
                pass
            self.stop.wait(5)


def run_backend(name, base_url, model, container, prompts):
    sampler = MemorySampler(container)
    sampler.start()
    rows = []
    for kind, idx, prompt in prompts:
        try:
            content, wall, tokens = chat(base_url, model, prompt)
            rows.append({"kind": kind, "index": idx, "wall_s": round(wall, 3), "completion_tokens": tokens,
                         "tokens_per_s": round(tokens / wall, 3) if tokens and wall else None,
                         "empty": not content.strip(),
                         "json_ok": extract_json(content) if kind == "Extraction" else None,
                         "failed": False, "output": content})
        except Exception as e:
            rows.append({"kind": kind, "index": idx, "wall_s": None, "completion_tokens": None, "tokens_per_s": None,
                         "empty": True, "json_ok": False if kind == "Extraction" else None, "failed": True, "output": f"ERROR: {e}"})
        print(f"[{name}] {kind} {idx}: {rows[-1]['wall_s']}s {rows[-1]['completion_tokens']} tok", flush=True)
    sampler.stop.set()
    sampler.join()
    walls = sorted(r["wall_s"] for r in rows if r["wall_s"] is not None)
    by_kind = {}
    for k in ("ChunkContext", "Summary", "Keywords", "Extraction"):
        ws = [r["wall_s"] for r in rows if r["kind"] == k and r["wall_s"] is not None]
        by_kind[k] = {"mean_wall_s": round(statistics.mean(ws), 3) if ws else None, "n": len(ws)}
    return {"name": name, "base_url": base_url, "model": model, "container": container, "rows": rows,
            "summary": {"by_kind": by_kind,
                        "p95_wall_s": walls[max(0, int(round(0.95 * len(walls))) - 1)] if walls else None,
                        "failed": sum(r["failed"] for r in rows), "empty": sum(r["empty"] for r in rows),
                        "extraction_parse_ok": sum(1 for r in rows if r["kind"] == "Extraction" and r["json_ok"]),
                        "peak_rss_bytes": sampler.peak_rss, "min_mem_available_bytes": sampler.min_avail}}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--corpus", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--backend", action="append", required=True, metavar="NAME=BASE_URL=MODEL=CONTAINER")
    args = ap.parse_args()
    prompts = build_prompts(args.corpus)
    os.makedirs(args.out, exist_ok=True)
    results = []
    for spec in args.backend:
        name, base_url, model, container = spec.split("=", 3)
        results.append(run_backend(name, base_url, model, container, prompts))
        with open(os.path.join(args.out, "results.json"), "w", encoding="utf-8") as f:
            json.dump({"prompts": len(prompts), "max_tokens": MAX_TOKENS, "backends": results}, f, indent=2)
    with open(os.path.join(args.out, "side-by-side.md"), "w", encoding="utf-8") as f:
        f.write("# Enrichment side-by-side\n\n")
        for kind, idx, prompt in prompts:
            f.write(f"## {kind} {idx}\n\n<details><summary>prompt</summary>\n\n```\n{prompt}\n```\n\n</details>\n\n")
            for r in results:
                row = next(x for x in r["rows"] if x["kind"] == kind and x["index"] == idx)
                f.write(f"**{r['name']}** ({row['wall_s']} s, {row['completion_tokens']} tokens):\n\n```\n{row['output']}\n```\n\n")
    for r in results:
        print(json.dumps({r["name"]: r["summary"]}, indent=2))


if __name__ == "__main__":
    main()
```
`python3 -m py_compile enrich_bench.py`; `python3 -c "import enrich_bench as b; print(len(b.build_prompts('/home/ben/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26/beir/corpus.jsonl')))"` → 75; `python3 -c 'import enrich_bench as b; print(b.extract_json("x ```json\n{\"a\":1}\n``` y"), b.extract_json("no"))'` → `True False` (single quotes around the Python: inside double quotes bash would command-substitute the backticks, P33).

- [ ] **Step 3: Measure.**
```bash
cd /home/ben/repositories/Iverson/Iverson.Server
docker stop iverson-worker iverson-starrocks iverson-kafka iverson-zookeeper iverson-jaeger 2>/dev/null
docker compose up -d --no-deps tgi                                    # first start: ~25 min
until curl -sf http://127.0.0.1:8092/health >/dev/null; do sleep 30; done
curl -s http://127.0.0.1:8092/info | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['model_id'], d['max_input_tokens'], d['max_total_tokens'])"   # Qwen/Qwen2.5-1.5B-Instruct 3072 3584
curl -s http://127.0.0.1:11434/api/tags | grep -o '"qwen2.5:3b"'
D=$(date +%F); OUT=/home/ben/repositories/iverson-benchmark-corpora/enrichment-bench-$D; mkdir -p "$OUT"    # tee opens $OUT/run.log before the script's makedirs runs
cd Iverson.LoadTest/scripts
python3 enrich_bench.py --corpus /home/ben/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26/beir/corpus.jsonl --out $OUT \
  --backend tgi=http://localhost:8092=Qwen/Qwen2.5-1.5B-Instruct=iverson-tgi \
  --backend ollama=http://localhost:11434=qwen2.5:3b=iverson-ollama 2>&1 | tee $OUT/run.log | tail -40   # TGI FIRST: Ollama keeps its model resident 5 min after a request (P27), which would sit inside TGI's MemAvailable window
docker inspect -f '{{.State.OOMKilled}}' iverson-tgi iverson-ollama iverson-api iverson-qdrant    # all false
```
(Run the script in the background and poll `$OUT/run.log`; Ollama loads its model on the first prompt, so its first `ChunkContext` row carries ~10 s of load time — report it as measured.)

- [ ] **Step 4: Apply the gate** from `results.json`, mechanically: (1) `tgi.by_kind.ChunkContext.mean_wall_s ≤ ollama.by_kind.ChunkContext.mean_wall_s`; (2) `tgi.p95_wall_s < 120`; (3) `tgi.failed == 0 and tgi.empty == 0`; (4) `tgi.extraction_parse_ok == 5`; (5) `tgi.min_mem_available_bytes > 500e6` and no OOM kill. All five → **PASS**; any miss → **FAIL**. Then `docker compose up -d` (restarts the worker and the stopped services; `tgi` stays up).

- [ ] **Step 5: Write `docs/plans/2026-09-GATE-enrichment-backend.md`** in the shape of `docs/plans/2026-09-GATE-embedding-migration.md`: header (date, branch HEAD, this plan/task, where `run.log`, `results.json`, `side-by-side.md` live); Method (spec §6, the 1.5B-only ruling, the box, TGI's flags, Ollama's `/v1/chat/completions` route, worker stopped); a per-backend table (mean wall per kind, p95, tokens/s, failed, empty, extraction parses, peak RSS, min MemAvailable); the five criteria each with its measured value and PASS/FAIL; the overall verdict line **GATE PASSED — Phase C** or **GATE FAILED — Phase C′**; a note that the side-by-side table is Ben's quality read and can veto a numeric pass (record his reading if given); the spec §7 consequence for the branch taken; the backend order (TGI measured first, so criterion 5's `MemAvailable` window is free of Ollama's model — P27); and, on a pass, the note that spec §8's Phase C kind assertion and spec §11's `Errno 30` check are not performed on this box (Task 11 names what performs them). Do not choose the 3B candidate or a cloud run; note it as Ben's option.

- [ ] **Step 6: Commit.**
```bash
cd /home/ben/repositories/Iverson
git add Iverson.Server/docker-compose.yml Iverson.Server/Iverson.LoadTest/scripts/enrich_bench.py
git add -f docs/plans/2026-09-GATE-enrichment-backend.md
git commit -m "add the compose tgi service and enrich_bench.py, and record the enrichment backend gate verdict"
```

**The verdict routes the rest:** PASS → Tasks 9, 10, 11. FAIL → Tasks 9′, 11. Never both branches.

### Task 9 (Phase C, on a pass): Helm `tgi` subchart, Ollama deleted, Terraform rename

**Files:**
- Create: `Iverson.Server/deploy/helm/iverson/charts/tgi/{Chart.yaml,values.yaml,templates/statefulset.yaml,templates/service.yaml,templates/pdb.yaml}`
- Modify: `Chart.yaml` (drop `ollama`, add `tgi`), `Chart.lock`, `charts/*.tgz`, `values.yaml` (`global.enrichmentEnabled`, `tgi:` block, `ollama:` block removed, the `generativeModel` comment), the five profiles (`tgi:` blocks, `ollama:` blocks removed; laptop `global.enrichmentEnabled: false`), `charts/api/templates/deployment.yaml:127-130`, `charts/worker/templates/deployment.yaml:122-125`, `templates/networkpolicies.yaml` (ollama targets and policies out; tgi in); `deploy/terraform/modules/cluster-{aws,gcp,azure}/{main.tf,variables.tf}`, `modules/operators/{main.tf,outputs.tf}`, `values-{aws,azure,gcp}.yaml` (`tgi:` with `iverson-tgi` / pool `tgi`)
- Delete: `charts/ollama/**` (`helm dependency update` prunes `charts/ollama-0.1.0.tgz`; `Chart.lock` is regenerated and committed)

- [ ] **Step 1: The subchart.** Copy `charts/tei/` to `charts/tgi/` and change: `Chart.yaml` name/description; `values.yaml` = `replicas: 2`, `storageSize: 8Gi`, `storageClassName: ""`, `imageTag: "3.3.4-intel-cpu"`, `resources: {requests: {cpu: "8", memory: "16Gi", ephemeral-storage: "2Gi"}, limits: {cpu: "8", memory: "16Gi", ephemeral-storage: "4Gi"}}`, `nodeSelector: {}`, `tolerations: []`, plus `enabled: true`; the templates render one object set (no `range`) guarded by `{{- if and .Values.global.enrichmentEnabled .Values.enabled }}`, named `{{ .Release.Name }}-tgi`, labels `app: {{ .Release.Name }}-tgi` and `iverson.io/component: tgi`; the container:
```yaml
        - name: tgi
          image: "ghcr.io/huggingface/text-generation-inference:{{ .Values.imageTag }}"
          imagePullPolicy: IfNotPresent
          # bf16 weights and a 3.5k context keep warm-up to minutes (the image defaults pre-allocate a
          # 32k context). HF_HUB_DISABLE_XET: the Hub's Xet path fails from some networks; the classic
          # download works. workingDir /tmp: under a read-only root the router writes a file named
          # `out` in its cwd and otherwise falls back to legacy tokenization. --port 8080: uid 1000
          # cannot bind 80.
          args: ["--model-id", {{ .Values.global.generativeModel | quote }}, "--port", "8080", "--dtype", "bfloat16", "--max-input-tokens", "3072", "--max-total-tokens", "3584", "--max-batch-prefill-tokens", "3072"]
          env:
            - { name: HF_HUB_DISABLE_XET, value: "1" }
          workingDir: /tmp
          securityContext:
            allowPrivilegeEscalation: false
            readOnlyRootFilesystem: true
            capabilities: { drop: ["ALL"] }
          ports:
            - containerPort: 8080
          resources: (as tei's block, from .Values.resources)
          startupProbe:
            httpGet: { path: /health, port: 8080 }
            periodSeconds: 15
            failureThreshold: 120
          readinessProbe:
            httpGet: { path: /health, port: 8080 }
            periodSeconds: 15
          volumeMounts:
            - name: tgi-data
              mountPath: /data
            - name: tmp
              mountPath: /tmp
            - name: shm
              mountPath: /dev/shm
      volumes:
        - name: tmp
          emptyDir: {}
        - name: shm
          emptyDir: { medium: Memory, sizeLimit: 1Gi }
```
with `volumeClaimTemplates` for `tgi-data`; Service headless on 8080; PDB as tei's. Parent `Chart.yaml`: remove the `ollama` dependency, add `tgi` with `condition: global.enrichmentEnabled`.

- [ ] **Step 2: Values.** `values.yaml`: add under `global:` after `generativeModel` (now `"Qwen/Qwen2.5-1.5B-Instruct"`, comment "the Hub id the tgi subchart serves and api/worker request through Enrichment__ModelId") `enrichmentEnabled: true` with the spec's comment; delete the `ollama:` block; add the `tgi:` block (values above). Wording the sweep in Task 10 would otherwise catch: `Chart.yaml:3`'s description `Ollama` → `TGI`; `values-laptop.yaml:6` `ollama 8` → `tgi 8`; the trailing comment in `values-aws.yaml:88`, `values-azure.yaml:81`, `values-gcp.yaml:82` `qdrant/ollama do` → `qdrant/tgi do`. Profiles: delete each `ollama:` block; local: `tgi: {replicas: 1, storageSize: 8Gi, storageClassName: "standard", resources: {requests: {cpu: "2", memory: "6Gi"}, limits: {cpu: "4", memory: "8Gi"}}}`; laptop: `global.enrichmentEnabled: false` (comment: TGI needs 4–5 GB and 8 threads the laptop budget does not have) and no `tgi:` block; cloud files: `tgi: {storageClassName: "iverson-tgi", nodeSelector: {iverson.io/node-pool: tgi}, tolerations: [{key: "iverson.io/node-pool", operator: "Equal", value: "tgi", effect: "NoSchedule"}]}`.

- [ ] **Step 3: Deployments and policies.** api/worker: `Enrichment__BaseUrl` value → `"http://{{ .Release.Name }}-tgi:8080"`; add `- name: Enrichment__Enabled` / `value: {{ .Values.global.enrichmentEnabled | quote }}` after `Enrichment__ModelId`. `networkpolicies.yaml`: delete the two ollama targets in api/worker egress and the `ollama-ingress`/`ollama-egress` policies; add `tgi-ingress`/`tgi-egress` (tei's two policies with `tgi` and the Hub comment), both wrapped in `{{- if .Values.global.enrichmentEnabled }} … {{- end }}` — the parent chart's templates render unconditionally, the laptop profile renders no tgi pod, and kube-score marks a pod-less NetworkPolicy CRITICAL (P31) — and `- to: [{ podSelector: { matchLabels: { iverson.io/component: tgi } } }]` / `ports: [{ protocol: TCP, port: 8080 }]` in api-egress and worker-egress. Delete `charts/ollama/` (Step 5's `helm dependency update` removes the tarball).

- [ ] **Step 4: Terraform rename.** In each cluster module rename the `ollama` map entry and variables to `tgi` (`tgi_instance_type` `"c7i.2xlarge"`, `tgi_machine_type` `"c2-standard-8"`, `tgi_vm_size` `"Standard_F8s_v2"`, `tgi_node_count` 2; Azure `label = "tgi"`); `operators/main.tf`: `kubernetes_storage_class.ollama` → `tgi`, name `iverson-tgi`; `outputs.tf`: `tgi = kubernetes_storage_class.tgi.metadata[0].name`; the comment at `cluster-aws/main.tf:488` `StarRocks/Qdrant/Kafka/Ollama StorageClasses` → `…/TGI …`. `grep -rni ollama Iverson.Server/deploy/terraform` → 0 (case-insensitive, or the comment survives).

- [ ] **Step 5: Checks.**
```bash
cd /home/ben/repositories/Iverson/Iverson.Server/deploy/helm/iverson
helm dependency update .           # regenerates Chart.lock, packages tgi, prunes charts/ollama-0.1.0.tgz itself
ls charts/*.tgz | grep -c ollama    # 0
for v in values-local values-laptop values-aws values-azure values-gcp; do
  helm lint . -f $v.yaml | tail -1
  helm template iverson . -f $v.yaml | kubeconform -kubernetes-version 1.30.0 -summary -ignore-missing-schemas | tail -1
  helm template iverson . -f $v.yaml | kube-score score --ignore-test pod-networkpolicy --ignore-test container-image-pull-policy --ignore-test container-security-context-user-group-id - 2>/dev/null | grep -E 'tei-|tgi' | grep -c '💥'   # 0
  helm template iverson . -f $v.yaml | grep -c 'ollama'    # 0
done
helm template iverson . -f values-local.yaml | grep -c '^  name: iverson-tgi$'     # 3
helm template iverson . -f values-laptop.yaml | grep -c '^  name: iverson-tgi$'    # 0 — enrichmentEnabled false on the laptop
helm template iverson . -f values-local.yaml | grep -A1 'Enrichment__BaseUrl\|Enrichment__Enabled' | grep -c 'iverson-tgi:8080\|"true"'   # 4
SC=/tmp/claude-1000/-home-ben-repositories-Iverson/690ec42d-1faa-4cc8-9c9a-e8965b19eebe/scratchpad/plan-tf; rm -rf $SC; mkdir -p $SC; cp -r /home/ben/repositories/Iverson/Iverson.Server/deploy/terraform $SC/; for c in aws azure gcp; do terraform -chdir=$SC/terraform/$c fmt -check && terraform -chdir=$SC/terraform/$c init -backend=false -input=false >/dev/null && terraform -chdir=$SC/terraform/$c validate | tail -1; done
```

- [ ] **Step 6: Commit.**
```bash
cd /home/ben/repositories/Iverson
git add -A Iverson.Server/deploy/helm/iverson Iverson.Server/deploy/terraform/modules
git commit -m "helm and terraform: serve enrichment from a tgi subchart, delete the ollama subchart, rename the ollama pool and storage class to tgi"
```

### Task 10 (Phase C, on a pass): Ollama removed from compose, code, Launcher, scripts

**Files:**
- Modify: `Iverson.Server/docker-compose.yml` (`ollama`, `ollama-init`, `ollama_data` deleted; worker `depends_on tgi`; api/worker `Enrichment__*`; the `starrocks-init` comment at `:67` "Follows the `ollama-init` idiom already in this file" → "Follows the init-container idiom this file uses"); `Iverson.Server/Iverson.Embeddings/EnrichmentServiceOptions.cs:6-7,14`; `Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs:16`; `Iverson.Server/Iverson.Launcher/Program.cs:20,28-29,72-103`; `Iverson.Server/Iverson.LoadTest/scripts/stack.py` (docstring); `Iverson.Server/deploy/kind/setup.sh:98`, `setup.ps1:95`

- [ ] **Step 1: Compose.** Delete the `ollama` and `ollama-init` services and the `ollama_data` volume; api/worker env `- Enrichment__BaseUrl=http://tgi:80` and `- Enrichment__ModelId=${GEN_MODEL_ID:-Qwen/Qwen2.5-1.5B-Instruct}`; worker `depends_on` gains `tgi: condition: service_healthy`. `docker compose config | grep -ci ollama` → 0.

- [ ] **Step 2: Code.** `EnrichmentServiceOptions`: `BaseUrl = "http://localhost:8092"`, `ModelId = "Qwen/Qwen2.5-1.5B-Instruct"`, the `:14` comment "hit Ollama simultaneously" → "hit the backend simultaneously"; `EnrichmentConsumer.cs:16` "from an Ollama generative model" → "from the generative backend". Launcher: `up` list without `ollama ollama-init`, `WaitForOllamaAsync` deleted, the TEI wait plus `await WaitForHttp200Async("http://localhost:8092/health", "TGI", TimeSpan.FromMinutes(30), cts.Token);`. `stack.py` docstring: the remaining `ollama-init` mention in the `--no-deps` note. kind notes: `tei.storageSize`/`tgi.storageSize`, `iverson-tei-bge-base`/`iverson-tgi`.
```bash
cd /home/ben/repositories/Iverson
grep -rli ollama --include=*.cs --include=*.yml --include=*.yaml --include=*.tf --include=*.sh --include=*.ps1 --include=*.py --include=*.tpl --include=*.ts --include=*.java --include=*.go . | grep -v '/bin/\|/obj/\|/.worktrees/\|/.claude/\|^./docs/\|node_modules\|README.md\|Iverson.Server/docs/security/tma.md\|Tests/\|_test.go\|tests/\|EmbeddingPrefixes.cs\|Iverson.Api.Tests\|scripts/ingest.py\|scripts/enrich_bench.py'
```
must print nothing (the exceptions are the spec's: docs, the two acknowledged documentation files, test comments, the prefix-table comment about id tags — plus `ingest.py`, whose `--model`/`--embed-url` help deliberately names Ollama as the alternative backend (Task 6), and `enrich_bench.py`, whose docstring names Ollama as the measurement baseline (Task 8); both are legitimate mentions, exactly as spec §2 leaves the `EmbeddingPrefixes` nomic rows in place).

- [ ] **Step 3: Run.** `dotnet build Iverson.slnx`; Embeddings, Api and ClientConformance.Tests suites; then on the stack:
```bash
cd /home/ben/repositories/Iverson/Iverson.Server
docker compose build iverson-api 2>&1 | tail -1
docker compose up -d --remove-orphans 2>&1 | tail -3        # removes the now-undefined ollama containers
docker ps -a --format '{{.Names}}' | grep -c ollama          # 0
docker logs iverson-worker 2>&1 | grep 'EmbeddingService initialized'
```
Then one real enrichment through TGI. No registerable type in the repo declares an enrichment target (P26: `VectorDoc` is deliberately annotation-free), so the trigger is a throwaway type in a scratch console: create `/tmp/claude-1000/-home-ben-repositories-Iverson/690ec42d-1faa-4cc8-9c9a-e8965b19eebe/scratchpad/enrich-smoke/` with a net10.0 console project referencing `Iverson.Clients/DotNet/Iverson.Client.Core`, `Iverson.Client.Contracts` and `Iverson.Client.Attributes` (the three `ProjectReference`s of `Iverson.Client.Conformance.Driver.csproj:12-14`) and a copy of the driver's `Auth.cs`; declare
```csharp
[IversonEntity]
public sealed class EnrichSmoke
{
    [IversonKey] public Guid Id { get; set; }
    [IversonEmbedding] public string Body { get; set; } = "";
    [IversonSummary] public string? Summary { get; set; }
    [IversonExtracted("the main finding")] public string? Finding { get; set; }
}
```
register it with `new SchemaRegistrar(registry, mapping, NullLogger<SchemaRegistrar>.Instance).RegisterAllAsync()` and write one row (a ~500-character `Body`) with `Coordinator<EnrichSmoke>().PostMappedAsync(...)`, modelled on the driver's registration and `write` phase (`Program.cs:109-127, 470-480`) and authenticated the way the driver is (its invoker from `Auth.cs`, the client credentials from Task 5's four `IVERSON_*` exports, an acting-user token for tenant `loadtest`). Then:
```bash
until docker logs iverson-worker 2>&1 | grep -q '\[Enrichment\] Enriched'; do sleep 10; done   # ~1–2 min per generation on this box; two generations
docker logs iverson-worker 2>&1 | grep -c '\[Enrichment\] Enriched'    # ≥ 1
docker logs iverson-tgi 2>&1 | grep -c 'chat_completions'               # ≥ 1
docker exec iverson-postgres psql -U iverson -d iverson -c "DELETE FROM public._iverson_schema WHERE type_name = 'EnrichSmoke';"
K=dev-only-not-for-production-qdrant-key-0123456789; for c in $(curl -s -H "api-key: $K" 127.0.0.1:6333/collections | grep -o '"name":"enrich_smoke[^"]*"' | cut -d'"' -f4); do curl -s -o /dev/null -w "$c %{http_code}\n" -X DELETE -H "api-key: $K" 127.0.0.1:6333/collections/$c; done
```
(clearing the type's schema row and collections afterwards, which Task 4 established is safe on this stack). The scratch project is never committed.

- [ ] **Step 4: Commit.**
```bash
cd /home/ben/repositories/Iverson
git add Iverson.Server/docker-compose.yml Iverson.Server/Iverson.Embeddings/EnrichmentServiceOptions.cs Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs Iverson.Server/Iverson.Launcher/Program.cs Iverson.Server/Iverson.LoadTest/scripts/stack.py Iverson.Server/deploy/kind/setup.sh Iverson.Server/deploy/kind/setup.ps1
git commit -m "remove ollama from compose, the Launcher and the enrichment defaults; enrichment is served by tgi"
```

### Task 9′ (Phase C′, on a fail): record the Ollama-for-enrichment end state

**Files:**
- Modify: `docs/plans/2026-09-GATE-enrichment-backend.md` (an "End state" section); `Iverson.Server/deploy/kind/setup.sh:98`, `setup.ps1:95`

No chart, compose, Terraform or code deletion. What Tasks 2–8 landed is already the spec's Phase C′ shape: the ollama subchart pulls only `global.generativeModel` (`qwen2.5:3b`), compose keeps `ollama` with an `ollama-init` that pulls only `qwen2.5:3b`, `Enrichment__BaseUrl` points at ollama, `EnrichmentServiceOptions` defaults stay Ollama's, the `tgi` compose service and the Terraform `tei` pool exist, and the ported `EnrichmentService` works against Ollama (Task 8's Ollama rows are its evidence).

- [ ] **Step 1: The ported client against Ollama, for real.** Nothing on this branch has rebuilt the image since Task 7, so the compose worker still runs the pre-port `/api/generate` build. From `Iverson.Server`: `docker compose build iverson-api 2>&1 | tail -1 && docker compose up -d iverson-worker iverson-api`, then run Task 10 Step 3's `EnrichSmoke` scratch-console smoke unchanged except for the backend log check, which becomes `docker logs iverson-ollama 2>&1 | grep -c 'POST     "/v1/chat/completions"'` ≥ 1 (Ollama's GIN access log). Clean up the type's schema row and collections as there.

- [ ] **Step 2:** Append to the gate document an "End state (Phase C′)" section listing exactly the above, plus what a future cloud measurement of the 3B candidate would need (the `tgi` compose service definition and `enrich_bench.py` are reusable as-is; `--backend tgi=…=Qwen/Qwen2.5-3B-Instruct=…` on an AMX node). Update the kind notes to name both `ollama.storageSize` and `tei.storageSize`.
- [ ] **Step 3: Commit** (`git add -f docs/plans/2026-09-GATE-enrichment-backend.md`, the two kind scripts; message "record the Phase C′ end state: ollama stays for enrichment only").

### Task 11: kind smoke on the laptop profile

**Files:** none in the repo (the smoke report goes in the task report; no commit unless a chart defect surfaces, which is then its own fix commit).

**Interfaces**
- Consumes: Task 9's chart (pass) or Task 2's chart (fail); the API image from the current branch.

The compose stack must be down first (the box has 4 CPUs / 9.9 GB and the laptop profile budgets ~3.8 of them). Reversible: `docker compose up -d` afterwards.

- [ ] **Step 1: Cluster.**
```bash
cd /home/ben/repositories/Iverson/Iverson.Server
docker compose stop
kind get clusters | grep -q '^iverson$' || kind create cluster --name iverson --config deploy/kind/kind-config.yaml
bash deploy/kind/setup.sh 2>&1 | tail -5                     # operators + metrics-server; podman pids_limit is already -1 (P21)
bash deploy/kind/build-and-load-image.sh 0.1.0 iverson 2>&1 | tail -2
cd deploy/helm/iverson && helm dependency update . && helm upgrade --install iverson . -f values-laptop.yaml -n iverson --create-namespace --wait --timeout 30m 2>&1 | tail -3
```

- [ ] **Step 2: Assert.**
```bash
kubectl -n iverson get sts,svc,pdb,networkpolicy | grep -E 'tei|tgi|ollama'      # iverson-tei-bge-base sts + headless svc + tei policies; no tgi (laptop: enrichmentEnabled false); no ollama on a pass
kubectl -n iverson rollout status sts/iverson-tei-bge-base --timeout=10m
kubectl -n iverson logs sts/iverson-tei-bge-base | grep -c 'Ready'               # ≥ 1
kubectl -n iverson logs deploy/iverson-api | grep 'EmbeddingService initialized'   # model=BAAI/bge-base-en-v1.5 dimension=768
kubectl -n iverson logs deploy/iverson-worker | grep 'EmbeddingService initialized'
kubectl -n iverson get pods                                                       # all Running/Completed; note any Pending with its reason
kubectl -n iverson exec deploy/iverson-api -- bash -c 'exec 3<>/dev/tcp/iverson-tei-bge-base/8080; printf "GET /info HTTP/1.1\r\nHost: tei\r\nConnection: close\r\n\r\n" >&3; cat <&3' | grep -o '"model_id":"[^"]*"'   # from inside the policy boundary. The aspnet image ships bash only — no wget, no curl (P25); this is the compose healthcheck's own idiom. A kubectl-run pod is no substitute: tei-ingress admits only app=iverson-api/worker pods, which is what this proves
```
On a pass branch also `helm template` of the laptop profile must contain no `ollama` object (already asserted in Task 9). **Not performed on this box, and the task report must say so:** spec §8's Phase C kind items "the tgi pod ready and one worker enrichment logged" and spec §11's `Errno 30` verification of `workingDir: /tmp`. The laptop profile runs with `global.enrichmentEnabled: false`, so the `tgi` subchart renders no pod, and `values-local.yaml`'s `tgi` sizing (2 CPU / 6 Gi requested, 4–5 GB resident) cannot run beside the rest of the stack on this 4-CPU / 10 GB host. They are performed by installing `values-local.yaml` on a ≥ 16 GB machine and asserting `kubectl -n iverson rollout status sts/iverson-tgi` and `kubectl -n iverson logs sts/iverson-tgi | grep -c 'Errno 30'` → 0 plus one `[Enrichment] Enriched` worker line. The `tgi` templates ship having passed `helm lint`, kubeconform and kube-score only.

- [ ] **Step 3: Restore the box.** `helm uninstall iverson -n iverson` is optional (leave the cluster for reuse); `kind delete cluster --name iverson` only if RAM is needed; then `cd /home/ben/repositories/Iverson/Iverson.Server && docker compose up -d` and confirm the six-plus-tgi containers are healthy again.

## Tasks NOT in this plan

Inherited from spec §2:

**Out:** `bge-small` (failed the Phase 1 gate); any migration tooling (no registered types exist); the
`docs/` history (untouched); Qwen2.5-3B on TGI (cannot be measured here — §6.1); the `EmbeddingPrefixes`
nomic/arctic rows (model ids, not a backend; the ingest contract pins them and nothing is served by them by
default).

## Known issues inherited from spec

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
