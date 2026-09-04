# Critical Implementation Review: 2026-09-04-embedding-migration-implementation-plan (Round 2)

**Plan:** docs/plans/2026-09-04-embedding-migration-implementation-plan.md
**Verified plan-level assumptions section:** present (P1–P34, plus an `Inherited from spec` block E1–E7 / S1–S12 / C1–C4 / B1–B13 / H1–H2)

⚠️ 3 commits since plan-write time (SHA `5d1ff5d`); cited file:line references re-checked under §1.
(`git log --oneline 5d1ff5d..HEAD` → `fd75023` the plan, `bf3ce58` CIR-1, `d4c9718` the CIR-1 fixes — all
this workstream's own commits, so no third-party drift.)

**Method.** Repo `main` @ `d4c9718`, clean tree. The §0 enumeration was re-derived from the plan text
before the round-1 diff was read in detail, and weighted toward the surfaces round 1 exercised least
(Tasks 5–8: compose, the arm protocol, the restore, the scoring and the verdict). Round 1's scratch copy
of `Iverson.Embeddings` / `Iverson.Embeddings.Tests` / `Iverson.LoadTest/scripts` was reused rather than
re-applied from scratch: its two `SuccessResponse` literals were confirmed byte-identical to the plan's
current lines 189 and 428, and the suite re-run there (one line, §4 below). Nothing under
`/home/ben/repositories/Iverson` or `~/repositories/iverson-benchmark-corpora/` was written. No container
was created, started, stopped or recreated; live probes are read-only `curl`s (Ollama, Qdrant `GET` with
the dev api-key, the API on 8081), `docker inspect`, `docker logs`, `docker compose config` /
`--dry-run`, and `SELECT` / `\d` through `docker exec … psql`. **TEI was never started, no ingest was
run, and no snapshot was created, uploaded or deleted**, so §10-measured TEI behaviour and the arm
wall-clocks are taken from the spec's ground truth. Where a claim could only be settled by a write, it
was settled instead from the running Qdrant's own OpenAPI document (see 0.29).

---

## 0. Coverage enumeration

### Tasks × surfaces

| # | Task / surface | Disposition |
|---|---|---|
| 0.1 | T1 step prose + code blocks (fakes, `CopyAsync`, `InfoResponse`, the five tests, `EmbeddingService.cs` edits) | ok — the plan's current text is what round 1's scratch tree holds; `sed -n 88p` / `34p` of the two test files match plan lines 189 / 428 exactly, and `dotnet test` there is `Failed: 0, Passed: 46` |
| 0.2 | T1 step 3/5/6 commands | ok — `dotnet build Iverson.slnx` and both `git add` paths resolve (`Iverson.slnx`, both `.cs` files present) |
| 0.3 | T1 code block — `EnsureInitializedAsync` replacement target | ok — `EmbeddingService.cs:34-52` is exactly the method the plan replaces; `EmbedAsync`'s request line is `:66` and the `embeddings[0]` parse `:77-82`, as the Files header states |
| 0.4 | T2 step prose + code blocks (`Models`/`ModelEndpoint`/`BaseUrlFor`, both construction sites) | ok — `EmbeddingServiceOptions.cs:1-13` is the plain-property class the append assumes; the quoted resolver line is unique in the file |
| 0.5 | T2 step 4/5 commands | ok — build + `git add` of five existing paths |
| 0.6 | T3 steps 1–5 (theory rows, table rows, regeneration, python check, commit) | ok — `IngestContractTests.cs:116-131` emits `documentPrefixes` and `documentComposition` from `EmbeddingPrefixes.Table`, `:170-174` sets `UnsafeRelaxedJsonEscaping`; the regeneration+gate command pair is the file's own protocol |
| 0.7 | T4 step 1–4 code blocks (`embed`, `ingest_document`, `update_stats_sidecar`, `main()`) | ok — every edit target verified against today's source: `embed` `:530`, `ingest_document` `:583`, the reuse gate `:597`, `update_stats_sidecar` `:656`, the probe `:754`, the `ingest_document` call `:822-825`, the sidecar call `:838-840`. The new positional arguments line up with the new signatures in all three call sites, and `**provenance` after `"finished_at"` is valid inside the existing `merged = {...}` literal and overrides a resumed run's keys, as the plan's comment claims |
| 0.8 | T4 step 5/6 commands | ok — `--limit`, `--object-collection`, `--chunks-collection`, `--drop`, `--key-map-path` all exist (`ingest.py:693-710`); `--drop` removes the progress file and sidecar before `ensure_collection`, so a scratch run is self-contained |
| 0.9 | T5 step 1 — the `tei-embed` service block | ok — key set (`image`, `container_name`, `profiles`, `command` of three entries, `ports`, `volumes`) is identical to `reranker` (`docker-compose.yml:156-165`); the insertion point after `reranker` and the `volumes:` block ending at `reranker_models:` (`:548`) both exist |
| 0.10 | T5 step 2 — api/worker env | ok — `Embeddings__BaseUrl=http://ollama:11434` exists on both services and `Embeddings__ModelId=nomic-embed-text` is the next `Embeddings__` line on each; today's render shows exactly two of each (`docker compose config \| grep -n Embeddings__` → lines 172/173 and 252/253) |
| 0.11 | T5 step 3 — the eight render checks | ok — all eight are now reachable. `Embeddings__BaseUrl: http://ollama:11434` renders **unquoted** today, so the `Models__0__BaseUrl: http://tei-embed:80` grep pattern will match; the `-A 20` and both `--volumes` lines were re-verified against `reranker` (0.24, §4) |
| 0.12 | T5 step 4 — TEI smoke | ok — `document prefix for model` is printed at `ingest.py:769`, so the `grep` target exists; the trailing `stop` runs from `…/scripts`, where `docker compose config --services` exits 0 (P33). Not executed (no TEI start) |
| 0.13 | T5 step 5 — commit | ok — path exists |
| 0.14 | T6 step 1 — environment gate | ok — re-ran the dry-run read-only: `Recreate` only for `iverson-postgres` and `iverson-authentik-server`; `docker inspect -f '{{.State.Health.Status}}'` returns `healthy` for all six (none lacks a healthcheck, so the template cannot error); live counts 19,967 / 5,183 at size 768 and `/build` composite `31583db5aea49136` |
| 0.15 | T6 step 2 — build + recreate + inspect + composite | ok — `Dockerfile:4-14` restores `Iverson.Embeddings.csproj` then `COPY . .`; `iverson-worker` shares `image: iverson-api` (`:467`), and step 1 leaves it stopped. `Program.cs:404-406` calls `EnsureInitializedAsync` eagerly at startup, **before** `app.Run()`, so the `until curl …/build` gate guarantees the `EmbeddingService initialized` line is already in the log when step 2 greps it; the failure branch logs `Embedding service not initialized at startup`, which the `not initialized` alternative matches |
| 0.16 | T6 step 3 — same-build nomic control | ok — and the control is *reproducible*, not just checkable: `git log --since=<image build time> -- Iverson.Server/{Iverson.Api,Iverson.Vector,Iverson.Embeddings,Iverson.Sql,Iverson.StarRocks,Iverson.Events,Iverson.Client.Contracts}` returns **no commits** (last server-code commit `5615ba1` 2026-09-02 13:43; `docker image inspect iverson-api --format '{{.Created}}'` → 2026-09-03 02:48 UTC), so the rebuild differs from the live image only by Tasks 1–3. `rerank-a0.meta.json` carries `"reranker": null`, and the gate doc records A0 at λ = **0.70** — the compose default the step-2 recreate uses (`2026-09-GATE-reranker-phase1.md:42`), so no `VECTOR_RANKING_LAMBDA` override is missing. The same technique already succeeded once on this build (`…:194-195`, `rerank-a0-2026-09-04` byte-identical on columns 1–5) |
| 0.17 | T6 step 4 — TEI up, ingest, count assertions | ok — `--corpus $M1/beir/corpus.jsonl` after `cp -r $SCI/beir $M1/` keeps `corpus_name` = `beir`, so `uuid5(NAMESPACE, "beir:<docId>")` reproduces the baseline's key derivation (`ingest.py:783,817`); `tail -3` lands on key-map / `this run:` / `cumulative` (`:840-849`); the chunk-count hard stop is exact — see 0.31 |
| 0.18 | T6 step 5 — snapshot loop | ok — Qdrant's REST bodies are **compact** (`{"result":[{"name":"…"` on the live listing), so `grep -o '"name":"[^"]*"' \| cut -d'"' -f4` is well-formed against the POST response, whose `result` object carries exactly one `name`. `$K` is set in step 1 of the same task. Not executed (no snapshot created) |
| 0.19 | T6 step 6 — schema delete, API recreate, log grep, `wc -l`, composite grep | ok — `_iverson_schema` is `type_name`(PK)/`schema_json`/`updated_at` and holds the `BenchmarkDocument` row today; the env change (`BENCH_EMBED_MODEL`) alters the config hash so the container is genuinely recreated and `docker logs` starts clean; `Program.cs:150-164` prints `Schemas registered.` only when credentials are configured — which they are in this task (`source …/bench-env.sh` at plan line 799, step 3) |
| 0.20 | T6 step 6 — registration against an already-ingested collection | ok — the expensive failure mode is closed: `IntelligenceCollectionManager.ApplyCollectionAsync` takes the "up to date" branch when every schema vector name exists at the declared dimension, and only migrates (copying points) when a *name* is missing (`IntelligenceCollectionManager.cs:75-96`). ingest.py creates `body_vector`+`body_centroid` / `body_vector` from the same contract that emits the schema, so the 5.5 h ingest is not wiped by registration |
| 0.21 | T6 step 7 — M2 (bge-small, 384 dims) | ok — `--drop` recreates both collections at the *probed* dimension before registration re-reads it (`ingest.py:754,771-780`), and `ApplyCollectionAsync`'s dimension check then compares 384 against 384 |
| 0.22 | T6 step 8 / T7 step 1 — park and re-gate | ok — `stack.py:124-131`'s `stop_out_of_tier` would stop `iverson-tei-embed`; no task calls `stack.py`, consistent with P27 |
| 0.23 | T7 step 2 — NFCorpus ingest and the 3,633 / 14,729 gate | ok — recomputed against the real corpus (0.31); `ingest.py` carries the Qdrant api-key itself (`:146`), so this step needs no shell `$K` |
| 0.24 | T7 step 3 — snapshot into `nfcorpus-bge-base-qdrant-snapshots/` | → **§2.1** |
| 0.25 | T7 step 4 — register and query N1 | → **§2.2** |
| 0.26 | T7 step 5 — restore, schema clear, API recreate with defaults | ok — the peer-id strip matches both `.snapshot` filenames; the recreate carries no env overrides, so the config hash differs from the bge-small container and it does recreate; `K` and `bench-env.sh` are both established inside this step (plan lines 896, 906) |
| 0.27 | T8 step 1 — the seven `report.py` invocations | ok — `--run` is `action="append", required=True` and `--stats-path` optional (`report.py:719-728`); with `--baseline` (never `--pair`) no `sys.exit` path is reachable after argument validation (`grep -n sys.exit report.py` → the only post-validation exits are `--pair`'s pool checks at `:642-660` and `"--baseline excludes every discovered run"` at `:551`, unreachable here because the baseline is not among the `--run` files), so "every invocation must exit 0" is achievable. `ls -d $C/scifact-bge-base-2026-* \| tail -1` matches the `$D`-suffixed directories T6 creates |
| 0.28 | T8 step 2 — gate arithmetic | ok — both directions of the sign convention checked: `diffs = run_arr - baseline_arr` (`report.py:427`), so a negative CI lower bound means the *candidate* is worse, which is the direction the −0.02 gate reads. `95% CI [lo, hi]` is printed per measure per run (`:509`), and `measures = [nDCG@10, R@50, AP]` (`:764`) supplies both gated measures |
| 0.29 | T8 step 3/4 — verdict doc shape and commit | ok — `docs/plans/2026-09-GATE-reranker-phase1.md` exists as the shape reference; `git check-ignore -v docs/plans/…` → `.gitignore:49`, so `-f` is required and the plan uses it |
| 0.30 | T8 — Holm family size | ok — `family_size = len(valid)` is computed **per measure** over the runs in that invocation (`report.py:576-579`), so two `--run` files on SciFact give m = 2 and one on NFCorpus gives m = 1, exactly as the Global Constraints state; the baseline is not in `run_paths` so it cannot inflate the family |

### Cross-task interface contracts

| # | Contract | Disposition |
|---|---|---|
| 0.31 | T4 produces the effective `(max_chars, step)`; T6/T7's chunk-count hard stops consume it | ok — recomputed in-round from the real corpora with today's chunker: `5183 → 19967` and `3633 → 14729` at 512/448, byte-matching the recorded sidecars. The stops test the window, not algorithm drift |
| 0.32 | T4 produces sidecar keys `model`/`embed_url`/`chunk_max_chars`/`chunk_step`; **T6/T7 `benchmark-query` consume the same file** | ok — persistence boundary. `IngestStats` is `record IngestStats(int Documents, int Chunks)` with `PropertyNameCaseInsensitive = true` and no `JsonUnmappedMemberHandling.Disallow` (`BenchmarkQueryScenario.cs:51-57,109-111`), so the four new keys are ignored |
| 0.33 | T4 produces the same sidecar; **T8 `report.py --stats-path` consumes it — a second, differently-sourced caller** | ok — one row per call site. Every access in `print_stats` is `stats.get(<key>)` over `documents`/`chunks`/`embed_calls`/`embeds_saved`/`started_at`/`finished_at`/`elapsed_seconds` (`report.py:320-365`); none of the four new keys collides, and `elapsed_seconds` is present so the older span-fallback branch is not taken |
| 0.34 | T6/T7 produce `<label>.{chunks,similar}.trec` + `<label>.meta.json`; T8 consumes those exact paths | ok — writer uses `Path.Combine(OutputDir, $"{ConfigLabel}.{suffix}.trec")` (`:284-285`) and `$"{ConfigLabel}.meta.json"` (`:220`); `report.py`'s `sidecar_path_for` strips `.trec` then `.chunks`/`.similar`, so `bge-small.chunks.trec` → `bge-small.meta.json`, which is what T6 step 7 writes |
| 0.35 | T6 produces the new `/build` composite; T8's `BUILD MISMATCH` prediction consumes it | ok — `load_build_composite` reads `<run>.meta.json`'s `composite` (`report.py:175-193`); both 2026-08 baselines carry `31583db5aea49136`, so a new composite prints `!! BUILD MISMATCH` and scoring continues (`:485-498`) |
| 0.36 | T6 produces `$SCI/runs/m0-control-<date>.*`; T8 consumes only explicit file paths from `$SCI/runs/` | ok — every `--run` in T8 is a file, never the directory, so the extra control run and the 40+ pre-existing `*.trec` files in `$SCI/runs/` cannot enter the comparison set and inflate the Holm family |
| 0.37 | T5 produces `EMBED_MODEL_ID`/`BENCH_EMBED_MODEL`; T6/T7 consume them on both the TEI and the API recreates | ok — both are `${VAR:-default}`; `VECTOR_RANKING_LAMBDA=0.99 docker compose config` → `"0.99"` confirms substitution reaches rendered env today |
| 0.38 | T7 step 5 consumes the *existing* `scifact-512-qdrant-snapshots/` pair after **deleting** both collections — a deviation from `RESTORE.md`, whose proven loop uploads over the live collections | ok — settled from the running server's own contract rather than by a write: `GET /dashboard/openapi.json` documents `POST /collections/{c}/snapshots/upload` as "Recover local collection data from an uploaded snapshot. This will overwrite any data, stored on this node, for the collection. **If collection does not exist - it will be created.**" So the added DELETE is redundant but not breaking |
| 0.39 | T7 step 3/4 consume shell state (`$K`, the `bench-env.sh` exports) that T6 established | → **§2.1**, **§2.2** |
| 0.40 | T1 produces `_baseUrl`/`EndpointUri`; T2 consumes `_baseUrl` | ok — T2 replaces exactly the initialiser T1 added; `EndpointUri` untouched |
| 0.41 | T2 produces `Models`/`BaseUrlFor`; T5's `Embeddings__Models__0__Name/__BaseUrl` bind to them | ok — env → `Embeddings:Models:0:Name` under the `Embeddings` section (P6's covering evidence); the rendered names in T5 step 3's greps are the ones the binder reads |
| 0.42 | T3 produces the bge query prefix; T6's query arms consume it through the resolver | ok — `EmbeddingPrefixes.For` is read in `EmbeddingService`'s field initialisers (`:20-23`), so a recreated API picks it up; P14 states T3 before T6 |

