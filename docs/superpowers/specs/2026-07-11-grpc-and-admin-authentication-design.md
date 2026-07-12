# gRPC and Admin Endpoint Authentication Design

**Date:** 2026-07-11
**Origin:** Part 2 of the 5-part identity-management initiative (originally scoped as "Admin endpoint
protection"), expanded during brainstorming to also absorb Part 3's scope ("Client API service
authentication") since both share the same ASP.NET Core pipeline and, on investigation, the same
underlying mechanism.

Iverson's `Iverson.Api` process currently has zero authentication or authorization anywhere: the
three `/admin/*` endpoints (`reconcile`, `dlq` list, `dlq replay`) and the four gRPC services
(Search, Post, Update/Persistence, Mapping) are all mapped on the same Kestrel listener (port 8080),
reachable from the public ingress with no NetworkPolicy path distinction between them, and with no
`[Authorize]`, `AddAuthentication`, or `AddAuthorization` anywhere in the codebase. Part 1 (Authentik
IdP deployment) already stood up the identity infrastructure this design consumes, but zero C# code
integrates with it yet.

## Scope

- **In scope:** authenticating and authorizing calls to the 4 gRPC services and the 3 `/admin/*`
  HTTP endpoints, for both human operators and machine callers (calling services, CI/runbook
  automation).
- **Out of scope:** end-user identity propagation through the gRPC calls themselves (Part 4 — the
  *acting human user's* identity, as opposed to the *calling service's* identity, which this design
  covers); row/field-level authorization (Part 5).

## Decisions carried in from brainstorming

- **Two authorization tiers, not one.** gRPC services require only "any authenticated known caller"
  — no role distinction, matching the original Part 3 framing. Admin endpoints additionally require
  an "operator" role/scope, satisfied by either a human's Authentik group membership or a dedicated
  `admin` scope granted only to the CI/runbook automation client — matching the original Part 2
  framing. One policy accommodates both caller shapes since a client-credentials token has no human
  "groups" concept at all.
- **OIDC/OAuth2 for both paths**, not the original plan's ambiguous "SAML-derived login" language
  for the human path — Part 1 already provisioned an OIDC public client (Authorization Code + PKCE);
  SAML in this repo is Authentik acting as an IdP *for other apps*, not something a curl/CLI-based
  API client consumes. MFA is enforced by Authentik's login flow regardless of protocol, so this
  doesn't weaken the original "MFA-enforced" requirement.
- **Each calling service and automation caller gets its own distinct client-credentials identity**
  (own `client_id`/`client_secret`), not a shared secret — matching the original Part 3 wording
  ("a Bearer token per calling service") and enabling per-caller revocation/attribution.
- **Hard cutover.** Authentication becomes mandatory the moment this ships; no permissive/warn-only
  rollout window. Every existing caller must be updated alongside this change.
- **All 5 client SDKs get the capability**, not just the in-repo .NET one — both known callers
  (Iverson.LoadTest, in-repo; Iverson.WebTest, external) are .NET today, but the design adds the
  mechanism to Java/Python/Go/TypeScript too per explicit user direction, so it's available
  uniformly rather than only where a caller happens to exist yet.

## Design

### 1. API-side authentication & authorization

**Package:** `Microsoft.AspNetCore.Authentication.JwtBearer` added to `Iverson.Api.csproj` (currently
absent — confirmed via repo-wide grep).

**Middleware (`Program.cs`):** `AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
.AddJwtBearer(...)`, with:
- `Authority` = `http://{{ .Release.Name }}-authentik:9000/application/o/iverson-api/` (in-cluster
  hostname; confirmed reachable once an Application is bound — see Section 2).
- `TokenValidationParameters.ValidAudiences` = **a list**, not a single value — confirmed empirically
  that Authentik's `aud` claim equals the calling client's own `client_id`, which differs per caller.
  The list contains every registered client_id: the human public client plus each service/automation
  client from Section 2.
- `app.UseAuthentication(); app.UseAuthorization();` inserted before endpoint mapping.

**Authorization model — fallback-deny by default.** A global `FallbackPolicy` requires authentication
on every endpoint; `/health`, `/health/live`, and `/probe/*` are explicitly `.AllowAnonymous()`
(kubelet-only reachable per existing NetworkPolicy; the kubelet sends no token). This closes the
exact failure mode that left `/admin/*` unauthenticated in the first place — future endpoints are
protected by default unless someone explicitly opts them out.

**Two policies**, confirmed via Microsoft's own docs that `.RequireAuthorization()` chained onto
`MapGrpcService(...)` enforces a policy on gRPC calls exactly as it would a regular endpoint (no
custom interceptor needed):
- **Default (fallback) policy** — any authenticated caller. Applied to the 4 gRPC services.
- **"Operator" policy** — applied to the 3 admin routes via `.RequireAuthorization("Operator")`.
  Satisfied by **either** a `groups` claim containing `operators` (human path) **or** a `scope`
  claim containing `admin` (automation path) — confirmed the `scope` claim is a plain
  space-separated string, not an array, so the check is a substring/token match.

**New config**, matching the existing `Qdrant`/`Kafka`/`Otel` naming convention:
`Authentication:Authority`, `Authentication:ValidAudiences` (array).

### 2. Authentik-side provisioning

Continuing Part 1's config-as-code Blueprint pattern. Every field below was determined by live
testing against a running Authentik instance, not assumed from documentation:

1. **Application binding for the existing human OIDC provider** (`iverson-oidc-default` → new
   `iverson-api` Application, slug `iverson-api`) — required for the standard discovery URL to
   resolve at all; confirmed 404 without it, 200 with it.
2. **Three new confidential OAuth2 providers**, one per caller (`iverson-loadtest`,
   `iverson-webtest`, `iverson-admin-automation`), each with its own Application binding. Each
   requires, confirmed necessary by live testing (none of these work correctly left at their
   schema defaults):
   - `client_type: confidential`, explicit `client_id`/`client_secret` (schema accepts explicit
     values; empty `redirect_uris: []` is accepted since this grant type never redirects).
   - `grant_types: ["client_credentials"]` **explicitly set** — the schema defaults this to `[]`,
     which the token endpoint rejects with `invalid_grant`.
   - `signing_key`: **explicitly set** to the shared `authentik Self-signed Certificate` — left
     unset, Authentik signs with `HS256` using the client's own secret as the HMAC key (confirmed by
     decoding an actual issued token's header), which cannot be validated via a standard JWKS
     endpoint (JWKS only ever publishes asymmetric public keys). Setting `signing_key` switches
     tokens to `RS256`, confirmed via a second decoded token plus a live JWKS fetch showing the
     matching public key.
   - `issuer_mode: "global"` **explicitly set** — left at the schema default (`per_provider`), each
     Application's tokens carry a distinct `iss` tied to its own slug (confirmed via two decoded
     tokens from two different test Applications). `global` mode produces one consistent `iss` value
     across all providers (confirmed), which is what makes a single `Authority`/issuer-validation
     config work for tokens from any of the 4 clients.
