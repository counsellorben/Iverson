# Critical Design Review: 2026-08-25-admin-console-landing-page-design (Round 9)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-25-admin-console-landing-page-design.md`
**Verified Assumptions section:** present

Sweep built before prior reviews were read. The spec is at 955 lines after round 8's three fixes.

## 0. Coverage enumeration

### Sections

| Section | Disposition |
|---|---|
| Problem | `ok` — unchanged; no claim touched by round 8's fixes |
| Scope (in / out) | `ok` — the Design 4 bullet still matches (4c, 4d) after 4a/4b deletion; the eleven-findings count is unchanged and still adds up |
| Design 1 — opening | `ok` — the inverted gRPC rationale is unchanged and internally consistent |
| Design 1 — Transport, hostname | `ok` — `charts/authentik/templates/ingress.yaml:21` renders the `printf` host; `charts/api/templates/deployment.yaml:137` confirms the api subchart sees `global.ingressHost`. Also confirmed the value resolves on every profile: aws/azure/gcp set it explicitly, local/laptop inherit `iverson.local` from `values.yaml:22` |
| Design 1 — Transport, three Prefix paths | `ok` — see rules rows |
| Design 1 — Transport, new Ingress + annotations block | `→ §2.2` |
| Design 1 — Transport, CORS | `ok` — `UseCors` before `UseAuthentication` is correct for preflight; `Authorization` is not CORS-safelisted so every widget GET is preflighted, which the spec's allow-list of request headers covers |
| Design 1 — Transport, `apiBaseUrl` | `ok` — `config.ts:16` is still its only reader; the spec says it carries the `admin-api` origin per environment, which covers all profiles generically |
| Design 1 — endpoint set, table | `ok` — round 8's two corrections landed: data-volume now reads "the extracted aggregate reader" and qdrant "a new read interface over the Qdrant client", neither naming a member that does not exist |
| Design 1 — endpoint set, two extractions | `ok` — both are now named, the aggregate one correctly identifies `search.AggregateAsync` as the reachable seam and `RequireSchema`/`RunAggregationAsync` as the private members it cannot use |
| Design 1 — endpoint set, authorization model | `ok` — three `Operator`, two authenticated-with-principal, one anonymous; each matches the gRPC counterpart (`Program.cs:443`, `ObjectMappingGrpcService.cs:61-62`, `:444`) |
| Design 1 — `GET /admin/console/metrics` | `ok` on substance — the fixed result set, the nine values across four widgets, and the two NetworkPolicy additions all check out |
| Design 1 — `GET /admin/console/qdrant` | `ok` — now says "this endpoint adds a read interface", consistent with the table |
| Design 1 — Authorization | `ok` — three policies at `Program.cs:141-156`; `RequireAuthorization()` bare yields the fallback |
| Design 1 — Explicitly not included | `ok` — unchanged |
| Design 2 — Bands A/B/C, constraints | `ok` — all four source rows name endpoints; the StarRocks three-state and no-Ollama-tile constraints are `/health` body properties, untouched |
| Design 3 — cadence / failure / implementation / testing | `ok` — none depends on transport; the hook still takes the token because `useAuth()` is context-only |
| Design 4 — intro | `ok` — reduced to (4c, 4d) with the 4a/4b deletion recorded |
| Design 4c | `ok` — headless Services + `dns_sd_configs`, and the `automountServiceAccountToken: false` reasoning, unchanged and still accurate |
| Design 4d | `ok` — the two identity gaps and their fixes are unchanged |
| Design 4e | `ok` — probe authorization and the 5s `/health` cache; `readinessProbe` still declares no `periodSeconds` so the 10s default still bounds it |
| Design 4f — consumer table | `ok` — the four consumers and their selectors are correct as written |
| Design 4f — `clusterCidrs` defaults | `→ §2.1` |
| Design 5 — 5a, 5b, 5d, 5e, 5f | `ok` — none touched by round 8 or transport-dependent |
| Design 5 — 5c | `ok` — names both `admin-api` and Authentik origins and states `'self'` is insufficient; because both are interpolated at container start from environment, this is per-profile correct without enumerating profiles |
| Design 5 — 5g | `ok` — `${apiBaseUrl}/v1/traces` served by the `/v1/traces` Prefix path |
| Verified assumptions | `ok` — A65-A67 appended and accurate; see §1 |
| Known issues | `ok` — the data-volume denial paragraph now correctly describes a direct `IRowFieldAuthorizationEvaluator` call rather than reuse of `Aggregate`'s private helper |

### Rules and operands

