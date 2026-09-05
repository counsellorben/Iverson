# Critical Design Review: 2026-09-05-embedding-migration-phase2-design (Round 1)

**Spec:** docs/specs/2026-09-05-embedding-migration-phase2-design.md
**Spec (absolute):** `/home/ben/repositories/Iverson/docs/specs/2026-09-05-embedding-migration-phase2-design.md`
**Verified Assumptions section:** present (§10, T1–T6, K1–K4, H1–H7, F1–F4, C1–C4, S1–S6, P1–P2, Q1–Q2, M1–M3, D1–D3)

Repo state: `main` @ `ece3af7`, clean working tree. Every `file:line` below is a read at that commit;
every command is quoted with its output. No `docker compose` command was run; no `iverson-*` container
was started, stopped or recreated. Live probes were: read-only `curl` POSTs against the already-running
`iverson-ollama` (two inference calls), `docker stats --no-stream`, `free`/`/proc/meminfo`, and two
read-only `SELECT`s through `docker exec iverson-postgres psql` (no writes). Two throwaway
`--rm --name cdr-tgi-probe` containers were run from the already-local
`ghcr.io/huggingface/text-generation-inference:3.3.4-intel-cpu` image with **no ports and no volumes**,
purely to `strings` the `text-generation-router` binary; both exited and `docker ps -a | grep cdr-` is
empty. No `cdr_tgi_cache` volume was ever created. No TEI or TGI server was started — the TGI-runtime
questions were settled against the v3.3.4 router source and the shipped binary's own strings instead,
which is cheaper and equally decisive. Nothing under `~/repositories/iverson-benchmark-corpora/` was
touched.

---

## 0. Coverage enumeration

### Sections