3. **This same `signing_key`/`issuer_mode` correction also applies to the existing human provider**
   from Part 1 (`iverson-oidc-default`) — it was also left at the schema defaults (confirmed
   `signing_key: null`), so it needs the same explicit values for consistency with the other three.
   It also needs an **explicit `client_id`**: confirmed live that Authentik auto-generated a random
   `client_id` for this provider since Part 1's blueprint never set one, so — unlike the label
   "known client_id value" in Section 3 assumed — there is no fixed, `lookup`-accessible value for
   it today. No `client_secret` is needed (it stays `client_type: public`/PKCE).
4. **A dedicated `admin` custom OAuth2 scope mapping**, granted only to
   `iverson-admin-automation`'s `property_mappings` — this is what the Operator policy's automation
   check looks for. `iverson-loadtest`/`iverson-webtest` don't get it.
5. **A custom `groups` OAuth2 scope mapping**, granted to the human provider — confirmed none of
   Authentik's 7 built-in scope mappings (`openid`, `email`, `profile`, `offline_access`, `ak_proxy`,
   `entitlements`, `goauthentik.io/api`) include group membership.
6. **An `operators` Authentik Group.** Membership is a runtime/operational step (adding specific
   people), not hardcoded into the Blueprint — matching how Part 1 left the bootstrap-admin flow
   open-ended.

