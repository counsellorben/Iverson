# Admin console landing page — merging `main`, resolved in main's favor

**Date:** 2026-09-22
**Branch:** `admin-console-landing-page` (tip `f01f03a7`)
**Base:** merge-base with `main` is `9eb99f76`; `main` is at `65cdf63a`, 576 commits ahead
**Status:** design approved, not yet implemented

## Context

The branch built the admin console landing page (nine widgets, `/admin/console/*` JSON
endpoints, a dedicated `admin-api.<host>` Ingress, an AdminUI CI job) and completed its
final whole-branch review. It forked from `main` a month ago and developed in isolation.

In that month `main` advanced 576 commits, a large part of it security-review-driven
hardening that independently touched the **same** admin-console surface: Authentik
blueprints, the AdminUI's CSP and auth config, and `Program.cs`. A trial merge conflicts
in 18 files.

**The central structural fact:** main's hardening assumed a *single-origin* console
(console and API sharing `iverson.local`). The branch's architecture is *split-origin*
(`admin-api.<host>` + CORS + CSP origins). Most conflicts trace to that one gap.

## Resolution policy (decided by the user)

**Main's code ships exactly as-is; fixes to main-owned concerns go elsewhere.**

- Where main and the branch made competing decisions about the same question, **main's
  decision wins**, untouched.
- The branch's unique feature work is preserved and re-applied **additively** on top of
  main's foundations. (A literal "never add to a main-owned file" reading is impossible —
  the console cannot exist without `MapAdminConsoleEndpoints()` in `Program.cs`.)
- Where main's version carries a defect the branch already fixed, **main's version ships
  with the defect** and the fix becomes a separate follow-up PR against `main`.

**The checkable property is: no main decision is overridden.** A literal "zero main-owned
lines changed" cannot hold, because `GetSchema`'s inline body *is* main's code and the
branch refactored it away. Every place main's lines necessarily change is listed
explicitly in R1.

## Phase 1 — the merge commit

`git merge main -X theirs`, which resolves 17 of 18 conflicts in main's favor. Deliberately
mechanical and reproducible, so the result is auditable rather than trusted.

Main's mechanisms win throughout: `RequireListenerPort` (over the branch's
`HttpListenerOnly`), its inline `/health` cache, its CSP and security headers, the global
rate limiter, the Secret-based Authentik blueprint, its `@vitejs/plugin-react` fix, and the
no-`offline_access` auth config. The branch's additive work survives where it did not
conflict, including both console endpoint registrations (verified present post-merge).

### The blueprint conflict (manual)

`blueprints-configmap-service-clients.yaml` is a modify/delete: main deleted it, renaming to
`secret-service-clients.yaml` and converting ConfigMap→Secret so `kubectl`'s stock `view`
role cannot read the OAuth client secrets and API tokens it carries (`74bcbf9e`).

**Resolution: accept main's deletion** (`git rm`). The branch's only content there was the
`include_claims_in_id_token: true` pin, which is dropped.

The same pin also exists in the compose blueprint, which auto-merges cleanly and would
therefore *keep* it — leaving the two provider definitions divergent as an accident of where
git detected a conflict. **Resolution: drop the compose pin too**, so both paths match main
exactly and the merge leaves no arbitrary divergence. Re-pinning both is a single follow-up.

### `-X theirs` does not produce a building tree

It *splices* three files into incoherence rather than picking a side. These must be resolved
**inside Phase 1**, or the merge commits a tree that does not compile — bad for bisect and
CI, for no gain. See R1.

## Phase 2 — the repair series

Each item is its own commit, so every deviation from main is visible and justified.

### R1 — Resolve the three splices *(in Phase 1; nothing builds otherwise)*

Measured: 12 compile errors across 2 files, plus 5 failing frontend tests in a third.

**`Program.cs` `/health`** — 10 × `CS0841`. `-X theirs` kept main's cache prologue
(`cache.TryGetValue("health-composite", …)`) and epilogue (`cache.Set(…, result, …)`)
against the branch's body, collapsing two different `result` values into one name and
leaving main's four raw task variables unused.

