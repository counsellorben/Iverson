# Critical Implementation Review: 2026-08-25-admin-console-landing-page-implementation-plan (Round 1)

**Plan:** `/home/ben/repositories/Iverson/docs/plans/2026-08-25-admin-console-landing-page-implementation-plan.md`
**Verified plan-level assumptions section:** present (22 rows)

⚠️ 1 commit since plan-write time (SHA `e271e77`); cited file:line references re-checked under §1. The commit is the plan's own (`1db76ab`), so no source drift.

No prior CIR reviews exist for this plan basename — §4 omitted. (The ten `…-design-critical-review-*.md` files in this directory are CDR output against the spec, a different basename.)

## 0. Coverage enumeration

### Tasks × surfaces

| Task / surface | Disposition |
|---|---|
| T1 prose — scope change, drop of `profile`/`email` | `ok` — matches spec 4d-1; the reasoning that both mappings are unbound to this provider is inherited, not re-derived |
| T1 code block — the `operators-group.yaml` blueprint | `ok` — schema checked against the repo's own: `blueprints/compose-only/service-clients.yaml:280-283` and `:297-300` use exactly `model: authentik_core.group` / `identifiers: name:` / `attrs: {}` |
| T1 prose — compose membership pattern | `ok` — the file's pattern is `groups: - !Find [authentik_core.group, [name, …]]` at `:295-296`; the plan points at the right precedent |
| T1 prose — top-level placement rationale | `ok` — `blueprints-configmap.yaml:6` globs `blueprints/*.yaml` (top level only) and `docker-compose.yml:294,328` bind-mount the directory; a top-level file genuinely covers both |
| T1 commands / commit | `ok` — paths exist; message matches the lowercase-imperative convention |
| T2 prose — probe gating | `ok` — four endpoints at `Program.cs:336-359`; no caller or test depends on them |
| **T2 prose — the `/health` cache and its stated bound** | `→ §2.1` |
| T2 commands | `ok` — `dotnet test Iverson.Server/Iverson.Api.Tests` is a real project path |
| T3 prose — four-consumer rule set on 8081, 8080 gap, Prometheus both directions | `ok` — the api policy genuinely has no Prometheus rule (only `worker-ingress` at `:88-103` does); the `{{- if .Values.prometheus.enabled }}` guard at `:474` exists |
| **T3 prose — "`required` … render must fail loudly on an empty list"** | `→ §2.2` |
| T3 commands — five-overlay helm lint loop | `ok` — matches `.github/workflows/deploy-validate.yml:28-33` verbatim |
| T4 prose — hostname, three Prefix paths, no rewrite | `ok` — mirrors `charts/authentik/templates/ingress.yaml:21`'s `printf` pattern; `Prefix` semantics re-confirmed |
| T4 prose — per-profile class/annotations/TLS, new Secret on azure/gcp | `ok` — `values-azure.yaml:78,130,141` and `values-gcp.yaml:79,131,143` confirm one Secret per Ingress per host |
| T4 prose — CORS placement before `UseAuthentication` | `ok` — preflight `OPTIONS` carries no `Authorization`, so ordering ahead of authentication is what stops the `FallbackPolicy` rejecting it. `UseHttpsRedirection` at `:280` is a no-op here (Kestrel declares only http endpoints at `appsettings.json:9-17`), so it cannot 307 a preflight |
| T4 prose — compose 8081, `.env.development` 8080→8081, hosts line | `ok` — `docker compose port iverson-api 8081` returns nothing today; `.env.development:3` names 8080 |
| T5 prose — both extractions, parameter-passing model | `ok` — `ObjectSearchGrpcService.cs:30-40` confirms the dependency set; `SchemaCatalogReader`'s inputs are `SchemaRegistry` + `IRowFieldAuthorizationEvaluator` + principal |
| T5 prose — behaviour-preservation gate | `ok` — and it is load-bearing: `Iverson.ClientConformance/Scenarios/SchemaCatalogScenario.cs`, both `Api.Tests` suites and `Iverson.LoadTest` exercise these paths |
| T6 prose — Qdrant read interface, `RequestHeaders.Use("api-key", …)` | `ok` — `IntelligenceCollectionManager.cs:9-16` confirms both the `QdrantClient` injection and the header requirement |
| T6 prose — the two authenticated rows must not be normalised | `ok` — `ObjectMappingGrpcService.cs:61-62` and `Program.cs:443` confirm the model being matched |
| T7 prose — fixed result set, mangled names, `http_route` exclusions | `ok` — the unit-suffix rule and the unfiltered metrics provider at `Program.cs:66-71` both re-checked |
| T7 prose — absent-Prometheus handling | `ok` — `values-laptop.yaml` does set `prometheus.enabled: false` |
| T8 prose — headless Services additive, `dns_sd_configs`, local worker target | `ok` — `charts/worker/templates/` already holds a ClusterIP `service.yaml`, so "alongside" is correct; `prometheus.local.yml` scrapes only the api today |
| T9 prose — fetch layer, 401 routing, hook semantics | `ok` — matches Design 3; the token-as-argument rationale holds (`useAuth()` is context-only) |
| T10 prose — index replacement, three-state starrocks, no Ollama tile, data-volume caveats | `ok` — all trace to Design 2's constraints |
| T10 prose — **the `router.test.tsx` update** | `ok` — the plan names it as required; `src/router.test.tsx:32,49` does assert `/performance` |
| T11 prose — transport-health labelling, embedding conflation | `ok` — both trace to Design 2 |
| T12 prose — four fixes, both halves of 5b | `ok` — the error-path argument matches `CallbackPage.tsx:15-19` |
| T13 prose — validation regex, CSP at container start | `ok` — see the dropped row below for the writability check |
| T14 prose — `Connection.LocalPort`, not `RequireHost` | `ok` — reasoning re-checked; `/health` on 8081 still serves both the kubelet and the console's ingress path, so the binding breaks neither |
| T14 Step 2 — the non-code ALB verification | `ok` — correctly marked as requiring a deployed environment, with instructions to leave it unchecked and report |

