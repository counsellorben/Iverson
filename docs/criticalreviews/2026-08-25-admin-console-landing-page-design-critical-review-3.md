# Critical Design Review: 2026-08-25-admin-console-landing-page-design (Round 3)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-25-admin-console-landing-page-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

Enumeration built before consulting rounds 1-2. The gRPC conversion and the new Transport
subsection are the round's main surface; the rest of the spec is re-swept at the same depth
rather than assumed settled by two prior rounds.

### Sections

| Row | Disposition |
|---|---|
| Problem | `ok` — re-read; `router.tsx` index redirect, four `Coming soon` pages, and the metric inventory all still resolve. |
| Scope — in-scope | `ok` — now says "two new read-only gRPC methods" and names the browser-reachable route; both resolve to Design 1. |
| Scope — out-of-scope (write exception) | `ok` — the exception matches `Program.cs:310-311`; wording no longer over-claims. |
| Design 1 — service placement | `ok` — `admin_console.proto` in `Iverson.Clients/Common/Proto/` is where all six existing protos live, and `TenantLifecycleGrpcService`/`TenantAdminGrpcService` set the precedent for admin-only services living there. |
| Design 1 — gRPC-not-REST rationale | `ok` — verified the collision claim in both directions. `/admin/metrics` matches `charts/admin-ui/templates/ingress.yaml:21`'s `/admin(/|$)(.*)`; `/iverson.AdminConsoleService/GetMetrics` does not (the regex is start-anchored). The stated reason is sound. |
| Design 1 — **Transport**, ingress bullet | `→ §2.1` and `→ §2.2` — two independent defects in one bullet. |
| Design 1 — Transport, vite bullet | `ok` — and see §2.1: the vite leg works with a plain string prefix, which is exactly why the asymmetry with the Ingress leg is easy to miss. |
| Design 1 — Transport, relative-base paragraph | `ok` — `grep -rn apiBaseUrl Iverson.AdminUI/src/` still returns only `config.ts:16`; `src/telemetry.ts:19` still uses a relative path. |
| Design 1 — `GetMetrics` | `ok` — the fixed-result-set rationale, the three added dependencies, the NetworkPolicy bullet (re-read `networkpolicies.yaml:7-10`, `:38-63`, `:474`, `:487-490`), the mangled-name note and the server-side Ollama filter all check out. |
| Design 1 — `GetQdrantStats` | `ok` — `IntelligenceCollectionManager.cs:16,43` injects the admin api-key so cross-tenant enumeration works; `IVectorRoles.cs:23-27` still exposes only the two write-ish methods. |
| Design 1 — Authorization | `ok` — `.RequireAuthorization("Operator").EnableGrpcWeb()` matches the mapping shape at `Program.cs:444`; the three policies at `:141-156` are unchanged. |
| Design 1 — Explicitly not included | `ok` — nothing load-bearing. |
| Design 2 — Band A / B / C tables | `ok` — Band C now names `AdminConsoleService.GetQdrantStats`; every other source re-resolved. |
| Design 2 — health-strip constraints | `ok` — tri-state `starrocks` (`Program.cs:318-323`) and Ollama absence unchanged. |
| Design 2 — data-volume constraints | `ok` — `:490-494` throw, `:514` never assigning `Total`, `:501` empty-on-denial all re-read. |
| Design 2 — RPC-health constraints | `ok` — `Program.cs:56-59` vs `:66-71` unchanged; the `http_route` exclusion paragraph is accurate. |
| Design 2 — embedding-latency constraint | `ok` — two named clients at `ServiceCollectionExtensions.cs:12,29`. |
| Design 3 — cadence table (60s health strip) | `ok` — the new rate matches the write-bearing rationale beneath it. |
| Design 3 — failure and degradation | `ok` — per-card isolation, stale, backoff, hidden-tab pause, 401 routing. |
| Design 3 — implementation shape | `ok` — `usePolledResource(fetcher, intervalMs)` is transport-agnostic, so four gRPC widgets plus one `/health` fetch all fit the one hook. |
| Design 3 — Testing | `ok` — vitest 3.2, three existing test files. |
| Design 4a — grpc-web enablement | `ok` — `Program.cs:438-445` still maps `ObjectMapping`/`ObjectSearch` without `.EnableGrpcWeb()`; `UseGrpcWeb()` at `:284`. |
| Design 4b — codegen (incl. the new CI paragraph) | `ok` — `.github/workflows/` still holds only `codeql.yml` and `deploy-validate.yml`; the paragraph's negative claim is accurate. |
| Design 4c — scrape targets | `ok` — VIP/HPA facts unchanged; `docker-compose.yml:438` worker still present. |
| Design 4d — identity | `ok` — blueprint glob (`blueprints-configmap.yaml:6`), compose mount (`docker-compose.yml:294,328`), `Sidebar.tsx:20,25`, `AppLayout.tsx:9` all re-read. |
| Verified assumptions (A1-A34) | See §1. |
| Known issues | `ok` — three entries; none contradicts the now-gRPC Design 1, and none is re-raised here. |

