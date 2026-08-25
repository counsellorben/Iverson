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
  (see Design 4): Prometheus scrape-target changes and the identity fixes without which every
  Operator-gated surface returns 403.
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

Five read-only JSON endpoints on `Iverson.Api`, under `/admin/console/`. They are ordinary
minimal-API endpoints, not gRPC.

**REST reaches the browser here because the console reaches the API on its own hostname.** An
earlier revision of this design made these gRPC methods, on the reasoning that
`charts/admin-ui/templates/ingress.yaml:21` matches `/admin(/|$)(.*)` and
`charts/api/templates/ingress.yaml:18` matches `/` on the same host, so any `/admin/*` endpoint is
rewritten to the console's own static file server and never reaches the API. That is true, and it
is a property of putting both on one hostname. Once the API has its own host the collision does
not exist, and the reason to introduce a proto contract no other client consumes goes with it.

Three of the five endpoints project data the existing gRPC services already produce; two are new
surface. **None of them changes the proto contract the five language SDK clients share**, and no
`.proto` file is added.

### Transport

The console is served at `<ingressHost>/admin`; the API answers on a **dedicated hostname**,
`admin-api.<ingressHost>`. This mirrors `charts/authentik/templates/ingress.yaml:21`, which
already does `printf "authentik.%s" .Values.global.ingressHost` — a second hostname is an
established, working pattern in this chart, not new machinery. The api subchart can see the
global value; `charts/api/templates/deployment.yaml:137` already uses
`.Values.global.ingressHost`.

The name is `admin-api`, not `api`, because the bare host already *is* the API: it serves native
gRPC on 8080 to the five SDK clients, and that Ingress is left completely alone.

**A new Ingress object** on `admin-api.<ingressHost>`, backed by the api service on port
**8081**, with three `pathType: Prefix` paths and **no rewrite annotation of any kind**:

| Path | Serves |
|---|---|
| `/admin` | the console's JSON endpoints under `/admin/console/`, and the existing Operator-gated `/admin/dlq` and `/admin/reconcile` |
| `/health` | the health strip's source |
| `/v1/traces` | the OTLP export (see Design 5g) |

`Prefix` matches element-wise on `/`-split segments, so `/admin` matches `/admin/console/tenants`
and `/admin/dlq` but not `/administration`. Plain prefix routing is expressible natively on
ingress-nginx **and** as an ALB path pattern, so one rule behaves identically on both
controllers — no regex, no `rewrite-target`, no path-stripping middleware, and no ordering
contest with the api Ingress, which is on a different host.

**The object carries its own annotations block**, supplied from its own values key on the pattern
`charts/admin-ui/templates/ingress.yaml` and `charts/authentik/templates/ingress.yaml` already
use. It does not reuse the api subchart's: `charts/api/templates/ingress.yaml:4-6` renders
`.Values.ingress.annotations` onto the Ingress's own metadata, so on AWS that value carries
`values-aws.yaml:75`'s `alb.ingress.kubernetes.io/backend-protocol-version: GRPC` — **which this
Ingress must not have**, because it serves HTTP/1.1 JSON and not gRPC. Declaring a gRPC target
group for it would break every console call on that profile.

**In every other respect it follows the api Ingress's own per-profile shape**, in the same values
file — `className`, annotations and TLS convention — exactly as the admin-ui and authentik
Ingresses already do. That shape is not one thing: `values-aws.yaml:71-85` uses `className: "alb"`
with `scheme`, `target-type`, `certificate-arn`, `listen-ports` and `ssl-redirect`, terminating TLS
on an ACM certificate with `tlsSecretName` deliberately empty; `values-azure.yaml:70-73` uses
`className: "azure-application-gateway"` and `values-gcp.yaml:71-74` uses `className: "gce"`, and
both of those terminate TLS from a Kubernetes Secret through the Ingress's own `tls:` block
instead. An object rendered without a `className` reaches no controller, and one rendered without
`tlsSecretName` on azure or gcp serves no TLS — either way the console cannot reach the API on that
profile.

`values-laptop.yaml` needs no such block: it sets `adminUi.enabled: false` (`:19`), so that profile
runs no console.

Whether this Ingress shares an ALB with the api Ingress through
`alb.ingress.kubernetes.io/group.name` or provisions its own is a cost preference, not a
correctness question — `backend-protocol-version` is Ingress-scoped, so group members may carry
different values. Unlike the previous path-prefix arrangement, **no `group.order` is needed**: the
two objects are on different hosts and cannot shadow one another.

**This is also what keeps the anonymous surface off the edge.** `/metrics` and the four
`/probe/*` endpoints sit under none of the three prefixes, so they are simply not routed to this
host. The allowlist is expressed as ordinary ingress paths rather than as a regex or a filter.
`/metrics` cannot be authenticated instead — Prometheus scrapes it anonymously (Design 4c) — so
keeping it unrouted is the control.

