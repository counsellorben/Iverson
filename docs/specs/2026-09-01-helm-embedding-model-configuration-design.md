# Embedding-Model Configuration and Per-Type Selection

**Status:** design, approved 2026-09-01. Covers deploy-time model configuration (Part A) and
per-type model selection at schema registration (Part B). Serving topology and GPU enablement remain
separate pieces (see "Scope and decomposition").

## Problem

The Helm chart pulls `nomic-embed-text` as a hardcoded literal (`charts/ollama/templates/statefulset.yaml:59`),
one line above `ollama pull {{ .Values.global.generativeModel }}`, which is templated. Neither the api
nor the worker deployment sets `Embeddings__ModelId`; both rely on the C# default in
`EmbeddingServiceOptions` happening to match what the chart pulled. `docker-compose.yml` sets the
variable explicitly at `:372` and `:457`, so Helm is the only deployment path with an implicit contract.

Two failures follow. A pull/request mismatch is **loud** — Ollama 404s on the first embed call. The
silent one is worse: api embeds queries and worker embeds documents, and if those two ever resolve to
different models, nothing errors and retrieval quietly returns garbage from two incompatible vector
spaces.

This matters more since prefixes became model-derived: `EmbeddingPrefixes` resolves the task prefix
from the model id, and a family absent from its table resolves to empty prefixes rather than failing.

## Scope and decomposition

The cloud multi-model goal decomposes into three independently deployable pieces:

1. **The model configuration contract** — this spec.
2. **Serving topology** — whether embedding and generative serving split into separate deployments,
   and how `Embeddings__BaseUrl` / `Enrichment__BaseUrl` route if they do.
3. **GPU enablement** — GPU nodes on the existing `iverson.io/node-pool: ollama` pool, device-plugin
   time-slicing, per-deployment resource requests.

Piece 1 is a prerequisite for both others. Ben's ordering: 1, then 3, then 2 — GPU changes the
throughput picture, and the eviction contention motivating piece 2 may not survive it.

**Piece 1 was widened on 2026-09-01, at Ben's direction, to include per-type model selection**
(Part B below). The author's recommendation was to spec Part B separately, on the grounds that it
combines a four-file chart change with a five-client registration change and a migration problem;
Ben decided one document. The two parts remain independently implementable in the order A then B —
B's fallback is A's default, so A landing alone is a coherent state, while B alone is not.

## Part A — deploy-time model configuration

### Values shape

```yaml
global:
  embeddingModels:
    - name: nomic-embed-text
      # prefixes omitted -> EmbeddingPrefixes derives them from the model family
    - name: snowflake-arctic-embed:s
      documentPrefix: ""
      queryPrefix: "Represent this sentence for searching relevant passages: "
  activeEmbeddingModel: nomic-embed-text
```

`embeddingModels` is the set Ollama pulls. `activeEmbeddingModel` names the **default** model — the
one api and worker request for any registered type that does not declare its own (Part B). Both live under `global` for the same reason `generativeModel` does, recorded in that key's
own comment: the ollama subchart PULLS these and the api/worker subcharts REQUEST one of them, so
scoping them to one subchart would let the two drift.

`generativeModel` stays a scalar. Nothing here asked for multiple generative models, and it is
already correctly templated.

Membership is seeded with the two families `EmbeddingPrefixes.Table` knows. Revising it is a values
edit with no chart change — which is the point of the shape, and what lets the follow-up model
investigation (below) land without touching templates.

### The three-state prefix mechanism

This is the part that has to be exactly right, because Global Constraint 1 of the prefixes design
distinguishes three states and two of them look alike:

| values | rendered manifest | C# property | meaning |
|---|---|---|---|
| key omitted | env var absent | `null` | derive from the model family |
| `documentPrefix: ""` | `value: ""` | `""` | deliberately no prefix |
| `documentPrefix: "x"` | `value: "x"` | `"x"` | this prefix |

