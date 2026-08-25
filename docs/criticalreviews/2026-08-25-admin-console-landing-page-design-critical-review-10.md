# Critical Design Review: 2026-08-25-admin-console-landing-page-design (Round 10)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-25-admin-console-landing-page-design.md`
**Verified Assumptions section:** present

Sweep built before prior reviews were read. Spec at 990 lines after round 9's two fixes.

## 0. Coverage enumeration

### Sections

| Section | Disposition |
|---|---|
| Problem | `ok` — unchanged |
| Scope (in / out) | `ok` — Design 4 bullet still matches (4c, 4d); the eleven-findings count and the 5a authentication exception both still hold |
| Design 1 — opening | `ok` — the inverted gRPC rationale unchanged |
| Design 1 — Transport, hostname | `ok` — `authentik/templates/ingress.yaml:21` renders the `printf` host; `charts/api/templates/deployment.yaml:137` confirms the api subchart sees the global; the value resolves on all five profiles (aws/azure/gcp explicit, local/laptop inherit `values.yaml:22`) |
| Design 1 — Transport, three Prefix paths | `ok` — see rules rows |
| Design 1 — Transport, new Ingress + per-profile shape | `ok` — round 9's fix landed: the object follows the api Ingress's own `className`/annotations/TLS convention per file, never carries `backend-protocol-version: GRPC`, and laptop is correctly excluded because `values-laptop.yaml:19` sets `adminUi.enabled: false` |
| Design 1 — Transport, hostname resolution and certificates | `→ §2.1` |
| Design 1 — Transport, CORS | `ok` — `UseCors` before `UseAuthentication`; `Authorization` is not CORS-safelisted so every widget GET preflights, which the header allow-list covers; `AllowCredentials` correctly omitted for header-borne tokens |
| Design 1 — Transport, `apiBaseUrl` | `ok` — `config.ts:16` still its only reader; the per-environment statement covers all profiles that run a console |
| Design 1 — endpoint set (table, extractions, authz, dependencies) | `ok` — round 8's corrections still stand: both extractions named, the aggregate seam is `search.AggregateAsync`, the Qdrant row names new surface rather than a phantom interface member |
| Design 1 — `GET /admin/console/metrics` | `ok` — fixed result set; nine values across four widgets matches Band B; the two Prometheus NetworkPolicy additions are correctly marked transport-independent |
| Design 1 — `GET /admin/console/qdrant` | `ok` — reads "this endpoint", consistent with the table |
| Design 1 — Authorization | `ok` — three policies at `Program.cs:141-156`; bare `RequireAuthorization()` yields the fallback |
| Design 1 — Explicitly not included | `ok` — unchanged |
| Design 2 — Bands A/B/C, constraints | `ok` — four source rows name endpoints; the StarRocks three-state and no-Ollama-tile constraints are `/health` body properties |
| Design 3 — cadence / failure / implementation / testing | `ok` — none transport-dependent; per-widget degradation is what lets Operator and authenticated-only endpoints coexist on one page |
| Design 4 — intro | `ok` — (4c, 4d) with the 4a/4b deletion recorded |
| Design 4c | `ok` — the headless-Service wording reads as additive ("exposing the metrics port"), and the `automountServiceAccountToken: false` reasoning re-checked; see rules rows for the candidate this generated |
| Design 4d | `ok` — the blueprint mechanism verified for the first time this round: `charts/authentik/templates/blueprints-configmap.yaml:6` globs `blueprints/*.yaml` (top-level only, which is exactly why the spec requires a top-level file), and `docker-compose.yml:294,328` bind-mount the whole directory. The two identity gaps and their fixes are unchanged |
| Design 4e | `ok` — probe authorization and the 5s `/health` cache; `readinessProbe` still declares no `periodSeconds` |
| Design 4f — consumer table | `ok` — four consumers, selectors correct |
| Design 4f — `clusterCidrs` five-profile table | `ok` — each value re-checked against its module: `cluster-aws/variables.tf:16-19`, `cluster-azure/main.tf:127`, `cluster-gcp/main.tf:16`; the two kind rows carry the "assigned, not guaranteed" caveat. The enforcement sentence now names all four mechanisms coherently |
| Design 5 — 5a, 5b, 5d, 5e, 5f | `ok` — none transport-dependent or touched by round 9 |
| Design 5 — 5c | `ok` — names both origins and states `'self'` is insufficient; both interpolated at container start, so per-profile correct without enumeration |
| Design 5 — 5g | `ok` — `${apiBaseUrl}/v1/traces` served by the `/v1/traces` Prefix path |
| Verified assumptions | `ok` — A68/A69 accurate; see §1 |
| Known issues | `ok` — the data-volume denial paragraph correctly describes a direct evaluator call |

