# Critical Implementation Review: 2026-10-07-csr-round10-orchestrator-workload-identity-implementation-plan (Round 1)

**Plan:** /home/ben/repositories/Iverson/docs/plans/2026-10-07-csr-round10-orchestrator-workload-identity-implementation-plan.md
**Artifact HEAD at review:** 15f8aa93afd8509e79883613cf90417a30d2c7d5
**Verified plan-level assumptions section:** present

⚠️ 1 commits since plan-write time (SHA 59782440); cited file:line references re-checked under §1. The one commit is the plan's own (`15f8aa93`). `git log 59782440..HEAD -- <spec>` is empty, so the spec is unchanged.

There are no prior reviews of this plan, so §4 is omitted. The spec's two CDRs were read for shared probe evidence only.

## 0. Coverage enumeration

### Probe key

Every probe used throwaway resources under `scratchpad/cir-c2-1/`, all deleted at round close: a detached worktree at `15f8aa93`, which was removed with `git worktree remove`; a compose project `cir-c2-ak`, brought down with `down -v`; and the Testcontainers fixture's own containers. No kind cluster was needed. Nothing belonging to the user or another session was touched, and no `.env` file was read.

- **P0. Anchor and drift.** `git log -1 -- <plan>` = `15f8aa93`. `git status --porcelain -- <plan>` is empty. `git rev-parse 15f8aa93:<plan>` = `git hash-object <plan>` = `d993e59c…`.
- **P1. Embedded code equals the scratch proofs.** Every fenced block was extracted from the plan and diffed. The handler (`:381`) equals `c2/proto/Handler.cs`. The handler tests (`:182`) equal `c2/code/AuthentikOrchestratorTokenHandlerTests.cs`. The T3 block (`:760`) equals `c2/code/IntegrationFact.cs.txt` exactly. `t4_edits.py` (`:982`) and `check_t4.py` (`:1329`) equal their scratch copies. All 12 T2 snippets (`:555`–`:716`) occur verbatim in `c2/t1_t3_apply.py`. There is no drift.
- **P2. T1+T2 unit run.** In the scratch worktree with `t1_t3_apply.py` applied, T2 Step 5's filter gave `Passed!  - Failed: 0, Passed: 42`, which is 9 handler tests plus 33 trust tests.
- **P3. T2 red state.** With T2's two production edits reverted and the test edits kept, T2 Step 2's command gave `Failed: 4, Passed: 29`. The four failures were exactly `Guard_RefusesNeitherCredentialMode`, `…OnlyInFederatedMode(federated: True)`, `Guard_RefusesBothCredentialModes` and `Guard_RefusesFederatedModeWithoutAClientId`.
- **P4. Margin mutation (row 9).** With `RefreshMargin` set to 0, only `CachedToken_IsReused_UntilTheRefreshMargin` failed (8 passed).
- **P5. T3 Step 3 as written.** The class gave 9 passed and 1 failed. The new fact failed at the first POST: `POST /api/v3/propertymappings/source/oauth/ returned 400: {"name":["This field is required."],"expression":["This field is required."]}`.
- **P6. T3 with one change.** The change was to `PostAsync` only: `admin.PostAsJsonAsync(path, body)` became `admin.PostAsync(path, Json(JsonSerializer.Serialize(body)))`, with the same serialised object and a length-delimited body. The new fact then passed in 20 s, and T3 Step 3's command gave `Passed!  - Failed: 0, Passed: 10` (47 s).
- **P7. T3 Step 4 mutation, with P6 applied.** The pin was changed to compare `sub` against `"x"`. The fact failed with `System.InvalidOperationException : Authentik token exchange failed: HTTP 400 invalid_grant.`, thrown at `AuthentikOrchestratorTokenHandler.GetAccessTokenAsync`.
- **P8. T4 Steps 3–5, verbatim, in the scratch worktree.**
  - On the base render, the checker failed on all five overlays, through a `KeyError: 'orchestrator-federation.yaml'`.
  - After `t4 edits applied`, all five overlays gave `[<overlay>] PASS`, kubeconform `Invalid: 0, Errors: 0`, `other-container criticals changed: 0  relay: 5` and `1 chart(s) linted, 0 chart(s) failed`.
  - `AuthentikOrchestratorRoleBlueprintTests` against the edited chart: 6/6.
- **P9. An isolated Authentik 2026.5.3 running the chart's own rendered blueprints.**
  - **Setup.** The compose project `cir-c2-ak` ran pg, server, worker and a JWKS file server. The JWKS server shared the worker's network namespace and listened only on `127.0.0.1:8001`. The rendered laptop ConfigMap `iverson-authentik-blueprints` was mounted at `/blueprints/custom`, and the Secret's `service-clients.yaml` at `/blueprints/custom/service-clients/`, as `deployment-worker.yaml` mounts them.
  - **Phase 1, `main`'s render.** All 5 `custom/` blueprints were `successful`, and `OLD 1 1` (the old user and old token both existed).
  - **Phase 2, the branch render swapped in.** T6 Step 4's `ak apply_blueprint custom/service-clients/service-clients.yaml custom/orchestrator-federation.yaml` gave `apply exit=0`. Re-running it with the old objects already gone also gave `exit=0`. All 6 `custom/` BlueprintInstances were `successful`.
  - **T6 Step 5's `ak shell -c` snippet, verbatim, with `2>/dev/null | tail -3`.** It printed `source kids: ['cir-kid-1']`, then `old user: 0 old token: 0`, then `workload groups: ['iverson-admin-orchestrators'] usable password: False`. The full output is 7 lines: a 3-line banner, a blank line, then the 3 prints.
  - **T6 Step 5's `exchange()` function, verbatim (port 19100).** `api sa:   MINTED` and `other sa: REFUSED invalid_grant`.
  - **The minted token.** Its keys were `['access_token', 'expires_in', 'id_token', 'scope', 'token_type']` with `expires_in` 300. `core/users` returned 200 and `rbac/roles` 403. The same assertion sent with the public-ingress marker header got `invalid_grant`. A string `aud` minted.
