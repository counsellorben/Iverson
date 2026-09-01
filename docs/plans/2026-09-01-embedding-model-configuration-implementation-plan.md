# Embedding-Model Configuration and Per-Type Selection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-01-helm-embedding-model-configuration-design.md` (commit SHA: `f21373b`)

**Goal:** Make the embedding model a deploy-time value rather than an implicit contract between the chart and a C# default, and let a registered type select its own model.

**Architecture:** Part A moves the model into `global.embeddingModels` / `global.activeEmbeddingModel`, pulled by the ollama subchart and requested by api and worker through one shared helper. Part B adds an optional class-level model declaration in all five clients, resolves it server-side into a per-model `EmbeddingService`, routes that model to the schema, the write path and the query path, and rejects a re-registration that changes it.

**Tech stack:** Helm 3.16.4, .NET 10, Ollama, Qdrant, Postgres; client libraries in .NET, Java, Python, TypeScript and Go.

---

## Global Constraints

Project-wide rules every task must hold to. Copied from the spec.

1. **The prefix keys are three-state.** A key omitted from a values entry leaves the C# property `null`, which means "derive from the model family". A key set to `""` means "deliberately no prefix". Arctic's document prefix **is** the empty string, so `""` cannot double as unset — templates use `hasKey`, never `default`, for the two prefix vars.
2. **One model per type.** The declaration is class-level, never per-property. Every vector and chunk field of a type carries the same model, so no query can fuse across incompatible vector spaces.
3. **Configured prefix overrides apply to the default model only.** A type declaring a different model derives its prefixes from `EmbeddingPrefixes.For()`. Never copy `DocumentPrefix`/`QueryPrefix` onto a non-default model's options.
4. **`""` on the wire means "not declared".** Four clients send `""` explicitly and Go omits the fields; both remain valid and mean the same thing, so an un-updated client keeps working with no server-side special-casing.

## File Structure

**Create**
- `Iverson.Server/deploy/helm/iverson/templates/_helpers.tpl` — the `iverson.activeEmbeddingModel` named template; one resolution shared by api and worker.
- `Iverson.Server/Iverson.Embeddings/IEmbeddingServiceResolver.cs` — model id → `IEmbeddingService`.
- `Iverson.Server/Iverson.Embeddings/EmbeddingServiceResolver.cs` — cached per-model instances; the default returns the DI singleton.
- `Iverson.Server/Iverson.ClientConformance/SchemaProbe.cs` — reads `_iverson_schema` with the harness's own copy of the table name.
- `Iverson.Server/Iverson.ClientConformance/Scenarios/ModelRejectedScenario.cs` — the guard's conformance scenario.
- `Iverson.Clients/DotNet/Iverson.Client.Attributes/IversonEmbeddingModelAttribute.cs` and its four per-language equivalents.
- One vector-carrying conformance fixture per driver (five files).

**Modify**
- Chart: `values.yaml`, `charts/ollama/templates/statefulset.yaml`, `charts/{api,worker}/templates/deployment.yaml`, `values-laptop.yaml`, `values-local.yaml`, `charts/ollama/Chart.yaml`.
- Server: `Iverson.Embeddings/ServiceCollectionExtensions.cs`, `Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`, `Iverson.Api/Consumers/IntelligenceStoreConsumer.cs`, `Iverson.Api/Grpc/ObjectSearchGrpcService.cs`, `Iverson.Api/Schema/SchemaDescriptor.cs`.
- Harness: `Reregistrar.cs`, `Program.cs`, `Requirements.cs`, `Iverson.ClientConformance.Tests/ScriptedDriverRunner.cs`, `docs/standards/iverson-client-standard.md`.
- Clients: each language's registrar and its conformance driver.

**Test**
- `Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`, consumer and search-service tests, new resolver tests, `Iverson.ClientConformance.Tests`, and one registrar test per client language.

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and NOT re-verified here (A1–A21, B1–B17, C1–C7). The load-bearing ones:

- The embedding pull is a hardcoded literal one line above a templated generative pull (A1); subcharts read `.Values.global.*` including a list of maps (A2); `_helpers.tpl` does not exist and a parent-chart named template is reachable from a subchart (A3); a profile list override replaces rather than merges (A6).
- An absent env var binds `null`, an empty one binds `""` (A8, A9); both prefixes are nullable and resolved with `?? EmbeddingPrefixes.For(...)` (A11); api and worker bind the same `Embeddings` section (A12).
- `model_id` (8) and `chunk_model_id` (12) already exist on the wire; four clients assign `""`, Go omits (B3, B4). `EmbeddingService`'s constructor takes `IOptions<EmbeddingServiceOptions>` and resolves prefixes in field initializers, so each instance derives its own (B7).
- One production `BuildDescriptor` call site (B5); the five singleton injection sites (B6); probing at registration is the existing shape (B10); nothing reads `VectorDescriptor.ModelId` today (B12).
- The consumer already holds `schema` at both embed call sites (B16); `UnregisterAsync` has no production caller, so clearing a prior model is a manual act (B17).
- The harness has a rejection pattern to mirror (C1) and a re-registration seam (C2), cannot observe the resolved model today (C5), the model is not observable over the wire (C4), has no per-driver vector-carrying fixture (C6), and `Reregistrar` cannot supply a differing model (C7).

**Two spec statements this plan deliberately departs from, both Ben's calls during plan-drafting:**

