# Critical Design Review: 2026-08-25-admin-console-landing-page-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-25-admin-console-landing-page-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

### Sections

| Row | Disposition |
|---|---|
| Problem | `ok` — re-read the three claims. `src/router.tsx` index route is the `<Navigate to="/performance" replace />` described; all four pages return `Coming soon`; the metric/probe inventory matches `Program.cs:318-323`, `ReconciliationTelemetry.cs:26,32,38`, `Telemetry.cs:14,17`. |
| Scope | `ok` — in/out lists are consistent with Designs 1-4. The added identity bullet resolves to Design 4d. |
| Design 1 — minimal-API pattern choice | `→ §2.3` — the pattern is real, but the ports it lands on are not reachable by a browser. |
| Design 1 — `/admin/metrics` | `→ §2.1` (network path) and `→ §2.3` (browser path). |
| Design 1 — `/admin/stores/qdrant` | `ok` on feasibility — `IntelligenceCollectionManager.cs:16,43` sets `RequestHeaders.Use("api-key", apiKey)` with the **admin** key from `ServiceCollectionExtensions.cs:45`, so `ListCollectionsAsync`/`GetCollectionInfoAsync` can enumerate cross-tenant as the spec requires. `→ §2.3` on reachability only. |
| Design 1 — Authorization | `ok` — `Program.cs:141-156` declares exactly the three named policies over a `FallbackPolicy` requiring an authenticated user; `Operator` is what `/reconcile` (`:373`), `/admin/dlq` (`:380`), `/admin/dlq/replay` (`:392`) use. Peer choice is correct. |
| Design 1 — Explicitly not included | `ok` — no caching / no metric-name config / no generic query endpoint. Nothing here is load-bearing. |
| Design 2 — Band A table | `ok` — all four sources exist: `/health` (`Program.cs:301`), `tenant_lifecycle.proto:9`, `object_mapping.proto:15`, `object_search.proto:13`. |
| Design 2 — Band B table | `→ §2.2` on the RPC-health row; other three rows `ok` (instrument names re-read and exact). |
| Design 2 — Band C table | `ok` — matches Design 1's second endpoint. |
| Design 2 — health-strip constraints | `ok` — re-read `Program.cs:318-323`; `starrocks` really is `engagementEnabled ? (object)(bool) : "disabled"`, so the tri-state claim holds. Ollama genuinely absent from the check. |
| Design 2 — data-volume constraints | `ok` — all three corrections re-verified against `ObjectSearchGrpcService.cs`: the zero-aggregations throw (`:490-494`), `Total` never assigned (`:514` constructs with `TraceId` only), denial returning an empty response (`:501`). |
| Design 2 — RPC-health constraint | `→ §2.2` — the spec's stated caveat (HTTP-not-gRPC semantics) is correct but is **not** the defect; the metric is unfiltered. |
| Design 2 — embedding-latency constraint | `ok` — `ServiceCollectionExtensions.cs:12,29` register two named clients; the conflation caveat is accurate. |
| Design 3 — cadence table | `ok` — the no-poll decision for data volume is consistent with the N-calls cost stated in Design 2. |
| Design 3 — failure and degradation | `ok` — per-card isolation, stale-with-timestamp, backoff, hidden-tab pause, 401 routing. Checked the 401 claim across both transports: `ObjectMapping`/`ObjectSearch` carry no explicit policy so the `FallbackPolicy` applies, and an unauthenticated gRPC-web call surfaces the HTTP status to `@improbable-eng`'s transport, so one 401 classifier can cover both. |
| Design 3 — implementation shape | `ok` — the `useAuth()` token-as-argument constraint matches `AuthProvider.tsx`; the TanStack Query rejection is reasoned, not asserted. |
| Design 3 — Testing | `ok` — `vitest` 3.2 + `@testing-library/react` present; three existing test files. |
| Design 4a — grpc-web enablement | `ok` — re-read `Program.cs:438-445`: `ObjectMapping` and `ObjectSearch` are mapped without `.EnableGrpcWeb()`; `app.UseGrpcWeb()` is present at `:284`. The prescription is correct and sufficient for the server side. |
| Design 4b — console gRPC client foundation | `→ §3.1` — the prescription "generated client code committed to the repo" collides with an existing ignore rule. |
| Design 4c — scrape targets | `ok` — the multi-replica VIP analysis re-checked (`charts/api/templates/service.yaml` is a ClusterIP; `charts/api/values.yaml:1,9-12` give 2 replicas under a 2-5 HPA). The DNS-discovery choice survives the NetworkPolicy: `prometheus-egress` (`networkpolicies.yaml:487-490`) selects api/worker by **podSelector**, so per-pod-IP targets remain allowed. The `automountServiceAccountToken: false` rationale re-verified at `charts/prometheus/templates/deployment.yaml:5,30`. |
| Design 4c — local worker target | `ok` — `prometheus.local.yml` has one job (`iverson-api:8081`); the Helm configmap has two. The asymmetry is as described. |
| Design 4d-1 — scope change | `ok` — verified empirically in-session: the console's scope string yields an empty `scope` claim and no `groups`/`tenant_id`; `openid groups tenant_id offline_access` against the same client yields both. |
| Design 4d-2 — operators group blueprint | `ok` — `blueprints-configmap.yaml:6` globs `blueprints/*.yaml`, and `docker-compose.yml:294,328` bind-mounts the whole `blueprints` directory, so one top-level file does reach both paths. Group name `operators` is fixed by two consumers (`OperatorAuthorizationPolicy.cs:11`, `Sidebar.tsx:20`) as the spec states. |
| Design 4d-3 — membership | `ok` — the compose blueprint's `groups:` attribute pattern exists on `iverson-loadtest-bypass-user`; the production-membership-is-operational position is stated with a reason. |
| Verified assumptions | See §1 — one failure. |
| Known issues | `ok` — the three entries are accurate and none is re-raised here. |