### Cross-task interface contracts

| Contract | Disposition |
|---|---|
| T1 produces the `operators` group → T6/T7's Operator rows, T10's tenant roster | `ok` — runtime dependency, correctly stated as such; no build coupling |
| T1 writes `oidcConfig.scope` → T12 adds two settings to the same object | `ok` — T12's Interfaces block names the constraint explicitly |
| T3 produces the api→prometheus egress rule → T7 consumes at runtime | `ok` — plan assumption 17 correctly classifies this as runtime, not build |
| T3 produces the 8081 ingress rule → T4's Ingress needs it | `ok` — ordering is T3 before T4 |
| T4 produces `apiBaseUrl` → T9's fetch layer, T12's 5g, T13's CSP origin | `ok` — all three consuming tasks are ordered after T4 and name it |
| T5 produces two readers → T6 consumes | `ok` — parameter shape (`ClaimsPrincipal?`) is stated on both sides |
| T6/T7 produce five endpoints → T10/T11's widgets | `ok` — each of the nine widgets maps to exactly one endpoint |
| T9 produces `usePolledResource` → T10, T11 | `ok` — hook signature stated once, consumed twice |
| T10 modifies `router.tsx` index → T12 adds guards to sibling routes in the same file | `ok` — T12's Interfaces block says to leave the index alone |
| **T5's aggregate reader "reports denial distinctly" → T5 Step 3 requires no observable gRPC change** | `dropped` — candidate generated. An implementer could read "reports denial distinctly" as licence to throw, which would break `Aggregate`'s empty-on-denial contract and its conformance scenario. But Step 4 mandates those suites pass **unchanged**, which catches it at execution. No demonstrable escape |

### Rule-like content, both directions

| Rule | Disposition |
|---|---|
| T3's `clusterCidrs` → `ipBlock` derivation, under-inclusion (value absent) | `→ §2.2` — nil fails, but empty list does not |
| T3's `clusterCidrs` → `ipBlock` derivation, over-inclusion (CIDR too broad) | `dropped` — the azure row uses the VNet `/16` where the node subnet is `/20`; a superset still admits the kubelet and remains far narrower than the `from: []` it replaces. No failure |
| T7's metric-name mangling, both directions | `ok` — unit suffix applied only to the two duration instruments, which is the direction round 5 of CDR corrected; the five unit-less instruments mangle straight |
| T7's `http_route` exclusion predicate — every producer of counted traffic | `ok` — enumerated: kubelet liveness/readiness, load-balancer checks, and Prometheus's own scrape of `MapPrometheusScrapingEndpoint` (`Program.cs:275`), plus the console's own 60s `/health` poll. All four are excluded by route |
| T12's `ignoreUrls` regex built from an unescaped URL | `dropped` — unescaped `.` over-matches by one character class; it still matches the real URL and the over-match is unreachable in practice. No failure |
| T13's `^[A-Za-z0-9:/._-]+$` validation, both directions | `ok` — tested against every value the repo ships: the dev client id, both localhost URLs, and `values-aws.yaml:142-143`'s two URLs all pass; a `"` or `;` is rejected |
| T13's entrypoint writing an nginx snippet as uid 101 | `dropped` — candidate generated and **refuted empirically**. `docker run --user 101` on the runtime image shows `/etc/nginx/conf.d` is `drwxrwxr-x nginx root` and a `touch` there succeeds. (`/usr/share/nginx/html` is `root:root 755` in the base image, which is exactly why `Dockerfile:20` chowns it — the asymmetry is real but does not affect `conf.d`) |
| T10's health-strip cadence — 60s vs Design 2's stale "every 10s" | `dropped` — the plan cites Design 3's cadence table, which is the section that owns cadence and argues explicitly for 60s over 10s. The plan picked correctly; the spec's stale sentence is a spec concern, and the plan-write handoff already surfaced it |

