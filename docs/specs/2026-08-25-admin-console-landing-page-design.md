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
- Two new read-only gRPC methods on `Iverson.Api` (see Design 1).
- The prerequisite work those widgets depend on and which does not exist today
  (see Design 4): grpc-web enablement for two existing services, a console gRPC client
  foundation, a browser-reachable route to the API's HTTP/1.1 port, Prometheus
  scrape-target changes, and the identity fixes without which every Operator-gated
  surface returns 403.
- The security remediation in Design 5. Eleven findings are folded into this design: nine from
  the two critical-security-review rounds of 2026-08-25, plus two adjacent defects this design's
  own verification surfaced. Four gate widget work — one in Design 1's transport allowlist, one
  in 4e, two in 4f — and the seven in Design 5 do not.

**Out of scope**

- The four existing stub pages. They stay stubs; this design does not fill them in.
- Jaeger traces. The console already relays browser spans to Jaeger via `/v1/traces`,
  but no widget reads trace data back.
- Any write or mutation surface, with one inherited exception: the health strip's source,
  `GET /health`, is itself write-bearing (`Program.cs:310-311` issues a Qdrant
  `EnsureCollectionAsync` and produces to `iverson.health.probe` on every call). Every other
  widget and both new RPCs are read-only.
- Alerting, thresholds, or notification. The page displays; it does not judge.
- Authentication changes, with one exception. The page uses the console's existing OIDC
  session and does not alter the login flow. Design 5a does change one session setting
  (`revokeTokensOnSignout`), which is remediation folded in here, not page behaviour.

## Design 1 — Server surface

Two new read-only RPCs on `Iverson.Api`, on a new `AdminConsoleService` defined in
`Iverson.Clients/Common/Proto/admin_console.proto`. Neither existing service is a natural home:
`ObjectSearch` is data queries, `ObjectMapping` is schema, and the two tenant services are
tenancy.

**These are gRPC, not minimal-API REST, because REST cannot reach the browser here.**
`charts/admin-ui/templates/ingress.yaml:21` matches `/admin(/|$)(.*)` with
`rewrite-target: /$2` and `charts/api/templates/ingress.yaml:18` matches `/`, both on the same
host — so any endpoint under `/admin/` is rewritten and served by the console's own static file
server, never reaching the API. gRPC paths (`/iverson.AdminConsoleService/<Method>`) do not
collide with that pattern. Going gRPC also puts all five data widgets on the one transport
Design 4b already builds, needs no CORS, and removes the JSON-versus-proto split.

### Transport

grpc-web from a browser is HTTP/1.1 over cleartext. `Iverson.Api/appsettings.json:12-13` binds
`http://*:8080` to `Protocols: Http2` and `:16-17` binds `http://*:8081` to `Protocols: Http1`,
and both Ingress backends currently target **8080**. A browser therefore reaches the API only
over TLS, where ALPN can negotiate HTTP/2 — and the default and local profiles are cleartext
(`values.yaml:22` and `values-local.yaml` both use `http://iverson.local`), where an HTTP/1.1
request to 8080 is rejected. Confirmed directly: HTTP/1.1 to `:8080/health` returns 400, the
same request to `:8081` returns 200.

That 400 is protocol negotiation, not authorization, and it is not a security boundary. A client
that speaks h2c reaches every route on 8080, because no endpoint in `Program.cs` is bound to a
listener (`RequireHost` appears nowhere in the file) — `GET /metrics` over h2c prior-knowledge on
`:8080` returns 200 with the full metrics body. The port split determines which wire protocol a
caller must speak, nothing more. See Design 5f.

This design adds a browser-reachable route to the HTTP/1.1 port under a **dedicated path
prefix**, `/admin-api`. The console calls `/admin-api/iverson.<Service>/<Method>`; the ingress
strips the prefix and forwards to port 8081. Bare `/iverson.<Service>/<Method>` is left alone on
8080, so the language SDK clients' native gRPC is unaffected. The prefix, not the service path,
is what distinguishes the two consumers — and it is the only axis that can, since both speak the
same gRPC paths.

`/admin-api` is deliberately not `/admin`. The console's own rule at
`charts/admin-ui/templates/ingress.yaml:21` is `/admin(/|$)(.*)`, whose `(/|$)` guard requires a
slash or end-of-string after `admin`, so `/admin-api/...` does not match it and no precedence
question arises between the two rules.

