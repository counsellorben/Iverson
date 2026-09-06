# Enrichment Backend Gate — TGI-1.5B vs Ollama-3B

Recorded 2026-09-06, from `.worktrees/embedding-migration-phase2` HEAD `553b3b2` (branch
`embedding-migration-phase2`), and amended at `c7352a1` (fix round) and `def62bb` (Phase C′ end
state). Corresponds to Task 8 of
`docs/plans/2026-09-05-embedding-migration-phase2-implementation-plan.md`; the gate rule is
`docs/specs/2026-09-05-embedding-migration-phase2-design.md` §6.4, and the branch it selects between
is §7's Phase C / Phase C′.

Run files live under `~/repositories/iverson-benchmark-corpora/enrichment-bench-2026-09-05/`:
- `run.log`, `results.json`, `side-by-side.md` — the original invocation (TGI, then Ollama). The TGI
  entry in `results.json` is the valid measurement. The `ollama` entry in this file is **invalid**
  (see "The first Ollama pass" below) and is kept, untouched, as the record of that defect.
- `ollama-rerun/run.log`, `ollama-rerun/results.json`, `ollama-rerun/side-by-side.md` — the corrected
  Ollama-only rerun, produced with TGI stopped. This is the Ollama measurement the gate uses.

## Method

The protocol is spec §6.2–§6.3: `Iverson.Server/Iverson.LoadTest/scripts/enrich_bench.py` builds 75
fixed prompts from the first documents of `scifact-run-2026-08-26/beir/corpus.jsonl` (50
`ChunkContext`, 10 `Summary`, 10 `Keywords`, 5 `Extraction`), the exact strings from
`EnrichmentPrompts.cs`, and runs them sequentially at temperature 0, `max_tokens 256`, through each
backend's OpenAI-compatible `/v1/chat/completions` route — the same route the ported
`EnrichmentService` uses. Per prompt it records wall seconds, completion tokens, tokens/s, whether the
output is empty, and for `Extraction` whether the reply parses under the service's own extraction rule
(first fenced block, else the first balanced `{...}` span). Per backend it records peak container RSS
(`docker stats --no-stream`, sampled every 5 s) and the box's minimum `MemAvailable`. Ben ruled
2026-09-05 (spec §6.1) that only the 1.5B TGI candidate is measured on this box; the 3B candidate is
deferred to a cloud AMX node and is not part of this gate.

This box is the i7-7500U (AVX2, no AVX-512/AMX, 4 threads, 9.9 GB RAM) documented in
`project-local-embedding-throughput.md` and `project-intel-igpu-unavailable-in-wsl2.md` as
sustained-load-throttled (15 W package limit). Both backends' measured throughput below runs well
under their isolated-probe numbers (spec §9 rows 11/14: TGI ~1.6 tok/s, Ollama-native ~6.9 tok/s) for
that reason — see "Box observations."

**Backend order:** TGI was measured **first**, Ollama second, per the script's own comment (P27):
Ollama keeps a model resident in memory for 5 minutes after its last request, which would otherwise
sit inside TGI's `MemAvailable` sampling window and confound gate criterion 5. Before the measurement,
`iverson-worker`, `iverson-starrocks`, `iverson-kafka`, `iverson-zookeeper` and `iverson-jaeger` were
stopped; only the query tier plus `tgi` ran during the TGI pass.

### The first Ollama pass is invalid — a run-ordering defect, not a measurement

The original invocation ran TGI to completion, then immediately started the Ollama backend **while
`iverson-tgi` was still up and holding its resident weights**. All 75 Ollama rows in
`enrichment-bench-2026-09-05/results.json` are `"output": "ERROR: HTTP Error 500: Internal Server
Error"` (`failed: 75`, `empty: 75`, `extraction_parse_ok: 0`). `iverson-ollama`'s container log for
that window (`docker logs --since 2026-09-06T05:50:00Z --until 2026-09-06T06:05:00Z iverson-ollama`)
shows the cause directly:

```
time=2026-09-06T06:02:02.707Z level=INFO source=sched.go:443 msg="system memory" total="9.7 GiB" free="1.4 GiB" free_swap="692.0 KiB"
time=2026-09-06T06:02:02.709Z level=INFO source=sched.go:470 msg="Load failed" model=/root/.ollama/models/blobs/sha256-5ee4f07... error="model requires more system memory (1.9 GiB) than is available (1.4 GiB)"
```

Ollama could not load `qwen2.5:3b` beside TGI's still-resident weights (TGI's own peak RSS on this
pass was 5.65 GB). This is a defect in the run's backend ordering, not evidence about Ollama's
performance. The controller stopped `iverson-tgi` (SIGKILL after a 10 s SIGTERM — TGI does not exit
cleanly on SIGTERM under load) between the two runs.

