# Laptop deployment profile and optional engagement store

**Date:** 2026-07-27
**Status:** Design approved, not yet implemented
**Origin:** Issues surfaced by the 2026-07-27 kind smoke test of the ingest-enrichment
branch (`main@485eaf9`).

## Problem

The Iverson Helm chart deploys 12 subcharts and cannot be composed: every component is
all-or-nothing. On a 4-CPU / 9GB WSL2 laptop the full stack does not fit, and the
components that do not fit are the ones a developer least needs — StarRocks in
particular, which the vendor sizes at 8 CPU / 16GB for a frontend node alone.

The smoke test could not complete an end-to-end enrichment run for exactly this reason:
StarRocks FE crash-looped at the `values-local.yaml` sizing, the api's readiness probe
therefore never passed, and the api's Service never gained endpoints.

Five concrete defects were found along the way. They are addressed together here because
they share one root cause: the chart assumes every component is present, and the server
assumes every store is deployed.

## Goals

- Compose any subset of the 12 subcharts.
- Run a usable Iverson on a 4-CPU / 9GB laptop, exercising the write and enrichment paths.
- Make "deployed without an engagement store" a supported, honest configuration rather
  than a broken one.
- Stop the api and worker crash-looping when Ollama is slow to start.

## Non-goals

- Postgres fallback for search/aggregate. This is a real product feature and deserves its
  own spec; smuggling it in here would risk silently different results between local and
  cloud.
- Trimming the operator layer (Calico, the StarRocks operator) for local. Considered and
  rejected: the budget fits without it, and losing Calico would mean losing NetworkPolicy
  enforcement locally, which could hide a class of bug.
- A dev auth bypass. Authentik fits in the budget; inventing an auth-disable switch trades
  a real security surface for ~200m of CPU.
- The broader `EmbeddingService`-vs-StarRocks resilience asymmetry beyond the specific fix
  below.

## Design

### 1. Chart composability

Add `condition: <key>.enabled` to all 12 dependencies in `Chart.yaml`, with
`enabled: true` defaulted in each subchart's values block. Defaults preserve today's
behaviour exactly, so `values.yaml` and the three cloud overlays are unaffected. This is
purely additive.

`admin-ui` is declared with `alias: adminUi`, so its condition key is `adminUi.enabled`.

**Cross-subchart coupling must be guarded at the same time.** The api and worker
deployments both hold a hard `secretKeyRef` to `{{ .Release.Name }}-starrocks-app`, a
Secret owned by the starrocks subchart. Disabling starrocks without guarding these would
leave both pods in `CreateContainerConfigError` — the toggle would take down the
application, not just the store. Specifically:

| Location | Reference | Treatment |
|---|---|---|
| `charts/api/templates/deployment.yaml:85-89` | `STARROCKS_APP_PASSWORD` secretKeyRef | wrap in `if .Values.global.engagementEnabled` |
| `charts/api/templates/deployment.yaml:91` | `ConnectionStrings__StarRocks` | same |
| `charts/worker/templates/deployment.yaml:78-84` | both of the above | same |
| `charts/api/templates/deployment.yaml:127,155` | jaeger OTLP endpoints | guard on `global.tracingEnabled` |
| `charts/worker/templates/deployment.yaml:122` | jaeger OTLP endpoint | same |

The jaeger references are non-fatal (a dangling endpoint only produces export errors), but
guarding them avoids a permanent error-log stream in a profile where jaeger is off.

**Values scoping.** A Helm subchart cannot read a sibling top-level value, so the api and
worker charts cannot see `starrocks.enabled`. The chart already solves this class of
problem with `global.*` (`global.ingressHost`, `global.generativeModel`), so the flag is
`global.engagementEnabled`, following that precedent. `values.yaml` sets
`starrocks.enabled` and `global.engagementEnabled` together, and the spec's tests assert
they agree.

### 2. New `values-laptop.yaml`

A preset layered on the same chart, not a fork. `values-local.yaml` is unchanged and
remains the full-fidelity profile.

| Component | State |
|---|---|
| postgres, kafka, qdrant, ollama | on |
| api, worker, redis, authentik | on |
| starrocks | **off** |
| jaeger, prometheus, adminUi | **off** |

**Capacity.** The app side is ~1.45 CPU of requests, verified by rendering the chart.
System and operator overhead measured ~2.0 CPU during the smoke test, and metrics-server
adds ~0.1. Total ≈ **3.55 of 4.0 CPU (≈89%)**.

This fits, but the margin is thin — roughly 450m. It works because requests are
reservations, not usage: Ollama's 3-CPU *limit* can still burst into unreserved capacity
during inference. Do not read 89% as comfortable, and re-measure with `kubectl top` after
implementation.

### 3. Optional engagement store (server side)

New `EngagementStoreOptions` in `Iverson.StarRocks`, following the established convention
(`const string Section = "Engagement"`, `bool Enabled = true`). Env form
`Engagement__Enabled`.