The route serves all five gRPC widgets and the health strip, not only these two RPCs:

- **a new Ingress object** — not the existing api Ingress — backed by the api service on port
  **8081**, carrying `rewrite-target: /$1` and **three `pathType: ImplementationSpecific` paths,
  as an allowlist rather than a catch-all**:

  ```
  /admin-api/(iverson\.[A-Za-z0-9_.]+/[A-Za-z0-9_]+)$   # gRPC-Web RPCs
  /admin-api/(health)$                                   # the health strip's source
  /admin-api/(v1/traces)$                                # the OTLP export; see Design 5g
  ```

  An earlier revision of this design used one path, `/admin-api(/|$)(.*)`, with
  `rewrite-target: /$2`. That is a **catch-all**, and port 8081 serves the entire application —
  not only the gRPC surface. It would therefore have published `/metrics` and all four
  `/probe/*` endpoints, every one of them `AllowAnonymous`, to the internet on every profile.
  The allowlist admits exactly the three shapes this design needs and leaves the rest
  unroutable from outside. Each regex captures its target into **group 1** because
  `rewrite-target` is an Ingress-level annotation shared by all paths on the object, so a single
  `/$1` has to serve all three.

  `/health/live` is deliberately absent: the health strip reads the per-store `checks` object,
  which only `/health` returns, and the kubelet reaches `/health/live` in-cluster without the
  ingress. `/metrics` is absent and must stay so — it cannot simply be authenticated instead,
  because Prometheus scrapes it anonymously (Design 4c); keeping it off the edge is the control.

  This mirrors the shape `charts/admin-ui/templates/ingress.yaml` already uses.
  It must be a separate object because `charts/api/templates/ingress.yaml:4-6`
  renders `.Values.ingress.annotations` onto the Ingress's metadata, so `values-aws.yaml:75`'s
  `alb.ingress.kubernetes.io/backend-protocol-version: GRPC` applies to the whole object; an 8081
  path there would place grpc-web, which speaks HTTP/1.1, under a GRPC protocol declaration.

  On AWS give both Ingresses the same `alb.ingress.kubernetes.io/group.name` so they keep sharing
  one ALB while carrying different backend-protocol annotations, **and set
  `alb.ingress.kubernetes.io/group.order` explicitly with this Ingress ahead of the api Ingress.**
  Within an IngressGroup the controller orders rules by that annotation and falls back to the
  lexical order of the Ingress's namespace/name; the api Ingress's `/` `pathType: Prefix` path
  matches every request, so without explicit ordering it shadows this one entirely.
- a `server.proxy` entry in `vite.config.ts` mapping `/admin-api` to `http://localhost:8081` with
  the prefix rewritten away, matching what the ingress does. `vite.config.ts` declares no proxy
  today, which is also why `src/telemetry.ts:19`'s relative `/v1/traces` export does not reach the
  API in development.

The console addresses the API by a **relative, same-origin base** of `/admin-api` — the
same-origin pattern `src/telemetry.ts` already uses — not through `config.apiBaseUrl`, which is
read nowhere in `src/` and whose development value points at the h2c port.
`@improbable-eng/grpc-web` takes that base as its `host` option and appends
`/<package>.<Service>/<Method>` itself.

### `GetMetrics` — Prometheus proxy

Queries Prometheus server-side and returns a fixed, named result set. It does **not** accept
a PromQL parameter from the browser: a pass-through would turn an authenticated console
RPC into an open query interface over every metric the deployment emits, and the page
needs seven numbers.

The response carries: the three reconciliation gauges, the two consumer counters, RPC
request rate / error percentage / p95, and Ollama client p95.

The API has no Prometheus client today — `Program.cs` wires only the *exporter*
(`:71` `AddPrometheusExporter`, `:275` `MapPrometheusScrapingEndpoint`). This RPC adds:

- a configuration key for the Prometheus base URL,
- a named `HttpClient` for it,
- graceful handling of Prometheus being absent, which is a real deployment state:
  `values-laptop.yaml:16-17` sets `prometheus.enabled: false`,
