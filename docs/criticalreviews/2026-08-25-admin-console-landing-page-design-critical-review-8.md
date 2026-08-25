# Critical Design Review: 2026-08-25-admin-console-landing-page-design (Round 8)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-25-admin-console-landing-page-design.md`
**Verified Assumptions section:** present

Round 8 is the first over the replaced transport: a dedicated `admin-api` hostname and five JSON
endpoints in place of the `/admin-api` path prefix and gRPC-Web. Design 1 is almost entirely new
text. The sweep was built before prior reviews were read.

## 0. Coverage enumeration

### Sections

| Section | Disposition |
|---|---|
| Problem | `ok` — unchanged; no claim touched by the transport replacement |
| Scope (in scope) | `ok` — the Design 4 bullet now names only scrape targets and identity, matching the deletion of 4a/4b; the eleven-findings bullet is unchanged and still accurate |
| Scope (out of scope) | `ok` — "no write or mutation surface except `/health`" still holds: all five endpoints are `GET` |
| Design 1 — opening | `ok` — the inverted rationale is internally consistent: it states the gRPC choice was a consequence of the shared host and that a dedicated host removes it |
| Design 1 — Transport, hostname | `ok` — `charts/authentik/templates/ingress.yaml:21` does render `printf "authentik.%s" .Values.global.ingressHost`, and `charts/api/templates/deployment.yaml:137` confirms the api subchart sees the global |
| Design 1 — Transport, three Prefix paths | `ok` on routing semantics — see rules rows |
| Design 1 — Transport, new Ingress object | `→ §2.3` |
| Design 1 — Transport, CORS | `ok` — `UseCors` before `UseAuthentication` is the correct order for preflight to bypass the `FallbackPolicy`; `AllowCredentials` correctly omitted for header-borne bearer tokens |
| Design 1 — Transport, `apiBaseUrl` | `ok` — `config.ts:16` reads it and nothing else does, so repurposing it breaks no consumer; `.env.development` 8080→8081 and the unpublished compose 8081 are both recorded |
| Design 1 — endpoint set, table | `→ §2.1`, `→ §2.2` |
| Design 1 — endpoint set, authorization model | `ok` — see rules rows; the two authenticated-only rows and the `HttpContext.User` decision are consistent with `RowFieldAuthorizationEvaluator.cs:14-15` and `:32-33` |
| Design 1 — endpoint set, extraction claim | `→ §2.2` |
| Design 1 — endpoint set, dependency removal | `ok` — `package.json` carries all four; `src/` imports none of them |
| Design 1 — `GET /admin/console/metrics` | `ok` — fixed result set preserved; "nine named values across four widgets" now matches Design 2's Band B (4 widgets) and the enumerated response contents (3 gauges + 2 counters + rate/error/p95 + Ollama p95 = 9); the Prometheus NetworkPolicy additions are correctly marked unaffected by the transport change |
| Design 1 — `GET /admin/console/qdrant` | `→ §2.1` — this section is where the contradiction is visible |
| Design 1 — Authorization | `ok` — three policies at `Program.cs:141-156` re-confirmed; `RequireAuthorization()` with no argument does yield the fallback |
| Design 1 — Explicitly not included | `ok` — unchanged |
| Design 2 — Bands A/B/C | `ok` — all four source rows now name endpoints; every one of the nine widgets maps to exactly one endpoint (see rules) |
| Design 2 — Constraints each widget carries | `ok` — the StarRocks three-state and no-Ollama-tile constraints are properties of `/health`'s body, unchanged by transport |
| Design 3 — Refresh cadence | `ok` — the 60s health-strip rationale still holds and 4e explicitly does not alter it |
| Design 3 — Failure and degradation | `ok` — per-widget degradation is what makes the two authenticated-only endpoints safe to render alongside Operator ones |
| Design 3 — Implementation shape | `ok` — the hook takes the access token because `useAuth()` is context-only; unchanged by moving from grpc-web to `fetch` |
| Design 3 — Testing | `ok` — unchanged |
| Design 4 — intro | `ok` — correctly reduced to (4c, 4d) and records why 4a/4b were deleted |
| Design 4c / 4d / 4e | `ok` — untouched by the transport change; 4c's scrape facts remain load-bearing for 4f and re-verified in round 7 |
| Design 4f | `ok` — naming updated to `admin-api` Ingress in both places; the four-consumer table and `clusterCidrs` defaults are unchanged and unaffected |
| Design 5 — 5a, 5b, 5d, 5e, 5f | `ok` — none depends on transport. 5f's `Connection.LocalPort` binding is unaffected: the `admin-api` Ingress targets 8081, which is where operational endpoints would be bound |
| Design 5 — 5c | `ok` — the CSP now names both the `admin-api` and Authentik origins and states `'self'` is insufficient, which is correct now the API is a separate origin |
| Design 5 — 5g | `ok` — `${apiBaseUrl}/v1/traces` is served by the `/v1/traces` Prefix path; `ignoreUrls` derives from the same constant so the self-trace guard follows |
| Verified assumptions | `ok` — A55-A64 appended, A38/A45/A52 marked moot with their original findings retained, preamble updated |
| Known issues | `ok` on the Operator blocker; the data-volume entry is re-examined under §2.2 |

