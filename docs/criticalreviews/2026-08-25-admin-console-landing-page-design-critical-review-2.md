# Critical Design Review: 2026-08-25-admin-console-landing-page-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-25-admin-console-landing-page-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

Enumeration built against the current spec before consulting round 1. The surfaces round 1
changed (Design 1's NetworkPolicy bullet, Design 2's new RPC-health paragraph, the rewritten
Design 4b, and rows A22/A30-A32) are four rows here, not the search area; the rest of the spec
is swept at the same depth.

### Sections

| Row | Disposition |
|---|---|
| Problem | `ok` — re-read; all three claims still resolve (`router.tsx` index redirect, four `Coming soon` pages, the metric/probe inventory). |
| Scope — in-scope list | `ok` — four bullets, each resolving to a Design section that exists. |
| Scope — out-of-scope list | `→ §2.1` — the read-only claim is contradicted by the design's own health-strip cadence. |
| Design 1 — minimal-API pattern paragraph | `→ §3.1` — still asserts the pattern and still names no port; round 1's §2.3 was left unapplied by design decision, and new evidence changes what the remedy can be. |
| Design 1 — `/admin/metrics` bullets (incl. the new NetworkPolicy bullet) | `ok` on the new bullet — re-read `networkpolicies.yaml`: default-deny at `:7-10`, `api-egress` at `:38-63` with no Prometheus rule, `prometheus-egress` at `:479-490`, and no `prometheus-ingress` object. The `{{- if .Values.prometheus.enabled }}` guard the bullet reuses is really at `:474`. Endpoint **path** `→ §3.1`. |
| Design 1 — `/admin/stores/qdrant` | `ok` on capability — `IntelligenceCollectionManager.cs:16,43` injects the admin api-key header, so cross-tenant enumeration works. Endpoint **path** `→ §3.1`. |
| Design 1 — Authorization | `ok` — `Program.cs:141-156` unchanged; `Operator` remains the right peer for `/reconcile` (`:373`) and `/admin/dlq` (`:380`). |
| Design 1 — Explicitly not included | `ok` — nothing load-bearing. |
| Design 2 — Band A / B / C tables | `ok` — every named source re-resolved: `/health` (`Program.cs:301`), `tenant_lifecycle.proto:9`, `object_mapping.proto:15`, `object_search.proto:13`, the three gauges, the two counters. |
| Design 2 — health-strip constraints | `ok` on the tri-state and Ollama-absence claims (`Program.cs:318-323`). The **side effects** of polling it `→ §2.1`. |
| Design 2 — data-volume constraints | `ok` — the zero-aggregations throw (`:490-494`), `Total` never assigned (`:514`), denial returning empty (`:501`) all re-read and unchanged. |
| Design 2 — RPC-health constraints (incl. the new paragraph) | `ok` — re-read `Program.cs:56-59` (tracing filter) against `:66-71` (unfiltered metrics). The new paragraph's prescription and its stated reason for filtering in PromQL are both accurate. |
| Design 2 — embedding-latency constraint | `ok` — two named clients at `ServiceCollectionExtensions.cs:12,29`; conflation caveat accurate. |
| Design 3 — cadence table | `→ §2.1` — the 10s health-strip row is the operand that makes the read-only claim false. |
| Design 3 — failure and degradation | `ok` — per-card isolation, stale-with-timestamp, backoff, hidden-tab pause. The 401 arm is checked in the dropped-candidates table below. |
| Design 3 — implementation shape | `ok` — `auth.user?.access_token` present; the TanStack rejection is reasoned. |
| Design 3 — Testing | `ok` — vitest 3.2 and three existing test files. Note the CI half is `→ §2.2`. |
| Design 4a — grpc-web enablement | `ok` — `Program.cs:438-445` unchanged; `UseGrpcWeb()` at `:284`. |
| Design 4b — rewritten codegen prescription | `→ §2.2` — the build-prerequisite half is actionable; the CI half names a surface that does not exist. |
| Design 4c — scrape targets (cloud) | `ok` — ClusterIP VIP + 2-5 HPA re-confirmed; DNS discovery survives `prometheus-egress`, which selects api/worker by **podSelector** (`:487-490`) so per-pod IPs stay allowed, and DNS itself is allowed at `:485-486`. |
| Design 4c — scrape targets (local) | `ok` — **checked the target exists this round.** `docker-compose.yml:438` defines an `iverson-worker` service with `WORKLOAD_ROLE=worker` (`:454`), and `MapPrometheusScrapingEndpoint` (`Program.cs:275`) sits outside the `if (workloadRole == "api")` gate at `:438`, so the worker really does serve `/metrics` on 8081. The prescribed target resolves. |
| Design 4d-1 — scope change | `ok` — **and the ID-token half was verified empirically this round**, which round 1 did not do. See §1's span check. |
| Design 4d-2 — operators group blueprint | `ok` — `blueprints-configmap.yaml:6` globs `blueprints/*.yaml`; `docker-compose.yml:294,328` mounts the directory. `authentik_core.group` with `identifiers: name:` matches the pattern the existing blueprint uses for `tenant-admins`. |
| Design 4d-3 — membership | `ok` — the `groups:` attribute pattern exists on `iverson-loadtest-bypass-user`. |
| Design 4d — "what this repairs" | `ok` — `Sidebar.tsx:20,25` and `AppLayout.tsx:9` re-read; the claim that both nav items are invisible today holds. |
| Verified assumptions (A1-A32) | See §1. |
| Known issues | `ok` — three entries, none re-raised here. |