| Rule | Disposition |
|---|---|
| `/admin` Prefix, over-inclusion | `ok` — re-enumerated every `/admin` route in `Program.cs`: `:361` reconcile, `:375` dlq, `:382` replay, all `Operator`, plus the five `/admin/console/*`. Nothing anonymous under the prefix |
| `/admin` Prefix, under-inclusion | `ok` — element-wise segment matching catches `/admin/console/tenants` and `/admin/dlq`, not `/administration` |
| `/health`, `/v1/traces` Prefix, both directions | `ok` — `/health` also catches `/health/live` (anonymous, no data); `/v1/traces` matches only itself |
| Eligibility predicate: what the prefixes leave unrouted | `ok` — all seven `AllowAnonymous` producers re-enumerated (`Program.cs:275`, `:299`, `:334`, `:340`, `:346`, `:352`, `:359`); `/metrics` and the four probes sit under no prefix |
| Recurrence — console→API call set | `ok` — five endpoints, `/health`, `/v1/traces`; OIDC goes to the Authentik host. Each covered by exactly one prefix |
| Recurrence — nine widgets to endpoints | `ok` — 1 health strip, 3 Band A, 4 Band B via `/metrics`, 1 Band C. Nine mapped, none duplicated |
| Recurrence — authorization model per endpoint | `ok` — each of the six sources matches the gRPC counterpart it projects |
| **Recurrence — the set of deployment profiles** | `→ §2.1`, `→ §2.2` — the chart carries six values files (`values.yaml` plus aws, azure, gcp, laptop, local). The design specifies per-profile values for two of them |
| `clusterCidrs` operand, both directions | `→ §2.1` — over-inclusion (a wrong inherited CIDR silently denies the kubelet) and under-inclusion (an unset key fails the render under `required`) both bite on the three unspecified profiles |
| New Ingress `className`/TLS operand across profiles | `→ §2.2` — ALB uses `certificate-arn`; azure and gcp both use Secret-based `tls:` via `tlsSecretName` |
| Stale "this **RPC** cannot connect in any Kubernetes profile" in the metrics section | `dropped` — candidate generated, failed literal-wrongness. The claim it makes (api→prometheus egress is required) is true and unchanged; only the noun is stale after the endpoint rename. Naming, not a defect |
| CORS origin and `apiBaseUrl` per profile | `dropped` — the spec specifies both generically ("per environment", "supplied by configuration"), which covers every profile. Enumerating each file's literal value is implementation detail, unlike §2.1 where the spec's own `required` turns absence into a render failure |

### Data-flow arrows

| Arrow → operation | Disposition |
|---|---|
| Browser → ingress-nginx → api:8081 (local, laptop) | `ok` — plain `Prefix`, no annotation needed |
| Browser → ALB → api:8081 (aws) | `ok` on routing after round 8's annotations fix; the AWS shape is now specified |
| Browser → AGIC → api:8081 (azure) | `→ §2.2` |
| Browser → GCE ingress → api:8081 (gcp) | `→ §2.2` |
| Browser → Authentik (discovery, token) | `ok` — unchanged third origin, named in 5c's `connect-src` |
| `telemetry.ts` → `${apiBaseUrl}/v1/traces` → `Program.cs:452-460` → Jaeger | `ok` — endpoint exists with `RequireAuthorization()`; the bearer token is attached via the exporter's `HeadersFactory` |
| Endpoint → `ITenantRepository.ListAsync()` | `ok` — `Program.cs:214`; the gRPC method is a three-line delegation over the same call |
| Endpoint → extracted schema-catalog reader → evaluator | `ok` — depends only on `_registry`, `_authEvaluator` and a `ClaimsPrincipal?` |
| Endpoint → extracted aggregate reader → `search.AggregateAsync` → StarRocks | `ok` after round 8 — the seam is named and takes domain types |
| Endpoint → new Qdrant read interface → client | `ok` after round 8 — the table and the endpoint section now agree that this is new surface over the existing client |
| kubelet → api:8081 `/health` (aws, azure, gcp, local, laptop) | `→ §2.1` — the rule admitting this source is specified for two of the five |
| Prometheus → api:8081 `/metrics` and worker:8081 | `ok` — pod-to-pod, unaffected by the new host; `/metrics` deliberately unrouted there |

## 1. Verified-assumptions cross-check

- **A65** — reconfirmed. `IVectorRoles.cs:23-27` declares `IVectorSchemaManager` with two members;
  `IntelligenceCollectionManager.cs:22,60` carry the `ListCollectionsAsync` and
  `GetCollectionInfoAsync` calls on the client. Correctly recorded as failed.
- **A66** — reconfirmed. `ObjectSearchGrpcService.cs:536` and `:771` are `private`; the delegation to
  `search.AggregateAsync` over domain types is the seam. Correctly recorded as failed.