1. The spec's *Server-side resolution* paragraph says `BuildDescriptor` takes a resolver, touching ~23 test call sites. This plan instead resolves one service per type in the orchestrator and passes it through the **existing** `IEmbeddingService` parameter. Same outcome, `SchemaBuilder` untouched, and one-model-per-type becomes structurally impossible to violate rather than merely conventional.
2. The spec's *Conformance harness* rejection bullet says each driver registers a second class bound to the same type name. Verified false as a general mechanism: only .NET honours a type-name override (`EntityRegistry.cs:31`); Java (`SchemaRegistrar.java:87`), Python (`annotations.py:222`), TypeScript (`annotations.ts:57`) and Go (`tags.go:207-208`) all derive the type name from the class/struct name with no override. This plan extends `Reregistrar` with a model override instead. **The spec's rejection bullet and its C7 row are stale as a result and need an `update-design-doc` pass.**

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time against `main@f21373b`.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | `templates/_helpers.tpl` does not exist chart-wide | `find . -name "_helpers.tpl"` under the chart returned nothing |
| P2 | File path | `values-laptop.yaml` has a `global:` block; `values-local.yaml` does not | `grep '^global:'` hits `values-laptop.yaml:12`, `values.yaml:21`, and the three cloud profiles; no hit in `values-local.yaml` |
| P3 | File path | `Iverson.Embeddings/` is the resolver's home | `IEmbeddingService.cs`, `EmbeddingService.cs`, `ServiceCollectionExtensions.cs` all sit there |
| P4 | File path | `Iverson.ClientConformance/SchemaProbe.cs` does not exist | `ls` → No such file |
| P5 | File path | `Scenarios/ModelRejectedScenario.cs` does not exist | `ls` → No such file |
| P6 | File path | `Iverson.ClientConformance.Tests` exists as a project | `Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj` |
| P7 | File path | .NET attributes live in `Iverson.Clients/DotNet/Iverson.Client.Attributes/` | `IversonEntityAttribute.cs` found at that path |
| P8 | File path | Java annotations live in `io/iverson/client/annotations/` | `IversonEmbedding.java` found at that path |
| P9 | File path | Per-language declaration homes | `Python/iverson_client/annotations.py`, `TypeScript/src/annotations.ts`, `Go/iverson/tags.go` |
| P10 | File path | Driver fixture homes | `DotNet/.../Driver/Models/`, `Java/conformance/src/main/java/io/iverson/conformance/models/`, `Python/conformance/models.py`, `TypeScript/conformance/models.ts`, `Go/conformance/models.go` |
| P11 | Signature | `EmbeddingService(IHttpClientFactory, IOptions<EmbeddingServiceOptions>, ILogger<EmbeddingService>)`, `public sealed` | `EmbeddingService.cs:9-12` |
| P12 | Signature | `EmbeddingService` never reads `options.Value.BaseUrl`; its only client is `httpClientFactory.CreateClient(Telemetry.HttpClientName)` | `EmbeddingService.cs:62`; `BaseAddress` bound at `ServiceCollectionExtensions.cs:12-19` |
| P13 | Signature | The orchestrator probes per type inside the phase-1 loop, so per-type probing is a local change | `SchemaRegistrationOrchestrator.cs:53-62` inside `foreach` at `:40`, `BuildDescriptor` at `:67` |
| P14 | Signature | `registry.Get` is used pre-`RegisterAsync` already; `RegisterAsync` is in phase 3 | `:218` reads the prior descriptor with the comment "while registry.Get still returns the PRIOR descriptor"; `:262` registers |
| P15 | Signature | `AddAnnotations(PropertyDescriptor, PropertyInfo)` is static and has no access to the entity descriptor | `SchemaRegistrar.cs:161` |
| P16 | Signature | **No `EntityDescriptor`/`EntityRegistry` change is needed** — `BuildTypeDescriptor(EntityDescriptor)` already reads a class-level attribute off `descriptor.EntityType` | `SchemaRegistrar.cs:52,56-57` reads `IversonDescriptionAttribute` the same way; `EntityDescriptor.cs:5-16` needs no new member |
| P17 | Signature | `IReregistrar.ReregisterAsync(JsonElement, string, string ownerField = "OwnerId", CancellationToken ct = default)` — an added optional parameter is source-compatible with all seven callers, which pass `ct:` by name | `Reregistrar.cs:18-22`; callers in `QueryScenario.cs:112`, `ErrorContractScenario.cs:127`, `IdentityScenario.cs:228`, `VectorSearchScenario.cs:168`, `SchemaCatalogScenario.cs:114`, `CrudRoundtripScenario.cs:88`, `InteropScenario.cs:84` |
| P18 | Signature | The driver reports its `TypeDescriptor` JSON through `Capture`, which is what `Reregistrar` parses | `Capture.cs:38-70`, `capture.Select(...)` used across the .NET driver |
| P19 | Signature | `schema` is in scope at both query-path embed sites | `ObjectSearchGrpcService.cs:188` and `:363` use `schema.Authorization` a few lines above `:201` / `:376` |
| P20 | Signature | `vf`/`cf` are in scope at both write-path embed sites | `IntelligenceStoreConsumer.cs:135` selects over `schema.VectorFields` carrying `vf`; `:190` is `foreach (var cf in schema.ChunkFields)` enclosing `:252` |
| P21 | Signature | Go's optional-interface pattern is copyable, value and pointer receivers | `registrar.go:199-209` (`typeDescription`) |
| P22 | Signature | Go's generated protos live in `Iverson.Clients/Go/generated/`; the registrar's struct literal omits both model fields today | `find -name "*.pb.go"` → `Go/generated/`; spec B4 for `registrar.go:124` |
| P23 | Command | `helm dependency build` works offline — every subchart is a local `file://` dependency | `Chart.yaml:7-10` (`repository: "file://charts/postgres"` and siblings) |
| P24 | Command | `helm template iverson . [-f values-*.yaml] [--set ...]` is the render form; helm is 3.16.4 | spec A20; chart root at `Iverson.Server/deploy/helm/iverson/` |
| P25 | Command | .NET tests run from `Iverson.slnx` at the repo root | `Iverson.slnx` present; no alternate invocation documented |
| P26 | Command | Per-language test commands: Maven, pytest, vitest, go test | `Java/pom.xml` (and `mvn -B -f Iverson.Clients/Java/pom.xml` in `codeql.yml:72`), `Python/pyproject.toml:17,25`, `TypeScript/package.json:16` (`npm test` = typecheck + vitest), `Go/go.mod` |
| P27 | Command | Commit messages are lowercase sentences with no Conventional-Commits prefix | `git log --oneline -12` |
| P28 | Ordering | T1 shares no file with T2–T10 | chart files vs. `Iverson.Server/` and `Iverson.Clients/` |
| P29 | Ordering | The guard can run in phase 1 before the probe — nothing mutates the registry until phase 3 | `registry.RegisterAsync` only at `:262`; `:218` already compares against `registry.Get` before it |
| P30 | Ordering | The harness renders a per-language skip for an unimplemented driver phase | `DriverPhaseOutcome.Skipped` → `ReportCell.Skip` at `VectorSearchScenario.cs:154,563-564` |
| P31 | Ordering | `ModelOf` is reachable from both consumers — orchestrator and search service are the same assembly | both under `Iverson.Api/Grpc/`; `SchemaDescriptor` is in `Iverson.Api/Schema/` |
| P32 | Code validity | The helper's `range` + `eq` + `toYaml` composition renders; a no-match returns empty, which `fromYaml` yields as an empty dict | to be confirmed by T1 Step 7's four renders; spec A4 covers `hasKey`/`fromYaml` availability |
| P33 | Code validity | `range` inside the `command:` block scalar renders with correct indentation | spec A5 rendered a prototype; T1 Step 7 re-renders this plan's exact text |
| P34 | Code validity | `Options.Create` is available and already used in-repo | `Iverson.Vector.Tests/VectorRankingOptionsTests.cs:26,32,59` |
| P35 | Code validity | `FailedPrecondition` is the file's status for state-dependent checks; `InvalidArgument` for input validation | `SchemaRegistrationOrchestrator.cs:201,259` vs. `:76,91,122,133,150,166` |
| P36 | Code validity | `Iverson.ClientConformance` already references Npgsql and Dapper | `Iverson.ClientConformance.csproj:14-15` |
| P37 | Consumer impact | `AddEmbeddings` has exactly one caller, so adding a registration to it is safe | `Program.cs:241` |
| P38 | Consumer impact | Adding a constructor parameter breaks 9 hand-constructions across tests | `new IntelligenceStoreConsumer(` ×2, `new ObjectSearchGrpcService(` ×3, `new SchemaRegistrationOrchestrator(` ×4 |
| P39 | Consumer impact | `IReregistrar` has a second implementer that must gain the parameter | `RecordingReregistrar` at `ClientConformance.Tests/ScriptedDriverRunner.cs:95,102` |
| P40 | Consumer impact | Exactly one client test asserts the model field, and it still passes — its fixture declares no model, so `""` is still correct | `DotNet/Iverson.Client.Core.Tests/SchemaRegistrarTests.cs:293` `bodyProp.ModelId.Should().BeEmpty()`; no equivalent assertion in the Java, Python, TypeScript or Go test suites |
| P41 | Consumer impact | The conformance environment pulls one model, and this plan keeps it that way — the guard fires before the probe, so the rejection needs no second pull | T3 Step 2's placement; `docker-compose.yml:372,457` set `Embeddings__ModelId=nomic-embed-text` |
| P42 | Code validity | `SchemaProbe` can be constructed the way `PostgresProbe` is | `Program.cs:17` reads `IVERSON_POSTGRES_CS` (default `Database=iverson`) into `postgresCs`, already passed to `new PostgresProbe(postgresCs)` at `:108` and `:119` |
| P43 | Code validity | A `jsonb` column reads back as `string`, so T5's `ExecuteScalarAsync(...) is not string json` holds | `SchemaRegistryRepository.LoadAllAsync:17-18` maps `schema_json` into a `string` tuple element in production today |
| P44 | Code validity | The write path needs no probed dimension, so the worker's unprobed per-model instances are safe | `_dimension` is read only by the `Dimension` getter (`EmbeddingService.cs:25-28`); `EmbedDocumentAsync`/`EmbedQueryAsync` route through `EmbedAsync` (`:54-95`), which never touches it |
| P45 | Code validity | `Requirements.cs` is gated at build time against the standard, not a free-form list | `Requirements.cs:5-14` states the contract; `RequirementsCoverageGateTests.cs:211` (Check 1, bidirectional), `:368` (Check 2, citation), `:796` with `:1038`/`:1095` (Check 4, exactly one claimant); `:115` resolves `docs/standards/iverson-client-standard.md`, `:92-94` fixes the nine known axes |

