# Critical Implementation Review: 2026-09-05-embedding-migration-phase2-implementation-plan (Round 1)

**Plan:** /home/ben/repositories/Iverson/docs/plans/2026-09-05-embedding-migration-phase2-implementation-plan.md
**Source spec:** /home/ben/repositories/Iverson/docs/specs/2026-09-05-embedding-migration-phase2-design.md (commit SHA `107eba5`)
**Verified plan-level assumptions section:** present (24 rows, P1–P22, P24, P23)

⚠️ 1 commit since plan-write time (SHA `107eba5`): `4f92d2e add the embedding migration Phase 2 implementation plan` — the plan's own commit; no code, chart, compose, Terraform or script drift. Cited file:line references re-checked under §1 regardless.

---

## 0. Coverage enumeration

### Task 1 — embedding defaults, telemetry names, wording (Phase A)

| Surface | Disposition |
|---|---|
| Step 1 code block (`EmbeddingServiceOptions` 6-7, `Telemetry` 8-9) | ok — read `EmbeddingServiceOptions.cs:6-7` (`http://localhost:11434` / `nomic-embed-text`) and `Telemetry.cs:8-9` (`iverson.ollama` / `iverson.ollama.enrichment`); both replacements are exact-line substitutions with no other reader (`grep HttpClientName`, `grep 'iverson\.ollama'` → only these two files) |
| Step 2 prose (nine wording sites + the deliberate no-change at `EmbeddingPrefixes.cs:16`) | ok — every cited site read and confirmed present; `SchemaRegistrationOrchestrator.cs:127-129` is the only production copy of the "must remain pulled … Ollama" tail, and `ModelRejectedScenario.cs:255-300` asserts only the model names, the `DELETE FROM` clause and the two tenant-qualified collection names — none of the changed prose |
| Step 3 commands (`dotnet build`, two suites, the `iverson\.ollama` grep) | ok on the greps and the suites; the parenthetical claim "The ClientConformance.Tests suite pins the scenario messages; it is the guard for the wording edits" is **wrong but harmless** — `ModelRejectedScenarioTests.GuardMessage` (`:44-59`) builds both sides of the comparison itself, so it can neither fail nor pass on an orchestrator wording change. Dropped: the step's edits are safe regardless, so no outcome breaks |
| Step 3 expected count `48/48` | ok — `Iverson.Embeddings.Tests` carries 39 `[Fact]`/`[Theory]` attributes and 12 `[InlineData]` rows, consistent with 48 executed cases and with Step 5's `51 = 48 + 9 − 6` in Task 7 |
| Step 4 `git add` list | ok — covers every file Step 2 actually edits; correctly omits `EmbeddingPrefixes.cs` (no change) |

### Task 2 — `tei` subchart and Phase A chart shape

| Surface | Disposition |
|---|---|
| Step 1 `Chart.yaml` / `values.yaml` / statefulset / service / pdb code blocks | ok — I wrote the four templates verbatim into a scratch copy of the chart and rendered all five profiles: `helm lint` passes ×5, `kubeconform` 0 invalid ×5, kube-score `✅` on `v1/Service iverson-tei-bge-base`, `apps/v1/StatefulSet iverson-tei-bge-base` and (aws/azure/gcp) `policy/v1/PodDisruptionBudget iverson-tei-bge-base`. `$`-rooting inside `range` resolves correctly; `index $.Values.resources.requests "ephemeral-storage"` survives the profile map-merge |
| Step 1 parent `Chart.yaml` dependency entry | → §2.1 |
| Step 2 values edits (`values.yaml`, local, laptop, aws/azure/gcp) | ok on shape — a profile `embeddingModels` override replaces (verified by render: exactly one `iverson-tei-bge-base` object set), `activeEmbeddingModel` inherited, cloud `tei:` blocks map-merge over `values.yaml`'s so `ephemeral-storage` survives |
| Step 2 laptop capacity comment (`250+250+250+250+250+100+100+50+200 = 1.7`) | dropped — arithmetic is correct; the file's pre-existing inventory already omits `authentik-worker`'s 100m, but a comment cannot break the spec's outcome |
| Step 3 helper code blocks (`iverson.embeddingEnv` append, `iverson.embeddingBaseUrl`) | ok — applied verbatim in scratch; renders `Embeddings__BaseUrl` and `Embeddings__Models__0__BaseUrl` as `http://iverson-tei-bge-base:8080` on both api and worker |
| Step 3 prose "the `Enrichment__BaseUrl` line two entries below stays on ollama" | ok — `charts/api/templates/deployment.yaml:125` and `:128` carry the identical literal; the plan explicitly names the trap |
| Step 4 networkpolicy blocks + the ollama pull-loop edit | ok — `networkpolicies.yaml:54,85` (api/worker egress) and `:391-418` (the two ollama policies) are exactly where the plan says; `charts/ollama/templates/statefulset.yaml:59-62` ranges over `embeddingModels` then pulls `generativeModel`, so reducing it to the single `ollama pull {{ .Values.global.generativeModel }}` line leaves `qwen2.5:3b` (values.yaml:29) — a valid Ollama id, and strictly less to pull than today |
| Step 5 assertion values (3 / 4 / 1 / clusterIP count) | ok — measured on the scratch render: `grep -c '^  name: iverson-tei-bge-base$'` = 3, the `-A1` BaseUrl count = 4, `clusterIP: None` = 3 |
| Step 5 kube-score assertion `grep -E 'tei-' \| grep -c '💥'` | ok — kube-score 1.19.0 prints `💥` on the **object header line**, so the pipeline is falsifiable; confirmed against the baseline render (`apps/v1/Deployment iverson-admin-ui 💥`) |
| Step 5 `helm dependency build` | → §2.1 |

### Task 3 — Terraform `tei` pool and storage class

| Surface | Disposition |
|---|---|
| Step 1 pool/variable prose (aws/gcp/azure) | ok — `cluster-aws/main.tf:511`, `cluster-gcp/main.tf:239`, `cluster-azure/main.tf:208` read; Azure's node label/taint come from `each.value.label` (`:221-226`), AWS/GCP from `each.key` (`:572`, `:266`), exactly as the plan states; `ollama_*` variable names and defaults confirmed (`c7i.2xlarge`, `c2-standard-8`, `Standard_F8s_v2`, count 2) |
| Step 2 `kubernetes_storage_class.tei` HCL + `outputs.tf` | ok — a byte-for-byte clone of `operators/main.tf:175-183`; `outputs.tf:7` is the referenced line |
| Step 3 validate commands (scratch copy, `fmt -check`/`init -backend=false`/`validate`) | ok — mirrors `deploy-validate.yml:118-126` (which is also non-recursive `fmt -check` on the cloud root only); `terraform v1.9.8` present. `tfsec Iverson.Server/deploy/terraform/` (workflow line 105) is not run locally and tfsec is not installed — dropped: the new objects are clones of existing resource shapes, so no new rule class is reachable |
| Step 4 commit | ok |