- **A67** — reconfirmed. `charts/api/templates/ingress.yaml:4-6` renders
  `.Values.ingress.annotations` onto the Ingress's own `metadata`, so the value is object-scoped.
- **A55, A56, A57** — reconfirmed (`deployment.yaml:137`; `authentik/templates/ingress.yaml:21` plus
  `docs/user-management-and-security.md:231-235`; `Program.cs:214` and `:348`).
- **A58, A59** — reconfirmed as failed. `Program.cs:87` registers the interceptor through `AddGrpc`;
  `RowFieldAuthorizationEvaluator.cs:14-15` and `:32-33` return unrestricted decisions.
- **A60, A61, A62, A63, A64** — reconfirmed as written.
- **A38, A45, A52** — still correctly moot; A52 continues to earn its place because `UseCors`
  ordering depends on the same auto-inserted-routing fact.

### Span check — uncovered dependencies

Two facts the design needs that no listed assumption covers. Both were checked in-round and both
became §2 findings:

1. **How many deployment profiles the design must supply values for.** A53 covers the AWS VPC CIDR
   and A54 covers Calico enforcement in kind. Neither states the profile set, and no assumption
   mentions azure or gcp — which exist, are complete, and enforce NetworkPolicy. → §2.1
2. **That the new Ingress's class and TLS convention are expressible on every profile.** A67 covers
   annotation *scoping*; nothing covers what class or TLS mechanism the object uses outside AWS. → §2.2

## 2. Literal-wrongness findings

### 2.1 — `clusterCidrs` is specified for two of five deployment profiles, and the spec's own `required` turns the gap into a render failure

**Description.** Design 4f replaces the port-8081 `from: []` rule with an `ipBlock` sourced from a
new `networkPolicy.clusterCidrs` key, states that "**Every profile ships a default, and the template
fails the render when the list is empty** — `required` in Helm", and then gives a two-row table:
`values-aws.yaml` and `values-local.yaml`.

The chart has six values files: `values.yaml`, `values-aws.yaml`, `values-azure.yaml`,
`values-gcp.yaml`, `values-laptop.yaml`, `values-local.yaml` — five deployment profiles over a
base. Three of the five get no `clusterCidrs`, and both outcomes are broken:

- **If `required` is implemented as the spec says**, `helm template` fails for azure, gcp and
  laptop. Three of five profiles cannot render.
- **If a base default is added instead**, those three inherit a CIDR belonging to a different
  network — AWS's `10.0.0.0/16` or kind's `172.18.0.0/16` — and the `ipBlock` matches nothing on
  their node network. The kubelet's readiness probe to `/health` on 8081 is denied, and pods never
  become Ready. That is the silent non-readiness 4f was specifically written to avoid.

This is not theoretical on azure and gcp: both enforce NetworkPolicy.
`deploy/terraform/modules/cluster-azure/main.tf:183` sets `network_policy = "azure"` with a comment
stating that without it "every NetworkPolicy silently does nothing", and
`deploy/terraform/modules/cluster-gcp/main.tf:165` uses Dataplane V2 for native enforcement.

**Evidence.**
- `docs/specs/…-design.md`, Design 4f — the `required`-on-empty statement and the two-row table.
- `ls deploy/helm/iverson/values*.yaml` — six files.
- `values-azure.yaml:70-73` (`className: "azure-application-gateway"`, api ingress host) and
  `values-gcp.yaml:71-74` (`className: "gce"`) — both are complete profiles with api, admin-ui and
  authentik ingress blocks, not stubs.
- `cluster-azure/main.tf:183`; `cluster-gcp/main.tf:165` — enforcement on both.

**Proposed fix.** Extend 4f's table to every profile. The values are determinate and already in the
repository, so this is a table extension rather than an open question:

| Profile | `networkPolicy.clusterCidrs` | Source |
|---|---|---|
| `values-aws.yaml` | `["10.0.0.0/16"]` | `cluster-aws/variables.tf:16-19`, `vpc_cidr` default |
| `values-azure.yaml` | `["10.1.0.0/16"]` | `cluster-azure/main.tf:127` VNet `address_space`; node subnet is `10.1.0.0/20` at `:136` |
| `values-gcp.yaml` | `["10.2.0.0/20"]` | `cluster-gcp/main.tf:16` subnet `ip_cidr_range` |
| `values-local.yaml` | `["172.18.0.0/16"]` | the Docker network kind uses |
| `values-laptop.yaml` | inherit or set explicitly | depends on whether that profile runs a cluster at all — 4f should say which |

