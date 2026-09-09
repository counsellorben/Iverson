# CSR Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-09-csr-remediation-design.md` (commit SHA: `c1020d2`)

**Goal:** Close all 12 findings from the 2026-09-09 whole-repo critical security review.

**Architecture:** Eleven independent tasks across six subsystems — the ASP.NET API, the Helm chart, five client SDKs, the Python agent, the admin-UI container, and CI. Two forward dependencies only: Task 9 consumes the `global.externalScheme` value Task 2 introduces, and Task 3's gating flag governs redirect URIs in the file Task 2 edits. Everything else is independent.

**Tech stack:** .NET 10 (`net10.0`), Helm 3, Kubernetes Ingress across four controller classes, Python 3.11+, TypeScript/vitest, Go 1.25, Java/Maven, nginx (`nginxinc/nginx-unprivileged:1.27-alpine`).

---

## Global Constraints

Project-wide rules every task must hold to. Values are verbatim from the spec.

- `global.externalScheme` — new Helm value, default `http`, set `https` in `values-aws.yaml`, `values-azure.yaml`, `values-gcp.yaml`. **Consumed only in chart templates**, never in a values file (Helm does not render values files as templates and this chart uses `tpl` nowhere).
- `global.provisionTestIdentities` — new Helm value, default **`false`**, set `true` in `values-local.yaml` and `values-laptop.yaml`.
- `Tenancy:SeedLegacyTenants` — new config key, bool, default **`false`**.
- There are **five** client SDKs (DotNet, Go, Java, Python, TypeScript) — `Iverson.Clients/Common` is shared protos, not an SDK.
- There are **six** values files: `values.yaml`, `values-local.yaml`, `values-laptop.yaml`, `values-aws.yaml`, `values-azure.yaml`, `values-gcp.yaml`.
- Commit messages: lowercase, descriptive, **no** Conventional-Commits prefix (matches `git log`).
- **No CI runs any test suite.** Every suite gate below is a local activity.

### Pre-flight (before Task 1)

- [ ] Create the worktree, then immediately run `git merge main --ff-only` inside it. Worktrees branch from `origin/main`, and `main` carries unpushed commits; skipping this loses them.
- [ ] Establish the suite baseline on `main` (see "Suite commands" below) and record it, so a pre-existing failure is not misattributed to this work.

### Suite commands (verified — see assumptions C1–C8)

| Suite | Command | Working directory |
|---|---|---|
| .NET (non-integration) | `dotnet test Iverson.slnx --filter 'Category!=Integration'` | repo root |
| Python agent | `pytest` | `Iverson.Agents/Python` |
| Python client | `pytest` | `Iverson.Clients/Python` |
| TypeScript client | `npm test` (runs `typecheck && vitest run`) | `Iverson.Clients/TypeScript` |
| Go client | `go test ./...` | `Iverson.Clients/Go` |
| Java client | `mvn test` | `Iverson.Clients/Java` |
| AdminUI | `npm test` (`vitest run`) | `Iverson.AdminUI` |
| Helm render | `helm template iverson Iverson.Server/deploy/helm/iverson -f <values-file>` | repo root |
| Helm schema | `… \| kubeconform -kubernetes-version 1.30.0 -summary -ignore-missing-schemas` | repo root |

Testcontainers note: prefix integration runs with `TESTCONTAINERS_RYUK_DISABLED=true` if the environment inherits it; Ryuk is enabled as of 2026-09-02.

---

## File Structure

**Create**
- `Iverson.Server/Iverson.StarRocks/DisabledEngagementStoreHealthCheck.cs` — no-op health check registered when engagement is off (Task 8)

**Modify — API**
- `Iverson.Server/Iverson.Api/Program.cs` — delete probe endpoints (T1), tenant-seeding gate (T3), connection-string throws (T8), `/v1/traces` limits (T10)
- `Iverson.Server/Iverson.Api/appsettings.json`, `appsettings.Development.json` — connection strings move (T8)
- `Iverson.Server/Iverson.Api/Tenancy/IdpAdminClient.cs` — pagination hardening (T10)
- `Iverson.Server/Iverson.Api/Reconciliation/ReconciliationService.cs` — operator message (T1)
- `Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs` — HAVING authorization (T6)

**Modify — Helm / compose**
- `charts/api/templates/ingress.yaml` (T1), `charts/api/templates/deployment.yaml` (T2, T3, T8)
- `charts/worker/templates/deployment.yaml` (T8)
- `charts/admin-ui/templates/deployment.yaml` (T2, T9), `charts/admin-ui/templates/ingress.yaml` (T9)
- `charts/admin-ui/values.yaml` (T2), `charts/authentik/templates/blueprints-configmap-service-clients.yaml` (T2, T3), `charts/authentik/templates/ingress.yaml` (T2, T9)
- `values.yaml` + the five environment values files (T2, T3)
- `Iverson.Server/docker-compose.yml` (T3, T8)

**Modify — clients / agent / UI / CI**
- `Iverson.Clients/Python/iverson_client/core.py`, `TypeScript/src/core.ts` (T4)
- `Iverson.Clients/Go/iverson/coordinator.go`, `Java/client/src/main/java/io/iverson/client/core/IversonClient.java`, `DotNet/Iverson.Client.Core/ServiceCollectionExtensions.cs` (T5)
- `Iverson.Agents/Python/iverson_agent/session.py`, `retrieval.py` (T7)
- `Iverson.AdminUI/docker-entrypoint.sh`, `nginx.conf` (T9)
- `.gitlab-ci.yml`, `.github/workflows/{codeql,deploy-validate}.yml`, four test `.csproj` (T11)