- **two NetworkPolicy additions, without which this RPC cannot connect in any Kubernetes
  profile.** `templates/networkpolicies.yaml:7-10` declares a namespace-wide default-deny on both
  Ingress and Egress; `api-egress` (`:38-63`) enumerates postgres, kafka, starrocks, qdrant,
  ollama, jaeger and authentik but has no Prometheus rule, and no `prometheus-ingress` policy
  exists at all. Both are needed, guarded by the existing `{{- if .Values.prometheus.enabled }}`
  used at `:474`: an `api-egress` rule to `podSelector: { app: {{ .Release.Name }}-prometheus }`
  on TCP 9090, and a `prometheus-ingress` policy allowing from `app: {{ .Release.Name }}-api` on
  TCP 9090. The reverse direction is already allowed (`:487-490`), which is why scraping works
  today and the missing direction is easy to overlook.

PromQL uses Prometheus-mangled metric names, not the OTel instrument names: dots to underscores,
`_total` on counters, `_bucket`/`_sum`/`_count` on histograms, **and the instrument's unit appended
where it declares one**. The five gauge and counter instruments declare no unit
(`ReconciliationTelemetry.cs:25-41` and `Iverson.Events/Telemetry.cs:14,17` pass only
`description:`), so they mangle straight: `reconciliation_queue_depth`, `dlq_unreplayed_count`,
`document_rerender_queue_depth`, `consumer_retries_total`, `consumer_dlq_routed_total`. The two
duration metrics are declared in seconds by the ASP.NET Core and HttpClient instrumentation, so
their real series are `http_server_request_duration_seconds_{bucket,sum,count}` and
`http_client_request_duration_seconds_{bucket,sum,count}` — written out here because the general
rule is exactly where the `_seconds` segment gets dropped.

The Ollama filter is built server-side from `EmbeddingServiceOptions.BaseUrl` rather than
hard-coded, because HTTP client metrics label by `server.address` and the host is
configuration-driven.

### `GetQdrantStats` — collection stats

Returns points count and indexed-vectors count per collection.

No such surface exists. `/health` performs a *write* probe
(`EnsureCollectionAsync("iverson-probe", 4)`) and never reads collection metadata, and
`IVectorSchemaManager` (`Iverson.Server/Iverson.Vector/IVectorRoles.cs:23-27`) exposes only
`EnsureCollectionAsync` and `ApplyCollectionAsync`. The underlying capability does exist —
`IntelligenceCollectionManager` already calls `ListCollectionsAsync` (`:22`) and
`GetCollectionInfoAsync` (`:60`) — so this RPC adds a read interface over the same
client, not new infrastructure.

Collections are tenant-scoped by `IntelligenceTenantScope.ResolveCollectionName`
(`Iverson.Server/Iverson.Vector/IntelligenceTenantScope.cs:11`), and per-collection scoped
API keys are minted at `:18`. Enumerating all collections is therefore a cross-tenant
operation and is gated accordingly.

### Authorization

`AdminConsoleService` is mapped with `.RequireAuthorization("Operator").EnableGrpcWeb()`,
matching how `TenantLifecycleGrpcService` is mapped at `Program.cs:444`.

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
| Fan-out backlog | `reconciliation.queue_depth`, `dlq.unreplayed_count`, `document_rerender.queue_depth` | Three gauges, current value |
| DLQ and retry rate | `consumer.retries`, `consumer.dlq_routed` | Rate over the window |
| RPC health | `http.server.request.duration` | Request rate, error percentage, p95 |
| Embedding latency | `http.client.request.duration` | p95 against Ollama |

### Band C — new surface

| Widget | Source | Shows |
|---|---|---|
| Qdrant collection stats | `AdminConsoleService.GetQdrantStats` | Points, indexed vectors per collection |

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
| Health strip | 60s poll |
| Band B metrics | 30s poll |
| Qdrant stats | 30s poll |
| Tenant roster, schema catalog | On mount, plus manual refresh |
| Data volume per type | On mount, plus manual refresh — never polled |

The health strip polls at 60s rather than the 10s a status tile would otherwise want, because
`/health` is write-bearing: each call produces a Kafka message and issues a Qdrant
collection-ensure (`Program.cs:310-311`). The kubelet already probes it; a console tab adding six
more writes a minute is the part that is hard to justify. `/health/live` (`:299`) would avoid the
writes entirely but returns no `checks` object, which is what the per-store tiles are built from.

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

