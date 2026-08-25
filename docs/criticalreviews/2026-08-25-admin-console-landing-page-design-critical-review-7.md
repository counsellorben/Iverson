# Critical Design Review: 2026-08-25-admin-console-landing-page-design (Round 7)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-25-admin-console-landing-page-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

Sweep built before prior reviews were read. The spec grew 594 → 850 lines since round 6; the added
material (Design 1 Transport rewrite, 4e, 4f, Design 5, A39-A51, Scope) gets deep rows, and the
sections round 6 covered get one row each re-checked against anything the new material changed.

### Sections

| Section | Disposition |
|---|---|
| Problem | `ok` — unchanged since round 6; no claim touched by the new material |
| Scope (in scope) | `ok` — new bullet's counts check out: 9 CSR + 2 verification-surfaced = 11; "four gate" = allowlist 1 + 4e 1 + 4f 2; "seven in Design 5" = 5a-5g |
| Scope (out of scope) | `ok` — the "Authentication changes" bullet now excepts 5a, resolving the contradiction the addition would otherwise have created. The "no write surface except `/health`" bullet still holds after 4e (caching bounds the writes, does not remove them) |
| Design 1 — Server surface, opening | `ok` — `admin_console.proto` home argument unchanged |
| Design 1 — Transport, opening + 400-qualification | `ok` — new paragraph correctly separates protocol negotiation from authorization; consistent with A51 |
| Design 1 — Transport, allowlist bullet | `→ §2.1` |
| Design 1 — Transport, `group.order` paragraph | `→ §2.1` — same root cause |
| Design 1 — Transport, vite bullet | `ok` — `server.proxy` prefix rewrite still covers all three allowlist paths in dev, including 5g's |
| Design 1 — `GetMetrics` / `GetQdrantStats` / Authorization / not-included | `ok` — untouched by this round's additions; round 6 dispositions stand and no new text contradicts them |
| Design 2 — Widget inventory (all bands) | `ok` — health strip still sourced from `/health`, which the allowlist admits; Band B/C sources are gRPC paths the regex matches (see rules) |
| Design 3 — Refresh cadence | `ok` — 4e explicitly does not alter the 60s poll, and the write-bearing rationale at Design 3 survives caching (the console still triggers work; caching bounds an arbitrary caller) |
| Design 3 — Failure/degradation, implementation shape, testing | `ok` — no new material touches them; 5e explicitly disclaims changing landing-page behaviour |
| Design 4 intro | `ok` — "(4a-4d)" scoping added, 4e/4f framed as gating; internally consistent with Design 5's non-gating claim |
| Design 4a / 4b / 4c / 4d | `ok` — unchanged; 4c's Prometheus scrape facts are now load-bearing for 4f and were re-verified (see rules) |
| Design 4e | `ok` — see rules/arrows rows below |
| Design 4f | `→ §2.3`, `→ §3.2` |
| Design 5 — 5a | `ok` — A47 reconfirmed; the `offline_access` retention argument is stated with its reason and does not contradict Scope |
| Design 5 — 5b | `ok` — both halves land on real APIs (A48, A49); error-path justification matches `CallbackPage.tsx:15-19` |
| Design 5 — 5c | `→ §2.2` |
| Design 5 — 5d | `ok` — see rules row (allowlist regex tested against real values, both directions) |
| Design 5 — 5e | `ok` — scoped as defense-in-depth, explicitly not a landing-page change; consistent with Scope's "stub pages stay stubs" |
| Design 5 — 5f | `ok` — `Connection.LocalPort` is a real API and the `RequireHost` rejection reason is correct (Host header carries no port behind an ingress) |
| Design 5 — 5g | `ok` — see arrows rows |
| Verified assumptions | `ok` — A38 restated rather than left describing the replaced catch-all; A39-A51 appended; preamble updated with the four failures |
| Known issues | `ok` — Operator blocker unchanged and still consistent with 4d |

### Rules and operands (both failure directions)