- **P10. Handler runtime console.** The handler source was copied verbatim from the plan, and `Microsoft.Extensions.Http` 10.0.0 was used.
  - With IHttpClientFactory's default lifetime, 6 calls across 2 batches built `handlers=1` and made `mints=1`. With `SetHandlerLifetime(1s)` they built `handlers=2` and made `mints=2`.
  - When `HttpClient.Timeout` (300 ms) fires during the exchange, the caller sees `TaskCanceledException` (inner `TimeoutException`), not `InvalidOperationException`.
  - A caller cancelled while waiting on the mint semaphore sees `TaskCanceledException`, and the next mint succeeds.
- **P11. Shell state between tool calls.**
  - A variable set in one Bash call was `[]` in the next, and the cwd reset to `/home/ben/repositories/Iverson`.
  - T1 Step 5's `git add --dry-run <paths>`, by cwd: from the main checkout, `exit=128` (`pathspec … did not match`); from `<worktree>/Iverson.Server`, `exit=128`; from the worktree root, `exit=0`.
  - In a fresh shell, T4 Step 4 failed with `python3: can't open file '/t4_edits.py'` (exit 2), and T6 Step 6's `mkdir -p $LIVE/createtenant` failed with `mkdir: Permission denied`.
  - A background process started with `&` survived into the next call, so a port-forward does too.
- **P12. Rendered names (laptop).**
  - **Containers.** The worker has the init container `jwks-relay` and the container `worker`. The API has `api`. The server has the init container `migrate` and the containers `server` and `tls-proxy`.
  - **Services.** `svc/iverson-authentik` has ports `http` 9000, `https` 8443 and `http-public` 9080. `svc/iverson-api` has `grpc` 8080 and `http` 8081.
  - **Security context.** The API and worker pods both run with `runAsUser: 1000`, `runAsGroup: 1000` and `fsGroup: 1000`.
- **P13. `main`'s chart from `git archive`.** After `git archive main Iverson.Server/deploy/helm/iverson | tar -x` and `helm dependency build`, `helm template` with T6 Step 3's flags rendered 76 documents.
- **P14. T6 Step 6's console.** `Program.cs` was extracted verbatim and built against the scratch worktree's `Iverson.Client.Core`: `Build succeeded.`
- **P15. The image-scan List step.** The step's `sed` over the AWS render prints `registry.k8s.io/kubectl:v1.37.1`.
- **P16. Audit follow-up probes.** These used a second throwaway detached worktree at `15f8aa93`, with `c2/t4_edits.py` applied (equal to the plan, P1) and `helm dependency build` run. It was removed afterwards.
  - **Absence of the orchestrator Secrets.** `command grep -cE 'authentik-admin-orchestrator-(token|user)'` over each of the five edited renders gave `0` hits ×5, and `command grep -c 'Authentik__AdminToken'` gave `0` ×5. Positive control: the same grep over the unmodified `15f8aa93` laptop render (from `git archive`) gave `3` hits.
  - **No tracked `.tgz` files.** `ls charts/*.tgz | wc -l` gave `13` files on disk, `git status --short | command grep -c '\.tgz'` gave `0`, and `git status --short` listed exactly the six chart files T4 modifies.
  - **The token path chain (laptop render).** The API container's `Authentik__OrchestratorAssertionPath` = `/var/run/secrets/iverson/authentik-orchestrator/token`. The `authentik-orchestrator-token` volumeMount's `mountPath` + `/` + the projected source's `path` gives the same string (`equal: True`).
  - **Fix-bullet probes.** In a fresh `env -i` shell, the line `SCR=…/c2-sdd; WT=…/csr10-orchestrator-workload-identity; LIVE=$SCR/live` made T4 S4's command expand to `python3 /tmp/…/c2-sdd/t4_edits.py /home/ben/repositories/Iverson/.worktrees/csr10-orchestrator-workload-identity/Iverson.Server/deploy/helm/iverson`, and T6 S6's to `mkdir -p /tmp/…/c2-sdd/live/createtenant`. `$SCR`'s parent directory exists and is writable. From `<wt>/Iverson.Server`, `ls Iverson.Api.Tests/Iverson.Api.Tests.csproj` resolved, and `git ls-tree 15f8aa93 Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj` lists the file.

### Tasks × surfaces