## Tasks

### Task 1: Chart — deploy-time model configuration

Independent of every other task; may run in parallel with T2–T4.

**Files:**
- Create: `Iverson.Server/deploy/helm/iverson/templates/_helpers.tpl`
- Modify: `Iverson.Server/deploy/helm/iverson/values.yaml`, `charts/ollama/templates/statefulset.yaml:59`, `charts/api/templates/deployment.yaml` (after `:125`), `charts/worker/templates/deployment.yaml` (after `:120`), `values-laptop.yaml`, `values-local.yaml`, `charts/ollama/Chart.yaml:3`

- [ ] **Step 1: Add the model keys to `values.yaml`'s `global:` block, beside `generativeModel`.**
```yaml
  # The embedding models Ollama pulls. Under `global` for the same reason generativeModel is:
  # the ollama subchart PULLS these and the api/worker subcharts REQUEST one of them, so
  # scoping them to one subchart would let the pulled and requested sets drift apart.
  #
  # The prefix keys are three-state and the distinction is load-bearing. Omitting a key leaves
  # the C# property null, which means "derive from the model family"; setting it to "" means
  # "deliberately no prefix". Arctic's document prefix IS the empty string, so "" cannot
  # double as unset — which is why the templates use `hasKey`, not `default`.
  embeddingModels:
    - name: nomic-embed-text
      # prefixes omitted -> EmbeddingPrefixes derives them from the model family
    - name: snowflake-arctic-embed:s
      documentPrefix: ""
      queryPrefix: "Represent this sentence for searching relevant passages: "
  # The default model api and worker request for any registered type that does not declare its
  # own. Must name an entry above; a mismatch renders cleanly and surfaces as a 404 from Ollama
  # naming the unpulled model on the first embed call, not at deploy.
  activeEmbeddingModel: nomic-embed-text
```

- [ ] **Step 2: Create `templates/_helpers.tpl`.**
```
{{/*
Resolve the active embedding-model entry from global.embeddingModels, as YAML for `fromYaml`.
One helper rather than an inline lookup per subchart: api and worker must not be able to
implement the resolution differently. A name matching no entry emits nothing, which fromYaml
yields as an empty dict — handled by the `default` on Embeddings__ModelId at the call sites.
*/}}
{{- define "iverson.activeEmbeddingModel" -}}
{{- $name := .Values.global.activeEmbeddingModel -}}
{{- range .Values.global.embeddingModels -}}
{{- if eq .name $name -}}
{{- toYaml . -}}
{{- end -}}
{{- end -}}
{{- end -}}
```

- [ ] **Step 3: Replace the hardcoded pull at `charts/ollama/templates/statefulset.yaml:59` with a range.**
```
              ollama serve &
              sleep 5
              {{- range .Values.global.embeddingModels }}
              ollama pull {{ .name }}
              {{- end }}
              ollama pull {{ .Values.global.generativeModel }}
```