### Rules and operands

| Rule | Disposition |
|---|---|
| `/admin` Prefix, over-inclusion | `ok` — re-enumerated `/admin` routes: `Program.cs:361`, `:375`, `:382`, all `Operator`, plus the five `/admin/console/*` |
| `/admin` Prefix, under-inclusion | `ok` — element-wise matching catches `/admin/console/tenants` and `/admin/dlq`, not `/administration` |
| `/health`, `/v1/traces` Prefix, both directions | `ok` — `/health` also catches `/health/live` (anonymous, no data) |
| Eligibility predicate: what the prefixes leave unrouted | `ok` — all seven `AllowAnonymous` producers re-enumerated; `/metrics` and the four probes under no prefix |
| Recurrence — console→API call set | `ok` — five endpoints, `/health`, `/v1/traces`; OIDC to the Authentik host |
| Recurrence — nine widgets to endpoints | `ok` — 1 + 3 + 4 + 1, none unserved or duplicated |
| Recurrence — authorization model per endpoint | `ok` — each matches its gRPC counterpart |
| **Recurrence — everything that must know the `admin-api` hostname** | one row per consumer: the new Ingress `host` `ok`; `adminUi.apiBaseUrl` `ok` (per-environment statement); CORS allowed origin `ok` (that is the *console's* origin, not admin-api's); 5c's `connect-src` `ok` (interpolated from env); **the TLS certificate → §2.1**; cloud DNS `dropped` (see below) |
| Recurrence — deployment profiles for each new per-profile value | `ok` — `clusterCidrs` five rows, the Ingress per-profile shape, `apiBaseUrl` and the CORS origin stated per environment. This is the set round 9 found missing and it is now closed |
| 4c headless Services vs the Service the two Ingresses target | `dropped` — candidate generated, failed literal-wrongness. "Headless Services **exposing the metrics port**" reads as additive objects, and even under a converting reading both ingress-nginx and ALB with `target-type: ip` resolve backends through Endpoints rather than the ClusterIP, so the Ingress path does not break. No demonstrable failure |
| Cloud DNS record for `admin-api.<ingressHost>` | `dropped` — candidate generated, failed the scope half of the test. No DNS step is documented anywhere in the repo for *any* hostname (`grep` for route53/CNAME/A-record across `docs/` and `deploy/terraform/` returns nothing), so `iverson.example.com` and `authentik.iverson.example.com` already carry the identical gap. This design adds a third instance of a pre-existing condition rather than a new failure |
| `clusterCidrs` azure row uses the VNet `/16` where the node subnet is `/20` | `dropped` — broader than strictly necessary, but a superset that still admits the kubelet. The rule works; over-breadth relative to an optimum is not literal-wrongness, and it remains vastly narrower than the `from: []` it replaces |
| Stale "this **RPC** cannot connect" in the metrics section | `dropped` — unchanged from round 9. The claim it makes is true; only the noun is stale |

### Data-flow arrows