### Rules and operands

| Row | Disposition |
|---|---|
| Health-strip state rule, both directions | `ok` — no fourth state in the `checks` object; the structurally-odd operand (`starrocks`, tri-state) is the one the spec tests. |
| Data-volume count rule, both directions | `ok` — over-inclusion (denied → zero) stated by the spec; under-inclusion bounded by `GetSchema`'s own type list, the same source the widget enumerates. |
| RPC-health metric selection, both directions | `ok` this round — the new paragraph closes the over-inclusion direction by `http_route`; under-inclusion is not possible, since excluding named routes cannot drop real RPC traffic. |
| Embedding-latency selection, both directions | `ok` — over-inclusion stated; `server.address` is present on every HttpClient metric so nothing is silently missed. |
| Identity rule: `operators` group name | `ok` — one name, two consumers (`OperatorAuthorizationPolicy.cs:11`, `Sidebar.tsx:20`), no near-name collision in the live directory. |
| Identity rule: endpoint **path** namespace `/admin/*` | `→ §3.1` — **checked this round for the first time.** The paths collide with an existing ingress path pattern; this is an over-merge of two distinct routing targets under one prefix. |
| Eligibility predicate: which processes emit Band B metrics | `ok` — every producer enumerated: the six hosted services are all inside `if (workloadRole == "worker")` (`Program.cs:254-264`). No producer outside that gate. |
| Eligibility predicate: which collections `/admin/stores/qdrant` enumerates | `ok` — `ListCollectionsAsync` returns every collection including the `iverson-probe` one `/health` creates (`Program.cs:310`). The widget shows one row per collection, which is what the spec says it shows; no predicate silently drops or invents rows. |

### Data-flow arrows

