# Admin Console × main Merge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-22-admin-console-main-merge-resolution-design.md` (commit SHA: `113106d7`)

**Goal:** Merge `main` (pinned at `65cdf63a`) into `admin-console-landing-page` with every competing decision resolved in main's favor, then repair the branch's admin-console feature so it works on main's foundations.

**Architecture:** One merge commit takes main's side of every conflict (`-X theirs`) plus only what compiling requires, so the commit is auditable as "main wins". A series of repair commits then re-applies the branch's split-origin chain additively, carries main's cross-tenant schema fix into the branch's registry readers, removes branch code main superseded, handles session expiry under main's no-refresh-token auth, and adapts the branch's CI job — each deviation from main its own reviewable commit.

**Tech stack:** .NET 10 (`Iverson.slnx`, xUnit, FluentAssertions, NSubstitute); React/TypeScript (`Iverson.AdminUI`, vite, vitest 5.0.0, `react-oidc-context` 3.3.1, `oidc-client-ts` 3.5.0); Helm 3.16 umbrella chart `Iverson.Server/deploy/helm/iverson`; docker-compose; GitHub Actions.

---

## Global Constraints

From the spec's resolution policy (decided by the user), verbatim:

- Where main and the branch made competing decisions about the same question, **main's decision wins**, untouched.
- The branch's unique feature work is preserved and re-applied **additively** on top of main's foundations.
- Where main's version carries a defect the branch already fixed, **main's version ships with the defect** and the fix becomes a separate follow-up PR against `main`.
- **The checkable property is: no main decision is overridden.**

Plan-wide rules:

- Merge **exactly** `65cdf63a`, never the moving `main` ref — every spec and plan assumption was verified against that SHA.
- **Never push, and never merge this branch into `main`.** Integration is the user's decision after the branch is done.
- The merge commit must **compile** (`dotnet build Iverson.slnx` and `npm run build` succeed). Test suites may be red at the merge commit; every suite must be green at the branch tip.
- Commit subjects are lowercase imperative with no type prefix (the repo's dominant convention; `git log --no-merges`), with a short body saying why. Append the executing session's `Co-Authored-By` trailer to every message shown in this plan. Stage files by name — never `git add -A`.
- `docs/specs/`, `docs/plans/` and `docs/criticalreviews/` are gitignored — use `git add -f`. `docs/runbooks/` is tracked normally.
- Run `helm dependency update` before **every** `helm template` or `helm lint`: packaged `.tgz` subcharts otherwise render stale.
- A test that was deleted to make a suite pass is a defect. The only permitted deletions are the ones this plan enumerates, each with its reason.

## File Structure

**Task 1 (merge commit)**
- Delete: `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml` — main renamed it to `secret-service-clients.yaml`.
- Delete: `Iverson.Server/Iverson.Api/HealthCheckCache.cs`, `Iverson.Server/Iverson.Api.Tests/HealthCheckCacheTests.cs` — superseded by main's inline `/health` cache; the tests no longer compile against main's interfaces.
- Delete: `Iverson.Server/Iverson.Api.Tests/OperationalListenerBindingPipelineTests.cs` — references the removed `HttpListenerOnly`.
- Modify: `Iverson.Server/Iverson.Api/Program.cs` — `/health` restored verbatim from main; `AddSingleton<HealthCheckCache>()` removed.
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` — `GetSchema` restored to the branch tip's body.
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml` — main's file plus only the bypass user's `operators` grant.

**Task 2** — Modify `Iverson.Api/Schema/SchemaCatalogReader.cs`, `Iverson.Api/Search/AggregateReader.cs`; Create `Iverson.Api.Tests/AdminConsoleSchemaEndpointTests.cs`; Modify `Iverson.Api.Tests/AdminConsoleDataVolumeEndpointTests.cs`.

**Task 3** — Delete `Iverson.Api/HealthCheckWireFormat.cs`, `Iverson.Api.Tests/HealthCheckWireFormatTests.cs`, `Iverson.Api.Tests/ProbeAuthorizationPipelineTests.cs`; Modify `Iverson.Api/Tenancy/TenantStatusCache.cs` (comment).

**Task 4** — Modify `Iverson.Server/Iverson.Vector.Tests/ServiceCollectionExtensionsTests.cs:44`.

**Task 5** — Modify `Iverson.Api.Tests/AuthenticationPipelineTests.cs` (one new test); Modify `Iverson.AdminUI/src/widgets/HealthStrip.test.tsx` (titles and comments).

**Task 6** — Modify `Iverson.Server/Iverson.Api/Program.cs` (`UseCors`).

**Task 7** — Modify `Iverson.AdminUI/nginx.conf`, `Iverson.AdminUI/docker-entrypoint.sh`.

**Task 8** — Modify `values.yaml`, `values-aws.yaml`, `values-azure.yaml`, `values-gcp.yaml`, `values-local.yaml` (under `Iverson.Server/deploy/helm/iverson/`), `charts/admin-ui/templates/ingress.yaml`, `charts/api/templates/admin-api-ingress.yaml`, `charts/api/values.yaml`, and `Iverson.Server/docker-compose.yml`.

**Task 9** — Modify `Iverson.AdminUI/src/auth/AuthProvider.tsx`, `src/auth/AuthProvider.test.tsx`, `src/api/client.ts`, `src/api/client.test.ts`; Delete `src/api/useTokenRenewal.ts`, `src/api/useTokenRenewal.test.ts`.

**Task 10** — Create `Iverson.AdminUI/src/auth/useSessionExpiry.ts`, `src/auth/useSessionExpiry.test.tsx`; Modify `src/auth/AuthProvider.tsx`, `src/auth/AuthProvider.test.tsx`.

**Task 11** — Modify `Iverson.AdminUI/README.md`, `Iverson.AdminUI/docker-entrypoint.sh`, `docs/runbooks/admin-console-landing-page-usage.md`, `docs/runbooks/operator-access-onboarding.md`.

**Task 12** — Modify `.github/workflows/admin-ui.yml`.

**Task 13** — no file changes; the final gates.

## Inherited from spec

The following assumptions were verified by `thorough-brainstorming` and four `critical-design-review` rounds at spec-write time and are NOT re-verified here. Trusted as ground truth (verbatim from the spec's `Verified assumptions` section):

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
| R1's splices alone do not compile `Iverson.Api.Tests` | Measured on the resolved merge: `dotnet build Iverson.slnx` gives 4 errors in `HealthCheckCacheTests.cs`, then 1 in `OperationalListenerBindingPipelineTests.cs` (`HttpListenerOnly` not found); 0 errors once both, plus `HealthCheckCache.cs` and its `AddSingleton`, are removed |
| The merged `package.json` declares `engines.node >=22.22.0` | `git merge 65cdf63a -X theirs` onto `e4028710`: `engines` = `{"node":">=22.22.0"}`; `main:Iverson.AdminUI/package.json` has no `engines` |
| The merged README and `.env.development` already say `8081` | merged `Iverson.AdminUI/README.md:23,55` and `.env.development:3` read `8081`; base `9eb99f76` and main `65cdf63a` both read `8080` on those README lines, so the branch's change merges cleanly and is not a main-line change; `docker-compose.yml` publishes `127.0.0.1:8081:8081` |
| The AdminUI CI job starts main's entrypoint without main's env contract | `admin-ui.yml:156-159` passes only `OIDC_CLIENT_ID`, `OIDC_AUTHORITY`, `API_BASE_URL`; `main:Iverson.AdminUI/docker-entrypoint.sh:2` `set -eu` and `:16` reads `$EXTERNAL_SCHEME`; the job asserts `frame-src` at `:210`, which `main:nginx.conf` omits; `BASELINE=7` at `:100`, compared with `-gt` at `:109` |
| Main's placeholder guard needs the CI overrides on cloud profiles | `main:templates/networkpolicies.yaml:1` includes `iverson.validateNoPlaceholders` (`_validate.tpl`, `https` profiles only); `values-{aws,azure,gcp}.ci-override.yaml` count 0 at `9eb99f76` and on the branch, 3 on main; `main:deploy-validate.yml:64-73` layers them |
| Four branch hunks auto-merge into the compose blueprint | branch `compose-only/service-clients.yaml`: the pin; `offline_access` `!Find` under `iverson-oidc-default` (`:188`) and `iverson-loadtest-human` (`:296`); the `operators` grant (`:335`). Main's compose file has 0 `offline_access` and 0 `operators`; main's Helm `iverson-loadtest-human` maps only `groups` and `tenant_id` (`secret-service-clients.yaml:485-486`); main's LoadTest requests `offline_access` through it (`Iverson.Server/Iverson.LoadTest/Auth/AuthentikFlowExecutorClient.cs:281`) |
| Main replaced the static compose passwords with generated ones | `git grep` for the two static strings over the pure merge tree `06b78f70` finds exactly four sites, all in branch-only runbooks: `admin-console-landing-page-usage.md:67,70,79` and `operator-access-onboarding.md:21`; main's README `:28` names the generated `AUTHENTIK_BOOTSTRAP_PASSWORD` |

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time. "Merged tree" means `git merge 65cdf63a -X theirs` onto the branch tip plus `git rm` of the ConfigMap blueprint, reproduced in a throwaway worktree.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| A1 | Command | `dotnet build Iverson.slnx` builds Api, Api.Tests, Vector and Vector.Tests | `Iverson.slnx` lists both test projects; `main:.github/workflows/dotnet-build.yml:19-20` builds and tests `Iverson.slnx` |
| C4 | Command | `git merge 65cdf63a --no-commit -X theirs` then one `git rm` leaves nothing unmerged | Merged tree: 1 `UD` (the ConfigMap blueprint) after `-X theirs`, 0 after `git rm` |
| D1 | Ordering | Task 1 compiles only if it also removes three of R2's files | Merged tree with R1 splices resolved: `dotnet build Iverson.slnx` → 4 errors in `HealthCheckCacheTests.cs` (fakes miss `IRecordStoreQueryExecutor`'s new parameter and `IVectorSchemaManager.PingAsync`); then 1 error in `OperationalListenerBindingPipelineTests.cs:135` (`HttpListenerOnly` not found); 0 errors with both removed |
| F5 | Consumer | Nothing but that test references `HttpListenerOnly` | `grep -rn HttpListenerOnly --include=*.cs Iverson.Server/` on the merged tree → only `OperationalListenerBindingPipelineTests.cs` |
| F4 | Consumer | Nothing else resolves `HealthCheckCache` | The solution builds with 0 errors after removing the class, its tests and its `AddSingleton` |
| — | Measurement | Test outcome on Task 1's tree | `Iverson.Api.Tests`: 1,186 run, 8 failed — `ObjectMappingGrpcServiceTests.GetSchema_OwnerTenantIdSet_ExcludesOtherTenantsCatalog_ButIncludesUnscopedTypes`, 4 × `AdminConsoleCorsPipelineTests`, 3 × `ProbeAuthorizationPipelineTests`. `Iverson.Vector.Tests`: 170 run, 1 failed — `ServiceCollectionExtensionsTests.AddQdrant_RegistersResolvableVectorCollectionReader` |
| A2 | Path | The R1 gRPC proof arm already exists and fails first | `Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs:596` `GetSchema_OwnerTenantIdSet_…` fails on Task 1's tree (measured) |
| B1 | Signature | `BuildCatalog` iterates `registry.All.Values`, calling `Evaluate` first | `SchemaCatalogReader.cs:60-63` |
| B2 | Signature | `CountRowsAsync` resolves the schema before `Evaluate` | `AggregateReader.cs:52-58`: `registry.Get(typeName)` → `UnknownType` if null → `Evaluate` |
| B3 | Signature | Tests construct owned schemas with `with { TypeName, OwnerTenantId }` and `RegisterAsync` | `ObjectMappingGrpcServiceTests.cs:604-615`; `SchemaRegistry.cs:238` `public async Task RegisterAsync(SchemaDescriptor)` |
| B4 | Signature | A tenant-bearing reader principal exists | `AdminConsoleDataVolumeEndpointTests.cs:50-57` `ReaderContext()` carries `tenant_id: tenant_alpha` and group `console-readers` |
| B5 | Path | Both REST handlers can be driven directly, with a fresh registry per test | `AdminConsoleEndpoints.cs:108` `GetSchema(HttpContext, SchemaRegistry, SchemaCatalogReader)`; `:169` `GetDataVolumeAsync`; `SchemaCatalogReader(SchemaRegistry, IRowFieldAuthorizationEvaluator, ILogger<SchemaCatalogReader>)`; `AdminConsoleSchemaRegistryRepository.UpsertAsync` is a no-op, so `RegisterAsync` works; `AdminConsoleSchemaRegistryRepository.Article()` is readable by `console-readers` |
| F1/F2 | Consumer | Adding the ownership filter breaks no existing expectation | Seeded console fixtures set no `OwnerTenantId` (0 occurrences), so the filter is a no-op for them; the only production callers are `ObjectMappingGrpcService.GetSchema`, `AdminConsoleEndpoints.cs:118` and `:186`; no test calls either method directly |
| — | Design | The two REST arms go at handler level, not in the shared pipeline fixture | `AdminConsoleEndpointsPipelineTests` shares one `IClassFixture` factory whose tests assert exact counts (`typeCount 2`, `withheldTypeCount 1`, the operator's withheld/denied totals); a foreign-owned fixture there would shift them |
| A3 | Path | R2's deletion targets exist at the cited paths | Deleted and rebuilt in the merged tree |
| A4 | Path | `TenantStatusCache.cs:12-15` comment names the deleted `HealthCheckCache.CacheKey` | Merged-tree read |
| A5 | Path | The Vector failure is a branch fixture shorter than main's 32-byte minimum | `ServiceCollectionExtensionsTests.cs:44` `apiKey: "test-api-key"`; `Iverson.Vector/ServiceCollectionExtensions.cs:27` rejects `< 32` bytes; main's own tests in the file use `"test-signing-key-0123456789abcdef"` (`:18`, `:56`, `:74`) |
| A6 | Path | Main's `/health` harness can host the R7 tripwire | `AuthenticationPipelineTests.cs:293-318` nested `HealthCacheTestFactory` substitutes all four `/health` dependencies; `:321-345` shows the call pattern |
| B9 | Signature | `EngagementHealthStatus.AuthPending` exists and engagement is on in that host | `Iverson.StarRocks/EngagementHealthStatus.cs:10`; `EngagementStoreOptions.Enabled` defaults to `true` (`EngagementStoreOptions.cs:12`) and no test host overrides it, so main's handler emits `starrocks = (status == Healthy)` → `false` |
| — | Code | `AuthenticationPipelineTests.cs` lacks `using System.Text.Json;` | Merged-tree read of its usings |
| A7 | Path | The four `authPending` widget tests | `HealthStrip.test.tsx:42` (mixed), `:117`, `:128` (authPending-only), `:149` (mixed) |
| A8 | Path | `AdminConsoleCorsPipelineTests` guards `UseCors` | 4 of its tests fail on Task 1's tree (measured) |
| — | Code | The branch's `UseCors` block to restore | `87914794:Iverson.Server/Iverson.Api/Program.cs:414-422`; main's pipeline at `main:Program.cs:436-440`; `adminConsoleOrigin` declared at `:100` in the surviving `AddCors` region |
| A9/E3 | Code | Pod CSP: the merged entrypoint keeps the branch's `origin_of`, uncalled; main's tail renders `nginx.conf` through an explicit SHELL-FORMAT list | Merged `docker-entrypoint.sh:69-75` (`origin_of`), `:106` `envsubst '${OIDC_ORIGIN} ${EXTERNAL_SCHEME} ${HSTS_LINE}'`; merged `nginx.conf:35` `connect-src 'self' ${OIDC_ORIGIN}`. Round 3 built and served exactly this edit (run tier) |
| A10 | Path | Which values keys are actually missing after the merge | Merged tree: `api.adminApiIngress`/`api.adminConsoleOrigin` present in azure (`:87,:95`), gcp (`:88,:96`), local (`:121,:125`); **absent** from `values.yaml` and `values-aws.yaml`. `networkPolicy.clusterCidrs` present in local/laptop, absent in aws/azure/gcp. `global.prometheusEnabled` present in local/laptop, absent in values/aws/azure/gcp. `apiBaseUrl` is main's single-origin value in values/local/aws/azure/gcp |
| — | Code | Branch-tip source ranges for the restored keys | `87914794`: `values.yaml:64-70` (`prometheusEnabled`), `:170-187` (`adminApiIngress: {}`, `adminConsoleOrigin: ""`), `:239` (`apiBaseUrl`); `values-aws.yaml:4-8`, `:14`, `:103-116`, `:186`; `values-azure.yaml:4-9`, `:15`, `:167`; `values-gcp.yaml:5-17`, `:23`, `:175`; `values-local.yaml:164` |
| F7 | Consumer | Templates reading the restored keys exist post-merge | `charts/api/templates/admin-api-ingress.yaml` reads `.Values.adminApiIngress.{annotations,className,tlsSecretName}` and `.Values.adminConsoleOrigin` (render guard); `templates/networkpolicies.yaml:2-3` guard; `Chart.yaml` condition and `charts/api/templates/deployment.yaml:169` guard (spec-verified) |
| E2 | Code | The Ingress snippet's `printf` is valid there | `charts/admin-ui/templates/ingress.yaml:27` already builds `$oidcOrigin` with the same `printf` over `.Values.global.externalScheme`/`.ingressHost` |
| F6 | Consumer | Dropping the admin-api `/v1/traces` rule breaks no test | `TracesRelayEndpointTests.cs` posts to `/v1/traces` through the test host, not the Ingress. Two comments name the route and are updated: `admin-api-ingress.yaml:14`, `charts/api/values.yaml:40` |
| A11 | Path | Compose lost both admin-console env lines | Merged `iverson-api` environment ends at `:498` with neither `AdminConsole__Origin` nor `Prometheus__BaseUrl`; branch tip `87914794:docker-compose.yml:474-484` has both with comments |
| A12 | Path | Post-merge auth file lines | `AuthProvider.tsx:4` import and `:54` `useTokenRenewal()` (the spec cites `:62` from the branch tip); doc paragraph `:46-50`; `AuthProvider.test.tsx:8` orphaned `capturedProviderProps`, `:18` import, tests at `:105`, `:132`, `:139`, `:148`, `:159` |
| F3 | Consumer | Every consumer of the renewal seam is inside R4's deletion set | `grep -rn 'setTokenRenewer\|requestTokenRenewal\|renewalInFlight\|useTokenRenewal' src/`: `AuthProvider.tsx:4,50,54`, `useTokenRenewal.{ts,test.ts}`, `client.ts:132-160,214`, `client.test.ts:5,39,121,145,159,175,245-258`, `AuthProvider.test.tsx:113,124` |
| — | Code | `client.ts` documents the renewal path in three places | `:16` table row, `:28-40` "401 is handled once" section, `:44` "NOT routed to renewal" |
| — | Code | `client.test.ts` renewal-coupled tests | `:119` 403 test (renewer lines), `:143`, `:154`, `:173` (renewal), `:190` (already asserts `401 → unauthorized`), `:245-` `describe("setTokenRenewer")` |
| B6/E4 | Signature | The expiry hook's API | `oidc-client-ts.d.ts:30` `addAccessTokenExpired(cb): () => void`; `react-oidc-context.d.ts` `readonly events: UserManagerEvents`, `removeUser(): Promise<void>`. Main's lockfile pins the same `react-oidc-context` 3.3.1 / `oidc-client-ts` 3.5.0 as the branch |
| F8 | Consumer | An unguarded hook would crash `AuthGate`'s existing tests | `AuthProvider.test.tsx:54-67` (and `:71`, `:88`) mock `useAuth` as `{ isLoading, isAuthenticated, signinRedirect }` — no `events`. `useTokenRenewal` set the precedent guard (`typeof signinSilent !== "function"`) |
| A13 | Path | The hook's files are free | `src/auth/useSessionExpiry.ts` and `.test.tsx` do not exist; `src/auth/` holds `RequireGroup.tsx` and `groups.ts` |
| B7/E5 | Code | AdminUI test idioms | `vi.mock("react-oidc-context", …)` with a `useAuthMock`; `renderHook` from `@testing-library/react` 16.3.2 (used by `useTokenRenewal.test.ts`); vitest 5.0.0 with `fileParallelism: false` |
| C2 | Command | AdminUI commands | `package.json` scripts: `test` = `vitest run`, `build` = `vite build`; `npx tsc --noEmit` for the type count |
| A14 | Path | README and runbook sites | Merged `README.md:23,55` already read `8081` (base and main both `8080`, untouched by main, so the branch's change merges cleanly); main's bootstrap sentence `:27-29`; the seeded-user sentence is absent. `docs/runbooks/admin-console-landing-page-usage.md:67,70,79` and `operator-access-onboarding.md:21` carry the static credentials; `scripts/generate-compose-secrets.sh:12,21` writes `IVERSON_BYPASS_PASSWORD` to `Iverson.Server/.env` |
| — | Code | Entrypoint leftovers | Merged `docker-entrypoint.sh:23-25` declares `CONFIG_TEMPLATE`, `CONFIG_OUTPUT`, `HEADERS_OUTPUT` — 0 uses each; header `:3-6` describes an `admin-ui-security-headers.inc` main's tail never writes |
| — | Code | `values-aws.yaml:142,162` comments need no edit | They name `api.ingress/adminApiIngress`; once Task 8 restores `api.adminApiIngress` on aws they are accurate again |
| A15 | Path | CI lines | `.github/workflows/admin-ui.yml` byte-identical to the branch tip after the merge; `:100` `BASELINE=7`, `:143` `docker build -f Iverson.AdminUI/Dockerfile -t "$IMAGE" .`, `:156-160` `docker run`, `:201` error message names `frame-src`, `:210` `frame-src` assertion; jobs `build-test`, `image-contract`; `image-contract` env `IMAGE`, `CONTAINER`, `API_ORIGIN=http://admin-api.ci.invalid`, `IDP_ORIGIN=http://authentik.ci.invalid` |
| C3 | Command | Helm | `helm version` → `v3.16.4`; the admin-api Ingress renders as `{{ .Release.Name }}-admin-api` (`admin-api-ingress.yaml:23`) |
| C5 | Convention | Commit subjects | Dominant: lowercase imperative, no prefix; the previous merge commit's subject is `merge main into admin-console-landing-page` (`41e01848`) |
| A16 | Path | `docs/plans/` is gitignored | `.gitignore:49` `**/docs/plans/` |
| D2-D6 | Ordering | Task dependencies | Task 2 needs Task 1's tree (measured: tests run there). Task 5 follows Task 3 (the tripwire replaces the deleted `HealthCheckWireFormatTests`). Tasks 9 and 10 both edit `AuthGate`, in that order. Task 11 edits the entrypoint after Task 7 and relies on Task 8 for the `values-aws` comments. Task 12 needs Tasks 7, 9, 10 (the served CSP and the final type count) |

## Tasks

### Task 1: The merge commit — main wins, plus exactly what compiling requires *(spec Phase 1, R1's splices, R2's first and third items)*

**Files:** see File Structure, Task 1.

- [ ] **Step 1: Check the starting state**

```bash
git status --porcelain            # must print nothing
git cat-file -t 65cdf63a          # must print "commit"
```

- [ ] **Step 2: Merge, taking main's side of every conflict**

```bash
git merge 65cdf63a --no-commit -X theirs
git status --short | grep -E '^(UU|UD|DU|AA)'
```

Expected: exactly one line, `UD Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml`.

- [ ] **Step 3: Accept main's deletion of the ConfigMap blueprint**

```bash
git rm Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml
```

- [ ] **Step 4: Restore main's `/health` handler verbatim**

`-X theirs` spliced main's cache prologue and epilogue around the branch's body. Replace the whole handler with main's:

```bash
python3 - <<'PY'
import pathlib, subprocess
p = pathlib.Path("Iverson.Server/Iverson.Api/Program.cs")
def block(lines):
    s = next(i for i, l in enumerate(lines) if l.startswith('app.MapGet("/health", async ('))
    e = next(i for i in range(s, len(lines)) if lines[i].startswith("})"))
    return s, e
main = subprocess.run(["git", "show", "65cdf63a:Iverson.Server/Iverson.Api/Program.cs"],
                      capture_output=True, text=True, check=True).stdout.splitlines(keepends=True)
cur = p.read_text().splitlines(keepends=True)
ms, me = block(main); cs, ce = block(cur)
p.write_text("".join(cur[:cs] + main[ms:me + 1] + cur[ce + 1:]))
PY
sed -i '/builder.Services.AddSingleton<HealthCheckCache>();/d' Iverson.Server/Iverson.Api/Program.cs
```

- [ ] **Step 5: Restore `GetSchema` to the branch tip's body**

The merge left main's two-pass catalog body dead above the branch's call to `SchemaCatalogReader.BuildCatalog`. Task 2 carries main's tenant filter into the reader; here the method is just the branch's:

```bash
python3 - <<'PY'
import pathlib, subprocess
p = pathlib.Path("Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs")
def method(lines):
    s = next(i for i, l in enumerate(lines) if "public override Task<GetSchemaResponse> GetSchema(" in l)
    r = next(i for i in range(s, len(lines)) if "return Task.FromResult(response);" in lines[i])
    assert lines[r + 1].strip() == "}"
    return s, r + 1
tip = subprocess.run(["git", "show", "87914794:Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs"],
                     capture_output=True, text=True, check=True).stdout.splitlines(keepends=True)
cur = p.read_text().splitlines(keepends=True)
ts, te = method(tip); cs, ce = method(cur)
p.write_text("".join(cur[:cs] + tip[ts:te + 1] + cur[ce + 1:]))
PY
```

- [ ] **Step 6: Remove the branch files that no longer compile against main**

```bash
git rm Iverson.Server/Iverson.Api/HealthCheckCache.cs \
       Iverson.Server/Iverson.Api.Tests/HealthCheckCacheTests.cs \
       Iverson.Server/Iverson.Api.Tests/OperationalListenerBindingPipelineTests.cs
```

- [ ] **Step 7: Resolve the compose blueprint to main's file plus the `operators` grant**

```bash
python3 - <<'PY'
import pathlib, subprocess
p = pathlib.Path("Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml")
text = subprocess.run(["git", "show", f"65cdf63a:{p}"], capture_output=True, text=True, check=True).stdout
anchor = "        - !Find [authentik_core.group, [name, iverson-loadtest-bypass]]\n"
assert text.count(anchor) == 1
grant = ("        # Compose-only: gives this dev user operator access so a developer can log the admin\n"
         "        # console into a user that already satisfies the Operator policy without a manual\n"
         "        # Authentik UI step. Never blueprinted for a deployment overlay -- see\n"
         "        # docs/runbooks/operator-access-onboarding.md.\n"
         "        - !Find [authentik_core.group, [name, operators]]\n")
p.write_text(text.replace(anchor, anchor + grant))
PY
git diff --numstat 65cdf63a -- Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml
```

Expected: `5	0	…service-clients.yaml` — five added lines, nothing removed. The pin and both `offline_access` mappings are gone.

- [ ] **Step 8: Prove the tree compiles**

```bash
dotnet build Iverson.slnx --nologo 2>&1 | tail -3        # expect "0 Error(s)"
(cd Iverson.AdminUI && npm ci && npm run build)          # expect exit 0
```

- [ ] **Step 9: Record the known red tests (not a gate at this commit)**

```bash
dotnet test Iverson.Server/Iverson.Api.Tests --no-build --nologo 2>&1 | grep -E "^\s+Failed |Failed!|Passed!"
dotnet test Iverson.Server/Iverson.Vector.Tests --no-build --nologo 2>&1 | grep -E "^\s+Failed |Failed!|Passed!"
```

Expected: Api.Tests fails exactly 8 — `GetSchema_OwnerTenantIdSet_ExcludesOtherTenantsCatalog_ButIncludesUnscopedTypes` (Task 2), four `AdminConsoleCorsPipelineTests` (Task 6), three `ProbeAuthorizationPipelineTests` (Task 3). Vector.Tests fails exactly 1 — `AddQdrant_RegistersResolvableVectorCollectionReader` (Task 4). Any other failure: stop and report it.

- [ ] **Step 10: Commit the merge**

```bash
git add Iverson.Server/Iverson.Api/Program.cs \
        Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs \
        Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml
git diff --name-only                     # must print nothing — everything staged
git commit -F - <<'EOF'
merge main into admin-console-landing-page

Merges 65cdf63a with -X theirs, so main's side wins every conflict, plus only
what compiling requires: main's /health handler restored verbatim, GetSchema
restored to the branch's body, main's rename of the ConfigMap blueprint
accepted, the compose blueprint reduced to main's file plus the bypass user's
operators grant, and three branch files removed because they no longer compile
against main (HealthCheckCache with its tests, and the listener test that
references the removed HttpListenerOnly).

Test suites are red at this commit by design; the repair series that follows
greens them. See docs/specs/2026-09-22-admin-console-main-merge-resolution-design.md.
EOF
git log -1 --format='%H %P'              # two parents: the branch tip and 65cdf63a
```

### Task 2: Carry main's cross-tenant schema filter into the branch's registry readers *(spec R1)*

**Interfaces:** Consumes Task 1's tree, where `GetSchema` delegates to `SchemaCatalogReader.BuildCatalog`.

- [ ] **Step 1: Write the REST schema arm**

Create `Iverson.Server/Iverson.Api.Tests/AdminConsoleSchemaEndpointTests.cs`:

```csharp
using System.Security.Claims;
using FluentAssertions;
using Iverson.Api.Authorization;
using Iverson.Api.Console;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Iverson.Api.Tests;

public class AdminConsoleSchemaEndpointTests
{
    [Fact]
    public async Task ForeignTenantSchema_IsAbsentFromTheCatalog()
    {
        var registry = new SchemaRegistry(
            new AdminConsoleSchemaRegistryRepository(), NullLogger<SchemaRegistry>.Instance);
        await registry.LoadAsync();
        await registry.RegisterAsync(AdminConsoleSchemaRegistryRepository.Article() with
        {
            TypeName = "ForeignArticle", OwnerTenantId = "tenant_beta"
        });
        var reader = new SchemaCatalogReader(
            registry, new RowFieldAuthorizationEvaluator(), NullLogger<SchemaCatalogReader>.Instance);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("tenant_id", "tenant_alpha"),
                new Claim("groups", AdminConsoleTestWebApplicationFactory.ReaderGroup)
            ], authenticationType: "test"))
        };

        var result = AdminConsoleEndpoints.GetSchema(http, registry, reader);

        var names = result.Should().BeOfType<Ok<SchemaCatalogResponse>>().Subject.Value!.Types
            .Select(t => t.Name).ToList();
        names.Should().Contain(AdminConsoleTestWebApplicationFactory.VisibleTypeWithRows);
        names.Should().NotContain("ForeignArticle");
    }
}
```

- [ ] **Step 2: Write the REST data-volume arm**

Add to `Iverson.Server/Iverson.Api.Tests/AdminConsoleDataVolumeEndpointTests.cs`, beside the existing tests (it reuses the file's private `SeededRegistryAsync`, `ReaderContext` and `NullReturningSearchService`):

```csharp
    [Fact]
    public async Task ForeignTenantSchema_IsNotEnumeratedByName()
    {
        var registry = await SeededRegistryAsync();
        await registry.RegisterAsync(AdminConsoleSchemaRegistryRepository.Article() with
        {
            TypeName = "ForeignArticle", OwnerTenantId = "tenant_beta"
        });
        var reader = new AggregateReader(
            new NullReturningSearchService(), registry, new RowFieldAuthorizationEvaluator());

        var result = await AdminConsoleEndpoints.GetDataVolumeAsync(ReaderContext(), registry, reader);

        var names = result.Should().BeOfType<Ok<DataVolumeResponse>>().Subject.Value!.Types
            .Select(t => t.TypeName).ToList();
        names.Should().Contain(AdminConsoleTestWebApplicationFactory.VisibleTypeWithRows);
        names.Should().NotContain("ForeignArticle");
    }
```

- [ ] **Step 3: Watch all three arms fail**

```bash
dotnet test Iverson.Server/Iverson.Api.Tests --nologo --filter "FullyQualifiedName~ForeignTenantSchema|FullyQualifiedName~GetSchema_OwnerTenantIdSet"
```

Expected: 3 failed — the two new arms (`ForeignArticle` is present) and main's gRPC arm.

- [ ] **Step 4: Add the filter to `BuildCatalog`**

In `Iverson.Server/Iverson.Api/Schema/SchemaCatalogReader.cs`, immediately before `foreach (var schema in registry.All.Values)`:

```csharp
        var callerTenant = actingUser?.FindFirst("tenant_id")?.Value;
```

and as the first statement inside that loop:

```csharp
            // Main's cross-tenant fix (6509b6be): another tenant's schema is not even enumerable.
            if (schema.OwnerTenantId is not null && schema.OwnerTenantId != callerTenant)
                continue;
```

- [ ] **Step 5: Add the filter to `CountRowsAsync`**

In `Iverson.Server/Iverson.Api/Search/AggregateReader.cs`, directly after `if (schema is null) return TypeRowCount.UnknownType;`:

```csharp
        if (schema.OwnerTenantId is not null
            && schema.OwnerTenantId != actingUser?.FindFirst("tenant_id")?.Value)
            return TypeRowCount.Denied;
```

`Denied` is folded into `deniedTypeCount` without the name reaching the wire, so this needs no new response shape.

- [ ] **Step 6: Watch the arms pass, and nothing else change**

```bash
dotnet test Iverson.Server/Iverson.Api.Tests --nologo --filter "FullyQualifiedName~ForeignTenantSchema|FullyQualifiedName~GetSchema_OwnerTenantIdSet"   # 3 passed
dotnet test Iverson.Server/Iverson.Api.Tests --nologo 2>&1 | grep -E "^\s+Failed |Failed!|Passed!"
```

Expected on the full run: exactly 7 failed — the four CORS and three probe tests from Task 1's list.

- [ ] **Step 7: Commit**

```bash
git add Iverson.Server/Iverson.Api/Schema/SchemaCatalogReader.cs \
        Iverson.Server/Iverson.Api/Search/AggregateReader.cs \
        Iverson.Server/Iverson.Api.Tests/AdminConsoleSchemaEndpointTests.cs \
        Iverson.Server/Iverson.Api.Tests/AdminConsoleDataVolumeEndpointTests.cs
git commit -F - <<'EOF'
carry main's cross-tenant schema filter into the console's registry readers

Main closed a cross-tenant metadata leak in GetSchema (6509b6be), but that filter lived only in
the body the branch's refactor replaced. Both readers now apply it, so the gRPC catalog,
/admin/console/schema and /admin/console/data-volume all withhold another tenant's types.
EOF
```

### Task 3: Delete the rest of the branch code main supersedes *(spec R2)*

- [ ] **Step 1: Delete**

```bash
git rm Iverson.Server/Iverson.Api/HealthCheckWireFormat.cs \
       Iverson.Server/Iverson.Api.Tests/HealthCheckWireFormatTests.cs \
       Iverson.Server/Iverson.Api.Tests/ProbeAuthorizationPipelineTests.cs
```

`HealthCheckWireFormat` has no caller once Task 1 restored main's `/health`; main deleted the `/probe/*` endpoints the probe tests exercise.

- [ ] **Step 2: Repoint `TenantStatusCache`'s comment at main's key**

In `Iverson.Server/Iverson.Api/Tenancy/TenantStatusCache.cs`, replace the four comment lines above `KeyFor` (`:12-15`) with:

```csharp
    // Prefixed so a raw tenantId can never collide with another consumer's key in the same
    // shared IMemoryCache: /health caches its composite result under "health-composite"
    // (Program.cs), and IMemoryCache.TryGetValue<TItem> returns true with a default value for a
    // cached null, so an un-namespaced tenant key could shadow -- or be shadowed by -- that entry.
```

The `tenant-status:` prefix itself stays — it is what closes main's own collision.

- [ ] **Step 3: Verify**

```bash
dotnet build Iverson.slnx --nologo 2>&1 | tail -3        # 0 Error(s)
dotnet test Iverson.Server/Iverson.Api.Tests --nologo 2>&1 | grep -E "^\s+Failed |Failed!|Passed!"
```

Expected: exactly the four `AdminConsoleCorsPipelineTests` failures.

- [ ] **Step 4: Commit**

```bash
git add Iverson.Server/Iverson.Api/Tenancy/TenantStatusCache.cs
git commit -F - <<'EOF'
delete branch code main's equivalents supersede

HealthCheckWireFormat has no caller once main's /health is restored, and main deleted the
/probe endpoints the probe tests exercise. TenantStatusCache keeps its prefix, which also
closes main's own health-composite key collision; its comment now points at main's key.
EOF
```

### Task 4: Meet main's 32-byte Qdrant key minimum in the branch's test fixture *(spec known-unknown 1)*

- [ ] **Step 1: Change the fixture**

In `Iverson.Server/Iverson.Vector.Tests/ServiceCollectionExtensionsTests.cs:44`, change `apiKey: "test-api-key"` to `apiKey: "test-signing-key-0123456789abcdef"` — the value main's own tests in this file use.

- [ ] **Step 2: Verify and commit**

```bash
dotnet test Iverson.Server/Iverson.Vector.Tests --nologo 2>&1 | grep -E "Failed!|Passed!"   # 0 failed
git add Iverson.Server/Iverson.Vector.Tests/ServiceCollectionExtensionsTests.cs
git commit -F - <<'EOF'
give the qdrant reader test a key that meets main's 32-byte minimum

Main now rejects a Qdrant API key under 32 bytes. The branch's test used a 12-byte fixture;
it now uses the value main's own tests in the file use.
EOF
```

### Task 5: Close the `authPending` detection gap *(spec R7)*

**Interfaces:** Follows Task 3, which deleted the only test that tied `HealthStrip`'s `authPending` state to the server.

- [ ] **Step 1: Add the tripwire to main's `/health` harness**

In `Iverson.Server/Iverson.Api.Tests/AuthenticationPipelineTests.cs`, add `using System.Text.Json;` to the usings, and this test directly after `GetHealth_TwoRequestsWithinCacheWindow_OnlyInvokesDependenciesOnce`:

```csharp
    [Fact]
    public async Task GetHealth_StarRocksAuthPending_IsReportedAsFalse()
    {
        using var factory = new HealthCacheTestFactory();
        factory.Db.QuerySingleOrDefaultAsync<int>(Arg.Any<string>()).Returns(1);
        factory.StarRocks.CheckHealthAsync().Returns(EngagementHealthStatus.AuthPending);
        factory.Vector.PingAsync().Returns(true);
        factory.Kafka.PingAsync().Returns(true);

        var response = await factory.CreateClient().GetAsync("/health");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("checks").GetProperty("starrocks").ValueKind.Should().Be(
            JsonValueKind.False,
            "main's /health collapses AuthPending into false; if it now emits \"authPending\", the "
            + "follow-up against main has landed — restore the live-path titles of HealthStrip's "
            + "authPending tests and update this assertion");
    }
```

- [ ] **Step 2: Prove the tripwire trips**

Run it (passes). Then temporarily change main's handler line in `Program.cs` from `(object)(srStatus == EngagementHealthStatus.Healthy)` to `(object)(srStatus == EngagementHealthStatus.AuthPending ? "authPending" : srStatus == EngagementHealthStatus.Healthy)`, run it again (must fail with the message above), then revert `Program.cs` and confirm `git diff --quiet -- Iverson.Server/Iverson.Api/Program.cs`.

```bash
dotnet test Iverson.Server/Iverson.Api.Tests --nologo --filter "FullyQualifiedName~GetHealth_StarRocksAuthPending"
```

- [ ] **Step 3: Make the widget tests honest**

In `Iverson.AdminUI/src/widgets/HealthStrip.test.tsx`:
- Retitle `:117` to `"renders an auth-pending StarRocks as its own state, not as Down (forward-compatible: the current /health cannot emit authPending)"`.
- Retitle `:128` to `"does not make a fresh install's bootstrapping StarRocks LOOK like a failure (forward-compatible: the current /health cannot emit authPending)"`.
- Above the `authPending` assertions at `:49` and `:153`, add: `// Forward-compatible: main's /health collapses AuthPending into false (see AuthenticationPipelineTests' tripwire).`

- [ ] **Step 4: Verify and commit**

```bash
(cd Iverson.AdminUI && npx vitest run src/widgets/HealthStrip.test.tsx)
git add Iverson.Server/Iverson.Api.Tests/AuthenticationPipelineTests.cs Iverson.AdminUI/src/widgets/HealthStrip.test.tsx
git commit -F - <<'EOF'
add a tripwire for the authPending state main's /health cannot emit

Main's /health collapses AuthPending into false, so HealthStrip's authPending state is
unreachable while its tests stay green. The tripwire pins today's value and fails, with
instructions, when a follow-up restores the four-value form.
EOF
```

### Task 6: Restore the CORS middleware *(spec R3, server)*

- [ ] **Step 1: Insert the branch's `UseCors` block into main's pipeline**

In `Iverson.Server/Iverson.Api/Program.cs`, between `app.UseHttpsRedirection();` and `app.UseAuthentication();`, insert the branch tip's block (`git show 87914794:Iverson.Server/Iverson.Api/Program.cs | sed -n '414,422p'`):

```csharp
// Must run before UseAuthentication: a cross-origin preflight OPTIONS carries no
// Authorization header, so if it reached the FallbackPolicy's RequireAuthenticatedUser
// first, every preflight would be rejected before CORS ever got to answer it.
// (AdminConsoleCorsPipelineTests.ConfiguredOrigin_PreflightOptions_AdminDlq_AnsweredByCors
// NotFallbackPolicy pins this: moving UseCors below UseAuthentication/UseAuthorization
// makes that one test fail with 401, confirmed empirically before this comment was
// restored.)
if (!string.IsNullOrEmpty(adminConsoleOrigin))
    app.UseCors(AdminConsoleCorsPolicy);
```

- [ ] **Step 2: Verify and commit**

```bash
dotnet test Iverson.Server/Iverson.Api.Tests --nologo --filter "FullyQualifiedName~AdminConsoleCorsPipelineTests"   # 6 passed
dotnet test Iverson.Server/Iverson.Api.Tests --nologo 2>&1 | grep -E "Failed!|Passed!"                            # 0 failed
git add Iverson.Server/Iverson.Api/Program.cs
git commit -F - <<'EOF'
restore the admin console's cors middleware in main's pipeline

-X theirs dropped the UseCors call because main rewrote that part of the pipeline. Without it
no response carries Access-Control-Allow-Origin and every cross-origin console request fails.
Main has no CORS of its own, so this overrides nothing.
EOF
```

### Task 7: Name the admin-api origin in the pod's CSP *(spec R3, pod)*

- [ ] **Step 1: `nginx.conf`**

In `Iverson.AdminUI/nginx.conf:35`, change `connect-src 'self' ${OIDC_ORIGIN};` to `connect-src 'self' ${ADMIN_API_ORIGIN} ${OIDC_ORIGIN};`.

- [ ] **Step 2: `docker-entrypoint.sh`**

After `validate API_BASE_URL "${API_BASE_URL-}"`, add:

```sh
export ADMIN_API_ORIGIN="$(origin_of API_BASE_URL "$API_BASE_URL")"
```

Change main's SHELL-FORMAT list from `envsubst '${OIDC_ORIGIN} ${EXTERNAL_SCHEME} ${HSTS_LINE}'` to `envsubst '${ADMIN_API_ORIGIN} ${OIDC_ORIGIN} ${EXTERNAL_SCHEME} ${HSTS_LINE}'`, and in the comment above it change "exactly these three names" to "exactly these four names".

- [ ] **Step 3: Verify against a built image**

```bash
sh -n Iverson.AdminUI/docker-entrypoint.sh
docker build -f Iverson.AdminUI/Dockerfile -t adminui-t7 .
docker run -d --name adminui-t7 -p 127.0.0.1:8098:8080 \
  -e OIDC_CLIENT_ID=ci-client -e "OIDC_AUTHORITY=http://authentik.ci.invalid/application/o/iverson/" \
  -e "API_BASE_URL=http://admin-api.ci.invalid" -e EXTERNAL_SCHEME=http \
  -e "OIDC_ORIGIN=http://authentik.ci.invalid" adminui-t7
sleep 3; curl -sI http://127.0.0.1:8098/ | grep -i '^content-security-policy'
docker rm -f adminui-t7 && docker rmi adminui-t7
```

Expected: the header's `connect-src` directive reads `'self' http://admin-api.ci.invalid http://authentik.ci.invalid` — both origins as whole tokens inside `connect-src`.

- [ ] **Step 4: Commit**

```bash
git add Iverson.AdminUI/nginx.conf Iverson.AdminUI/docker-entrypoint.sh
git commit -F - <<'EOF'
name the admin-api origin in the console pod's csp

Main's CSP names only the console's own origin and Authentik in connect-src, so every call to
the admin-api host is blocked. The entrypoint now derives the origin from API_BASE_URL with the
origin_of helper the merge left in place.
EOF
```

### Task 8: Restore the split-origin Helm values, the Ingress CSP and compose *(spec R3, configuration)*

Every restored block below is copied from the branch tip `87914794`, comments included (`git show 87914794:<path> | sed -n 'A,Bp'`).

- [ ] **Step 1: `values-aws.yaml`**
  - Insert branch `:4-8` (the `networkPolicy` comment and `networkPolicy.clusterCidrs: ["10.0.0.0/16"]`) as a top-level block immediately before `global:`.
  - Add branch `:14` (`prometheusEnabled: true   # single source of truth …`) under `global:`.
  - Add branch `:103-116` (the comment, `adminApiIngress:` and `adminConsoleOrigin: "https://iverson.example.com"`) as the last keys of the `api:` mapping. Its `certificate-arn: "<ACM_CERT_ARN>"` is a placeholder the deployer fills, like the other AWS Ingresses'.
  - Set `adminUi.apiBaseUrl` to `"https://admin-api.iverson.example.com"` (branch `:186`).

- [ ] **Step 2: `values-azure.yaml`** — insert branch `:4-9` before `global:`; add branch `:15` under `global:`; set `adminUi.apiBaseUrl` to `"https://admin-api.iverson.example.com"` (branch `:167`). `adminApiIngress`/`adminConsoleOrigin` already survive here.

- [ ] **Step 3: `values-gcp.yaml`** — insert branch `:5-17` before `global:`; add branch `:23` under `global:`; set `adminUi.apiBaseUrl` to `"https://admin-api.iverson.example.com"` (branch `:175`). `adminApiIngress`/`adminConsoleOrigin` already survive here.

- [ ] **Step 4: `values.yaml`** — add branch `:64-70` (the comment and `prometheusEnabled: true`) under `global:`; add branch `:170-187` (`adminApiIngress: {}` and `adminConsoleOrigin: ""` with their comments) under `api:`; set `adminUi.apiBaseUrl` to `"http://admin-api.iverson.local"` (branch `:239`).

- [ ] **Step 5: `values-local.yaml`** — set `adminUi.apiBaseUrl` to `"http://admin-api.iverson.local"` (branch `:164`). Everything else survives here.

- [ ] **Step 6: Main's admin-ui Ingress CSP snippet**

In `charts/admin-ui/templates/ingress.yaml:42`, change `connect-src 'self' {{ $oidcOrigin }};` to:

```
connect-src 'self' {{ printf "%s://admin-api.%s" .Values.global.externalScheme .Values.global.ingressHost }} {{ $oidcOrigin }};
```

- [ ] **Step 7: Drop the dead `/v1/traces` route**

In `charts/api/templates/admin-api-ingress.yaml`, delete the seven-line `- path: /v1/traces` rule (`:51-57`). In its header comment (`:14`) and in `charts/api/values.yaml:40`, change "/admin, /health and /v1/traces" to "/admin and /health".

- [ ] **Step 8: Compose**

In `Iverson.Server/docker-compose.yml`, append branch `:474-484` (the `AdminConsole__Origin=http://localhost:5173` and `Prometheus__BaseUrl=http://prometheus:9090` lines with their comments) to the `iverson-api` service's `environment:` list, after `Tenancy__SeedLegacyTenants=true`.

- [ ] **Step 9: Verify every overlay renders, with main's CI layering**

```bash
cd Iverson.Server/deploy/helm/iverson && helm dependency update . >/dev/null
for p in local laptop aws azure gcp; do
  extra=""; [ -f "values-$p.ci-override.yaml" ] && extra="-f values-$p.ci-override.yaml"
  helm template t . -f "values-$p.yaml" $extra > "/tmp/render-$p.yaml" || { echo "RENDER FAILED: $p"; exit 1; }
  printf '%s admin-api hosts=%s prometheus-url=%s admin-api-traces=%s\n' "$p" \
    "$(grep -cE '^[[:space:]]*- host: "admin-api\.' /tmp/render-$p.yaml)" \
    "$(grep -c 'name: Prometheus__BaseUrl' /tmp/render-$p.yaml)" \
    "$(awk '/name: t-admin-api$/,/^---/' /tmp/render-$p.yaml | grep -c '/v1/traces')"
done
grep -c "connect-src 'self' http://admin-api.iverson.local http://authentik.iverson.local;" /tmp/render-local.yaml
cd -
```

Expected: every render exits 0; admin-api hosts `local=1 laptop=0 aws=1 azure=1 gcp=1`; `prometheus-url` 1 on local/aws/azure/gcp and 0 on laptop; `admin-api-traces=0` everywhere; the final grep prints `1`. `helm lint` is not a substitute — it exits 0 even when a template `fail` fires.

- [ ] **Step 10: Commit**

```bash
git add Iverson.Server/deploy/helm/iverson/values.yaml Iverson.Server/deploy/helm/iverson/values-aws.yaml \
        Iverson.Server/deploy/helm/iverson/values-azure.yaml Iverson.Server/deploy/helm/iverson/values-gcp.yaml \
        Iverson.Server/deploy/helm/iverson/values-local.yaml \
        Iverson.Server/deploy/helm/iverson/charts/admin-ui/templates/ingress.yaml \
        Iverson.Server/deploy/helm/iverson/charts/api/templates/admin-api-ingress.yaml \
        Iverson.Server/deploy/helm/iverson/charts/api/values.yaml Iverson.Server/docker-compose.yml
git commit -F - <<'EOF'
restore the split-origin chart values, ingress csp and compose env on main's foundations

The merge took main's values files, dropping the keys the admin-api Ingress, the network-policy
guard and the metrics widget read. Main's nginx-class Ingress replaces the pod's CSP, so it needs
the admin-api origin too. The /v1/traces rule goes because main serves that route only on 8080.
EOF
```

### Task 9: Resolve the AuthProvider fallout under main's auth decisions *(spec R4)*

- [ ] **Step 1: `AuthProvider.tsx`**
  - Delete the `:4` import of `useTokenRenewal` and the `useTokenRenewal();` call in `AuthGate` (`:54`).
  - In `AuthGate`'s doc comment, delete the renewal paragraph (`:46-50`, "It is also where the console's fetch layer is bridged to silent renewal …").
  - Re-add the branch's `onSigninCallback` (`git show 87914794:Iverson.AdminUI/src/auth/AuthProvider.tsx`, the exported function at `:23-25` with its doc comment above it) before main's `oidcConfig`, and add `onSigninCallback,` as a property of main's `oidcConfig`. Main's `scope` and `automaticSilentRenew: false` stay untouched.

- [ ] **Step 2: `AuthProvider.test.tsx`**
  - Delete `let capturedProviderProps: Record<string, unknown> | null = null;` (`:8`).
  - Change the import at `:18` to `import { AuthGate, AuthProvider, onSigninCallback } from "./AuthProvider";` and add directly below it:

```tsx
function renderProviderAndCaptureSettings(): Record<string, unknown> {
  capturedOidcProps.length = 0;
  render(
    <AuthProvider>
      <div>App</div>
    </AuthProvider>
  );
  if (capturedOidcProps.length === 0) {
    throw new Error("AuthProvider did not render the OIDC provider");
  }
  return capturedOidcProps[0];
}
```

  - Delete the tests at `:105` ("bridges the console's fetch layer to silent renewal …"), `:132` ("asks the IdP to revoke the tokens at signout …") and `:139` ("keeps offline_access in the requested scope"): the first's registrant is gone; the other two assert decisions main reversed, and `:139` negates main's own test at `:25`. Keep `:148` and `:159`.

- [ ] **Step 3: Delete the renewal module**

```bash
git rm Iverson.AdminUI/src/api/useTokenRenewal.ts Iverson.AdminUI/src/api/useTokenRenewal.test.ts
```

- [ ] **Step 4: `client.ts`**
  - Delete the `// ── 401 → silent renewal` section (`TokenRenewer`, `tokenRenewer`, `renewalInFlight`, `setTokenRenewer`, `requestTokenRenewal`, `:120-161`).
  - In the 401 branch, delete `requestTokenRenewal();`, leaving `return { kind: "unauthorized", status: 401 };`.
  - In the header doc: change the `"unauthorized"` table row (`:16`) to `401. The session is no longer valid; the widget shows nothing new.`; replace the "401 is handled once, not nine times" section (`:28-40`) with one paragraph stating that a 401 returns `unauthorized`, that there is no renewal (main's auth config issues no refresh token and its CSP blocks the iframe fallback), and that an expired session is ended by `useSessionExpiry`, whose `removeUser()` trips `AuthGate`'s redirect; and in the 403 section (`:44`) replace "403 is deliberately NOT routed to renewal: it is" with "403 is".

- [ ] **Step 5: `client.test.ts`**
  - Remove `setTokenRenewer,` from the import (`:5`) and `setTokenRenewer(null);` from `getJson`'s `afterEach` (`:39`).
  - In the 403 test (`:119`), delete the `renewer` declaration, `setTokenRenewer(renewer);`, the two-line comment before the last assertion and `expect(renewer).not.toHaveBeenCalled();`.
  - Delete the tests at `:143` ("routes a 401 to silent renewal …"), `:154` ("coalesces concurrent 401s …") and `:173` ("renews again on a later 401 …"), and the whole `describe("setTokenRenewer", …)` block (`:245-`).
  - Retitle `:190` to `"reports a 401 as unauthorized"` — its assertion already pins that mapping.

- [ ] **Step 6: Verify and commit**

```bash
(cd Iverson.AdminUI && npm test && npm run build)       # 0 failed
grep -rn "setTokenRenewer\|requestTokenRenewal\|renewalInFlight\|useTokenRenewal" Iverson.AdminUI/src   # no hits
git add Iverson.AdminUI/src/auth/AuthProvider.tsx Iverson.AdminUI/src/auth/AuthProvider.test.tsx \
        Iverson.AdminUI/src/api/client.ts Iverson.AdminUI/src/api/client.test.ts
git commit -F - <<'EOF'
resolve the auth fallout under main's decisions and restore the signin-code scrub

Main dropped offline_access and silent renew, so the renewal bridge has nothing to call and the
tests asserting the branch's reversed decisions go. onSigninCallback, which main never had, is
restored so the OIDC code does not survive in browser history.
EOF
```

### Task 10: End the session when the access token expires *(spec R5)*

**Interfaces:** Consumes Task 9's `AuthGate` (renewal call removed).

- [ ] **Step 1: Write the failing tests**

Create `Iverson.AdminUI/src/auth/useSessionExpiry.test.tsx`:

```tsx
import { renderHook } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";

const useAuthMock = vi.fn();
vi.mock("react-oidc-context", () => ({ useAuth: () => useAuthMock() }));

import { useSessionExpiry } from "./useSessionExpiry";

describe("useSessionExpiry", () => {
  beforeEach(() => useAuthMock.mockReset());

  it("unsubscribes from the expiry event on unmount", () => {
    const unsubscribe = vi.fn();
    useAuthMock.mockReturnValue({
      removeUser: vi.fn(),
      events: { addAccessTokenExpired: () => unsubscribe },
    });

    const { unmount } = renderHook(() => useSessionExpiry());
    unmount();

    expect(unsubscribe).toHaveBeenCalledTimes(1);
  });

  it("does nothing when the auth context carries no events", () => {
    useAuthMock.mockReturnValue({ removeUser: vi.fn() });

    expect(() => renderHook(() => useSessionExpiry())).not.toThrow();
  });
});
```

Add to the `AuthGate` describe block in `Iverson.AdminUI/src/auth/AuthProvider.test.tsx`:

```tsx
  it("ends the session when the access token expires, so the redirect can fire", () => {
    let onExpired: (() => void) | undefined;
    const removeUser = vi.fn(async () => undefined);
    useAuthMock.mockReturnValue({
      isLoading: false,
      isAuthenticated: true,
      signinRedirect,
      removeUser,
      events: {
        addAccessTokenExpired: (cb: () => void) => {
          onExpired = cb;
          return () => {};
        },
      },
    });

    render(
      <AuthGate>
        <div>Protected content</div>
      </AuthGate>
    );
    onExpired!();

    expect(removeUser).toHaveBeenCalledTimes(1);
  });
```

`removeUser()` raises `userUnloaded`, which clears `isAuthenticated`; the existing "redirects an unauthenticated visitor" test already pins `AuthGate`'s redirect on that state.

```bash
(cd Iverson.AdminUI && npx vitest run src/auth)          # fails: module not found / removeUser not called
```

- [ ] **Step 2: Implement the hook**

Create `Iverson.AdminUI/src/auth/useSessionExpiry.ts`:

```ts
import { useEffect } from "react";
import { useAuth } from "react-oidc-context";

// No refresh token and no iframe fallback, so an expired token cannot renew: end the session instead.
export function useSessionExpiry(): void {
  const auth = useAuth();
  const events = auth?.events;
  const removeUser = auth?.removeUser;

  useEffect(() => {
    if (!events || typeof removeUser !== "function") return;
    return events.addAccessTokenExpired(() => {
      void removeUser();
    });
  }, [events, removeUser]);
}
```

The guard matches `useTokenRenewal`'s precedent and keeps `AuthGate`'s existing tests, which mock `useAuth` without `events`, valid.

- [ ] **Step 3: Mount it in `AuthGate`**

In `AuthProvider.tsx`, add `import { useSessionExpiry } from "./useSessionExpiry";` and call `useSessionExpiry();` directly after `const auth = useAuth();` in `AuthGate`. Add one sentence to `AuthGate`'s doc comment: "It also ends the session when the access token expires — see `useSessionExpiry`."

- [ ] **Step 4: Verify, falsify, commit**

```bash
(cd Iverson.AdminUI && npx vitest run src/auth && npm test)   # 0 failed
```

Delete the `useSessionExpiry();` line from `AuthGate`, confirm the new `AuthGate` test fails, then restore it and confirm `git diff` shows only the intended changes.

```bash
git add Iverson.AdminUI/src/auth/useSessionExpiry.ts Iverson.AdminUI/src/auth/useSessionExpiry.test.tsx \
        Iverson.AdminUI/src/auth/AuthProvider.tsx Iverson.AdminUI/src/auth/AuthProvider.test.tsx
git commit -F - <<'EOF'
end the console session when the access token expires

With no refresh token and no iframe fallback, an expired token left the console authenticated
but dead, every widget polling a 401 forever. The expiry event now removes the user, which trips
AuthGate's existing redirect to login.
EOF
```

### Task 11: Consistency sweep *(spec R6)*

**Interfaces:** After Task 7 (entrypoint) and Task 8 (`values-aws` comments).

- [ ] **Step 1: README seeded-user sentence**

In `Iverson.AdminUI/README.md`, after main's bootstrap sentence ending `into \`.env\` by \`scripts/generate-compose-secrets.sh\`.)` (`:29`), append on the same bullet:

```
  For compose, one is already seeded: `iverson-loadtest-bypass-user`, whose password is
  the `IVERSON_BYPASS_PASSWORD` value in the same `.env`; it belongs to `operators` out of
  the box (`blueprints/compose-only/service-clients.yaml`).
```

Leave main's bootstrap sentence and the `8081` port lines as they are.

- [ ] **Step 2: Runbooks**
  - `docs/runbooks/admin-console-landing-page-usage.md:67`: `password: <IVERSON_BYPASS_PASSWORD from Iverson.Server/.env>`.
  - `:70`: replace "These credentials are **dev-only and hardcoded deliberately**," with "The password is generated per checkout by `scripts/generate-compose-secrets.sh` and is **dev-only**,".
  - `:79` and `docs/runbooks/operator-access-onboarding.md:21`: replace `` `dev-admin-password` `` with "the `AUTHENTIK_BOOTSTRAP_PASSWORD` value in `Iverson.Server/.env`".

- [ ] **Step 3: Entrypoint leftovers**

In `Iverson.AdminUI/docker-entrypoint.sh`, delete the three unused declarations `CONFIG_TEMPLATE=…`, `CONFIG_OUTPUT=…`, `HEADERS_OUTPUT=…` (`:23-25`), and in the header comment (`:3-6`) replace the artefact list with the two files main's tail actually writes: `config.js` (from `config.js.template`) and the CSP rendered in place into `/etc/nginx/conf.d/default.conf`.

- [ ] **Step 4: Confirm the `values-aws` comments are accurate again**

`grep -n "adminApiIngress" Iverson.Server/deploy/helm/iverson/values-aws.yaml` — the comments at `:142`-area and `:162`-area now refer to a key Task 8 restored. No edit.

- [ ] **Step 5: Verify and commit**

```bash
sh -n Iverson.AdminUI/docker-entrypoint.sh
git grep -n "dev-only-not-for-production-bypass-password\|dev-admin-password" -- ':!docs/criticalreviews'   # no hits
git add Iverson.AdminUI/README.md Iverson.AdminUI/docker-entrypoint.sh \
        docs/runbooks/admin-console-landing-page-usage.md docs/runbooks/operator-access-onboarding.md
git commit -F - <<'EOF'
point the console docs at main's generated compose credentials

Main replaced the static compose passwords with generated ones, so the README and runbooks now
name the generated values. The entrypoint also loses path variables and a header description
left over from the branch's design.
EOF
```

### Task 12: Adapt the branch's AdminUI CI job *(spec R8)*

**Interfaces:** After Tasks 7, 9 and 10.

- [ ] **Step 1: Main's entrypoint contract** — in the served-CSP step's `docker run` (`:156-160`), add `-e EXTERNAL_SCHEME=http -e "OIDC_ORIGIN=$IDP_ORIGIN"`.

- [ ] **Step 2: `frame-src`** — delete `assert_directive_names frame-src "$IDP_ORIGIN"` (`:210`), and in the error message at `:201` delete ", and frame-src without it blocks the iframe silent-renew fallback".

- [ ] **Step 3: The type-error ratchet**

```bash
(cd Iverson.AdminUI && npx tsc --noEmit 2>&1 | grep -cE "error TS[0-9]+")
```

Set `BASELINE` (`:100`) to that count, and update the `BASELINE` number in the comment above it.

- [ ] **Step 4: Run the `image-contract` job locally, in the workflow's order**

```bash
python3 - <<'PY'
import os, subprocess, yaml
job = yaml.safe_load(open(".github/workflows/admin-ui.yml"))["jobs"]["image-contract"]
env = {**os.environ, **{k: str(v) for k, v in job["env"].items()}, "IMAGE": "iverson-adminui-ci:local"}
for step in job["steps"]:
    if "run" in step:
        print("::step::", step.get("name"))
        subprocess.run(["bash", "-e", "-c", step["run"]], env=env, check=True)
PY
```

Expected: every step exits 0, with `OK: connect-src names http://admin-api.ci.invalid` and `OK: connect-src names http://authentik.ci.invalid`. Then falsify: temporarily remove `${ADMIN_API_ORIGIN} ` from `Iverson.AdminUI/nginx.conf:35`, re-run, confirm the served-CSP step fails on `http://admin-api.ci.invalid`, then `git checkout -- Iverson.AdminUI/nginx.conf`. Remove the local image afterwards.

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/admin-ui.yml
git commit -F - <<'EOF'
adapt the admin console ci job to main's entrypoint and csp

The job started main's entrypoint without the two env vars its set -eu tail requires, asserted
a frame-src main's CSP omits by decision, and ratcheted type errors below the merged count.
EOF
```

### Task 13: Final gates *(spec Verification plan)*

No file changes; stop and report on any failure.

- [ ] **Step 1: All suites green**

```bash
dotnet build Iverson.slnx --nologo 2>&1 | tail -3
dotnet test Iverson.Server/Iverson.Api.Tests --nologo 2>&1 | grep -E "Failed!|Passed!"      # 0 failed
dotnet test Iverson.Server/Iverson.Vector.Tests --nologo 2>&1 | grep -E "Failed!|Passed!"   # 0 failed
(cd Iverson.AdminUI && npm test && npx tsc --noEmit 2>&1 | grep -cE "error TS[0-9]+")        # count == BASELINE
```

- [ ] **Step 2: Every overlay renders** — re-run Task 8 Step 9 on the tip.

- [ ] **Step 3: The three targeted proofs still discriminate**
  - R1: delete the two ownership checks (Task 2 Steps 4-5); the three arms fail; restore.
  - R5: delete the `useSessionExpiry();` mount; the `AuthGate` expiry test fails; restore.
  - R3 CSP: Task 12 Step 4 (pod side) and Task 8 Step 9's snippet grep (Ingress side) both pass.

- [ ] **Step 4: Known-unknown 2 — the rate limit**

Read the console's poll cadences (`grep -rn "intervalMs" Iverson.AdminUI/src/widgets Iverson.AdminUI/src/pages`) and main's global limiter (`Program.cs`, `PermitLimit = 6_000` per principal per minute). Record requests per minute per open tab and confirm it is orders of magnitude below 6,000.

- [ ] **Step 5: The audit property**

```bash
git diff --stat 65cdf63a..HEAD -- $(git diff --name-only 9eb99f76 65cdf63a)
```

For each main-changed file this lists, confirm the change is on the spec's roster (R1's `GetSchema`, R3's three CSP sites and five `apiBaseUrl` values) or is an additive insertion (`UseCors`, `onSigninCallback`, `useSessionExpiry`, the R7 tripwire, restored values keys). Anything else is an override of a main decision: report it.

- [ ] **Step 6: Leave the tree clean** — `git status --porcelain` prints nothing; no `adminui-*` or `iverson-adminui-ci` images remain.

## Tasks NOT in this plan

Inherited verbatim from the spec's "Out of scope — follow-ups against `main`" section. A new spec → plan cycle is required to add any of these.

These are main-owned and deliberately excluded by the resolution policy:

1. Re-pin `include_claims_in_id_token: true` on **both** blueprint paths.
2. Restore the four-value `starrocks` wire form (`authPending`) on `/health`; trips R7's tripwire.
3. `Dockerfile` pins `node:20-alpine` **by digest** (a deliberate hardening decision), while the
   merged `package.json` declares `engines.node >=22.22.0` — main's own `package.json` has no
   `engines` field, but the branch's survives the merge, and it matches the floor main's
   react-router 8.3.1 requires. So the merged tree declares a Node floor its own build image
   does not meet. `EBADENGINE` is a warning, not a failure, so this breaks nothing today.
4. Deleting the branch's drift-guard test loses its "the marked endpoint set has not drifted"
   assertion; main's `AuthenticationPipelineTests` covers `RequireListenerPort` but not that
   invariant.
5. Serve `/v1/traces` on the listener the admin-api Ingress targets. Main pins it to
   `RequireListenerPort(8080)` (`main:Program.cs:697`) and the admin-api Ingress sends it to 8081,
   so R3 drops that rule and console span export is off until this lands. Re-add the `/v1/traces`
   rule to `admin-api-ingress.yaml` with it.

**Not a follow-up:** main's `"health-composite"` cache-key collision, closed incidentally by R2.