| Section | Disposition |
|---|---|
| §1 Why | → §2.2 — the load-bearing sentence "There are no registered types on any deployment, so no migration procedure exists in this spec" is false on the compose deployment §8 tests against (36 rows in `public._iverson_schema`, all pinned to `nomic-embed-text`). |
| §2 Scope | ok — checked the "Out" list against the design body: `bge-small` appears nowhere else; `EmbeddingPrefixes.Table` is in §5's Untouched row; the 3B exclusion is re-derived under §1's T6 note and survives. |
| §3.1 Helm values | → §2.3 — the local/laptop `global.embeddingModels` overrides are not in §3.1's list of profile changes; `grep -c` shows neither file carries `activeEmbeddingModel`. |
| §3.2 `tei` subchart | → §2.1 — headless Service + `port: 80 → targetPort: 8080` against a container on 8080. Rest checked: `--auto-truncate` (Phase 1 §3.4), `HUGGINGFACE_HUB_CACHE=/data` (§9 row 5), `range .Values.global.embeddingModels` inside a subchart is the shape `charts/ollama/templates/statefulset.yaml:59-61` already uses, so subchart access to `global` is proven. |
| §3.3 `tgi` subchart | → §2.1 (same Service defect). `condition: global.enrichmentEnabled` checked: `Chart.yaml:14-17` already uses `condition: global.engagementEnabled` for starrocks, so a `global.`-prefixed condition path is proven to resolve in this chart. `--dtype bfloat16`, `--max-input-tokens 3072`/`--max-total-tokens 3584` checked against the router's own `` `max_input_tokens` must be < `max_total_tokens` `` validation string and the 3072+256 ≤ 3584 arithmetic — holds. |
| §3.4 Helpers, deployments, policies | ok — `charts/api/templates/deployment.yaml:124-130` and `charts/worker/…:119-125` carry exactly the three env blocks the section replaces; `_helpers.tpl:22-34` is the include site. NetworkPolicy pod-port choice (8080) checked below. `helm dependency build` / stale-tgz trap matches `charts/ollama-0.1.0.tgz` on disk. |
| §3.5 Server code | → §2.5, §2.6. Also checked: no `appsettings*.json` under `Iverson.Api/` sets `Embeddings:*` or `Enrichment:*` (`grep` returns nothing), so the option defaults really are the `dotnet run` values; `ServiceCollectionExtensions.cs:18,36` set `BaseAddress` from `BaseUrl`, so the absolute-URI pattern is compatible; every wording site in the list reads as claimed (spot-checked `SchemaRegistrationOrchestrator.cs:128`, `Program.cs:410-412`, `EnrichmentConsumer.cs:16`, `EmbeddingService.cs:66,112`, `EmbeddingPrefixes.cs:16`). |
| §3.6 Launcher | ok — `Iverson.Launcher/Program.cs:20` is the `up` list, `:28,72-103` is `WaitForOllamaAsync`; the new 8091/8092 waits line up with the compose port maps and with `EmbeddingServiceOptions`/`EnrichmentServiceOptions` defaults. |
| §3.7 Compose | ok — every cited line is what the spec says it is: `docker-compose.yml:124` `ollama:`, `:138` `ollama-init:`, `:171` `tei-embed:`, `:387` `iverson-api:`, `:473`/`:545` the two `ollama-init:` `depends_on` entries, `:483` `iverson-worker:`, `:571` `ollama_data:`. `tei-embed` already maps `8091:80` and `tgi`'s `8092:80` matches the image's `PORT=80` (§9 row 3). `start_period: 25m` is a valid compose healthcheck duration. |
| §3.8 Scripts and kind | ok — `ingest.py:534` posts `{embed_url}/v1/embeddings`, which is the TEI route, so repointing `--embed-url` at 8091 is coherent; `stack.py:82-84` `TIERS` / `:88-95` `CONTAINER` / `:169-173` `READY_CHECKS` are the three tables the change edits; `deploy/kind/setup.sh:98` is the storageSize note. |
| §3.9 Terraform | → §2.4 — the "pool key is the label" rule is false for Azure. Rest checked: `operators/main.tf:175-182` and `outputs.tf:7` are the two storage-class sites, and `grep -rn storage_class_names` shows the map is only re-exported by `aws|azure|gcp/main.tf:77`, never indexed by key anywhere in-repo, so renaming the key breaks no in-repo consumer. |
| §3.10 Conformance fixtures | ok — every fixture site named exists and holds `nomic-embed-text` (`Go/conformance/models.go:208,218`, `Java/…/S11ModelJava.java:30`, `S12DeclaredJava.java:11`, `Python/conformance/models.py:202,231`, `TypeScript/conformance/models.ts:303,327`, `InheritedModelScenario.cs:63`); `ModelRejectedScenario.cs:84` `OverrideModelId` is the never-deployed id. A repo-wide grep for other model declarations in the five conformance trees turns up only comments. |
| §4 Failure semantics | → §2.5, §2.6 — "A prompt over 8,000 characters is cut, never rejected" and the JSON-parse branch are both wrong about what actually happens. |
| §5 Component contract | ok — every row maps to a file that exists; the "Untouched" row checked against §3 (nothing in §3 edits `IEmbeddingService`, `EmbeddingPrefixes.Table`, `report.py`). |
| §6.1 Candidates | ok, with the §1 T6 correction — the 3B "no" survives re-derivation from the corrected `MemAvailable` (see §1). |
| §6.2 `enrich_bench.py` | ok — `EnrichmentPrompts.cs` exists with exactly the four prompt constants named (`Summary`, `Keywords`, `Extraction`, `ChunkContext`, the last with the two `{0}`/`{1}` slots the script needs); `usage.completion_tokens` is obtainable from **both** backends (measured live on Ollama, present in the TGI router's `Usage` struct); `docker stats --no-stream` returns a `MemUsage` column under this podman-backed `docker`. |
| §6.3 Ordering of the measurement | ok — checked that `stack.py`'s "stop every non-tier `iverson-*` container" behaviour does not make the §6.4-(5) condition unreachable: it stops containers once, at invocation, so `docker compose up -d --no-deps tgi`/`ollama` afterwards still produces the stated state. |
| §6.4 Gate | ok — 50+10+10+5 = 75 matches "p95 wall over all 75 prompts"; 120 s matches `EnrichmentServiceOptions.Timeout` (`EnrichmentServiceOptions.cs:9`, `TimeSpan.FromMinutes(2)`); criterion (5)'s 500 MB floor is comfortably satisfiable on the corrected `MemAvailable` (see §1). |
| §7 Ordering / two end states | → §2.2 (Phase A does not leave the live compose stack working) and §2.5 (Phase B lands the JSON port while Ollama is still the backend). |
| §8 Testing | → §2.2, §2.3. Also checked: the three `deploy-validate.yml` jobs are as H5 says (`:25-35`, `:52-63`, `:80-97`), and kube-score is invoked with three `--ignore-test` flags and no `--exit-one-on-warning`, so a missing livenessProbe on the new StatefulSets does not fail CI. |
| §9 Measurements | one row corrected (row 1's "2 GB available") — see §1; rows 4, 11, 12, 14 re-checked against the router binary and a live Ollama call. |
| §10 Verified assumptions | see §1 — 4 items failed. |
| §11 Known issues | ok — each of the four is genuinely scoped out and none is load-bearing for §3's mechanics. |

### Rules and operands

| Rule | Disposition |
|---|---|
| `iverson.activeEmbeddingModel`: match `global.activeEmbeddingModel` against each entry's `.name` (`_helpers.tpl:8-13`) | → §2.3 — under-match: on local/laptop the active name (inherited `BAAI/bge-base-en-v1.5`) matches no entry in the profile's own list. Over-match direction checked: names are exact-equality, one entry, no collision. |
| `<release>-tei-<slug>` name construction (§3.2) | → §2.3 — under-specified operand: the `slug` key exists only on values.yaml's entry; the two profile overrides carry no `slug`, so the rendered name is `iverson-tei-`. Over-inclusion direction (two entries colliding on one slug) checked: only one entry anywhere. |
| Service `port`/`targetPort` mapping (§3.2, §3.3) vs the URL the client dials (§3.4, §3.7) | → §2.1 — both operands checked: pod listens on 8080 (K2/§9 row 6), client dials `:80`, Service is headless. |
| NetworkPolicy port selection: pod-selector rules on TCP 8080 (§3.4) | ok — with a headless Service there is no DNAT at all, and with the §2.1 ClusterIP fix Calico applies egress policy after the nat-table DNAT, so the backend pod port 8080 is the correct operand in both directions. The existing rules give no counter-example (`networkpolicies.yaml:54,85` use 11434, which is both service and container port for ollama). |
| Chart.yaml `condition:` path resolution for `global.enrichmentEnabled` + the `tgi.enabled` second guard | ok — `Chart.yaml:14-17` proves `condition: global.engagementEnabled` resolves; the second guard is additive and cannot re-enable a chart the condition disabled, so neither direction misfires. |
| `MaxPromptChars = 8_000` applied to the assembled prompt (§3.5) | → §2.6 — both operands examined: `EnrichmentPrompts.Summary`/`.Keywords`/`.Extraction` put the instruction at the head, `.ChunkContext` and the consumer's appended hint put it at the tail. |
| Fence strip: leading ```` ``` ````/```` ```json ````, trailing fence, then `JsonDocument.Parse` (§3.5) | → §2.5 — under-inclusion measured on the real second backend: Ollama wraps the fenced object in prose on both sides. |
| `SchemaRegistrationOrchestrator` model-equality guard (`prior != next` → `FailedPrecondition`) | → §2.2 — this is the eligibility predicate the design's default switch runs into; enumerated its producers by reading the live `_iverson_schema` rather than trusting the spec's "no registered types". |
| Terraform pool key → `iverson.io/node-pool` label (§3.9) | → §2.4 — checked all three producers of that label, not just the two the spec's F1 cites: `cluster-aws/main.tf:572` `each.key`, `cluster-gcp/main.tf:266` `each.key`, `cluster-azure/main.tf:221,225` `each.value.label`. |
| `EmbeddingServiceOptions.BaseUrlFor(modelId)` fallback (`EmbeddingServiceOptions.cs:19-21`) | ok — with `Models__N` emitted for every entry the fallback is only reached for a model absent from the list; `iverson.embeddingBaseUrl` pointing at the *first* entry rather than the *active* one is therefore unreachable-by-construction on a well-formed values file. (On a malformed one it is reached — that is §2.3, not a second finding.) |
| TGI request validation: `temperature: 0`, `max_tokens: 256` | ok, and a candidate dropped — v3.3.4's `ChatRequest` conversion maps `Some(0.0) => (false, None)`, i.e. greedy, so the `` `temperature` must be strictly positive `` validator (present in the shipped binary's strings) is never reached. |

### Data-flow arrows

| Arrow (consuming operation) | Disposition |
|---|---|
| `values.yaml global.embeddingModels[i]` → `charts/tei` templates → StatefulSet/Service **name** | → §2.3 — the operation needs `.slug`, which the profile overlays do not supply. |
| `_helpers.tpl iverson.embeddingBaseUrl` → api/worker `Embeddings__BaseUrl` → `HttpClient` **connect** | → §2.1 — the operation is a TCP connect to `<pod-ip>:80`; nothing listens there. |
| `_helpers.tpl Models__<i>__BaseUrl` → `EmbeddingServiceOptions.Models` → `BaseUrlFor` → **connect** | → §2.1 (same port defect); binding shape checked against `ModelEndpoint` (`EmbeddingServiceOptions.cs:24-28`). |
| compose `tgi` healthcheck → `depends_on: service_healthy` on worker → **worker start** | ok — `start_period: 25m` is honoured before the first failure counts; §4 states the wait. |
| `EnrichmentPrompts.Extraction` + hint (`EnrichmentConsumer.cs:274-276`) → 8,000-char cut → **HTTP body** | → §2.6 — the required operand (the hint) is at the tail. |
| Backend response → fence strip → `JsonDocument.Parse` → **`Extracted` column write** | → §2.5 — checked at *both* call sites of the generative operation, not one: `EnrichmentConsumer.cs:274` (`GenerateJsonAsync`) and `IntelligenceStoreConsumer.cs:636` (`GenerateAsync`, ChunkContext). Only the first parses. |
| **Persistence boundary:** registration writes `_iverson_schema.schema_json` → `SchemaRegistry.Get` reads it back → model-equality guard | → §2.2 — dumped a real persisted row rather than reasoning from the in-memory descriptor: `chunkFields[0].modelId` / `vectorFields[0].modelId` are `"nomic-embed-text"` on all 36 rows. |
| `enrich_bench.py` → `/v1/chat/completions` (Ollama **and** TGI) → `usage.completion_tokens` | ok — one row per backend. Ollama measured live (`{'prompt_tokens': 82, 'completion_tokens': 92, 'total_tokens': 174}`); TGI's `Usage` struct with `prompt_tokens`/`completion_tokens`/`total_tokens` is present in `text-generation-router`'s strings. |
| `enrich_bench.py` → `docker stats --no-stream` → peak RSS | ok — ran it: `iverson-ollama 109.5MB / 10.43GB`, so container memory is sampled under this podman-backed `docker`. |

### Dropped candidates

- `{{ .Release.Name }}-tei-{{ .slug }}` inside a `range` needs `$.Release.Name` — Helm template mechanics, an implementation detail a plan review owns. Dropped.
- TEI's startupProbe budget (30 × 10 s = 5 min) covering a cold 0.44 GB Hub download — no evidence it is insufficient; speculation. Dropped.
- 8,000 characters exceeding 3,072 tokens for token-dense input (JSON-ish values via `ExtractString`'s `v.ToString()`) — the failure is loud (`HttpRequestException`, §4) and the ratio claim holds for prose. Dropped.
- `stack.py query` stopping `iverson-ollama`/`iverson-tgi` before the §6.4-(5) measurement — recoverable with one `docker compose up -d --no-deps`, not a break. Dropped.

---

## 1. Verified-assumptions cross-check

**Still hold (37):** T1, T2, T3, T4, T5 (§9 rows 3–5, 8–13 re-read; the TGI-runtime claims corroborated
against the shipped `text-generation-router` binary, which carries the `Usage`/`ChatCompletion` structs,
the `Grammar must have a 'properties' field` validation string, and the `` `max_input_tokens` must be <
`max_total_tokens` `` check the §3.3 args satisfy); K1, K2, K3, K4; H1, H2, H3, H4, H5, H7; F2, F3, F4
(`grep -rn storage_class_names` confirms the map is re-exported by `aws|azure|gcp/main.tf:77` and never
indexed by key, so the `ollama` key rename breaks no in-repo consumer); C1, C2, C3, C4; S1, S2, S3, S4,
S5, S6; P1, P2; Q1, Q2; M1, M2, M3; D1, D2.

**Failed (4):**

**T6** — "1.5B bf16 fits beside the query tier only barely | §9 rows 1, 10". The *assumption* holds; its
*evidence* does not reproduce. §9 row 1 says "9.9 GB RAM (2 GB available with the query tier up)". With
all six stack containers running:

```
$ free -m
               total        used        free      shared  buff/cache   available
Mem:            9946        2709        2137         110        5410        7237
$ grep -E 'MemTotal|MemAvailable' /proc/meminfo
MemTotal:       10185320 kB
MemAvailable:    7410632 kB
```

2,137 MB is the **free** column; `MemAvailable` is **7,237 MB**. Against §9 row 10's 4.24 GB RSS at the
chosen limits, the 1.5B candidate has ~3 GB of headroom, not "barely" — which makes gate criterion (5)
(`MemAvailable` > 500 MB) comfortably satisfiable rather than marginal. The dependent conclusion in §6.1
and §2 ("Qwen2.5-3B … no — 9.9 GB RAM, 2 GB free with the stack up") was re-derived on the corrected
number and **survives**: scaling §9 row 10's 1.15 GB of non-weight overhead onto 3B's ~6.2 GB of weights
gives ~7.4 GB RSS against 7.24 GB available. Fix: correct row 1 to `MemAvailable ≈ 7.2 GB` and restate
the 3B exclusion on the RSS estimate rather than on "2 GB free".

**H6** — "Six values files: … `values.yaml`, `-local`, `-laptop` carry `embeddingModels`/`activeEmbeddingModel`".
The `activeEmbeddingModel` half is false:

```
$ for f in values.yaml values-local.yaml values-laptop.yaml; do echo "$f: embeddingModels=$(grep -c 'embeddingModels:' $f) activeEmbeddingModel=$(grep -c 'activeEmbeddingModel:' $f)"; done
values.yaml: embeddingModels=1 activeEmbeddingModel=1
values-local.yaml: embeddingModels=1 activeEmbeddingModel=0
values-laptop.yaml: embeddingModels=1 activeEmbeddingModel=0
```

`values-local.yaml:21-22` and `values-laptop.yaml:25-26` override the list only; the active name is
inherited from `values.yaml:54`. That inheritance is what turns §2.3 from a cosmetic omission into a
render defect.

**F1** — "label `iverson.io/node-pool` = pool key (`aws:572`, `gcp:266`, `azure:221`)". True for AWS and
GCP, false for Azure. `cluster-azure/main.tf:203-208` gives every pool a separate `label` attribute, and
`:221-226` reads it, not the key:

```hcl
    ollama      = { vm_size = var.ollama_vm_size, count = var.ollama_node_count, label = "ollama" }
...
  node_labels = {
    "iverson.io/node-pool" = each.value.label
  }

  node_taints = [
    "iverson.io/node-pool=${each.value.label}:NoSchedule"
  ]
```

The key and the label diverge for `starrocksfe`/`starrocks-fe` already, so this is a real second operand,
not an incidental duplicate. Feeds §2.4.

**D3** — "48 files reference Ollama outside `docs/`; every one is in §3.5, §3.7, §3.8, §3.9, §3.10 or the
deleted subchart, except `Iverson.Server/docs/security/tma.md` and `README.md`". `git ls-files | grep -v
'^docs/' | xargs grep -lil ollama` returns **53** tracked files, and four of them are named by no section
of the spec and are not the two acknowledged exceptions:

```
Iverson.Clients/Java/client/src/main/java/io/iverson/client/annotations/IversonSummary.java:9:  * Marks a string field as the target for an Ollama-generated summary during
Iverson.Clients/Java/client/src/main/java/io/iverson/client/annotations/IversonKeywords.java:9: * Marks a string field as the target for Ollama-generated keywords during
Iverson.Clients/Java/client/src/main/java/io/iverson/client/annotations/IversonExtracted.java:9:  * Marks a field as the target for an Ollama-driven extraction during ingest
Iverson.Clients/TypeScript/src/annotations.ts:71:  * Declares the Ollama model this type's embedding/chunk properties are generated with.
```

§3.5's wording list names `Iverson.Clients/Python/iverson_client/annotations.py` but not its Java and
TypeScript counterparts. These are doc comments, so nothing breaks — but the enumeration D3 asserts is
incomplete, and the fix is to add the four files to §3.5's wording list (or extend the acknowledged
documentation-pass exception to cover them).

**Span check — dependencies with no covering assumption:**

- **"There are no registered types on any deployment" (§1).** Load-bearing: it is the stated reason no
  migration procedure exists and the reason §3.1 may delete the `embeddingModels` invariant comment. No
  §10 item covers it. Verified in-round and **false** for the deployment §6.3 and §8 use → §2.2.
- **Ollama's behaviour on `/v1/chat/completions` with no format directive.** Load-bearing for §3.5's
  "this port is backend-neutral: the fail branch of §7 keeps it". §9 row 12 measured TGI only; row 14
  measured only that `choices[0].message.content` exists. Verified in-round → §2.5.
- **Ollama's prompt-truncation direction.** Load-bearing for §3.5's "Parity, not a regression". No §10
  item covers it. Verified in-round → §2.6.
- **Kubernetes headless-Service port semantics.** Load-bearing for §3.2/§3.3's Service shape and §3.4's
  env URLs. No §10 item covers it. Verified in-round → §2.1.

---

## 2. Literal-wrongness findings

### 2.1 The `tei`/`tgi` Services are headless, so `port: 80 → targetPort: 8080` never happens and api/worker connect to a dead port

**Description.** §3.2 specifies "a headless Service `<release>-tei-<slug>` with `port: 80`,
`targetPort: 8080`" and §3.3 "a headless Service `<release>-tgi` (`80 → 8080`)". §3.4 then hands
api/worker `Embeddings__BaseUrl: http://<release>-tei-<slug>:80`, `Embeddings__Models__<i>__BaseUrl:
http://<release>-tei-<slug>:80` and `Enrichment__BaseUrl: http://<release>-tgi:80`. A headless Service
performs no port remapping: `targetPort` is only consulted by kube-proxy (and by named-port SRV
records), and kube-proxy does not handle headless Services at all. DNS hands the client a **Pod IP**,
the client dials that Pod on **port 80**, and TEI/TGI are listening on **8080** — because §9 row 6 /
K2 established they cannot bind 80 as uid 1000. Every embed and every enrichment call in the chart
deployment gets connection-refused.

**Evidence.**
- Spec §3.2 / §3.3 / §3.4 as quoted.
- The shape the subchart mirrors is headless: `Iverson.Server/deploy/helm/iverson/charts/ollama/templates/service.yaml:5-10`
  — `spec: clusterIP: None`. Ollama survived this because its Service port and container port are the
  same number (11434), so no remapping was needed.
- Kubernetes documentation, <https://kubernetes.io/docs/concepts/services-networking/service/#headless-services>:
  "**No kube-proxy handling**: kube-proxy does not handle headless Services, and there is no
  load-balancing or proxying done by the platform. … **DNS returns Pod IPs**: For a headless Service
  with selectors, DNS queries return the individual Pod IP addresses instead of a single Service IP."
- The pod really is on 8080: §9 row 6, "port 80: `Could not bind TCP Listener … Permission denied`;
  `--port 8080`: healthy in 45 s".
- The spec's own NetworkPolicy choice corroborates that 8080 is the wire port it expects
  (§3.4: "tei-ingress (from api and worker, TCP 8080 …)"), which is inconsistent with dialling `:80`.

**Proposed fix.** Pick one and state it: (a) drop `clusterIP: None` from the `tei` and `tgi` Services so
kube-proxy actually applies `80 → 8080` (a ClusterIP Service is what these two want anyway — neither is
a peer-addressed StatefulSet like Qdrant's Raft members; the StatefulSet's `serviceName` can keep
pointing at it), or (b) keep them headless, set `port: 8080` with no `targetPort`, and make every URL
in §3.4 and the `iverson.embeddingBaseUrl` helper `:8080`. Option (a) leaves the NetworkPolicy ports
in §3.4 correct as written; option (b) does too.

### 2.2 §1's "no registered types on any deployment" is false on the compose stack, so Phase A breaks the very tests §8 runs on it

**Description.** §1 states "There are no registered types on any deployment, so no migration procedure
exists in this spec", and §3.1 deletes the "must contain every model any registered type names" comment
on that basis. The compose deployment — the one §6.3 runs the enrichment measurement against and §8
runs the conformance harness and the Phase 1 benchmark harness against — carries **36** `_iverson_schema`
rows, and every field of every one of them is pinned to `nomic-embed-text`. Phase A switches
`Embeddings__ModelId` to `BAAI/bge-base-en-v1.5`, repoints `Embeddings__BaseUrl` at `tei-embed`, and
removes the `Embeddings__Models__0__*` pair. Two things then break, both loudly and both inside §8's own
test list:

1. Re-registering any of those types (which is exactly what §8's "conformance harness (five drivers)
   against compose after Phase A" does, with fixtures §3.10 has just changed to declare bge-base) hits
   `SchemaRegistrationOrchestrator`'s model-equality guard and fails with `FailedPrecondition`.
2. Any type still resolving to `nomic-embed-text` now resolves its backend through
   `BaseUrlFor("nomic-embed-text")`, which — with `Models` emptied — falls back to `Embeddings__BaseUrl`
   = `http://tei-embed:80`, whose `/info` reports `BAAI/bge-base-en-v1.5`. `VerifyServedModelAsync`
   throws on first initialisation, surfaced as `Unavailable`.

**Evidence.**

```
$ docker exec iverson-postgres psql -U iverson -d iverson -Atc "select count(*) from public._iverson_schema;"
36
$ docker exec iverson-postgres psql -U iverson -d iverson -Atc "select type_name, schema_json from public._iverson_schema where type_name in ('S11ModelDotnet','BenchmarkDocument');"
S11ModelDotnet|{... "chunkFields": [{"modelId": "nomic-embed-text", ... "propertyName": "Body"}], ... "vectorFields": [{"modelId": "nomic-embed-text", "dimension": 768, "propertyName": "Title"}] ...}
BenchmarkDocument|{... "chunkFields": [{"modelId": "nomic-embed-text", ...}], "vectorFields": [{"modelId": "nomic-embed-text", "dimension": 768, "propertyName": "Body"}] ...}
```

The 36 rows include all five `S11Model*` and all five `S12Inherited*` conformance fixtures and
`BenchmarkDocument`.

- The guard: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:105-129` —
  `if (priorModel is not null && nextModel is not null && !string.Equals(priorModel, nextModel, …))
  throw new RpcException(new Status(StatusCode.FailedPrecondition, …))`, whose message is "To change it,
  BOTH clear the schema row and drop the collections: `DELETE FROM _iverson_schema WHERE type_name =
  '…'`; then, for every tenant that has ingested '…', drop Qdrant collections …".
- The identity check: `Iverson.Server/Iverson.Embeddings/EmbeddingService.cs:67-83` `VerifyServedModelAsync`
  throws `InvalidOperationException` when `/info`'s `model_id` differs from the configured `ModelId`.
- The fallback that routes nomic-pinned types at the bge-base container:
  `Iverson.Server/Iverson.Embeddings/EmbeddingServiceOptions.cs:19-21`
  (`Models.FirstOrDefault(…)?.BaseUrl ?? BaseUrl`), combined with §3.7's removal of
  `Embeddings__Models__0__*`.
- The conformance harness does not clear the table: `Iverson.ClientConformance/SchemaProbe.cs:17` reads
  `_iverson_schema`; nothing under `Iverson.ClientConformance/` issues a `DELETE`.
- Corroboration that this is the known procedure and not a novelty: the Phase 1 gate document,
  `docs/plans/2026-09-GATE-embedding-migration.md`, "Method": "a Qdrant snapshot, **a schema-row clear**
  so `BenchmarkDocument` re-registers under the candidate model, an API recreate".

**Proposed fix.** Two edits. (1) Scope §1's sentence to what it is true of — no *cloud/Helm* deployment
exists (§3.9 already says "none is running") — and state that the compose dev stack does hold registered
types. (2) Add to §7 Phase A, as a step before the api/worker env switch, the guard's own remedy on the
compose stack: `DELETE FROM public._iverson_schema` for the affected types and drop their
tenant-qualified Qdrant collections (`{base}_<tenantId>`, `{base}_chunks_<tenantId>`), and say so in §8
next to "the conformance harness … after Phase A" and "the restored SciFact index (now registered under
bge-base by default)" — the latter phrase already presumes this step without naming it.

### 2.3 `values-local.yaml` / `values-laptop.yaml` still override `embeddingModels` with `nomic-embed-text` and no `slug`

**Description.** §3.1 enumerates what the two small profiles change — `tei.replicas: 1`, `tgi.replicas: 1`,
`storageClassName: standard`, laptop's `global.enrichmentEnabled: false`, smaller `tei.resources` — and
does not include their `global.embeddingModels` override. A profile list override **replaces**, as both
files say in their own comments. Left as-is, on the two profiles the kind smoke and the laptop deploy
use:

- the `tei` templates range over `[{name: nomic-embed-text}]`, so the StatefulSet/Service/PDB render as
  `iverson-tei-` (empty `slug`) — a name ending in `-`, which is not a valid RFC 1123 label and is
  rejected by the API server on apply. `kubeconform` will not catch it (`metadata.name` is a bare string
  in the schema), so CI stays green and the kind smoke is where it fails;
- `iverson.activeEmbeddingModel` resolves nothing, because those two files carry no
  `activeEmbeddingModel` of their own (see §1/H6) and inherit `BAAI/bge-base-en-v1.5` from `values.yaml`
  while their only entry is `nomic-embed-text`;
- `iverson.embeddingBaseUrl` renders `http://iverson-tei-:80`;
- §8's kind smoke assertion — "API log `EmbeddingService initialized: model=BAAI/bge-base-en-v1.5
  dimension=768`" — cannot be produced by a profile whose only served model is nomic.

**Evidence.**
- `Iverson.Server/deploy/helm/iverson/values-local.yaml:21-22` and `values-laptop.yaml:25-26`:
  `embeddingModels:` / `- name: nomic-embed-text`.
- Both files state the replace semantics themselves: `values-local.yaml:12-13` "A profile list override
  REPLACES rather than merges, so one entry is sufficient".
- Neither carries `activeEmbeddingModel` (the `grep -c` output quoted in §1/H6).
- The helper that would find nothing: `templates/_helpers.tpl:8-13`.

**Proposed fix.** Add to §3.1's profile paragraph: both files' `global.embeddingModels` become the single
`{name: BAAI/bge-base-en-v1.5, slug: bge-base}` entry, and note that neither file sets
`activeEmbeddingModel` (so it is inherited from `values.yaml` and needs no per-profile edit — but the
list must agree with it).

### 2.4 The Terraform pool rename does not relabel Azure's nodes, so `values-azure.yaml`'s `tgi`/`tei` node selectors never match

**Description.** §3.9 says "the `ollama` pool entry becomes `tgi` … and a `tei` entry is added … The pool
key is the `iverson.io/node-pool` label the values files select on." On AWS and GCP that is true. On
Azure the label and taint come from a separate `label` attribute in the pool map, not from the key.
Renaming only the key produces an AKS pool named `tgi` whose nodes are still labelled and tainted
`iverson.io/node-pool=ollama`, while §3.1's `values-azure.yaml` selects `iverson.io/node-pool: tgi` with
a matching toleration — so the TGI pod is unschedulable and never tolerates the taint. The new `tei`
entry has the same problem, and additionally will render with an empty label if it is added without one.

**Evidence.**
- `Iverson.Server/deploy/terraform/modules/cluster-azure/main.tf:202-208`:
  `ollama = { vm_size = var.ollama_vm_size, count = var.ollama_node_count, label = "ollama" }`
- `Iverson.Server/deploy/terraform/modules/cluster-azure/main.tf:220-227`:
  `node_labels = { "iverson.io/node-pool" = each.value.label }` and
  `node_taints = ["iverson.io/node-pool=${each.value.label}:NoSchedule"]`
- Contrast `cluster-aws/main.tf:572` and `cluster-gcp/main.tf:266`, both `= each.key`.
- The key and label genuinely diverge in this map already (`starrocksfe` → `starrocks-fe`), so this is
  not an incidental duplicate that a key rename happens to carry.
- The consumer: `values-azure.yaml:59-67` (`ollama: nodeSelector: iverson.io/node-pool: ollama` plus the
  matching toleration), which §3.1 replaces with `tgi`/`tei`.

**Proposed fix.** State in §3.9 that on Azure the `label` attribute must be updated alongside the key
(`tgi = { …, label = "tgi" }`, `tei = { …, label = "tei" }`), and drop the blanket "the pool key is the
label" sentence in favour of a per-cloud statement.

### 2.5 Dropping the format directive is not backend-neutral: Ollama wraps the JSON in prose, so the fence strip leaves unparseable text

**Description.** §3.5 rules that `GenerateJsonAsync` sends "the same request with no grammar", strips
"a leading ```` ``` ```` or ```` ```json ```` fence and a trailing fence", parses the remainder, and
throws otherwise — and concludes "Ollama also serves `/v1/chat/completions` (§9 row 14), so this port is
backend-neutral: the fail branch of §7 keeps it." §9 row 12 measured the prompt-only JSON behaviour on
**TGI**. On the live Ollama with `qwen2.5:3b`, the enrichment extraction prompt returns a sentence of
prose, then the fenced object, then a further paragraph. Stripping a leading and a trailing fence
removes nothing (neither end *is* a fence), `JsonDocument.Parse` fails, and §4's contract fires:
`InvalidOperationException`, "nothing is stored for that column". Today this cannot happen because the
Ollama request carries `format = "json"`, which forces a bare object.

This is not a Phase-C′-only problem. §7 lands the `EnrichmentService` port in **Phase B**, while Ollama
is still the enrichment backend and the removal is ungated — so every `[IversonExtracted]` target on the
compose stack goes from working to empty at Phase B, and stays that way in the fail branch.

**Evidence.**
- Today's forcing directive: `Iverson.Server/Iverson.Embeddings/EnrichmentService.cs:35-37` —
  `object payload = jsonFormat ? new { model = ModelId, prompt, stream = false, format = "json" } : …`.
- Live probe against the running `iverson-ollama` (single inference call, read-only), using the real
  prompt shape `EnrichmentPrompts.Extraction` + the consumer's appended hint:

```
$ curl -s -X POST http://localhost:11434/v1/chat/completions -d '{"model":"qwen2.5:3b","messages":[{"role":"user","content":"Extract structured information from the following text and return it as JSON:\n\nAcme Corp reported revenue of 4.2 billion dollars in 2024, up 12 percent, across 31 countries.\n\nExtract specifically: the revenue figure and the year"}],"max_tokens":256,"temperature":0,"stream":false}'
CONTENT: 'Here is the extracted information formatted as JSON:\n\n```json\n{\n  "revenue": {\n    "figure": 4.2,\n    "unit": "billion dollars"\n  },\n  "year": 2024\n}\n```\n\nNote: The percentage increase (12%) and the number of countries are not included in the structured information as they do not represent specific numerical values that can be directly extracted from the text provided.'
```

- The consequence path: `EnrichmentConsumer.cs:273-276` is `GenerateJsonAsync`'s only caller, and
  §9 row 15 records that `Extracted` stores the returned text verbatim as the target column.

**Proposed fix.** Replace "strip a leading and a trailing fence" with "extract the first fenced code
block if one is present, else the first balanced `{ … }` span, then parse; throw naming the head of the
text if that fails." That is backend-neutral and also survives the same preamble on TGI. If the design
prefers to keep the strict strip, then §3.5's ruling must be split by backend — Ollama's
`/v1/chat/completions` accepts `response_format: {"type":"json_object"}`, TGI does not (§9 row 12) —
and §7's Phase B must say which one the port sends while Ollama is still serving.

### 2.6 The 8,000-character cut removes the end of the prompt, which is where the extraction hint lives — a regression, not parity

**Description.** §3.5 introduces `MaxPromptChars = 8_000`, "the prompt is cut to this length before
sending … Ollama silently truncated at its 2,048-token context … Parity, not a regression", and §4
restates it as "A prompt over 8,000 characters is cut, never rejected." Cutting the *assembled* prompt to
its first 8,000 characters drops the tail — and for `EnrichmentKind.Extracted` the tail is the
per-target hint, which `SchemaRegistrationOrchestrator` makes mandatory for `[IversonExtracted]`. The
model then extracts arbitrary structure and §9 row 15's verbatim store writes it into the target column:
a silent wrong value, not a failure. Cutting the other end is not a fix either — the `Extraction`,
`Summary` and `Keywords` prompts put their instruction at the **head**.

Two measurements make "parity" the wrong word. First, Ollama truncates head-first and keeps the tail, so
the hint survives today. Second, the context is 4,096 tokens on this Ollama, not 2,048 — so an 8,000-char
(~2,000-token) cut roughly halves the source text that reaches the model even where the instruction
survives.

**Evidence.**
- The hint is appended after the source text: `Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs:273-276`
  — `string.Format(EnrichmentPrompts.Extraction, sourceText) + $"\n\nExtract specifically: {target.Hint}"`.
- The instruction is at the head for three of the four prompts and at the tail for the fourth:
  `Iverson.Server/Iverson.Embeddings/EnrichmentPrompts.cs:5-23`.
- `sourceText` is unbounded — the concatenation of every `[IversonEmbedding]` and `[IversonChunk]`
  property value: `EnrichmentConsumer.cs:293-304` `BuildSourceText`. (`ChunkContext` is the one bounded
  prompt: `IntelligenceStoreConsumer.cs:46` `ParentTextContextChars = 2000` plus one chunk, so it does
  not reach the cap — the cap bites exactly on the three prompts fed the whole document.)
- Live probe against the running `iverson-ollama` (single inference call): a 40,625-character prompt
  whose **last** line is the only instruction:

```
prompt chars: 40625
RESPONSE: 'BANANA'
prompt_eval_count 4096 eval_count 4
```

  The tail survived truncation (the model obeyed the trailing instruction) and the context is 4,096
  tokens, not 2,048.

**Proposed fix.** Apply the cap to the interpolated source text, not to the assembled prompt — i.e. cut
`sourceText` (and `documentContext`/`chunkText`) before `string.Format`, so both the leading instruction
and the trailing hint always survive. Restate §3.5's parity note: Ollama's effective input was 4,096
tokens and was truncated from the head; an 8,000-character source-text cap is a deliberate reduction, not
parity. §8's test list should gain a case pinning that a 20,000-character source text still sends the
hint.

---

## 3. Forced decisions

No forced decisions found.

---

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 has six findings and §3 is empty. Four of them (2.1,
2.3, 2.4 in the chart/Terraform layer, and 2.2 across Phase A's own test list) would stop the deployment
surfaces from working at all; two (2.5, 2.6) silently corrupt enrichment output on the backend Phase B
still runs against. The gate design (§6), the TGI runtime choices (§3.3), the compose shape (§3.7) and
the conformance-fixture sweep (§3.10) all held up. Address §2, correct the four §1 evidence items, and
the spec is ready for implementation planning.