| Arrow → operation | Disposition |
|---|---|
| Browser → ingress-nginx → api:8081 (local, laptop) | `ok` — plain `Prefix`; laptop runs no console so only local matters |
| Browser → ALB → api:8081 (aws) | `ok` — annotations specified since round 8; TLS via ACM covered by §2.1's AWS half |
| Browser → AGIC → api:8081 (azure) | `→ §2.1` — reaches the controller after round 9's fix, but the TLS material it presents is unspecified |
| Browser → GCE ingress → api:8081 (gcp) | `→ §2.1` — same |
| Browser → Authentik (discovery, token) | `ok` — third origin, named in 5c |
| `telemetry.ts` → `${apiBaseUrl}/v1/traces` → `Program.cs:452-460` → Jaeger | `ok` — endpoint exists with `RequireAuthorization()`; bearer token via the exporter's `HeadersFactory` |
| Endpoint → `ITenantRepository.ListAsync()` | `ok` — `Program.cs:214` |
| Endpoint → schema-catalog reader → evaluator | `ok` — depends on `_registry`, `_authEvaluator`, a `ClaimsPrincipal?` |
| Endpoint → aggregate reader → `search.AggregateAsync` → StarRocks | `ok` — seam named, domain types |
| Endpoint → Qdrant read interface → client | `ok` — new surface over `IntelligenceCollectionManager`'s client |
| kubelet → api:8081 `/health` (all five profiles) | `ok` — `clusterCidrs` now supplies a value for each |
| Prometheus → api:8081 and worker:8081 | `ok` — pod-to-pod, `/metrics` deliberately unrouted on the new host |

## 1. Verified-assumptions cross-check

- **A68** — reconfirmed. Six values files; azure and gcp complete with their own api, admin-ui and
  authentik ingress blocks; both enforce NetworkPolicy (`cluster-azure/main.tf:183`,
  `cluster-gcp/main.tf:165`); overlays self-contained per `values-laptop.yaml`'s header.
- **A69** — reconfirmed, and the evidence is now richer than when it was written: azure and gcp do
  not merely use Secret-based TLS, they use **one Secret per Ingress** —
  `values-azure.yaml:78,130,141` and `values-gcp.yaml:79,131,143` declare `iverson-api-tls`,
  `iverson-authentik-tls` and `iverson-admin-ui-tls`. That per-Ingress pattern is what §2.1 turns on.
- **A65, A66, A67** — reconfirmed (`IVectorRoles.cs:23-27`; `ObjectSearchGrpcService.cs:536,771`;
  `charts/api/templates/ingress.yaml:4-6`).
- **A55-A64** — reconfirmed as written, including the four recorded failures (A58, A59, A62, A64).
- **A38, A45, A52** — still correctly moot; A52 still earns its place through `UseCors` ordering.

### Span check — uncovered dependency

One fact the design needs that no listed assumption covers. It was checked in-round and became a
§2 finding:

1. **That a certificate covering `admin-api.<ingressHost>` exists on each cloud profile.** A69
   covers which TLS *mechanism* each profile uses; nothing covers what the certificate must
   *contain*, and the spec's only sentence on the subject names a mechanism two of the three cloud
   profiles do not have. → §2.1

## 2. Literal-wrongness findings

### 2.1 — The certificate requirement is written in ACM terms for all cloud profiles, and contradicts the per-profile TLS rule two paragraphs above it

**Description.** Design 1's Transport section closes with:

> "On the cloud profiles the ACM certificate must carry the new name as a SAN alongside the
> existing host."

That is correct for AWS and wrong for the other two cloud profiles, which have no ACM certificate.
`values-azure.yaml` and `values-gcp.yaml` terminate TLS from Kubernetes Secrets, and they do so
**one Secret per Ingress, each covering that Ingress's own host**:
`iverson-api-tls`, `iverson-authentik-tls`, `iverson-admin-ui-tls`
(`values-azure.yaml:78,130,141`; `values-gcp.yaml:79,131,143`). The api Ingress template renders
`tls: - hosts: [{{ .Values.ingress.host }}] secretName: {{ .Values.ingress.tlsSecretName }}`
(`charts/api/templates/ingress.yaml:9-13`) — one host, one Secret.

