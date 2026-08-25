# Critical Design Review: 2026-08-25-admin-console-landing-page-design (Round 5)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-25-admin-console-landing-page-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

The spec is byte-identical to the round-4 text: round 4's findings were deliberately not applied
pending this round. Enumeration was therefore built against the whole spec rather than a diff, and
this round pushed on two surfaces earlier rounds took on assertion — the Prometheus name mangling
and the metrics response contract — by reading the live `/metrics` endpoint instead of reasoning
about it.

### Sections

| Row | Disposition |
|---|---|
| Problem | `ok` — `router.tsx` index redirect, four `Coming soon` pages, metric inventory re-resolved. |
| Scope — in-scope | `ok` — four bullets, each resolving to a Design section. |
| Scope — out-of-scope | `ok` — `/health` write exception matches `Program.cs:310-311`; Jaeger traces still excluded. |
| Design 1 — service placement / gRPC rationale | `ok` — `/admin/*` collision re-verified against `charts/admin-ui/templates/ingress.yaml:21`; `/iverson.*` does not match the start-anchored regex. |
| Design 1 — Transport subsection | `→ §3.1` — unchanged since round 4 and still carrying round 4's §2.1/§2.2. Not re-raised; the **evidence** behind the remedy has changed, which is what §3.1 records. |
| Design 1 — `GetMetrics`, PromQL mangling rule | `→ §2.1` — **checked against the live endpoint for the first time.** |
| Design 1 — `GetMetrics`, response contract | `→ §2.2` — checked against what Design 2 renders. |
| Design 1 — `GetMetrics`, other bullets | `ok` — config key / named `HttpClient` / absent-Prometheus handling (`values-laptop.yaml:16-17`) / the two NetworkPolicy additions (`networkpolicies.yaml:7-10`, `:38-63`, `:474`, `:487-490`) / server-side Ollama filter from `EmbeddingServiceOptions.BaseUrl`. |
| Design 1 — `GetQdrantStats` | `ok` — `IntelligenceCollectionManager.cs:16,43` admin api-key header; `IVectorRoles.cs:23-27` still only the two write-ish methods. |
| Design 1 — Authorization | `ok` — `.RequireAuthorization("Operator").EnableGrpcWeb()` matches `Program.cs:444`; policies at `:141-156`. |
| Design 1 — Explicitly not included | `ok` — nothing load-bearing. |
| Design 2 — Band A table | `ok` — `/health` (`Program.cs:301`), `tenant_lifecycle.proto:9`, `object_mapping.proto:15`, `object_search.proto:13`. |
| Design 2 — Band B table | `→ §2.1` (metric names) and `→ §2.2` (sparkline). |
| Design 2 — Band C table | `ok` — names `AdminConsoleService.GetQdrantStats`, matching Design 1. |
| Design 2 — health-strip constraints | `ok` — tri-state `starrocks` at `Program.cs:318-323`; Ollama absent from the check. |
| Design 2 — data-volume constraints | `ok` — `:490-494` throw, `:514` never assigning `Total`, `:501` empty-on-denial. |
| Design 2 — RPC-health constraints | `ok` on the HTTP-not-gRPC caveat and on the `http_route` exclusion mechanism — **`http_route` confirmed as a real label on the live metric**, and the scrape endpoint's route value confirmed as `/metrics`. Metric **name** `→ §2.1`. |
| Design 2 — embedding-latency constraint | `ok` on the conflation caveat (`ServiceCollectionExtensions.cs:12,29`). Metric **name** `→ §2.1`. |
| Design 3 — cadence table | `ok` — 60s health strip matches the write-bearing rationale beneath it. |
| Design 3 — failure and degradation | `ok` — per-card isolation, stale-with-timestamp, backoff, hidden-tab pause, 401 routing. |
| Design 3 — implementation shape | `ok` — hook is transport-agnostic; `auth.user?.access_token` exists. |
| Design 3 — Testing | `ok` — vitest 3.2, three existing test files. |
| Design 4a | `ok` — `Program.cs:438-445` still lacks `.EnableGrpcWeb()` on the two named services; `UseGrpcWeb()` at `:284`. |
| Design 4b | `ok` — `.github/workflows/` still only `codeql.yml`, `deploy-validate.yml`. |
| Design 4c | `ok` — VIP/HPA unchanged; `docker-compose.yml:438` worker present. **Corroborated this round:** the API's live `/metrics` carries no `reconciliation_*`, `dlq_*`, `document_rerender_*` or `consumer_*` series at all, which is exactly what the worker-only registration predicts. |
| Design 4d | `ok` — blueprint glob, compose mount, `Sidebar.tsx:20,25`, `AppLayout.tsx:9`. |
| Verified assumptions (A1-A35) | See §1. |
| Known issues | `ok` — three entries, none re-raised. |