### Rules and operands

| Rule | Disposition |
|---|---|
| `/admin` Prefix, over-inclusion | `ok` — enumerated every route under `/admin` in `Program.cs`: `/admin/reconcile/{typeName}` (`:361`), `/admin/dlq` (`:375`), `/admin/dlq/{id}/replay` (`:382`), all `RequireAuthorization("Operator")`, plus the five new `/admin/console/*`. Nothing anonymous is under the prefix |
| `/admin` Prefix, under-inclusion | `ok` — `Prefix` matches element-wise on `/`-split segments, so `/admin` catches `/admin/console/tenants` and `/admin/dlq` and does not catch `/administration` |
| `/health` and `/v1/traces` Prefix, both directions | `ok` — `/health` also matches `/health/live`, which is anonymous and returns no data; `/v1/traces` matches only itself |
| Eligibility predicate: what the three prefixes leave unrouted | `ok` — enumerated all seven `AllowAnonymous` producers (`Program.cs:275`, `:299`, `:334`, `:340`, `:346`, `:352`, `:359`). `/metrics` and the four `/probe/*` sit under none of the three prefixes and are unreachable on this host; `/health` and `/health/live` are deliberately routed |
| Recurrence — the full set of console→API calls | `ok` — five JSON endpoints, `/health`, and `/v1/traces`. OIDC discovery and token exchange go to the Authentik host, not the API. Every call is covered by exactly one prefix, and no call falls outside |
| Recurrence — all nine widgets to endpoints | `ok` — health strip→`/health`; roster→`/tenants`; catalog→`/schema`; volume→`/data-volume`; four Band B widgets→`/metrics`; Qdrant→`/qdrant`. Nine mapped, none unserved, none duplicated |
| Recurrence — authorization model of all six sources | `ok` — three `Operator` (matching `/admin/*`'s existing policy and `TenantLifecycle`'s mapping at `:444`), two authenticated-only (matching `GetSchema`'s no-`[Authorize]` at `:61-62` and `ObjectSearchGrpcService`'s bare mapping at `:443`), one anonymous (`/health`). Each matches the gRPC counterpart it projects |
| CORS preflight vs the `FallbackPolicy` | `ok` — `OPTIONS` preflight carries no `Authorization`, so ordering `UseCors` before `UseAuthentication` is what prevents the fallback from rejecting it; the spec states this ordering |
| `/admin` prefix newly exposes `/admin/dlq` and `/admin/reconcile` to browsers | `dropped` — candidate generated, failed literal-wrongness. Both are `Operator`-gated, so the asked-for behaviour is unaffected. The change in reachable surface is `critical-security-review`'s question, not this skill's |
| CORS origin differs per environment (`localhost:5173` in dev) | `dropped` — the spec says the origin is supplied by configuration, which is determinate; enumerating each environment's value is implementation detail |

### Data-flow arrows

| Arrow → operation | Disposition |
|---|---|
| Browser → ingress-nginx → api:8081, nginx profiles | `ok` — plain `Prefix` needs no annotation and no regex mode; nothing to negotiate |
| Browser → ALB → api:8081, AWS profile | `→ §2.3` — the arrow's ingress object has no specified annotation set |
| Browser → Authentik (discovery, token exchange) | `ok` — unchanged third origin; 5c's `connect-src` names it |
| `telemetry.ts` → `${apiBaseUrl}/v1/traces` → `Program.cs:452-460` → Jaeger | `ok` — endpoint exists with `RequireAuthorization()`; `telemetry.ts:29-38` attaches the bearer token through the exporter's `headers` factory, which is a supported `HeadersFactory` |
| Endpoint → `ITenantRepository.ListAsync()` | `ok` — registered `Program.cs:214`; `TenantLifecycleGrpcService.ListTenants` is a three-line delegation over the same call, so the projection is real |
| Endpoint → extracted schema-catalog reader → evaluator | `ok` — the extraction target is `ObjectMappingGrpcService.GetSchema`'s two-pass body, which depends only on `_registry` and `_authEvaluator` plus a `ClaimsPrincipal?`; taking the principal as a parameter is sufficient for both callers |
| Endpoint → aggregate path → StarRocks | `→ §2.2` — crosses into private members of the gRPC service |
| Endpoint → Qdrant collection info | `→ §2.1` — the named interface does not carry the operation |
| Endpoint → Prometheus `HttpClient` | `ok` — new client, new config key and the two NetworkPolicy rules are all still specified in the metrics section |
| kubelet → api:8081 `/health` (cached per 4e) | `ok` — unaffected by the new host; the probe targets the pod directly |
| Prometheus → api:8081 `/metrics` | `ok` — unaffected; scraping is pod-to-pod and `/metrics` is deliberately unrouted on the new host |