Verification found four foundations this design assumed were present that do not exist (4a-4d).
They are in scope because without them the specified widgets are impossible. 4d is the one
that blocks everything else: until it lands, every Operator-gated surface returns 403.

4e and 4f are security findings that gate widget work for the same reason — this design either
causes them or depends on them. The seven findings that do **not** gate widget work are in
Design 5.

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
resolvable path (a protoc devDependency, or a documented `PROTOC` override). This costs a build
dependency and buys a client that cannot drift from the `.proto` contract.

CI enforcement is **not** in scope, because there is nothing to enforce it in:
`.github/workflows/` contains only `codeql.yml` and `deploy-validate.yml`, neither of which
installs Node, runs `npm`, or references `Iverson.AdminUI`. The console has no build, lint,
type-check or test job in CI today. Making the build regenerate is the drift guarantee this
section claims; adding an AdminUI CI job is separate, currently-unscoped work.

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


### 4e — Operational endpoint authorization and the cost of `/health`

The four probe endpoints — `/probe/sql`, `/probe/starrocks`, `/probe/vector` (`Program.cs:336-352`)
and `/probe/kafka` (`:354-359`) — are `AllowAnonymous` and perform real work against live
datastores. `/probe/vector` issues a Qdrant `EnsureCollectionAsync`; `/probe/kafka` produces to
`iverson.probe`. Verified live: a single anonymous `POST /probe/kafka` **created** the
`iverson.probe` topic, which did not exist beforehand, through broker auto-create.

**Fix:** add `.RequireAuthorization("Operator")` to all four. They are operator diagnostics, and
nothing calls them — `grep -rn "/probe/"` across the repo returns only the four definitions and one
descriptive mention in `Iverson.Server/docs/security/tma.md:117`. No kubelet probe, compose
healthcheck, test, or script depends on them being anonymous. This sequences after 4d, since
`Operator` is unsatisfiable until then.

**`/metrics` is not included and must stay anonymous.** Design 4c has Prometheus scraping
`{{ .Release.Name }}-api:8081` and `-worker:8081` with no auth; requiring authorization there
breaks metrics collection, which would take Band B down with it. It is kept off the public edge by
Design 1's allowlist instead — routing, not authentication, is the right control for it.

**`/health` stays anonymous and gets a cache.** The kubelet's `readinessProbe`
(`charts/api/templates/deployment.yaml:173-180`) hits `/health` on 8081, and Design 1's allowlist
publishes it for the health strip. It is also the most expensive endpoint in the application: each
call fans out to Postgres, StarRocks, Qdrant and Kafka (`Program.cs:308-313`), two of those being
writes. One cheap request therefore costs four backend operations, at any rate a caller chooses.

**Fix:** memoize the fan-out result for **5 seconds** behind `IMemoryCache`, already registered at
`Program.cs:217`, following the existing `Tenancy/TenantStatusCache.cs` pattern. The window must
stay below the readiness probe's period — that probe declares no `periodSeconds`, so it inherits
Kubernetes' 10-second default. 5s keeps readiness at most one cycle stale while bounding the
Qdrant and Kafka writes to at most one per 5s per pod **regardless of request rate**. Today that
rate is unbounded; that is the whole of the change.

This does not alter Design 3's 60-second health-strip poll. The cadence reasoning there is about
what the console itself should cost; this is about what an arbitrary caller can force.

### 4f — NetworkPolicy: reaching port 8081

`templates/networkpolicies.yaml:7-10` applies a namespace-wide default-deny. The rule that
currently lets anything reach the api on 8081 is `:28-33`:

```yaml
- from: []   # kubelet readiness/liveness probes: ...
  ports: [{ protocol: TCP, port: 8081 }]
```

An **empty `from` matches every source**, not the kubelet its comment describes. Since 8081 serves
the whole application, every pod in the cluster can currently reach every REST endpoint on the api
directly by pod IP. That is the finding.

It is also, today, the only thing that would let the new `/admin-api` Ingress work at all — **this
design's transport currently depends on the defect.** Closing the hole without replacing it breaks
the landing page. Both halves land together.

Port 8081 has **four** legitimate consumers, and a correct rule set names each:

| Consumer | Selector | Why |
|---|---|---|
| ingress-nginx | `namespaceSelector{kubernetes.io/metadata.name: ingress-nginx}` | serves the new `/admin-api` Ingress on the nginx profiles |
| Prometheus | `podSelector{app: <release>-prometheus}` | scrapes `api:8081` and `worker:8081` (Design 4c) — omitting it silently empties Band B |
| kubelet | `ipBlock` over the node CIDR | `readinessProbe` and `livenessProbe` both target 8081 |
| **AWS ALB** | `ipBlock` over the VPC CIDR | `values-aws.yaml:74` sets `target-type: ip`, so the ALB registers pod IPs and traffic arrives from VPC ENIs — **no `namespaceSelector` can express this**, because there is no ingress-nginx namespace on AWS |

The CIDRs differ per environment, so they come from a values key
(`networkPolicy.clusterCidrs`, a list) rather than being hardcoded; the nginx profiles set the node
CIDR and the AWS overlay sets the VPC CIDR.

**The same gap exists on port 8080 and is fixed here too.** The rule at `:22-27` admits only the
`ingress-nginx` namespace. NetworkPolicy is enforced on AWS —
`deploy/terraform/modules/cluster-aws/main.tf:475` sets `enableNetworkPolicy = "true"` on the VPC
CNI, and `values-aws.yaml` never disables `networkPolicy.enabled` (`values.yaml:13-14`, default
`true`). The operators module installs `aws-load-balancer-controller`, not ingress-nginx. So on AWS
the port-8080 rule selects a namespace that does not exist, and the default-deny denies ALB→8080:
**the api Ingress cannot work on the AWS profile as configured.** The same `ipBlock` list fixes it.
This is a pre-existing defect that this design does not cause; it is repaired here because 4f is
already rewriting these rules and leaving one port correct and the other broken would be worse.

## Design 5 — Security remediation

Seven findings that do **not** gate widget work — six from the critical-security-review rounds of
2026-08-25, and 5g, which this design's own verification surfaced. They are in this spec because
all eleven findings were scoped into it; the plan should not sequence widget tasks behind them.

### 5a — Revoke tokens at signout

`AuthProvider.tsx:5-12` never sets `revokeTokensOnSignout`, which defaults to `false`
(`oidc-client-ts.js:2484`); `_signoutStart` revokes only when it is true (`:3418-3420`). Because
the console requests `offline_access`, a **refresh token** is issued and persisted, and clicking
Logout ends the Authentik session while leaving that credential valid until its own expiry.

**Fix:** set `revokeTokensOnSignout: true`. `react-oidc-context`'s `AuthProvider` destructures its
own props and spreads `...userManagerSettings` into the `UserManager`
(`react-oidc-context.js:139-149`), so the setting passes straight through.

**`offline_access` is deliberately kept.** Dropping it would remove the refresh token from browser
storage, but this design creates a page that polls for hours, so the access token must renew.
Without a refresh token `automaticSilentRenew` falls back to iframe silent renew, which needs a
`silent_redirect_uri` handler the console does not have — `CallbackPage` would mount the entire app
inside the iframe. That is real work this design does not otherwise need, and the revocation fix
addresses the sharper half of the finding without it.

### 5b — Keep the OIDC authorization code out of telemetry

`DocumentLoadInstrumentation` writes the full `location.href` into `http.url` and `url.full`
(`instrumentation-document-load/instrumentation.js:69,72,83,87`), and the OIDC redirect lands on
`/admin/callback?code=…&state=…`. Those spans are exported to `/v1/traces` and relayed to Jaeger.
Nothing cleans the URL: `react-oidc-context` strips auth params only when an `onSigninCallback` is
supplied (`react-oidc-context.js:209`) and `AuthProvider.tsx` supplies none, so the only thing that
removes them is `CallbackPage`'s `navigate("/", { replace: true })` — which `CallbackPage.tsx:15-19`
runs **only on the success path**. On an authentication error the code stays in the address bar.

**Fix, both halves:**

- pass `onSigninCallback: () => window.history.replaceState({}, document.title, window.location.pathname)`
  to `AuthProvider` — a documented prop (`react-oidc-context` types, `onSigninCallback?: (user) => Promise<void> | void`)
- pass `applyCustomAttributesOnSpan: { documentLoad: span => …, documentFetch: span => … }` to
  `DocumentLoadInstrumentation`, overwriting `http.url`/`url.full` with `location.pathname`. The
  option exists with exactly that shape (`instrumentation-document-load/types.d.ts:14-18`).

