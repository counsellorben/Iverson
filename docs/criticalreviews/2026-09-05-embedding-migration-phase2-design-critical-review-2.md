# Critical Design Review: 2026-09-05-embedding-migration-phase2-design (Round 2)

**Spec:** docs/specs/2026-09-05-embedding-migration-phase2-design.md
**Spec (absolute):** `/home/ben/repositories/Iverson/docs/specs/2026-09-05-embedding-migration-phase2-design.md`
**Verified Assumptions section:** present (§10, T1–T6, K1–K5, H1–H7, F1–F4, C1–C5, S1–S8, P1–P2, Q1–Q2, M1–M3, D1–D3)

Repo state: `main` @ `f44654c`, clean working tree (`git status --porcelain` empty before and after).
Every `file:line` below is a read at that commit; every command is quoted with its output. No
`docker compose` command was run; no `iverson-*` container was started, stopped or recreated. Live
probes were: two read-only `curl`/`urllib` POSTs against the already-running `iverson-ollama`
(two inference calls with `qwen2.5:3b`), read-only Qdrant `GET`s, one read-only `SELECT` through
`docker exec iverson-postgres psql`, and `free`/`/proc/meminfo`. `helm dependency build` +
`helm template` + `kube-score` were run only against a **copy** of the chart in the scratchpad
(`$SCRATCH/cdr2-phase2/chart`); the repo's `charts/*.tgz` were not touched. No probe container was
created (none was needed — round 1 settled the TGI runtime questions from the router source, and
this round's open question was answerable with the locally installed `kube-score`, which is the
same v1.19.0 the workflow pins). Nothing under `~/repositories/iverson-benchmark-corpora/` was
written.

---

## 0. Coverage enumeration

### Sections