### Rule-like content (both failure directions)

| # | Rule | Disposition |
|---|---|---|
| 0.43 | Chunk-budget eligibility: `ChunkBudgetGuard.Evaluate(documents, chunks, 50, 5)` runs on every arm | ok — both directions: M1/M2 are 19,967/5,183 = 3.85 chunks/doc and N1 is 14,729/3,633 = 4.05, the *same* ratios as the baselines that already passed the guard, so it can neither refuse a valid arm nor pass a mis-windowed one silently (a wrong window changes the chunk count, which the hard stop catches first) |
| 0.44 | Profile gating as an inclusion rule over `docker compose config` output | ok — both directions now asserted by the plan: `config --services`/`--volumes` must yield 0 for a profiled service and its volume, `--profile tei` forms must yield 1 (re-verified against `reranker`: 0/1 and 0/1) |
| 0.45 | `BaseUrlFor` identity: ordinal match on the full model id | ok — over-inclusion impossible (exact ordinal); under-inclusion only for a tagged id, which no arm uses |
| 0.46 | `/info` guard predicate: `!= OK → return`, then `TryGetProperty("model_id")` | ok — 200+match initialises, 200+other throws on every call, 404 initialises; all three are asserted by the passing suite |
| 0.47 | Reuse gate `len(body) <= step` against `split_into_chunks(body, max_chars, step)` | ok — the argparse guard `0 < step <= max_chars` is what makes "reuse ⇒ exactly one chunk equal to body" hold (`ingest.py:597-605`); over-inclusion is closed by that guard, under-inclusion costs only an extra embed |
| 0.48 | Every producer of a family key reaching `main()`'s strict `require_known_family` loop | ok — the loop iterates `CONTRACT["embedding"]["documentPrefixes"]` itself (`ingest.py:730-732`), so adding the two bge rows adds two strict cases, both of which pass |
| 0.49 | `EmbeddingPrefixes.Table` consumers, both directions | ok — `EmbeddingService` field initialisers, `IngestContractTests` (emit), `ModelRejectedScenarioTests` (compares families, override id still absent from the table); no other reference |

