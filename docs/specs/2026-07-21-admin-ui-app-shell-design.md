# Admin UI: App Shell + Auth — Design

## Context

This is the first of several planned sub-projects toward a browser-based admin
dashboard showing performance and storage metrics, plus a UI for the
already-built tenant admin gRPC services (`TenantLifecycleGrpcService`,
`TenantAdminGrpcService`). The full initiative decomposes into independent
pieces:

1. **App shell + auth** (this spec) — React/Vite/TS scaffold, grpc-web client
   codegen, PKCE login against Authentik, role-filtered nav skeleton, and full
   kind deployment (Helm subchart, ingress, container).
2. **Performance metrics view** — surfacing existing Prometheus data.
3. **Storage metrics** — new backend instrumentation (Postgres/StarRocks/Qdrant
   size/usage stats) plus a UI view.
4. **Tenant admin screens** — real content for the Tenants/Tenant Admin pages
   this shell stubs out.

Sub-projects 2–4 are out of scope here; this spec covers only the shell.

## Repo location

New top-level directory `Iverson.AdminUI/`, a sibling to `Iverson.Server`,
`Iverson.Clients`, `Iverson.LoadTest` — matching the existing convention of
one top-level `Iverson.*` directory per major component.

## Scope: "done" for this sub-project

Login via Authentik (PKCE), landing on an authenticated shell with a
persistent layout (sidebar/header/routing) and placeholder routes for every
future page — not just a bare login screen. Real page content for any of
those routes is out of scope (sub-projects 2–4's job). Must run both via
local `npm run dev` and deployed in the kind cluster.

## Tech stack

- **React + Vite + TypeScript** — standard, fast dev iteration, standard
  production build for the Dockerfile to consume.
- **React Router** (`createBrowserRouter`) for the nav skeleton: a layout
  route (sidebar/header + `<Outlet/>`) wrapping placeholder leaf routes.
  `basename` is set conditionally, not fixed — `/admin` in the built app,
  empty string in `npm run dev` (via `import.meta.env.DEV`) — since dev
  serves the app at the Vite dev server's root while kind serves it under
  the `/admin` ingress path. This is what lets the callback route (see Nav
  Skeleton) resolve correctly in both environments.
- **gRPC-web client generation via `ts-proto`**, consistent with
  `Iverson.Clients/TypeScript/scripts/generate_protos.sh`'s existing
  `protoc` + `ts-proto` invocation, rather than introducing a second,
  competing codegen toolchain (e.g. Buf/Connect-Web) into the repo. The
  admin-ui gets its own `generate_protos.sh`, identical to the existing one
  except `--ts_proto_opt=outputClientImpl=grpc-web,esModuleInterop=true`
  (**not** `outputServices=grpc-web` — corrected during verification; see
  Verified Assumptions). This requires adding `@improbable-eng/grpc-web` as a
  new runtime npm dependency (the transport library `outputClientImpl=grpc-web`
  generates clients against). It speaks the standard grpc-web wire protocol,
  which ASP.NET Core's `Grpc.AspNetCore.Web` middleware (`app.UseGrpcWeb()`,
  already wired) implements — no vendor lock-in, no Envoy needed.
- **`oidc-client-ts` + `react-oidc-context`** for the PKCE login flow, rather
  than hand-rolling PKCE — auth-flow correctness is security-sensitive.
- No state-management or data-fetching library yet (no React Query, no
  Redux) — nothing in this sub-project's scope fetches real data.

## Auth flow

### Issuer/Host-header fix (prerequisite, not optional)