Arctic's document prefix genuinely **is** the empty string, so `""` cannot double as "unset". The
template must therefore *omit the env var entirely* rather than emit an empty one when the key is
absent — a `hasKey` guard, not a default:

```
{{- $active := include "iverson.activeEmbeddingModel" . | fromYaml }}
- name: Embeddings__ModelId
  value: {{ $active.name | default .Values.global.activeEmbeddingModel | quote }}
{{- if hasKey $active "documentPrefix" }}
- name: Embeddings__DocumentPrefix
  value: {{ $active.documentPrefix | quote }}
{{- end }}
{{- if hasKey $active "queryPrefix" }}
- name: Embeddings__QueryPrefix
  value: {{ $active.queryPrefix | quote }}
{{- end }}
```

The `default` on `Embeddings__ModelId` is load-bearing, not defensive tidiness. With no render-time
assertion, an `activeEmbeddingModel` naming no entry makes the helper return nothing, `fromYaml` yield
an empty dict, and `$active.name` render as YAML null — emitting a bare `value:` with nothing after
it. Falling back to the raw `activeEmbeddingModel` string instead renders the typo verbatim, so
Ollama returns a 404 naming the model nobody pulled. That is the diagnosable failure; a null
`value:` is not.

The conditional-env pattern is already established in this chart (`charts/api/templates/deployment.yaml:130`,
guarded on `global.tracingEnabled`).

### The active-entry helper

`templates/_helpers.tpl` **does not exist today** and is created by this work. It defines
`iverson.activeEmbeddingModel`, which resolves the entry whose `name` equals
`global.activeEmbeddingModel` and returns it as YAML for `fromYaml`.

One helper rather than an inline lookup in each subchart: api and worker must not be able to
implement the resolution differently. Named templates defined in the parent chart's `_helpers.tpl`
are reachable from subchart templates (verified by render).

### The pull loop

`charts/ollama/templates/statefulset.yaml:59` becomes a `range` over `global.embeddingModels`
emitting one `ollama pull {{ .name }}` per entry. The generative pull at `:60` is unchanged.

### Env wiring

Both api and worker get `Embeddings__ModelId` plus the active entry's prefixes, inserted next to the
existing `Embeddings__BaseUrl` (`charts/api/templates/deployment.yaml:124`,
`charts/worker/templates/deployment.yaml:119`).

**Both deployments get both prefixes**, even though api only embeds queries and worker only embeds
documents. They run the same `iverson-api` image with the role selected by `WorkloadRole`, so they
bind the same options object; configuring the two halves of one vector space independently is the
silent-garbage failure named above.

### Profiles

`values.yaml` carries the curated set. The three cloud profiles inherit it — they have `global:`
blocks but set no model keys, so the set is stated once rather than three times.

`values-laptop.yaml` and `values-local.yaml` override `embeddingModels` down to `nomic-embed-text`
alone: their PVCs are 8Gi and their CPU budgets are 250m, and a second model buys them nothing.
A profile override **replaces** a list rather than merging it, so a one-entry override is sufficient.
`values-local.yaml` has no `global:` block today and gains one.

`charts/ollama/Chart.yaml:3` currently reads "Ollama embedding service for Iverson (nomic-embed-text)";
the model name is dropped, since the chart no longer serves a fixed model.

## Part B — per-type model selection

### The attribute

A class-level, optional declaration in all five clients:

| Client | Mechanism | Precedent |
|---|---|---|
| .NET | `[IversonEmbeddingModel("...")]`, `AttributeTargets.Class` | `IversonEntityAttribute.cs:7` |
| Java | class annotation | the existing entity annotation |
| Python | decorator argument on the entity decorator | `@iverson_entity(description=...)` |
| TypeScript | class decorator | the existing entity decorator |
| Go | optional interface, e.g. `IversonEmbeddingModel() string` | `DescribedEntity` / `IversonDescription()`, `registrar.go:199-209`, checked on both value and pointer receivers |