### Task 4 — compose Phase A and the box

| Surface | Disposition |
|---|---|
| Step 1 compose edits (ollama-init command, tei-embed healthcheck, api/worker env, depends_on) | ok — `ollama-init` at `:138-149` is a `curlimages/curl` two-`POST /api/pull` command (not `ollama pull`), so "drop the `nomic-embed-text` line and its `&&`" is the right edit; `tei-embed :171-179` has `profiles: ["tei"]` and no healthcheck; the TEI image carries `curl`; api `depends_on … ollama-init` at `:473`, worker at `:545` |
| Step 1 `docker compose config` assertions | ok — `docker compose config` renders env as an unquoted mapping (`Embeddings__BaseUrl: http://ollama:11434`), so all five greps match the rendered form; `--services` currently omits profiled services, so `grep -c '^tei-embed$'` → 1 after the profile is dropped |
| Step 2 psql/Qdrant/snapshot commands | ok — live check: 36 `_iverson_schema` rows, collections `benchmark_documents{,_chunks}_tenant_bypass`, `vector_docs{,_chunks}_tenant_bypass` plus `iverson-probe`; `scifact-bge-base-qdrant-snapshots/` holds exactly the two `.snapshot` files whose names carry peer `6802952876034638`, so `RESTORE.md`'s `${f%%-6802952876034638*}` derivation yields the two collection names |
| Step 3 prose about recreated containers / named volumes | ok — every running container carries compose project `iversonserver` (even those whose `working_dir` label points at deleted worktrees), and the volumes are `iversonserver_*`, so `docker compose up -d` from this checkout reuses `postgres_data`/`qdrant_data` and the Step 2 mutations survive the recreate |
| Step 3 log assertions | ok — `EmbeddingService.cs:54-55` logs `EmbeddingService initialized: model={Model} dimension={Dimension} …`, matching the grep and the expected text |
| Step 4 commit | ok |

### Task 5 — conformance fixtures declare bge-base

| Surface | Disposition |
|---|---|
| Step 1 file/line list | ok — `grep -rn nomic-embed-text` over the five conformance trees + `Iverson.ClientConformance` returns exactly the 37 sites the plan's Files list enumerates, with no site left out |
| Step 1 prose "SDK unit tests keep their literals" | ok — `ModelRejectedScenarioTests.cs:23`, `ReregistrarTests.cs:22` are self-contained test constants; `InheritedModelScenarioTests.cs:19` reads `InheritedModelScenario.ExpectedModelId`, so it follows the edit |
| Step 1 grep (`Iverson.Clients/*/conformance …`) | ok — the glob matches Java/Python/TypeScript/Go conformance dirs and the DotNet driver is named explicitly |
| Step 2 matrix commands + the four `IVERSON_*` exports | ok — `docs/runbooks/client-conformance-matrix.md:14-46` carries the exact four exports; node 24 / go 1.22 / java 21 + mvn 3.9.9 / python 3.14 / dotnet all present |
| Step 2 psql assertion | ok — `_iverson_schema` has columns `type_name, schema_json, updated_at`, and the five `S11Model*` rows exist today |
| Step 3 commit | ok |

### Task 6 — scripts and the Launcher's TEI half

| Surface | Disposition |
|---|---|
| Step 1 `stack.py` code block + docstring prose | ok — `TIERS :83-84`, `CONTAINER :91-97`, `READY_CHECKS :169-172`, `wait_http_200(url, timeout)` all as cited; removing `ollama` from the tiers means `stack.py query` will now stop `iverson-ollama`, which Step 5's closing `docker compose up -d` restores before Task 8 needs it |
| Step 2 `ingest.py` prose | ok — `OLLAMA_URL :150`, its two uses at `:736-737`, `--model` default at `:723`, `--limit` exists, and the probe line `[ingest] probed embedding dimension … for model '…' at …` is `:787`, matching the expected grep |
| Step 2 residual "Ollama" wording deliberately kept in `--model`/`--embed-url` help | → §2.2 |
| Step 2 smoke command (`--drop --limit 5 --object-collection scratch_objects …`) + stats grep | ok — the stats sidecar is `json.dump(indent=2)` so `"model": "…"` / `"embed_url": "…"` match the `grep -o` pattern (confirmed against `scifact-bge-base-2026-09-04/keymap.json.stats.json`) |
| Step 3 Launcher code block | ok — `WaitForHttp200Async` mirrors the existing `WaitForOllamaAsync` (`:72-103`) shape exactly, including its `catch (OperationCanceledException) { return; }`; compose `up` list at `:20`, the wait at `:28`. Dropped: the silent return on a 5-second `HttpClient.Timeout` is the pre-existing house pattern, not something this plan breaks |
| Step 4 kind note prose | ok — `setup.sh:98` / `setup.ps1:95` are the only ollama mentions in those files |
| Step 5 harness commands and expected values | ok — `bench-env.sh` exists with the admin client credentials; `scifact-bge-base-2026-09-04/runs/bge-base.chunks.trec` is exactly 15,000 lines; `scifact-run-2026-08-26/keymap.json` and `scifact-bge-base-2026-09-04/keymap.json` are byte-identical (`md5 875be16b…`), so `--key-map-path $SCI/keymap.json` against the restored M1 index is right |
| Step 6 commit | ok |

### Task 7 — `EnrichmentService` chat port + source-text cap (Phase B)