**Test**
- `Iverson.StarRocks.Tests/StarRocksQueryBuilderTests.cs` (T6), `Iverson.Agents/Python/tests/test_session.py` + `test_retrieval.py` (T7), `Iverson.Clients/Python/tests/test_auth.py` (T4)

**Docs**
- `docs/runbooks/chunk-collection-cleanup-2026-07.md`, `docs/user-management-and-security.md` (T1)

---

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and **not re-verified here**. Trusted as ground truth (spec §5):

- `/probe/*` has zero consumers — repo-wide grep, only the definitions in `Program.cs`
- All six gRPC services share proto package `iverson` — `Common/Proto/*.proto` line 3
- Prometheus does not scrape via the Ingress — `charts/prometheus/templates/configmap.yaml:12`, target `:8081`
- `/build` has no consumer through the Ingress — in-process test plus `BenchmarkQueryScenario.cs:172` on port 8081
- Admin UI makes no API calls — only OIDC and same-origin `/v1/traces`
- Admin orchestrator is production infrastructure — `charts/api/templates/deployment.yaml:158-160`
- Conformance harness needs the gated identities — it targets compose, whose static blueprint Task 3 does not touch
- Config/Helm wiring conventions exist — `Program.cs:171`, `deployment.yaml:94-95`
- `BuildAggregate` alias set `{bucket_key, doc_count, metric_val}`; `BuildGroupBy` alias set is `metric.Name`
- `_user_key` has one caller (`session.py:132`); `_render_one` is the single format point
- `.NET` insecure flag is unconditional — `ServiceCollectionExtensions.cs:93`
- HAVING field-authz gap is real — probe test emitted `HAVING \`Rating\` > @h0` for an unauthorized column
- The .NET test host loads `appsettings.Development.json`, **and `Engagement:Enabled` defaults true there**
- Task 3's blueprint gating does not break pod admission — Secrets render unconditionally
- Java TLS needs no new dependency — `grpc-netty-shaded` bundles a TLS provider
- No `Directory.Packages.props` exists; three tests pin HAVING behaviour; `test_session.py:243-251` pins `_user_key`

---

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | All 29 modified paths exist exactly as cited | Bulk existence check: 29/29 `ok`, plus 6 values files, 4 test `.csproj`, 2 workflows |
| 2 | File path | `DisabledEngagementStoreHealthCheck.cs` does not already exist | `ls` returned no such file |
| 3 | Command | .NET non-integration filter | `[Trait("Category", "Integration")]` at `ObjectSearchVectorIntegrationTests.cs:53` → `--filter 'Category!=Integration'` |
| 4 | Command | Both Python suites run bare `pytest` | `[tool.pytest.ini_options] testpaths = ["tests"]` in both `pyproject.toml` files |
| 5 | Command | TS `npm test` = `typecheck && vitest run`; AdminUI = `vitest run` | `TypeScript/package.json:16`; `AdminUI/package.json:8` |
| 6 | Command | Go = `go test ./...`; Java = Maven | `Go/go.mod` (module, go 1.25.0); `Java/client/pom.xml` present |
| 7 | Command | Commit style is lowercase, no prefix | `git log --format=%s -8` — e.g. `add postgres sigpipe ledger implementation plan` |
| 8 | Signature | `use_tls`/`useTls` is the **third positional** parameter in both Python and TS constructors | `core.py:828`; `core.ts:810` |
| 9 | Signature | `IEngagementStoreHealthCheck` has exactly two members: `CheckHealthAsync()`, `IsHealthyAsync()` | `IEngagementStoreRoles.cs:11-15` |
| 10 | Signature | `AddStarRocks(connectionString, EngagementResilienceOptions, engagementEnabled)`; `Engagement:Enabled` defaults **true** | `Program.cs:166-180`; `ServiceCollectionExtensions.cs:8-11` |
| 11 | Code validity | `IHttpMaxRequestBodySizeFeature` exists for `net10.0`; `RequestSizeLimitAttribute` does **not** (MVC-only) | `Microsoft.AspNetCore.App.Ref/10.0.12/ref/net10.0/Microsoft.AspNetCore.Http.Features.dll` |
| 12 | Code validity | A subchart template can read `.Values.global.*` | `charts/admin-ui/templates/ingress.yaml:14,18` already reads `.Values.global.ingressHost` |
| 13 | Code validity | Go TLS needs no new module — `credentials` is part of the existing grpc dependency | `Go/go.mod:6` — `google.golang.org/grpc v1.83.1` |
| 14 | Code validity | The admin-ui image already has a runtime hook: `/docker-entrypoint.d/40-admin-ui-config.sh` | `Iverson.AdminUI/Dockerfile:25-26` — extend this, do not introduce `/etc/nginx/templates` |
| 15 | Code validity | Patched SSH.NET exists past GHSA-q939-rpr3-3284 | nuget flat-container index: `2024.0.0 … 2026.0.0` |
| 16 | Consumer impact | **`oidcAuthority` lives in SIX places, not the four the spec named** — also `values-local.yaml:142` and the subchart default `charts/admin-ui/values.yaml:13` | `grep -rn oidcAuthority` across the chart |
| 17 | Consumer impact | Python: the agent already passes `use_tls=tls` explicitly; **seven test call sites rely on the default** | `__main__.py:72,75` explicit; `test_auth.py:148,191,220,250,266`, `test_conformance_driver.py:122` implicit |
| 18 | Consumer impact | TS: 12 non-generated call sites; some pass `false` explicitly (`sample/main.ts:22`, `conformance/driver.ts:356`), others rely on the default | grep of `new IversonClient(` |
| 19 | Consumer impact | Go: the default applies only when `opts` is empty; both conformance sites pass `WithInsecure()` explicitly | `coordinator.go:79-83`; `conformance/main.go:462,741` |
| 20 | Consumer impact | .NET: two `AddIversonClient` callers | `DotNet/Iverson.Client.Sample/Program.cs:34`; `Iverson.LoadTest/Program.cs:127` |
| 21 | Consumer impact | **`test_retrieval.py:144-152` asserts on token budgets** that adding delimiters will shift | `render_context(ctx, budget_tokens=260)` and `=60` assertions |
| 22 | Ordering | T9 consumes `global.externalScheme` (T2); T3's flag governs redirect URIs in T2's file — both forward, no cycle | Spec §3 Tasks 2, 3, 9 |
| 23 | Ordering | T8's new disabled health check is referenced by no other task | Only `Program.cs` T8 step registers it |
| 24 | Cross-cutting | `docs/plans/` is **gitignored** (`.gitignore:49`) yet 60 plans are tracked → commit needs `git add -f` | `git check-ignore -v` on the plan path |