Four changes. Notably **zero** changes to `StoreTargeting` or its ten call sites: every one
of those uses the result only to stamp `TargetStores` onto an outgoing `EntityEvent`, and
an unconsumed flag is harmless.

**3a. Do not register `EngagementStoreConsumer` when disabled.** At `Program.cs:246`,
inside the existing worker-role block, mirroring the `AddEnrichmentPipeline` gate. This one
change covers both engagement *writes* and StarRocks *DDL*, because
`EnsureTenantProvisionedAsync` — the only place StarRocks tables are created — is called
solely from that consumer (`EngagementStoreConsumer.cs:73`).

**3b. Search and aggregate fail cleanly.** Register a disabled implementation of
`IEngagementStoreSearchService` that throws a new `EngagementStoreDisabledException`, and
add one catch in `ObjectSearchGrpcService` mapping it to `FailedPrecondition`, beside the
existing `EngagementQueryTranslationException` and `EngagementNotReadyException` catches.
The swap point is `AddStarRocks` (`ServiceCollectionExtensions.cs:20`).

A null object rather than per-method guards, because the interface has **four** methods
(`SearchAsync`, `AggregateAsync`, `GroupByAsync`, `PipelineAsync`) and a fifth added later
should inherit the behaviour rather than silently reach a dead StarRocks.

`EngagementNotReadyException` is deliberately *not* reused: it maps to `Unavailable`, which
means "retry later", and a client would retry forever against a store that will never
exist. `FailedPrecondition` is the honest signal.

**3c. Readiness excludes it.** `ReadinessPolicy.Evaluate` gains the enabled flag. When
disabled, StarRocks is dropped from the verdict and reported in the body as
`"starrocks": "disabled"` rather than `false`, so the response distinguishes "not deployed"
from "broken". The existing `AuthPending` tolerance is untouched — that logic exists to
avoid a first-install deadlock and must keep working.

**3d. Chart wiring.** `Engagement__Enabled` is templated from `global.engagementEnabled`
into the api and worker env blocks, so the chart toggle and the server flag cannot drift.

### 4. Embedding startup resilience

`EmbeddingService.InitializeAsync` probes Ollama to discover the vector dimension and
throws on failure. At `Program.cs:378` that exception is unhandled, so both api and worker
crash-loop whenever Ollama is not yet answering — which is guaranteed on a first install,
where the init container is pulling ~2.2GB of models. `CrashLoopBackOff` then delays
recovery by up to five minutes *after* Ollama becomes healthy.

The key constraint: `Dimension` is consumed only by `SchemaBuilder.BuildDescriptor`, whose
sole call site is `SchemaRegistrationOrchestrator.cs:39` — an async method on the **api**
role's registration path. `EmbedAsync` does not reference `_dimension` at all. The worker
therefore probes at startup, crash-loops on it, and never uses the result.

Four changes:

1. `IEmbeddingService.EnsureInitializedAsync()` — idempotent, `SemaphoreSlim`-guarded so
   concurrent callers probe once. The `Dimension` property keeps throwing
   `InvalidOperationException` when genuinely uninitialized; that is a programming error
   and should stay loud.
2. `Program.cs:378` becomes non-fatal: try/catch, log a warning, continue. The
   "initialized: model=… dimension=768" log stays on the happy path.
3. `await embedding.EnsureInitializedAsync()` at `SchemaRegistrationOrchestrator.cs:39`, so
   registering a schema with embedding or chunk fields while Ollama is down yields a clear
   `Unavailable` gRPC error naming Ollama, instead of a confusing `InvalidOperationException`.
   Recoverable: retry the registration once Ollama is up.
4. Readiness gains an `embeddings` check, so k8s holds traffic rather than admitting a
   server that would fail every registration.

This matches the "degrade and report, don't die" shape the StarRocks path already uses,
rather than introducing a second competing pattern.

Deliberately **not** doing: a configurable `Embeddings__Dimension`. That reintroduces the
pulled-vs-requested drift class that the `global.generativeModel` fix just eliminated.

### 5. Supporting fixes

**metrics-server** added to `deploy/kind/setup.sh` with `--kubelet-insecure-tls` (required
on kind). This is the root cause of the silent worker outage in the smoke test: without
metrics-server both HPAs report `<unknown>`, so a Deployment manually scaled to 0 is never
restored to `minReplicas`. `EnrichmentConsumer` was simply not running, with no error
anywhere. It also makes `kubectl top` work, which this design's capacity numbers depend on.

**StarRocks sizing in `values-local.yaml`.** The current 250m/512Mi is far below anything
that can start; StarRocks documents FE at 8 CPU / 16GB and BE at 16 CPU / 64GB. There is no
laptop-viable sizing, so `values-local` gets a documented *dev floor* — FE and BE each
`requests: { cpu: "1", memory: "4Gi" }`, `limits: { cpu: "2", memory: "6Gi" }` — plus an
explicit comment that the profile assumes a machine with ≥16GB. These are a starting point
to be confirmed empirically during implementation, not vendor-sanctioned minimums. Running
StarRocks on a laptop is not supported; that is what `values-laptop.yaml` is for.

