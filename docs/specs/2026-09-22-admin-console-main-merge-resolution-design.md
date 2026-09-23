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
explicitly: in R1 (`GetSchema`'s dead body), and in R3 — the CSP origin added to main's
`nginx.conf`, `docker-entrypoint.sh` and admin-ui Ingress snippet, and the `apiBaseUrl` value R3
writes over main's in `values.yaml`, `values-local.yaml`, `values-aws.yaml`, `values-azure.yaml`
and `values-gcp.yaml`.

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
git detected a conflict. **Resolution: drop the compose pin too.** Re-pinning both is a single
follow-up.

The compose blueprint auto-merges **four** branch hunks, not one, so the pin is not the only
branch content there. Also drop the two `offline_access` scope-mapping entries and their comment
blocks: on `iverson-oidc-default` (merged `:174-188`) and on `iverson-loadtest-human` (merged
`:282-296`). Both exist to serve the console's `offline_access` scope, which main reversed. The
second would also open a new divergence: main's Helm `iverson-loadtest-human` maps only `groups`
and `tenant_id`, so keeping it would make compose issue refresh tokens to main's LoadTest where
Helm does not. Dropping both restores main's mapping lists on both providers and needs no
follow-up.

After that, compose matches main **except** the bypass user's `operators` membership (merged
`:331-335`), which ships deliberately as branch feature work: a compose-only operator identity
for console development, resolved against the branch-only `blueprints/operators-group.yaml` and
documented by `docs/user-management-and-security.md` and R6.

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

- The admin-api origin in the CSP `connect-src`, at **three** sites: the pod's `nginx.conf` and
  `docker-entrypoint.sh`, and main's `charts/admin-ui/templates/ingress.yaml:42`. Main's CSP is
  `default-src 'self'; … connect-src 'self' ${OIDC_ORIGIN}` — no admin-api origin. The third
  site matters because for `ingress.className: nginx` main's template emits a
  `configuration-snippet` whose `more_set_headers` **replaces** the pod's CSP at the Ingress layer
  (its own comment at `:6-7` says so), so on the `nginx`-class profiles the browser enforces the
  Ingress header whatever the pod sends. Its `connect-src` becomes
  `'self' {{ printf "%s://admin-api.%s" .Values.global.externalScheme .Values.global.ingressHost }} {{ $oidcOrigin }}`,
  mirroring main's own `$oidcOrigin` formula at `:27`. No other class writes CSP text from the
  chart: the `azure-application-gateway` branch is opt-in via `securityHeadersRuleSet` (default
  `""`, set by no profile), and `alb` and `gce` leave the pod's header in force.
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
- The CORS middleware in `Program.cs`: `if (!string.IsNullOrEmpty(adminConsoleOrigin))
  app.UseCors(AdminConsoleCorsPolicy);`, between main's `app.UseHttpsRedirection();` and
  `app.UseAuthentication();`. The policy registration (`AddCors`, where `adminConsoleOrigin` is
  declared) survives the merge, but this call sits in the middleware region main rewrote — adding
  `ListenerPortGateAsync` and a pre-auth `UseRateLimiter` — so `-X theirs` removes it. Without it
  no response carries `Access-Control-Allow-Origin` and preflights fall through to the
  FallbackPolicy as 401. It must precede authentication because a preflight carries no
  `Authorization` header. Main has no CORS at all, so this inserts into main's pipeline without
  overriding a main decision.

Re-applied **on top of** main's restructured values — main eliminated `api.ingress.host` drift,
added a `tlsSecretName` guard and templated the external scheme. Most of this is additive: the
restored keys are ones main never declared. Two parts are not, and both are in the resolution
policy's roster: the CSP origin written into main's three CSP sites, and `apiBaseUrl`, whose
single-origin value on main (`http://iverson.local`, `https://iverson.example.com`) R3
**overwrites** with the admin-api host in five files. `values-laptop.yaml` sets `apiBaseUrl` on
neither side.

One route is **removed** rather than restored: drop the `/v1/traces` rule from the branch-only
`charts/api/templates/admin-api-ingress.yaml:51-57`. Main pins `/v1/traces` to
`RequireListenerPort(8080)` (`main:Program.cs:697`), while this Ingress targets Service port 8081,
where main's `ListenerPortGateAsync` answers 404 to every export. Console span export is off until
follow-up 5 lands, and silently so: the exporter surfaces no failure, and `WebApplicationFactory`'s
`LocalPort` of 0 passes the gate in tests.

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