| Section | Disposition |
|---|---|
| §1 Why | ok — the rewritten paragraph ("No Helm or cloud deployment exists … The compose dev stack does hold registered types — 36 rows") reproduces: `select type_name from public._iverson_schema` returns exactly 36 names, and no Helm release is running. |
| §2 Scope | ok — re-derived against the new body: `bge-small` appears nowhere else; `EmbeddingPrefixes.Table` is still in §5's Untouched row and `EmbeddingPrefixes.cs:29-39` still carries the nomic/arctic rows the "Out" list protects; the 3B exclusion re-derives from the re-measured `MemAvailable` (see §1/T6). |
| §3.1 Helm values | ok — the profile paragraph now covers the two overrides that were §2.3 last round; checked against `values-local.yaml:21-22` and `values-laptop.yaml:25-26` (list only, no `activeEmbeddingModel`) and against `values-{aws,azure,gcp}.yaml`, which carry no `embeddingModels` and so inherit the single slug'd entry. Also checked that dropping values.yaml's second (`snowflake-arctic-embed:s`) entry strands nothing: no `_iverson_schema` row names it (all 36 are `nomic-embed-text`, C5). |
| §3.2 `tei` subchart | → §2.1 — the ClusterIP governing Service is a `statefulset-has-servicename` CRITICAL under the kube-score version and flags the workflow pins. Rest checked: `--auto-truncate`, `HUGGINGFACE_HUB_CACHE=/data`, `containerPort: 8080`, the securityContext and `volumeClaimTemplates` shapes against `charts/ollama/templates/statefulset.yaml:37-113`. |
| §3.3 `tgi` subchart | → §2.1 (same Service defect). Rest checked: `condition: global.enrichmentEnabled` against the proven `condition: global.engagementEnabled` (`Chart.yaml:16-19`); `workingDir: /tmp`; the 3072/3584 arithmetic; `shm_size`/`emptyDir medium: Memory`. |
| §3.4 Helpers, deployments, policies | ok — `charts/api/templates/deployment.yaml:124,126,127,129` and `charts/worker/…:119,121,122,124` are exactly the four sites named (H2); `_helpers.tpl:7-14,22-34` is the helper pair; the `-ollama` targets to be repointed are `networkpolicies.yaml:54,85`, the policies to delete `:393-418`. `Enrichment__Enabled: {{ .Values.global.enrichmentEnabled }}` checked against the chart's own boolean-env convention (`deployment.yaml:94-95` uses `| quote`), so the sibling pattern is right there. |
| §3.5 Server code | ok — `MaxSourceChars`'s new home checked in full (see the arrows table); the JSON-extraction rule exercised against two fresh live Ollama replies; the wording list now includes the Java/TypeScript annotation files round 1 found missing (they exist and hold the strings). Two stale cross-references dropped (below). |
| §3.6 Launcher | ok — `Iverson.Launcher/Program.cs:20` is the `up` list, `:28,72-103` `WaitForOllamaAsync`; 8091/8092 line up with compose's `8091:80` (`docker-compose.yml:176-177`) and the planned `8092:80`. |
| §3.7 Compose | ok — every cited line reproduces: `docker-compose.yml:124` `ollama:`, `:138` `ollama-init:`, `:171` `tei-embed:`, `:387` api, `:473`/`:545` the two `ollama-init` `depends_on`, `:483` worker, `:571` `ollama_data`. api/worker `Embeddings__ModelId=${BENCH_EMBED_MODEL:-nomic-embed-text}` (`:417`, `:507`) is the default the section switches. |
| §3.8 Scripts and kind | ok — `stack.py:82-84` TIERS / `:88-95` CONTAINER / `:169-173` READY_CHECKS are the three tables; `ingest.py:531-534` posts `{embed_url}/v1/embeddings` for **both** backends (no Ollama-specific branch anywhere in the file), `:150` `OLLAMA_URL`, `:723,736` the two defaults; `setup.sh:98` / `setup.ps1:95` are the storageSize notes. |
| §3.9 Terraform | ok — the per-cloud rule now matches the code: `cluster-aws/main.tf:505-513` + `labels = { "iverson.io/node-pool" = each.key }`, `cluster-gcp/main.tf:232-241` + `each.key`, `cluster-azure/main.tf:201-208` (`label = "…"` per entry) + `:220-227` `each.value.label`. `operators/main.tf:175-177` and `outputs.tf:7` are the two storage-class sites; `grep -rn storage_class_names` shows the map is only re-exported (`aws|azure|gcp/main.tf:77`), never indexed. |
| §3.10 Conformance fixtures | ok — all twelve fixture sites read, including the two .NET ones round 1 did not open: `Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Models/S11ModelDotnet.cs:24` and `S12ModelDotnet.cs:11` both carry `[IversonEmbeddingModel("nomic-embed-text")]`, as §3.10 says. `ModelRejectedScenario.cs:84` `OverrideModelId` is the never-deployed id. |
| §4 Failure semantics | ok — each of the six bullets re-derived: the extraction bullet against the live replies; the 8,000-char bullet against `EnrichmentConsumer.cs:129-130,264-284`; the `enrichmentEnabled: false` bullet against `IntelligenceStoreConsumer.cs:179-180` (`contextualEnabled = enrichmentOptions.Value.Enabled && …`), which is what makes "which nothing calls" true. |
| §5 Component contract | → §2.2 — the new `EnrichmentConsumer.cs` / `MaxSourceChars` row is carried by no §7 phase. Every other row maps to a file that exists. |
| §6.1 Candidates | ok — the 3B "no" re-derives on the re-measured 7.25 GB `MemAvailable` (see §1/T6). |
| §6.2 `enrich_bench.py` | ok — `scifact-run-2026-08-26/beir/corpus.jsonl` exists (8.5 MB); `EnrichmentPrompts.cs:5-23` has exactly the four constants with the slot counts the script needs; `usage.completion_tokens` present on the live Ollama reply. |
| §6.3 / §6.4 Gate | ok — 50+10+10+5 = 75; 120 s = `EnrichmentServiceOptions.cs:9`; criterion (5)'s 500 MB floor against the re-measured 7.25 GB. |
| §7 Ordering / two end states | → §2.2. Phase A's new clear/drop/restore step checked end-to-end (arrows table): it is coherent and self-healing for both harnesses. |
| §8 Testing | → §2.1. Also checked: the three `deploy-validate.yml` jobs are as H5 says (`:25-35`, `:52-63`, `:80-97`), and the kube-score step runs under `set -e` with three `--ignore-test` flags, so a CRITICAL fails the job. |
| §9 Measurements | ok — row 1's corrected figure re-measured; rows 16, 17 are the CDR-1 probes and row 17's shape reproduced today on two fresh prompts. |
| §10 Verified assumptions | see §1 — 2 items fail (one of them evidence-only). |
| §11 Known issues | ok — all five are genuinely scoped out; none is load-bearing for §3's mechanics. |