| Rule | Disposition |
|---|---|
| Allowlist regex vs **gRPC-Web** paths, over-inclusion | `ok` — `iverson\.[A-Za-z0-9_.]+/[A-Za-z0-9_]+` under `/admin-api/` cannot reach `/metrics` or `/probe/*`: those do not begin with `iverson.`. A crafted `/admin-api/iverson.x/metrics` rewrites to `/iverson.x/metrics`, which the API does not serve → 404, not an exposure |
| Allowlist regex vs **gRPC-Web** paths, under-inclusion | `ok` — enumerated every service the design calls against the real protos: `package iverson` in all of `Iverson.Clients/Common/Proto/*.proto`, services `ObjectPersistenceService`, `ObjectRetrievalService`, `ObjectSearchService`, `ObjectMappingService`, `TenantLifecycleGrpcService`, `TenantAdminGrpcService`, plus the new `AdminConsoleService`. All match, methods included. gRPC-Web appends no query string or suffix, so the `$` anchor does not exclude live traffic |
| Allowlist regex vs **native SDK** gRPC, both directions | `ok` — SDK clients send bare `/iverson.<Service>/<Method>`, which lacks the `/admin-api` prefix, so they are neither captured nor broken. This is round 4's operand and the prefix still separates the two consumers |
| Rewrite rule: `rewrite-target: /$1` across **three** paths | `ok` on the capture-index question — all three regexes capture into group 1, which is required because `charts/admin-ui/templates/ingress.yaml:5` shows `rewrite-target` is Ingress-level metadata shared by every path on the object. `/admin-api/(v1/traces)$` captures `v1/traces` including the inner slash → `/v1/traces`. The *controller* question is separate → §2.1 |
| Eligibility predicate: which endpoints 4e gates | `ok` — enumerated every producer of `AllowAnonymous` in `Program.cs` (seven sites: `:275`, `:299`, `:334`, `:340`, `:346`, `:352`, `:359`), not just the ones the spec names. 4e gates four, leaves `/metrics` (Prometheus) and `/health` + `/health/live` (kubelet) anonymous. The set is completely partitioned with a stated reason per member |
| 4e gating vs callers of `/probe/*`, under-inclusion | `ok` — A39's negative claim re-tested with a wider net than the spec cites: `grep -rn "/probe/"` across `*.yaml`, `*.yml`, `*.cs`, `*.sh`, `*.md`, `*.ts` returns only the four definitions and `tma.md:117`. `docker-compose.yml` is covered by that glob and declares no healthcheck against them |
| 4e cache window vs readiness period | `ok` — `charts/api/templates/deployment.yaml:173-180` declares no `periodSeconds`, so 10s default; a 5s window keeps readiness at most one cycle stale. Cache is per-process and readiness is per-pod, so 2-5 replicas do not interact |
| 4f consumer set for port 8081, under-inclusion | `→ §2.3` — the enumeration itself is **correct and complete** (ingress-nginx, Prometheus, kubelet, ALB), and checking it surfaced a confirming asymmetry: `networkpolicies.yaml:88-103` gives *worker* an explicit `podSelector{prometheus} → 8081` rule while the api policy has none, so api's scrape access today rests solely on `from: []`. The defect is in the mechanism, not the consumer list |
| 5d validation regex vs real config values, both directions | `ok` — tested against every value the repo actually ships: `dev-iverson-human-oidc-client-id`, `http://localhost:9000/application/o/iverson-api/`, `http://localhost:8080` (`.env.development`), and `http://authentik.iverson.example.com/application/o/iverson-api/`, `https://iverson.example.com` (`values-aws.yaml:142-143`). All pass `^[A-Za-z0-9:/._-]+$`; none is rejected, and a `"` or `;` is |
| 5c CSP directive set vs the traffic this design introduces | `→ §2.2` |
| 5c CSP: `font-src` absent from the named directives | `dropped` — candidate generated, failed literal-wrongness. `@fontsource/fraunces` is bundled into the build (the woff2/woff files land in `dist/assets/`), so the fonts are same-origin and covered by `'self'`; no rendering breaks |
| 5a: keeping `offline_access` vs `frame-ancestors 'none'` in 5c | `dropped` — candidate generated, failed literal-wrongness. With a refresh token present `oidc-client-ts` renews via the token endpoint and opens no iframe, so the frame directive and the renewal path do not interact |

### Data-flow arrows