**Fix:** the Ollama backend was re-run in isolation, into `enrichment-bench-2026-09-05/ollama-rerun/`,
after confirming the box was calm (1-minute load average 3.28, `MemAvailable` 5.4 GB) and
`iverson-tgi` was not running. This is the Ollama measurement used below. The original `results.json`
and its invalid `ollama` entry are left in place, untouched, as the record.

### The 600 s client timeouts are a script artifact, not backend errors

Five of TGI's 75 rows are `failed` (`Summary` 0, 5, 6, 7; `Keywords` 7), all with `output: "ERROR:
timed out"` — the script's own `urllib` client gave up at its 600 s socket timeout
(`enrich_bench.py:102`), which is 5× `EnrichmentServiceOptions.Timeout`'s production default (2
minutes / 120 s, `EnrichmentServiceOptions.cs:9`). These are not TGI HTTP errors, and they say nothing
about the `Extraction` parse rule — the failures are all `Summary`/`Keywords` prompts, and all five
`Extraction` prompts in the same run succeeded and parsed (`extraction_parse_ok: 5`). The mechanism is
**decode length, not prompt size**: the 75 selected SciFact abstracts are 441–5,253 characters (mean
1,488) — none reaches the `text[:8000]` cap the script applies before building the prompt, and the
five timed-out sources themselves are 5,253 / 2,886 / 1,441 / 2,943 / 1,355 characters, two of them
below the set's own mean. At TGI's measured ~0.223 tok/s under this run's throttling, completed
neighbouring prompts that happened to generate long enough replies already sat at the edge of the 600 s
window: `Summary` 2 took 561.9 s for 101 tokens, `Summary` 8 took 572.3 s for 105 tokens, and
`Keywords` 3 took 589.95 s for 108 tokens — all under 600 s only because their completions stopped
short of `max_tokens 256`. Any generation whose decode ran longer toward that 256-token ceiling was
positioned to cross 600 s regardless of how long its source text was, which is what happened to the
five failed rows.

## Box observations

During the TGI pass, `vmstat` showed 24–47 % iowait and swap fully committed (4095/4096 MB used) from
roughly the run's midpoint onward, with `iverson-tgi` pinned near 103 % CPU while the box overall
measured ~36 % idle — the documented 15 W package-power throttling under sustained load. Per-prompt
TGI walls grew from ~150–200 s (prompts 0–2) to 250–530 s across the run as a result.

After the TGI+invalid-Ollama run, the prior implementer's `docker compose up -d` restore drove the box
into thrash (load average 74–102, `MemAvailable` < 0.9 GB) until the controller manually stopped
`iverson-tgi` and the out-of-tier services, leaving the query tier up overnight.

Before the Ollama rerun, the box was polled every 60 s until calm: 1-minute load average dropped from
8.27 → 3.28 and `MemAvailable` held at ~5.4 GB. One `free -m` / `vmstat 1 3` sample was taken during
the rerun itself:

```
free -m:               total 9946, used 5866, free 1187, buff/cache 3996, available 4079, swap used 3699/4096
vmstat (3 samples):     r/b 1-2/0-1, swpd ~3.79M KB, iowait 1-66%, si/so up to 80/66
```

Even in isolation, the Ollama rerun ran far below its isolated-probe throughput: mean tokens/s across
all 75 rows was **0.848 tok/s** (vs the spec §9 row-14 native-eval figure of 6.94 tok/s) — an ~8×
slowdown consistent with `project-local-embedding-throughput.md`'s "sustained load is ~20x worse than
idle projections" finding. TGI's mean was **0.223 tok/s** across its 70 successful rows. Both numbers
are recorded as measured; they are not used to adjust the gate's fixed thresholds.

## Results

### Per-backend summary

| Backend | ChunkContext mean (s) | Summary mean (s) | Keywords mean (s) | Extraction mean (s) | p95 (s) | mean tok/s | failed | empty | extraction parses | peak RSS | min MemAvailable |
|---|---|---|---|---|---|---|---|---|---|---|---|
| TGI (Qwen2.5-1.5B-Instruct, `results.json`) | 285.527 (n=50) | 490.369 (n=6/10) | 380.951 (n=9/10) | 344.839 (n=5/5) | 527.387 | 0.223 | 5 | 5 | 5/5 | 5.65 GB | 1.29 GB |
| Ollama (qwen2.5:3b, `ollama-rerun/results.json`) | 95.011 (n=50) | 113.611 (n=10) | 64.343 (n=10) | 72.269 (n=5) | 129.658 | 0.848 | 0 | 0 | 5/5 | 2.63 GB | 3.28 GB |

