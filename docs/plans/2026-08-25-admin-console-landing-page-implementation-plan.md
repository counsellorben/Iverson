# Admin Console Landing Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-25-admin-console-landing-page-design.md` (commit SHA: `e271e77`)

**Goal:** Give the admin console a landing page at `/` showing nine read-only widgets across three source bands, and land the prerequisite and security work the spec scoped alongside it.

**Architecture:** The console reaches `Iverson.Api` on a dedicated hostname, `admin-api.<ingressHost>`, over five JSON endpoints under `/admin/console/` plus the existing `/health`. Three `pathType: Prefix` ingress paths carry it, with no rewrite, so one rule behaves identically on ingress-nginx and on every cloud controller. The console fetches each widget independently through one shared polling hook; no page-level load gate.

**Tech stack:** .NET 10 minimal APIs on `Iverson.Api`; React 19 + MUI 9 + react-router 8 + `react-oidc-context` in `Iverson.AdminUI`; vitest 3.2 for console tests; Helm with six values files (base + five deployment profiles); Authentik blueprints for identity.

---

## Global Constraints

Project-wide rules every task must hold to. Copied from the spec.

- **The `operators` group name is fixed by two independent consumers** and is not a free choice: `OperatorAuthorizationPolicy.cs:11` tests `groupClaims.Contains("operators")`, and `Sidebar.tsx:20` gates the Tenants nav item on the same string.
- **The new `admin-api` Ingress must never carry `alb.ingress.kubernetes.io/backend-protocol-version: GRPC`.** It serves HTTP/1.1 JSON; a gRPC target-group declaration breaks every console call on AWS.
- **Every per-profile value must be supplied for all five deployment profiles**, not two. `.github/workflows/deploy-validate.yml:28-33` runs `helm lint` against `values-local`, `values-laptop`, `values-aws`, `values-azure`, `values-gcp`. Overlays are self-contained; a key absent from an overlay is absent, not inherited.
- **`/metrics` stays anonymous and stays off the `admin-api` host.** Prometheus scrapes it without auth; routing, not authentication, is the control.
- **Commit messages are plain lowercase imperative sentences with no Conventional-Commits prefix**, matching `git log --oneline -12`.

## File Structure

**Create**
- `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/operators-group.yaml` — the `operators` group, top-level so Helm's glob and compose's bind-mount both pick it up
- `Iverson.Server/deploy/helm/iverson/charts/api/templates/admin-api-ingress.yaml` — the console-facing Ingress
- `Iverson.Server/deploy/helm/iverson/charts/api/templates/headless-service.yaml` and `charts/worker/templates/headless-service.yaml` — per-pod DNS for Prometheus
- `Iverson.Server/Iverson.Api/Schema/SchemaCatalogReader.cs` — the two-pass catalog algorithm, extracted
- `Iverson.Server/Iverson.Api/Search/AggregateReader.cs` — count-per-type over `IEngagementStoreSearchService`
- `Iverson.Server/Iverson.Api/Console/AdminConsoleEndpoints.cs` — the five endpoint registrations
- `Iverson.Server/Iverson.Vector/IntelligenceCollectionReader.cs` — read interface over `QdrantClient`
- `Iverson.AdminUI/src/api/` — fetch layer built on `config.apiBaseUrl`
- `Iverson.AdminUI/src/hooks/usePolledResource.ts` — the shared polling hook
- `Iverson.AdminUI/src/pages/LandingPage.tsx` and `src/widgets/` — the page and its nine widgets
- `Iverson.AdminUI/src/auth/RequireGroup.tsx` — route-level group guard

**Modify**
- `Iverson.Server/Iverson.Api/Program.cs` — probe authorization, `/health` cache, CORS, endpoint registration, listener binding
- `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`, `Grpc/ObjectSearchGrpcService.cs` — delegate to the extracted readers
- `Iverson.Server/deploy/helm/iverson/templates/networkpolicies.yaml` — api ingress rules on both ports, Prometheus both directions
- `Iverson.Server/deploy/helm/iverson/values.yaml` + all five overlays — `clusterCidrs`, the `admin-api` Ingress block, `apiBaseUrl`
- `Iverson.Server/deploy/helm/iverson/charts/prometheus/templates/configmap.yaml`, `deploy/prometheus/prometheus.local.yml` — scrape targets
- `Iverson.Server/docker-compose.yml` — publish 8081
- `Iverson.AdminUI/src/auth/AuthProvider.tsx`, `src/telemetry.ts`, `src/router.tsx`, `src/config.ts`
- `Iverson.AdminUI/.env.development`, `nginx.conf`, `docker-entrypoint.sh`
- `docs/user-management-and-security.md` — the `/etc/hosts` line
- `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml` — dev-user membership

**Test**
- `Iverson.Server/Iverson.Api.Tests/` — existing suites must stay green through the T5 extraction; new tests for the endpoints and the cache
- `Iverson.AdminUI/src/**/*.test.tsx` — hook unit tests with fake timers, widget render tests, guard unreachability test, and an update to the existing `router.test.tsx`

## Inherited from spec