| Arrow → operation | Disposition |
|---|---|
| Browser → ingress → api:8081, **nginx profiles** | `ok` — a matching regex location wins over the api Ingress's `/` prefix location; the `/admin` precedent proves regex mode engages without a `use-regex` annotation |
| Browser → ingress → api:8081, **AWS profile** | `→ §2.1` — crosses a controller boundary; the annotation the arrow depends on is not interpreted by the controller on this side |
| Browser → Authentik token endpoint (cross-origin fetch) | `→ §2.2` — this arrow is the one 5c's `connect-src` governs, and it is the login path |
| `telemetry.ts` → `/admin-api/v1/traces` → api `:452-460` → Jaeger | `ok` — endpoint exists with `RequireAuthorization()` (fallback policy = any authenticated user), and `telemetry.ts:29-38` already attaches the bearer token via the exporter's `headers` factory, verified as a supported `HeadersFactory`. `ignoreUrls` uses the same constant so the self-trace guard follows the change |
| `telemetry.ts` → `/admin-api/v1/traces` in **dev** | `ok` — Design 1's vite `server.proxy` maps the whole `/admin-api` prefix, so 5g's path is proxied by the same entry as the RPCs; no second proxy rule needed |
| kubelet → api:8081 `/health` → cached fan-out → readiness verdict | `→ §2.3` — the arrow's *source* is the operand 4f cannot express portably |
| Prometheus → api:8081 `/metrics`, and → worker:8081 | `ok` — `charts/prometheus/templates/configmap.yaml:10-15` targets both; pod label `app: {{ .Release.Name }}-prometheus` confirmed at `charts/prometheus/templates/deployment.yaml:27`, so 4f's proposed `podSelector` operand names a label that exists |
| Console → `/admin-api/health` → health-strip tiles | `ok` — `/health` returns the per-store `checks` object the tiles read; 4e keeps it anonymous and the allowlist admits it. `/health/live` correctly excluded (no `checks` object) |
| Entrypoint env → `config.js` → browser execution | `ok` — 5d validates before substituting; `envsubst` confirmed present in the runtime image (A43) so the mechanism still exists after the change |

## 1. Verified-assumptions cross-check

Fresh read of the cited evidence for the assumptions this round's material rests on. Round 1-6
assumptions not touched by the new text were not re-litigated.

- **A38 (restated)** — holds as restated. The rewrite *mechanism* is unchanged; only the capture
  index moved from `$2` to `$1`. The restatement correctly stops describing the replaced catch-all.
- **A39** — reconfirmed, and re-tested with a wider glob than the spec cites. Only the four
  definitions plus `Iverson.Server/docs/security/tma.md:117`. Gating them breaks no caller.
- **A40** — reconfirmed at `charts/prometheus/templates/configmap.yaml:10-15`; both `iverson-api`
  → `<release>-api:8081` and `iverson-worker` → `<release>-worker:8081`, no auth stanza.
- **A41** — reconfirmed at `charts/api/templates/deployment.yaml:173-180`. `/health` on 8081, no
  `periodSeconds`, so the 10s default bounds 4e's window as the spec says.
- **A42** — reconfirmed: `Program.cs:217` `AddMemoryCache()`, and `Tenancy/TenantStatusCache.cs:8`
  is a live `IMemoryCache` consumer to pattern after.
- **A44** — reconfirmed on all three legs: `values-aws.yaml:74` `target-type: ip`;
  `deploy/terraform/modules/cluster-aws/main.tf:475` `enableNetworkPolicy = "true"`; operators
  installs `aws-load-balancer-controller`. Selector-based rules genuinely grant nothing on AWS.
- **A45** — reconfirmed at `charts/admin-ui/templates/ingress.yaml:5`: `rewrite-target` sits in
  `metadata.annotations`, shared by every path on the object. The three-paths-into-`$1` constraint
  the spec derives from it is correct.
- **A46** — reconfirmed against the real protos; every service and the new one match the regex.
- **A47, A48, A49** — reconfirmed against the installed libraries (settings spread at
  `react-oidc-context.js:139-149`; `onSigninCallback` in the published types; the
  `{documentLoad, documentFetch, resourceFetch}` shape in `types.d.ts:14-18`).