Authentik derives the `iss` claim it stamps into every token from the **Host
header of the actual request that hit it** — not from static config. The
API's `Authentication:Authority` is currently the in-cluster-only DNS name
(`http://{{ .Release.Name }}-authentik:9000/...`), unreachable by any
browser. This is a pre-existing gap (already documented as a known trap in
`docs/runbooks/kind-cluster-troubleshooting.md` §5.1–5.3, and noted as "not
kind-specific" — the same mismatch exists in compose too), not something this
design introduces, but it blocks browser login entirely and must be fixed
here since kind is in scope.

Fix:
- **New `Ingress` route for Authentik** (a template that doesn't exist yet,
  added to the existing `charts/authentik` chart): `host:
  authentik.{{ .Values.global.ingressHost }}` (i.e. `authentik.iverson.local`
  for kind) → the existing `authentik-server` Service. A separate,
  templated subdomain — not a hardcoded kind-only string — avoids
  path-rewrite complexity with Authentik's own internal routing, and carries
  over correctly to cloud (see Cloud Wiring). (Whoever tests this in kind
  needs one more `/etc/hosts` line alongside the existing `iverson.local`
  one — no existing doc covers browser DNS resolution for `iverson.local` at
  all today, so this needs a short setup note.)
- **`ValidIssuers` (plural), not a repoint**: the API's JWT validation gains a
  second acceptable issuer string
  (`http://authentik.iverson.local/application/o/iverson-api/`) alongside the
  existing internal one — both validate against the same signing key/JWKS,
  so no existing service-to-service flow is affected. `MetadataAddress` stays
  pointed at the internal DNS name; the API pod never needs to reach
  Authentik externally.

### Redirect URIs

Replace `iverson-oidc-default`'s dead placeholder
(`https://.../placeholder-callback` — already broken for kind today, since
kind never terminates TLS; a real pre-existing bug in code this design
touches) with real `http://`-scheme entries:
- `http://iverson.local/admin/callback` (kind)
- `http://localhost:5173/callback` (local dev)

The **compose** blueprint (`blueprints/compose-only/service-clients.yaml`)
has the identical placeholder gap on its own `iverson-oidc-default` entry.
Since local dev iteration may run against a compose-based backend too, its
entry gets the same `http://localhost:5173/callback` addition (mechanical,
same fix, sibling file).

### Silent renewal (refresh token, not iframe)

`oidc-client-ts`'s `UserManager` is configured with `automaticSilentRenew:
true`. Given a refresh token is present, its `signinSilent()` renews directly
against the token endpoint — no iframe, no dependency on Authentik's
`X-Frame-Options` behavior.

For Authentik to actually issue a refresh token, **two things are required,
not just `grant_types`** (corrected during verification — see Verified
Assumptions): `iverson-oidc-default`'s provider needs
1. `grant_types: ["authorization_code", "refresh_token"]` (mirrors the
   existing `iverson-loadtest-human` client's pattern), **and**
2. Authentik's built-in `offline_access` scope mapping added to
   `property_mappings` (`!Find [authentik_providers_oauth2.scopemapping,
   [scope_name, offline_access]]`, same pattern as the existing `groups`/
   `tenant_id` entries) — Authentik (2024.2+) only issues a refresh token
   when `offline_access` is both attached to the provider *and* explicitly
   requested.

`oidc-client-ts`'s `UserManagerSettings.scope` must therefore include
`offline_access` alongside the standard `openid profile email` scopes.

### Logout

`signoutRedirect` against Authentik's end-session endpoint.

## Nav skeleton structure

- Layout route (`/admin`), auth-gated: header (logged-in user identity +
  logout), sidebar nav, `<Outlet/>`.
- A dedicated, unguarded callback route at the router-root-relative path
  `/callback` — which resolves to `/admin/callback` under kind's `/admin`
  basename and to `/callback` under dev's empty basename, matching both
  registered redirect URIs exactly (see Auth Flow). It invokes
  `oidc-client-ts`'s sign-in callback handling to complete the code
  exchange; without it, Authentik's post-login redirect has nothing to
  land on and login never completes.
- **Always-visible**: `/admin/performance`, `/admin/storage` — no backend
  authorization model exists yet for either, so shown to any authenticated
  user.
- **Role-filtered**, read from the ID token's `groups` claim (already present
  via the existing `groups` scope mapping, no extra API call):
  - `/admin/tenants` — visible only if `groups` contains `"operators"`
    (maps to `TenantLifecycleGrpcService`, matching
    `OperatorAuthorizationPolicy.IsSatisfiedBy`).
  - `/admin/tenant-admin` — visible only if `groups` contains
    `"tenant-admins"` (maps to `TenantAdminGrpcService`, matching
    `TenantAdminAuthorizationPolicy.IsSatisfiedBy`).
  - These are deliberately two separate nav items, not one merged "Admin"
    item — they map to two different backend services with two different,
    non-overlapping personas (operator vs. tenant-admin).
- All four routes render a "Coming soon" placeholder — this sub-project only
  changes nav visibility, not page content.
- Default route (`/admin` exactly) redirects to `/admin/performance`.

## Deployment

- **Dockerfile** (`Iverson.AdminUI/Dockerfile`, new pattern — no Node/nginx
  precedent exists elsewhere in the repo): multi-stage — Node build
  (`npm ci && npm run build`) → `nginx:alpine` runtime serving the static
  build, non-root (matching `Iverson.Api/Dockerfile`'s uid/gid 1000
  convention).
- **Runtime config, not build-time baking**: the OIDC client-id
  (`{{ .Release.Name }}-authentik-human-oidc-client` Secret's `client-id`
  key) is generated by Helm at install time — it doesn't exist at Docker
  image-build time, so it cannot be baked in via build-args. The container's
  entrypoint runs `envsubst` over a template at **startup**, generating
  `config.js` (`window.__ADMIN_UI_CONFIG__ = {...}`), loaded by `index.html`
  before the React bundle. `OIDC_CLIENT_ID` comes from the same
  `secretKeyRef` pattern the API's `deployment.yaml` already uses;
  `OIDC_AUTHORITY`/`API_BASE_URL` are plain values (static per environment).
- **New Helm subchart** `charts/admin-ui`: `Deployment`, `Service`, and its
  own `Ingress` (`host: iverson.local`, `path: /admin` Prefix) — no new
  `enabled` toggle (no such per-service pattern exists anywhere in this
  chart today; Jaeger/Prometheus/Authentik are all unconditional
  dependencies, and this follows that same convention).
- **Authentik ingress** (see Auth flow above) lives in `charts/authentik`,
  not this new subchart.
- **kind image build+load**: `deploy/kind/build-and-load-image.sh` is
  currently hardcoded to the api image/Dockerfile (only `tag`/
  `cluster-name` are parameterized). Generalize it to accept
  `--dockerfile`/`--image-name` params so the same script serves both
  images, preserving its existing podman-vs-docker re-tag-to-qualified-
  reference logic for whichever image is built.
- **Values files**: add `adminUi:` blocks (image repo/tag/pullPolicy,
  resources, replicas) to `values.yaml`/`values-local.yaml`/
  `values-<cloud>.yaml`, matching the existing per-subchart structure.

### Local dev config (separate from the container's runtime config)

`npm run dev` never runs inside the container, so it needs its own config
path, distinct from the `envsubst`/`config.js` mechanism above:
- Targeting **compose**: `iverson-oidc-default`'s client-id there is a fixed
  literal (`dev-iverson-human-oidc-client-id`, confirmed in
  `blueprints/compose-only/service-clients.yaml`), safe to default in a
  committed Vite `.env.development`-style file, matching how this repo
  already treats other compose dev secrets as known literals.
- Targeting **kind**: the client-id is Helm-random and not known in advance
  — a developer fetches it from the cluster secret (same
  `kubectl get secret ... -o jsonpath` pattern already used elsewhere in this
  repo's tooling) into their own gitignored Vite `.env.local`.

Application code reads config through a single loader, not two separate code
paths: it checks `window.__ADMIN_UI_CONFIG__` first (present in the built
container) and falls back to `import.meta.env.VITE_*` when absent (dev,
where no runtime-injected global exists) — one code path serves both
environments.

## Browser OTel via API proxy

- Jaeger's `jaegertracing/all-in-one:1.62.0` image (the version pinned in
  `charts/jaeger/values.yaml`) natively accepts OTLP/HTTP on port 4318 with
  no extra flags — confirmed past the known v1.59 OTLP-port regression. Its
  k8s `Service` currently declares only `ui`(16686) and `otlp-grpc`(4317);
  4318 needs adding.
- **Backend addition**: one new minimal-API route, `POST /v1/traces`, on the
  existing `Iverson.Api` — a byte-for-byte HTTP relay (no protobuf
  decode/re-encode) to Jaeger's `4318/v1/traces`, via a named `HttpClient`
  (same pattern as `AuthentikAdminClient`'s named-client setup). **Requires
  authentication** (any valid token, no specific policy) — leaving it
  anonymous would reproduce the unauthenticated-public-ingestion exposure
  this proxy approach was chosen to avoid.
- **Infra**: add port 4318 to Jaeger's `Service`; widen exactly two
  `NetworkPolicy` rules by one port each —
  `{{ .Release.Name }}-jaeger-ingress` (currently 4317-only, from api/worker
  pods) and `{{ .Release.Name }}-api-egress`'s jaeger rule (currently
  4317-only) — both confirmed as single-line diffs. The worker's egress rule
  is untouched (only the API hosts this route).
- **No new ingress route needed** — `/v1/traces` is just another path on the
  API's existing `Program.cs`/ingress.
- **No CORS needed** — the admin-ui (`iverson.local/admin`) and the API
  (`iverson.local/`) share the same origin (scheme+host+port; path doesn't
  count), so both the trace-export calls and W3C `traceparent` correlation
  between frontend and backend spans work with zero cross-origin
  configuration.
- **Frontend**: `@opentelemetry/sdk-trace-web` + `instrumentation-fetch`
  (auto-traces grpc-web calls) + `instrumentation-document-load` (page-load
  spans) + `exporter-trace-otlp-http` pointed at the relative, same-origin
  `/v1/traces`, with the same Bearer token grpc-web calls already attach.

## Cloud wiring

Extends the pattern the existing `api` ingress already uses in each cloud
values file — no new TLS mechanism invented:
- `values-aws.yaml`: admin-ui and Authentik's new ingress get
  `className: "alb"` + a placeholder `alb.ingress.kubernetes.io/
  certificate-arn` annotation, matching the existing api ingress's
  "replace before applying" comment.
- `values-azure.yaml` / `values-gcp.yaml`: `className:
  "azure-application-gateway"` / `"gce"` + placeholder `tlsSecretName`
  values (`iverson-admin-ui-tls`, `iverson-authentik-tls`), same
  "replace with a real cert-manager-issued secret" comment already used for
  `api.ingress.tlsSecretName`.
- The admin-ui ingress reuses `global.ingressHost` directly
  (`iverson.example.com` for cloud), same as `api` does today. Authentik's
  ingress uses `authentik.` prefixed onto that same value
  (`authentik.iverson.example.com`), since its hostname is templated off
  `global.ingressHost` rather than hardcoded (see Auth Flow) — both follow
  whatever real domain is configured per cloud, automatically.
- This does **not** produce a working end-to-end cloud deployment — exactly
  as true for the existing api ingress today. It keeps the new pieces
  consistent with that same, already-accepted, deliberately-incomplete
  state.

## Testing

Vitest + React Testing Library (matching `Iverson.Clients/TypeScript`'s
existing test runner):
- Nav renders the correct set of links for each combination of `groups`
  claim (operator, tenant-admin, both, neither).
- The auth-gated layout route redirects an unauthenticated visitor into the
  login flow.
- Default route (`/admin`) redirects to `/admin/performance`.

No integration/e2e test against a real Authentik instance in this
sub-project — that requires a live compose/kind environment. Instead, add a
short note to `docs/user-management-and-security.md`'s existing
operator-onboarding procedure, which currently says a browser login happens
"through whatever frontend/tool initiates the Authorization Code flow" — now
it can name this app.

## Out of scope

- Real content for any of the four pages (Performance, Storage, Tenants,
  Tenant Admin) — each future sub-project's own job.
- A working end-to-end cloud deployment (see Cloud Wiring above).
- Handling a revoked/expired refresh token beyond the basic case (redirect
  to login) — no special recovery UI.

## Known issue found during review, not part of this design

While verifying the `offline_access` requirement for `iverson-oidc-default`'s
silent renewal, the same check applied to the sibling `iverson-loadtest-human`
client (per the skill's "check the whole set, not just the instance you're
touching" discipline) revealed it has the identical gap already:
`grant_types` includes `refresh_token`, but no `offline_access` scope mapping
is attached — so its silent-renewal capability may be non-functional today.
This design does not touch `iverson-loadtest-human` at all. The acting-user
smoke-test script (`mint_acting_user_token.py`) only performs one-shot code
exchange, never renewal, so this is likely dormant/harmless in practice —
flagged for a human to decide whether it's worth its own fix, not addressed
here.

## Verified assumptions

All verified against the actual codebase/docs/upstream sources during
design, not taken on faith:

- `Iverson.Clients/TypeScript/scripts/generate_protos.sh:12-17` — confirmed
  exact existing `protoc` + `ts-proto` invocation and options.
- ts-proto's browser-client flag is `outputClientImpl=grpc-web` (requiring
  `@improbable-eng/grpc-web`), not `outputServices=grpc-web` — corrected via
  ts-proto's own README/GitHub.
- `blueprints-configmap-service-clients.yaml:111-114` — confirmed
  `iverson-oidc-default`'s current placeholder redirect URI, strict matching
  mode, `https://` scheme.
- `values.yaml:22` / `values-local.yaml:66-70` — confirmed
  `global.ingressHost = "iverson.local"` and kind's `tlsSecretName: ""`
  (plain HTTP only), so the existing placeholder's `https://` scheme is
  already broken for kind.
- Authentik derives `iss` from the request's Host header — confirmed via
  this repo's own `docs/user-management-and-security.md` troubleshooting
  section and `mint_acting_user_token.py`'s documented Host-header-override
  workaround.
- ASP.NET Core's `TokenValidationParameters.ValidIssuers` (plural) validates
  independently of `MetadataAddress` — confirmed via Microsoft's official
  JWT bearer authentication docs.
- oidc-client-ts's `signinSilent()` prefers refresh-token-based renewal (no
  iframe) when a refresh token is present — confirmed via oidc-client-ts's
  own docs.