*Resolution:* restore main's handler verbatim. This drops the branch's single-flight
`HealthCheckCache` and its `authPending` wire format (see R2, R7).

**`ObjectMappingGrpcService.GetSchema`** — `CS0103`, `CS1503`. The merge left main's entire
two-pass catalog body *dead* above the branch's one-line call to the extracted reader, calling
a `ProjectField` helper the branch moved into `SchemaCatalogReader` (`:171`).

*Resolution:* delete main's dead body; keep `SchemaCatalogReader.BuildCatalog(...)`.

**Carry main's tenant filter into the branch's reader — the highest-stakes item in the merge.**
Main's cross-tenant fix (`6509b6be`) exists *only* inside the body being deleted:

```csharp
var callerTenant = _actingUserAccessor.ActingUser?.FindFirst("tenant_id")?.Value;
if (schema.OwnerTenantId is not null && schema.OwnerTenantId != callerTenant) continue;
```

`BuildCatalog` already receives `ClaimsPrincipal? actingUser`, so `callerTenant` is derived
inside it — **no signature change**. `SchemaDescriptor.OwnerTenantId` arrives from main
(`SchemaDescriptor.cs:93`).

Both *catalog* consumers route through this one method — the gRPC `GetSchema` and
`AdminConsoleEndpoints.cs:118` (`reader.ReadCatalog(http.User)`) — so one fix covers both.
**Without it, main's just-closed cross-tenant schema-metadata leak reopens through the
branch's new `/admin/console/schema` endpoint**, exposed to any authenticated caller.

**The filter must reach every branch surface that enumerates the registry for a caller, not
just the catalog.** There is a second one: `/admin/console/data-volume` never touches
`BuildCatalog`. It walks the registry itself — `AdminConsoleEndpoints.cs:179`,
`registry.All.Values.Select(s => s.TypeName)` — and routes each name through
`AggregateReader.CountRowsAsync`, which applies only the row/field evaluator; there is no
ownership term anywhere in `Iverson.Api/Search/`. Left alone, `/admin/console/schema` would
withhold another tenant's schema while `/admin/console/data-volume` lists it by name, which is
exactly the enumeration `6509b6be` closed.

Fix it at the same grain: in `AggregateReader.CountRowsAsync` derive `callerTenant` from the
`ClaimsPrincipal? actingUser` it already takes (`AggregateReader.cs:50` — no signature change)
and return `TypeRowCount.Denied` for a foreign-owned schema *before* the evaluator call.
`Denied` is already the status the endpoint folds into `deniedTypeCount` without emitting the
name, so this needs no new wire shape.

*Semantic note:* a caller with no `tenant_id` claim (an operator) yields `callerTenant = null`,
so every tenant-owned schema is excluded. Fail-closed, and consistent with main's semantics.

**`AuthProvider.test.tsx`** — branch test bodies survived without their helper definitions
(`setTokenRenewer`, `requestTokenRenewal`, `renderProviderAndCaptureSettings`,
`onSigninCallback`). Resolve per R4's disposition list.

### R2 — Delete branch code main's equivalents supersede

- `HealthCheckCache.cs` + `HealthCheckCacheTests.cs`, and its `AddSingleton<HealthCheckCache>()`
  registration — orphaned once main's inline cache owns `/health`.
- `HealthCheckWireFormat.cs` + `HealthCheckWireFormatTests.cs` — branch-only, no other callers.
- `OperationalListenerBindingPipelineTests.cs` — references the removed `HttpListenerOnly`;
  main covers `RequireListenerPort` in `AuthenticationPipelineTests.cs`.
- `ProbeAuthorizationPipelineTests.cs` — main deleted the `/probe/*` endpoints outright.

