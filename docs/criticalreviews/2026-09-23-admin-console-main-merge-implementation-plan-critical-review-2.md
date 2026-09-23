# Critical Implementation Review: 2026-09-23-admin-console-main-merge-implementation-plan (Round 2)

**Plan:** /home/ben/repositories/Iverson/.worktrees/admin-console-landing-page/docs/plans/2026-09-23-admin-console-main-merge-implementation-plan.md
**Artifact HEAD at review:** 7ab2ba5bc4ffb3d9c86df1701c009bb8ec73a757
**Verified plan-level assumptions section:** present

⚠️ 2 commits since plan-write time (SHA 113106d7); cited file:line references re-checked under §1.

## 0. Coverage enumeration

**How this round was run.** Every task was executed as written, in order, in a throwaway detached worktree at `7ab2ba5b`. That covers Task 1's merge of `65cdf63a` (commit `bdb21aff`, parents `7ab2ba5b` and `65cdf63a`) and the code blocks of Tasks 2–12 (tip `a89056e4`). The four blocks round 1 replaced were copied from the plan and run verbatim. Task 13's gates ran on that tip. Rows cite this run unless marked otherwise. On this host `docker` is podman 5.7.0, and `grep` is a shell function (ugrep). Plan code is identical between the branch tip `87914794` and `7ab2ba5b` (`git diff --stat 87914794 7ab2ba5b` touches only the spec and the plan).

The §0 row set below was drafted from a full read of the plan before round 1's diff was opened; the three mandatory row families come first.

**(a) Fix neighbourhoods — round 1's fix sites and their restatements**

| # | Surface | Disposition |
|---|---|---|
| FN1 | Round-1 §2.1 fix site, Task 7 Step 2 (`ADMIN_API_ORIGIN=$(origin_of …)` then `export ADMIN_API_ORIGIN`). Restatements: V-row 51 (export masking), V-row 24 (A9/E3, "Round 3 built and served exactly this edit"), the Task 7 Step 4 commit message, and Task 13 Step 5's expected `docker-entrypoint.sh` lines | [compat] ok — the plan's text, built into an image and entered through the real entrypoint with `API_BASE_URL=admin-api.ci.invalid`: `status=exited exit=78`, logging `admin-ui entrypoint: API_BASE_URL ('admin-api.ci.invalid') is not an absolute URL…`. Happy path: served `connect-src 'self' http://admin-api.ci.invalid http://authentik.ci.invalid` (T7.3). Restatements: V-row 51 re-run under `/bin/sh` (dash): the one-statement form printed `reached end: []` and exited 0, the two-line form exited 78. The commit message is accurate. Task 13 Step 5's entrypoint lines are unchanged (IF1). V-row 24's "exactly this edit" describes the spec-round form, not the fixed one: dropped — evidence prose, and the edit itself is run-verified above |
| FN2 | Round-1 §2.2 fix site, Task 11 Step 5 grep pathspec. Restatement: V-row 52 | [bidirectional] over: ok — on Task 11's tree the plan's grep exits 1 with no output; without the two exclusions the residual is exactly plan `:938`, `:952` and spec `:311`, `:312` / under: ok — the same pathspec on the Task 10 commit gives `docs/runbooks/admin-console-landing-page-usage.md:2`, `docs/runbooks/operator-access-onboarding.md:1` |
| FN3 | Round-1 §2.3 fix site, Task 12 Step 4 harness and its "Expected" sentence. Restatements: V-row 53, Task 13 Step 3 (R3 re-run), Task 13 Step 6. Rule-like: `always = [s … if s.get("if") == "always()"]` | [compat] ok — the harness run is `diff`-identical to the plan block. Selection rule, over: ok — a YAML parse of `image-contract` shows `Clean up` as the only step with an `if` (value `'always()'`) / under: ok — no other step carries `if`. Call sites: the passing run exits 0 with both `OK: connect-src names …` lines and `rejected with exit 78 and nginx never started` (md5 `b0f9efed`); the falsification run exits 1 with `::error::the Content-Security-Policy's connect-src does not name http://admin-api.ci.invalid` (md5 `24ceb9e1`); the Task 13 re-run exits 0. After each run: 0 `adminui` containers, 0 `adminui` images, and `git status --porcelain` shows only the intended workflow edit (nothing after the Task 12 commit) |
| FN4 | Round-1 §2.4 fix site, Task 13 Step 5 audit script and its expected ten lines. Restatements: V-row 54, Global Constraints "checkable property", Architecture "auditable as 'main wins'". Rule-like | [bidirectional] over: ok — on the tip it prints exactly the ten expected lines and 0 `RE-ADDED` (md5 `aa1f9f2e`). An instrumented re-run finds 26 files, 34 removal hunks and 229 blamed lines, with 0 blame failures and 0 short outputs, so `sh()` swallowing errors dropped nothing. Main's file list has 0 renames (`git diff --name-status 9eb99f76 65cdf63a \| grep -c '^R'`) and 0 of its 448 paths contain a space, so `--name-only` plus `.split()` loses no path / under: ok — with `TIP=7ab2ba5b` (unmerged): 62,054 `MAIN LINE CHANGED` and 6,249 `RE-ADDED`, including `AuthProvider.test.tsx 23)` (main's offline_access test) and `RE-ADDED: Iverson.AdminUI/Dockerfile COPY Iverson.AdminUI/nginx.conf …`. The restatements agree with the line-level rule |
| FN5 | Round-1 span lines recorded as V-rows 48–50 (PyYAML, podman, Task 5 falsification compiles) | [compat] ok — `pyyaml 6.0.3`; `podman version 5.7.0`, and every docker command in Tasks 7 and 12 ran; the Task 5 Step 2 edit compiled and tripped (T5.2) |