- Authentik requires the `offline_access` scope attached *and requested*,
  not just `grant_types`, to issue a refresh token (2024.2+) — confirmed via
  Authentik's official docs; this was a correction to the original design.
- `iverson-loadtest-human`'s existing `property_mappings`
  (`blueprints-configmap-service-clients.yaml:143-145`) — confirmed directly,
  revealing the adjacent finding above.
- `TenantAdminAuthorizationPolicy.cs` and `OperatorAuthorizationPolicy.cs` —
  both read directly; confirmed exact group/scope strings
  (`"tenant-admins"`, `"operators"`, `"admin"`).
- ingress-nginx merges multiple `Ingress` resources for the same host by
  longest-path-match, independent of which resource declared each path —
  confirmed via ingress-nginx's own documentation/issue tracker.
- `charts/jaeger/templates/service.yaml` — confirmed directly: only
  `ui`(16686) and `otlp-grpc`(4317) currently declared.
- `charts/jaeger/values.yaml:1` — confirmed `imageTag: "1.62.0"`; confirmed
  via search this version has OTLP/HTTP enabled by default, past the known
  v1.59 regression.
- `templates/networkpolicies.yaml` — confirmed exact shape of
  `{{ .Release.Name }}-jaeger-ingress` and `{{ .Release.Name }}-api-egress`;
  both are single-port-list one-line additions for 4318.