| Surface | Disposition |
|---|---|
| Step 1 test file (9 facts) | ok — `IEnrichmentService` is `ModelId` / `GenerateAsync` / `GenerateJsonAsync`; `EnrichmentServiceTests.cs:37` is the only `new EnrichmentService(`; the URI assertion `http://tgi:8092/v1/chat/completions` is what `new Uri(new Uri("http://tgi:8092"), "/v1/chat/completions")` produces |
| Step 2 consumer cap test | ok — `EnrichedArticle()` has `VectorFields = []` and one `ChunkFields` entry `"Body"`, and `RowJson(body)` sets only `Body`, so `BuildSourceText` returns exactly the 20,000 `'x'` characters and the cut leaves exactly `MaxSourceChars` of them: both `Contain(new string('x', 8000))` and `NotContain(new string('x', 8001))` hold. `HandleUpdated_PublishesPostCommitRefetch_NotThePreGenerationSnapshot` exists at `:252` as the stated insertion point; the extraction hint `"the author's stated conclusion"` and the `StartWith`/`EndWith` targets match `EnrichmentPrompts.Extraction` + `EnrichmentConsumer.cs:273-276` |
| Step 3 `EnrichmentService.cs` body (regex, `BalancedObject`, `ExtractJson`, request shape) | ok — compiled and ran the exact `FencedBlock`/`BalancedObject`/`ExtractJson` code plus the serialization on net10.0: `{"model":"m","messages":[{"role":"user","content":"hi"}],"max_tokens":256,"temperature":0,"stream":false}`; the prose-wrapped fenced reply, the brace-inside-a-string reply and the bare object all extract correctly, and the no-object input throws `InvalidOperationException` naming the head. `using System.Text.RegularExpressions;` is present; the primary-constructor field initializer `_baseUrl = options.Value.BaseUrl` is legal |
| Step 3 prose "the named client's BaseAddress is not the request base" | ok — `ServiceCollectionExtensions.cs:36-37` still sets `BaseAddress` and `Timeout` from options; only `Timeout` remains load-bearing after the absolute-URI change, and nothing else reads the base address |
| Step 4 consumer cap placement (`:43` constant, `:129` cut) | ok — `EnrichmentConsumer.cs:129` is `var sourceText = BuildSourceText(schema, row);` and `:130` is `ComputeHash(sourceText, …)`, so the cut lands before the loop-prevention hash exactly as spec S9 requires; `GroupId` is at `:43` |
| Step 5 run + two hand mutations | ok — `51 = 48 + 9 − 6` is consistent with the counted attributes; the Api.Tests filter targets `Iverson.Api.Tests.Consumers.EnrichmentConsumerTests`, a substitute-only class |
| Step 6 commit | ok |

### Task 8 — compose `tgi`, `enrich_bench.py`, measurement, verdict

| Surface | Disposition |
|---|---|
| Step 1 compose `tgi` service block | ok — rendered the exact block through `docker compose config` in scratch: `start_period` normalises to `25m0s`, `shm_size` to `1073741824`, `8092:80` published |
| Step 2 `enrich_bench.py` prompt constants | ok — `SUMMARY`, `KEYWORDS`, `EXTRACTION`, `CHUNK_CONTEXT` are byte-for-byte `EnrichmentPrompts.cs:5-23`; `build_prompts` slices 50/10/10/5 over 5,183 corpus documents and caps Summary/Keywords/Extraction inputs at 8,000 chars, matching `MaxSourceChars` |
| Step 2 `extract_json` (Python replica of the service rule) | ok — same fence-then-balanced-brace mechanics as the C# original, including the string-aware brace scan and the `json.loads` validation; both failure directions checked (fenced-but-invalid → False, no object → False) |
| Step 2 `MemorySampler` regex + unit table | ok — podman's `docker stats --no-stream --format '{{.MemUsage}}'` prints `63.19MB / 10.43GB`; `re.search` takes the first group, i.e. the usage not the limit, and `MB→1e6` is in the table |
| Step 2 `--backend NAME=URL=MODEL=CONTAINER` parsing | ok — `split("=", 3)` yields 4 fields for both invocations; neither URL nor model id contains `=` |
| Step 3 command block (`tee $OUT/run.log`) | → §2.6 |
| Step 3/Step 4 gate criterion 5 measurement conditions | → §2.7 |
| Step 4 gate arithmetic (p95 index, failed/empty/parse counts) | ok — `walls[max(0, int(round(0.95*75))) - 1]` = index 70 over the sorted 75, and every key the gate reads (`by_kind.ChunkContext.mean_wall_s`, `p95_wall_s`, `failed`, `empty`, `extraction_parse_ok`, `min_mem_available_bytes`) is emitted by `run_backend`'s summary dict |
| Step 5 verdict-document prose | ok — mirrors `docs/plans/2026-09-GATE-embedding-migration.md`'s shape; correctly refuses to pick the 3B/cloud follow-up |
| Step 6 commit (`git add -f` for the gitignored gate doc) | ok |

### Task 9 (Phase C) — `tgi` subchart, Ollama deleted, Terraform rename

| Surface | Disposition |
|---|---|
| Step 1 subchart prose + container code block | ok — the container block is `tei`'s shape plus `env`, `workingDir: /tmp`, the `/dev/shm` memory `emptyDir` and the 15s/120 startup budget; parity with the tei render (which kube-score scored ✅ on every profile) means no new CRITICAL is reachable — ephemeral-storage requests/limits are present, tag is pinned, no liveness/readiness collision |
| Step 1 `condition: global.enrichmentEnabled` + the in-template `and` guard | ok — `Chart.yaml:17` (`condition: global.engagementEnabled` on starrocks) is the precedent, and the subchart's own `enabled: true` default makes `--set tgi.enabled=false` work |
| Step 2 values prose (global flag, `tgi:` blocks, laptop `enrichmentEnabled: false`) | ok as written; but see §2.5 for what "laptop has no tgi" costs |
| Step 3 deployment/policy edits + `charts/ollama` deletion | ok — the two `Enrichment__BaseUrl` sites (`api :127-128`, `worker :122-123`) and the two egress ollama targets (`:54`, `:85`) plus the two policies (`:391-418`) are the complete set |
| Step 4 Terraform rename | ok — the map keys, variables and Azure `label` attribute are as cited. `grep -rn ollama Iverson.Server/deploy/terraform` → 0 holds, because the surviving mention (`cluster-aws/main.tf:488`, "StarRocks/Qdrant/Kafka/Ollama StorageClasses") is capitalised and the grep is case-sensitive — but that same line is caught by Task 10's case-insensitive sweep (→ §2.2) |
| Step 5 `helm dependency build` | → §2.1 |
| Step 5 remaining assertions (`ls charts/*.tgz \| grep -c ollama`, per-profile `grep -c 'ollama'`, `iverson-tgi` counts, the `Enrichment__*` count of 4) | ok — `helm dependency update` deletes the outdated `ollama-0.1.0.tgz` automatically (verified in scratch: "Saving 12 charts / Deleting outdated charts"); the Chart.yaml `description` line is not rendered by `helm template`, so the per-profile `grep -c 'ollama'` = 0 assertion holds |
| Step 6 commit | ok |