## 1. Verified-plan-assumptions cross-check

Fresh read of each cited evidence reference. **All 22 still hold.** Spot-detail on the ones a fresh read could have moved:

- **4** — `Iverson.Sql/IRecordStoreRoles.cs:125` still declares `Task<IEnumerable<TenantRow>> ListAsync()`.
- **5** — `ObjectSearchGrpcService.cs:30-40` still injects `IEngagementStoreSearchService search` alongside `SchemaRegistry`, `IActingUserAccessor` and `IRowFieldAuthorizationEvaluator`.
- **6** — `IntelligenceCollectionManager.cs:9-16` still takes `QdrantClient client` and still wraps calls in `RequestHeaders.Use("api-key", apiKey)`.
- **7** — `TenantStatusCache.cs:6-24` still shows the primary-constructor + `private static readonly TimeSpan Ttl` + `TryGetValue`/`Set` shape. **The assumption is true as written**; what it does not state is the concurrency behaviour of that shape — see §2.1 and the span check.
- **10** — `deploy-validate.yml:28-33` still loops exactly `values-local values-laptop values-aws values-azure values-gcp`.
- **12** — `router.test.tsx:32,49` still asserts `expect(window.location.pathname).toBe("/performance")`.
- **13** — the caller set is unchanged across `ClientConformance`, both `Api.Tests` suites and `LoadTest`.
- **21, 22** — `charts/worker/templates/` still holds `deployment.yaml hpa.yaml service.yaml`; `docs/runbooks/grpc-admin-auth-cutover.md` still present.

### Span check — uncovered dependencies

Two facts tasks depend on that no listed assumption covers, and that the "Inherited from spec" list does not state either. Both were checked in-round; both became §2 findings.

1. **That the cited `IMemoryCache` pattern bounds work under concurrency.** Assumption 7 verifies the *shape* of `TenantStatusCache`. Nothing states whether that shape single-flights, which is what T2's stated bound requires. → §2.1
2. **That Helm's `required` fails on an empty list.** T3 Step 4 rests on it entirely; no assumption covers Helm's `required` semantics, and the spec's 4f asserts the behaviour without evidence. → §2.2

## 2. Literal-wrongness findings

### 2.1 — The cited cache pattern does not single-flight, so T2's stated bound is not achieved (dynamic)

**Description.** T2 Step 2 instructs the implementer to memoize `/health`'s four-way fan-out "following `Tenancy/TenantStatusCache.cs`'s pattern — a `private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5)`, `TryGetValue` then `Set`." The stated outcome, in the plan and in spec 4e, is that this bounds the Qdrant `EnsureCollectionAsync` and the Kafka `ProduceAsync` "to at most one per 5s per pod **regardless of request rate**", where "today that rate is unbounded; that is the whole of the change."

That pattern is a classic cache-stampede shape and provides no such bound. `TenantStatusCache` holds no lock, no semaphore, no `Lazy<Task<T>>` — `grep -nE "lock|Semaphore|GetOrCreate|Lazy"` over the file returns nothing. On a miss, every concurrent caller independently falls through to the work and every one of them writes the cache. `IMemoryCache.GetOrCreateAsync` would not fix it either; it is not atomic.

The consequence is specific to this endpoint. `/health` is `AllowAnonymous` and, under this plan, is routed to the public `admin-api` host through the `/health` Prefix path. N concurrent anonymous requests arriving at or after expiry produce N fan-outs — N Postgres queries, N StarRocks checks, N Qdrant collection-ensures and **N Kafka produces**. The amplification the cache exists to remove survives at exactly the moment it matters, and a caller who wants to drive it need only send requests concurrently rather than serially.

The pattern is correct where it is used today: `TenantStatusCache` guards a single indexed read, so a stampede costs redundant SELECTs and nothing else. Copying it to guard a write-bearing four-way fan-out is where it stops holding.