**Deferred to task time** (need a live service, per spec §5): whether StarRocks requires `AllowPublicKeyRetrieval` (T8); the Authentik pagination envelope (T10); whether Authentik already emits `Access-Control-Allow-Origin` (T2); whether StarRocks *executes* the HAVING reference (T6 — severity only, the fix is identical either way).

---

## Tasks

### Task 1: Delete `/probe/*`; Ingress path allow-list

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Program.cs:345-368`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/api/templates/ingress.yaml:17-24`
- Modify: `docs/runbooks/chunk-collection-cleanup-2026-07.md:25`
- Modify: `docs/user-management-and-security.md:440`
- Modify: `Iverson.Server/Iverson.Api/Reconciliation/ReconciliationService.cs`

**Interfaces:**
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Delete the four anonymous probe endpoints.** Remove `/probe/sql`, `/probe/starrocks`, `/probe/vector` and `/probe/kafka` from `Program.cs`. They have zero consumers; `/health` already aggregates the same four checks. Leave `/health`, `/health/live`, `/build` and `/metrics` exactly as they are.

- [ ] **Step 2: Replace the Ingress rule with seven path rules.** In `charts/api/templates/ingress.yaml`, replace the single `path: /` / `pathType: Prefix` rule with one `Prefix` rule per gRPC service plus the trace relay:

```yaml
          - path: /iverson.ObjectMappingService
            pathType: Prefix
            backend: { service: { name: {{ .Release.Name }}-api, port: { number: 8080 } } }
          # …repeat for ObjectPersistenceService, ObjectRetrievalService, ObjectSearchService,
          #   TenantLifecycleGrpcService, TenantAdminGrpcService…
          - path: /v1/traces
            pathType: Prefix
            backend: { service: { name: {{ .Release.Name }}-api, port: { number: 8080 } } }
```

  Do **not** use a single `/iverson.` prefix: `pathType: Prefix` matches element-wise on `/`-split segments, so `/iverson.` never matches the first element `iverson.ObjectSearchService`. ingress-nginx would match it by string prefix (so kind passes) while ALB and GCE 404 every call.

  Do **not** add `/admin`: the admin-ui Ingress already owns `/admin(/|$)(.*)` on the same host.

- [ ] **Step 3: Correct the three operator-facing documents.** They currently instruct calling `/admin/*` at the ingress host, which has never worked (that path routes to the admin-ui SPA). Point each at in-cluster access (`kubectl port-forward` / `exec`): the runbook's `curl -X POST http://<api-host>/admin/reconcile/{TypeName}`, `ReconciliationService`'s "requires manual POST /admin/reconcile/{Type}" operator message, and the `iverson-admin-automation` description.

- [ ] **Step 4: Render and schema-check.** For each of the four values files the CI lints (`values-local`, `values-aws`, `values-azure`, `values-gcp`), run `helm template … | kubeconform -kubernetes-version 1.30.0 -summary -ignore-missing-schemas`. Confirm seven paths render on the api Ingress and no `/admin` rule appears.

- [ ] **Step 5: Validate against a non-nginx class.** kind only exercises `nginx`, which would mask an element-wise mismatch. Confirm the rendered ALB/GCE manifests carry one path pattern per service.

- [ ] **Step 6: Run the .NET suite.** `dotnet test Iverson.slnx --filter 'Category!=Integration'`

- [ ] **Step 7: Commit**
```bash
git add Iverson.Server/Iverson.Api/Program.cs \
        Iverson.Server/deploy/helm/iverson/charts/api/templates/ingress.yaml \
        Iverson.Server/Iverson.Api/Reconciliation/ReconciliationService.cs \
        docs/runbooks/chunk-collection-cleanup-2026-07.md docs/user-management-and-security.md
git commit -m "delete anonymous probe endpoints and allow-list the api ingress paths"
```

---

### Task 2: Transport scheme in chart templates

**Files:**
- Modify: `values.yaml`, `values-aws.yaml`, `values-azure.yaml`, `values-gcp.yaml`, `values-local.yaml`
- Modify: `charts/admin-ui/values.yaml:13`, `charts/admin-ui/templates/deployment.yaml:61-62`
- Modify: `charts/api/templates/deployment.yaml:137-138`
- Modify: `charts/authentik/templates/blueprints-configmap-service-clients.yaml:119,122,125,128`
- Modify: `charts/authentik/templates/ingress.yaml:6-9`

