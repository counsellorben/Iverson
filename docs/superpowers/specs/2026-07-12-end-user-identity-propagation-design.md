# End-user identity propagation — design

**Origin:** Part 4 of the 5-part identity-management initiative. Part 1 (Authentik IdP deployment)
and Part 2+3 (gRPC/admin endpoint authentication) are complete and merged. This spec covers Part 4
only — Part 5 (row/field-level authorization) is out of scope and gets its own brainstorm → spec →
plan cycle once this ships.

## Scope

**In scope:** the 4 gRPC services (`ObjectMappingGrpcService`, `ObjectPersistenceGrpcService`,
`ObjectRetrievalGrpcService`, `ObjectSearchGrpcService`). Validate an optional second identity —
the human "acting user" on whose behalf a calling service is making the call — alongside the
existing service-credential auth from Part 2+3, expose it to service-layer code, and emit one
structured log line per call when present.

**Explicitly out of scope:**
- Any authorization decision based on *who* the acting user is. This design only validates that
  the token is well-formed and genuinely issued by Authentik to a real user — it does not change
  whether any call succeeds or what data it returns. That's Part 5's job.
- The 3 `/admin/*` HTTP endpoints. Those already authenticate the human directly (Part 2+3); there
  is no "calling on behalf of" concept there — the operator *is* the caller.
- Any `.proto` contract change.

**Motivation:** this is explicitly a foundation for Part 5, not an end in itself. Part 5's
row/field-level authorization will need a validated end-user identity to check against; this design
builds the plumbing that gets it there, plus a minimal observable signal (the log line) that proves
the plumbing works end-to-end today, before Part 5 exists to consume it.

## Wire protocol

Dual-token pass-through via gRPC metadata, not a proto field — matching how the existing
service-credential Bearer token already travels as a call credential rather than a message field:

- `authorization` metadata key: unchanged from Part 2+3. Always the calling service's own
  client-credentials token.
- `x-acting-user-authorization` metadata key (new): `Bearer <token>`, the human's own
  Authentik-issued access token — present only when the calling service is acting on behalf of a
  specific human request. **Optional** — its absence is not an error; a call with only the service
  token behaves exactly as it does today (covers automation/background/CI callers with no human
  behind them).

Every calling service's human end-users authenticate through this same Authentik instance (there is
no external corporate IdP and no calling service is expected to run its own separate end-user auth)
— this is what makes independent cryptographic validation of the second token possible without any
token-exchange step.

**Why not OAuth2 Token Exchange (RFC 8693):** verified directly against Authentik's own OAuth2
provider docs — the supported grant types are authorization code, implicit, hybrid, client
credentials, device code, and refresh token. No token-exchange grant. Token exchange would have let
the calling service trade the human's token for one asserting "service X acting as user Y," which
Iverson could then treat as a single verified assertion; without it, the design instead sends both
tokens independently and validates each on its own terms.

## Server-side validation

A second ASP.NET Core JwtBearer scheme, `"ActingUser"`:
- Same Authentik `Authority` as the existing (default) scheme.
- Its own audience allowlist, `Authentication:ActingUser:ValidAudiences` — config-driven like the
  existing `Authentication:ValidAudiences`, but a **separate list**, populated with the client_ids
  of calling services' human-facing OIDC clients as they're onboarded. Kept separate from the
  service-credential audience list because the two token types are never interchangeable — a
  client-credentials token has no human `sub` at all, so accepting one as an "acting user" assertion
  would be a category error, not just a permissive default.
