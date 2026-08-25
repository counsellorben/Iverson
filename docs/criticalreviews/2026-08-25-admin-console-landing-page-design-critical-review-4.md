# Critical Design Review: 2026-08-25-admin-console-landing-page-design (Round 4)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-25-admin-console-landing-page-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

Enumeration built before consulting rounds 1-3. The rewritten Transport bullet is the round's
main surface; the rest of the spec is re-swept at the same depth.

### Sections

| Row | Disposition |
|---|---|
| Problem | `ok` — `router.tsx` index redirect, four `Coming soon` pages, metric inventory all still resolve. |
| Scope — in-scope | `ok` — four bullets, each resolving to a Design section. |
| Scope — out-of-scope | `ok` — the `/health` write exception matches `Program.cs:310-311`; Jaeger traces still excluded, which disposes the `/v1/traces` candidate below. |
| Design 1 — service placement | `ok` — `admin_console.proto` alongside the six existing protos; `TenantLifecycleGrpcService`/`TenantAdminGrpcService` precedent an admin service living there. |
| Design 1 — gRPC-not-REST rationale | `ok` — re-verified both directions: `/admin/metrics` matches `charts/admin-ui/templates/ingress.yaml:21`; `/iverson.*` does not (start-anchored regex). |
| Design 1 — Transport, opening paragraph | `ok` — `appsettings.json:12-13` (8080 Http2) and `:16-17` (8081 Http1) unchanged; the cleartext-profile claim re-checked against `values.yaml:22` and `values-local.yaml`. |
| Design 1 — Transport, new-Ingress bullet | `→ §2.1`, `→ §2.2`, `→ §3.1` — three distinct problems in the rewritten bullet. |
| Design 1 — Transport, per-service path list | `ok` on **spelling** — checked all five names against the protos: `ObjectMappingService`, `ObjectPersistenceService`, `ObjectRetrievalService`, `TenantAdminGrpcService`, `TenantLifecycleGrpcService`, `ObjectSearchService`. The four the console needs are present and correctly spelled; `AdminConsoleService` is the new one. Routing consequence `→ §2.1`. |
| Design 1 — Transport, pathType explanation | `ok` — the element-wise rule and the `/iverson.AdminConsoleService` whole-element claim both check out; a Prefix path equal to a full service name does match that service's methods. |
| Design 1 — Transport, vite bullet | `ok` — vite proxy keys are string prefixes, so `/iverson.` works there; the asymmetry is now stated rather than implied. |
| Design 1 — Transport, relative-base paragraph | `ok` — `apiBaseUrl` still read only at `config.ts:16`; `telemetry.ts:19` still relative. |
| Design 1 — `GetMetrics` | `ok` — fixed result set, three added dependencies, NetworkPolicy bullet (`networkpolicies.yaml:7-10`, `:38-63`, `:474`, `:487-490`), mangled names, server-side Ollama filter. |
| Design 1 — `GetQdrantStats` | `ok` — `IntelligenceCollectionManager.cs:16,43` admin api-key header; `IVectorRoles.cs:23-27` unchanged. |
| Design 1 — Authorization | `ok` — `.RequireAuthorization("Operator").EnableGrpcWeb()` matches `Program.cs:444`'s shape; policies at `:141-156` unchanged. |
| Design 1 — Explicitly not included | `ok` — nothing load-bearing. |
| Design 2 — Band A / B / C tables | `ok` — Band C names `AdminConsoleService.GetQdrantStats`; other sources re-resolved. |
| Design 2 — health-strip constraints | `ok` — tri-state `starrocks` at `Program.cs:318-323`; Ollama absent. |
| Design 2 — data-volume constraints | `ok` — `:490-494`, `:514`, `:501` all re-read. |
| Design 2 — RPC-health constraints | `ok` — `:56-59` vs `:66-71` unchanged; `http_route` exclusion accurate. |
| Design 2 — embedding-latency constraint | `ok` — two named clients at `ServiceCollectionExtensions.cs:12,29`. |
| Design 3 — cadence table | `ok` — 60s health strip matches the write-bearing rationale beneath it. |
| Design 3 — failure and degradation | `ok` — isolation, stale, backoff, hidden-tab, 401. |
| Design 3 — implementation shape | `ok` — hook is transport-agnostic. |
| Design 3 — Testing | `ok` — vitest 3.2, three test files. |
| Design 4a — grpc-web enablement | `ok` — `Program.cs:438-445` still lacks `.EnableGrpcWeb()` on the two named services. |
| Design 4b — codegen + CI paragraph | `ok` — `.github/workflows/` still only `codeql.yml`, `deploy-validate.yml`. |
| Design 4c — scrape targets | `ok` — VIP/HPA and `docker-compose.yml:438` unchanged. |
| Design 4d — identity | `ok` — blueprint glob, compose mount, `Sidebar.tsx:20,25`, `AppLayout.tsx:9` re-read. |
| Verified assumptions (A1-A35) | See §1. |
| Known issues | `ok` — three entries, none contradicting the current Design 1, none re-raised. |