So on azure and gcp the new Ingress does not need a SAN added to anything. It needs a **fourth
Secret**, holding a certificate for `admin-api.<ingressHost>`, exactly as the three existing
Ingresses each have their own.

The sentence also contradicts the paragraph two above it, which round 9 added: that the new object
"follows the api Ingress's own per-profile shape — `className`, annotations and TLS convention".
An implementer reconciling the two most naturally sets `tlsSecretName: "iverson-api-tls"` — reusing
the api Ingress's Secret, whose certificate names `iverson.example.com`. The Ingress then declares
`hosts: [admin-api.iverson.example.com]` against a certificate that does not cover it, and the
browser rejects the TLS handshake on SNI name mismatch. The console reaches no endpoint on either
profile. Leaving `tlsSecretName` unset instead suppresses the `tls:` block entirely, so an HTTPS
console page then makes plaintext calls, which the browser also blocks.

**Evidence.**
- `docs/specs/…-design.md`, Design 1 → Transport, final paragraph — the ACM/SAN sentence,
  scoped to "the cloud profiles".
- `values-azure.yaml:78,130,141` and `values-gcp.yaml:79,131,143` — three per-host TLS Secrets on
  each profile.
- `values-aws.yaml:77,135,148` — `tlsSecretName` deliberately empty on all three AWS Ingresses,
  with comments stating ALB terminates on an ACM certificate referenced by ARN instead.
- `charts/api/templates/ingress.yaml:9-13` — the `tls:` block binds one host to one Secret.

**Proposed fix.** Replace the single sentence with the per-profile form, matching how the rest of
this section now reads:

- **aws** — the ACM certificate referenced by `certificate-arn` must cover
  `admin-api.<ingressHost>`, as a SAN or via a wildcard.
- **azure, gcp** — the new Ingress gets its own `tlsSecretName` (for example
  `iverson-admin-api-tls`) holding a certificate for `admin-api.<ingressHost>`, following the
  one-Secret-per-Ingress pattern the three existing Ingresses already use on those profiles. It
  must not reuse `iverson-api-tls`, whose certificate names the bare host.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 9 §2.1 (`clusterCidrs` for two of five profiles)** — resolved. The table now carries all
  five, each value traced to its terraform module, and the enforcement sentence names all four
  mechanisms in one coherent list rather than leaving AWS under "the two local profiles".
- **Round 9 §2.2 (Ingress specified for ALB only)** — resolved for `className`, annotations and the
  choice of TLS mechanism, with laptop correctly excluded via `adminUi.enabled: false`. §2.1 of this
  round is the adjacent question that fix did not reach: what the certificate must *contain*.
- **Round 8 §2.1 / §2.2 (phantom interface member; extraction count)** — still resolved; the
  endpoint table and both extraction paragraphs read correctly on a fresh pass.
- **Round 8 §2.3 (Ingress annotation set)** — still resolved for AWS and now generalised.
- **Round 7 §2.1-§2.3 and §3.1-§3.2** — all still resolved or dissolved by the hostname change.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**

One §2 item, no forced decisions.

The finding sits in the same seam as the last two rounds — a per-profile fact stated in the terms of
one profile — but it is the narrowest instance yet, and the surrounding surface came through clean.
Everything the previous three rounds corrected held on a fresh read, the profile enumeration round 9
opened is now closed for every value it applies to, and 4d's blueprint mechanism was verified
directly for the first time. Two candidates that would have padded this review were generated and
dropped on the evidence: the 4c headless-Service reading does not break either ingress controller,
and the missing cloud DNS record is a pre-existing condition affecting every hostname in the chart
rather than something this design introduces.