### Rules and operands

| Rule | Disposition |
|---|---|
| `statefulset-has-servicename` (the checker's rule over the design's Service/StatefulSet pair) | → §2.1 — both operands checked: the Service (`clusterIP` unset ⇒ not headless) and the StatefulSet's `serviceName`. Both directions tested by A/B on identical manifests. |
| `iverson.activeEmbeddingModel`: match `global.activeEmbeddingModel` against each entry's `.name` (`_helpers.tpl:8-13`) | ok — under-match direction (last round's §2.3) closed by §3.1's new sentence: the profile lists now carry `BAAI/bge-base-en-v1.5`, which is what `values.yaml`'s `activeEmbeddingModel` will name. Over-match: exact equality, one entry, no collision. |
| `<release>-tei-<slug>` name construction | ok — the `slug` operand now exists on every entry any of the six values files supplies (§3.1); the cloud files carry no `embeddingModels` at all, so they inherit it. `iverson-tei-bge-base` is 20 characters, inside the 63-char RFC 1123 label limit (H7). |
| Service `port`/`targetPort` mapping vs the URL the client dials | ok — with the ClusterIP change the runtime arrow is sound (kube-proxy applies 80→8080); the residual defect is the checker's, not the packet's → §2.1. |
| Fence/`{ … }` extraction (§3.5) | ok — both directions exercised on real replies. Under-inclusion: a prose-wrapped reply now yields the object (the failure that motivated the rule). Over-inclusion (a first fenced block that is not JSON short-circuiting the balanced-span fallback) did not occur in either probe and is covered by §4's throw. Two live replies below. |
| `MaxSourceChars = 8_000` applied to `BuildSourceText`'s result | ok — checked at the one call site (`EnrichmentConsumer.cs:129`), where the same `sourceText` also feeds `ComputeHash` (`:130`): cutting shifts the loop-prevention hash, which costs one re-enrichment pass per over-long object and cannot strand one, because the generated values would not have differed. Both prompt ends survive: instruction at the head (`EnrichmentPrompts.cs:5-13`), hint appended at the tail (`EnrichmentConsumer.cs:273-276`). |
| "`IntelligenceStoreConsumer`'s chunk-context input is already bounded" (a negative claim, load-bearing for capping only one call site) | ok at the shipped default, dropped otherwise — the bound is `ParentTextContextChars` 2000 + `cf.MaxTokens * 4` + ~220 chars of boilerplate (`IntelligenceStoreConsumer.cs:46,239,246,666,635-636`). At the client default of 512 tokens (`Go/iverson/tags.go:293`, `IversonChunk.java:18`, `annotations.py:42`, `SchemaBuilder.cs:161`) that is ~4,270 chars ≈ 1,070 tokens, comfortably under 3,072. See the dropped list for the >2,500-token declaration case. |
| Eligibility predicate: which registrations the model-equality guard fires on after Phase A's clear | ok — enumerated the producers of a prior model rather than trusting the spec: `SchemaRegistrationOrchestrator.cs:100-106` reads `priorModel` from `batchDescriptors` or `registry.Get`, and both are empty for a type whose row was deleted, so the guard cannot fire on the first re-registration. Every one of the 36 deleted rows has a producer that re-registers it (the five conformance drivers, `SchemaRegistrar.RegisterAllAsync` for the four `Benchmark*` types, the ClientConformance scenarios for the rest). |
| Terraform pool key → `iverson.io/node-pool` label | ok — all three producers re-read; §3.9's per-cloud statement matches each. |
| Phase-assignment rule: every §5 component-contract row lands in exactly one §7 phase | → §2.2 — one row (`EnrichmentConsumer.cs` / `MaxSourceChars`) lands in none. |

### Data-flow arrows