**CORS is required and is new.** The console at `<ingressHost>` calling
`admin-api.<ingressHost>` is cross-origin. The API adds `AddCors`/`UseCors` with the console
origin supplied by configuration — explicitly **not** `AllowAnyOrigin` — allowing the
`Authorization` and `Content-Type` request headers so the preflight succeeds. `AllowCredentials`
is **not** set: authentication is a bearer token in a header, not a cookie, so there is no
`SameSite` or credentialed-CORS dimension. `UseCors` is placed before `UseAuthentication`, so
preflight `OPTIONS` requests are answered before the fallback policy can reject them.

The console is already a multi-origin application — it calls `authentik.<ingressHost>` for OIDC
discovery and the token exchange on every login — so this is a third origin in an existing
pattern, not a new architecture.

**`config.apiBaseUrl` becomes the base for every API call.** It is read nowhere in `src/` today;
it now carries the `admin-api` origin per environment, and the console composes absolute URLs
from it. This removes the relative-path arrangement entirely: no `server.proxy` entry in
`vite.config.ts`, and development exercises the same cross-origin CORS path as production rather
than a proxy that conceals it.

Two consequential changes come with that:

- `Iverson.AdminUI/.env.development` sets `VITE_API_BASE_URL=http://localhost:8080`, which is the
  h2c-only gRPC port. It moves to `8081`.
- **`docker-compose.yml` does not publish 8081** (`docker compose port iverson-api 8081` returns
  nothing). It must, or local development cannot reach the API at all.

Local and kind profiles need the new hostname resolvable.
`docs/user-management-and-security.md:231-235` already documents adding
`<ingress-controller-ip>  iverson.local authentik.iverson.local` to `/etc/hosts`;
`admin-api.iverson.local` joins that line.

The cloud profiles need a certificate covering the new name, and the two mechanisms differ:

- **aws** — the ACM certificate referenced by `alb.ingress.kubernetes.io/certificate-arn` must
  cover `admin-api.<ingressHost>`, as a SAN alongside the existing host or via a wildcard.
- **azure and gcp** — the new Ingress gets its **own** `tlsSecretName`, for example
  `iverson-admin-api-tls`, holding a certificate for `admin-api.<ingressHost>`. This follows the
  one-Secret-per-Ingress pattern those profiles already use three times over
  (`values-azure.yaml:78,130,141` and `values-gcp.yaml:79,131,143` declare `iverson-api-tls`,
  `iverson-authentik-tls` and `iverson-admin-ui-tls`), and it must **not** reuse
  `iverson-api-tls`: that certificate names the bare host, and
  `charts/api/templates/ingress.yaml:9-13` binds one host to one Secret, so presenting it for
  `admin-api.<ingressHost>` fails the handshake on name mismatch.

### The endpoint set

