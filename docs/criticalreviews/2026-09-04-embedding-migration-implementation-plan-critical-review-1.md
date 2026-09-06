# Critical Implementation Review: 2026-09-04-embedding-migration-implementation-plan (Round 1)

**Plan:** docs/plans/2026-09-04-embedding-migration-implementation-plan.md
**Verified plan-level assumptions section:** present (P1–P28, plus an `Inherited from spec` block E1–E7 / S1–S12 / C1–C4 / B1–B13 / H1–H2)

⚠️ 1 commit since plan-write time (SHA `5d1ff5d`); cited file:line references re-checked under §1. (`git log --oneline 5d1ff5d..HEAD` → `fd75023 add the embedding migration implementation plan` — the plan's own commit, so no third-party drift.)

**Method.** Repo `main` @ `fd75023`, clean tree. Tasks 1–4 were applied **verbatim** to a throwaway copy of
`Iverson.Server/Iverson.Embeddings`, `Iverson.Embeddings.Tests` and `Iverson.LoadTest/scripts`
(`rsync -a --exclude=bin --exclude=obj` into the session scratchpad); every `dotnet build` / `dotnet test`
and every Python check below ran there. Nothing under `/home/ben/repositories/Iverson` or
`~/repositories/iverson-benchmark-corpora/` was written. No container was created, started, stopped or
recreated: live probes are read-only `curl`s, `docker inspect`, `docker logs`, `docker compose config` and
`docker compose --dry-run`, plus `SELECT` / `\d` through `docker exec … psql`. **TEI was never started and
no ingest was run**, so §10-measured TEI behaviour, the arm wall-clocks and the `--auto-truncate` smoke are
taken from the spec's ground truth, not re-executed. Task 3's `IVERSON_REGENERATE_INGEST_CONTRACT=1` run was
**not** executed against the repo; the regenerated diff was derived from `IngestContractTests.cs:116-131,170-174`
and reproduced by hand in the scratch copy, then diffed.

---

## 0. Coverage enumeration

### Tasks × surfaces

| # | Task / surface | Disposition |
|---|---|---|
| 0.1 | T1 step prose (fake rewrite scope, "every existing assertion keeps its literal value") | ok — applied verbatim; the three `CallCount` literals at `:210`, `:222`, `:256` stay at 1/1/2 and pass, because both counting handlers now branch on `/info` before incrementing |
| 0.2 | T1 code block — `FakeHttpMessageHandler` / `CopyAsync` / `InfoResponse` / `CountingHttpMessageHandler` / `FlakyThenSuccessHandler` | ok — compiles and behaves as described; `LastRequest` on a disposed `HttpRequestMessage` is still readable (`Method`, `RequestUri`, `Headers` are plain properties), and `EmbedDocumentAsync_ThrowsHttpRequestException_OnNonSuccessStatusCode` survives `CopyAsync` over a 500 with default empty content |
| 0.3 | T1 code block — `SuccessResponse` raw string literal | → §2.1 |
| 0.4 | T1 code block — the five new tests | ok — all five compile and pass against the step-4 service; `EnsureInitializedAsync_InfoReportsAnotherModel_…_OnEveryCall` asserts `InfoCalls == 2` and does get 2 |
| 0.5 | T1 step 3 predicted failures | ok — with the tests in and the service unchanged, the parse tests, the URI test and the guard tests all fail as stated (`RequestUri` is absolutised by `HttpClient` before the handler sees it, so `AbsolutePath` is safe even with the old relative `/api/embed`) |
| 0.6 | T1 code block — `EmbeddingService.cs` (`using System.Net;`, `_baseUrl`, `EnsureInitializedAsync`, `VerifyServedModelAsync`, `EndpointUri`, `EmbedAsync` request line + parse) | ok — applied verbatim, compiles, 39/41 green at the end of Task 1 (only the two Task-2 resolver tests red, exactly as step 5 predicts) |
| 0.7 | T1 step 5 / step 6 commands | ok — `dotnet build Iverson.slnx` is unaffected at this point (`EmbeddingServiceResolverTests.cs` still holds the old Ollama-shaped `SuccessResponse`, which compiles); `git add` paths exist |
| 0.8 | T2 step prose (`Models` binding, "the closing brace of `EmbeddingServiceOptions` moves above `ModelEndpoint`") | ok — followed literally; `EmbeddingServiceOptions.cs` compiles with `List<>`/`FirstOrDefault`/`StringComparison` under the csproj's `ImplicitUsings` |
| 0.9 | T2 code block — `RecordingHttpMessageHandler` + resolver `SuccessResponse` | → §2.1 (same literal) |
| 0.10 | T2 code block — the two resolver tests + the default-service test | ok — all three compile once `Models`/`ModelEndpoint` exist and all three pass |
| 0.11 | T2 code block — `EmbeddingServiceOptions.Models` / `BaseUrlFor` / `ModelEndpoint` | ok — matches P6's binding shape; the resolver's per-model options object carries an empty `Models`, so the inner `BaseUrlFor` is a no-op over the already-resolved URL |
| 0.12 | T2 code block — `_baseUrl = options.Value.BaseUrlFor(options.Value.ModelId)` and `EmbeddingServiceResolver.cs` | ok — quoted line is unique in the file (it sits at `:27`, not the `:21` the header names; `:21` is the `new EmbeddingService(` site S3 cites, so the edit is unambiguous) |
| 0.13 | T3 step 1/2 — theory rows and table rows | ok — with the rows in the theory and not in the table, `--filter For_ResolvesByFamily` is 2 failed / 4 passed; with both, the whole suite is 46/46 |
| 0.14 | T3 step 3 — regeneration + `grep -c 'BAAI/bge'` = 4 | ok — reproduced the emitter's output by hand in the scratch copy from `IngestContractTests.cs:116-131`; `diff -u … \| grep '^+' \| grep -c 'BAAI/bge'` → `4`, and the `chunkWindow` block is byte-identical (2048/1792/50) |
| 0.15 | T3 step 4 — Python-side check | ok — ran against the scratch contract: `verify_contract('BAAI/bge-base-en-v1.5:probe-tag', require_known_family=True)` and the small variant both exit 0, `document_prefix_for` → `''` |
| 0.16 | T3 rule — the two new families in `documentPrefixes` also enter `main()`'s strict loop (`ingest.py:732-733`) | ok — `family("BAAI/bge-base-en-v1.5:probe-tag")` = `BAAI/bge-base-en-v1.5`, a real `golden.documentComposition` key, so `require_known_family=True` passes for all four families |
| 0.17 | T4 code blocks — `embed`, `ingest_document`, `update_stats_sidecar`, `main()` argparse/probe/call sites | ok — applied verbatim; `python3 -m py_compile` clean; all call sites are positional-compatible with the new signatures |
| 0.18 | T4 step 5 commands | ok — ran both: `2 5` exactly, and the guard prints `ingest.py: error: --chunk-step must be in 1..--chunk-max-chars (got step 1792, max-chars 512)` before any network call |
| 0.19 | T4 step 6 smoke expectations (`points_count > 5`) | ok — the first five SciFact documents are 2041/1416/841/1304/946 chars, so 512/448 yields ≥ 2 chunks each and 2048/1792 yields exactly 5 in total; the check does discriminate |
| 0.20 | T5 step 1/2 — `tei-embed` service + api/worker env | ok — key set is identical to `reranker` (`docker-compose.yml:156-164`); insertion anchors `Embeddings__BaseUrl=http://ollama:11434` exist at `:386` and `:481`, `volumes:` block at `:539-548` |
| 0.21 | T5 step 3 — render checks | → §2.2 (`grep -A 12 … # 4`) and §2.3 (`config --volumes # 1`); the other four lines are ok — `config --services` prunes profiled services (0/1 confirmed against `reranker`), and `docker compose config` today already renders `Embeddings__ModelId: nomic-embed-text` twice (lines 173, 253) |
| 0.22 | T5 step 4 — TEI smoke, incl. the trailing `docker compose --profile tei stop tei-embed` from `Iverson.LoadTest/scripts` | ok — Compose v2 walks up for the project file: `docker compose config --services` from that directory exits 0 and lists the project's services. Not executed (no TEI start) |
| 0.23 | T6 step 1 — environment gate | ok — ran the dry-run read-only: `grep -o "Container iverson-[a-z-]* *Recreate" \| sort -u` → exactly `iverson-authentik-server` and `iverson-postgres`; `docker inspect -f '{{.Name}} {{.State.Health.Status}}'` returns `healthy` for all six (none lacks a healthcheck, so the template cannot error); live counts are 19,967 / 5,183 at size 768 and `/build` composite `31583db5aea49136` |
| 0.24 | T6 step 2 — build + recreate + `docker inspect … grep -o '"Embeddings__[A-Za-z_0-9]*=[^"]*"'` | ok — the character class covers `Models__0__Name`/`Models__0__BaseUrl`; `Dockerfile:4-14` restores `Iverson.Embeddings.csproj` then `COPY . .`, and `iverson-worker` shares `image: iverson-api`. Not executed (no build) |
| 0.25 | T6 step 3 — same-build control + `cut -d' ' -f1-5` diff | ok — run files are space-delimited `qid Q0 docid rank score tag` (`head -2 … \| cat -A`), so `-f1-5` drops exactly the config label; `$SCI/keymap.json` and `$SCI/keymap.json.stats.json` exist; `benchmark-query --help` really does print `Schemas registered.` (`Iverson.LoadTest/Program.cs:18,88-89,151-164`) and then fails safely on the missing `--corpus-path` (`BenchmarkQueryScenario.cs:60-64`) |
| 0.26 | T6 step 4 — ingest + "chunk count must be exactly 19,967" | ok — recomputed from the real corpus with today's chunker: `5183` docs → `19967` non-empty chunks at 512/448 (and `6587` at 2048/1792). The hard stop cannot false-alarm on a chunker drift |
| 0.27 | T6 step 5 — snapshot loop + RESTORE.md | ok — the loop mirrors `scifact-512-qdrant-snapshots/RESTORE.md:11-17` verbatim; both existing snapshots carry peer `6802952876034638` |
| 0.28 | T6 step 6 — schema-row delete, recreate, log grep, `wc -l`, `.meta.json` | ok — `_iverson_schema` is `type_name`(PK)/`schema_json`/`updated_at` and holds the `BenchmarkDocument` row today; the log line format matches the live `EmbeddingService initialized: model=… dimension=768 documentPrefix=… queryPrefix=…`; `benchmark-query` writes `<label>.meta.json` unconditionally (`BenchmarkQueryScenario.cs:168-223`, inside a plain `using` block, not a reranker branch); 15,000 rows / 300 queries confirmed on `rerank-a0.chunks.trec` |
| 0.29 | T6 step 7 — M2 (bge-small, 384 dims) | ok — `--drop` recreates both collections at the probed dimension before registration re-reads it, and the arctic runs already exercised a 384-dim benchmark collection |
| 0.30 | T6 step 8 / T7 step 1 — park and re-gate | ok — `stack.py:124-131`'s `stop_out_of_tier` would stop `iverson-tei-embed`; the plan never calls `stack.py`, consistent with P27 |
| 0.31 | T7 step 2 — NFCorpus ingest, "documents 3633, chunks 14729" | ok — recomputed: `3633` docs → `14729` chunks at 512/448 (`4837` at 2048/1792), so the spec's §6.2 inference of N0's window is reproducible and the gate is achievable |
| 0.32 | T7 step 4 — 16,150 rows / 323 queries | ok — `wc -l` and `cut -d' ' -f1 \| sort -u \| wc -l` on `nfcorpus-run-2026-08-27/runs/rerank-a0.chunks.trec` → 16150 / 323 |
| 0.33 | T7 step 5 — restore, schema clear, API recreate with defaults | ok — the two `.snapshot` files, the peer-id strip and the upload endpoint all match RESTORE.md; the API recreate has no env overrides, so Compose's config hash differs from the bge-small container and it does recreate |
| 0.34 | T8 step 1 — five (seven) `report.py` invocations, `echo "exit=$?"` | ok — `echo A; python3 X; echo "exit=$?"` reports python3's status; `--run` is `required=True` and `--stats-path` optional (`report.py:720-728`), so the three throughput invocations are well-formed; `ls -d $C/scifact-bge-base-2026-* \| tail -1` matches the `$D`-suffixed directories T6 creates |
| 0.35 | T8 step 2/3/4 — gate arithmetic, verdict doc shape, `git add -f` | ok — `report.py:511` prints `95% CI [lo, hi]` per measure, which is what the gate reads; `docs/plans/2026-09-GATE-reranker-phase1.md` exists as the shape reference; `docs/plans/` is gitignored so `-f` is required |

### Cross-task interface contracts

| # | Contract | Disposition |
|---|---|---|
| 0.36 | T1 produces `_baseUrl` / `EndpointUri`; T2 consumes `_baseUrl` | ok — T2 replaces exactly the initialiser T1 added; `EndpointUri` is untouched |
| 0.37 | T2 produces `Models`/`ModelEndpoint`/`BaseUrlFor`; T5's `Embeddings__Models__0__Name/__BaseUrl` bind to them | ok — env → `Embeddings:Models:0:Name` under `config.GetSection("Embeddings")` (`ServiceCollectionExtensions.cs:10`); P6's scratch-console binding is the covering evidence and the property shape in the plan matches it |
| 0.38 | T3 produces the bge query prefix; T6's query arms consume it through the resolver | ok — `EmbeddingPrefixes.For` is read in `EmbeddingService`'s field initialisers, so the recreated API picks it up; P14's ordering (T3 before T6) is stated |
| 0.39 | T3 produces the regenerated contract; `ingest.py` consumes it at import | ok — `ingest.py:141-143` reads it from its own directory; `document_prefix_for` keys by family with the `defaultDocumentPrefix` fallback |
| 0.40 | T4 produces sidecar keys `model`/`embed_url`/`chunk_max_chars`/`chunk_step`; **T6/T7 (`benchmark-query`) consume the same file** | ok — persistence boundary. `IngestStats` is `record IngestStats(int Documents, int Chunks)` deserialised with `PropertyNameCaseInsensitive = true` and no `JsonUnmappedMemberHandling.Disallow` (`BenchmarkQueryScenario.cs:50-56,99-104`), so the four extra keys are ignored |
| 0.41 | T4 produces the same sidecar; **T8 (`report.py --stats-path`) consumes it — a second, differently-sourced caller** | ok — separate row per call site. Every access in `print_stats` is `stats.get(<key>)` over `documents`/`chunks`/`embed_calls`/`embeds_saved`/`started_at`/`finished_at`/`elapsed_seconds` (`report.py:320-365`); none of the four new keys collides |
| 0.42 | T6/T7 produce `<label>.{chunks,similar}.trec` + `<label>.meta.json`; T8 consumes those exact paths | ok — writer uses `Path.Combine(OutputDir, $"{ConfigLabel}.{suffix}.trec")` and `$"{ConfigLabel}.meta.json"`; the plan's `$M1/runs/bge-base.*`, `$M2/runs/bge-small.*`, `$N1/runs/bge-base.*` line up with the `--config-label` values |
| 0.43 | T6 produces the new `/build` composite; T8's `BUILD MISMATCH` prediction consumes it | ok — `load_build_composite` reads `<run>.meta.json`'s `composite` (`report.py:175-193`); both 2026-08 baselines carry `31583db5aea49136`, so a new composite prints `!! BUILD MISMATCH` and scoring continues (`report.py:485-498`) |
| 0.44 | T5 produces `EMBED_MODEL_ID`/`BENCH_EMBED_MODEL`; T6/T7 consume them on both the TEI and the API recreate | ok — both are `${VAR:-default}`; `VECTOR_RANKING_LAMBDA=0.99 docker compose config` → `"0.99"` confirms substitution reaches rendered env today |

### Rule-like content (both failure directions)

| # | Rule | Disposition |
|---|---|---|
| 0.45 | `BaseUrlFor` identity: ordinal match on the **full** model id, not the family | ok — over-inclusion impossible (exact ordinal); under-inclusion only for a tagged id (`BAAI/bge-base-en-v1.5:latest`), which no arm uses and which would fall back to the global Ollama URL and 404 loudly per spec §4 |
| 0.46 | `/info` guard predicate: `!= HttpStatusCode.OK → return`, then `TryGetProperty("model_id")` | ok — both directions exercised: 200 + matching id initialises, 200 + other id throws on **every** call, 404 initialises. Mutation-tested below |
| 0.47 | Reuse gate `len(body) <= step` vs `split_into_chunks(body, max_chars, step)` | ok — the argparse guard `0 < step <= max_chars` is what makes `reuse ⇒ exactly one chunk equal to body` hold; without it, `step > max_chars` would fire the reuse gate on a body that splits into two and trip the assert at `:601`. Over-inclusion (reuse with >1 chunk) is closed by that guard; under-inclusion (`step < len ≤ max_chars`) only costs an extra embed |
| 0.48 | `family()` = id before the first `:` applied to `BAAI/bge-*-en-v1.5` (ids that contain `/` but no `:`) | ok — each is its own family on both sides; `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` leaves `/` unescaped in the emitted keys (confirmed on the reproduced diff) |
| 0.49 | Every producer of a family key that reaches `main()`'s strict `require_known_family` loop | ok — the loop iterates `documentPrefixes` itself, so adding rows adds strict cases; ran all four and they pass |
| 0.50 | `EmbeddingPrefixes.Table` consumers, both directions | ok — `EmbeddingService` field initialisers, `IngestContractTests` (emit), and `ModelRejectedScenarioTests.cs:516-522` (`OverrideModelId` = `iverson-conformance-model-that-is-not-deployed`, still absent from the table). No other reference in the repo (`grep -rn EmbeddingPrefixes --include=*.cs`) |
| 0.51 | Profile gating as an inclusion rule over `docker compose config` output | → §2.2, §2.3 |

### Named mutations (T1/T2 "tests fail against a named mutation")

Run in the scratch copy against the fully applied Tasks 1–3 (baseline: `Passed! - Failed: 0, Passed: 46`):

| Mutation | Result |
|---|---|
| `EndpointUri("/v1/embeddings")` → `"/api/embed"` (the `/api/embed` regression) | **red** — 4 failed (both URI tests, both resolver base-URL tests) |
| resolver: `BaseUrlFor(m)` → `options.Value.BaseUrl` (a resolver that ignores `Models`) | **red** — 1 failed (`Get_ForAListedModel_SendsToThatModelsBaseUrl`) |
| guard: `if (!string.Equals(servedId, ModelId, …))` → `if (false)` (a guard that compares nothing) | **red** — 2 failed (`…ThrowsNamingBothIds_OnEveryCall`, `…SendsInfoAfterTheProbe_NotBeforeIt`) |
| guard moved **after** `_dimension = probe.Length` (CDR-2 §2.1's placement) | **red** — 1 failed (`…ThrowsNamingBothIds_OnEveryCall`) — the assertion does separate the two placements |
| default service: `BaseUrlFor(options.Value.ModelId)` → `options.Value.BaseUrl` (CDR-1 §2.1's regression) | **red** — 1 failed (`DefaultService_WhoseModelIsListedInModels_UsesTheListedBaseUrl`) |

---

## 1. Verified-plan-assumptions cross-check

| # | Verdict |
|---|---|
| P1 | still holds — all 14 paths in the check exist (`ls` of each) |
| P2 | still holds — `docs/plans/2026-09-GATE-embedding-migration.md` and all three `*-bge-*` corpora directories are absent |
| P3 | **holds in substance, cited evidence inaccurate.** The grep does **not** return 0: `grep -rn 'VerifyServedModelAsync\|EndpointUri\|BaseUrlFor\|ModelEndpoint\|InfoCalls\|LastRequestUri\|CopyAsync\|InfoResponse' --include=*.cs Iverson.Server` (bin/obj excluded) → 2 hits, `Iverson.Server/Iverson.LoadTest/Benchmark/TeiRerankClient.cs:26` (`private sealed record InfoResponse`) and `:33`. That is a private nested type in a different assembly, so there is no collision — and the applied build confirms it. Only the "0 hits" figure is wrong (a non-globstar `Iverson.Server/**/*.cs` glob would explain the miss) |
| P4 | still holds — `IEmbeddingService.cs:3-11` is exactly those six members; the plan adds none |
| P5 | still holds — proved by execution rather than re-read: `handler.LastRequest.RequestUri` is `http://tei-embed:8091/v1/embeddings` and the `/info` request's `AbsolutePath` is `/info` in the passing suite |
| P6 | still holds — the plain `ModelEndpoint { Name; BaseUrl }` + `List<ModelEndpoint> Models = []` shape compiles and `BaseUrlFor` returns `BaseUrl` for an unlisted id and for an empty list (both covered by passing tests) |
| P7 | still holds — `EmbeddingService.cs:1-5` has no `using System.Net;`; `Iverson.Embeddings.csproj:6,10-14` has `ImplicitUsings` and `Microsoft.Extensions.Options.ConfigurationExtensions` 10.0.9. The applied Task 1 needed no other using or package |
| P8 | still holds — `Iverson.Embeddings.Tests.csproj:5` `ImplicitUsings`; `EmbeddingServiceTests.cs:1` `using System.Net;`; `CreateService(HttpMessageHandler, EmbeddingServiceOptions)` at `:50` |
| P9 | still holds — the existing `CountingHttpMessageHandler` re-reads `response.Content` at `:193-197` and the concurrency test drives 10 callers at `:220`; the applied `CopyAsync` needs no null branch and the 500-with-no-content test passes through it |
| P10 | still holds — both csproj paths exist, `IngestContractTests.cs` declares no `IClassFixture`/`ICollectionFixture`/`[Collection`, `Iverson.Api.Tests` declares no assembly fixture, `dotnet --version` → `10.0.111` |
| P11 | still holds — after applying Task 4, `grep -n "embed(\|ingest_document(\|update_stats_sidecar(\|split_into_chunks("` shows the only call sites are the ones the task edits (`:601`, `:613`, `:777`, `:847`, `:863`) plus `_verify_algorithm_goldens`'s no-arg call, which is untouched |
| P12 | still holds — `IngestContractTests.cs:116-131` emits both maps from `EmbeddingPrefixes.Table` with `UnsafeRelaxedJsonEscaping` (`:170-174`); `ingest.py:414-416,435` keys by family with the `__default__` fallback; `ModelRejectedScenarioTests.cs:516-522` (in `Iverson.ClientConformance.Tests`, not the bare filename the plan cites) compares families and the override id is still absent from the table |
| P13 | still holds — `EmbeddingService.cs:9-12` constructor signature unchanged; the five construction sites compiled untouched in the applied build |
| P14 | still holds — Task 5's smoke calls `verify_contract('BAAI/bge-base-en-v1.5')` permissively and `main()`'s strict loop iterates only the families the contract carries, so it passes with or without Task 3; ran both shapes |
| P15 | still holds — `docker-compose.yml:373-377,464-468` (`context: ..`, `dockerfile: Iverson.Server/Iverson.Api/Dockerfile`, `image: iverson-api` on both); `Dockerfile:7` restores `Iverson.Embeddings.csproj` and `:12-13` `COPY . .` + publish |
| P16 | still holds — `docker compose version` → `2.40.3+ds1-0ubuntu1`; `--profile reranker config --services \| grep -c '^reranker$'` → 1; `grep -c '^networks:' docker-compose.yml` → 0; `iverson-ollama` is on `iversonserver_default` and `iverson-api` carries `com.docker.compose.project=iversonserver` |
| P17 | still holds — re-ran the dry-run: `Recreate` for `iverson-postgres` and `iverson-authentik-server` only; the other four print `Running`/`Healthy` |
| P18 | still holds — `\d _iverson_schema` → `type_name` (PK), `schema_json`, `updated_at`, no model column; the `BenchmarkDocument` row exists (`updated_at 2026-09-04 16:31:54+00`) |
| P19 | still holds — `curl 127.0.0.1:6333/` → `1.18.2`; the two snapshot files carry peer `6802952876034638`, matching `RESTORE.md:11-17`'s strip |
| P20 | still holds — `Program.cs:18,88-89,151-164` (`--help` becomes the *flags*, `benchmark-query` is still the command, so schemas register and `Schemas registered.` prints); `BenchmarkQueryScenario.cs:60-81` then refuses safely; run files and `<label>.meta.json` written at `:219-222,283-287` |
| P21 | still holds — `report.py:720-728` (`--run` repeatable and required, `--qrels` required, `--stats-path` optional, `--baseline` optional), `:496` is the `BUILD MISMATCH` print, `:543-548` excludes the baseline from the comparison set |
| P22 | still holds — every listed file present; both `rerank-a0.meta.json` sidecars carry `"composite": "31583db5aea49136"` |
| P23 | still holds — `benchmark_documents_chunks_tenant_bypass` 19,967 points / size 768, `benchmark_documents_tenant_bypass` 5,183; `/build` composite `31583db5aea49136`; the API log has exactly one `EmbeddingService initialized: model=nomic-embed-text dimension=768` line |
| P24 | still holds — no `*tei*` container in `docker ps -a`, nothing on 8091; `ir_measures` 0.4.3 and `scipy` 1.18.1 import off the corpora `python-libs` |
| P25 | still holds **as written** — `VECTOR_RANKING_LAMBDA=0.99 docker compose config` → `VectorRanking__Lambda: "0.99"`, unset → `"0.70"`; `Embeddings__ModelId: nomic-embed-text` renders unquoted (twice); `config --volumes` lists volume names. What it does *not* state — and what Task 5 step 3's last line depends on — is how `config --volumes` treats a volume referenced only by a profile-gated service: see §2.3 |
| P26 | still holds — `git log --oneline -6` is lowercase imperative with no type prefix |
| P27 | still holds — `stack.py:124-131` `stop_out_of_tier` stops every running `iverson-`-prefixed container outside the tier; no task invokes `stack.py` |
| P28 | still holds — `wc -l` 15,000 / 16,150 and 300 / 323 distinct query ids on the two `rerank-a0.chunks.trec`; `ingest.split_into_chunks` on a 2,000-character text → `2` at the default and `5` at 512/448 |

### Span check — plan dependencies with no covering assumption

- **The new C# test code blocks compile.** P5/P6 cover URI composition and options binding, P7/P8 cover usings and packages, P9 covers response re-reading — nothing covers the *syntactic* validity of the new blocks. Verified in-round by applying them: two of them do not compile → **§2.1**.
- **How `docker compose config` renders a profile-gated service block and its volume.** P25 covers `${VAR:-default}` substitution, unquoted string rendering and `config --volumes` in general; nothing covers the rendered block's line count or the profile-pruning of top-level volumes. Verified in-round against the structurally identical `reranker` service → **§2.2** and **§2.3**.
- **The candidate corpora chunk to exactly 19,967 / 14,729 at 512/448 with *today's* chunker.** P28 covers the row counts and a synthetic 2,000-character case, and B3 covers where the baseline's window value came from — but the hard stops in T6 step 4 ("Any other number … the arm is invalid") and T7 step 2 depend on the *algorithm* not having drifted since the 2026-08 ingests. Verified in-round against the real corpora: `5183 → 19967` and `3633 → 14729`, exactly the recorded sidecar totals. Holds.
- **`report.py --baseline` computes R@50 and AP deltas against the real baseline, not an exhausted generator.** B6 covers only that `--baseline` has no pool check and prints CI/MDE/Holm; the gate's second criterion is an R@50 CI lower bound, and a known past defect in this file made exactly that number wrong. Verified in-round: `report.py:557-560` materialises `baseline_run = list(ir_measures.read_trec_run(baseline_path))` before the per-measure loop. Holds.
- **`docker compose` resolves the project file from `Iverson.LoadTest/scripts`** (T5 step 4's trailing `docker compose --profile tei stop tei-embed` runs from that cwd after `cd Iverson.LoadTest/scripts`). Verified in-round: `docker compose config --services` from that directory exits 0 and lists the `iversonserver` services. Holds.
- **`benchmark-query` writes `<label>.meta.json` on a non-reranked run.** P20 lists the sidecar among the outputs but cites `:429`, a reranker-adjacent line; T8's `BUILD MISMATCH` prediction and T6 step 6's composite grep both depend on it. Verified in-round: the write is inside an unconditional `using (var http = new HttpClient())` block at `BenchmarkQueryScenario.cs:168-223`. Holds.

---

## 2. Literal-wrongness findings

### 2.1 — The OpenAI-shaped `SuccessResponse` raw string literal does not compile: the JSON's trailing `}}` is parsed as an interpolation terminator (CS9007), in both test files

**Description.** Task 1 step 1 and Task 2 step 1 both introduce this literal:

```csharp
$$"""{"object":"list","data":[{"object":"embedding","index":0,"embedding":[{{string.Join(",", embedding)}}]}],"model":"x","usage":{"prompt_tokens":1,"total_tokens":1}}"""
```

Under a `$$`-prefixed raw string, **two** consecutive closing braces are the interpolation-hole terminator. The
JSON payload ends with `…"total_tokens":1}}` — one brace closing `usage`, one closing the root object — so the
compiler reads that pair as closing a hole that was never opened and rejects the file. This is the
`SuccessResponse` every existing and every new test in `EmbeddingServiceTests` and `EmbeddingServiceResolverTests`
depends on, so neither test project builds, and with `Iverson.Embeddings.Tests` in `Iverson.slnx`, `dotnet build
Iverson.slnx` (Task 1 step 5, Task 2 step 4) fails too. Task 1 step 3's "confirm the expected failures" step never
gets to run a test at all: it gets a build error instead of the predicted red assertions.

`InfoResponse`'s literal on the next lines is fine — it ends with a single `}`.

**Evidence.** Applied Task 1 steps 1–2 verbatim to the scratch copy and built:

```
$ dotnet test Iverson.Embeddings.Tests/Iverson.Embeddings.Tests.csproj
.../Iverson.Embeddings.Tests/EmbeddingServiceTests.cs(88,182): error CS9007: The interpolated raw string
literal does not start with enough '$' characters to allow this many consecutive closing braces as content.
```

Column 182 of that line is `…al_tokens":1}}""",`. Applying Task 2 step 1 reproduces it in the other file:

```
.../Iverson.Embeddings.Tests/EmbeddingServiceResolverTests.cs(34,182): error CS9007: ...
```

**Proposed fix.** Raise the delimiter count in both occurrences so a literal `}}` is unambiguous — replace `$$"""`
with `$$$"""` and the hole `{{string.Join(",", embedding)}}` with `{{{string.Join(",", embedding)}}}`, leaving the
JSON text byte-identical. With exactly that change applied (and nothing else), the scratch build is clean and the
suite is `Passed! - Failed: 0, Passed: 44` at the end of Task 2 / `46` at the end of Task 3, with all five named
mutations red. (Any equivalent escape works — e.g. splitting the trailing `}}` out of the raw literal — but the
`$$$` form is the one verified here.)

### 2.2 — Task 5 step 3's `grep -A 12 … | grep -c …` can only ever print 2, not the `# 4` the plan states

**Description.** The step's third render check is:

```bash
docker compose --profile tei config | grep -A 12 '^  tei-embed:' | grep -c 'BAAI/bge-base-en-v1.5\|--auto-truncate\|8091\|tei_models'   # 4
```

`docker compose config` expands `ports:` and `volumes:` into multi-line mappings. In the rendered block, `published:
"8091"` is the **14th** line and `source: tei_models` the **19th**, both outside the 13 lines `grep -A 12` emits. Only
the `command:` entries (`- BAAI/bge-base-en-v1.5` and `- --auto-truncate`) fall inside the window, so the count is 2.
An implementer following the plan reads `2 ≠ 4` as a failed verification of a compose file that is in fact correct,
and the natural response — editing the compose file to chase the missing two — makes it worse.

**Evidence.** The plan's `tei-embed` has exactly the `reranker` service's key set (`image`, `container_name`,
`profiles`, `command`, `ports`, `volumes`), so its rendered block is line-for-line identical in structure. Against
the live file:

```
$ docker compose --profile reranker config | grep -A 12 '^  reranker:' | grep -c 'cross-encoder/ms-marco-MiniLM-L-6-v2\|--auto-truncate\|8090\|reranker_models'
2
$ docker compose --profile reranker config | grep -A 12 '^  reranker:' | tail -3
      default: null
    ports:
      - mode: ingress
```

and the full block (`docker compose --profile reranker config | sed -n '482,501p'`) shows `published: "8090"` on the
14th line and `source: reranker_models` on the 19th.

**Proposed fix.** Widen the window past the rendered `volumes:` entry and state the reachable count — e.g.
`grep -A 20 '^  tei-embed:' | grep -c 'BAAI/bge-base-en-v1.5\|--auto-truncate\|8091\|tei_models'   # 4` (verified: the
same command with `-A 20` against `reranker` prints 4). Alternatively split it into four `grep -q` assertions over the
whole `--profile tei config` output, which removes the dependency on the block's line count entirely.

### 2.3 — Task 5 step 3's `docker compose config --volumes | grep -c '^tei_models$'` prints 0, not 1: Compose prunes volumes referenced only by a profile-gated service

**Description.** The step's last render check is:

```bash
docker compose config --volumes | grep -c '^tei_models$'                                  # 1
```

with no `--profile tei`. `docker compose config` without the profile drops `tei-embed` from the model (which the
step's *first* line deliberately asserts, expecting 0 from `config --services`), and it also drops any top-level
volume that no remaining service mounts. `tei_models` is mounted only by `tei-embed`, so it disappears from
`--volumes` as well. The check therefore reports 0 against a correct compose file — the same false-negative shape as
§2.2, and the one most likely to be read as "the `volumes:` entry was never added".

**Evidence.** `reranker_models` is in the identical position today (declared at `docker-compose.yml:548`, mounted only
by the profile-gated `reranker`):

```
$ docker compose config --volumes | grep -c '^reranker_models$'
0
$ docker compose --profile reranker config --volumes | grep -c '^reranker_models$'
1
$ docker compose config --volumes | tr '\n' ' '
zookeeper_logs ollama_data postgres_data kafka_data qdrant_data starrocks_data prometheus_data zookeeper_data
```

**Proposed fix.** Add the profile to that one line: `docker compose --profile tei config --volumes | grep -c
'^tei_models$'   # 1` (verified against `reranker` above). If the pruning itself is worth pinning, the stronger pair is
`docker compose config --volumes | grep -c '^tei_models$'   # 0 — pruned with the profiled service` followed by the
`--profile tei` form expecting 1.

---

## 3. Forced decisions

No forced decisions found.

---

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes.** §1 has no failed assumption (P3's *evidence* count is wrong but its
claim is verified by compilation); §2 carries three findings — one that stops Tasks 1–2 from building at all and two
verification commands in Task 5 whose stated expected output a correct implementation cannot produce; §3 is empty.
With the §2.1 delimiter fix applied and nothing else changed, Tasks 1–4 build clean in a throwaway copy, the suite is
46/46 green, and all five of the plan's named mutations go red.