TGI's `Summary`/`Keywords` `n` counts are out of 10 each because 5 of those 20 rows (`Summary` 0, 5, 6,
7 and `Keywords` 7 — 4 + 1) are the 600 s client timeouts discussed above; their wall times are
excluded from the kind mean (the script only averages non-`None` walls).

TGI's `/info` at the start of its pass: `Qwen/Qwen2.5-1.5B-Instruct 3072 3584` (`model_id`,
`max_input_tokens`, `max_total_tokens`) — matching the compose command's `--max-input-tokens 3072
--max-total-tokens 3584`. First start: 22:27 → healthy ~22:56 box time (~11.5 min weight download +
~10 min warm-up, matching spec §9 row 10's ~10-minute figure at these limits).

`docker inspect -f '{{.State.OOMKilled}}'` on `iverson-tgi`, `iverson-ollama`, `iverson-api`,
`iverson-qdrant` (TGI pass) and `iverson-ollama`, `iverson-api`, `iverson-qdrant` (Ollama rerun): all
**false**. No container was OOM-killed in either pass.

## The five criteria (spec §6.4), applied mechanically

| # | Criterion | TGI value | Ollama value | Result |
|---|---|---|---|---|
| 1 | `tgi.ChunkContext.mean_wall_s ≤ ollama.ChunkContext.mean_wall_s` | 285.527 | 95.011 | **FAIL** — 285.527 is not ≤ 95.011 |
| 2 | `tgi.p95_wall_s < 120` | 527.387 | — | **FAIL** |
| 3 | `tgi.failed == 0 and tgi.empty == 0` | failed=5, empty=5 | — | **FAIL** |
| 4 | `tgi.extraction_parse_ok == 5` | 5 | — | **PASS** |
| 5 | `tgi.min_mem_available_bytes > 500e6` and no OOM kill | 1,294,192,640 B (1.29 GB); OOM=false | — | **PASS** (qualified — see "Box observations": iowait 24–47 % and swap fully committed at 4095/4096 MB were concurrent conditions during this same pass, per ruling R8) |

Three of five criteria fail. Per spec §6.4 ("all five" required to pass), the gate does not pass.

This was the expected outcome, stated in the spec before the run (§6.4, §11): "Ollama-3B measured 6.9
tok/s and TGI-1.5B ~1.6 tok/s in the design probes... criterion (1) is likely to fail on this box."
Criteria 2 and 3 also fail, driven by the same underlying cause — TGI's ~1.6 tok/s isolated throughput
degrading further under this box's sustained-load throttling, pushing every prompt's wall time past
both the p95 threshold and, for five `Summary`/`Keywords` prompts, the script's own 600 s client
timeout.

**No side-by-side quality read from Ben is recorded here.** Spec §6.4 makes the side-by-side table a
veto over a numeric pass — moot here since the numeric gate already fails, but noted per the required
document shape: `enrichment-bench-2026-09-05/side-by-side.md` and `ollama-rerun/side-by-side.md`
contain every prompt's output from both backends for that review if Ben wants it.

## Verdict: **GATE FAILED — Phase C′**

Per spec §7's Phase C′: Ollama stays for enrichment only. `charts/ollama` remains (its pull loop
reduced to `global.generativeModel`, an Ollama id, `qwen2.5:3b`); `Enrichment__BaseUrl` stays
`http://<release>-ollama:11434`; compose keeps `ollama` and an `ollama-init` that pulls only
`qwen2.5:3b`; `EnrichmentServiceOptions` defaults stay Ollama's. The `tgi` compose service added in
this task (and the Terraform `tei` pool addition from earlier tasks) land as written — they are not
reverted — but the `ollama → tgi` rename does not happen. The Phase B enrichment port (backend-neutral
`/v1/chat/completions` client, `MaxSourceChars` cap) works unchanged against Ollama (spec §9 row 14).

Per the task routing: this FAIL selects **Task 9′** (not Task 9/10) plus **Task 11**. Ben decides
separately whether to pursue the 3B candidate on a cloud AMX node (`c7i.2xlarge` or equivalent) — that
decision, and any cloud run, are **not** made or scheduled here.

Spec §8's Phase C kind-smoke assertions (tgi pod ready, worker enrichment logged under TGI) and §11's
`Errno 30` read-only-tokenizer-fallback check are Phase C-only checks; they are not performed on this
box, and are not applicable given this FAIL verdict — Task 11 (which runs regardless of the verdict)
names what performs the Phase C′-relevant checks instead.