### Rules and operands

| Row | Disposition |
|---|---|
| Health-strip state rule, both directions | `ok` — over-inclusion: no fourth state exists in the `checks` object. Under-inclusion: the tri-state `starrocks` operand is the one that differs structurally from its three siblings, and the spec tests exactly that one. |
| Data-volume count rule | `ok` — over-inclusion (denied type counted as zero) is caught by the spec; under-inclusion (a type omitted from the loop) is bounded by `GetSchema`'s own type list, which is the same source the widget enumerates. |
| Metric-selection rule for RPC health, **both directions** | `→ §2.2` — over-inclusion **FAILS**: `http.server.request.duration` is emitted with no path filter on the metrics provider, so probe and scrape traffic are counted as RPC. |
| Metric-selection rule for embedding latency, both directions | `ok` — over-inclusion (enrichment conflated) is stated by the spec; under-inclusion is not possible, since `server.address` is present on every HttpClient metric. |
| Backoff / stale / hidden-tab rules | `ok` — no identity or eligibility semantics; these set values, not mechanics. |
| Identity rule: `operators` group name | `ok` — checked over-merge: the name is consumed identically by the server policy and the Sidebar; no second group shares it, and the live directory has no near-name collision. |
| Eligibility predicate: which processes emit Band B metrics | `ok` — enumerated every producer. The six hosted services are all inside `if (workloadRole == "worker")` (`Program.cs:254-264`); `Program.cs:68` registers both meters on the API's provider but the API role instantiates none of them. The spec's local-scrape fix follows from this, and no additional producer exists. |

### Data-flow arrows