### Rules and operands

| Row | Disposition |
|---|---|
| Name-mangling rule: OTel instrument name → Prometheus series name, both directions | `→ §2.1` — **over-inclusion ok, under-inclusion FAILS.** The rule as stated produces names that do not exist for the two instruments that declare a unit. Checked against the live endpoint, not against the rule's own description. |
| Name-mangling rule applied to the gauges and counters specifically | `ok` — read the instrument declarations: `ReconciliationTelemetry.cs:25-41` and `Iverson.Events/Telemetry.cs:14,17` pass only `description:`, no `unit:`. So those five mangle exactly as the spec's rule says. The rule is right for five of seven and wrong for two — which is why it reads as correct. |
| Label rule: `http_route` exclusion for probe and scrape traffic | `ok` — `http_route` is a real label on `http_server_request_duration_seconds_count`; the scrape endpoint appears as `http_route="/metrics"`. The exclusion is expressible as written. |
| Response-shape rule: what `GetMetrics` returns vs what each Band B widget renders | `→ §2.2` — one widget needs a series; the contract describes scalars. |
| Identity rule: proto package namespace | `ok` — all six protos declare `package iverson;`; live metrics confirm real request routes of the form `/iverson.TenantLifecycleGrpcService/ListTenants`. |
| Eligibility predicate: which processes emit Band B metrics | `ok` — the six hosted services are all inside `if (workloadRole == "worker")` (`Program.cs:254-264`), and the API's live `/metrics` confirms none of their series is exported there. |
| Health-strip state rule / data-volume count rule | `ok` — unchanged; both operands re-checked against `Program.cs:318-323` and `ObjectSearchGrpcService.cs:490-514`. |

### Data-flow arrows

| Row | Disposition |
|---|---|
| Prometheus series → `GetMetrics` PromQL → response field | `→ §2.1` — **the consuming operation's parameter (the series name) does not exist in the artifact it reads.** Verified by dumping the real name set rather than reasoning from the instrument name. |
| `GetMetrics` response → fan-out backlog widget render | `→ §2.2` — the render needs a series; the response carries a scalar. |
| `GetMetrics` response → DLQ/retry, RPC-health, embedding-latency widget renders | `ok` — each of those renders a scalar (a rate, a percentage, a p95), which the contract does supply. |
| browser → gRPC paths → api | `→ §3.1` — unchanged from round 4. |
| api pod → Prometheus `:9090` | `ok` — both policy additions stated in Design 1. |
| ID token → `Sidebar`'s `profile.groups` | `ok` — A33; verified by real token dump in round 2. |
| new proto → .NET server build | `ok` — A35; `Iverson.Client.Contracts.csproj:17` globs with `GrpcServices="Both"`. |

### Candidates generated and dropped

| Candidate | Why dropped |
|---|---|
| Design 1 says "the page needs seven numbers" but its own list enumerates nine | Arithmetic slip in a justifying clause; an implementer follows the list, not the count. Recorded as corroborating evidence inside §2.2 rather than as its own finding. |
| `otel_scope_name` appears on every series and the spec's PromQL never mentions it | Not a filter the design needs; its absence changes no result. |
| The live metric shows `network_protocol_version="1.1"` for scrapes and `"2"` for gRPC, which distinguishes the two transports | True and interesting, but a metric label cannot route traffic, so it does not bear on §3.1's remedy. |
| Worker `/metrics` could not be reached from inside the container (no curl/wget) to confirm the gauges' mangled names directly | The instrument declarations were read instead and carry no `unit:`, which settles the naming question by the same rule the live endpoint confirmed. Not a gap. |
| `/health/live` is excluded by the spec's PromQL but the kubelet may not use it | Speculation about probe configuration the spec does not own. |

## 1. Verified-assumptions cross-check

A1-A35 reconfirmed under a fresh read; citations resolve to the lines they name. Nothing
re-litigated.