### Rules and operands

| Row | Disposition |
|---|---|
| Path-routing rule: `/admin(/|$)(.*)` vs gRPC paths, both directions | `ok` — over-inclusion: the regex is start-anchored, so `/iverson.*` cannot match it. Under-inclusion: `/admin/metrics` does match, which is the reason the spec gives for going gRPC. Both directions check out. |
| Path-routing rule: the new 8081 Ingress path vs real gRPC paths, both directions | `→ §2.1` — **under-inclusion FAILS.** Checked the assumed-clean operand (the path pattern) against the real paths rather than accepting "the gRPC path prefix" by assertion. |
| Backend-protocol rule: one Ingress, two backend ports | `→ §2.2` — the annotation's scope is the Ingress, not the path. |
| Identity rule: proto package namespace | `ok` — **checked all six protos**: every one declares `package iverson;`, so paths are uniformly `/iverson.<Service>/<Method>` and the spec's `/iverson.AdminConsoleService/<Method>` is correctly formed. No two services share a name. |
| Eligibility predicate: which codegen pipelines consume a new proto | `ok` — **enumerated every producer** rather than the ones the spec names. `Iverson.Client.Contracts.csproj:17` globs `../../Common/Proto/*.proto` (server + .NET client, needed); `Iverson.AdminUI/scripts/generate_protos.sh` globs (needed); `Iverson.Clients/TypeScript/scripts/generate_protos.sh` globs (generates unused stubs, harmless). Python (`generate_protos.sh`) and Go both list the four `object_*` protos **explicitly**, so neither is touched. No pipeline breaks. |
| Eligibility predicate: does the conformance harness enumerate the service set | `ok` — grepped `Iverson.Server/Iverson.ClientConformance/`: every service reference is a doc-comment naming a specific method (`Requirements.cs`, `Scenarios/*`). Nothing enumerates or asserts the set of services, so adding one cannot break the harness. |
| Health-strip state rule | `ok` — unchanged; tri-state operand still the one the spec tests. |
| Data-volume count rule | `ok` — unchanged. |

### Data-flow arrows

| Row | Disposition |
|---|---|
| browser → `/iverson.AdminConsoleService/GetMetrics` → Ingress → api:8081 | `→ §2.1` — the routing operation's match parameter does not match the real path. |
| browser → `/iverson.AdminConsoleService/GetQdrantStats` → Ingress → api:8081 | `→ §2.1` — same. |
| browser → the three existing gRPC widgets → Ingress → api:8081 | `→ §2.1` — the spec says the route serves all five widgets, so all five ride the same broken match. |
| browser → `/health` → Ingress → api:8081 | `ok` — `/health` is a single whole path element, so a `pathType: Prefix` path of `/health` matches it correctly. This arrow is the one in the bullet that does work. |
| dev browser → vite `server.proxy` → `localhost:8081` | `ok` — vite's proxy keys are plain string prefixes, not element-wise, so `/iverson.` works here. Noted in §2.1 because it is the reason the two bullets look symmetric and are not. |
| api pod → Prometheus `:9090` | `ok` — both policy additions stated in Design 1. |
| Prometheus → api/worker `:8081` per-pod | `ok` — podSelector egress survives; compose target exists. |
| new proto → .NET server build (crosses codegen boundary) | `ok` — `Iverson.Client.Contracts.csproj:17` globs with `GrpcServices="Both"`, so the server base class is generated without a csproj edit. |
| ID token → `Sidebar`'s `profile.groups` | `ok` — verified by real token dump in round 2; A33 records it. |