### 3. Credential provisioning mechanism

Part 1's blueprint ConfigMap (`blueprints-configmap.yaml`) loads static files via
`.Files.Glob`/`.Files.Get` — raw content, no `{{ }}` templating. This design adds a second,
genuinely-templated mechanism alongside it, since the new providers need Secret-sourced values
embedded into their Blueprint YAML:

1. **Four new lookup-guarded Secrets** (`iverson-loadtest-client`, `iverson-webtest-client`,
   `iverson-admin-automation-client`, each holding a generated `client-id`/`client-secret` pair; and
   `iverson-human-oidc-client`, holding only a generated `client-id` — no secret, since the human
   provider stays `client_type: public`/PKCE — for the explicit `client_id` Section 2 point 3
   requires) — same `randAlphaNum` + `lookup`-guard pattern used for every other secret in this repo
   (confirmed `lookup` correctly reads real Secret `.data` values during an actual `helm
   install`/`upgrade` — note `helm template` alone always returns an empty map for `lookup` by
   design, regardless of cluster state; this is not itself a design problem, just a fact about how
   to *test* the mechanism).
2. **A second, templated ConfigMap** whose Blueprint YAML content embeds those values (plus the
   `grant_types`/`signing_key`/`issuer_mode` fields from Section 2) at Helm render time via
   `lookup`, so the created provider objects have the exact client_id/secret already in the
   Secrets — no "fetch the generated value back out of Authentik" step needed.