### Task 10 (Phase C) — Ollama removed from compose, code, Launcher, scripts

| Surface | Disposition |
|---|---|
| Step 1 compose deletions and `Enrichment__*` env | ok — `ollama :124-136`, `ollama-init :138-149`, `ollama_data :571`; `docker compose config \| grep -ci ollama` → 0 holds because `config` strips comments |
| Step 2 code/wording edits (`EnrichmentServiceOptions :6-7,14`, `EnrichmentConsumer.cs:16`, Launcher, stack.py docstring, kind notes) | ok — every cited line confirmed |
| Step 2 repo-wide "must print nothing" Ollama grep | → §2.2 |
| Step 3 build/test/stack commands | ok |
| Step 3 live enrichment verification via the .NET conformance driver | → §2.3 |
| Step 3 log-line greps | ok on the worker side — `EnrichmentConsumer.cs:200` logs `[Enrichment] Enriched {Count} column(s) for {Type}:{Key}`, so the grep pattern is real (it just never fires, per §2.3) |
| Step 4 commit | ok |

### Task 9′ (Phase C′) and Task 11 (kind smoke)

| Surface | Disposition |
|---|---|
| Task 9′ prose "what Tasks 2–8 landed is already the spec's Phase C′ shape" | ok on every enumerated element — the ollama subchart pulls only `global.generativeModel` (`qwen2.5:3b`), compose keeps `ollama`/`ollama-init`, `Enrichment__BaseUrl` still points at ollama, `EnrichmentServiceOptions` defaults untouched (Task 10 is the only task that changes them), and the `tgi` compose service plus the Terraform `tei` pool exist |
| Task 9′ prose "the ported EnrichmentService works against Ollama (Task 8's Ollama rows are its evidence)" | dropped as its own finding — `enrich_bench.py` speaks HTTP directly and never instantiates `EnrichmentService`, so the citation is wrong, but the underlying claim is carried by spec §9 row 14 / S7, which CIR trusts. The consequence (nothing ever runs the ported client against a live backend) is folded into §2.3 |
| Task 9′ Steps 1–2 | ok |
| Task 11 Step 1 (`docker compose stop`, kind create, setup.sh, build-and-load-image.sh, helm upgrade) | ok on mechanics — `kind-config.yaml` is a one-node cluster with the default CNI disabled, `setup.sh` installs operators + metrics-server and does not create the cluster, `build-and-load-image.sh [tag] [cluster-name]` takes exactly that positional order, podman `pids_limit = -1` is set, no kind cluster exists today (so `kind create` runs), and the compose stack's host 8080 is freed by the `docker compose stop` first. `helm dependency build` here → §2.1 |
| Task 11 Step 1 profile choice (`values-laptop.yaml`) | → §2.5 |
| Task 11 Step 2 `kubectl exec … wget -qO- …/info` | → §2.4 |
| Task 11 Step 2 remaining assertions | ok — `kubectl logs sts/…`, the `EmbeddingService initialized` grep and the Pending-pod note are all reachable; laptop-profile requests rise from ~4.4 Gi to ~5.4 Gi of rendered memory and by 250m CPU, which the step already instructs the implementer to observe |
| Task 11 Step 3 restore | ok |

### Cross-task interface contracts

| Contract | Disposition |
|---|---|
| Task 2 **produces** the values shape → Task 9 / Task 11 **consume** it | ok — `global.embeddingModels[0].slug` is the only new required key and both later tasks read it through the same helper |
| Task 2 **produces** `iverson.embeddingBaseUrl` → api/worker deployments consume it | ok — rendered from the api and worker subchart contexts; `.Values.global` propagates and `index … 0` is satisfied by the single-entry list on every profile |
| Task 4 **produces** the compose stack (0 schema rows, TEI serving bge-base, restored M1 index) → Tasks 5, 6, 8 consume it | ok — the persistence boundary is the `iversonserver_*` named volumes plus Qdrant's on-disk collections, both of which survive `docker compose up -d`; Task 6's `stack.py query` stops the out-of-tier containers Task 8 needs and Step 5 restores them |
| Task 4 **produces** the restored `benchmark_documents*` collections → Task 6 Step 5's `benchmark-query` consumes them | ok — `benchmark-query` re-registers `BenchmarkDocument` itself (spec P3) and the snapshot vectors are 768-dim bge-base, matching the new default; `keymap.json` is byte-identical between the two corpus directories |
| Task 7 **produces** `EnrichmentConsumer.MaxSourceChars` (internal) → the Api.Tests case reads it | ok — `Iverson.Api.csproj:10-11` grants `InternalsVisibleTo("Iverson.Api.Tests")`; `Iverson.Embeddings.csproj` grants none, which is why the service-side test asserts the literal `256` instead |
| Task 7 **produces** the backend-neutral client → Task 8 (Phase C) and Task 10 consume it | **contract is not exercised** — `enrich_bench.py` is a Python replica that never calls `EnrichmentService`, and Task 10's live check cannot fire (→ §2.3). This is the eval-side-replica-of-a-runtime-call case: the replica's parameters (prompt strings, `max_tokens`, temperature) were traced to `EnrichmentPrompts.cs` and match, but nothing traces the runtime path |
| Task 8 **produces** `results.json` → Task 8 Step 4's gate and Step 5's verdict consume it | ok on field names (every key the gate reads is written); `run.log` is the one produced artifact the consumer names that never gets created (→ §2.6) |
| Task 8 **produces** the verdict → Task 9/10 (pass) or Task 9′ (fail) | ok — "Never both branches" is stated and the five criteria are mechanical |

### Rule-like content (both failure directions)

| Rule | Disposition |
|---|---|
| `ExtractJson`: first fenced block, else first balanced `{…}` | ok both ways — over-inclusion (trailing prose, a `}` inside a string, a second object) and under-inclusion (no fence, no object) all exercised on the real code |
| Cap rule: cut the **source text**, never the assembled prompt | ok both ways — under-cut (a cap on the prompt drops the trailing hint) is the named mutation; over-cut is bounded because the cut is exactly `MaxSourceChars` and lands before the instruction is prepended |
| Loop-prevention hash consistency (cut before `ComputeHash`) | ok — `:129` precedes `:130`, so the hash covers what was sent; cutting after would re-enrich forever |
| Task 10's "every remaining Ollama reference" exclusion rule | → §2.2 — under-inclusive exclusions and over-inclusive expectation both fail |
| Gate: five criteria, "any miss → FAIL" | ok on mechanics; criterion 5's measurement conditions → §2.7 |
| kube-score scoping rule ("assert on new `-tei-`/`-tgi-` objects only, never exit 0") | ok — the baseline genuinely exits 1 (`iverson-starrocks-create-user-egress` and others), `iverson-ollama` scores ✅, and the `💥`-on-header-line format makes the scoped grep falsifiable rather than vacuous |