The spec's `Verified assumptions` section carries **A1–A70**, verified by `thorough-brainstorming` and across ten `critical-design-review` rounds. They are trusted as ground truth here and are **not** re-verified. Rather than duplicate seventy rows, each task below cites the specific items it leans on by number; the authoritative text is the spec at `e271e77`.

The load-bearing ones, by area:

- **Identity (T1):** A27–A29 — no human identity satisfies `Operator` today; the console requests a scope that yields no `groups`; no `operators` group exists.
- **Transport (T4):** A55, A56, A69, A70 — the api subchart sees `global.ingressHost`; a second hostname is an established pattern; the cloud profiles use three ingress classes and two TLS mechanisms; no certificate covers the new host yet.
- **Endpoints (T5, T6, T7):** A58, A59 — the acting-user interceptor is gRPC-only and the evaluator grants full access with a null principal; A65, A66 — `GetCollectionInfoAsync` is not on `IVectorSchemaManager`, and the aggregate path is private.
- **Deployment (T3):** A68 — five deployment profiles, overlays self-contained; A53, A54 — the AWS VPC CIDR default and Calico enforcement in kind.
- **Console (T9–T12):** A47, A48, A49 — `revokeTokensOnSignout` passes through, `onSigninCallback` is a real prop, `applyCustomAttributesOnSpan` has the shape the fix uses; A63 — the four gRPC-Web npm dependencies are unused.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | `Iverson.Api` groups services in domain folders; new readers belong in `Schema/` and a new `Search/` | `ls Iverson.Api/*/` returns `Authorization/ Consumers/ Grpc/ Reconciliation/ Schema/ Tenancy/`; root holds only policies and accessors |
| 2 | File path | `Iverson.AdminUI/src/` has no `api/` or `hooks/` directory; the convention is lowercase feature folders | `ls -d src/*/` returns `auth/ layout/ pages/ theme/` |
| 3 | File path | `docs/runbooks/grpc-admin-auth-cutover.md` exists, so T1's membership note has a home beside it | present in `docs/runbooks/` |
| 4 | Signature | `ITenantRepository.ListAsync()` returns `Task<IEnumerable<TenantRow>>` | `Iverson.Sql/IRecordStoreRoles.cs:125` |
| 5 | Signature | `ObjectSearchGrpcService`'s search dependency is `IEngagementStoreSearchService`, injected by primary constructor alongside `SchemaRegistry`, `IActingUserAccessor` and `IRowFieldAuthorizationEvaluator` | `Grpc/ObjectSearchGrpcService.cs:30-40` |
| 6 | Signature | The Qdrant client is `QdrantClient`, injected directly, and every call must be wrapped in `RequestHeaders.Use("api-key", apiKey)` | `Iverson.Vector/IntelligenceCollectionManager.cs:9-16` |
| 7 | Signature | The `IMemoryCache` convention is a primary constructor plus a `private static readonly TimeSpan Ttl`, with `TryGetValue`/`Set(key, value, Ttl)` | `Iverson.Api/Tenancy/TenantStatusCache.cs:6-24` |
| 8 | Command | The console test command is `npm test`, which runs `vitest run` | `Iverson.AdminUI/package.json:8` |
| 9 | Command | .NET tests run per project, e.g. `dotnet test Iverson.Server/Iverson.Api.Tests`; `Iverson.slnx` carries 24 projects | `ls Iverson.Server/*Tests*` returns 8 test projects incl. `Iverson.Api.Tests` |
| 10 | Command | Chart changes are CI-gated by `helm lint` against exactly five overlays, then kubeconform | `.github/workflows/deploy-validate.yml:28-33` loops `values-local values-laptop values-aws values-azure values-gcp` |
| 11 | Command | Commit messages are plain lowercase imperative with no prefix | `git log --oneline -12` — "add …", "applied …", "replace …" |
| 12 | Consumer impact | **`router.test.tsx` asserts the index route redirects to `/performance`** and breaks when T10 replaces it | `src/router.test.tsx:32,49` — `expect(window.location.pathname).toBe("/performance")` |
| 13 | Consumer impact | `GetSchema` and `Aggregate` have callers beyond their own services, so T5 must preserve behaviour and keep existing suites green | `Iverson.ClientConformance/Requirements.cs`, `Scenarios/SchemaCatalogScenario.cs`, `Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`, `ObjectSearchGrpcServiceTests.cs`, `Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs` |
| 14 | Consumer impact | No test references the four probe endpoints, so T2's authorization change breaks no suite | `grep -rn "/probe/" Iverson.Api.Tests/` returns nothing |
| 15 | Consumer impact | Dropping `profile`/`email` from the requested scope does not regress `AppLayout.tsx:9`, which already renders the literal `"User"` because neither mapping is bound to the provider | spec A28; `AppLayout.tsx:9` unchanged by this plan |
| 16 | Ordering | T5's readers are consumed only by T6; T7's metrics endpoint uses a Prometheus `HttpClient` and neither reader | draft task inputs; no shared symbol |
| 17 | Ordering | T3's NetworkPolicy rules are a **runtime** dependency of T7, not a build one — T7 compiles and tests without them | netpol governs pod-to-pod traffic only |
| 18 | Sibling sweep | Every one of the six values files carries an `adminUi:` block, so `apiBaseUrl` has a home in each | `grep -c '^adminUi:'` returns 1 for all six |
| 19 | Sibling sweep | Three files are modified by more than one task — `Program.cs` (T2, T4, T6, T7, T14), `AuthProvider.tsx` (T1, T12), `router.tsx` (T10, T12) — and each such task carries an Interfaces block | draft task file lists |
| 20 | Code validity | `AddCors`/`UseCors` and `AddEndpointFilter` need no package reference; they ship in the ASP.NET Core shared framework used by `Iverson.Api.csproj` (`net10.0`) | `Iverson.Api.csproj:4` `<TargetFramework>net10.0</TargetFramework>`; no CORS package referenced anywhere today |
| 21 | File path | `charts/worker/templates/` exists and already holds a ClusterIP `service.yaml`, so T8's headless Service sits alongside it rather than replacing it | `ls charts/worker/templates/` returns `deployment.yaml hpa.yaml service.yaml` |
| 22 | File path | `docs/runbooks/grpc-admin-auth-cutover.md` exists, so T1 Step 4's membership note has the neighbour the spec names | `ls docs/runbooks/` returns it among seven runbooks |

