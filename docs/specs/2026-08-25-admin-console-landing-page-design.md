# Admin Console Landing Page — Design

**Date:** 2026-08-25
**Status:** Approved (design), not yet planned
**Repo HEAD at write time:** `8638e81`

## Problem

`Iverson.AdminUI` has four routes — Performance, Storage, Tenants, Tenant Admin — and every
one of them is a stub returning `Coming soon`. The router has no landing page at all:
`src/router.tsx` maps `{ index: true }` to `<Navigate to="/performance" replace />`, so
signing in drops an operator onto an empty page.

Meanwhile the deployment already emits a great deal of operational signal that nothing
surfaces: four store health probes, three reconciliation gauges, two consumer counters,
ASP.NET Core and HttpClient instrumentation, a tenant registry, a schema catalog, and
Qdrant collection metadata. Prometheus scrapes and retains the metrics; nobody looks at them.

This design specifies a landing page at `/` that pulls from each data store and from OTel,
so that the first screen after sign-in answers "is this deployment healthy, and what is in it".

## Scope

**In scope**

- A new landing page route at `/`, replacing the current redirect.
- Nine widgets across three source bands (see Design 2).
- Two new read-only REST endpoints on `Iverson.Api` (see Design 1).
- The prerequisite work those widgets depend on and which does not exist today
  (see Design 4): grpc-web enablement for two services, a console gRPC client
  foundation, Prometheus scrape-target changes, and the identity fixes without
  which every Operator-gated surface returns 403.

**Out of scope**

- The four existing stub pages. They stay stubs; this design does not fill them in.
- Jaeger traces. The console already relays browser spans to Jaeger via `/v1/traces`,
  but no widget reads trace data back.
- Any write or mutation surface. Every widget and both new endpoints are read-only.
- Alerting, thresholds, or notification. The page displays; it does not judge.
- Authentication changes. The page uses the console's existing OIDC session.

## Design 1 — Server surface

Two new endpoints on `Iverson.Api`, following the minimal-API pattern already established
by `MapGet("/health")` (`Iverson.Server/Iverson.Api/Program.cs:301`) and
`MapGet("/probe/starrocks")` (`:342`) rather than adding gRPC services. The console needs
plain JSON for these two reads, and grpc-web codegen would be ceremony for that.

### `GET /admin/metrics` — Prometheus proxy

Queries Prometheus server-side and returns a fixed, named result set. It does **not** accept
a PromQL parameter from the browser: a pass-through would turn an authenticated console
endpoint into an open query interface over every metric the deployment emits, and the page
needs seven numbers.

The response carries: the three reconciliation gauges, the two consumer counters, RPC
request rate / error percentage / p95, and Ollama client p95.

The API has no Prometheus client today — `Program.cs` wires only the *exporter*
(`:71` `AddPrometheusExporter`, `:275` `MapPrometheusScrapingEndpoint`). This endpoint adds:

- a configuration key for the Prometheus base URL,
- a named `HttpClient` for it,
- graceful handling of Prometheus being absent, which is a real deployment state:
  `values-laptop.yaml:16-17` sets `prometheus.enabled: false`,
- **two NetworkPolicy additions, without which this endpoint cannot connect in any Kubernetes
  profile.** `templates/networkpolicies.yaml:7-10` declares a namespace-wide default-deny on both
  Ingress and Egress; `api-egress` (`:38-63`) enumerates postgres, kafka, starrocks, qdrant,
  ollama, jaeger and authentik but has no Prometheus rule, and no `prometheus-ingress` policy
  exists at all. Both are needed, guarded by the existing `{{- if .Values.prometheus.enabled }}`
  used at `:474`: an `api-egress` rule to `podSelector: { app: {{ .Release.Name }}-prometheus }`
  on TCP 9090, and a `prometheus-ingress` policy allowing from `app: {{ .Release.Name }}-api` on
  TCP 9090. The reverse direction is already allowed (`:487-490`), which is why scraping works
  today and the missing direction is easy to overlook.

PromQL uses Prometheus-mangled metric names (dots to underscores, `_total` on counters,
`_bucket`/`_sum`/`_count` on histograms), not the OTel instrument names.