Keeping each in lockstep with its terraform module is the same discipline
`global.ingressHost`/`api.ingress.host` already carry.

### 2.2 — The new Ingress is specified for the ALB profile only; azure and gcp use different classes and a different TLS mechanism

**Description.** Round 8 gave the new Ingress an annotations block, but scoped it to AWS: it names
`scheme`, `target-type`, `certificate-arn`, `listen-ports` and `ssl-redirect`, and forbids
`backend-protocol-version: GRPC`. That is correct for ALB and says nothing about the other two cloud
profiles, both of which run this chart with a different ingress controller and a different TLS
convention:

- **azure** — `values-azure.yaml:70-73` sets `className: "azure-application-gateway"` with
  `annotations: {}`, and its comments state AGIC "terminates TLS from a standard Kubernetes Secret
  referenced via the Ingress's `tls:` block".
- **gcp** — `values-gcp.yaml:71-74` sets `className: "gce"`, with the same Secret-based TLS note.

Both differ from ALB, where `tlsSecretName` is deliberately left empty because TLS terminates on an
ACM certificate referenced by ARN. An Ingress rendered without a `className` does not reach either
controller, and one rendered without `tlsSecretName` on azure or gcp serves no TLS — so on those two
profiles the console cannot reach the API at all.

Every other Ingress in this chart already carries this per-profile treatment: api, admin-ui and
authentik each appear in all three cloud values files with their own `className`, annotations and
TLS convention. The new object is the only one specified for a single profile.

**Evidence.**
- `docs/specs/…-design.md`, Design 1 → Transport, the annotations paragraph — names only the ALB
  annotation set.
- `values-azure.yaml:70-73`, `:124-128`, `:135-140` — api, authentik and admin-ui ingresses, all
  `azure-application-gateway`, all Secret-based TLS.
- `values-gcp.yaml:71-74`, `:125-129`, `:136-141` — the same three under `gce`.
- `values-aws.yaml:71-85`, `:131-139`, `:144-152` — the ALB counterparts, with `certificate-arn`
  and empty `tlsSecretName`.

**Proposed fix.** State that the new Ingress takes the same per-profile shape as every other Ingress
in this chart: its own `className`, annotations and TLS convention in each of the three cloud values
files and in `values-local.yaml`, following whatever the api Ingress in that same file uses — with
the single documented exception that it must not carry `backend-protocol-version: GRPC` on AWS.
Framing it as "follows the api Ingress's per-profile shape, minus the gRPC backend protocol" is
shorter than enumerating each and cannot drift as the profiles evolve.

## 3. Forced decisions

No forced decisions found.

§2.1's missing CIDRs were considered as a candidate — round 7's §3.2 established the per-profile
mechanism, and one could argue the azure/gcp values are a fresh choice. They are not: both are
declared in the repository (`cluster-azure/main.tf:127`, `cluster-gcp/main.tf:16`), so the fix is a
table extension with known values rather than a decision the user must make.

## 4. Previously addressed

- **Round 8 §2.1 (Qdrant backing named a non-existent interface member)** — resolved. The table now
  reads "a new read interface over the Qdrant client", and the endpoint section's stale "this RPC"
  is now "this endpoint". Table and section agree.
- **Round 8 §2.2 (data-volume backing and the extraction count)** — resolved. "Two extractions are
  required", with the aggregate reader's private-member obstacle and the `search.AggregateAsync`
  seam both named, and the "only place where logic would otherwise be duplicated" sentence removed.
  The Known-issues denial claim is correspondingly narrowed to a direct evaluator call.
- **Round 8 §2.3 (Ingress annotation set unspecified)** — resolved for AWS, which is what the
  finding described. §2.2 of this round is the same object on the two profiles that finding did not
  reach.
- **Round 7 §2.1 / §3.1 (ALB cannot rewrite)** — still dissolved by the hostname change.
- **Round 7 §2.2 (CSP origin from a static `nginx.conf`)** — still resolved, and correctly
  strengthened when the API became a separate origin.
- **Rounds 1-6 transport findings** — all dissolved with the path-prefix arrangement.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**

Two §2 items, no forced decisions. Both are the same defect seen from two angles: the design
specifies per-profile values for the two profiles that have been under discussion all session, and
the chart has five. Neither is deep — one extends a table with values already in the repository, the
other states that a new Ingress follows the same per-profile shape as the three Ingresses already
beside it.

Worth noting what the sweep did *not* find. The endpoint set, the authorization model, the routing
rules in both directions, the widget mapping and all three of round 8's fixes came through clean on
a fresh read. The remaining defects are at the deployment-configuration edge, which is the one
surface no round before this one enumerated as a set — every prior round checked the profiles the
spec happened to name.