## Tasks

### Task 1: Identity — make the `Operator` policy satisfiable

Every Operator-gated surface returns 403 until this lands, so it runs first. Leans on spec A27–A29.

**Files:**
- Create: `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/operators-group.yaml`
- Modify: `Iverson.AdminUI/src/auth/AuthProvider.tsx`, `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml`, `docs/runbooks/`

**Interfaces:**
- Produces: the `operators` group and a token carrying `groups` and `tenant_id`. T6, T7 and T10's tenant-roster widget all depend on it at runtime.
- Note for T12: this task changes `oidcConfig`'s `scope` line in `AuthProvider.tsx`. T12 adds two further settings to the same object; it must preserve this scope string.

- [ ] **Step 1: Request the claims the policy reads.** In `AuthProvider.tsx`, change `scope` to `"openid groups tenant_id offline_access"`. Drop `profile` and `email`: neither mapping is bound to this provider, so requesting them yields an empty `scope` claim and nothing else.

- [ ] **Step 2: Create the `operators` group blueprint** at the path above, top-level so `blueprints-configmap.yaml`'s `blueprints/*.yaml` glob and `docker-compose.yml:294,328`'s bind-mount both pick it up:
```yaml
version: 1
metadata:
  name: Iverson operator group
entries:
  - model: authentik_core.group
    identifiers:
      name: operators
    attrs: {}
```

- [ ] **Step 3: Add compose dev-user membership** in `blueprints/compose-only/service-clients.yaml`, following the `groups:` attribute pattern the file already uses for `iverson-loadtest-bypass-user`. Do **not** blueprint membership in any deployment overlay — seeding a user into an operator group in a production values file is a privilege-escalation defect.

- [ ] **Step 4: Record the real-deployment onboarding step** in a runbook beside `docs/runbooks/grpc-admin-auth-cutover.md`: who holds operator rights is an operational decision, granted through Authentik, not through a blueprint.

- [ ] **Step 5: Verify against the running stack.** Mint a token for a compose dev user and confirm the `groups` claim contains `operators`; confirm `/admin/dlq` returns 200 rather than 403.

- [ ] **Step 6: Commit**
```bash
git add Iverson.AdminUI/src/auth/AuthProvider.tsx Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/operators-group.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml docs/runbooks/
git commit -m "make the Operator policy satisfiable: request groups scope and create the operators group"
```

