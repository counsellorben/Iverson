# Critical Implementation Review: 2026-09-05-embedding-migration-phase2-implementation-plan (Round 2)

**Plan:** `/home/ben/repositories/Iverson/docs/plans/2026-09-05-embedding-migration-phase2-implementation-plan.md`
**Verified plan-level assumptions section:** present (P1–P28, 27 rows; P23 listed last)

⚠️ 3 commits since plan-write time (SHA `107eba5`); cited file:line references re-checked under §1. All three are plan/review-document commits (`4f92d2e`, `b213d31`, `3492e80`); no tracked code, chart, compose or Terraform file changed, so the codebase the plan was written against is the codebase reviewed here.

Method note: §0 was enumerated and worked before the round-1 review was read in detail. Everything in §2 is backed by a command actually run this round — a scratch copy of the chart carried through Task 2's and Task 9's full edit set (`helm dependency update`, `helm lint`, `kubeconform`, `kube-score` on all five profiles), a scratch copy of `Iverson.Embeddings` + `Iverson.Embeddings.Tests` carrying Task 7's rewritten test file and rewritten service (`dotnet test`, plus one mutation run), a scratch copy of `docker-compose.yml` carrying Task 4's and Task 8's edits (`docker compose config`), and the plan's own `enrich_bench.py` extracted verbatim and executed against the SciFact corpus. Nothing in the repo, the compose stack, Qdrant, Postgres or the corpora directory was mutated.

---

## 0. Coverage enumeration

### Task 1 — embedding defaults, telemetry names, wording (Phase A)

| Surface | Disposition |
|---|---|
| Step 1 code block (`EmbeddingServiceOptions`, `Telemetry`) | ok — `EmbeddingServiceOptions.cs:6-7` is exactly the two lines quoted (`http://localhost:11434` / `nomic-embed-text`); `Telemetry.cs:8-9` is exactly `"iverson.ollama"` / `"iverson.ollama.enrichment"`. Both replacements are drop-in `const`/property initialisers |
| Step 2 prose — the eight named wording sites | → §2.4 (line-level under-coverage in `IversonExtracted.java` and `annotations.ts`). The six server-side sites themselves check out: `SchemaRegistrationOrchestrator.cs:127-129`, `Program.cs:410-413`, `EmbeddingService.cs:66,112`, `EmbeddingPrefixes.cs:16` (left alone, correct), `BenchmarkIngestScenario.cs:240`, `ModelRejectedScenario.cs:79`, `VectorSearchScenario.cs:124` all read exactly as quoted |
| Step 2 — consumer impact of the orchestrator message rewrite | ok — checked whether anything asserts the changed tail. `ModelRejectedScenario.cs:263-297` asserts only `embedding model '<prior>'`, `resolves to '<override>'`, the `DELETE FROM …` clause and the two tenant-qualified collection names; the "must remain pulled … Ollama" sentence is asserted nowhere. `ModelRejectedScenarioTests.cs:44-59` holds a deliberate *copy* of the server message (its own doc comment says so) and is spec-D3-exempt, so it does not go red |
| Step 3 commands | ok — `grep -rn 'iverson\.ollama' --include=*.cs Iverson.Server` returns exactly the two `Telemetry.cs` lines today, so `0` after Step 1. The `# 48/48` count is confirmed indirectly: the scratch run of the Task 7 suite (48 − 6 + 9) produced exactly 51 |
| Step 3 claim "ClientConformance.Tests pins the scenario messages; it is the guard for the wording edits" | dropped — the two ClientConformance sites Task 1 edits (`:79`, `:124`) are XML doc comments, so the suite is not in fact a guard for them. No behaviour changes either way; the claim is optimistic, not wrong-making |
| Step 4 commit list | ok — all 13 paths exist |

### Task 2 — `tei` subchart and the Phase A chart shape

| Surface | Disposition |
|---|---|
| Step 1 `Chart.yaml` / `values.yaml` / three template code blocks | ok — written verbatim into a scratch chart. `helm dependency update` succeeds ("Saving 13 charts", produces `charts/tei-0.1.0.tgz`); `helm lint` passes on all five profiles; `kubeconform -kubernetes-version 1.30.0` reports Invalid: 0 / Errors: 0 on all five. The trailing `---` inside each `range` produces an empty trailing document that kubeconform tolerates |
| Step 1 — `$` root inside `range`, `index … "ephemeral-storage"` | ok — rendered correctly on all five profiles; the profile `tei:` blocks that omit `ephemeral-storage` still render it, because Helm coalesces the `resources` map deeply against `values.yaml` |
| Step 2 `values.yaml:29-50` range | ok — lines 29-50 are exactly the `embeddingModels` comment + list; 51-54 are the `activeEmbeddingModel` comment + key; 118-129 the `ollama:` block |
| Step 2 `values-local.yaml:10-22` range | ok — off by one at the start (`:10` is `global:`), but replacing it would orphan the two-space-indented block and fail `helm lint` immediately, so it self-corrects |
| Step 2 `values-laptop.yaml:14-26` range | → §2.2 |
| Step 2 laptop capacity-comment arithmetic | ok — 250×5 + 100×2 + 50 + 200 = 1.70 CPU; +2.0 system/operators +0.1 metrics-server = 3.8 of 4.0, matching P19 |
| Step 2 cloud `tei:` blocks | ok — `values-{aws,azure,gcp}.yaml` carry the `ollama:` block at `:59-67`/`:59-67`/`:60-68`; the new `tei:` block renders `storageClassName: iverson-tei` and the pool selector/toleration on all three |
| Step 3 `_helpers.tpl` append + `iverson.embeddingBaseUrl` | ok — appended before the final `{{- end -}}` of `iverson.embeddingEnv`; `$.Release.Name` resolves because both call sites `include … .` from the api/worker subchart context. Rendered output carries `Embeddings__Models__0__Name/BaseUrl` on both deployments |
| Step 3 deployment edit | ok — `charts/api/templates/deployment.yaml:124-125` and `charts/worker/…:119-120` are the exact `Embeddings__BaseUrl` pair; the `Enrichment__BaseUrl` two entries below does stay on `iverson-ollama:11434` in the render |
| Step 4 networkpolicies edit | ok — `:54-55` and `:85-86` are the two api/worker-egress ollama targets; the two new policies insert cleanly before `ollama-ingress` (`:393`) and kube-score scores both ✅ on every profile in Phase A |
| Step 4 ollama init-script edit | → §2.1 |
| Step 5 commands + five numeric assertions | ok — ran all of them against the scratch render: `^  name: iverson-tei-bge-base$` → **3**, the `Embeddings__*BaseUrl` grep → **4**, `ollama pull` → **1**, `clusterIP: None` → 3, `kube-score … \| grep -E 'tei-' \| grep -c '💥'` → **0** on local, laptop and aws. Every asserted number is exactly right |
| Step 6 commit | ok — `Chart.lock` and `charts/*.tgz` are tracked; the note about CI's `helm dependency build` matches `deploy-validate.yml:25,52,80` |

### Task 3 — Terraform `tei` pool and storage class

