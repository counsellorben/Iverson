# Helm Embedding-Model Configuration

**Status:** design, approved 2026-09-01. Piece 1 of a three-piece decomposition (see "Scope and decomposition").

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

## Design

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

`embeddingModels` is the set Ollama pulls. `activeEmbeddingModel` names the one api and worker
request. Both live under `global` for the same reason `generativeModel` does, recorded in that key's
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
  value: {{ $active.name | quote }}
{{- if hasKey $active "documentPrefix" }}
- name: Embeddings__DocumentPrefix
  value: {{ $active.documentPrefix | quote }}
{{- end }}
{{- if hasKey $active "queryPrefix" }}
- name: Embeddings__QueryPrefix
  value: {{ $active.queryPrefix | quote }}
{{- end }}
```

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

## Out of scope

- **`docker-compose.yml`** already sets `Embeddings__ModelId` explicitly. Not broken, not touched.
- **`ingest.py`** takes `--model` and is hand-run for benchmarking, not deployed.
- **`EmbeddingPrefixes.Table`** is unchanged. This design uses the configuration escape hatch the
  prefixes spec already documents ("Configuration is the escape hatch, which is why the override
  exists; the table is not designed to be exhaustive") rather than widening the table.
- **The prose comment at `templates/networkpolicies.yaml:416`** naming nomic-embed-text as an example
  of the external-pull need. It is illustrative, not a contract.
- **Serving topology and GPU** — pieces 2 and 3.

## Known issues and accepted decisions

**No render-time assertion that `activeEmbeddingModel` appears in `embeddingModels`.** Ben's decision,
2026-09-01, having seen the alternative: a `fail` in the helper would reject the mismatch at
`helm upgrade`. As specified, a mismatch renders cleanly and surfaces as a 404 from Ollama on the
first embed call — at first ingest or first query, not at deploy.

**One failed `ollama pull` crashloops the init container, and N models means N chances of it.** Same
failure mode as today, N times more likely. Carried forward, not designed around.

**Stale `charts/*.tgz` silently shadow live subchart edits.** These are gitignored build artifacts
(`.gitignore:74`) present in the main checkout and absent in fresh worktrees. During verification of
this design they caused template edits to render as though they did not exist. Anyone implementing
this must `helm dependency build` (or delete the archives) before rendering, or they will conclude
their template is wrong. Separately and pre-existing: `deploy/kind/setup.sh:97` ends by instructing
`helm upgrade --install iverson . -f values-local.yaml` with no dependency-build step, so a stale
archive can deploy an old chart. Out of scope here; flagged because it is a live hazard.

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