**Interfaces:**
- Produces: `global.externalScheme` — consumed by Task 9.

- [ ] **Step 1: Add `global.externalScheme`.** Default `http` in `values.yaml`; `https` in `values-aws.yaml`, `values-azure.yaml`, `values-gcp.yaml`. Leave `values-local.yaml` and `values-laptop.yaml` on the default.

- [ ] **Step 2: Template the two chart-template scheme sites.** `Authentication__ExternalIssuer` in `charts/api/templates/deployment.yaml:138`, and the four redirect/logout URIs in the blueprint at `:119,122,125,128`. Both become `{{ .Values.global.externalScheme }}://…`.

  `ExternalIssuer` matters most: it feeds `TokenValidationParameters.ValidIssuers`, and the blueprint's `issuer_mode: global` makes Authentik derive `iss` from the request — under HTTPS it issues `https://…`, and a stale `http://` entry rejects **every** human/acting-user token.

- [ ] **Step 3: Move `oidcAuthority` into the template and delete all six value entries.** Compose the URL in `charts/admin-ui/templates/deployment.yaml`:

```yaml
            - name: OIDC_AUTHORITY
              value: "{{ .Values.global.externalScheme }}://authentik.{{ .Values.global.ingressHost }}/application/o/iverson-api/"
```

  Then delete the entry from **all six sites** — `values.yaml:199`, `values-local.yaml:142`, `values-aws.yaml:152`, `values-azure.yaml:143`, `values-gcp.yaml:144`, **and the subchart default `charts/admin-ui/values.yaml:13`**. (The spec named four; verification found six.) A values-file entry cannot carry the templated scheme, because Helm never renders values files as templates.

- [ ] **Step 4 (experiment): settle the Authentik CORS question.** Bring up compose and check whether Authentik's OAuth2 token and JWKS responses already carry `Access-Control-Allow-Origin` for a registered redirect-URI origin.
  - If they do → **delete** the four `nginx.ingress.kubernetes.io/*cors*` annotations from `charts/authentik/templates/ingress.yaml:6-9`. They are ingress-nginx-only and have no producer on `alb`/`gce`, so removing them closes the gap rather than papering over it.
  - If they do not → template `cors-allow-origin` on `global.externalScheme` **and** record in the task report that `alb`/`gce` remain uncovered, naming the Authentik-side mechanism needed.
  - Record the verdict either way.

- [ ] **Step 5: Render all four values files** and confirm no `{{` survives into `OIDC_AUTHORITY`, and that `https` appears in the three cloud renders and `http` in local.

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/deploy/helm/iverson/values.yaml Iverson.Server/deploy/helm/iverson/values-local.yaml \
        Iverson.Server/deploy/helm/iverson/values-aws.yaml Iverson.Server/deploy/helm/iverson/values-azure.yaml \
        Iverson.Server/deploy/helm/iverson/values-gcp.yaml \
        Iverson.Server/deploy/helm/iverson/charts/admin-ui/values.yaml \
        Iverson.Server/deploy/helm/iverson/charts/admin-ui/templates/deployment.yaml \
        Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml \
        Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml \
        Iverson.Server/deploy/helm/iverson/charts/authentik/templates/ingress.yaml
git commit -m "template the external scheme across the chart instead of hardcoding http"
```

---

### Task 3: Gate test identities and tenant seeding

**Files:**
- Modify: `values.yaml`, `values-local.yaml`, `values-laptop.yaml`
- Modify: `charts/authentik/templates/blueprints-configmap-service-clients.yaml`
- Modify: `charts/api/templates/deployment.yaml`
- Modify: `Iverson.Server/Iverson.Api/Program.cs:438-439`
- Modify: `Iverson.Server/docker-compose.yml`

**Interfaces:**
- Consumes: the blueprint file Task 2 edited (sequential, same file).

- [ ] **Step 1: Add `global.provisionTestIdentities`,** default **`false`** in `values.yaml`; `true` in `values-local.yaml` and `values-laptop.yaml`.

- [ ] **Step 2: Wrap the dev-fixture blueprint entries** in `{{- if .Values.global.provisionTestIdentities }}`: the `iverson-acting-user-smoke-test` user; the `iverson-loadtest-bypass` group and user; the `loadtest` and `webtest` OAuth2 providers and applications; the `tenant_id_loadtest` and `tenant_id_webtest` scope mappings; the `iverson-loadtest-human` provider and application; and the `http://localhost:5173/*` redirect URIs.

  **Do not gate** `iverson-admin-orchestrator` or its API token — they back `Authentik__AdminToken` and the production `TenantAdmin` RPCs.

- [ ] **Step 3: Gate the tenant-seeding loop.** In `Program.cs:438-439`, wrap the five-tenant `SeedIfMissingAsync` loop in a `cfg.GetValue("Tenancy:SeedLegacyTenants", false)` check, following the existing `cfg.GetValue` convention at `:171`.

- [ ] **Step 4: Wire the env var** in `charts/api/templates/deployment.yaml`, following the `Engagement__Enabled` pattern at `:94-95`:

```yaml
            - name: Tenancy__SeedLegacyTenants
              value: {{ .Values.global.provisionTestIdentities | quote }}
```

- [ ] **Step 5: Keep compose working.** Add `Tenancy__SeedLegacyTenants=true` to both compose API services. compose uses the separate static `blueprints/compose-only/` file, which Helm does not template, so the conformance harness's identities are unaffected.

