# Critical Implementation Review: 2026-09-23-admin-console-main-merge-implementation-plan (Round 1)

**Plan:** /home/ben/repositories/Iverson/.worktrees/admin-console-landing-page/docs/plans/2026-09-23-admin-console-main-merge-implementation-plan.md
**Artifact HEAD at review:** a900eb70291ef95801b9c59829e668a094f78322
**Verified plan-level assumptions section:** present

⚠️ 1 commits since plan-write time (SHA 113106d7); cited file:line references re-checked under §1.

## 0. Coverage enumeration

Execution basis: every task was executed as written, in order, in a throwaway detached worktree at `a900eb70`. That covers Task 1's merge of `65cdf63a` and scripts, the code blocks of Tasks 2–12, and Task 13's gates. Rows cite that run unless marked otherwise. `docker` on this host is podman 5.7.0.

**Tasks × surfaces**

| # | Surface | Disposition |
|---|---|---|
| T1.1 | Task 1 Steps 1–3: prose and commands (clean check, `-X theirs` merge, unmerged grep, `git rm`) | [compat] ok — merge exits 1; the grep prints exactly `UD Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml`; after `git rm`, `git diff --name-only --diff-filter=U` → 0 |
| T1.2 | Task 1 Step 4 code (`/health` restore). Rule-like: `block()`'s `startswith("})")` end marker | [bidirectional] over: ok — no earlier `})` line inside either handler / under: ok — the extracted handler (46 lines, `app.MapGet("/health"` … `.WithMetadata(new RequireListenerPort(8081))`) is `diff`-identical to `65cdf63a:Iverson.Server/Iverson.Api/Program.cs`; after the `sed`, `Program.cs` has 0 `HealthCheckCache` references |
| T1.3 | Task 1 Step 5 code (`GetSchema` restore). Rule-like: `method()` bounds | [bidirectional] over: ok — the assert on `lines[r + 1].strip() == "}"` held (tip 60–72, merged 67–117) / under: ok — the restored method is byte-identical to `87914794`'s `GetSchema` |
| T1.4 | Task 1 Step 6 deletions | [compat] ok — all three `git rm` calls succeed, and the resulting tree builds with `0 Error(s)` at T1.6 (against 4 + 1 errors with the files restored — §1 D1) |
| T1.5 | Task 1 Step 7 code (compose blueprint = main's file + `operators` grant). Rule-like: "main's file plus the grant drops exactly the pin and the two `offline_access` mappings" | [bidirectional] over: ok — `git diff --numstat 65cdf63a` → `5 0`; the grant is byte-identical to `87914794`'s / under: ok — `git diff -U0 9eb99f76 87914794 -- …/compose-only/service-clients.yaml` has exactly four hunks (`:167` pin, `:174` and `:282` `offline_access`, `:331` operators), so no fifth branch hunk is silently dropped |
| T1.6 | Task 1 Step 8 commands | [compat] ok — `dotnet build Iverson.slnx` → `0 Error(s)`. The first invocation died after 0.6 s with `Internal CLR error. (0x80131506)`; an identical retry was clean, so this is environmental. `npm ci && npm run build` exits 0 with EBADENGINE warnings only (local Node 22.16.0; no `.npmrc` `engine-strict`) |
| T1.7 | Task 1 Step 9 expected red set | [totality] ok — Api.Tests `Failed: 8, Passed: 1178, Total: 1186`: exactly `GetSchema_OwnerTenantIdSet_…`, 4 × `AdminConsoleCorsPipelineTests`, 3 × `ProbeAuthorizationPipelineTests`. Vector.Tests `Failed: 1, Passed: 169, Total: 170`: `AddQdrant_RegistersResolvableVectorCollectionReader` |
| T1.8 | Task 1 Step 10 commit | [compat] ok — `git diff --name-only` is empty after the three named `git add`s; `git log -1 --format='%H %P'` shows parents `a900eb70` `65cdf63a` |
| T2.1 | Task 2 Steps 1–3: test code and the failing run. Rule-like: Step 3's `--filter "FullyQualifiedName~ForeignTenantSchema\|FullyQualifiedName~GetSchema_OwnerTenantIdSet"` substring matcher | [compat] ok — both arms compile against the merged tree and fail for the intended reason: `Expected names {"Author", "ForeignArticle", "Article"} to not contain "ForeignArticle"` (both REST arms), plus main's gRPC arm. Matcher, over (selects extra tests): [totality] ok — the run reports `Total: 3` / under (misses an arm): [totality] ok — the three results are exactly `AdminConsoleSchemaEndpointTests.ForeignTenantSchema_IsAbsentFromTheCatalog`, `AdminConsoleDataVolumeEndpointTests.ForeignTenantSchema_IsNotEnumeratedByName` and `ObjectMappingGrpcServiceTests.GetSchema_OwnerTenantIdSet_ExcludesOtherTenantsCatalog_ButIncludesUnscopedTypes` |
| T2.2 | Task 2 Steps 4–5 filter code, at the prose anchors | under (foreign schema still enumerated): [compat] ok — anchors found (`foreach (var schema in registry.All.Values)`; the `if (schema is null)` / `return TypeRowCount.UnknownType;` pair, which spans two lines in the file while the plan quotes it as one — unambiguous); Step 6's filter → `Passed: 3`, so all three foreign-absence arms pass / over (own or unscoped schema withheld): [compat] ok — the T2.3 probe keeps `OwnArticle`, `Author` and `Article` on both endpoints, and the full suite after Task 6 is 1170/0 |
| T2.3 | Rule-like: the ownership filter × (caller tenant, schema owner) | [bidirectional] over: ok — temporary probe with `OwnArticle` (owner `tenant_alpha`) and `ForeignArticle` (owner `tenant_beta`), read as a `tenant_alpha` reader: schema `[OwnArticle,Author,Article] withheld=2`, data-volume `[OwnArticle,Author,Article] denied=2`, so own and unscoped types are kept / under: ok — `ForeignArticle` is absent from both. Operator and placement are identical to `65cdf63a:…/ObjectMappingGrpcService.cs:82-87` |
| T2.4 | Rule population: every caller-facing enumeration of the registry | under (a caller-facing enumeration left unfiltered): [totality] ok — `git grep` for `registry.All` in `Iverson.Api` on the merged tree finds three caller-facing sites: `SchemaCatalogReader.cs:60` (Task 2 filter); `AdminConsoleEndpoints.cs:179` (filtered through `CountRowsAsync`); `:115` (a count only — the spec-accepted `withheldTypeCount`). Main's only `OwnerTenantId` enumeration filter is `GetSchema` (`git grep OwnerTenantId 65cdf63a`) / over (a filter applied where main applies none): [negative] ok — the residual hits, `SchemaRegistrationOrchestrator.cs:271` and `Program.cs:700`, are not caller-facing, identical on main, and untouched by Task 2 |
| T2.5 | Task 2 Step 6 checkpoint "exactly 7 failed" | ok — not run separately. The post-Task-6 full run (1170 run, 0 failed) plus the deletion arithmetic 1186 − 9 probe − 10 wire-format + 2 + 1 = 1170 leaves no room for an extra failure introduced at Task 2 |
| T3.1 | Task 3 Step 1 deletions | [negative] ok — afterwards, `grep -rn "HealthCheckWireFormat\|HealthCheckCache\.\|ProbeAuthorization"` over `*.cs` hits only `<c>…</c>` doc text (no `cref`) and the `TenantStatusCache.cs:13` comment Step 2 rewrites; build `0 Error(s)` |
| T3.2 | Task 3 Step 2 comment | [existence] ok — `TenantStatusCache.cs:12-15` are the four lines naming `HealthCheckCache.CacheKey` |
| T3.3 | Task 3 Step 3 checkpoint "exactly the four CORS failures" | ok — same arithmetic as T2.5 |
| T4.1 | Task 4 | [compat] ok — the `:44` edit leaves Vector.Tests at `Passed: 170` |
| T5.1 | Task 5 Step 1 test code | [compat] ok — compiles with the added `using System.Text.Json;` and passes |
| T5.2 | Task 5 Step 2 falsification edit | [compat] ok — the `string`/`bool` conditional inside `(object)(…)` compiles (target-typed conditional); the tripwire fails with `…but found JsonValueKind.String`; `git diff --quiet` holds after the revert |
| T5.3 | Task 5 Step 3 retitles and comments | [existence] ok — `:117`, `:128`, `:49`, `:153` match; `npx vitest run src/widgets/HealthStrip.test.tsx` → 15 passed |
| T6.1 | Task 6 Step 1 block and placement | [compat] ok — `87914794:Program.cs:414-422` extracted verbatim and inserted between main's `app.UseHttpsRedirection();` and `app.UseAuthentication();` (`adminConsoleOrigin` is declared earlier, at merged `:166`). The placed block builds (`0 Error(s)`) and is exercised by the T6.2 run: `AdminConsoleCorsPipelineTests` `Passed: 6`, including the preflight test its comment names |
| T6.2 | Task 6 Step 2 | [totality] ok — `AdminConsoleCorsPipelineTests` `Passed: 6`; full Api.Tests `Failed: 0, Passed: 1170, Total: 1170` |
| T6.3 | Dynamic: the console endpoints behind main's listener gate (admin-api Ingress → 8081). `ListenerPortGateAsync` 404s only when `RequireListenerPort` metadata is present and `LocalPort` ≠ its port | over (gate wrongly blocks the console on 8081): [negative] ok — `git grep -n RequireListenerPort 4524630b -- Iverson.Server/Iverson.Api/Console/` → 0 hits, and `app.MapAdminConsoleEndpoints();` (tip `Program.cs:654`) carries no metadata / under (gate must admit `/health` on 8081): [existence] ok — the `/health` handler (tip `Program.cs:526`) ends `.WithMetadata(new RequireListenerPort(8081))` |
| T7.1 | Task 7 Step 1 (`nginx.conf`) | [compat] ok — after the `sed`, `nginx.conf:35` reads `connect-src 'self' ${ADMIN_API_ORIGIN} ${OIDC_ORIGIN};`, and the built image serves that directive rendered (T7.3) |
| T7.2 | Task 7 Step 2 code: `export ADMIN_API_ORIGIN="$(origin_of …)"` | → §2.1 |
| T7.3 | Task 7 Step 2: SHELL-FORMAT list and comment | [compat] ok — served CSP `connect-src 'self' http://admin-api.ci.invalid http://authentik.ci.invalid`, both as whole tokens; `/` serves 200, so `$uri` was not substituted |
| T7.4 | Task 7 Step 3 commands | [compat] ok — `sh -n` passes; the image builds, serves and is removed |
| T8.1 | Task 8 Steps 1–5: values restorations from cited branch ranges | [presence] ok — each `87914794` range, extracted, matches its description: aws `:4-8`, `:14`, `:103-116`, `:186`; azure `:4-9`, `:15`, `:167`; gcp `:5-17`, `:23`, `:175`; values `:64-70`, `:170-187`, `:239`. The keys appear in the rendered manifests (T8.5) |
| T8.2 | Task 8 Step 6: Ingress snippet | [compat] ok — local render: `grep -c "connect-src 'self' http://admin-api.iverson.local http://authentik.iverson.local;"` → 1 |
| T8.3 | Task 8 Step 7: route and comments | [compat] ok — the seven-line rule at `admin-api-ingress.yaml:51-57` is removed, and the edited template renders on all five overlays with exit 0 and `admin-api-traces=0` in each rendered `t-admin-api` Ingress (T8.5); the comment phrase occurs once each in `admin-api-ingress.yaml` and `charts/api/values.yaml` |
| T8.4 | Task 8 Step 8: compose | [compat] ok — the anchor `- Tenancy__SeedLegacyTenants=true` also occurs in `iverson-worker` (`:580`), but the plan scopes the edit to `iverson-api` (`:498`), so it is unambiguous; `docker compose config` (dummy secrets) resolves `AdminConsole__Origin` and `Prometheus__BaseUrl` |
| T8.5 | Task 8 Step 9 render loop | [totality] ok — all five renders exit 0; admin-api hosts `local=1 laptop=0 aws=1 azure=1 gcp=1`; prometheus-url `1/0/1/1/1`; `admin-api-traces=0` on every overlay; snippet grep `1` |
| T8.6 | Dynamic: the restored aws `<ACM_CERT_ARN>` vs main's placeholder guard (a matcher over ingress annotations) | over (guard wrongly fails the aws render): [compat] ok — `helm template t . -f values-aws.yaml -f values-aws.ci-override.yaml` exits 0 after Task 8 / under (guard misses the restored `api.adminApiIngress` placeholder): dropped — `_validate.tpl` scans only the `api.ingress`, `authentik.ingress` and `adminUi.ingress` annotations; extending main's guard is not in the spec and would change a main-owned file |
| T8.7 | Dynamic: main's `deploy-validate.yml` checks (kubeconform, kube-score) on the tip | dropped — kubeconform `Invalid: 0` on all five overlays. kube-score exits 1, but with the same CRITICAL counts on main's own `65cdf63a` chart (local 12, laptop 10, aws 13) and no tip-only CRITICAL objects: pre-existing, and not one of the spec's gates |
| T9.1 | Task 9 Step 1 | [compat] ok — the `:4` import, `:54` call and `:46-50` paragraph are removed; branch `onSigninCallback` (`87914794:…/AuthProvider.tsx:6-25`) is inserted before `oidcConfig` and added as its property; `npm test` (221 passed) and `npm run build` pass (T9.6); the `:148` test calls it through the provider props |
| T9.2 | Task 9 Step 2: rewritten helper and deletions | [compat] ok — `:148` and `:159` pass through the helper rewritten against `capturedOidcProps` |
| T9.3 | Task 9 Step 3 | [compat] ok — `git rm` of both files succeeds; afterwards the T9.6 grep finds 0 importers, and build and tests pass |
| T9.4 | Task 9 Step 4: `client.ts` prose edits | [compat] ok — `:120-161` removed and `:16`, `:28-40` and `:44` edited. The edited file then passes `npm run build` (exit 0) and `npm test` (221 passed, including `client.test.ts`'s retitled `reports a 401 as unauthorized`) at T9.6, and Task 12 Step 3's `tsc` lists no `client.ts` error |
| T9.5 | Task 9 Step 5 | [compat] ok — leaves `beforeEach` imported but unused. `npx tsc --noEmit`, run at Task 12 Step 3 over the whole `src/**/*` include, lists 8 errors, none in `src/api/client.test.ts` (`tsconfig.json` sets no `noUnusedLocals`) |
| T9.6 | Task 9 Step 6 | [negative] ok — `npm test` 221 passed; build exit 0; the grep for the four identifiers over `Iverson.AdminUI/src` → 0 hits |
| T9.7 | Stale renewal prose outside R4's set (`WidgetCard.tsx:39`, `usePolledResource.ts:318`, `usePolledResource.test.ts:480`) | dropped — comments only; no build, test or behaviour effect |
| T10.1 | Task 10 Step 1: failing tests | [compat] ok — `Failed to resolve import "./useSessionExpiry"` and `TypeError: onExpired is not a function` |
| T10.2 | Task 10 Steps 2–3: hook and mount | [compat] ok — `vitest run src/auth` 23 passed; `npm test` 224 passed |
| T10.3 | Task 10 Step 4 falsification | [compat] ok — without the mount, the new `AuthGate` test fails; restored, it passes |
| T10.4 | Dynamic: the expiry chain against the real library | [compat] ok — probe with the real `react-oidc-context` 3.3.1 `AuthProvider`, a real `oidc-client-ts` 3.5.0 `UserManager` (in-memory store, stored user with `expires_at = now + 2 s`) and the real `AuthGate`: `signinRedirect` is called once and the children unmount. With `useSessionExpiry();` removed, `signinRedirect` is called 0 times in 12 s |
| T11.1 | Task 11 Steps 1–3 | [compat] ok — README anchor `:29` matches; each runbook string matches once; `CONFIG_TEMPLATE`, `CONFIG_OUTPUT` and `HEADERS_OUTPUT` have 0 uses; `sh -n` passes. `IVERSON_BYPASS_PASSWORD` is the bypass user's `!Env` (`compose-only/service-clients.yaml:291`), and `scripts/generate-compose-secrets.sh:12,21` writes it to `Iverson.Server/.env` |
| T11.2 | Task 11 Step 4 | ok — informational grep; after Task 8, `values-aws.yaml:163,183` name the restored `adminApiIngress` |
| T11.3 | Task 11 Step 5: grep expectation `# no hits` (rule-like) | over: → §2.2 (4 hits in the tracked spec and plan on a correct tree) / under: [totality] ok — on the pre-Task-11 commit, the grep reports all three runbook sites it exists to catch (matrix P1) |
| T12.1 | Task 12 Steps 1–2 | [compat] ok — each edit's anchor matched once: `-e EXTERNAL_SCHEME=http -e "OIDC_ORIGIN=$IDP_ORIGIN"` added to the `:156-160` `docker run`; the `:210` `frame-src` assertion and the `:201` message clause removed. The edited job runs green (T12.3) |
| T12.2 | Task 12 Step 3 | [compat] ok — count 8: 4 × `import.meta.env`, 3 × the `future` prop, 1 × the `@fontsource/fraunces/900.css` side-effect import |
| T12.3 | Task 12 Step 4 harness, passing run | [compat] ok — Build; served CSP `OK: connect-src names http://admin-api.ci.invalid` and `…authentik.ci.invalid`; malformed value `rejected with exit 78 and nginx never started`; Clean up |
| T12.4 | Task 12 Step 4 harness: falsification run and "Remove the local image afterwards" | → §2.3 |
| T13.1 | Task 13 Step 1 | [totality] ok — build `0 Error(s)`; Api.Tests 1170 run / 0 failed (no `*.cs`/`*.csproj`/`*.slnx` change after Task 6: `git diff --name-only` → empty); Vector 170/0; `npm test` 224 passed; tsc 8 == BASELINE 8 |
| T13.2 | Task 13 Step 2 | [compat] ok — all five renders exit 0 |
| T13.3 | Task 13 Step 3: R1 and R5 proofs | [compat] ok — R1: both ownership checks removed → `Failed: 3`; restored → `Passed: 3`. R5: see T10.3 |
| T13.4 | Task 13 Step 3: R3 proof (re-run of Task 12 Step 4) | → §2.3 |
| T13.5 | Task 13 Step 4: rate-limit reading | dropped — the step's `grep -rn "intervalMs"` hits only comments and a parameter, but the step is completable by reading. Cadences come from the `*_POLL_INTERVAL_MS` constants (health 60 s, Qdrant 30 s, Metrics 30 s; tenants and data-volume unpolled), about 5 req/min per tab against `PermitLimit = 6_000` (`Program.cs:132`) |
| T13.6 | Task 13 Step 5 audit rule (rule-like) | over: → §2.4 / under: [totality] ok — blame over all 34 removed-line hunks in the 26 listed files finds main-authored lines only at the six sanctioned sites, and the re-addition probe over the same 26 files finds 0 (matrix P2, both covered and residual sets) |
| T13.7 | Task 13 Step 6: clean tree | → §2.3 |

**Cross-task interface contracts**

| # | Contract | Disposition |
|---|---|---|
| X1 | Task 1 → Task 2: `GetSchema` delegates to `SchemaCatalogReader.BuildCatalog` | [compat] ok — after Task 1, `GetSchema`'s body is the single `SchemaCatalogReader.BuildCatalog(…)` call (T1.3). Main's gRPC arm, which drives `GetSchema`, fails on Task 1's tree and passes once Task 2 adds the filter inside `BuildCatalog` (T2.1, T2.2) |
| X2 | Task 3 → Task 5: the tripwire replaces the deleted wire-format coupling | ok — T5.1 |
| X3 | Task 7 → Task 11: entrypoint line targets | [existence] ok — Task 7 inserts at `:80`, so Task 11's `:23-25` and `:3-6` do not shift |
| X4 | Task 8 → Task 11 Step 4: `api.adminApiIngress` restored on aws | [presence] ok — dump of the Task 8 commit's `values-aws.yaml` (`grep -n "adminApiIngress\|adminConsoleOrigin"`): `106:  adminApiIngress:`, `114:  adminConsoleOrigin: "https://iverson.example.com"`, plus the two comments at `163` and `183` that Task 11 Step 4 checks; the aws render carries one `admin-api.` host (T8.5) |
| X5 | Task 9 → Task 10: `AuthGate` without `useTokenRenewal()` | ok — T10.2 |
| X6 | Tasks 7, 9, 10, 11 → Task 12: image contents and tsc count | ok — T12.2 and T12.3 |
| X7 | Task 12 → Task 13 Step 1: `BASELINE` | [compat] ok — `npx tsc --noEmit … \| grep -cE "error TS[0-9]+"` measured 8 at Task 12 Step 3 and 8 again at Task 13 Step 1; `admin-ui.yml:100` reads `BASELINE=8` |
| X8 | The Task 12 Step 4 harness has three call sites: the Task 12 passing run, the Task 12 falsification run, and the Task 13 Step 3 re-run | → §2.3 (residue at all three) |
| X9 | The Task 8 Step 9 loop, at Task 8 and at Task 13 Step 2 | [compat] ok — both runs render all five overlays with exit 0; outputs go to `/tmp/render-*.yaml`, and `git status --porcelain` shows no new paths afterwards |
| X10 | Persistence boundary: Task 11 Step 5 greps the tracked tree, which includes the force-added spec (`c6f313c1`…`113106d7`) and plan (`a900eb70`) | → §2.2 |

**Population-closure matrices**

- **P1 — §2.2's rule ("no tracked file outside `docs/criticalreviews` names either static password") × every tracked file, after Task 11:**
  - **Over** (hits that must not happen on a correct tree):
    - plan `:930`, `:944`: over: → §2.2
    - spec `:311`, `:312`: over: → §2.2
    - every other tracked file: over: [totality] ok — `git grep -n` over the full tree (excluding `docs/criticalreviews`) returns only those four lines
  - **Under** (sites the rule must still catch):
    - the three former runbook sites (`admin-console-landing-page-usage.md:67`, `:79`; `operator-access-onboarding.md:21`): under: [totality] ok — the plan's grep and the corrected pathspec both report all three on the pre-Task-11 commit (`git grep -c`: `…usage.md:2`, `…onboarding.md:1`), and both report 0 for them after Task 11.
- **P2 — §2.4's rule ("any non-additive, off-roster change in a main-changed file is an override of a main decision") × the real population.** The population is 26 files: 34 hunks that remove `65cdf63a` lines, spread over 15 files, plus 11 additive-only files. Each hunk was classified with `git blame 65cdf63a` plus `git merge-base --is-ancestor <commit> 9eb99f76`. Line numbers below are `65cdf63a`'s:
  - Direction key: *over* = the rule reports a change that overrides no main decision; *under* = the rule misses a real override.
  - under: Roster or R4-sanctioned, main-authored — `docker-entrypoint.sh:22`, `:28`; `nginx.conf:35`; `AuthProvider.test.tsx:16` (R4's import extension); `ObjectMappingGrpcService.cs:72-112` (5 main-authored lines, `6509b6be`'s filter, carried over by Task 2); `charts/admin-ui/templates/ingress.yaml:42`: [totality] ok — blame at `65cdf63a` attributes each removed line to a post-fork main commit, and every one falls in a site on Step 5's roster or in R4's `onSigninCallback` insertion.
  - over: Roster, pre-fork — `ObjectMappingGrpcService.cs:114-144`, `:149-245` (R1's dead body and the moved helpers); `apiBaseUrl` at `values-aws:150`, `values-azure:141`, `values-gcp:142`, `values-local:147`, `values:238`: [totality] ok — blame gives 0 post-fork lines in these ranges, and each is on Step 5's roster (R1's `GetSchema`, R3's five `apiBaseUrl` values).
  - over: Off-roster, non-additive, pre-fork — 21 hunks in 7 files → §2.4 (reported as overrides; 0 main-authored lines):
    - `README.md:23`, `:55`
    - `ObjectSearchGrpcService.cs:767-768`, `:773`
    - `networkpolicies.yaml:13-14`, `:29-32`, `:161-164`, `:476`, `:508`, `:539-542`
    - `values-aws.yaml:142-146`
    - `values-laptop.yaml:11`, `:29-30`
    - `values.yaml:211`
    - `user-management-and-security.md:112-115`, `:128-132`, `:137-139`, `:239-240`, `:242-243`, `:245`, `:247-249`
  - under: The 11 additive-only files: [totality] ok — a probe over all 26 files for tip-added lines that main deleted since `9eb99f76` finds 0 (`TOTAL 0`).
- **P3 — §2.1's family (command substitution whose status `export` masks, in plan code).** *under* = a masking site the sweep misses; *over* = a harmless `$(…)` flagged as masking.
  - under: Task 7 Step 2 (plan `:621`): → §2.1
  - under: the merged entrypoint's other `export`: [negative] ok — `HSTS_LINE` is assigned a literal on both branches of the `if` (merged `docker-entrypoint.sh:94-98`), so no other masking site
  - over: every other `$(…)` in the plan's code blocks: [negative] ok — `grep -nE 'export |\$\(' <plan>` finds them only as `printf` arguments (`:696-698`) and a pathspec (`:1027`), none under `export`, so correctly excluded
- **P4 — §2.3's family (plan commands whose side effects outlive them).** *under* = residue the sweep misses; *over* = a harmless side effect flagged as residue.
  - under: the three Task 12 Step 4 call sites: → §2.3
  - over: Task 1 Step 8: [negative] ok — after `npm ci && npm run build` and `dotnet build`, `git status --porcelain` shows no `??` paths (`node_modules`, `dist`, `bin` and `obj` are gitignored)
  - over: Task 7 Step 3: [compat] ok — run: `docker rm -f adminui-t7 && docker rmi adminui-t7` removes both; the image list diff afterwards shows only a dangling build-stage layer, which Task 13 Step 6's `adminui-*` criterion does not cover
  - over: Task 8 Step 9: [negative] ok — writes only `/tmp/render-*.yaml`, outside the repo; `git status --porcelain` shows no new paths
  - under: every other plan command: [totality] ok — after all 13 tasks had run, `git status --porcelain` listed exactly `?? directives.txt` and `?? headers.txt` (the §2.3 files). The only leftover container during the run was §2.3's `iverson-adminui-ci`, removed while reproducing that finding, and the final `docker ps -a` comparison against the pre-run snapshot showed no other container. So no other command left residue

## 1. Verified-plan-assumptions cross-check

1. A1 — [existence] still holds — `Iverson.slnx:14,29`; `65cdf63a:.github/workflows/dotnet-build.yml:19-20` build and test `Iverson.slnx`.
2. C4 — [totality] still holds — re-run: one `UD` after `-X theirs`, 0 unmerged after `git rm`.
3. D1 — [compat] still holds — re-measured on the Task 1 commit with the three files restored: 4 × CS0535 at `HealthCheckCacheTests.cs:239` (×3) and `:278`; with that file removed, 1 × CS0246 at `OperationalListenerBindingPipelineTests.cs:135` (`HttpListenerOnly`).
4. F5 — [negative] still holds — `git grep HttpListenerOnly` on the merge commit's tree → 0; on `a900eb70` it is only in `Program.cs` (replaced by main's side) and that test.
5. F4 — [negative] still holds — after Task 1, `HealthCheckCache` appears only in the `TenantStatusCache.cs:13` comment (rewritten in Task 3); the solution builds.
6. Measurement (Task 1 test outcome) — [totality] still holds — re-run: 1186 run / 8 failed and 170 run / 1 failed, with exactly the named tests.
7. A2 — [existence] still holds — `ObjectMappingGrpcServiceTests.cs:596`; fails on Task 1's tree.
8. B1 — [existence] still holds — `SchemaCatalogReader.cs:60` `foreach`, then `Evaluate`.
9. B2 — [existence] still holds — `AggregateReader.cs:52-58`.
10. B3 — [existence] still holds — `ObjectMappingGrpcServiceTests.cs:604-615` (`RegisterAsync(… with { TypeName = "TenantAWidget", OwnerTenantId = "tenant-a" })`); `SchemaRegistry.cs:238`.
11. B4 — [existence] still holds — `AdminConsoleDataVolumeEndpointTests.cs:50-57`.
12. B5 — [compat] still holds — `AdminConsoleEndpoints.cs:108`, `:169`; `UpsertAsync` returns `Task.CompletedTask`; both arms ran.
13. F1/F2 — [negative] still holds — the only callers of `BuildCatalog`/`ReadCatalog`/`CountRowsAsync` are `ObjectMappingGrpcService.cs:76` and `AdminConsoleEndpoints.cs:118`, `:186`; the full suite is 1170/0 after the filter.
14. Design (handler-level arms) — [compat] still holds — `AdminConsoleEndpointsPipelineTests` stays green in the full run.
15. A3 — [existence] still holds — the deletions ran; build clean.
16. A4 — [existence] still holds — `TenantStatusCache.cs:12-15`.
17. A5 — [existence] still holds — `:44` `"test-api-key"`; `ServiceCollectionExtensions.cs:27` `< 32`; `:18`, `:56` and `:74` use the 32-byte value.
18. A6 — [existence] still holds — the `HealthCacheTestFactory` pattern at `AuthenticationPipelineTests.cs:293-345`.
19. B9 — [existence] still holds — `EngagementHealthStatus.cs:10` `AuthPending`; `EngagementStoreOptions.cs:12` defaults to `true`. [negative] No test overrides `Engagement:Enabled` for this host: `grep -rn "Engagement__Enabled\|Engagement:Enabled\|EngagementStoreOptions" Iverson.Api.Tests` hits only a doc comment (`AdminConsoleDataVolumeEndpointTests.cs:29`) and a consumer unit test's in-memory config (`EngagementStoreConsumerTests.cs:521`), neither of which is `AuthTestWebApplicationFactory`.
20. Code (missing `using System.Text.Json;`) — [absence] still holds — dump of merged `AuthenticationPipelineTests.cs:1-17`: `System.Linq`, `System.Net`, `System.Net.Http.Headers`, `System.Security.Claims`, `FluentAssertions`, `Iverson.Api.Tests.Helpers`, `Iverson.Events`, `Iverson.Sql`, `Iverson.StarRocks`, `Iverson.Vector`, `Microsoft.AspNetCore.Hosting`, `Microsoft.AspNetCore.Http`, `Microsoft.AspNetCore.Routing`, `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.DependencyInjection.Extensions`, `NSubstitute`, `Xunit` — no `System.Text.Json`.
21. A7 — [existence] still holds — `HealthStrip.test.tsx:42`, `:117`, `:128`, `:149`.
22. A8 — [totality] still holds. In the full Api.Tests run on Task 1's tree (1186 tests, 8 failed), the covered set is exactly four `AdminConsoleCorsPipelineTests`: `ConfiguredOrigin_CrossOriginGet_HealthLive_ReturnsOkWithMatchingAllowOrigin`, `…_ExposesTraceIdHeader`, `…_CarriesNoAllowCredentialsHeader` and `ConfiguredOrigin_PreflightOptions_AdminDlq_AnsweredByCorsNotFallbackPolicy`. The residual set is the other four failures (the gRPC arm and three probe tests), none of them CORS. After Task 6, the class runs 6 passed.
23. Code (`UseCors` block) — [existence] still holds — `87914794:Program.cs:414-422`; `65cdf63a:Program.cs:436-440`; `:100` is the branch-tip line (on the merged tree the declaration is at `:166`, still ahead of the pipeline).
24. A9/E3 — [existence] still holds — merged `docker-entrypoint.sh:69-75` and `:106`; `nginx.conf:35`.
25. A10 — [presence] still holds — a key grep over the six merged values files (`^  (adminApiIngress\|adminConsoleOrigin\|apiBaseUrl\|prometheusEnabled\|clusterCidrs):` plus `clusterCidrs\|prometheusEnabled`) prints `adminApiIngress`/`adminConsoleOrigin` at azure `:87`, `:95`; gcp `:88`, `:96`; local `:121`, `:125`. [absence] The same dump prints neither key for `values.yaml` or aws; `clusterCidrs` only for local `:16` and laptop `:19`; and `prometheusEnabled` only for local `:22` and laptop `:24`. `apiBaseUrl` is main's value in the five files (`values:252`, aws `:158`, azure `:150`, gcp `:151`, local `:168`).
26. Code (branch-tip ranges) — [presence] still holds — each range was dumped with `git show 87914794:<path>` and sliced to the cited lines; for example aws `:14` is `  prometheusEnabled: true   # single source of truth — see values.yaml's comment on this flag`, azure `:4-9` ends `networkPolicy:` / `  clusterCidrs: ["10.1.0.0/16"]`, and values `:170-187` ends `  adminConsoleOrigin: ""` (all ranges listed in T8.1).
27. F7 — [compat] still holds — the renders consume the keys (T8.5). The deployment guards are at merged `charts/api/templates/deployment.yaml:177` and `:181`.
28. E2 — [existence] still holds — `charts/admin-ui/templates/ingress.yaml:27`.
29. F6 — [negative] still holds — `/v1/traces` readers outside the edited sites: `TracesRelayEndpointTests.cs` (test host) and main's `charts/api/templates/ingress.yaml:95` (port 8080); none in `.github/`.
30. A11 — [existence] still holds — merged `docker-compose.yml:498`; `87914794:…:474-484`.
31. A12 — [existence] still holds — `AuthProvider.tsx:4`, `:54`, `:46-50`; test file `:8`, `:18`, `:105`, `:132`, `:139`, `:148`, `:159`.
32. F3 — [totality] still holds — `command grep -rn` for the four identifiers over merged `src/` finds 40 lines, all in `AuthProvider.tsx`, `AuthProvider.test.tsx`, `useTokenRenewal.{ts,test.ts}`, `client.ts` and `client.test.ts`.
33. Code (`client.ts` doc sites) — [existence] still holds — `:16`, `:28-40`, `:44`.
34. Code (`client.test.ts` tests) — [existence] still holds — `:119`, `:143`, `:154`, `:173`, `:190`, `:245`.
35. B6/E4 — [existence] still holds — `oidc-client-ts.d.ts:30` `addAccessTokenExpired(cb: AccessTokenCallback): () => void`; `react-oidc-context` d.ts `:31` `events`, `:33` `removeUser()`; installed 3.3.1 and 3.5.0.
36. F8 — [existence] still holds — `AuthProvider.test.tsx:54-103` mock `useAuth` without `events`.
37. A13 — [absence] still holds. Dump of the tree Task 10 edits, `git ls-tree --name-only 9ae2e551 Iverson.AdminUI/src/auth/` (the Task 1 merge commit, unchanged there through Task 9): `AuthProvider.test.tsx`, `AuthProvider.tsx`, `CallbackPage.tsx`, `RequireGroup.test.tsx`, `RequireGroup.tsx`, `groups.ts` — no `useSessionExpiry.*`. The plan's listing of the directory is incomplete, but that half is not load-bearing.
38. B7/E5 — [existence] still holds — `@testing-library/react` 16.3.2; vitest 5.0.0; `vitest.config.ts:16` `fileParallelism: false`.
39. C2 — [existence] still holds — `test` = `vitest run`, `build` = `vite build`.
40. A14 — [existence] still holds — `README.md:23`, `:55` (`8081`); `:27-29`; runbook `:67`, `:70`, `:79`; `operator-access-onboarding.md:21`; `generate-compose-secrets.sh:12`, `:21`.
41. Code (entrypoint leftovers) — [negative] still holds — 0 uses of each name; the header at `:3-6`.
42. Code (`values-aws` comments) — [existence] still holds — they name `adminApiIngress`, which is present after Task 8.
43. A15 — [existence] still holds — `admin-ui.yml:100`, `:143`, `:156-160`, `:201`, `:210`; jobs and env as cited.
44. C3 — [existence] still holds — `helm version` v3.16.4; `admin-api-ingress.yaml:23`.
45. C5 — [existence] still holds — `41e01848` `merge main into admin-console-landing-page`.
46. A16 — [existence] still holds — `.gitignore:49` `**/docs/plans/`.
47. D2–D6 — [compat] still holds — executed in plan order; every task consumed its predecessors' output (X1–X7).

Span check (plan dependencies with no covering assumption):

- Task 7's call form propagates `origin_of`'s exit status. Uncovered; verified false in-round → §2.1.
- Task 11 Step 5's grep population contains no tracked file that quotes the passwords. Uncovered: the inherited row measured tree `06b78f70`, before the spec and plan were committed. Verified false → §2.2.
- Task 12 Step 4's local harness reproduces CI's `if: always()` clean-up and ephemeral workspace. Uncovered; verified false → §2.3.
- Task 13 Step 5's file-granular diff identifies main decisions. Uncovered; verified false → §2.4.
- PyYAML is importable for Task 12 Step 4. Uncovered; [compat] verified — `python3 -c "import yaml"` reports `pyyaml 6.0.3`, and the harness ran.
- Task 5 Step 2's falsification edit compiles. Uncovered; [compat] verified by build (T5.2).
- `docker` on this host is podman 5.7.0. Uncovered; [compat] verified — every docker command in Tasks 7 and 12 ran as written (build, run, rm, rmi).
- Local Node 22.16.0 is below the merged `engines` floor. Covered by the inherited EBADENGINE row; [compat] verified — `npm ci` exits 0 with `npm warn EBADENGINE` only.
- `useSessionExpiry` works against the real library. Inherited row (trusted); [compat] additionally verified by run (T10.4).

## 2. Literal-wrongness findings

1. **Task 7 Step 2's `export ADMIN_API_ORIGIN="$(origin_of API_BASE_URL "$API_BASE_URL")"` hides `origin_of`'s refusal. A scheme-less `API_BASE_URL` then serves a CSP with the admin-api origin silently missing, instead of stopping the container.**
   - **Mechanism.** In `sh`, `export NAME="$(cmd)"` returns `export`'s status (0), not `cmd`'s. `origin_of`'s `fail` → `exit 78` leaves only the command-substitution subshell, so `set -eu` never fires.
   - **The input that reaches it.** `API_BASE_URL=admin-api.iverson.local` passes `validate`, because every character is in `[A-Za-z0-9:/._-]`. It is exactly the input `origin_of` exists to reject. Its comment (merged `docker-entrypoint.sh:62-68`) says "Failing loudly when there is no scheme beats emitting a policy that silently omits an origin", and the file header (`:18-20`) calls stopping the container "deliberate and load-bearing".
   - **Why this is a regression.** The branch derived the origin with the propagating form (`87914794:Iverson.AdminUI/docker-entrypoint.sh:84-86`: `ADMIN_API_ORIGIN=$(origin_of API_BASE_URL "$API_BASE_URL")`, then `export ADMIN_API_ORIGIN OIDC_ORIGIN`). R3's re-application therefore loses the branch's fail-closed behaviour.
   - **Evidence (run).** Image built from Task 7's tree, started with `-e API_BASE_URL=admin-api.ci.invalid` (other env as in Task 7 Step 3): container `running exit=0`, serving `Content-Security-Policy: default-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'  http://authentik.ci.invalid; …`, with an empty admin-api slot.
   - **Proposed fix.** In Task 7 Step 2, replace the one added line with the branch's two-line form:

     ```sh
     ADMIN_API_ORIGIN=$(origin_of API_BASE_URL "$API_BASE_URL")
     export ADMIN_API_ORIGIN
     ```

   Evidence: [compat] run — the same image, with only this change mounted as `/docker-entrypoint.d/40-admin-ui-config.sh`:
   - scheme-less `API_BASE_URL` → `exited exit=78`, logging `admin-ui entrypoint: API_BASE_URL ('admin-api.ci.invalid') is not an absolute URL; cannot derive its origin for the Content-Security-Policy`;
   - `API_BASE_URL=http://admin-api.ci.invalid` → `connect-src 'self' http://admin-api.ci.invalid http://authentik.ci.invalid`, identical to the plan's happy path.
   - The falsifier: if busybox `sh` propagated the substitution's status through `export`, the plan-form container would also have exited 78; it stayed `running`.

2. **Task 11 Step 5's `git grep -n "dev-only-not-for-production-bypass-password\|dev-admin-password" -- ':!docs/criticalreviews'   # no hits` cannot come out empty. The branch tracks the spec and this plan, and both quote the two strings.**
   - **Evidence (run on Task 11's tree).** Four hits: `docs/plans/2026-09-23-admin-console-main-merge-implementation-plan.md:930`, `:944` and `docs/specs/2026-09-22-admin-console-main-merge-resolution-design.md:311`, `:312`.
   - **Why the plan expected none.** The inherited row the step relies on ("finds exactly four sites, all in branch-only runbooks") was measured over the pure merge tree `06b78f70`. That tree predates force-adding the spec (`c6f313c1`) and the plan (`a900eb70`). As written, the step fails on a correct tree.
   - **Proposed fix.** Change the Step 5 grep to exclude the two artifacts by name:

     ```bash
     git grep -n "dev-only-not-for-production-bypass-password\|dev-admin-password" -- ':!docs/criticalreviews' ':!docs/specs/2026-09-22-admin-console-main-merge-resolution-design.md' ':!docs/plans/2026-09-23-admin-console-main-merge-implementation-plan.md'   # no hits
     ```

   Evidence: [totality] run —
   - on Task 11's tree: exit 1, no output;
   - the same pathspec with `git grep -c` against the pre-Task-11 commit: `docs/runbooks/admin-console-landing-page-usage.md:2` and `docs/runbooks/operator-access-onboarding.md:1`, so it still catches every runbook site it exists to catch (P1).

3. **Task 12 Step 4's local harness leaves state behind that makes three later steps fail on a correct tree.**
   - **(a) The falsification run.** `subprocess.run(…, check=True)` aborts at the failing served-CSP step, so the workflow's `Clean up` step (`if: always()`, `admin-ui.yml:258-262`) never runs. Container `iverson-adminui-ci` stays up on `127.0.0.1:8099`, using `iverson-adminui-ci:local`. Two consequences:
     - the plan's "Remove the local image afterwards" fails: `docker rmi iverson-adminui-ci:local` → `image is in use by a container`, exit 2;
     - Task 13 Step 3's re-run of this harness fails at the served-CSP step: `the container name "iverson-adminui-ci" is already in use`.
   - **(b) Every run.** The served-CSP step writes `headers.txt` and `directives.txt` into its working directory (`admin-ui.yml:176`, `:187`). Locally that is the repo root; in CI it is an ephemeral workspace. Task 13 Step 6's `git status --porcelain` then prints `?? directives.txt` and `?? headers.txt`.
   - **Evidence.** All three failures were reproduced by running the plan's harness verbatim, with the messages quoted above.
   - **Proposed fix.** In Task 12 Step 4, replace the harness block with the version below, which runs the `if: always()` steps in a `finally` and deletes the step's scratch files. Also delete the sentence "Remove the local image afterwards." Task 13 Step 3 re-runs this block, so it inherits the clean-up.

     ```python
     python3 - <<'PY'
     import os, subprocess, yaml
     job = yaml.safe_load(open(".github/workflows/admin-ui.yml"))["jobs"]["image-contract"]
     env = {**os.environ, **{k: str(v) for k, v in job["env"].items()}, "IMAGE": "iverson-adminui-ci:local"}
     always = [s for s in job["steps"] if s.get("if") == "always()"]
     try:
         for step in job["steps"]:
             if "run" in step and step not in always:
                 print("::step::", step.get("name"), flush=True)
                 subprocess.run(["bash", "-e", "-c", step["run"]], env=env, check=True)
     finally:
         for step in always:
             subprocess.run(["bash", "-c", step["run"]], env=env)
         for f in ("headers.txt", "directives.txt"):
             if os.path.exists(f):
                 os.remove(f)
     PY
     ```

   Evidence: [compat] run —
   - on the falsified tree (`${ADMIN_API_ORIGIN} ` removed from `nginx.conf:35`): exit 1 with `::error::the Content-Security-Policy's connect-src does not name http://admin-api.ci.invalid…`, then 0 `adminui-ci` containers, 0 `adminui-ci` images and an empty `git status --porcelain`;
   - on the restored tree: exit 0, with both `OK: connect-src names …` lines and `rejected with exit 78 and nginx never started`, and the same zero leftovers.

4. **Task 13 Step 5's audit rule reports 21 non-overrides as main-decision overrides. The final gate therefore fails on a tree where "no main decision is overridden" actually holds.**
   - **The rule.** It is file-granular. For each main-changed file, any change that is neither on the roster nor an additive insertion "is an override of a main decision: report it", and Task 13 says "stop and report on any failure".
   - **Why that over-reports.** A main decision is a line main changed after the fork. Branch edits to pre-fork lines inside a main-touched file merge cleanly and override nothing. The spec's own inherited row says exactly this of the README port lines ("not a main-line change"), and `113106d7` withdrew them from the roster for that reason.
   - **Evidence (run on the reproduced tip).** The Step 5 command lists 26 files, and 34 hunks remove `65cdf63a` lines. Classifying each removed line with `git blame 65cdf63a` and `git merge-base --is-ancestor <commit> 9eb99f76` shows that 21 hunks, in 7 files, remove only pre-fork lines while being neither roster nor additive (full matrix in §0 P2):
     - `README.md:23`, `:55`
     - `ObjectSearchGrpcService.cs:767-768`, `:773`
     - `networkpolicies.yaml` × 6
     - `values-aws.yaml:142-146`
     - `values-laptop.yaml` × 2
     - `values.yaml:211`
     - `user-management-and-security.md` × 7

     Step 5 as written reports every one of them. The main-authored lines the tip changes are only the sanctioned sites, and 0 lines main deleted are re-added.
   - **Proposed fix.** Replace Step 5's command and rule with this hunk-granular check:

     ```bash
     python3 - <<'PY'
     import os, re, subprocess
     tip = os.environ.get("TIP", "HEAD")
     sh = lambda *a: subprocess.run(a, capture_output=True, text=True).stdout
     pre = {}
     def prefork(c):
         if c not in pre: pre[c] = subprocess.run(["git","merge-base","--is-ancestor",c,"9eb99f76"]).returncode == 0
         return pre[c]
     files = sh("git","diff","--name-only",f"65cdf63a..{tip}","--",*sh("git","diff","--name-only","9eb99f76","65cdf63a").split()).split()
     for f in files:
         d = sh("git","diff","-U0","65cdf63a",tip,"--",f)
         for m in re.finditer(r"^@@ -(\d+)(?:,(\d+))? ", d, re.M):
             a, b = int(m[1]), int(m[2] or 1)
             if b == 0: continue
             for l in sh("git","blame","-l","-s","-L",f"{a},{a+b-1}","65cdf63a","--",f).splitlines():
                 sha, rest = l.split(" ", 1)
                 if not prefork(sha.lstrip("^")): print("MAIN LINE CHANGED:", f, rest.strip()[:90])
         gone = {l[1:].strip() for l in sh("git","diff","-U0","9eb99f76","65cdf63a","--",f).splitlines() if l[:1] == "-" and l[:3] != "---"}
         for l in d.splitlines():
             s = l[1:].strip()
             if l[:1] == "+" and l[:3] != "+++" and len(s) > 3 and s in gone: print("MAIN-DELETED LINE RE-ADDED:", f, s[:90])
     PY
     ```

     Expected output, with no `MAIN-DELETED LINE RE-ADDED` line: exactly ten `MAIN LINE CHANGED` lines, for these sites:
     - `Iverson.AdminUI/docker-entrypoint.sh` `22)` and `28)`
     - `Iverson.AdminUI/nginx.conf` `35)`
     - `Iverson.AdminUI/src/auth/AuthProvider.test.tsx` `16)` (the import R4 extends with `onSigninCallback`)
     - `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` `82)`, `83)`, `86)`, `87)`, `88)` (`6509b6be`'s filter, carried over by Task 2)
     - `Iverson.Server/deploy/helm/iverson/charts/admin-ui/templates/ingress.yaml` `42)`

     Any other `MAIN LINE CHANGED` line, or any `MAIN-DELETED LINE RE-ADDED` line, is an override of a main decision: report it.

   Evidence: [bidirectional] run —
   - Over direction: on the reproduced tip, the script prints exactly those ten lines and no `RE-ADDED` line, so none of the 21 pre-fork hunks is reported.
   - Under direction: run with `TIP=a900eb70` (the unmerged branch, which does override main), it prints 68,303 `MAIN LINE CHANGED` lines and 6,249 `MAIN-DELETED LINE RE-ADDED` lines. These include main's `does not request the offline_access scope` test at `AuthProvider.test.tsx:23` and the Dockerfile's re-added `COPY Iverson.AdminUI/nginx.conf /etc/nginx/conf.d/default.conf`, so both detectors fire on real overrides.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ Approve with literal-wrongness fixes