### Candidates generated and dropped

| Candidate | Why dropped |
|---|---|
| Transport says "a browser reaches the API only over TLS, where ALPN can negotiate HTTP/2" — but ingress→backend is a separate hop, and `nginx.ingress.kubernetes.io/backend-protocol` is set **nowhere** in the chart, so nginx speaks HTTP/1.1 to Kestrel's Http2-only 8080 regardless of front-side TLS | The sentence is descriptively imprecise about the status quo, and the truth is *worse* than stated (8080 is unreachable on nginx even over TLS). But the section's prescription — add an 8081 route — is unaffected and in fact strengthened. No prescription changes, so it fails literal-wrongness. |
| The TypeScript client generates `AdminConsoleService` stubs it never uses | Harmless bloat; nothing breaks and nothing the spec promises fails. |
| Routing `/health` on a public ingress path exposes a write-bearing anonymous endpoint | `/health` already matches the api Ingress's `/` prefix today, so the design adds no exposure. Security posture is `critical-security-review`'s surface regardless. |
| `admin_console.proto` has no `option go_package`, unlike what Go codegen would need | Go's script lists the four `object_*` protos explicitly and never compiles this one. Not reachable. |
| The spec doesn't name the RPCs' request/response message shapes | Implementation detail; `critical-implementation-review`'s surface once a plan exists. |

## 1. Verified-assumptions cross-check

A1-A34 reconfirmed under a fresh read; citations resolve to the lines they name. A33 and A34 are
new since round 2 and were verified independently rather than taken from the update: A33 against
the ID-token claim set dumped from a real token response, A34 against `docker-compose.yml:438,454`
and the placement of `MapPrometheusScrapingEndpoint` (`Program.cs:275`) outside the role gate at
`:438`.

**Span check — dependencies introduced by the Transport subsection with no covering assumption:**

1. *"A Kubernetes Ingress path can express the gRPC path prefix."* Load-bearing for the entire
   Transport bullet; no listed assumption covers Ingress path-matching semantics.
   **Verified in-round and FAILS** — see §2.1.
2. *"One Ingress can serve two backend ports with different wire protocols."* Load-bearing for
   "an api Ingress path… to service port 8081"; no assumption covers annotation scope.
   **Verified in-round and FAILS** — see §2.2.
3. *"Adding a proto to `Common/Proto` does not break the other language clients."* Load-bearing
   for the gRPC conversion; no assumption covers the codegen pipelines.
   **Verified in-round and holds** — Python and Go enumerate their protos explicitly; only the
   globbing pipelines (.NET Contracts, both TypeScript scripts) pick the new file up, and all
   three tolerate it. Closes clean.

## 2. Literal-wrongness findings

### 2.1 — "The gRPC path prefix" cannot be expressed as a Kubernetes Ingress Prefix path

**Description.** Design 1's Transport bullet prescribes "an api Ingress path routing the gRPC path
prefix and `/health` to service port 8081". There is no single `pathType: Prefix` value that
matches the gRPC paths, because Kubernetes Prefix matching is element-wise, not string-wise. An
Ingress written literally from this bullet routes `/health` correctly and routes **no gRPC traffic
at all** — which means all five gRPC widgets, not just the two new RPCs, fail.

**Evidence.**

- Every proto declares `package iverson;` — checked all six in
  `Iverson.Clients/Common/Proto/` — so gRPC paths are `/iverson.<Service>/<Method>`, e.g.
  `/iverson.AdminConsoleService/GetMetrics`.
- Kubernetes `pathType: Prefix` is defined to match "based on a URL path prefix split by `/`…
  on a path element by element basis". The documented negative example is exactly this shape:
  prefix `/aaa` does **not** match request `/aaabbb`.
- So a path of `/iverson.` has the single element `iverson.`, which is not equal to the request's
  first element `iverson.AdminConsoleService`. No match.