The Ollama filter is built server-side from `EmbeddingServiceOptions.BaseUrl` rather than
hard-coded, because HTTP client metrics label by `server.address` and the host is
configuration-driven.

### `GET /admin/stores/qdrant` — collection stats

Returns points count, vectors count, and indexed-vectors count per collection.

No such surface exists. `/health` performs a *write* probe
(`EnsureCollectionAsync("iverson-probe", 4)`) and never reads collection metadata, and
`IVectorSchemaManager` (`Iverson.Server/Iverson.Vector/IVectorRoles.cs:23-27`) exposes only
`EnsureCollectionAsync` and `ApplyCollectionAsync`. The underlying capability does exist —
`IntelligenceCollectionManager` already calls `ListCollectionsAsync` (`:22`) and
`GetCollectionInfoAsync` (`:60`) — so this endpoint adds a read interface over the same
client, not new infrastructure.

Collections are tenant-scoped by `IntelligenceTenantScope.ResolveCollectionName`
(`Iverson.Server/Iverson.Vector/IntelligenceTenantScope.cs:11`), and per-collection scoped
API keys are minted at `:18`. Enumerating all collections is therefore a cross-tenant
operation and is gated accordingly.

### Authorization

Both endpoints require `RequireAuthorization("Operator")`.

There is no generic "admin" scope. `Program.cs:141-156` declares exactly three policies —
`Operator`, `SchemaAdmin`, `TenantAdmin` — over a `FallbackPolicy` that requires an
authenticated user (`:142-145`). `Operator` is the policy the existing operational endpoints
use (`/reconcile` at `:373`, `/admin/dlq` at `:380`, `/admin/dlq/replay` at `:392`), and it is
the correct peer for these two.

This is a deliberate departure from `/health` and `/probe/*`, which are `AllowAnonymous`
because a load balancer calls them.

### Explicitly not included

No caching layer (Prometheus is already the cache), no metric-name configurability, no
generic store-query endpoint.

## Design 2 — Widget inventory

Nine widgets in three bands. The landing page is a new route at `/`, replacing
`{ index: true, element: <Navigate to="/performance" replace /> }` in `src/router.tsx`.

### Band A — store-sourced, direct

| Widget | Source | Shows |
|---|---|---|
| Store health strip | `GET /health` | One tile per store: postgres, starrocks, qdrant, kafka |
| Tenant roster | `TenantLifecycleGrpcService.ListTenants` | Tenant count and list with state |
| Schema catalog | `ObjectMappingGrpcService.GetSchema` | Object types, field counts, relation edges |
| Data volume per type | `ObjectSearchGrpcService.Aggregate`, one call per type | Row count per object type |

### Band B — OTel via the metrics proxy

| Widget | Metrics | Shows |
|---|---|---|
| Fan-out backlog | `reconciliation.queue_depth`, `dlq.unreplayed_count`, `document_rerender.queue_depth` | Three gauges, current value plus sparkline |
| DLQ and retry rate | `consumer.retries`, `consumer.dlq_routed` | Rate over the window |
| RPC health | `http.server.request.duration` | Request rate, error percentage, p95 |
| Embedding latency | `http.client.request.duration` | p95 against Ollama |

### Band C — new surface

| Widget | Source | Shows |
|---|---|---|
| Qdrant collection stats | `GET /admin/stores/qdrant` | Points, vectors, indexed vectors per collection |

### Constraints each widget carries

**The health strip is binary per store, not graded.** `/health` (`Program.cs:301-334`)
returns 200 or 503 for the set, with a per-store `checks` object. A tile is green, red, or
unknown — there is no per-store latency. Two specifics:

- StarRocks has **three** states, not two: the `checks.starrocks` field is
  `engagementEnabled ? (bool) : "disabled"` (`:321`) — a literal string when the engagement
  store is switched off. The widget must handle it.
- **Ollama has no tile.** It is not in the health check, and adding it would mean changing
  a liveness endpoint the load balancer depends on. Ollama's state is inferred from the
  Band B latency widget instead.