### Task 2: Operational endpoint authorization and the `/health` cache

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Program.cs:336-359` (probes), `:301-334` (`/health`)
- Test: `Iverson.Server/Iverson.Api.Tests/`

**Interfaces:**
- Consumes: T1's `operators` group, without which the gated probes 403 for everyone.
- Note for T4, T6, T7, T14: all touch `Program.cs`. This task changes only the probe registrations and the `/health` handler body.

- [ ] **Step 1: Gate the four probes.** Add `.RequireAuthorization("Operator")` to `/probe/sql`, `/probe/starrocks`, `/probe/vector` and `/probe/kafka`. Nothing calls them (plan assumption 14, spec A39), so no caller breaks. Leave `/metrics`, `/health` and `/health/live` anonymous.

- [ ] **Step 2: Cache `/health`'s fan-out for 5 seconds.** Memoize the four-way result behind `IMemoryCache` (already registered at `Program.cs:217`), following `Tenancy/TenantStatusCache.cs`'s pattern — a `private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5)`, `TryGetValue` then `Set`. Cache the checks result, not the `IResult`, so the 200/503 decision is re-derived each call. The window must stay under the readiness probe's 10-second default period.

- [ ] **Step 3: Test.** Assert an anonymous probe request is rejected, an Operator-authorized one succeeds, and that two `/health` calls inside the window issue one fan-out.
```bash
dotnet test Iverson.Server/Iverson.Api.Tests
```

- [ ] **Step 4: Commit**
```bash
git add Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api.Tests/
git commit -m "require Operator on the probe endpoints and cache the health fan-out"
```

### Task 3: NetworkPolicy and per-profile cluster CIDRs

**Files:**
- Modify: `Iverson.Server/deploy/helm/iverson/templates/networkpolicies.yaml`, `values.yaml` and all five overlays

**Interfaces:**
- Produces: the api→prometheus egress rule T7 needs at runtime, and the 8081 ingress rule T4's Ingress needs.

- [ ] **Step 1: Replace the port-8081 `from: []` rule** with the four legitimate consumers — `namespaceSelector` for ingress-nginx, `podSelector` for `{{ .Release.Name }}-prometheus`, and an `ipBlock` list from `.Values.networkPolicy.clusterCidrs` covering the kubelet and, on AWS, the ALB's pod-IP targets. The api policy has no Prometheus rule today (only `worker-ingress` does, at `:88-103`), so omitting it empties Band B.

- [ ] **Step 2: Fix the same gap on port 8080.** Its rule admits only the `ingress-nginx` namespace, which does not exist on AWS/Azure/GCP; add the same `ipBlock` list.

- [ ] **Step 3: Add the two Prometheus rules Design 1 requires** — an `api-egress` rule to `podSelector: { app: {{ .Release.Name }}-prometheus }` on TCP 9090, and a `prometheus-ingress` policy allowing from `app: {{ .Release.Name }}-api` on TCP 9090. Guard both with the existing `{{- if .Values.prometheus.enabled }}` used at `:474`.

- [ ] **Step 4: Make the key required and supply it for all five profiles.** Render must fail loudly on an empty list rather than emit an `ipBlock` matching nothing. Values: aws `["10.0.0.0/16"]`, azure `["10.1.0.0/16"]`, gcp `["10.2.0.0/20"]`, local and laptop `["172.18.0.0/16"]`.

- [ ] **Step 5: Verify the CI gate passes for every overlay.**
```bash
helm dependency build Iverson.Server/deploy/helm/iverson
for v in values-local values-laptop values-aws values-azure values-gcp; do
  helm lint Iverson.Server/deploy/helm/iverson -f "Iverson.Server/deploy/helm/iverson/$v.yaml"
done
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/deploy/helm/iverson/templates/networkpolicies.yaml Iverson.Server/deploy/helm/iverson/values.yaml Iverson.Server/deploy/helm/iverson/values-local.yaml Iverson.Server/deploy/helm/iverson/values-laptop.yaml Iverson.Server/deploy/helm/iverson/values-aws.yaml Iverson.Server/deploy/helm/iverson/values-azure.yaml Iverson.Server/deploy/helm/iverson/values-gcp.yaml
git commit -m "scope the api network policy to its real consumers on both ports"
```

### Task 4: Transport — the `admin-api` hostname, CORS, and local plumbing

**Files:**
- Create: `Iverson.Server/deploy/helm/iverson/charts/api/templates/admin-api-ingress.yaml`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`, six values files, `Iverson.Server/docker-compose.yml`, `Iverson.AdminUI/.env.development`, `docs/user-management-and-security.md`

**Interfaces:**
- Consumes: T3's port-8081 ingress rule.
- Produces: `config.apiBaseUrl` pointing at the new origin, which T9's fetch layer and T12's 5g both build on; and the `admin-api` origin T13's CSP interpolates.
- Note: touches `Program.cs` only to add CORS registration and `UseCors` placement.

- [ ] **Step 1: Create the Ingress** on `admin-api.{{ .Values.global.ingressHost }}`, mirroring `charts/authentik/templates/ingress.yaml:21`'s `printf` pattern, backed by the api service on port 8081, with three `pathType: Prefix` paths — `/admin`, `/health`, `/v1/traces` — and **no rewrite annotation**. Give it its own values-driven annotations block; never `backend-protocol-version: GRPC`.

- [ ] **Step 2: Supply its per-profile shape** in each values file, following the api Ingress's own shape in that same file: `alb` with `scheme`/`target-type`/`certificate-arn`/`listen-ports`/`ssl-redirect` on aws; `azure-application-gateway` and `gce` with their own `tlsSecretName` on azure and gcp; `nginx` on local. `values-laptop.yaml` needs none — it sets `adminUi.enabled: false`. On azure and gcp the Secret must be a **new** one covering `admin-api.<ingressHost>`; reusing `iverson-api-tls` fails the handshake on name mismatch.

- [ ] **Step 3: Add CORS to the API.** `AddCors` with the console origin from configuration — never `AllowAnyOrigin` — allowing the `Authorization` and `Content-Type` request headers. Do not set `AllowCredentials`: authentication is a bearer token, not a cookie. Place `UseCors` **before** `UseAuthentication`, so preflight `OPTIONS` is answered before the `FallbackPolicy` rejects it.

- [ ] **Step 4: Point `apiBaseUrl` at the new origin** in every values file that runs a console, and change `.env.development`'s `VITE_API_BASE_URL` from `8080` (the h2c port) to `8081`.

- [ ] **Step 5: Publish 8081 in compose.** `docker compose port iverson-api 8081` returns nothing today, so local development cannot reach the API at all.

- [ ] **Step 6: Add the hostname to the documented hosts line** in `docs/user-management-and-security.md:231-235`, joining `iverson.local` and `authentik.iverson.local`.