**A17 warrants a note rather than a failure.** It states that OTel-to-Prometheus name mangling
applies and that PromQL must use the mangled names — which is true, and remains true. The
*specific* mangling rule the design writes out in Design 1 is the incomplete artifact; the
assumption itself is correctly scoped and holds. The gap between A17 as written and the rule
Design 1 derives from it is precisely the kind of span the check below exists to catch.

**Span check — dependencies with no covering assumption:**

1. *"The mangled names Design 1's rule produces are the names Prometheus actually holds."* A17
   covers that mangling happens; nothing covers whether the stated rule reproduces it.
   **Verified in-round and FAILS** — see §2.1.
2. *"`GetMetrics`'s response shape supplies every value the Band B widgets render."* No assumption
   covers the response contract against the render requirements.
   **Verified in-round and FAILS** — see §2.2.
3. *"`http_route` is a real label on the server-duration metric."* Load-bearing for the round-2
   exclusion fix; no assumption covers it. **Verified in-round and holds** — present on every
   `http_server_request_duration_seconds_*` sample, with the scrape endpoint appearing as
   `http_route="/metrics"`. Closes clean.

## 2. Literal-wrongness findings

### 2.1 — The stated name-mangling rule omits unit suffixes, so two widgets' PromQL names nothing

**Description.** Design 1 tells the implementer how to construct PromQL names: "dots to
underscores, `_total` on counters, `_bucket`/`_sum`/`_count` on histograms". The OpenTelemetry
Prometheus exporter also appends the instrument's **unit** to the series name. Both metrics the
spec names for Band B — `http.server.request.duration` and `http.client.request.duration` — are
declared in seconds by the ASP.NET Core and HttpClient instrumentation, so their real series carry
a `_seconds` segment. PromQL written from the spec's rule references
`http_server_request_duration_bucket`, which does not exist; the RPC-health and embedding-latency
widgets return empty.

**Evidence.** Read from the running API's `/metrics` endpoint rather than derived:

- `http_server_request_duration_seconds_bucket`, `_count`, `_sum` — the real names.
- `http_client_request_duration_seconds_bucket`, `_count`, `_sum` — likewise.
- The rule is correct for the other five metrics, which is why it reads as right:
  `ReconciliationTelemetry.cs:25-41` and `Iverson.Events/Telemetry.cs:14,17` declare their
  instruments with `description:` only and no `unit:`, so `reconciliation.queue_depth` →
  `reconciliation_queue_depth` and `consumer.retries` → `consumer_retries_total` exactly as
  stated. The rule fails only on the two instruments that carry a unit — and those are the two the
  spec did not name concretely.

**Proposed fix.** Extend the sentence to include the unit segment, and write the two histogram
names out literally so no derivation is needed:
`http_server_request_duration_seconds_{bucket,sum,count}` and
`http_client_request_duration_seconds_{bucket,sum,count}`. Leaving the general rule in place is
fine; naming the two concretely removes the step where it is applied wrongly.

### 2.2 — The `GetMetrics` response contract cannot feed the fan-out backlog widget's sparkline

**Description.** Design 2's Band B table specifies the fan-out backlog widget as "Three gauges,
**current value plus sparkline**". Design 1 specifies `GetMetrics` as returning "a fixed, named
result set" of scalars — a sparkline needs a time series. Implementing the contract as described
makes the widget as described impossible; one of the two has to give.

**Evidence.**

- Design 2, Band B table: `| Fan-out backlog | reconciliation.queue_depth, dlq.unreplayed_count,
  document_rerender.queue_depth | Three gauges, current value plus sparkline |`.
- Design 1, `GetMetrics`: "returns a fixed, named result set… the page needs seven numbers", then
  "The response carries: the three reconciliation gauges, the two consumer counters, RPC request
  rate / error percentage / p95, and Ollama client p95."
- The "seven numbers" framing is itself evidence the contract was conceived as scalars — and it
  does not match its own list, which enumerates nine items. No element of that list is a series.
- Every other Band B widget renders a scalar (a rate, a percentage, a p95), so this is the only
  render the contract cannot satisfy.

**Proposed fix.** Say which side moves. Either the fan-out backlog widget drops the sparkline and
shows three current values — the smaller change, and consistent with every other Band B widget —
or `GetMetrics` returns a range for the three gauges, which means the contract carries both
scalars and series and the "seven numbers" justification for refusing pass-through PromQL needs
restating on its actual grounds (a fixed named surface, not a small one).

## 3. Forced decisions

### 3.1 — The transport axis, with a corrected and enlarged option set