- [ ] **Step 6: Render both flag states.** `helm template` with `values-local.yaml` (flag true → fixtures present) and `values-aws.yaml` (flag false → fixtures absent, orchestrator still present).

- [ ] **Step 7: Run the .NET suite,** then commit
```bash
git add Iverson.Server/deploy/helm/iverson/values.yaml Iverson.Server/deploy/helm/iverson/values-local.yaml \
        Iverson.Server/deploy/helm/iverson/values-laptop.yaml \
        Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml \
        Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml \
        Iverson.Server/Iverson.Api/Program.cs Iverson.Server/docker-compose.yml
git commit -m "gate test identities and legacy tenant seeding behind an explicit flag"
```

---

### Task 4: SDK TLS default — Python and TypeScript

**Files:**
- Modify: `Iverson.Clients/Python/iverson_client/core.py:828`
- Modify: `Iverson.Clients/TypeScript/src/core.ts:810`
- Test: `Iverson.Clients/Python/tests/test_auth.py`, `test_conformance_driver.py`
- Modify: TS tests, `sample/main.ts`, `conformance/driver.ts` as needed

- [ ] **Step 1: Rewrite the pinning test first (TDD).** `test_auth.py:87` (`test_client_without_use_tls_and_credentials_uses_local_channel_credentials`) exists to hold the plaintext default — its docstring says so. Rewrite it to assert the **new** default: a client built with no `use_tls` uses `ssl_channel_credentials()`. It should fail before Step 2.

- [ ] **Step 2: Flip the Python default** to `use_tls: bool = True`. It is the third positional parameter, so callers passing it positionally are unaffected.

- [ ] **Step 3: Update the seven Python call sites that rely on the default.** `test_auth.py:148,191,220,250,266` and `test_conformance_driver.py:122` construct clients with no `use_tls`; add explicit `use_tls=False` wherever the test intends plaintext. `iverson_agent/__main__.py:72,75` already pass `use_tls=tls` and need no change.

- [ ] **Step 4: Flip the TypeScript default** to `useTls: boolean = true` and update the call sites that relied on the old default (`tests/schema-registrar.test.ts:772`, `tests/core.test.ts:128`, and any of the 12 non-generated sites not already passing `false`). `sample/main.ts:22` and `conformance/driver.ts:356` already pass `false` explicitly.

- [ ] **Step 5: Run both suites.** `pytest` in `Iverson.Clients/Python`; `npm test` in `Iverson.Clients/TypeScript` (includes typecheck).

- [ ] **Step 6: Commit**
```bash
git add Iverson.Clients/Python Iverson.Clients/TypeScript
git commit -m "default the python and typescript clients to tls"
```

---

### Task 5: SDK TLS default — Go, Java, .NET

**Files:**
- Modify: `Iverson.Clients/Go/iverson/coordinator.go:79-83`
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/IversonClient.java:46-78`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/ServiceCollectionExtensions.cs:87-99`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Sample/Program.cs:34`, `Iverson.Server/Iverson.LoadTest/Program.cs:127`

- [ ] **Step 1: Go — default to TLS when no dial options are supplied.** `coordinator.go:81` currently falls back to `grpc.WithInsecure()` when `opts` is empty; make the fallback `grpc.WithTransportCredentials(credentials.NewTLS(&tls.Config{}))`. `credentials` ships with the existing `google.golang.org/grpc v1.83.1`; no new module. Both conformance sites already pass `WithInsecure()` explicitly and are unaffected.

- [ ] **Step 2: Java — add TLS-by-default constructors.** The three `(host, port, …)` constructors at `:46,68,77` hardcode `.usePlaintext()`. Make them use transport security by default, and add an explicitly-named plaintext factory (e.g. `IversonClient.plaintext(host, port, …)`) for local use. `grpc-netty-shaded` bundles a TLS provider, so no dependency changes.

- [ ] **Step 3: .NET — make the insecure flag conditional.** `AttachCredentials` at `:93` sets `UnsafeUseInsecureChannelCallCredentials = true` unconditionally, defeating grpc-dotnet's own guard for every consumer. Thread an explicit opt-in through `AddIversonClient` and set the flag only when it is requested.

- [ ] **Step 4: Update the two `AddIversonClient` callers** — `DotNet/Iverson.Client.Sample/Program.cs:34` and `Iverson.Server/Iverson.LoadTest/Program.cs:127` — to pass the plaintext opt-in, since both target local h2c endpoints.

- [ ] **Step 5: Run the three suites.** `go test ./...`; `mvn test`; `dotnet test Iverson.slnx --filter 'Category!=Integration'`.

- [ ] **Step 6: Commit**
```bash
git add Iverson.Clients/Go Iverson.Clients/Java Iverson.Clients/DotNet Iverson.Server/Iverson.LoadTest/Program.cs
git commit -m "default the go, java and dotnet clients to tls"
```

---

### Task 6: Authorize HAVING clauses

**Files:**
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs:262,380,604-624`
- Test: `Iverson.Server/Iverson.StarRocks.Tests/StarRocksQueryBuilderTests.cs:2064,2619,2659`