The asymmetry is what makes this easy to miss: the bullet's sibling — the `vite.config.ts`
`server.proxy` entry — *does* work with a plain `/iverson.` key, because vite's proxy matches
string prefixes. The spec says the two legs forward "the same prefixes"; only one of them can.

**Proposed fix.** State the path form explicitly, in one of the two shapes that actually match:

- one `pathType: Prefix` path **per service** — `/iverson.AdminConsoleService`,
  `/iverson.ObjectSearchService`, `/iverson.ObjectMappingService`, `/iverson.TenantLifecycleGrpcService`,
  `/iverson.TenantAdminGrpcService` — since each is a whole path element and therefore matches its
  own methods; or
- one `pathType: ImplementationSpecific` regex path, the form this chart already uses at
  `charts/admin-ui/templates/ingress.yaml:21` (`/admin(/|$)(.*)`).

The per-service form is the safer default: it needs no `use-regex` annotation and it fails loudly
(a missing service 404s) rather than silently over-matching.

### 2.2 — One Ingress cannot carry both backend ports, because the backend-protocol annotation is Ingress-scoped

**Description.** The same bullet prescribes adding the 8081 path to the **api** Ingress. On AWS
that Ingress carries `alb.ingress.kubernetes.io/backend-protocol-version: GRPC`, which applies to
the Ingress as a whole rather than per path. Adding an 8081 path to it places the grpc-web backend
— which speaks HTTP/1.1 — under a GRPC protocol declaration. The spec names the conflict and then
prescribes the arrangement that creates it.

**Evidence.**

- `charts/api/templates/ingress.yaml:4-6` renders `.Values.ingress.annotations` onto the Ingress's
  `metadata.annotations` — Ingress-level, with no per-path mechanism in the template.
- `values-aws.yaml:75` sets `alb.ingress.kubernetes.io/backend-protocol-version: GRPC` in that
  block.
- The spec's own text acknowledges the annotation is "correct for native gRPC on 8080 and wrong
  for grpc-web on 8081", but the bullet still says to add the path to that Ingress.

**Proposed fix.** Put the 8081 paths on a **separate Ingress object** with its own annotations,
rather than on the api Ingress. On AWS, give both Ingresses the same
`alb.ingress.kubernetes.io/group.name` so they continue to share one ALB while carrying different
backend-protocol annotations. This also composes with §2.1's fix: the new Ingress is where the
per-service paths (or the regex path) live, and it leaves the existing native-gRPC path on 8080
untouched.

## 3. Forced decisions

No forced decisions found.

Round 2's §3.1 picked gRPC and the spec now states it with its reasoning; this round's two
findings are both bounded corrections to how that decision is expressed in deployment
configuration, with a concrete fix each, rather than choices between alternatives.

## 4. Previously addressed

- **Round 1 §2.3 / Round 2 §3.1** — the transport question is now decided and written: Design 1
  is gRPC on a new `AdminConsoleService`, with the `/admin/` path collision recorded as the
  reason. §2.1 and §2.2 here concern how the supporting route is configured, not whether the
  decision was right.
- **Round 2 §2.1** — the Scope bullet now states the `/health` write exception, and the cadence
  table drops the health strip to 60s with the rationale beneath it.
- **Round 2 §2.2** — Design 4b's CI clause is gone, replaced by an explicit statement that no
  AdminUI CI job exists and that adding one is separate work.
- **Round 2 §1 span check** — A33 and A34 now cover the ID-token claim path and the compose
  worker scrape target.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 non-empty, §3 empty.

Both findings sit in the Transport bullet added during round 2's update, and both are deployment
configuration rather than design shape: the gRPC decision itself survives review. §2.1 is the more
serious of the two because it silently breaks all five gRPC widgets rather than only the AWS
profile, and because its sibling bullet works, which makes the failure look like a typo rather
than a semantics mismatch.

Five candidates were generated and dropped with reasons recorded in §0. The closest was the
Transport section's claim about TLS and ALPN, which is imprecise in a direction that makes the
current state worse than described — but it changes no prescription, so it fails the
literal-wrongness test.