| Row | Disposition |
|---|---|
| browser → `/admin/metrics` (crosses browser↔ingress boundary) | `→ §3.1` — the path's routing target is not the API. |
| browser → `/admin/stores/qdrant` (same boundary) | `→ §3.1` — same. |
| browser → `/health` every 10s → Kafka produce + Qdrant ensure | `→ §2.1` — **the consuming operations at the far end of this arrow are writes.** |
| api pod → Prometheus `:9090` | `ok` this round — the requirement is now stated in Design 1 with both policy additions named. |
| Prometheus → api/worker `:8081` per-pod | `ok` — podSelector-based egress survives the VIP→pod-IP move; compose target verified to exist. |
| ID token → `Sidebar`'s `auth.user?.profile?.groups` (crosses token-serialization boundary) | `ok` — **dumped a real token's key set rather than reasoning from the access token.** See §1. |
| `operators-group.yaml` → Authentik blueprint loader | `ok` — Helm glob and compose mount both reach a top-level file; recursion proven by `compose-only/` applying today. |
| `npm run build` → generated client → widget imports | `ok` on the local leg (`package.json` has `build`, `test`, `generate`); `→ §2.2` on the CI leg. |

### Candidates generated and dropped

| Candidate | Why dropped |
|---|---|
| No `refresh_token` is issued, so Design 3's "route 401 to the oidc-client-ts renewal path" may not work | **New evidence this round** — a real token response for the console's client carries only `access_token`, `id_token`, `expires_in`, `scope`, `token_type`; the compose `iverson-oidc-default` provider binds no `offline_access` mapping. Round 1 dropped this claiming "refresh works empirically", which was wrong. But it still fails literal-wrongness: without a refresh token `automaticSilentRenew` falls back to iframe silent-renew against a registered redirect_uri, and if that also fails `AuthGate` already redirects to login. The degradation is benign re-authentication, not the nine-error-cards outcome the design set out to avoid. |
| The Qdrant widget will show the `iverson-probe` collection alongside real ones | Cosmetic. The spec says one row per collection and that is what it gets. |
| Design 4d-1 drops `profile`/`email`, so the AppBar keeps showing "User" | The spec states this outcome explicitly and declines to fix it with a reason. Not a defect. |
| `/health`'s Kafka probe topic has no consumer, so messages accumulate | Pre-existing and unchanged by this design; the retention question is not the spec's outcome. |
| The headless Services in 4c need names the spec doesn't give | Implementation detail. `critical-implementation-review`'s surface. |

## 1. Verified-assumptions cross-check

A1-A21 and A23-A29 reconfirmed under a fresh read; citations resolve to the lines they name.
A22 and A30-A32 are new or corrected since round 1 and were checked independently rather than
taken from the update:

- **A22** (now "Failed") — re-verified: `grep -rn apiBaseUrl Iverson.AdminUI/src/` still returns
  only `config.ts:16`; `appsettings.json:12-13` still binds 8080 to `Protocols: Http2`.
- **A30** (Prometheus reachability, "Failed") — re-verified against `networkpolicies.yaml`;
  `prometheus-ingress` still does not exist.
- **A31** (metric contents, "Failed") — re-verified at `Program.cs:56-59` vs `:66-71`.
- **A32** (codegen, "Failed") — re-verified: `Iverson.AdminUI/.gitignore:3` still ignores
  `generated/`.

**Span check — dependencies introduced or changed since round 1 with no covering assumption:**

1. *"The `groups` claim reaches `auth.user?.profile`."* Design 4d claims the scope change repairs
   `Sidebar.tsx:20`. `oidc-client-ts` populates `user.profile` from the **ID token**, not the
   access token — and round 1's verification only ever decoded an access token, so nothing
   covered this. **Verified in-round:** a real token response for
   `dev-iverson-human-oidc-client-id` with scope `openid groups tenant_id offline_access` carries
   an `id_token` whose claims include `groups: ["iverson-loadtest-bypass"]` and
   `tenant_id: "tenant_bypass"`. The dependency holds; Design 4d's repair claim is sound.
   Closes clean.
2. *"An `iverson-worker` scrape target exists in docker-compose."* Design 4c prescribes adding it;
   nothing covered whether the host resolves. **Verified in-round:** `docker-compose.yml:438`
   with `WORKLOAD_ROLE=worker` at `:454`. Closes clean.