## 1. Verified-assumptions cross-check

Fresh read of the evidence behind the assumptions this round's material rests on.

- **A55** — reconfirmed. `charts/api/templates/deployment.yaml:137` interpolates
  `.Values.global.ingressHost`, so the global is in scope for this subchart.
- **A56** — reconfirmed on both legs: `charts/authentik/templates/ingress.yaml:21` renders the
  `printf "authentik.%s"` host, and `docs/user-management-and-security.md:231-235` documents the
  `/etc/hosts` line for kind.
- **A57** — reconfirmed. `ITenantRepository` registered at `Program.cs:214`; `Program.cs:348`
  already injects `IVectorSchemaManager` into the `/probe/vector` minimal-API endpoint. Note this
  assumption is about *injectability*, not about which methods the interface carries — see §2.1.
- **A58** — reconfirmed as failed, correctly. `Program.cs:87` registers the interceptor through
  `AddGrpc`, and `ActingUserInterceptor.ValidateActingUserAsync` returns early when the header is
  absent.
- **A59** — reconfirmed as failed, correctly. `RowFieldAuthorizationEvaluator.cs:14-15` returns
  `new AuthorizationDecision(true, false, …)` on a null acting user, and `:32-33` does the same
  when `tenant_id` is absent. The design's consequence — that an operator sees the full view either
  way — follows.
- **A60** — reconfirmed. `ObjectMappingGrpcService.cs:61-62` documents GetSchema as unauthorized
  discovery; `Program.cs:443` maps `ObjectSearchGrpcService` with no `RequireAuthorization`.
- **A61** — reconfirmed. No reference to `admin_console.proto` or `AdminConsoleService` outside
  `docs/`.
- **A62** — reconfirmed as failed. Compose does not publish 8081; `.env.development` names 8080.
- **A63** — reconfirmed. All four packages are in `package.json` and none is imported from `src/`.
- **A64** — reconfirmed as failed. Every generation script carries only `-I"$PROTO_DIR"`, and
  nothing in the repo supplies `google/api/annotations.proto`.
- **A38, A45, A52** — correctly marked moot; each retains its original finding, and A52 correctly
  keeps the `UseRouting`-is-auto-inserted fact because `UseCors` ordering still depends on it.

### Span check — uncovered dependencies

Three facts the design needs that no listed assumption covers. All three were checked in-round and
all three became §2 findings:

1. **That the Qdrant read operation is reachable through an injectable interface.** A57 covers
   *injectability* of `IVectorSchemaManager`; no assumption covers whether that interface carries
   `GetCollectionInfoAsync`. It does not. → §2.1
2. **That the aggregate path is reachable from outside the gRPC service.** No assumption covers
   the visibility of the members the data-volume endpoint would call. → §2.2
3. **That the new Ingress's AWS annotation set is defined.** A56 covers the *hostname* pattern; no
   assumption covers what annotations the new object carries on the ALB profile. → §2.3

## 2. Literal-wrongness findings

### 2.1 — The endpoint table names an operation that does not exist on the interface it names, and the spec contradicts itself two sections later

**Description.** Design 1's endpoint table gives the Qdrant row as backed by
`IVectorSchemaManager.GetCollectionInfoAsync`. That interface does not declare that method.
`Iverson.Server/Iverson.Vector/IVectorRoles.cs:23-27` declares `IVectorSchemaManager` with exactly
two members, `EnsureCollectionAsync` and `ApplyCollectionAsync`. The only call to
`GetCollectionInfoAsync` in the repository is at
`Iverson.Server/Iverson.Vector/IntelligenceCollectionManager.cs:60`, made on a Qdrant `client`
object, not through the interface.

The spec already knows this. Its own `GET /admin/console/qdrant` section says, verbatim:
`IVectorSchemaManager` "(`Iverson.Server/Iverson.Vector/IVectorRoles.cs:23-27`) exposes only
`EnsureCollectionAsync` and `ApplyCollectionAsync`", and that the endpoint "adds a read interface
over the same client, not new infrastructure." The table and the section contradict each other, and
the table is the wrong one. An implementer working from the table finds no such method.