---

## 1. Verified-plan-assumptions cross-check

| # | Verdict |
|---|---|
| P1 | still holds — all 14 paths exist (`ls` of each) |
| P2 | still holds — `docs/plans/2026-09-GATE-embedding-migration.md` absent; `ls -d …/*bge*` → "No such file or directory" |
| P3 | still holds **as corrected in `d4c9718`** — the fresh grep returns exactly the two hits the row now names: `Iverson.Server/Iverson.LoadTest/Benchmark/TeiRerankClient.cs:26` and `:33`, a private nested record in a different assembly |
| P4 | still holds — `IEmbeddingService.cs:3-11` is exactly those six members |
| P5 | still holds — proved by execution: the scratch suite's URI assertions (`http://tei-embed:8091/v1/embeddings`) and the `/info` `AbsolutePath` test both pass |
| P6 | still holds — `EmbeddingServiceOptions.cs:1-13` is the plain-property class the append assumes, and the three binding-dependent tests pass in the scratch tree |
| P7 | still holds — `EmbeddingService.cs:1-5` has no `using System.Net;`; the applied Task 1 needed no other using or package |
| P8 | still holds — `EmbeddingServiceTests.cs:1` is `using System.Net;`; `CreateService(HttpMessageHandler, EmbeddingServiceOptions)` at `:50` |
| P9 | still holds — the existing `CountingHttpMessageHandler` re-reads `response.Content` across 10 concurrent callers; the applied `CopyAsync` needs no null branch |
| P10 | still holds — `grep -c 'IClassFixture\|ICollectionFixture\|\[Collection' IngestContractTests.cs` → 0; no `[assembly:` attribute in `Iverson.Api.Tests` source; `dotnet --version` → `10.0.111` |
| P11 | still holds — `grep -n "embed(\|ingest_document(\|update_stats_sidecar(\|split_into_chunks("` gives defs at `:237,:530,:583,:656` and call sites at `:481,:585,:598,:610,:754,:822,:838` — every caller the new parameters touch is inside Task 4, and `_verify_algorithm_goldens`'s `:481` no-arg call is untouched |
| P12 | still holds — `IngestContractTests.cs:116-131` emits both maps from `EmbeddingPrefixes.Table`, `:170-174` sets `UnsafeRelaxedJsonEscaping`; `ingest.py:414-416,435` keys by family with the `__default__` fallback |
| P13 | still holds — `EmbeddingService.cs:9-12` constructor signature unchanged; the five construction sites compiled untouched in the scratch build |
| P14 | still holds — `main()`'s strict loop iterates only the families the contract carries (`ingest.py:730-732`), so Task 5's smoke passes with or without Task 3; Task 3 precedes Task 6 in the plan's order |
| P15 | still holds — `docker-compose.yml:374-376,465-467` (`context: ..`, `Iverson.Server/Iverson.Api/Dockerfile`, `image: iverson-api` on both); `Dockerfile:7` restores `Iverson.Embeddings.csproj`, `:12-13` `COPY . .` + publish |
| P16 | still holds — `docker compose version --short` → `2.40.3+ds1-0ubuntu1`; `--profile reranker config --services \| grep -c '^reranker$'` → 1; `grep -c '^networks:' docker-compose.yml` → 0 |
| P17 | still holds — re-ran the dry-run: `Recreate` for `iverson-postgres` and `iverson-authentik-server` only; the other four print `Running`/`Healthy` |
| P18 | still holds — `\d _iverson_schema` → `type_name` (PK, text), `schema_json` (jsonb), `updated_at` (timestamptz), no model column; the `BenchmarkDocument` row exists (`2026-09-04 16:31:54+00`) |
| P19 | still holds — `curl 127.0.0.1:6333/` → `1.18.2`; a ranged `GET` of an existing snapshot → `206`; both `.snapshot` files carry peer `6802952876034638`, the peer the live listing shows |
| P20 | still holds — `Program.cs:151-164` prints `Schemas registered.`; `BenchmarkQueryScenario.cs` (in `Scenarios/`, not the `Benchmark/` directory the bare filename might suggest) writes `<label>.{chunks,similar}.trec` at `:284-289` and `<label>.meta.json` at `:220-223` |
| P21 | still holds — `report.py:719-728` (`--run` repeatable and required, `--qrels` required, `--stats-path` and `--baseline` optional), `:496` is the `BUILD MISMATCH` print, `:543-551` excludes the baseline from the comparison set |
| P22 | still holds — all eleven listed inputs present; both `rerank-a0.meta.json` sidecars carry `"composite": "31583db5aea49136"` |
| P23 | still holds — `benchmark_documents_chunks_tenant_bypass` 19,967 points / size 768, `benchmark_documents_tenant_bypass` 5,183; `/build` composite `31583db5aea49136`; exactly one `EmbeddingService initialized` line in the API log |
| P24 | still holds — `docker ps -a \| grep -c tei` → 0, nothing on 8091; `bench-env.sh` exports the five variables; `ir_measures` 0.4.3 and `scipy` 1.18.1 import off the corpora `python-libs` |
| P25 | still holds — `Embeddings__BaseUrl: http://ollama:11434` and `Embeddings__ModelId: nomic-embed-text` both render unquoted, twice each; `config --volumes` lists volume names |
| P26 | still holds — `git log --oneline -12` is lowercase imperative with no type prefix |
| P27 | still holds — `stack.py:124-131` `stop_out_of_tier` stops every running `iverson-`-prefixed container outside the tier; no task invokes `stack.py` |
| P28 | still holds — `wc -l` 15,000 / 16,150; distinct query ids 300 / 323 |
| P29 | still holds — the plan's two `$$$"""…{{{…}}}…"""` literals (lines 189, 428) are byte-identical to the scratch tree's, and `dotnet test Iverson.Embeddings.Tests` there is `Failed: 0, Passed: 46` |
| P30 | still holds — re-verified against `reranker`: `grep -A 12` → 2 and `grep -A 20` → 4; `config --volumes \| grep -c '^reranker_models$'` → 0, `--profile reranker config --volumes` → 1. The rendered block puts `published: "8090"` 14 lines after the header and `source: reranker_models` 18 lines after, both inside `-A 20` |
| P31 | still holds — recomputed in-round: `5183 → 19967` (SciFact) and `3633 → 14729` (NFCorpus) at 512/448 with today's `split_into_chunks` |
| P32 | still holds — `report.py:558` is `baseline_run = list(ir_measures.read_trec_run(baseline_path))`, materialised before the `for measure in measures` loop at `:560` |
| P33 | still holds — `docker compose config --services` from `Iverson.Server/Iverson.LoadTest/scripts` exits 0 and lists the project's services |
| P34 | still holds — the sidecar write is inside an unconditional `using (var http = new HttpClient())` block (`BenchmarkQueryScenario.cs:168-223`), not a reranker branch |