**Data volume costs one gRPC call per object type**, and is the most expensive widget on the
page. Two corrections that the implementation must honour:

- `Aggregate` **rejects a request with no aggregations** —
  `ObjectSearchGrpcService.cs:490-494` throws `InvalidArgument`. The widget sends an explicit
  `AggregationSpec` of `AggregationType.COUNT` and reads `AggregationResult.metric_value`.
- The widget must **not** read `AggregateResponse.total`. The proto comments that field
  `// total matching docs`, but the implementation never assigns it — it constructs
  `new AggregateResponse { TraceId = ... }` at `:514` and only ever adds to `Results`.
  The field is always zero.
- A **denied type is indistinguishable from an empty type.** When authorization denies the
  primary type, `:501` returns an empty response rather than an error. The widget renders
  zero in both cases and the page says so, rather than implying the count is authoritative.

**Data volume is tenant-scoped.** `Aggregate` enforces tenant isolation, so the number is the
acting user's tenant, not a deployment total. The widget labels it as such.

**RPC health measures HTTP, not gRPC semantics.** `AddAspNetCoreInstrumentation` (`Program.cs:69`) observes gRPC-over-HTTP/2 as HTTP requests, so "error percentage" is HTTP
status. A gRPC call that fails with a non-OK status inside a 200 response does not count.
The widget is honest as *transport* health; it is not labelled "RPC errors".

**RPC health must exclude probe and scrape traffic.** The `/health` path filter at
`Program.cs:56-59` is on the **tracing** provider; the metrics provider (`:66-71`) is a bare
`AddAspNetCoreInstrumentation()`. So kubelet liveness/readiness probes, load-balancer health
checks and Prometheus's own scrape of `MapPrometheusScrapingEndpoint` (`:275`) are all counted as
requests, and the unfiltered rate is dominated by them. This design compounds it: the health strip
polls `/health` every 10s per open tab, feeding the metric shown in the card beside it.

Every RPC-health query is therefore constrained by `http_route`, excluding `/health`,
`/health/live` and the scraping endpoint's route. The filtering lives in the proxy's PromQL rather
than on the shared metrics provider, because mirroring the tracing `Filter` onto
`AddAspNetCoreInstrumentation` would change what the whole deployment exports — not just what this
page displays — and other consumers may rely on total request volume.

**Embedding latency may conflate two clients.** `AddEmbeddings` and `AddEnrichment`
(`Iverson.Server/Iverson.Embeddings/ServiceCollectionExtensions.cs:12` and `:29`) each
register a named `HttpClient`, but HTTP client metrics label by `server.address`, not by the
logical client name. If both base URLs resolve to the same host, the p95 covers both.

**Tenant roster and schema catalog need no new authorization thinking.** Both go through the
console's existing OIDC token and return what that user is entitled to.

## Design 3 — Page behaviour

**Every widget fetches independently.** There is no page-level load gate and no combined
request. Nine widgets across seven sources means a page that waits for all of them shows a
spinner indefinitely; a page that renders each card as its data arrives is usable from the
first response.

### Refresh cadence

| Band | Refresh |
|---|---|
| Health strip | 10s poll |
| Band B metrics | 30s poll |
| Qdrant stats | 30s poll |
| Tenant roster, schema catalog | On mount, plus manual refresh |
| Data volume per type | On mount, plus manual refresh — never polled |

Data volume is excluded from polling deliberately: it is N gRPC calls per render, and a
30-second timer turns an open browser tab into sustained aggregate load against StarRocks
for a number that changes slowly.

### Failure and degradation

- **Failure is per-card, never per-page.** A widget that fails renders its own error state
  inside its own card with a retry control; the other eight keep working. With seven sources,
  the steady state is that something is unavailable, and a page that blanks on one dead
  source would be dead most of the time.
- **A failed refresh keeps the last good value**, marked stale with an "as of HH:MM" line,
  rather than reverting to a spinner. A stale number with an honest timestamp beats no number.
- **Consecutive failures back off** — the interval doubles to a ceiling, then polling stops
  and the card offers manual retry. Nine widgets retrying a downed backend on a fixed timer
  is a self-inflicted load test.