- [ ] **Step 7: Verify.** Run the five-overlay `helm lint` loop from Task 3 Step 5, then confirm a browser-shaped cross-origin `GET` to `/health` on 8081 returns 200 with the expected `Access-Control-Allow-Origin`.

- [ ] **Step 8: Commit**
```bash
git add Iverson.Server/deploy/helm/iverson/charts/api/templates/admin-api-ingress.yaml Iverson.Server/deploy/helm/iverson/values*.yaml Iverson.Server/Iverson.Api/Program.cs Iverson.Server/docker-compose.yml Iverson.AdminUI/.env.development docs/user-management-and-security.md
git commit -m "serve the console API on a dedicated admin-api hostname with CORS"
```

### Task 5: Extract the schema-catalog and aggregate readers

Both become services taking `ClaimsPrincipal?` as a parameter, so the gRPC method passes `_actingUserAccessor.ActingUser` and the endpoint passes `HttpContext.User`. Neither depends on a shared mutable accessor or on the gRPC interceptor.

**Files:**
- Create: `Iverson.Server/Iverson.Api/Schema/SchemaCatalogReader.cs`, `Iverson.Server/Iverson.Api/Search/AggregateReader.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`, `Grpc/ObjectSearchGrpcService.cs`, `Program.cs` (DI registration)

**Interfaces:**
- Produces: both readers, consumed by T6.

- [ ] **Step 1: Extract the schema-catalog reader.** Move `GetSchema`'s two-pass algorithm — pass one drops types under row-level denial or an empty authorized-field set, pass two emits relations and drops those whose related type did not survive — into a service depending on `SchemaRegistry` and `IRowFieldAuthorizationEvaluator`, with the principal as a parameter. Preserve the `OrdinalIgnoreCase` keying and the `__TenantId` exclusion exactly.

- [ ] **Step 2: Extract the aggregate reader.** Build a service over `IEngagementStoreSearchService`, `SchemaRegistry` and `IRowFieldAuthorizationEvaluator` that resolves a schema, evaluates authorization, constructs a COUNT `EngagementAggSpec`, and calls `search.AggregateAsync(SchemaBuilder.ToEngagementQuerySchema(schema), …)`. Because the endpoint holds the principal, it can report denial distinctly rather than returning an empty result the way `Aggregate` must.

- [ ] **Step 3: Repoint both gRPC methods at the readers**, changing no observable behaviour.

- [ ] **Step 4: Prove behaviour preservation.** `ObjectMappingGrpcServiceTests`, `ObjectSearchGrpcServiceTests` and the `Iverson.ClientConformance` schema-catalog scenario all exercise these paths and must pass **unchanged** — do not edit them to fit the refactor.
```bash
dotnet test Iverson.Server/Iverson.Api.Tests
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.Api/Schema/SchemaCatalogReader.cs Iverson.Server/Iverson.Api/Search/AggregateReader.cs Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api/Program.cs
git commit -m "extract the schema-catalog and aggregate readers from their gRPC services"
```

### Task 6: The four data endpoints

**Files:**
- Create: `Iverson.Server/Iverson.Api/Console/AdminConsoleEndpoints.cs`, `Iverson.Server/Iverson.Vector/IntelligenceCollectionReader.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/`

**Interfaces:**
- Consumes: T5's two readers; T1's `operators` group for the two Operator-gated rows.

- [ ] **Step 1: Add the Qdrant read interface.** `GetCollectionInfoAsync` is **not** on `IVectorSchemaManager` (spec A65) — it is called on a `QdrantClient` at `IntelligenceCollectionManager.cs:60`. Add a read interface over the same client, injecting `QdrantClient` directly and wrapping every call in `RequestHeaders.Use("api-key", apiKey)` as that file does, exposing collection listing plus points and indexed-vectors counts.

- [ ] **Step 2: Register four endpoints** under `/admin/console/`:
  - `GET /admin/console/tenants` — `Operator`, over `ITenantRepository.ListAsync()`
  - `GET /admin/console/schema` — **authenticated only**, passing `HttpContext.User` to the schema-catalog reader
  - `GET /admin/console/data-volume` — **authenticated only**, same principal handling, one count per type
  - `GET /admin/console/qdrant` — `Operator`, over the new read interface

- [ ] **Step 3: Do not normalise the two authenticated rows to `Operator`.** `GetSchema` carries no `[Authorize]` by design (`ObjectMappingGrpcService.cs:61-62`) and `ObjectSearchGrpcService` is mapped without one (`Program.cs:443`); gating them would change who can see what. They pass `HttpContext.User` explicitly because the acting-user interceptor is gRPC-only and the evaluator grants full access on a null principal (spec A58, A59).

- [ ] **Step 4: Return projections, not descriptors** — the schema endpoint returns object types with field counts and relation edges, which is what the widget renders.

- [ ] **Step 5: Test** each endpoint's authorization and response shape.
```bash
dotnet test Iverson.Server/Iverson.Api.Tests
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.Api/Console/ Iverson.Server/Iverson.Vector/IntelligenceCollectionReader.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api.Tests/
git commit -m "add the four admin console data endpoints"
```

### Task 7: The metrics endpoint