- **A50, A51, A43** — reconfirmed as failures; each is correctly recorded as driving a design change
  (5g, 5f, and 5d's mechanism respectively).

### Span check — uncovered dependencies

Three facts the design needs that no listed assumption covers, as scoped. All three were verified
in-round and all three became §2 findings:

1. **That the ingress controller can perform a path rewrite at all.** A38 and A45 verify the rewrite
   *semantics* and the annotation's *scope*, both against ingress-nginx. Neither states that the
   controller on the AWS profile interprets `nginx.ingress.kubernetes.io/rewrite-target`. → §2.1
2. **That a per-environment Authentik origin can reach a static `nginx.conf`.** No assumption covers
   how 5c's `connect-src` operand is populated; `nginx.conf` is baked into the image at
   `Dockerfile:24` while the origin is a Helm value. → §2.2
3. **What `networkPolicy.clusterCidrs` contains when unset.** 4f introduces the key; no assumption
   covers its default, and the chart has no existing CIDR value or `ipBlock` to inherit one from. → §2.3

## 2. Literal-wrongness findings

### 2.1 — The AWS profile cannot perform the `/admin-api` rewrite, so every console RPC 404s there

**Description.** Design 1's transport depends on the ingress stripping `/admin-api` before the
request reaches the API. The mechanism specified is
`nginx.ingress.kubernetes.io/rewrite-target: /$1`. That annotation is implemented by ingress-nginx.
On the AWS profile the controller is the AWS Load Balancer Controller
(`deploy/terraform/modules/operators/main.tf:113-115`), which does not interpret
`nginx.ingress.kubernetes.io/*` annotations and offers no path-rewrite action — ALB matches and
forwards, it does not rewrite. The prefix therefore survives to the backend, which receives
`/admin-api/iverson.AdminConsoleService/GetMetrics` and serves no such route. Every widget on the
page fails on AWS.

The spec is explicit that AWS is a supported target for this route — it instructs the reader to give
both Ingresses the same `alb.ingress.kubernetes.io/group.name` and to set `group.order` so this
Ingress is ordered ahead of the api Ingress. Ordering the rule correctly only guarantees ALB matches
it; matching it still forwards the unrewritten path.

**Evidence.**
- `docs/specs/…-design.md`, Design 1 → Transport: the new-Ingress bullet specifies
  `rewrite-target: /$1` and the following paragraph specifies `group.name`/`group.order` for AWS.
- `deploy/terraform/modules/operators/main.tf:113-115` installs `aws-load-balancer-controller`;
  nothing in the module installs ingress-nginx.
- `values-aws.yaml:71` sets `className: "alb"` for the api Ingress and `:144-152` sets it for
  admin-ui, confirming the AWS profile routes through ALB rather than nginx.
- Round 6 checked this arrow for *ordering* (its row 51) and traced the rewrite under nginx
  semantics (row 52). Neither asked whether the AWS controller performs rewrites at all.

**Note on blast radius.** `charts/admin-ui/templates/ingress.yaml:21` already uses the same
`rewrite-target` pattern, so the console's own static route has the same problem on AWS today. That
is pre-existing and not this spec's defect — but it means the AWS profile cannot be assumed to work
by analogy with the existing rule, which is the argument the spec currently rests on ("This mirrors
the shape `charts/admin-ui/templates/ingress.yaml` already uses").

**Proposed fix.** The rewrite has to stop being load-bearing on ALB. Three architectures do that,
and they differ enough that the choice is surfaced as §3.1 rather than picked here. The smallest is
to drop the prefix on the AWS profile and separate the two consumers by **host** instead of path —
a second Ingress on `api.<ingressHost>` routing `/` to 8081 with no rewrite needed, leaving the
path-prefix arrangement for the nginx profiles.

### 2.2 — 5c's CSP cannot name the Authentik origin from a static `nginx.conf`, and omitting it breaks login

**Description.** 5c specifies `connect-src 'self' <authentik-origin>` added to `nginx.conf`. The
Authentik origin is per-environment: `adminUi.oidcAuthority` is
`http://localhost:9000/application/o/iverson-api/` in `.env.development` and
`http://authentik.iverson.example.com/application/o/iverson-api/` at `values-aws.yaml:142`.
`nginx.conf` is a static file copied into the image at `Dockerfile:24`, before any environment is
known. Implemented literally, the directive either carries an unresolved placeholder or omits the
origin.

Either way the effect is the same and it is not cosmetic: `oidc-client-ts` fetches the OIDC
discovery document and performs the authorization-code token exchange as XHR/fetch to the Authentik
origin. Those are `connect-src` subjects. With `connect-src 'self'` and no Authentik origin, the
token exchange is blocked by the browser and **login cannot complete** — a stricter failure than the
one the console has today, and one this spec would introduce.

**Evidence.**
- `docs/specs/…-design.md`, Design 5c: "`connect-src 'self' <authentik-origin>`" with the
  instruction to add an `add_header` block, in a section whose only named file is `nginx.conf`.
- `Iverson.AdminUI/Dockerfile:24` — `COPY Iverson.AdminUI/nginx.conf /etc/nginx/conf.d/default.conf`,
  a build-time copy.
- `Iverson.AdminUI/src/config.ts:14-16` — the authority is a runtime value read from
  `window.__ADMIN_UI_CONFIG__`, i.e. resolved *after* the image is built.
- `values-aws.yaml:142` vs `.env.development` — the two origins differ, so no single baked value works.

**Proposed fix.** Generate the header at container start from the same environment the config
already comes from, rather than baking it. `docker-entrypoint.sh` already runs at startup and
already reads `OIDC_AUTHORITY`; have it emit a small `conf.d` snippet (or an `nginx.conf` rendered by
`envsubst`, which A43 confirms is present in the image) containing the CSP with the origin
interpolated. 5d's validation regex already constrains that value, so the same check that makes
`config.js` safe makes the header safe. The spec should say which of the two files carries the CSP
and that the origin is derived from `OIDC_AUTHORITY`, not hardcoded.

### 2.3 — 4f replaces a working rule with one whose required value the chart cannot supply, breaking readiness

**Description.** 4f removes `from: []` on port 8081 and replaces the kubelet's access with an
`ipBlock` over "the node CIDR", sourced from a new values key `networkPolicy.clusterCidrs`. The spec
does not say what that key defaults to. If it is unset or wrong, the kubelet's readiness probe to
`/health` on 8081 is denied, pods never become Ready, and the deployment fails — including the
landing page this spec exists to ship.

This is not a hypothetical gap in an otherwise-portable mechanism. The chart already tried to scope a
non-pod source by address and rejected it in writing. `templates/networkpolicies.yaml:154-166`
documents the Kubernetes API server case: *"No portable way to scope this tighter: the API server
isn't a normal in-namespace pod, and its address/CIDR differs across kind/EKS/AKS/GKE (managed
control planes aren't even in-cluster pods), so this can't be a podSelector/namespaceSelector rule."*
The resolution adopted there was a port-scoped `to: []` — structurally the same allow-all that 4f is
removing on the ingress side. The kubelet has the identical property: its source address is the node
IP, which differs across kind (the Docker network), EKS (the VPC), AKS and GKE.

There is also no existing CIDR anywhere in the chart to inherit a default from: `grep -rn
"ipBlock\|cidr\|CIDR"` across `deploy/helm/iverson/` returns only that comment. `values.yaml:13-14`
defines `networkPolicy` with a single key, `enabled`.

**Evidence.**
- `docs/specs/…-design.md`, Design 4f: the four-consumer table specifies `ipBlock` over the node CIDR
  and the VPC CIDR, sourced from `networkPolicy.clusterCidrs`, with no default stated.
- `templates/networkpolicies.yaml:154-166` — the chart's own documented rejection of CIDR-based
  scoping as non-portable across exactly the platforms this chart targets.
- `templates/networkpolicies.yaml:22-33` — the current api-ingress rules; `from: []` on 8081 is what
  readiness depends on today.
- `values.yaml:13-14` — `networkPolicy` has only `enabled`; `clusterCidrs` is wholly new.
- `grep -rn "ipBlock" deploy/helm/iverson/` — no existing use.

**What 4f gets right, and should keep.** The consumer enumeration is correct and complete, and the
finding it rests on is real: checking it confirmed that `networkpolicies.yaml:88-103` gives *worker*
an explicit `podSelector{prometheus} → 8081` rule while the api policy has none, so Prometheus's
access to `api:8081` today is granted solely by `from: []`. Any replacement must add that api-side
Prometheus rule or Band B goes empty. That half of 4f is sound and is not in question here.

**Proposed fix.** The determinate parts should land as specified — the ingress-nginx rule and the
api-side Prometheus rule are both expressible as selectors and both are needed. The kubelet arm is
the part that has no portable expression, and the choice between the available treatments is
surfaced as §3.2. Whatever is chosen, the spec must state the behaviour when the value is absent,
because the failure mode is silent non-readiness rather than a template error.

## 3. Forced decisions

### 3.1 — How the AWS profile reaches port 8081 without a path rewrite

**The choice.** ALB cannot rewrite request paths, so the `/admin-api` prefix arrangement cannot work
unchanged on the AWS profile (§2.1). The spec must pick how AWS is served.

**Why it's forced.** The controller is fixed by the deployment
(`operators/main.tf:113-115` installs `aws-load-balancer-controller`), and the gRPC path segment is
fixed by the proto contract, so the prefix cannot be absorbed into the service path. The design
cannot both keep a single path-prefix mechanism and support AWS.

**The options.**
- **(a) Separate by host on AWS.** A second Ingress on `api.<ingressHost>` (or similar) routing `/`
  to 8081 with no rewrite. The console's base becomes a host rather than a path prefix on that
  profile. Costs a second hostname and certificate SAN; keeps one code path in the console if the
  base is already configuration.
- **(b) Keep the path prefix and make the API serve it.** Add a path-base so the API answers
  `/admin-api/iverson.<Service>/<Method>` natively, making the rewrite unnecessary on every profile.
  Costs a server-side routing change and a divergence from the bare gRPC paths the SDK clients use.
- **(c) Declare the console nginx-only for now.** Scope the landing page to the kind/local profiles
  and record AWS as unsupported until the transport is revisited. Costs nothing now; leaves the
  AWS deployment without a console.

### 3.2 — How the kubelet reaches port 8081 once `from: []` is removed

**The choice.** The kubelet's source address cannot be expressed as a pod or namespace selector, and
the chart has already rejected CIDR-based scoping as non-portable for the structurally identical API
server case (§2.3). The spec must pick a treatment.

**Why it's forced.** NetworkPolicy offers exactly three source forms — `podSelector`,
`namespaceSelector`, `ipBlock` — and the kubelet is reachable by none of the first two. The third
requires an environment-specific value the chart cannot derive, on a rule whose failure mode is pods
never becoming Ready.

**The options.**
- **(a) Ship `clusterCidrs` with per-profile defaults.** `values-local.yaml` carries the kind node
  network, `values-aws.yaml` the VPC CIDR, and the template fails the render loudly when the list is
  empty rather than producing a rule that silently denies. Costs a value operators must get right per
  environment, and contradicts the precedent at `networkpolicies.yaml:154-166`.
- **(b) Split liveness onto its own port.** Bind `/health` and `/health/live` to a dedicated Kestrel
  listener and keep a port-scoped allow-all on *that* port only. The allow-all then grants access to
  nothing but health, which is the outcome the finding wants, and it composes with 5f's listener
  binding. Costs a third listener and a probe-port change in the chart.
- **(c) Keep the port-scoped allow-all on 8081 and accept the exposure.** Matches the chart's own
  documented precedent for unexpressible sources. Costs leaving the finding open — with 4e landed,
  what an in-cluster caller reaches on 8081 is `/metrics`, `/health`, and authenticated routes only.

## 4. Previously addressed

- **Round 6 §2.1 (Qdrant `vectors_count`)** — resolved. Design 1 and Design 2's Band C row both read
  points and indexed vectors; no bare "vectors count" remains.
- **Round 6 span check (A37/A38)** — resolved. Both covering rows are present, and A38 has been
  restated to describe the allowlist rather than the replaced catch-all.
- **Round 5's transport resolution (`/admin-api` over `/grpcweb`, and the not-`/admin` argument)** —
  still holds on the nginx profiles; §2.1 concerns the AWS controller only, which no prior round
  examined.
- **Round 4 §2.1 (grpc-web and native gRPC share URL paths)** — still resolved; the prefix continues
  to separate the two consumers, and the allowlist narrows rather than widens that separation.
- **Round 4 §2.2 (IngressGroup rule ordering)** — the `group.order` instruction remains necessary and
  is unaffected by §2.1, which is about rewriting rather than ordering.

## 5. Recommendation

🛑 **Surface forced decisions to user**

Two forced decisions (§3.1, §3.2), both arising from §2 findings that would break the design as
written. §2.2 has a determinate fix and can be applied without user input.

The three findings share a shape worth naming: each is a dependency that was verified on one profile
and assumed on the other. The rewrite was traced under ingress-nginx and assumed for ALB; the CSP
origin was specified as a literal and assumed to be knowable at image-build time; the kubelet rule
was designed for a cluster whose node CIDR the chart was assumed to know. The spec's own
`networkpolicies.yaml:154-166` had already recorded the general form of that lesson for the API
server, one file away from where 4f reintroduced it.
