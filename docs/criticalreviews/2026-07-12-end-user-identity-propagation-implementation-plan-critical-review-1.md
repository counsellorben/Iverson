# Critical Implementation Review: 2026-07-12-end-user-identity-propagation-implementation-plan (Round 1)

**Plan:** `/home/ben/repositories/Iverson/docs/superpowers/plans/2026-07-12-end-user-identity-propagation-implementation-plan.md`
**Verified plan-level assumptions section:** present

## 0. Coverage enumeration

| # | Item | Disposition |
|---|---|---|
| 1 | Task 1 Step 1 (`IActingUserAccessor.cs`) | ok — trivial interface+impl, no logic to break |
| 2 | Task 1 Step 2 (appsettings.json config block) | ok — additive JSON, no key collision with existing `Authentication` keys |
| 3 | Task 1 Step 3 code: `AddJwtBearer("ActingUser", ...)` + `OnMessageReceived` | ok (mechanism) / → §2.1 (claim-mapping consequence — see below) |
| 4 | Task 1 Step 3 code: `OnMessageReceived`'s fallback-to-default-header behavior when the acting-user header is present but malformed (no "Bearer " prefix, or empty after "Bearer ") | dropped — traced through `JwtBearerHandler.HandleAuthenticateAsync`'s confirmed fallback-to-standard-`Authorization`-header behavior when `context.Token` stays empty; the fallback pulls in the *service's own* token and attempts to validate it against the `ActingUser` scheme, but since `Authentication:ActingUser:ValidAudiences` is a disjoint list from `Authentication:ValidAudiences` (Global Constraint, and confirmed disjoint in Tasks 4/5's actual values), audience validation fails and the interceptor still throws `Unauthenticated` — the required "reject" outcome still holds, just via an internally different path. No wrong-*acceptance* path found. |
| 5 | Task 2 Step 1 code: `ActingUserInterceptor` — `UnaryServerHandler`/`ServerStreamingServerHandler` overrides, signatures | ok — matches `Grpc.Core.Interceptors.Interceptor`'s actual signatures (already verified at plan-write time); both call `ValidateActingUserAsync` before `continuation`, so a thrown `RpcException` never lets a streaming response begin |
| 6 | Task 2 Step 1 code: `ValidateActingUserAsync`'s `sub`-claim extraction (`serviceSubject`, `actingUserSubject`) | → §2.1 |
| 7 | Task 2 Step 1 code: `IActingUserAccessor` DI resolution via `httpContext.RequestServices` | ok — `HttpContext.RequestServices` is the same per-request scoped container the interceptor and any future constructor-injected consumer would both resolve from |
| 8 | Task 2 Step 2 (`AddGrpc` registration) | ok — global registration, matches the confirmed `options.Interceptors.Add<T>()` API |
| 9 | Task 3 Step 1 (`Grpc.Net.Client` package add) | ok — version matches already-referenced `Grpc.AspNetCore 2.80.0` |
| 10 | Task 3 Step 2 (`TestJwtFactory`) — signing key length | ok — `"test-signing-key-at-least-32-bytes-long-for-hs256"` is 49 bytes, comfortably above HS256's 256-bit/32-byte minimum (verified by direct byte-length check; a too-short key would throw at signing time) |
| 11 | Task 3 Step 3 (`PostConfigure` overriding both schemes) | ok — `PostConfigure` is guaranteed to run after all `Configure`/`AddJwtBearer` calls regardless of registration order (standard `Microsoft.Extensions.Options` contract); neither override touches `Events`, so Task 1's custom `OnMessageReceived` on the `ActingUser` scheme survives into the test host unchanged |
| 12 | Task 3 Step 4 code: `TryAggregateAsync`/three `[Fact]`s — status-code assertions | ok — assertions check `NotBe(Unauthenticated)` / `Be(Unauthenticated)` only, not the specific non-auth status code, so they pass regardless of §1.1's finding below |
| 13 | Task 3 Step 4 code comment: `// NotFound from RequireSchema` | → §1 (assumption #17 failed) |
| 14 | Task 3 — consumer impact: does any other test file use `AuthTestWebApplicationFactory` besides `AuthenticationPipelineTests`? | ok — fresh `grep` confirms only `AuthenticationPipelineTests.cs` consumes it today; `ActingUserInterceptorTests.cs` becomes a second, non-conflicting consumer |
| 15 | Task 4 Step 1 (compose-only blueprint additions) | ok — new `model:` entries only, indentation matches the file's existing 6/8/10-space nesting exactly |
| 16 | Task 4 Step 2 (docker-compose.yml env vars) | ok — `environment:` is a YAML sequence; append order is irrelevant, no duplicate-key risk |
| 17 | Task 5 Step 1-2 (helm blueprint + secret additions) | ok — new `Secret`/`model:` entries only, matches existing `lookup`/`randAlphaNum`/lookup-preserve pattern exactly; `---` document separators correctly placed |
| 18 | Task 5 Step 3 (deployment.yaml env vars) | ok — mirrors the existing `Authentication__ValidAudiences__*` block's exact shape |
| 19 | Task 5 Step 4 (`helm dependency build` reminder) | ok — correctly scoped to the templates this task actually touches |
| 20 | Task 6 (.NET SDK helper) | ok — straightforward extension method, no issues |
| 21 | Task 7 (Go SDK) code: `GetRequestMetadata` context-value read | ok — type-safe `.(string)` assertion with `ok` check, no panic risk on missing/wrong-type context value |
| 22 | Task 8 (Java SDK) code: `CallOptions.Key` + `applyRequestMetadata` | ok — matches confirmed grpc-java `RequestInfo.getCallOptions()` API |
| 23 | Task 9 (Python SDK) helper | ok — pure function, no state |
| 24 | Task 10 (TypeScript SDK) helper | ok — pure function, no state |
| 25 | Task 11 Step 1 (flow-executor harness spec) | → §2.2 |
| 26 | Task 11 Step 2 code: `services.GetRequiredService<ObjectSearchService.ObjectSearchServiceClient>()`, `Aggregate` usage, error-swallowing `catch` clause | ok (resolution pattern, matches Task 3/11's shared `Aggregate` rationale) / dropped (error-swallowing) — the `catch (RpcException ex) when (ex.StatusCode != StatusCode.Unauthenticated)` clause lets a genuine `Unauthenticated` (i.e. a real bug) propagate as an unhandled exception rather than a clean diagnostic message; this is a UX rough edge, not a correctness break — the tool would still fail loudly and visibly. Doesn't meet literal-wrongness. |
| 27 | Task 11 Step 3-4 (live-run commands) — cross-referenced against Step 1's script spec | → §2.2 (same root cause as row 25) |
| 28 | Cross-task contract: Task 1 → Task 2 (`IActingUserAccessor`, `"ActingUser"` scheme name) | ok — Task 2's code references both correctly and consistently with Task 1's registrations |
| 29 | Cross-task contract: Task 2 → Task 3 (`ActingUserInterceptor.MetadataKey`) | ok — Task 3 executes after Task 2 in sequence, so the constant exists by the time Task 3's code references it |
| 30 | Cross-task contract: Task 2's "Interfaces: Produces" line claiming the 5 SDK tasks "consume" `ActingUserInterceptor.MetadataKey` | dropped — imprecise bookkeeping (the 5 SDKs are separate language projects with no compile/runtime reference to this C# constant; each defines its own literal-string constant, per assumption #14's sibling-set sweep), but doesn't cause any execution-time failure — nothing in Tasks 6-10's actual code references the C# symbol |
| 31 | Cross-task contract: Task 4/5 (`iverson-loadtest-human` client_id, test-user credentials) → Task 11 (script needs these values) | → §2.2 (same root cause as row 25) |
| 32 | Global Constraints vs. Task bodies — internal consistency | ok — the log-line format, optional-token behavior, and reject-on-invalid behavior are consumed consistently across Tasks 2, 3, and 11 |
| 33 | "Tasks NOT in this plan" vs. spec's "Explicitly out of scope" | ok — verbatim match |

## 1. Verified-plan-assumptions cross-check

Assumptions #1-16, #18 still hold under a fresh read of their cited evidence.

**Assumption #17 — failed.** The assumption states: *"`Aggregate` calls `RequireSchema(request.TypeName)` before anything else, throwing `RpcException(NotFound, ...)`."* A fresh read of `ObjectSearchGrpcService.cs:354-356` shows `RequireSchema` actually throws `RpcException(StatusCode.FailedPrecondition, ...)`, not `NotFound`:
```csharp
private SchemaDescriptor RequireSchema(string typeName) =>
    registry.Get(typeName) ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
        $"No schema registered for '{typeName}'. Call RegisterSchema first."));
```
This does **not** break Task 3's or Task 11's actual logic — both only assert/branch on whether the status is `Unauthenticated`, never on the specific non-auth code — but the plan's own code comments in Task 3 Step 4 (`// NotFound from RequireSchema`) and Task 11 Step 2 (`RequireSchema/NotFound`) repeat the incorrect claim and should read `FailedPrecondition` for accuracy.

**Span check:** one uncovered dependency, verified in-round with concrete evidence — see §2.1. No other uncovered dependency found.

## 2. Literal-wrongness findings

### 2.1 — The log line always prints "unknown"/"unknown": `JwtBearerOptions.MapInboundClaims` defaults to `true`, remapping `"sub"` away from its literal name

**Description:** `ActingUserInterceptor.ValidateActingUserAsync` (Task 2 Step 1) reads the acting subject and service subject via `httpContext.User.FindFirst("sub")?.Value` and `result.Principal.FindFirst("sub")?.Value`. Neither of Task 1's two `AddJwtBearer` registrations sets `MapInboundClaims = false`. Microsoft's own docs (current, spanning through ASP.NET Core 10.0, the version this repo targets) state: *"[MapInboundClaims] is used when determining whether or not to map claim types that are extracted when validating a JwtSecurityToken... The default value is true."* — and separately confirm that under the default mapping, **"the `sub` claim is mapped to `ClaimTypes.NameIdentifier`"** specifically. With the default in effect, the `ClaimsPrincipal` produced by *both* JwtBearer schemes never carries a claim literally typed `"sub"` — it carries `ClaimTypes.NameIdentifier` (`http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier`) instead. `FindFirst("sub")` therefore returns `null` on every call, and both `serviceSubject` and `actingUserSubject` fall through to the `?? "unknown"` default — **every single time**, on every correctly-authenticated call, not just edge cases.

This directly defeats the spec's own stated purpose for the log line: *"a minimal observable signal (the log line) that proves the plumbing works end-to-end today, before Part 5 exists to consume it."* A log line that unconditionally reads `service unknown acting as user unknown called <Method>` proves nothing and would not be caught by Task 3's tests, since those tests assert only on `RpcException.StatusCode`, never on the log content or the `IActingUserAccessor.ActingUser` claims — so this bug ships with zero automated coverage and would only surface if someone reads Task 11's live log output closely enough to notice "unknown" isn't a real identity.

Why Part 2+3's existing code never hit this: `OperatorAuthorizationPolicy.IsSatisfiedBy` reads `"groups"` and `"scope"` claims — neither name appears in the default WS-Federation-era claim-mapping table `MapInboundClaims` uses, so they pass through unmapped regardless of the setting. `"sub"` is one of the specifically-remapped names, and this plan is the first code in the repo to read it directly.

**Evidence:**
- `docs/superpowers/plans/2026-07-12-end-user-identity-propagation-implementation-plan.md:150,251` (both `FindFirst("sub")` call sites) and `:143-171` (neither `AddJwtBearer` call sets `MapInboundClaims`).
- Microsoft Learn: `JwtBearerOptions.MapInboundClaims` property page (default `true`, spanning aspnetcore-5.0 through 11.0, current for 10.0).
- Confirmed via search: "By default, the 'sub' claim is mapped to `ClaimTypes.NameIdentifier`."
- `Iverson.Server/Iverson.Api/OperatorAuthorizationPolicy.cs:7-9` — confirms the only pre-existing direct-claim-read code in this repo reads `"groups"`/`"scope"`, not `"sub"`, so this is genuinely new exposure, not a previously-tested path.

**Proposed fix:** add `options.MapInboundClaims = false;` to both `AddJwtBearer` calls in Task 1 Step 3 (the default scheme's and the `"ActingUser"` scheme's). This makes claim types match their raw JWT names exactly, so `FindFirst("sub")` in Task 2's interceptor resolves correctly, and is also the more predictable contract for any future code (Part 5) reading claims off either scheme. `IActingUserAccessor.ActingUser` (the `ClaimsPrincipal` itself) isn't corrupted by the current bug — only the interceptor's own direct `"sub"` lookups are — so no other part of the plan needs to change. Task 3's tests would benefit from one added assertion (e.g. capturing the logger or reading `IActingUserAccessor.ActingUser.FindFirst("sub")`) to give this specific behavior real coverage going forward, though that's a test-thoroughness improvement rather than a requirement to unblock the fix itself.

### 2.2 — Task 11 Step 1 never specifies the harness script's environment/target interface, but Steps 3-4 and Task 4/5's provisioned values depend on it

**Description:** Task 11 Step 3 invokes the not-yet-written script as `python3 deploy/scripts/mint_acting_user_token.py --target compose`, and Step 4 describes a materially different invocation for kind (port-forwarding, a Host-header override, a different client_id source). But Step 1 — the step that actually specifies what the script must do — never mentions a `--target` flag, never states how the script selects between the compose-only fixed `client_id` (`dev-iverson-loadtest-human-client-id`, Task 4) and the kind target's `randAlphaNum`-generated one (retrievable only via `kubectl get secret ... -o jsonpath=...`, Task 5), and never mentions the Host-header handling Step 4 alludes to. An implementer following Step 1's description alone would have no basis for building the `--target` flag or the environment-specific base-URL/client-id/Host-header logic Steps 3-4 assume exists — Step 3's command, run against a script built strictly to Step 1's spec, would fail with an unrecognized argument or, if a flag happens to be added ad hoc, with no defined behavior for what `compose` vs. the kind case should actually do.

This is a different gap from Step 1's existing (and appropriately honest) hedge about live JSON challenge/response shapes — that hedge concerns response *parsing*, which genuinely can't be nailed down without a live instance. The target/environment-selection interface, client_id sourcing, and Host-header handling are all things the plan itself already establishes elsewhere (Task 4's fixed dev client_id, Task 5's secret-based client_id, the kind Host-header workaround referenced from `docs/runbooks/kind-cluster-troubleshooting.md`) and could have been stated directly in Step 1 without needing any live instance.

**Evidence:** `docs/superpowers/plans/2026-07-12-end-user-identity-propagation-implementation-plan.md:838-851` (Step 1, no target/client-id/Host-header spec) vs. `:885-901` (Steps 3-4, which assume all three exist).

**Proposed fix:** add to Task 11 Step 1 an explicit requirement that the script accept a target selector (e.g. `--target compose|kind`) and, per target: (a) the base URL to reach Authentik's flow-executor and token endpoints (`http://localhost:9000` for compose per the existing Part 2+3 precedent that already mints service tokens this way without a Host-header override; the kind path's port-forward + Host-header override, referencing the same workaround `docs/runbooks/kind-cluster-troubleshooting.md` already documents), and (b) how the script obtains the target `client_id` for the PKCE flow (literal `dev-iverson-loadtest-human-client-id` for compose; a `kubectl get secret <release>-authentik-loadtest-human-client -o jsonpath='{.data.client-id}' | base64 -d` step for kind).

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

n/a (first round)

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §1 has one failed (but non-blocking) assumption, §2 has two findings, §3 is empty. §2.1 is the one that must be fixed before this plan is executed — as written, the plan's core observable deliverable (the log line) would silently never work. §2.2 should be resolved before Task 11 is executed (not before Tasks 1-10, which don't depend on it). §1's assumption-#17 correction is a comment-accuracy fix, not a blocker.