**Placement is the class, not the `[IversonChunk]` property.** `is_embedding` and `is_chunk` are
independent per-property booleans on the same descriptor, one property can carry both
(`BenchmarkDocument.cs:16-18`), and a class may have embedding-only properties or several of them —
the one-chunk-per-type rule constrains chunk only. The class is the only placement that covers every
case, and it keeps exactly one model per type, so no query can fuse across incompatible vector spaces.

### Transport

The client stamps the declared value into `model_id` (field 8) and `chunk_model_id` (field 12) on
every property where `is_embedding` or `is_chunk` is true. Both wire fields already exist; no proto
change is required.

This is backward compatible by construction. Four clients assign `""` explicitly today —
`SchemaRegistrar.cs:166,174`, Java `:216,224`, Python `core.py:275,279`, TypeScript `core.ts:365,393` —
and Go omits the fields from its struct literal (`registrar.go:124`), sending the proto3 zero value.
`""` continues to mean "not declared", so an un-updated client keeps working with no server-side
special-casing.

### Server-side resolution

`SchemaBuilder.BuildDescriptor` takes an embedding-service **resolver** rather than a single service.
There is one production call site (`SchemaRegistrationOrchestrator.cs:67`) and roughly twenty-three
test call sites.

Resolution per type: the declared model if present, else `EmbeddingServiceOptions.ModelId` — which is
what Part A's `activeEmbeddingModel` sets. The resolver constructs a per-model `EmbeddingService` via
`Options.Create(...)` (its constructor takes `IOptions<EmbeddingServiceOptions>`) and caches one
instance per model id.

**Prefix rule.** The configured `Embeddings__DocumentPrefix` / `Embeddings__QueryPrefix` overrides
bind to a single options object, so they apply to the **default model only**. A type declaring a
different model derives its prefixes from `EmbeddingPrefixes.For()`. Without this rule a
nomic-shaped override would be stamped silently onto arctic's embeddings. The consequence is a real
limitation, accepted here: a per-type model must be one `EmbeddingPrefixes.Table` knows, because
there is no way to configure prefixes for a non-default model. Widening that means shipping the whole
`embeddingModels` list to the server as structured config; not built until something needs it.

### Dimension probing

The resolver probes each model once, at registration, and caches the result. This is the existing
shape, not a new one: `SchemaRegistrationOrchestrator.cs:52-63` already calls `EnsureInitializedAsync`
immediately before `BuildDescriptor` and maps failure to `Unavailable` with a message about
determining the vector dimension. Consequence: **registering a type against an unpulled model fails
at registration**, with a clear error, rather than at first ingest.

Client-supplied `vector_dim` / `chunk_vector_dim` remain 0 and ignored — dimensions are
server-determined today and stay that way. `Program.cs:406`'s startup probe stays, now meaning "the
default model is reachable".

### The re-registration guard

`SchemaRegistry.Get(typeName)` returns the previously registered descriptor. If its model differs
from the newly declared one, registration is **rejected**, naming the old model, the new one, and the
collection that must be dropped. Ben's decision, 2026-09-01.

This is the first reader of `VectorDescriptor.ModelId`, which is written at three sites today and
read nowhere.

Rejection is the only option that cannot silently corrupt a collection. Letting it through fails
loudly when dimensions differ, but two 768-dimension models produce no failure at all: points are
upserted by deterministic id, so the collection quietly accumulates vectors from two incompatible
spaces.

### Query path

`SearchSimilarRequest.type_name` and `SearchChunksRequest.type_name` identify the type. The server
resolves that type's model from the registered schema and embeds the query with it. Without this, a
query embedded with the default model would be compared against documents embedded with a declared
one — silently, since nothing about the comparison errors.

### Conformance harness

Both halves land in `Iverson.ClientConformance`:

- **A rejection scenario**, mirroring `Scenarios/NamingRejectedScenario.cs` and
  `Scenarios/TenantRejectedScenario.cs` and driving `Reregistrar.cs`, which already re-registers a
  type with a changed schema. All five drivers register a type, re-register it declaring a different
  model, and each must receive the same rejection.
- **A positive parity assertion made server-side.** The harness already probes state directly
  (`PostgresProbe.cs`), so it asserts that all five drivers' registrations resolve to the same model —
  without requiring a second model pulled in the conformance environment.

`GetSchemaResponse.SchemaField` carries `is_embedding` and `is_chunk` but **no model id**, so the
resolved model is not observable to a client over the wire. Exposing it would make the positive case
assertable by each driver directly and would give agent-facing schema discovery the model; that is an
additive proto change across five generated clients and is deliberately not taken here.


## Out of scope

- **`docker-compose.yml`** already sets `Embeddings__ModelId` explicitly. Not broken, not touched.
- **`ingest.py`** takes `--model` and is hand-run for benchmarking, not deployed.
- **`EmbeddingPrefixes.Table`** is unchanged. This design uses the configuration escape hatch the
  prefixes spec already documents ("Configuration is the escape hatch, which is why the override
  exists; the table is not designed to be exhaustive") rather than widening the table.
- **The prose comment at `templates/networkpolicies.yaml:416`** naming nomic-embed-text as an example
  of the external-pull need. It is illustrative, not a contract.
- **Serving topology and GPU** — pieces 2 and 3.
- **Per-property model selection.** Ruled out by Ben: one model per type, so no query fuses across
  incompatible vector spaces.
- **Multi-property search and cross-vector fusion.** A separate, previously costed avenue.
- **Migration tooling for a model change.** Rejection makes the drop a deliberate manual act.
- **Per-model prefix configuration**, per the prefix rule in Part B.
- **Exposing the resolved model through `GetSchema`.**

## Known issues and accepted decisions

**No render-time assertion that `activeEmbeddingModel` appears in `embeddingModels`.** Ben's decision,
2026-09-01, having seen the alternative: a `fail` in the helper would reject the mismatch at
`helm upgrade`. As specified, a mismatch renders cleanly and surfaces as a
404 from Ollama naming the unpulled model, on the first embed call — at first ingest or first query,
not at deploy. This holds only because of the `default` fallback described above; without it the
same mismatch emits a null `value:` and the runtime error names nothing useful.

**One failed `ollama pull` crashloops the init container, and N models means N chances of it.** Same
failure mode as today, N times more likely. Carried forward, not designed around.

**Stale `charts/*.tgz` silently shadow live subchart edits.** These are gitignored build artifacts
(`.gitignore:74`) present in the main checkout and absent in fresh worktrees. During verification of
this design they caused template edits to render as though they did not exist. Anyone implementing
this must `helm dependency build` (or delete the archives) before rendering, or they will conclude
their template is wrong. Separately and pre-existing: `deploy/kind/setup.sh:97` ends by instructing
`helm upgrade --install iverson . -f values-local.yaml` with no dependency-build step, so a stale
archive can deploy an old chart. Out of scope here; flagged because it is a live hazard.

**A per-type model must be one `EmbeddingPrefixes.Table` knows.** Configured prefix overrides bind to
the default model only (Part B's prefix rule), so a declared non-default model has no way to supply
prefixes and falls back to table derivation — which yields empty prefixes for an unknown family,
silently, rather than failing.

**The resolved model is not client-observable.** `GetSchemaResponse.SchemaField` has no model field,
so a client cannot read back which model its type resolved to. The conformance harness asserts this
server-side instead.

## Follow-up project (not this spec)

**Investigate the five best embedding models in Ollama.** Criteria to be defined. Its output revises
`global.embeddingModels`' membership; this design deliberately makes that a values edit rather than a
chart change. A model whose family is absent from `EmbeddingPrefixes.Table` must carry explicit
`documentPrefix`/`queryPrefix` in its entry, or it deploys with empty prefixes and no error.

## Verified assumptions

Verified against the codebase on 2026-09-01 at `main@359893f`, by reading files, rendering a
prototype of the full design with `helm template`, and probing .NET's configuration binder with a
standalone console app.

| # | Assumption | Evidence |
|---|---|---|
| A1 | The embedding pull is a hardcoded literal; the generative pull is templated | `charts/ollama/templates/statefulset.yaml:59-60` |
| A2 | Subcharts can read `.Values.global.*`, including a list of maps | already used at `charts/api/templates/deployment.yaml:129`; a debug ConfigMap rendered inside the ollama subchart returned both entries intact |
| A3 | **FAILED** — a `_helpers.tpl` exists | `find . -name "_helpers.tpl"` returned nothing. Design revised: the file is created by this work, and a prototype proved a parent-chart named template is reachable from the api subchart |
| A4 | `hasKey` and `fromYaml` are available | `helm version --short` → `v3.16.4`; both rendered correctly in the prototype |
| A5 | `range` renders correctly inside the `command:` block scalar | prototype rendered three correctly-indented `ollama pull` lines |
| A6 | A profile override replaces a list rather than merging | a one-entry override rendered exactly one embedding pull |
| A7 | `Embeddings__DocumentPrefix` binds to `EmbeddingServiceOptions.DocumentPrefix` under section `Embeddings` | standalone binder probe |
| A8 | An absent env var leaves the property `null` | binder probe: `DocumentPrefix = <NULL>` |
| A9 | An env var present with an empty value binds to `""`, not `null` | binder probe: `DocumentPrefix = ""`, raw env present `True` |
| A10 | The manifest emits `value: ""` for an explicit empty prefix | prototype render with arctic active. **Residual:** kubelet actually setting an empty-valued env var was not verified on a live cluster — no cluster was reachable — so this is verified only to the manifest boundary |
| A11 | Both prefixes are nullable and resolved with `?? EmbeddingPrefixes.For(...)` | `EmbeddingServiceOptions.cs:11-13`, `EmbeddingService.cs:20-22` |
| A12 | api and worker bind the same `Embeddings` section | `charts/worker/values.yaml:1-2` — same `iverson-api` image, role selected by `WorkloadRole` |
| A13 | Every profile has a `global:` block | **Partial** — `values-local.yaml` has none and gains one; the other five have one |
| A14 | Cloud profiles do not override `global` model keys, so they inherit | read of `values-aws.yaml`, `values-gcp.yaml`, `values-azure.yaml` |
| A15 | `Chart.yaml` names the model in its description | `charts/ollama/Chart.yaml:3` |
| A16 | The api and worker env lists have an insertion point near `Embeddings__BaseUrl` | `charts/api/templates/deployment.yaml:124`, `charts/worker/templates/deployment.yaml:119` |
| A17 | The full set of embedding-model references in the chart is enumerated | grep across all `*.yaml`/`*.tpl` found exactly five: the pull line, `Chart.yaml:3`, the `networkpolicies.yaml:416` comment, and the two `Embeddings__BaseUrl` anchors. No `Embeddings__ModelId` anywhere — the gap this design closes |
| A18 | All six profile values files were considered | enumerated and checked individually |
| A19 | Nothing else consumes `Embeddings__ModelId` in a way this breaks | absent from the chart entirely (A17); `docker-compose.yml` sets it independently and is out of scope |
| A20 | Local `helm` supports the templating used | `v3.16.4+g7877b45` |
| A21 | A mismatched `activeEmbeddingModel` produces a diagnosable failure | prototype render with `--set global.activeEmbeddingModel=typo-model`. **Initially false:** it emitted a bare `value:` (YAML null). Design amended to add `| default .Values.global.activeEmbeddingModel`; re-rendered as `value: "typo-model"`, and the two real cases still render correctly |

### Part B assumptions

| # | Assumption | Evidence |
|---|---|---|
| B1 | A class-level attribute has a home and precedent in .NET | `IversonEntityAttribute.cs:7` and `IversonDescriptionAttribute.cs:7` both use `AttributeTargets.Class` |
| B2 | Every client has a class-level declaration mechanism | .NET/Java/Python/TypeScript use attribute/annotation/decorator; Go uses an optional interface — `typeDescription` resolves `DescribedEntity.IversonDescription()` on both value and pointer receivers (`registrar.go:199-209`) |
| B3 | Each client has a site where the model fields are set | explicit `""` at `SchemaRegistrar.cs:166,174`, Java `:216,224`, Python `core.py:275,279`, TypeScript `core.ts:365,393` |
| B4 | The Go client has an equivalent site | **Differs:** Go omits `ModelId`/`ChunkModelId` from the `PropertyDescriptor` literal at `registrar.go:124` and relies on the proto3 zero value. Same wire result, but the edit is an addition rather than a change |
| B5 | `BuildDescriptor`'s call sites are enumerable | one production site (`SchemaRegistrationOrchestrator.cs:67`); ~23 test sites across `SchemaBuilderTests.cs` and `ServerOwnedTenantColumnTests.cs` |
| B6 | The singleton's injection sites are enumerable | `ServiceCollectionExtensions.cs:21` (registration), `Program.cs:406`, `IntelligenceStoreConsumer.cs:32`, `ObjectSearchGrpcService.cs:35`, `SchemaRegistrationOrchestrator.cs:17` |
| B7 | A per-model service is constructible | `EmbeddingService`'s primary constructor takes `IOptions<EmbeddingServiceOptions>` (`EmbeddingService.cs:9-12`); prefixes resolve in field initializers, so each instance derives its own |
| B8 | A schema is retrievable by type name at query time | `SchemaRegistry.Get(string)` at `:23-24`, over a `ConcurrentDictionary<string, SchemaDescriptor>` |
| B9 | Re-registration can see the prior descriptor | same registry lookup; the guard reads it before the overwrite |
| B10 | Probing at registration is the existing shape | `SchemaRegistrationOrchestrator.cs:52-63` already calls `EnsureInitializedAsync` before `BuildDescriptor` and maps failure to `Unavailable` |
| B11 | Qdrant collection creation already uses a per-vector dimension | `SchemaBuilder.cs:330,339-340` — `NamedVector(name, dimension)` per field |
| B12 | Nothing reads `VectorDescriptor.ModelId` today | grep across `Iverson.Server/` finds three writes in `SchemaBuilder.cs` and no production read; the guard is the first reader |
| B13 | Test fixtures do not assert model uniformity in a way this breaks | `SchemaRegistrationOrchestratorTests.cs:623,632` sends `ModelId = string.Empty` and asserts the resolved `nomic-embed-text` — that is the fallback path and still passes; consumer fixtures construct `ChunkDescriptor` literals directly, bypassing `BuildDescriptor` |
| B14 | The ingest contract survives per-type selection | `IngestContractTests.cs:117-130` iterates `EmbeddingPrefixes.Table` and pins the whole table plus a per-family golden, not one model |
| B15 | The startup probe still has a meaning | `Program.cs:406` probes the singleton, which becomes the default model's reachability check |

### Conformance-harness assumptions

| # | Assumption | Evidence |
|---|---|---|
| C1 | The harness has a rejection-scenario pattern to mirror | `Scenarios/NamingRejectedScenario.cs`, `Scenarios/TenantRejectedScenario.cs` |
| C2 | The harness can re-register a type with a changed schema | `Reregistrar.cs:10-32` — the documented seam scenarios re-register through |
| C3 | The harness can assert server-side state directly | `PostgresProbe.cs`, plus `Verifier.cs` as the pure-assertion home |
| C4 | The resolved model is **not** observable over the wire | `GetSchemaResponse` → `SchemaType` → `SchemaField` carries `is_embedding` and `is_chunk` but no model id (`object_mapping.proto:145-152`) |