| Arrow (consuming operation) | Disposition |
|---|---|
| **Persistence boundary:** Phase A `DELETE FROM public._iverson_schema` → conformance driver re-registration → guard | ok — dumped the real rows (36 type names) rather than reasoning from the descriptor; with the row gone `priorModel` is null and the guard is unreachable. |
| Phase A "drop those types' tenant-qualified Qdrant collections" → Qdrant | ok — read the live collection list rather than assuming: `benchmark_documents_tenant_bypass`, `benchmark_documents_chunks_tenant_bypass`, `vector_docs_tenant_bypass`, `vector_docs_chunks_tenant_bypass`, `iverson-probe`. The conformance fixtures (`S11Model*`, `S12Inherited*`) have **no** collections, so that half of the step is a no-op rather than a missing operand; the naming rule `{base}_{tenantId}` / `{base}_chunks_{tenantId}` matches `IntelligenceTenantScope.cs:11-16`. |
| Phase A snapshot restore → `benchmark-query` **key-map load** | ok — one row per required parameter. `BenchmarkQueryScenario.cs:226` loads `--key-map-path`; the spec names `scifact-bge-base-2026-09-04/keymap.json`, which is byte-identical to the `scifact-run-2026-08-26` one (`md5sum` equal, 277,996 bytes each), so either path feeds the same map. `:101` reads `<key-map-path>.stats.json`, which exists and gives 19,967/5,183 = 3.85 chunks/doc against `ChunkBudgetMultiplier = 5` — the guard passes. |
| Phase A snapshot restore → `benchmark-query` **schema registration** | ok — the operation `benchmark-query` needs (a registered `BenchmarkDocument`) is supplied by its own run: `Program.cs:89` puts `benchmark-query` in `needsTenantAndSchema` and `:151-171` calls `SchemaRegistrar.RegisterAllAsync` before dispatch. `BenchmarkDocument.cs` declares no `[IversonEmbeddingModel]`, so it re-registers under the API default — bge-base after Phase A — against a restored 768-dim bge-base index (`GET /collections/benchmark_documents_tenant_bypass` → `body_vector`/`body_centroid`, size 768, 5,183 points). |
| `_helpers.tpl iverson.embeddingBaseUrl` → `Embeddings__BaseUrl` → HttpClient **connect** | ok — with a ClusterIP Service the `:80` URL DNATs to 8080; `ServiceCollectionExtensions.cs:18` sets `BaseAddress` from `BaseUrl`. |
| `EnrichmentConsumer.BuildSourceText` → cap → `ComputeHash` **and** prompt assembly | ok — see the rules table; one row per consumer of the cut value. |
| `IntelligenceStoreConsumer` ChunkContext → `enrichment.GenerateAsync` → TGI **prefill** | ok at the default (second call site of the generative operation, sourced from its own artifacts — chunk text and parent-text slice, not `BuildSourceText`). |
| Backend reply → extraction → `Extracted` **column write** | ok — exercised live twice; §9 row 15's verbatim store means the extracted span is what lands. |
| §5 row → §7 phase → **end state** | → §2.2. |

### Dropped candidates

- **Chunk-context prompt above `--max-input-tokens` for a type declaring `maxTokens > ~2,500`.** No server-side cap on `ChunkMaxTokens` exists, so a client can declare one; the resulting 422 is caught per chunk (`IntelligenceStoreConsumer.cs:645-651` logs a warning and embeds the chunk unprefixed). Degradation on a non-default declaration, not a break of the asked-for behaviour. Dropped.
- **`enrich_bench.py`'s ChunkContext prompts are smaller than production's** (1,500 + 512 chars vs a 2,000 + 2,048-char worst case). Both backends see the identical set, so gate criterion (1) — the comparative one — is unaffected, and I have no measurement showing the difference moves criterion (2). Speculation. Dropped.
- **`values-local.yaml` inherits `tei.resources` (2 CPU / 2Gi requests) where it overrode `ollama` down to 250m/1Gi.** §3.1 gives the smaller override to laptop only. Whether the §8 kind smoke's TEI pod schedules depends on the kind host's capacity, which the profile's own header leaves at ">=16GB" — no evidence it fails. Dropped.
- **Two stale §9 cross-references in §3.5/§3.3** (grammar behaviour is row 12 not row 11; the read-only `out` write is row 13 not row 12 — §9 rows 1–15 were not renumbered by `f44654c`). Documentation nits with no behavioural consequence. Dropped.
- **`ModelRejectedScenarioTests.GuardMessage` is a deliberate copy of the guard string §3.5 rewords.** Checked what the harness actually asserts: `ModelRejectedScenario.cs:263,272,282-284,295-296` match only the model names, the `DELETE FROM` clause and the two collection names — none of them the reworded sentence. Neither the unit test nor the live harness goes red. Dropped.
- **`MaxGeneratedTokens = 256` justified as "the chat API requires an explicit `max_tokens`"** — TGI defaults it when omitted. The value is right for the prompts; the justification is loose, not wrong. Dropped.