- [ ] **Step 1: Rewrite the three pinned tests and add negative tests (TDD).**
  - `BuildGroupBy_HavingPropertyWithBacktick_EscapesEmbeddedBacktick` (`:2064`) passes `Property = "evil\`alias"` and asserts escaping. That property is neither an alias nor a column, so it must now be **rejected**; rewrite the assertion accordingly. `EscapeIdentifier` becomes defence-in-depth rather than the primary control.
  - `BuildHaving_PrefixOverload_UsesPrefix` (`:2619`) calls the four-argument overload — update for the new signature.
  - `BuildHaving_VectorSimilarClause_ThrowsInvalidArgument` (`:2659`) calls the three-argument form. Its `VectorClause()` sets `Property = "Name"`, which is neither an alias nor an authorized column in that fixture, so the rewrite must pin that the `VectorSimilar` guard at `:617-620` still fires **ahead of** the new validation at `:622`.
  - Add negative tests mirroring the existing GROUP BY / ORDER BY authorization tests, so all four clause families stay in lockstep.

- [ ] **Step 2: Extend the `BuildHaving` signature** to accept the column resolver, the authz context, and an explicit alias set. Validate each `clause.Property` as **either** a member of the statement's alias set **or** a column passing `IsFieldAllowed`, rejecting everything else with the same exception shape the sibling gates use. Keep the `VectorSimilar` guard first.

- [ ] **Step 3: Pass the alias sets at both call sites.** They differ and must be explicit, not inferred:
  - `BuildAggregate` (`:262`) — the fixed set `{bucket_key, doc_count, metric_val}`
  - `BuildGroupBy` (`:380`) — `request.Metrics.Select(m => m.Name)` plus the GROUP BY key columns (already authorized)
  - `StarRocksPipelineBuilder` (`:518`) already pre-validates via `RequireColumn` against `metricAliases` — leave it unchanged.

- [ ] **Step 4: Run the .NET suite.** `dotnet test Iverson.slnx --filter 'Category!=Integration'`

- [ ] **Step 5 (optional, records severity only): check StarRocks' HAVING semantics.** If a compose StarRocks is up, run `SELECT … GROUP BY … HAVING <non-grouped column> > n` and record whether it executes. The fix is identical either way; this only settles whether the original finding was a live bypass or a latent one.

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs \
        Iverson.Server/Iverson.StarRocks.Tests/StarRocksQueryBuilderTests.cs
git commit -m "authorize having clause properties against aliases and allowed fields"
```

---

### Task 7: Agent cache key and document delimiters

**Files:**
- Modify: `Iverson.Agents/Python/iverson_agent/session.py:97-104,187`
- Modify: `Iverson.Agents/Python/iverson_agent/retrieval.py:168`
- Test: `Iverson.Agents/Python/tests/test_session.py:243-251`, `tests/test_retrieval.py:144-152`

- [ ] **Step 1: Replace the `_user_key` test with the security property (TDD).** `test_user_key_is_the_jwt_subject_when_the_token_is_a_jwt` pins exactly the behaviour being removed. Replace it with a test asserting that a token carrying another user's `sub` does **not** collide with that user's cache key, and that distinct tokens yield distinct keys.

- [ ] **Step 2: Key the cache on the token, not on an unverified claim.** Replace `_user_key`'s body with `hashlib.sha256(token.encode()).hexdigest()`. The current implementation base64-decodes the JWT payload and returns `sub` without verifying the signature; because `SchemaCache.get` returns on a cache hit without contacting the server, a forged `sub` returns another user's schema. No LRU bound — entries are created only after a successful server round-trip.

- [ ] **Step 3: Delimit documents in `_render_one`.** Wrap each rendered document in explicit delimiters with a provenance marker. This is the single formatting point, covering both the initial page and tool results.

- [ ] **Step 4: Add a data-not-instructions line to `REASONER_SYSTEM`** (`session.py:22-29`) stating that document content is data and never instructions.

- [ ] **Step 5: Adjust the render-budget assertions.** `test_retrieval.py:144-152` asserts truncation behaviour at `budget_tokens=260` and `=60`; the delimiters add tokens per document, so these thresholds shift. Re-derive them from the new rendering rather than loosening the assertions.

- [ ] **Step 6: Run the agent suite.** `pytest` in `Iverson.Agents/Python`.

- [ ] **Step 7: Commit**
```bash
git add Iverson.Agents/Python
git commit -m "key the agent schema cache on the token and delimit retrieved documents"
```

---

### Task 8: Connection strings fail closed

**Files:**
- Create: `Iverson.Server/Iverson.StarRocks/DisabledEngagementStoreHealthCheck.cs`
- Modify: `Iverson.Server/Iverson.Api/appsettings.json`, `appsettings.Development.json`
- Modify: `Iverson.Server/Iverson.Api/Program.cs:163-180`
- Modify: `Iverson.Server/Iverson.StarRocks/ServiceCollectionExtensions.cs`
- Modify: `Iverson.Server/docker-compose.yml:459,544`
- Modify: `charts/api/templates/deployment.yaml:92`, `charts/worker/templates/deployment.yaml:85`

- [ ] **Step 1: Move the dev credentials out of the shipped image.** Delete both connection strings from `appsettings.json` and put the dev values in `appsettings.Development.json`. The `??` fallbacks in `Program.cs` are dead code today precisely because `appsettings.json` supplies the values — the credentials ship inside the container image.

  **The .NET test host loads `appsettings.Development.json`** (Mvc.Testing sets the environment to Development), **and `Engagement:Enabled` defaults `true` there**, so the StarRocks string must land in that file too or `Iverson.Api.Tests` will throw at startup after Step 3.

- [ ] **Step 2: Give compose an explicit Postgres string.** compose runs `ASPNETCORE_ENVIRONMENT=Production` and currently relies on `appsettings.json` for `ConnectionStrings__Postgres`; add it explicitly to both API services. (compose already sets `ConnectionStrings__StarRocks`.)

- [ ] **Step 3: Make the configuration actually fail closed.**
  - **Postgres** is always required: `cfg.GetConnectionString("Postgres") ?? throw new InvalidOperationException(...)`. Deleting the `??` alone yields `null`, not a fail-closed startup.
  - **StarRocks** is required only when engagement is on. Throw when `Engagement:Enabled` is true and the string is missing.

- [ ] **Step 4: Add the disabled health check and register it when engagement is off.** Create `DisabledEngagementStoreHealthCheck` implementing `IEngagementStoreHealthCheck`'s two members (`CheckHealthAsync()`, `IsHealthyAsync()`), placed beside `DisabledEngagementStoreSearchService`.

  **Mirror that class's shape but not its behaviour:** the search service *throws*, which is correct for a query path but wrong here — `/health` calls `CheckHealthAsync()` on every request and the api Deployment's `readinessProbe` polls `/health`. Return a benign status so `/health` takes the `"disabled"` branch it already has at `Program.cs:330`.

  Then in `AddStarRocks`, register this implementation instead of `EngagementHealthChecker` when `engagementEnabled` is false. **Do not skip the `AddStarRocks` call** — `IEngagementStoreQueryExecutor`, `IEngagementStoreEntityStore` and `IEngagementStoreHealthCheck` are all registered unconditionally and have unconditional consumers (`/health`, `EngagementStoreConsumer`); skipping it leaves the readiness probe unresolvable on the `engagementEnabled: false` profile (`values-laptop.yaml:13`).

- [ ] **Step 5 (experiment): remove `AllowPublicKeyRetrieval=true` from all four configuration sites** — `appsettings.json` (moved in Step 1), `charts/api/templates/deployment.yaml:92`, `charts/worker/templates/deployment.yaml:85`, and both compose services — then bring up compose and confirm the StarRocks connection still works. The verdict applies to api and worker **together**: if StarRocks genuinely requires the flag, restore it in both and record why in the task report.

- [ ] **Step 6: Run the .NET suite** and render `values-laptop.yaml` (engagement off) plus `values-local.yaml` (engagement on) to confirm both profiles start.

- [ ] **Step 7: Commit**
```bash
git add Iverson.Server/Iverson.Api/appsettings.json Iverson.Server/Iverson.Api/appsettings.Development.json \
        Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.StarRocks Iverson.Server/docker-compose.yml \
        Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml \
        Iverson.Server/deploy/helm/iverson/charts/worker/templates/deployment.yaml