**The choice.** Round 4's §3.1 asked how browser grpc-web is separated from native gRPC, since
both occupy `/iverson.<Service>/<Method>`. That decision is still open and the spec still carries
the arrangement round 4 showed to be wrong. This is not a re-raise: the **evidence behind the
options has changed materially** since round 4, in two ways that alter what can be picked.

**Why it's forced.** Beyond round 4's three constraints, which stand:

- **Round 4's option (a) is empirically dead on cleartext.** Kestrel's `Http1AndHttp2` on a
  cleartext endpoint does not serve HTTP/2 prior-knowledge: a spike with two endpoints on one
  process returned 200 for HTTP/1.1 and `PROTOCOL_ERROR` ("Remote peer returned unexpected data
  while we expected SETTINGS frame") for h2c, while an `Http2`-only control endpoint returned 200
  for h2c and 400 for HTTP/1.1. `Http1AndHttp2` serves both **only under TLS**, where ALPN
  negotiates. Adopting (a) as written would break native gRPC for every SDK client on every
  cleartext profile — relocating round 4's §2.1 rather than fixing it.
- **The option set round 4 offered was incomplete.** At least one axis was never enumerated: a
  **rewritten path prefix**, where the console calls `/grpcweb/iverson.<Service>/<Method>`, the
  ingress strips the prefix and routes to 8081, and bare `/iverson.*` stays on 8080 for SDK
  clients. This is the `rewrite-target` pattern `charts/admin-ui/templates/ingress.yaml:21`
  already uses in this chart, and grpc-web clients accept a base path, so the console side is a
  configuration value rather than new machinery. It is listed here because a forced decision made
  over an option set known to be partial is not actually decided.

**The options.**

- **(a′) Rewritten path prefix.** Console → `/grpcweb/iverson.<Service>/<Method>`; ingress strips
  and routes to 8081; `/iverson.*` untouched on 8080. Keeps one host, needs no TLS change, needs
  no Kestrel change, and separates the two consumers on an axis that actually distinguishes them.
  Cost: the rewrite rule is `ImplementationSpecific` and therefore ingress-controller-specific, and
  the console's grpc-web client must be configured with the base path.
- **(b) Separate by host.** Console grpc-web on its own hostname routed to 8081; existing host
  entirely on 8080. Unaffected by the spike result. Cost: a second DNS name and certificate per
  environment; the console's same-origin assumption needs re-checking, since the console is served
  from the existing host.
- **(c) Require TLS, console on HTTP/2.** Drop the 8081 route; the console speaks grpc-web over
  HTTP/2 to 8080 via ALPN, which the spike confirms works under TLS. Cost: the cleartext profiles
  (`values.yaml:22` and `values-local.yaml`, both `http://iverson.local`) must gain TLS, so local
  and default deployments change.

Not picking between these: (a′) adds controller-specific configuration, (b) adds per-environment
DNS and certificate surface, and (c) changes the deployment requirements of two profiles. Round
4's (a) is excluded on evidence rather than preference.

## 4. Previously addressed

- **Round 3 §2.1 / §2.2** — the Transport bullet names the path form explicitly and places it on a
  separate Ingress with the annotation-scoping rationale. Both fixes remain as applied; whether
  the arrangement survives depends on §3.1.
- **Round 2 §2.1 / §2.2** — the read-only scope exception and the removed CI clause both still
  read correctly against `Program.cs:310-311` and `.github/workflows/`.
- **Round 1 §2.1 / §2.2** — the NetworkPolicy requirement and the `http_route` exclusion are both
  in the spec; this round confirmed `http_route` is a real label and that the scrape endpoint
  appears under it as `/metrics`, so the exclusion is expressible as written.
- **Round 4 §2.1 / §2.2** — still open, deliberately unapplied, and not re-raised here. §3.1
  carries the decision they both depend on.

## 5. Recommendation

🛑 **Surface forced decisions to user** — §3 is non-empty.

The two §2 findings are the first in three rounds to come from outside the Transport subsection,
and both were found the same way: by reading what the system actually emits instead of what the
spec says it emits. §2.1 in particular had survived four rounds because the rule it states is
correct for five of the seven metrics involved — the two it gets wrong are the two the spec never
wrote out concretely.

§3.1 is the same decision round 4 surfaced, re-opened because one of its three options is now
disproved and a fourth exists. It remains the gate: Design 1's Transport subsection is currently
known-wrong, and nothing downstream should be planned against it.

Five candidates were generated and dropped with reasons recorded in §0.