**(b) Intersected fix texts**

| # | Intersection | Disposition |
|---|---|---|
| IF1 | Fix 4's expected ten lines were measured on a tip built with fix 1's pre-fix one-statement Task 7 form | [compat] ok — re-run on a tip built with the two-line form gives the identical ten lines; the added lines remove no `65cdf63a` line |
| IF2 | Fix 2's pathspec × tracked files that quote the passwords since round 1 | [totality] ok — a full-tree `git grep` on Task 11's tree (excluding only `docs/criticalreviews`) returns only the four spec/plan lines (FN2) |
| IF3 | Paired-artifact probe results: CDR-3 row I7's over-direction ("[presence] … the step fails on exit 0 or exit 124 (`:241–249`)") and CDR-4 row 11 ("over: n/a — R8 does not touch it"), both about the malformed-value step × this round's run of that step against the regression it guards | → §2.1 (the regression now exits 2, which neither arm catches) |
| IF4 | CDR-3's M-E closure (consumers that start the image × the entrypoint's env contract) counted `admin-ui.yml image-contract` as one consumer; R8 item 1's fix covers one of its two container starts | → §2.1 (matrix P1) |

**(c) Amendment hunks since round 1's anchor**

| # | Check | Disposition |
|---|---|---|
| AH1 | Previous anchor: SHA `a900eb70` | [totality] ok. The content-identity check is unequal: `git rev-parse a900eb70:<plan>` = `d5856617…`, `git hash-object <plan>` = `9c94b9d5…` (= `HEAD:<plan>`). The authoritative hunk set, `git diff a900eb70 -- <plan>`, has 5 hunks: +7 V-rows after D2-D6, Task 7 Step 2, Task 11 Step 5, Task 12 Step 4 (plus its Expected sentence), and Task 13 Step 5 (plus a rule paragraph and Expected). The forward window `git log a900eb70..HEAD -- <plan>` is exactly `7ab2ba5b applied 7 fixes from 2026-09-23-…-critical-review-1 to …`, the in-band shape. The reverse window is empty, and `a900eb70` is an ancestor of HEAD. Every hunk maps to a round-1 §2 fix (4) or span line (3), and FN1–FN5 cover each one, so there is no out-of-band amendment |

**Tasks × surfaces**