- **Polling pauses when the tab is hidden**, so a forgotten tab does not generate requests
  indefinitely.
- **A 401 is a page concern, not a widget concern.** An expired token fails every widget at
  once, and nine identical "unauthorized" cards is the wrong output. The shared fetch layer
  distinguishes 401 from source failure and routes it to the existing `oidc-client-ts`
  renewal path.

### Implementation shape

All of the above lives in one shared hook, roughly `usePolledResource(fetcher, intervalMs)`,
used by all nine widgets.

The hook **takes the access token as an argument.** `AuthProvider.tsx` uses
`react-oidc-context`, whose `useAuth()` is React-context-only and exposes the token at
`auth.user?.access_token`. A module-scope fetch layer cannot reach it.

The alternative considered was adding TanStack Query, which does polling, caching, retry and
stale-while-revalidate out of the box. Rejected: the console has no data-fetching library
today (`Iverson.AdminUI/package.json`), these are nine read-only sources with no shared cache
keys and no mutations, and the subset of the library that would be used is about the size of
the hook.

### Testing

Follows the existing vitest setup (`vitest` 3.2, `@testing-library/react`, three existing
test files under `src/`). The hook gets unit tests for the poll, backoff, stale and
visibility transitions using fake timers; each widget gets a render test over fixture
responses, including its error and stale states.

## Design 4 — Prerequisite work

Verification found four foundations this design assumed were present that do not exist.
They are in scope because without them the specified widgets are impossible. 4d is the one
that blocks everything else: until it lands, every Operator-gated surface returns 403.

### 4a — grpc-web enablement for two services

`Program.cs:438-445` maps six gRPC services, but only two carry `.EnableGrpcWeb()`:

```
app.MapGrpcService<ObjectMappingGrpcService>();                                          // no grpc-web
app.MapGrpcService<ObjectPersistenceGrpcService>();
app.MapGrpcService<ObjectRetrievalGrpcService>();
app.MapGrpcService<ObjectSearchGrpcService>();                                           // no grpc-web
app.MapGrpcService<TenantLifecycleGrpcService>().RequireAuthorization("Operator").EnableGrpcWeb();
app.MapGrpcService<TenantAdminGrpcService>().RequireAuthorization("TenantAdmin").EnableGrpcWeb();
```

`app.UseGrpcWeb()` is already in the pipeline at `:284`, so the middleware is present; the
two services simply are not opted in. The schema-catalog and data-volume widgets are
unreachable from a browser until `ObjectMappingGrpcService` and `ObjectSearchGrpcService`
gain `.EnableGrpcWeb()`.

Neither service carries an explicit `.RequireAuthorization(...)`, so both fall under the
`FallbackPolicy` requiring an authenticated user. That is the correct level for them and this
design does not change it.

### 4b — Console gRPC client foundation

`Iverson.AdminUI/src/` contains no generated proto code and no gRPC client wrapper.
`package.json` carries `@improbable-eng/grpc-web`, `google-protobuf` and `ts-proto`, and
`scripts/generate_protos.sh` generates into an uncommitted `generated/` directory — but
nothing consumes it and no token-attaching transport exists.

This design adds a grpc-web transport that attaches the OIDC access token to outbound calls,
and wires proto generation into the build rather than committing its output. `generated/` stays
ignored (`Iverson.AdminUI/.gitignore:3`): `generate` becomes a prerequisite of `build` and
`test`, `scripts/generate_protos.sh`'s hardcoded `~/sdk/protoc/bin/protoc` is replaced by a
resolvable path (a protoc devDependency, or a documented `PROTOC` override), and CI gains the
codegen step. This costs a build dependency and buys a client that cannot drift from the
`.proto` contract.

All three Band A gRPC widgets depend on this, as will the already-planned Tenants and Tenant
Admin pages.

### 4c — Prometheus scrape targets

Two independent problems, both in scrape configuration rather than in PromQL.