**Files:**
- Create: metrics endpoint in `Iverson.Server/Iverson.Api/Console/`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`, `appsettings.json`
- Test: `Iverson.Server/Iverson.Api.Tests/`

**Interfaces:**
- Consumes: T3's api→prometheus egress rule — a **runtime** dependency; this task compiles and tests without it.

- [ ] **Step 1: Add a Prometheus client.** The API wires only the *exporter* today (`Program.cs:71`, `:275`). Add a configuration key for the base URL and a named `HttpClient`.

- [ ] **Step 2: Handle Prometheus being absent gracefully** — `values-laptop.yaml` sets `prometheus.enabled: false`, so this is a real deployment state, not an error path.

- [ ] **Step 3: Register `GET /admin/console/metrics`** with `Operator`, returning a **fixed named result set** — nine values across four widgets. It must **not** accept a PromQL parameter: a pass-through turns an authenticated console endpoint into an open query interface over every metric the deployment emits.

- [ ] **Step 4: Use Prometheus-mangled names.** Dots to underscores, `_total` on counters, `_bucket`/`_sum`/`_count` on histograms, **and the unit appended where the instrument declares one**. The five gauges and counters declare no unit and mangle straight; the two duration metrics are declared in seconds, so the real series are `http_server_request_duration_seconds_*` and the HttpClient equivalent.

- [ ] **Step 5: Exclude probe and scrape traffic** from every RPC-health query by constraining `http_route`, dropping `/health`, `/health/live` and the scraping endpoint's route. The `/health` filter at `Program.cs:56-59` is on the *tracing* provider; the metrics provider is unfiltered, so those requests are counted. Filter in this endpoint's PromQL, not on the shared metrics provider — that would change what the whole deployment exports.

- [ ] **Step 6: Test** the mangled names and the route exclusions against fixture responses.
```bash
dotnet test Iverson.Server/Iverson.Api.Tests
```

- [ ] **Step 7: Commit**
```bash
git add Iverson.Server/Iverson.Api/Console/ Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api/appsettings.json Iverson.Server/Iverson.Api.Tests/
git commit -m "add the admin console metrics endpoint over a prometheus client"
```

### Task 8: Prometheus scrape targets

**Files:**
- Create: `charts/api/templates/headless-service.yaml`, `charts/worker/templates/headless-service.yaml`
- Modify: `charts/prometheus/templates/configmap.yaml`, `deploy/prometheus/prometheus.local.yml`

- [ ] **Step 1: Add headless Services** (`clusterIP: None`) exposing the metrics port for api and worker, **alongside** the existing ClusterIP Services — both Ingresses target those and must keep working.

- [ ] **Step 2: Switch the scrape config to `dns_sd_configs` with `type: A`.** Prometheus resolves the A records and creates one target per pod IP, fixing the non-monotonic counters that come from scraping a ClusterIP VIP behind 2-5 HPA replicas. This is chosen over `kubernetes_sd_configs` because the prometheus chart sets `automountServiceAccountToken: false` in both the ServiceAccount and the pod spec, with no RBAC objects — DNS discovery needs none of that.

- [ ] **Step 3: Add the missing local worker target.** `deploy/prometheus/prometheus.local.yml` scrapes only `iverson-api:8081`, so the fan-out backlog and DLQ/retry widgets are permanently empty in docker-compose.

- [ ] **Step 4: Verify** with the five-overlay `helm lint` loop.

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/deploy/helm/iverson/charts/api/templates/headless-service.yaml Iverson.Server/deploy/helm/iverson/charts/worker/templates/headless-service.yaml Iverson.Server/deploy/helm/iverson/charts/prometheus/templates/configmap.yaml Iverson.Server/deploy/prometheus/prometheus.local.yml
git commit -m "scrape api and worker per pod through headless services"
```

### Task 9: Console foundation — fetch layer and polling hook

**Files:**
- Create: `Iverson.AdminUI/src/api/`, `src/hooks/usePolledResource.ts`
- Test: `src/hooks/usePolledResource.test.ts`

**Interfaces:**
- Consumes: T4's `apiBaseUrl`.
- Produces: the hook and fetch layer every widget in T10 and T11 uses.

- [ ] **Step 1: Build the fetch layer** on `config.apiBaseUrl`, composing absolute URLs. It distinguishes **401 from source failure** and routes 401 to the existing `oidc-client-ts` renewal path — an expired token fails every widget at once, and nine identical "unauthorized" cards is the wrong output.

- [ ] **Step 2: Write `usePolledResource(fetcher, intervalMs)`**, carrying: the poll; **doubling backoff to a ceiling** then stopping with manual retry; **last good value retained** and marked stale with an "as of HH:MM" line rather than reverting to a spinner; and **pausing while the tab is hidden**. It takes the access token as an argument — `useAuth()` is React-context-only and a module-scope fetch layer cannot reach it.

- [ ] **Step 3: Unit-test** poll, backoff, stale and visibility transitions with fake timers.
```bash
cd Iverson.AdminUI && npm test
```

- [ ] **Step 4: Commit**
```bash
git add Iverson.AdminUI/src/api Iverson.AdminUI/src/hooks
git commit -m "add the console fetch layer and polled-resource hook"
```