**Ollama `storageSize` in `values-local.yaml`,** 2Gi → 8Gi. See "Known issues" below: this
is documentation of intent, not a bug fix.

## Testing

Unit-level, matching existing patterns:

- `ReadinessPolicy.Evaluate` — table-driven over enabled/disabled × health-status,
  including that disabled excludes StarRocks from the verdict. Existing `AuthPending` cases
  must keep passing.
- Disabled `IEngagementStoreSearchService` → `FailedPrecondition`, not `Unavailable`.
- `EnsureInitializedAsync` — idempotent, probes once under concurrency, and a failed probe
  does not kill startup.
- `EngagementStoreConsumer` not registered when `Engagement__Enabled=false`, still
  registered when true.
- `helm template` assertions: each `condition:` removes its subchart; `Engagement__Enabled`
  tracks `global.engagementEnabled` on both api and worker; and — the regression this
  design exists to prevent — **rendering with `starrocks.enabled=false` produces api and
  worker Deployments with no reference to the `-starrocks-app` Secret.**

## Verified assumptions

Nineteen assumptions were enumerated against the design and checked against the codebase
before this spec was written. Fifteen held; four changed the design.

| # | Assumption | Result |
|---|---|---|
| A1 | Helm/chart apiVersion supports `condition:` | PASS — Helm 3.16.4, `apiVersion: v2` |
| A2 | Every dep has a matching top-level values key | PASS — all 12, incl. `adminUi` alias |
| A3 | Disabling a subchart does not break others' rendering | **FAIL** — api/worker hold a hard `secretKeyRef` to `-starrocks-app`; drove §1's guard table |
| A4 | Capacity arithmetic | **REVISED** — app 1.45 CPU verified from templates; system ≈2.0, so total ≈89%, not the 81% first estimated |
| A7 | `EngagementStoreConsumer` registration is gateable | PASS — `Program.cs:246`, worker-role block |
| A8 | StarRocks DDL happens only in that consumer | PASS — `EnsureTenantProvisionedAsync` only at `EngagementStoreConsumer.cs:73` |
| A9 | No per-store completion tracking | PASS — `ReconciliationQueueRepository` has no per-store enqueue; rows come from publish failures |
| A10/A11 | Search and aggregate both route through the interface | PASS — four methods: Search/Aggregate/GroupBy/Pipeline |
| A12/A25 | `ReadinessPolicy.Evaluate` callers are contained | PASS — `Program.cs:310` + `ReadinessPolicyTests.cs` |
| A13 | Only that consumer branches on `StoreTarget.Engagement` | PASS — `EngagementStoreConsumer.cs:46,103` |
| A14/A26 | Options convention; no config-section collision | PASS — `AddStarRocks:20` is the swap seam; the `"Engagement"` literal is a log label |
| A15 | Subcharts cannot read sibling top-level values | CONFIRMED — chart already uses `.Values.global.*` for this |
| A16 | Interface change breaks no implementers | PARTIAL — hand-written `NoOpEmbeddingService` in `StartupNoOpFakes.cs` needs the new member; NSubstitute mocks unaffected |
| A17 | One production `InitializeAsync` call site | PASS — `Program.cs:378`; test hits are xUnit `IAsyncLifetime` name collisions |
| A19 | `EmbedAsync` does not need the dimension | PASS — no `_dimension` reference; worker never needs init |
| A20 | Readiness endpoint can inject `IEmbeddingService` | PASS — minimal-API endpoint with DI params |
| A21 | 2Gi ollama storage is a real regression | **DISPROVED** — local-path-provisioner ignores capacity limits |
| A22 | StarRocks can be right-sized for a laptop | **DISPROVED** — vendor sizes FE at 8 CPU/16GB, BE at 16 CPU/64GB |
| A23/A24 | metrics-server absent; `setup.sh` is the right place | PASS — 0 occurrences in `setup.sh` |

## Known issues / accepted

**The ollama `storageSize: 2Gi` was reported as a regression during the smoke test. It is
not one.** Rancher's local-path-provisioner — kind's default `standard` StorageClass —
explicitly ignores capacity limits ("No support for the volume capacity limit currently"),
so 2Gi was never enforced and the model pull would have succeeded. `values.yaml` defaults
to 20Gi and the cloud overlays inherit it, so no environment was ever at risk. The 2Gi →
8Gi change is retained only so the declared number reflects actual usage (~2.2GB) for
anyone running this profile against a provisioner that does enforce quotas.

**The laptop profile cannot exercise search, aggregate, group-by, or pipeline queries.**
With the engagement store disabled these return `FailedPrecondition` by design. Testing
those paths requires `values-local.yaml` on a ≥16GB machine.

**Capacity is tight, not comfortable.** ≈89% of CPU requests on a 4-CPU node. Adding any
further component to the laptop preset will require removing another.

## References

- Rancher local-path-provisioner capacity limitation: https://github.com/rancher/local-path-provisioner
- StarRocks cluster planning: https://docs.starrocks.io/docs/deployment/plan_cluster/