### Span check — plan dependencies with no covering assumption

- **`report.py`'s delta sign convention.** P21 covers the flags and the `BUILD MISMATCH` line; nothing covers whether the printed CI is `run − baseline` or the reverse, and the whole gate is a *signed* threshold on that CI's lower bound. Verified in-round: `report.py:427` is `diffs = run_arr - baseline_arr`. Holds.
- **Holm's family size is per measure, over the `--run` set only.** The Global Constraints assert m = 2 on SciFact and m = 1 on NFCorpus; no P covers where `report.py` draws the family from. Verified in-round: `report.py:576-579` recomputes `valid`/`family_size` inside the per-measure loop over `compare_paths`, and the baseline never enters `run_paths`. Holds.
- **Schema registration does not destroy an already-ingested collection.** B4 covers "the API default decides at registration" and P18 the schema-row delete; nothing covers what the API does to a Qdrant collection that already holds the arm's 19,967 points when the type is re-registered. Verified in-round: `IntelligenceCollectionManager.cs:64-96` takes the "up to date" branch when every schema vector exists at the declared dimension, and migrates (copying points, not dropping them) only when a vector *name* is missing. Holds.
- **`benchmark-query` needs client credentials to register anything.** P20 covers the flags and the `Schemas registered.` line; nothing states that the line is *conditional*. Verified in-round: `Program.cs:117-121` prints "Client credentials not configured … skipping tenant provisioning and schema registration" and never registers when `IVERSON_CLIENT_ID`/`SECRET`/`TOKEN_ENDPOINT` are unset. This turns out to be load-bearing → **§2.2**.
- **Qdrant refuses an unauthenticated request.** P19 exercises the snapshot endpoints *with* the api-key; nothing states that the key is mandatory. Verified in-round: `curl 127.0.0.1:6333/collections/…` without the header → `401`, body `Must provide an API key or an Authorization bearer token`. Load-bearing → **§2.1**.
- **Snapshot upload into a collection that was just deleted.** B9/P19 cover the files, the naming and the endpoint, but `RESTORE.md`'s proven loop uploads *over* the live collections; Task 7 step 5 adds a `DELETE` first. Settled without a write, from the running server's own OpenAPI document (`GET /dashboard/openapi.json`): "Recover local collection data from an uploaded snapshot. This will overwrite any data, stored on this node, for the collection. **If collection does not exist - it will be created.**" Holds.
- **The rebuilt API still reproduces `rerank-a0`'s rankings.** B3 covers the fusion-weight change and P22/P23 the composites, but nothing covers whether `main` carries a ranking-affecting server commit that the live image predates — the thing Task 6 step 3 would stop on. Verified in-round: no commit touches `Iverson.Api`/`Iverson.Vector`/`Iverson.Embeddings`/`Iverson.Sql`/`Iverson.StarRocks`/`Iverson.Events`/`Iverson.Client.Contracts` after the image's build time; and `rerank-a0` ran at λ = 0.70 (`2026-09-GATE-reranker-phase1.md:42`), the compose default the step-2 recreate restores. Holds.
- **The chunk-budget guard admits every arm.** B11 covers which sidecar keys `benchmark-query` reads; nothing covers the guard those keys feed. Verified in-round: `ChunkBudgetGuard.Evaluate(documents, chunks, 50, 5)` sees 3.85 and 4.05 chunks/doc — the baselines' own ratios. Holds.