- Token source redirected from the default `Authorization` header to the
  `x-acting-user-authorization` metadata key via `JwtBearerEvents.OnMessageReceived` (the standard
  ASP.NET Core extension point for this — confirmed against Microsoft's own docs).

A gRPC interceptor, registered once and applied globally
(`AddGrpc(options => options.Interceptors.Add<T>())`), covering all 4 services uniformly. Because 2
of the 4 services (`ObjectRetrievalGrpcService`, `ObjectSearchGrpcService`) use server-streaming
RPCs, the interceptor overrides both `UnaryServerHandler` and `ServerStreamingServerHandler`,
sharing one common validation method between them (confirmed via `Grpc.Core.Interceptors.Interceptor`
— these are genuinely distinct override points, not one call site). Behavior:

- `x-acting-user-authorization` metadata key absent → proceed exactly as today. No acting user.
- Present and valid (signature, expiry, issuer, and audience all check out against the `ActingUser`
  scheme) → populate the identity (see below) and log one structured line:
  `service {ServiceClientId} acting as user {ActingUserSub} called {Method}`.
- Present and invalid (expired, bad signature, wrong issuer, audience not in the allowlist) →
  reject the call with `RpcException(StatusCode.Unauthenticated, ...)`. A calling service sending a
  malformed acting-user assertion is either buggy or misconfigured, and since Part 5 will eventually
  depend on this identity being trustworthy, silently downgrading to "no acting user" here would
  hide exactly the kind of bug that becomes security-relevant later.

**Exposure to service code:** a scoped `IActingUserAccessor` (same idiom as ASP.NET Core's own
`IHttpContextAccessor`), populated by the interceptor before the RPC handler executes, exposing
`ClaimsPrincipal? ActingUser` — nullable, since it's optional. The interceptor and the gRPC service
implementation share the same per-request DI scope (Grpc.AspNetCore interceptors have per-request
lifetime by default), so a constructor-injected accessor in the service class sees whatever the
interceptor set. Nothing reads it today beyond the structured log line; Part 5's future
authorization checks are the intended consumer.

**Ordering:** the existing service-credential JwtBearer scheme's `ClaimsPrincipal` (`HttpContext.User`)
is already populated and enforced by `UseAuthentication()`/`UseAuthorization()` middleware before any
gRPC endpoint or interceptor executes — this is not just a general ASP.NET Core guarantee but
already empirically relied on by Part 2+3's `RequireAuthorization`/`FallbackPolicy`, which
demonstrably works against these same 4 services today.

## Authentik provisioning

Extend the existing blueprint pattern (`blueprints-configmap-service-clients.yaml`) with a new range
of **public, PKCE-enabled** OAuth2 clients — one per calling service that wants to support
acting-user propagation. This is parallel to, but distinct from, both:
- the existing per-service *confidential* client-credentials clients (`iverson-loadtest`,
  `iverson-webtest`, `iverson-admin-automation`) — those authenticate the service, not a human;
- `iverson-oidc-default` — that authenticates an Iverson admin operator logging into Iverson's own
  admin console directly, a different scenario from a third-party calling service's own end-users.

Since no real external calling service exists in this repo, `Iverson.LoadTest` is the concrete
reference implementation: it already has a machine identity (`iverson-loadtest`) from Part 2+3 and
gains a paired human-facing client, `iverson-loadtest-human`, so the full flow can be exercised
end-to-end — mirroring how Part 2+3's Task 10 used LoadTest as the live smoke-test harness.

## SDK per-language mechanisms

The acting-user token varies per call — unlike the service token, which is static for the lifetime
of a client instance, since a calling service serves many different end-users concurrently. Each of
the 5 SDKs needs its own per-call passing idiom, reusing each language's existing generated-stub or
credentials-layer hook rather than inventing new plumbing:

| Language | Mechanism | Verified |
|---|---|---|
| .NET | No new core plumbing — grpc-dotnet's generated client methods already accept a per-call `Metadata headers` parameter, which combines with whatever `AddCallCredentials` adds (confirmed by reading the existing `AttachCredentials`/`AddCallCredentials` wiring in `ServiceCollectionExtensions.cs`). Add a header-key constant and a tiny `Metadata.AddActingUser(token)` extension for ergonomics only. | Read existing source |
| Go | Ambient `context.Context` value — new `iverson.WithActingUserToken(ctx, token) context.Context` helper; `OAuth2ClientCredentials.GetRequestMetadata(ctx, ...)` reads it and adds the second metadata entry when present. Confirmed: grpc-go's client infrastructure passes the caller's own per-call `ctx` (enriched with `RequestInfo`, not replaced) into `GetRequestMetadata`. | Confirmed via grpc-go docs/source |
| Java | `CallOptions.Key<String>` — new public `ACTING_USER_TOKEN` key; caller does `stub.withOption(ACTING_USER_TOKEN, token).search(request)`; `OAuth2ClientCredentials.applyRequestMetadata` reads `requestInfo.getCallOptions().getOption(ACTING_USER_TOKEN)`. Confirmed: `CallCredentials.RequestInfo.getCallOptions()` exists and returns the call's `CallOptions`. | Confirmed via grpc-java Javadoc |
| Python | Explicit per-call metadata — new `acting_user_metadata(token)` helper returning a tuple; caller passes it via the stub's existing `metadata=` kwarg, which combines with whatever the `AuthMetadataPlugin` (service credential) adds. | Standard gRPC CallCredentials semantics (additive, not replacing) |
| TypeScript | Explicit per-call metadata — new `createActingUserMetadata(token)` helper returning a `grpc.Metadata`; caller merges it into the call's metadata argument, which combines with the `CallCredentials`-supplied metadata. | Standard gRPC CallCredentials semantics (additive, not replacing) |

## Testing plan

**Automated (unit/integration):** the interceptor's three branches (absent / valid / invalid) get
deterministic coverage using a test-only signing key, not live Authentik — extending the existing
`AuthTestWebApplicationFactory`/`AuthenticationPipelineTests` pattern (which today only exercises
the "no token" 401 path) with a hand-signed JWT for the positive/negative acting-user cases.

**Scripted end-to-end smoke test.** Part 2+3's Task 10 found that the browser + PKCE + MFA
human-login path could not be scripted with curl and left it as a manual post-deploy step. This
design instead scripts it, using Authentik's flow-executor API
(`/api/v3/flows/executor/<slug>/`, a get-challenge/solve-challenge JSON API, session-cookie-based —
confirmed this is Authentik's documented mechanism for non-interactive flow execution) against a
dedicated test human user, provisioned via blueprint with a known plaintext `password` (confirmed
`authentik_core.user` supports this directly):

1. **Identification + password stages** (`default-authentication-identification`,
   `default-authentication-password`) — solved directly with the test user's known credentials.
2. **MFA stage** (`default-authentication-mfa-validation`) — confirmed via Authentik's own stock
   `default-authentication-flow` blueprint (fetched from source) that these three stages, in this
   order, are exactly what this repo's `mfa-enforcement.yaml` blueprint configures by matching that
   same stock stage name. On a test user with no enrolled device, the enrollment challenge response
   itself contains the TOTP shared secret (the same way a QR code would show it to a human) — the
   script captures it, computes a valid code, and solves the stage. Idempotent: subsequent runs
   validate against the already-enrolled device instead of re-enrolling.
3. **Authorization Code + PKCE** — the script generates a `code_verifier`/`code_challenge` pair,
   drives the OAuth2 authorization request through the now-authenticated session, captures the
   redirected `code`, and exchanges it at the token endpoint for a real Authentik-signed access
   token.
4. That token is fed into a LoadTest invocation carrying both the service credential and the
   acting-user token, confirming the interceptor accepts it, populates the accessor, and emits the
   structured log line.

The dedicated test user and its TOTP enrollment are one-time bootstrap state, re-usable across
future runs of the script.

## Verified assumptions

- The 4 gRPC services are the complete set (`Program.cs` `MapGrpcService` calls) — no 5th service.
  *Read `Program.cs`.*
- 2 of the 4 (`ObjectRetrievalGrpcService`, `ObjectSearchGrpcService`) use server-streaming RPCs
  (`IServerStreamWriter<T>`); none use client-streaming or duplex. The interceptor needs both
  `UnaryServerHandler` and `ServerStreamingServerHandler` overrides. *Grepped all 4 service files.*
- No gRPC interceptor is registered today — this is additive. *Grepped `Program.cs` for
  `Interceptor`.*
- ASP.NET Core supports multiple named JwtBearer schemes, each independently configurable, with
  explicit `AuthenticateAsync(scheme)` and a custom token source via `OnMessageReceived`. *Confirmed
  via Microsoft Learn docs.*
- Grpc.AspNetCore interceptors register globally via `AddGrpc(options.Interceptors.Add<T>())` and
  apply uniformly to all mapped services; default per-request lifetime supports the scoped-accessor
  pattern. *Confirmed via Microsoft Learn gRPC interceptors docs.*
- The existing service-credential scheme's `HttpContext.User` is populated before any gRPC
  interceptor executes. *Empirically relied on by Part 2+3's already-working
  `RequireAuthorization`/`FallbackPolicy` enforcement on these same 4 services.*
- Go's `PerRPCCredentials.GetRequestMetadata` receives the caller's actual per-call `context.Context`
  (enriched with `RequestInfo`, not a detached one), so app-supplied `context.WithValue` data is
  visible inside it. *Confirmed via grpc-go docs/source (`credentials` package).*
- Java's `CallCredentials.RequestInfo.getCallOptions()` exists and exposes per-call `CallOptions`,
  including custom `CallOptions.Key<T>` values set via `stub.withOption(...)`. *Confirmed via
  grpc-java Javadoc.*
- Authentik's OAuth2 provider does not support RFC 8693 token exchange (only authorization code,
  implicit, hybrid, client credentials, device code, refresh token). *Confirmed directly against
  Authentik's own OAuth2 provider docs.*
- `authentik_core.user` blueprint model supports a plaintext `password` attr for provisioning a
  known-credential test user. *Confirmed via Authentik's blueprint models docs.*
- Authentik's flow-executor API (`/api/v3/flows/executor/<slug>/`) is a documented,
  get-challenge/solve-challenge JSON mechanism designed for non-interactive flow execution,
  including TOTP stages, with the shared secret available in the enrollment challenge response.
  *Confirmed via Authentik's own developer docs and API reference.*
- `default-authentication-flow`'s stage order is identification (10) → password (20) → MFA
  validation (30) → login (100), and this repo's `mfa-enforcement.yaml` blueprint configures that
  exact stock MFA-validation stage by name (`default-authentication-mfa-validation`). *Fetched
  Authentik's own stock `flow-default-authentication-flow.yaml` from source and cross-checked
  against this repo's blueprint.*
- All 5 SDKs' existing call-credentials mechanisms are as described in the table above. *Read each
  of the 5 source files directly (`IversonClientCredentials.cs`/`CachedClientCredentialsTokenProvider.cs`/
  `ServiceCollectionExtensions.cs`, `auth.go`, `auth.py`, `OAuth2ClientCredentials.java`, `auth.ts`).*
- Python's and TypeScript's per-call explicit metadata arguments combine additively with
  `CallCredentials`-supplied metadata rather than overwriting it. *Standard, foundational gRPC
  `CallCredentials` semantics across mature implementations — this is the abstraction's entire
  purpose (augment outgoing metadata, never replace caller-supplied metadata).*

## Self-review

- **Placeholder scan:** no TBD/TODO; every config key, metadata key name, and per-language
  mechanism is named explicitly.
- **Internal consistency:** the optional/reject-on-invalid decision (server-side validation) is
  consumed consistently by the interceptor description and the testing plan (which exercises all
  three branches).
- **Scope:** Parts 5 and admin-endpoint changes remain explicitly out of scope, matching the
  decisions made during brainstorming; no silent expansion.
- **Ambiguity check:** "always Authentik" for end-user auth is stated explicitly as a precondition,
  not left implicit — the design would need to change if that assumption is ever false for some
  future calling service.