---

## 1. Verified-assumptions cross-check

**Still hold (37):** T1, T2, T3, T4, T5, T6 (see below — the corrected evidence now reproduces);
K1, K2, K3, K4, K5 (reconfirmed against the Kubernetes Service docs, and independently corroborated
by kube-score's own message, which quotes the same StatefulSet limitation); H1 (`Chart.lock:14-16`;
`charts/ollama-0.1.0.tgz` present; `deploy-validate.yml:25-26,52-53,80`), H2 (line numbers verified
exactly: api `:124,126,127,129`, worker `:119,121,122,124`), H3 (`_helpers.tpl:9` and
`charts/ollama/templates/statefulset.yaml:59-62` are the only readers), H4 (`:54,85` and `:393-418`),
H5, H6 (both profile files re-read), H7 (`setup.sh:97`; `iverson-tei-bge-base` = 20 chars); F1
(all three maps re-read), F2 (`operators/main.tf:175-177`, `outputs.tf:7`), F3, F4 (`grep -rn ollama
aws/ azure/ gcp/ bootstrap/` → no output); C1 (every compose line number reproduces), C2, C3, C4,
C5 (36 rows, the type-name list read); S1, S3, S4, S5, S6 (`IntelligenceStoreConsumer.cs:179-180`
shows the same flag also gates the chunk-context path), S7 (reproduced live today), S8; P1, P2;
Q1 (all twelve fixture sites read, including the two .NET files), Q2; M1 (`qwen2.5:3b` answered two
live calls), M2, M3; D1, D2.

**Failed (2):**

**D3** — "53 tracked files reference Ollama outside `docs/`; every one is in §3.5, §3.7, §3.8, §3.9,
§3.10 or the deleted subchart, except `Iverson.Server/docs/security/tma.md` and `README.md`". The
count is now right; the enumeration is still not. `f44654c` added the four client-annotation files
round 1 named, but seven **test** files in the same 53 are named by no section of the spec:

```
$ git ls-files | grep -v '^docs/' | xargs grep -lil ollama | sort | wc -l
53
```

The unnamed residue is `Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs:380`,
`Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs:593,609,1428,1530`,
`Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs:836`,
`Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs:11`,
`Iverson.Api.Tests/Schema/IngestContractTests.cs:310`,
`Iverson.ClientConformance.Tests/ModelRejectedScenarioTests.cs:58,504` and
`Iverson.Embeddings.Tests/EmbeddingServiceTests.cs:32,85,244,248,265`. All are comments or fake
exception messages, so nothing breaks — but D3 asserts a completed enumeration and the enumeration
is not complete. Fix: extend the acknowledged exception to "test comments and fake-exception
strings", or add the seven files to §3.5's list.

**S2 (evidence only)** — "`EnrichmentServiceTests`: 7 facts; three pin Ollama specifics". There are
**6**:

```
$ grep -c '\[Fact\]' Iverson.Embeddings.Tests/EnrichmentServiceTests.cs
6
$ grep -c '\[Theory\]' Iverson.Embeddings.Tests/EnrichmentServiceTests.cs
0
```

The load-bearing half holds exactly as stated: `/api/generate` at `:85`, the `response` field at
`:47`, `format: json` at `:97`. Fix: change 7 to 6.

**Span check — dependencies with no covering assumption:**

- **"kube-score accepts a StatefulSet governed by a non-headless Service."** Load-bearing: §8 names
  the kube-score job as a check on the new objects, and §3.2's ClusterIP ruling is what creates
  them. No §10 item covers the checker's behaviour (K5 covers Kubernetes', not kube-score's).
  Verified in-round and **false** → §2.1.
- **"`benchmark-query` re-registers `BenchmarkDocument` itself, so Phase A's row clear is
  self-healing for the Phase 1 harness."** Load-bearing for §8's last bullet. No §10 item covers it.
  Verified in-round (`Iverson.LoadTest/Program.cs:89,151-171`) → holds.