---

## 2. Literal-wrongness findings

### 2.1 — Task 7 step 3 runs Task 6's snapshot loop without ever setting `$K`; Qdrant answers 401 and no N1 snapshot is preserved

**Description.** Task 7 step 3 is written as a reference: "**Step 3: Snapshot** as Task 6 step 5 into
`nfcorpus-bge-base-qdrant-snapshots/`". Task 6 step 5's loop authenticates every call with `-H "api-key:
$K"`, and `$K` is assigned in **Task 6 step 1** — a different task. Inside Task 7, the first (and only)
`K=…` assignment is at plan line 896, in **step 5**, two steps after step 3 needs it. The plan's own
header mandates `superpowers:subagent-driven-development` ("one subagent per task"), so Task 7 starts
with a fresh shell and no inherited exports; and even in one continuous Task 7 shell the assignment comes
too late.

With `$K` empty the snapshot POST is rejected, so `name` resolves to the empty string, the download
`curl … -o $SNAP/` targets a directory and fails, and the subsequent `DELETE` addresses
`…/snapshots/` — nothing is written and nothing is cleaned up server-side. The N1 collections are the
only copy of a ≈ 4.1 h ingest, and Task 7 step 5 drops them minutes later, so the arm becomes
unreproducible. (The step's own `ls -la $SNAP` check makes the failure visible, but the step as written
cannot produce its stated artefact.)

