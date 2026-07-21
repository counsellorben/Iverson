# Admin UI: App Shell + Auth Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-07-21-admin-ui-app-shell-design.md` (commit SHA: `f6117f46e2095420e29384c0ea91dc92ee3d2644`)

**Goal:** Stand up a React admin-dashboard shell — PKCE login against Authentik, a role-filtered nav skeleton, and full kind deployment — as the first of four sub-projects toward a browser-based admin dashboard.

**Architecture:** A new top-level `Iverson.AdminUI/` React+Vite+TS app, served in kind via a new `charts/admin-ui` Helm subchart behind nginx, authenticating against Authentik (which gains its own ingress route + CORS annotations to fix a pre-existing Host-header/issuer bug), with a small backend proxy on `Iverson.Api` relaying browser-originated OTel traces to Jaeger.

**Tech stack:** React 18 + Vite + TypeScript 5.8 (matching `Iverson.Clients/TypeScript`'s conventions), React Router, `ts-proto` (`outputClientImpl=grpc-web`) + `@improbable-eng/grpc-web`, `oidc-client-ts` + `react-oidc-context`, Vitest + React Testing Library, ASP.NET Core 10 / .NET 10 (existing `Iverson.Api`), Helm/ingress-nginx.

---

## File Structure

**Create:**
- `Iverson.AdminUI/package.json`, `tsconfig.json`, `vite.config.ts`, `index.html`, `vitest.config.ts`
- `Iverson.AdminUI/src/main.tsx`, `src/router.tsx`, `src/config.ts` (unified config loader)
- `Iverson.AdminUI/src/auth/` — `AuthProvider.tsx`, `CallbackPage.tsx`
- `Iverson.AdminUI/src/layout/` — `AppLayout.tsx`, `Sidebar.tsx`
- `Iverson.AdminUI/src/pages/` — `PerformancePage.tsx`, `StoragePage.tsx`, `TenantsPage.tsx`, `TenantAdminPage.tsx`
- `Iverson.AdminUI/src/telemetry.ts` (OTel SDK wiring)
- `Iverson.AdminUI/scripts/generate_protos.sh`
- `Iverson.AdminUI/Dockerfile`, `Iverson.AdminUI/docker-entrypoint.sh`
- `Iverson.AdminUI/.env.development`, `.gitignore`
- `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/ingress.yaml`
- `Iverson.Server/deploy/helm/iverson/charts/admin-ui/` — `Chart.yaml`, `values.yaml`, `templates/deployment.yaml`, `templates/service.yaml`, `templates/ingress.yaml`
- `Iverson.Server/Iverson.Api/Grpc/TracesProxyEndpoint.cs` (or inline in `Program.cs` — decided in Task 5)

**Modify:**
- `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml`
- `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml`
- `Iverson.Server/deploy/helm/iverson/charts/authentik/values.yaml`
- `Iverson.Server/deploy/helm/iverson/values.yaml`, `values-local.yaml`, `values-aws.yaml`, `values-azure.yaml`, `values-gcp.yaml`
- `Iverson.Server/Iverson.Api/Program.cs`
- `docs/user-management-and-security.md`
- `Iverson.Server/deploy/helm/iverson/charts/jaeger/templates/service.yaml`
- `Iverson.Server/deploy/helm/iverson/templates/networkpolicies.yaml`
- `Iverson.Server/deploy/kind/build-and-load-image.sh`

**Test:**
- `Iverson.AdminUI/src/auth/AuthProvider.test.tsx`
- `Iverson.AdminUI/src/layout/Sidebar.test.tsx`
- `Iverson.AdminUI/src/router.test.tsx`

---

## Inherited from spec

Trusted as ground truth, not re-verified here (see spec's "Verified assumptions" for original evidence):

- ts-proto's browser-client flag is `outputClientImpl=grpc-web` (requires `@improbable-eng/grpc-web`).
- Authentik derives `iss` from the request's Host header; requires `ValidIssuers` (plural), not a repoint.
- Authentik requires `offline_access` scope attached *and* requested (2024.2+) to issue a refresh token.
- Authentik's OAuth2 token endpoint sends no `Access-Control-Allow-Origin` by default — needs ingress-nginx CORS annotations.
- `groups` scope-mapping claims populate the ID token, not just the access token.
- Authentik's `redirect_uris` field accepts multiple entries.
- ingress-nginx merges multiple `Ingress` resources for the same host by longest-path-match.
- Jaeger `1.62.0` (pinned) has OTLP/HTTP enabled by default on 4318; its Service currently declares only 16686+4317.
- `{{ .Release.Name }}-jaeger-ingress` / `-api-egress` NetworkPolicy rules are single-line, one-port-each additions for 4318.
- No per-service `enabled` boolean pattern exists in any values file.
- `deploy/kind/build-and-load-image.sh` is hardcoded to the api image/Dockerfile; only `tag`/`cluster-name` are parameterized; its podman-vs-docker re-tag logic must be preserved when generalized.
- Compose's `iverson-oidc-default` client-id is a fixed literal (`dev-iverson-human-oidc-client-id`); kind's is Helm-random.
- The `{{ .Release.Name }}-authentik-human-oidc-client` Secret's `client-id` key is the existing `secretKeyRef` pattern `charts/api/templates/deployment.yaml` already uses (spec's Deployment section) — Task 6's new subchart mirrors this exact reference.

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | `Iverson.Clients/TypeScript/package.json` and `tsconfig.json` exist with the exact conventions the plan mirrors (TS `^5.8.0`, `target: ES2022`, `module: ESNext`, `moduleResolution: bundler`, `strict: true`, npm, `vitest: {environment: "node"}`) | Read both files directly this session |
| 2 | File path/shape | `iverson-oidc-default` provider block in `blueprints-configmap-service-clients.yaml` is at lines 104-120 (`redirect_uris`, `property_mappings` with `groups`/`tenant_id` only, no `grant_types` currently) | Read directly this session |
| 3 | File path/shape | `iverson-oidc-default` block in `blueprints/compose-only/service-clients.yaml` — `redirect_uris` at line 147 uses `http://localhost/placeholder-callback` | Read directly this session (grep + surrounding context) |
| 4 | File path/shape | `iverson-loadtest-human`'s existing `grant_types: ["authorization_code", "refresh_token"]` + `access_token_validity: "hours=2"` pattern (lines 141-142) is the exact convention Task 2 mirrors for `iverson-oidc-default` | Read directly this session |
| 5 | No existing Authentik ingress | `charts/authentik/templates/` has no `ingress.yaml` today; `charts/api/templates/ingress.yaml` is the only Ingress-kind resource in the whole chart tree, and is the template Task 2's new file mirrors (`ingressClassName`, conditional `tls:` block, `host`/`path` rule shape) | `find`+`grep` across `charts/*/templates/` this session; read `charts/api/templates/ingress.yaml` in full |
| 6 | Values structure | `charts/authentik/values.yaml` exists but has no `ingress:` block yet; top-level `values.yaml`/`values-local.yaml` nest `api.ingress.{className,annotations,host,tlsSecretName}`; `values-aws.yaml`'s `api.ingress` uses `className: "alb"` + `alb.ingress.kubernetes.io/*` annotations, no `tlsSecretName` (ACM instead) | Read `charts/authentik/values.yaml`, `values.yaml:143-153`, `values-local.yaml:57-71`, `values-aws.yaml:70-85` directly this session |
| 7 | Code shape | `Program.cs`'s primary JWT bearer scheme (lines 87-106) sets only `options.Authority` + `TokenValidationParameters.ValidAudiences` — no explicit `ValidIssuer(s)` or `MetadataAddress` today, meaning `ValidIssuer` currently defaults implicitly to `Authority`'s value; adding `ValidIssuers` as an explicit two-element array (existing `Authority` value + the new external string) is the only change needed — `MetadataAddress` does not need to be touched since `Authority` (used for metadata fetch) is unchanged | Read `Program.cs:87-132` directly this session |
| 8 | Consumer impact (Cat 6) | Nothing else in `Iverson.Api`/`Iverson.Api.Tests` reads `options.TokenValidationParameters.ValidIssuer` (singular) directly, so switching to `ValidIssuers` (plural) has no other call site to update | `grep -rn "ValidIssuer" Iverson.Server/` — only the one line this task touches |
| 9 | Compose scope boundary | Compose's `Authentication:Authority`/`Authentication:ActingUser:Authority` are both `http://authentik-server:9000/...` (the compose-internal Docker network hostname) — the spec's `ValidIssuers`/ingress/CORS fix is scoped to kind only; compose retains its own pre-existing, pre-spec issuer mismatch for real browser logins, unaddressed by the spec and therefore by this plan. Flagged to the user, not a plan change (spec explicitly scopes the fix to "kind is in scope") | `grep -n "Authentication__.*Authority" Iverson.Server/docker-compose.yml:269,274` this session |
| 10 | Existing HttpClient pattern | `AuthentikAdminClient`'s named-`HttpClient` registration pattern (`Program.cs:207-215`) is what Task 5's new Jaeger-proxy `HttpClient` mirrors | Read `Program.cs:207-215` directly this session |
| 11 | File path/shape | `charts/jaeger/templates/service.yaml` declares exactly `ui`(16686)+`otlp-grpc`(4317); `templates/networkpolicies.yaml`'s `{{ .Release.Name }}-jaeger-ingress` (~line 421-433) and `{{ .Release.Name }}-api-egress`'s jaeger rule (~line 56-57) are the two single-port-list rules Task 5 widens | Read both files directly this session (also independently confirmed during the design's CDR round) |
| 12 | Dockerfile convention | `Iverson.Api/Dockerfile`'s multi-stage build + reuse of the base image's built-in `uid=1000/gid=1000` "ubuntu" user (no new user created) is the non-root convention Task 6's new Dockerfile mirrors | Read `Iverson.Api/Dockerfile` in full this session |
| 13 | Build/test commands | `npm test` → `vitest run` and `npm run generate` → `bash scripts/generate_protos.sh` are the exact script names/invocations the existing TS client uses, which Task 1's `package.json` mirrors verbatim (plus new `dev`/`build` scripts Vite provides by convention) | Read `Iverson.Clients/TypeScript/package.json` directly this session |
| 14 | Task ordering | Task 4 (nav skeleton) imports the auth context/hook Task 3 creates (`AuthProvider`); Task 5's frontend OTel wiring reads the Bearer token Task 3's auth context exposes; Task 6 packages the build output of Tasks 1-5 — no task later in the sequence is imported by an earlier one | Traced import direction across all 6 tasks' planned files; no reverse reference found |
| 15 | Sibling-set sweep: values files | The `adminUi:`/`authentik.ingress` values additions must land in ALL FIVE values files (`values.yaml`, `values-local.yaml`, `values-aws.yaml`, `values-azure.yaml`, `values-gcp.yaml`), not just kind's — checked each file's existing `api:` block is present in all five (structural precedent for where the new blocks go) | `grep -c "^api:" Iverson.Server/deploy/helm/iverson/values*.yaml` — 1 in each of the 5 files |
| 16 | File path/shape | Authentik's own Kubernetes Service is named `{{ .Release.Name }}-authentik` (not `-authentik-server`) | Read `charts/authentik/templates/service.yaml:2-4` directly this session — corrected an earlier drafting guess |
| 17 | Consumer impact (Cat 6) | `AuthTestWebApplicationFactory.cs:63,70` sets `TokenValidationParameters.ValidateIssuer = false` for every existing auth-pipeline test — no existing test exercises `ValidIssuer`/`ValidIssuers` matching at all, and this codebase's convention is deliberately not to. Task 2's originally-drafted "add a test for the new issuer" step was dropped as unauthorized scope (the spec's Testing section never asked for backend issuer-validation coverage) rather than inventing new test infrastructure to cover one config line | Read `AuthTestWebApplicationFactory.cs:60-73` directly this session |
| 18 | Frontend data shape | `oidc-client-ts`'s `user.profile.groups` is a genuine `string[]` (decoded straight from the raw ID token JWT) — distinct from `ClaimsPrincipal.FindAll("groups")`'s server-side "one Claim per array element" shape (`Program.cs:141`), which is a .NET-specific artifact of JWT-to-Claims mapping, not part of the token itself. Confirmed via Authentik's `groups` scope-mapping expression, which returns a Python list (`[group.name for group in user.ak_groups.all()]`), serializing as a JSON array | Read `Program.cs:141,145,149` and `RowFieldAuthorizationEvaluator.cs:24` directly this session; cross-checked against the scope-mapping expression documented in `docs/user-management-and-security.md` |
| 19 | Task ordering / insertion point | The new `/v1/traces` route belongs inside the existing `if (workloadRole == "api") { ... }` block (`Program.cs:381-388`), after `TenantAdminGrpcService` — `worker` serves no inbound HTTP traffic today, so gating this alongside the other browser/client-facing endpoints is correct, not arbitrary | Read `Program.cs:375-391` directly this session |

## Tasks

### Task 1: Project scaffold

**Files:**
- Create: `Iverson.AdminUI/package.json`
- Create: `Iverson.AdminUI/tsconfig.json`
- Create: `Iverson.AdminUI/vite.config.ts`
- Create: `Iverson.AdminUI/vitest.config.ts`
- Create: `Iverson.AdminUI/index.html`
- Create: `Iverson.AdminUI/src/main.tsx`
- Create: `Iverson.AdminUI/src/router.tsx`
- Create: `Iverson.AdminUI/scripts/generate_protos.sh`
- Create: `Iverson.AdminUI/.gitignore`
- Test: `Iverson.AdminUI/src/router.test.tsx` (smoke test only — full router content added in Tasks 3-4)

**Interfaces:**
- Produces: `src/router.tsx` exporting a `createBrowserRouter` instance with conditional `basename` (empty in dev via `import.meta.env.DEV`, `/admin` otherwise) and one placeholder root route — Tasks 3-4 add routes to this router.

- [ ] **Step 1: Initialize `package.json`**, mirroring `Iverson.Clients/TypeScript/package.json`'s conventions (npm, TS `^5.8.0`) but for a Vite React app:
```json
{
  "name": "@iverson/admin-ui",
  "version": "0.1.0",
  "type": "module",
  "scripts": {
    "dev": "vite",
    "build": "vite build",
    "test": "vitest run",
    "generate": "bash scripts/generate_protos.sh"
  },
  "dependencies": {
    "react": "^18.3.0",
    "react-dom": "^18.3.0",
    "react-router-dom": "^6.28.0",
    "@improbable-eng/grpc-web": "^0.15.0",
    "google-protobuf": "^3.21.0",
    "long": "^5.2.3"
  },
  "devDependencies": {
    "@types/react": "^18.3.0",
    "@types/react-dom": "^18.3.0",
    "@vitejs/plugin-react": "^4.3.0",
    "vite": "^5.4.0",
    "typescript": "^5.8.0",
    "ts-proto": "^2.7.0",
    "vitest": "^3.2.0",
    "jsdom": "^25.0.0",
    "@testing-library/react": "^16.0.0",
    "@testing-library/jest-dom": "^6.5.0"
  }
}
```

- [ ] **Step 2: `tsconfig.json`**, matching the existing client's compiler options plus browser/JSX needs:
```json
{
  "compilerOptions": {
    "target": "ES2022",
    "lib": ["ES2022", "DOM", "DOM.Iterable"],
    "module": "ESNext",
    "moduleResolution": "bundler",
    "jsx": "react-jsx",
    "strict": true,
    "outDir": "dist",
    "skipLibCheck": true
  },
  "include": ["src/**/*", "generated/**/*"]
}
```

- [ ] **Step 3: `vite.config.ts`** — React plugin, conditional base path matching the router's conditional basename:
```ts
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig(({ mode }) => ({
  plugins: [react()],
  base: mode === "development" ? "/" : "/admin/",
}));
```

- [ ] **Step 4: `vitest.config.ts`** — jsdom environment (browser DOM, unlike the Node-environment TS client):
```ts
import { defineConfig } from "vitest/config";

export default defineConfig({
  test: { environment: "jsdom", globals: true, setupFiles: [] },
});
```

- [ ] **Step 5: `index.html`** at the project root, loading `src/main.tsx`; leave a `<script>` comment placeholder for the runtime `config.js` Task 3 introduces (do not implement config loading yet — this task has no auth).

- [ ] **Step 6: `src/router.tsx`** — conditional `basename`, one placeholder root route:
```tsx
import { createBrowserRouter } from "react-router-dom";

export const router = createBrowserRouter(
  [{ path: "/", element: <div>Admin UI</div> }],
  { basename: import.meta.env.DEV ? "" : "/admin" }
);
```

- [ ] **Step 7: `src/main.tsx`** — renders `<RouterProvider router={router} />` into `#root`.

- [ ] **Step 8: `scripts/generate_protos.sh`**, mirroring `Iverson.Clients/TypeScript/scripts/generate_protos.sh` exactly except the output option:
```bash
#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ADMIN_UI_DIR="$(dirname "$SCRIPT_DIR")"
PROTO_DIR="$(dirname "$(dirname "$ADMIN_UI_DIR")")/Iverson.Clients/Common/Proto"

cd "$ADMIN_UI_DIR"
mkdir -p generated

~/sdk/protoc/bin/protoc \
  --plugin=./node_modules/.bin/protoc-gen-ts_proto \
  --ts_proto_out=generated \
  --ts_proto_opt=outputClientImpl=grpc-web,esModuleInterop=true \
  -I"$PROTO_DIR" \
  "$PROTO_DIR"/*.proto

echo "Proto generation complete."
```

- [ ] **Step 9: `.gitignore`** — `node_modules/`, `dist/`, `generated/`, `.env.local`.

- [ ] **Step 10: Smoke test `src/router.test.tsx`** — renders `<RouterProvider router={router} />` via React Testing Library, asserts "Admin UI" text appears.

- [ ] **Step 11: Install deps and verify.** `npm install`, `npm run generate` (confirms the codegen script runs and produces output in `generated/`, even though nothing imports it yet), `npm test` (1/1 passing), `npm run build` (produces `dist/`).

- [ ] **Step 12: Commit.**
```bash
git add Iverson.AdminUI/
git commit -m "add Iverson.AdminUI project scaffold (Vite+React+TS, grpc-web codegen)"
```

### Task 2: Backend auth infra

**Files:**
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml`
- Create: `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/ingress.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/values.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/values.yaml`, `values-local.yaml`, `values-aws.yaml`, `values-azure.yaml`, `values-gcp.yaml`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Modify: `docs/user-management-and-security.md`

**Note before starting:** compose's `Authentication:Authority` is `http://authentik-server:9000/...` (the compose-internal Docker hostname), which has the same Host-header/issuer mismatch as kind's pre-fix state — but the spec scopes this fix to kind only ("must be fixed here since kind is in scope"). This plan does not add a compose-side `ValidIssuers` fix; real browser login against a compose-targeted backend remains non-functional after this plan, same as before it. Flagging per Verified Assumption #9 — not a task in this plan.

- [ ] **Step 1: Update kind's `iverson-oidc-default` provider** (`blueprints-configmap-service-clients.yaml`, currently lines 104-120) — replace `redirect_uris`, add `grant_types`, add `offline_access` to `property_mappings`:
```yaml
          redirect_uris:
            - matching_mode: strict
              url: "http://{{ .Values.global.ingressHost }}/admin/callback"
              redirect_uri_type: authorization
            - matching_mode: strict
              url: "http://localhost:5173/callback"
              redirect_uri_type: authorization
          client_id: {{ if $humanOidc }}{{ index $humanOidc.data "client-id" | b64dec }}{{ else }}{{ randAlphaNum 40 }}{{ end }}
          signing_key: !Find [authentik_crypto.certificatekeypair, [name, "authentik Self-signed Certificate"]]
          issuer_mode: global
          grant_types: ["authorization_code", "refresh_token"]
          property_mappings:
            - !Find [authentik_providers_oauth2.scopemapping, [scope_name, groups]]
            - !Find [authentik_providers_oauth2.scopemapping, [scope_name, tenant_id]]
            - !Find [authentik_providers_oauth2.scopemapping, [scope_name, offline_access]]
```

- [ ] **Step 2: Update compose's `iverson-oidc-default` provider** (`blueprints/compose-only/service-clients.yaml`, `redirect_uris` at line 147) — add the local-dev entry alongside the existing placeholder (do not touch `grant_types`/`property_mappings` — compose's silent-renewal story is out of this plan's scope, matching the spec):
```yaml
          redirect_uris:
            - matching_mode: strict
              url: "http://localhost/placeholder-callback"
              redirect_uri_type: authorization
            - matching_mode: strict
              url: "http://localhost:5173/callback"
              redirect_uri_type: authorization
```

- [ ] **Step 3: Create `charts/authentik/templates/ingress.yaml`**, mirroring `charts/api/templates/ingress.yaml`'s shape (conditional TLS block, `ingressClassName`, one host/path rule) with CORS annotations and a templated (not values-controlled) host:
```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: {{ .Release.Name }}-authentik
  annotations:
    nginx.ingress.kubernetes.io/enable-cors: "true"
    nginx.ingress.kubernetes.io/cors-allow-origin: "http://{{ .Values.global.ingressHost }}"
    nginx.ingress.kubernetes.io/cors-allow-methods: "POST, OPTIONS"
    nginx.ingress.kubernetes.io/cors-allow-headers: "content-type"
    {{- toYaml .Values.ingress.annotations | nindent 4 }}
spec:
  ingressClassName: {{ .Values.ingress.className | quote }}
{{- if .Values.ingress.tlsSecretName }}
  tls:
    - hosts: [{{ printf "authentik.%s" .Values.global.ingressHost | quote }}]
      secretName: {{ .Values.ingress.tlsSecretName | quote }}
{{- end }}
  rules:
    - host: {{ printf "authentik.%s" .Values.global.ingressHost | quote }}
      http:
        paths:
          - path: /
            pathType: Prefix
            backend:
              service:
                name: {{ .Release.Name }}-authentik
                port:
                  number: 9000
```
(Service name confirmed as `{{ .Release.Name }}-authentik` via `charts/authentik/templates/service.yaml:4` — not `-authentik-server`.)

- [ ] **Step 4: Add `ingress:` block to `charts/authentik/values.yaml`**:
```yaml
ingress:
  className: "nginx"
  annotations: {}
  tlsSecretName: ""
```

- [ ] **Step 5: Add matching per-cloud overrides** under `authentik:` in `values-aws.yaml` (className `alb` + `alb.ingress.kubernetes.io/certificate-arn: "<ACM_CERT_ARN>"` placeholder, same comment convention as `api.ingress`), `values-azure.yaml`/`values-gcp.yaml` (their existing `api.ingress.className` values + placeholder `tlsSecretName: "iverson-authentik-tls"`), matching each file's existing `api.ingress` comment style. `values.yaml`/`values-local.yaml` need no override — the `charts/authentik/values.yaml` default (`nginx`, empty `tlsSecretName`) already matches kind.

- [ ] **Step 6: Modify `Program.cs`'s primary JWT bearer scheme** (lines 87-106) — add `ValidIssuers`:
```csharp
options.TokenValidationParameters.ValidIssuers = new[]
{
    cfg["Authentication:Authority"],
    cfg["Authentication:ExternalIssuer"]
};
```
placed after the existing `options.Authority = ...` line. Add `Authentication__ExternalIssuer=http://authentik.iverson.local/application/o/iverson-api/` to `docker-compose.yml`'s `iverson-api` environment block (harmless no-op there per Step-before-Task-2's note — compose doesn't route browser traffic through this host, so the extra valid issuer is simply never matched) and `Authentication__ExternalIssuer=http://authentik.{{ .Values.global.ingressHost }}/application/o/iverson-api/` to `charts/api/templates/deployment.yaml`'s env block.

- [ ] **Step 7: Add a doc note** to `docs/user-management-and-security.md`'s operator-onboarding section — replace "this happens through whatever frontend/tool initiates the Authorization Code flow" with a reference naming `Iverson.AdminUI`.

- [ ] **Step 8: Build and verify.** No new/extended `Iverson.Api.Tests` coverage for the `ValidIssuers` addition — `AuthTestWebApplicationFactory.cs:63,70` sets `TokenValidationParameters.ValidateIssuer = false` for every existing auth-pipeline test, so this codebase's existing convention is to trust ASP.NET Core's own JWT-bearer middleware for issuer matching rather than re-test it; inventing new test infrastructure to cover one config line would be scope the spec never asked for.
```bash
helm dependency build Iverson.Server/deploy/helm/iverson
helm template test-release Iverson.Server/deploy/helm/iverson -f Iverson.Server/deploy/helm/iverson/values-local.yaml --show-only charts/authentik/templates/ingress.yaml
cd Iverson.Server && dotnet build Iverson.Api
```

- [ ] **Step 9: Commit.**
```bash
git add Iverson.Server/deploy/helm/iverson/charts/authentik/ Iverson.Server/deploy/helm/iverson/values*.yaml Iverson.Server/Iverson.Api/Program.cs Iverson.Server/docker-compose.yml docs/user-management-and-security.md
git commit -m "feat(auth): add Authentik ingress+CORS, offline_access refresh support, external issuer validation"
```

### Task 3: Frontend auth

**Files:**
- Create: `Iverson.AdminUI/src/config.ts`
- Create: `Iverson.AdminUI/src/auth/AuthProvider.tsx`
- Create: `Iverson.AdminUI/src/auth/CallbackPage.tsx`
- Create: `Iverson.AdminUI/.env.development`
- Modify: `Iverson.AdminUI/src/router.tsx` (add callback route + auth-gated layout placeholder)
- Modify: `Iverson.AdminUI/package.json` (add `oidc-client-ts`, `react-oidc-context`)
- Test: `Iverson.AdminUI/src/auth/AuthProvider.test.tsx`

**Interfaces:**
- Consumes: Task 1's `router.tsx` (adds routes to it), Task 1's config-loading placeholder in `index.html`.
- Produces: `useAuth()` hook (via `react-oidc-context`) exposing `user.profile.groups` and the access token — consumed by Task 4 (nav filtering) and Task 5 (OTel exporter's Bearer header).

- [ ] **Step 1: `src/config.ts`** — the unified config loader:
```ts
declare global {
  interface Window { __ADMIN_UI_CONFIG__?: Record<string, string> }
}

function readConfig(key: string): string {
  const runtime = window.__ADMIN_UI_CONFIG__?.[key];
  if (runtime) return runtime;
  const buildTime = import.meta.env[`VITE_${key}`];
  if (buildTime) return buildTime;
  throw new Error(`Missing admin-ui config: ${key}`);
}

export const config = {
  oidcClientId: readConfig("OIDC_CLIENT_ID"),
  oidcAuthority: readConfig("OIDC_AUTHORITY"),
  apiBaseUrl: readConfig("API_BASE_URL"),
};
```

- [ ] **Step 2: `.env.development`** (committed, compose's fixed dev literal):
```
VITE_OIDC_CLIENT_ID=dev-iverson-human-oidc-client-id
VITE_OIDC_AUTHORITY=http://authentik-server:9000/application/o/iverson-api/
VITE_API_BASE_URL=http://localhost:8080
```

- [ ] **Step 3: Add `oidc-client-ts` + `react-oidc-context`** to `package.json` dependencies.

- [ ] **Step 4: `src/auth/AuthProvider.tsx`** — wraps `react-oidc-context`'s `AuthProvider` with the spec's settings:
```tsx
import { AuthProvider as OidcAuthProvider } from "react-oidc-context";
import { config } from "../config";

const oidcConfig = {
  authority: config.oidcAuthority,
  client_id: config.oidcClientId,
  redirect_uri: `${window.location.origin}${import.meta.env.DEV ? "" : "/admin"}/callback`,
  scope: "openid profile email offline_access",
  automaticSilentRenew: true,
};

export function AuthProvider({ children }: { children: React.ReactNode }) {
  return <OidcAuthProvider {...oidcConfig}>{children}</OidcAuthProvider>;
}
```

- [ ] **Step 5: `src/auth/CallbackPage.tsx`** — invokes `react-oidc-context`'s callback handling (`useAuth()`'s `isLoading`/`error` states while `signinRedirectCallback` completes), then navigates to `/` on success.

- [ ] **Step 6: Modify `router.tsx`** — add the callback route as a sibling of (not nested under) an auth-gated layout route:
```tsx
export const router = createBrowserRouter(
  [
    { path: "/callback", element: <CallbackPage /> },
    { path: "/", element: <AuthGate><AppLayout /></AuthGate> }, // AppLayout is Task 4's
  ],
  { basename: import.meta.env.DEV ? "" : "/admin" }
);
```
`AuthGate` (a small wrapper using `react-oidc-context`'s `useAuth()` to redirect to `signinRedirect()` when unauthenticated) is added in this step; `AppLayout` itself is Task 4's responsibility — Task 3 provides a placeholder here.

- [ ] **Step 7: Wire logout** — a `signOutRedirect` call available via `useAuth()`, exposed for Task 4's header to call (no dedicated route needed).

- [ ] **Step 8: Test** — `AuthProvider.test.tsx`: with `react-oidc-context` mocked to report "not authenticated," rendering `AuthGate`'s children does not render them and instead triggers the redirect path (assert `signinRedirect` was called, per the spec's "redirects an unauthenticated visitor into the login flow").

- [ ] **Step 9: Run `npm test`, `npm run build`.**

- [ ] **Step 10: Commit.**
```bash
git add Iverson.AdminUI/
git commit -m "feat(admin-ui): PKCE auth via oidc-client-ts, unified config loader, callback route"
```

### Task 4: Nav skeleton + role filtering

**Files:**
- Create: `Iverson.AdminUI/src/layout/AppLayout.tsx`
- Create: `Iverson.AdminUI/src/layout/Sidebar.tsx`
- Create: `Iverson.AdminUI/src/pages/PerformancePage.tsx`, `StoragePage.tsx`, `TenantsPage.tsx`, `TenantAdminPage.tsx`
- Modify: `Iverson.AdminUI/src/router.tsx` (nest the four pages + default redirect under the layout route)
- Test: `Iverson.AdminUI/src/layout/Sidebar.test.tsx`

**Interfaces:**
- Consumes: Task 3's `useAuth()` (reads `user.profile.groups` for role filtering, exposes logout).

- [ ] **Step 1: Four placeholder pages** — each renders a "Coming soon" placeholder, nothing else.

- [ ] **Step 2: `Sidebar.tsx`** — reads `user?.profile?.groups` from `useAuth()` as a genuine `string[]` (`oidc-client-ts`'s `user.profile` decodes the raw ID token JWT payload directly; Authentik's `groups` scope-mapping expression returns a Python list — `[group.name for group in user.ak_groups.all()]` — which serializes as a JSON array in the token, not the "exploded into one Claim per array element" shape `ClaimsPrincipal.FindAll("groups")` produces server-side in `Iverson.Api/Program.cs:141` — that explosion is a .NET-specific artifact, not part of the JWT itself). Renders Performance/Storage links unconditionally; renders Tenants only if `groups.includes("operators")`; renders Tenant Admin only if `groups.includes("tenant-admins")`.

- [ ] **Step 3: `AppLayout.tsx`** — header (user identity from `user.profile`, logout button calling `signoutRedirect`), `<Sidebar />`, `<Outlet />`.

- [ ] **Step 4: Modify `router.tsx`** — nest the four page routes as children of the layout route, add `{ index: true, element: <Navigate to="/performance" replace /> }` as the default-redirect child.

- [ ] **Step 5: Test `Sidebar.test.tsx`** — four cases (operator only, tenant-admin only, both, neither) asserting exactly the expected link set renders for each, per the spec's stated test.

- [ ] **Step 6: Test router default-redirect** — extend `router.test.tsx` (from Task 1) to assert navigating to `/` (with a mocked-authenticated user) redirects to `/performance`.

- [ ] **Step 7: Run `npm test`, `npm run build`.**

- [ ] **Step 8: Commit.**
```bash
git add Iverson.AdminUI/
git commit -m "feat(admin-ui): nav skeleton with role-filtered Tenants/Tenant Admin links"
```

### Task 5: Browser OTel

**Files:**
- Create: `Iverson.AdminUI/src/telemetry.ts`
- Modify: `Iverson.AdminUI/package.json` (OTel web SDK packages)
- Modify: `Iverson.AdminUI/src/main.tsx` (call telemetry init)
- Modify: `Iverson.Server/Iverson.Api/Program.cs` (named `HttpClient` + `/v1/traces` route)
- Modify: `Iverson.Server/deploy/helm/iverson/charts/jaeger/templates/service.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/templates/networkpolicies.yaml`

**Interfaces:**
- Consumes: Task 3's `useAuth()` access token (attached to the OTLP exporter's headers).

- [ ] **Step 1: Add port 4318 to `charts/jaeger/templates/service.yaml`**:
```yaml
    - name: otlp-http
      port: 4318
```

- [ ] **Step 2: Widen `templates/networkpolicies.yaml`'s two rules** by one port each — `{{ .Release.Name }}-jaeger-ingress`'s `ports` list gains `{ protocol: TCP, port: 4318 }`; `{{ .Release.Name }}-api-egress`'s jaeger `to` rule's `ports` list gains the same.

- [ ] **Step 3: `Program.cs` — named `HttpClient` for the Jaeger relay**, mirroring `AuthentikAdminClient`'s registration pattern:
```csharp
builder.Services.AddHttpClient("JaegerOtlpHttp", client =>
{
    client.BaseAddress = new Uri(cfg["Jaeger:OtlpHttpUrl"] ?? "http://iverson-jaeger:4318");
});
```

- [ ] **Step 4: `Program.cs` — the `/v1/traces` relay route**, requiring authentication, byte-for-byte body relay:
```csharp
app.MapPost("/v1/traces", async (HttpContext ctx, IHttpClientFactory httpClientFactory) =>
{
    var client = httpClientFactory.CreateClient("JaegerOtlpHttp");
    using var content = new StreamContent(ctx.Request.Body);
    content.Headers.ContentType = MediaTypeHeaderValue.Parse(ctx.Request.ContentType ?? "application/x-protobuf");
    var response = await client.PostAsync("/v1/traces", content);
    ctx.Response.StatusCode = (int)response.StatusCode;
    await response.Content.CopyToAsync(ctx.Response.Body);
}).RequireAuthorization();
```
Placed inside the existing `if (workloadRole == "api") { ... }` block (`Program.cs:381-388`), after the `TenantAdminGrpcService` line — matching where the other browser/client-facing endpoints are registered, since `worker` serves no inbound HTTP traffic today.

- [ ] **Step 5: `src/telemetry.ts`** — `@opentelemetry/sdk-trace-web` + `instrumentation-fetch` + `instrumentation-document-load` + `exporter-trace-otlp-http`, exporter `url: "/v1/traces"` (relative, same-origin), headers including the current access token from `oidc-client-ts`'s stored user (read via the same `WebStorageStateStore` `react-oidc-context` uses, not a React hook, since this initializes once at app startup before React renders).

- [ ] **Step 6: `main.tsx`** — call the telemetry init before rendering.

- [ ] **Step 7: Build+test.**
```bash
cd Iverson.Server && dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~Traces"
helm dependency build Iverson.Server/deploy/helm/iverson
helm template test-release Iverson.Server/deploy/helm/iverson -f Iverson.Server/deploy/helm/iverson/values-local.yaml --show-only charts/jaeger/templates/service.yaml
cd Iverson.AdminUI && npm run build
```

- [ ] **Step 8: Commit.**
```bash
git add Iverson.AdminUI/ Iverson.Server/Iverson.Api/Program.cs Iverson.Server/deploy/helm/iverson/charts/jaeger/ Iverson.Server/deploy/helm/iverson/templates/networkpolicies.yaml
git commit -m "feat(otel): proxy browser traces to Jaeger via authenticated /v1/traces relay"
```

### Task 6: Admin-ui deployment

**Files:**
- Create: `Iverson.AdminUI/Dockerfile`, `Iverson.AdminUI/docker-entrypoint.sh`
- Create: `Iverson.Server/deploy/helm/iverson/charts/admin-ui/Chart.yaml`, `values.yaml`, `templates/deployment.yaml`, `templates/service.yaml`, `templates/ingress.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/Chart.yaml` (register `admin-ui` dependency)
- Modify: `Iverson.Server/deploy/helm/iverson/values.yaml`, `values-local.yaml`, `values-aws.yaml`, `values-azure.yaml`, `values-gcp.yaml` (`adminUi:` blocks)
- Modify: `Iverson.Server/deploy/kind/build-and-load-image.sh`

- [ ] **Step 1: `Iverson.AdminUI/Dockerfile`** — multi-stage, non-root, matching `Iverson.Api/Dockerfile`'s conventions:
```dockerfile
FROM node:20-alpine AS build
WORKDIR /src
COPY Iverson.AdminUI/package.json Iverson.AdminUI/package-lock.json ./
RUN npm ci
COPY Iverson.AdminUI/ .
COPY Iverson.Clients/Common/Proto ../Iverson.Clients/Common/Proto
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.27-alpine AS runtime
COPY --from=build /src/dist /usr/share/nginx/html
COPY Iverson.AdminUI/docker-entrypoint.sh /docker-entrypoint.d/40-admin-ui-config.sh
USER 101
EXPOSE 8080
```
(`nginx-unprivileged`'s built-in non-root user is uid 101, not the `.NET` images' uid 1000 — no conflict: each subchart's `Deployment` sets its own `securityContext`, so `charts/admin-ui/templates/deployment.yaml` sets `runAsUser: 101`/`runAsGroup: 101` independently of `api`'s/`worker`'s `1000`. Same non-root discipline, different image family, no shared value to reconcile.)

- [ ] **Step 2: `docker-entrypoint.sh`** — `envsubst` template writing `config.js`:
```bash
#!/bin/sh
set -eu
envsubst '${OIDC_CLIENT_ID} ${OIDC_AUTHORITY} ${API_BASE_URL}' \
  < /usr/share/nginx/html/config.js.template \
  > /usr/share/nginx/html/config.js
```
(Also add the `config.js.template` static asset and its `<script>` include in `index.html`, completing Task 1's placeholder.)

- [ ] **Step 3: New Helm subchart `charts/admin-ui`** — `Chart.yaml` (name/version), `values.yaml` (image repo/tag/pullPolicy, resources, replicas), `templates/deployment.yaml` (env vars `OIDC_CLIENT_ID` via the same `secretKeyRef` pattern `charts/api/templates/deployment.yaml` already uses against `{{ .Release.Name }}-authentik-human-oidc-client`/`client-id`; `OIDC_AUTHORITY`/`API_BASE_URL` as plain values), `templates/service.yaml` (ClusterIP :8080), `templates/ingress.yaml` (`host: {{ .Values.global.ingressHost }}`, `path: /admin` Prefix).

- [ ] **Step 4: Register the new subchart** in the parent `Chart.yaml`'s `dependencies:` list, alongside `api`/`worker`/`authentik`/etc.

- [ ] **Step 5: Add `adminUi:` blocks** to all five values files, matching the existing per-subchart structure (image/resources/replicas), plus the cloud files' CORS/TLS placeholders for this ingress per the spec's Cloud Wiring section.

- [ ] **Step 6: Generalize `build-and-load-image.sh`** to accept `--dockerfile`/`--image-name` params (defaulting to today's api values so the existing no-arg invocation is unchanged), preserving its podman-vs-docker re-tag-to-qualified-reference logic verbatim for whichever image is built.

- [ ] **Step 7: Build+verify.**
```bash
helm dependency build Iverson.Server/deploy/helm/iverson
helm template test-release Iverson.Server/deploy/helm/iverson -f Iverson.Server/deploy/helm/iverson/values-local.yaml --show-only charts/admin-ui/templates/deployment.yaml --show-only charts/admin-ui/templates/ingress.yaml
docker build -f Iverson.AdminUI/Dockerfile -t iverson-admin-ui:test .
```

- [ ] **Step 8: Commit.**
```bash
git add Iverson.AdminUI/Dockerfile Iverson.AdminUI/docker-entrypoint.sh Iverson.Server/deploy/helm/iverson/charts/admin-ui/ Iverson.Server/deploy/helm/iverson/Chart.yaml Iverson.Server/deploy/helm/iverson/values*.yaml Iverson.Server/deploy/kind/build-and-load-image.sh
git commit -m "feat(deploy): add admin-ui Helm subchart, Dockerfile, and generalize kind image build script"
```

## Tasks NOT in this plan

- Real content for any of the four pages (Performance, Storage, Tenants, Tenant Admin) — each future sub-project's own job.
- A working end-to-end cloud deployment (see Cloud Wiring above).
- Handling a revoked/expired refresh token beyond the basic case (redirect to login) — no special recovery UI.

## Known issues inherited from spec

While verifying the `offline_access` requirement for `iverson-oidc-default`'s silent renewal, the same check applied to the sibling `iverson-loadtest-human` client (per the skill's "check the whole set, not just the instance you're touching" discipline) revealed it has the identical gap already: `grant_types` includes `refresh_token`, but no `offline_access` scope mapping is attached — so its silent-renewal capability may be non-functional today. This design does not touch `iverson-loadtest-human` at all. The acting-user smoke-test script (`mint_acting_user_token.py`) only performs one-shot code exchange, never renewal, so this is likely dormant/harmless in practice — flagged for a human to decide whether it's worth its own fix, not addressed here.