- **"The bge-base key map and the 2026-08-26 key map are interchangeable."** Load-bearing for §7
  Phase A naming a different key map than the Phase 1 runs used. No §10 item covers it. Verified
  in-round (`md5sum` identical) → holds.
- **"The conformance fixtures' Qdrant collections exist to be dropped."** Load-bearing for Phase A's
  drop step. No §10 item covers it. Verified in-round: they do not exist; the step is a no-op for
  them and drops only `benchmark_documents*` (restored immediately after) and `vector_docs*` (the
  harness recreates them) → holds.
- **"Cutting `BuildSourceText`'s result does not corrupt the enrichment loop-prevention hash."**
  Load-bearing for placing the cap in the consumer rather than the service. No §10 item covers it.
  Verified in-round (`EnrichmentConsumer.cs:129-130`) → holds.
- **"Every §5 component-contract row is carried by a §7 phase."** Load-bearing for §7's promise that
  both end states are fully defined. No §10 item covers it. Verified in-round and **false** → §2.2.

---

## 2. Literal-wrongness findings

### 2.1 A ClusterIP governing Service makes both new StatefulSets a kube-score CRITICAL, and §8 names kube-score as the check

**Description.** §3.2 rules that the `tei` Service is "a ClusterIP Service `<release>-tei-<slug>` …
not headless like ollama's … the StatefulSet's `serviceName` still names this Service", and §3.3
repeats it for `tgi`. §8 then lists "`kube-score` on all five profiles (the `deploy-validate`
workflow's three jobs, run locally with `helm dependency build` first)" as the chart test for this
change. kube-score's `statefulset-has-servicename` check grades a StatefulSet whose `serviceName`
names a **non-headless** Service as CRITICAL — it is not one of the workflow's three documented
`--ignore-test` exemptions, and the step runs under `set -e`, so every overlay's render reports two
new CRITICALs (one per `tei` entry, one for `tgi`) that the design has no answer for.

This is not the runtime defect round 1 raised (that one is fixed — kube-proxy really does apply
80→8080 for a ClusterIP Service). It is the second half of the same operand: the option the fix
picked is the one Kubernetes documents as unsupported for a StatefulSet's governing Service, and the
repo's own CI encodes that.

**Evidence.**

- Spec §3.2 / §3.3 / §8 as quoted; the workflow's exemption list is `deploy-validate.yml:90-92`
  (`pod-networkpolicy`, `container-image-pull-policy`, `container-security-context-user-group-id`)
  with the reasons documented at `:93-95`, and the loop is `set -e` at `:85`.
- A/B on two manifests differing **only** in `clusterIP: None`, with the CI-pinned tool and the CI
  flags (local `kube-score version: 1.19.0, commit: a0a0f48…` = `deploy-validate.yml:77`'s
  `v1.19.0`):

```
$ kube-score score --ignore-test pod-networkpolicy --ignore-test container-image-pull-policy \
    --ignore-test container-security-context-user-group-id tei-headless.yaml
apps/v1/StatefulSet iverson-tei-bge-base   ✅
EXIT=0

$ kube-score score ... tei-clusterip.yaml
apps/v1/StatefulSet iverson-tei-bge-base   💥
    [CRITICAL] StatefulSet has ServiceName
        · StatefulSet does not have a valid serviceName
            StatefulSets currently require a Headless Service to be responsible
            for the network identity of the Pods. You are responsible for
            creating this Service.
            https://kubernetes.io/docs/concepts/workloads/controllers/statefulset/#limitations
EXIT=1
```

  (Both manifests carry the PDB, the anti-affinity, the ephemeral-storage requests/limits and the
  startupProbe/readinessProbe pair §3.2 specifies, so the CRITICAL is attributable to the Service
  shape alone.)
- The check id, confirmed by suppressing it: adding `--ignore-test statefulset-has-servicename` to
  the same command turns the ClusterIP manifest `✅` and `EXIT=0`.