**Evidence.**

```
$ curl -s -H "api-key: " "127.0.0.1:6333/collections/benchmark_documents_chunks_tenant_bypass/snapshots"
Must provide an API key or an Authorization bearer token
$ curl -s -o /dev/null -w '%{http_code}\n' "127.0.0.1:6333/collections/benchmark_documents_chunks_tenant_bypass"
401
$ curl -s -o /dev/null -w '%{http_code}\n' -H "api-key: dev-only-not-for-production-qdrant-key-0123456789" \
    "127.0.0.1:6333/collections/benchmark_documents_chunks_tenant_bypass"
200
```

The 401 body carries no `"name":`, so `grep -o '"name":"[^"]*"' | cut -d'"' -f4` yields the empty string.
Plan line numbers: Task 7 spans 862–908; step 3 is line 889; the only `K=` inside Task 7 is line 896
(step 5). Task 6's assignment is at line 778 (step 1). `ingest.py` is unaffected — it carries the key
itself (`Iverson.Server/Iverson.LoadTest/scripts/ingest.py:146`), which is why step 2 works and step 3
does not.

**Proposed fix.** Give Task 7 the same one-line preamble Task 6 has, before step 3 — e.g. add
`K=dev-only-not-for-production-qdrant-key-0123456789` to step 1's gate block (which already runs `docker
inspect`), or spell the loop out in step 3 with the assignment included rather than delegating to "as
Task 6 step 5".

### 2.2 — Task 7 step 4 runs `benchmark-query` without sourcing `bench-env.sh`, so the schema is never registered and N1 produces no run files

**Description.** Task 7 step 4 is likewise a reference: "**Step 4: Register and query** as Task 6 step 6
with `--corpus-path $N1 …`". Task 6 step 6 relies on the OIDC client credentials that Task 6 **step 3**
exported via `source /home/ben/iverson-benchmark-data/bench-env.sh` (plan line 799). Task 7 sources that
file exactly once, at plan line 906 — inside **step 5**, after step 4 has already run.

Without `IVERSON_CLIENT_ID` / `IVERSON_CLIENT_SECRET` / `IVERSON_TOKEN_ENDPOINT`, `Iverson.LoadTest`
takes its no-credentials branch: it prints "Client credentials not configured … skipping tenant
provisioning and schema registration" and **never registers the type**. So the step's own
`grep -q "Schemas registered." && echo SCHEMA-OK` cannot print, the `_iverson_schema` read-back returns
no row, `BenchmarkDocument` is left unregistered under bge-base (the row having been deleted by the
step's first command), and the `benchmark-query` run that follows has no authenticated channel — the
16,150-row run files the step asserts on are not produced. N1 is the plan's only NFCorpus candidate, so
its two `report.py` blocks in Task 8 have no `--run` file.

**Evidence.** `Iverson.Server/Iverson.LoadTest/Program.cs:117-121`:

```csharp
else if (needsTenantAndSchema)
{
    Console.WriteLine(
        "Client credentials not configured (IVERSON_CLIENT_ID/IVERSON_CLIENT_SECRET/IVERSON_TOKEN_ENDPOINT) — " +
        "skipping tenant provisioning and schema registration; StarRocks seeding will be empty.");
}
```

`Schemas registered.` is printed only inside the `if (needsTenantAndSchema)` branch that this `else if`
excludes (`Program.cs:150-165`). The variables come only from the environment
(`Program.cs:28-31`), and the only file that exports them is `/home/ben/iverson-benchmark-data/bench-env.sh`
(`export IVERSON_GRPC_URL=… IVERSON_CLIENT_ID=… IVERSON_CLIENT_SECRET=… IVERSON_TOKEN_ENDPOINT=…
IVERSON_CLIENT_SCOPE=…`). Plan line numbers: Task 7 step 4 is line 891; the only `source …/bench-env.sh`
in Task 7 is line 906 (step 5). Task 6's is line 799 (step 3), before its step 6.

**Proposed fix.** Add `source /home/ben/iverson-benchmark-data/bench-env.sh` to Task 7 before step 4 —
naturally in step 1's gate block alongside the fix for §2.1, so Task 7 establishes its own shell state
exactly once, the way Task 6 does.

---

## 3. Forced decisions

No forced decisions found.

---

## 4. Previously addressed

Round 1 (`docs/criticalreviews/2026-09-04-embedding-migration-implementation-plan-critical-review-1.md`,
committed `bf3ce58`) raised three §2 findings and one §1 evidence correction; all five edits in `d4c9718`
were checked against the plan's current text and each resolves its finding.

- **§2.1 (CS9007 on the `$$`-delimited `SuccessResponse`) — resolved.** Both occurrences now read
  `$$$"""…[{{{string.Join(",", embedding)}}}]…"""` (plan lines 189 and 428), leaving the JSON text
  byte-identical. Confirmed byte-for-byte identical to the scratch tree's applied form, where
  `dotnet test Iverson.Embeddings.Tests/Iverson.Embeddings.Tests.csproj` reports
  `Passed! - Failed: 0, Passed: 46, Skipped: 0, Total: 46`.
- **§2.2 (`grep -A 12` could only print 2) — resolved.** Plan line 729 now uses `grep -A 20` and states
  `# 4`, with the rendered-block geometry noted inline. Re-verified against the structurally identical
  `reranker`: `docker compose --profile reranker config | grep -A 20 '^  reranker:' | grep -c
  'cross-encoder/ms-marco-MiniLM-L-6-v2\|--auto-truncate\|8090\|reranker_models'` → `4`.