**Evidence.**
- `Iverson.Server/Iverson.Vector/IVectorRoles.cs:23-27` — the two-member interface.
- `Iverson.Server/Iverson.Vector/IntelligenceCollectionManager.cs:22,60` — `ListCollectionsAsync`
  and `GetCollectionInfoAsync` called on the client.
- `docs/specs/…-design.md`, Design 1 endpoint table, Qdrant row, against the same file's
  `GET /admin/console/qdrant` section.

**Proposed fix.** Change the table's Qdrant row to name the read interface the qdrant section
already says must be added, rather than an existing interface that lacks the method — the two
sections should agree that this endpoint's backing is *new* surface over the existing client. While
correcting the block, the qdrant section's remaining stale word should go with it: it still says
"this RPC adds a read interface" after the transport change made it an endpoint.

### 2.2 — The data-volume endpoint's backing path is private, and the spec asserts an extraction count that is wrong

**Description.** The endpoint table backs `GET /admin/console/data-volume` with "the aggregate path
`ObjectSearchGrpcService.Aggregate` uses", and the endpoint-set section states that the GetSchema
extraction "is the only place where logic would otherwise be duplicated — `ListTenants` is already
a three-line delegation to the repository."

`Aggregate`'s path is not delegable as described. Its body is inline in the gRPC service and
composed from members that are private to it: `RequireSchema` (`ObjectSearchGrpcService.cs:771`,
`private`), `RunAggregationAsync` (`:536`, `private`), plus `ProtoToEngagementSpec` and
`EvaluateAuthorization`. It also operates on proto types — `AggregateRequest`, `AggregationSpec`,
`request.Joins` — which a JSON endpoint has no reason to construct, and the gRPC method additionally
requires a `ServerCallContext`.

There is a reachable seam one level down: `RunAggregationAsync` delegates to
`search.AggregateAsync(SchemaBuilder.ToEngagementQuerySchema(schema), query, spec, having, …)`,
taking domain types rather than proto types. So the endpoint is buildable — but by reassembling
four or five pieces (registry lookup, `ToEngagementQuerySchema`, an `EngagementAggSpec` for the
count, and the authorization constraints that `EvaluateAuthorization` currently produces), not by
calling an existing path. That is a second extraction, and the spec says there is only one.

**This also undercuts the Known-issues change.** The spec now states that data volume "can now
report authorization denial" because "a server-side endpoint holds the caller's principal and can
consult `IRowFieldAuthorizationEvaluator` directly." Consulting the evaluator is genuinely
reachable — it is DI-registered and takes a `ClaimsPrincipal?`. But `Aggregate`'s own denial
decision comes from the private `EvaluateAuthorization`, which also handles the joined-type case,
so the endpoint reproduces that logic rather than reusing it. The claim is achievable; the route to
it is not the one the spec describes.

**Evidence.**
- `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:488-530` — `Aggregate`'s inline body:
  `RequireSchema`, the zero-aggregation `InvalidArgument`, `EvaluateAuthorization`, then
  `RunAggregationAsync` per spec via `ProtoToEngagementSpec`.
- `:536` — `private async Task<EngagementAggResult?> RunAggregationAsync(…)`.
- `:771` — `private SchemaDescriptor RequireSchema(string typeName)`.
- `:541-548` — `RunAggregationAsync` delegating to `search.AggregateAsync(...)` over domain types,
  which is the reachable seam.
- `docs/specs/…-design.md`, Design 1 endpoint set: "it is the only place where logic would
  otherwise be duplicated".

**Proposed fix.** State both extractions. The data-volume endpoint needs the same treatment
GetSchema gets: a service taking the schema, the principal and a count spec, calling
`search.AggregateAsync`, used by both the gRPC method and the endpoint — or, if the smaller change
is preferred, make `RequireSchema` and the authorization helper reachable and have the endpoint
call `search.AggregateAsync` directly. Either way the sentence claiming a single extraction has to
go, because it sets the implementation budget.

### 2.3 — The new Ingress has no specified annotation set, and both plausible defaults break the AWS profile

**Description.** Design 1 specifies the new Ingress by host, backend port, path type and the absence
of a rewrite annotation. It does not say what annotations the object carries. On the nginx profiles
that is harmless — none are needed. On AWS it is not, and both readings of the silence fail:

- **If the object is given no ALB annotations**, it renders with `ingressClassName: alb` and no
  `scheme`, no `target-type`, no `certificate-arn` and no `listen-ports`. The controller defaults to
  an internal-scheme load balancer with no TLS, which does not serve a browser on the public
  internet. Every other Ingress in this chart carries these explicitly — `values-aws.yaml:71-85`
  for api, `:144-152` for admin-ui, `:131-139` for authentik.
- **If the object reuses the api subchart's `.Values.ingress.annotations`** — the path of least
  resistance, since it lives in the api subchart and targets the api service — it inherits
  `values-aws.yaml:75`'s `alb.ingress.kubernetes.io/backend-protocol-version: GRPC`, declaring a
  gRPC target group for an endpoint that serves HTTP/1.1 JSON.

An earlier revision of this spec documented exactly this hazard, in the paragraph explaining why the
new route had to be a separate Ingress object rather than a path on the api Ingress. The transport
rewrite removed the paragraph along with the arrangement it described, but the hazard is a property
of `charts/api/templates/ingress.yaml:4-6` rendering `.Values.ingress.annotations` onto Ingress
metadata, which is unchanged.

**Evidence.**
- `charts/api/templates/ingress.yaml:4-6` — `annotations: {{- toYaml .Values.ingress.annotations }}`
  on the Ingress's own metadata, so the value is object-scoped.
- `values-aws.yaml:75` — `alb.ingress.kubernetes.io/backend-protocol-version: GRPC` under
  `api.ingress.annotations`.
- `values-aws.yaml:71-85`, `:131-139`, `:144-152` — every existing Ingress in this chart carries an
  explicit ALB annotation block, so "no annotations" is not the established pattern.
- `docs/specs/…-design.md`, Design 1 → Transport, the new-Ingress bullet: host, port, path type and
  "no rewrite annotation of any kind", and nothing further.

**Proposed fix.** Give the new Ingress its own annotations block in values, on the pattern
admin-ui and authentik already use, and state explicitly that it must **not** carry
`backend-protocol-version: GRPC` — the whole point of the separate object is that this endpoint
speaks HTTP/1.1. If a single ALB is wanted rather than a second one, the two Ingresses can share an
`alb.ingress.kubernetes.io/group.name` while carrying different backend-protocol annotations, since
that annotation is Ingress-scoped; unlike the previous arrangement no `group.order` is needed,
because the two objects are on different hosts and cannot shadow one another.

## 3. Forced decisions

No forced decisions found.

Whether the two Ingresses share one ALB via `group.name` or provision two was considered and is not
one: both configurations work, and the choice is a cost preference rather than something a codebase
or product constraint forces. It appears inside §2.3's fix as an option rather than as a decision
the user must make before proceeding.

## 4. Previously addressed

- **Round 7 §2.1 (ALB cannot rewrite paths)** — resolved, and by removal rather than by
  compensation. No profile performs a rewrite; the `admin-api` host serves plain `Prefix` paths that
  both controllers handle natively. §2.3 concerns the same object's annotations, which is a
  different question and was not raised before.
- **Round 7 §2.2 (CSP naming a per-environment origin from a static `nginx.conf`)** — resolved, and
  correctly strengthened: 5c now states `'self'` is insufficient and names both the `admin-api` and
  Authentik origins, which the transport change made necessary.
- **Round 7 §2.3 / §3.2 (NetworkPolicy `clusterCidrs`)** — resolved in 4f and untouched by this
  round; the rule set and per-profile defaults still stand.
- **Round 7 §3.1 (how AWS reaches 8081)** — resolved by the hostname change, which supersedes all
  three options that were on the table.
- **Round 6 §2.1 (Qdrant `vectors_count`)** — still resolved; the field names in Design 2's Band C
  row and the qdrant section remain points and indexed vectors. §2.1 of this round is about the
  interface carrying the call, not the fields it returns.
- **Rounds 1-5 transport findings** — all dissolved with the path-prefix arrangement rather than
  fixed. The spec retains the record of why it was abandoned.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**

Three §2 items, no forced decisions. All three are narrow: two are wrong identifiers or wrong claims
about what the codebase exposes (§2.1, §2.2), and one is a gap the rewrite opened by deleting a
paragraph whose hazard outlived the arrangement it described (§2.3). None challenges the transport
decision, which holds up under the sweep — the routing, the prefix coverage, the authorization
model and the widget-to-endpoint mapping all check out.

The pattern in §2.1 and §2.2 is worth naming: both are places where the new endpoint table asserts a
*backing* for an endpoint, and in both cases the assertion was made at the level of "this capability
exists somewhere in the codebase" rather than "this operation is reachable from where the endpoint
will call it." §2.1 is the sharper instance, because the spec states the correct fact two sections
away from the table that contradicts it.