- Honest baseline, so the delta is not overstated: the job is **not green today** either. Rendering
  the current chart in a scratchpad copy (`helm dependency build` then
  `helm template iverson . -f values-local.yaml`) and running the CI command gives `EXIT=1` with 12
  CRITICALs across six pre-existing objects (`iverson-admin-ui`, `iverson-authentik-server`,
  `iverson-authentik-worker`, `iverson-redis`, and two NetworkPolicies) — none of them ollama's:
  `apps/v1/StatefulSet iverson-ollama ✅`, because its Service is headless
  (`charts/ollama/templates/service.yaml:6` `clusterIP: None`). So this finding is that the design's
  **own new objects** cannot come back clean from the check it nominates, and the spec neither picks
  a Service shape that passes nor extends the exemption list.

**Proposed fix.** Pick one and say so in §3.2/§3.3 (and, for the last option, in §8):
(a) give each StatefulSet a headless governing Service (`<release>-tei-<slug>-headless`, `port: 8080`,
no remapping) named by `serviceName`, plus the ClusterIP Service the clients dial — the standard
pairing, and it keeps §3.4's `:80` URLs;
(b) keep the single headless Service, set `port: 8080` with no `targetPort`, and make every URL in
§3.4 and the `iverson.embeddingBaseUrl` helper `:8080`;
(c) keep the ClusterIP as written and add a fourth `--ignore-test statefulset-has-servicename` to
`deploy-validate.yml` with the same style of documented reason the other three carry (this is the
only option that changes CI rather than the chart, and it also suppresses the check for any future
StatefulSet).

### 2.2 §7 assigns the `MaxSourceChars` cap to no phase, so the Phase C end state contradicts §4

**Description.** Round 1's §2.6 fix moved the cap out of `EnrichmentService` and into
`EnrichmentConsumer`, and §5 gained a row for it: "`Iverson.Api/Consumers/EnrichmentConsumer.cs` |
`MaxSourceChars` cap on `BuildSourceText` before prompt assembly". §7's phase lists were not updated
to match, and they are file-level elsewhere ("the Phase 1 `Embeddings__Models__0__*` removal",
"Launcher's TEI wait"). Phase A lists the embedding switch only; Phase B lists "`EnrichmentService`
port (backend-neutral), compose `tgi` service, `enrich_bench.py`, the measurement, the verdict";
Phase C lists "§3.5 `Enrichment` defaults and wording" — the port is explicitly Phase B's and the
"defaults and wording" qualifier excludes the cap. No phase carries it.

That matters at the end state Phase C defines, which is the one where TGI actually serves
enrichment: without the cap, any object whose concatenated `[IversonEmbedding]`/`[IversonChunk]`
text exceeds `--max-input-tokens` gets a 422 from TGI, which §4 turns into an
`HttpRequestException` and no stored value — while §4 simultaneously promises "Source text over
8,000 characters is cut before the prompt is built, never rejected". Two of the four mutations §8
requires the tests to fail against ("no cap or a cap on the assembled prompt") also have no phase
in which they can be written.

**Evidence.**
- Spec §5 (the `EnrichmentConsumer.cs` row), §7 (the four phase bullets), §4 (the cut-never-rejected
  bullet), §8 (the `EnrichmentConsumerTests` case and the named mutations) as quoted.
- The cap's only sensible site is `Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs:129`
  (`var sourceText = BuildSourceText(schema, row);`), whose value flows to `ComputeHash` at `:130`
  and to `GenerateAsync(schema, sourceText, ct)` at `:146`; nothing in the `EnrichmentService` port
  Phase B names touches that file.
- Phase C′ is unaffected either way (Ollama truncates from the head — §9 row 16 / S8 — so the cap is
  a deliberate reduction there, not a requirement), which is why the omission bites only on the pass
  branch.

**Proposed fix.** Name it. Put `EnrichmentConsumer`'s `MaxSourceChars` cap (and its
`EnrichmentConsumerTests` case) in Phase B beside the `EnrichmentService` port — it is
backend-neutral for the same reason the port is — or, if the intent is to keep Ollama's behaviour
until TGI actually serves, in Phase C, and say in Phase C′ that the cap does not land.

---

## 3. Forced decisions

No forced decisions found.

---

## 4. Previously addressed

Round 1 (`docs/criticalreviews/2026-09-05-embedding-migration-phase2-design-critical-review-1.md`,
commit `101a733`) raised six §2 findings, four §1 failures and four span-check items. All fourteen
edits are present in `f44654c`. Confirmed resolved, with two carrying a residue:

- **§2.1 (headless Service, dead port 80)** — resolved *as a runtime defect*: §3.2/§3.3 now specify
  ClusterIP with the reason inline, and K5 records the mechanism. The option chosen introduces a new
  problem in the design's own check → §2.1 this round.
- **§2.2 (no registered types on any deployment)** — resolved. §1 is rescoped to "No Helm or cloud
  deployment exists", C5 records the 36 nomic-pinned rows, §7 Phase A carries the
  `DELETE FROM public._iverson_schema` + collection drop + M1 restore, and §8 says
  "after Phase A's schema-row clear". I re-derived the whole step: the guard cannot fire on a
  cleared row (`SchemaRegistrationOrchestrator.cs:100-106`), `benchmark-query` re-registers
  `BenchmarkDocument` itself (`Iverson.LoadTest/Program.cs:89,151-171`) under a default the entity
  does not override, the named key map is byte-identical to the one the Phase 1 runs used, its
  `.stats.json` passes the chunk-budget guard, and the restored collections are 768-dim bge-base.
- **§2.3 (local/laptop `embeddingModels` override)** — resolved. §3.1 now states both files replace
  the override with `{name: BAAI/bge-base-en-v1.5, slug: bge-base}` and notes that neither sets
  `activeEmbeddingModel`, so the list must agree with the inherited one. That closes both the empty
  `slug` and the unresolvable active-model lookup.
- **§2.4 (Azure's `label` attribute)** — resolved. §3.9's blanket "the pool key is the label"
  sentence is gone, replaced by a per-cloud statement that sets `label = "tgi"` / `label = "tei"`;
  it matches `cluster-azure/main.tf:201-208,220-227` exactly, and F1 records the split.
- **§2.5 (fence strip is not backend-neutral)** — resolved. §3.5 now specifies "the first fenced code
  block … else the first balanced `{ … }` span", §4 and §8 follow, and S7/§9 row 17 record the
  Ollama behaviour. I exercised the rule on two fresh live replies — one prose-wrapped, one bare
  fenced block — and both extract and parse:

```
RAW: 'Here is the structured information extracted from the text as JSON:\n\n```json\n{\n  "number_of_patients": 812,\n  "hazard_ratio": 0.68\n}\n```'
VIA: fence | PARSES: True
RAW: '```json\n{\n  "dividend": "$1.35 per share",\n  "auditor": "R. Nakamura"\n}\n```'
VIA: fence | PARSES: True
```

- **§2.6 (the cap removed the extraction hint)** — resolved *as mechanics*: the cap is now
  `MaxSourceChars` on `BuildSourceText`'s result, §4 restates it, §5 gains the consumer row, §8 gains
  the 20,000-character `EnrichmentConsumerTests` case, and S8/§9 row 16 correct the parity claim. The
  residue is that §7 was not updated to carry the new component → §2.2 this round.
- **§1/T6 (2 GB "available")** — resolved. §9 row 1 now reads "7.2 GB available, 2.1 GB free", and it
  reproduces with the same six containers up: `free -m` → `available 7253`, `/proc/meminfo` →
  `MemAvailable: 7427308 kB`. §6.1's 3B exclusion is restated on the RSS estimate as recommended.
- **§1/H6 (`activeEmbeddingModel` in the profiles)** — resolved; the row now says "-local and -laptop
  carry `embeddingModels` only (a replacing override) and inherit `activeEmbeddingModel`", which both
  files confirm.
- **§1/F1 (Azure label)** — resolved; the row now names the per-cloud split.
- **§1/D3 (incomplete enumeration)** — count corrected to 53 and the four client-annotation files
  added to §3.5, but the enumeration is still incomplete → §1 this round.
- **Span-check items** — all four now carry an assumption: headless-Service semantics → K5;
  registered types on compose → C5; Ollama's un-directed JSON shape → S7; Ollama's truncation
  direction → S8.

---

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 has two findings and §3 is empty. Both are one-line
design decisions rather than redesigns: pick a Service shape (or an exemption) that the kube-score
job §8 nominates can actually return clean on, and name the phase that carries the `MaxSourceChars`
cap so the Phase C end state matches §4. Everything round 1 raised is addressed in the spec text,
and the two heaviest new surfaces this round — Phase A's schema-clear/collection-drop/snapshot-restore
step and the backend-neutral JSON extraction — both held up against the live stack, the live key maps
and two fresh model replies.