## Box state at close

`iverson-worker` (which the pre-measurement step stopped) was restarted along with
`iverson-starrocks`, `iverson-kafka`, `iverson-zookeeper`, `iverson-jaeger`, `iverson-prometheus` via
`docker compose up -d` from the worktree's `Iverson.Server`. `iverson-tgi` came up too — it is now a
default compose service after this task's Step 1 — but the restore pushed the box back into a load
spike (1-minute load average 53.28, exceeding the 20 guard from this task's instructions), so
`iverson-tgi` was stopped again (`docker stop iverson-tgi`, SIGKILL after a 10 s SIGTERM timeout).
`MemAvailable` recovered to 4.19 GB immediately after.

Final container list (14, matching the pre-measurement set — `tgi` excluded by the guard):
`iverson-api`, `iverson-authentik-server`, `iverson-authentik-worker`, `iverson-jaeger`,
`iverson-kafka`, `iverson-ollama`, `iverson-postgres`, `iverson-prometheus`, `iverson-qdrant`,
`iverson-redis`, `iverson-starrocks`, `iverson-tei-embed`, `iverson-worker`, `iverson-zookeeper` — all
healthy or running. No Qdrant or Postgres data was touched; no `docker compose down`, no volume
removal, no `stack.py` invocation.

## End state (Phase C′)

Recorded 2026-09-06, Task 9′. Summarizes what Tasks 2–8 already landed (unchanged, not reverted by
this task), what this task additionally verified, and what a future cloud measurement of the deferred
3B candidate would need.

### What ships on this branch, unchanged

- The `ollama` compose service and its `ollama-init` sidecar remain, `ollama-init` pulling only
  `qwen2.5:3b`. The `ollama` Helm subchart remains, its pull loop reduced to
  `global.generativeModel` (an Ollama id, `qwen2.5:3b`).
- `Enrichment__BaseUrl` stays `http://ollama:11434` (compose) / `http://<release>-ollama:11434`
  (Helm); `EnrichmentServiceOptions` defaults stay Ollama's (`BaseUrl = "http://localhost:11434"`,
  `ModelId = "qwen2.5:3b"`).
- The `tgi` compose service (Qwen2.5-1.5B-Instruct) and the Terraform `tei` pool added by earlier
  tasks land as written and are not reverted; the `ollama → tgi` rename this branch would have made
  on a PASS does not happen.
- The Phase B enrichment port — the backend-neutral `/v1/chat/completions` client
  (`Iverson.Embeddings/EnrichmentService.cs`) and its `MaxSourceChars` cap — is unchanged.

### What Task 9′ verified: the ported client against Ollama, for real

Task 8's Ollama rows exercised the ported client via `enrich_bench.py`, which talks the same
`/v1/chat/completions` route directly — not through `iverson-worker`. Nothing on this branch had
rebuilt the `iverson-api`/`iverson-worker` image since Task 7's port, so the compose worker was still
running the pre-port `/api/generate` build. This task rebuilt and restarted it
(`docker compose build iverson-api && docker compose up -d iverson-worker iverson-api`), confirmed
both containers recreated on the new image
(`docker inspect … --format '{{.State.StartedAt}} {{.Image}}'` matched the freshly built image ID,
and `docker logs … | grep 'EmbeddingService initialized'` printed on both), and drove one real
enrichment through the full path: HTTP → gRPC → Kafka → `EnrichmentConsumer` →
`EnrichmentService` → Ollama.

The trigger was a throwaway `EnrichSmoke` type (`[IversonEmbedding] Body`, `[IversonSummary] Summary`,
`[IversonExtracted("the main finding")] Finding`) registered and written from a scratch console
(`/tmp/.../scratchpad/enrich-smoke/`, never committed), authenticated with the client-credentials
service identity (`dev-iverson-loadtest-client-id`, scopes `schema_admin tenant_id_loadtest`) plus an
acting-user token for the `iverson-loadtest-bypass-user` identity (group `iverson-loadtest-bypass`,
tenant `tenant_bypass`) — `EnrichSmoke` declares no `[IversonAuthorization]`-shaped rule of its own
(no such client attribute exists; row/field authorization is opt-in via
`SchemaRegistrar.RegisterAllAsync`'s `authorizationByTypeName` parameter), and
`RowFieldAuthorizationEvaluatorTests.Evaluate_NoAuthorizationRules_ReturnsDenied` pins that omitting
it denies every write regardless of tenant claim — so the scratch console registered `EnrichSmoke`
with a bypass `RowPermission` for that role, matching the identity above.

The first generation attempt (cold: Ollama had not yet loaded `qwen2.5:3b`) exceeded
`EnrichmentServiceOptions.Timeout`'s 120s default and failed client-side
(`TaskCanceledException`/`HttpClient.Timeout`) — consistent with this run's box being under load
(a concurrent `docker compose build iverson-api` had just finished, and `iverson-postgres` briefly
entered crash recovery mid-run: `database system was not properly shut down; automatic recovery in
progress`, self-resolved in under 6 seconds) and with the gate's own measured Ollama summary mean of
113.611s under calmer conditions. `EnrichmentConsumer`'s failure path is best-effort (no state row
written, no retry scheduled), so the row was retried once via `UpdateMappedAsync` with unchanged
source text — the model was warm this time, and both generations succeeded:

```
info: System.Net.Http.HttpClient.iverson.enrichment.ClientHandler[101]
      Received HTTP response headers after 21223.9385ms - 200
info: Iverson.Api.Consumers.EnrichmentConsumer[0]
      [Enrichment] Enriched 2 column(s) for EnrichSmoke:01a0760b-84fe-7c0d-bdec-a8c3548d9b6c
```

Ollama's own access log for the same window (`docker logs iverson-ollama`):

```
[GIN] 2026/09/06 - 09:34:44 | 200 | 21.152745675s |      10.89.0.84 | POST     "/v1/chat/completions"
[GIN] 2026/09/06 - 09:36:09 | 200 |         1m25s |      10.89.0.84 | POST     "/v1/chat/completions"
```

The stored row (`SELECT "Summary", "Finding" FROM enrich_smokes WHERE "Id" = …`) carried real
generated text, not an error string or an empty value: a one-sentence `Summary` and a `Finding`
JSON object with a `mainFinding` key, both on-topic for the smoke's source text, under
`__TenantId = 'tenant_bypass'`. The type's schema row and Qdrant collections were cleaned up
afterwards exactly as Task 10 Step 3 specifies (`DELETE FROM public._iverson_schema WHERE type_name =
'EnrichSmoke'`, and a scan for `enrich_smoke*` Qdrant collections — none existed, for the unrelated
reason below).

**Observation, not fixed (out of scope for this task):** every `entity.created`/`entity.updated`
event for the `EnrichSmoke` key also dispatched to `IntelligenceStoreConsumer` (group
`iverson.consumer.intelligence`, a different consumer than `EnrichmentConsumer`), which threw
`IndexOutOfRangeException` in `ExtractString`/`FetchAuthoritativeOwnerValueAsync`, exhausted its 3
dispatch attempts, and routed the message to the DLQ. This is why no `enrich_smoke*` Qdrant
collection was ever created — `EnrichSmoke`'s `[IversonEmbedding] Body` never made it into Qdrant —
and is unrelated to enrichment or to this task's Ollama-vs-TGI question; it reproduces with any
throwaway type this shape registers via a bypass `RowPermission` and no `OwnerField`. Left for a
separate investigation.

### What a future cloud measurement of the 3B candidate would need

Deferred by Ben's 2026-09-05 ruling (spec §6.1); not scheduled here. If pursued:

- `Iverson.Server/Iverson.LoadTest/scripts/enrich_bench.py` is reusable as-is — it already accepts
  an arbitrary `--backend name=base_url=model=container` for the run, and its script header
  documents the exact invocation shape.
- The `tgi` compose service definition (`Iverson.Server/docker-compose.yml`) is reusable as-is: swap
  its `--model-id` to `Qwen/Qwen2.5-3B-Instruct` and size `--max-input-tokens`/`--max-total-tokens`
  for the larger model, or run a second TGI instance alongside it.
- The measurement itself: `enrich_bench.py --backend
  ollama=http://localhost:11434=qwen2.5:3b=iverson-ollama --backend
  tgi=http://localhost:8092=Qwen/Qwen2.5-3B-Instruct=iverson-tgi` on an AMX-capable cloud node —
  `c7i.2xlarge` or equivalent (matching `deploy/terraform/modules/cluster-aws/variables.tf`'s
  `ollama_instance_type` default, sized for the 3B model's memory footprint; `tei_instance_type`
  defaults to the smaller `c7i.xlarge`) — where this box's documented 15W sustained-load throttling
  (`project-local-embedding-throughput.md`, `project-intel-igpu-unavailable-in-wsl2.md`) does not
  apply and the spec §9 isolated-probe throughput figures (TGI ~1.6 tok/s, Ollama-native ~6.9 tok/s)
  are more likely to hold.
