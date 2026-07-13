# Critical Implementation Review: 2026-07-12-end-user-identity-propagation-implementation-plan (Round 2)

**Plan:** `/home/ben/repositories/Iverson/docs/superpowers/plans/2026-07-12-end-user-identity-propagation-implementation-plan.md`
**Verified plan-level assumptions section:** present

⚠️ 3 commits since the spec's cited SHA (`8c79d45...`): `f1b5b82` (the plan itself), `2d77037` (round-1 review), `72dae5a` (round-1 fixes). All three are documentation-only commits to the plan/review files themselves — none touch application code the plan's cited `file:line` references point at (`Program.cs`, `ObjectSearchGrpcService.cs`, `OperatorAuthorizationPolicy.cs`, etc.), so no re-verification of application-code citations was needed beyond the normal §1 fresh-read pass.

## 0. Coverage enumeration

Rebuilt fresh this round (not scoped to the round-1 diff), covering all 11 tasks. Rows already dispositioned `ok` in round 1 are re-verified rather than re-derived from scratch where the underlying code is unchanged; rows touched by round-1 fixes get full fresh re-derivation.

| # | Item | Disposition |
|---|---|---|
| 1 | Round-1 fix: `MapInboundClaims = false` on both `AddJwtBearer` calls (Task 1, lines 155, 162) | ok — present on both schemes, syntactically correct |
| 2 | Round-1 fix, consumer-impact re-check: does disabling `MapInboundClaims` on the **default** scheme (already used by Part 2+3's production-deployed `/admin/*` authorization) break anything relying on the old remapped claim types (`ClaimTypes.*`)? | ok — fresh `grep -rn "ClaimTypes\." Iverson.Server --include=*.cs` → zero hits anywhere in the codebase; combined with `OperatorAuthorizationPolicy`'s already-confirmed literal `"groups"`/`"scope"` reads (never remapped either way), disabling the flag has no consumer to break |
| 3 | Round-1 fix: does the fix reach `TestJwtFactory`-signed tokens in Task 3's tests, or does Task 3's `PostConfigure` reset it? | ok — traced: `PostConfigure` mutates the same `options` object `Configure` (Task 1's `AddJwtBearer` delegate) already set; Task 3's `PostConfigure` touches `Authority`/`ValidateIssuer`/`ValidAudiences`/`IssuerSigningKey` only, never `MapInboundClaims`, so Task 1's `false` setting persists into the test host unchanged |
| 4 | Round-1 fix: Task 11 Step 1's added `--target compose\|kind` interface — internal consistency of its own claims | ok — re-derived the "compose needs no Host-header override" claim independently: the `iss` claim is set by whichever request ultimately hits `POST /application/o/token/` (the token-issuing step), which is the *same* endpoint for both the client_credentials grant (Part 2+3's already-proven precedent) and this plan's authorization_code grant's final exchange step — the preceding flow-executor/PKCE steps don't change which request sets `iss`. Claim holds. |
| 5 | Round-1 fix: `RequireSchema`/`FailedPrecondition` correction | ok — fresh `grep -n "NotFound"` across the whole plan file → zero hits; correction applied consistently in the assumptions table (#17) and both code comments (Task 3, Task 11) |
| 6 | Task 8 step numbering: "Step 2: Read it in `applyRequestMetadata`" (line 731) immediately followed by a second "Step 2: Build" (line 754), then "Step 3: Commit" (line 760) | dropped — genuine mechanical duplicate-label defect (confirmed via `grep -n "\*\*Step [0-9]"` across the whole file — every other task numbers sequentially; only Task 8 repeats "Step 2"). Doesn't break execution: this repo's SDD/executing-plans convention tracks progress via the `- [ ]` checkboxes in document order, not by parsing/matching the printed step number as a controlling index — the four steps (add fields → wire them in → build → commit) are still in the correct sequence. Cosmetic, not a literal-wrongness break. |
| 7 | Interceptor dynamic-mode: does `httpContext.AuthenticateAsync("ActingUser")` ever *throw* (JWKS-fetch failure, malformed token) rather than returning a failed `AuthenticateResult`, which would bypass the interceptor's `if (!result.Succeeded...)` check and surface as `Internal` instead of the required `Unauthenticated`? | dropped — standard, well-established `JwtBearerHandler` behavior catches internal validation/discovery failures and returns `AuthenticateResult.Fail(...)`, not an unhandled exception; this is the entire purpose of the `AuthenticateResult` pattern. No concrete failure path found. |
| 8 | Task 11 Step 3's verification method: `docker compose logs iverson-api \| grep "acting as user"` — does this app's OpenTelemetry logging configuration suppress Console/stdout output, making the grep a false negative even on success? | ok — `WebApplication.CreateBuilder(args)` configures Console logging by default; `builder.Logging.AddOpenTelemetry(...)` is additive (no `ClearProviders()` call anywhere in `Program.cs`), and `appsettings.json`'s `Logging:LogLevel:Default` is `"Information"`, matching `LogInformation`'s level. Console output is not suppressed. |
| 9 | Task 5 Step 1/3: does the new `iverson-loadtest-human` Secret exist before the Deployment's `secretKeyRef` needs it, on first install? | dropped — no explicit Helm hook ordering exists for this new Secret+Deployment pair, but none exists for the 4 *already-deployed* Secret+Deployment pairs either (same file, same pattern); Kubernetes' pod-scheduling reconciliation self-heals a transient ordering race rather than failing permanently, and this exact unordered pattern is already proven working in production per Part 2+3. Not a new risk. |
| 10 | Task 4/5's `redirect_uris: ["http://localhost/placeholder-callback"]` / `https://{{ ingressHost }}/placeholder-callback` — does a PKCE script need this URL to actually resolve? | ok — matches `iverson-oidc-default`'s already-proven-working identical pattern (Part 1/2+3); a script capturing the `code` from the redirect's `Location` header doesn't need the target to be live, same as a human capturing it from the browser address bar |
| 11 | Task 1 Steps 1-5 (accessor, config, scheme registration, build, commit) — re-swept fresh | ok — unchanged from round 1's clean disposition, no new issue found |
| 12 | Task 2 Steps 1-4 (interceptor, registration, build, commit) — re-swept fresh, including streaming-vs-unary call-order and DI-scope sharing | ok — unchanged from round 1 |
| 13 | Task 3 Steps 1-6 (package ref, JWT helper, scheme overrides, tests, run, commit) — re-swept fresh | ok — unchanged from round 1 aside from the already-covered `FailedPrecondition` comment fix |
| 14 | Task 6 (.NET SDK helper) — re-swept fresh | ok — unchanged |
| 15 | Task 7 (Go SDK) — re-swept fresh, including nil-context and type-assertion safety | ok — `ctx.Value(...).(string)` uses the comma-ok form, no panic risk; Go contexts are never nil in idiomatic call sites |
| 16 | Task 9 / Task 10 (Python/TypeScript helpers) — re-swept fresh | ok — unchanged, pure functions |
| 17 | Cross-task contracts (Task 1→2, Task 2→3, Task 4/5→11) — re-swept fresh | ok — all still consistent; Task 11's contract with Task 4/5 is now fully specified per the round-1 fix |
| 18 | Global Constraints vs. task bodies | ok — still internally consistent |

## 1. Verified-plan-assumptions cross-check

All 19 assumptions (#1-19, including #19 added in round 1) still hold under a fresh read of their cited evidence. #19 (`MapInboundClaims` default) received additional fresh verification this round beyond a citation re-read — see §0 rows 1-3 above, which confirm both that the fix is correctly applied and that it introduces no consumer-impact regression on the already-deployed default scheme.

**Span check:** no uncovered dependency found.

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- §2.1 (round 1): `MapInboundClaims` defaulting to `true` and silently breaking the log line's `"sub"` lookups — fixed by adding `options.MapInboundClaims = false` to both schemes; re-verified this round with an additional consumer-impact check (no other code in the repo relies on the old remapped claim types).
- §2.2 (round 1): Task 11 Step 1's missing target/environment-selection interface — fixed with an explicit `--target compose|kind` spec covering base URL and `client_id` sourcing per environment; re-verified this round that its "no Host-header override needed for compose" claim holds under independent re-derivation.
- §1 (round 1): `RequireSchema` assumption stated the wrong status code (`NotFound` vs. actual `FailedPrecondition`) — corrected in the assumptions table and both code comments; re-verified this round with a fresh whole-file grep confirming no stale reference remains.

## 5. Recommendation

✅ **Approve as-is** — §1 has no failed assumptions, §2 and §3 are both empty. Plan is ready for `subagent-driven-development`.
