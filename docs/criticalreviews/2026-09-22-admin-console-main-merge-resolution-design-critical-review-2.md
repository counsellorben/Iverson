# Critical Design Review: 2026-09-22-admin-console-main-merge-resolution-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson/.worktrees/admin-console-landing-page/docs/specs/2026-09-22-admin-console-main-merge-resolution-design.md`
**Artifact HEAD at review:** b60e492829e510c134842ea760e4ea2e4794770d
**Verified Assumptions section:** present

All probes ran in a throwaway worktree, `/tmp/cdr2-merge-check`: detached at `b60e4928`, then `git merge main -X theirs`, then patched step by step (R1, R2, then each candidate fix) until it reached a runnable state. One further probe ran in a throwaway `docker.io/openresty/openresty:alpine` container. Every `helm template` ran after `helm dependency update`. The worktree was removed at round close. The primary worktree was not mutated and is still clean at `b60e4928`. Line numbers written as "merged `:N`" refer to the unpatched merge result (`git show :<path>` in the probe worktree).

## 0. Coverage enumeration

### Round-2 mandatory row families

| # | Row | Disposition |
|---|---|---|
| (c) | Amendment hunks since round-1 anchor `c6f313c1` | ok — `[negative]` content-identity check: `git rev-parse c6f313c1:<spec>` = `7e8fa99b…` and `git hash-object <spec>` = `f0c750f1…`. They differ, so the authoritative hunk set is `git diff c6f313c1 -- <spec>` (8 hunks). The forward window `git log c6f313c1..HEAD -- <spec>` holds exactly one commit, `b60e4928 applied 7 fixes from …critical-review-1…`, which is the in-band commit shape. The reverse window `git log HEAD..c6f313c1` is empty, and `c6f313c1` is an ancestor of HEAD. There is no out-of-band amendment. Hunk-to-row map, by new-side spec lines: H1 `+102,32` → a4, a7; H2 `+151,27` → a1, a2, a7 (the R4 heading); H3 `+180,32` → a3; H4 `+217,19` → a3 (R5 restatement), a5; H5 `+268,12` → a6; H6 `+281,26` → a3 (Gates), a1 (Helm), a4 (third arm); H7 `+328,21` → a1, a2, a4, a5, a6, a8 (VA rows); H8 `+354,4` → a6. |
| a1 | Fix neighborhood of r1 §2.1 (`clusterCidrs`): R3 bullet 3 (spec `:154–159`) and its restatements, VA row `:342` and the Helm gate `:290–295` | ok — `[compat]` on the merged tree, `helm template` (with each profile's `ci-override`) exits 1 on aws/azure/gcp with `networkpolicies.yaml:4:4 … clusterCidrs must list at least one CIDR`. local and laptop render. `helm lint` exits **0** on all five, so the rewording of the Helm gate is correct. The bullet's values match `git show b60e4928:…values-{aws,azure,gcp}.yaml` lines 8/9/17 verbatim. `[negative]` `git grep -c clusterCidrs main -- Iverson.Server/deploy` returns 0. |
| a2 | Fix neighborhood of r1 §2.2 (`global.prometheusEnabled`): R3 bullet 4 (`:160–167`) and VA row `:343` | ok — `[compat]` merged aws render with only `clusterCidrs` supplied: `charts/prometheus` deployment/configmap/pvc sources are present and `Prometheus__BaseUrl` appears ×0. Adding `--set global.prometheusEnabled=true` gives ×1 (rendered line 2333). The branch lines `values.yaml:70`, `values-aws.yaml:14`, `values-azure.yaml:15` and `values-gcp.yaml:23` are confirmed. The "four of five profiles" wording is handled in D4. |
| a3 | Fix neighborhood of r1 §2.3: the R4 disposition list (`:183–208`) and its restatements at R1 `:128–130`, R5 `:220–221` and Gates `:284–285` | under (a kept test or symbol left unrunnable): → §2.3 for the "Restore only `renderProviderAndCaptureSettings`" entry. Every other entry: ok — `[compat]` I applied the list to the merged `AuthProvider.{tsx,test.tsx}` and ran `vitest`. The deletions at `:105/:132/:139` and the `:4` import / `useTokenRenewal()` removal leave only the helper failing (matrix M4). `grep -rn 'useTokenRenewal\|setTokenRenewer\|requestTokenRenewal\|renewalInFlight' src` finds referents only in the files the list names. Line-cite drift is handled in D3. / over (R4 deletes something a kept test or main relies on): ok — `[compat]` inside `AuthProvider.test.tsx`, see R-5 over. For the entries outside it (`useTokenRenewal.{ts,test.ts}`, `client.ts`'s seam, `client.test.ts`'s renewal cases), the full AdminUI suite still passes after the whole list plus the §2.3 fix is applied: 21 files / 221 tests (§2.3 Evidence). |
| a4 | Fix neighborhood of r1 §2.4: the data-volume paragraph (`:110–123`), VA rows `:331,:344,:345` and the third proof arm (`:301–303`) | ok — `[totality]` I re-enumerated registry enumerations on the merged tree with `grep -rnE '(registry\|Registry)\.All\b\|\.All\.(Values\|Keys\|Count)'` (non-test). The hits are `SchemaCatalogReader.cs:60`, `AdminConsoleEndpoints.cs:115` (count only), `:179` (data-volume), `Program.cs:699` and `SchemaRegistrationOrchestrator.cs:271` (not caller-facing), which is the same closed set as r1. `CountRowsAsync` has one caller (`AdminConsoleEndpoints.cs:186`). `RunAsync`'s other caller (`ObjectSearchGrpcService.cs:772`) is untouched by the fix. `AggregateReader.cs` is absent at `9eb99f76` and on `main`, so it is branch-owned. `TypeRowCount.Denied` is at `:128`. The third arm has a seam: `AdminConsoleDataVolumeEndpointTests` builds a real `AggregateReader` over a seeded registry and a stub search service. |
| a5 | Fix neighborhood of the r1 §1 failure: the R5 fix paragraph (`:223–232`) and VA row `:338` | ok — `[presence]` In `dist/umd/react-oidc-context.js`, `:196–201` builds `Object.assign({ settings: userManager.settings, events: userManager.events }, …)` and `:308–313` spreads it into the context. `dist/types/*.d.ts:31` declares `readonly events: UserManagerEvents`. In `oidc-client-ts.d.ts`, `:30` declares `addAccessTokenExpired(cb): () => void`. The expiry timer is armed by `getUser()` → `_events.load(user)` (esm `:2955–2960`) → `_expiredTimer.init(duration + 1)` (`:522–524`). The branch, merge-base and main lockfiles all resolve react-oidc-context 3.3.1 / oidc-client-ts 3.5.0, so these node_modules are what the merge ships. The merged `AuthGate` effect (`AuthProvider.tsx:56–60`) depends on `[isLoading, isAuthenticated, signinRedirect]` and has no one-shot guard, so it re-fires on the `USER_UNLOADED` flip. |
| a6 | Fix neighborhood of r1 R-h (`/build`): follow-up removed, VA row `:341` added, follow-ups renumbered, cross-reference "Follow-up 1 removes the dependency" (`:357`) | ok — `[presence]` `main:Program.cs:463` `…WithMetadata(new RequireListenerPort(8081))` and `main:AuthenticationPipelineTests.cs:199` `[InlineData("/build")]`. Follow-up 1 is now the re-pin, so `:357` points at the right item. |
| a7 | "Hand-resolve" → "Resolve per R4's disposition list" (`:130`, `:174`) | ok — wording only; the target list exists. |
| a8 | r1 span item 5 promoted to VA row `:340` (admin-ui env vars) | ok — `[presence]` `main:charts/admin-ui/templates/deployment.yaml` has `- name: OIDC_CLIENT_ID` at `:56`, `OIDC_AUTHORITY` at `:61`, `API_BASE_URL` at `:63`, `EXTERNAL_SCHEME` at `:65` and `OIDC_ORIGIN` at `:67`. The merged `docker-entrypoint.sh` validates only `OIDC_CLIENT_ID`/`OIDC_AUTHORITY`/`API_BASE_URL` and reads `EXTERNAL_SCHEME`/`OIDC_ORIGIN` in main's tail, so all five are supplied. |
| b1 | Intersected fix text: r1 §2.3's table cell "`:148,:159` … ok — R4's re-add makes both pass" and its fix text "restore only `renderProviderAndCaptureSettings` and `onSigninCallback`" | → §2.3. The evidence changed: round 2 is the first run of the restored helper against the test file's surviving `vi.mock`. |
| b2 | Intersected fix text: r1 A6 ("`:169` `AddCors(...)` survives the merge, so the arrow's required parameter exists") and r1's R-a matrix cell for `Program.cs` ("branch `/health`, `HttpListenerOnly`, `/probe/*`") | → §2.1. The evidence changed: the round-2 dropped-line sweep finds `app.UseCors(...)` absent, and the branch's CORS pipeline tests run red. |
| b3 | Intersected fix text: r1 §2.1/§2.2 (extend R3's list) and r1 A8 ("R3's admin-api origin is derivable without touching a main-owned template") | → §2.2. The pod-side derivation holds, but main's admin-ui Ingress replaces the pod's CSP header on the `nginx` class. |
| b4 | Intersected fix text: r1 S22 dropped row (a `"traces"` 429 "degrades telemetry, not the nine widgets") | → §3.1. This is not a re-raise. S22 was a rate-limit breach conditioned on multi-tab use. §3.1 is a deterministic 404 on every export, caused by main's listener-port pin, which S22 never examined. |
| b5 | Paired-artifact probe evidence (spec ↔ plan) | ok — `[negative]` `grep -rl main-merge-resolution docs/` finds only this spec and review 1. No plan or CIR exists yet. |

### Sections

| # | Section | Disposition |
|---|---|---|
| S1 | Header (tip `f01f03a7`) | dropped — r1 S1 (a one-commit-stale tip sha), not re-raised. |
| S2 | Context (18 conflicts, split-origin premise) | ok — `[totality]` `git merge-tree --write-tree --name-only b60e4928 main` lists exactly 18 conflicted paths (the population of M1). |
| S3 | Resolution policy | ok — used as the frame for R-1…R-7. |
| S4 | Phase 1 — the merge commit | ok — `[totality]` `git merge main -X theirs` leaves one unmerged path (`UD …/blueprints-configmap-service-clients.yaml`). `[presence]` Registrations survive at merged `Program.cs:642,647`. `[totality]` The premise "additive work survives where it did not conflict" holds over the whole population. Of the 96 non-conflicted branch-changed files, 88 keep every non-blank branch-added line in the merge result (`git show :<f>`), with 0 dropped. The other 8 are exactly R2's six deletions (`HealthCheckCache.cs`, `HealthCheckCacheTests.cs`, `HealthCheckWireFormat.cs`, `HealthCheckWireFormatTests.cs`, `OperationalListenerBindingPipelineTests.cs`, `ProbeAuthorizationPipelineTests.cs`) and R4's two (`useTokenRenewal.ts`, `useTokenRenewal.test.ts`), which I had already `git rm`'d in the probe. The 18 conflicted files are M1. Both failure directions of this merge rule are dispositioned at R-1 (over/under). The additive hunk that *did* conflict and is dispositioned nowhere is → §2.1. |
| S5 | Phase 1 — blueprint conflict | ok — `[presence]` the merged `compose-only/service-clients.yaml:170` keeps the pin, and `main:…/secret-service-clients.yaml` has 0 occurrences. The safety premise (Authentik default) is verified in §1, span item 5. |
| S6 | "`-X theirs` does not produce a building tree" | ok — `[compat]` `dotnet build Iverson.Api` on the merge: 12 errors, `CS0841` ×10 (`Program.cs:539–548`), `CS0103` (`ObjectMappingGrpcService.cs:102`), `CS1503` (`:109`). |
| S7 | R1 — `/health` | ok — `[compat]` I copied main's handler region verbatim from `main:Program.cs` over the splice and applied R2. `dotnet build Iverson.Api.Tests` gives 0 errors. |
| S8 | R1 — `GetSchema` | ok — `[presence]` in `git diff 9eb99f76 main -- …/ObjectMappingGrpcService.cs`, the tenant filter and its `callerTenant` local are main's only changes inside `GetSchema`'s body. Deleting the dead loop strands nothing else of main's. |
| S9 | R1 — carry main's tenant filter into `BuildCatalog` | ok — `[compat]` applied exactly as described (`callerTenant` derived from `actingUser` inside `BuildCatalog`, no signature change). Main's own `GetSchema_OwnerTenantIdSet_ExcludesOtherTenantsCatalog_ButIncludesUnscopedTypes` fails before the carry and passes after. `ObjectMappingGrpcServiceTests` + `AdminConsole*` = 157/157. (both directions: see R-6). |
| S10 | R1 — data-volume filter | ok — see a4 (both directions: see R-6). |
| S11 | R1 — semantic note (no `tenant_id` ⇒ tenant-owned schemas excluded) | both directions: see R-6 (over/under dispositioned there). ok — `[presence]` matches main's predicate `schema.OwnerTenantId is not null && schema.OwnerTenantId != callerTenant` at `main:ObjectMappingGrpcService.cs:86`. |
| S12 | R1 — `AuthProvider.test.tsx` pointer | → §2.3 (other entries: ok — see a3). |
| S13 | R2 — deletions and `TenantStatusCache` (rule: "delete branch code main's equivalents supersede") | over (R2 deletes something still referenced): ok — `[negative]` `grep -rn` over the merged tree: `HealthCheckWireFormat` is referenced only by `Program.cs:542` and its own test. `HealthCheckCache` is referenced only by `Program.cs:355`, the `TenantStatusCache.cs:13` comment and its own test. `HttpListenerOnly` is referenced only by `OperationalListenerBindingPipelineTests.cs`. After the R2 deletions the test project builds with 0 errors. / under (R2 leaves a branch symbol stranded by R1): ok — `[compat]` after R1+R2, `grep -rn 'HealthCheckCache\|HealthCheckWireFormat\|HttpListenerOnly' --include=*.cs Iverson.Server/` returns only the `TenantStatusCache.cs:13` comment, which R2 already rewrites. `dotnet build Iverson.Api.Tests` gives 0 errors. `TenantStatusCache.cs:16` `KeyFor` = `tenant-status:{tenantId}`. |
| S14 | R3 — CSP bullet | → §2.2 (both directions: see R-4). |
| S15 | R3 — values-key bullets (`adminApiIngress`/`adminConsoleOrigin`/`apiBaseUrl`, `clusterCidrs`, `prometheusEnabled`) | ok — `[totality]` see R-2. |
| S16 | R3 — `docker-compose.yml` (rule: re-add the local CORS origin and `Prometheus__BaseUrl`) | over (re-adding overrides a main compose decision): ok — `[negative]` `git show main:Iverson.Server/docker-compose.yml \| grep -c 'AdminConsole__Origin\|Prometheus__BaseUrl'` → 0. / under (the merge drops a compose line R3 does not name): ok — `[presence]` `git diff main -- Iverson.Server/docker-compose.yml` on the merge is empty. The only branch lines it drops are `AdminConsole__Origin` and `Prometheus__BaseUrl`, which are exactly R3's named pair. |
| S17 | R3 as a whole ("one unit … the console cannot load data without all of it") | over (a re-applied link overrides a main decision): ok — `[negative]` see R-2 over (no restored key exists on `main`), S16 over (neither compose key exists on `main`) and R-4 over (the only CSP addition is the admin-api origin). `apiBaseUrl` value overwrite: dropped — r1 R-c, not re-raised. / under (a link the console needs is missing from the chain): → §2.1, §2.2. Two links are absent from the enumeration. `[totality]` I enumerated the chain as every hop a console request traverses, in order. The document CSP is A4 (matrix M2). The runtime `apiBaseUrl` config is A7 plus R-2. The DNS/Ingress host and NetworkPolicy are A8. The Kestrel listener gate is A6 (matrix M3). CORS is A5. Token audience is A10. The endpoints' own readers are A1–A3. The response consumer is A11. Every hop has a row in the Data-flow arrows table, and each is dispositioned there. |
| S18 | R4 | → §2.3 (other entries: ok — see a3). |
| S19 | R5 | ok — `[presence]` see a5 (both directions: see R-7). |
| S20 | R6 | ok — `[presence]` the listed stale comments are in the merge result: `values-aws.yaml:142,162,166` name `adminApiIngress`/`adminConsoleOrigin`, which the merge removed, and `docker-entrypoint.sh:9,12` describe the admin-api origin, whose CSP render the merge replaced. Further copy residue is handled in D5. |
| S21 | R7 | ok — `[presence]` main's handler emits `engagementEnabled ? (object)(srStatus == EngagementHealthStatus.Healthy) : "disabled"`. `EngagementHealthStatus.AuthPending` exists on main (`Iverson.StarRocks/EngagementHealthStatus.cs:10`). `main:AuthenticationPipelineTests.cs` already substitutes `IEngagementStoreHealthCheck`, so the tripwire has a seam. |
| S22 | Follow-ups 1–4 | #1 (re-pin on both paths): ok — `[negative]` after Phase 1 neither path carries the pin. `main:…/secret-service-clients.yaml` has 0 `include_claims_in_id_token` hits, and Phase 1 drops the compose pin (merged `compose-only/service-clients.yaml:170`); span item 5 shows the default is `True`. #2 (restore `authPending`): ok — `[presence]` main's handler collapses `AuthPending` to `false` (S21). #3 (`node:20` digest): dropped — the "no `engines` field" premise is r1 R-i (dropped there), not re-raised. #4 (drift-guard direction): ok — `[presence]` `main:AuthenticationPipelineTests.cs:198–209` (`[InlineData]` ×4 + `HealthListenerEndpoint_IsMarkedForHealthListenerPort`) asserts the marker per listed route only, while the branch's `OperationalListenerBindingPipelineTests.cs:111,120` (`HttpListenerOnlyMetadata_IsOnExactlyTheOperationalEndpoints`) asserted the marked set is exactly eight patterns. |
| S23 | "Not a follow-up" (health-key collision) | ok — `[presence]` merged `TenantStatusCache.cs:16` `KeyFor` = `tenant-status:{tenantId}`; main's health key is the bare literal `"health-composite"` (`main:Program.cs` health handler). |
| S24 | Verification — Gates | under (the gates cannot be met as written): → §2.1, §2.3. Each leaves a kept test red; the full suite × result population is matrix M5. / over (the gates pass while the merged tree is broken): → §2.2 and → §3.1. No gated suite exercises the Ingress-layer CSP (§2.2's fix extends R3's CSP proof to cover it), and `/v1/traces` 404s only on a real bound port, which `WebApplicationFactory` (`LocalPort` 0) never presents (§3.1). |
| S25 | Verification — Helm (rule: `helm dependency update`, then `helm template` over all five overlays, then assert `adminApiIngress` presence per profile) | under (the gate cannot be met once R3 is applied): ok — `[compat]` with `clusterCidrs` supplied, aws/azure/gcp render with exit 0 (§2.2 Evidence, a2), and local/laptop render with exit 0 (a1). / over (the gate passes on a render that is broken): → §2.2. `values-local` renders with exit 0 while its admin-ui Ingress carries a CSP without the admin-api origin; that fix extends the proof to catch it. The lint-only blind spot is closed by the spec's own wording: `[compat]` `helm lint` exits 0 while `helm template` exits 1 (a1). Separately, after I edited a subchart template in the probe, a `helm template` without re-running `helm dependency update` rendered the **stale** packaged `.tgz`. The spec's "before every" instruction is load-bearing. |
| S26 | Verification — targeted proofs | R1 arms: ok (S9, a4). R5: ok (a5). R3's CSP proof: → §2.2, because it inspects only the pod-side CSP. |
| S27 | Known-unknowns | ok — `[presence]` KU-1 is borne out (D1 is one such case). KU-2: the post-auth `GlobalLimiter` exempts only 8081-marked endpoints and gRPC (merged `Program.cs:117–119`). The pre-auth limiter is 50,000/min per IP (`:455–476`, `PermitLimit = 50_000` at `:473`). |
| S28 | Verified-assumptions table | ok — see §1 (30 of 30 reconfirmed). |
| S29 | "Carried over, not re-verified" (Authentik default) | ok — `[presence]` verified in-round; `models.py:250–251` `default=True` in the pinned image (§1, span item 5). |

### Rules and operands (both failure directions)

| # | Rule | Disposition |
|---|---|---|
| R-1 | Merge rule: "`-X theirs` keeps branch additive work where it did not conflict, and everything it drops is dispositioned by Phase 1 / R1–R7" | over (merged content keeps branch material that overrides a main decision): ok — `[totality]` r1's 28-file contested sweep still applies because its inputs are unchanged. `git rev-parse main` = `65cdf63ac7ed…`, the same `main` r1 swept (r1 S2). `git diff --stat c6f313c1 b60e4928` = 1 file changed, the spec itself, so no contested code path moved since r1's sweep. / under (the merge drops branch code that nothing dispositions): → §2.1 — full matrix M1. |
| R-2 | R3's key restoration × values files | over (restoring a key overwrites a main decision): ok — `[negative]` `git grep` on `main` finds `adminApiIngress`, `adminConsoleOrigin`, `clusterCidrs` and `prometheusEnabled` in no `values*.yaml`. `apiBaseUrl` value overwrite: dropped — r1 R-c, not re-raised. / under (a branch key the merge drops is missing from R3): ok — `[totality]` a script computed, for each of 7 files (`values.yaml`, `values-local.yaml`, `values-laptop.yaml`, `values-aws.yaml`, `values-azure.yaml`, `values-gcp.yaml`, `charts/api/values.yaml`), (leaf keys at `b60e4928`) − (leaf keys post-merge). The dropped set is exactly {`global.prometheusEnabled`, `api.adminApiIngress.*`, `api.adminConsoleOrigin`, `networkPolicy.clusterCidrs`, `apiBaseUrl` value}, all named by R3, plus `api.ingress.host` and `adminUi.oidcAuthority`. Those last two are absent on `main` (main removed them), and `grep -rn 'ingress.host\|oidcAuthority' charts/*/templates templates` finds only comments. |
| R-3 | Main's `ListenerPortGateAsync` × every route the console calls, arriving on 8081 (the admin-api Ingress's target) | over (the gate blocks a console route): → §3.1 (`/v1/traces`) — matrix M3. / under (a console route answers on a listener main forbids): ok — `[totality]` the only main-marked routes in M3 (`/health` → 8081, `/v1/traces` → 8080) are enforced by the same gate that M3 ran. The five `/admin/console/*` routes are branch-new and unmarked, so no main decision governs their listener. |
| R-4 | CSP sources governing the console **document** × profiles | over (a source admits an origin main did not sanction): ok — `[totality]` M2 enumerates every CSP source for the console document on every profile: the pod (`nginx.conf:35` via the entrypoint's `envsubst`), the admin-ui Ingress snippet (`nginx` class, `:42`), and the AGIC rule set (opt-in, never rendered by default). ALB and GCE have no source at the Ingress. R3 plus the §2.2 fix add exactly one token to `connect-src`, the admin-api origin. The §2.2 patched render differs from main's line only by that token. / under (after R3, a source still withholds the admin-api origin from the header the browser enforces): → §2.2 — matrix M2. |
| R-5 | R4 test disposition × `AuthProvider.test.tsx` dependencies | over (R4 deletes something main relies on): ok — `[presence]` main's `:25`, `:37` and the three `AuthGate` tests are untouched and pass. / under (a kept test left unrunnable): → §2.3 — matrix M4. |
| R-6 | Tenant-ownership predicate × caller-facing registry enumerations | over (the predicate hides a schema the caller is entitled to see): ok — `[totality]` I re-ran r1's producer enumeration on the merged tree: `grep -rn OwnerTenantId` over all non-test sources (§1, the "`BuildCatalog` has no tenant filter" bullet) returns exactly `SchemaDescriptor.cs:93` (declaration), `SchemaRegistrationOrchestrator.cs:79` (read), `:351` (the only write) and `ObjectMappingGrpcService.cs:86` (main's filter). / under (a caller-facing enumeration returns another tenant's schema): ok — `[totality]` a4 re-closes the surface set, and the spec now covers both caller-facing members. |
| R-7 | R5 expiry → redirect | over (fires while the token is valid): ok — `[presence]` `_expiredTimer.init(duration + 1)` (esm `:522–524`). / under (never fires): ok — `[presence]` the timer is armed by `getUser()` at provider init (esm `:2955–2960`) and by the sign-in paths (`:3163`, `:3331`, `:3507`), and the `AuthGate` effect is not one-shot (a5). |

**M1 — population closure for R-1 (18 conflicted files × branch code the merge drops).** Method: for each file, I took the non-blank `+` lines of `git diff 9eb99f76 b60e4928 -- <f>` that are absent from the merged `git show :<f>`, excluding comment-only lines. A second pass checked each branch hunk for contiguity in the merged file. It found 3 hunks whose lines are all individually present but not contiguous (the entrypoint prologue, the `:105` test, and the console registrations). All 3 survive intact, so the per-line method's blind spot holds no dropped code.

| Conflicted file | Branch code lines dropped | Cell |
|---|---|---|
| `Iverson.AdminUI/Dockerfile` | `FROM node:22-alpine` | ok — Phase 1 (main's digest pin wins); r1 R-i |
| `Iverson.AdminUI/README.md` | 3 (compose bypass-user text) | ok — R6 |
| `Iverson.AdminUI/docker-entrypoint.sh` | 10 (`ADMIN_API_ORIGIN`/`OIDC_ORIGIN` derivation, `.inc` CSP render) | ok — R3 CSP bullet (see §2.2 for the site it misses) |
| `Iverson.AdminUI/nginx.conf` | `include …admin-ui-security-headers.inc` | ok — R3 CSP bullet |
| `Iverson.AdminUI/package-lock.json` | 13 (`@vitejs/plugin-react` 5.2.0 graph) | ok — Phase 1 (main's plugin-react fix wins) |
| `Iverson.AdminUI/package.json` | `"@vitejs/plugin-react": "^5.2.0"` | ok — Phase 1 |
| `src/auth/AuthProvider.test.tsx` | 13 (branch `vi.mock`, helper, imports) | → §2.3 (helper vs. surviving mock); rest: ok — R4's disposition list (see a3) |
| `src/auth/AuthProvider.tsx` | 6 (`onSigninCallback` fn + key, `export` on `oidcConfig`, `offline_access` scope, `revokeTokensOnSignout`) | ok — `[negative]` `grep -rn oidcConfig src` finds no importer, so losing `export` is inert. `onSigninCallback` is re-added by R4, and main's scope/revocation win |
| `Iverson.Api.Tests/Helpers/AuthTestWebApplicationFactory.cs` | 0 | ok — `[totality]` 0 dropped code lines (M1 method); the 4 dropped lines are comments |
| `Iverson.Api/Grpc/ObjectMappingGrpcService.cs` | 0 | ok — R1 |
| `Iverson.Api/Program.cs` | 34 lines. 31 are the `HttpListenerOnly` mechanism (port parse/log, gate, `TryParseListenerPort`, class, and 8 markers, 4 of which are on `/probe/*`), and 2 are the branch `/health` handler | ok — Phase 1 (`RequireListenerPort` wins), R1, R2 |
|  | `app.UseCors(AdminConsoleCorsPolicy);` | **→ §2.1** |
| `…/blueprints-configmap-service-clients.yaml` | 0 (file deleted by Phase 1) | ok — `[totality]` 0 dropped lines (M1 method); Phase 1 `git rm`s the file |
| `values-aws.yaml` | `networkPolicy`/`clusterCidrs`, `prometheusEnabled`, `adminApiIngress`, `adminConsoleOrigin`, `apiBaseUrl`, `adminUi.oidcAuthority` | ok — R3, except `oidcAuthority`, which is ok because main removed it and no template reads it (R-2) |
| `values-azure.yaml`, `values-gcp.yaml` | `networkPolicy`/`clusterCidrs`, `prometheusEnabled`, `apiBaseUrl`, `adminUi.oidcAuthority` | ok — same |
| `values-local.yaml` | `apiBaseUrl` | ok — R3 |
| `values.yaml` | `prometheusEnabled`, `adminApiIngress: {}`, `adminConsoleOrigin: ""`, `apiBaseUrl` | ok — R3 |
| `docker-compose.yml` | `AdminConsole__Origin`, `Prometheus__BaseUrl` | ok — R3 |

**M2 — population closure for R-4 (CSP source for the console document × profile).** `adminUi.ingress.className` per profile was read by script from each merged values file layered over `values.yaml`.

| Profile | className | Header the browser enforces | Cell |
|---|---|---|---|
| `values-local` | `nginx` | The admin-ui Ingress's `more_set_headers` **replaces** the pod's CSP. The rendered merged snippet is `connect-src 'self' http://authentik.iverson.local` | **→ §2.2** |
| `values-aws` | `alb` | pod CSP only | ok — `[absence]` the rendered admin-ui Ingress (`ingressClassName: "alb"`, merged aws render with `clusterCidrs` supplied) carries 0 `Content-Security-Policy`, 0 `configuration-snippet` and 0 `rewrite-rule-set` annotations, so R3's pod-side edit governs |
| `values-azure` | `azure-application-gateway` | pod CSP, unless `ingress.securityHeadersRuleSet` is set. Its default is empty, so no annotation renders; any operator-provisioned rule set lives outside the chart | default (rule set unset): ok — `[absence]` the rendered admin-ui Ingress (`ingressClassName: "azure-application-gateway"`) carries 0 `Content-Security-Policy`, 0 `configuration-snippet` and 0 `rewrite-rule-set` annotations. Opt-in (an operator sets `securityHeadersRuleSet`): dropped — the rule set's header text is operator-authored outside this chart and repo. `[negative]` `git grep -c azurerm_application_gateway_rewrite_rule_set main -- Iverson.Server/deploy/terraform` → 0, so no CSP text for it exists for the merge to carry or miss |
| `values-gcp` | `gce` | pod CSP only | ok — `[absence]` the rendered admin-ui Ingress (`ingressClassName: "gce"`) carries 0 `Content-Security-Policy`, 0 `configuration-snippet` and 0 `rewrite-rule-set` annotations |
| `values-laptop` | — (`adminUi.enabled: false`) | no console | ok — `[absence]` the merged `values-laptop` layered over `values.yaml` reads `adminUi.enabled: False` (M2 script), and its full render (`helm template … -f values-laptop.yaml`, exit 0) contains 0 `charts/adminUi` sources, so no admin-ui Ingress or pod exists |
| `values.yaml` alone | `""` | no snippet | ok — `[absence]` the rendered admin-ui Ingress (`ingressClassName: ""`, with `clusterCidrs` supplied) carries 0 `Content-Security-Policy`, 0 `configuration-snippet` and 0 `rewrite-rule-set` annotations |

**M3 — population closure for R-3 (console route × gate on 8081).** The route population comes from grepping `Iverson.AdminUI/src` (non-test) for `/admin`, `/health` and `/v1` literals. The console uses `console.ts:24` prefix `/admin/console` for five endpoints, plus `/health` and `/v1/traces` (`telemetry.ts:27`). Probe: a throwaway xUnit theory in the probe worktree resolves each route's **real** `RouteEndpoint` from the built app's `EndpointDataSource`, then calls main's real `Program.ListenerPortGateAsync` with `Connection.LocalPort = 8081`.

| Route | Marker | Gate result on 8081 | Cell |
|---|---|---|---|
| `/admin/console/tenants` | none | `next` invoked | ok — `[compat]` run |
| `/admin/console/schema` | none | `next` invoked | ok — `[compat]` run |
| `/admin/console/data-volume` | none | `next` invoked | ok — `[compat]` run |
| `/admin/console/qdrant` | none | `next` invoked | ok — `[compat]` run |
| `/admin/console/metrics` | none | `next` invoked | ok — `[compat]` run |
| `/health` | 8081 | `next` invoked | ok — `[compat]` run |
| `/v1/traces` | **8080** | **404, `next` not invoked** | **→ §3.1** |

**M4 — population closure for R-5 (`AuthProvider.test.tsx` after R4 × its dependencies).**

| Test / symbol | Depends on | Cell |
|---|---|---|
| `:25`, `:37` (main's scope / `automaticSilentRenew`) | main's `oidcConfig` | ok — `[compat]` pass |
| `:54`, `:71`, `:88` (`AuthGate` ×3) | `useAuthMock` | ok — `[compat]` pass |
| `:105`, `:132`, `:139` | — | ok — deleted by R4 |
| `:148` "wires a signin callback…" | `renderProviderAndCaptureSettings` + `oidcConfig.onSigninCallback` | **→ §2.3** — the helper, restored as written, throws |
| `:159` "leaves no history entry…" | `onSigninCallback` import + production function | ok — `[compat]` pass |
| `let capturedProviderProps` (merged `:8`) | the branch helper only | → §2.3 (orphaned once the helper is corrected) |

**M5 — population closure for S24 (Gates × every gated suite, run on the patched merge tree).** Server tree: R1 (including the tenant carry) + R2 + the §2.1 line. AdminUI tree: R4's full list + the §2.3 helper fix.

| Gated suite | Result | Cell |
|---|---|---|
| `Iverson.Api.Tests` (full) | Without the §2.1 line: `AdminConsoleCorsPipelineTests` 4 failed / 2 passed. With it: **1167 / 1167 passed** (`dotnet test`, 5 m 58 s) | without the fix: → §2.1. With it: ok — `[compat]` full-suite run |
| `Iverson.Vector.Tests` (full) | **169 / 170 passed**; the 1 failure is `ServiceCollectionExtensionsTests.AddQdrant_RegistersResolvableVectorCollectionReader` | dropped — D1 (a fixture-only remedy, nothing deleted) |
| AdminUI `npm test` (`vitest run`, full) | Helper restored as R4 writes it: `AuthProvider.test.tsx` 1 failed / 6 passed. With the §2.3 fix: **21 files / 221 tests passed** | without the fix: → §2.3. With it: ok — `[compat]` full-suite run |
| `npx tsc --noEmit` baseline | not pass/fail. The spec re-establishes the number after the merge, and r1 S19 recorded 14 on the spliced tree | dropped — the spec defines no threshold that could fail; r1 S19, not re-raised |

### Data-flow arrows

| # | Arrow → consuming operation | Disposition |
|---|---|---|
| A1 | `SchemaCatalogReader.BuildCatalog` → gRPC `GetSchema` | ok — see S9 |
| A2 | `ReadCatalog(http.User)` → `/admin/console/schema` | ok — `[compat]` `AdminConsole*` tests pass on the R1-applied tree |
| A3 | `registry.All` → `/admin/console/data-volume` → `CountRowsAsync` | ok — see a4 |
| A4 | console document ← CSP (pod **or** Ingress) → `fetch` to `admin-api.<host>` | → §2.2 |
| A5 | cross-origin `fetch` / preflight → CORS middleware → endpoint | → §2.1 |
| A6 | console OTLP exporter → `admin-api.<host>/v1/traces` → Service `8081` → Kestrel `Http` listener → `ListenerPortGateAsync` | → §3.1 |
| A7 | values → `charts/api/templates/deployment.yaml` env (`AdminConsole__Origin`, `Prometheus__BaseUrl`) | ok — `[compat]` renders confirm both are gated on R3's restored keys (a2) |
| A8 | values → `networkpolicies.yaml` guard; NetworkPolicy → `api:8081` | ok — `[compat]` the values → guard hop: the azure render exits 0 with `clusterCidrs` supplied and exits 1 without it (a1). `[presence]` the NetworkPolicy → `api:8081` hop: the rendered `iverson-api-ingress` NetworkPolicy admits port 8081 from `namespaceSelector`/`podSelector`/`ipBlock` (read from that render) |
| A9 | blueprint without the pin → id_token claims | ok — `[presence]` Authentik default (§1, span item 5) |
| A10 | console access-token `aud` → API `ValidAudiences` | ok — `[presence]` the admin-ui `OIDC_CLIENT_ID` (merged `charts/admin-ui/templates/deployment.yaml:56–60`) and `Authentication__ValidAudiences__0` (`charts/api/templates/deployment.yaml:141–143`) both read `{{ .Release.Name }}-authentik-human-oidc-client`/`client-id`. In compose, `.env.development:1` `dev-iverson-human-oidc-client-id` equals compose `ValidAudiences__0` (`:483`) |
| A11 | main `/health` JSON → `HealthStrip` | ok — `[presence]` this is r1 A5's declaration comparison, and both of its inputs are unchanged. The producer is main's handler, restored verbatim (S7; the `starrocks` expression is re-read in S21). The consumer `HealthStrip.tsx` is branch-only and outside the 18 conflicted files (M1), so the merge carries it byte-for-byte |

### Dropped candidates

| # | Candidate | Disposition |
|---|---|---|
| D1 | `Iverson.Vector.Tests.ServiceCollectionExtensionsTests.AddQdrant_RegistersResolvableVectorCollectionReader` fails on the merged tree: its fixture `apiKey: "test-api-key"` (12 bytes) trips main's new ≥32-byte guard (`ServiceCollectionExtensions.cs:27–31`) | dropped — KU-1 names this class of failure exactly. The remedy is a fixture value (main's sibling tests use a 32-byte key), deletes nothing and involves no design choice |
| D2 | The admin-api Ingress sits outside main's placeholder and TLS-secret guards (`templates/_validate.tpl:70`, `$ingressBlocks` = api/authentik/adminUi) | dropped — a correctly configured deploy works; this is an operator-error path |
| D3 | R4 cites `AuthProvider.tsx` `:62` for `useTokenRenewal()`; in the merged file it is `:54` (`:62` is the branch file's line) | dropped — the symbol is unique |
| D4 | R3 says the metrics widget is dead "on four of five profiles"; the actual set is aws/azure/gcp plus the bare `values.yaml` default (local works, and laptop has no console) | dropped — the fix's file list is correct |
| D5 | After R4, the `client.ts` module doc (`:28–40`) and `WidgetCard`'s "Session expired — renewing…" (`:153–154`) still describe renewal | dropped — comments and copy; R6's category |
| D6 | If R5's hook is mounted inside `AuthGate`, main's `AuthGate` tests' `useAuth` mock returns no `events` | dropped — the mount point is an implementation detail (CIR) |
| D7 | Operator-gated `/admin/console/qdrant` lists collection names | dropped — Operator-gated by design, and not a registry enumeration |
| D8 | VA row `:338` cites the UMD build; Vite bundles the ESM build, which has the same content at `:154–159` / `:266–271` | dropped — not load-bearing |

## 1. Verified-assumptions cross-check

All 30 listed items are reconfirmed under a fresh read of the cited evidence:

- `-X theirs` leaves exactly 1 conflict — `[totality]` reproduced; `git status` lists every path, and the only unmerged one is `UD …/blueprints-configmap-service-clients.yaml`.
- Console endpoint registrations survive — `[presence]` merged `Program.cs:642,647`.
- `-X theirs` does not build — 12 errors, with the stated shape and files (S6).
- `ProjectField` moved — `[presence]` `SchemaCatalogReader.cs:171`.
- `BuildCatalog` receives `ClaimsPrincipal? actingUser` — `[presence]` `:44–48`.
- `BuildCatalog` has no tenant filter — `[negative]` `grep -rn OwnerTenantId` across the non-test merged tree, `OwnerTenantId` appears only at `SchemaDescriptor.cs:93`, `SchemaRegistrationOrchestrator.cs:79,351` and `ObjectMappingGrpcService.cs:86`.
- `OwnerTenantId` arrives from main — `[presence]` `main:SchemaDescriptor.cs:93`.
- One fix covers both catalog consumers, and only the catalog — `[presence]` `AdminConsoleEndpoints.cs:118`; the scope limit is confirmed (a4).
- Main's `/health` is self-consistent — `[compat]` `main:Iverson.Api/ReadinessPolicy.cs` exists, and the verbatim handler compiles (S7).
- `HealthCheckWireFormat` is branch-only — S13.
- Main's CSP blocks the silent-renew iframe — `[presence]` `main:nginx.conf:35`: `default-src 'self'`, no `frame-src`. The merged `nginx.conf` is byte-identical to main's.
- Main's CSP blocks console data fetches — `[presence]` the same line, `connect-src 'self' ${OIDC_ORIGIN}`, has no admin-api origin.
- A failed renewal does not clear the user — `[presence]` the reducer's `ERROR` case (esm `react-oidc-context.js:47–55`) preserves `user`.
- `removeUser()` reaches the redirect — `[presence]` `oidc-client-ts.js:2971–2976` → `:2737–2740`, then `react-oidc-context.js:238–239` `dispatch USER_UNLOADED` → reducer `:29–33` → `AuthGate` `:56–60`.
- `addAccessTokenExpired` is reachable — a5.
- `user.expires_at` is available — `[presence]` `oidc-client-ts.d.ts:1259` `expires_at?: number`, `:1266` `get expires_in()`.
- Main's admin-ui deployment supplies every env var — `[presence]` `main:charts/admin-ui/templates/deployment.yaml` `:56, :61, :63, :65, :67`.
- Main already marks and drift-guards `/build` — a6.
- `networkPolicy.clusterCidrs` is branch-only — a1.
- `global.prometheusEnabled` is branch-only — `[negative]` `git grep -c prometheusEnabled main -- Iverson.Server/deploy` → 0, and `git diff main -- Chart.yaml` on the merge is exactly the branch's `condition: global.prometheusEnabled` line (main has `prometheus.enabled`); branch lines per a2.
- `/admin/console/data-volume` enumerates outside `BuildCatalog` — `[presence]` `:179`; `grep -rn OwnerTenantId Iverson.Api/Search/` → 0.
- `CountRowsAsync` can filter without a signature change — `[presence]` `:50`, `Denied` at `:128`.
- Main's AuthProvider has no `onSigninCallback` — `[negative]` `grep -c` on `main:AuthProvider.tsx` = 0.
- Main deleted `/probe/*` — `[negative]` `git show main:…/Program.cs | grep -n 'probe/'`: the only hit is the comment at `main:Program.cs:112`.
- Main has `RequireListenerPort` test coverage — `[presence]` `main:AuthenticationPipelineTests.cs:185, 209, 213`.
- `TenantStatusCache`'s prefix survives and closes the collision — `[presence]` merged `:16` `KeyFor` = `tenant-status:{tenantId}`.
- Main's `starrocks` domain — S21.
- The widget `authPending` tests are hand-fed — `[presence]` `HealthStrip.test.tsx:118,133`.
- The AdminUI CI job is branch-unique — `[negative]` `git ls-tree --name-only main .github/workflows/` has no admin workflow.
- Branch work is well-isolated — `[totality]` `comm -12` over the full name lists: 113 branch-changed files at `f01f03a7` and 448 main-changed give 28 contested, which leaves 85.

**Span check — design dependencies that no listed assumption covers:**

1. *That the branch's CORS **middleware** survives the merge.* The VA table covers endpoint registrations only, and r1 A6 checked `AddCors`. Verified in-round: it does not survive → **§2.1**.
2. *That the header the browser enforces on the console document is the pod's CSP on every console profile.* No row covers this. Verified in-round: false on the `nginx` class → **§2.2**.
3. *That every route the console calls answers on the listener the admin-api Ingress targets.* No row covers this. Verified in-round: `/v1/traces` does not → **§3.1**.
4. *That R4's restored helper runs against the test file's surviving mock.* No row covers this, and r1's table asserted it. Verified in-round: it does not → **§2.3**.
5. *That Authentik defaults `include_claims_in_id_token` to `True`* (the spec's "carried over, not re-verified" item, and the safety premise for dropping the pin). Verified in-round: `[presence]` `docker run --rm --entrypoint sh ghcr.io/goauthentik/server:2026.5.3 -c 'grep -n -A3 "include_claims_in_id_token = " /authentik/providers/oauth2/models.py'` → `:250–251` `include_claims_in_id_token = models.BooleanField(default=True, …)`. 2026.5.3 is the tag main pins (`charts/authentik/values.yaml:1`, `docker-compose.yml:331,349,392`). Holds; no finding.
6. *That the console's access token is accepted by the merged API.* Verified in-round (A10). Holds.

## 2. Literal-wrongness findings

### 2.1 — `-X theirs` drops the branch's `app.UseCors(...)`, and R3's "one unit" chain never restores it, so every cross-origin console request fails

**Description.** The branch's CORS has two halves in `Program.cs`. The first is the policy registration (`AddCors`). The second is the middleware call, `if (!string.IsNullOrEmpty(adminConsoleOrigin)) app.UseCors(AdminConsoleCorsPolicy);`, which the branch placed between `UseHttpsRedirection` and `UseAuthentication` (`b60e4928:Program.cs:421–422`). Main rewrote that middleware region: it added `app.Use(ListenerPortGateAsync)` and a pre-auth `UseRateLimiter`. So the hunk conflicts, and `-X theirs` takes main's side. The registration survives (merged `:165–177`), but the middleware call is gone, and nothing in the merged tree calls `UseCors`. Without it, no response carries `Access-Control-Allow-Origin`, and a preflight falls through to the FallbackPolicy and is answered 401.

The console fetches cross-origin on every split-origin profile (k8s `admin-api.<host>`, and `localhost:5173` → `:8081` in dev), so it loads nothing. R3 presents itself as the complete chain ("one unit; partial re-application is worthless … The console cannot load data without all of it") and does not name this link. R2 does not delete the branch's `AdminConsoleCorsPipelineTests`, and those tests go red, so the Gates clause also cannot be met as written.

**Evidence.**
- `git show b60e4928:…/Program.cs | grep -n UseCors` → `:418` (comment) and `:422` `app.UseCors(AdminConsoleCorsPolicy);`. The same grep on `main` and on `9eb99f76` returns nothing. On the merged tree, `grep -rn 'UseCors\|RequireCors' Iverson.Server/Iverson.Api/` also returns nothing.
- The merged middleware order is `:487` `app.Use(ListenerPortGateAsync);`, `:488` `app.UseRateLimiter(preAuthOptions);`, `:489` `app.UseHttpsRedirection();`, `:490` `app.UseAuthentication();`, with no CORS middleware anywhere.
- Run on the merged tree with R1+R2 applied: `dotnet test --filter AdminConsoleCorsPipelineTests` → **Failed 4, Passed 2**. `ConfiguredOrigin_CrossOriginGet_HealthLive_ReturnsOkWithMatchingAllowOrigin`: `Access-Control-Allow-Origin` not present. `ConfiguredOrigin_PreflightOptions_AdminDlq_AnsweredByCorsNotFallbackPolicy`: status 401. `…ExposesTraceIdHeader` and `…CarriesNoAllowCredentialsHeader` fail the same way.
- M1 closes the population: across all 18 conflicted files, this is the only dropped branch code line that no Phase 1 / R1–R7 item dispositions.

**Proposed fix.** Add a bullet to R3: re-add the branch's `if (!string.IsNullOrEmpty(adminConsoleOrigin)) app.UseCors(AdminConsoleCorsPolicy);` to `Program.cs` between main's `app.UseHttpsRedirection();` (merged `:489`) and `app.UseAuthentication();` (merged `:490`). It must come before authentication, because a CORS preflight carries no `Authorization` header. It is an insertion into main's pipeline, like `MapAdminConsoleEndpoints()`, and not a change to a main decision, since main has no CORS at all. Name `AdminConsoleCorsPipelineTests` in the Gates as this link's guard.

`Evidence:` `[compat]` On the same R1+R2-applied merged tree, with the line inserted at exactly that position, `AdminConsoleCorsPipelineTests` passes 6/6 (it was 4 failed / 2 passed without it). With R1's tenant carry also applied, the full `Iverson.Api.Tests` suite passes 1167/1167 (M5). `[negative]` `git show main:Iverson.Server/Iverson.Api/Program.cs | grep -c 'UseCors\|AddCors'` → 0, so the insertion overrides no main CORS decision. `[presence]` The insertion point sits after main's `ListenerPortGateAsync`, which passes unmarked endpoints on any port (`main:AuthenticationPipelineTests.cs` `ListenerPortGate_UnmarkedEndpoint_InvokesNext`, and the M3 run).

### 2.2 — Main's admin-ui Ingress replaces the console's CSP on the `nginx` class, and R3 adds the admin-api origin only to the pod's CSP

**Description.** R3 puts the admin-api origin into the CSP `connect-src` "in `nginx.conf`, `docker-entrypoint.sh`", which is the pod's header. Main independently added a second CSP source for the console document (`8ef549bd`, `10bf1e0d`). `charts/admin-ui/templates/ingress.yaml` mirrors the security-header block at the Ingress layer, and for `ingress.className: nginx` it emits a `configuration-snippet` with `more_set_headers "Content-Security-Policy: … connect-src 'self' {{ $oidcOrigin }}; …"` (`main:…/charts/admin-ui/templates/ingress.yaml:37–42`). `more_set_headers` **replaces** the upstream header, as main's own template comment says (`:6–7`).

The branch never touched this file (`git diff --stat 9eb99f76 b60e4928 -- …/charts/admin-ui/` is empty), so it survives the merge as main's. On `values-local`, the profile whose `adminUi.ingress.className` is `nginx` (M2), the browser therefore enforces `connect-src 'self' http://authentik.iverson.local`, whatever R3 writes into the pod. Every console fetch to `http://admin-api.iverson.local` is blocked there. R3's targeted proof ("the admin-api origin is present in `connect-src` as a whole token") checks the pod-side CSP, so it passes while the browser still blocks.

**Evidence.**
- Merged `helm template iverson . -f values-local.yaml` (after `helm dependency update`) contains, under `# Source: iverson/charts/adminUi/templates/ingress.yaml` (rendered line 3493), `more_set_headers "Content-Security-Policy: default-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self' http://authentik.iverson.local; …"`.
- `main:Iverson.Server/deploy/kind/setup.sh:94` installs ingress-nginx with `--set controller.allowSnippetAnnotations=true`, so the snippet is live on the kind profile.
- Replacement probed at the nginx mechanism: an openresty container (the same `headers-more` module) ran an upstream `location` that sends `Content-Security-Policy: default-src 'self'; connect-src 'self' http://admin-api.iverson.local http://authentik.iverson.local`, standing in for the pod after R3. In front of it sat a proxy `location` carrying the rendered snippet line verbatim. Direct to upstream, the CSP names admin-api. Through the snippet there is **exactly 1** CSP header, and it contains admin-api **0** times. The same probe config, run on the **ingress-nginx controller's own nginx binary** (`registry.k8s.io/ingress-nginx/controller:v1.12.8`, built with `--add-module=…/headers-more-nginx-module`), gives the same result: 1 CSP header, 0 admin-api occurrences.
- Snippet placement, read from the version kind installs (`main:…/deploy/kind/setup.sh` pins chart `4.12.8` = controller `v1.12.8`). The controller's `/etc/nginx/template/nginx.tmpl:1107` opens `location {{ $path }} {`, and `:1327` emits `{{ $location.ConfigurationSnippet }}` inside that block (it closes at `:1409`). So the annotation lands in each `location` of the admin-ui Ingress's path, which is where the probe placed it.

**Proposed fix.** In R3's CSP bullet, name the third site. Main's `charts/admin-ui/templates/ingress.yaml:42` nginx-class `Content-Security-Policy` must carry the same admin-api origin as the pod, for example `connect-src 'self' {{ printf "%s://admin-api.%s" .Values.global.externalScheme .Values.global.ingressHost }} {{ $oidcOrigin }}`. That is the host formula `charts/api/templates/admin-api-ingress.yaml:34` already uses. This keeps main's decision (the Ingress mirrors the pod's header block) rather than overriding it; list it next to R3's `nginx.conf` edit as a place a main line necessarily changes. Extend R3's targeted CSP proof to assert the origin as a whole `connect-src` token in the rendered `values-local` admin-ui Ingress snippet, not only in the pod's CSP.

`Evidence:` `[compat]` With that edit and `helm dependency update`, `helm template -f values-local.yaml` renders `connect-src 'self' http://admin-api.iverson.local http://authentik.iverson.local`. Fed the patched rendered line, both the openresty probe and the ingress-nginx `v1.12.8` controller's nginx binary return exactly 1 CSP header whose `connect-src` carries `http://admin-api.iverson.local` (admin-api count 1, against 0 unpatched). `values-aws/azure/gcp` still render (exit 0, with `clusterCidrs` supplied). `[presence]` `admin-api.<global.ingressHost>` equals `admin-api-ingress.yaml:34`'s rule host and the branch's `values-local` `apiBaseUrl` `http://admin-api.iverson.local`, which is the origin the pod's entrypoint derives. Without the patch, the rendered line lacks it.

### 2.3 — R4's "restore `renderProviderAndCaptureSettings`" reinstates a helper that reads a variable main's surviving mock never writes

**Description.** R4 says to "Restore only `renderProviderAndCaptureSettings` and `onSigninCallback`". The branch's helper resets and returns `capturedProviderProps`, and only the **branch's** `vi.mock("react-oidc-context", …)` ever assigned that variable. The branch mock lost the conflict, so the merged file carries **main's** mock (`:10–16`), which pushes into `capturedOidcProps` instead. The `let capturedProviderProps` declaration survived at merged `:8`, so the restored helper compiles. At runtime it hits its own guard and throws `AuthProvider did not render the OIDC provider`.

The kept test at `:148` ("wires a signin callback that strips the authorization code out of the URL") therefore fails. The Gates clause permits deleting only the tests R2 and R4 enumerate, and `:148` is not among them. So the disposition list, followed as written, leaves the AdminUI suite red. Round 1's table recorded `:148` as "ok — R4's re-add makes both pass". That was never run.

**Evidence.** Probe on the merged tree. I applied R4's list: deleted the `:105/:132/:139` tests, the `:4` import and the `useTokenRenewal()` call. I re-added `onSigninCallback` to `AuthProvider.tsx` and its `oidcConfig`, added `onSigninCallback` to the test's import, and restored the helper in its branch form (`git show b60e4928:…/AuthProvider.test.tsx:23–34`). `npx vitest run src/auth/AuthProvider.test.tsx` → **Tests 1 failed | 6 passed (7)**. The failure is `Error: AuthProvider did not render the OIDC provider` in "wires a signin callback…". The merged `:10–16` mock is main's, and it writes only `capturedOidcProps.push(props)` (`:13`).

**Proposed fix.** Replace "Restore only `renderProviderAndCaptureSettings` and `onSigninCallback`" with the following. Restore `renderProviderAndCaptureSettings` rewritten against main's surviving mock: reset `capturedOidcProps.length = 0`, render, and return `capturedOidcProps[0]`. Delete the orphaned `let capturedProviderProps` (merged `:8`). Add `onSigninCallback` to main's import at merged `:18`. Main's `vi.mock` stays as it is.

`Evidence:` `[compat]` Same probe tree, with the helper rewritten exactly that way and `capturedProviderProps` removed: `AuthProvider.test.tsx` passes 7/7. The full AdminUI `npx vitest run` then gives 22 files / 228 tests, all passing. I then applied the rest of R4's list: `git rm` of `useTokenRenewal.{ts,test.ts}`; deletion of `client.ts`'s renewal section and its `:214` call; removal of `client.test.ts`'s renewal import, its `afterEach` reset, the renewer lines in the 403 test, the three renewal `it` blocks and the `setTokenRenewer` `describe`. The full suite then gives 21 files / 221 tests, all passing. `[negative]` After that, `grep -rn 'setTokenRenewer\|requestTokenRenewal\|renewalInFlight\|useTokenRenewal' src` hits only doc comments (`AuthProvider.tsx:54`, `client.ts:33`; D5).

## 3. Forced decisions

### 3.1 — Console span export: main pins `/v1/traces` to listener 8080, and the admin-api Ingress sends it to 8081

**The choice.** Either accept that the console's browser traces stop reaching Jaeger after the merge, or re-route them additively, in branch-owned files, to a listener where main serves `/v1/traces`.

**Why it's forced.**
- Main's `0baebc4b` marks `/v1/traces` with `RequireListenerPort(8080)` (merged `Program.cs:757`, `main:Program.cs:697`).
- The branch's admin-api Ingress routes `/v1/traces` to Service port 8081 (`charts/api/templates/admin-api-ingress.yaml:51–57`; rendered azure manifest: `path: /v1/traces` → `number: 8081`). That port targets container 8081, which is Kestrel's `Http` listener (`service.yaml:13–14`, `appsettings.json` `"Http": { "Url": "http://*:8081", "Protocols": "Http1" }`).
- After R3 sets `apiBaseUrl` to the admin-api origin, the console exports to `absoluteUrl("/v1/traces")` (`telemetry.ts:27`), and the dev build exports to `http://localhost:8081` (`.env.development:3`). Main's `ListenerPortGateAsync` answers 404 to every such request (M3, run-tier).
- The resolution policy forbids touching main's marker (Phase 1 already states "`RequireListenerPort` (over the branch's `HttpListenerOnly`)"). So the branch's route cannot survive as-is.
- The spec is silent on this. As written, R3 publishes a dead route, and console tracing works on no profile: the branch's split-origin route 404s, and R3's `apiBaseUrl` moves exports off main's same-origin route.
- The failure is silent in every gate. `WebApplicationFactory`'s `LocalPort` is 0, which the gate deliberately passes (`main:AuthenticationPipelineTests.cs` `ListenerPortGate_LocalPortZero_InvokesNext`). The exporter's failures are not surfaced either (`telemetry.ts:22–25`: "every span export would 404 and tracing would be silently dead").

**Options.**

- **A — Accept the loss, and record it.** Leave main's pin untouched. Add a fifth follow-up against `main`: serve `/v1/traces` on the listener the admin-api Ingress targets. Optionally drop the dead `/v1/traces` rule from the branch-owned `admin-api-ingress.yaml:51–57`. Console span export is dead until that follow-up lands.
  `Evidence:` `[compat]` The M3 probe used the real `/v1/traces` endpoint metadata from the built app and main's real `ListenerPortGateAsync` at `LocalPort = 8081`: 404, `next` not invoked, marker 8080. `[negative]` A changes no main-owned line: it adds a follow-up entry and optionally deletes from a branch-only template. `admin-api-ingress.yaml` is absent at `9eb99f76` and on `main` (`git cat-file -e` fails on both).

- **B — Re-route additively to main's own 8080 route.** Point the console's OTLP exporter back at the same-origin path main used (`main:Iverson.AdminUI/src/telemetry.ts:19`, `OTLP_TRACES_URL = "/v1/traces"`), served by main's api Ingress on `global.ingressHost` (`main:…/charts/api/templates/ingress.yaml:95–101`, `/v1/traces` → 8080). Drop the `/v1/traces` rule from the admin-api Ingress. This touches only `telemetry.ts` (a branch-modified file) and a branch-only template.
  `Evidence:` `[presence]` main's api Ingress routes `/v1/traces` → 8080 (read at `:95–101`), and main's `telemetry.ts:19` uses the relative path. UNVERIFIED: that this route actually delivers a browser HTTP/1.1 `POST` to Kestrel's `Protocols: Http2` listener (`main:appsettings.json:11–13`). Main's `values-local` `api.ingress.annotations` is `{}` (no backend-protocol annotation), and `values-aws` sets `alb.ingress.kubernetes.io/backend-protocol-version: GRPC`; this was not probed in-round. `[absence]` The merged `Iverson.AdminUI/vite.config.ts` is 7 lines with no `server`/`proxy` block, so under `npm run dev` a relative `/v1/traces` is served by Vite with no route to the API (as `telemetry.ts:22–25` says). B therefore also gives up dev-mode tracing.

**Why no combination or unlisted variant dominates.**
- C, re-marking `/v1/traces` for 8081 or unmarking it, overrides main's `0baebc4b` decision. The policy excludes it, and it is exactly the follow-up A would file.
- A+B (re-route now and file the follow-up) dominates A only if B's transport works, and that is unverified in-round. On `values-local`, main's route carries no HTTP/2 backend annotation at all.
- A dedicated additive Ingress for `admin-api.<host>/v1/traces` → 8080, with a per-controller HTTP/2 backend-protocol annotation, is a variant of B. It carries the same unverified transport claim and adds a new object per controller class.

## 4. Previously addressed

- r1 §2.1 (`clusterCidrs` stranded, so `helm template` fails on three profiles) is resolved by R3's `clusterCidrs` bullet and the rewording of the Helm gate (a1).
- r1 §2.2 (`global.prometheusEnabled` stranded, so the metrics widget reports `notDeployed`) is resolved by R3's `prometheusEnabled` bullet (a2).
- r1 §2.3 (AuthProvider test fallout under-enumerated; Gates clause unsatisfiable) is resolved by R4's explicit disposition list and the narrowed Gates clause (a3). The list's one defective entry is new finding §2.3, not a residue of the original under-enumeration.
- r1 §2.4 (`/admin/console/data-volume` outside the tenant filter) is resolved by R1's second-surface paragraph, the two new VA rows and the third proof arm (a4).
- r1 §1 failed VA row (`addAccessTokenExpired` "unreachable") is resolved: R5 now uses `useAuth().events.addAccessTokenExpired`, and the VA row is corrected (a5).
- r1 R-h (the follow-up-1 `/build` premise was false on main) is resolved: the follow-up was removed and replaced by VA row `:341` (a6).

Round 1's other dropped rows (S1, S19, S22, R-c, R-i) remain as the spec has them and are not re-raised.

## 5. Recommendation

🛑 **Surface forced decisions to user.** §3 has 1 item (§3.1, console span export under main's `/v1/traces` 8080 pin), and §2 has 3 items.

The three §2 items are enumeration gaps in the split-origin chain and its test disposition, and each has a probed one-site fix:
- §2.1: the `UseCors` middleware the merge drops.
- §2.2: main's Ingress-layer CSP on the `nginx` class.
- §2.3: R4's helper restored against the wrong mock.

§2.1 and §2.2 each independently stop the console loading any data: §2.1 on every split-origin profile, §2.2 on `values-local`. Both are invisible to the spec's verification plan as written.