---

## 1. Verified-plan-assumptions cross-check

| # | Result |
|---|---|
| P1 | **still holds** — `charts/` holds no `tei`/`tgi`; `scripts/` holds no `enrich_bench.py`; `docs/plans/` holds no `2026-09-GATE-enrichment-backend.md`; `charts/ollama/templates/{pdb,service,statefulset}.yaml` all present |
| P2 | **still holds** — `setup.sh:97` echoes `helm upgrade --install iverson . -f values-local.yaml -n iverson`; `build-and-load-image.sh` takes `[tag] [cluster-name]` and re-tags `docker.io/library/…`; `kind-config.yaml` is one node with `disableDefaultCNI: true`; `setup.sh` installs operators + metrics-server and creates no cluster |
| P3 | **still holds** — `IEnrichmentService.cs` is exactly the three members; `grep 'new EnrichmentService('` → only `EnrichmentServiceTests.cs:37`; `BuildSut()` at `:133-135` constructs `EnrichmentConsumer` with nine arguments and gains none |
| P4 | **still holds** — `EnrichmentConsumerTests.cs:75` configures `GenerateJsonAsync`, `:77` feeds `FetchByKeyAsync`, `:81-104` is `EnrichedArticle()` with the `"the author's stated conclusion"` hint, `:119` is `RowJson` |
| P5 | **still holds** — `EnrichmentConsumer.cs:272-276` builds `string.Format(EnrichmentPrompts.Extraction, sourceText) + $"\n\nExtract specifically: {target.Hint}"`; `EnrichmentPrompts.cs:12-13` starts `"Extract structured information"`; `:129`/`:130` are the assignment and the hash |
| P6 | **still holds** — `GenerateInternalAsync(prompt, jsonFormat, ct)` at `:25-66`, `_jsonOpts` camelCase at `:14-15`, `Telemetry.EnrichmentHttpClientName` at `:34`, the four activity tags at `:28-30`/`:55` |
| P7 | **still holds** — `WaitForOllamaAsync(url, modelName, ct)` at `:72-103` with `Task.Delay(3000)`, `WaitForPortAsync` at `:105`, the `up` list at `:20` |
| P8 | **still holds** — `stack.py` `TIERS :83-84`, `CONTAINER :91-97`, `READY_CHECKS :169-172`, `wait_http_200(url, timeout) :148`; `ingest.py` `OLLAMA_URL :150` used at `:736-737` |
| P9 | **still holds** — `charts/ollama/templates/statefulset.yaml:59-62` reads `.Values.global.embeddingModels` and `.Values.global.generativeModel`; `Chart.yaml:17` carries `condition: global.engagementEnabled`; `charts/ollama/values.yaml` has exactly `replicas, storageSize, storageClassName, imageTag, resources, nodeSelector, tolerations` |
| P10 | **still holds** — helm 3.16.4, kube-score 1.19.0, kubeconform, terraform 1.9.8, kind, kubectl all on PATH; the workflow's invocations at `deploy-validate.yml:28-33,55-62,80-96,110-126` are quoted correctly |
| P11 | **holds in part — the `helm dependency build` half fails once the plan's own edits land.** Reproduced on a scratch copy of the *current* chart: `helm dependency build` succeeds, `helm lint -f values-laptop.yaml` passes, kubeconform reports 0 invalid, kube-score exits 1 on pre-existing objects while `iverson-ollama` scores ✅ — so the "assert on the new objects only" scoping is correct. But the assumption was measured against the unmodified `Chart.yaml`; with a `tei` dependency added it errors `the lock file (Chart.lock) is out of sync with the dependencies file (Chart.yaml)` and exits 1. See §2.1 |
| P12 | **still holds** — all three csproj paths exist; `EnrichmentConsumerTests` is substitute-only (no `IClassFixture`, no Testcontainers) |
| P13 | **still holds** — `docs/runbooks/client-conformance-matrix.md:20-46` carries the four exports verbatim and documents `dotnet run -- --languages …` as the partial form; node 24.18, go 1.22, java 21, maven 3.9.9, python 3.14, dotnet all present |
| P14 | **still holds** — 36 `_iverson_schema` rows; Qdrant holds the four benchmark/vector collections plus `iverson-probe`; 11434 listening; 8091 and 8092 free |
| P15 | **still holds** — two `.snapshot` files, both named `…-6802952876034638-…`, and `scifact-512-qdrant-snapshots/RESTORE.md` carries the `${f%%-6802952876034638*}` loop |
| P16 | **still holds** — ran the exact serialization on net10.0: `{"model":"m","messages":[{"role":"user","content":"hi"}],"max_tokens":256,"temperature":0,"stream":false}` |
| P17 | **still holds** — ran the exact `ExtractJson`/`BalancedObject`/`FencedBlock` code over the four inputs; three extract, the fourth throws `InvalidOperationException` naming the head |
| P18 | **still holds** — `docker compose config` renders `start_period: 25m0s` and `shm_size: "1073741824"`; podman's `docker stats --no-stream --format '{{.MemUsage}}'` prints `63.19MB / 10.43GB`; `/proc/meminfo` line 1 is `MemTotal` in kB with `MemAvailable` in the same units |
| P19 | **still holds as stated** — `values-laptop.yaml:9-11` reads "250+250+250+250+100+100+50+200 = 1.45 CPU … Total ~3.55 of 4.0 … roughly 450m"; `nproc` = 4; a 500m TEI request would take it to 4.05 and 250m to 3.8. (The file's own inventory omits `authentik-worker`'s 100m, so the real headroom is ~100m thinner than either the old or the new comment claims — comment-only, no finding.) |
| P20 | **still holds** — `grep HttpClientName` finds exactly `ServiceCollectionExtensions.cs:13,31`, `EmbeddingService.cs:69,97`, `EnrichmentService.cs:34`; `IdpAdminClient.HttpClientName` = `"iverson.authentik"` is a separate constant |
| P21 | **still holds** — `~/.config/containers/containers.conf:7` has `pids_limit = -1` |
| P22 | **still holds** — every dependency the row asserts is real; additionally verified that Task 6's `stack.py query` stops `iverson-ollama` and Step 5 restarts it before Task 8 needs it |
| P24 | **still holds** — `Iverson.Embeddings.csproj` declares no `InternalsVisibleTo`; `Iverson.Api.csproj:9-12` grants it to `Iverson.Api.Tests` |
| P23 | **still holds** — every `.Values.tei.*` key the rendered templates read is defaulted in the subchart `values.yaml` and survives the parent/profile map-merge (verified by rendering all five profiles); every `$.Release.Name`/`$.Values` inside a `range` uses the `$` root; `c7i.xlarge` / `c2-standard-4` / `Standard_F4s_v2` are one step below `c7i.2xlarge` / `c2-standard-8` / `Standard_F8s_v2` |