**Multi-replica sampling.** `charts/prometheus/templates/configmap.yaml` scrapes
`{{ .Release.Name }}-api:8081` via `static_configs`. That name resolves to the API's
ClusterIP Service (`charts/api/templates/service.yaml`), behind which
`charts/api/values.yaml:1` sets 2 replicas and `charts/api/values.yaml:9-12` places the
Deployment under an HPA ranging from 2 to 5. Each scrape therefore lands on a random pod, producing one `instance`
label whose counters are non-monotonic. No PromQL aggregation repairs this — `sum()` across
instances cannot help when there is only one instance label.

**Fix:** headless Services (`clusterIP: None`) exposing the metrics port for api and worker,
and `dns_sd_configs` with `type: A` in the scrape config. Prometheus resolves the A records
and creates one target per pod IP.

This route is chosen over `kubernetes_sd_configs` deliberately. Pod-role service discovery
needs a ClusterRole, a ClusterRoleBinding, and a mounted service-account token — and
`charts/prometheus/templates/deployment.yaml` sets `automountServiceAccountToken: false` in
both the ServiceAccount (`:5`) and the pod spec (`:30`), with no RBAC objects in the chart.
DNS-based discovery needs none of that and leaves the security posture intact.

**Worker metrics missing locally.** All six consumer and reconciliation hosted services are
registered only for the worker role (`Program.cs:254-264`, inside `if (workloadRole ==
"worker")`). The Helm scrape config already has an `iverson-worker` job, but
`deploy/prometheus/prometheus.local.yml` scrapes only `iverson-api:8081`. In docker-compose
the fan-out backlog and DLQ/retry widgets would be permanently empty.

**Fix:** add `iverson-worker:8081` to `prometheus.local.yml`, matching the Helm chart.

### 4d — Identity: making the `Operator` policy satisfiable

Verified live on 2026-08-25 against the running compose stack: **no human identity can satisfy
the `Operator` policy today.** The tenant-roster widget and both new endpoints would return 403
for every user. Two independent gaps must both close. The evidence is in Known Issues; the
fixes are specified here.

#### 4d-1 — Request the claims the policy reads

`Iverson.AdminUI/src/auth/AuthProvider.tsx:10` requests
`scope: "openid profile email offline_access"`. Authentik applies a scope mapping only when the
client requests that scope, so the `groups` and `tenant_id` mappings bound to the
`iverson-oidc-default` provider never fire.

Change the requested scope to `openid groups tenant_id offline_access`.

- `groups` is the claim `OperatorAuthorizationPolicy` reads.
- `tenant_id` is what the data-volume widget's tenant scoping needs, and is absent today for
  the same reason.
- This exact scope string was verified working against this exact client
  (`dev-iverson-human-oidc-client-id`), returning populated `groups` and `tenant_id` claims.
- `profile` and `email` are dropped from the request because they are inert. A provider's
  `property_mappings` list replaces Authentik's default set, and neither mapping is bound to
  this provider — requesting them today yields an empty `scope` claim and nothing else.

#### 4d-2 — Create the `operators` group

No `operators` group exists — not in any blueprint, and not in the live directory. The name is
fixed by two independent consumers and is not a free choice: `OperatorAuthorizationPolicy.cs:11`
tests `groupClaims.Contains("operators")`, and `Iverson.AdminUI/src/layout/Sidebar.tsx:20` gates
the Tenants nav item on `groups.includes("operators")`.

Add it as a **new top-level blueprint**, `charts/authentik/blueprints/operators-group.yaml`:

```yaml
version: 1
metadata:
  name: Iverson operator group
entries:
  - model: authentik_core.group
    identifiers:
      name: operators
    attrs: {}
```

Top-level placement is deliberate. `blueprints-configmap.yaml` globs `blueprints/*.yaml` for the
Helm path, and `docker-compose.yml:294,328` bind-mounts the whole `blueprints` directory for
compose — so one file covers both. The alternative, adding the group to
`blueprints/compose-only/service-clients.yaml` *and*
`templates/blueprints-configmap-service-clients.yaml`, would duplicate an
environment-independent definition across two files that already drift from each other.

#### 4d-3 — Group membership

For local development, add the compose dev users to the group in
`blueprints/compose-only/service-clients.yaml`, using the `groups:` attribute pattern the file
already uses for `iverson-loadtest-bypass-user`.