**Restore only** `renderProviderAndCaptureSettings` and `onSigninCallback` — and restore the
helper **rewritten against main's surviving mock**, not in its branch form. The branch helper
resets and returns `capturedProviderProps`, which only the branch's `vi.mock` ever assigned; the
merged file carries main's mock, which pushes into `capturedOidcProps` instead. Restored as-is it
compiles (the `let capturedProviderProps` declaration survives) and throws `AuthProvider did not
render the OIDC provider`, failing the kept `:148` test. So: reset `capturedOidcProps.length = 0`,
render, return `capturedOidcProps[0]`; delete the orphaned `let capturedProviderProps`; add
`onSigninCallback` to main's import. Main's `vi.mock` stays as it is. `setTokenRenewer` and
`requestTokenRenewal` are needed by no surviving test.

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
- README port correction, and the seeded-user sentence added **after** main's bootstrap-password
  sentence (`main:Iverson.AdminUI/README.md:27-29`, which stays). It names main's generated
  `IVERSON_BYPASS_PASSWORD` from `.env` (via `scripts/generate-compose-secrets.sh`), not the
  branch's static password — restoring the branch text verbatim would name a password main
  replaced and revert main's own sentence.
- The two branch-only runbooks, which still give the static credentials main replaced with
  generated ones: `docs/runbooks/admin-console-landing-page-usage.md` `:67` (bypass password),
  `:70` ("hardcoded deliberately") and `:79` (`dev-admin-password`), and
  `docs/runbooks/operator-access-onboarding.md:21` (`dev-admin-password` →
  `AUTHENTIK_BOOTSTRAP_PASSWORD`). A developer following either gets a login that does not work.

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

### R8 — Adapt the branch's AdminUI CI job

`.github/workflows/admin-ui.yml` is branch-only, so it survives the merge untouched and runs on
the merge PR (its triggers include `Iverson.AdminUI/**`). Three of its checks fail on the tree
R1–R7 produce. All three fixes are in this one branch-owned file, so no main line changes:

1. **Main's entrypoint contract.** The served-CSP step's `docker run` (`:156-160`) passes only
   `OIDC_CLIENT_ID`, `OIDC_AUTHORITY` and `API_BASE_URL` — the branch entrypoint's contract. After
   the merge the executing tail is main's, which runs `set -eu` and reads `$EXTERNAL_SCHEME` and
   `${OIDC_ORIGIN}`, so the container exits before nginx starts. Add
   `-e EXTERNAL_SCHEME=http -e "OIDC_ORIGIN=$IDP_ORIGIN"`.
2. **`frame-src`.** Delete `assert_directive_names frame-src "$IDP_ORIGIN"` (`:210`). Main's CSP
   omits `frame-src` by decision (see R5), so the assertion encodes a branch decision main
   reversed — the same class R4 deletes.
3. **The `tsc` ratchet.** Set `BASELINE` (`:100`) to the count `npx tsc --noEmit` measures on the
   post-merge tree. The ratchet compares with `-gt` (`:109`), so a baseline equal to the measured
   count passes.

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
5. Serve `/v1/traces` on the listener the admin-api Ingress targets. Main pins it to
   `RequireListenerPort(8080)` (`main:Program.cs:697`) and the admin-api Ingress sends it to 8081,
   so R3 drops that rule and console span export is off until this lands. Re-add the `/v1/traces`
   rule to `admin-api-ingress.yaml` with it.

**Not a follow-up:** main's `"health-composite"` cache-key collision, closed incidentally by R2.

## Verification plan