### Span check — plan dependencies with no covering assumption

- **`helm dependency build` behaviour when the dependency list itself changes.** P11 verifies the command only on the unmodified chart. Verified in-round: it fails. → §2.1.
- **What HTTP client the `iverson-api` runtime image ships**, which Task 11 Step 2's in-cluster `/info` probe depends on. No assumption covers it. Verified in-round against the running container: only `bash`; no `wget`, no `curl`. → §2.4.
- **Whether any live-registerable type declares an enrichment target**, which Task 10 Step 3's end-to-end TGI check depends on. No assumption covers it. Verified in-round: `[IversonSummary]`/`[IversonKeywords]`/`[IversonExtracted]` appear nowhere in the five conformance drivers, the `Iverson.LoadTest` entities or any sample — `VectorDoc.cs:10-12` says so explicitly. → §2.3.
- **Ollama's model residency during the TGI measurement window**, which gate criterion 5 depends on. No assumption covers it. Verified in-round by reasoning from Ollama's default 5-minute `keep_alive` against the plan's Ollama-first backend order and the 5-second sampling interval. → §2.7.
- **The laptop profile's *memory* headroom** with the added TEI pod. P19 covers CPU only. Verified in-round: rendered container memory requests rise from ~4.4 Gi to ~5.44 Gi (plus ~1 Gi from the CNPG/Strimzi-owned pods the chart does not render) on a 10.19 GB box — tight but not disqualifying, and Task 11 Step 2 already instructs the implementer to record any Pending pod with its reason. No finding.

---

## 2. Literal-wrongness findings

### 2.1 `helm dependency build` fails at every point the plan calls it, because the plan changes `Chart.yaml`'s dependency list

**Description.** Tasks 2 (Step 5), 9 (Step 5) and 11 (Step 1) all run `helm dependency build .` after `Chart.yaml`'s `dependencies:` list has changed — Task 2 adds `tei`, Task 9 removes `ollama` and adds `tgi`. `helm dependency build` only materialises what `Chart.lock` already records; when the lock and `Chart.yaml` disagree it refuses. The chart checks that follow (`helm lint`, `helm template | kubeconform`, `helm template | kube-score`) never run, and on Task 11 the release is installed against whatever `charts/*.tgz` happens to be on disk.

**Evidence.** On a scratch copy of `Iverson.Server/deploy/helm/iverson` with a `tei` dependency added to `Chart.yaml` and a minimal `charts/tei/` present:

```
$ helm dependency build .
Error: the lock file (Chart.lock) is out of sync with the dependencies file (Chart.yaml). Please update the dependencies
exit=1
```

`helm dependency update .` on the same tree succeeds ("Saving 13 charts / Deleting outdated charts") and produces `charts/tei-0.1.0.tgz` plus a regenerated `Chart.lock`; repeating it after deleting the `ollama` dependency and `charts/ollama/` yields "Saving 12 charts" and removes `charts/ollama-0.1.0.tgz` without a manual `rm`. Plan assumption P11 measured `helm dependency build` against the *unmodified* chart only. `.github/workflows/deploy-validate.yml:25,52,80` also runs `helm dependency build`, which is correct in CI precisely because the regenerated `Chart.lock` is committed.

**Proposed fix.** Change all three `helm dependency build .` invocations to `helm dependency update .` (Task 2 Step 5, Task 9 Step 5, Task 11 Step 1). In Task 9 Step 5, the manual `charts/ollama-0.1.0.tgz` deletion in the Files list becomes redundant — `update` prunes it — but leave the `ls charts/*.tgz | grep -c ollama # 0` assertion in place as the check. Note in Task 2 Step 6 and Task 9 Step 6 that the regenerated `Chart.lock` must be committed so CI's `helm dependency build` stays in sync.

### 2.2 Task 10 Step 2's "must print nothing" Ollama sweep is unsatisfiable as written

**Description.** Task 10 Step 2 ends with a repo-wide case-insensitive `grep -rli ollama` over `*.cs *.yml *.yaml *.tf *.sh *.ps1 *.py *.tpl *.ts *.java *.go` with a fixed exclusion list, asserted to "print nothing". Six classes of file still match after every Phase A + Phase C edit the plan describes, so the Phase C branch ends on a check that cannot pass — and the implementer's only options are to edit files no task lists or to silently weaken the assertion.