- [ ] **Step 4: Insert the same env block into `charts/api/templates/deployment.yaml` and `charts/worker/templates/deployment.yaml`, immediately after each file's `Embeddings__BaseUrl` name/value pair.**

Both deployments get both prefixes even though api only embeds queries and worker only embeds documents: they run the same image with the role selected by `WorkloadRole`, so they bind the same options object, and configuring the two halves of one vector space independently is the silent-garbage failure this design exists to prevent.
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
The `| default` is load-bearing, not defensive tidiness: without it an `activeEmbeddingModel` naming no entry renders a bare `value:` (YAML null) and the runtime error names nothing useful. With it, the typo renders verbatim and Ollama 404s by name.

- [ ] **Step 5: Trim the model list in the two laptop-class profiles.** Add to `values-laptop.yaml`'s existing `global:` block, and create a `global:` block in `values-local.yaml` (map keys merge, so it still inherits every other global value):
```yaml
  # One model only: the PVC is 8Gi and ollama's CPU request is 250m, so a second buys nothing
  # here. A profile list override REPLACES rather than merges, so one entry is sufficient.
  embeddingModels:
    - name: nomic-embed-text
```
Leave the three cloud profiles alone — they have `global:` blocks but set no model keys, so the curated set is stated once.

- [ ] **Step 6: Drop the model name from `charts/ollama/Chart.yaml:3`** — the chart no longer serves a fixed model: `description: Ollama model server for Iverson`.

- [ ] **Step 7: Render-verify — `helm dependency build` FIRST, and this is not optional.**

Stale `charts/*.tgz` silently shadow live subchart edits. They are gitignored build artifacts (`.gitignore:74`), twelve are present in this checkout, and during the design's own verification they caused template edits to render as though they did not exist.
```bash
cd Iverson.Server/deploy/helm/iverson
helm dependency build
helm template iverson . | grep -E 'ollama pull|Embeddings__'
helm template iverson . -f values-local.yaml | grep -c 'ollama pull'
helm template iverson . --set global.activeEmbeddingModel=snowflake-arctic-embed:s \
  | grep -A1 'Embeddings__DocumentPrefix'
helm template iverson . --set global.activeEmbeddingModel=typo | grep -A1 'Embeddings__ModelId'
```
Expected: default render has three pulls, `Embeddings__ModelId` = `"nomic-embed-text"`, and **no** prefix env vars (nomic derives them). The local render has two pulls (one embedding + one generative). The arctic render emits `value: ""` for the document prefix and the full sentence for the query prefix. The typo render emits `value: "typo"`, **not** a bare `value:`.

- [ ] **Step 8: Commit.**
```bash
git add Iverson.Server/deploy/helm/iverson/values.yaml \
        Iverson.Server/deploy/helm/iverson/templates/_helpers.tpl \
        Iverson.Server/deploy/helm/iverson/charts/ollama/templates/statefulset.yaml \
        Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml \
        Iverson.Server/deploy/helm/iverson/charts/worker/templates/deployment.yaml \
        Iverson.Server/deploy/helm/iverson/values-laptop.yaml \
        Iverson.Server/deploy/helm/iverson/values-local.yaml \
        Iverson.Server/deploy/helm/iverson/charts/ollama/Chart.yaml
git commit -m "configure the embedding model through helm values"
```

---

### Task 2: Server — embedding-service resolver and per-type resolution

**Files:**
- Create: `Iverson.Server/Iverson.Embeddings/IEmbeddingServiceResolver.cs`, `Iverson.Server/Iverson.Embeddings/EmbeddingServiceResolver.cs`
- Modify: `Iverson.Server/Iverson.Embeddings/ServiceCollectionExtensions.cs:21`, `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`
- Test: `Iverson.Server/Iverson.Embeddings.Tests/` (resolver), `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`

**Interfaces:**
- Produces: `IEmbeddingServiceResolver` (consumed by T4's four embed sites), and a `DeclaredModel(TypeDescriptor)` helper on the orchestrator (consumed by T3's guard).

- [ ] **Step 1: Define the resolver interface.**
```csharp
namespace Iverson.Embeddings;

public interface IEmbeddingServiceResolver
{
    /// <summary>
    /// The service for <paramref name="modelId"/>, cached per model. Null or empty resolves to
    /// the configured default — which is what "" means on the wire.
    /// </summary>
    IEmbeddingService Get(string? modelId);
}
```

- [ ] **Step 2: Implement it. The prefix rule falls out of construction, not out of a separate check.**
```csharp
public sealed class EmbeddingServiceResolver(
    IHttpClientFactory httpClientFactory,
    IOptions<EmbeddingServiceOptions> options,
    IEmbeddingService defaultService,
    ILogger<EmbeddingService> serviceLogger) : IEmbeddingServiceResolver
{
    private readonly ConcurrentDictionary<string, IEmbeddingService> _byModel = new(StringComparer.Ordinal);

    public IEmbeddingService Get(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId) ||
            string.Equals(modelId, options.Value.ModelId, StringComparison.Ordinal))
            return defaultService;

        return _byModel.GetOrAdd(modelId, m => new EmbeddingService(
            httpClientFactory,
            // DocumentPrefix/QueryPrefix are deliberately NOT copied. The configured overrides
            // are shaped for the DEFAULT model, and stamping a nomic-shaped prefix onto arctic's
            // embeddings is exactly the misconfiguration EmbeddingPrefixes exists to prevent.
            // Left null, the field initializers derive this model's own pair from the table.
            Options.Create(new EmbeddingServiceOptions { BaseUrl = options.Value.BaseUrl, ModelId = m }),
            serviceLogger));
    }
}
```
`BaseUrl` is carried across for completeness only: `EmbeddingService` never reads it, since its client comes from the named `HttpClient` whose `BaseAddress` is bound once at registration.

- [ ] **Step 3: Register the resolver as a singleton** in `AddEmbeddings`, immediately after the existing `services.AddSingleton<IEmbeddingService, EmbeddingService>();` at `:21`.