| Row | Disposition |
|---|---|
| browser → `/admin/metrics` (crosses browser↔cluster boundary) | `→ §2.3` — the operation's transport requirement is not satisfied by either configured base. |
| api pod → Prometheus `:9090` (crosses pod boundary under default-deny) | `→ §2.1` — **FAILS**. No egress rule and no ingress policy. |
| browser → `/admin/stores/qdrant` → `QdrantClient` | `ok` on the server leg (admin api-key header, see above); `→ §2.3` on the browser leg. |
| browser → grpc-web → `ObjectSearch.Aggregate` / `ObjectMapping.GetSchema` | `→ §2.3` — same browser leg. Server leg `ok` once 4a lands. |
| browser → `/health` | `→ §2.3` on the browser leg. Note this arrow also **feeds** §2.2: the design polls `/health` every 10s per open tab, into an unfiltered metric the same page displays. |
| Prometheus → api/worker `:8081` per-pod (4c change) | `ok` — podSelector-based egress rule survives the move from VIP to pod IPs. |
| `operators-group.yaml` → Authentik blueprint loader (crosses file→ConfigMap→container boundary) | `ok` — Helm glob and compose bind-mount both reach a top-level file; recursion is proven by `compose-only/service-clients.yaml` applying today. |
| `usePolledResource` → OIDC token | `ok` — `auth.user?.access_token` exists on the `react-oidc-context` user; the parameter the operation needs is present in the artifact the hook reads. |

### Candidates generated and dropped

| Candidate | Why dropped |
|---|---|
| The Qdrant read interface must repeat `RequestHeaders.Use("api-key", …)` or the call is unauthenticated | True, but it is how to implement the endpoint, not whether the design works. `critical-implementation-review`'s surface. |
| `dns_sd_configs` against a headless Service may miss not-ready pods without `publishNotReadyAddresses` | Speculation about a transient state; scraping a not-ready pod is not the asked-for behavior, and the design's outcome (one target per pod) holds. |
| Nine widgets on one page will be slow / need virtualization | Scalability without a literal-wrongness justification. Drop. |
| `values-laptop.yaml` disables `adminUi` as well as `prometheus`, so the page never renders there anyway | Correct and harmless — it strengthens the spec's own handling rather than contradicting it. Not a defect. |
| The compose `iverson-oidc-default` provider lacks the `offline_access` property mapping that the Helm template binds | Real drift, but refresh works empirically today and the spec's outcome does not depend on it. Not literal-wrongness. |

## 1. Verified-assumptions cross-check

A1-A21 and A23-A29 reconfirmed under a fresh read; citations resolve to the lines they name.
A27-A29 were verified empirically in-session and are not re-litigated here.

**A22 — FAILS.**

The spec records: *"`config.ts`'s `apiBaseUrl` is the right base for the new endpoints — **Holds**."*
Three independent reads contradict it:

1. **It has no consumer.** `grep -rn apiBaseUrl Iverson.AdminUI/src/` returns exactly one hit —
   its own declaration at `src/config.ts:16`. Nothing reads it.
2. **The dev value points at a port browsers cannot use.** `.env.development:3` sets
   `VITE_API_BASE_URL=http://localhost:8080`, and `Iverson.Api/appsettings.json:12-13` binds
   `http://*:8080` to `"Protocols": "Http2"` — cleartext h2c. Browsers do not speak h2c; they
   send HTTP/1.1, which Kestrel rejects. Confirmed empirically: an HTTP/1.1 request to
   `http://localhost:8080/health` returns **400**, while the same request to `:8081` returns **200**.
3. **The console's one real API call deliberately does not use it.** `src/telemetry.ts:19` sets
   `OTLP_TRACES_URL = "/v1/traces"` — a relative, same-origin path, with the comment at `:43`
   naming same-origin as the reason.

The assumption's table row should read as failed, and Design 1's transport story depends on it —
see §2.3.

**Span check — dependencies with no covering assumption:**

1. *"The API pod can open a connection to Prometheus."* No listed assumption covers cluster
   network policy. **Verified in-round and FAILS** — see §2.1.
2. *"`http.server.request.duration` measures RPC traffic."* A14 verifies the metric is *emitted*;
   nothing covers what it *contains*. **Verified in-round and FAILS** — see §2.2.
3. *"Generated proto code can be committed to the repo."* No assumption covers it.
   **Verified in-round and FAILS** — `Iverson.AdminUI/.gitignore:3` ignores `generated/`.
   Surfaced as §3.1 because the remedy genuinely forks.

## 2. Literal-wrongness findings

### 2.1 — The API cannot reach Prometheus in any Kubernetes deployment