**Evidence.**
- `Iverson.Server/Iverson.Api/Tenancy/TenantStatusCache.cs:12-23` — `TryGetValue` → work → `Set`, no synchronisation of any kind.
- `Iverson.Server/Iverson.Api/Program.cs:308-313` — the four operations, two of which are writes.
- Plan, T2 Step 2 — "bounding … to at most one per 5s per pod regardless of request rate", and the instruction to follow `TenantStatusCache`'s pattern.
- Plan, Design 1 Transport (inherited) — `/health` is one of the three Prefix paths on the public host.

**Proposed fix.** Make the fan-out single-flight rather than merely memoized: cache a `Lazy<Task<HealthChecks>>` (or a `Task<HealthChecks>`) rather than the resolved value, so concurrent callers on a miss await the same in-flight operation and only one fan-out runs per window. `cache.GetOrCreate(key, entry => { entry.AbsoluteExpirationRelativeToNow = Ttl; return new Lazy<Task<HealthChecks>>(FanOutAsync); }).Value` is the smallest form. Amend Step 2 to say so explicitly and to state that `TenantStatusCache`'s shape is being deliberately departed from, so the next reader does not "correct" it back.

### 2.2 — Helm's `required` does not fail on an empty list, and the rule it guards renders as allow-all (static, with a runtime consequence)

**Description.** T3 Step 4 says: "Make the key required and supply it for all five profiles. Render must fail loudly on an empty list rather than emit an `ipBlock` matching nothing." The spec's 4f states the same mechanism — "`required` in Helm, not a rule that silently denies."

Helm's `required` fails on `nil` and on an empty **string**. It does not fail on an empty **list**. Verified directly against helm v3.16.4:

```
$ cat values.yaml
cidrs: []
$ cat templates/t.yaml
x: {{ required "clusterCidrs must be set" .Values.cidrs }}
$ helm template ./c
x: []          # exit 0 — no failure
```

So `clusterCidrs: []` in any overlay passes the guard the plan relies on. What renders then is worse than the "ipBlock matching nothing" the plan anticipates: a NetworkPolicy ingress rule whose `from` list is empty **matches all sources**. That is precisely the `from: []` semantics that 4f exists to remove — the guard's failure silently reinstates the defect it was written to prevent, on whichever profile carries the empty list.

The narrow reading is that this only bites if someone writes `clusterCidrs: []`. But the plan makes `required` the reason it is safe to depend on the key being present, and that reason is false; a key omitted entirely does fail, so the guard appears to work in the case people test and fails in the case they do not.

**Evidence.**
- `helm template` run above — exit 0, `x: []` rendered, helm v3.16.4+g7877b45.
- Plan, T3 Step 4 — the "fail loudly on an empty list" claim.
- Spec 4f — "`required` in Helm, not a rule that silently denies" (inherited claim, same defect).
- Kubernetes NetworkPolicy semantics — an ingress rule with an empty `from` matches all sources; this is the same reading 4f applies to the existing `:28-33` rule.

**Proposed fix.** Guard on emptiness explicitly rather than through `required`:

```
{{- if not .Values.networkPolicy.clusterCidrs }}
{{- fail "networkPolicy.clusterCidrs must list at least one CIDR; an empty list renders an allow-all ingress rule" }}
{{- end }}
```

`fail` aborts the render unconditionally, and `not` is true for an empty list as well as for nil. Amend T3 Step 4 to specify this form, and add a step asserting it — render one overlay with `--set networkPolicy.clusterCidrs=null` and with an empty list, and confirm both abort.

## 3. Forced decisions

No forced decisions found.

T14 Step 2's ALB verification was considered and is not one: the plan already names it as non-code, states it requires a deployed AWS environment, and instructs the executor to leave it unchecked and report rather than guess. That is a handled constraint, not an unpicked choice.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**

Two §2 findings, no forced decisions, all 22 verified plan-level assumptions reconfirmed.

Both findings are the same species and neither is visible from the static surface `thorough-writing-plans` already verified: a mechanism the plan cites by name does not have the property the plan needs from it. Assumption 7 correctly verified what `TenantStatusCache` *looks like*; the gap was whether that shape single-flights. The `required` claim was inherited from a spec that asserted Helm's behaviour without testing it, and it survived ten CDR rounds because nothing in that pipeline runs `helm template`.

Both fixes are small and local — one cached type change in T2, one `fail` guard in T3 — and neither disturbs task ordering or the plan's shape. Two candidates were generated and dropped on evidence rather than carried: the entrypoint's write permission on `/etc/nginx/conf.d` was refuted by running the image, and the aggregate reader's denial-signalling ambiguity is caught by T5's own behaviour-preservation gate.