- [ ] **Step 4: Extract the declared model in the orchestrator.** One private static helper, reading the INBOUND `typeDesc` — the first non-empty model across the properties that carry one:
```csharp
// The declaration is class-level in every client, so every embedding/chunk property of a type
// carries the same value; taking the first is therefore taking the type's model, not one
// field's. Empty means "not declared" — four clients send "" and Go omits the fields.
private static string? DeclaredModel(TypeDescriptor typeDesc) =>
    typeDesc.Properties
        .Select(p => p.IsEmbedding ? p.ModelId : p.IsChunk ? p.ChunkModelId : null)
        .FirstOrDefault(m => !string.IsNullOrEmpty(m));
```

- [ ] **Step 5: Probe the type's model, not the singleton.** In the phase-1 loop, replace the `await embedding.EnsureInitializedAsync(ct)` at `:55` with a probe of the resolved service:
```csharp
var service = resolver.Get(DeclaredModel(typeDesc));
try
{
    await service.EnsureInitializedAsync(ct);
}
catch (Exception ex)
{
    throw new RpcException(new Status(StatusCode.Unavailable,
        $"Embedding service is unavailable, so schema registration cannot determine the vector "
        + $"dimension. Check that Ollama is reachable and retry. ({ex.Message})"));
}
```
The message is the existing one at `:59-61`, unchanged. Then pass `service` to
`BuildDescriptor(typeDesc, service)` at `:67`. **T3 inserts its guard between the `resolver.Get`
call and the `EnsureInitializedAsync` await**, so keep those two statements adjacent and separable. `BuildDescriptor`'s signature is unchanged, so its ~23 test call sites are untouched, and because it stamps `embedding.ModelId` onto every vector and chunk field, a type cannot end up with two models. Registering against an unpulled model still fails at registration with the existing `Unavailable` message rather than at first ingest.

- [ ] **Step 6: Tests.**
  - The resolver returns the same instance for null, `""`, and the configured default; a distinct, cached instance for any other model id.
  - A non-default model derives its prefixes from `EmbeddingPrefixes.For()` even when `DocumentPrefix`/`QueryPrefix` are configured — assert the configured override does **not** appear on the non-default instance.
  - `DeclaredModel` returns null for a type whose properties all send `""`, and the declared value when one carries it.
  - The existing `EmbeddableDoc` fixture (sends `ModelId = string.Empty`, asserts the resolved `nomic-embed-text`) still passes — this is the fallback arm.
  - Update the 4 `new SchemaRegistrationOrchestrator(` constructions for the added resolver parameter.

- [ ] **Step 7: Run and commit.**
```bash
dotnet test Iverson.slnx
git add Iverson.Server/Iverson.Embeddings/ Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs Iverson.Server/Iverson.Api.Tests/
git commit -m "resolve a per-type embedding service at schema registration"
```

---

### Task 3: Server — the re-registration guard

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs` (the shared `ModelOf` helper), `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`

**Interfaces:**
- Consumes: T2's `DeclaredModel` and resolver, so a descriptor carries a per-type model.
- Produces: `ModelOf(SchemaDescriptor)` — consumed by T4's query path. Defined once, here.

- [ ] **Step 1: Add the shared `ModelOf` helper** beside `SchemaDescriptor`:
```csharp
// A type's model lives on its vector and chunk fields, which exist only when it has embedding
// or chunk properties — so null here means "this type has no embedded content", not "unknown".
// Read by the re-registration guard and by the query path; one definition, because two copies
// that disagree would reject a legal registration or embed a query with the wrong model.
internal static string? ModelOf(SchemaDescriptor d) =>
    d.VectorFields.FirstOrDefault()?.ModelId ?? d.ChunkFields.FirstOrDefault()?.ModelId;
```

- [ ] **Step 2: Add the guard to the phase-1 loop, BEFORE T2 Step 5's probe.**

Placement is load-bearing in two directions. It must be before the probe, or re-registering with a model the deployment has not pulled returns `Unavailable` from the probe instead of the rejection — which would make the conformance scenario require a second model pulled. And it must be before phase 3, which is where `registry.RegisterAsync` overwrites the prior descriptor; `:218` already reads `registry.Get` pre-overwrite for the document-template comparison, so this is an established move in this file.
Insert it between T2 Step 5's `resolver.Get(...)` and its `EnsureInitializedAsync` await. The
resolved service already knows its own model, so the guard needs no new dependency on
`IOptions<EmbeddingServiceOptions>`:
```csharp
var priorModel = registry.Get(typeDesc.TypeName) is { } prior ? SchemaDescriptor.ModelOf(prior) : null;

// Null when this registration carries no embedded content at all — a type that has just lost its
// last embedding/chunk property is not changing its model, it is ceasing to have one, and the
// write path already supports that. Taking service.ModelId here instead would reject exactly that
// evolution whenever the deployment default has moved on.
var hasEmbedded = typeDesc.Properties.Any(p => p.IsEmbedding || p.IsChunk);
var nextModel   = hasEmbedded ? service.ModelId : null;

if (priorModel is not null && nextModel is not null &&
    !string.Equals(priorModel, nextModel, StringComparison.Ordinal))
{
    throw new RpcException(new Status(StatusCode.FailedPrecondition,
        $"Type '{typeDesc.TypeName}' is registered with embedding model '{priorModel}', but this "
        + $"registration resolves to '{nextModel}'. Changing a type's model would leave one "
        + $"collection holding vectors from two incompatible spaces, which no dimension check "
        + $"catches when the two models share a dimension. To change it, BOTH clear the schema "
        + $"row and drop the collection: "
        + $"DELETE FROM _iverson_schema WHERE type_name = '{typeDesc.TypeName}'; "
        + $"then drop Qdrant collection '{SchemaBuilder.ToTableName(typeDesc.TypeName)}'. "
        + $"Dropping the collection alone leaves this row, and the next registration is "
        + $"rejected identically."));
}
```
The condition is three-way, matching the spec: the prior descriptor must carry a model, this registration must resolve to one, and the two must differ. A type gaining its first embedded property has `priorModel == null` and is allowed — that is the `missingVectors` → `MigrateCollectionAsync` migration the write path already supports. A type losing its last has `nextModel == null` and is allowed: it is removing vectors, not mixing two spaces, and `SchemaBuilder.cs:213` gives it a null `CollectionName`, so a rejection would name a collection that will not exist. `hasEmbedded` is read off the inbound request's properties, not off vector/chunk fields, because the guard runs before `BuildDescriptor` produces any.

`FailedPrecondition`, not `InvalidArgument`: this file already splits the two that way — input validation throws `InvalidArgument` (`:76,91,122,133,150,166`), checks against already-registered state throw `FailedPrecondition` (`:201,259`). The declaration is not invalid; it conflicts with state.

- [ ] **Step 3: Tests.**
  - Re-registering the same type with the same model succeeds.
  - Re-registering with a different declared model throws `FailedPrecondition`, and the message names the old model, the new model, **and both** the `DELETE` and the collection.
  - A type with no embedded properties gaining its first one registers cleanly (absent → present).
  - A type losing its last embedded property registers cleanly (present → absent).
  - An undeclared type whose deployment default has changed is rejected — the guard compares resolved models, not declared ones.
  - **The discriminating case: the deployment default has changed AND the type drops its last embedded property → registers cleanly.** Neither of the two cases above catches this alone — a two-check guard taking `nextModel = service.ModelId` unconditionally passes both of them and fails only this one.

- [ ] **Step 4: Run and commit.**
```bash
dotnet test Iverson.slnx
git add Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs
git commit -m "reject a re-registration that changes a type's embedding model"
```

---

### Task 4: Server — write path and query path embed with the type's model

Independent of T3; both consume T2. Either order.

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs:32,140,252`, `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:35,201,376`
- Test: the consumer's and search service's existing test files