**Keep** `TenantStatusCache`'s `tenant-status:{tenantId}` prefix. It lives in a file main never
touched, so it survives the merge — and it *incidentally closes main's own
`"health-composite"` collision*, because no bare tenant id can shadow that key any more.
Rewrite its comment, which currently points at the deleted `HealthCheckCache.CacheKey`.
**Main's health-cache collision therefore needs no follow-up.**

### R3 — Restore the split-origin chain *(one unit; partial re-application is worthless)*

The console cannot load data without all of it:

- The admin-api origin in the CSP `connect-src` (`nginx.conf`, `docker-entrypoint.sh`).
  Main's CSP is `default-src 'self'; … connect-src 'self' ${OIDC_ORIGIN}` — no admin-api origin.
- `adminApiIngress`, `adminConsoleOrigin` and `apiBaseUrl` across the five profile values files.
- `networkPolicy.clusterCidrs` in `values-aws.yaml` (`["10.0.0.0/16"]`), `values-azure.yaml`
  (`["10.1.0.0/16"]`) and `values-gcp.yaml`
  (`["10.2.0.0/20", "130.211.0.0/22", "35.191.0.0/16"]`). `templates/networkpolicies.yaml`
  carries a branch-authored hard `fail` when this key is unset and survives the merge, but main
  never declared the key — so without this, `helm template` errors on those three profiles.
  `values-local` and `values-laptop` keep theirs, which is what hides it in local checks.
- `global.prometheusEnabled` in `values.yaml`, `values-aws.yaml`, `values-azure.yaml` and
  `values-gcp.yaml` (`true` each). `Chart.yaml`'s subchart condition and
  `charts/api/templates/deployment.yaml`'s guard over `Prometheus__BaseUrl` both survive the
  merge reading this flag, but main's values never declared it. Helm ignores a `condition` whose
  path is absent, so Prometheus still **installs** while the env var is **omitted**, and
  `AdminConsoleMetricsEndpoint` then returns 503 `notDeployed` without attempting a call — the
  metrics widget is permanently dead on four of five profiles. The key must be *set* either way:
  absent is the one state that installs the subchart and withholds its URL.
- `docker-compose.yml`: local CORS origin and `Prometheus__BaseUrl`.

Re-applied **on top of** main's restructured values — main eliminated `api.ingress.host` drift,
added a `tlsSecretName` guard and templated the external scheme — so this is an additive fit,
not a revert of main's files.

### R4 — Resolve `AuthProvider.test.tsx`; re-add `onSigninCallback`

Main's auth *decisions* stand untouched: no `offline_access`, `automaticSilentRenew: false`.

`onSigninCallback` — which scrubs the OIDC `?code=` out of history so it cannot be restored
with the back button or shipped to traces — is **not** a contradiction. Main's AuthProvider
has no such callback at all (verified: 0 occurrences). It is a branch security fix main never
considered. **Re-add it.**

Restoring the four missing helpers is not enough: two surviving branch tests then *run and
fail* on main's values, because they assert the decisions main reversed. `main:AuthProvider.tsx`
has `scope: "openid profile email groups tenant_id"` (`:32`), `automaticSilentRenew: false`
(`:33`) and no `revokeTokensOnSignout`. So the disposition is explicit:

**Delete** — each asserts a branch decision main reversed, or a module removed here, the same
justification R2 uses for its server-side deletions:

- `AuthProvider.test.tsx:105` ("bridges the console's fetch layer to silent renewal") — its
  registrant is gone.
- `AuthProvider.test.tsx:132` ("asks the IdP to revoke the tokens at signout") — main sets no
  `revokeTokensOnSignout`.
- `AuthProvider.test.tsx:139` ("keeps `offline_access` in the requested scope") — the exact
  negation of main's own test at `:25` in the same file.
- All of `src/api/useTokenRenewal.test.ts`, and `src/api/useTokenRenewal.ts` itself.
- The two surviving branch lines in `AuthProvider.tsx` — the `:4` import and the `:62`
  `useTokenRenewal()` call inside `AuthGate`. `-X theirs` keeps these on top of main's
  `oidcConfig`; leaving them is a compile break, not dead code.