### Rules and operands

| Row | Disposition |
|---|---|
| Path-routing rule: new Ingress paths vs **browser** grpc-web traffic, both directions | `ok` — over-inclusion and under-inclusion both fine for this consumer: each per-service Prefix path matches exactly its own methods. |
| Path-routing rule: new Ingress paths vs **native gRPC SDK** traffic, both directions | `→ §2.1` — **over-inclusion FAILS.** This is the operand the bullet assumes is clean. The rule was checked against only one of the two consumers that send `/iverson.<Service>/<Method>`. |
| Rule-ordering: new Ingress paths vs the api Ingress's `/` catch-all | `→ §2.2` — checked cross-Ingress precedence, not just within-Ingress specificity. |
| Identity rule: proto package namespace | `ok` — all six protos declare `package iverson;`; no two services share a name; the spec's five names are spelled correctly. |
| Eligibility predicate: which consumers send `/iverson.*` paths | `→ §2.1` — **enumerated every producer of that path shape** rather than the one the spec names. Two: the console's grpc-web client (HTTP/1.1) and the five language SDK clients over native gRPC (HTTP/2). The spec's routing accounts for the first only. |
| Eligibility predicate: which codegen pipelines consume a new proto | `ok` — A35 covers it; re-confirmed Python and Go enumerate their four `object_*` protos explicitly. |
| Health-strip state rule | `ok` — unchanged. |
| Data-volume count rule | `ok` — unchanged. |

### Data-flow arrows

| Row | Disposition |
|---|---|
| browser → `/iverson.AdminConsoleService/GetMetrics` → new Ingress → api:8081 | `→ §2.2` — the arrow is shadowed on AWS before it reaches the new Ingress. |
| browser → `/health` → new Ingress → api:8081 | `→ §2.2` — same shadowing; `/health` itself is a clean single element. |
| **SDK client → `/iverson.ObjectSearchService/Search` → api Ingress → api:8080** | `→ §2.1` — **this arrow is re-pointed by the design and breaks.** Its consuming operation needs HTTP/2; 8081 is `Protocols: Http1`. |
| dev browser → vite `server.proxy` → `localhost:8081` | `ok` — no native gRPC consumer traverses vite, so the string-prefix key is safe here. |
| api pod → Prometheus `:9090` | `ok` — both policy additions stated. |
| Prometheus → api/worker `:8081` per-pod | `ok` — podSelector egress; compose target exists. |
| new proto → .NET server build | `ok` — A35; `Iverson.Client.Contracts.csproj:17` globs with `GrpcServices="Both"`. |
| ID token → `Sidebar`'s `profile.groups` | `ok` — A33, verified by real token dump in round 2. |

### Candidates generated and dropped

| Candidate | Why dropped |
|---|---|
| The path list includes `TenantAdminGrpcService`, which no landing-page widget uses | Harmless forward-looking inclusion; routing a service the page does not call breaks nothing the spec promises. |
| Neither the new Ingress nor the vite proxy covers `/v1/traces`, so the console's trace export stays broken | Scope explicitly puts Jaeger traces out of scope; the spec mentions `/v1/traces` as evidence of the missing proxy, not as a promise to fix it. |
| Adding `group.name` to the already-deployed api Ingress causes the AWS LBC to replace the existing ALB | Operational/rollout consequence, not a break of the asked-for behavior. |
| The spec doesn't say which `className` the new Ingress carries | Implementation detail; `critical-implementation-review`'s surface. |
| The new Ingress needs the same `certificate-arn` / `listen-ports` / `ssl-redirect` annotations as the api Ingress on AWS | Same — configuration detail an implementer resolves from the existing Ingress. |

## 1. Verified-assumptions cross-check

A1-A35 reconfirmed under a fresh read; citations resolve to the lines they name. A35 is new since
round 3 and was verified independently: `Iverson.Client.Contracts.csproj:17` globs with
`GrpcServices="Both"`, the two TypeScript scripts glob, and Python and Go each name the four
`object_*` protos explicitly.

