# Laptop Deployment Profile Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-07-27-laptop-deployment-profile-design.md` (commit SHA: `475b6c594ce20a8d9b047c3f6e8f75e7f870845e`)

**Goal:** Make the Helm chart composable, add a laptop-sized preset, and make "deployed without an engagement store" a supported configuration rather than a broken one.

**Architecture:** Two `global.*` flags each drive one subchart's `condition:` *and* the api/worker env vars that talk to it, so chart and server cannot drift. Server-side, disabling the engagement store unregisters its consumer, swaps its search service for a null object that throws `FailedPrecondition`, and drops it from the readiness verdict. Separately, embedding initialization stops being fatal at startup and moves to the one code path that actually needs the vector dimension.

**Tech stack:** Helm 3 (`apiVersion: v2` umbrella chart, 12 subcharts), .NET / C# (`Iverson.Server.slnx`), kind + `deploy/kind/setup.sh`, GitHub Actions chart validation (`helm lint`, `kubeconform`, `kube-score`).

---

## Global Constraints

Copied verbatim from the spec; every task must hold to these.

- **Defaults preserve today's behaviour exactly.** `values.yaml` and the three cloud overlays are unaffected. The change is purely additive.
- **Both global flags must be defined, not merely referenced.** `global.engagementEnabled: true` and `global.tracingEnabled: true` go in `values.yaml`. Helm treats the two consumers asymmetrically: an undefined `condition:` path "has no effect" (subchart still renders) while an undefined template-guard value is falsey (env block omitted). Leaving either undefined deploys a component while stripping the env that talks to it, in every profile, with no failed pod to signal it.
- **Exactly one flag per concern.** There is deliberately no separate `starrocks.enabled` or `jaeger.enabled`; two flags that must "agree" can disagree, and the disagreeing state reproduces the `CreateContainerConfigError` the guards exist to prevent.
- **Commit messages use Conventional Commits** — the repo's log shows `feat(ts-client):`, `fix(enrichment):`, `docs(specs):`.
- **`EngagementNotReadyException` is not reused for the disabled case.** It maps to `Unavailable` ("retry later"); a client would retry forever against a store that will never exist. `FailedPrecondition` is the honest signal.

## File Structure

**Create**
- `Iverson.Server/deploy/helm/iverson/values-laptop.yaml` — laptop preset.
- `Iverson.Server/Iverson.StarRocks/EngagementStoreOptions.cs` — `Engagement` config section.
- `Iverson.Server/Iverson.StarRocks/EngagementStoreDisabledException.cs` — signal for the disabled path.
- `Iverson.Server/Iverson.StarRocks/DisabledEngagementStoreSearchService.cs` — null object for the four search methods.

**Modify**
- `Iverson.Server/deploy/helm/iverson/Chart.yaml` — `condition:` on all 12 dependencies.
- `Iverson.Server/deploy/helm/iverson/values.yaml` — `enabled` defaults + the two `global.*` flags.
- `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml` — env guards.
- `Iverson.Server/deploy/helm/iverson/charts/worker/templates/deployment.yaml` — env guards.
- `Iverson.Server/deploy/helm/iverson/values-local.yaml` — StarRocks dev floor, ollama storage.
- `.github/workflows/deploy-validate.yml` — include the new overlay in all three validation loops.
- `Iverson.Server/deploy/kind/setup.sh` — metrics-server.
- `Iverson.Server/Iverson.Api/Program.cs` — consumer gate, readiness arg, non-fatal embedding init.
- `Iverson.Server/Iverson.Api/ReadinessPolicy.cs` — enabled flag in the verdict.
- `Iverson.Server/Iverson.StarRocks/ServiceCollectionExtensions.cs` — conditional search-service registration.
- `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` — one catch clause.
- `Iverson.Server/Iverson.Embeddings/IEmbeddingService.cs` — `EnsureInitializedAsync`.
- `Iverson.Server/Iverson.Embeddings/EmbeddingService.cs` — idempotent init.
- `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` — await init at the chokepoint.

