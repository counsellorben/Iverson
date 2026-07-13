# Critical Design Review: 2026-07-12-end-user-identity-propagation-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/superpowers/specs/2026-07-12-end-user-identity-propagation-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

| # | Item | Disposition |
|---|---|---|
| 1 | Section: Scope (in/out, motivation) | ok — cross-checked "4 gRPC services" and "3 admin/* endpoints" claims directly against `Program.cs` (`MapGrpcService` ×4, `RequireAuthorization("Operator")` ×3); consistent. No contradiction between "no proto changes" here and the Wire protocol section. |
| 2 | Section: Wire protocol — dual metadata key rule | ok — checked both operands (`authorization`, `x-acting-user-authorization`) for case-sensitivity/collision risk; gRPC metadata keys are lowercase ASCII text keys, no collision with existing key. |
| 3 | Rule: audience-allowlist separation (service vs. ActingUser lists) | ok — checked over-inclusion risk (a service client_id accidentally present in `ActingUser:ValidAudiences`); this is an operator misconfiguration scenario, not a design defect — the design correctly keeps the lists separate. Dropped — fails literal-wrongness (spec's own design is sound; only a misconfigured deployment would misbehave). |
| 4 | Rule: interceptor absent/valid/invalid branches | ok — matches the three explicit user decisions (optional, reject-on-invalid) captured earlier in the process; no drift between spec text and those decisions. |
| 5 | Rule: interceptor log line `service {ServiceClientId} acting as user {ActingUserSub} called {Method}` — claim-source operand | → §2.1 — see below. |
| 6 | Rule: streaming vs. unary interceptor override (`ObjectRetrievalGrpcService`, `ObjectSearchGrpcService` use `IServerStreamWriter`) | ok — spec already accounts for this explicitly (`UnaryServerHandler` + `ServerStreamingServerHandler`, shared logic); confirmed via Microsoft's own gRPC interceptor docs that these are genuinely distinct override points. |
| 7 | Ordering: default scheme's `HttpContext.User` populated before interceptor runs | ok — re-derived: `UseAuthorization()` short-circuits before any mapped endpoint (and therefore any interceptor within it) executes; already empirically relied on by Part 2+3's working `FallbackPolicy` enforcement on these same 4 services. |
| 8 | Section: Authentik provisioning — new public/PKCE client parallel to existing patterns | ok — read `blueprints-configmap-service-clients.yaml` directly; the proposed `iverson-loadtest-human` client follows the exact same `identifiers`/`attrs` shape already used for `iverson-oidc-default`, just `client_type: public` with PKCE instead of `confidential`/`client_credentials`. No structural gap. |
| 9 | Data-flow arrow: SDK per-call token → gRPC metadata (.NET) | ok — read `ServiceCollectionExtensions.cs`/`AttachCredentials` directly; generated grpc-dotnet client methods' `Metadata headers` parameter is a separate object from what `AddCallCredentials` populates, so both reach the wire additively. |
| 10 | Data-flow arrow: SDK per-call token → gRPC metadata (Go) | ok — confirmed via grpc-go `credentials` package docs/source: the `ctx` passed into `GetRequestMetadata` is the caller's own per-call context enriched with `RequestInfo`, not a detached one, so `context.WithValue` data set by app code is visible inside it. |
| 11 | Data-flow arrow: SDK per-call token → gRPC metadata (Java) | ok — confirmed via grpc-java Javadoc: `CallCredentials.RequestInfo.getCallOptions()` exists and exposes custom `CallOptions.Key<T>` values set via `stub.withOption(...)`. |
| 12 | Data-flow arrow: SDK per-call token → gRPC metadata (Python) | ok — grpc-python's generated stub call methods accept an explicit `metadata` parameter independent of whatever `AuthMetadataPlugin`/`CallCredentials` is bound to the channel; both apply additively (standard, well-established grpc-python stub signature). |
| 13 | Data-flow arrow: SDK per-call token → gRPC metadata (TypeScript) | ok — confirmed via `@grpc/grpc-js` docs: generated client calls accept an explicit `Metadata` argument distinct from channel-level `CallCredentials`; both are sent. |
| 14 | Section: Testing plan — automated interceptor unit tests via test signing key | ok — standard, well-established ASP.NET Core `WebApplicationFactory` testing technique (override `TokenValidationParameters.IssuerSigningKey` per scheme in `ConfigureWebHost`); not demonstrated in the current `AuthTestWebApplicationFactory` but this is routine additive test-harness work, not a design-level gap. |
| 15 | Section: Testing plan — scripted TOTP secret extraction from enrollment challenge | ok — re-verified directly against Authentik's own source (`authentik/stages/authenticator_totp/stage.py`): `AuthenticatorTOTPChallenge.config_url = CharField()` returns the `otpauth://` URI as plain JSON text. Stronger evidence than the spec's own citation; reconfirmed. |
| 16 | Section: Testing plan — flow slug/stage order (`default-authentication-flow`: identification→password→MFA→login) | ok — re-verified against Authentik's own stock blueprint (`blueprints/default/flow-default-authentication-flow.yaml`); matches spec's claim exactly, and this repo's `mfa-enforcement.yaml` targets the same stock stage name. |
| 17 | Verified assumptions: RFC 8693 not supported by Authentik | ok — reconfirmed against Authentik's own OAuth2 provider docs (unchanged since original verification). |
| 18 | Verified assumptions: `authentik_core.user` blueprint `password` field | ok — re-fetched Authentik's blueprint models doc directly this round; text confirms the plaintext `password` field is blueprint-settable (stronger citation than original, which was search-synthesis only). |
| 19 | Section: Self-review (spec's own) | ok — boilerplate cross-check; claims made there (placeholder scan, scope, ambiguity check) are consistent with the spec body as written. |

## 1. Verified-assumptions cross-check

All 13 listed items in the spec's `Verified assumptions` section still hold under a fresh read of their cited evidence (items on RFC 8693, TOTP `config_url`, and blueprint `password` field were independently re-fetched this round with stronger primary-source citations than the spec's own references — see rows 15, 17, 18 above).

**Span check** — one dependency the design needs that no listed item verifies:

- The interceptor's structured log line (`service {ServiceClientId} acting as user {ActingUserSub} called {Method}`) depends on a specific claim, on the *existing* service-credential `ClaimsPrincipal`, carrying an identifier the spec calls `ServiceClientId`. No item in the Verified Assumptions section names or verifies which claim this is, and this repo's own code has no precedent for reading it (grep for `client_id`/`azp`/`ClientId` across `Iverson.Api` returns zero hits — unlike `groups`/`scope`, which the codebase's own comments state were "confirmed via a real decoded token"). See §2.1 for what was found trying to verify it in-round.

## 2. Literal-wrongness findings

### 2.1 — Log line's `{ServiceClientId}` is not what Authentik's client_credentials tokens actually carry

**Description:** The spec commits explicitly to a structured log line — `service {ServiceClientId} acting as user {ActingUserSub} called {Method}` — appearing in two places (Server-side validation section, and restated in Testing plan step 4). This is the design's sole concrete observable deliverable for the "foundation for Part 5" motivation. Read naturally, `ServiceClientId` means the raw OAuth2 `client_id` value already used throughout the rest of the design (the same value populating `Authentication:ValidAudiences`, the blueprint's `client_id` attrs, etc.).

Authentik's own docs for this exact provisioning shape (`client_type: confidential`, `grant_types: [client_credentials]`, secret-based — exactly how `iverson-loadtest`/`iverson-webtest`/`iverson-admin-automation` are configured in `blueprints-configmap-service-clients.yaml`) state directly: *"you can pass the configured `client_secret` value of an OAuth provider. In that case, authentik automatically generates a service account for which the JWT token will be issued. The automatically generated service account follows this naming scheme: `ak-<provider_name>-client_credentials`."* This passage is explicit that it describes Authentik's own self-issued client-credentials tokens, not only externally-issued/federated JWTs.

That means the identity claim actually available on the calling service's token is a synthesized service-account name (e.g. `ak-iverson-loadtest-client_credentials`, derived from the *provider* name), not the literal `client_id` string (`dev-iverson-loadtest-client-id` in dev, or a `randAlphaNum 40` value in a live deployment) the design's naming implies. As written, the log line the spec commits to cannot be produced as literally named — the interceptor would either need to log the synthesized service-account name under a misleading field name, or perform an additional lookup that the spec never describes.

**Evidence:** `docs.goauthentik.io/add-secure-apps/providers/oauth2/client_credentials/` (fetched directly this round); cross-checked against this repo's own `blueprints-configmap-service-clients.yaml:31-46` provisioning (`client_type: confidential`, `grant_types: ["client_credentials"]` — matches the exact shape the Authentik doc describes); cross-checked that this repo has never verified any `client_id`/`azp` claim itself (`grep -rn "client_id|azp|ClientId" Iverson.Api` → no hits, unlike the `groups`/`scope` claims which `OperatorAuthorizationPolicy.cs:7-8` explicitly notes were "confirmed via a real decoded token").

**Proposed fix:** before implementation, mint a real client-credentials token from one of the already-provisioned service clients and decode it (the same technique Part 2+3 already used for `groups`/`scope`) to see exactly which claim is populated and with what value. Then pick one of two paths and update the spec's log-line description accordingly:
- Log the synthesized service-account subject as-is (rename the field from `ServiceClientId` to something like `ServiceAccount`, and accept that it won't literally match the configured `client_id` string used elsewhere in this design's config).
- If the raw `client_id` is required for consistency with `ValidAudiences`/blueprint config, add a property mapping (Authentik blueprints support `scopemapping` entries, as already used for the `admin` and `groups` scopes in this same file) to embed `client_id` as an explicit custom claim on these providers' tokens.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

n/a (first round)

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 has one finding, §3 is empty. Resolve 2.1 (verify the actual claim shape and update the log-line field name/source accordingly) before moving to implementation planning; nothing else in the spec blocks progress.