The second is what covers the error path, where the first never runs.

### 5c — Security response headers

`nginx.conf:10-20` is the complete server block and sets no `Content-Security-Policy`,
`X-Frame-Options`, `X-Content-Type-Options`, or `Referrer-Policy`. The base image adds none, and
`Dockerfile:24` replaces the stock `conf.d/default.conf` wholesale.

**Fix:** add an `add_header` block. Three directives are dictated by what this design introduces,
which is why authoring it before the widgets land means writing it twice:

- `connect-src 'self' <authentik-origin>` — `'self'` covers the same-origin `/admin-api` gRPC-Web
  calls and the `/v1/traces` export; the Authentik origin is needed for the token endpoint
- `style-src 'self' 'unsafe-inline'` — MUI/Emotion injects styles at runtime
- `frame-ancestors 'none'` — an authenticated admin console should not be framable

`Strict-Transport-Security` belongs at the TLS-terminating ingress, not here: this listener is
plaintext HTTP behind a proxy.

### 5d — Runtime config generation

`docker-entrypoint.sh:3-5` runs `envsubst` over `config.js.template` to produce `config.js`, a file
the browser executes. `envsubst` has no notion of JavaScript syntax, so a value containing a double
quote breaks out of its string literal into executable code that runs on every page load with full
session access.

**Fix:** validate before substituting. Each of `OIDC_CLIENT_ID`, `OIDC_AUTHORITY` and
`API_BASE_URL` must match `^[A-Za-z0-9:/._-]+$`; the entrypoint exits non-zero otherwise. That is
an allowlist, not a denylist, and the value space (a client id and two URLs) genuinely fits it.

Emitting JSON with `jq` would be the more usual fix and was rejected on evidence: **`jq` is not
present in the runtime image.** Verified by running it — `nginxinc/nginx-unprivileged:1.27-alpine`
reports `jq` absent and `envsubst` present. Adding `apk add --no-cache jq` would work but grows the
runtime image for a check three lines of shell already cover.

### 5e — Route-level group guards

`router.tsx:20-21` registers `/tenants` and `/tenant-admin` with no authorization guard;
`Sidebar.tsx:18,23` only hides the links. Any authenticated user reaching those URLs directly gets
the component. `Sidebar.test.tsx:57-64` asserts link absence, never route unreachability.

**Fix:** a `RequireGroup` wrapper mirroring `AuthGate`'s shape, checking the claim and redirecting
otherwise, plus a test asserting the route is unreachable rather than merely unlinked.

This is defense-in-depth, not a live exposure: both pages are `Coming soon` stubs and stay stubs
under this design. It is explicitly **not** a change to the landing page at `/`, which shows
Operator-gated widgets to every authenticated user by design and degrades per Design 3 — the server
is the authority there, and a 403 renders as an unavailable card.

### 5f — Bind operational endpoints to a listener

No endpoint is bound to a Kestrel listener; `RequireHost` appears nowhere in `Program.cs`. The
`appsettings.json:9-17` split between 8080 (`Http2`) and 8081 (`Http1`) selects which wire protocol
a caller must speak, and nothing else. `GET /metrics` over h2c on 8080 returns 200 with the full
body, and 8080 is the port `charts/api/templates/ingress.yaml:18-24` routes `/` to.

**Fix, two parts:**

- Settle empirically whether AWS ALB with `backend-protocol-version: GRPC` (`values-aws.yaml:75`)
  forwards a non-gRPC HTTP/2 request through to `/metrics` on 8080. One request against a deployed
  ALB answers it. If it does, this is already reachable from the internet and the finding is more
  urgent than its current rating.
- Serve the operational endpoints only on 8081, by testing `HttpContext.Connection.LocalPort` in an
  endpoint filter. **Not** `RequireHost("*:8081")`: `RequireHost` matches the `Host` header, and
  behind an ingress that header carries the external hostname with no port, so the filter would
  reject legitimate traffic and admit nothing. `Connection.LocalPort` reads the accepting socket.

### 5g — Telemetry export path

`telemetry.ts:19` posts spans to a relative `/v1/traces`. In production that resolves against the
api Ingress's `/` `pathType: Prefix` rule to port 8080, where a browser's HTTP/1.1 POST is rejected
with 400 — the same failure this design already documents for development, where no Vite proxy
exists. The console's trace export therefore does not work in either environment.