- **§2.3 (`config --volumes` prunes a profiled service's volume) — resolved.** Plan lines 733–734 are now
  the stronger pair the review offered: `# 0 — pruned with the profiled service` followed by the
  `--profile tei` form expecting `1`. Re-verified: `docker compose config --volumes | grep -c
  '^reranker_models$'` → `0`; `docker compose --profile reranker config --volumes | grep -c
  '^reranker_models$'` → `1`.
- **§1's P3 evidence correction — resolved.** P3 now states "→ 2 hits, both the private nested
  `InfoResponse` record in `Iverson.LoadTest/Benchmark/TeiRerankClient.cs:26,33`, a different assembly —
  no collision (CIR-1)". A fresh `grep -rn … --include=*.cs Iverson.Server` (bin/obj excluded) returns
  exactly those two lines.
- **The six covering rows (P29–P34) — added and all independently reconfirmed this round**, including
  P31's chunk counts (recomputed from the real corpora) and P32's baseline materialisation
  (`report.py:558`). See §1.

---

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes.** §1 has no failed assumption — all 34 reconfirmed, and every
span-check dependency was resolvable in-round (including the snapshot-restore question, settled from the
running Qdrant's own OpenAPI document rather than by a write). §2 carries two findings, both in Task 7 and
both of the same shape: Task 7 delegates its steps to "as Task 6 step N" but never establishes the shell
state those steps consume — `$K` (needed at step 3, first assigned at step 5) and the `bench-env.sh`
credentials (needed at step 4, first sourced at step 5) — so the NFCorpus arm's snapshot is not preserved
and its schema is never registered. §3 is empty. Both fixes are one line each in Task 7's step 1.