git commit -m "fail closed on missing connection strings and stop shipping dev credentials"
```

---

### Task 9: Security headers

**Files:**
- Modify: `Iverson.AdminUI/nginx.conf`, `Iverson.AdminUI/docker-entrypoint.sh`
- Modify: `charts/admin-ui/templates/deployment.yaml`, `charts/admin-ui/templates/ingress.yaml`
- Modify: `charts/api/templates/ingress.yaml`

**Interfaces:**
- Consumes: `global.externalScheme` (Task 2).

- [ ] **Step 1: Add the header block to `nginx.conf`** — `X-Frame-Options: DENY`, CSP `frame-ancestors 'none'`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, a `Content-Security-Policy` of `default-src 'self'` plus `connect-src 'self' ${OIDC_AUTHORITY}`, and `Strict-Transport-Security` emitted only when the scheme is `https`. Check whether the Vite build emits inline styles requiring `style-src 'self' 'unsafe-inline'`.

- [ ] **Step 2: Substitute the two parameters at container start.** Helm cannot reach `nginx.conf` — it is `COPY`'d into the image at build time and no ConfigMap is mounted over it. Extend the **existing** hook `Iverson.AdminUI/docker-entrypoint.sh` (already installed as `/docker-entrypoint.d/40-admin-ui-config.sh`) to `envsubst` `${OIDC_AUTHORITY}` and a new `${EXTERNAL_SCHEME}` into the nginx config, exactly as it already does for `config.js`. Do not introduce a second mechanism.

  The authority is a **different origin** from the console (`authentik.<ingressHost>` vs `<ingressHost>`) and the OIDC flow calls it cross-origin for discovery, token exchange and JWKS — a CSP that cannot name it breaks login everywhere but the environment the image was built for.

- [ ] **Step 3: Wire `EXTERNAL_SCHEME`** into `charts/admin-ui/templates/deployment.yaml` from `{{ .Values.global.externalScheme }}`.

- [ ] **Step 4: Add the per-controller Ingress annotations.** There is no portable response-header annotation, so each class gets its own answer:

| Class | Mechanism |
|---|---|
| `nginx` | `configuration-snippet` (requires `allow-snippet-annotations: true`, disabled by default since ingress-nginx v1.9) or the `custom-headers` annotation plus a controller-namespace ConfigMap |
| `azure-application-gateway` | A pre-provisioned App Gateway rewrite rule set referenced by annotation; the header text cannot live in the annotation |
| `alb` | No response-header annotation exists — **headers come from the origin only** |
| `gce` | No response-header annotation exists — **headers come from the origin only** |

  Record the two external prerequisites this takes on (the ingress-nginx controller setting and the AGIC rewrite rule set) in the task report. Note the api Ingress serves gRPC, which no browser renders, so the mirror's value there is materially lower than on the admin-ui Ingress.

- [ ] **Step 5: Render all four values files;** confirm the annotations differ per class and that HSTS appears only under `https`.

- [ ] **Step 6: Run the AdminUI suite.** `npm test` in `Iverson.AdminUI`.

- [ ] **Step 7: Commit**
```bash
git add Iverson.AdminUI/nginx.conf Iverson.AdminUI/docker-entrypoint.sh \
        Iverson.Server/deploy/helm/iverson/charts/admin-ui/templates \
        Iverson.Server/deploy/helm/iverson/charts/api/templates/ingress.yaml