| Surface | Disposition |
|---|---|
| Step 1 pool/variable additions | ok — `cluster-aws/main.tf:511`, `cluster-gcp/main.tf:239`, `cluster-azure/main.tf:208` are the `ollama` map entries; the Azure map is the only one carrying `label`, and `main.tf:219-226` reads `each.value.label` for both node label and taint, so the plan's "Azure's label comes from this attribute, not the key" is right. Variable blocks at `aws:79-87`, `gcp:73-81`, `azure:84-92` |
| Step 2 storage class + output | ok — `operators/main.tf:175-181` is `kubernetes_storage_class.ollama` (`iverson-ollama`); `outputs.tf:7` references it; the new resource is a literal copy with a new name |
| Step 3 validate commands | ok — `deploy/terraform/{aws,azure,gcp}` roots exist and reference `../modules/…` relatively, so the scratch copy resolves. `terraform fmt -check` is non-recursive, so the module files Step 1 edits are not format-checked — but CI's own `fmt -check` (`deploy-validate.yml:118`) is non-recursive on the same roots, so no CI gate is missed |
| Step 4 commit | ok |

### Task 4 — compose Phase A and the box

| Surface | Disposition |
|---|---|
| Step 1 compose edits | ok — applied verbatim to a scratch `docker-compose.yml`. `ollama-init`'s `command` at `:143-148` is a two-line `&&` chain, so dropping the nomic line is mechanical; `tei-embed` at `:171-179` carries `profiles: ["tei"]`; api env `:401,410-419`, worker `:501,508-509`; `depends_on … ollama-init` at `:473` and `:545` |
| Step 1 five `docker compose config` assertions | ok — ran all five on the edited scratch file: `^tei-embed$` → **1**, `Embeddings__BaseUrl: http://tei-embed:80` → **2**, `Embeddings__ModelId: BAAI/bge-base-en-v1.5` → **2**, `Embeddings__Models__0` → **0**, `nomic-embed-text` → **0**. `BENCH_EMBED_MODEL`/`EMBED_MODEL_ID` are unset in `~/iverson-benchmark-data/bench-env.sh`, so the `:-` defaults do render |
| Step 2 row clear + collection drop + snapshot restore | ok — live read-only checks: `select count(*)` → **36**; Qdrant holds exactly the four benchmark/vector collections plus `iverson-probe`; `scifact-bge-base-qdrant-snapshots/` holds two `.snapshot` files both named with peer `6802952876034638`, and `scifact-512-qdrant-snapshots/RESTORE.md` carries the identical `${f%%-6802952876034638*}` loop; the bge-base `RESTORE.md` states 5,183 / 19,967 points and 768 dims, matching the asserted values |
| Step 2 — "read-only checks first" ordering | ok — the count and the collection listing precede the `DELETE` and the `DELETE /collections` calls |
| Step 3 rebuild/up + log assertions | ok — `EmbeddingService.cs:55` emits `EmbeddingService initialized: model={Model} dimension={Dimension} …`, `:81` emits `… serves model '…' but this service is …`; `curl -sf 127.0.0.1:8081/build` answers 200 on the live stack today |
| Step 3 recreate warning | ok — the four containers named are the ones whose config hashes changed; a recreate is explicitly not a stop condition |
| Step 4 commit | ok |

### Task 5 — conformance fixtures declare bge-base