| # | Task / surface | Disposition |
|---|---|---|
| T0 | Step 1: create the worktree | ok, not load-bearing. The block is self-contained: an absolute `cd`, a relative `git worktree add` from there, and an absolute `mkdir` |
| T1a | Test code | ok [compat]. P2: all 9 handler tests pass. P1: the code equals the proven scratch file |
| T1b | Handler code, static | ok [compat]. P2 builds it, with no new warnings in the filtered output. P1 shows it equals the plan |
| T1c | Handler, dynamic: concurrent mints | ok [compat]. `ConcurrentCalls_MintOnce` passes (P2). In P10, a waiter cancelled on the semaphore leaves it usable: `WaitAsync` sits outside the `try`, so no unmatched `Release` runs |
| T1d | Handler, dynamic: IHttpClientFactory handler lifetime | dropped. P10: the cache lives per handler chain, and `IdpAdminClient` calls `CreateClient` per call (`IdpAdminClient.cs:55,141,206,214,243,251`), so each 2-minute rotation re-mints once. Each mint succeeds, and the outcome (a short-lived token, admin calls succeed) is unchanged |
| T1e | Handler, dynamic: timeout path | dropped. P10: `HttpClient.Timeout` cancels the linked token the handler receives, so the `TaskCanceledException && !cancellationToken.IsCancellationRequested` arm never fires for it, and the caller gets `TaskCanceledException`. `CreateTenant` still fails as it does today on an admin-call timeout: `TenantLifecycleGrpcService.cs:43-47` catches everything, deletes the tenant row and rethrows. The spec's named-step list covers network errors, which are wrapped (`network` theory row) |
| T1f | Handler, dynamic: no credential in messages | ok [compat]. The `Failure_NamesTheStep_AndCarriesNoCredential` rows pass (P2). They assert `NotContain` on the assertion, `id-token` and `access-token` |
| T1g | Steps 2/4: commands and Expected | ok, not load-bearing. Step 2 has an absolute `cd`. Step 4's Expected `Passed: 9` = 42 − 33 (P2, P3) |
| T1h | Step 5: commit | → §2.2 (P11: `exit=128` from either cwd the harness can leave) |
| T2a | Guard code | ok [compat]. P2 passes. P3 shows the four new tests fail without it |
| T2b | `Program.cs` wiring | ok [compat]. Both rows of the theory pass (P2). Federated True fails without the wiring (P3) |
| T2c | Step 1 and Steps 3–4: edit anchors | ok [compat]. `t1_t3_apply.py` asserts `count == 1` for each anchor and applied cleanly at `15f8aa93` (P2) |
| T2d | Step 2 Expected | ok [compat]. P3 failed exactly the four named tests, and the other 29 passed |
| T2e | Step 5: command | → §2.2. It has no `cd`, and its project path is relative |
| T2f | Step 5 Expected `42` | ok, not load-bearing (P2) |
| T2g | Step 6: commit | → §2.2 |
| T2h | Consumer impact: the guard on non-Development API hosts | ok [negative]. `command grep -rn 'UseEnvironment\|EnvironmentName\|ASPNETCORE_ENVIRONMENT\|WebApplicationFactory<'` over `Iverson.Server`, excluding bin/obj: only `AuthentikTrustTests.cs:177` (its own `Env()`), `AuthTestWebApplicationFactory` (no environment, so Development) and `docker-compose.yml:492,616`. The compose API (`:492`) sets `Authentik__AdminToken=${IVERSON_ADMIN_ORCHESTRATOR_TOKEN:?…}` (`:534`), so it is static mode. `:616` is the worker role, which the guard skips |
| T3a | Code: the `PostAsync` helper | → §2.1 (P5) |
| T3b | Code: the payload fields of all nine POSTs | ok [compat]. P6: with only the transfer encoding changed, every POST was accepted by the live 2026.5.3 and the fact passed |
| T3c | Code: the `ExchangeAsync` body | ok [compat]. Its `FormUrlEncodedContent` is length-delimited. In P6 the fact's three `invalid_grant` assertions passed, plus the one taken before the user exists |
| T3d | Step 3: Expected, and the "fix the payload field it names" prose | → §2.1. The step fails as written (P5), and the prose points the executor at fields that were never wrong |
| T3e | Step 4: mutation | ok [compat]. P7 failed at the exchange, as Step 4 predicts. That was measured with §2.1's fix applied, because without it the fact fails before reaching the mutation |
| T3f | The shared fixture: side effects on the other 9 facts | ok [compat]. P6: the class gave 10/10 with the new fact's objects present |
| T3g | Step 5: commit | → §2.2 |
| T4a | Step 1: `t4_edits.py` anchors against the files at HEAD | ok [compat]. P8: every `edit()` asserts `count == 1`, and the script printed `t4 edits applied` at `15f8aa93` |
| T4b | Step 2: checker | ok [compat]. P8: PASS ×5 on the edit and fail ×5 on the base. **Not load-bearing** for the base direction: the checker fails there by `KeyError` at section 4, before sections 5–6 run, but the edited render passes every section |
| T4c | Steps 3 and 5: Expected lines | ok [compat]. P8 matches all four Expected lines on all five overlays. For the `git status` line, see the next row |
| T4c′ | Step 5: "`git status` shows only the six chart files" (no build artefacts) | ok [absence]. Search: `git status --short \| command grep -c '\.tgz'` after `helm dependency build`. Result: `0`, although `ls charts/*.tgz \| wc -l` = `13` on disk, so the files are ignored and not merely absent. `git status --short` listed exactly the six T4 chart files (P16) |
| T4d | Steps 3–6: commands | Step 3 ok [negative]. Search target: every `$`-variable and every relative path in the block (plan `:1430-1436`). Result: `SCR` is defined on line 1 and `WT` on line 2; `v` and `o` are defined by the `for` loop; the relative paths `.`, `$v.yaml` and `$v.ci-override.yaml` resolve against `cd $WT/Iverson.Server/deploy/helm/iverson` on line 3; `$SCR/…` is absolute. Nothing comes from another block (R6). Steps 4, 5 and 6 → §2.2 (P11) |
| T4e | Runtime: the rendered blueprints apply in a real 2026.5.3 | ok [compat]. P9: `apply exit=0` twice, all six `successful`, the old user and token removed, and the workload user in the group with an unusable password |
| T4f | Runtime: the source fetches the JWKS from the worker's loopback | ok [compat]. P9: `source kids: ['cir-kid-1']`, served only inside the worker's network namespace |
| T4g | Runtime: the policy pins | ok [compat]. P9: other SA, marker header → `invalid_grant`; pinned SA, list or string `aud` → minted |
| T4h | Runtime: the API container reads its projected token at the mounted path | ok [compat]: a read check of the path chain. The rendered env `Authentik__OrchestratorAssertionPath` equals the volumeMount's `mountPath` plus `/` plus the projected source's `path: token` (P16, `equal: True`). The plan's `Program.cs` edit passes that configuration value as `assertionPath` (plan `:720-722`), and the handler reads exactly that file (`File.ReadAllTextAsync(assertionPath, …)`, plan `:482`). The file-permission part is **not load-bearing** here: it rests on VA41 (inherited, a relay at uid 65532 with `fsGroup` 1000 read its projected token), P12 renders `runAsUser 1000`/`fsGroup 1000` on the API pod, and T6 Step 6 exercises it live |
| T4i | NetworkPolicy | ok [compat]. P8 checker section 6, on every overlay: the selector, the overlay's CIDRs and ports 443/6443 |
| T5a | Runbook §9 content | ok [existence]. The §4 it cites is "First-deploy gate", which covers API-server egress (`runbook:108-136`). The apply command is P9's, with exit 0. A failed fetch raises `ValidationError({"oidc_jwks_url": …})` (`sources/oauth/api/source.py:110-114`), and `apply_blueprint.py` prints the log attributes, so the named key reaches the operator |
| T5b | Step 2: commit | → §2.2 |
| T6a | Step 1: suite loop | ok [totality]. `ls -d *.Tests` in `Iverson.Server` gives exactly the loop's 9 projects, and each `$p/$p.csproj` exists |
| T6b | Step 2: prepare | ok, not load-bearing. The block is self-contained |
| T6c | Step 3: install `main` | → §2.2 for `$WT`/`$LIVE`. Its content is ok [compat]: P13 for `git archive`; `kind-config.yaml`, `setup.sh` and `build-and-load-image.sh` exist; and the branch image runs static mode under `main`'s chart, because `charts/api/templates/deployment.yaml:191` sets `Authentik__AdminToken` there and T2's guard passes static-only (P2, `GoodConfig`) |
| T6d | Step 4: upgrade and runbook | → §2.2. Its content is ok [compat]: the apply command (P9) and the container names `worker`/`jwks-relay` (P12) |
| T6e | Step 5: live checks | → §2.2 for `$LIVE`. Its content is ok [compat]: `ak shell` and `tail -3` (P9), `exchange()` (P9), and svc ports 9000/8080 (P12). The two relay `kubectl exec` lines use the startup probe's exact exec form (VA42, inherited) and are verification only, so **not load-bearing** |
| T6f | Step 6: `CreateTenant` console | → §2.2 for `$LIVE`. Its content is ok [compat]: P14 builds it, and the tenant id has the shape of C1's `c1-live-check`, which passed live |
| T6g | Step 7: teardown and diff | → §2.2 for `$LIVE`. The `comm … \| head && echo no-image-removed-or-repointed` line prints its echo whatever the result, but the removed lines print above it. It is verification only, so it is dropped for literal-wrongness |