**Description.** `GET /admin/metrics` queries Prometheus server-side. Under the chart's
default-deny NetworkPolicy, the API has no egress rule permitting it, and Prometheus has no
ingress policy at all. Every Band B widget — four of the nine — returns an error in every
k8s profile. The spec never mentions NetworkPolicy.

**Evidence.**

- `templates/networkpolicies.yaml:7-10` — `{{ .Release.Name }}-default-deny`, `podSelector: {}`,
  `policyTypes: ["Ingress", "Egress"]`. Both directions are denied namespace-wide by default.
- `:38-63` — `api-egress` enumerates postgres (5432), kafka (9093), starrocks-fe (9030),
  qdrant (6334), ollama (11434), jaeger (4317/4318), authentik-server (9000), and DNS.
  **There is no rule for Prometheus.**
- `grep -n prometheus templates/networkpolicies.yaml` returns four hits: `:102` (worker-ingress
  *from* prometheus), and `:474,479,482` — the `prometheus-egress` policy. **No
  `prometheus-ingress` policy exists**, so even with an api-egress rule the connection would be
  refused at the destination.

The reverse direction is already allowed (`:487-490`, prometheus → api/worker on 8081), which is
why scraping works today and makes the missing direction easy to overlook.

**Proposed fix.** Two additions to `templates/networkpolicies.yaml`, both guarded by the existing
`{{- if .Values.prometheus.enabled }}` used at `:474`:

- an `api-egress` rule `to: [{ podSelector: { matchLabels: { app: {{ .Release.Name }}-prometheus } } }]`
  on TCP 9090;
- a `prometheus-ingress` policy selecting `app: {{ .Release.Name }}-prometheus`, allowing from
  `app: {{ .Release.Name }}-api` on TCP 9090.

### 2.2 — The RPC-health widget measures health probes, not RPC

**Description.** The widget reads `http.server.request.duration` and presents request rate,
error percentage, and p95 as the API's transport health. That metric is emitted **unfiltered**,
so kubelet liveness/readiness probes, load-balancer health checks, and Prometheus's own scrape
of `/metrics` are all counted as requests. The displayed rate is dominated by probe traffic and
the p95 is pulled toward the trivial `/health` path — the widget does not show the quantity it
claims to show.