| # | Surface | Disposition |
|---|---|---|
| T1.1 | Task 1 Steps 1–3: clean check, `-X theirs` merge, unmerged grep, `git rm` | [compat] ok — clean; `commit`; the merge stops with `Automatic merge failed`; the grep prints exactly `UD Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml`; `git rm` succeeds |
| T1.2 | Task 1 Step 4 `/health` restore. Rule-like: `block()`'s `startswith("})")` end marker | [bidirectional] over: ok — the restored handler (awk range `app.MapGet("/health", async (` … `})`) is `diff`-identical to `65cdf63a`'s, so the marker did not stop late / under: ok — same diff, so no main line is missing. After the `sed`, `Program.cs` has 0 `HealthCheckCache` hits |
| T1.3 | Task 1 Step 5 `GetSchema` restore. Rule-like: `method()` bounds | [bidirectional] over: ok — the `assert lines[r + 1].strip() == "}"` held / under: ok — the restored method is `diff`-identical to `87914794`'s `GetSchema` |
| T1.4 | Task 1 Step 6 deletions | [compat] ok — three `git rm`s; build `0 Error(s)` (T1.6); counterfactual in §1 item 3 |
| T1.5 | Task 1 Step 7 compose blueprint. Rule-like: main's file plus exactly the grant | [bidirectional] over: ok — `git diff --numstat 65cdf63a` → `5 0` / under: ok — at `bdb21aff` the file has `offline_access` 0, `include_claims_in_id_token` 0 and `name, operators` 1, against `87914794`'s 10 / 1 / 1; `git diff -U0 9eb99f76 87914794` shows the four branch hunks the plan accounts for |
| T1.6 | Task 1 Step 8 | [compat] ok — `dotnet build Iverson.slnx` → `0 Error(s)`; `npm ci && npm run build` exit 0 |
| T1.7 | Task 1 Step 9 expected red set | [totality] ok — Api.Tests `Failed: 8, Passed: 1178, Total: 1186`: the gRPC arm, 4 × `AdminConsoleCorsPipelineTests`, 3 × `ProbeAuthorizationPipelineTests`. Vector.Tests `Failed: 1, Passed: 169, Total: 170`: `AddQdrant_RegistersResolvableVectorCollectionReader` |
| T1.8 | Task 1 Step 10 | [compat] ok — after the three named `git add`s, `git diff --name-only` is empty and there are no `??` paths; `git log -1 --format='%H %P'` shows parents `7ab2ba5b` and `65cdf63a` |
| T2.1 | Task 2 Steps 1–3 test code. Rule-like: Step 3's `--filter` substring matcher | [compat] ok — both arms compile and fail with `Expected names {"Author", "ForeignArticle", "Article"} to not contain "ForeignArticle"`; the gRPC arm fails with `…to not contain "TenantBWidget"`. Matcher over: [totality] ok — `Total: 3` / under: ok — the three results are exactly the two new arms and `GetSchema_OwnerTenantIdSet_…` |
| T2.2 | Task 2 Steps 4–5 anchors | [compat] ok — `foreach (var schema in registry.All.Values)` is unique (`:60`); the `if (schema is null)` / `return TypeRowCount.UnknownType;` pair is unique (`:53-54`, one statement over two lines); Step 6 gives `Passed: 3` |
| T2.3 | Rule-like: the ownership filter × (reader × caller-tenant relation) | [bidirectional]. `BuildCatalog`, own-tenant, over: ok — main's gRPC arm asserts `Contain("TenantAWidget")` for a `tenant-a` caller and passes on the tip. Unscoped, over: ok — `UnscopedWidget`, `Author` and `Article` stay. Foreign, under: ok — `ForeignArticle` and `TenantBWidget` are absent (`Passed: 3`). `CountRowsAsync`, unscoped, over: ok — `VisibleTypeWithRows` stays in the data-volume arm. Foreign, under: ok — absent. Own-tenant, over: ok — round 1's `OwnArticle` probe (`[OwnArticle,Author,Article] denied=2`) ran on Step 5 code that is byte-identical today (the amendment diff does not touch Task 2), and the predicate is the same text as `BuildCatalog`'s |
| T2.4 | Task 2 Step 6 checkpoint "exactly 7 failed" | [totality] ok — full run: `Failed: 7, Passed: 1181, Total: 1188`, = 4 CORS + 3 probe |
| T3.1 | Task 3 Steps 1–2 | [negative] ok — afterwards, `grep -rn "HealthCheckWireFormat\|HealthCheckCache\|ProbeAuthorization" --include=*.cs` hits only `<c>`/comment text in four `AdminConsole*Tests` lines; `TenantStatusCache.cs:12-15` matched and was replaced; build `0 Error(s)` |
| T3.2 | Task 3 Step 3 checkpoint | [totality] ok — full run: `Failed: 4, Passed: 1165, Total: 1169`, exactly the four CORS tests |
| T4.1 | Task 4 | [compat] ok — `:44` read `apiKey: "test-api-key"`; after the edit, Vector.Tests `Passed: 170` |
| T5.1 | Task 5 Step 1 | [compat] ok — compiles with the added `using System.Text.Json;` and passes |
| T5.2 | Task 5 Step 2 falsification | [compat] ok — the edit compiles, and the tripwire fails with `…but found JsonValueKind.String {value: 3}`; after the revert, `git diff --quiet -- …/Program.cs` holds |
| T5.3 | Task 5 Steps 3–4 | [existence] ok — the `:117` and `:128` titles and the `:49` and `:153` `authPending` assertions match; `npx vitest run src/widgets/HealthStrip.test.tsx` → 15 passed |
| T6.1 | Task 6 Step 1 | [compat] ok — `87914794:Program.cs:414-422` inserted between the adjacent `app.UseHttpsRedirection();` (`:488`) and `app.UseAuthentication();` (`:489`); `AdminConsoleCorsPipelineTests` `Passed: 6` |
| T6.2 | Task 6 Step 2 "0 failed" | [totality] ok — the Task 13 Step 1 run (C# unchanged after Task 6: `git diff --name-only 54460458 a89056e4 -- '*.cs' '*.csproj' '*.slnx'` is empty) is `Failed: 0, Passed: 1170` |
| D-1 | My Task 6 full run showed 9 `AuthentikRecoveryFlowIntegrationTests` failures (`TimeoutException … did not become available within 2 minutes`, in the class fixture) | dropped — my own concurrent npm, vitest and helm runs loaded the host during that run. The class alone then passed 9/9 in 28 s, and the uncontended Task 13 run is 1170/0. The plan runs steps sequentially |
| T7.1 | Task 7 Step 1 | [compat] ok — `nginx.conf:35` anchor unique; served as rendered in T7.3 |
| T7.2 | Task 7 Step 2 anchors | [compat] ok — `validate API_BASE_URL "${API_BASE_URL-}"` (`:79`), the SHELL-FORMAT list (`:106`) and "exactly these three names" (`:100`) are each unique. Content: FN1 |
| T7.3 | Task 7 Step 3 | [compat] ok — `sh -n` passes; the image builds; the served header reads `connect-src 'self' http://admin-api.ci.invalid http://authentik.ci.invalid`; `/callback` returns 200; `docker rm -f adminui-t7 && docker rmi adminui-t7` removes both |
| T8.1 | Task 8 Steps 1–5: values restorations from the cited `87914794` ranges | [presence] ok — each range was dumped and matches its description. After the edits, a YAML parse gives `clusterCidrs` aws `10.0.0.0/16`, azure `10.1.0.0/16`, gcp `[10.2.0.0/20, 130.211.0.0/22, 35.191.0.0/16]`; `prometheusEnabled: true` in values/aws/azure/gcp; `adminApiIngress` in all five; `adminConsoleOrigin` values `""`, aws/azure/gcp `https://iverson.example.com`, local `http://iverson.local`; and `apiBaseUrl` `http://admin-api.iverson.local` (values, local) or `https://admin-api.iverson.example.com` (aws, azure, gcp) |
| T8.2 | Task 8 Step 6 | [compat] ok — the anchor is unique, and the local render's snippet grep → `1` |
| T8.3 | Task 8 Step 7 | [compat] ok — `admin-api-ingress.yaml:51-57` ran from `- path: /v1/traces` to `number: 8081` and was deleted; the phrase was replaced once in each file; `admin-api-traces=0` on all five overlays |
| T8.4 | Task 8 Step 8 | [presence] ok — YAML parse of the Task 8 commit: `iverson-api` environment carries `AdminConsole__Origin=http://localhost:5173` and `Prometheus__BaseUrl=http://prometheus:9090`; `iverson-worker` (the anchor's second occurrence, `:580`) carries neither |
| T8.5 | Task 8 Step 9 render loop | [totality] ok — all five renders exit 0; hosts `local=1 laptop=0 aws=1 azure=1 gcp=1`; prometheus-url `1/0/1/1/1`; `admin-api-traces=0` ×5; snippet grep `1` |
| T8.6 | Task 8 Step 10 | [compat] ok — the nine named files stage and commit; `git status --porcelain` is empty afterwards |
| T9.1 | Task 9 Step 1 | [compat] ok — the import, call and paragraph anchors are each unique; `87914794` `onSigninCallback` (`:6-25`) is inserted before `const oidcConfig` and added as a property |
| T9.2 | Task 9 Step 2 | [compat] ok — the helper reads `capturedOidcProps`; the `:105`, `:132` and `:139` tests are deleted; `:148` and `:159` pass |
| T9.3 | Task 9 Steps 3–5 | [compat] ok — `npm test` 21 files / 221 passed; `npm run build` exit 0 |
| T9.4 | Task 9 Step 6 | [negative] ok — the four-identifier grep over `Iverson.AdminUI/src` exits 1 under both the host's `grep` function and `command grep` |
| T10.1 | Task 10 Step 1 | [compat] ok — `Failed to resolve import "./useSessionExpiry"` and `TypeError: onExpired is not a function` |
| T10.2 | Task 10 Steps 2–3 | [compat] ok — `vitest run src/auth` 23 passed |
| T10.3 | Task 10 Step 4 | [compat] ok — without the mount the `AuthGate` expiry test fails (`1 failed \| 22 passed`); restored, 23 passed; `npm test` 224 passed on two sequential runs |
| D-2 | My first `npm test` at Task 10 Step 4 reported `1 failed \| 223 passed` | dropped — it ran concurrently with my background Api.Tests run. Three later uncontended runs pass 224/224 (T10.3, T13.1) |
| T11.1 | Task 11 Steps 1–3 | [compat] ok — the README anchor (`:29`) and each runbook string (`:67`, `:70`, `:79`, onboarding `:21`) match once; entrypoint `:23-25` are the three declarations; `sh -n` passes; the Task 11 entrypoint runs in the T13 harness image (exit 78 on the malformed value, correct CSP) |
| T11.2 | Task 11 Step 4 | [existence] ok — `values-aws.yaml:105 adminApiIngress:`; the comments at `:162` and `:182` name it |
| T11.3 | Task 11 Step 5 | see FN2 |
| T12.1 | Task 12 Step 1 scope: env added to the served-CSP step's `docker run` only | → §2.1 |
| T12.2 | Task 12 Step 2 | [compat] ok — the `:210` assertion and the `:201` clause each match once and are removed; the served-CSP step passes (FN3) |
| T12.3 | Task 12 Step 3 | [compat] ok — count 8 (`AuthProvider.tsx` 2, `config.ts` 1, `main.tsx` 2, `router.test.tsx` 2, `router.tsx` 1); `BASELINE=8` and the comment updated |
| T12.4 | Task 12 Step 4 | see FN3 |
| T12.5 | Dynamic: does the adapted job still catch the regression its malformed-value step exists for? | → §2.1 |
| T12.6 | Dynamic: harness shell options vs CI (the harness runs `bash -e -c`, without `pipefail`) | [negative] ok — `image-contract` has no `shell:` or `defaults`, and its pipelines (`:182` `sed … \| tr -d '\r'`, `:187` `printf \| tr \| sed`) completed with every stage at 0 on both the passing and the falsified run, so `pipefail` could not flip a step |
| T12.7 | Stale workflow text: `:81-88` "7 pre-existing" / "an eighth error", `:150` frame-src, `:184` `admin-ui-security-headers.inc` | dropped — comments and a failure message; no step outcome depends on them |
| T13.1 | Task 13 Step 1 | [totality] ok — build `0 Error(s)`; Api.Tests `Failed: 0, Passed: 1170`; Vector `Failed: 0, Passed: 170`; `npm test` 224 passed; tsc 8 == `admin-ui.yml:100` `BASELINE=8` (md5 `0a26da24`) |
| T13.2 | Task 13 Step 2 | [compat] ok — five renders; counts identical to T8.5 |
| T13.3 | Task 13 Step 3 | [compat] ok — R1: both checks removed → `Failed: 3`; restored → `Passed: 3`. R5: mount removed → the expiry test fails; restored → 23 passed. R3: FN3's third call site, plus the snippet grep `1` |
| T13.4 | Task 13 Step 4 | dropped — round-1 T13.5's disposition; the evidence is unchanged (no plan step edits a widget or page poll constant) |
| T13.5 | Task 13 Step 5 | see FN4 |
| T13.6 | Task 13 Step 6 | [negative] ok — after all 13 tasks and all three harness call sites: `git status --porcelain` prints 0 lines; no `adminui-*` or `iverson-adminui-ci` image; 0 `adminui` containers |

**Cross-task interface contracts**

| # | Contract | Disposition |
|---|---|---|
| X1 | Task 1 → Task 2: `GetSchema` delegates to `BuildCatalog` | [compat] ok — T1.3; the gRPC arm fails at Task 1 and passes after Task 2 (T2.1, T2.2) |
| X2 | Task 3 → Task 5: the tripwire replaces the deleted wire-format coupling | [compat] ok — T5.1, T5.2 |
| X3 | Task 7 → Task 11: entrypoint line targets | [existence] ok — Task 7 inserts at `:80-81`, below Task 11's `:3-6` and `:23-25`; Task 11's asserts on those lines held |
| X4 | Task 8 → Task 11 Step 4 | [presence] ok — T11.2 |
| X5 | Task 9 → Task 10: `AuthGate` without `useTokenRenewal()` | [compat] ok — T10.2 |
| X6 | Tasks 7, 9, 10, 11 → Task 12: image contents and tsc count | [compat] ok — T12.3, FN3 |
| X7 | Task 12 → Task 13 Step 1: `BASELINE` | [compat] ok — 8 at Task 12 Step 3 and 8 at Task 13 Step 1 |
| X8 | Harness call sites: the Task 12 passing run, the Task 12 falsification, and the Task 13 Step 3 re-run | [compat] ok — FN3 |
| X9 | Task 8 Step 9 loop at Task 8 and at Task 13 Step 2 | [compat] ok — T8.5, T13.2 |
| X10 | Operation "start the image under main's entrypoint in CI": two call sites in `image-contract` (`:156-160`, `:229-233`) | one site: [compat] ok — `:156-160` (T12.2) / other site: → §2.1 (`:229-233`) |
| X11 | Tasks 2, 7, 8, 9 → Task 13 Step 5 expected output | [compat] ok — FN4, IF1 |

**Population-closure matrix**

- **P1 — §2.1's rule × every start of the admin-ui image.** The rule: a start of the admin-ui image must supply main's env contract (`OIDC_CLIENT_ID`, `OIDC_AUTHORITY`, `API_BASE_URL`, `EXTERNAL_SCHEME`, `OIDC_ORIGIN`), or main's `set -eu` tail masks the failure mode that start asserts. The population comes from `git grep -nE "docker run|podman run"` over the tracked tree, filtered to the image and its env names, plus the plan's own commands and main's chart.
  - Direction key: *under* = a start whose assertion is masked; *over* = supplying the contract breaks a correct-tree pass or masks something.
  - `admin-ui.yml:156-160`, served-CSP (Task 12 Step 1 supplies the pair): under: ok — [compat] harness `OK` lines (FN3) / over: ok — [compat] the falsification still fails on the admin-api origin.
  - `admin-ui.yml:229-233`, malformed-value: under: → §2.1 / over: ok — [compat] with the pair added, the correct tree still passes with exit 78 (§2.1 run 4).
  - Task 7 Step 3's `docker run` (plan): under: ok — [compat] it passes all five and serves the correct CSP (T7.3) / over: dropped — a happy-path check with no failure assertion, so nothing can be masked.
  - Helm `charts/admin-ui/templates/deployment.yaml` (main's): under: ok — [presence] inherited row, all five at `:56,61,63,65,67` / over: dropped — a deployment, with no assertion to mask.
  - `Iverson.Server/docker-compose.yml`: n/a — [absence] `command grep -n -i "adminui\|admin-ui"` hits only the Vite-dev-server comment at `:499-500`; there is no admin-ui service.
  - `Iverson.AdminUI/README.md:81`: n/a — [absence] a descriptive sentence, not a command.
  - `deploy/kind/build-and-load-image.sh`: n/a — [absence] no `docker run` hit in the tracked-tree grep (a build/load only, per CDR-3 M-E).

## 1. Verified-plan-assumptions cross-check

Numbered in table order. Rows without an ID are named by their category and subject.

1. A1 — [existence] still holds — `Iverson.slnx:14`, `:29`; `65cdf63a:.github/workflows/dotnet-build.yml:19` `dotnet build Iverson.slnx`, `:20` `dotnet test Iverson.slnx …`.
2. C4 — [totality] still holds — T1.1.
3. D1 — [compat] still holds — re-measured on `bdb21aff` with the files restored: 4 × CS0535 at `HealthCheckCacheTests.cs(239,96)` ×3 (`IRecordStoreQueryExecutor` members) and `(278,91)` (`IVectorSchemaManager.PingAsync()`). With that file removed: 1 × CS0246 at `OperationalListenerBindingPipelineTests.cs(135,62)` `'HttpListenerOnly'`. 0 once all three are removed (T1.6).
4. F5 — [negative] still holds — `git grep HttpListenerOnly bdb21aff -- '*.cs'` → 0.
5. F4 — [negative] still holds — `HealthCheckCache` on `bdb21aff` appears only in the `TenantStatusCache.cs:13` comment; the solution builds.
6. Measurement (Task 1 test outcome) — [totality] still holds — T1.7.
7. A2 — [existence] still holds — `ObjectMappingGrpcServiceTests.cs:596`; fails at Task 1 (T1.7).
8. B1 — [existence] still holds — `SchemaCatalogReader.cs:60-63`: `foreach`, then `Evaluate`.
9. B2 — [existence] still holds — `AggregateReader.cs:52-58`: `registry.Get`, `UnknownType`, `Evaluate`.
10. B3 — [existence] still holds — `ObjectMappingGrpcServiceTests.cs:604-615` (`with { TypeName = "TenantAWidget", OwnerTenantId = "tenant-a" }` …); `SchemaRegistry.cs:238`.
11. B4 — [existence] still holds — `AdminConsoleDataVolumeEndpointTests.cs:50-57` (`tenant_id` `tenant_alpha`, `ReaderGroup`).
12. B5 — [compat] still holds — `AdminConsoleEndpoints.cs:108`, `:169`; `AdminConsoleTestWebApplicationFactory.cs:165` `UpsertAsync … => Task.CompletedTask`; both arms ran.
13. F1/F2 — [negative] still holds — callers: `ObjectMappingGrpcService.cs:76`, `AdminConsoleEndpoints.cs:118`, `:186` (plus `SchemaCatalogReader.cs:42` `ReadCatalog` → `BuildCatalog`); no test calls either method; `OwnerTenantId` count 0 in `AdminConsoleTestWebApplicationFactory.cs`.
14. Design (handler-level arms) — [compat] still holds — `AdminConsoleEndpointsPipelineTests` is green in T13.1's 1170/0.
15. A3 — [existence] still holds — T3.1.
16. A4 — [existence] still holds — `TenantStatusCache.cs:12-15` (T3.1).
17. A5 — [existence] still holds — `:44` `"test-api-key"`; `:18`, `:56`, `:74` use `"test-signing-key-0123456789abcdef"`; `ServiceCollectionExtensions.cs:27` `GetByteCount(apiKey) < 32`.
18. A6 — [existence] still holds — `AuthenticationPipelineTests.cs:293` `HealthCacheTestFactory`; `:321-322` the cache-window test.
19. B9 — [compat] still holds — `EngagementHealthStatus.cs:10` `AuthPending`; `EngagementStoreOptions.cs:12` `Enabled … = true`; the tripwire passing with `JsonValueKind.False` is run evidence that engagement is on in that host (a disabled store would emit the string `"disabled"`).
20. Code (missing `using System.Text.Json;`) — [absence] still holds — merged `AuthenticationPipelineTests.cs:1-17` usings contain no `System.Text.Json`.
21. A7 — [existence] still holds — T5.3.
22. A8 — [totality] still holds — at Task 1 the covered set is exactly the 4 CORS tests; the residual 4 failures are not CORS (T1.7); after Task 6, 6 passed.
23. Code (`UseCors` block) — [existence] still holds — `87914794:Program.cs:414-422` (T6.1).
24. A9/E3 — [existence] still holds — merged `docker-entrypoint.sh:69-75` `origin_of`, `:106` the SHELL-FORMAT list; `nginx.conf:35`.
25. A10 — [presence] still holds — a key dump of `bdb21aff`'s six values files: `adminApiIngress`/`adminConsoleOrigin` at azure `:87`/`:95`, gcp `:88`/`:96`, local `:121`/`:125`. [absence] Neither key is in `values.yaml` or aws. `clusterCidrs` appears only at local `:16` and laptop `:19`, `prometheusEnabled` only at local `:22` and laptop `:24`, and `apiBaseUrl` is main's single-origin value in the five files.
26. Code (branch-tip ranges) — [presence] still holds — T8.1.
27. F7 — [compat] still holds — the renders consume the keys (T8.5).
28. E2 — [existence] still holds — `charts/admin-ui/templates/ingress.yaml:27` `printf "%s://authentik.%s" .Values.global.externalScheme .Values.global.ingressHost`.
29. F6 — [negative] still holds — `/v1/traces` readers on `bdb21aff`: `TracesRelayEndpointTests.cs` (test host), main's `charts/api/templates/ingress.yaml:95`, and the two edited comment sites; none in `.github/`.
30. A11 — [existence] still holds — merged `docker-compose.yml:498`; `87914794:…:474-484` (T8.4).
31. A12 — [existence] still holds — `AuthProvider.tsx:4`, `:54`, `:46-50`; test file `:8`, `:18`, `:105`, `:132`, `:139`, `:148`, `:159`.
32. F3 — [totality] still holds — `git grep` for the four identifiers over `bdb21aff:Iverson.AdminUI/src`: 41 lines, all in `client.test.ts` (11), `client.ts` (10), `useTokenRenewal.test.ts` (11), `useTokenRenewal.ts` (4), `AuthProvider.test.tsx` (2) and `AuthProvider.tsx` (3).
33. Code (`client.ts` doc sites) — [existence] still holds — `:16`, `:28`, `:44`.
34. Code (`client.test.ts` tests) — [existence] still holds — `:119`, `:143`, `:154`, `:173`, `:190`, `:245`.
35. B6/E4 — [existence] still holds — `oidc-client-ts.d.ts:30` `addAccessTokenExpired(cb: AccessTokenCallback): () => void`; `react-oidc-context.d.ts:31` `readonly events`, `:33` `removeUser(): Promise<void>`; lockfile pins 3.3.1 and 3.5.0.
36. F8 — [existence] still holds — `AuthProvider.test.tsx:54-67` mocks `useAuth` with `isLoading`, `isAuthenticated` and `signinRedirect` only.
37. A13 — [absence] still holds — `git ls-tree --name-only bdb21aff Iverson.AdminUI/src/auth/`: `AuthProvider.test.tsx`, `AuthProvider.tsx`, `CallbackPage.tsx`, `RequireGroup.test.tsx`, `RequireGroup.tsx`, `groups.ts`.
38. B7/E5 — [existence] still holds — lockfile: `@testing-library/react` 16.3.2, `vitest` 5.0.0; `vitest.config.ts:16` `fileParallelism: false`.
39. C2 — [existence] still holds — `test` = `vitest run`, `build` = `vite build`.
40. A14 — [existence] still holds — merged `README.md:23` and `:55` read `8081`; `.env.development:3` `8081`; bootstrap sentence `:27-29`; runbook `:67`, `:70`, `:79`; onboarding `:21`; `generate-compose-secrets.sh:12` (`ENV_FILE=…/Iverson.Server/.env`), `:21` (`IVERSON_BYPASS_PASSWORD`).
41. Code (entrypoint leftovers) — [negative] still holds — a full read of the merged 108-line entrypoint finds `CONFIG_TEMPLATE`, `CONFIG_OUTPUT` and `HEADERS_OUTPUT` only at `:23-25`; header `:3-6`.
42. Code (`values-aws` comments) — [existence] still holds — T11.2.
43. A15 — [existence] still holds — `admin-ui.yml:100`, `:143`, `:156-160`, `:201`, `:210`; jobs `build-test` and `image-contract`; env `IMAGE`, `CONTAINER`, `API_ORIGIN`, `IDP_ORIGIN`.
44. C3 — [existence] still holds — `helm version --short` → `v3.16.4+g7877b45`; `admin-api-ingress.yaml:23` `name: {{ .Release.Name }}-admin-api`.
45. C5 — [existence] still holds — `41e01848` subject `merge main into admin-console-landing-page`.
46. A16 — [existence] still holds — `.gitignore:49` `**/docs/plans/`.
47. D2-D6 — [compat] still holds — executed in plan order; X1–X9.
48. Command (PyYAML) — [compat] still holds — `pyyaml 6.0.3`; the harness ran.
49. Command (`docker` is podman) — [compat] still holds — `podman version 5.7.0`; every docker command in Tasks 7 and 12 ran.
50. Code (Task 5 falsification compiles) — [compat] still holds — T5.2.
51. Code (`export` masks, assign-then-export propagates) — [compat] still holds — FN1: dash run, plus the real image; `87914794:Iverson.AdminUI/docker-entrypoint.sh:85` is `ADMIN_API_ORIGIN=$(origin_of API_BASE_URL "$API_BASE_URL")`.
52. Path (the branch tracks the spec and plan, both quote the passwords) — [totality] still holds — `git grep` at `7ab2ba5b` hits spec `:311`, `:312` and plan `:938`, `:952`; FN2.
53. Command (`admin-ui.yml` clean-up and scratch files) — [presence] still holds — parse: `Clean up` `if: 'always()'`, running `docker rm -f "$CONTAINER"` and `docker rmi -f "$IMAGE"`; `:176` `-D headers.txt`, `:187` `> directives.txt`.
54. Convention (a main decision is a line main changed after the fork) — [bidirectional] still holds — FN4.

Span check (plan dependencies with no covering assumption):

- Task 12 relies on the adapted `image-contract` job still catching the regressions its steps guard, which needs every container start in the job to supply main's env contract. Uncovered: the inherited row "The AdminUI CI job starts main's entrypoint without main's env contract" cites only `:156-159`. Verified false in-round → §2.1.
- Task 12 Step 4's harness reproduces CI's shell options. Uncovered; [negative] verified non-load-bearing (T12.6).
- The plan's `grep` commands behave as GNU grep, although on this host `grep` is a ugrep shell function. Uncovered; [compat] verified — every plan grep produced the plan's expected output, and Task 9 Step 6's negative grep also exits 1 under `command grep` (T9.4).

## 2. Literal-wrongness findings

1. **Task 12 adapts only one of the `image-contract` job's two container starts to main's entrypoint contract. The malformed-value step therefore stops testing anything: it passes an image whose injection validation has been removed.**
   - **Mechanism.** The merged entrypoint is the branch's `validate` block (merged `docker-entrypoint.sh:77-79`) followed by main's tail. That tail reads `$EXTERNAL_SCHEME` under `set -eu` (`:94`). The malformed-value step's `docker run` (`admin-ui.yml:229-233`) passes only `OIDC_CLIENT_ID`, `OIDC_AUTHORITY` and `API_BASE_URL`, and Task 12 leaves it that way.
     - On a correct image, `validate` exits 78 before the tail runs, so the step passes, as it should.
     - On the regression the step exists to catch (its comment, `:213-223`: "with the validate calls removed, this exact run was observed exiting 124"), the container now dies in main's tail on the unset variable with exit 2. The step fails only on exit 0, exit 124 or a `start worker process` log line (`:241-255`), so it prints `rejected with exit 2 and nginx never started` and passes.
   - **Why this breaks the outcome.** The job's header comment (`:119-125`) calls this step one of two controls that "nothing else in the repo would notice … regressing". Two stated outcomes depend on it:
     - The resolution policy preserves the branch's unique feature work, and this CI job is branch-only feature work (`git cat-file -e 65cdf63a:.github/workflows/admin-ui.yml` fails).
     - The spec's Verification plan makes "the `admin-ui.yml` workflow … itself a gate".

     After Task 12, that gate passes the regression it was built for. Task 12's own commit message ("The job started main's entrypoint without the two env vars its set -eu tail requires") is still true of this step.
   - **What changed since this was last cleared.** CDR-3 row I7 cleared the step's over-direction on read-tier evidence ("the step fails on exit 0 or exit 124"). CDR-4 row 11 marked it "n/a — R8 does not touch it". Spec R8 item 1 and the inherited row name only `:156-160`. The run below shows the regression now exits 2, which neither arm checks.
   - **Evidence.** [compat] run — the plan's Task 12 Step 4 harness, verbatim, on four trees:
     1. Branch tip `87914794` (unmerged; the branch's own job) with the three `validate` calls deleted: harness exit 1, and the malformed-value step fails with `::error::the container was still running after 60s…` (md5 `f18301f9`). Before the merge, the step caught this regression.
     2. The plan's tip (all 12 tasks) with the three `validate` calls deleted: harness exit 0. The served-CSP step passes, and the malformed-value step logs `/docker-entrypoint.d/40-admin-ui-config.sh: line 89: EXTERNAL_SCHEME: parameter not set` and prints `rejected with exit 2 and nginx never started` (md5 `9bd04ff0`).

     The falsifier: if the step still discriminated, run 2 would have failed the way run 1 did.
   - **Proposed fix.** Replace Task 12 Step 1 with:

     > - [ ] **Step 1: Main's entrypoint contract** — add `-e EXTERNAL_SCHEME=http -e "OIDC_ORIGIN=$IDP_ORIGIN"` to both `docker run`s that start the image: the served-CSP step's (`:156-160`), and the malformed-value step's (`:229-233`, directly below its `-e "API_BASE_URL=$API_ORIGIN" \`). The second one passes without the pair, because the branch's `validate` exits 78 before main's tail runs. But if validation regresses, the container dies on the unset `$EXTERNAL_SCHEME` (exit 2) instead of starting nginx, and the step, which fails only on exit 0 or 124, reads that as a pass.

     Spec R8 item 1 names only the served-CSP step. The fix applies the same prescribed pair to the job's second start, within R8's own constraint ("All three fixes are in this one branch-owned file, so no main line changes"). Task 12 Step 4's existing harness run already exercises the edited step.

   Evidence: [compat] run — the plan's tip with this edit applied to `admin-ui.yml`:
   - run 3, with the three `validate` calls deleted: harness exit 1, and the malformed-value step fails with `::error::the container was still running after 60s. A quote-bearing OIDC_AUTHORITY must abort startup…` (md5 `e89221e6`);
   - run 4, on the correct tree: harness exit 0, with both `OK: connect-src names …` lines, `admin-ui entrypoint: OIDC_AUTHORITY contains characters outside [A-Za-z0-9:/._-]`, and `rejected with exit 78 and nginx never started` (md5 `aac1c678`);
   - after both runs: 0 `adminui` containers and 0 `adminui` images.

   The falsifier: had the tail not needed the pair, or the pair not reached it, run 3 would have exited 2 and passed like run 2. [negative] The fix changes no main line: `git diff --name-only 9eb99f76 65cdf63a | grep -c '^.github/workflows/admin-ui.yml$'` → 0, so Task 13 Step 5's audit output is unaffected.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- Round-1 §2.1 (`export` masked `origin_of`'s refusal): resolved. Task 7 Step 2 now assigns and then exports; the built image exits 78 on a scheme-less `API_BASE_URL` (FN1).
- Round-1 §2.2 (Task 11's grep could not come out empty): resolved. The pathspec excludes the spec and the plan; it exits 1 on Task 11's tree and still catches the three runbook sites before it (FN2).
- Round-1 §2.3 (the harness stranded a container, an image and two scratch files): resolved. The `finally` clean-up leaves no residue after the passing run, the falsification run or the Task 13 re-run (FN3).
- Round-1 §2.4 (the file-level audit reported 21 pre-fork hunks as overrides): resolved. The line-level script prints exactly the ten sanctioned lines on the tip and fires on the unmerged branch (FN4, IF1).
- Round-1 span lines (PyYAML, podman, Task 5's falsification edit compiles): recorded as V-rows 48–50 and reconfirmed (FN5).

## 5. Recommendation

⚠️ Approve with literal-wrongness fixes