**Span check — dependencies introduced by the rewritten Transport bullet with no covering
assumption:**

1. *"Only the browser sends `/iverson.<Service>/<Method>` requests."* Load-bearing for routing
   those paths to 8081; no listed assumption covers who else uses the gRPC path space.
   **Verified in-round and FAILS** — see §2.1.
2. *"A path on a second Ingress takes precedence over a catch-all path on the first."*
   Load-bearing for the new Ingress being reachable at all; no assumption covers cross-Ingress
   rule ordering. **Verified in-round and FAILS on AWS** — see §2.2.
3. *"An IngressGroup can carry two Ingresses with different `backend-protocol-version` values."*
   Load-bearing for the separate-Ingress remedy. **Verified in-round and holds** — the annotation
   is per-Ingress and the AWS Load Balancer Controller resolves target-group settings per Ingress,
   which is why the split works at all. Closes clean.

## 2. Literal-wrongness findings

### 2.1 — Routing the per-service paths to 8081 breaks native gRPC for the SDK clients

**Description.** The Transport bullet routes `/iverson.AdminConsoleService`,
`/iverson.ObjectSearchService`, `/iverson.ObjectMappingService`,
`/iverson.TenantLifecycleGrpcService` and `/iverson.TenantAdminGrpcService` to service port 8081,
and asserts this leaves "the existing native-gRPC path on 8080 untouched." That assertion is
false. Browser grpc-web and native gRPC use **the same URL paths** — the gRPC wire protocol
defines the path as `/<package>.<Service>/<Method>` regardless of which transport carries it. A
path-based rule cannot tell the two apart, so re-pointing those five paths at 8081 re-points the
SDK clients' traffic too. 8081 is `Protocols: Http1`, and native gRPC requires HTTP/2, so five of
the seven services stop working for every external SDK client.

**Evidence.**

- `Iverson.Api/appsettings.json:16-17` binds `http://*:8081` to `"Protocols": "Http1"`; `:12-13`
  binds 8080 to `"Protocols": "Http2"`.
- `values-aws.yaml:73-75` sets `alb.ingress.kubernetes.io/scheme: internet-facing` **and**
  `alb.ingress.kubernetes.io/backend-protocol-version: GRPC` on the api Ingress. That annotation
  exists precisely because external clients speak native gRPC through this ingress — it is the
  spec's own evidence that a second consumer occupies the path space.
- All six existing protos declare `package iverson;`, so SDK calls land on exactly the
  `/iverson.<Service>/<Method>` paths the bullet claims.
- `ObjectPersistenceService` and `ObjectRetrievalService` are not in the bullet's list, so those
  two keep working — which makes the failure partial and therefore harder to spot in testing:
  a smoke test that only writes and reads objects would pass.

**Proposed fix.** The separation cannot be by path. Which axis to use instead is a real choice
with different costs — see §3.1. Whichever is chosen, the sentence "leave the existing
native-gRPC path on 8080 untouched" must go, because no path-based arrangement can make it true.

### 2.2 — On AWS the api Ingress's catch-all shadows every path on the new Ingress

**Description.** The bullet puts the new paths on a second Ingress and joins the two with
`alb.ingress.kubernetes.io/group.name`, but says nothing about ordering. Within an IngressGroup,
the AWS Load Balancer Controller orders listener rules by `alb.ingress.kubernetes.io/group.order`,
defaulting every Ingress without one to the same value and breaking ties by the lexical order of
the Ingress's namespace/name. The api Ingress's single path is `/` with `pathType: Prefix`, which
matches every request. If its rule is evaluated first, no request ever reaches the new Ingress's
rules — every gRPC widget and the health strip fail on AWS.

**Evidence.**

- `charts/api/templates/ingress.yaml:17-24` — the api Ingress's only path is `/`, `pathType: Prefix`,
  which matches all paths.
- `grep -rn "group.name\|group.order"` across `deploy/helm/iverson/` returns no Ingress
  annotations at all — only Authentik blueprint expressions. Neither ordering nor grouping exists
  today, so the new Ingress would inherit the default tie-break.
- `{release}-api` sorts lexically before any plausible name for a second api-related Ingress
  (`{release}-api-grpcweb`, `{release}-api-console`), so the catch-all wins the tie.