**Test**
- `Iverson.Server/Iverson.Api.Tests/ReadinessPolicyTests.cs` — 4 existing calls gain an argument, plus disabled-case rows.
- `Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs` — fake gains the new interface member.
- New/extended tests colocated with the projects above, per each task.

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and **not** re-verified here. Trusted as ground truth (spec's `Verified assumptions`, A1–A28):

- A1–A2: Helm 3.16.4 + `apiVersion: v2` support `condition:`; all 12 dependencies have matching top-level values keys including the `adminUi` alias.
- A3: api and worker hold a hard `secretKeyRef` to `-starrocks-app`, owned by the starrocks subchart — the reason the guard table exists.
- A4: laptop app-side requests ≈1.45 CPU; total ≈89% of a 4-CPU node.
- A7–A9: `EngagementStoreConsumer` registration is gateable; StarRocks DDL happens only in that consumer via `EnsureTenantProvisionedAsync`; no per-store completion tracking in the reconciliation queue.
- A10–A15: search and aggregate both route through `IEngagementStoreSearchService`; `ReadinessPolicy.Evaluate` callers are contained; only that consumer branches on `StoreTarget.Engagement`; `AddStarRocks:20` is the swap seam; subcharts cannot read sibling top-level values.
- A16–A20: one hand-written `NoOpEmbeddingService`; one production `InitializeAsync` call site; `EmbedAsync` does not need the dimension; the readiness endpoint can take DI params.
- A21–A24: local-path-provisioner ignores capacity limits; StarRocks has no laptop-viable sizing; metrics-server is absent from `setup.sh`.
- A27–A28: api and worker share `/health` and both gate readiness on it; removing `ConnectionStrings__StarRocks` is non-crashing (`Program.cs:165-167` falls back to `localhost:9030`).

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | The four Task-1 chart files exist; all five guard-table references are real | `Chart.yaml` 44 lines, `values.yaml` 175, `charts/api/.../deployment.yaml` 199, `charts/worker/.../deployment.yaml` 163. Read of `api:85-91`, `api:127`, `api:155`, `worker:78-84`, `worker:122` |
| P2 | File path | `values-laptop.yaml` does not exist; `values-local.yaml` and `setup.sh` do | `ls` — laptop absent; `values-local.yaml` 2987b, `deploy/kind/setup.sh` 4321b |
| P3 | File path | The three new server files do not exist (no name conflict) | `[ -f ]` on `EngagementStoreOptions.cs`, `EngagementStoreDisabledException.cs`, `DisabledEngagementStoreSearchService.cs` — all absent |
| P4 | File path | All eight modify-target server files exist | `[ -f ]` on each — all present |
| P5 | Signature | `ReadinessPolicy.Evaluate(bool, EngagementHealthStatus, bool, bool)`; enum is `Healthy`/`AuthPending`/`Unhealthy` | `ReadinessPolicy.cs:14-15`; `EngagementHealthStatus.cs` |
| P6 | Signature | The null object must implement **four** methods with these exact optional-parameter lists: `SearchAsync` (8 params), `AggregateAsync` (7), `GroupByAsync` (4), `PipelineAsync` (4) | Read of `IEngagementStoreRoles.cs:24-52` |
| P7 | Signature | `IEmbeddingService` = `Dimension`, `ModelId`, `InitializeAsync(CancellationToken)`, `EmbedAsync(string, CancellationToken)` | Read of `IEmbeddingService.cs` |
| P8 | Signature | **`AddStarRocks` takes no `IConfiguration`** — `(IServiceCollection, string connectionString, EngagementResilienceOptions? = null)`. The flag must therefore be a new **optional** parameter | `ServiceCollectionExtensions.cs:8-11`; 3 call sites: `Program.cs:165`, `ServiceCollectionExtensionsTests.cs:19,36` — both tests omit the new arg and keep compiling |
| P9 | Signature | The consumer registration sits in an `if (workloadRole == "worker")` block with `cfg` in scope | `Program.cs:244-247`; `cfg` used in the same scope at `:235` |
| P10 | Signature | `SchemaRegistrationOrchestrator` has `IEmbeddingService embedding` as a primary-ctor param and `RegisterAsync` is async | `SchemaRegistrationOrchestrator.cs:15-19`, `:29` |
| P11 | Command | Server solution is `Iverson.Server/Iverson.Server.slnx` | `ls Iverson.Server/*.slnx` |
| P12 | Command | **A chart-validation harness already exists** with three hardcoded overlay loops and three step names saying "all 4 values overlays" | `.github/workflows/deploy-validate.yml:31,58,86` — `for values in values-local values-aws values-azure values-gcp` feeding helm-lint, kubeconform, kube-score |
| P13 | Command | Commit convention is Conventional Commits | `git log --format=%s -8`: `feat(ts-client):`, `fix(enrichment):`, `docs(specs):` |
| P14 | Code validity | Options binding is `services.Configure<TOptions>(config.GetSection(TOptions.Section))` | `Iverson.Embeddings/ServiceCollectionExtensions.cs:10,27` |
| P15 | Code validity | **`checks` is an anonymous type with a `bool starrocks`** — emitting `"disabled"` for the same property requires `(object)` boxing or it will not compile | Read of `Program.cs:301-308` |
| P16 | Ordering | Task 5 depends only on Task 4's options type; Task 2 only on Task 1's flags; Tasks 1, 3, 4, 6 are mutually independent | Cross-checked each task's inputs against every other task's created symbols |
| P17 | Consumer impact | Adding an interface member breaks exactly one hand-written fake | `NoOpEmbeddingService` at `StartupNoOpFakes.cs`; all other test doubles are `Substitute.For<IEmbeddingService>()` |
| P18 | Consumer impact | Changing `Evaluate`'s signature touches 5 call sites | `grep -c`: `Program.cs` 1, `ReadinessPolicyTests.cs` 4 |
| P19 | Consumer impact | Only `EngagementRepository` implements `IEngagementStoreSearchService`; tests substitute the interface | `EngagementRepository.cs:16`; `ObjectSearchGrpcServiceTests.cs:38`, `ObjectSearchVectorIntegrationTests.cs:77` use `Substitute.For` |
| P20 | Consumer impact | Nothing reads the `/health` body's `starrocks` field | `grep "checks.starrocks"` — only comments in `EngagementHealthChecker.cs:39` and `ReadinessPolicy.cs:12` |
| P21 | Code validity | **The OTLP env vars are `Otel__Endpoint` and `Jaeger__OtlpHttpUrl`, not `OTEL_EXPORTER_OTLP_ENDPOINT`** — the spec's testing bullet names an env var that does not exist in this chart | `charts/api/.../deployment.yaml:126,154`; `charts/worker/.../deployment.yaml:121`. The worker has one OTLP entry; the api has two |

## Tasks

### Task 1: Chart composability

**Files:**
- Modify: `Iverson.Server/deploy/helm/iverson/Chart.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/values.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/worker/templates/deployment.yaml`

**Interfaces:**
- Produces: `global.engagementEnabled` and `global.tracingEnabled` (Task 2 sets them); the env var name `Engagement__Enabled` (Task 4 reads it server-side).

- [ ] **Step 1: Add `condition:` to all 12 dependencies in `Chart.yaml`**

Ten dependencies get `condition: <key>.enabled`. Two get a global path, because they also gate env vars in the api and worker charts and must not be settable twice:

```yaml
  - name: starrocks
    version: "0.1.0"
    repository: "file://charts/starrocks"
    condition: global.engagementEnabled
  - name: jaeger
    version: "0.1.0"
    repository: "file://charts/jaeger"
    condition: global.tracingEnabled
```

The remaining ten use their own key — `postgres.enabled`, `kafka.enabled`, `qdrant.enabled`, `ollama.enabled`, `api.enabled`, `worker.enabled`, `adminUi.enabled` (the dependency is `admin-ui` with `alias: adminUi`, so the condition uses the alias), `prometheus.enabled`, `redis.enabled`, `authentik.enabled`.

- [ ] **Step 2: Define the values in `values.yaml`**

Add `enabled: true` to each of the ten subchart value blocks. Add both global flags to the existing `global:` block, beside `ingressHost` and `generativeModel`:

```yaml
global:
  ingressHost: "iverson.local"
  # Drives the starrocks subchart's condition AND the api/worker env guards AND
  # Engagement__Enabled. One flag, because two flags that must agree can disagree —
  # and the disagreeing state renders a secretKeyRef against a Secret that no longer
  # exists. Same reasoning as generativeModel below.
  engagementEnabled: true
  # Drives the jaeger subchart's condition AND the api/worker OTLP env vars.
  tracingEnabled: true
```

Both must be defined, not merely referenced — see Global Constraints for why an undefined value behaves differently for a `condition:` than for a template guard.

- [ ] **Step 3: Guard the StarRocks env entries in the api and worker deployments**

In `charts/api/templates/deployment.yaml`, wrap the whole block spanning `STARROCKS_APP_PASSWORD` and `ConnectionStrings__StarRocks` (lines 85-91) and add the server flag beside it:

```yaml
            {{- if .Values.global.engagementEnabled }}
            - name: STARROCKS_APP_PASSWORD
              valueFrom:
                secretKeyRef:
                  name: {{ .Release.Name }}-starrocks-app
                  key: app-password
            - name: ConnectionStrings__StarRocks
              value: "Server={{ .Release.Name }}-starrocks-fe-service;Port=9030;Database=iverson;User Id=iverson_app;Password=$(STARROCKS_APP_PASSWORD);AllowPublicKeyRetrieval=true;"
            {{- end }}
            - name: Engagement__Enabled
              value: {{ .Values.global.engagementEnabled | quote }}
```

`Engagement__Enabled` sits **outside** the `if`, so it is always emitted carrying the flag's value. If it were inside, disabling the store would omit the variable entirely and `EngagementStoreOptions.Enabled` would fall back to its `true` default — re-enabling the store the flag was meant to disable.

Apply the identical change to `charts/worker/templates/deployment.yaml` at lines 78-84.

- [ ] **Step 4: Guard the OTLP env entries**

The api has **two** OTLP entries; the worker has **one**. In `charts/api/templates/deployment.yaml` wrap the `Otel__Endpoint` entry (name at `:126`, value at `:127`) and the `Jaeger__OtlpHttpUrl` entry (name at `:154`, value at `:155`):

```yaml
            {{- if .Values.global.tracingEnabled }}
            - name: Otel__Endpoint
              value: "http://{{ .Release.Name }}-jaeger:4317"
            {{- end }}
```

and likewise around `Jaeger__OtlpHttpUrl`. In `charts/worker/templates/deployment.yaml`, wrap only the `Otel__Endpoint` entry (name at `:121`, value at `:122`) — the worker has no HTTP OTLP entry.

- [ ] **Step 5: Verify the render, both directions**

Dependencies must be rebuilt before templating: `charts/*.tgz` produced by `helm dependency build` shadows live edits to `charts/<subchart>/templates/*`, so a `helm template` run against stale archives proves nothing.

```bash
cd Iverson.Server/deploy/helm/iverson
helm dependency build
# defaults unchanged: all four env vars present
helm template t . | grep -c "Otel__Endpoint\|Jaeger__OtlpHttpUrl\|ConnectionStrings__StarRocks\|Engagement__Enabled"
# disabled: no reference to the starrocks Secret anywhere
helm template t . --set global.engagementEnabled=false | grep -c "starrocks-app"   # expect 0
helm template t . --set global.tracingEnabled=false | grep -c "Otel__Endpoint"     # expect 0
# subchart actually disappears
helm template t . --set global.engagementEnabled=false | grep -c "kind: StarRocksCluster"  # expect 0
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/deploy/helm/iverson/Chart.yaml Iverson.Server/deploy/helm/iverson/values.yaml Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml Iverson.Server/deploy/helm/iverson/charts/worker/templates/deployment.yaml
git commit -m "feat(helm): make all 12 subcharts conditional and guard cross-subchart env"
```

---

### Task 2: Laptop preset and `values-local` sizing

**Files:**
- Create: `Iverson.Server/deploy/helm/iverson/values-laptop.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/values-local.yaml`
- Modify: `.github/workflows/deploy-validate.yml`

**Interfaces:**
- Consumes: Task 1's `global.engagementEnabled`, `global.tracingEnabled`, and the ten `<key>.enabled` values.

- [ ] **Step 1: Create `values-laptop.yaml`**

Each disabled component must use the key that actually drives it — `jaeger` and `starrocks` are global-conditioned, the rest are not:

```yaml
# Laptop profile — fits a 4-CPU / 9GB host by omitting what a developer least needs.
# values-local.yaml remains the full-fidelity profile for a >=16GB machine.
#
# Capacity: app side ~1.45 CPU of requests; system + operators measured ~2.0 CPU;
# metrics-server ~0.1. Total ~3.55 of 4.0 (~89%). The margin is thin — roughly 450m.
# It works because requests are reservations: ollama's 3-CPU limit still bursts into
# unreserved capacity during inference. Re-measure with `kubectl top` after deploying.
global:
  engagementEnabled: false   # no StarRocks: vendor sizes FE at 8 CPU/16GB
  tracingEnabled: false      # no jaeger

prometheus:
  enabled: false
adminUi:
  enabled: false
```

Everything else inherits `values.yaml`. Do **not** restate resource blocks that `values-local.yaml` already sets — this preset is layered, not a fork.

- [ ] **Step 2: Update `values-local.yaml` StarRocks sizing and ollama storage**

Replace the StarRocks FE and BE resource blocks with the documented dev floor, and raise ollama storage:

```yaml
# StarRocks dev floor. The vendor sizes FE at 8 CPU / 16GB and BE at 16 CPU / 64GB;
# there is no laptop-viable sizing, so this profile assumes a machine with >=16GB.
# These values are a starting point to confirm empirically, not vendor minimums.
# For a laptop, use values-laptop.yaml, which omits StarRocks entirely.
starrocks:
  storageClassName: "standard"
  fe:
    replicas: 1
    storageSize: 1Gi
    resources:
      requests: { cpu: "1", memory: "4Gi" }
      limits:   { cpu: "2", memory: "6Gi" }
  be:
    replicas: 1
    storageSize: 2Gi
    resources:
      requests: { cpu: "1", memory: "4Gi" }
      limits:   { cpu: "2", memory: "6Gi" }
```

and in the `ollama:` block, `storageSize: 2Gi` → `storageSize: 8Gi`. This is documentation of intent, not a bug fix — see Known issues.

- [ ] **Step 3: Add the new overlay to all three CI validation loops**

`.github/workflows/deploy-validate.yml` hardcodes the overlay list in **three** places — `:31` (helm lint), `:58` (kubeconform), `:86` (kube-score). Update every one, or `values-laptop.yaml` is never validated:

```bash
for values in values-local values-laptop values-aws values-azure values-gcp; do
```

Also update the three step names that read "all 4 values overlays" to "all 5 values overlays".

- [ ] **Step 4: Verify the preset renders and lints**
```bash
cd Iverson.Server/deploy/helm/iverson
helm dependency build
helm lint . -f values-laptop.yaml
helm template t . -f values-laptop.yaml | grep -c "kind: StarRocksCluster"   # expect 0
helm template t . -f values-laptop.yaml | grep -c "starrocks-app"            # expect 0
helm template t . -f values-laptop.yaml | grep -c "Otel__Endpoint"           # expect 0
helm template t . -f values-laptop.yaml | grep -c "name: t-prometheus"       # expect 0
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/deploy/helm/iverson/values-laptop.yaml Iverson.Server/deploy/helm/iverson/values-local.yaml .github/workflows/deploy-validate.yml
git commit -m "feat(helm): add laptop values profile and raise values-local starrocks floor"
```

---

### Task 3: metrics-server in the kind setup script

**Files:**
- Modify: `Iverson.Server/deploy/kind/setup.sh`

- [ ] **Step 1: Install metrics-server alongside the other operators**

Add before the closing "All operators installed." echo, matching the file's existing `helm upgrade --install … --wait` style:

```bash
echo "Installing metrics-server..."
# Without it both HPAs report cpu: <unknown> and never restore a Deployment that was
# manually scaled to 0 — which is how the worker (and with it EnrichmentConsumer) sat
# at 0 replicas during the 2026-07-27 smoke test with no error anywhere. Also makes
# `kubectl top` work, which the laptop profile's capacity numbers depend on.
# --kubelet-insecure-tls is required on kind: kubelet serves a self-signed cert that
# metrics-server will otherwise reject.
helm upgrade --install metrics-server metrics-server \
  --repo https://kubernetes-sigs.github.io/metrics-server/ \
  --namespace kube-system \
  --set 'args={--kubelet-insecure-tls}' \
  --wait
```

- [ ] **Step 2: Commit**
```bash
git add Iverson.Server/deploy/kind/setup.sh
git commit -m "fix(kind): install metrics-server so HPAs can compute"
```

Verifying this end-to-end requires a live kind cluster, which this plan does not stand up. The script is not executed here; `bash -n Iverson.Server/deploy/kind/setup.sh` confirms it still parses.

---

### Task 4: Engagement store disable plumbing

**Files:**
- Create: `Iverson.Server/Iverson.StarRocks/EngagementStoreOptions.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Modify: `Iverson.Server/Iverson.Api/ReadinessPolicy.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/ReadinessPolicyTests.cs`

**Interfaces:**
- Produces: `EngagementStoreOptions` (Task 5 consumes it).
- Consumes: the env var `Engagement__Enabled`, emitted by Task 1.

- [ ] **Step 1: Add the options class**

Mirrors `EmbeddingServiceOptions` / `EnrichmentServiceOptions`:

```csharp
namespace Iverson.StarRocks;

public sealed class EngagementStoreOptions
{
    public const string Section = "Engagement";

    /// <summary>
    /// When false, the engagement store is not deployed: the consumer is not registered,
    /// search/aggregate fail with FailedPrecondition, and StarRocks is dropped from the
    /// readiness verdict. Defaults true so existing deployments are unaffected.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
```

Register it in `Program.cs` beside the other options bindings, using the established pattern:

```csharp
builder.Services.Configure<EngagementStoreOptions>(cfg.GetSection(EngagementStoreOptions.Section));
```

- [ ] **Step 2: Write the `ReadinessPolicy` tests first**

`ReadinessPolicyTests.cs` has four existing `ReadinessPolicy.Evaluate(...)` calls; each gains the new argument. Keep every existing `AuthPending` case passing — that logic exists to avoid a first-install deadlock. Add rows asserting that when the store is disabled, an `Unhealthy` StarRocks no longer blocks readiness and `FullyHealthy` is still reported.

- [ ] **Step 3: Add the flag to `ReadinessPolicy.Evaluate`**

```csharp
    public static ReadinessResult Evaluate(
        bool postgresHealthy,
        EngagementHealthStatus starRocksStatus,
        bool qdrantHealthy,
        bool kafkaHealthy,
        bool engagementEnabled = true)
    {
        if (!engagementEnabled)
        {
            var readyWithoutEngagement = postgresHealthy && qdrantHealthy && kafkaHealthy;
            return new ReadinessResult(readyWithoutEngagement, readyWithoutEngagement);
        }

        var ready = postgresHealthy && qdrantHealthy && kafkaHealthy
            && starRocksStatus != EngagementHealthStatus.Unhealthy;
        var fullyHealthy = ready && starRocksStatus == EngagementHealthStatus.Healthy;

        return new ReadinessResult(ready, fullyHealthy);
    }
```

The parameter is optional so the four test call sites and any future caller compile unchanged; the production call site passes it explicitly.

- [ ] **Step 4: Report `"disabled"` in the health body**

In the `/health` handler, `checks` is an anonymous type whose `starrocks` member is currently `bool`. Reporting a string for the same member requires boxing, or the anonymous type will not compile:

```csharp
    var engagementEnabled = engagementOptions.Value.Enabled;
    var checks = new
    {
        postgres  = pgTask.Result,
        starrocks = engagementEnabled ? (object)(srStatus == EngagementHealthStatus.Healthy) : "disabled",
        qdrant    = vectorTask.Result,
        kafka     = kafkaTask.Result
    };

    var readiness = ReadinessPolicy.Evaluate(
        pgTask.Result, srStatus, vectorTask.Result, kafkaTask.Result, engagementEnabled);
```

Note the `Evaluate` call now takes `pgTask.Result` rather than `checks.postgres`, because `checks.starrocks` is no longer a `bool` and reading the others from the anonymous type alongside it reads inconsistently. Inject `IOptions<EngagementStoreOptions> engagementOptions` as an additional handler parameter — the endpoint already takes four DI parameters.

- [ ] **Step 5: Gate the consumer registration, in a testable shape**

Follow the existing precedent exactly: `AddEnrichmentPipeline` is an `internal static IServiceCollection` extension declared at the bottom of `EnrichmentConsumer.cs:376` and called from `Program.cs:235`, which lets its gate be tested without booting a worker-role `WebApplicationFactory`. Add the same shape for engagement — an `internal static AddEngagementStoreConsumer(this IServiceCollection, IConfiguration, bool isWorker)` at the bottom of `EngagementStoreConsumer.cs`:

```csharp
    internal static IServiceCollection AddEngagementStoreConsumer(
        this IServiceCollection services, IConfiguration config, bool isWorker)
    {
        if (isWorker && config.GetValue($"{EngagementStoreOptions.Section}:Enabled", true))
            services.AddHostedService<EngagementStoreConsumer>();

        return services;
    }
```

and replace the bare `builder.Services.AddHostedService<EngagementStoreConsumer>();` inside the `if (workloadRole == "worker")` block with a call to it, alongside the enrichment one.

This single gate covers engagement **writes** and StarRocks **DDL**, because `EnsureTenantProvisionedAsync` — the only place StarRocks tables are created — is called solely from this consumer.

Test it the way the enrichment gate is tested (`EnrichmentConsumerTests.cs:402-425`): a theory over the flag asserting the hosted service is registered when true and absent when false, plus the api-role case registering nothing. Assert against the `IServiceCollection`; do not stand up a host.

- [ ] **Step 6: Run tests**
```bash
dotnet test Iverson.Server/Iverson.Server.slnx
```

- [ ] **Step 7: Commit**
```bash
git add Iverson.Server/Iverson.StarRocks/EngagementStoreOptions.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api/ReadinessPolicy.cs Iverson.Server/Iverson.Api.Tests/ReadinessPolicyTests.cs
git commit -m "feat(engagement): make the engagement store optional at startup and in readiness"
```

---

### Task 5: Fail-cleanly surface for search and aggregate

**Files:**
- Create: `Iverson.Server/Iverson.StarRocks/EngagementStoreDisabledException.cs`
- Create: `Iverson.Server/Iverson.StarRocks/DisabledEngagementStoreSearchService.cs`
- Modify: `Iverson.Server/Iverson.StarRocks/ServiceCollectionExtensions.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

**Interfaces:**
- Consumes: Task 4's `EngagementStoreOptions`.

- [ ] **Step 1: Add the exception**

```csharp
namespace Iverson.StarRocks;

public sealed class EngagementStoreDisabledException(string message) : Exception(message);
```

- [ ] **Step 2: Add the null object implementing all four methods**

The interface has four methods; a null object rather than per-method guards means a fifth added later inherits the behaviour instead of silently reaching a dead StarRocks. Signatures must match `IEngagementStoreRoles.cs:24-52` exactly, including every optional parameter:

```csharp
namespace Iverson.StarRocks;

/// <summary>
/// Registered in place of EngagementRepository when Engagement__Enabled is false.
/// Every method throws: the store is not deployed and never will be in this instance.
/// </summary>
internal sealed class DisabledEngagementStoreSearchService : IEngagementStoreSearchService
{
    private const string Message =
        "The engagement store is not deployed in this instance (Engagement__Enabled=false). " +
        "Search, aggregate, group-by and pipeline queries require StarRocks.";

    public Task<IEnumerable<dynamic>> SearchAsync(
        EngagementQuerySchema schema, SearchQuery? query, int page, int pageSize,
        IReadOnlyList<string>? fields = null, IReadOnlyList<JoinSpec>? joins = null,
        Func<string, EngagementQuerySchema?>? registry = null,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
        => throw new EngagementStoreDisabledException(Message);

    public Task<AggregationResult?> AggregateAsync(
        EngagementQuerySchema schema, SearchQuery? query, AggregationDescriptor spec,
        SearchQuery? having = null, IReadOnlyList<JoinSpec>? joins = null,
        Func<string, EngagementQuerySchema?>? registry = null,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
        => throw new EngagementStoreDisabledException(Message);

    public Task<IEnumerable<dynamic>> GroupByAsync(
        EngagementQuerySchema schema, GroupByRequest request,
        Func<string, EngagementQuerySchema?> registry,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
        => throw new EngagementStoreDisabledException(Message);

    public Task<IEnumerable<dynamic>> PipelineAsync(
        EngagementQuerySchema schema, PipelineRequest request,
        Func<string, EngagementQuerySchema?> registry,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
        => throw new EngagementStoreDisabledException(Message);
}
```

- [ ] **Step 3: Swap the registration in `AddStarRocks`**

`AddStarRocks` takes no `IConfiguration`, so the flag arrives as a new **optional** parameter — both existing test call sites omit it and keep compiling:

```csharp
    public static IServiceCollection AddStarRocks(
        this IServiceCollection services,
        string connectionString,
        EngagementResilienceOptions? resilienceOptions = null,
        bool engagementEnabled = true)
```

and at the `IEngagementStoreSearchService` registration:

```csharp
        if (engagementEnabled)
            services.AddSingleton<IEngagementStoreSearchService>(sp => sp.GetRequiredService<EngagementRepository>());
        else
            services.AddSingleton<IEngagementStoreSearchService>(new DisabledEngagementStoreSearchService());
```

Leave the other registrations alone: `IEngagementStoreHealthCheck` must stay resolvable because `/health` still injects it, and the entity/query executors stay for the same reason nothing else loses a dependency. Pass the flag from `Program.cs:165`'s call.

- [ ] **Step 4: Map the exception in the gRPC service**

Add one catch beside the existing two, in each method that calls the search service:

```csharp
        catch (EngagementStoreDisabledException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
```

`FailedPrecondition`, not `Unavailable` — see Global Constraints.

- [ ] **Step 5: Test**

Assert that with the disabled service registered, a search call surfaces `FailedPrecondition` and **not** `Unavailable`. `ObjectSearchGrpcServiceTests` already substitutes `IEngagementStoreSearchService`, so configure the substitute to throw `EngagementStoreDisabledException`.

```bash
dotnet test Iverson.Server/Iverson.Server.slnx
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.StarRocks/EngagementStoreDisabledException.cs Iverson.Server/Iverson.StarRocks/DisabledEngagementStoreSearchService.cs Iverson.Server/Iverson.StarRocks/ServiceCollectionExtensions.cs Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs
git commit -m "feat(engagement): fail search and aggregate with FailedPrecondition when the store is absent"
```

---

### Task 6: Embedding startup resilience

**Files:**
- Modify: `Iverson.Server/Iverson.Embeddings/IEmbeddingService.cs`
- Modify: `Iverson.Server/Iverson.Embeddings/EmbeddingService.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs`, `Iverson.Server/Iverson.Embeddings.Tests/EmbeddingServiceTests.cs`

- [ ] **Step 1: Write the tests first**

In `EmbeddingServiceTests`: `EnsureInitializedAsync` is idempotent (two calls, one probe); concurrent callers probe once; a failing probe throws to the caller but leaves the service usable for a later successful call. Keep the existing `Dimension_BeforeInitializeAsync_ThrowsInvalidOperationException` test passing — that behaviour is deliberate.

- [ ] **Step 2: Add `EnsureInitializedAsync` to the interface and implementation**

```csharp
    Task EnsureInitializedAsync(CancellationToken ct = default);
```

In `EmbeddingService`, guard with a `SemaphoreSlim` so concurrent callers probe once:

```csharp
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_dimension > 0) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_dimension > 0) return;
            var probe = await EmbedAsync("probe", ct);
            _dimension = probe.Length;
            logger.LogInformation(
                "EmbeddingService initialized: model={Model} dimension={Dimension}", ModelId, _dimension);
        }
        finally
        {
            _initLock.Release();
        }
    }
```

Keep `InitializeAsync` as-is or delegate it to `EnsureInitializedAsync`; the `Dimension` property keeps throwing when genuinely uninitialized, because that is a programming error and should stay loud.

Add the member to the hand-written `NoOpEmbeddingService` in `StartupNoOpFakes.cs` — `=> Task.CompletedTask`, matching its existing `InitializeAsync`. NSubstitute doubles need no change.

- [ ] **Step 3: Make startup non-fatal**

At `Program.cs`, replace the bare `await …InitializeAsync();` with:

```csharp
try
{
    await app.Services.GetRequiredService<IEmbeddingService>().EnsureInitializedAsync();
}
catch (Exception ex)
{
    // Ollama is commonly still pulling ~2.2GB of models on a first install. Dying here
    // crash-loops both roles and CrashLoopBackOff then delays recovery by up to five
    // minutes AFTER Ollama is healthy. Initialization retries lazily at the one place
    // that needs the dimension (schema registration), so continue.
    app.Logger.LogWarning(ex,
        "Embedding service not initialized at startup; will initialize on first schema registration.");
}
```

- [ ] **Step 4: Await initialization at the registration chokepoint**

In `SchemaRegistrationOrchestrator.RegisterAsync`, before the loop that calls `SchemaBuilder.BuildDescriptor(typeDesc, embedding)`:

```csharp
        try
        {
            await embedding.EnsureInitializedAsync(ct);
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Unavailable,
                $"Embedding service is unavailable, so schema registration cannot determine the vector "
                + $"dimension. Check that Ollama is reachable and retry. ({ex.Message})"));
        }
```

This is the only path that consumes `Dimension`, and it is on the api role. The worker never needs it — which is why startup no longer blocks on it.

- [ ] **Step 5: Run tests**
```bash
dotnet test Iverson.Server/Iverson.Server.slnx
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.Embeddings/IEmbeddingService.cs Iverson.Server/Iverson.Embeddings/EmbeddingService.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs Iverson.Server/Iverson.Embeddings.Tests/EmbeddingServiceTests.cs
git commit -m "fix(embeddings): stop crash-looping when Ollama is slow to start"
```

---

## Tasks NOT in this plan

Inherited from the spec's Non-goals. A new spec → plan cycle is required to add any of these.

- Postgres fallback for search/aggregate. This is a real product feature and deserves its own spec; smuggling it in here would risk silently different results between local and cloud.
- Trimming the operator layer (Calico, the StarRocks operator) for local. Considered and rejected: the budget fits without it, and losing Calico would mean losing NetworkPolicy enforcement locally, which could hide a class of bug.
- A dev auth bypass. Authentik fits in the budget; inventing an auth-disable switch trades a real security surface for ~200m of CPU.
- The broader `EmbeddingService`-vs-StarRocks resilience asymmetry beyond the specific fix above.

## Known issues inherited from spec

**The ollama `storageSize: 2Gi` was reported as a regression during the smoke test. It is not one.** Rancher's local-path-provisioner — kind's default `standard` StorageClass — explicitly ignores capacity limits ("No support for the volume capacity limit currently"), so 2Gi was never enforced and the model pull would have succeeded. `values.yaml` defaults to 20Gi and the cloud overlays inherit it, so no environment was ever at risk. The 2Gi → 8Gi change is retained only so the declared number reflects actual usage (~2.2GB) for anyone running this profile against a provisioner that does enforce quotas.

**The laptop profile cannot exercise search, aggregate, group-by, or pipeline queries.** With the engagement store disabled these return `FailedPrecondition` by design. Testing those paths requires `values-local.yaml` on a ≥16GB machine.

**Capacity is tight, not comfortable.** ≈89% of CPU requests on a 4-CPU node. Adding any further component to the laptop preset will require removing another.