- Grepped the whole repo for `placeholder-callback`: all other references
  (`mint_acting_user_token.py`, `Iverson.LoadTest`, its README) target
  `iverson-loadtest-human`/loadtest tooling exclusively, confirmed via
  `mint_acting_user_token.py`'s own `DEFAULT_COMPOSE_CLIENT_ID` and
  `--client-id` default logic — none reference `iverson-oidc-default`, so
  this design's redirect-URI change is safely scoped.
- Grepped all values files for an existing per-service `enabled` boolean
  pattern: only `networkPolicy.enabled` exists — confirmed no such
  convention to follow for `adminUi`.
- Re-read `deploy/kind/build-and-load-image.sh` directly: confirmed hardcoded
  to the api image/Dockerfile, only `tag`/`cluster-name` parameterized,
  including its podman-vs-docker re-tag logic that must be preserved when
  generalized.
- `blueprints/compose-only/service-clients.yaml` — confirmed
  `iverson-oidc-default`'s compose client-id is a fixed literal
  (`dev-iverson-human-oidc-client-id`), unlike kind's Helm-random value —
  resolves the local-dev config mechanism cleanly (see Deployment).
- Authentik's scope-mapping claims (including `groups`) populate the ID
  token, not just the access token — confirmed via Authentik's own
  property-mappings documentation. Load-bearing for the Nav Skeleton's
  role-based filtering, which reads `oidc-client-ts`'s `user.profile.groups`
  (decoded from the ID token only).
- Authentik's OAuth2 Provider `redirect_uris` field accepts a list of
  multiple entries, not just one — confirmed via Authentik's own OAuth2
  provider documentation. Load-bearing since the design registers two
  entries (kind + local dev) on `iverson-oidc-default`.