3. *"There is a CI surface for the console's codegen step."* Introduced by the rewritten Design 4b
   this round; no assumption covers it. **Verified in-round and FAILS** — see §2.2.

## 2. Literal-wrongness findings

### 2.1 — The page is not read-only; the health strip writes to Kafka and Qdrant every 10 seconds

**Description.** Scope states, under out-of-scope: *"Any write or mutation surface. Every widget
and both new endpoints are read-only."* The health-strip widget's source is `GET /health`, and
Design 3's cadence table polls it on a **10s** timer. `/health` is not a read: each call produces
a Kafka message and issues a Qdrant collection-ensure. The design therefore turns every open
console tab into a sustained writer — six Kafka produces and six Qdrant ensure calls per minute,
per tab — and the spec's read-only claim is false as written.

**Evidence.**

- `Program.cs:310` — `vector.EnsureCollectionAsync("iverson-probe", 4)`.
- `Program.cs:311` — `kafka.ProduceAsync("iverson.health.probe", "probe", new { ts = DateTime.UtcNow })`.
- Design 3's cadence table — `| Health strip | 10s poll |`.
- Scope, out-of-scope bullet 3 — the read-only claim.

This compounds with the widget's own reporting: the health strip is the one widget whose
polling both writes to two stores and inflates the RPC-health metric beside it.

**Proposed fix.** Two parts, and the spec should say which it takes. Either drop the health-strip
cadence to something that matches a write-bearing probe (the kubelet already polls it; a console
tab adding six more writes a minute is the part that is hard to justify), or point the widget at
`/health/live` (`Program.cs:299`), which returns `{status:"alive"}` with no store access — at the
cost of losing the per-store `checks` object the widget's tiles are built from, which makes the
first option the likelier one. Whichever is chosen, the Scope bullet must stop claiming the page
performs no writes, or must state the exception explicitly.

### 2.2 — Design 4b's "CI gains the codegen step" names a CI surface that does not exist

**Description.** The rewritten Design 4b prescribes that proto generation be wired into the build
and that "CI gains the codegen step." There is no CI job that builds or tests `Iverson.AdminUI`
at all, so the second half of the prescription has nothing to attach to, and an implementer would
discover this only after picking up the task.

**Evidence.** `.github/workflows/` contains exactly two files, `codeql.yml` and
`deploy-validate.yml`. `grep -rn "AdminUI\|admin-ui\|npm \|node-version" .github/workflows/`
returns **no matches** — neither workflow installs Node, runs `npm`, or references the console.
The console has no build, lint, type-check or test job in CI today.

**Proposed fix.** Narrow the clause to what the design actually controls: making `generate` a
prerequisite of `build` and `test` is sufficient to guarantee the client is regenerated whenever
the console is built, and that is the drift guarantee the section claims. If CI enforcement is
wanted, creating an AdminUI CI job is separate, currently-unscoped work and the spec should say
so rather than implying a step can be added to a pipeline that does not exist.

## 3. Forced decisions

### 3.1 — Which transport the two new reads use, now that the relative-path remedy is ruled out

**The choice.** Round 1's §2.3 established that neither new endpoint is reachable from a browser;
that finding was deliberately left unapplied pending this round. The spec still carries Design 1's
minimal-API framing, names no port, and places both endpoints under `/admin/`. New evidence, not
cited in round 1, rules out the remedy round 1 proposed and makes the choice explicit.

**Why it's forced.** Three constraints, all verified, and they eliminate the obvious answer:

- **The endpoint paths collide with the console's own ingress.**
  `charts/admin-ui/templates/ingress.yaml:21` matches `/admin(/|$)(.*)` with
  `rewrite-target: /$2`; `charts/api/templates/ingress.yaml:18` matches `/`. Both are published on
  the same host (`values.yaml:22` `ingressHost: "iverson.local"`, `:121` the api ingress host).
  `/admin/metrics` and `/admin/stores/qdrant` match the admin-ui pattern, so a browser request for
  them is rewritten to `/metrics` and `/stores/qdrant` and served by the console's static file
  server — never reaching the API. Round 1's proposed relative-same-origin remedy assumed the
  opposite.