**Interfaces:**
- Consumes: T2's `IEmbeddingServiceResolver`; T3's `SchemaDescriptor.ModelOf`.

- [ ] **Step 1: Write path.** Inject `IEmbeddingServiceResolver` into `IntelligenceStoreConsumer` alongside (or in place of) `IEmbeddingService` at `:32`. At `:140`, the enclosing `Select` already carries `vf`; at `:252`, the closure sits inside `foreach (var cf in schema.ChunkFields)` at `:190`:
```csharp
vector: await resolver.Get(x.vf.ModelId).EmbedDocumentAsync(x.text!, ct)      // :140
var chunkVector = await resolver.Get(cf.ModelId).EmbedDocumentAsync(textToEmbed, ct);   // :252
```
Per field rather than per type because that is what the descriptor carries; one model per type means the two always agree.

- [ ] **Step 2: Query path.** Inject the resolver into `ObjectSearchGrpcService` at `:35`. `schema` is already in scope at both sites (`:188`, `:363`):
```csharp
queryVector = await resolver.Get(SchemaDescriptor.ModelOf(schema))
    .EmbedQueryAsync(request.Query, context.CancellationToken);
```
Leave both `EmptyEmbeddingInputException` catch blocks exactly as they are — the guard is on the raw text and is unaffected.

- [ ] **Step 3: Tests.**
  - A type whose descriptor names a non-default model embeds its documents *and* its chunks with that model — asserted against a fake resolver, not against Ollama.
  - The same type's queries embed with the same model, for both `SearchSimilar` and `SearchChunks`.
  - A type whose descriptor carries the default still uses the default on both paths.
  - Update the 2 `new IntelligenceStoreConsumer(` and 3 `new ObjectSearchGrpcService(` constructions for the added parameter.

- [ ] **Step 4: Run and commit.**
```bash
dotnet test Iverson.slnx
git add Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests/
git commit -m "embed documents and queries with the type's own model"
```

---

### Task 5: Harness — schema-table read, model override, rejection scenario

**Files:**
- Create: `Iverson.Server/Iverson.ClientConformance/SchemaProbe.cs`, `Iverson.Server/Iverson.ClientConformance/Scenarios/ModelRejectedScenario.cs`
- Modify: `Iverson.Server/Iverson.ClientConformance/Reregistrar.cs`, `Program.cs`, `Requirements.cs`, `Iverson.Server/Iverson.ClientConformance.Tests/ScriptedDriverRunner.cs`
- Test: `Iverson.Server/Iverson.ClientConformance.Tests/`

**Interfaces:**
- Consumes: T3's guard and its status code; T4's resolution.
- Produces: the driver phase name and step names that T6–T10 implement against.

- [ ] **Step 1: `SchemaProbe`** — the harness's own read of the schema-registry table:
```csharp
/// <summary>
/// Reads a registered type's resolved embedding model straight out of Postgres. The harness
/// cannot deserialize SchemaDescriptor — Iverson.ClientConformance has no project reference to
/// Iverson.Api — and the model is not on the wire, so this is the only way to observe it.
///
/// The table name is this project's OWN copy, for exactly the reason PostgresProbe.TableName is
/// (PostgresProbe.cs:20-23): a harness sharing the server's own constant could not catch the
/// server using a different one. Connects as the table-owning role, like PostgresProbe.
/// </summary>
public sealed class SchemaProbe(string connectionString)
{
    public const string SchemaTable = "_iverson_schema";

    /// <summary>The resolved model on <paramref name="typeName"/>'s registered schema, or null
    /// when the type is unregistered or carries no embedded content.</summary>
    public async Task<string?> FetchModelAsync(string typeName, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            $"SELECT schema_json FROM {SchemaTable} WHERE type_name = @t", connection);
        command.Parameters.AddWithValue("t", typeName);

        if (await command.ExecuteScalarAsync(ct) is not string json) return null;

        // vectorFields first, chunkFields second — the same order as SchemaDescriptor.ModelOf, so
        // a type with only a chunked property is still observable. camelCase because
        // SchemaRegistry serialises with JsonNamingPolicy.CamelCase.
        using var doc = JsonDocument.Parse(json);
        foreach (var collection in new[] { "vectorFields", "chunkFields" })
            if (doc.RootElement.TryGetProperty(collection, out var arr) &&
                arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                return arr[0].GetProperty("modelId").GetString();

        return null;
    }
}
```

- [ ] **Step 2: Give `Reregistrar` a model override.** Add `string? modelId = null` to `IReregistrar.ReregisterAsync` and to `Reregistrar.ReregisterAsync`, rewriting `model_id`/`chunk_model_id` on every embedding/chunk property of the parsed descriptor before re-posting — the same shape as the existing `descriptor.Authorization = Rules(ownerField)` assignment. Default null leaves every existing caller's behaviour identical. **`RecordingReregistrar` (`ScriptedDriverRunner.cs:95-102`) implements the interface and must gain the parameter too.**