For real deployments, membership is **not** blueprinted. Who holds operator rights is an
operational decision, and seeding a test user into an operator group in a production overlay
would be a privilege-escalation defect. It is an onboarding step, and belongs in a runbook
beside the existing `docs/runbooks/grpc-admin-auth-cutover.md`.

#### What this repairs beyond this design

Both are pre-existing defects that the missing `groups` claim currently masks:

- `Sidebar.tsx:20` and `:25` gate the Tenants and Tenant Admin nav items on group membership.
  With no `groups` claim, **both are invisible to every user** — two of the console's four pages
  are unreachable through the UI today. 4d-1 alone restores Tenant Admin, since the
  `tenant-admins` group already exists; Tenants additionally needs 4d-2.
- `AppLayout.tsx:9` renders `auth.user?.profile?.email`, which is never emitted, so the AppBar
  shows the literal string `"User"` for everyone.

The email display is left alone by this design. Fixing it means binding Authentik's built-in
`email` scope mapping to the provider, which is unrelated to the landing page. It is recorded
here so the next reader does not mistake it for something this work broke.


## Verified assumptions

A1-A26 were enumerated against the approved design before any file was read for verification,
then checked: nineteen held, seven failed or resolved differently. A27-A29 record the live
verification of the `Operator` policy, run afterwards against the running compose stack on
2026-08-25; all three failed.