**Evidence** (all confirmed by running the plan's exact grep against the current tree and then subtracting the edits each task specifies):

1. `Iverson.Server/docker-compose.yml:67` — `# … Follows the \`ollama-init\` idiom already in this file:` sits in the `starrocks-init` comment. Task 10 Step 1 deletes only the `ollama`/`ollama-init` services and the volume. (`docker compose config | grep -ci ollama` → 0 still passes, because `config` strips comments.)
2. `Iverson.Server/deploy/helm/iverson/Chart.yaml:3` — `description: Iverson — … Qdrant, Ollama, API, …`. Task 9 Step 1 edits only the `dependencies:` list.
3. `values-laptop.yaml:6` — `# … PRODUCTION defaults (postgres 8 CPU x2, qdrant 8, ollama 8) …`, outside both the `embeddingModels` block Task 2 replaces (`:14-26`) and the `ollama:` block Task 9 deletes (`:57-68`).
4. `values-aws.yaml:88`, `values-azure.yaml:81`, `values-gcp.yaml:82` — `# modules/cluster-<cloud> (only postgres/kafka/starrocks/qdrant/ollama do — confirmed`, outside the `ollama:` blocks at `:59-67` / `:60-68`.
5. `Iverson.Server/deploy/terraform/modules/cluster-aws/main.tf:488` — `# StarRocks/Qdrant/Kafka/Ollama StorageClasses this module creates (Task 2)`. Task 9 Step 4's own check (`grep -rn ollama …` → 0) misses it because that grep is case-sensitive; Task 10's is not.
6. `Iverson.Server/Iverson.LoadTest/scripts/ingest.py` — Task 6 Step 2 *deliberately* keeps `"Against Ollama it must already be pulled …"` in `--model`'s help and `"… Ollama is http://localhost:11434"` in `--embed-url`'s. And `Iverson.Server/Iverson.LoadTest/scripts/enrich_bench.py`, created by Task 8, carries `--backend ollama=http://localhost:11434=qwen2.5:3b=iverson-ollama` in its module docstring and usage example. Both are `*.py` under `scripts/` and match no exclusion.

**Proposed fix.** Split the assertion into the two things it is conflating. (a) Add the missing wording edits to the tasks that own those files: `Chart.yaml:3`'s description and the three cloud values files' trailing comment to Task 9 Step 2; `values-laptop.yaml:6` to Task 9 Step 2; `docker-compose.yml:67` and `cluster-aws/main.tf:488` to Task 10 Step 2's file list. (b) Extend the grep's exclusion list with `ingest.py` and `enrich_bench.py` and state why they are exempt (both legitimately name Ollama as an alternative backend / the measurement baseline, exactly as spec §2 leaves the `EmbeddingPrefixes` nomic rows in place). Do the same for Task 9 Step 4's Terraform grep by making it `grep -rni`.

### 2.3 Task 10 Step 3's live enrichment verification rests on a false premise: no registerable type in the repo declares an enrichment target

**Description.** Task 10 Step 3 is the only step in either branch that exercises the ported `EnrichmentService` against a real generative backend. It says: "the .NET conformance driver's `VectorDoc` carries `[IversonSummary]`/`[IversonKeywords]`, so … run `… dotnet run -- --languages dotnet` (exit 0), then `docker logs iverson-worker … | grep -c '\[Enrichment\] Enriched'` ≥ 1 and `docker logs iverson-tgi … | grep -c 'chat_completions'` ≥ 1." `VectorDoc` carries no enrichment annotation, and neither does any other type the harness or the LoadTest registers, so the worker's `EnrichmentConsumer` is never invoked, no `[Enrichment] Enriched` line is ever written, and no request ever reaches TGI. The step cannot pass and the branch's only end-to-end check is vacuous.

**Evidence.** `Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Models/VectorDoc.cs:10-12` states the opposite of the plan's premise:

```
/// Deliberately relation-free, and deliberately without any enrichment annotation
/// (<c>[IversonSummary]</c>, <c>[IversonKeywords]</c>, contextual chunking): the scenario's exact
/// set comparisons must not depend on generative output that differs run to run.
```

and its body (`:33-43`) carries only `[IversonEntity]`, `[IversonKey]`, `[IversonMetadata]`, `[IversonEmbedding]`, `[IversonChunk]`. Repo-wide, `grep -rn '\[IversonSummary\]\|\[IversonKeywords\]\|\[IversonExtracted'` over the five conformance drivers, `Iverson.Server/Iverson.LoadTest` and every sample returns nothing but that one doc comment; the only other hits are the attribute definitions, the registrar guards, and `EnrichmentConsumer.cs:15` / `SchemaRegistrationOrchestrator.cs:696` comments. `Iverson.Server/Iverson.ClientConformance/` contains no file mentioning `Enrich` at all. The log string the step greps for is real (`EnrichmentConsumer.cs:200`) — it simply never fires.

The gap is wider than one step: on the expected FAIL branch (Task 9′) nothing rebuilds or redeploys the API image after Task 7, so the compose worker keeps running the pre-port `/api/generate` build, and `enrich_bench.py` talks HTTP directly rather than through `EnrichmentService`. Across both branches, the ported client is exercised only by `EnrichmentServiceTests`' fakes.

**Proposed fix.** Give Task 10 Step 3 (and, for symmetry, Task 9′) an actual enrichment trigger: register a throwaway type carrying `[IversonSummary]` + `[IversonExtracted]` through the existing `Iverson.LoadTest` or a scratch client program, post one row, and then assert the `[Enrichment] Enriched` line and the TGI (or, in Phase C′, Ollama) access log — dropping the schema row and its collections afterwards, since Task 4 has already established that clearing `_iverson_schema` is safe here. On the C′ branch, add a `docker compose build iverson-api && docker compose up -d iverson-worker` before the check so the running worker actually carries Task 7's code.

### 2.4 Task 11 Step 2's in-cluster `/info` probe cannot run in either of the two forms the plan names

**Description.** Task 11 Step 2 ends with

```
kubectl -n iverson exec deploy/iverson-api -- sh -c 'wget -qO- http://iverson-tei-bge-base:8080/info' … # use curl if wget is absent in the api image
```

The API runtime image ships neither `wget` nor `curl`, so both the command and the fallback the plan names fail. This is the only assertion in the smoke that proves the api pod can actually reach TEI from *inside* the NetworkPolicy boundary — the rest of Step 2 reads logs and object lists, which pass whether or not the `tei-ingress`/`api-egress` rules are right.

**Evidence.** `Iverson.Server/Iverson.Api/Dockerfile:16` — `FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime`. Against the running container built from that image:

```
$ docker exec iverson-api sh -c 'command -v wget; command -v curl; command -v bash'
/usr/bin/bash
```

`docker-compose.yml:453-456` records the same fact for the healthcheck: "A bash /dev/tcp redirect rather than curl/wget: the aspnet:10.0 runtime image ships neither (verified), but does ship bash."

**Proposed fix.** Use the repo's own idiom, which is already proven against this image:

```
kubectl -n iverson exec deploy/iverson-api -- bash -c \
  'exec 3<>/dev/tcp/iverson-tei-bge-base/8080; printf "GET /info HTTP/1.1\r\nHost: tei\r\nConnection: close\r\n\r\n" >&3; cat <&3' \
  | grep -o '"model_id":"[^"]*"'
```

A throwaway `kubectl run` pod is not a substitute: `tei-ingress` admits only pods labelled `app: iverson-api` / `app: iverson-worker`, which is precisely what this assertion is meant to prove.

### 2.5 Task 11 substitutes `values-laptop.yaml` for the spec's `values-local.yaml`, which removes the only verification the spec assigns to the `tgi` subchart

**Description.** Spec §8 lists, as a required test, "kind deployment on `values-local.yaml`: TEI pod ready, API log `EmbeddingService initialized: model=BAAI/bge-base-en-v1.5 dimension=768`, and (Phase C) the tgi pod ready and one worker enrichment logged"; spec §11 says the `workingDir: /tmp` fix for TGI's read-only-root `Errno 30` failure "is verified by the kind smoke's absence of the `Errno 30` line, not by a design probe." Task 11 installs `values-laptop.yaml` instead, and Task 9 Step 2 sets `global.enrichmentEnabled: false` on that profile — so the `tgi` subchart renders zero objects there. The plan states the consequence ("the `Errno 30` check applies only where a tgi pod runs, which the laptop profile does not — record that explicitly") but does not state that it is dropping a spec §8 test item, and no other task fills the gap: Task 9's `tgi` templates are validated only by `helm template` / `kubeconform` / `kube-score`, never by a running pod. `workingDir: /tmp`, the uid-1000 read-only `securityContext`, the `/dev/shm` memory `emptyDir`, the `--port 8080` binding and the 15s×120 startup budget all ship having never been executed.

**Evidence.** Plan Global Constraints ("the kind smoke runs on `values-laptop.yaml`") and Task 11 Step 1 (`helm upgrade --install iverson . -f values-laptop.yaml …`) against spec §8's `values-local.yaml`; Task 9 Step 2 ("laptop: `global.enrichmentEnabled: false` … and no `tgi:` block"); Task 11 Step 2 ("no tgi (laptop: enrichmentEnabled false)"). Confirmed against the chart: the `tgi` dependency's `condition: global.enrichmentEnabled` (Task 9 Step 1) means Helm does not even load the subchart on that profile. The substitution itself is defensible on this box — Task 9 Step 2 sizes `values-local.yaml`'s `tgi` at `requests: {cpu: "2", memory: "6Gi"}`, which cannot schedule beside the rest of the profile on a 4-CPU / 10.19 GB host — but the plan never says so.

**Proposed fix.** Either (a) add a Phase-C-only Task 11 step that installs `values-local.yaml` with `--set tgi.resources.requests.memory=3Gi --set tgi.resources.requests.cpu=1 --set tgi.replicas=1 --set global.enrichmentEnabled=true` (and postgres/kafka/qdrant left at the local sizing) purely to bring the `tgi` pod to Ready and assert `kubectl logs sts/iverson-tgi | grep -c 'Errno 30'` → 0, or (b) state explicitly in Task 11 and in the gate document that spec §8's Phase-C kind assertion and spec §11's `Errno 30` verification are not performed on this box and name what would perform them. Silently substituting the profile is the one option that leaves the record wrong.

### 2.6 Task 8 Step 3 pipes into `$OUT/run.log` before anything creates `$OUT`

**Description.** The measurement command is

```
D=$(date +%F); OUT=/home/ben/repositories/iverson-benchmark-corpora/enrichment-bench-$D
…
python3 enrich_bench.py … --out $OUT … 2>&1 | tee $OUT/run.log | tail -40
```

Nothing in the shell creates `$OUT`; the script creates it (`os.makedirs(args.out, exist_ok=True)`) only after argparse, inside `main()`. The shell builds the whole pipeline before `python3` starts, so `tee` opens `$OUT/run.log` against a directory that does not exist yet and fails immediately. The 2.5-hour run then produces no log. The plan's own Step 3 note ("Run the script in the background and poll `$OUT/run.log`") and Step 5's requirement that the gate document say "where `run.log`, `results.json`, `side-by-side.md` live" both depend on that file.

**Evidence.**

```
$ TD=…/nodir; rm -rf $TD; (echo hello | tee $TD/run.log | tail -1); echo "exit=$?"
…/nodir/run.log: No such file or directory (os error 2)
hello
exit=0
```

— the pipeline's exit status comes from `tail`, so the failure is silent in a `set -e`-less step.

**Proposed fix.** Add `mkdir -p "$OUT"` immediately after the `OUT=` assignment in Task 8 Step 3 (the plan already creates the directory in every other artifact path it owns).

### 2.7 Gate criterion 5 is measured with Ollama's model still resident, so `min_mem_available_bytes` for TGI is not what the criterion asks for

**Description.** Spec §6.4 criterion 5 is "with the query tier and `tgi` up, the box's minimum `MemAvailable` stays above 500 MB". Task 8 Step 3 runs `--backend ollama=… --backend tgi=…` in that order, and `enrich_bench.py`'s `MemorySampler` records `min_avail` over each backend's own window at 5-second intervals. Ollama keeps a model resident for 5 minutes after its last request by default, so the first ~5 minutes of TGI's ~75-minute window — which is where the sampled minimum will land, since that is when both a ~2 GB Q4 model and TGI's 4.2–5.3 GB RSS coexist — is measured under conditions the criterion does not describe. With ~7.2 GB available before TGI starts, subtracting both models plausibly puts the recorded minimum under the 500 MB threshold and records criterion 5 as FAIL for a reason that has nothing to do with whether TGI-1.5B fits beside the query tier. The verdict is unaffected (criterion 1 is expected to fail anyway), but the gate document is the artifact Ben uses to decide whether to pursue the 3B candidate on a cloud node, and its "does 1.5B fit?" row would be wrong.

**Evidence.** Plan Task 8 Step 3's backend order; `enrich_bench.py`'s `run_backend` starts a fresh `MemorySampler` per backend and Step 3's own note says "Ollama loads its model on the first prompt"; spec §6.2 states only that "Backends are never run concurrently", which the request sequencing satisfies while memory residency does not. Spec §9 row 1 records 7.2 GB available with the query tier up; §9 row 10 records TGI RSS 4.24 GB at the chosen limits.

**Proposed fix.** Either reverse the backend order (`--backend tgi=… --backend ollama=…`, so TGI's window is clean and Ollama's is the one contaminated by TGI's already-freed memory), or unload Ollama before the TGI pass with `curl -s http://localhost:11434/api/generate -d '{"model":"qwen2.5:3b","keep_alive":0}'` and a short settle, and record in the gate document which one was done.

---

## 3. Forced decisions

No forced decisions found.

---

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes.**

§1 reconfirms 23 of 24 assumptions outright; P11 holds for the half it measured and fails for the half it did not (§2.1). §2 carries seven findings, none of which touches the plan's design: two are command defects that stop a task dead (§2.1 chart checks, §2.6 the measurement log), two are assertions that cannot pass as written (§2.2 the Ollama sweep, §2.4 the in-cluster probe), one is a verification built on a premise the codebase contradicts (§2.3), one is an undeclared substitution of a spec-mandated test (§2.5), and one is a measurement condition that records a wrong criterion value (§2.7). §3 is empty, so nothing blocks the plan pending Ben's input. Address the seven, then proceed to `subagent-driven-development`.