### Task 10: Landing page and Band A widgets

**Files:**
- Create: `src/pages/LandingPage.tsx`, `src/widgets/` (health strip, tenant roster, schema catalog, data volume) + tests
- Modify: `src/router.tsx`, **`src/router.test.tsx`**

**Interfaces:**
- Consumes: T9's hook; T6's four endpoints.
- Note for T12: this task changes the index route in `router.tsx`. T12 adds guards to `/tenants` and `/tenant-admin` in the same file and must not disturb the index.

- [ ] **Step 1: Replace the index redirect** — `{ index: true, element: <Navigate to="/performance" replace /> }` becomes the landing page at `/`.

- [ ] **Step 2: Update `router.test.tsx`.** It asserts `window.location.pathname` is `/performance` after the index redirect (`:32,49`); that behaviour is being removed, so the test must assert the landing page renders instead. This is a required update, not an optional one.

- [ ] **Step 3: Health strip.** One tile per store from `/health`'s `checks` object. `checks.starrocks` has **three** states — it is a literal `"disabled"` string when the engagement store is off, not a boolean. **Ollama has no tile**; its state is inferred from the Band B latency widget. Poll at **60s** (Design 3's cadence table), because `/health` is write-bearing.

- [ ] **Step 4: Tenant roster and schema catalog.** Fetch on mount plus manual refresh; no polling. Both go through the console's existing OIDC token and return what that user is entitled to.

- [ ] **Step 5: Data volume.** One call per object type, on mount plus manual refresh, **never polled** — a 30-second timer turns an open tab into sustained aggregate load against StarRocks for a number that changes slowly. Label it **tenant-scoped**, not a deployment total. State that zero may mean denied rather than empty.

- [ ] **Step 6: Render-test each widget** over fixture responses, including error and stale states.
```bash
cd Iverson.AdminUI && npm test
```

- [ ] **Step 7: Commit**
```bash
git add Iverson.AdminUI/src/pages Iverson.AdminUI/src/widgets Iverson.AdminUI/src/router.tsx Iverson.AdminUI/src/router.test.tsx
git commit -m "add the landing page and its store-sourced widgets"
```

### Task 11: Band B and Band C widgets

**Files:**
- Create: five widgets in `src/widgets/` + tests

**Interfaces:**
- Consumes: T9's hook; T7's metrics endpoint; T6's qdrant endpoint.

- [ ] **Step 1: Four metrics widgets** — fan-out backlog (three gauges), DLQ and retry rate, RPC health, embedding latency. Poll at 30s.

- [ ] **Step 2: Label RPC health honestly.** `AddAspNetCoreInstrumentation` observes gRPC-over-HTTP/2 as HTTP requests, so "error percentage" is HTTP status; a gRPC call failing with a non-OK status inside a 200 response does not count. Present it as **transport** health; do not label it "RPC errors".

- [ ] **Step 3: Carry the embedding-latency caveat.** HTTP client metrics label by `server.address`, not by logical client name, so if the embeddings and enrichment base URLs resolve to the same host the p95 covers both.

- [ ] **Step 4: Qdrant stats** — points and indexed vectors per collection, polled at 30s.

- [ ] **Step 5: Render-test** each over fixtures including error and stale states.
```bash
cd Iverson.AdminUI && npm test
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.AdminUI/src/widgets
git commit -m "add the metrics and qdrant widgets"
```

### Task 12: Console security fixes

**Files:**
- Modify: `src/auth/AuthProvider.tsx`, `src/telemetry.ts`, `src/router.tsx`
- Create: `src/auth/RequireGroup.tsx` + test

**Interfaces:**
- Consumes: T1's scope string in `oidcConfig` (preserve it); T10's index route in `router.tsx` (leave it alone); T4's `apiBaseUrl` for 5g.

- [ ] **Step 1 (5a): Revoke tokens at signout.** Set `revokeTokensOnSignout: true`. `react-oidc-context` spreads unrecognised settings into the `UserManager`, so it passes straight through. Keep `offline_access`: this page polls for hours and the access token must renew, and without a refresh token `automaticSilentRenew` falls back to iframe silent renew the console has no handler for.

- [ ] **Step 2 (5b): Keep the OIDC code out of telemetry — both halves.** Pass `onSigninCallback` calling `history.replaceState` to strip `?code=&state=`, **and** pass `applyCustomAttributesOnSpan` to `DocumentLoadInstrumentation` overwriting `http.url`/`url.full` with `location.pathname`. The second is what covers the error path, where `CallbackPage.tsx:15-19` never navigates and the first never runs.

- [ ] **Step 3 (5e): Add a `RequireGroup` guard** mirroring `AuthGate`'s shape, on `/tenants` and `/tenant-admin`. Add a test asserting the route is **unreachable**, not merely unlinked — `Sidebar.test.tsx:57-64` only asserts link absence. This is explicitly **not** a change to the landing page at `/`, which shows Operator-gated widgets to every authenticated user by design and degrades per Design 3.

- [ ] **Step 4 (5g): Compose the OTLP URL from `apiBaseUrl`** — `${apiBaseUrl}/v1/traces`. The `ignoreUrls` self-trace guard derives from the same constant and follows automatically.