### Cross-task interface contracts

| # | Contract (producer → consumer operation) | Disposition |
|---|---|---|
| C1 | T1's `AuthentikOrchestratorTokenHandler(string, string, TimeProvider)` → T2 `Program.cs` and T3 (two call sites) | ok [compat]. Both call sites compile and run (P2, P6) |
| C2 | T2's keys `Authentik:OrchestratorAssertionPath`/`OrchestratorClientId` → T4's env `Authentik__…` | ok [compat]. P8 checker section 2 asserts both env names and values, and `__` binds to `:` in .NET configuration |
| C3 | T4's projected volume (`path: token`, mount `/var/run/secrets/iverson/authentik-orchestrator`) → T4's env value `…/authentik-orchestrator/token` | ok [compat] (P8 section 2) |
| C4 | T4's blueprint paths → the T5 runbook and T6 Step 4's `ak apply_blueprint` | ok [compat]. P9 mounted them as the chart does, and the apply exited 0 |
| C5 | T4's pinned `sub` `system:serviceaccount:<ns>:<release>-api` → T6's `kubectl create token iverson-api` in `iverson` | ok [compat]. P9 minted for exactly that `sub` |
| C6 | T4's `client_id` (blueprint) ↔ T4's env ↔ T6's `exchange()` | ok [compat] (P8, P9) |
| C7 | T4's audience (volume) ↔ the policy ↔ T6's `--audience` | ok [compat] (P8 section 2, P9) |
| C8 | The fixture's groups `iverson-admin-orchestrators`/`tenant-admins` → T3 | ok [compat] (P6) |
| C9 | Persistence boundary: the Secrets T4 stops rendering → their deletion in T5/T6 | ok [absence]. Search: `command grep -cE 'authentik-admin-orchestrator-(token\|user)'` over each edited overlay render. Result: `0` ×5, against `3` on the unmodified laptop render (positive control), and `Authentik__AdminToken` `0` ×5 (P16). That nothing renders them is why VA37's keep annotation (inherited) leaves them for the runbook's deletion step |
| C10 | Shell boundary: `SCR`/`WT`/`LIVE` from T4 Step 3 and T6 Step 2, and the cwd from earlier `cd`s → later blocks | → §2.2 |