**Gates.** All three suites green with no test deleted except the ones R2 and R4 enumerate, each
with its stated reason: `Iverson.Api.Tests`,
`Iverson.Vector.Tests`, AdminUI `npm test`. `AdminConsoleCorsPipelineTests` is the guard for R3's
CORS middleware — it goes red if the `UseCors` call is missing. `npx tsc --noEmit` back to a
baseline **re-established
post-merge and written here as a number** (the branch's was 7; the spliced merge showed 13;
main's own count is unmeasured) — and into `admin-ui.yml`'s `BASELINE` (R8), which is where the
number is enforced. The `admin-ui.yml` workflow is itself a gate: both its jobs green.

**Helm.** `helm dependency update` **before every** lint and template — packaged `.tgz` subcharts
shadow live directory edits and silently render stale output, which invalidated an entire
validation round earlier on this branch. Then **`helm template` across all five overlays** — that
is the check that can actually fail here, because `helm lint` exits **0** even when a template
`fail` fires, so a lint-only pass would sign off a chart that cannot render. Render per overlay
the way `main:.github/workflows/deploy-validate.yml:64-73` does: `helm template … -f
values-<p>.yaml` for `local` and `laptop`, and `helm template … -f values-<p>.yaml -f
values-<p>.ci-override.yaml` for `aws`, `azure` and `gcp`. Without the override, main's
placeholder guard (`templates/_validate.tpl`, included at `networkpolicies.yaml:1`) fails those
three renders on the shipped `iverson.example.com` host whatever R3 does. Assert that each
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
  `connect-src`, because the same string appeared in `frame-src`. Assert it in **both** places
  the browser can get the header from: the pod's CSP, and the rendered `values-local` admin-ui
  Ingress `configuration-snippet`. The pod-side assertion alone passes while the Ingress snippet
  replaces the header and the browser still blocks. The pod-side half is `admin-ui.yml`'s
  served-CSP step (R8), which asserts each origin per directive against the built image.

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
| Authentik defaults `include_claims_in_id_token` to `True` | `ghcr.io/goauthentik/server:2026.5.3` (the tag main pins in `charts/authentik/values.yaml`), `/authentik/providers/oauth2/models.py:250-251` `BooleanField(default=True, …)`, re-probed with `docker run`. This is what makes dropping the pin safe; follow-up 1 removes the dependency |
| The console's access token is accepted by the merged API | Helm: `main:charts/admin-ui/templates/deployment.yaml:56-60` `OIDC_CLIENT_ID` and `main:charts/api/templates/deployment.yaml:141-143` `Authentication__ValidAudiences__0` both read secret `{{ .Release.Name }}-authentik-human-oidc-client` / `client-id`. Compose: `.env.development:1` and `main:docker-compose.yml:483` are both `dev-iverson-human-oidc-client-id` |
| The merge drops the CORS middleware, and main has no CORS | `main:Program.cs` has 0 `UseCors`/`AddCors`; the branch's call at `Program.cs:421-422` sits in the region main rewrote (`main:Program.cs:436-440`); `adminConsoleOrigin` is declared at `:100` in the surviving `AddCors` region |
| Main's admin-ui Ingress replaces the pod's CSP on the `nginx` class only | `main:charts/admin-ui/templates/ingress.yaml:37-42` `more_set_headers` CSP with `connect-src 'self' {{ $oidcOrigin }}`; the AGIC branch is opt-in (`securityHeadersRuleSet: ""` at `main:values.yaml:182,243`, set by no profile); `alb` and `gce` do not override; the branch never touched `charts/admin-ui/` |
| R3 overwrites main's `apiBaseUrl` in five files | main: `values.yaml:238` and `values-local.yaml:147` `http://iverson.local`; `values-aws.yaml:150`, `values-azure.yaml:141`, `values-gcp.yaml:142` `https://iverson.example.com`. `values-laptop.yaml` sets it on neither side |
| Main's surviving test mock writes `capturedOidcProps`, not `capturedProviderProps` | `main:AuthProvider.test.tsx:6` `const capturedOidcProps`, `:11` push inside `vi.mock`, `:20` reset; the branch helper reads `let capturedProviderProps` (`b60e4928:…:7,24-34`), set only by the branch's mock at `:16` |
| Main pins `/v1/traces` to 8080; the admin-api Ingress targets 8081 | `main:Program.cs:697` `.WithMetadata(new RequireListenerPort(8080))`; `charts/api/templates/admin-api-ingress.yaml:51` `/v1/traces` to port `8081`, the `Protocols: Http1` listener (`main:appsettings.json:15-17`) |
| The AdminUI CI job starts main's entrypoint without main's env contract | `admin-ui.yml:156-159` passes only `OIDC_CLIENT_ID`, `OIDC_AUTHORITY`, `API_BASE_URL`; `main:Iverson.AdminUI/docker-entrypoint.sh:2` `set -eu` and `:16` reads `$EXTERNAL_SCHEME`; the job asserts `frame-src` at `:210`, which `main:nginx.conf` omits; `BASELINE=7` at `:100`, compared with `-gt` at `:109` |
| Main's placeholder guard needs the CI overrides on cloud profiles | `main:templates/networkpolicies.yaml:1` includes `iverson.validateNoPlaceholders` (`_validate.tpl`, `https` profiles only); `values-{aws,azure,gcp}.ci-override.yaml` count 0 at `9eb99f76` and on the branch, 3 on main; `main:deploy-validate.yml:64-73` layers them |
| Four branch hunks auto-merge into the compose blueprint | branch `compose-only/service-clients.yaml`: the pin; `offline_access` `!Find` under `iverson-oidc-default` (`:188`) and `iverson-loadtest-human` (`:296`); the `operators` grant (`:335`). Main's compose file has 0 `offline_access` and 0 `operators`; main's Helm `iverson-loadtest-human` maps only `groups` and `tenant_id` (`secret-service-clients.yaml:485-486`); main's LoadTest requests `offline_access` through it (`Iverson.Server/Iverson.LoadTest/Auth/AuthentikFlowExecutorClient.cs:281`) |
| Main replaced the static compose passwords with generated ones | `git grep` for the two static strings over the pure merge tree `06b78f70` finds exactly four sites, all in branch-only runbooks: `admin-console-landing-page-usage.md:67,70,79` and `operator-access-onboarding.md:21`; main's README `:28` names the generated `AUTHENTIK_BOOTSTRAP_PASSWORD` |