- [ ] **Step 3: `ModelRejectedScenario`, per language.** Mirrors `NamingRejectedScenario` / `TenantRejectedScenario` in shape. For each language: take the driver's reported fixture descriptor, re-register that type name through `Reregistrar` with a different model, and assert `FailedPrecondition` plus a message naming both artifacts. No second model needs pulling, because T3's guard runs before the probe.

- [ ] **Step 4: The positive parity assertion.** Read all five fixtures' rows through `SchemaProbe` and assert they resolve to the same model.

**State the limitation in the scenario's own doc comment rather than overselling it:** in a single-model environment this cannot distinguish "the client stamped the declared model" from "the client sent `""` and the server fell back to the same value". Per-client stamping is pinned by a client-side unit test in each of T6–T10; this assertion covers server-side parity across the five, which is what it is for.

- [ ] **Step 5: Wire it up — the requirement ID lands in four places, not one.** `Requirements.cs` is not a list a task may append to: `RequirementsCoverageGateTests` enforces its contract as an ordinary test, so adding the const alone turns Step 7's `dotnet test` red.
  1. **The standard.** A row in `docs/standards/iverson-client-standard.md`'s REG requirement table: `| IVC-REG-006 | Active | Behaviour | The server rejects re-registration of a type whose resolved embedding model differs from the model its registered schema carries |`. REG is "Schema registration and reregistration behaviour", its four Active rows are all "The server rejects registration of …", and `IVC-REG-006` is unused. Check 1 is bidirectional — a const with no Active row fails exactly as an Active row with no const does.
  2. **The const** in `Requirements.cs`, carrying the rationale-and-discharging-assertion doc comment the axis preamble says this document's convention requires.
  3. **The citation.** Check 2 requires the const's C# identifier to appear as a whole identifier, outside a whole-line comment, under `Iverson.ClientConformance/` excluding `Requirements.cs`, build output and the test project — so Steps 3 and 4 construct their assertions citing `Requirements.<NewConst>`.
  4. **The coverage row.** One new Covered area in REG's `#### Coverage` ledger citing `IVC-REG-006`. Check 4 requires exactly one claimant — unclaimed is Mode 5, two claimants is Mode 7. The ledger already carries a `Reregistration | Deferred` row whose Evidence reads "no assertion cites a requirement ID against that behaviour", which this work makes false: update or narrow that row's text, but do **not** also cite `IVC-REG-006` from it.

  Then scenario registration in `Program.cs`, and a per-language skip for a driver that has not implemented the phase yet, following `DriverPhaseOutcome.Skipped` → `ReportCell.Skip` (`VectorSearchScenario.cs:154,563-564`).

- [ ] **Step 6: Tests** in `Iverson.ClientConformance.Tests`, driving the scenario through `ScriptedDriverRunner` as the existing scenario tests do.

- [ ] **Step 7: Run and commit.**
```bash
dotnet test Iverson.slnx
git add Iverson.Server/Iverson.ClientConformance/ Iverson.Server/Iverson.ClientConformance.Tests/ \
        docs/standards/iverson-client-standard.md
git commit -m "add the model-rejection conformance scenario and a schema-table probe"
```

---

### Task 6: .NET client — the declaration, stamping, and its fixture

Canonical for T7–T10. Each of those repeats these steps with a different mechanism.

**Files:**
- Create: `Iverson.Clients/DotNet/Iverson.Client.Attributes/IversonEmbeddingModelAttribute.cs`, `Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Models/DotNetModelDoc.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/SchemaRegistrar.cs`, `Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Program.cs`
- Test: `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/SchemaRegistrarTests.cs`

- [ ] **Step 1: The attribute**, beside `IversonEntityAttribute.cs`:
```csharp
/// <summary>
/// Declares the embedding model for every embedded and chunked property of this type. Optional;
/// omitted means the deployment's default model. Class-level, not per-property: one model per
/// type is what keeps a query from fusing across two incompatible vector spaces.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class IversonEmbeddingModelAttribute(string modelId) : Attribute
{
    public string ModelId { get; } = modelId;
}
```

- [ ] **Step 2: Stamp it in `BuildTypeDescriptor`.** No `EntityRegistry` or `EntityDescriptor` change is needed: `BuildTypeDescriptor(EntityDescriptor descriptor)` already reads a class-level attribute off `descriptor.EntityType` two lines in (`SchemaRegistrar.cs:56-57`, for `IversonDescriptionAttribute`). Read the model the same way, then stamp it in one pass after the properties are built, rather than threading it through the static `AddAnnotations(PropertyDescriptor, PropertyInfo)`:
```csharp
var model = descriptor.EntityType.GetCustomAttribute<IversonEmbeddingModelAttribute>()?.ModelId;
if (model is not null)
    foreach (var p in typeDesc.Properties)
    {
        if (p.IsEmbedding) p.ModelId      = model;
        if (p.IsChunk)     p.ChunkModelId = model;
    }
```
Undeclared types keep sending `""` from `AddAnnotations` at `:166`/`:174` — which is what makes this backward compatible with no server-side special-casing.

- [ ] **Step 3: The client-side stamping test.** This is where stamping is falsifiable — the conformance harness cannot distinguish a stamped default from a server-side fallback:
  - A type carrying `[IversonEmbeddingModel("snowflake-arctic-embed:s")]` sends that value on **both** `ModelId` and `ChunkModelId` for a property that is both embedded and chunked.
  - A type without the attribute still sends `""` on both. The existing assertion at `SchemaRegistrarTests.cs:293` is exactly this case and must keep passing unchanged.

- [ ] **Step 4: The conformance fixture** `Models/DotNetModelDoc.cs` — `[IversonEntity]`, a UUID key, an owner field, `[IversonEmbeddingModel("nomic-embed-text")]`, one `[IversonEmbedding]` property and one `[IversonChunk]` property. Declaring the default explicitly exercises the whole declaration path while keeping the conformance environment single-model.

- [ ] **Step 5: The driver's register-phase step** — register `DotNetModelDoc` and report its descriptor through `Capture`, so T5's `Reregistrar` has JSON to mutate. Use `OnlySendTypeName` for the register-once discipline the other fixtures follow.

- [ ] **Step 6: Run and commit.**
```bash
dotnet test Iverson.slnx
git add Iverson.Clients/DotNet/
git commit -m "declare a per-type embedding model from the dotnet client"
```