- The renewal seam in `src/api/client.ts` — `setTokenRenewer`, `requestTokenRenewal`,
  `renewalInFlight` — and `client.test.ts`'s renewal cases. Once `useTokenRenewal` is gone the
  seam has no production registrant, so `requestTokenRenewal()` at `client.ts:214` is a no-op.
  Deleting it removes the false `client.ts:152` comment rather than rewriting it. A non-expiry
  401 (a revoked session) then falls through to the widget's `unauthorized` state.

**Restore only** `renderProviderAndCaptureSettings` and `onSigninCallback`. `setTokenRenewer`
and `requestTokenRenewal` are needed by no surviving test.

### R5 — Session expiry

**The verified defect.** With no refresh token and the iframe fallback blocked by main's CSP
(`default-src 'self'`, no `frame-src`), renewal is impossible. A failed renewal dispatches
`ERROR`, whose reducer case preserves `user` and never touches `isAuthenticated`; only
`USER_SIGNED_OUT`/`USER_UNLOADED` clear it, and nothing registers an expiry handler. So
`AuthGate` — which redirects only on `!isAuthenticated` — never fires.

Result: the console sits authenticated-but-dead. Nine widgets poll, every poll 401s, every
card renders "nothing new" — frozen, silently, indefinitely. The branch's own comment at
`client.ts:152` claims this self-heals; **that claim is false.** R4 deletes the renewal seam
and that comment with it.

**Fix:** a small branch-owned hook registering
`useAuth().events.addAccessTokenExpired(() => void removeUser())`. Verified chain:
`removeUser()` → `_events.unload()` → `userUnloaded` → `USER_UNLOADED` →
`isAuthenticated: false` → the **existing** `AuthGate` redirect.

This uses the library's own expiry signal rather than polling `user.expires_at`. `events` is on
the context — `react-oidc-context.js:196-201` builds `userManagerContext` as
`Object.assign({ settings, events }, …)` and `:308-313` spreads it into what `useAuth()` returns
— so the hook stays branch-owned and needs no `userManager` prop, and therefore does not collide
with the resolution policy.

### R6 — Consistency sweep

- Stale comments left pointing at removed structures (`values-aws.yaml`, `docker-entrypoint.sh`
  still reference `adminApiIngress` / admin-api after `-X theirs` removed them).
- README port and user corrections.

`HealthStrip` needs no behavioral change — it already handles booleans and `"disabled"`
correctly. Its narrowed `starrocks` domain is handled entirely by R7.

### R7 — Close the `authPending` detection gap

Main's `/health` emits `starrocks = engagementEnabled ? (srStatus == Healthy) : "disabled"` —
so `true | false | "disabled"`, and **`AuthPending` collapses into `false`**. Exactly one of
`HealthStrip`'s `CheckState` arms goes dead: `"authPending"`. `"disabled"` survives.

The danger is not the dead arm, it is that **nothing detects it**. `HealthStrip.test.tsx`
hand-feeds `starrocks: "authPending"` as a fixture (`:118`, `:133`), so those tests stay green
no matter what the server emits — and R2 deletes
`HealthCheckWireFormatTests.StarRocksCheck_AuthPending_IsItsOwnValueAndIsNotFalse`, the only
test currently tying the two sides together. A capability disappears with a fully green suite.

**Remediation:**

1. **Tripwire (required).** A test asserting that under `EngagementHealthStatus.AuthPending`
   with engagement enabled, main's `/health` emits `false` — documenting the collapse as
   current, known and temporary, with the comment naming the follow-up and pointing at
   `HealthStrip`. When the follow-up restores the four-value form on main, **this test fails**,
   and its message is the instruction to restore the widget's arm. A silent capability loss
   becomes a loud one.
2. **Honest widget tests.** Keep the four `"authPending"` assertions as genuine
   forward-compatibility coverage, but retitle/annotate them so nobody reads them as
   live-path proof — the same discipline already applied to `ProbeAuthorizationPipelineTests`.