| # | Assumption | Disposition |
|---|---|---|
| A1 | `Iverson.Api` uses minimal-API `MapGet` for `/health` and `/probe/starrocks`, both `AllowAnonymous` | **Holds** — `Program.cs:301`/`:334` and `:342`/`:346` |
| A2 | An `admin` scope or policy exists for a new endpoint to require | **Failed** — no admin scope. `Program.cs:141-156` declares `Operator`, `SchemaAdmin`, `TenantAdmin` over a `FallbackPolicy` requiring an authenticated user (`:142-145`). Design uses `Operator` |
| A3 | Prometheus is reachable from the API at a known address | **Failed** — no Prometheus URL exists in the API's configuration; only the exporter is wired (`:71`, `:275`). New config key and named client required. `values-laptop.yaml:16-17` also disables Prometheus entirely |
| A4 | `/health`'s body reports per-store status for the four stores | **Holds, with a wrinkle** — `Program.cs:318-323` returns `postgres`, `starrocks`, `qdrant`, `kafka`; `starrocks` is the literal string `"disabled"` when the engagement store is off |
| A5 | The Qdrant client can read collection info | **Holds** — `IntelligenceCollectionManager.cs:22` (`ListCollectionsAsync`) and `:60` (`GetCollectionInfoAsync`); `Qdrant.Client` 1.18.1. Not on `IVectorSchemaManager` (`IVectorRoles.cs:23-27`), so a read interface is new |
| A6 | Qdrant collections are per-tenant, needing a scoping decision | **Holds** — `IntelligenceTenantScope.cs:11` resolves per-tenant names; `:18` mints per-collection scoped keys |
| A7 | `TenantLifecycle.ListTenants` exists and is reachable from the console | **Holds** — `tenant_lifecycle.proto:9`; mapped with `.RequireAuthorization("Operator").EnableGrpcWeb()` at `Program.cs:444` |
| A8 | `ObjectMapping.GetSchema` returns types, fields, relations | **Holds** — `object_mapping.proto:15`; `GetSchemaResponse` carries `repeated SchemaType`, each with `fields` and `relations` |
| A9 | `Aggregate` can produce a row count for one object type | **Failed twice** — `ObjectSearchGrpcService.cs:490-494` throws `InvalidArgument` on zero aggregations, so a `COUNT` spec is mandatory; and `AggregateResponse.Total` is never assigned (`:514`), so the proto's `// total matching docs` field is always zero. Also `:501` returns an empty response on denial rather than an error |
| A10 | grpc-web is wired in the console with a token-attaching interceptor | **Failed** — `Iverson.AdminUI/src/` has no generated proto code and no client wrapper; `scripts/generate_protos.sh` writes to an uncommitted `generated/`. See Design 4b |
| A11 | A grpc-web path exists for the services the page calls | **Failed** — `app.UseGrpcWeb()` is present (`Program.cs:284`), but only `TenantLifecycle` and `TenantAdmin` call `.EnableGrpcWeb()` (`:444-445`). `ObjectMapping` and `ObjectSearch` do not. See Design 4a |
| A12 | The three reconciliation gauge names are exact | **Holds** — `ReconciliationTelemetry.cs:26`, `:32`, `:38` |
| A13 | The consumer counters exist and are emitted by a scraped process | **Failed in part** — names correct (`Iverson.Events/Telemetry.cs:14`, `:17`) and the meter is registered on the API's provider (`Program.cs:68`), but all six emitting hosted services run only under `workloadRole == "worker"` (`:254-264`). Helm scrapes the worker; `prometheus.local.yml` does not. See Design 4c |
| A14 | `http.server.request.duration` is emitted as a histogram | **Holds** — `AddAspNetCoreInstrumentation()` on the metrics provider (`Program.cs:69`), `OpenTelemetry.Instrumentation.AspNetCore` 1.15.2 |
| A15 | `http.client.request.duration` carries a label distinguishing Ollama | **Failed in part** — the label is `server.address`, not the logical client name, and both `AddEmbeddings` and `AddEnrichment` register Ollama-shaped clients (`ServiceCollectionExtensions.cs:12`, `:29`). Filter is built server-side from `EmbeddingServiceOptions.BaseUrl`; conflation is possible if hosts match |
| A16 | The exporter is on `:8081` and Prometheus scrapes it | **Holds** — `MapPrometheusScrapingEndpoint().AllowAnonymous()` (`Program.cs:275`); both scrape configs target port 8081 |
| A17 | OTel-to-Prometheus name mangling applies | **Holds** — `OpenTelemetry.Exporter.Prometheus.AspNetCore` 1.16.0-beta.1; PromQL uses mangled names |
| A18 | Prometheus requires no authentication | **Holds** — neither scrape config nor the chart declares any auth |
| A19 | The router's `index` route is the redirect to be replaced | **Holds** — `src/router.tsx`, `{ index: true, element: <Navigate to="/performance" replace /> }` |
| A20 | MUI is available for layout | **Holds** — `@mui/material` ^9.2.0 |
| A21 | vitest is configured and runs | **Holds** — `vitest` ^3.2.0, `"test": "vitest run"`, three existing test files |
| A22 | `config.ts`'s `apiBaseUrl` is the right base for the new endpoints | **Failed** — it is read nowhere in `src/` (one hit: its own declaration at `config.ts:16`); `.env.development:3` points it at `http://localhost:8080`, which `appsettings.json:12-13` binds as `Protocols: Http2` cleartext h2c that browsers will not speak (empirically 400, versus 200 on `:8081`); and the console's only real API call uses a relative same-origin path instead (`src/telemetry.ts:19`, reason at `:43`) |
| A23 | No data-fetching library is present | **Holds** — `package.json` has none |
| A24 | The OIDC token is reachable from a plain fetch layer | **Failed** — `AuthProvider.tsx` uses `react-oidc-context`; the token is React-context-only at `auth.user?.access_token`. The hook takes it as an argument |
| A25 | The four existing pages are stubs, so nothing conflicts | **Holds** — all four return `Coming soon` |
| A26 | The API may run multiple replicas, requiring cross-instance aggregation | **Holds, and worse than assumed** — `charts/api/values.yaml:1` sets 2 replicas; `charts/api/values.yaml:9-12` sets an HPA range of 2-5. Prometheus scrapes the ClusterIP Service VIP, so there is only ever one `instance` label and `sum()` cannot repair it. See Design 4c |
| A27 | The console's token satisfies the `Operator` policy | **Failed — verified live 2026-08-25.** A token minted with the console's exact scope against its own client carried no `groups`, no `tenant_id`, and an empty `scope` claim; `/admin/dlq` returned **403** (401 with no token). See Design 4d |
| A28 | An `operators` group exists for users to belong to | **Failed — verified live 2026-08-25.** Live groups are `authentik Admins`, `authentik Read-only`, `iverson-admin-orchestrators`, `iverson-loadtest-bypass`, `tenant-admins`. A correctly-scoped token for a user with real group membership still returned **403**. See Design 4d-2 |
| A29 | The console does not already depend on the `groups` claim | **Failed** — `Sidebar.tsx:20` and `:25` already gate two nav items on it, so both are invisible to every user today; `AppLayout.tsx:9` reads a never-emitted `email` claim |
| A30 | The API pod can open a connection to Prometheus | **Failed** — `networkpolicies.yaml:7-10` declares a namespace-wide default-deny on Ingress and Egress; `api-egress` (`:38-63`) has no Prometheus rule; and no `prometheus-ingress` policy exists at all. See Design 1's `/admin/metrics` section |
| A31 | `http.server.request.duration` measures RPC traffic | **Failed** — the `/health` path filter is on the tracing provider (`Program.cs:56-59`); the metrics provider (`:66-71`) is a bare `AddAspNetCoreInstrumentation()`, so probe and scrape traffic are counted. See Design 2's RPC-health constraint |
| A32 | Generated proto code can be committed to the repo | **Failed** — `Iverson.AdminUI/.gitignore:3` ignores `generated/`, which is where `scripts/generate_protos.sh` writes. Resolved by generating at build time instead; see Design 4b |