- **No ingress route to the HTTP/1.1 port exists.** Both Ingress backends target service port
  **8080**, which `appsettings.json:12-13` binds to `Protocols: Http2`; `:16-17` binds 8081 to
  `Protocols: Http1`. There is no path to 8081 in any profile.
- **There is no CORS configuration.** `grep -n "Cors\|UseCors" Program.cs` returns nothing, so any
  cross-origin remedy requires adding CORS as well.

The design cannot proceed without picking one, and the options differ in what they cost and in
what they reverse.

**The options.**

- **(a) Make the two reads gRPC.** Both become RPCs reached over the grpc-web transport Design 4b
  already builds for the other three widgets. No new ingress path, no port question, no CORS, no
  path-namespace collision — one transport for all five data widgets. Cost: proto changes, and
  Design 1's "the console needs plain JSON… grpc-web codegen would be ceremony" rationale is
  discarded — a rationale that was weaker once 4b committed to building the transport anyway.
- **(b) Keep REST and add an ingress route.** Rename the endpoints out of the `/admin/` namespace
  so they do not match the console's pattern, and add an api Ingress path routing those prefixes
  to service port 8081. Cost: a second backend port on the api Ingress with per-profile annotation
  care (AWS marks the existing path `backend-protocol-version: GRPC` at `values-aws.yaml:75`), plus
  a `vite.config.ts` dev proxy, which does not exist today.
- **(c) Keep REST but do not expose it to the browser.** Serve the two reads through an existing
  browser-reachable surface instead — for example having the console call them via a gRPC method
  that proxies internally. Cost: an extra hop and a second implementation of each read.

Not picking between these: (a) reverses a stated design decision, (b) changes deployment topology
across four profiles, and (c) trades transport simplicity for an internal indirection. The right
answer depends on how much weight the "plain JSON" rationale still carries, which is the spec
author's call.

## 4. Previously addressed

- **Round 1 §1 / A22** — the assumption is now recorded as Failed with all three grounds
  (unused in `src/`, h2c port, relative-path precedent).
- **Round 1 §1 span check** — all three uncovered dependencies now carry covering rows
  (A30, A31, A32), so they are recorded rather than re-derived.
- **Round 1 §2.1** — Design 1's `/admin/metrics` section now states both NetworkPolicy additions,
  names the `prometheus.enabled` guard, and notes why the missing direction is easy to overlook.
- **Round 1 §2.2** — Design 2 gained a paragraph specifying `http_route` exclusion in the proxy's
  PromQL, with the reason for filtering there rather than on the shared metrics provider.
- **Round 1 §3.1** — resolved to build-time generation; Design 4b now says `generated/` stays
  ignored and describes the protoc-path and prerequisite work. The CI half of that resolution is
  §2.2 above.

## 5. Recommendation

🛑 **Surface forced decisions to user** — §3 is non-empty.

§3.1 is the blocking item and is the same subject as round 1's §2.3, carried forward with new
evidence that eliminates the remedy round 1 proposed. It should be settled before planning: two
of the nine widgets depend on it, and option (a) would change Design 1's shape rather than just
its wiring.

§2's two findings are both bounded. 2.1 is a contradiction between the spec's own scope claim and
its own cadence table, and the fix is a sentence plus a cadence decision. 2.2 is a clause in a
section written during round 1's update that points at a pipeline the repo does not have.

Five candidates were generated and dropped with their reasons recorded in §0 rather than promoted.
The closest was the missing `refresh_token` — genuinely new evidence that corrects a wrong drop
rationale from round 1, but the degradation it causes is benign re-authentication rather than a
break in the asked-for behavior.