3. Mounts into Authentik's blueprints directory alongside Part 1's static ConfigMap (a subdirectory,
   e.g. `/blueprints/custom/service-clients/` — confirmed Authentik's blueprint scan is recursive).
4. **`Authentication:ValidAudiences`** (Section 1) is populated from all four client_ids — the three
   service/automation Secrets' `client-id` keys, plus the new `iverson-human-oidc-client` Secret's
   `client-id` key — via the same `lookup` mechanism, rendered as the Helm-conventional indexed
   env-var list (`Authentication__ValidAudiences__0`, `__1`, ...).

### 4. Client-side changes, all 5 SDKs

Same conceptual capability per language — attach `Authorization: Bearer <token>` via that language's
per-call credentials mechanism, with an in-memory token cache doing the OAuth2 Client Credentials
fetch/refresh. Confirmed empirically (built a real listening server and checked whether the header
actually arrived, not just whether client code threw) that **the correct mechanism differs
meaningfully by language** — this repo's plaintext h2c deployment (no TLS anywhere in the stack)
exposed real gaps in each language's default behavior:

- **.NET** (`Iverson.Client.Core`): new optional parameter on `AddIversonClient`, wired via
  `Grpc.Net.ClientFactory`'s `.AddCallCredentials(...)` **plus** `.ConfigureChannel(o =>
  o.UnsafeUseInsecureChannelCallCredentials = true)`. Confirmed: without the second call, credentials
  are **silently dropped** — no exception anywhere, the call just goes out with no `Authorization`
  header. Microsoft's own docs state this explicitly; it was not obvious from the API surface alone.
- **Java**: new constructor overload `IversonClient(String host, int port, CallCredentials
  credentials)`, grpc-java's own `CallCredentials`. Confirmed works with no special channel
  configuration — the header arrives over plaintext by default.
- **Python**: new constructor parameter on `IversonClient.__init__`. Confirmed the naive approach
  (`grpc.insecure_channel(...)` + a `credentials=` kwarg per call) is **flatly rejected** with
  `UNAUTHENTICATED: Established channel does not have a sufficient security level to transfer call
  credential`. The correct mechanism is `grpc.secure_channel(target,
  grpc.composite_channel_credentials(grpc.local_channel_credentials(), call_creds))` —
  `local_channel_credentials()` is a lightweight "trusted network" designation (not real TLS) that
  satisfies grpcio's security-level check without requiring actual certificates.
- **Go**: **no signature change needed** — `NewIversonClient(target string, opts
  ...grpc.DialOption)` already forwards dial options. This design adds an exported helper type
  implementing `credentials.PerRPCCredentials` with `RequireTransportSecurity() bool { return
  false }` — confirmed via grpc-go's actual per-call invocation source (`http2_client.go`'s
  `getCallAuthData`, not just the channel-construction-time gate) that this correctly allows the
  credential through over plaintext.
- **TypeScript**: new constructor parameter `callCredentials?: grpc.CallCredentials`. Confirmed the
  attachment **must** go through per-call `CallOptions.credentials` on each RPC invocation — the
  channel-level `grpc.credentials.combineChannelCredentials()` helper explicitly **throws**
  (`Cannot compose insecure credentials`) when the base channel credentials are insecure, so it
  cannot be used with this repo's plaintext deployment.

**LoadTest wiring:** new env vars `IVERSON_CLIENT_ID`/`IVERSON_CLIENT_SECRET`/`IVERSON_TOKEN_ENDPOINT`
in `LoadTest/Program.cs` (matching its existing `IVERSON_GRPC_URL` convention — confirmed no SDK has
an env-var convention today, so this is LoadTest's own pattern, not a new cross-SDK standard).

### 5. NetworkPolicy changes

Only the `api` role needs a new path (confirmed gRPC/admin mapping is gated to `workloadRole ==
"api"`; `worker` never serves these endpoints):
- New egress rule on `api-egress`: `api` → `authentik-server:9000` (OIDC discovery + JWKS
  fetch/refresh).
- New ingress entry on `authentik-ingress`: accept from `api` on port 9000 (currently accepts only
  kubelet health-probe traffic — confirmed zero existing path from either direction).

`worker-egress` is untouched.

### 6. Deployment-target parity & testing

**docker-compose gap:** Section 3's Helm-templated mechanism doesn't apply to docker-compose (no
templating engine). Resolution: a docker-compose-only blueprint file at
`blueprints/compose-only/service-clients.yaml` with hardcoded dev-only values (matching Part 1's
existing precedent for dev-only credentials directly in `docker-compose.yml`) — including the same
`grant_types`/`signing_key`/`issuer_mode` fields. Confirmed this doesn't collide with Helm's existing
`.Files.Glob "blueprints/*.yaml"` static loader (single-level match, per Go's `filepath.Match`
semantics — `*` never matches a path separator), while still being picked up by docker-compose's
full-directory bind-mount plus Authentik's recursive blueprint scan. `iverson-api`'s docker-compose
service also needs `Authentication__Authority`/`Authentication__ValidAudiences__*` env vars, pointed
at the compose-network Authentik hostname — confirmed `iverson-api` already exists in
`docker-compose.yml` with a directly analogous env-var-injection pattern (`Otel__Endpoint`,
`Kafka__BootstrapServers`, etc.).

**Testing:**
- **Unit tests** for the "Operator" policy's dual-claim logic (satisfied by `groups` containing
  `operators` OR `scope` containing `admin`; rejected otherwise) — pure claims-evaluation logic.
- **Live kind smoke test:** confirm unauthenticated calls to admin routes and gRPC services are
  rejected; confirm each of the 3 caller identities gets the access level the design specifies — a
  service-caller token works for gRPC but is rejected on `/admin/*`; the admin-automation token
  works on both.
- **docker-compose smoke test:** same shape, confirming local dev parity.

## Verified assumptions

All verified by direct evidence (live Authentik API calls, real client/server test harnesses, or
authoritative source/documentation) rather than assumed — see inline citations throughout the
Design section above. Summary list:

| # | Assumption | Evidence |
|---|---|---|
| 1 | gRPC libraries in all 5 languages allow call-credentials over a plaintext channel, but the mechanism differs per language | Built a real minimal listening server; confirmed header arrival (or rejection) for each language directly — see Section 4 |
| 2 | gRPC endpoints participate in ASP.NET Core's standard Authentication/Authorization pipeline | Microsoft Learn docs (`aspnet/core/grpc/authn-and-authz`), explicit `.RequireAuthorization()` example on `MapGrpcService` |
| 3 | Authentik's OIDC discovery URL requires an Application binding | Live test: 404 unbound, 200 bound, at `/application/o/<slug>/.well-known/openid-configuration` |
| 4 | No default `groups` scope mapping exists | Live query of `/api/v3/propertymappings/provider/scope/` — 7 built-in mappings, none named `groups` |
| 5 | `scope` claim is a plain string, not an array | Decoded a real issued JWT payload |
| 6 | Empty `redirect_uris` and explicit `client_id`/`client_secret` accepted by the provider schema | Live `POST /api/v3/providers/oauth2/` — HTTP 201 |
| 7 | Helm `lookup` reads real Secret data (not just existence) | Real `helm install` (not `helm template`) against the live kind cluster, confirmed decoded value matched |
| 8 | `.Files.Glob "blueprints/*.yaml"` is single-level | Go `filepath.Match` semantics (authoritative stdlib behavior — `*` never matches `/`) |
| 9 | Authentik's blueprint scan is recursive | Part 1's own bundled blueprints (`default/`, `system/`, `testing/` subdirectories) are auto-discovered |
| 10 | `iverson-api` docker-compose service has a matching config-injection convention | Read `docker-compose.yml` directly |
| 11 | Nothing else in-repo depends on unauthenticated `/admin/*` or gRPC access | Repo-wide grep for references to these ports/paths in scripts/CI/deploy configs |
| 12 | `grant_types` must be explicitly set for client_credentials providers | Live token request against a provider left at schema default — `invalid_grant` |
| 13 | `signing_key` must be explicitly set or tokens are unvalidatable via JWKS | Decoded token header showed `HS256` when unset; `RS256` + matching JWKS entry when set |
| 14 | `issuer_mode` must be `"global"` for consistent `iss` across providers | Decoded tokens from two different test Applications under both modes |
| 15 | `aud` = calling client's own `client_id` | Decoded a real client_credentials token |
| 16 | `FallbackPolicy` + `AllowAnonymous()` reliably exempts specific endpoints from a global auth requirement | Microsoft Learn (`aspnet/core/security/authorization/secure-data`): "action methods with an authorization attribute... use the applied authorization attribute rather than the fallback authorization policy" |
| 17 | `api-egress` has zero path to Authentik; `authentik-ingress` accepts nothing from `api`/`worker` (only kubelet probes) | Fresh read of `templates/networkpolicies.yaml` against current `main` — no `authentik` reference in `api-egress`'s block; `authentik-ingress` still only has a `from: []` kubelet rule on port 9000 |

## Explicitly out of scope

- End-user identity propagation (Part 4) — this design authenticates the *calling service*, not the
  human end-user acting through it.
- Row/field-level authorization (Part 5).
- Any change to the `.proto` contracts themselves (only client-side call construction changes).
- TLS for the gRPC/HTTP listener — this design works within the existing plaintext h2c deployment;
  enabling TLS is a separate, larger change not requested here.

## Self-review

- **Placeholder scan:** no TBD/TODO; every provisioning field, config key, and per-language mechanism
  named explicitly with the evidence that determined it.
- **Internal consistency:** the two-tier authorization model (Section 1) is consumed consistently by
  Section 2's scope-mapping design and Section 6's testing plan.
- **Scope:** the merge of Parts 2+3 was an explicit user decision (documented above), not a silent
  expansion; Parts 4-5 remain explicitly deferred.
- **Ambiguity check:** "SAML-derived login" from the original plan is explicitly resolved to
  OIDC/OAuth2 with a stated rationale, so this doesn't get re-litigated in the implementation plan.