| Surface | Disposition |
|---|---|
| Files list, 37 line citations | ok — `grep -rn 'nomic-embed-text'` over the four `conformance/` directories, the .NET driver and the two scenario files returns exactly the cited lines: `models.go:192,208,218`, `main.go:65,72`, `S11ModelJava.java:21,30`, `S12DeclaredJava.java:8,11`, `S12InheritedJava.java:13`, `Driver.java:89,96,394`, `models.py:202,214,231,234,246`, `driver.py:57,63,915`, `models.ts:295,303,322,327,333`, `driver.ts:49,55,520`, `S11ModelDotnet.cs:15,24`, `S12ModelDotnet.cs:7,11,18`, `Program.cs:54,62,902`, `InheritedModelScenario.cs:63`. No site is missed and none is invented |
| Step 1 grep | ok — the `Iverson.Clients/*/conformance` glob expands to the Go/Java/Python/TypeScript directories (DotNet's driver is named explicitly), so the grep covers every site above |
| Step 1 "SDK unit tests keep their literals" | ok — the five named files all live under `tests/`/`Tests/`/`_test.go` and none contacts a backend (spec Q2) |
| Step 2 matrix run + the four exports | ok — `docs/runbooks/client-conformance-matrix.md:23-27` carries the four exports verbatim, `:41-44` documents `dotnet run` / `--languages` |
| Step 2 psql assertion "five rows, all t" | ok — live `select type_name … where type_name like 'S11Model%'` returns exactly five rows today (`S11ModelDotnet/Go/Java/Python/Typescript`) |
| Step 3 commit | ok |

### Task 6 — scripts and the Launcher's TEI half

| Surface | Disposition |
|---|---|
| Step 1 `stack.py` code block + prose | ok as far as it goes — `TIERS` at `:82-85`, `CONTAINER` at `:91-97`, `READY_CHECKS` at `:169-172`, `wait_http_200(url, timeout)` at `:148` all match. The `CONTAINER` comment at `:89` naming ollama is outside every cited range → folded into §2.4 |
| Step 2 `ingest.py` edits | ok — `OLLAMA_URL` at `:150`, used at `:736` (`--embed-url` default) and `:737` (its help f-string); the docstring sites `:26,64,121-124` and the comment at `:153` read as quoted |
| Step 2 smoke command | ok — `ingest.py:787` prints `[ingest] probed embedding dimension {dimension} for model '{model}' at {url}`; `:875` writes `{"model": …, "embed_url": …}` into the stats sidecar, so both greps have real targets |
| Step 3 Launcher code block | ok — `:20` is the compose `up` list, `:28` the `WaitForOllamaAsync` call, `:72-103`/`:105` the two helpers; `WaitForHttp200Async` is a valid top-level-statement local function and mirrors the existing wait's structure exactly |
| Step 3 — `catch (OperationCanceledException) { return; }` swallowing an `HttpClient` 5 s timeout | dropped — it mirrors the existing `WaitForOllamaAsync` verbatim, and both TEI and TGI answer `/health` with 503 (not a hang) once bound, so no concrete failure path against this spec's outcome |
| Step 4 kind notes | ok — `setup.sh:98` / `setup.ps1:95` are the storageSize notes |
| Step 5 harness run | ok — `scifact-run-2026-08-26/beir/corpus.jsonl` (5,183 lines) and `scifact-bge-base-2026-09-04/runs/bge-base.chunks.trec` both exist; `md5sum` confirms the two keymaps are byte-identical, so no key-map switch is needed; `cut -f1-5` correctly drops the run-tag field that the `--config-label` change would otherwise perturb |
| Step 5 vs the "corpora is read-only except `enrichment-bench-<date>/`" global constraint | dropped — the step writes new files under `$SCI/runs/`, which the constraint's literal wording forbids. The constraint's operative half ("snapshots are restored, never deleted") is honoured, spec §8 requires the run files, and every prior harness run has written there. Ambiguity, not wrongness |
| Step 6 commit | ok |

### Task 7 — `EnrichmentService` chat port and the source-text cap

| Surface | Disposition |
|---|---|
| Step 1 test file (9 facts) | ok — extracted verbatim from the plan into a scratch copy of `Iverson.Embeddings.Tests`; compiles on net10.0 with xunit 2.9.3 / FluentAssertions 7.0.0 / NSubstitute 5.3.0 and the `protected override SendAsync` accessibility is correct across the assembly boundary |
| Step 1 "Named mutations" line | → §2.6 (the "no parse check (test 7)" claim). The other four claims hold: an `/api/generate` regression fails tests 1-2, a `response_format` fails test 4, reading `response` fails test 3, a strip-only extraction fails tests 5-6 |
| Step 2 consumer cap test | ok — `EnrichmentConsumerTests.cs:74-75` already stubs both enrichment methods, `:81-104` is `EnrichedArticle()` with the `"the author's stated conclusion"` hint, `:119` is `RowJson(body)`, `:133-135` is `BuildSut()`. `BuildSourceText` (`:292-303`) concatenates `VectorFields ∪ ChunkFields`, and `EnrichedArticle` has exactly one chunk field (`Body`), so the 20,000-`x` row yields a 20,000-char source text and the `Contain`/`NotContain` pair is exact. `Arg.Do<…>().Returns(…)` is standard NSubstitute (the repo's known quirk is `Arg.Do` inside `Received()`, which this is not) |
| Step 3 service rewrite | ok — compiles and passes. `new Uri(new Uri(_baseUrl), "/v1/chat/completions")` yields the absolute URI the test asserts even though the injected `HttpClient.BaseAddress` is a different host; `max_tokens`/`temperature`/`stream` survive the camelCase policy; the primary-constructor field initialiser `_baseUrl = options.Value.BaseUrl` is legal |
| Step 4 consumer cap | ok — `GroupId` is at `:43` and `var sourceText = BuildSourceText(schema, row);` at `:129` with `ComputeHash` at `:130`, so the cut lands before the loop-prevention hash exactly as spec S9 requires. `IntelligenceStoreConsumer`'s `ParentTextContextChars = 2000` claim is consistent with the spec |
| Step 5 counts and commands | ok — the scratch suite reports **Passed: 51, Failed: 0**, matching `# 51/51 (48 + 9 − 6)` |
| Step 6 commit | ok |
| Contract: `EnrichmentService` internals vs. test access | ok — `Iverson.Embeddings.csproj` declares no `InternalsVisibleTo`, so the test asserting the literal `256` and driving `ExtractJson` through `GenerateJsonAsync` is the only shape that compiles; `Iverson.Api.csproj:9-12` does grant it to `Iverson.Api.Tests`, so `EnrichmentConsumer.MaxSourceChars` is reachable from the consumer test |
| Contract: named-client registration | ok — `ServiceCollectionExtensions.cs:30-38` sets `BaseAddress` and `Timeout` on the enrichment client; the absolute request URI makes `BaseAddress` inert while `Timeout` (2 min) still bounds the call, which is what gate criterion 2 measures against |

### Task 8 — compose `tgi`, `enrich_bench.py`, measurement, verdict

| Surface | Disposition |
|---|---|
| Step 1 `tgi` compose block | ok — applied to the scratch compose file: `docker compose config --services \| grep -c '^tgi$'` → **1**; `start_period: 25m` and `shm_size: 1g` both render; `tgi_models` added under `volumes:`; port 8092 is free on the box today |
| Step 2 `enrich_bench.py` (180 lines) | ok — extracted verbatim, `python3 -m py_compile` clean, `--help` renders. `build_prompts` over the real corpus returns exactly **75** prompts (50/10/10/5) with a maximum prompt of 5,309 characters ≈ 1.3k tokens, comfortably under `--max-input-tokens 3072`, so no 422 is possible. `--backend` `split("=", 3)` handles the `http://host:port` value correctly. The prompt constants are byte-identical to `EnrichmentPrompts.cs:5-23` |
| Step 2 `extract_json` twin | ok — the Python rule matches the C# one branch for branch (fence wins outright, no fallback; else the string-aware balanced scan; else `False`). Exercised on the fenced, bare-with-embedded-brace, and no-object inputs |
| Step 2 verification commands | → §2.5 (the `python3 -c "…"` backtick case). The `py_compile` and `build_prompts` commands are fine |
| Step 3 measurement commands | ok — `mkdir -p "$OUT"` now precedes the `tee`; `/info` returns `model_id`/`max_input_tokens`/`max_total_tokens`; the TGI-first backend order is present with the P27 rationale in the trailing comment; the unbounded `until curl … sleep 30` loop is bounded in practice by the note that the first start is ~25 min |
| Step 4 gate arithmetic vs. `results.json` shape | ok — every field the five criteria name exists in the script's output (`by_kind.<kind>.mean_wall_s`, `p95_wall_s`, `failed`, `empty`, `extraction_parse_ok`, `min_mem_available_bytes`, all under each backend's `summary`), and the units line up (`> 500e6` against a byte count). The plan's `tgi.<field>` shorthand omits `.summary` uniformly, which is unambiguous |
| Step 5 gate document | ok — `docs/plans/2026-09-GATE-embedding-migration.md` exists as the shape to mirror; the section list covers spec §6.4's five criteria plus the veto, the branch consequence and the two not-performed items |
| Step 6 commit | ok — `docs/plans/` is gitignored at `.gitignore:49`, so the `git add -f` is required and present |

### Task 9 (Phase C) — `tgi` subchart, Ollama deleted, Terraform rename

| Surface | Disposition |
|---|---|
| Step 1 subchart | ok — reconstructed from the plan's description into the scratch chart. Renders, lints, kubeconforms on all five profiles; `condition: global.enrichmentEnabled` plus the in-template `and .Values.global.enrichmentEnabled .Values.enabled` guard both behave (laptop renders zero `tgi` objects) |
| Step 2 values edits + wording sweep | ok — the six sites named (`Chart.yaml:3`, `values-laptop.yaml:6`, `values-aws.yaml:88`, `values-azure.yaml:81`, `values-gcp.yaml:82`) all exist and read as quoted |
| Step 3 deployments | ok — `Enrichment__BaseUrl` → `http://iverson-tgi:8080` and `Enrichment__Enabled` render on both api and worker, `"false"` on laptop; `Enrichment__Enabled` maps to `Enrichment:Enabled`, the key `AddEnrichmentPipeline` (`EnrichmentConsumer.cs:376`) actually reads |
| Step 3 policies | → §2.3 |
| Step 4 Terraform rename | ok — `grep -rni ollama deploy/terraform` returns exactly 13 lines, and every one is covered: three pool-map entries, eight variable blocks, `operators/main.tf:175,177`, `outputs.tf:7`, and the `cluster-aws/main.tf:488` comment the plan names explicitly. Case-insensitive `-i` is what catches `:488` |
| Step 5 numeric assertions | mixed — `ls charts/*.tgz \| grep -c ollama` → **0** (update prunes it), `^  name: iverson-tgi$` → **3** on local and **0** on laptop, the `Enrichment__*` grep → **4**, `helm template \| grep -c 'ollama'` → **0** on all five profiles: all verified exactly. The kube-score assertion → §2.3 |
| Step 6 commit (`git add -A`) | ok — `-A` is what stages the `charts/ollama/` deletion |

### Task 10 (Phase C) — Ollama removed from compose, code, Launcher, scripts

| Surface | Disposition |
|---|---|
| Step 1 compose deletions + `Enrichment__*` | ok — `ollama` `:124-136`, `ollama-init` `:138-149`, `ollama_data` `:571`, worker `depends_on`; the `starrocks-init` comment at `:67` is named. `docker compose config \| grep -ci ollama` → 0 is reachable because `config` strips comments |
| Step 2 code/Launcher/script edits | ok for the named sites — `EnrichmentServiceOptions.cs:6-7,14`, `EnrichmentConsumer.cs:16`, `Launcher/Program.cs:20,28-29,72-103`, the kind notes |
| Step 2 repo-wide sweep grep, over-inclusion direction | ok — every file that legitimately keeps an Ollama mention is excluded: `EmbeddingPrefixes.cs`, `scripts/ingest.py`, `scripts/enrich_bench.py`, and all seven spec-D3 test files (matched by `Tests/`, `tests/`, `_test.go` or the explicit `Iverson.Api.Tests`). `README.md`/`tma.md` are moot — the `--include` list has no `*.md` |
| Step 2 repo-wide sweep grep, under-inclusion direction | → §2.4 |
| Step 3 build/run + `EnrichSmoke` scratch console | ok — the three `ProjectReference`s at `Iverson.Client.Conformance.Driver.csproj:12-14` transitively supply `Grpc.Net.ClientFactory` and `IdentityModel` (both non-private `PackageReference`s on `Iverson.Client.Core`), so the copied `Auth.cs` compiles without adding the driver's own two package references. `SchemaRegistrar(EntityRegistry, ObjectMappingServiceClient, ILogger)` and `RegisterAllAsync()` (both parameters optional) match the plan's call verbatim; `Coordinator<T>()`/`PostMappedAsync` exist at `Program.cs:124-126` / `EntityCoordinator.cs:102`, and `Program.cs:109-127,470-480` are the right models |
| Step 3 `EnrichSmoke` shape | ok — omitting a tenant property is correct, not an oversight: `SchemaRegistrationOrchestrator.cs:68-71` says `SchemaBuilder.BuildDescriptor` injects the server-owned tenant column and `RejectDeclaredTenantField` refuses a declared one, so the persisted row carries the tenant `EnrichmentConsumer.cs:119` reads. `[IversonExtracted(string hint)]` takes the positional hint the plan passes, and `[IversonSummary]` is parameterless. `[IversonEmbedding] Body` lands in `VectorFields`, which `BuildSourceText` reads |
| Step 3 acting-user token | ok — `Auth.BuildInvoker(…, actingToken, …)` needs a pre-minted token and the plan does not say how to mint one, but `Iverson.ClientConformance/TokenBroker.cs:113` (`GetActingTokenAsync`, with working compose defaults per the runbook) is in-repo and is the obvious source |
| Step 3 cleanup | ok — `PostgresProbe.TableName("EnrichSmoke")` snake-cases and pluralises, so the collection prefix `enrich_smoke` the cleanup greps for matches `enrich_smokes_<tenant>`; the schema-row `DELETE` is the same operation Task 4 Step 2 already performs on this stack. (The Postgres table itself is left behind; harmless) |
| Step 4 commit | ok |

### Task 9′ (Phase C′) — record the Ollama-for-enrichment end state

| Surface | Disposition |
|---|---|
| Preamble "what Tasks 2–8 landed is already the Phase C′ shape" | ok — checked each of the six claims against the Phase A scratch chart and compose file: the ollama subchart pulls only `global.generativeModel`, compose keeps `ollama` + a qwen-only `ollama-init`, `Enrichment__BaseUrl` still renders `iverson-ollama:11434`, `EnrichmentServiceOptions` defaults are untouched by Task 1, and the `tgi` compose service and Terraform `tei` pool are present |
| Step 1 rebuild + smoke | ok — the premise "nothing on this branch has rebuilt the image since Task 7" is right (Task 4 Step 3's build precedes Task 7); the ported client posting to `http://ollama:11434/v1/chat/completions` is spec §9 row 14 |
| Step 1 Ollama access-log grep | ok — verified against the live container: gin pads the method field to nine columns, so a POST line reads `\| POST     "/v1/chat/completions"` with exactly five spaces, which is exactly what the plan's pattern has (counted programmatically). Live `GET      "/api/tags"` and `HEAD     "/"` lines confirm the padding |
| Step 1 unbounded `until … grep -q '\[Enrichment\] Enriched'` | ok — `docker logs` is read from a container the same step has just recreated, so a stale hit from an earlier run cannot short-circuit it; `EnrichmentConsumer.cs:200` is the real log string |
| Steps 2-3 gate-doc append + commit | ok |

### Task 11 — kind smoke on the laptop profile

| Surface | Disposition |
|---|---|
| Step 1 cluster/install commands | ok — `setup.sh:97` echoes the `helm upgrade --install iverson . -f values-local.yaml -n iverson` form the plan adapts, `setup.sh` creates no cluster, `kind-config.yaml` is a one-node cluster with `disableDefaultCNI: true`, `build-and-load-image.sh [tag] [cluster-name]` takes the two positional arguments the plan passes, and `pids_limit = -1` is set at `~/.config/containers/containers.conf:7`. `helm dependency update` inside the repo is a no-op here because Tasks 2/9 already committed the lock and tarballs |
| Step 1 port collision | ok — `kind-config.yaml` maps hostPort 8080, which the compose api also publishes; the plan stops compose first |
| Step 2 assertions | ok — `kubectl logs sts/<name>` resolves through the controller; `EmbeddingService.cs:55` is the log line grepped for; the `/dev/tcp` probe is the compose healthcheck's own idiom (`docker-compose.yml:453-456`) and the aspnet:10.0 image ships `bash` only, as P25 records; TEI's `/info` really does carry `model_id` (it is what `VerifyServedModelAsync` reads) |
| Step 2 "not performed on this box" paragraph | ok — states the spec §8 Phase-C kind item and the spec §11 `Errno 30` item as unperformed and names what would perform them, which is what CIR-1's §2.5 option (b) asked for |
| Step 3 restore | ok |
| Consumer impact: the laptop install must actually converge under `--wait` | → §2.2 |

### Cross-task interface contracts

| Contract | Disposition |
|---|---|
| Task 2 → Task 9: the values shape (`tei:` block, `global.embeddingModels` single entry) | ok — Task 9's edits apply cleanly on top of Task 2's scratch output; `helm dependency update` transitions 13 → 13 charts (tei stays, ollama out, tgi in) and prunes `ollama-0.1.0.tgz` |
| Task 2 → Task 11 (fail branch): the chart Task 11 installs | ok — the Phase A scratch chart renders `iverson-tei-bge-base` and `iverson-ollama` side by side on `values-laptop.yaml`, which is the C′ end state |
| Task 4 → Task 5: 0 schema rows + TEI serving bge-base | ok — the row clear is Task 4 Step 2, and Task 5 Step 2 re-asserts it via the S11 model query |
| Task 4 → Task 6: the restored bge-base index | ok — Task 4 Step 2 restores the two collections and Task 6 Step 5 queries them; `benchmark-query` re-registers `BenchmarkDocument` itself (spec P3), so the row clear is self-healing |
| Task 4 → Task 8: Ollama still serving `qwen2.5:3b` | ok — Task 6 Step 5's `stack.py query` stops `iverson-ollama` but the same step's closing `docker compose up -d` restarts it before Task 8 |
| Task 7 → Task 8 | ok — the plan is explicit that the ported client is *not* on the measurement path (`enrich_bench.py` talks HTTP directly) |
| Task 7 → Tasks 10 / 9′: the ported client, exercised for real | ok — both branches now rebuild the image and drive one live generation through `EnrichSmoke`; the two branches differ only in the backend access-log grep |
| `enrich_bench.py` → gate (persistence boundary: `results.json`) | ok — every field the five gate criteria read is written by `run_backend`'s `summary` dict; units are bytes for `min_mem_available_bytes` and seconds for the wall fields, matching the criteria's `500e6` and `120` |
| Task 8 verdict → Task 9/10 vs Task 9′ | ok — "Never both branches" is stated, and Task 11 names which chart it consumes per branch |

### Rule-like content (both failure directions)

| Rule | Disposition |
|---|---|
| `ExtractJson` / `extract_json`: fence-first, else first balanced object, else throw | ok both directions — over-inclusion: the fenced branch does not fall back, so a fenced non-JSON block throws rather than silently scanning past it; under-inclusion: a `}` inside a string does not terminate the span, and trailing prose is dropped (both exercised in the passing scratch suite and in the Python twin) |
| `MaxSourceChars` cut placement (source text, not assembled prompt) | ok both directions — cutting the prompt would drop the trailing hint (the test's `EndWith` catches it); cutting nothing would leave 20,000 characters (the `NotContain` catches it) |
| Gate: five criteria, all-must-pass | ok both directions — every criterion maps to a field that is always populated on a completed run; `failed`/`empty` are counts so a partial failure cannot register as a pass |
| Task 10 Step 2 Ollama sweep: which files must be clean vs. exempt | over-inclusion ok (every legitimate mention is excluded); under-inclusion → §2.4 |
| Task 9 Step 5 kube-score: which objects must score clean | under-inclusion ok (`tei-`/`tgi` covers every new object); over-inclusion → §2.3 (it also catches two objects that render on a profile where no matching pod exists) |
| Task 1 Step 3 `iverson\.ollama` grep → 0 | ok both directions — the only two matches in the repo are the `Telemetry.cs` constants Step 1 renames |

---

## 1. Verified-plan-assumptions cross-check

| # | Result |
|---|---|
| P1 | **still holds** — `charts/` contains no `tei`/`tgi`; `Iverson.LoadTest/scripts/` has no `enrich_bench.py`; `docs/plans/` has no `2026-09-GATE-enrichment-backend.md`; `charts/ollama/templates/{pdb,service,statefulset}.yaml` all present and read as the mirror sources |
| P2 | **still holds** — `setup.sh:97` echoes the `helm upgrade --install iverson . -f values-local.yaml -n iverson` line; `build-and-load-image.sh:10-14` documents `[tag] [cluster-name]` with the `docker.io/library/` re-tag; `kind-config.yaml` is one control-plane node with `disableDefaultCNI: true`; `setup.sh` creates no cluster |
| P3 | **still holds** — `IEnrichmentService` is the three members; `grep 'new EnrichmentService('` matches only `EnrichmentServiceTests.cs:37`; `BuildSut()` at `:133-135` passes nine arguments and the plan adds none |
| P4 | **still holds** — `EnrichmentConsumerTests.cs:74-75` stubs both enrichment methods, `:77` feeds `FetchByKeyAsync`, `:81-104` is `EnrichedArticle()` with hint `"the author's stated conclusion"`, `:119` is `RowJson(body)` |
| P5 | **still holds** — `EnrichmentConsumer.cs:272-276` is `string.Format(EnrichmentPrompts.Extraction, sourceText) + $"\n\nExtract specifically: {target.Hint}"`; `EnrichmentPrompts.cs:12-13` starts `"Extract structured information"`; `:129`/`:130` are the assignment and `ComputeHash` |
| P6 | **still holds** — `GenerateInternalAsync(prompt, jsonFormat, ct)` at `:25-66`, camelCase `_jsonOpts` at `:14-15`, `Telemetry.EnrichmentHttpClientName` at `:34`, tags at `:28-30,55` |
| P7 | **still holds** — `WaitForOllamaAsync` at `:72-103`, `WaitForPortAsync` at `:105`, the `up` list at `:20`, the `Task.Delay(3000)` retry |
| P8 | **still holds** — `stack.py` `TIERS :82-85`, `CONTAINER :91-97`, `READY_CHECKS :169-172`, `wait_http_200(url, timeout)`; `ingest.py` `OLLAMA_URL :150`, used at `:736-737` |
| P9 | **still holds** — `charts/ollama/templates/statefulset.yaml:59-62` reads `global.embeddingModels`/`global.generativeModel`; `Chart.yaml:19` carries `condition: global.engagementEnabled` (the starrocks precedent); `charts/ollama/values.yaml` has exactly the seven keys. (The row is about *values keys*, which is why it does not cover what the init script's other lines do — see §2.1) |
| P10 | **still holds** — helm 3.16.4, kubeconform 0.8.0, kube-score 1.19.0, terraform, kind and kubectl all present; `deploy-validate.yml:28-33,55-62,80-96,110-126` quote the invocations exactly as the plan reproduces them |
| P11 | **still holds as revised** — re-ran on a scratch copy carrying the plan's *own* edits: `helm dependency update .` succeeds and produces `charts/tei-0.1.0.tgz` plus a regenerated `Chart.lock`; `helm lint` passes and kubeconform reports Invalid: 0 on all five profiles; kube-score still exits 1 on pre-existing objects while every `tei-` object scores ✅. The revised wording ("this plan runs `update`, commits the regenerated lock, and CI's `build` stays in sync") is correct |
| P12 | **still holds** — the three csproj paths exist; `EnrichmentConsumerTests` is substitute-only (all nine collaborators are `Substitute.For<…>`, the registry is built over a substituted `IRecordStoreQueryExecutor`) |
| P13 | **still holds** — `docs/runbooks/client-conformance-matrix.md:23-27` carries the four exports verbatim, `:41-44` documents the full and partial forms |
| P14 | **still holds** — live: 36 `_iverson_schema` rows; Qdrant holds `benchmark_documents_tenant_bypass`, `benchmark_documents_chunks_tenant_bypass`, `vector_docs_tenant_bypass`, `vector_docs_chunks_tenant_bypass`, `iverson-probe`; 11434 listening, 8091/8092 free |
| P15 | **still holds** — two `.snapshot` files, both `…-6802952876034638-…`; the `RESTORE.md` loop is byte-for-byte what the plan reproduces |
| P16 | **still holds** — confirmed indirectly and more strongly than a scratch console: the Task 7 test asserting `max_tokens`/`temperature`/`stream` on the serialized body passes in the scratch suite |
| P17 | **still holds** — the C# `ExtractJson`/`BalancedObject` code passes tests 5, 6 and 7 in the scratch suite; the Python twin returns `True` on the fenced and the embedded-brace inputs and `False` on the no-object input |
| P18 | **still holds** — `docker compose config` on a scratch file carrying the plan's `tgi` block renders `start_period: 25m0s` and `shm_size` normally; `/proc/meminfo`'s `MemAvailable` is in kB |
| P19 | **still holds** — `values-laptop.yaml:9-11` reads exactly the quoted 1.45-CPU inventory; `nproc` = 4; the plan's replacement arithmetic (1.7 CPU, ~3.8 of 4.0) is correct |
| P20 | **still holds** — `grep HttpClientName` finds only `ServiceCollectionExtensions.cs:13,31`, `EmbeddingService.cs:69,97`, `EnrichmentService.cs:34`; `IdpAdminClient.HttpClientName` is a separate constant |
| P21 | **still holds** — `pids_limit = -1` at `~/.config/containers/containers.conf:7` |
| P22 | **still holds** — every stated dependency is real; additionally re-confirmed that Task 6 Step 5 restarts what `stack.py query` stops before Task 8 needs Ollama |
| P24 | **still holds** — `Iverson.Embeddings.csproj` declares no `InternalsVisibleTo` (and the scratch test suite compiles only because the plan's tests avoid internals); `Iverson.Api.csproj:9-12` grants it to `Iverson.Api.Tests` |
| P25 | **still holds** — `Iverson.Api/Dockerfile` runtime stage is `mcr.microsoft.com/dotnet/aspnet:10.0`; `docker-compose.yml:453-456` records the bash-only fact and uses the `/dev/tcp` idiom the plan now copies |
| P26 | **still holds** — `grep -rn '\[IversonSummary\]\|\[IversonKeywords\]\|IversonExtracted('` over the five conformance drivers and `Iverson.LoadTest` returns only `VectorDoc.cs:11`'s doc comment saying the type deliberately has none |
| P27 | **still holds** — the plan's Task 8 Step 3 now orders `--backend tgi=…` before `--backend ollama=…` with the residency rationale inline |
| P28 | **still holds** — the laptop render's container memory requests come to ~5.4 Gi with the TEI pod, and Task 11 Step 2 instructs the implementer to record any Pending pod with its reason |
| P23 | **still holds** — every `.Values.tei.*`/`.Values.tgi.*` key the templates read resolves on all five profiles (verified by rendering both the Phase A and the Phase C scratch charts); every `$.Release.Name`/`$.Values` inside a `range` uses the `$` root; `c7i.xlarge`/`c2-standard-4`/`Standard_F4s_v2` are one size below the ollama defaults in each cloud |

### Span check — plan dependencies with no covering assumption

- **What the ollama init container's script lines other than the pull loop do.** P9 covers only which *values keys* the ollama templates read. Verified in-round: `charts/ollama/templates/statefulset.yaml:53-62` is `sh -c` with `ollama serve &` and `sleep 5` preceding the pulls, and `ollama pull` is a client command that fails without that server. → §2.1.
- **The exact line inventory at `values-laptop.yaml`'s replacement range.** No assumption covers it; P19 reads only `:9-11`. Verified in-round: `:14` is `tracingEnabled: false`, and the comment+list the step replaces starts at `:15`. → §2.2.
- **kube-score's verdict on a NetworkPolicy whose `podSelector` matches no rendered pod.** P11 covers the exit-1 baseline and the "new objects only" scoping; K6/spec §9 row 18 covers only `statefulset-has-servicename`. Verified in-round by running kube-score over the Phase C laptop render. → §2.3.
- **Line-level completeness of the Ollama mentions inside the files Task 1 and Task 6 edit by line number.** Spec D3 establishes *file*-level completeness only. Verified in-round with a per-file `grep -n -i ollama`. → §2.4.
- **Whether the plan's own verification shell commands survive bash quoting.** No assumption covers it. Verified in-round by executing the command as written. → §2.5.
- **Whether the plan's named mutations actually falsify the tests they name.** P17 verifies `ExtractJson`'s behaviour, not the tests' discriminating power. Verified in-round by applying the named mutation to the scratch service and re-running the suite. → §2.6.
- **`docs/plans/` and `docs/criticalreviews/` gitignore status** (Task 8 Step 6, Task 9′ Step 3 use `git add -f`). No assumption covers it. Verified in-round: `.gitignore:49` and `:47`. No finding.
- **Whether anything asserts the `SchemaRegistrationOrchestrator` guard message's Ollama tail** (Task 1 Step 2 rewrites it; the live matrix in Task 5 runs against the rewritten server). No assumption covers it. Verified in-round: `ModelRejectedScenario.cs:263-297` asserts four other substrings and never that sentence. No finding.

Every uncovered dependency was verifiable in-round; none is deferred to §3.

---

## 2. Literal-wrongness findings

### 2.1 Task 2 Step 4's ollama init-script instruction, applied literally, removes `ollama serve &` and the pod never starts

**Description.** Step 4 ends: "In `charts/ollama/templates/statefulset.yaml:56-62` the init script becomes only `ollama pull {{ .Values.global.generativeModel }}`". Lines 56-62 are the whole `- |` block scalar — `ollama serve &`, `sleep 5`, the `range` over `global.embeddingModels`, its pull, the `end`, and the generative pull. "Becomes only \<one pull line\>" over that range deletes the background server and the settle, and `ollama pull` is a client command that needs a listening server: without it the init container exits non-zero, the `iverson-ollama` StatefulSet never becomes Ready, and Task 11 Step 1's `helm upgrade --install … --wait --timeout 30m` hangs for thirty minutes and then fails. This lands on the *expected* branch: Phase C′ keeps the ollama subchart, and Task 11 installs `values-laptop.yaml`, which still deploys it.

The parenthetical the step gives — "(an `ollama pull` of a Hub id fails the init container; embeddings are no longer Ollama's job)" — explains only why the `embeddingModels` loop goes; it does not scope the edit away from lines 56-58.

**Evidence.** `Iverson.Server/deploy/helm/iverson/charts/ollama/templates/statefulset.yaml:53-62`:

```
          command:
            - sh
            - -c
            - |
              ollama serve &
              sleep 5
              {{- range .Values.global.embeddingModels }}
              ollama pull {{ .name }}
              {{- end }}
              ollama pull {{ .Values.global.generativeModel }}
```

Line 56 is `- |`, 57 `ollama serve &`, 58 `sleep 5`. Removing only the three `range` lines (which is what the intent requires) renders correctly — on the scratch chart the Phase A `values-local` render keeps `ollama serve &` at render line 2343 and reports `grep -c 'ollama pull'` → 1, which is the count Step 5 asserts. That assertion passes under *either* edit, so nothing downstream catches the wrong one.

**Proposed fix.** Change the sentence to name the deletion precisely: "delete the `{{- range .Values.global.embeddingModels }} / ollama pull {{ .name }} / {{- end }}` block (`:59-61`), leaving `ollama serve &`, `sleep 5` and the `ollama pull {{ .Values.global.generativeModel }}` line intact", and correct the Files-list range from `:56-62` to `:59-61`. Optionally strengthen Step 5's check to `grep -c 'ollama serve'` → 1 alongside the existing `ollama pull` → 1.

### 2.2 Task 2's `values-laptop.yaml:14-26` replacement range swallows `tracingEnabled: false`, which makes Task 11's `--wait` install fail

**Description.** Task 2's Files list and Step 2 both scope the laptop `embeddingModels` replacement to `values-laptop.yaml:14-26`. Line 14 is not part of the comment+list; it is `tracingEnabled: false      # no jaeger`. The comment+list actually runs `:15-26`. Replacing `:14-26` with the plan's five-line block silently drops the laptop profile's tracing override, so `global.tracingEnabled` falls back to `values.yaml`'s `true`: the `jaeger` subchart's `condition: global.tracingEnabled` fires and the jaeger Deployment renders — at `values.yaml:167-169`'s **production** sizing, `requests: {cpu: "2", memory: "4Gi"}`, because `values-laptop.yaml` overrides jaeger nowhere (it disables it instead).

The laptop profile is budgeted at ~3.8 of 4 CPU with roughly 200m of headroom (the plan's own P19 and its rewritten capacity comment). A 2-CPU / 4-Gi pod cannot schedule there, so Task 11 Step 1's `helm upgrade --install iverson . -f values-laptop.yaml --wait --timeout 30m` blocks on a Pending pod for the full thirty minutes and then reports failure. Nothing between Task 2 and Task 11 catches it: Step 5's assertions count `tei-`/`ollama pull`/`clusterIP` objects, never jaeger, and `helm lint`/`kubeconform`/`kube-score` all pass on a rendered jaeger.

**Evidence.** `values-laptop.yaml` lines 12-26:

```
12  global:
13    engagementEnabled: false   # no StarRocks: vendor sizes FE at 8 CPU/16GB
14    tracingEnabled: false      # no jaeger
15    # One model only: the PVC is 8Gi and ollama's CPU request is 250m, so a second buys nothing
…
25    embeddingModels:
26      - name: nomic-embed-text
```

`Chart.yaml:41-44` gates the jaeger dependency on `global.tracingEnabled`; `values.yaml:165-169` is jaeger's only sizing. `values-local.yaml`'s analogous citation (`:10-22`, where `:10` is `global:`) is harmless by comparison — replacing it orphans an indented block and `helm lint` rejects it at once — but the laptop case renders cleanly and fails only at install.

**Proposed fix.** Change both the Files-list range and Step 2's prose to `values-laptop.yaml:15-26`, and add `helm template iverson . -f values-laptop.yaml | grep -c 'iverson-jaeger'` → `0` to Task 2 Step 5 so the regression cannot reach Task 11 silently.

### 2.3 Task 9 Step 3's `tgi-ingress` / `tgi-egress` policies render on the laptop profile with no matching pod, and kube-score marks both CRITICAL — Step 5's `grep -c '💥'` → 0 assertion returns 2

**Description.** Step 3 adds `tgi-ingress` and `tgi-egress` to `templates/networkpolicies.yaml`, which is the *parent* chart's template and therefore unconditional. Step 2 sets `global.enrichmentEnabled: false` on `values-laptop.yaml`, so the `tgi` subchart (`condition: global.enrichmentEnabled`) renders zero pods there while the two policies still render. kube-score 1.19.0's `networkpolicy-targets-pod` check marks a NetworkPolicy whose selector matches no pod CRITICAL, so Step 5's per-profile assertion

```
helm template iverson . -f $v.yaml | kube-score score --ignore-test … - | grep -E 'tei-|tgi' | grep -c '💥'   # 0
```

returns **2** on `values-laptop`. The existing `ollama-ingress`/`ollama-egress` pair never hit this because ollama renders on every profile; `tgi` is the first component the chart gates off on a profile.

**Evidence.** Ran the Phase C scratch chart (`values-laptop.yaml` with `global.enrichmentEnabled: false`, no `tgi:` block, `charts/ollama/` deleted) through the exact command:

```
networking.k8s.io/v1/NetworkPolicy iverson-tei-egress     ✅
networking.k8s.io/v1/NetworkPolicy iverson-tei-ingress    ✅
networking.k8s.io/v1/NetworkPolicy iverson-tgi-egress     💥
    [CRITICAL] NetworkPolicy targets Pod
        · The NetworkPolicys selector doesn't match any pods
networking.k8s.io/v1/NetworkPolicy iverson-tgi-ingress    💥
    [CRITICAL] NetworkPolicy targets Pod
        · The NetworkPolicys selector doesn't match any pods
```

`grep -E 'tei-|tgi' | grep -c '💥'` → **2** on `values-laptop`, **0** on local/aws/azure/gcp. The same chart's other Step 5 assertions all pass exactly as written (`^  name: iverson-tgi$` → 3 local / 0 laptop, the `Enrichment__*` grep → 4, `helm template | grep -c 'ollama'` → 0 on all five, `ls charts/*.tgz | grep -c ollama` → 0). Only the api-egress/worker-egress `to:` targets are unaffected — kube-score checks a policy's own `podSelector`, not its egress destinations.

**Proposed fix.** Wrap the two new policies in `{{- if .Values.global.enrichmentEnabled }} … {{- end }}` in `templates/networkpolicies.yaml`, mirroring the flag that already gates the subchart, and say so in Step 3. (Guarding the api/worker egress `to:` entry is optional — it costs nothing and kube-score does not check it.)

### 2.4 Task 10 Step 2's "must print nothing" sweep still cannot pass: three sites belong to no task's edit list

**Description.** Round 1 closed six classes of residual match and added the two `scripts/*.py` exemptions; the assertion is nonetheless still unsatisfiable, because three Ollama mentions sit outside every line range the plan's tasks name. Task 1 Step 2 scopes the client-annotation rewording to `annotations.py:89-90,170,175,180,230`, "the three Java annotation Javadocs at `:9`" and `annotations.ts:71`; Task 6 Step 1 scopes `stack.py` to `:10-13,27,43,83-84,91-97,169-172`. The following are not in any of those and match no exclusion:

1. `Iverson.Clients/Java/client/src/main/java/io/iverson/client/annotations/IversonExtracted.java:17` — `/** The extraction hint guiding the Ollama prompt. Required; must not be blank. */`. The plan names only `:9` for this file, and none of the three transformation rules it gives ("Ollama enrichment targets" → …, "Ollama-driven"/"Ollama-generated" → …, "the Ollama model this type's …" → …) matches "the Ollama prompt".
2. `Iverson.Clients/TypeScript/src/annotations.ts:181,197,219` — three further `Ollama-driven` doc comments on `IversonSummary` / `IversonKeywords` / `IversonExtracted`. The plan names only `:71`.
3. `Iverson.Server/Iverson.LoadTest/scripts/stack.py:89` — `# ollama and iverson-api.`, the tail of the `CONTAINER` comment block at `:87-90` ("…differ from service names for every entry but qdrant, ollama and iverson-api"), immediately above the `:91-97` range the plan does cite. It is also factually stale once `ollama` leaves `CONTAINER`.

**Evidence.** `grep -n -i ollama` per file, on the current tree:

```
IversonExtracted.java:9  * Marks a field as the target for an Ollama-driven extraction during ingest
IversonExtracted.java:17    /** The extraction hint guiding the Ollama prompt. Required; must not be blank. */
annotations.ts:71   * Declares the Ollama model this type's embedding/chunk properties are generated with. Class-
annotations.ts:181  /** Marks a property as the target for an Ollama-driven summary during ingest enrichment. */
annotations.ts:197  /** Marks a property as the target for Ollama-driven keyword extraction during ingest enrichment. */
annotations.ts:219  * Marks a property as the target for an Ollama-driven extraction during
stack.py:89         # ollama and iverson-api.
```

Running the plan's exact grep over the current tree returns 41 files; subtracting every file each task fully cleans leaves these three. `IversonSummary.java` and `IversonKeywords.java` have their single mention at `:9` only, and `annotations.py`'s five sites are all cited — so the gap is exactly these three files.

This is a different set of sites from the six classes round 1 named (`docker-compose.yml:67`, `Chart.yaml:3`, `values-laptop.yaml:6`, the three cloud values comments, `cluster-aws/main.tf:488`, and the two `scripts/*.py` exemptions), all of which the current plan text does now handle.

**Proposed fix.** Extend Task 1 Step 2's site list to `IversonExtracted.java:9,17` ("the Ollama prompt" → "the extraction prompt") and `annotations.ts:71,181,197,219`, and add `stack.py:89` to Task 6 Step 1's edits ("…for every entry but qdrant and iverson-api"). A cheap standing guard: make each of those tasks end with the same `grep -li ollama` restricted to the files it owns.

### 2.5 Task 8 Step 2's `extract_json` verification command is mangled by bash and prints `False False`, not the asserted `True False`

**Description.** Step 2's third verification command is

```
python3 -c "import enrich_bench as b; print(b.extract_json('x ```json\n{\"a\":1}\n``` y'), b.extract_json('no'))"   → `True False`
```

The `-c` argument is double-quoted, so bash treats the six backticks as two command substitutions and one command run: the middle pair executes `json\n{"a":1}\n` as a command. What Python receives is `'x  y'` — no fence, no object — so `extract_json` returns `False`, and the step prints `False False` plus `bash: jsonn{a:1}n: command not found`. The asserted output is unreachable, and the failure reads as "the extraction rule the service and the script share is broken", pointing an implementer at correct code. The pipeline exit status is 0, so nothing flags it either.

**Evidence.** Executed the command verbatim against the plan's own `extract_json` (extracted from lines 1055-1234 and `py_compile`-clean):

```
$ python3 -c "import eb_test as b; print(b.extract_json('x ```json\n{\"a\":1}\n``` y'), b.extract_json('no'))"
/bin/bash: line 34: jsonn{a:1}n: command not found
False False
```

With the `-c` argument single-quoted instead, the same function prints `True False`, so the script is correct and only the command is wrong:

```
$ python3 -c 'import enrich_bench as b; print(b.extract_json("x ```json\n{\"a\":1}\n``` y"), b.extract_json("no"))'
True False
```

**Proposed fix.** Swap the quoting: `python3 -c 'import enrich_bench as b; print(b.extract_json("x ```json\n{\"a\":1}\n``` y"), b.extract_json("no"))'`, or replace it with a heredoc (`python3 - <<'PY' … PY`), which is quoting-proof. The other two commands on that line (`py_compile`, `build_prompts` → 75) run correctly as written and were confirmed against the real corpus.

### 2.6 Task 7's named mutation for test 7 does not fail the suite, and the spec's parse-validation behaviour ends up untested

**Description.** Step 1 closes with "Named mutations: … no parse check (test 7)." Test 7 feeds the reply `"I could not find any structured information."`, which contains neither a fence nor a `{`, so `BalancedObject` returns `null` and `ExtractJson` throws on the null-candidate path — with or without the `JsonDocument.Parse` guard. Removing the guard therefore leaves the suite green, so the plan's mutation claim is unfalsifiable, and spec §3.5's "validates it with `JsonDocument.Parse`" / spec §4's "for which neither a fenced block nor a `{ … }` span **parses**: `InvalidOperationException`" has no test at all. Under the mutation, a reply carrying a malformed object (`{"a": 1,}`) returns that text and `EnrichmentConsumer` (`:284`) stores it verbatim in the target column — the silent-bad-data outcome spec §4 exists to prevent.

**Evidence.** Built a scratch copy of `Iverson.Embeddings` + `Iverson.Embeddings.Tests` carrying the plan's Step 3 service and Step 1 test file:

```
Passed!  - Failed: 0, Passed: 51, Skipped: 0, Total: 51
```

Then applied exactly the named mutation — replacing

```csharp
        if (candidate is not null)
        {
            try { using var _ = JsonDocument.Parse(candidate); return candidate; }
            catch (JsonException) { }
        }
```

with `if (candidate is not null) { return candidate; }` — and re-ran:

```
Passed!  - Failed: 0, Passed: 51, Skipped: 0, Total: 51
```

The other four named mutations do discriminate: tests 1-2 pin the path and body, test 3 pins `choices[0].message.content`, test 4 pins the absence of `response_format`, tests 5-6 fail under a strip-only extraction.

**Proposed fix.** Add one fact whose reply carries an unparseable object so the guard is on the only path to the assertion — e.g. `ChatResponse("Result: {\"a\": 1,} — that's all")`, asserting `ThrowAsync<InvalidOperationException>().WithMessage("*Result: {*")` — and re-word the mutation line to name that test rather than test 7. (Test 7 remains valuable as the no-object case; it just does not cover the parse check.)

---

## 3. Forced decisions

No forced decisions found.

---

## 4. Previously addressed

Items from round 1 that the plan's current state (`3492e80`) resolves:

- **§2.1 — `helm dependency build` refuses once `Chart.yaml`'s dependency list changes.** All three call sites now run `helm dependency update .`; Task 2 Step 5 and Task 9 Step 5 carry the inline rationale, Task 2 Step 6 and Task 9 Step 5 note that the regenerated `Chart.lock` is committed so CI's `build` stays in sync, and P11's text was rewritten to record both behaviours. Re-verified on a scratch chart carrying the plan's own edits: `update` succeeds through both Phase A and Phase C, and prunes `charts/ollama-0.1.0.tgz` without a manual `rm`.
- **§2.2 — the Task 10 sweep's six residual classes.** `Chart.yaml:3`, `values-laptop.yaml:6` and the three cloud values comments are now Task 9 Step 2 edits; `docker-compose.yml:67`'s `starrocks-init` comment is now a Task 10 Step 1 edit; `cluster-aws/main.tf:488` is a Task 9 Step 4 edit and that step's own grep is now `grep -rni`; `scripts/ingest.py` and `scripts/enrich_bench.py` are excluded with the reason stated inline. (Three further sites remain — different files, see §2.4.)
- **§2.3 — the vacuous end-to-end enrichment check.** Task 10 Step 3 now builds an `EnrichSmoke` throwaway type in a scratch console project, registers it, writes a row, waits for `[Enrichment] Enriched`, and cleans up the schema row and collections; Task 9′ Step 1 rebuilds the API image and runs the same smoke against Ollama's `/v1/chat/completions` access log. P26 records the underlying fact.
- **§2.4 — the in-cluster `/info` probe.** Task 11 Step 2 now uses the `bash` `/dev/tcp` idiom with an inline note that the aspnet:10.0 image ships bash only and that a `kubectl run` pod would not prove the same thing; P25 records the image fact.
- **§2.5 — the silent `values-laptop.yaml` substitution.** Task 11 Step 2 now states explicitly that spec §8's Phase-C kind items and spec §11's `Errno 30` check are not performed on this box, why (`enrichmentEnabled: false` renders no tgi pod; `values-local.yaml`'s tgi sizing cannot schedule here), and what would perform them; Task 8 Step 5 requires the gate document to carry the same note on a pass.
- **§2.6 — `tee` into a directory that does not exist yet.** Task 8 Step 3 now has `mkdir -p "$OUT"` on the `OUT=` line with the reason inline.
- **§2.7 — gate criterion 5 measured under Ollama's residency.** Task 8 Step 3 now runs `--backend tgi=…` first with the P27 rationale in the trailing comment, and Task 8 Step 5 requires the gate document to record the backend order.
- **§1 P11's partial failure** and the four span-check rows are now assumption rows P25-P28 plus a rewritten P11; all five reconfirmed in §1 above.

---

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §1 reconfirms all 27 verified plan-level assumptions and the span check resolved every uncovered dependency in-round; §3 is empty. Six §2 items need addressing before SDD: two are wrong line ranges whose literal application breaks the Task 11 kind install on the expected (Phase C′) branch (§2.1, §2.2), one is a chart-check assertion that returns 2 instead of 0 on the laptop profile (§2.3), one is an assertion that still cannot print nothing (§2.4), one is a verification command bash mangles (§2.5), and one is a false mutation claim that leaves a spec-required guard untested (§2.6). All six are local edits to task text; none touches the plan's structure, its ordering, or the gate.