git commit -m "serve security headers from the admin ui and mirror them per ingress controller"
```

---

### Task 10: `/v1/traces` limit and Authentik pagination

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Program.cs:456-470`
- Modify: `Iverson.Server/Iverson.Api/Tenancy/IdpAdminClient.cs:57-101`
- Test: `Iverson.Server/Iverson.Api.Tests/`

- [ ] **Step 1: Bound the relay body.** Set a maximum request body size on `/v1/traces` via `IHttpMaxRequestBodySizeFeature` (verified present for `net10.0`; `RequestSizeLimitAttribute` is MVC-only and not available here). This is the half that addresses the unbounded-relay finding.

- [ ] **Step 2: Allow-list both content types.** Accept `application/json` **and** `application/x-protobuf`. Do **not** restrict to protobuf alone: the endpoint's only consumer is the admin UI's browser OTel SDK, which sends JSON (`JsonTraceSerializer`, hardcoded `Content-Type: application/json`, and no `-proto` exporter is a dependency). A protobuf-only restriction would 415 every export silently. Correct the endpoint's comment, which asserts protobuf and is the source of the error.

- [ ] **Step 3 (experiment): verify the Authentik pagination envelope** against a running Authentik (compose) or its `/api/v3/schema/` document. `ListUsersByTenantAsync` infers the shape and the class comment records it was never verified.

- [ ] **Step 4: Treat an unrecognised envelope as an error,** not as end-of-list. Truncation currently fails closed in `RequireUserInTenantAsync` (safe) but fails open in user listing and offboarding (not safe).

- [ ] **Step 5: Run the .NET suite,** then commit
```bash
git add Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api/Tenancy/IdpAdminClient.cs \
        Iverson.Server/Iverson.Api.Tests
git commit -m "bound the trace relay body and fail loudly on an unknown authentik page shape"
```

---

### Task 11: Supply chain

**Files:**
- Modify: `Iverson.Api.Tests`, `Iverson.Sql.Tests`, `Iverson.StarRocks.Tests`, `Iverson.Vector.Tests` `.csproj`
- Modify: `.gitlab-ci.yml:26-28,47-51`
- Modify: `.github/workflows/codeql.yml`, `.github/workflows/deploy-validate.yml`

- [ ] **Step 1: Pin SSH.NET past the advisory in all four test projects.** `2023.0.0` (GHSA-q939-rpr3-3284, High) arrives transitively via `Testcontainers 3.9.0`. There is no `Directory.Packages.props`, so add a direct `<PackageReference Include="SSH.NET" Version="2024.0.0" />` to each of the four test `.csproj` files. Prefer the minimal jump past the advisory over a Testcontainers 4.x upgrade, which is a breaking change to test infrastructure and out of proportion to a test-only issue.

- [ ] **Step 2: Confirm the advisory is cleared and nothing broke.** `dotnet list Iverson.slnx package --vulnerable --include-transitive` should report no vulnerable packages, and the .NET suite must still pass — the pin overrides what Testcontainers expects, so a runtime break would show up here.

- [ ] **Step 3: Verify CI downloads before executing them.** `.gitlab-ci.yml` pipes kubeconform, kube-score and tfsec release archives straight into `tar`/`chmod +x` with no integrity check. Pin each to a published SHA-256 and verify before extraction.

- [ ] **Step 4: Pin the GitHub Actions by commit SHA** rather than major tag in both workflow files.

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj \
        Iverson.Server/Iverson.Sql.Tests/Iverson.Sql.Tests.csproj \
        Iverson.Server/Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj \
        Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj \
        .gitlab-ci.yml .github/workflows
git commit -m "pin ssh.net past the advisory and verify ci tool downloads"
```

---

## Before merge

Beyond the per-task suites above, the spec's §4 gate requires **all** of these green, plus a final whole-branch review scoped to seams rather than to tasks:

- The seven suites in the "Suite commands" table
- The **five-language client conformance harness** — Tasks 4 and 5 change transport defaults across every SDK, so this is the gate that catches a missed call site

**Regression surface that must still work:** compose bring-up, kind bring-up and its smoke test, the LoadTest, the conformance harness, and the admin UI login flow end to end. Tasks 3, 4 and 5 are the likeliest to break these.

---

## Known issues inherited from spec

Agreed with **ben** during design. These exist in the implementation by design; a new spec → plan cycle is required to change any of them.

- **Narrowing `iverson-admin-orchestrator` off `is_superuser: true`.** Real Authentik-role work with uncertain payoff. The account remains a standing superuser in every environment.
- **Rotating local dev credentials.** They are intentionally static and local-only, and the 2026-07-16 remediation settled the same question with documentation rather than rotation. Nothing has been deployed to a cloud, so nothing has crossed a real wire.
- **In-cluster TLS / mTLS between services.** Every finding here concerns the edge and the client SDKs; in-cluster hops are covered by the default-deny NetworkPolicies. A service mesh is a far larger project.
- **Breaking change to the client SDKs.** Flipping transport defaults to TLS breaks any external consumer of the five SDKs. Accepted deliberately in preference to a narrower credentials-only gate.
- **`AllowedHosts: "*"`** in `appsettings.json`. Noted during verification; no Host-header-dependent logic was identified, so it is not treated as a finding.