This is AWS-specific: ingress-nginx merges Ingresses for a host into one server block and resolves
prefix locations by longest match, so the specific paths win there without explicit ordering. The
design is silent on ordering, which reads as correct on nginx and fails on ALB.

**Proposed fix.** Set `alb.ingress.kubernetes.io/group.order` explicitly on both Ingresses, with
the new specific-path Ingress ordered ahead of the api Ingress's catch-all, and say so in the
bullet rather than relying on a default tie-break. Note the ordering requirement applies to
whatever arrangement §3.1 settles on, unless that arrangement removes the second Ingress entirely.

## 3. Forced decisions

### 3.1 — How browser grpc-web is separated from native gRPC, given path cannot do it

**The choice.** §2.1 establishes that both consumers occupy the same URL path space, so the
design's path-based split cannot work. The spec must separate them on some other axis, and it has
not named one.

**Why it's forced.** Three facts, all verified, and together they close off the obvious answer:

- Both transports address `/iverson.<Service>/<Method>`; the path carries no protocol
  information.
- The two Kestrel endpoints are mutually exclusive as configured: `appsettings.json:12-13` makes
  8080 HTTP/2-only (native gRPC works, cleartext browser grpc-web cannot) and `:16-17` makes 8081
  HTTP/1-only (browser grpc-web works, native gRPC cannot).
- Both consumers arrive through the same internet-facing ingress on the same host
  (`values-aws.yaml:73`, `charts/api/templates/ingress.yaml:15`).

So any arrangement must change one of those three, and each change lands in a different part of
the system.

**The options.**

- **(a) Make port 8080 accept both protocols.** Change `appsettings.json:12-13` from
  `"Protocols": "Http2"` to `"Http1AndHttp2"`, so one port serves native gRPC (via the HTTP/2
  cleartext preface) and browser grpc-web (HTTP/1.1) on the same paths. This is the smallest
  change by far — one config value — and it deletes the entire second-Ingress arrangement, taking
  §2.2 with it. It rests on Kestrel's cleartext preface detection continuing to serve the SDK
  clients unchanged, which this review could not confirm by reading and which should be
  spike-tested against the running stack before the spec commits to it.
- **(b) Separate by host.** Give the console's grpc-web its own hostname (e.g. a
  `console.`-prefixed host) routed to 8081, leaving `iverson.example.com` entirely on 8080. Path
  collision disappears because the host differs. Cost: a second DNS name and certificate per
  environment, and the console's same-origin assumption has to be re-checked, since the console
  is served from the existing host.
- **(c) Put the console on native gRPC over TLS only.** Drop the 8081 route; the console speaks
  grpc-web over HTTP/2 to 8080, which browsers negotiate via ALPN. Cost: the cleartext profiles
  (`values.yaml:22` and `values-local.yaml`, both `http://iverson.local`) stop working for the
  console, so local and default deployments would need TLS.

Not picking between these: (a) is a server-configuration change whose viability needs an empirical
check, (b) adds per-environment DNS and certificate work, and (c) changes the deployment
requirements of two profiles. The trade-off is between spike cost, operational surface, and
narrowing where the console runs.

## 4. Previously addressed

- **Round 3 §2.1** — the bullet now names the path form explicitly, with the element-wise rule
  spelled out and the per-service list given; the vite bullet no longer claims both legs forward
  the same prefixes.
- **Round 3 §2.2** — the paths moved to a separate Ingress with the annotation-scoping evidence
  and the `group.name` mechanism. §2.2 here concerns ordering within that group, which the fix
  did not address.
- **Round 3 §1 span check** — A35 now covers the codegen pipelines.
- **Round 2 §2.1 / §2.2** — the read-only scope exception and the removed CI clause both remain
  correct on a fresh read.

## 5. Recommendation

🛑 **Surface forced decisions to user** — §3 is non-empty.

§2.1 and §3.1 are the same subject seen from two sides: the current text is wrong, and the remedy
genuinely forks. §2.1 is the more serious finding in this review's history so far, because unlike
every prior transport finding it breaks something that works **today** — five of seven services
for every external SDK client — rather than failing to enable something new. Its partial nature
makes it worse: a smoke test exercising only persistence and retrieval would pass.

§2.2 is bounded and has a concrete fix, but it is conditional on §3.1: option (a) removes the
second Ingress entirely and the ordering question with it.

Five candidates were generated and dropped with reasons recorded in §0. This is the fourth
consecutive round in which the Transport surface produced a finding, and the third in which the
finding was in text written by the previous round's update.