- [ ] **Step 5: Test.**
```bash
cd Iverson.AdminUI && npm test
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.AdminUI/src/auth Iverson.AdminUI/src/telemetry.ts Iverson.AdminUI/src/router.tsx
git commit -m "revoke tokens at signout, scrub the oidc code from traces, and guard privileged routes"
```

### Task 13: Serving security — CSP and config validation

**Files:**
- Modify: `Iverson.AdminUI/docker-entrypoint.sh`, `Iverson.AdminUI/nginx.conf`

**Interfaces:**
- Consumes: T4's `admin-api` origin, which the CSP must name.

- [ ] **Step 1 (5d): Validate before substituting.** Each of `OIDC_CLIENT_ID`, `OIDC_AUTHORITY` and `API_BASE_URL` must match `^[A-Za-z0-9:/._-]+$`; exit non-zero otherwise. `envsubst` has no notion of JavaScript syntax, so an unvalidated value containing a double quote breaks out of its string literal into code that runs on every page load. `jq` is **not** in the runtime image (spec A43), so JSON emission is not available.

- [ ] **Step 2 (5c): Emit the CSP at container start, not at build time.** `nginx.conf` is copied in at build time while both origins are per-environment values, so a baked `connect-src` either carries an unresolved placeholder or omits the origin — and omitting it blocks the discovery fetch and token exchange, so **login cannot complete**. Render the header in the entrypoint with both origins interpolated via `envsubst`.

- [ ] **Step 3: Set the directives this design dictates** — `connect-src 'self' <admin-api-origin> <authentik-origin>` (`'self'` alone is insufficient now the API is a separate origin), `style-src 'self' 'unsafe-inline'` for MUI/Emotion's runtime injection, `frame-ancestors 'none'`, plus `X-Content-Type-Options` and `Referrer-Policy`. `Strict-Transport-Security` belongs at the TLS-terminating ingress, not on this plaintext listener.

- [ ] **Step 4: Verify** by building the image and confirming the container starts, `config.js` renders, the CSP header is present with both origins, and a deliberately malformed value exits non-zero.

- [ ] **Step 5: Commit**
```bash
git add Iverson.AdminUI/docker-entrypoint.sh Iverson.AdminUI/nginx.conf
git commit -m "validate runtime config and emit the console csp at container start"
```

### Task 14: Bind operational endpoints to a listener

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/`

- [ ] **Step 1: Add an endpoint filter testing `HttpContext.Connection.LocalPort`** so the operational endpoints answer only on 8081. **Not** `RequireHost("*:8081")`: `RequireHost` matches the `Host` header, which behind an ingress carries the external hostname with no port, so the filter would reject legitimate traffic and admit nothing. `Connection.LocalPort` reads the accepting socket.

- [ ] **Step 2 (non-code, requires a deployed AWS environment): settle the ALB question.** Send `GET /metrics` and `POST /probe/kafka` over HTTP/2 to a deployed ALB configured with `backend-protocol-version: GRPC` and record the response. If it forwards, `/metrics` is already reachable from the internet and this finding is more urgent than its current rating. **This step cannot be completed from the repository** — leave it unchecked and report it if no such environment is available.

- [ ] **Step 3: Test** that a request arriving on the gRPC listener does not reach the operational endpoints.
```bash
dotnet test Iverson.Server/Iverson.Api.Tests
```

- [ ] **Step 4: Commit**
```bash
git add Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api.Tests/
git commit -m "serve the operational endpoints only on the http listener"
```

## Tasks NOT in this plan

Inherited verbatim from the spec's `Out of scope` section. A new spec → plan cycle is required to add any of these.

- The four existing stub pages. They stay stubs; this design does not fill them in.
- Jaeger traces. The console already relays browser spans to Jaeger via `/v1/traces`, but no widget reads trace data back.
- Any write or mutation surface, with one inherited exception: the health strip's source, `GET /health`, is itself write-bearing (`Program.cs:310-311` issues a Qdrant `EnsureCollectionAsync` and produces to `iverson.health.probe` on every call). Every other widget and both new RPCs are read-only.
- Alerting, thresholds, or notification. The page displays; it does not judge.
- Authentication changes, with one exception. The page uses the console's existing OIDC session and does not alter the login flow. Design 5a does change one session setting (`revokeTokensOnSignout`), which is remediation folded in here, not page behaviour.

## Known issues inherited from spec

These exist by design, accepted during brainstorming.

**Data volume can now report authorization denial.** Previously accepted as unsolved, because `Aggregate` returns an empty response on denial and distinguishing it would have meant changing that contract for all five clients. A server-side endpoint holds the caller's principal and calls `IRowFieldAuthorizationEvaluator` itself, so denial and genuinely-zero become distinguishable without touching the proto contract.

**The page has no cross-tenant aggregate view.** Data volume is tenant-scoped by design; an operator wanting deployment-wide row counts is not served by this page. No such surface exists today and inventing one is out of scope.

**The `Operator` policy blocker.** Verified live on 2026-08-25: no human identity satisfies it today, through two independent gaps — the console never requests the `groups` scope, and no `operators` group exists. Task 1 closes both, and every Operator-gated surface returns 403 until it lands.