### Rule-like content

| # | Rule | Over-inclusion | Under-inclusion |
|---|---|---|---|
| R1 | Guard: exactly one mode outside Development | ok [compat]. Both modes set → throws (`Guard_RefusesBothCredentialModes`, P2) | ok [compat]. Neither set → throws. Federated mode without a client id → throws. Static only, or federated with a client id → passes (P2). |
| R2 | Program attaches the handler when `AssertionPath is { Length: > 0 }` | dropped. A whitespace-only path would attach the handler while the guard counts it unset. No profile sets one: Helm sets a literal path, and compose sets none (`command grep -rn OrchestratorAssertionPath Iverson.Server/docker-compose.yml`: 0 hits) | ok [compat]. The theory's federated row attaches the handler (P2) |
| R3 | Freshness: `now < ExpiresAt − 60 s` | ok [compat]. Re-mints at 240 s, and the margin mutation is caught (P4) | ok [compat]. Reuses the token at 239 s (P2) |
| R4 | Workload policy: `sub` pin, `aud` as list or string, user and group gate | ok [compat]. Other SA, a wrong `aud`, and the marker → `invalid_grant` (P6, P9) | ok [compat]. The pinned SA mints with a list `aud` (P6, P9) and with a string `aud` (P9) |
| R5 | Every request body sent to Authentik must be length-delimited (population matrix below) | ok [compat] (no site flagged that works; the matrix's ok rows were run green) | → §2.1 (the T3 helper, P5) |
| R6 | A plan command block may rely only on state it sets itself (population matrix below) | ok [compat] (no self-contained block is flagged; each was read for state use) | → §2.2 |

**R5 population: every site in the plan that sends a body to Authentik.**

| Site | Body | Disposition |
|---|---|---|
| T3 `PostAsync` helper: 9 call sites (mapping, source, provider, application, 2 policies, 2 bindings, workload user) | `PostAsJsonAsync`, which is `JsonContent` and sent chunked | → §2.1 (P5: the first call gets a 400) |
| T3 `ExchangeAsync` | `FormUrlEncodedContent` | ok [compat] (P6) |
| T1 handler exchange | `FormUrlEncodedContent` | ok [compat]. P6: `CreateUserAsync` succeeded through the handler |
| `IdpAdminClient` through the handler (POST users, recovery) | `StringContent` via `JsonBody` (`IdpAdminClient.cs:349`) | ok [compat] (P6) |
| T6 Step 5 `exchange()`, T6 Step 6 token `curl` | `curl -d` (Content-Length) | ok [compat] (P9: MINTED/REFUSED) |
| T6 Step 6 gRPC call | HTTP/2 to the API, not to Authentik | n/a: not an Authentik body |

**R6 population: every step in the plan that carries a command block or a file edit.** It has 32 rows:
- 20 bash blocks;
- 2 steps that re-run an earlier block by reference (T1 S4, T3 S4), so 22 runnable rows in all;
- 10 non-runnable file edits or writes.

"Fresh" means a new shell per call, which is this harness (P11). "Persisted cwd" means an inline executor whose cwd persists but whose variables still do not. For each self-contained row, the search target is every `$`-variable and every relative path in the block, and the result names where each is defined within that same block.

| Block (plan line) | Search result: variables and relative paths, and where each is set | Fresh shell | Persisted cwd |
|---|---|---|---|
| T0 S1 (`:163`) | No variables. The relative `.worktrees/csr10-…` (and the ref `main`) resolve against `cd /home/ben/repositories/Iverson` on line 1. `mkdir` is absolute | ok [negative], **not load-bearing** (as T0 in §0) | ok [negative], **not load-bearing** |
| T1 S1 (`:182`) | Non-runnable: the test file content, written with the Write tool to the Files path, which is under the worktree named in Global Constraints | n/a: not a shell block | n/a: not a shell block |
| T1 S2 (`:372`) | No variables. `Iverson.Api.Tests/Iverson.Api.Tests.csproj` resolves against `cd /home/…/.worktrees/csr10-orchestrator-workload-identity/Iverson.Server` on line 1 | ok [negative] | ok [negative] |
| T1 S3 (`:381`) | Non-runnable: the handler file content (Write) | n/a: not a shell block | n/a: not a shell block |
| T1 S4 (prose, "same command as Step 2") | Same as T1 S2: its own absolute `cd` on line 1 | ok [negative] | ok [negative] |
| T1 S5 commit (`:533`) | Relative `Iverson.Server/…` paths with no `cd` in the block | → §2.2 (P11 `exit=128`) | → §2.2 (P11 `exit=128`: cwd is `<wt>/Iverson.Server`) |
| T2 S1 (`:555`–`:607`) | Non-runnable: three edits in `AuthentikTrustTests.cs` (Edit) | n/a: not a shell block | n/a: not a shell block |
| T2 S2 (`:636`) | No variables. `Iverson.Api.Tests/…csproj` resolves against the absolute `cd` on line 1 | ok [negative] | ok [negative] |
| T2 S3 (`:653`–`:677`) | Non-runnable: two edits in `AuthentikTrust.cs` (Edit) | n/a: not a shell block | n/a: not a shell block |
| T2 S4 (`:695`–`:716`) | Non-runnable: two edits in `Program.cs` (Edit) | n/a: not a shell block | n/a: not a shell block |
| T2 S5 test (`:731`) | Relative `Iverson.Api.Tests/…csproj` with no `cd` in the block | → §2.2 (no `Iverson.Api.Tests/` at the main checkout's root) | ok [compat]: Step 2's `cd` persists |
| T2 S6 commit (`:739`) | Relative `Iverson.Server/…` paths, no `cd` | → §2.2 (runs in the main checkout: nothing staged → `git commit` fails) | → §2.2 (pathspec, as T1 S5) |
| T3 S1 (prose) | Non-runnable: one `using` line added (Edit) | n/a: not a shell block | n/a: not a shell block |
| T3 S2 (`:760`) | Non-runnable: the integration code block (Edit) | n/a: not a shell block | n/a: not a shell block |
| T3 S3 (`:939`) | No variables. `Iverson.Api.Tests/…csproj` resolves against the absolute `cd` on line 1 | ok [negative] | ok [negative] |
| T3 S4 (prose: mutate, re-run, revert) | The edit is non-runnable. The re-run is T3 S3's command, whose own absolute `cd` is on line 1 | ok [negative] | ok [negative] |
| T3 S5 commit (`:955`) | Relative `Iverson.Server/…` path, no `cd` | → §2.2 | → §2.2 |
| T4 S1 (`:982`) | Non-runnable: "Write `$SCR/t4_edits.py`" (Write). `$SCR` is given as an absolute path in Global Constraints (`:46`) | n/a: not a shell block | n/a: not a shell block |
| T4 S2 (`:1329`) | Non-runnable: "Write `$SCR/check_t4.py`" (Write). `$SCR` as in T4 S1 | n/a: not a shell block | n/a: not a shell block |
| T4 S3 (`:1430`) | `SCR` is set on line 1 and `WT` on line 2; `v`/`o` come from the `for` loop. `.`, `$v.yaml` and `$v.ci-override.yaml` resolve against `cd $WT/…/helm/iverson` on line 3 | ok [negative] | ok [negative] |
| T4 S4 (`:1444`) | `$SCR`, `$WT` are not set in the block | → §2.2 (P11 `/t4_edits.py`) | → §2.2 (variables don't persist) |
| T4 S5 (`:1452`) | `$WT`, `$SCR` are not set | → §2.2 | → §2.2 |
| T4 S6 commit (`:1476`) | `$WT` is not set | → §2.2 (`cd` with no argument goes to `$HOME`, which is not a repo) | → §2.2 |
| T5 S1 (`:1492`) | Non-runnable: runbook text appended (Edit). Its inner bash blocks are runbook content, not plan commands | n/a: not a shell block | n/a: not a shell block |
| T5 S2 commit (`:1519`) | Relative `docs/runbooks/…`, no `cd` | → §2.2 (in the main checkout `docs/runbooks/…` is unmodified → nothing to commit) | → §2.2 |
| T6 S1 (`:1532`) | `p` comes from the `for` loop. `$p/$p.csproj` resolves against the absolute `cd` on line 1 | ok [negative] | ok [negative] |
| T6 S2 (`:1544`) | `SCR` and `LIVE` are set on line 1 and `WT` on line 2. Every output is `$LIVE/…`, which is absolute. No relative paths | ok [negative] | ok [negative] |
| T6 S3 (`:1555`) | `$WT`, `$LIVE` are not set | → §2.2 | → §2.2 |
| T6 S4 (`:1573`) | `$WT`, `$LIVE` are not set | → §2.2 | → §2.2 |
| T6 S5 (`:1594`) | `$LIVE` is not set | → §2.2 (port-forward logs and pids under `/`) | → §2.2 |
| T6 S6 (`:1631`) | `$LIVE` is not set | → §2.2 (P11 `mkdir: Permission denied`) | → §2.2 |
| T6 S7 (`:1660`) | `$LIVE` is not set | → §2.2 (`cat /pf-ak.pid` fails, and the user-state diffs read `/before-*.txt`) | → §2.2 |

## 1. Verified-plan-assumptions cross-check

1. Still holds. In the main checkout, `ls` of both new paths gives "No such file or directory".
2. Still holds. `command grep -n '^## '` on the runbook ends with `201:## 8. EKS`.
3. Still holds. P8's `edit()` asserted each target text in all six chart files.
4. Still holds. `FirstCall_ExchangesTheAssertion_ThenSendsTheAccessToken` asserts the absolute token URI and passed (P2).
5. Still holds. The same test asserts two requests in order, with the bearer on the second (P2).
6. Still holds. P2's wiring theory asserts `handlers[^1]` is a `SocketsHttpHandler`, and it passed.
7. Still holds. `FakeClock : TimeProvider` compiles and drives the 239/240 s assertions (P2, P4).
8. Still holds as scoped: 42/42 (P2). It covers compilation of T3, not T3's run; see the span check.
9. Still holds. P4 for the margin. P3 for the mode check: the guard tests fail without it.
10. Still holds, now at a higher tier: P6's live Authentik validated the C#-signed assertion against the C#-built JWKS.
11. Still holds. Every route was exercised in P6.
12. Still holds. P6 created the source with a static `oidc_jwks` and blank URLs.
13. Still holds (P6).
14. Still holds (P6).
15. Still holds (P6, and C8 above).
16. Still holds. See T2h: the grep finds only the `AuthentikTrustTests` call sites (`:195,203,209,213,217`) plus `Program.cs:466`.
17. Still holds. `AuthentikOrchestratorRoleBlueprintTests` passed 6/6 against the edited chart (P8).
18. Still holds. On HEAD, `command grep -rn adminOrchestrator Iverson.Server/deploy` hits only `secret-service-clients.yaml:95-108,199,209,663,673`. The edited chart renders on all five overlays, which a dangling variable reference would break (P8).
19. Still holds. `command grep -rni --exclude='.env*' 'AdminToken\|orchestrator token\|orchestrator-token' docs/runbooks Iverson.Server/deploy/kind` finds 0 hits.
20. Still holds (P8).
21. Still holds (P8).
22. Still holds (P8: `relay: 5`, other containers changed 0).
23. Still holds (P15).
24. Still holds (P8 checker section 6, ×5).
25. Still holds. See T6a: the 9 `*.Tests` directories equal the loop.
26. Still holds. `ls Iverson.Server/deploy/kind` lists `build-and-load-image.sh`, `kind-config.yaml` and `setup.sh`.
27. Still holds (P13).
28. Still holds (P14).
29. Still holds. `kubectl create token --help` (client v1.36.2) lists `--audience` and `--duration`.
30. Still holds. `git log --oneline -6 main` subjects are lowercase imperative.
31. Still holds. T2/T3 construct T1's class (C1). T4 touches only chart files and T5 only the runbook (P8's `git status`).

**Span check:**
- **The request encoding T3 depends on.** Authentik must accept T3's request bodies. No row covers this: row 8 ran a filter that excludes the integration class, and rows 11–14 are source reads. → **failed**, §2.1 (P5).
- **The shell state the plan's command blocks depend on.** Blocks rely on variables and a cwd carried over from earlier blocks. No row covers this: rows 25–28 verify the commands' content, not their shell context. → **failed**, §2.2 (P11).
- **The Helm-rendered federation and service-clients blueprints apply in a real 2026.5.3, and the upgrade removes the old objects.** No plan row covers this; spec VA9/VA17 used probe blueprints, not the plan's render. Verified in-round (P9).
- **T6 Step 5's `ak shell … | tail -3` yields the three prints.** No row covers this. Verified in-round (P9).
- **The API container can read its projected token.** Covered in-round by T4h.

## 2. Literal-wrongness findings

### 2.1 T3's helper sends every admin-API body chunked, which Authentik discards, so the new fact cannot pass

**Description.** T3 Step 2's `PostAsync` helper posts with `admin.PostAsJsonAsync(path, body)`. `JsonContent` serialises lazily and sets no `Content-Length`, so `HttpClient` sends the body with `Transfer-Encoding: chunked`. Authentik's ASGI server drops a chunked body, and DRF answers "This field is required." for every field that was in fact sent. The codebase already records this trap in `IdpAdminClient.cs:339-347`: "Every request body in this class must therefore go through here." The first `CreateFederationObjectsAsync` call therefore fails, and the fact never reaches its exchange checks. T3 Step 3's Expected (`Passed!`, 0 failed) is unreachable.

The step's own recovery advice makes it worse: "Fix the payload field it names". The 400 names `name` and `expression`, which are correct. An executor following the advice edits the wrong thing.

**Evidence (finding).** [compat] P5 is T3 Step 3's command, run verbatim against the live fixture (Authentik 2026.5.3). The new fact failed with `POST /api/v3/propertymappings/source/oauth/ returned 400: {"name":["This field is required."],"expression":["This field is required."]}`. The other 9 facts passed. P6 is the discriminating control: the same anonymous object, serialised and sent as a length-delimited `StringContent`, was accepted, and the fact passed.

**Proposed fix.**
- [compat] In T3 Step 2's block, replace `using var response = await admin.PostAsJsonAsync(path, body);` with `using var response = await admin.PostAsync(path, Json(JsonSerializer.Serialize(body)));`. `Json(string)` is the class's existing length-delimited helper (`AuthentikRecoveryFlowIntegrationTests.cs:87` at HEAD, `private static StringContent Json(string json)`), and `using System.Text.Json;` is already at `:5`.
- [compat] In T3 Step 3, replace "Fix the payload field it names; the routes and fields themselves were verified (plan rows 11–14)" with a note that every body must be length-delimited (`IdpAdminClient.cs:339-347`), because a chunked body reaches Authentik empty and every field is reported missing.
- [existence] Update the plan's scratch proof `c2/code/IntegrationFact.cs.txt` to match, so the plan and its proof stay equal (P1). Path check: `ls -la …/scratchpad/c2/code/IntegrationFact.cs.txt` shows the file exists (10765 bytes), and `c2/t1_t3_apply.py` reads it to build T3.

Evidence: [compat] P6: with exactly the first change, the new fact passed (20 s) and T3 Step 3's command gave `Passed!  - Failed: 0, Passed: 10`. P7: with the change applied, T3 Step 4's mutation fails at the exchange (`HTTP 400 invalid_grant`), so the fact stays falsifiable. [negative] R5's matrix shows no other body-sending site in the plan uses `JsonContent`: the `ExchangeAsync` and handler bodies are `FormUrlEncodedContent`, `IdpAdminClient` uses `JsonBody`, and T6 uses `curl -d`. All of those ran green in P6 and P9.

### 2.2 Most command blocks depend on shell variables or a working directory left by an earlier block, and the harness keeps neither

**Description.** Each Bash call here starts with no variables from the previous call and with the cwd reset to the main checkout (P11). Many blocks still use `$SCR`, `$WT` or `$LIVE` defined only in an earlier block, or repo-relative paths that need a `cd` an earlier block made. R6's matrix lists all 22 runnable rows (20 bash blocks plus T1 S4 and T3 S4, which re-run earlier blocks) and the 10 non-runnable edit steps. 13 of the 22 runnable rows fail in the harness's fresh shell, and the other 9 are self-contained. In summary:
- **T1 S5, T2 S6, T3 S5 and T5 S2 (commits).** They use repo-root-relative paths with no `cd`. From the reset cwd (the main checkout), `git add` fails (P11, `exit=128`), or it stages nothing because the main checkout's copies are unmodified. `git commit` then runs in the main checkout: it fails with nothing to commit, or it commits whatever happens to be staged there onto `main`. From a persisted `<wt>/Iverson.Server` cwd, the paths double and `git add` fails (P11, `exit=128`).
- **T2 S5 (tests).** It has no `cd`. From the main checkout, `Iverson.Api.Tests/Iverson.Api.Tests.csproj` does not exist.
- **T4 S4–S6.** They use `$SCR` and `$WT`. Step 4 runs `python3 /t4_edits.py` (P11, exit 2). Step 5 `cd`s to a path that does not exist. Step 6's bare `cd` lands in `$HOME`, which is not a repo.
- **T6 S3–S7.** They use `$WT` and `$LIVE`. Kind creation, the chart copy, the port-forward logs and pids, the console directory (P11, `Permission denied`), and the teardown's user-state diffs all resolve under `/` or `$HOME`. The diffs are the plan's only proof that the user's containers, volumes, networks and images are unchanged.

The C1 plan, which this plan cites for its kind commands (row 26), avoided exactly this by stating "Every block starts with `source $SCR/c1/live/env.sh`" (`2026-10-05-…-identity-plane-implementation-plan.md:782`).

**Evidence (finding).** [compat] P11:
- A variable set in one call was empty in the next, and the cwd reset to `/home/ben/repositories/Iverson`.
- T1 S5's `git add --dry-run` exited 128 from both possible cwds and 0 only from the worktree root, which no step `cd`s to.
- T4 S4 run in a fresh shell gave `can't open file '/t4_edits.py'`.
- T6 S6's `mkdir -p $LIVE/createtenant` gave `Permission denied`.

**Proposed fix.**
- [compat] Add a "Shell state" rule to Global Constraints: every bash block begins with `SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/c2-sdd; WT=/home/ben/repositories/Iverson/.worktrees/csr10-orchestrator-workload-identity; LIVE=$SCR/live`. Either prepend that line to T4 S4–S6 and T6 S3–S7, or write it once to `$SCR/env.sh` in Task 0 and `source` it, as C1 did (C1 plan `:782`, `:796-805`). Evidence: [compat] P16 executed the line in a fresh `env -i` shell. T4 S4's command then expanded to the absolute `python3 /tmp/…/c2-sdd/t4_edits.py /home/ben/repositories/Iverson/.worktrees/csr10-orchestrator-workload-identity/Iverson.Server/deploy/helm/iverson`, and T6 S6's to `mkdir -p /tmp/…/c2-sdd/live/createtenant`, under an existing, writable parent. The same blocks failed without the line (P11). T4 S3 and T6 S2, which already define their variables first, run correctly (R6, ok rows).
- [compat] Begin T1 S5, T2 S6, T3 S5 and T5 S2 with `cd /home/ben/repositories/Iverson/.worktrees/csr10-orchestrator-workload-identity &&`, or use `git -C <worktree>` for both `add` and `commit`. Evidence: [compat] P11: the same `git add --dry-run` of T1 S5's paths exits `0` from the worktree root, against `128` from the main checkout and from `<wt>/Iverson.Server`.
- [compat] Begin T2 S5 with `cd /home/ben/repositories/Iverson/.worktrees/csr10-orchestrator-workload-identity/Iverson.Server &&`. Evidence: [compat] P16: from `<wt>/Iverson.Server`, `ls Iverson.Api.Tests/Iverson.Api.Tests.csproj` resolves, and `git ls-tree 15f8aa93` lists `Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj`. P2 ran T2 S5's exact `dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter …` from that directory and got 42/42.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes.** Two findings: §2.1 (T3's chunked request bodies) and §2.2 (shell state across blocks). Nothing else blocks execution. The handler, guard, wiring, chart edits and the Helm-rendered blueprints all behaved as the plan states when run: the unit tests, the live fixture with §2.1's fix, the five-overlay render gates, and an isolated Authentik 2026.5.3 running the chart's own rendered blueprints through the T6 commands.

Slot audit: pass after 6 fixes (independent audit)