## Known issues

**CONFIRMED BLOCKER: no human identity can satisfy the `Operator` policy today.**
Verified live against the running compose stack on 2026-08-25. This is not a risk to watch —
it is a defect that must be fixed before the tenant-roster widget or either new endpoint can
work. There are **two independent gaps**, and both must close.

`OperatorAuthorizationPolicy.IsSatisfiedBy` (`Iverson.Server/Iverson.Api/OperatorAuthorizationPolicy.cs:9-15`)
has two arms: the `groups` claim contains `operators`, or the `scope` claim contains `admin`.

**Gap A — the console never requests the `groups` scope.** `AuthProvider.tsx` requests
`scope: "openid profile email offline_access"`. Authentik applies a scope mapping only when
the client requests that scope, so the `groups` mapping bound to the `iverson-oidc-default`
provider never fires. A token minted with the console's exact scope string against the
console's own client (`dev-iverson-human-oidc-client-id`) came back with **no `groups` claim,
no `tenant_id` claim, and an empty `scope` claim** — `profile` and `email` are not bound to
that provider either, so nothing at all was granted. Both policy arms fail on an empty scope.
The same token against the Operator-gated `/admin/dlq` returned **403** (versus 401 with no
token, confirming it authenticated and failed authorization, not authentication).

**Gap B — the `operators` group does not exist.** Live enumeration of Authentik groups returns
`authentik Admins`, `authentik Read-only`, `iverson-admin-orchestrators`,
`iverson-loadtest-bypass`, `tenant-admins`. No `operators` group is provisioned by any
blueprint. Fixing Gap A alone is therefore insufficient: a token minted for
`iverson-loadtest-bypass-user` **with** the `groups` scope correctly emitted
`groups: ["iverson-loadtest-bypass"]` — proving the mapping works — and still returned **403**
from `/admin/dlq`.

The `admin` scope arm is no escape hatch: that scope mapping is bound only to
`iverson-admin-automation`, a `client_credentials` service client with no human login path.

**The fixes are specified in Design 4d** and are in scope for this work. They must sequence
ahead of any widget implementation: until both gaps close, the tenant-roster widget and both
new endpoints return 403 for every user.

This is not scope this design invents — the already-planned Tenants page hits the identical
wall through the same policy on the same service, and its nav item is invisible today for the
same reason — but it is now a confirmed defect rather than an open question.

**Data volume cannot report authorization denial.** Accepted, not solved — see Design 2. The
alternative would be changing `Aggregate`'s denial behaviour from empty-response to error,
which is a contract change affecting all five clients and well outside this page's scope.

**The page has no cross-tenant aggregate view.** Data volume is tenant-scoped by design;
an operator wanting deployment-wide row counts is not served by this page. No such surface
exists today and inventing one is out of scope.