| Endpoint | Authorization | Backed by |
|---|---|---|
| `GET /admin/console/tenants` | `Operator` | `ITenantRepository.ListAsync()` (`Program.cs:214`) |
| `GET /admin/console/schema` | authenticated; evaluator consulted with the caller's own principal | the extracted schema-catalog reader |
| `GET /admin/console/data-volume` | authenticated; evaluator consulted with the caller's own principal | the extracted aggregate reader |
| `GET /admin/console/metrics` | `Operator` | a new Prometheus `HttpClient` |
| `GET /admin/console/qdrant` | `Operator` | a new read interface over the Qdrant client (see that endpoint's section) |

The health strip uses the existing anonymous `GET /health` and adds no endpoint.

Injecting these services into minimal-API endpoints follows a pattern already in the file:
`Program.cs:348` injects `IVectorSchemaManager` into `/probe/vector`.

**The two authenticated rows are deliberate and must not be normalised to `Operator`.**
`ObjectMappingGrpcService.GetSchema` carries no `[Authorize]` — `:61-62` records that it is
discovery, reachable by any authenticated caller, filtering per-row and per-field internally —
and `ObjectSearchGrpcService` is mapped at `Program.cs:443` with no `RequireAuthorization` for
the same reason. Gating these two on `Operator` because their three siblings are would silently
change who can see what.

**These two endpoints pass `HttpContext.User` to the evaluator as the acting user.** This matters
because of how the evaluator treats an absent one: `RowFieldAuthorizationEvaluator.cs:14-15`
returns a not-denied, unrestricted decision when `actingUser` is null. The acting user is
populated only by `ActingUserInterceptor`, which is registered on the gRPC pipeline
(`Program.cs:87`) and returns early when no `x-acting-user-authorization` header is present. The
console sends no such header, so without this the filtering would be inert and every
authenticated caller would receive the complete catalog and every type's row count.

Note what this does and does not change: `RowFieldAuthorizationEvaluator.cs:32-33` also returns
an unrestricted decision when the principal carries no `tenant_id` claim, and an operator has
none. So for an operator the result is the same full view either way; the difference appears only
for a tenant-scoped human, which is exactly where it should.

**Two extractions are required**, and they are the genuine cost of serving JSON from endpoints
rather than annotating the proto. `ListTenants` needs neither: it is already a three-line
delegation to `ITenantRepository.ListAsync()`.

**The schema-catalog reader.** `GetSchema`'s two-pass algorithm — pass one drops types under
row-level denial or an empty authorized-field set, pass two emits relations and drops those whose
related type did not survive — moves into a service taking `ClaimsPrincipal?` as a parameter. The
gRPC method passes `_actingUserAccessor.ActingUser`; the endpoint passes `HttpContext.User`. Each
caller supplies its own identity source, so the extraction depends on no shared mutable accessor
and no interceptor.

**The aggregate reader.** `Aggregate`'s path is not callable as it stands: its body is inline in
the gRPC service, composed from members private to it — `RequireSchema`
(`ObjectSearchGrpcService.cs:771`) and `RunAggregationAsync` (`:536`) — and it operates on proto
types (`AggregateRequest`, `AggregationSpec`, `request.Joins`) that a JSON endpoint has no reason
to construct, while the method itself needs a `ServerCallContext`. The reachable seam is one level
down: `RunAggregationAsync` delegates to
`search.AggregateAsync(SchemaBuilder.ToEngagementQuerySchema(schema), query, spec, having, …)`,
which takes domain types. The extraction is a service that resolves the schema, evaluates
authorization, builds a count spec and calls `search.AggregateAsync` — used by both the gRPC
method and the endpoint.

Responses are plain JSON projections of what each widget renders: the schema endpoint returns
object types with field counts and relation edges, not full descriptors.

**The console gains no client dependency and loses four.** `@improbable-eng/grpc-web` (last
published 2022-04-05), `google-protobuf`, `long`, and the `ts-proto` devDependency are removed
from `Iverson.AdminUI/package.json`. There is no generated code to delete — `src/` has none — so
this is subtraction only. The console calls the API with `fetch`.

### `GET /admin/console/metrics` — Prometheus proxy

Queries Prometheus server-side and returns a fixed, named result set. It does **not** accept
a PromQL parameter from the browser: a pass-through would turn an authenticated console
endpoint into an open query interface over every metric the deployment emits, and the page needs
nine named values across four widgets.

The response carries: the three reconciliation gauges, the two consumer counters, RPC
request rate / error percentage / p95, and Ollama client p95.

The API has no Prometheus client today — `Program.cs` wires only the *exporter*
(`:71` `AddPrometheusExporter`, `:275` `MapPrometheusScrapingEndpoint`). This endpoint adds:

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
  today and the missing direction is easy to overlook. These are unaffected by the transport
  change: they concern api→prometheus egress inside the cluster.

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

### `GET /admin/console/qdrant` — collection stats

Returns points count and indexed-vectors count per collection.

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

Per-endpoint, as tabled above. Three endpoints carry `Operator`; two are authenticated-only with
the evaluator consulted, matching the gRPC services they project.

There is no generic "admin" scope. `Program.cs:141-156` declares exactly three policies —
`Operator`, `SchemaAdmin`, `TenantAdmin` — over a `FallbackPolicy` requiring an authenticated
user (`:142-145`). `Operator` is the policy the existing operational endpoints use (`/reconcile`
at `:373`, `/admin/dlq` at `:380`, `/admin/dlq/replay` at `:392`), so the three new
Operator-gated endpoints sit alongside them under the same `/admin` prefix and the same policy.
`RequireAuthorization()` with no policy name yields the fallback, which is what the two
authenticated-only endpoints use.

This is a deliberate departure from `/health` and `/probe/*`, which are `AllowAnonymous` because
a load balancer calls them — and which Design 4e narrows.

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
| Tenant roster | `GET /admin/console/tenants` | Tenant count and list with state |
| Schema catalog | `GET /admin/console/schema` | Object types, field counts, relation edges |
| Data volume per type | `GET /admin/console/data-volume` | Row count per object type |

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
| Qdrant collection stats | `GET /admin/console/qdrant` | Points, indexed vectors per collection |

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

Verification found foundations this design assumed were present that do not exist (4c, 4d).
They are in scope because without them the specified widgets are impossible. 4d is the one
that blocks everything else: until it lands, every Operator-gated surface returns 403.

Two earlier prerequisites, 4a (grpc-web enablement on two services) and 4b (a console gRPC client
foundation with build-time proto codegen), existed only to serve a gRPC transport. Moving the
console to JSON on its own hostname removed both, along with the browser-reachability problem
they were solving.

4e and 4f are security findings that gate widget work for the same reason — this design either
causes them or depends on them. The seven findings that do **not** gate widget work are in
Design 5.

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

It is also, today, the only thing that would let the new `admin-api` Ingress work at all — **this
design's transport currently depends on the defect.** Closing the hole without replacing it breaks
the landing page. Both halves land together.

Port 8081 has **four** legitimate consumers, and a correct rule set names each:

| Consumer | Selector | Why |
|---|---|---|
| ingress-nginx | `namespaceSelector{kubernetes.io/metadata.name: ingress-nginx}` | serves the new `admin-api` Ingress on the nginx profiles |
| Prometheus | `podSelector{app: <release>-prometheus}` | scrapes `api:8081` and `worker:8081` (Design 4c) — omitting it silently empties Band B |
| kubelet | `ipBlock` over the node CIDR | `readinessProbe` and `livenessProbe` both target 8081 |
| **AWS ALB** | `ipBlock` over the VPC CIDR | `values-aws.yaml:74` sets `target-type: ip`, so the ALB registers pod IPs and traffic arrives from VPC ENIs — **no `namespaceSelector` can express this**, because there is no ingress-nginx namespace on AWS |

The CIDRs differ per environment, so they come from a values key
(`networkPolicy.clusterCidrs`, a list) rather than being hardcoded. **Every profile ships a
default, and the template fails the render when the list is empty** — `required` in Helm, not a
rule that silently denies. That failure mode is the reason: an empty list would render an
`ipBlock` matching nothing, the kubelet's readiness probe to `/health` on 8081 would be denied,
and pods would never become Ready — a silent non-readiness rather than a visible error.

The chart has **five deployment profiles** over a base, and every one of them needs a value.
Overlays are self-contained here by convention — `values-laptop.yaml`'s header records that the CI
harness passes exactly one `-f` per overlay — so each restates the key rather than relying on
inheritance:

| Profile | `networkPolicy.clusterCidrs` | Source |
|---|---|---|
| `values-aws.yaml` | `["10.0.0.0/16"]` | the VPC CIDR — `deploy/terraform/modules/cluster-aws/variables.tf:16-19` declares `vpc_cidr` with exactly this default |
| `values-azure.yaml` | `["10.1.0.0/16"]` | the VNet `address_space` at `deploy/terraform/modules/cluster-azure/main.tf:127`; the node subnet is `10.1.0.0/20` at `:136` |
| `values-gcp.yaml` | `["10.2.0.0/20"]` | the subnet `ip_cidr_range` at `deploy/terraform/modules/cluster-gcp/main.tf:16` |
| `values-local.yaml` | `["172.18.0.0/16"]` | the Docker network kind puts its nodes on |
| `values-laptop.yaml` | `["172.18.0.0/16"]` | also kind — `values-laptop.yaml:65` uses `className: "nginx"` like local |

Keep each in lockstep with its terraform module, the same way `global.ingressHost` and
`api.ingress.host` are kept in lockstep. The two kind rows are the least certain: verify per
machine with `docker network inspect kind`, because `172.18.0.0/16` is Docker's default for that
network but is assigned rather than guaranteed.

This choice is taken with one eye open. `templates/networkpolicies.yaml:154-166` already records
this chart rejecting CIDR-scoping as non-portable for the Kubernetes API server, whose address
"differs across kind/EKS/AKS/GKE". That reasoning applies to the kubelet too, and the cost accepted
here is a value operators must get right per environment. It is accepted because the alternative —
a port-scoped allow-all — is what this subsection exists to remove, and because the failure is now
loud at render time rather than silent at readiness time.

NetworkPolicy is genuinely enforced on all five profiles, so this rule is not decorative anywhere:
`cluster-aws/main.tf:475` enables the VPC CNI's native enforcement,
`deploy/terraform/modules/cluster-azure/main.tf:183` sets `network_policy = "azure"` — its own
comment notes that without it "every NetworkPolicy silently does nothing" —
`deploy/terraform/modules/cluster-gcp/main.tf:165` uses Dataplane V2, and the two kind profiles get
Calico because `deploy/kind/kind-config.yaml:3-9` disables kindnet specifically so it can be
installed in its place.

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

**Fix:** add an `add_header` block — **emitted at container start, not baked into the image.**
`nginx.conf` is copied in at build time (`Dockerfile:24`) while the Authentik origin is a
per-environment value (`http://localhost:9000/...` in `.env.development`,
`http://authentik.iverson.example.com/...` at `values-aws.yaml:142`), so a hardcoded
`connect-src` either carries an unresolved placeholder or omits the origin. Omitting it blocks
`oidc-client-ts`'s discovery fetch and token exchange, which are `connect-src` subjects — **login
cannot complete.** `docker-entrypoint.sh` already runs at startup and already reads
`OIDC_AUTHORITY`, so it renders the CSP header there with the origin interpolated, using
`envsubst` (present in the image, A43). 5d's validation regex already constrains that value, so
the same check that makes `config.js` safe makes the header safe.

Three directives are dictated by what this design introduces, which is why authoring it before the
widgets land means writing it twice:

- `connect-src 'self' <admin-api-origin> <authentik-origin>` — **`'self'` is not sufficient.**
  The API now answers on `admin-api.<ingressHost>`, a different origin from the console, so every
  widget fetch and the `/v1/traces` export are cross-origin and must be named explicitly; the
  Authentik origin is needed for OIDC discovery and the token exchange. Both origins are
  interpolated at container start from the same environment the entrypoint already reads
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

**Fix:** compose the export URL from `config.apiBaseUrl` — `${apiBaseUrl}/v1/traces` — which
Design 1's `admin-api` host serves through its `/v1/traces` Prefix path. No server change is needed: the endpoint already
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

A52-A54 were added when critical-design-review round 7 found that the transport's rewrite had been
verified on ingress-nginx and assumed on ALB. All three hold.

A55-A64 were enumerated cold against the transport replacement — a dedicated `admin-api` hostname
and JSON endpoints in place of a path prefix and gRPC-Web — and then checked: six held, four
failed. Two of the failures shaped the design rather than a detail. The acting-user mechanism does
not reach minimal-API endpoints and the evaluator grants full access without one (A58, A59), so the
two authenticated endpoints pass `HttpContext.User` explicitly. And `google.api.http` annotations
would have broken codegen for four SDK clients (A64), which is why this design uses endpoints
rather than JSON transcoding. A38, A45 and A52 are marked moot: the mechanisms they describe are no
longer part of the design.

A65-A67 close the three gaps critical-design-review round 8's span check found — each a fact the
endpoint set or the Ingress depends on that no earlier item covered. Two failed, and both were
cases where a capability existing somewhere in the codebase had been read as the operation being
reachable from where the endpoint would call it.

A68-A69 close round 9's two span-check gaps, and both failed for one reason: the design had been
enumerating the two deployment profiles under discussion rather than the five the chart actually
carries. Every per-profile value this design introduces — `clusterCidrs`, the new Ingress's class
and annotations, the CORS origin and `apiBaseUrl` — is supplied for all five.

A70 closes round 10's span-check gap. It is the same shape one level finer: A69 established that
the profiles use different TLS *mechanisms*, and the gap was what each mechanism's certificate has
to *contain*.

| # | Assumption | Disposition |
|---|---|---|
| A1 | `Iverson.Api` uses minimal-API `MapGet` for `/health` and `/probe/starrocks`, both `AllowAnonymous` | **Holds** — `Program.cs:301`/`:334` and `:342`/`:346` |
| A2 | An `admin` scope or policy exists for a new endpoint to require | **Failed** — no admin scope. `Program.cs:141-156` declares `Operator`, `SchemaAdmin`, `TenantAdmin` over a `FallbackPolicy` requiring an authenticated user (`:142-145`). Design uses `Operator` |
| A3 | Prometheus is reachable from the API at a known address | **Failed** — no Prometheus URL exists in the API's configuration; only the exporter is wired (`:71`, `:275`). New config key and named client required. `values-laptop.yaml:16-17` also disables Prometheus entirely |
| A4 | `/health`'s body reports per-store status for the four stores | **Holds, with a wrinkle** — `Program.cs:318-323` returns `postgres`, `starrocks`, `qdrant`, `kafka`; `starrocks` is the literal string `"disabled"` when the engagement store is off |
| A5 | The Qdrant client can read collection info | **Holds** — `IntelligenceCollectionManager.cs:22` (`ListCollectionsAsync`) and `:60` (`GetCollectionInfoAsync`); `Qdrant.Client` 1.18.1. Not on `IVectorSchemaManager` (`IVectorRoles.cs:23-27`), so a read interface is new |
| A6 | Qdrant collections are per-tenant, needing a scoping decision | **Holds** — `IntelligenceTenantScope.cs:11` resolves per-tenant names; `:18` mints per-collection scoped keys |
| A7 | `TenantLifecycle.ListTenants` exists and is reachable from the console | **Holds** — `tenant_lifecycle.proto:9`, mapped with `.RequireAuthorization("Operator")` at `Program.cs:444`. The console now reaches it through `GET /admin/console/tenants`, which projects `ITenantRepository.ListAsync()` under the same `Operator` policy |
| A8 | `ObjectMapping.GetSchema` returns types, fields, relations | **Holds** — `object_mapping.proto:15`; `GetSchemaResponse` carries `repeated SchemaType`, each with `fields` and `relations` |
| A9 | `Aggregate` can produce a row count for one object type | **Failed twice** — `ObjectSearchGrpcService.cs:490-494` throws `InvalidArgument` on zero aggregations, so a `COUNT` spec is mandatory; and `AggregateResponse.Total` is never assigned (`:514`), so the proto's `// total matching docs` field is always zero. Also `:501` returns an empty response on denial rather than an error |
| A10 | grpc-web is wired in the console with a token-attaching interceptor | **Failed, and now moot** — `Iverson.AdminUI/src/` has no generated proto code and no client wrapper. The design no longer uses grpc-web: the console calls JSON endpoints with `fetch`, attaching the token from `useAuth()` per Design 3's hook. See A63 |
| A11 | A grpc-web path exists for the services the page calls | **Failed, and now moot** — only `TenantLifecycle` and `TenantAdmin` call `.EnableGrpcWeb()` (`Program.cs:444-445`); `ObjectMapping` and `ObjectSearch` do not. The design no longer needs them to: those two services are projected through JSON endpoints instead, which also avoids widening the browser-reachable surface to every method on both services |
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
| A32 | Generated proto code can be committed to the repo | **Failed, and now moot** — `Iverson.AdminUI/.gitignore:3` ignores `generated/`. The design generates no proto code for the console at all; `scripts/generate_protos.sh` is left unused by this work |
| A33 | The `groups` claim reaches `auth.user?.profile` | **Holds** — `oidc-client-ts` populates `user.profile` from the ID token, not the access token. A real token response for `dev-iverson-human-oidc-client-id` with scope `openid groups tenant_id offline_access` carries an `id_token` whose claims include `groups` and `tenant_id`. Design 4d's `Sidebar.tsx:20` repair claim therefore holds |
| A34 | An `iverson-worker` scrape target exists in docker-compose | **Holds** — `docker-compose.yml:438` defines the service with `WORKLOAD_ROLE=worker` at `:454`, and `MapPrometheusScrapingEndpoint` (`Program.cs:275`) sits outside the `if (workloadRole == "api")` gate at `:438`, so the worker serves `/metrics` on 8081 |
| A35 | Adding a proto to `Common/Proto` does not break the other language clients | **Holds** — `Iverson.Client.Contracts.csproj:17` globs `../../Common/Proto/*.proto` with `GrpcServices="Both"`, so the server base class generates without a csproj edit; `Iverson.AdminUI/scripts/generate_protos.sh` and `Iverson.Clients/TypeScript/scripts/generate_protos.sh` also glob. Python (`Iverson.Clients/Python/scripts/generate_protos.sh`) and Go both list the four `object_*` protos explicitly, so neither is touched. Nothing in `Iverson.ClientConformance` enumerates the service set |
| A36 | `http_route` is a real label on the server-duration metric | **Holds** — present on every `http_server_request_duration_seconds_*` sample on the live `/metrics` endpoint, alongside `http_request_method`, `http_response_status_code` and `network_protocol_version`. The Prometheus scrape endpoint appears as `http_route="/metrics"` and gRPC calls as `http_route="/iverson.<Service>/<Method>"`, so Design 2's exclusion filter is expressible as written |
| A37 | `/admin-api` cannot match the console's own ingress rule | **Moot** — there is no `/admin-api` path; the API is on its own hostname, so no rule on the console's host can contest it. Original finding retained: | **Holds** — `charts/admin-ui/templates/ingress.yaml:21` is `/admin(/|$)(.*)`, whose `(/|$)` guard requires `/` or end-of-string after a literal `admin`; `/admin-api/...` supplies `-` at that position and `^/admin` cannot match elsewhere. No precedence contest between the two rules |
| A38 | An ingress rewrite can strip the prefix so the backend sees the real gRPC path | **Moot** — the design no longer routes the console through a path prefix on the shared host, so nothing strips anything. The console reaches the API on `admin-api.<ingressHost>` and the ingress rewrites nothing. Retained because it records why the prefix arrangement was abandoned: the mechanism works on ingress-nginx and has no equivalent on ALB |
| A39 | Nothing in the deployment calls `/probe/*` anonymously | **Holds** — `grep -rn "/probe/"` across the repo returns only the four definitions in `Program.cs:336-359` and one descriptive mention in `Iverson.Server/docs/security/tma.md:117`. No kubelet probe, compose healthcheck, test or script depends on them. Gating them behind `Operator` (4e) breaks nothing |
| A40 | Prometheus scrapes `/metrics` on 8081 without authentication | **Holds** — `charts/prometheus/templates/configmap.yaml:10-15` defines `iverson-api` → `<release>-api:8081` and `iverson-worker` → `<release>-worker:8081`, no auth stanza. This is why 4e leaves `/metrics` anonymous and Design 1's allowlist excludes it instead |
| A41 | The kubelet's readiness probe period bounds `/health`'s cache window | **Holds** — `charts/api/templates/deployment.yaml:173-180` targets `/health` on 8081 and declares **no** `periodSeconds`, inheriting Kubernetes' 10-second default. 4e's 5-second window sits under it |
| A42 | An in-process cache is available without new infrastructure | **Holds** — `Program.cs:217` already calls `AddMemoryCache()`, and `Tenancy/TenantStatusCache.cs:8` is an existing `IMemoryCache` consumer to pattern 4e after |
| A43 | `jq` is available in the runtime image, so `config.js` can be emitted as JSON | **Failed** — running `nginxinc/nginx-unprivileged:1.27-alpine` reports `jq` **absent**, `envsubst` present. The JSON-emission fix would have crash-looped the container at startup. Resolved by allowlist validation of the three values instead; see Design 5d |
| A44 | NetworkPolicy on AWS can admit ingress traffic by namespace selector | **Failed** — `values-aws.yaml:74` sets `target-type: ip`, so the ALB registers pod IPs and traffic originates from VPC ENIs, not from any namespace; `deploy/terraform/modules/cluster-aws/main.tf:475` sets `enableNetworkPolicy = "true"` so policies **are** enforced; and the operators module installs `aws-load-balancer-controller`, so no `ingress-nginx` namespace exists. Selector-based rules grant nothing on AWS. Resolved with an `ipBlock` list; see Design 4f |
| A45 | Multiple regex paths on one Ingress can each be rewritten correctly | **Moot** — the new Ingress carries three `pathType: Prefix` paths and no rewrite annotation, so the shared-annotation constraint this recorded no longer applies to anything in the design |
| A46 | Real gRPC-Web request paths match the allowlist regex | **Moot** — there is no allowlist regex and no gRPC-Web. Original finding retained: | **Holds** — every proto in `Iverson.Clients/Common/Proto/` declares `package iverson`, and the six services are `ObjectPersistenceService`, `ObjectRetrievalService`, `ObjectSearchService`, `ObjectMappingService`, `TenantLifecycleGrpcService`, `TenantAdminGrpcService`. `iverson\.[A-Za-z0-9_.]+/[A-Za-z0-9_]+` matches all of them and the new `AdminConsoleService` |
| A47 | `revokeTokensOnSignout` reaches `UserManagerSettings` through `react-oidc-context` | **Holds** — `react-oidc-context.js:139-149` destructures its own props and spreads `...userManagerSettings` into the `UserManager` constructor, so unrecognised settings pass through unchanged |
| A48 | `onSigninCallback` is a supported `AuthProvider` prop | **Holds** — declared in `react-oidc-context`'s types as `onSigninCallback?: (user: User \| undefined) => Promise<void> \| void`, and invoked at `react-oidc-context.js:209` only when supplied |
| A49 | `DocumentLoadInstrumentation` allows overwriting span attributes | **Holds** — `instrumentation-document-load/types.d.ts:14-18` declares `applyCustomAttributesOnSpan?: { documentLoad?, documentFetch?, resourceFetch? }`, covering both spans that receive `location.href` |
| A50 | The console's `/v1/traces` export works in production | **Failed** — `telemetry.ts:19` posts to a relative `/v1/traces`, which resolves against the api Ingress's `/` `Prefix` rule to port 8080, where a browser's HTTP/1.1 POST is rejected with 400. The export works in neither environment. Resolved by composing the URL from `config.apiBaseUrl`; see Design 5g |
| A52 | A path-rewriting middleware can be placed before endpoint routing | **Moot** — there is no path-rewriting middleware. The finding it recorded is still true of `Program.cs` (routing is auto-inserted at the head of the pipeline because `UseRouting()` is never called explicitly) and is retained for the one place it still bears on this design: `UseCors` must be registered before `UseAuthentication` so preflight `OPTIONS` requests are answered before the fallback policy rejects them |
| A53 | The AWS VPC CIDR is knowable at chart-authoring time | **Holds** — `deploy/terraform/modules/cluster-aws/variables.tf:16-19` declares `vpc_cidr` with default `10.0.0.0/16`, so `values-aws.yaml` can ship a matching `clusterCidrs` default. It is a variable, not a constant, so the two must be kept in lockstep like `global.ingressHost` and `api.ingress.host` |
| A54 | NetworkPolicy is actually enforced on the local profile | **Holds** — `deploy/kind/kind-config.yaml:3-9` sets `disableDefaultCNI: true` precisely because kindnet does not enforce NetworkPolicy, so `setup.sh`/`setup.ps1` can install Calico instead. 4f's rules are load-bearing locally, not decorative |
| A55 | The api subchart can see `global.ingressHost` | **Holds** — `charts/api/templates/deployment.yaml:137` already interpolates `http://authentik.{{ .Values.global.ingressHost }}/...`, so the global value is in scope for this subchart's templates |
| A56 | A second hostname is an established, resolvable pattern | **Holds** — `charts/authentik/templates/ingress.yaml:21` renders `printf "authentik.%s" .Values.global.ingressHost`, and `docs/user-management-and-security.md:231-235` documents adding `<ingress-controller-ip>  iverson.local authentik.iverson.local` to `/etc/hosts` for kind. `admin-api.<ingressHost>` follows both |
| A57 | The backing services are injectable into minimal-API endpoints | **Holds** — `ITenantRepository` is registered at `Program.cs:214`, and `Program.cs:348` already injects `IVectorSchemaManager` into the `/probe/vector` minimal-API endpoint. The pattern exists in the same file |
| A58 | The acting-user identity mechanism reaches minimal-API endpoints | **Failed** — `ActingUserInterceptor` is registered on the gRPC pipeline only (`Program.cs:87`, `AddGrpc(options => options.Interceptors.Add<...>())`), and even on that pipeline it returns early leaving the acting user null when no `x-acting-user-authorization` header is present, which the console never sends. Resolved by having the two authenticated endpoints pass `HttpContext.User` to the evaluator; see Design 1's endpoint set |
| A59 | The evaluator filters by default when no acting user is supplied | **Failed** — `RowFieldAuthorizationEvaluator.cs:14-15` returns a not-denied, unrestricted decision when `actingUser` is null, and `:32-33` does the same when the principal carries no `tenant_id` claim. So "authenticated plus internal filtering" would have been inert for the console. This is what forced the acting-user decision above, and it means an operator (who has no `tenant_id`) sees the full view either way |
| A60 | The two projected gRPC services are authenticated-only, not Operator-gated | **Holds** — `ObjectMappingGrpcService.cs:61-62` states GetSchema is discovery with no `[Authorize]`, filtering internally; `Program.cs:443` maps `ObjectSearchGrpcService` with no `RequireAuthorization`. The two JSON endpoints projecting them match that model rather than the Operator model of their three siblings |
| A61 | Nothing depends on `admin_console.proto` or `AdminConsoleService` | **Holds** — `grep -rn "admin_console\|AdminConsoleService"` across `*.cs`, `*.proto` and `*.ts` returns nothing outside `docs/`. The proto was specified but never created, so dropping it removes no dependency |
| A62 | Local development can reach the API today | **Failed** — `docker compose port iverson-api 8081` returns nothing, so the REST port is unpublished, and `.env.development` points `VITE_API_BASE_URL` at `8080`, the h2c-only port. Both must change for the console to reach the API locally |
| A63 | The four gRPC-Web npm dependencies can be removed without breaking anything | **Holds** — `Iverson.AdminUI/src/` contains no generated proto code and no gRPC client, and nothing imports `@improbable-eng/grpc-web`, `google-protobuf` or `long`. Removal is subtraction only |
| A64 | `google.api.http` annotations could be added to the shared protos instead | **Failed** — each client's protoc invocation carries exactly one include path, `-I"$PROTO_DIR"` (TypeScript, Python, Go and AdminUI generation scripts), and nothing in the repo supplies `google/api/annotations.proto`; unlike `google/protobuf/struct.proto` it is not a bundled well-known type. Annotating a shared proto would break codegen for four clients until all four scripts gained a googleapis include. This is why JSON transcoding was rejected in favour of endpoints |
| A65 | The Qdrant read operation is reachable through an injectable interface | **Failed** — `Iverson.Server/Iverson.Vector/IVectorRoles.cs:23-27` declares `IVectorSchemaManager` with exactly two members, `EnsureCollectionAsync` and `ApplyCollectionAsync`. The only `GetCollectionInfoAsync` call in the repository is on a Qdrant `client` object at `IntelligenceCollectionManager.cs:60`, alongside `ListCollectionsAsync` at `:22`. The endpoint therefore adds a read interface over that client rather than consuming an existing one. A57 covers injectability of the interface, not which operations it carries |
| A66 | The aggregate path is reachable from outside the gRPC service | **Failed** — `Aggregate`'s body is inline in `ObjectSearchGrpcService` and built from members private to it (`RequireSchema` at `:771`, `RunAggregationAsync` at `:536`), over proto types, in a method requiring a `ServerCallContext`. The reachable seam is one level down: `RunAggregationAsync` delegates to `search.AggregateAsync(...)` over domain types. This is why the design specifies two extractions rather than one |
| A67 | An Ingress's annotations are scoped to the object that declares them | **Holds, and it is the hazard** — `charts/api/templates/ingress.yaml:4-6` renders `.Values.ingress.annotations` onto the Ingress's own `metadata`, so a second Ingress reusing that value would inherit `values-aws.yaml:75`'s `backend-protocol-version: GRPC`. Object scoping is also what lets two Ingresses in one ALB group carry different backend-protocol annotations |
| A68 | The chart's deployment profiles are the two this design had been reasoning about | **Failed** — `deploy/helm/iverson/` carries six values files: `values.yaml` plus `values-aws.yaml`, `values-azure.yaml`, `values-gcp.yaml`, `values-laptop.yaml` and `values-local.yaml`, i.e. five deployment profiles over a base. `values-azure.yaml` and `values-gcp.yaml` are complete profiles with their own api, admin-ui and authentik ingress blocks, and both enforce NetworkPolicy (`cluster-azure/main.tf:183` `network_policy = "azure"`; `cluster-gcp/main.tf:165` Dataplane V2). Overlays are self-contained by convention — `values-laptop.yaml`'s header records that the CI harness passes exactly one `-f` per overlay — so a key absent from an overlay is absent, not inherited from a sibling. Any per-profile value this design introduces must be supplied for all five |
| A69 | The new Ingress's class and TLS convention are the same on every profile | **Failed** — the three cloud profiles use three different ingress classes and two different TLS mechanisms: `values-aws.yaml:71-85` `alb` with an ACM `certificate-arn` and empty `tlsSecretName`; `values-azure.yaml:70-73` `azure-application-gateway` and `values-gcp.yaml:71-74` `gce`, both terminating TLS from a Kubernetes Secret via the Ingress's `tls:` block. `values-laptop.yaml:19` disables the console entirely (`adminUi.enabled: false`), so it needs no such Ingress |
| A70 | A certificate covering `admin-api.<ingressHost>` exists on each cloud profile | **Failed as scoped** — A69 covers which TLS *mechanism* each profile uses, not what the certificate must contain, and no certificate covering the new host exists on any profile today. The two mechanisms need different work: aws adds the name to the ACM certificate behind `certificate-arn` (`values-aws.yaml:77,135,148` keep `tlsSecretName` empty on purpose), while azure and gcp need a new per-host Secret, since `values-azure.yaml:78,130,141` and `values-gcp.yaml:79,131,143` give every Ingress its own and `charts/api/templates/ingress.yaml:9-13` binds one host to one Secret |
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

**Data volume can now report authorization denial.** Previously accepted as unsolved, because
`Aggregate` returns an empty response on denial and distinguishing it would have meant changing
that contract for all five clients. A server-side endpoint holds the caller's principal and calls
`IRowFieldAuthorizationEvaluator` itself — the evaluator is DI-registered and takes a
`ClaimsPrincipal?`, so this is a direct call, not a reuse of `Aggregate`'s private
`EvaluateAuthorization` helper. Denial and genuinely-zero become distinguishable without touching
the proto contract. The same applies to `Aggregate`'s other two
awkwardnesses — its `InvalidArgument` on a zero-aggregation request and its never-assigned
`Total` field — which are now handled once server-side rather than worked around in the browser.

**The page has no cross-tenant aggregate view.** Data volume is tenant-scoped by design;
an operator wanting deployment-wide row counts is not served by this page. No such surface
exists today and inventing one is out of scope.