---

### Task 7: Java client

Repeats T6's six steps. Deltas only:

- [ ] **Step 1:** `@IversonEmbeddingModel(String value())`, `@Target(ElementType.TYPE)`, `@Retention(RUNTIME)`, in `io/iverson/client/annotations/` beside `IversonEmbedding.java`.
- [ ] **Step 2:** Read `cls.getAnnotation(IversonEmbeddingModel.class)` in `core/SchemaRegistrar.java` near the `.setTypeName(cls.getSimpleName())` at `:87`, and stamp in place of the `b.setModelId("")` at `:216` and `b.setChunkModelId("")` at `:224`.
- [ ] **Steps 3-5:** stamping test in `Iverson.Clients/Java/client/src/test/java/io/iverson/client/`; fixture `conformance/src/main/java/io/iverson/conformance/models/JavaModelDoc.java`; driver register-phase step in `conformance/.../ConformanceDriver.java`.
- [ ] **Step 6:** `mvn -B -f Iverson.Clients/Java/pom.xml test`, then commit.

### Task 8: Python client

- [ ] **Step 1:** `@iverson_entity(embedding_model="...")` — a new keyword-only parameter on the existing decorator at `annotations.py:222`, stored into `cls._iverson_meta`.
- [ ] **Step 2:** Stamp in place of `model_id=""` at `core.py:275` and `chunk_model_id=""` at `:279`.
- [ ] **Steps 3-5:** stamping test in `Iverson.Clients/Python/tests/`; fixture `PyModelDoc` in `conformance/models.py`; driver register-phase step in `conformance/driver.py`.
- [ ] **Step 6:** `pytest` from `Iverson.Clients/Python/`, then commit.

### Task 9: TypeScript client

- [ ] **Step 1:** `IversonEmbeddingModel(modelId: string): ClassDecorator` in `src/annotations.ts`, storing through `Reflect.defineMetadata` beside `IVERSON_ENTITY_KEY`.
- [ ] **Step 2:** Stamp at **four** sites in `src/core.ts` — `modelId: ''` at `:361` and `:389`, `chunkModelId: ''` at `:365` and `:393`. Two code paths, both of which must carry the declared value; the second is the one a single-site edit would silently miss.
- [ ] **Steps 3-5:** stamping test in `Iverson.Clients/TypeScript/tests/`; fixture `TsModelDoc` in `conformance/models.ts`; driver register-phase step in `conformance/driver.ts`.
- [ ] **Step 6:** `npm test` from `Iverson.Clients/TypeScript/` (typecheck + vitest), then commit.

### Task 10: Go client

- [ ] **Step 1:** An optional interface in `iverson/`, mirroring `DescribedEntity`:
```go
// EmbeddingModelEntity is implemented by entities that declare their embedding model.
type EmbeddingModelEntity interface{ IversonEmbeddingModel() string }
```
- [ ] **Step 2:** A resolver copying `typeDescription`'s shape at `registrar.go:199-209` — check the value, then a pointer to it, so pointer-receiver implementations are honoured. Stamp into the `pb.PropertyDescriptor` literal at `registrar.go:124`. **This is an addition, not a change:** Go omits `ModelId`/`ChunkModelId` today and relies on the proto3 zero value, so the fields must be added to the literal (guarded on `IsEmbedding`/`IsChunk` to keep `""` for everything else).
- [ ] **Steps 3-5:** stamping test in `Iverson.Clients/Go/iverson/registrar_test.go`, covering both value and pointer receivers; fixture `GoModelDoc` in `conformance/models.go`; driver register-phase step in `conformance/main.go`.
- [ ] **Step 6:** `go test ./...` from `Iverson.Clients/Go/`, then commit.

## Tasks NOT in this plan

Inherited from the spec's "Out of scope" section:

- **`docker-compose.yml`** already sets `Embeddings__ModelId` explicitly. Not broken, not touched.
- **`ingest.py`** takes `--model` and is hand-run for benchmarking, not deployed.
- **`EmbeddingPrefixes.Table`** is unchanged. This design uses the configuration escape hatch the prefixes spec already documents rather than widening the table.
- **The prose comment at `templates/networkpolicies.yaml:416`** naming nomic-embed-text as an example of the external-pull need. It is illustrative, not a contract.
- **Serving topology and GPU** — pieces 2 and 3.
- **Per-property model selection.** Ruled out by Ben: one model per type, so no query fuses across incompatible vector spaces.
- **Multi-property search and cross-vector fusion.** A separate, previously costed avenue.
- **Migration tooling for a model change**, and **an unregister path**. Rejection makes clearing both artifacts a deliberate manual act.
- **Per-model prefix configuration**, per the prefix rule in Part B.
- **Exposing the resolved model through `GetSchema`.**

## Known issues inherited from spec

These exist in the implementation by design — accepted during brainstorming.

**No render-time assertion that `activeEmbeddingModel` appears in `embeddingModels`.** Ben's decision, having seen the alternative: a `fail` in the helper would reject the mismatch at `helm upgrade`. As specified, a mismatch renders cleanly and surfaces as a 404 from Ollama naming the unpulled model, on the first embed call — at first ingest or first query, not at deploy. This holds only because of the `default` fallback; without it the same mismatch emits a null `value:` and the runtime error names nothing useful.

**One failed `ollama pull` crashloops the init container, and N models means N chances of it.** Same failure mode as today, N times more likely. Carried forward, not designed around.

**Stale `charts/*.tgz` silently shadow live subchart edits.** Gitignored build artifacts (`.gitignore:74`) present in the main checkout and absent in fresh worktrees. Anyone implementing this must `helm dependency build` (or delete the archives) before rendering, or they will conclude their template is wrong. Separately and pre-existing: `deploy/kind/setup.sh:97` ends by instructing `helm upgrade --install` with no dependency-build step, so a stale archive can deploy an old chart. Out of scope here; flagged because it is a live hazard.

**A per-type model must be one `EmbeddingPrefixes.Table` knows.** Configured prefix overrides bind to the default model only, so a declared non-default model has no way to supply prefixes and falls back to table derivation — which yields empty prefixes for an unknown family, silently, rather than failing.

**The resolved model is not client-observable.** `GetSchemaResponse.SchemaField` has no model field, so a client cannot read back which model its type resolved to. The conformance harness asserts this server-side instead.