## Out of scope — follow-ups against `main`

These are main-owned and deliberately excluded by the resolution policy:

1. Re-pin `include_claims_in_id_token: true` on **both** blueprint paths.
2. Restore the four-value `starrocks` wire form (`authPending`) on `/health`; trips R7's tripwire.
3. `Dockerfile` pins `node:20-alpine` **by digest** (a deliberate hardening decision) with no
   `engines` field, while main's own react-router 8.3.1 declares `engines.node >=22.22.0`.
   `EBADENGINE` is a warning, not a failure, so this breaks nothing today.
4. Deleting the branch's drift-guard test loses its "the marked endpoint set has not drifted"
   assertion; main's `AuthenticationPipelineTests` covers `RequireListenerPort` but not that
   invariant.

**Not a follow-up:** main's `"health-composite"` cache-key collision, closed incidentally by R2.

## Verification plan

**Gates.** All three suites green with no test deleted except the ones R2 and R4 enumerate, each
with its stated reason: `Iverson.Api.Tests`,
`Iverson.Vector.Tests`, AdminUI `npm test`. `npx tsc --noEmit` back to a baseline **re-established
post-merge and written here as a number** (the branch's was 7; the spliced merge showed 13;
main's own count is unmeasured).

**Helm.** `helm dependency update` **before every** lint and template — packaged `.tgz` subcharts
shadow live directory edits and silently render stale output, which invalidated an entire
validation round earlier on this branch. Then **`helm template` across all five overlays** — that
is the check that can actually fail here, because `helm lint` exits **0** even when a template
`fail` fires, so a lint-only pass would sign off a chart that cannot render. Assert that each
profile renders `adminApiIngress` where a console exists and does not where one does not.

**Targeted proofs — each must fail against the unfixed code, not merely pass:**

- *R1's tenant filter* — a schema owned by another tenant is absent from the catalog, asserted
  through **both** consumers. Without the REST-side assertion the leak can reopen with the gRPC
  test still green. A **third arm** asserts the same schema is absent from
  `/admin/console/data-volume`'s `types[]`, since that endpoint enumerates the registry on its
  own path and neither catalog assertion can see it.
- *R5's expiry handling* — expiry clears the user and trips `AuthGate`'s redirect. Today's
  behavior passes every existing test, so a test that does not fail against current code proves
  nothing.
- *R3's CSP* — the admin-api origin is present in `connect-src` **as a whole token within that
  directive**. An earlier check on this branch passed while the origin was missing from
  `connect-src`, because the same string appeared in `frame-src`.

**Known-unknowns, named rather than discovered later:**

1. The server suite has never run in a merged state — the build failed before reaching it — so
   main's several hundred new tests against the branch's changes are unmeasured. R1/R2 may
   surface more than the 12 compile errors did.
2. Main's global rate limiter (6,000/min per principal) now covers `/admin/console/*`. Nine
   widgets at a 30-second cadence is ~18 req/min — fine by three orders of magnitude — but
   multi-tab use multiplies it, so check rather than assume.

## Verified assumptions

| Assumption | Evidence |
|---|---|
| `-X theirs` leaves exactly 1 conflict | Ran it; only the blueprint modify/delete remains |
| Console endpoint registrations survive the merge | `Program.cs:642,647` post-merge |
| `-X theirs` does not build | 12 errors: `CS0841` ×10 (`/health`), `CS0103`+`CS1503` (`GetSchema`) |
| `ProjectField` moved into the branch's reader | `SchemaCatalogReader.cs:171` |
| `BuildCatalog` receives `ClaimsPrincipal? actingUser` | `SchemaCatalogReader.cs:44-48` — no signature change needed |
| `BuildCatalog` has no tenant filter today | grep: no `OwnerTenantId` in the file |
| `OwnerTenantId` arrives from main | `main:SchemaDescriptor.cs:93` |
| One fix covers both catalog consumers — but **only** the catalog | `AdminConsoleEndpoints.cs:118` → `reader.ReadCatalog(http.User)`. Scope limit: `/admin/console/data-volume` enumerates the registry on its own path and needs the separate filter in R1 |
| Main's `/health` is self-consistent | `main:ReadinessPolicy.cs` exists; handler uses only main symbols |
| `HealthCheckWireFormat` is branch-only | Absent from main; callers are its own test + `Program.cs` |
| Main's CSP blocks the silent-renew iframe | `main:nginx.conf:35` — no `frame-src`, `default-src 'self'` |
| Main's CSP blocks console data fetches | `connect-src 'self' ${OIDC_ORIGIN}` — no admin-api origin |
| A failed renewal does **not** clear the user | Reducer `ERROR` case preserves `user`; no expiry handler registered; `AuthGate` gates on `!isAuthenticated` |
| `removeUser()` reaches the redirect | `removeUser()` → `_events.unload()` → `userUnloaded` → `USER_UNLOADED` |
| `addAccessTokenExpired` **is** reachable from app code | `react-oidc-context.js:196-201` builds `userManagerContext` as `Object.assign({ settings, events }, …)`; `:308-313` spreads it into the context, so `useAuth().events` is available without a `userManager` prop |
| `user.expires_at` is available | `oidc-client-ts` `User`; `expires_in` is a computed getter |
| Main's admin-ui deployment supplies every env var the entrypoint needs | `main:charts/admin-ui/templates/deployment.yaml` — `OIDC_CLIENT_ID:56`, `OIDC_AUTHORITY:61`, `API_BASE_URL:63`, `EXTERNAL_SCHEME:65`, `OIDC_ORIGIN:67` |
| Main already marks and drift-guards `/build` | `main:Program.cs:463` `.WithMetadata(new RequireListenerPort(8081))`; `main:AuthenticationPipelineTests.cs:199` `[InlineData("/build")]` |
| `networkPolicy.clusterCidrs` is branch-only | `git grep -c clusterCidrs main -- Iverson.Server/deploy` → 0; branch values at `values-aws.yaml:8`, `values-azure.yaml:9`, `values-gcp.yaml:17` |
| `global.prometheusEnabled` is branch-only | `main:Chart.yaml` condition is `prometheus.enabled`; branch values at `values.yaml:70`, `values-aws.yaml:14`, `values-azure.yaml:15`, `values-gcp.yaml:23` |
| `/admin/console/data-volume` enumerates the registry outside `BuildCatalog` | `AdminConsoleEndpoints.cs:179` `registry.All.Values.Select(s => s.TypeName)`; no `OwnerTenantId` anywhere in `Iverson.Api/Search/` |
| `CountRowsAsync` can filter without a signature change | `AggregateReader.cs:50` takes `ClaimsPrincipal? actingUser`; `TypeRowCount.Denied` static at `:128`, which the endpoint folds into `deniedTypeCount` without emitting the name |
| Main's AuthProvider has no `onSigninCallback` | 0 occurrences in `main:AuthProvider.tsx` |
| Main deleted the `/probe/*` endpoints | Only a comment mentions `probe/` in `main:Program.cs` |
| Main has `RequireListenerPort` test coverage | `main:AuthenticationPipelineTests.cs` |
| `TenantStatusCache`'s prefix survives and closes main's collision | Post-merge: `KeyFor` → `tenant-status:{id}`; main's key is bare `"health-composite"` |
| Main's `starrocks` domain | `true \| false \| "disabled"`; `AuthPending` collapses to `false` |
| Widget `authPending` tests are hand-fed fixtures | `HealthStrip.test.tsx:118,133` |
| The AdminUI CI job is branch-unique | Absent from `main:.github/workflows/` |
| Branch work is well-isolated | 85 of 113 changed files untouched by main |

**Carried over, not re-verified:** that Authentik defaults `include_claims_in_id_token` to
`True` — which is what makes dropping the pin safe — was verified against the pinned image in
an earlier session. Follow-up 1 removes the dependency entirely.