**Fix:** point `OTLP_TRACES_URL` at `/admin-api/v1/traces`, which Design 1's allowlist admits and
the ingress rewrites to `/v1/traces` on 8081. No server change is needed: the endpoint already
exists at `Program.cs:452-460` with `RequireAuthorization()`, and `telemetry.ts:29-38` already
attaches the bearer token through the exporter's `headers` factory.

Reading trace data back into a widget remains out of scope. This restores an export path the
console already believes it has.

## Verified assumptions

A1-A26 were enumerated against the approved design before any file was read for verification,
then checked: nineteen held, seven failed or resolved differently. A27-A29 record the live
verification of the `Operator` policy, run afterwards against the running compose stack on
2026-08-25; all three failed.

A39-A51 were enumerated the same way against the security remediation added to this design
(Design 1's allowlist, 4e, 4f and Design 5), before any file was read for that pass: nine held,
four failed. Two of the failures changed the design's shape rather than a detail — `jq` is absent
from the runtime image (A43), and namespace-selector NetworkPolicy rules grant nothing on AWS
(A44). Two more corrected beliefs this spec previously relied on: the anonymous endpoints are not
confined to port 8081 (A51), and the console's trace export works in neither environment (A50).

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
| A33 | The `groups` claim reaches `auth.user?.profile` | **Holds** — `oidc-client-ts` populates `user.profile` from the ID token, not the access token. A real token response for `dev-iverson-human-oidc-client-id` with scope `openid groups tenant_id offline_access` carries an `id_token` whose claims include `groups` and `tenant_id`. Design 4d's `Sidebar.tsx:20` repair claim therefore holds |
| A34 | An `iverson-worker` scrape target exists in docker-compose | **Holds** — `docker-compose.yml:438` defines the service with `WORKLOAD_ROLE=worker` at `:454`, and `MapPrometheusScrapingEndpoint` (`Program.cs:275`) sits outside the `if (workloadRole == "api")` gate at `:438`, so the worker serves `/metrics` on 8081 |
| A35 | Adding a proto to `Common/Proto` does not break the other language clients | **Holds** — `Iverson.Client.Contracts.csproj:17` globs `../../Common/Proto/*.proto` with `GrpcServices="Both"`, so the server base class generates without a csproj edit; `Iverson.AdminUI/scripts/generate_protos.sh` and `Iverson.Clients/TypeScript/scripts/generate_protos.sh` also glob. Python (`Iverson.Clients/Python/scripts/generate_protos.sh`) and Go both list the four `object_*` protos explicitly, so neither is touched. Nothing in `Iverson.ClientConformance` enumerates the service set |
| A36 | `http_route` is a real label on the server-duration metric | **Holds** — present on every `http_server_request_duration_seconds_*` sample on the live `/metrics` endpoint, alongside `http_request_method`, `http_response_status_code` and `network_protocol_version`. The Prometheus scrape endpoint appears as `http_route="/metrics"` and gRPC calls as `http_route="/iverson.<Service>/<Method>"`, so Design 2's exclusion filter is expressible as written |
| A37 | `/admin-api` cannot match the console's own ingress rule | **Holds** — `charts/admin-ui/templates/ingress.yaml:21` is `/admin(/|$)(.*)`, whose `(/|$)` guard requires `/` or end-of-string after a literal `admin`; `/admin-api/...` supplies `-` at that position and `^/admin` cannot match elsewhere. No precedence contest between the two rules |
| A38 | An ingress rewrite can strip the prefix so the backend sees the real gRPC path | **Holds, restated** — originally verified for `rewrite-target: /$2` over `/admin-api(/|$)(.*)`. That catch-all was replaced by Design 1's three-path allowlist, so the live form is `rewrite-target: /$1` with each regex capturing into group 1. The rewrite mechanism is unchanged and the same pattern is already in service in this chart for `/admin`, which sets no `use-regex` annotation either. See A45 |
| A39 | Nothing in the deployment calls `/probe/*` anonymously | **Holds** — `grep -rn "/probe/"` across the repo returns only the four definitions in `Program.cs:336-359` and one descriptive mention in `Iverson.Server/docs/security/tma.md:117`. No kubelet probe, compose healthcheck, test or script depends on them. Gating them behind `Operator` (4e) breaks nothing |
| A40 | Prometheus scrapes `/metrics` on 8081 without authentication | **Holds** — `charts/prometheus/templates/configmap.yaml:10-15` defines `iverson-api` → `<release>-api:8081` and `iverson-worker` → `<release>-worker:8081`, no auth stanza. This is why 4e leaves `/metrics` anonymous and Design 1's allowlist excludes it instead |
| A41 | The kubelet's readiness probe period bounds `/health`'s cache window | **Holds** — `charts/api/templates/deployment.yaml:173-180` targets `/health` on 8081 and declares **no** `periodSeconds`, inheriting Kubernetes' 10-second default. 4e's 5-second window sits under it |
| A42 | An in-process cache is available without new infrastructure | **Holds** — `Program.cs:217` already calls `AddMemoryCache()`, and `Tenancy/TenantStatusCache.cs:8` is an existing `IMemoryCache` consumer to pattern 4e after |
| A43 | `jq` is available in the runtime image, so `config.js` can be emitted as JSON | **Failed** — running `nginxinc/nginx-unprivileged:1.27-alpine` reports `jq` **absent**, `envsubst` present. The JSON-emission fix would have crash-looped the container at startup. Resolved by allowlist validation of the three values instead; see Design 5d |
| A44 | NetworkPolicy on AWS can admit ingress traffic by namespace selector | **Failed** — `values-aws.yaml:74` sets `target-type: ip`, so the ALB registers pod IPs and traffic originates from VPC ENIs, not from any namespace; `deploy/terraform/modules/cluster-aws/main.tf:475` sets `enableNetworkPolicy = "true"` so policies **are** enforced; and the operators module installs `aws-load-balancer-controller`, so no `ingress-nginx` namespace exists. Selector-based rules grant nothing on AWS. Resolved with an `ipBlock` list; see Design 4f |
| A45 | Multiple regex paths on one Ingress can each be rewritten correctly | **Holds, with a constraint** — `charts/admin-ui/templates/ingress.yaml:5` sets `rewrite-target` as an **Ingress-level annotation**, shared by every path on the object. Design 1's three paths therefore all capture into group 1 so one `/$1` serves all three. Restates A38 |
| A46 | Real gRPC-Web request paths match the allowlist regex | **Holds** — every proto in `Iverson.Clients/Common/Proto/` declares `package iverson`, and the six services are `ObjectPersistenceService`, `ObjectRetrievalService`, `ObjectSearchService`, `ObjectMappingService`, `TenantLifecycleGrpcService`, `TenantAdminGrpcService`. `iverson\.[A-Za-z0-9_.]+/[A-Za-z0-9_]+` matches all of them and the new `AdminConsoleService` |
| A47 | `revokeTokensOnSignout` reaches `UserManagerSettings` through `react-oidc-context` | **Holds** — `react-oidc-context.js:139-149` destructures its own props and spreads `...userManagerSettings` into the `UserManager` constructor, so unrecognised settings pass through unchanged |
| A48 | `onSigninCallback` is a supported `AuthProvider` prop | **Holds** — declared in `react-oidc-context`'s types as `onSigninCallback?: (user: User \| undefined) => Promise<void> \| void`, and invoked at `react-oidc-context.js:209` only when supplied |
| A49 | `DocumentLoadInstrumentation` allows overwriting span attributes | **Holds** — `instrumentation-document-load/types.d.ts:14-18` declares `applyCustomAttributesOnSpan?: { documentLoad?, documentFetch?, resourceFetch? }`, covering both spans that receive `location.href` |
| A50 | The console's `/v1/traces` export works in production | **Failed** — `telemetry.ts:19` posts to a relative `/v1/traces`, which resolves against the api Ingress's `/` `Prefix` rule to port 8080, where a browser's HTTP/1.1 POST is rejected with 400. The export works in neither environment. Resolved by routing it through `/admin-api`; see Design 5g |
| A51 | The `AllowAnonymous` endpoints are reachable only on port 8081 | **Failed** — `RequireHost` appears nowhere in `Program.cs`, so ASP.NET routing serves every endpoint on both listeners. `GET /metrics` over h2c prior-knowledge on `:8080` returns 200 with the full 121,797-byte body, and an anonymous `POST /probe/kafka` on the same port created the `iverson.probe` Kafka topic. The port split is a protocol convention, not a security boundary; see Design 5f |

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