The design makes this worse by its own hand: the health-strip widget polls `/health` on a **10s**
timer per open browser tab (Design 3's cadence table), feeding the very metric displayed in the
card beside it.

**Evidence.** The path filter exists, but on the wrong provider.

- `Program.cs:56-59` — on `.WithTracing(...)`:
  `o.Filter = ctx => ctx.Request.Path != "/health" && ctx.Request.Path != "/health/live";`
  with the comment `// skip noisy health checks`.
- `Program.cs:66-71` — on `.WithMetrics(...)`: a bare `.AddAspNetCoreInstrumentation()` with no
  `Filter`, followed by `.AddPrometheusExporter()`.

So the noise the tracing provider deliberately excludes is exactly what the metrics provider
keeps, and the metrics provider is the one this widget reads. `Program.cs:275`
(`MapPrometheusScrapingEndpoint()`) adds a further scrape-interval request on the same metric.

**Proposed fix.** Filter in the widget's PromQL rather than changing the shared metrics provider,
since other consumers may rely on total request volume: constrain every RPC-health query by
`http_route`, excluding `/health`, `/health/live`, and the scraping endpoint's route. If the
project would rather fix it at the source, mirroring the tracing `Filter` onto the metrics
`AddAspNetCoreInstrumentation` is the one-line alternative — but that is a change in what the
deployment exports, not just what this page displays, so the spec should say which it wants.

### 2.3 — Neither new REST endpoint is reachable from the browser as specified

**Description.** Design 1 places both new endpoints on the minimal-API surface "following the
pattern already established by `MapGet("/health")`". That pattern's endpoints are reachable by
the kubelet and by operators with a port-forward — not by a browser. Combined with A22's failure,
the design specifies two endpoints the console cannot call, in both dev and the default
Kubernetes profile.

**Evidence.**

- **Ports.** `Iverson.Api/appsettings.json:12-13` binds `http://*:8080` to `Protocols: Http2`
  (cleartext h2c) and `:16-17` binds `http://*:8081` to `Protocols: Http1`. A browser will not
  speak h2c, so only 8081 can serve it — confirmed empirically (`:8080/health` → 400,
  `:8081/health` → 200).
- **Ingress.** `charts/api/templates/ingress.yaml:17-24` publishes exactly one path, `/` →
  service port **8080**. There is no Ingress path to 8081 in any profile. On AWS the ingress is
  additionally annotated `alb.ingress.kubernetes.io/backend-protocol-version: GRPC`
  (`values-aws.yaml:75`), i.e. the published path is explicitly a gRPC path.
- **Dev.** `.env.development:3` points at 8080; `vite.config.ts` declares **no `server.proxy`**,
  so a relative path from `localhost:5173` hits the Vite dev server, and an absolute one to
  `localhost:8080` is both the wrong protocol and cross-origin — and `Program.cs` contains **no
  CORS configuration at all** (`grep -n "Cors\|UseCors"` returns nothing).
- **The existing precedent is already affected.** `src/telemetry.ts:19` posts traces to the
  relative `/v1/traces`, which resolves against the Vite dev server in development.

**Proposed fix.** Adopt the same-origin relative-path pattern `telemetry.ts` already uses — it
is correct in Kubernetes, where `charts/admin-ui/templates/ingress.yaml:22` serves the console at
`/admin(/|$)(.*)` on the same host the API's `/` path is published on — and add the missing dev
leg: a `server.proxy` entry in `vite.config.ts` forwarding `/admin/metrics`, `/admin/stores`,
`/health`, and the grpc-web paths to `http://localhost:8081`. That keeps one transport story,
needs no CORS, and makes `config.apiBaseUrl` either used deliberately or removed rather than left
declared-and-unread.

The spec should also state which port the browser-facing paths are served on, since 8080 and
8081 are not interchangeable and the design currently names neither.

## 3. Forced decisions

### 3.1 — How the console obtains generated protobuf client code

**The choice.** Design 4b prescribes "generated client code committed to the repo".
`Iverson.AdminUI/.gitignore:3` contains `generated/`, which is where
`scripts/generate_protos.sh` writes (`--ts_proto_out=generated`). The prescription cannot be
followed without deciding what to do about that rule, and the spec does not name the conflict.

**Why it's forced.** The obvious alternative — generate at build time instead of committing —
is not free here: `scripts/generate_protos.sh` invokes `~/sdk/protoc/bin/protoc`, an absolute
path under the developer's home directory. Nothing in `package.json`'s scripts runs `generate`
as part of `build` or `test`, and no CI step provisions protoc. So each option requires work the
spec has not scoped, and the choice determines whether CI needs a new toolchain dependency.

**The options.**

- **(a) Commit the generated code.** Remove `generated/` from `Iverson.AdminUI/.gitignore` and
  check the output in. Matches the spec's current wording. Cost: generated code in review diffs,
  and a regeneration step that can silently drift from the `.proto` files.
- **(b) Generate at build time.** Make `generate` a prerequisite of `build` and `test`, replace
  the hardcoded protoc path with a resolvable one (a `devDependency` such as a protoc binary
  package, or a documented `PROTOC` env var), and add the step to CI. Cost: a new build
  dependency; benefit: the client cannot drift from the contract.
- **(c) Commit now, revisit later.** Take (a) to unblock this page, and record (b) as follow-up.

Not picking between these: (b) touches CI and the toolchain, which is outside what this design
was scoped to decide.

## 5. Recommendation

🛑 **Surface forced decisions to user** — §3 is non-empty.

§2 carries three findings, and 2.1 and 2.3 are both blocking: as written, four of the nine
widgets cannot reach their data source in Kubernetes, and both new endpoints cannot be called
from a browser in either dev or the default k8s profile. 2.2 is narrower — the widget renders,
but the number it renders is not the quantity the spec names.

The spec's own verification work was sound as far as it went; all three §2 findings and the A22
failure live in the gap between "the surface exists and behaves as documented" (which the spec
checked thoroughly) and "the caller can actually reach it" (which no assumption covered). The
span check is what surfaced all four.
