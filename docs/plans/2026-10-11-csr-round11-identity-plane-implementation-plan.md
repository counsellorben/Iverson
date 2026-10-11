# CSR Round 11 Sub-project B: Identity Plane Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-10-09-csr-round11-identity-plane-design.md` (commit SHA: `3de6e75c`)

**Goal:** harden the Authentik identity plane against findings #5, #7, #8 and #9 of `docs/criticalreviews/2026-10-08-iverson-critical-security-review-11.md`. The plan covers:
- a trustworthy client address;
- login throttling at Authentik and at the cloud edge;
- MFA bound at recovery time;
- no superusers on the public listener;
- no standing admin-automation secret on Helm;
- bounded orchestrator assertions.

**Architecture:**
- **Client address.** `global.trustedProxies` feeds both the API's rate limiters and a `geo`/`map` selection in the Authentik tls-proxy. Authentik then trusts only that sidecar, so it records one bare client address.
- **Authentik blueprints.** A failure-only reputation policy and recovery-time TOTP enrolment live in static blueprints, shared with compose. Helm-only templated blueprints add four things:
  - deny stages for superusers on the public listener;
  - `akadmin` deactivated;
  - a password-less break-glass admin;
  - a self-contained admin-automation federation file keyed on a new ServiceAccount.
- **Tooling.** LoadTest and the .NET SDK gain an assertion-file mode and a service-token provider.
- **Cloud edge.** Terraform adds per-address rate limits on the login path: AWS WAFv2, GCP Cloud Armor, and Azure WAF on a Terraform-owned `WAF_v2` gateway. Helm wires them to the Authentik Ingress.

**Tech stack:**
- .NET 10: `Iverson.Api.Tests`, `Iverson.LoadTest(.Tests)`, and `Iverson.Client.Core(.Tests)`. Test libraries are xUnit 2.9.3, FluentAssertions 8.11, Testcontainers 4.15.0 and IdentityModel 7.0.0.
- Helm 3 (umbrella chart `Iverson.Server/deploy/helm/iverson`).
- Authentik 2026.5.3.
- nginx 1.30.5 (`nginxinc/nginx-unprivileged:1.30.5-alpine`).
- Terraform 1.9.8 locally (CI pins 1.7.5), with providers aws 5.100.0, google 5.45.2 and azurerm 3.117.1.
- kind with podman, for the live check.

---

## Global Constraints

Copied from the spec, plus the session rules every task must hold to.

**From the spec:**
- **One branch, one merge.** No proto change. One additive .NET SDK change (Task 6): `AddIversonClient` gains an optional service-token provider; `IversonClientCredentials` and the other SDKs are untouched.
- **Conformance:** no requirement changes.
- **Security write-ups** (comments, test names, docs) describe conditions and behaviour, never step-by-step exploitation.
- **`docs/` is gitignored:** commit docs files with `git add -f`.
- **The user's running stack is off-limits.**
  - Never start, stop or modify its `iversonserver` containers, volumes, networks or images (`iverson-api:latest`, `iverson-api:0.1.0`, `localhost/iverson-api:0.1.0`), or another session's `rfdt-*` containers and `tei_models` volume.
  - Never run `docker compose up` against `Iverson.Server/docker-compose.yml`, except as Task 10's isolated `-p csr11b` project from a `container_name`-stripped copy.
  - Live checks use throwaway resources, which they delete afterwards.
- **Memory.** Run one heavy process at a time, because a WSL OOM kills the whole VM. Wrap builds and test runs in `systemd-run --user --scope -q -p MemoryMax=…`. Stage kind image loads under `/var/tmp`, never `/tmp`.
- **`docs/security/tma.md` is not edited** (user decision 2026-10-11). The user's uncommitted TMA refresh holds it, and the next refresh records this remediation.

**Session rules:**
- **Location.** Work in the worktree `/home/ben/repositories/Iverson/.worktrees/csr11-identity-plane` on branch `csr11-identity-plane`. Use absolute paths, and never `cd` to the main checkout.
- **Commands.**
  - Use `command grep`, because plain `grep` is a gitignore-aware ugrep that skips `docs/`.
  - Every repo-wide grep excludes `.env*`, and nobody reads `Iverson.Server/.env` or the worktree's `.env` beyond the `val` helper Task 10 defines.
  - The docker CLI is podman.
  - Kill processes by PID, never with `pkill -f`.
  - Never use `git stash`.
- **Helm.** Run `helm dependency build` in `Iverson.Server/deploy/helm/iverson` before every `helm template` or `helm lint`, because a stale `charts/*.tgz` renders old subchart content. Render cloud profiles with their `*.ci-override.yaml`, as CI does.
- **Commits.**
  - Add the task's files by explicit path. Never use `-A` or `.`, and never touch `.superpowers/` or `docs/security/tma.md`.
  - Messages are lowercase imperative and end with `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`. A subagent puts exactly this trailer, never its own model name.
- **Scratch.** Scratch files go under `$SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd`, never in the repo.
- **Shell state.** Each bash block runs in a fresh shell, and no variable or working directory carries over. Blocks that use `$SCR`, `$WT` or `$LIVE` start with `source $SCR/env.sh`, written out in full as `source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh`, which Task 0 writes.

## File Structure

**Create:**
- `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/reputation.yaml`: the failure-only login throttle (static; compose and the Api.Tests fixture apply it too).
- `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/serviceaccount-admin-automation.yaml`: the `<release>-admin-automation` ServiceAccount.
- `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/backendconfig.yaml`: the GKE BackendConfig that carries the Cloud Armor policy.
- `Iverson.Server/Iverson.LoadTest/Auth/AdminAutomationCredentials.cs`: the secret or assertion-file identity, plus token caching.
- `Iverson.Server/Iverson.LoadTest.Tests/Auth/AdminAutomationCredentialsTests.cs`.
- `Iverson.Server/deploy/terraform/aws/waf.tf`, `gcp/waf.tf`, `azure/waf.tf` and `modules/cluster-azure/waf.tf`.
- `docs/runbooks/csr11-identity-plane.md`.

**Modify:**
- **Umbrella chart:** `values.yaml` (`global.trustedProxies`); `values-{aws,azure,gcp,laptop,local}.yaml` (the block moves from `api:` to `global:`; the cloud profiles gain WAF values); `values-{aws,azure,gcp}.ci-override.yaml` (placeholder WAF values).
- **`charts/api/`:** `values.yaml` and `templates/deployment.yaml` read the global key.
- **`charts/authentik/`:** `values.yaml`, `blueprints/recovery-flow.yaml`, and these templates: `configmap.yaml`, `configmap-tls-proxy.yaml`, `blueprints-configmap.yaml`, `secret-service-clients.yaml`, `deployment-server.yaml`, `ingress.yaml`, `service.yaml`.
- `Iverson.Server/docker-compose.yml`: `AUTHENTIK_LISTEN__TRUSTED_PROXY_CIDRS` on `authentik-server`.
- `Iverson.Server/Iverson.LoadTest/Auth/AuthentikFlowExecutorClient.cs`, `Program.cs` and `README.md`.
- `Iverson.Clients/DotNet/Iverson.Client.Core/ServiceCollectionExtensions.cs`.
- **Tests:**
  - `Iverson.Server/Iverson.LoadTest.Tests/Auth/AuthentikFlowExecutorClientTests.cs`;
  - `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/ServiceCollectionExtensionsTests.cs`;
  - `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikRecoveryFlowIntegrationTests.cs`;
  - `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikOrchestratorFederationBlueprintTests.cs`.
- **Terraform:** `Iverson.Server/deploy/terraform/azure/main.tf`, `modules/cluster-azure/{main,variables,outputs}.tf`.
- **Docs:** `docs/user-management-and-security.md`, `docs/runbooks/operator-access-onboarding.md`, `docs/runbooks/admin-console-landing-page-usage.md`, `docs/runbooks/grpc-admin-auth-cutover.md`.

## Inherited from spec

The spec's verified assumptions (`docs/specs/2026-10-09-csr-round11-identity-plane-design.md`, "Verified assumptions", items 1–45) are trusted as ground truth and not re-verified here. Each spec item carries its evidence. In brief:

1. No built-in login throttling.
2. The stock reputation policy refuses when bound flow-level and negated.
3. Authentik takes the left-most `X-Forwarded-For` entry from a trusted peer.
4. Superuser refusal works only as a deny stage after identification.
5. Recovery with `configure` forces TOTP enrolment.
6. `akadmin` deactivation survives restarts.
7. Expression policies read numeric `iat`/`exp` and the nested `kubernetes.io` claim.
8. Projected tokens carry `kubernetes.io.pod`, and unbound `kubectl create token` tokens don't.
9. TokenReview catches a deleted pod's token, and JWKS doesn't.
10. `real_ip_recursive` is wrong for 2 hops.
11. The `geo`/`map` selection works on nginx 1.30.5.
12. Only the api chart reads `trustedProxies`.
13. The tls-proxy image is 1.30.5 and sets no `X-Forwarded-For` today.
14. Authentik's client IP comes from `ClientIPMiddleware`.
15. The compose tls-proxy has only `:8443`.
16. Kind's ingress-nginx overwrites `X-Forwarded-For`.
17. No blueprint reads the client IP.
18. Recovery-flow bindings were 0–4.
19. `default-authentication-flow` binds 10/20/30/100.
20. `ReputationPolicy` fields and defaults; scores are deletable.
21. `denystage` and binding defaults.
22. Bootstrap runs in server and worker.
23. `authentik Admins` superuser.
24. `ak create_recovery_key` bypasses the flow, and `_create_recovery_link` doesn't.
25. Nothing on the Helm path logs in as `akadmin`.
26. The orchestrator pin is the templated file.
27. The projected token's 600 s and its per-mint re-read.
28. `jwt_federation_sources`; a blank secret means unauthenticated; `partial=True`.
29. A cross-file `!Find` failure fails the whole file; apply order isn't guaranteed.
30. The relay serves the cluster JWKS.
31. The service-clients Secret is mounted whole.
32. LoadTest mints with IdentityModel 7.0.0.
33. Conformance is unaffected.
34. Terraform layout.
35. Pinned providers validate the planned resources.
36. One ALB per Ingress; the annotations exist.
37. All Azure Ingresses use AGIC.
38. Blueprints re-apply on hash change.
39. Reputation rows floor at −5.
40. The bare-address map.
41. SDK registration.
42. Azure WAF policy modes.
43. Encoded executor paths.
44. The AGIC add-on grant.
45. Successes offset failures in the stock policy; the failure-only expression policy and the `policybindingmodel` binding target.

## Verified plan-level assumptions

Verified at plan-write time (2026-10-11, `main` at `3de6e75c`). Scratch evidence is under `/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-plan/`: `probe-report.md`, `render/`, `tf/` and `dotnet/`.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | None of the files this plan creates exists yet. | `[ -e ]` on all eight paths: each `absent`. |
| 2 | File path | The tls-proxy file is included inside `http {}`, so top-level `geo`/`map` are valid. | Image `1c5c57be5370` is `nginx/1.30.5`. Its `nginx.conf` has `http {` at line 13 and `include /etc/nginx/conf.d/*.conf;` at line 36. The chart mounts the ConfigMap at `/etc/nginx/conf.d` (`deployment-server.yaml:181`). |
| 3 | File path | `authentik-config` is `envFrom`'d by the server (and the worker). | `deployment-server.yaml:97-99` and `deployment-worker.yaml:75-77`. |
| 4 | File path | The doc lines edited say what Task 9 replaces. | Read: `user-management-and-security.md:130-131` (kind bootstrap) and `:485-488`; `operator-access-onboarding.md:27-29`; `admin-console-landing-page-usage.md:100-102`; `grpc-admin-auth-cutover.md:35-38` ("3 of which carry both…"). |
| 5 | File path | The worktree path and branch name are free. | `git branch --list 'csr11*'` and `ls .worktrees` both print nothing for them. |
| 6 | Signature | `IversonClientCredentials(ClientId, ClientSecret, TokenEndpoint, Scope = null, HostHeader = null)`. | `IversonClientCredentials.cs:12-17`. |
| 7 | Signature | IdentityModel 7.0.0 sends `ClientAssertion` as `client_assertion_type`/`client_assertion` in the body (with `ClientCredentialStyle.PostBody`), and a secret without an assertion. | `IdentityModel.xml`: `P:…ProtocolRequest.ClientAssertion` and `T:…ClientAssertion` (`Type`, `Value`). Scratch run of Task 7's tests at plan time: secret mode sends HTTP `Basic` with a form of only `grant_type,scope`; assertion mode sends `client_id`, `client_assertion_type`, `client_assertion` in the body and no `Authorization` (18/18 passed). |
| 8 | Signature | Grpc.Net.ClientFactory 2.83.0 names each client's `HttpClient` `typeof(TClient).Name`; a `HttpClientFactoryOptions` action added after `AddIversonClient` replaces the primary handler; call credentials reach it on an https address with no opt-in. | Scratch run of Task 6's tests at plan time: 13/13 passed; removing the mapping `else if` makes both header facts fail. |
| 9 | Signature | A cheap unary RPC per client: `GetSchema`, `Persistence.Post`, `Retrieval.Get`, `Search.Aggregate`. | `Iverson.Clients/Common/Proto/object_{mapping,persistence,retrieval,search}.proto`. |
| 10 | Signature | `AuthentikFlowExecutorClient` members reused: `Totp`, `ParseTotpSecretFromConfigUrl`, `SaveCachedTotpSecret`, `SendAsync`, `JsonBody`, `SubmitTotpCodeAsync(flowUrl, secret, TotpAttemptState)`, `MaxFlowStages`. | `AuthentikFlowExecutorClient.cs:68,132,161,278,299,302`, and `:25`. |
| 11 | Signature | Api.Tests helpers used: `Assertion`, `ExchangeAsync`, `RecoveryFlowSession.{StartAsync,SubmitPasswordAsync,AuthenticatedUsernameAsync,LastChallengeBody,ReadComponentAsync}`, `_orchestrator.CreateUserAsync`. | `AuthentikRecoveryFlowIntegrationTests.cs:200,256,551-627`. |
| 12 | Ordering | `FederationBlockLines` ends at the next two-space key, so Task 3's appended key doesn't change it. `ServiceClientEntries` (split by six-space items from the first `entries:`) still finds the workload user and the retired token after Task 5's appended key, because the new entries name neither. | `AuthentikOrchestratorFederationBlueprintTests.cs:142-187`. The scratch render of all edits gives 33 `service-clients` entries and 12 `admin-automation` entries. |
| 13 | Command | `dotnet test <proj> --filter …` under `systemd-run … MemoryMax`; the Authentik class needs containers. | Round 11 A's plan used the same form. `AuthentikContainerFixture` is Testcontainers. |
| 14 | Command | All five profiles render (cloud ones with `*.ci-override.yaml`). | Scratch render at HEAD: every profile `exit=0`. |
| 15 | Command | `terraform fmt -check -recursive`, `init -backend=false -lockfile=readonly` and `validate` per root, and `tfsec`, all run locally. | Scratch copy with all of Task 8: `fmt exit=0`, `init=0` ×3, "Success! The configuration is valid." ×3; tfsec "No problems detected!" (100 passed, 33 ignored; HEAD also 0). |
| 16 | Command | nginx 1.30.5 is cached locally. | `docker images`: `nginx-unprivileged <none> 1c5c57be5370`, which reports `nginx/1.30.5`. |
| 17 | Command | The kind bring-up is the same as round 10 C2's. | `docs/plans/2026-10-07-…-implementation-plan.md` Task 6. `kind-config.yaml` binds ingress to `127.0.0.1:8080`. |
| 18 | Command | `up -d authentik-server authentik-worker` brings up only migrate, postgres and redis besides them. The default network is `<project>_default`. | `docker-compose.yml:353-440` `depends_on`; the file has no top-level `networks:`. |
| 19 | Command | Commit style is lowercase imperative. | `git log --format=%s -8`. |
| 20 | Ordering | Task 3 needs Task 2's order 0; Task 7 needs Task 6's parameter; nothing else crosses. | Task Interfaces; `recovery-flow.yaml` before Task 2 binds the prompt at 0. |
| 21 | Code | `dig "trustedProxies" "cidrs" (list) .Values.global` is nil-safe in both subcharts, and the moved values render the API env byte-identically. | Scratch render of Task 1: `api-env-same=yes` on all five profiles, and `trusted=1`. |
| 22 | Code | The rendered tls-proxy config selects and normalises the address as designed. | nginx 1.30.5 run of Task 1 Step 7's script: all 11 expected outputs. |
| 23 | Code | A `policybinding` can target a `flowstagebinding` via `!KeyOf`; the deny stage sees the pending user; `!Find` resolves `iverson-recovery` from a templated file. | Probe P1 (apply exit 0, twice) and P4: marker plus superuser → `ak-stage-access-denied`, at login after identification and at recovery's first GET. |
| 24 | Code | No-device recovery with one configuration stage serves `ak-stage-authenticator-totp` directly, after two 302s; the code POST then 302s three times to `xak-flow-redirect`. | Probe P2. A device holder gets `ak-stage-authenticator-validate` (P3). A wrong code → 200 with `response_errors.code`. |
| 25 | Code | The break-glass `ak shell -c` snippet returns a working link whose host is the request's. | Probe P5: exit 0, `http://localhost:9000/if/flow/iverson-recovery/?flow_token=…`, enrolled and signed in. |
| 26 | Code | The admin-automation provider refuses a secret grant (pin policy, no `oauth_jwt`), a 3600 s assertion, a wrong `sub` and the marker; a 600 s assertion mints `admin schema_admin tenant_id_admin` with `tenant_id=tenant_admin`. | Probe P7 (a)–(f). The refused secret grant leaves Authentik's own client-credentials service-account user behind, minting nothing. |
| 27 | Code | `akadmin`'s templated entry works whichever of bootstrap and the chart applies first. | Probe P1: delete, then re-apply → inactive, no password, exit 0. P8: restart and a forced bootstrap keep it inactive. |
| 28 | Code | The orchestrator pin clauses behave: pod-bound 600 s passes; no pod, 3600 s and a string `iat` fail without an exception. | Probe P9. |
| 29 | Code | The stock reputation `!Find` flow binding target fails every re-apply; the `policybindingmodel` `!Find` re-applies cleanly. | Probe U1. |
| 30 | Code | The failure-only expression policy refuses an address after five failures even after a success from it, and spread across usernames. Other addresses are unaffected, and re-applies exit 0. | Follow-up probe R1–R4. R1: re-apply ×2 exit 0, one binding on the flow's `pbm_uuid`, `negate=False`. R2: after +1 for X and five failures for Y from one address, the 6th first GET is `ak-stage-access-denied` (stock policy sum −4 would not block); X is then refused too; peer B logs in. R3: rotating three usernames, refused on the 6th. R4: no `policy_exception`. |
| 31 | Code | Terraform value domains: WAF `evaluation_window_sec` ∈ {60,120,300,600}, `limit` ≥ 10; Cloud Armor `interval_sec` list and `ban_duration_sec` 600 valid; `deny(429)`; App Gateway `OneMin`/`FiveMins`. | AWS `API_RateBasedStatement` docs; Cloud Armor rate-limiting overview; azurerm 3.117.1 schema; `validate` (row 15). |
| 32 | Code | Cloud Armor CEL has `request.path`, `.urlDecode()` and `.startsWith()`, and `request.path` is raw. | Cloud Armor rules-language reference. |
| 33 | Code | The BackendConfig shape and the Service annotation render as designed; kubeconform skips the CRD; kube-score critical counts are unchanged. | Scratch render: gcp `{"ports": {"9080": "iverson-authentik"}}` and `securityPolicy.name`; kubeconform `Invalid: 0` on all five profiles; kube-score critical counts equal to HEAD's per profile. |
| 34 | Code | The WAF guard fires only for alb, gce and azure-application-gateway with an empty value. | Scratch: `--set authentik.ingress.<key>=` → `execution error at (iverson/charts/authentik/templates/ingress.yaml:3:4)`; laptop and local render. |
| 35 | Consumer | Inserting `serviceTokenProvider` before `params entityAssemblies` breaks no call site. | All 10 call sites pass arguments after the third by name (`ServiceCollectionExtensionsTests.cs` ×8, `Iverson.Client.Sample/Program.cs:34`, `Iverson.LoadTest/Program.cs:184`). |
| 36 | Consumer | Nothing on Helm reads the admin-automation `client-secret` except `secret-service-clients.yaml`; one doc paragraph says Helm stores it. | Repo-wide grep (excluding `.env*`, `bin`, `obj`, worktrees and design docs): `charts/api` reads `client-id` only. `user-management-and-security.md:485-488` is edited in Task 9. |
| 37 | Consumer | `trustedProxies` has no consumer outside the api chart and the profiles; Api.Tests use config keys. | Grep: `charts/api/templates/deployment.yaml`, `charts/api/values.yaml`, `values-*.yaml` ×5; `Iverson.Api.Tests/RateLimiting/*` use `RateLimiting:TrustedProxies:*`. |
| 38 | Consumer | Every renderer of the cloud profiles passes the `ci-override`. | `.github/workflows/deploy-validate.yml:34,70,114`, `image-scan.yml:71` and `.gitlab-ci.yml:42,65,100`. |
| 39 | Consumer | The Api.Tests fixture mounts `blueprints/` (`AuthentikContainerFixture.cs:253`); no fact logs in through `default-authentication-flow`; the `CreateUserAsync_*` facts don't walk the flow. | Grep of `Iverson.Api.Tests` for `default-authentication-flow` and `flows/executor`: only `iverson-recovery` at `:572`. |
| 40 | Consumer | LoadTest's existing `SetPasswordFromRecoveryLinkAsync_*` facts never reach the TOTP stage, so they hold. | `AuthentikFlowExecutorClientTests.cs:54-137`: each scripts prompt, 302s and an end component, with no `config_url`. |
| 41 | Consumer | Compose keeps admin-automation secret-based, and never sees the templated keys. | `compose-only/service-clients.yaml` is static; `superusers.yaml` and `admin-automation.yaml` exist only in templates. |
| 42 | Consumer | LoadTest's README documents its environment variables in one table. | `Iverson.LoadTest/README.md:168-170`. |
| 43 | Code | The plan's own code blocks for Tasks 1–5 apply cleanly to `main` and pass. With Tasks 6–8 they render all five profiles and pass Task 10's render check. | `replay.py` applied Tasks 1–5 from this plan's code blocks to a scratch worktree at `a018a182`, plus the Task 6–8 drafts. Results: Client.Core.Tests 13/13; LoadTest.Tests 134/134; the Authentik classes (`AuthentikRecoveryFlowIntegrationTests`, `…RoleBlueprintTests`, `…FederationBlueprintTests`) 23/23 against a real Authentik 2026.5.3 in 4 m 25 s; Task 10 Step 3 `OK` ×5. |
| 44 | Code | `recovery-flow.yaml`'s `metaapplyblueprint` entry makes its `!Find` of the stock TOTP setup stage independent of apply order. | Probe M1 (2026-10-11): with the stock stage deleted, the file without the entry exits 1 (`configuration_stages: Invalid pk "None"`). M2: with the entry, the stock blueprint is applied inside validation, the apply exits 0 and the stage is back in `configuration_stages`; a re-apply exits 0. M3: on a fresh volume the stock instance exists from migrate, the file's first automatic apply is `successful`, and `flow_recovery` is set about 45 s after worker start. M4: `GET /api/v3/stages/authenticator/totp/?name=default-authenticator-totp-setup` returns count 1 or 0 by exact name. |
| 45 | Code | Authentik parses `AUTHENTIK_LISTEN__TRUSTED_PROXY_CIDRS` as a comma-separated list. `ak apply_blueprint` exits non-zero only on a validation failure. | CIR-2 S1: `strings /usr/bin/authentik-server` (image 2026.5.3, read-only mount) shows `env:"TRUSTED_PROXY_CIDRS, overwrite"` with no `delimiter=`, decoded by go-envconfig v1.3.0 (default comma). CIR-2 S5: `authentik/blueprints/management/commands/apply_blueprint.py` has one `sys_exit(1)`, on `not valid`. |
| 46 | Code | The meta entry resolves in every consumer that mounts `blueprints/`: the stock `BlueprintInstance` exists from `authentik-migrate` before the worker runs, and the Api.Tests fixture's extra mounted files neither create nor alter the stock TOTP flow or stage. | `authentik/blueprints/v1/meta/apply_blueprint.py:33-34` (`if not instance and required`). Probe M3: instance created during migrate; first automatic apply `successful`. CIR-2 S2-1: the fixture's migrate is M3's `lifecycle.migrate` (`AuthentikContainerFixture.cs:167-176`), and a grep of the mounted files finds no write to the stock TOTP flow or stage. |
| 47 | Code | Task 5's lookup regenerates the client id when the Secret or its `client-id` key is missing. On upgrade, the moved marker-policy binding updates the existing row rather than duplicating it. | CIR-2 S3: a four-case scratch chart (Secret with key / without key / without `data` / absent), input → output. `sed -n 737,846p authentik/core/models.py \| command grep -c primary_key` → `0`: `Application` inherits `pbm_uuid`, so `!KeyOf` and `!Find` resolve to the same target. |
| 48 | Code | Task 10 Step 3's `recovery configure`, `superusers file` and `reputation file` checks parse the blueprints, so they fail on the broken renders the spec's render list names. | UIP-2 run of the plan's Step 3 script with these entries: True on the replayed renders of all five profiles. False on `skip`, no `configuration_stages`, WebAuthn in place of TOTP, `akadmin` active, deny at order 25, binding removed, `score__lt=0` removed, and a flow-`!Find` binding target (CIR-2 §2.1–2.3 reproduced). |

## Tasks

### Task 0: Branch

**Files:** none in the repo.

- [ ] **Step 1: Create the worktree and the scratch environment.**

```bash
cd /home/ben/repositories/Iverson && git status --porcelain -- . ':!docs/security/tma.md' ':!22' ':!fsc' ':!sego' | head
git -C /home/ben/repositories/Iverson worktree add -b csr11-identity-plane /home/ben/repositories/Iverson/.worktrees/csr11-identity-plane main
mkdir -p /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd
cat > /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh <<'X'
SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd
WT=/home/ben/repositories/Iverson/.worktrees/csr11-identity-plane
CH=$WT/Iverson.Server/deploy/helm/iverson
LIVE=$SCR/live
TRAILER='Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>'
X
git -C /home/ben/repositories/Iverson/.worktrees/csr11-identity-plane log -1 --format=%H
```

Expected: the first command prints nothing, so the main checkout has no other uncommitted work. The `git worktree add` succeeds. The last line prints `main`'s HEAD, the commit that holds this plan.

### Task 1: Client address (#8)

**Files:**
- Modify: `$CH/values.yaml`, `$CH/charts/api/values.yaml:48-55`, `$CH/charts/api/templates/deployment.yaml:1-8,145-150`
- Modify: `$CH/values-{aws,azure,gcp,laptop,local}.yaml` (the `trustedProxies` block under `api:`)
- Modify: `$CH/charts/authentik/templates/configmap.yaml`, `$CH/charts/authentik/templates/configmap-tls-proxy.yaml`
- Modify: `$WT/Iverson.Server/docker-compose.yml` (`authentik-server` environment)

**Interfaces:**
- Produces: `global.trustedProxies.{cidrs,hops}`. Task 10 renders it and Task 8 leaves it untouched. On Azure it stays `10.1.16.0/24`, the gateway subnet Task 8 creates.

- [ ] **Step 1: Move the value to `global`.**
  1. In `$CH/values.yaml`, append this to the `global:` block, directly after `  provisionTestIdentities: false`:

```yaml
  # CSR round-11 #8: the load-balancer or ingress ranges whose X-Forwarded-For is trusted, and which
  # entry from the right holds the client (2 behind Google's load balancer, which appends
  # "<client>,<lb-ip>"). Global because two readers must agree: the API's rate limiters
  # (charts/api) and the Authentik tls-proxy, which picks the address Authentik records
  # (charts/authentik). No usable default: every profile sets its own, and charts/api fails the
  # render while the list is empty.
  trustedProxies:
    cidrs: []
    hops: 1
```

  2. In `$CH/charts/api/values.yaml`, delete the `# CSR round-10 #3: …` comment and the `trustedProxies:` block it describes (lines 48–55, ending at `  hops: 1`).
  3. In each of the five profiles, cut the `trustedProxies:` block from under `api:` together with the comment lines directly above it. Paste it unchanged, at the same two-space indent, as the last entries of the profile's `global:` block. The paste goes after `  externalScheme: "https"` in aws, azure and gcp, and after `  provisionTestIdentities: true` in laptop and local. In `values-azure.yaml`, change that comment's `ingress_application_gateway.subnet_cidr` to `azurerm_subnet.appgw`, the Task 8 resource that keeps the same range.

- [ ] **Step 2: The api chart reads the global key.** In `$CH/charts/api/templates/deployment.yaml`, replace lines 1–8 (the two `fail` guards) with:

```yaml
{{- $trustedCidrs := dig "trustedProxies" "cidrs" (list) .Values.global }}
{{- if not $trustedCidrs }}
{{- fail "global.trustedProxies.cidrs must list at least one CIDR: the load-balancer or ingress ranges whose X-Forwarded-For the rate limiters and the Authentik tls-proxy trust" }}
{{- end }}
{{- range $trustedCidrs }}
{{- if not (trim .) }}
{{- fail "global.trustedProxies.cidrs contains a blank entry; every entry must be a CIDR" }}
{{- end }}
{{- end }}
```

Then, in the env list, change `{{- range $i, $cidr := .Values.trustedProxies.cidrs }}` to `{{- range $i, $cidr := $trustedCidrs }}`. Also change `value: {{ .Values.trustedProxies.hops | quote }}` to `value: {{ dig "trustedProxies" "hops" 1 .Values.global | quote }}`.

- [ ] **Step 3: Authentik trusts only its sidecar.** In `$CH/charts/authentik/templates/configmap.yaml`, add after the `AUTHENTIK_BOOTSTRAP_EMAIL` line:

```yaml
  # CSR round-11 #8: Authentik takes the left-most X-Forwarded-For entry from a trusted peer, and its
  # default trusts every private range. Only the tls-proxy sidecar (loopback) is trusted now; it
  # always sends exactly one address (configmap-tls-proxy.yaml).
  AUTHENTIK_LISTEN__TRUSTED_PROXY_CIDRS: "127.0.0.1/32,::1/128"
```

- [ ] **Step 4: The tls-proxy selects and normalises the address.** In `$CH/charts/authentik/templates/configmap-tls-proxy.yaml`:
  1. Insert this between `  default.conf: |` and the first `    server {`:

```yaml
    # CSR round-11 #8: the address Authentik records, by the API's ClientPartitionKey rule and from
    # the same values (global.trustedProxies). A peer inside the trusted ranges is a load balancer or
    # ingress, so the client is the entry `hops` positions from the right of X-Forwarded-For; any
    # other peer, or a header with no such entry, gives the peer itself. The last map strips a port
    # (AGIC writes "ip:port") and unwraps "[v6]:port", as ClientPartitionKey's IPEndPoint.TryParse
    # does, so Authentik always receives one bare address.
{{- $hops := int (dig "trustedProxies" "hops" 1 .Values.global) }}
{{- if not (has $hops (list 1 2)) }}
{{- fail "global.trustedProxies.hops must be 1 or 2: the Authentik tls-proxy selects only those positions" }}
{{- end }}
    geo $remote_addr $iverson_peer_trusted {
      default 0;
{{- range (dig "trustedProxies" "cidrs" (list) .Values.global) }}
      {{ . }} 1;
{{- end }}
    }
    map $http_x_forwarded_for $iverson_xff_entry {
      default "";
{{- if eq $hops 2 }}
      "~(?<e>[^,\s]+)\s*,\s*[^,\s]+\s*$" $e;
{{- else }}
      "~(?<e>[^,\s]+)\s*$" $e;
{{- end }}
    }
    map "$iverson_peer_trusted:$iverson_xff_entry" $iverson_client {
      default $remote_addr;
      "~^1:(?<c>.+)$" $c;
    }
    map $iverson_client $iverson_client_addr {
      "~^\[(?<a>[0-9A-Fa-f:.]+)\]:[0-9]+$" $a;
      "~^(?<a>[0-9]{1,3}(?:\.[0-9]{1,3}){3}):[0-9]+$" $a;
      default $iverson_client;
    }
```

  2. In the `8443` server's `location /`, after `proxy_set_header X-Forwarded-Proto https;`, add:

```
        # In-cluster callers: the address is the caller itself.
        proxy_set_header X-Forwarded-For $remote_addr;
```

  3. In the `9080` server, after `proxy_set_header X-Iverson-Public-Ingress 1;`, add `      proxy_set_header X-Forwarded-For $iverson_client_addr;`.

- [ ] **Step 5: Compose trusts only loopback.** In `$WT/Iverson.Server/docker-compose.yml`, in the `authentik-server` service only, add these lines after `IVERSON_ADMIN_ORCHESTRATOR_TOKEN: …` and before `    ports:` / `      - "127.0.0.1:9000:9000"`. The worker's identical env block is left alone, because it serves no flows.

```yaml
      # CSR round-11 #8: trust X-Forwarded-For only from loopback (the tls-proxy sharing this
      # network namespace), not from the bridge peer of the host-published :9000.
      AUTHENTIK_LISTEN__TRUSTED_PROXY_CIDRS: "127.0.0.1/32,::1/128"
```

- [ ] **Step 6: Render all five profiles, and check the API env is unchanged.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh; mkdir -p $SCR/t1
mkdir -p $SCR/t1/base && git -C $WT archive main Iverson.Server/deploy/helm/iverson | tar -x -C $SCR/t1/base
(cd $SCR/t1/base/Iverson.Server/deploy/helm/iverson && helm dependency build . >/dev/null)
cd $CH && helm dependency build . >/dev/null
for v in values-local values-laptop values-aws values-azure values-gcp; do o="-f $v.yaml"; [ -f $v.ci-override.yaml ] && o="$o -f $v.ci-override.yaml"
  helm template iverson . -n iverson $o > $SCR/t1/$v.yaml; e=$?
  (cd $SCR/t1/base/Iverson.Server/deploy/helm/iverson && helm template iverson . -n iverson $o > $SCR/t1/base-$v.yaml)
  same=$([ "$(command grep -A1 RateLimiting__TrustedProxies $SCR/t1/$v.yaml)" = "$(command grep -A1 RateLimiting__TrustedProxies $SCR/t1/base-$v.yaml)" ] && echo yes || echo NO)
  echo "$v exit=$e api-env-same=$same trusted=$(command grep -c 'AUTHENTIK_LISTEN__TRUSTED_PROXY_CIDRS: "127.0.0.1/32,::1/128"' $SCR/t1/$v.yaml)"; done
cd $WT/Iverson.Server && docker compose -p csr11b-config --env-file /dev/null -f docker-compose.yml config --no-interpolate --quiet && echo compose-parses
```

Expected: every line reads `exit=0 api-env-same=yes trusted=1`, then `compose-parses`. `docker compose … config` only parses the file and starts nothing.

- [ ] **Step 7: Run the rendered config through nginx 1.30.5.** Write `$SCR/nginx_map_check.sh` with exactly this content:

```bash
#!/usr/bin/env bash
# Renders the tls-proxy config three ways from the chart at $1 and runs each in a throwaway nginx
# 1.30.5 container, with a loopback echo server in place of Authentik (:9000) that returns the
# X-Forwarded-For it received. Requests are sent from inside the container, so the peer is
# 127.0.0.1: "trusted" renders list 127.0.0.0/8, "untrusted" lists 192.0.2.0/24 only.
# Usage: nginx_map_check.sh <helm/iverson dir> <work dir>
set -euo pipefail
CHART=$1; W=$2; IMG=1c5c57be5370   # docker.io/nginxinc/nginx-unprivileged:1.30.5-alpine, cached untagged
docker run --rm --entrypoint nginx $IMG -v 2>&1 | grep -q 'nginx/1.30.5'
mkdir -p "$W"; cd "$W"
[ -f tls.key ] || openssl req -x509 -newkey rsa:2048 -nodes -subj /CN=t -days 1 -keyout tls.key -out tls.crt 2>/dev/null
chmod 644 tls.key tls.crt
cat > echo.conf <<'Y'
server { listen 127.0.0.1:9000; location / { add_header X-Got-XFF "[$http_x_forwarded_for]" always; return 204; } }
Y
render() { # name hops cidr
  helm template iverson "$CHART" -f "$CHART/values-laptop.yaml" \
    --set "global.trustedProxies.hops=$2" --set "global.trustedProxies.cidrs={$3}" \
    --show-only charts/authentik/templates/configmap-tls-proxy.yaml \
  | python3 -c 'import sys,yaml; print(yaml.safe_load(sys.stdin)["data"]["default.conf"])' > "$1.conf"
}
render trusted1 1 127.0.0.0/8
render trusted2 2 127.0.0.0/8
render untrusted 1 192.0.2.0/24
probe() { # conf port [header ...] -> prints what Authentik would receive
  local conf=$1 port=$2; shift 2
  local args=(); for h in "$@"; do args+=(-H "$h"); done
  docker exec csr11b-nginx-$conf curl -sk -o /dev/null -D - "${args[@]}" "$( [ "$port" = 8443 ] && echo https || echo http )://127.0.0.1:$port/-/health/live/" \
    | tr -d '\r' | sed -n 's/^X-Got-XFF: //Ip'
}
for conf in trusted1 trusted2 untrusted; do
  docker run -d --name csr11b-nginx-$conf --memory 64m \
    -v "$W/$conf.conf:/etc/nginx/conf.d/default.conf:ro" -v "$W/echo.conf:/etc/nginx/conf.d/zz-echo.conf:ro" \
    -v "$W/tls.crt:/etc/iverson/tls/tls.crt:ro" -v "$W/tls.key:/etc/iverson/tls/tls.key:ro" $IMG >/dev/null
done
sleep 2
run() { printf '%-10s %-5s %-42s -> %s\n' "$1" "$2" "${3:-<none>}${4:+ + $4}" "$(probe "$@")"; }
run trusted1 9080 "X-Forwarded-For: spoof, 203.0.113.7"
run trusted1 9080 "X-Forwarded-For: spoof, 203.0.113.7:51234"
run trusted1 9080 "X-Forwarded-For: [2001:db8::1]:443"
run trusted1 9080 "X-Forwarded-For: a" "X-Forwarded-For: 203.0.113.9"
run trusted1 9080 "X-Forwarded-For: a, b,"
run trusted1 9080 "X-Forwarded-For;"
run trusted1 9080
run trusted2 9080 "X-Forwarded-For: spoof, 203.0.113.7, 130.211.0.5"
run trusted2 9080 "X-Forwarded-For: 203.0.113.7"
run untrusted 9080 "X-Forwarded-For: spoof, 203.0.113.7"
run trusted1 8443 "X-Forwarded-For: spoof, 203.0.113.7"
for conf in trusted1 trusted2 untrusted; do docker rm -f csr11b-nginx-$conf >/dev/null; done
echo removed
```

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh
bash $SCR/nginx_map_check.sh $CH $SCR/t1/nginx
```

Expected, in order:
- `[203.0.113.7]`, `[203.0.113.7]`, `[2001:db8::1]`, `[203.0.113.9]`: the trusted peer, 1 hop. These are the right-most entry, port-stripped and with IPv6 unwrapped; two headers arrive joined.
- `[127.0.0.1]` three times: a trailing comma, an empty header, and no header all fall back to the peer.
- `[203.0.113.7]`, then `[127.0.0.1]`: 2 hops, then a chain too short for 2 hops.
- `[127.0.0.1]`: an untrusted peer.
- `[127.0.0.1]`: `8443` always sends the caller.
- `removed`.

If image `1c5c57be5370` is gone, `docker pull docker.io/nginxinc/nginx-unprivileged:1.30.5-alpine` and set `IMG` to that tag.

- [ ] **Step 8: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh; cd $WT
git add Iverson.Server/deploy/helm/iverson/values.yaml Iverson.Server/deploy/helm/iverson/values-aws.yaml Iverson.Server/deploy/helm/iverson/values-azure.yaml Iverson.Server/deploy/helm/iverson/values-gcp.yaml Iverson.Server/deploy/helm/iverson/values-laptop.yaml Iverson.Server/deploy/helm/iverson/values-local.yaml Iverson.Server/deploy/helm/iverson/charts/api/values.yaml Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/templates/configmap.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/templates/configmap-tls-proxy.yaml Iverson.Server/docker-compose.yml
git commit -m "record the client address authentik sees from the trusted proxy chain

$TRAILER"
```

### Task 2: Login hardening in Authentik (#5)

**Files:**
- Modify: `$CH/charts/authentik/blueprints/recovery-flow.yaml`
- Create: `$CH/charts/authentik/blueprints/reputation.yaml`
- Modify: `$WT/Iverson.Server/Iverson.LoadTest/Auth/AuthentikFlowExecutorClient.cs` (`SetPasswordFromRecoveryLinkAsync`, `ReadComponentAsync`)
- Test: `$WT/Iverson.Server/Iverson.LoadTest.Tests/Auth/AuthentikFlowExecutorClientTests.cs`
- Test: `$WT/Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikRecoveryFlowIntegrationTests.cs` (`RecoveryFlow_UserWithNoEnrolledFactor_CompletesAndSignsTheUserIn` replaced; `RecoveryFlowSession` gains two members)

**Interfaces:**
- Produces: `iverson-recovery` binds prompt 1, MFA 2, user-write 3, user-login 4 and redirect 5, so **order 0 is free for Task 3**. The flow enrols TOTP for an account with no device.

- [ ] **Step 1: Write the failing LoadTest tests.** Append these two facts to `AuthentikFlowExecutorClientTests` (they use the class's existing `FakeHandler`, `Json`, `Redirect`, `Client`, `WithTempHome` and `CachePath`):

```csharp
    // CSR round-11 #5: an account with no second factor enrols TOTP inside the recovery flow, before
    // its password is written. After the password, Authentik answers two 302s and then the TOTP setup
    // challenge; after the code, three 302s and then completion.
    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task SetPasswordFromRecoveryLinkAsync_EnrolsTotp_CachingTheSecretWhereMintAsyncReadsIt()
    {
        await WithTempHome(async home =>
        {
            var handler = new FakeHandler(
                Json("""{"component":"ak-stage-prompt"}"""),
                Redirect(), Redirect(),
                Json("""{"component":"ak-stage-authenticator-totp","config_url":"otpauth://totp/iverson?secret=JBSWY3DPEHPK3PXP"}"""),
                Redirect(), Redirect(), Redirect(),
                Json("""{"component":"xak-flow-redirect","to":"/"}"""));

            await Client(handler).SetPasswordFromRecoveryLinkAsync(Link);

            handler.Requests.Should().HaveCount(8, "nothing is sent after the xak-flow-redirect");
            handler.Requests[4].Method.Should().Be(HttpMethod.Post);
            using (var body = JsonDocument.Parse(handler.Requests[4].Body))
                body.RootElement.GetProperty("code").GetString().Should().MatchRegex("^[0-9]{6}$");
            handler.Requests.Where((_, i) => i != 1 && i != 4).Should().AllSatisfy(r =>
            {
                r.Method.Should().Be(HttpMethod.Get);
                r.Url.Should().Be(ExecutorUrl);
            });
            File.ReadAllText(CachePath(home)).Should().Be("JBSWY3DPEHPK3PXP\n",
                "the tenant admin's first login validates with this secret instead of enrolling again");
        });
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task SetPasswordFromRecoveryLinkAsync_Throws_WhenTheEnrolmentCodeIsRefused()
    {
        await WithTempHome(async _ =>
        {
            const string totpStage = """{"component":"ak-stage-authenticator-totp","config_url":"otpauth://totp/iverson?secret=JBSWY3DPEHPK3PXP"}""";
            var handler = new FakeHandler(
                Json("""{"component":"ak-stage-prompt"}"""),
                Redirect(),
                Json(totpStage),
                Json("""{"component":"ak-stage-authenticator-totp","response_errors":{"code":[{"string":"Code does not match","code":"invalid"}]}}"""),
                Json(totpStage));

            var act = () => Client(handler).SetPasswordFromRecoveryLinkAsync(Link);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ak-stage-authenticator-totp*");
            handler.Requests.Should().HaveCount(5);
        });
    }
```

Run:

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-identity-plane/Iverson.Server
systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj -p:UseSharedCompilation=false --filter "FullyQualifiedName~AuthentikFlowExecutorClientTests" 2>&1 | tail -15; dotnet build-server shutdown >/dev/null 2>&1
```

Expected: both new facts fail, and every other fact passes.
- The first throws "ended at 'ak-stage-authenticator-totp'", because the client doesn't handle the stage yet.
- The second throws the expected message but has sent 3 requests, not 5.

- [ ] **Step 2: Handle the TOTP setup stage in `SetPasswordFromRecoveryLinkAsync`.** In `AuthentikFlowExecutorClient.cs`:
  1. Replace the block from `// Each completed stage (prompt, MFA check, user write) answers a body-less 302 back to the` through `throw new InvalidOperationException($"The recovery flow ended at '{end}', not 'xak-flow-redirect'; the password was not set.");` with:

```csharp
        // Each completed stage answers a body-less 302 back to the same executor URL; this client
        // doesn't follow redirects, so follow them here.
        resp = await FollowRecoveryRedirectsAsync(flowUrl, resp);
        var (end, configUrl) = await ReadChallengeAsync(resp);

        // CSR round-11 #5: an account with no second factor enrols TOTP here, before the password is
        // written. The secret is cached where MintAsync reads it, so the first login validates with it.
        if (end == "ak-stage-authenticator-totp")
        {
            var secret = ParseTotpSecretFromConfigUrl(
                configUrl ?? throw new InvalidOperationException("The recovery flow's TOTP setup stage sent no config_url."));
            SaveCachedTotpSecret(secret);
            await SubmitTotpCodeAsync(flowUrl, secret, new TotpAttemptState());
            resp = await FollowRecoveryRedirectsAsync(flowUrl, await SendAsync(HttpMethod.Get, flowUrl));
            (end, _) = await ReadChallengeAsync(resp);
        }

        if (end != "xak-flow-redirect")
            throw new InvalidOperationException($"The recovery flow ended at '{end}', not 'xak-flow-redirect'; the password was not set.");
    }

    private async Task<HttpResponseMessage> FollowRecoveryRedirectsAsync(string flowUrl, HttpResponseMessage resp)
    {
        for (var i = 0; i < MaxFlowStages && resp.StatusCode == System.Net.HttpStatusCode.Redirect; i++)
            resp = await SendAsync(HttpMethod.Get, flowUrl);
        if (resp.StatusCode == System.Net.HttpStatusCode.Redirect)
            throw new InvalidOperationException($"The recovery flow was still redirecting after {MaxFlowStages} requests.");
        return resp;
```

     This keeps the method's closing `}` that follows the replaced block, which now closes `FollowRecoveryRedirectsAsync`.

  2. Replace `ReadComponentAsync` with:

```csharp
    private static async Task<string?> ReadComponentAsync(HttpResponseMessage resp) =>
        (await ReadChallengeAsync(resp)).Component;

    private static async Task<(string? Component, string? ConfigUrl)> ReadChallengeAsync(HttpResponseMessage resp)
    {
        if (resp.StatusCode != System.Net.HttpStatusCode.OK)
            throw new InvalidOperationException($"The recovery flow answered HTTP {(int)resp.StatusCode}.");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        return (root.TryGetProperty("component", out var c) ? c.GetString() : null,
                root.TryGetProperty("config_url", out var u) ? u.GetString() : null);
    }
```

  3. Update the method's `<summary>`. After "…through Authentik's flow executor." add: "When the account has no second factor, the flow enrols TOTP before writing the password; the secret is cached for `MintAsync`."

Re-run Step 1's command. Expected: `Passed!` with 0 failed. That includes the five existing `SetPasswordFromRecoveryLinkAsync_*` facts, which never reach the TOTP stage.

- [ ] **Step 3: The recovery flow enrols a device.** In `$CH/charts/authentik/blueprints/recovery-flow.yaml`:
  1. In the header comment, replace "The user sets a new password (validated by the password policy), presents any MFA factor they already have enrolled, and is logged in." with "The user sets a new password (validated by the password policy), presents the MFA factor they have enrolled or enrols TOTP if they have none, and is logged in."
  2. Replace the comment block above `iverson-recovery-mfa-validation`, from `# Second factor, consulted between …` through `# never enrols a factor -- it only validates one that already exists.`, with:

```yaml
  # Second factor, consulted between "choose a new password" and "write it / log in".
  #
  # CSR round-11 #5: not_configured_action: configure. An account with no device (an invitee, a new
  # tenant admin, the break-glass admin) enrols TOTP here, before its password is written, so the
  # second factor belongs to whoever completes the recovery link. Under the former `skip`, the first
  # password login enrolled it, for whoever signed in first. An account that HAS a TOTP/WebAuthn
  # device must present it before the reset is written, so a leaked recovery link alone is not
  # sufficient.
  #
  # device_classes mirrors mfa-enforcement.yaml's default-authentication-mfa-validation stage
  # exactly (totp + webauthn). That alignment is intentional: the recovery path must accept
  # precisely the factor classes the authentication path enrols, no more (a class listed here but
  # never enrolled is dead weight) and no fewer (a class enrolled there but missing here would make
  # this stage find "no allowed devices" for exactly the users who did enrol a factor).
  #
  # configuration_stages names only the stock TOTP setup stage (the one mfa-enforcement.yaml also
  # offers): with exactly one, Authentik goes straight to it instead of asking which to set up, and
  # LoadTest's recovery client (AuthentikFlowExecutorClient) handles exactly that stage.
```

  3. In that stage's `attrs`, change `not_configured_action: skip` to `not_configured_action: configure`. After the `device_classes` list, add:

```yaml
      configuration_stages:
        - !Find [authentik_stages_authenticator_totp.authenticatortotpstage, [name, default-authenticator-totp-setup]]
```

  4. Renumber the five stage bindings' `order` values (they are under `attrs`): prompt 0→1, mfa-validation 1→2, user-write 2→3, user-login 3→4, redirect-console 4→5.
  5. Replace the comment line `# Stage chain: prompt (0) -> MFA validation (1) -> user_write (2) -> user_login (3) -> redirect to the console (4).` with:

```yaml
  # Stage chain: prompt (1) -> MFA validation or TOTP enrolment (2) -> user_write (3) -> user_login (4)
  # -> redirect to the console (5). Order 0 is left for the Helm-only refusal of superusers on the
  # public listener (templates/blueprints-configmap.yaml, superusers.yaml).
```

  6. Insert this entry directly after the `entries:` line, as the file's first entry (before the `# CSR round-3 finding #2, defence-in-depth half` comment):

```yaml
  # CSR round-11 #5: configuration_stages below finds the stock TOTP setup stage, which Authentik's own
  # blueprints create in no fixed order relative to this file. Applying that stock blueprint first,
  # inside this file's validation, makes the !Find resolve on every apply, a cold start included.
  - model: authentik_blueprints.metaapplyblueprint
    attrs:
      identifiers:
        path: default/flow-default-authenticator-totp-setup.yaml
      required: true
```

- [ ] **Step 4: Create `$CH/charts/authentik/blueprints/reputation.yaml`** with exactly:

```yaml
version: 1
metadata:
  name: Iverson - Login failures by client address
entries:
  # CSR round-11 #5. Authentik's own reputation policy sums every (address, username) score for the
  # address, and a successful login raises its row, so one account's successes offset failures
  # against another from the same address. This sums only the failing rows. Each failed
  # identification, password or MFA attempt lowers its row (floored at -5); a row expires
  # reputation.expiry (default 24 h) after its first failure.
  - id: login-failures-by-address
    model: authentik_policies_expression.expressionpolicy
    identifiers:
      name: iverson-login-failures-by-address
    attrs:
      expression: |
        from django.db.models import Sum
        from authentik.policies.reputation.models import Reputation
        failures = Reputation.objects.filter(ip=str(ak_client_ip), score__lt=0).aggregate(total=Sum("score"))["total"] or 0
        return failures > -5
  # Flow-level, so a refused address is denied before identification. PolicyBinding.target is the
  # flow's policybindingmodel row (pbm_uuid), not its flow_uuid: a flow !Find here creates the binding
  # once and then fails every later apply.
  - model: authentik_policies.policybinding
    identifiers:
      policy: !KeyOf login-failures-by-address
      target: !Find [authentik_policies.policybindingmodel, [flow__slug, default-authentication-flow]]
      order: 0
```

- [ ] **Step 5: Replace the Api.Tests no-device recovery fact.** In `AuthentikRecoveryFlowIntegrationTests.cs`:
  1. Add `using System.Web;` to the usings.
  2. Replace `RecoveryFlow_UserWithNoEnrolledFactor_CompletesAndSignsTheUserIn` and its `<summary>` with:

```csharp
    /// <summary>
    /// CSR round-11 #5: an account with no second factor enrols TOTP inside its recovery flow, after
    /// choosing a password and before the password is written, so the factor belongs to whoever
    /// completes the link rather than to whoever signs in first. Asserted behaviourally: the flow
    /// stops at TOTP setup with no session, and only a valid code reaches a real authenticated
    /// session.
    /// </summary>
    [Fact]
    public async Task RecoveryFlow_UserWithNoEnrolledFactor_EnrolsTotpBeforeSigningIn()
    {
        var username = Unique("csr11-nomfa");
        var created = await _orchestrator.CreateUserAsync(
            username, $"{username}@iverson-test.invalid", "csr11-tenant", groups: []);

        using var flow = new RecoveryFlowSession(_authentik.BaseUrl, created.RecoveryLink!);

        (await flow.StartAsync()).Should().Be("ak-stage-prompt");
        (await flow.SubmitPasswordAsync("Correct-Horse-Battery-9")).Should().Be("ak-stage-authenticator-totp",
            "an account with no enrolled factor must enrol one before the reset completes");
        (await flow.AuthenticatedUsernameAsync()).Should().BeNull("enrolment has not finished");

        (await flow.SubmitTotpSetupCodeAsync(Totp(flow.TotpSetupSecret()))).Should().Be("xak-flow-redirect");
        (await flow.AuthenticatedUsernameAsync()).Should().Be(username);
    }

    /// <summary>RFC 6238 TOTP (HMAC-SHA1, 6 digits, 30 s), the code Authentik's TOTP stages accept.</summary>
    private static string Totp(string base32Secret)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var key = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var ch in base32Secret.TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | alphabet.IndexOf(ch);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                key.Add((byte)(buffer >> bits));
            }
        }
        var counter = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        var hash = HMACSHA1.HashData(key.ToArray(), counter);
        var offset = hash[^1] & 0x0F;
        var code = ((hash[offset] & 0x7F) << 24 | hash[offset + 1] << 16 | hash[offset + 2] << 8 | hash[offset + 3]) % 1_000_000;
        return code.ToString("D6");
    }
```

  3. In `RecoveryFlowSession`, after `SubmitTotpCodeAsync`, add:

```csharp
        public Task<string?> SubmitTotpSetupCodeAsync(string code) => ReadComponentAsync(
            _client.PostAsync(_executorUrl, Json(
                $$"""{"component":"ak-stage-authenticator-totp","code":"{{code}}"}""")));

        /// <summary>The base32 secret in the last challenge's config_url (the TOTP setup stage).</summary>
        public string TotpSetupSecret()
        {
            using var doc = JsonDocument.Parse(LastChallengeBody);
            var configUrl = new Uri(doc.RootElement.GetProperty("config_url").GetString()!);
            return HttpUtility.ParseQueryString(configUrl.Query)["secret"]!;
        }
```

- [ ] **Step 6: Run the Authentik class** (container-backed: one Authentik stack, about 2 GB; check `free -g` first):

```bash
free -g | sed -n 2p
cd /home/ben/repositories/Iverson/.worktrees/csr11-identity-plane/Iverson.Server
systemd-run --user --scope -q -p MemoryMax=7G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj -p:UseSharedCompilation=false --filter "FullyQualifiedName~AuthentikRecoveryFlowIntegrationTests|FullyQualifiedName~AuthentikOrchestratorRoleBlueprintTests|FullyQualifiedName~AuthentikOrchestratorFederationBlueprintTests" 2>&1 | tail -15; dotnet build-server shutdown >/dev/null 2>&1
```

Expected: `Passed!` with 0 failed. That includes `RecoveryFlow_UserWithEnrolledTotp_IsChallengedAndCannotCompleteWithoutIt`: a device holder still meets `ak-stage-authenticator-validate`. It also includes the fixture's readiness probe, which mints a recovery link with the new flow and the new static `reputation.yaml` mounted.

- [ ] **Step 7: Mutation proof.**
  1. Stage the task's files first, by running Step 8's `git add` line.
  2. Change only the `not_configured_action` line back to `skip`.
  3. Re-run Step 6's command, and record that `RecoveryFlow_UserWithNoEnrolledFactor_EnrolsTotpBeforeSigningIn` fails.
  4. Restore the staged version: `git -C /home/ben/repositories/Iverson/.worktrees/csr11-identity-plane checkout -- Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/recovery-flow.yaml`.
  5. Confirm that `git -C /home/ben/repositories/Iverson/.worktrees/csr11-identity-plane diff --stat` prints nothing.

- [ ] **Step 8: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh; cd $WT
git add Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/recovery-flow.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/reputation.yaml Iverson.Server/Iverson.LoadTest/Auth/AuthentikFlowExecutorClient.cs Iverson.Server/Iverson.LoadTest.Tests/Auth/AuthentikFlowExecutorClientTests.cs Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikRecoveryFlowIntegrationTests.cs
git commit -m "enrol totp during recovery and refuse an address after five login failures

$TRAILER"
```

### Task 3: Superusers and the bootstrap credential (#9; Helm only)

**Files:**
- Modify: `$CH/charts/authentik/templates/blueprints-configmap.yaml` (append the `superusers.yaml` key)
- Modify: `$CH/charts/authentik/templates/deployment-server.yaml:114-116`

**Interfaces:**
- Consumes: Task 2's free order 0 on `iverson-recovery`.
- Produces: `custom/superusers.yaml` in the worker, which Task 9's runbook applies and Task 10 checks.

- [ ] **Step 1: Append the templated key.** At the end of `blueprints-configmap.yaml`, after the orchestrator file's last line (`          order: 1`), append the following. It uses the same two-space key indent as `orchestrator-federation.yaml`, which `AuthentikOrchestratorFederationBlueprintTests.FederationBlockLines` relies on to end that block.

```yaml
  {{- /* CSR round-11 #9: Helm only. Compose and the Api.Tests fixture mount blueprints/, keep akadmin
     and log in with it, and the fixture's OrchestratorGroup_IsNotASuperuserGroup forbids a superuser
     group in the service-clients blueprints. The deny bindings !Find flows other files define
     (default-authentication-flow, iverson-recovery), so apply this file after those. */}}
  superusers.yaml: |
    version: 1
    metadata:
      name: Iverson - Superusers and break-glass
    entries:
      # Declared here because the stock events blueprint can create the group without the flag.
      - id: admins
        model: authentik_core.group
        identifiers:
          name: authentik Admins
        attrs:
          is_superuser: true
      # The named break-glass admin. No password: it gets one, and enrols TOTP, only through a recovery
      # link an operator generates in the worker (docs/runbooks/csr11-identity-plane.md).
      - model: authentik_core.user
        identifiers:
          username: iverson-breakglass
        attrs:
          username: iverson-breakglass
          name: Iverson break-glass admin
          is_active: true
          groups:
            - !KeyOf admins
      # Bootstrap's own admin, never used after install. Inactive, it cannot log in; whichever of
      # bootstrap and this file runs first, it ends inactive.
      - model: authentik_core.user
        identifiers:
          username: akadmin
        attrs:
          name: authentik Default Admin
          is_active: false
      # True for a superuser on the public listener, whose tls-proxy marks every request. A deny stage
      # runs only when its policy passes; evaluated at execution (evaluate_on_plan false,
      # re_evaluate_policies true), it sees the identified or recovering user.
      - id: superuser-on-public-listener
        model: authentik_policies_expression.expressionpolicy
        identifiers:
          name: iverson-superuser-on-public-listener
        attrs:
          expression: |
            return request.user.is_superuser and "HTTP_X_IVERSON_PUBLIC_INGRESS" in request.http_request.META
      - id: refuse-superuser
        model: authentik_stages_deny.denystage
        identifiers:
          name: iverson-refuse-superuser-on-public-listener
        attrs:
          deny_message: Administrators sign in through the in-cluster route.
      # Login: after identification (10), before password (20), so a superuser's password is never
      # checked on the public listener.
      - id: login-refuse-binding
        model: authentik_flows.flowstagebinding
        identifiers:
          target: !Find [authentik_flows.flow, [slug, default-authentication-flow]]
          stage: !KeyOf refuse-superuser
        attrs:
          order: 15
          evaluate_on_plan: false
          re_evaluate_policies: true
      - model: authentik_policies.policybinding
        identifiers:
          policy: !KeyOf superuser-on-public-listener
          target: !KeyOf login-refuse-binding
          order: 0
      # Recovery: first, before any password or device is written.
      - id: recovery-refuse-binding
        model: authentik_flows.flowstagebinding
        identifiers:
          target: !Find [authentik_flows.flow, [slug, iverson-recovery]]
          stage: !KeyOf refuse-superuser
        attrs:
          order: 0
          evaluate_on_plan: false
          re_evaluate_policies: true
      - model: authentik_policies.policybinding
        identifiers:
          policy: !KeyOf superuser-on-public-listener
          target: !KeyOf recovery-refuse-binding
          order: 0
```

- [ ] **Step 2: Drop the bootstrap password from the server container.** In `deployment-server.yaml`, delete the three lines of the `AUTHENTIK_BOOTSTRAP_PASSWORD` env entry (`- name: AUTHENTIK_BOOTSTRAP_PASSWORD` and its `valueFrom`/`secretKeyRef`). The worker keeps its copy (`deployment-worker.yaml:102-104`), and bootstrap runs there on a fresh install.

- [ ] **Step 3: Render, and run the text-level blueprint tests.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh; mkdir -p $SCR/t3
cd $CH && helm dependency build . >/dev/null && helm template iverson . -n iverson -f values-laptop.yaml > $SCR/t3/laptop.yaml && echo rendered
python3 - $SCR/t3/laptop.yaml <<'PY'
import sys, yaml
class L(yaml.SafeLoader): pass
for t in ("!Find", "!KeyOf", "!Env"):
    L.add_constructor(t, lambda l, n: ("TAG", l.construct_sequence(n) if isinstance(n, yaml.SequenceNode) else l.construct_scalar(n)))
docs = [d for d in yaml.safe_load_all(open(sys.argv[1])) if d]
cm = next(d for d in docs if d["kind"] == "ConfigMap" and d["metadata"]["name"] == "iverson-authentik-blueprints")
su = yaml.load(cm["data"]["superusers.yaml"], Loader=L)
print("superusers entries:", len(su["entries"]))
srv = next(d for d in docs if d["kind"] == "Deployment" and d["metadata"]["name"] == "iverson-authentik-server")
wrk = next(d for d in docs if d["kind"] == "Deployment" and d["metadata"]["name"] == "iverson-authentik-worker")
env = lambda dep: {e["name"] for c in dep["spec"]["template"]["spec"]["containers"] for e in c.get("env", [])}
print("server bootstrap:", "AUTHENTIK_BOOTSTRAP_PASSWORD" in env(srv), "| worker bootstrap:", "AUTHENTIK_BOOTSTRAP_PASSWORD" in env(wrk))
PY
cd $WT/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj -p:UseSharedCompilation=false --filter "FullyQualifiedName~AuthentikOrchestratorFederationBlueprintTests|FullyQualifiedName~AuthentikOrchestratorRoleBlueprintTests" 2>&1 | tail -3; dotnet build-server shutdown >/dev/null 2>&1
```

Expected:
- `rendered`;
- `superusers entries: 9`;
- `server bootstrap: False | worker bootstrap: True`;
- `Passed!` with 0 failed, which shows the federation block still parses as it did.

The behaviour (deny on the public listener, `akadmin` inactive) is checked live in Task 10. Neither compose nor the Api.Tests fixture mounts this key.

- [ ] **Step 4: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh; cd $WT
git add Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/templates/deployment-server.yaml
git commit -m "refuse superusers on the public listener, add a break-glass admin and retire akadmin

$TRAILER"
```

### Task 4: Orchestrator pin bounds (#7)

**Files:**
- Modify: `$CH/charts/authentik/templates/blueprints-configmap.yaml` (the `orchestrator-workload` expression)
- Test: `$WT/Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikOrchestratorFederationBlueprintTests.cs` (`PinExpression_PinsSubjectAudienceAndGroupGate`)
- Test: `$WT/Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikRecoveryFlowIntegrationTests.cs` (`Assertion`, `OrchestratorFederation_MintsOnlyForThePinnedWorkload_OnceTheUserExists`)

- [ ] **Step 1: Write the failing tests.**
  1. In `AuthentikOrchestratorFederationBlueprintTests`, rename `PinExpression_PinsSubjectAudienceAndGroupGate` to `PinExpression_PinsSubjectAudienceLifetimePodAndGroupGate`. After `pin.Should().Contain("user is not None");`, add:

```csharp
        // CSR round-11 #7: an unbound token (no pod claim) or a long-lived one is refused.
        pin.Should().Contain("exp - iat <= 600");
        pin.Should().Contain("isinstance(iat, int) and isinstance(exp, int)");
        pin.Should().Contain("\"pod\" in k8s");
```

  2. In `AuthentikRecoveryFlowIntegrationTests`, replace `Assertion` with:

```csharp
    /// <summary>
    /// An RS256 JWT shaped like a kubelet-projected service-account token: pod-bound (a
    /// <c>kubernetes.io.pod</c> claim) with a 600 s life, unless told otherwise.
    /// </summary>
    private static string Assertion(RSA rsa, string kid, string subject, string audience, bool podBound = true, int lifetimeSeconds = 600)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT", kid }));
        var claims = new Dictionary<string, object>
        {
            ["iss"] = "https://kubernetes.default.svc.cluster.local", ["sub"] = subject, ["aud"] = new[] { audience },
            ["iat"] = now, ["exp"] = now + lifetimeSeconds,
        };
        var kubernetes = new Dictionary<string, object>
        {
            ["namespace"] = "iverson-test",
            ["serviceaccount"] = new { name = "iverson-api", uid = "00000000-0000-0000-0000-000000000001" },
        };
        if (podBound)
            kubernetes["pod"] = new { name = "iverson-api-0", uid = "00000000-0000-0000-0000-000000000002" };
        claims["kubernetes.io"] = kubernetes;
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signature = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{Base64Url(signature)}";
    }
```

  3. In `OrchestratorFederation_MintsOnlyForThePinnedWorkload_OnceTheUserExists`:
     - Change the comment `// Wrong subject, wrong audience, or arrival through the public listener: refused, nobody created.` to `// Wrong subject, wrong audience, arrival through the public listener, no pod binding, or a lifetime over 600 s: refused, nobody created.`
     - Before the final `(await UserCountAsync(admin)).Should().Be(usersAfter);`, add:

```csharp
        (await ExchangeAsync(clientId, Assertion(rsa, kid, FederatedSubject, FederatedAudience, podBound: false)))
            .Should().Be((HttpStatusCode.BadRequest, "invalid_grant"));
        (await ExchangeAsync(clientId, Assertion(rsa, kid, FederatedSubject, FederatedAudience, lifetimeSeconds: 3600)))
            .Should().Be((HttpStatusCode.BadRequest, "invalid_grant"));
```

Run Task 2 Step 6's command. Expected:
- `PinExpression_PinsSubjectAudienceLifetimePodAndGroupGate` fails on `exp - iat <= 600`;
- the federation fact fails on the pod-less assertion, which still mints;
- every other fact passes.

- [ ] **Step 2: Bound the pin.** In the `orchestrator-workload` entry's expression in `blueprints-configmap.yaml`, replace:

```
            jwt = request.context.get("oauth_jwt") or {}
            aud = jwt.get("aud")
            auds = aud if isinstance(aud, list) else [aud]
            user = ak_user_by(username="iverson-admin-orchestrator-workload")
            return (
                jwt.get("sub") == "system:serviceaccount:{{ .Release.Namespace }}:{{ .Release.Name }}-api"
                and "iverson-authentik-orchestrator" in auds
                and user is not None
```

with:

```
            jwt = request.context.get("oauth_jwt") or {}
            aud = jwt.get("aud")
            auds = aud if isinstance(aud, list) else [aud]
            iat = jwt.get("iat")
            exp = jwt.get("exp")
            k8s = jwt.get("kubernetes.io") or {}
            user = ak_user_by(username="iverson-admin-orchestrator-workload")
            return (
                jwt.get("sub") == "system:serviceaccount:{{ .Release.Namespace }}:{{ .Release.Name }}-api"
                and "iverson-authentik-orchestrator" in auds
                and isinstance(iat, int) and isinstance(exp, int)
                and exp - iat <= 600
                and "pod" in k8s
                and user is not None
```

Then extend the comment above the entry (`# Authentik checks only a federated JWT's signature and expiry, …`) with: "CSR round-11 #7: it also bounds the token's lifetime to 600 s, which with Authentik's expiry check bounds its usable age, and requires the kubelet's pod binding, which `kubectl create token` without a bound pod lacks."

Re-run Task 2 Step 6's command. Expected: `Passed!` with 0 failed.

- [ ] **Step 3: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh; cd $WT
git add Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap.yaml Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikOrchestratorFederationBlueprintTests.cs Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikRecoveryFlowIntegrationTests.cs
git commit -m "bound the orchestrator assertion to a pod-bound token of at most 600 seconds

$TRAILER"
```

### Task 5: Federated admin-automation (#9; Helm only)

**Files:**
- Create: `$CH/charts/authentik/templates/serviceaccount-admin-automation.yaml`
- Modify: `$CH/charts/authentik/templates/secret-service-clients.yaml` (lookup `:52-61`, Secret `:124-133`, loop `:263-301`, marker `:302-316`, end of file)
- Modify: `$CH/charts/authentik/templates/blueprints-configmap.yaml` (one comment, `:102-104`)

**Interfaces:**
- Produces:
  - the ServiceAccount `<release>-admin-automation`;
  - the blueprint `custom/service-clients/admin-automation.yaml`;
  - the provider `iverson-admin-automation`, which takes a jwt-bearer assertion of audience `iverson-authentik-admin-automation`;
  - the Secret `<release>-authentik-admin-automation-client`, which now holds only `client-id`.
- Consumed by: Task 7 (LoadTest's assertion mode), Task 9 (runbook) and Task 10.

- [ ] **Step 1: Create the ServiceAccount template** with exactly:

```yaml
# CSR round-11 #9: the identity admin-automation callers mint short-lived tokens for
# (`kubectl create token <release>-admin-automation --audience iverson-authentik-admin-automation`),
# which admin-automation.yaml federates. No pod runs as it.
apiVersion: v1
kind: ServiceAccount
metadata:
  name: {{ .Release.Name }}-admin-automation
automountServiceAccountToken: false
```

- [ ] **Step 2: Keep only the client id.** In `secret-service-clients.yaml`:
  1. Replace the admin-automation lookup block (from `{{- $adminAutoExisting := lookup …` through its `{{- end }}`) with:

```yaml
{{- /* CSR round-11 #9: admin-automation keeps its stable client-id and has no client secret here;
   callers authenticate with a service-account token (admin-automation.yaml below). */}}
{{- $adminAutoExisting := lookup "v1" "Secret" $ns (printf "%s-authentik-admin-automation-client" $rel) }}
{{- $adminAutoClientId := "" }}
{{- if and $adminAutoExisting (hasKey (default dict $adminAutoExisting.data) "client-id") }}
{{- $adminAutoClientId = index $adminAutoExisting.data "client-id" | b64dec }}
{{- else }}
{{- $adminAutoClientId = randAlphaNum 40 }}
{{- end }}
```

  2. In the `<release>-authentik-admin-automation-client` Secret, delete the line `  client-secret: {{ $adminAutoClientSecret | b64enc }}`.

- [ ] **Step 3: Take admin-automation out of `service-clients.yaml`.** In the same file:
  1. Change the loop header lines:
     - `{{- range $name := list "loadtest" "webtest" "admin-automation" }}` → `{{- range $name := list "loadtest" "webtest" }}`
     - `{{- if or (eq $name "admin-automation") $.Values.global.provisionTestIdentities }}` → `{{- if $.Values.global.provisionTestIdentities }}`
     - `{{- $clientId := index (dict "loadtest" $loadtestClientId "webtest" $webtestClientId "admin-automation" $adminAutoClientId) $name }}` → `{{- $clientId := index (dict "loadtest" $loadtestClientId "webtest" $webtestClientId) $name }}`
     - `{{- $clientSecret := index (dict "loadtest" $loadtestClientSecret "webtest" $webtestClientSecret "admin-automation" $adminAutoClientSecret) $name }}` → `{{- $clientSecret := index (dict "loadtest" $loadtestClientSecret "webtest" $webtestClientSecret) $name }}`
  2. Delete the `{{- if eq $name "admin-automation" }}` branch: its `access_token_validity` line and its three `!Find` property mappings. Turn `{{- else if eq $name "loadtest" }}` into `{{- if eq $name "loadtest" }}`.
  3. Delete three things, which run from `      # CSR round-10 #4: the admin-automation client` through the binding's `          order: 0` (15 lines):
     - the `# CSR round-10 #4: the admin-automation client mints Operator tokens, …` comment;
     - the `admin-automation-in-cluster-only` expression policy entry;
     - its policy binding.

     The `tenant_id_admin` scope mapping stays, and admin-automation.yaml repeats it identically.

- [ ] **Step 4: Add the self-contained file.** Append this as the last key of the `<release>-authentik-blueprints-service-clients` Secret's `stringData`, at the end of the file, at the same two-space key indent as `service-clients.yaml: |`:

```yaml
  # CSR round-11 #9: everything admin-automation needs, in one file with no reference to another chart
  # blueprint, because a missing cross-file referent fails a whole file and apply order across files is
  # not guaranteed. Callers exchange a short-lived token of the <release>-admin-automation
  # ServiceAccount (client_credentials with a jwt-bearer client assertion) for a 1-hour Operator token.
  # The client secret is a fresh random value on every render: the assertion grant never checks it, a
  # blank one would leave the client unauthenticated, and the pin policy refuses a secret grant (it
  # carries no assertion). Moved entries keep their identifiers, so they update the existing objects.
  admin-automation.yaml: |
    version: 1
    metadata:
      name: Iverson - admin-automation workload identity
    entries:
      # Every accepted assertion maps to the one pre-created user below. A property mapping applies to
      # its whole source, so admin-automation has its own source rather than sharing the orchestrator's.
      - id: workload-username
        model: authentik_sources_oauth.oauthsourcepropertymapping
        identifiers:
          name: "Iverson: k8s admin-automation username"
        attrs:
          expression: 'return {"username": "iverson-admin-automation-workload"}'
      # Trust anchor only: the cluster's service-account keys, through the worker's JWKS relay.
      - id: k8s-admin-automation-source
        model: authentik_sources_oauth.oauthsource
        identifiers:
          slug: iverson-k8s-admin-automation
        attrs:
          name: iverson-k8s-admin-automation
          provider_type: openidconnect
          enabled: true
          consumer_key: iverson-k8s-admin-automation
          consumer_secret: unused
          authorization_url: ""
          access_token_url: ""
          profile_url: ""
          oidc_jwks_url: http://127.0.0.1:8001/openid/v1/jwks
          user_property_mappings:
            - !KeyOf workload-username
      # Pre-created: a user the token exchange created would get an expiry. No password.
      - model: authentik_core.user
        identifiers:
          username: iverson-admin-automation-workload
        attrs:
          username: iverson-admin-automation-workload
          name: "Iverson admin automation (workload identity)"
          email: iverson-admin-automation@example.invalid
          is_active: true
      # The same three scope mappings service-clients.yaml declares, identical, so this file never
      # waits on that one's apply.
      - id: scope-admin
        model: authentik_providers_oauth2.scopemapping
        identifiers:
          scope_name: admin
        attrs:
          name: "Iverson: admin operator scope"
          description: Grants operator-tier access to Iverson's admin endpoints
          expression: "return {}"
      - id: scope-schema-admin
        model: authentik_providers_oauth2.scopemapping
        identifiers:
          scope_name: schema_admin
        attrs:
          name: "Iverson: schema admin scope"
          description: Grants rights to register/redefine entity schemas
          expression: "return {}"
      - id: scope-tenant-id-admin
        model: authentik_providers_oauth2.scopemapping
        identifiers:
          scope_name: tenant_id_admin
        attrs:
          name: "Iverson: admin automation tenant id"
          description: Fixed tenant identifier issued to the admin-automation service client
          expression: 'return {"tenant_id": "tenant_admin"}'
      - id: admin-automation-provider
        model: authentik_providers_oauth2.oauth2provider
        identifiers:
          name: iverson-admin-automation
        attrs:
          authorization_flow: !Find [authentik_flows.flow, [slug, default-provider-authorization-implicit-consent]]
          invalidation_flow: !Find [authentik_flows.flow, [slug, default-provider-invalidation-flow]]
          client_type: confidential
          client_id: {{ $adminAutoClientId }}
          client_secret: {{ randAlphaNum 64 }}
          redirect_uris: []
          grant_types: ["client_credentials"]
          signing_key: !Find [authentik_crypto.certificatekeypair, [name, "authentik Self-signed Certificate"]]
          issuer_mode: global
          access_token_validity: "hours=1"
          property_mappings:
            - !KeyOf scope-admin
            - !KeyOf scope-schema-admin
            - !KeyOf scope-tenant-id-admin
          jwt_federation_sources:
            - !KeyOf k8s-admin-automation-source
      # all, not the default any: both bindings must pass.
      - id: admin-automation-app
        model: authentik_core.application
        identifiers:
          slug: iverson-admin-automation
        attrs:
          name: iverson-admin-automation
          provider: !KeyOf admin-automation-provider
          policy_engine_mode: all
      # CSR round-10 #4: Operator tokens mint only from inside the cluster. The tls-proxy's public
      # listener marks every request it forwards; a request without the marker did not come through
      # the Ingress.
      - id: admin-automation-in-cluster-only
        model: authentik_policies_expression.expressionpolicy
        identifiers:
          name: iverson-admin-automation-in-cluster-only
        attrs:
          expression: |
            return "HTTP_X_IVERSON_PUBLIC_INGRESS" not in request.http_request.META
      # Authentik checks only a federated JWT's signature and expiry. This pins the ServiceAccount and
      # audience, and bounds the token's lifetime to 600 s (so its usable age too). No pod claim: these
      # tokens are minted with kubectl and are not pod-bound.
      - id: admin-automation-workload
        model: authentik_policies_expression.expressionpolicy
        identifiers:
          name: iverson-admin-automation-workload
        attrs:
          expression: |
            jwt = request.context.get("oauth_jwt") or {}
            aud = jwt.get("aud")
            auds = aud if isinstance(aud, list) else [aud]
            iat = jwt.get("iat")
            exp = jwt.get("exp")
            return (
                jwt.get("sub") == "system:serviceaccount:{{ .Release.Namespace }}:{{ .Release.Name }}-admin-automation"
                and "iverson-authentik-admin-automation" in auds
                and isinstance(iat, int) and isinstance(exp, int)
                and exp - iat <= 600
            )
      - model: authentik_policies.policybinding
        identifiers:
          policy: !KeyOf admin-automation-in-cluster-only
          target: !KeyOf admin-automation-app
          order: 0
      - model: authentik_policies.policybinding
        identifiers:
          policy: !KeyOf admin-automation-workload
          target: !KeyOf admin-automation-app
          order: 1
```

- [ ] **Step 5: One comment follows the move.** In `blueprints-configmap.yaml`, in the comment above `orchestrator-in-cluster-only`, change `service-clients.yaml's apply order.` to `admin-automation.yaml's apply order.`.

- [ ] **Step 6: Render and check.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh; mkdir -p $SCR/t5
cd $CH && helm dependency build . >/dev/null
for v in values-laptop values-aws; do o="-f $v.yaml"; [ -f $v.ci-override.yaml ] && o="$o -f $v.ci-override.yaml"; helm template iverson . -n iverson $o > $SCR/t5/$v.yaml || echo "RENDER FAIL $v"; done
python3 - $SCR/t5/values-laptop.yaml <<'PY'
import re, sys, yaml
class L(yaml.SafeLoader): pass
for t in ("!Find", "!KeyOf", "!Env"):
    L.add_constructor(t, lambda l, n: ("TAG", l.construct_sequence(n) if isinstance(n, yaml.SequenceNode) else l.construct_scalar(n)))
docs = [d for d in yaml.safe_load_all(open(sys.argv[1])) if d]
sec = next(d for d in docs if d["kind"] == "Secret" and d["metadata"]["name"] == "iverson-authentik-blueprints-service-clients")
aac = next(d for d in docs if d["kind"] == "Secret" and d["metadata"]["name"] == "iverson-authentik-admin-automation-client")
sa = [d["metadata"]["name"] for d in docs if d["kind"] == "ServiceAccount"]
aa = yaml.load(sec["stringData"]["admin-automation.yaml"], Loader=L)
sc = sec["stringData"]["service-clients.yaml"]
prov = next(e for e in aa["entries"] if e.get("model") == "authentik_providers_oauth2.oauth2provider")["attrs"]
pin = next(e for e in aa["entries"] if e.get("id") == "admin-automation-workload")["attrs"]["expression"]
print("stringData keys:", sorted(sec["stringData"]), "| client Secret keys:", sorted(aac["data"]))
print("sa:", "iverson-admin-automation" in sa, "| entries:", len(aa["entries"]), "| secret len:", len(prov["client_secret"]), "| client_id len:", len(str(prov["client_id"])))
print("pin sub:", re.search(r'== "([^"]+)"', pin).group(1))
print("service-clients still has admin-automation provider:", "name: iverson-admin-automation\n" in sc,
      "| marker policy:", "iverson-admin-automation-in-cluster-only" in sc, "| tenant_id_admin kept:", "scope_name: tenant_id_admin" in sc)
PY
cd $WT/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj -p:UseSharedCompilation=false --filter "FullyQualifiedName~AuthentikOrchestratorFederationBlueprintTests|FullyQualifiedName~AuthentikOrchestratorRoleBlueprintTests" 2>&1 | tail -3; dotnet build-server shutdown >/dev/null 2>&1
```

Expected:
- no `RENDER FAIL`;
- `stringData keys: ['admin-automation.yaml', 'service-clients.yaml'] | client Secret keys: ['client-id']`;
- `sa: True | entries: 12 | secret len: 64 | client_id len: 40`;
- `pin sub: system:serviceaccount:iverson:iverson-admin-automation`;
- `service-clients still has admin-automation provider: False | marker policy: False | tenant_id_admin kept: True`;
- `Passed!` with 0 failed. The service-clients text tests still find the workload user and the retired token: the new key's entries are parsed after them and name neither.

- [ ] **Step 7: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh; cd $WT
git add Iverson.Server/deploy/helm/iverson/charts/authentik/templates/serviceaccount-admin-automation.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/templates/secret-service-clients.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap.yaml
git commit -m "federate admin-automation from a service-account token and drop its client secret

$TRAILER"
```

### Task 6: .NET SDK service-token provider

**Files:**
- Modify: `$WT/Iverson.Clients/DotNet/Iverson.Client.Core/ServiceCollectionExtensions.cs:38-46,93-118`
- Test: `$WT/Iverson.Clients/DotNet/Iverson.Client.Core.Tests/ServiceCollectionExtensionsTests.cs`

**Interfaces:**
- Produces: `AddIversonClient(…, bool allowInsecureChannelCallCredentials = false, Func<Task<string>>? serviceTokenProvider = null, params Assembly[] entityAssemblies)`. Task 7 consumes it.

- [ ] **Step 1: Write the failing tests.**
  1. In `ServiceCollectionExtensionsTests.cs`, add `using Grpc.Core;`, `using Iverson.Client.Contracts;` and `using Microsoft.Extensions.Http;` to the usings.
  2. Append to the class:

```csharp
    // ---- CSR round-11 #9: the service-token provider ------------------------------------------------

    private static Func<Task<string>> ServiceTokenProvider => () => Task.FromResult("service-token");

    [Fact]
    public void AddIversonClient_WithCredentialsAndServiceTokenProvider_Throws()
    {
        var services = new ServiceCollection();
        var credentials = new IversonClientCredentials("test-client", "test-secret", "https://auth.example.com/oauth/token");

        var act = () => services.AddIversonClient(
            grpcEndpoint: "https://localhost:5000",
            credentials: credentials,
            serviceTokenProvider: ServiceTokenProvider);

        act.Should().Throw<InvalidOperationException>().WithMessage("*credentials*serviceTokenProvider*");
    }

    [Fact]
    public async Task AddIversonClient_WithServiceTokenProvider_AuthenticatesAllFourClients()
    {
        var seen = await AuthorizationHeadersAsync(serviceTokenProvider: ServiceTokenProvider);

        seen.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["mapping"] = "Bearer service-token",
            ["persistence"] = "Bearer service-token",
            ["retrieval"] = "Bearer service-token",
            ["search"] = "Bearer service-token",
        });
    }

    [Fact]
    public async Task AddIversonClient_WithServiceTokenProvider_LeavesTheDataPlaneToItsOwnProvider()
    {
        var seen = await AuthorizationHeadersAsync(
            serviceTokenProvider: ServiceTokenProvider,
            dataPlaneTokenProvider: () => Task.FromResult("data-plane-token"));

        seen.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["mapping"] = "Bearer service-token",
            ["persistence"] = "Bearer data-plane-token",
            ["retrieval"] = "Bearer data-plane-token",
            ["search"] = "Bearer data-plane-token",
        });
    }

    /// <summary>
    /// Registers the clients against an https endpoint, replaces each one's transport with a handler
    /// that records the request's authorization header and fails the call, makes one unary call per
    /// client, and returns what each sent.
    /// </summary>
    private static async Task<Dictionary<string, string?>> AuthorizationHeadersAsync(
        Func<Task<string>>? serviceTokenProvider = null, Func<Task<string>>? dataPlaneTokenProvider = null)
    {
        var seen = new Dictionary<string, string?>();
        var services = new ServiceCollection();
        services.AddIversonClient(
            grpcEndpoint: "https://localhost:5000",
            dataPlaneTokenProvider: dataPlaneTokenProvider,
            serviceTokenProvider: serviceTokenProvider);
        foreach (var (name, label) in new[]
        {
            (nameof(ObjectMappingService.ObjectMappingServiceClient), "mapping"),
            (nameof(ObjectPersistenceService.ObjectPersistenceServiceClient), "persistence"),
            (nameof(ObjectRetrievalService.ObjectRetrievalServiceClient), "retrieval"),
            (nameof(ObjectSearchService.ObjectSearchServiceClient), "search"),
        })
        {
            services.Configure<HttpClientFactoryOptions>(name, o => o.HttpMessageHandlerBuilderActions.Add(b =>
                b.PrimaryHandler = new RecordingHandler(header => seen[label] = header)));
        }
        using var provider = services.BuildServiceProvider();

        await Swallow(provider.GetRequiredService<ObjectMappingService.ObjectMappingServiceClient>().GetSchemaAsync(new GetSchemaRequest()).ResponseAsync);
        await Swallow(provider.GetRequiredService<ObjectPersistenceService.ObjectPersistenceServiceClient>().PostAsync(new PersistRequest()).ResponseAsync);
        await Swallow(provider.GetRequiredService<ObjectRetrievalService.ObjectRetrievalServiceClient>().GetAsync(new RetrievalRequest()).ResponseAsync);
        await Swallow(provider.GetRequiredService<ObjectSearchService.ObjectSearchServiceClient>().AggregateAsync(new AggregateRequest()).ResponseAsync);
        return seen;

        static async Task Swallow(Task call)
        {
            try { await call; } catch (RpcException) { }
        }
    }

    private sealed class RecordingHandler(Action<string?> record) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            record(request.Headers.TryGetValues("authorization", out var values) ? values.Single() : null);
            throw new HttpRequestException("recorded");
        }
    }
```

Run:

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-identity-plane/Iverson.Clients/DotNet
systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj -p:UseSharedCompilation=false --filter "FullyQualifiedName~ServiceCollectionExtensionsTests" 2>&1 | tail -5; dotnet build-server shutdown >/dev/null 2>&1
```

Expected: the build fails with CS1739 (`AddIversonClient` has no parameter named `serviceTokenProvider`).

- [ ] **Step 2: Add the parameter.** In `ServiceCollectionExtensions.cs`:
  1. Add this `<param>` after the `allowInsecureChannelCallCredentials` one:

```csharp
    /// <param name="serviceTokenProvider">
    /// Token source for the schema-registration (mapping) client, and for the data-plane clients
    /// when <paramref name="dataPlaneTokenProvider"/> is omitted, in place of
    /// <paramref name="credentials"/>: for a caller that obtains its service token itself, such as
    /// one that exchanges a Kubernetes service-account token instead of holding a client secret.
    /// Mutually exclusive with <paramref name="credentials"/>.
    /// </param>
```

  2. Insert `Func<Task<string>>? serviceTokenProvider = null,` between `bool allowInsecureChannelCallCredentials = false,` and `params Assembly[] entityAssemblies)`.
  3. Make the first statements of the method body:

```csharp
        if (credentials is not null && serviceTokenProvider is not null)
        {
            throw new InvalidOperationException(
                "Pass either credentials or serviceTokenProvider, not both: each authenticates the " +
                "schema-registration client.");
        }
```

  4. After the `if (credentials is not null) { … AttachCredentials(mappingBuilder, … ) }` block, add:

```csharp
        else if (serviceTokenProvider is not null)
        {
            AttachCredentials(mappingBuilder, _ => serviceTokenProvider(), allowInsecureChannelCallCredentials);
        }
```

  5. After the data-plane `else if (credentials is not null) { … searchBuilder … }` block, add:

```csharp
        else if (serviceTokenProvider is not null)
        {
            AttachCredentials(persistenceBuilder, _ => serviceTokenProvider(), allowInsecureChannelCallCredentials);
            AttachCredentials(retrievalBuilder, _ => serviceTokenProvider(), allowInsecureChannelCallCredentials);
            AttachCredentials(searchBuilder, _ => serviceTokenProvider(), allowInsecureChannelCallCredentials);
        }
```

Re-run Step 1's command. Expected: `Passed!` with 0 failed, the existing guard facts included.

- [ ] **Step 3: Every caller still compiles.**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-identity-plane/Iverson.Clients/DotNet
systemd-run --user --scope -q -p MemoryMax=4G dotnet build Iverson.Client.Sample/Iverson.Client.Sample.csproj -p:UseSharedCompilation=false 2>&1 | command grep -E "error|Build succeeded" | head -3; dotnet build-server shutdown >/dev/null 2>&1
```

Expected: `Build succeeded`. LoadTest, the other caller, is built in Task 7. Every call site passes the arguments after the third by name.

- [ ] **Step 4: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh; cd $WT
git add Iverson.Clients/DotNet/Iverson.Client.Core/ServiceCollectionExtensions.cs Iverson.Clients/DotNet/Iverson.Client.Core.Tests/ServiceCollectionExtensionsTests.cs
git commit -m "let addiversonclient take a service-token provider instead of client credentials

$TRAILER"
```

### Task 7: LoadTest assertion mode

**Files:**
- Create: `$WT/Iverson.Server/Iverson.LoadTest/Auth/AdminAutomationCredentials.cs`
- Test: `$WT/Iverson.Server/Iverson.LoadTest.Tests/Auth/AdminAutomationCredentialsTests.cs`
- Modify: `$WT/Iverson.Server/Iverson.LoadTest/Program.cs` (`:5`, `:36-41`, `:109-124`, `:157-160`, `:184-188`, `:389-416`)
- Modify: `$WT/Iverson.Server/Iverson.LoadTest/README.md` (env table, `:168-169`)

**Interfaces:**
- Consumes: Task 6's `serviceTokenProvider`, and Task 5's provider (`client_id` from the `<release>-authentik-admin-automation-client` Secret; assertion audience `iverson-authentik-admin-automation`).

- [ ] **Step 1: Write the failing tests.** Create `AdminAutomationCredentialsTests.cs` with exactly:

```csharp
using System.Net;
using System.Text;
using System.Web;
using FluentAssertions;
using Iverson.LoadTest.Auth;
using Xunit;

namespace Iverson.LoadTest.Tests.Auth;

public class AdminAutomationCredentialsTests
{
    private const string Endpoint = "http://localhost:9000/application/o/token/";

    private sealed record Sent(string? Host, string? Authorization, Dictionary<string, string> Form);

    // Answers every token request with a token that expires inside the 60 s re-use margin, so each
    // GetTokenAsync call sends a new request, and records what was sent.
    private sealed class TokenEndpoint : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var form = HttpUtility.ParseQueryString(body);
            Requests.Add(new Sent(
                request.Headers.Host,
                request.Headers.Authorization?.ToString(),
                form.AllKeys.ToDictionary(k => k!, k => form[k]!)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"access_token":"operator-{{Requests.Count}}","token_type":"Bearer","expires_in":30}""",
                    Encoding.UTF8, "application/json"),
            };
        }
    }

    private static Func<string, string?> Env(params (string Key, string Value)[] vars) =>
        key => vars.FirstOrDefault(v => v.Key == key).Value;

    [Theory]
    [InlineData("IVERSON_CLIENT_ID")]
    [InlineData("IVERSON_TOKEN_ENDPOINT")]
    public void FromEnvironment_IsNull_WithoutClientIdOrTokenEndpoint(string missing)
    {
        var all = new[] { ("IVERSON_CLIENT_ID", "id"), ("IVERSON_TOKEN_ENDPOINT", Endpoint), ("IVERSON_CLIENT_SECRET", "s") };

        AdminAutomationCredentials.FromEnvironment(Env(all.Where(v => v.Item1 != missing).ToArray()), null)
            .Should().BeNull();
    }

    [Fact]
    public void FromEnvironment_IsNull_WithNeitherSecretNorAssertionFile() =>
        AdminAutomationCredentials.FromEnvironment(Env(("IVERSON_CLIENT_ID", "id"), ("IVERSON_TOKEN_ENDPOINT", Endpoint)), null)
            .Should().BeNull();

    [Fact]
    public void FromEnvironment_PrefersTheAssertionFile_OverASecret()
    {
        using var creds = AdminAutomationCredentials.FromEnvironment(Env(
            ("IVERSON_CLIENT_ID", "id"), ("IVERSON_TOKEN_ENDPOINT", Endpoint),
            ("IVERSON_CLIENT_SECRET", "s"), ("IVERSON_CLIENT_ASSERTION_FILE", "/tmp/sa-token")), null)!;

        creds.UsesAssertion.Should().BeTrue();
        creds.ClientSecret.Should().BeNull("a secret must never be sent alongside the assertion");
        creds.SdkCredentials.Should().BeNull("in assertion mode the SDK takes the service-token provider instead");
    }

    [Fact]
    public async Task GetTokenAsync_WithASecret_SendsTheSecretAndNoAssertion()
    {
        var endpoint = new TokenEndpoint();
        using var creds = new AdminAutomationCredentials("id", Endpoint, "s3cret", null, "admin schema_admin", "authentik-server:9000", endpoint);

        (await creds.GetTokenAsync()).Should().Be("operator-1");

        var sent = endpoint.Requests.Single();
        sent.Form["grant_type"].Should().Be("client_credentials");
        sent.Form["scope"].Should().Be("admin schema_admin");
        sent.Form.Should().NotContainKey("client_assertion").And.NotContainKey("client_secret");
        sent.Authorization.Should().StartWith("Basic ", "IdentityModel's default sends the secret as HTTP Basic, as today");
        sent.Host.Should().Be("authentik-server:9000");
        creds.SdkCredentials.Should().NotBeNull();
    }

    [Fact]
    public async Task GetTokenAsync_WithAnAssertionFile_SendsTheFileAsAJwtBearerAssertion_ReadingItForEachMint()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "first.jwt.token\n");
            var endpoint = new TokenEndpoint();
            using var creds = new AdminAutomationCredentials("id", Endpoint, null, file, "admin", null, endpoint);

            (await creds.GetTokenAsync()).Should().Be("operator-1");
            await File.WriteAllTextAsync(file, "second.jwt.token");
            (await creds.GetTokenAsync()).Should().Be("operator-2");

            endpoint.Requests.Should().HaveCount(2);
            foreach (var sent in endpoint.Requests)
            {
                sent.Form["grant_type"].Should().Be("client_credentials");
                sent.Form["client_id"].Should().Be("id");
                sent.Form["client_assertion_type"].Should().Be(AdminAutomationCredentials.JwtBearerAssertionType);
                sent.Form.Should().NotContainKey("client_secret");
                sent.Authorization.Should().BeNull();
            }
            endpoint.Requests[0].Form["client_assertion"].Should().Be("first.jwt.token");
            endpoint.Requests[1].Form["client_assertion"].Should().Be("second.jwt.token",
                "the file is read again for every mint, so a refreshed token is picked up");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task GetTokenAsync_WithAMissingAssertionFile_SaysHowToMintOne()
    {
        using var creds = new AdminAutomationCredentials("id", Endpoint, null, "/nonexistent/sa-token", null, null, new TokenEndpoint());

        var act = () => creds.GetTokenAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*kubectl create token*");
    }
}
```

Run:

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-identity-plane/Iverson.Server
systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj -p:UseSharedCompilation=false --filter "FullyQualifiedName~AdminAutomationCredentialsTests" 2>&1 | tail -5; dotnet build-server shutdown >/dev/null 2>&1
```

Expected: the build fails with CS0246, because `AdminAutomationCredentials` doesn't exist yet.

- [ ] **Step 2: Create `AdminAutomationCredentials.cs`** with exactly:

```csharp
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using IdentityModel.Client;
using Iverson.Client.Core;

namespace Iverson.LoadTest.Auth;

/// <summary>
/// The admin-automation identity LoadTest provisions tenants and registers schemas with, from
/// <c>IVERSON_CLIENT_ID</c>, <c>IVERSON_TOKEN_ENDPOINT</c>, <c>IVERSON_CLIENT_SCOPE</c> and either
/// <c>IVERSON_CLIENT_SECRET</c> (compose) or <c>IVERSON_CLIENT_ASSERTION_FILE</c>. On Helm the
/// admin-automation client has no usable secret (CSR round-11 #9): the caller mints a short-lived
/// token of the <c>&lt;release&gt;-admin-automation</c> ServiceAccount into a file, and LoadTest sends
/// it as a jwt-bearer client assertion. The file is read again for every token request, so a run
/// that outlasts one Operator token (1 hour) picks up a refreshed file.
/// </summary>
public sealed class AdminAutomationCredentials : IDisposable
{
    public const string JwtBearerAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public AdminAutomationCredentials(
        string clientId, string tokenEndpoint, string? clientSecret, string? assertionFile,
        string? scope, string? hostHeader, HttpMessageHandler? handler = null)
    {
        ClientId = clientId;
        TokenEndpoint = tokenEndpoint;
        ClientSecret = clientSecret;
        AssertionFile = assertionFile;
        Scope = scope;
        HostHeader = hostHeader;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
    }

    public string ClientId { get; }
    public string TokenEndpoint { get; }
    public string? ClientSecret { get; }
    public string? AssertionFile { get; }
    public string? Scope { get; }
    public string? HostHeader { get; }

    public bool UsesAssertion => AssertionFile is not null;

    /// <summary>
    /// The configured identity, or null when there is none: no client id, no token endpoint, or
    /// neither a secret nor an assertion file. An assertion file takes precedence over a secret.
    /// </summary>
    public static AdminAutomationCredentials? FromEnvironment(Func<string, string?> env, string? hostHeader)
    {
        var clientId = env("IVERSON_CLIENT_ID");
        var tokenEndpoint = env("IVERSON_TOKEN_ENDPOINT");
        var secret = env("IVERSON_CLIENT_SECRET");
        var assertionFile = env("IVERSON_CLIENT_ASSERTION_FILE") is { Length: > 0 } file ? file : null;
        if (clientId is null || tokenEndpoint is null || (secret is null && assertionFile is null))
            return null;
        return new AdminAutomationCredentials(
            clientId, tokenEndpoint, assertionFile is null ? secret : null, assertionFile,
            env("IVERSON_CLIENT_SCOPE"), hostHeader);
    }

    /// <summary>The SDK's own client-credentials identity, in secret mode only.</summary>
    public IversonClientCredentials? SdkCredentials =>
        UsesAssertion ? null : new IversonClientCredentials(ClientId, ClientSecret!, TokenEndpoint, Scope, HostHeader);

    /// <summary>An Operator access token, re-used until a minute before it expires.</summary>
    public async Task<string> GetTokenAsync()
    {
        if (_token is not null && DateTimeOffset.UtcNow < _expiresAt)
            return _token;

        await _lock.WaitAsync();
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiresAt)
                return _token;

            using var request = BuildRequest();
            var response = await _http.RequestClientCredentialsTokenAsync(request);
            if (response.IsError)
                throw new InvalidOperationException($"Failed to acquire admin-automation token: {response.Error}");
            _token = response.AccessToken!;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn - 60);
            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

    private ClientCredentialsTokenRequest BuildRequest()
    {
        var request = new ClientCredentialsTokenRequest
        {
            Address = TokenEndpoint,
            ClientId = ClientId,
            Scope = Scope,
        };
        if (AssertionFile is not null)
        {
            if (!File.Exists(AssertionFile))
                throw new InvalidOperationException(
                    $"IVERSON_CLIENT_ASSERTION_FILE names '{AssertionFile}', which does not exist. Mint a token with " +
                    "`kubectl create token <release>-admin-automation --audience iverson-authentik-admin-automation " +
                    "--duration 10m` and write it there.");
            request.ClientAssertion = new ClientAssertion
            {
                Type = JwtBearerAssertionType,
                Value = File.ReadAllText(AssertionFile).Trim(),
            };
            request.ClientCredentialStyle = ClientCredentialStyle.PostBody;
        }
        else
        {
            request.ClientSecret = ClientSecret;
        }
        // Authentik's issuer_mode:global stamps the token's `iss` from the request's Host header, so a
        // token minted through a host-exposed port names an issuer the API rejects unless the request
        // carries the in-network host (IversonClientCredentials.HostHeader).
        if (HostHeader is { Length: > 0 } host)
            request.Headers.Host = host;
        return request;
    }

    public void Dispose()
    {
        _http.Dispose();
        _lock.Dispose();
    }
}
```

Re-run Step 1's command. Expected: `Passed!` with 0 failed.

- [ ] **Step 3: Use it in `Program.cs`.**
  1. Delete `using IdentityModel.Client;` (`:5`). Nothing else in the file uses it once step 6 below removes the static minting function.
  2. Delete the three variable lines for `IVERSON_CLIENT_ID`, `IVERSON_CLIENT_SECRET` and `IVERSON_CLIENT_SCOPE` (`clientId`, `clientSecret`, `clientScope`). Keep `tokenEndpoint`, which `actingUserBaseUrl` reads.
  3. Replace `var clientCredentials = clientId is not null && … : null;` (its three lines, under the HostHeader comment) with:

```csharp
// A client secret (compose) or, on Helm, a service-account token file (CSR round-11 #9).
using var adminAutomation = AdminAutomationCredentials.FromEnvironment(
    Environment.GetEnvironmentVariable, actingUserHostHeader);
```

  4. Change:
     - `if (needsTenantAndSchema && clientCredentials is not null)` to `if (needsTenantAndSchema && adminAutomation is not null)`;
     - `var adminToken = await MintClientCredentialsTokenAsync(clientCredentials);` to `var adminToken = await adminAutomation.GetTokenAsync();`.
  5. In the skip message, replace `"Client credentials not configured (IVERSON_CLIENT_ID/IVERSON_CLIENT_SECRET/IVERSON_TOKEN_ENDPOINT) — " +` with `"Client credentials not configured (IVERSON_CLIENT_ID/IVERSON_TOKEN_ENDPOINT, and IVERSON_CLIENT_SECRET or IVERSON_CLIENT_ASSERTION_FILE) — " +`.
  6. In `.AddIversonClient(`:
     - change `grpcUrl, clientCredentials,` to `grpcUrl, adminAutomation?.SdkCredentials,`;
     - add `serviceTokenProvider: adminAutomation is { UsesAssertion: true } ? adminAutomation.GetTokenAsync : null,` after `allowInsecureChannelCallCredentials: true,`.
  7. Delete the whole `static async Task<string> MintClientCredentialsTokenAsync(IversonClientCredentials creds)` function, up to the `// Returns CreateTenant's one-time admin recovery link` comment. Its Host-header reasoning now lives in `AdminAutomationCredentials.BuildRequest`.

- [ ] **Step 4: Document the variable.** In `README.md`'s environment table, insert this row before the `IVERSON_CLIENT_SCOPE` row:

```
| `IVERSON_CLIENT_ASSERTION_FILE` | unset (a file holding a `<release>-admin-automation` ServiceAccount token; when set, it replaces `IVERSON_CLIENT_SECRET` and is sent as a jwt-bearer client assertion, re-read on every token request — the Helm path, `docs/runbooks/csr11-identity-plane.md`) |
```

- [ ] **Step 5: Build and run the LoadTest suite.**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-identity-plane/Iverson.Server
systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj -p:UseSharedCompilation=false 2>&1 | command grep -E "warn.*Program.cs|error|Passed!|Failed!" | head; dotnet build-server shutdown >/dev/null 2>&1
command grep -c 'MintClientCredentialsTokenAsync\|clientCredentials\b' Iverson.LoadTest/Program.cs
```

Expected: `Passed!` with 0 failed, no error or `Program.cs` warning lines, and the count `0`.

- [ ] **Step 6: Mutation proof.**
  1. Stage the task's files (Step 7's `git add`).
  2. In `BuildRequest`, swap the two branches' bodies, so an assertion file sends the secret. Run Step 1's command, and record that the assertion and secret facts fail.
  3. Restore with `git -C /home/ben/repositories/Iverson/.worktrees/csr11-identity-plane checkout -- Iverson.Server/Iverson.LoadTest/Auth/AdminAutomationCredentials.cs`.

- [ ] **Step 7: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh; cd $WT
git add Iverson.Server/Iverson.LoadTest/Auth/AdminAutomationCredentials.cs Iverson.Server/Iverson.LoadTest.Tests/Auth/AdminAutomationCredentialsTests.cs Iverson.Server/Iverson.LoadTest/Program.cs Iverson.Server/Iverson.LoadTest/README.md
git commit -m "let loadtest authenticate admin-automation with a service-account token file

$TRAILER"
```

### Task 8: Cloud edge rate limits (#5; Terraform + Helm wiring)

**Files:**
- Create: `$WT/Iverson.Server/deploy/terraform/aws/waf.tf`, `gcp/waf.tf`, `azure/waf.tf`, `modules/cluster-azure/waf.tf`
- Modify: `$WT/Iverson.Server/deploy/terraform/azure/main.tf`, `modules/cluster-azure/{main,variables,outputs}.tf`
- Modify: `$CH/charts/authentik/values.yaml`, `$CH/charts/authentik/templates/ingress.yaml`, `$CH/charts/authentik/templates/service.yaml`
- Create: `$CH/charts/authentik/templates/backendconfig.yaml`
- Modify: `$CH/values-{aws,gcp,azure}.yaml` and `$CH/values-{aws,gcp,azure}.ci-override.yaml`

**Interfaces:**
- Produces Terraform outputs that the runbook (Task 9) copies into values:
  - `authentik_waf_acl_arn` (aws) → `authentik.ingress.wafAclArn`;
  - `authentik_security_policy_name` (gcp) → `authentik.ingress.securityPolicy`;
  - `authentik_waf_policy_id` (azure) → `authentik.ingress.wafPolicyId`.

- [ ] **Step 1: Variables shared by the three roots.** Each root's `waf.tf` starts with the same two variables. Only the `contains(...)` list and its message differ: they are the windows each cloud accepts.

```hcl
# CSR round-11 #5: the edge rate limit on Authentik's flow executor, the path every login and MFA
# submission uses. Counted per client address; requests over the limit in the window are blocked.
variable "authentik_login_rate_limit" {
  type    = number
  default = 100
}

variable "authentik_login_rate_window" {
  type    = number
  default = 300
  validation {
    condition     = contains(<LIST>, var.authentik_login_rate_window)
    error_message = "authentik_login_rate_window must be one of <WORDS> seconds<WHY>."
  }
}
```

The substitutions per root:
- **aws:** `<LIST>` = `[60, 120, 300, 600]`, `<WORDS>` = `60, 120, 300 or 600`, `<WHY>` = `, the windows WAF rate rules accept`.
- **gcp:** `<LIST>` = `[10, 30, 60, 120, 180, 240, 300, 600, 900, 1200, 1800, 2700, 3600]`, `<WORDS>` = `10, 30, 60, 120, 180, 240, 300, 600, 900, 1200, 1800, 2700 or 3600`, `<WHY>` = `, the intervals Cloud Armor rate limits accept`.
- **azure:** `<LIST>` = `[60, 300]`, `<WORDS>` = `60 or 300`, `<WHY>` = `, the durations Application Gateway rate-limit rules accept`.

- [ ] **Step 2: AWS.** Create `terraform/aws/waf.tf`: Step 1's variables (aws substitutions), followed by:

```hcl
# One ACL, attached only to the Authentik Ingress's own ALB (authentik.ingress.wafAclArn; each
# Ingress gets its own ALB). The path is URL-decoded before it is matched, because Authentik decodes
# percent-encoded path segments and serves the executor for them.
resource "aws_wafv2_web_acl" "authentik_login" {
  name  = "${var.cluster_name}-authentik-login"
  scope = "REGIONAL"

  default_action {
    allow {}
  }

  rule {
    name     = "authentik-login-rate"
    priority = 1

    action {
      block {}
    }

    statement {
      rate_based_statement {
        limit                 = var.authentik_login_rate_limit
        aggregate_key_type    = "IP"
        evaluation_window_sec = var.authentik_login_rate_window

        scope_down_statement {
          byte_match_statement {
            search_string         = "/api/v3/flows/executor/"
            positional_constraint = "STARTS_WITH"
            field_to_match {
              uri_path {}
            }
            text_transformation {
              priority = 0
              type     = "URL_DECODE"
            }
          }
        }
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "authentik-login-rate"
      sampled_requests_enabled   = true
    }
  }

  visibility_config {
    cloudwatch_metrics_enabled = true
    metric_name                = "${var.cluster_name}-authentik-login"
    sampled_requests_enabled   = true
  }
}

# Copy into values-aws.yaml's authentik.ingress.wafAclArn (docs/runbooks/csr11-identity-plane.md).
output "authentik_waf_acl_arn" { value = aws_wafv2_web_acl.authentik_login.arn }
```

- [ ] **Step 3: GCP.** Create `terraform/gcp/waf.tf`: Step 1's variables (gcp substitutions), followed by:

```hcl
# Attached only to the Authentik Service's backend, through the BackendConfig the chart renders when
# authentik.ingress.securityPolicy is set. A client over the limit is banned for 10 minutes. The path
# is URL-decoded before it is matched, because Authentik decodes percent-encoded path segments.
resource "google_compute_security_policy" "authentik_login" {
  name = "${var.cluster_name}-authentik-login"

  rule {
    action   = "rate_based_ban"
    priority = 1000
    match {
      expr {
        expression = "request.path.urlDecode().startsWith('/api/v3/flows/executor/')"
      }
    }
    rate_limit_options {
      conform_action   = "allow"
      exceed_action    = "deny(429)"
      enforce_on_key   = "IP"
      ban_duration_sec = 600
      rate_limit_threshold {
        count        = var.authentik_login_rate_limit
        interval_sec = var.authentik_login_rate_window
      }
    }
  }

  rule {
    action   = "allow"
    priority = 2147483647
    match {
      versioned_expr = "SRC_IPS_V1"
      config {
        src_ip_ranges = ["*"]
      }
    }
  }
}

# Copy into values-gcp.yaml's authentik.ingress.securityPolicy (docs/runbooks/csr11-identity-plane.md).
output "authentik_security_policy_name" { value = google_compute_security_policy.authentik_login.name }
```

- [ ] **Step 4: Azure root.**
  1. Create `terraform/azure/waf.tf` holding only Step 1's variables (azure substitutions).
  2. In `terraform/azure/main.tf`, after the `cluster_admin_group_object_ids` variable, add:

```hcl

# CSR round-11 #5: the Authentik host the login rate limit matches (authentik.<global.ingressHost>);
# no default.
variable "authentik_host" { type = string }
```

  3. In the `module "cluster"` block, after `cluster_admin_group_object_ids = var.cluster_admin_group_object_ids`, add these three lines, aligned:

```hcl
  authentik_host                 = var.authentik_host
  authentik_login_rate_limit     = var.authentik_login_rate_limit
  authentik_login_rate_window    = var.authentik_login_rate_window
```

  4. Before `output "kubeconfig_command"`, add:

```hcl
# Copy into values-azure.yaml's authentik.ingress.wafPolicyId (docs/runbooks/csr11-identity-plane.md).
output "authentik_waf_policy_id" { value = module.cluster.authentik_waf_policy_id }
```

- [ ] **Step 5: Azure module.**
  1. In `modules/cluster-azure/variables.tf`, before `# No default: the Entra group(s) whose members administer the cluster through Azure RBAC.`, add:

```hcl
# CSR round-11 #5: the edge rate limit on Authentik's flow executor (waf.tf). The window is one of
# the two durations Application Gateway rate-limit rules accept.
variable "authentik_host" { type = string }

variable "authentik_login_rate_limit" { type = number }

variable "authentik_login_rate_window" {
  type = number
  validation {
    condition     = contains([60, 300], var.authentik_login_rate_window)
    error_message = "authentik_login_rate_window must be 60 or 300 seconds."
  }
}

```

  2. In `modules/cluster-azure/outputs.tf`, append:

```hcl

output "authentik_waf_policy_id" { value = azurerm_web_application_firewall_policy.authentik.id }
```

  3. In `modules/cluster-azure/main.tf`, replace `  ingress_application_gateway {` / `    subnet_cidr = "10.1.16.0/24"` / `  }` with:

```hcl
  # CSR round-11 #5: AGIC programs the WAF_v2 gateway in waf.tf instead of creating its own
  # Standard_v2 gateway, which could carry no WAF policy.
  ingress_application_gateway {
    gateway_id = azurerm_application_gateway.agic.id
  }
```

  4. Create `modules/cluster-azure/waf.tf` with exactly:

```hcl
# CSR round-11 #5: the Application Gateway AGIC programs, owned here so it can be WAF_v2. It sits in
# 10.1.16.0/24, the range the add-on's own gateway used, so global.trustedProxies.cidrs on the Azure
# profile still names the gateway, and in this resource group, which the add-on identity is granted
# Network Contributor on below.
resource "azurerm_subnet" "appgw" {
  name                 = "${var.cluster_name}-appgw-subnet"
  resource_group_name  = azurerm_resource_group.this.name
  virtual_network_name = azurerm_virtual_network.this.name
  address_prefixes     = ["10.1.16.0/24"]
}

resource "azurerm_public_ip" "appgw" {
  name                = "${var.cluster_name}-appgw-pip"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  allocation_method   = "Static"
  sku                 = "Standard"
}

# The gateway-wide policy, for every host but Authentik's: OWASP in Detection, which logs and never
# blocks.
resource "azurerm_web_application_firewall_policy" "gateway" {
  name                = "${var.cluster_name}-waf-gateway"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name

  policy_settings {
    mode = "Detection"
  }

  managed_rules {
    managed_rule_set {
      type    = "OWASP"
      version = "3.2"
    }
  }
}

# The Authentik Ingress's per-path policy (authentik.ingress.wafPolicyId). Prevention, because a
# rate rule blocks only in Prevention; the managed rule set the policy requires therefore also blocks
# on the Authentik host. The path is URL-decoded before it is matched, because Authentik decodes
# percent-encoded path segments.
resource "azurerm_web_application_firewall_policy" "authentik" {
  name                = "${var.cluster_name}-waf-authentik"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name

  policy_settings {
    mode = "Prevention"
  }

  custom_rules {
    name                 = "authentiklogin"
    priority             = 1
    rule_type            = "RateLimitRule"
    action               = "Block"
    rate_limit_duration  = var.authentik_login_rate_window == 60 ? "OneMin" : "FiveMins"
    rate_limit_threshold = var.authentik_login_rate_limit
    group_rate_limit_by  = "ClientAddr"

    match_conditions {
      match_variables {
        variable_name = "RequestHeaders"
        selector      = "Host"
      }
      operator     = "Equal"
      match_values = [var.authentik_host]
      transforms   = ["Lowercase"]
    }

    match_conditions {
      match_variables {
        variable_name = "RequestUri"
      }
      operator     = "BeginsWith"
      match_values = ["/api/v3/flows/executor/"]
      transforms   = ["UrlDecode"]
    }
  }

  managed_rules {
    managed_rule_set {
      type    = "OWASP"
      version = "3.2"
    }
  }
}

# Terraform owns only the shell AGIC needs to exist; AGIC writes the listeners, pools, rules and
# probes from the Ingresses, so those are ignored here.
resource "azurerm_application_gateway" "agic" {
  name                = "${var.cluster_name}-appgw"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  firewall_policy_id  = azurerm_web_application_firewall_policy.gateway.id

  sku {
    name     = "WAF_v2"
    tier     = "WAF_v2"
    capacity = 1
  }

  gateway_ip_configuration {
    name      = "gw"
    subnet_id = azurerm_subnet.appgw.id
  }

  frontend_port {
    name = "http"
    port = 80
  }

  frontend_ip_configuration {
    name                 = "public"
    public_ip_address_id = azurerm_public_ip.appgw.id
  }

  backend_address_pool {
    name = "default"
  }

  backend_http_settings {
    name                  = "default"
    cookie_based_affinity = "Disabled"
    port                  = 80
    protocol              = "Http"
  }

  http_listener {
    name                           = "default"
    frontend_ip_configuration_name = "public"
    frontend_port_name             = "http"
    protocol                       = "Http"
  }

  request_routing_rule {
    name                       = "default"
    rule_type                  = "Basic"
    http_listener_name         = "default"
    backend_address_pool_name  = "default"
    backend_http_settings_name = "default"
    priority                   = 1
  }

  lifecycle {
    ignore_changes = [
      backend_address_pool, backend_http_settings, frontend_port, http_listener, probe,
      request_routing_rule, url_path_map, ssl_certificate, redirect_configuration, tags,
    ]
  }
}

# azure-cli grants this when it enables the add-on with a gateway id; Terraform's gateway_id does
# not, and without it AGIC cannot program the gateway (VA 44).
resource "azurerm_role_assignment" "agic_network_contributor" {
  scope                = azurerm_resource_group.this.id
  role_definition_name = "Network Contributor"
  principal_id         = azurerm_kubernetes_cluster.this.ingress_application_gateway[0].ingress_application_gateway_identity[0].object_id
}
```

- [ ] **Step 6: Terraform checks, in a scratch copy** (`init` writes `.terraform/` into whatever directory it runs in):

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh; rm -rf $SCR/t8tf && mkdir -p $SCR/t8tf
terraform fmt -check -recursive $WT/Iverson.Server/deploy/terraform && echo fmt-ok
cp -r $WT/Iverson.Server/deploy/terraform $SCR/t8tf/
for c in aws gcp azure; do
  systemd-run --user --scope -q -p MemoryMax=2G terraform -chdir=$SCR/t8tf/terraform/$c init -backend=false -lockfile=readonly -input=false > $SCR/t8tf/init-$c.log 2>&1 || echo "$c INIT FAIL"
  echo "$c: $(systemd-run --user --scope -q -p MemoryMax=2G terraform -chdir=$SCR/t8tf/terraform/$c validate -no-color 2>&1 | tail -2 | tr '\n' ' ')"; done
rm -rf $SCR/t8tf/terraform/*/.terraform
tfsec --no-color $SCR/t8tf/terraform 2>&1 | tail -10
```

Expected:
- `fmt-ok`;
- no `INIT FAIL`;
- `Success! The configuration is valid.` for each of aws, gcp and azure;
- tfsec reports `No problems detected!`.

- [ ] **Step 7: Helm wiring.**
  1. In `$CH/charts/authentik/values.yaml`, under `ingress:` after `tlsSecretName: ""`, add:

```yaml
  # CSR round-11 #5: the cloud edge rate limit on the login path, from the Terraform output of the
  # same name (docs/runbooks/csr11-identity-plane.md). templates/ingress.yaml refuses an empty value
  # for its className's cloud: alb needs wafAclArn (authentik_waf_acl_arn), gce needs securityPolicy
  # (authentik_security_policy_name), azure-application-gateway needs wafPolicyId
  # (authentik_waf_policy_id).
  wafAclArn: ""
  securityPolicy: ""
  wafPolicyId: ""
```

  2. In `templates/ingress.yaml`, insert before `apiVersion: networking.k8s.io/v1`:

```yaml
{{- $wafKey := get (dict "alb" "wafAclArn" "gce" "securityPolicy" "azure-application-gateway" "wafPolicyId") .Values.ingress.className }}
{{- if and $wafKey (not (get .Values.ingress $wafKey)) }}
{{- fail (printf "authentik.ingress.%s must be set with ingress className %q: copy it from the Terraform output (docs/runbooks/csr11-identity-plane.md). Without it the public login has no edge rate limit (CSR round-11 #5)." $wafKey .Values.ingress.className) }}
{{- end }}
{{- $annotations := deepCopy (default dict .Values.ingress.annotations) }}
{{- with .Values.ingress.wafAclArn }}
{{- $_ := set $annotations "alb.ingress.kubernetes.io/wafv2-acl-arn" . }}
{{- end }}
{{- with .Values.ingress.wafPolicyId }}
{{- $_ := set $annotations "appgw.ingress.kubernetes.io/waf-policy-for-path" . }}
{{- end }}
```

     Then change `    {{- toYaml .Values.ingress.annotations | nindent 4 }}` to `    {{- toYaml $annotations | nindent 4 }}`.
  3. In `templates/service.yaml`, after `  name: {{ .Release.Name }}-authentik`, add:

```yaml
  {{- with .Values.ingress.securityPolicy }}
  # CSR round-11 #5: GCE attaches backendconfig.yaml (the Cloud Armor policy) to the public
  # listener's backend.
  annotations:
    cloud.google.com/backend-config: '{"ports": {"9080": "{{ $.Release.Name }}-authentik"}}'
  {{- end }}
```

  4. Create `templates/backendconfig.yaml` with exactly:

```yaml
{{- with .Values.ingress.securityPolicy }}
# CSR round-11 #5: the Cloud Armor policy (Terraform's authentik_security_policy_name) on the Authentik
# backend, attached by service.yaml's backend-config annotation.
apiVersion: cloud.google.com/v1
kind: BackendConfig
metadata:
  name: {{ $.Release.Name }}-authentik
spec:
  securityPolicy:
    name: {{ . | quote }}
{{- end }}
```

  5. Add the empty values with a pointer to the output, at the end of each cloud profile's `authentik.ingress` block (four-space indent):
     - In `values-aws.yaml`, after `      alb.ingress.kubernetes.io/ssl-redirect: "443"`, the last annotation of the authentik block (directly above the blank line before `adminUi:`).
     - In `values-gcp.yaml` and `values-azure.yaml`, after the authentik block's `    tlsSecretName: "iverson-authentik-tls"`.

```yaml
    # CSR round-11 #5: Terraform output authentik_waf_acl_arn (docs/runbooks/csr11-identity-plane.md).
    wafAclArn: ""
```

     The same goes into gcp, with `authentik_security_policy_name` / `securityPolicy: ""`, and into azure, with `authentik_waf_policy_id` / `wafPolicyId: ""`.
  6. CI renders the cloud profiles with their overrides, so give each a placeholder.
     - **`values-aws.ci-override.yaml`:** under `authentik:` → `ingress:` (sibling of `annotations:`), add `    wafAclArn: "arn:aws:wafv2:us-east-1:123456789012:regional/webacl/iverson-authentik-login/00000000-0000-0000-0000-000000000000"`.
     - **`values-gcp.ci-override.yaml`:** append:

```yaml
authentik:
  ingress:
    securityPolicy: "iverson-authentik-login"
```

     - **`values-azure.ci-override.yaml`:** append:

```yaml
authentik:
  ingress:
    wafPolicyId: "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/iverson-rg/providers/Microsoft.Network/ApplicationGatewayWebApplicationFirewallPolicies/iverson-waf-authentik"
```

- [ ] **Step 8: Render, lint, check the guard, and run kubeconform.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh; mkdir -p $SCR/t8
cd $CH && helm dependency build . >/dev/null
for v in values-local values-laptop values-aws values-azure values-gcp; do o="-f $v.yaml"; [ -f $v.ci-override.yaml ] && o="$o -f $v.ci-override.yaml"
  helm template iverson . -n iverson $o > $SCR/t8/$v.yaml; e=$?
  echo "$v template=$e lint=$(helm lint . $o 2>&1 | tail -1) | $(kubeconform -kubernetes-version 1.30.0 -summary -ignore-missing-schemas < $SCR/t8/$v.yaml 2>&1 | tail -1)"; done
for c in aws:wafAclArn gcp:securityPolicy azure:wafPolicyId; do cl=${c%%:*}; k=${c##*:}
  helm template iverson . -f values-$cl.yaml -f values-$cl.ci-override.yaml --set authentik.ingress.$k= 2>&1 | head -1 | cut -c1-140; done
python3 - $SCR/t8 <<'PY'
import sys, yaml
for v in ("values-aws", "values-gcp", "values-azure", "values-laptop"):
    docs = [d for d in yaml.safe_load_all(open(f"{sys.argv[1]}/{v}.yaml")) if d]
    ing = next(d for d in docs if d["kind"] == "Ingress" and d["metadata"]["name"] == "iverson-authentik")
    svc = next(d for d in docs if d["kind"] == "Service" and d["metadata"]["name"] == "iverson-authentik")
    print(v, sorted(k for k in (ing["metadata"].get("annotations") or {}) if "waf" in k),
          (svc["metadata"].get("annotations") or {}).get("cloud.google.com/backend-config"),
          [d["spec"] for d in docs if d["kind"] == "BackendConfig"])
PY
```

Expected:
- every profile reads `template=0 lint=1 chart(s) linted, 0 chart(s) failed`, with `Invalid: 0, Errors: 0`;
- the three guard lines start `Error: execution error at (iverson/charts/authentik/templates/ingress.yaml` and name `authentik.ingress.wafAclArn`, `.securityPolicy` and `.wafPolicyId` in turn;
- `values-aws ['alb.ingress.kubernetes.io/wafv2-acl-arn'] None []`;
- `values-gcp [] {"ports": {"9080": "iverson-authentik"}} [{'securityPolicy': {'name': 'iverson-authentik-login'}}]`;
- `values-azure ['appgw.ingress.kubernetes.io/waf-policy-for-path'] None []`;
- `values-laptop [] None []`.

- [ ] **Step 9: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh; cd $WT
git add Iverson.Server/deploy/terraform/aws/waf.tf Iverson.Server/deploy/terraform/gcp/waf.tf Iverson.Server/deploy/terraform/azure/waf.tf Iverson.Server/deploy/terraform/azure/main.tf Iverson.Server/deploy/terraform/modules/cluster-azure/waf.tf Iverson.Server/deploy/terraform/modules/cluster-azure/main.tf Iverson.Server/deploy/terraform/modules/cluster-azure/variables.tf Iverson.Server/deploy/terraform/modules/cluster-azure/outputs.tf Iverson.Server/deploy/helm/iverson/charts/authentik/values.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/templates/ingress.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/templates/service.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/templates/backendconfig.yaml Iverson.Server/deploy/helm/iverson/values-aws.yaml Iverson.Server/deploy/helm/iverson/values-gcp.yaml Iverson.Server/deploy/helm/iverson/values-azure.yaml Iverson.Server/deploy/helm/iverson/values-aws.ci-override.yaml Iverson.Server/deploy/helm/iverson/values-gcp.ci-override.yaml Iverson.Server/deploy/helm/iverson/values-azure.ci-override.yaml
git commit -m "rate-limit the authentik login path at the aws, gcp and azure edge

$TRAILER"
```

### Task 9: Runbook and docs

**Files:**
- Create: `$WT/docs/runbooks/csr11-identity-plane.md`
- Modify: `$WT/docs/user-management-and-security.md:130-131,485-488`, `$WT/docs/runbooks/operator-access-onboarding.md:27-29`, `$WT/docs/runbooks/admin-console-landing-page-usage.md:100-102`, `$WT/docs/runbooks/grpc-admin-auth-cutover.md:35-38`

- [ ] **Step 1: Create the runbook** with exactly this content:

````markdown
# CSR Round-11 Identity Plane — Operator Runbook

**Why:** this change makes Authentik record the real client address and throttle failed logins. It
enrols a second factor during recovery, refuses superusers on the public listener, and replaces the
`akadmin` password with a named break-glass admin. Admin-automation callers authenticate with a
short-lived service-account token instead of a client secret. The login path is also rate-limited
at the cloud edge. Compose keeps `akadmin`, its bootstrap credentials and its client secrets.

## 1. Edge rate limits (cloud profiles)

Terraform creates the limit, and Helm attaches it to the Authentik Ingress. Terraform does not
feed Helm values, so copy the output across by hand. A cloud profile's render fails until you do.

| Cloud | Terraform output | Values key (`values-<cloud>.yaml`) |
|---|---|---|
| AWS | `authentik_waf_acl_arn` | `authentik.ingress.wafAclArn` |
| GCP | `authentik_security_policy_name` | `authentik.ingress.securityPolicy` |
| Azure | `authentik_waf_policy_id` | `authentik.ingress.wafPolicyId` |

1. Optionally set `authentik_login_rate_limit` (default 100 requests) and
   `authentik_login_rate_window` (default 300 s) on the root. Azure accepts only 60 or 300 s.
   Azure also requires `authentik_host`, the Authentik host: `authentik.<global.ingressHost>`.
2. `terraform apply`, then `terraform output -raw <output>` and put the value under the key in the
   table.
3. `helm upgrade`. The limit counts requests per client address to `/api/v3/flows/executor/` on
   the Authentik host, and blocks (GCP: bans for 10 minutes) over the limit.

On Azure the Application Gateway is now Terraform's own `WAF_v2` gateway in `10.1.16.0/24`. The
add-on is pointed at it and granted Network Contributor on the resource group. This assumes a fresh
install: no install with the add-on's own gateway exists. On the Authentik host the gateway runs
the OWASP rule set in Prevention, so a managed-rule false positive can refuse a login there. The
other hosts run it in Detection.

## 2. After an install or upgrade: apply the changed blueprints

Authentik re-applies a blueprint whose file changed, through a file watcher and an hourly
schedule. On a fresh install, its first automatic apply can also run before the stock login flow
exists, which fails `reputation.yaml` and `superusers.yaml` until the hourly re-apply. To have
everything in force at once, apply the files yourself after the install or upgrade, static files
first. `superusers.yaml` refers to flows defined in them, and a missing referent fails the
whole file.

1. Wait until the new worker pod is the only one:
   `kubectl -n <ns> rollout status deploy/<release>-authentik-worker`, then repeat
   `kubectl -n <ns> get pods -l app=<release>-authentik-worker` until it lists one pod.
2. On a fresh install, wait until Authentik's own blueprints have created the login flow and the
   TOTP setup stage the static files refer to (up to 5 minutes):

   ```bash
   ok=no; for i in $(seq 1 30); do [ "$(kubectl -n <ns> exec deploy/<release>-authentik-worker -c worker -- ak shell -c "from authentik.flows.models import Flow; from authentik.stages.authenticator_totp.models import AuthenticatorTOTPStage; print(Flow.objects.filter(slug='default-authentication-flow').count(), AuthenticatorTOTPStage.objects.filter(name='default-authenticator-totp-setup').count())" 2>/dev/null | tail -1)" = "1 1" ] && { ok=yes; break; }; sleep 10; done; echo "stock objects present: $ok"
   ```

   Continue when it prints `yes`. If it prints `no`, Authentik's own blueprints have not applied yet:
   check the worker's log for blueprint errors and repeat this step before applying.
3. Apply:

   ```bash
   kubectl -n <ns> exec deploy/<release>-authentik-worker -c worker -- \
     ak apply_blueprint custom/recovery-flow.yaml custom/reputation.yaml
   kubectl -n <ns> exec deploy/<release>-authentik-worker -c worker -- \
     ak apply_blueprint custom/superusers.yaml custom/orchestrator-federation.yaml \
       custom/service-clients/service-clients.yaml custom/service-clients/admin-automation.yaml
   ```

   Each command exits non-zero if a blueprint fails. `admin-automation.yaml` and
   `orchestrator-federation.yaml` fetch the cluster's keys from the worker's JWKS relay when they
   apply. If the relay can't reach the API server, both fail and wait for the next re-apply; the
   message names `oidc_jwks_url`.

## 3. Break-glass admin

`akadmin` is deactivated, and nobody has an Authentik superuser password. The superuser is
`iverson-breakglass`, which has no password until you activate it. Superusers are refused on the
public listener, at login and in recovery, so everything here uses the in-cluster route.

1. Forward the in-cluster listener: `kubectl -n <ns> port-forward svc/<release>-authentik 9000:9000`.
2. Generate a recovery link in the worker. This builds the same link as the admin API's user
   recovery action; it is valid for one hour and one use:

   ```bash
   kubectl -n <ns> exec deploy/<release>-authentik-worker -c worker -- ak shell -c '
   from django.test import RequestFactory
   from rest_framework.request import Request
   from authentik.brands.utils import get_brand_for_request
   from authentik.core.api.users import UserViewSet
   from authentik.core.models import User
   req = RequestFactory().get("/", HTTP_HOST="localhost:9000")
   req.brand = get_brand_for_request(req)
   view = UserViewSet()
   view.request = Request(req)
   view.get_object = lambda: User.objects.get(username="iverson-breakglass")
   link, _ = view._create_recovery_link("hours=1")
   print(link)'
   ```

3. Open the printed `http://localhost:9000/if/flow/iverson-recovery/?flow_token=…` link. Set a
   password, then scan the TOTP code shown, enter a code, and you are signed in. The flow ends on
   the console URL; the admin interface is at `http://localhost:9000/if/admin/`.
4. Later sign-ins: `http://localhost:9000/if/flow/default-authentication-flow/` through the same
   port-forward, with the password and a TOTP code. If the device is lost, generate a new link (step 2).

Do not use `ak create_recovery_key`: its link signs the user straight in and skips the recovery flow,
so no second factor is checked or enrolled.

## 4. Admin-automation callers

The `iverson-admin-automation` client accepts only a token of the `<release>-admin-automation`
ServiceAccount, with audience `iverson-authentik-admin-automation` and a lifetime of at most 600 s.
Its former client secret stops working at the upgrade. The token buys a 1-hour Operator token.

```bash
kubectl -n <ns> create token <release>-admin-automation --audience iverson-authentik-admin-automation \
  --duration 10m > /path/to/sa-token
export IVERSON_CLIENT_ID="$(kubectl -n <ns> get secret <release>-authentik-admin-automation-client -o jsonpath='{.data.client-id}' | base64 -d)"
export IVERSON_CLIENT_ASSERTION_FILE=/path/to/sa-token
export IVERSON_CLIENT_SCOPE="admin schema_admin tenant_id_admin"
export IVERSON_TOKEN_ENDPOINT=http://localhost:9000/application/o/token/   # with the port-forward from §3
export IVERSON_ACTING_USER_HOST_HEADER=<release>-authentik:9000             # the issuer the API accepts
```

LoadTest re-reads the file for every Operator token it requests, about 59 minutes after the previous
one, so the token in the file must still be valid then; it lives 10 minutes. For a run longer than an
hour, rewrite the file with a freshly minted token every 5 minutes for the length of the run. A caller of its own sends the token as
a `client_credentials` request with `client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer`
and `client_assertion=<token>`.

## 5. Login throttling

Authentik refuses a client address, before it asks for a username, once its failed attempts sum to
five. A failed password costs one and a failed MFA code two, and successful logins do not offset
failures. The address is the one Authentik records: the client itself on every profile, selected
from the load balancer's `X-Forwarded-For` by `global.trustedProxies`. A shared NAT can therefore be
refused for up to 24 hours, counted from the first failure.

To clear an address before then, delete its scores in the admin interface (in-cluster, §3) or with
the API:

```bash
curl -s -H "Authorization: Bearer <admin token>" "http://localhost:9000/api/v3/policies/reputation/scores/?ip=<address>"
curl -s -X DELETE -H "Authorization: Bearer <admin token>" "http://localhost:9000/api/v3/policies/reputation/scores/<pk>/"   # pk from the listing
```

## 6. Namespace access

The orchestrator and admin-automation trust Kubernetes service-account tokens, so whoever can mint
those tokens or run code as those accounts in the release namespace can obtain the tokens' rights.
Restrict these verbs in the release namespace to the deploy identity: create on `pods`,
`pods/exec` and `serviceaccounts/token`. Check a principal with
`kubectl auth can-i create serviceaccounts/token -n <ns> --as <user>`.
````

- [ ] **Step 2: Point the Helm admin logins at the break-glass procedure.**
  1. **`docs/user-management-and-security.md`.**
     - Replace the bullet `- **kind:** email from \`AUTHENTIK_BOOTSTRAP_EMAIL\`, password from the` / `\`<release>-authentik-app\` Secret's \`bootstrap-password\` key.` (lines 130–131) with:

```markdown
   - **kind/Helm:** sign in as `iverson-breakglass`, activated through a recovery link generated in
     the worker (`docs/runbooks/csr11-identity-plane.md` §3); `akadmin` is deactivated since CSR
     round 11, and superusers are refused on the public listener.
```

     - At lines 485–488, replace `On kind, every \`client_id\`/\`client_secret\` above is instead generated once` through the end of that paragraph (`… read into the` / `API's \`Authentication:ValidAudiences\` env vars at deploy time.`) with:

```markdown
On kind, every `client_id`/`client_secret` above is instead generated once
and stored in a `lookup`-guarded Secret (`<release>-authentik-{loadtest,
webtest,admin-automation,human-oidc,loadtest-human}-client`), read into the
API's `Authentication:ValidAudiences` env vars at deploy time. The
admin-automation Secret holds only a `client-id`: on Helm that client takes a
service-account token instead of a secret (`docs/runbooks/csr11-identity-plane.md` §4).
```

  2. **`docs/runbooks/operator-access-onboarding.md`.** Replace `1. Log into the Authentik admin UI with the deployment's bootstrap credentials (kind: email from` / `   \`AUTHENTIK_BOOTSTRAP_EMAIL\`, password from the \`<release>-authentik-app\` Secret's` / `   \`bootstrap-password\` key).` (lines 27–29) with:

```markdown
1. Log into the Authentik admin UI as the break-glass admin, `iverson-breakglass`
   (`docs/runbooks/csr11-identity-plane.md` §3; `akadmin` is deactivated since CSR round 11).
```

  3. **`docs/runbooks/admin-console-landing-page-usage.md`.** Replace `1. Sign in to Authentik with the deployment's bootstrap credentials — email from` / `   \`AUTHENTIK_BOOTSTRAP_EMAIL\`, password from the \`<release>-authentik-app\` Secret's` / `   \`bootstrap-password\` key.` (lines 100–102) with:

```markdown
1. Sign in to Authentik as the break-glass admin, `iverson-breakglass`, over the in-cluster route
   (`docs/runbooks/csr11-identity-plane.md` §3; `akadmin` is deactivated since CSR round 11).
```

  4. **`docs/runbooks/grpc-admin-auth-cutover.md`.** After the sentence that ends `… blueprint's \`stringData\` on the very first \`helm install\` — no second pass needed` / `for these credentials specifically.` (line 38's paragraph), add the sentence: "Since CSR round 11 the admin-automation Secret carries only its `client-id`; its client authenticates with a service-account token (`docs/runbooks/csr11-identity-plane.md` §4)."

  The compose instructions in those files stay as they are.

- [ ] **Step 3: Check the docs read right.**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-identity-plane
command grep -n 'bootstrap-password' docs/user-management-and-security.md docs/runbooks/operator-access-onboarding.md docs/runbooks/admin-console-landing-page-usage.md
command grep -c 'iverson-breakglass' docs/user-management-and-security.md docs/runbooks/operator-access-onboarding.md docs/runbooks/admin-console-landing-page-usage.md docs/runbooks/csr11-identity-plane.md
```

Expected: the first grep prints nothing, because no Helm login uses the bootstrap password. The second prints a count of at least 1 for each file.

- [ ] **Step 4: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh; cd $WT
git add -f docs/runbooks/csr11-identity-plane.md docs/user-management-and-security.md docs/runbooks/operator-access-onboarding.md docs/runbooks/admin-console-landing-page-usage.md docs/runbooks/grpc-admin-auth-cutover.md
git commit -m "document break-glass access, admin-automation tokens and the login limits

$TRAILER"
```

### Task 10: Verification

No commits. The live part is heavy: run one heavy process at a time, and check `free -g` first. The kind and compose checks run one after the other, never together.

- [ ] **Step 1: Full suites.** These run one at a time; the container-backed classes are inside them.

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-identity-plane/Iverson.Server
for p in Iverson.Api.Tests Iverson.ClientConformance.Tests Iverson.Embeddings.Tests Iverson.Events.Tests Iverson.LoadTest.Tests Iverson.Patterns.Tests Iverson.Sql.Tests Iverson.StarRocks.Tests Iverson.Vector.Tests; do
  echo "$p: $(systemd-run --user --scope -q -p MemoryMax=7G dotnet test $p/$p.csproj -p:UseSharedCompilation=false 2>&1 | command grep -E '^(Passed|Failed)!' | tail -1)"; done
cd ../Iverson.Clients/DotNet && echo "Client.Core.Tests: $(systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj -p:UseSharedCompilation=false 2>&1 | command grep -E '^(Passed|Failed)!' | tail -1)"
dotnet build-server shutdown >/dev/null 2>&1
```

Expected: every line reads `Passed!`, with 0 failed.

- [ ] **Step 2: Mutation proofs.** Read every task report under `$SCR`, and check each proof is recorded:
  - Task 2 Step 7: `skip`;
  - Task 4: Step 1's failures before Step 2;
  - Task 6: Step 1's compile failure, plus one more: drop the new `else if (serviceTokenProvider is not null)` mapping branch, and the "all four clients" fact must fail;
  - Task 7 Step 6.

  Run any proof a report lacks the same way: stage first, mutate, run that task's filter and record the failing test, `git checkout --` the file. End with `git -C $WT status --porcelain` empty.

- [ ] **Step 3: Render checks on the final branch** (spec §7's list):

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh; mkdir -p $SCR/t10
cd $CH && helm dependency build . >/dev/null
for v in values-local values-laptop values-aws values-azure values-gcp; do o="-f $v.yaml"; [ -f $v.ci-override.yaml ] && o="$o -f $v.ci-override.yaml"
  helm template iverson . -n iverson $o > $SCR/t10/$v.yaml || echo "RENDER FAIL $v"; helm lint . $o >/dev/null 2>&1 || echo "LINT FAIL $v"; done
python3 - $SCR/t10 <<'PY'
import sys, yaml
class L(yaml.SafeLoader): pass
for t in ("!Find", "!KeyOf", "!Env"):
    L.add_constructor(t, lambda l, n: ("TAG", l.construct_sequence(n) if isinstance(n, yaml.SequenceNode) else l.construct_scalar(n)))
waf = {"values-aws": "alb.ingress.kubernetes.io/wafv2-acl-arn", "values-azure": "appgw.ingress.kubernetes.io/waf-policy-for-path"}
for v in ("values-local", "values-laptop", "values-aws", "values-azure", "values-gcp"):
    docs = [d for d in yaml.safe_load_all(open(f"{sys.argv[1]}/{v}.yaml")) if d]
    get = lambda kind, name: next(d for d in docs if d["kind"] == kind and d["metadata"]["name"] == name)
    env = lambda dep: {e["name"]: e.get("value") for c in dep["spec"]["template"]["spec"]["containers"] for e in c.get("env", [])}
    api, srv = env(get("Deployment", "iverson-api")), env(get("Deployment", "iverson-authentik-server"))
    proxy = get("ConfigMap", "iverson-authentik-tls-proxy")["data"]["default.conf"]
    cfg = get("ConfigMap", "iverson-authentik-config")["data"]
    bp = get("ConfigMap", "iverson-authentik-blueprints")["data"]
    sec = get("Secret", "iverson-authentik-blueprints-service-clients")["stringData"]
    rec = bp["recovery-flow.yaml"]; orch = bp["orchestrator-federation.yaml"]
    ing = get("Ingress", "iverson-authentik")["metadata"].get("annotations") or {}
    checks = {
        "api trusted env": "RateLimiting__TrustedProxies__Cidrs__0" in api and "RateLimiting__TrustedProxies__Hops" in api,
        "proxy geo/map/xff": all(s in proxy for s in ("geo $remote_addr $iverson_peer_trusted", "map $iverson_client $iverson_client_addr",
                                                      "proxy_set_header X-Forwarded-For $iverson_client_addr;", "proxy_set_header X-Forwarded-For $remote_addr;")),
        "authentik trusts loopback": cfg.get("AUTHENTIK_LISTEN__TRUSTED_PROXY_CIDRS") == "127.0.0.1/32,::1/128",
        "no bootstrap on server": "AUTHENTIK_BOOTSTRAP_PASSWORD" not in srv,
        "recovery configure": any(e.get("identifiers", {}).get("name") == "iverson-recovery-mfa-validation" and e["attrs"].get("not_configured_action") == "configure" and e["attrs"].get("configuration_stages") == [("TAG", ["authentik_stages_authenticator_totp.authenticatortotpstage", ["name", "default-authenticator-totp-setup"]])] for e in yaml.load(rec, Loader=L)["entries"]),
        "reputation file": (lambda rep: any(e["model"] == "authentik_policies_expression.expressionpolicy" and e["identifiers"] == {"name": "iverson-login-failures-by-address"} and "score__lt=0" in e["attrs"]["expression"] and "return failures > -5" in e["attrs"]["expression"] for e in rep) and any(e["model"] == "authentik_policies.policybinding" and e["identifiers"] == {"policy": ("TAG", "login-failures-by-address"), "target": ("TAG", ["authentik_policies.policybindingmodel", ["flow__slug", "default-authentication-flow"]]), "order": 0} for e in rep))(yaml.load(bp.get("reputation.yaml", "version: 1\nentries: []"), Loader=L)["entries"]),
        "superusers file": (lambda su: len(su) == 9 and any(e["model"] == "authentik_core.user" and e["identifiers"] == {"username": "akadmin"} and e["attrs"].get("is_active") is False for e in su) and any(e["model"] == "authentik_core.group" and e["identifiers"] == {"name": "authentik Admins"} and e["attrs"].get("is_superuser") is True for e in su) and any(e["model"] == "authentik_core.user" and e["identifiers"] == {"username": "iverson-breakglass"} and e["attrs"].get("groups") == [("TAG", "admins")] for e in su) and sorted((e["identifiers"]["target"][1][1][1], e["attrs"]["order"]) for e in su if e["model"] == "authentik_flows.flowstagebinding" and e["identifiers"].get("stage") == ("TAG", "refuse-superuser")) == [("default-authentication-flow", 15), ("iverson-recovery", 0)])(yaml.load(bp["superusers.yaml"], Loader=L)["entries"]),
        "pin bounds": "exp - iat <= 600" in orch and '"pod" in k8s' in orch,
        "admin-automation file": "jwt_federation_sources" in sec.get("admin-automation.yaml", ""),
        "admin-automation SA": any(d["kind"] == "ServiceAccount" and d["metadata"]["name"] == "iverson-admin-automation" for d in docs),
        "no client-secret": sorted(get("Secret", "iverson-authentik-admin-automation-client")["data"]) == ["client-id"],
        "waf wiring": (waf[v] in ing) if v in waf else (any(d["kind"] == "BackendConfig" for d in docs) if v == "values-gcp" else not any("waf" in k for k in ing)),
    }
    bad = [k for k, ok in checks.items() if not ok]
    print(v, "OK" if not bad else f"FAIL {bad}")
PY
```

Expected: no `RENDER FAIL` or `LINT FAIL`, and `OK` for all five profiles.

- [ ] **Step 4: Terraform.** Re-run Task 8 Step 6 on the final branch. Expected: as there.

- [ ] **Step 5: Record the user's state, and write the live driver.**
  - Check the user's stack is down: `docker ps --format '{{.Names}}'`. If the list isn't empty, **stop and ask the user**. Never stop their containers yourself: compose binds host port 9000 and kind binds 8080.
  - Record their state:

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh; mkdir -p $LIVE
docker ps -a --format '{{.Names}}' | sort > $LIVE/before-containers.txt
docker volume ls -q | sort > $LIVE/before-volumes.txt
docker network ls --format '{{.Name}}' | sort > $LIVE/before-networks.txt
docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort > $LIVE/before-images.txt
```

  - Write `$LIVE/flow.py`, the flow-executor driver both live checks use, with exactly this content. It is a browser-shaped client: it follows each 302 by GETting the same executor URL, as Authentik's own web UI and LoadTest do.

```python
"""Drives Authentik's flow executor for the CSR round-11 sub-project B live checks."""
import base64, hashlib, hmac, struct, time, urllib.parse
import requests

def totp(secret_b32):
    key = base64.b32decode(secret_b32.upper() + "=" * (-len(secret_b32) % 8))
    mac = hmac.new(key, struct.pack(">Q", int(time.time() // 30)), hashlib.sha1).digest()
    o = mac[-1] & 0x0F
    return f"{(struct.unpack('>I', mac[o:o + 4])[0] & 0x7FFFFFFF) % 1000000:06d}"

def secret_of(challenge):
    return urllib.parse.parse_qs(urllib.parse.urlparse(challenge["config_url"]).query)["secret"][0]

class Flow:
    """One session against one executor URL. base: e.g. http://127.0.0.1:9000; host: Host header or None."""
    def __init__(self, base, slug, flow_token=None, host=None, headers=None):
        self.base, self.s = base, requests.Session()
        self.h = dict(headers or {})
        if host:
            self.h["Host"] = host
        q = "?query=" + urllib.parse.quote(f"flow_token={flow_token}", safe="") if flow_token else ""
        self.url = f"{base}/api/v3/flows/executor/{slug}/{q}"
    def _follow(self, r):
        for _ in range(20):
            if r.status_code != 302:
                break
            r = self.s.get(self.url, headers=self.h, allow_redirects=False, timeout=30)
        return r.json() if r.status_code == 200 else {"component": f"HTTP {r.status_code}"}
    def get(self):
        return self._follow(self.s.get(self.url, headers=self.h, allow_redirects=False, timeout=30))
    def post(self, data):
        return self._follow(self.s.post(self.url, json=data, headers=self.h, allow_redirects=False, timeout=30))
    def me(self):
        r = self.s.get(f"{self.base}/api/v3/core/users/me/", headers=self.h, timeout=30)
        return r.json()["user"]["username"] if r.status_code == 200 else None

def login(base, username, password, host=None, headers=None, secret=None):
    """default-authentication-flow; enrols TOTP when asked. Returns (final component, secret)."""
    f = Flow(base, "default-authentication-flow", host=host, headers=headers)
    c = f.get()
    if c["component"] != "ak-stage-identification":
        return c["component"], secret
    c = f.post({"component": "ak-stage-identification", "uid_field": username})
    if c["component"] != "ak-stage-password":
        return c["component"], secret
    c = f.post({"component": "ak-stage-password", "password": password})
    if c["component"] == "ak-stage-authenticator-validate" and c.get("configuration_stages"):
        stage = next(s for s in c["configuration_stages"] if s["meta_model_name"].endswith("authenticatortotpstage"))
        c = f.post({"component": "ak-stage-authenticator-validate", "selected_stage": stage["pk"]})
    if c["component"] == "ak-stage-authenticator-totp":
        secret = secret_of(c)
        c = f.post({"component": "ak-stage-authenticator-totp", "code": totp(secret)})
    elif c["component"] == "ak-stage-authenticator-validate":
        c = f.post({"component": "ak-stage-authenticator-validate", "code": totp(secret)})
    return c["component"], secret

def recover(base, link, password, host=None, headers=None):
    """Drives a recovery link: prompt, password, TOTP enrolment. Returns the components seen and the session's user."""
    token = urllib.parse.parse_qs(urllib.parse.urlparse(link).query)["flow_token"][0]
    f = Flow(base, "iverson-recovery", flow_token=token, host=host, headers=headers)
    seen = [f.get()["component"]]
    if seen[-1] != "ak-stage-prompt":
        return seen, f.me()
    c = f.post({"component": "ak-stage-prompt", "password": password, "password_repeat": password})
    seen.append(c["component"])
    if c["component"] == "ak-stage-authenticator-totp":
        seen.append(None)
        user_before_code = f.me()
        seen[-1] = f"me-before-code={user_before_code}"
        c = f.post({"component": "ak-stage-authenticator-totp", "code": totp(secret_of(c))})
        seen.append(c["component"])
    return seen, f.me()
```

- [ ] **Step 6: Compose live (isolated project `csr11b`, Authentik services only).**
  - **What the setup does.** It uses round 11 A's pattern: a separate project name, `container_name:` stripped, `--project-directory` on the worktree, and the worktree's own generated `.env`. `generate-compose-secrets.sh` writes that `.env` into the worktree, never into the main checkout's `Iverson.Server/.env`.
  - **Peer A.** Under rootless podman, the host's requests reach the server from a bridge address that Authentik does not trust.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh
cat >> $SCR/env.sh <<'X'
ENVF=$WT/Iverson.Server/.env
DC="docker compose -p csr11b -f $LIVE/compose.yml --project-directory $WT/Iverson.Server --env-file $ENVF"
val() { command grep "^$1=" "$ENVF" | cut -d= -f2-; }
X
source $SCR/env.sh; umask 077
bash $WT/scripts/generate-compose-secrets.sh >/dev/null
command grep -v container_name $WT/Iverson.Server/docker-compose.yml > $LIVE/compose.yml
free -g | sed -n 2p
$DC up -d authentik-server authentik-worker > $LIVE/compose-up.log 2>&1; echo "up exit=$?"
for i in $(seq 1 60); do [ "$(docker inspect -f '{{.State.Health.Status}}' csr11b-authentik-server-1 2>/dev/null)" = healthy ] && break; sleep 10; done; echo "server: $(docker inspect -f '{{.State.Health.Status}}' csr11b-authentik-server-1)"
for i in $(seq 1 30); do curl -s -H "Authorization: Bearer $(val AUTHENTIK_BOOTSTRAP_TOKEN)" "http://127.0.0.1:9000/api/v3/flows/instances/?slug=default-authentication-flow" | python3 -c 'import sys,json; sys.exit(0 if json.load(sys.stdin)["pagination"]["count"]==1 else 1)' 2>/dev/null && break; sleep 10; done
ok=no; for i in $(seq 1 30); do curl -s -H "Authorization: Bearer $(val AUTHENTIK_BOOTSTRAP_TOKEN)" "http://127.0.0.1:9000/api/v3/stages/authenticator/totp/?name=default-authenticator-totp-setup" | python3 -c 'import sys,json; sys.exit(0 if json.load(sys.stdin)["pagination"]["count"]==1 else 1)' 2>/dev/null && { ok=yes; break; }; sleep 10; done; echo "totp stage present: $ok"
docker exec csr11b-authentik-worker-1 ak apply_blueprint custom/recovery-flow.yaml custom/reputation.yaml > $LIVE/compose-apply.log 2>&1; echo "apply exit=$?"
for i in $(seq 1 30); do curl -s -H "Authorization: Bearer $(val AUTHENTIK_BOOTSTRAP_TOKEN)" "http://127.0.0.1:9000/api/v3/policies/expression/?name=iverson-login-failures-by-address" | python3 -c 'import sys,json; sys.exit(0 if json.load(sys.stdin)["pagination"]["count"]==1 else 1)' 2>/dev/null && break; sleep 10; done; echo "reputation policy applied: $?"
```

Expected: `up exit=0`, `server: healthy`, `totp stage present: yes`, `apply exit=0`, `reputation policy applied: 0`. The explicit apply is runbook §2's: the worker's first automatic apply can run before the stock login flow exists. The two polls wait for both stock objects the static files `!Find`: the login flow (`reputation.yaml`) and the TOTP setup stage (`recovery-flow.yaml`).

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh
cd $LIVE && TOKEN="$(val AUTHENTIK_BOOTSTRAP_TOKEN)" python3 - <<'PY'
import os, requests, flow
B = "http://127.0.0.1:9000"; H = {"Authorization": f"Bearer {os.environ['TOKEN']}"}
def user(name, password=None):
    pk = requests.post(f"{B}/api/v3/core/users/", json={"username": name, "name": name}, headers=H).json()["pk"]
    if password:
        requests.post(f"{B}/api/v3/core/users/{pk}/set_password/", json={"password": password}, headers=H).raise_for_status()
    return pk
# C1: a no-device recovery enrols TOTP before it signs in.
pk = user("csr11b-nodev")
link = requests.post(f"{B}/api/v3/core/users/{pk}/recovery/", headers=H).json()["link"]
print("C1", *flow.recover(B, link, "Correct-Horse-Battery-9"))
# C2: one address, a success for X then failures for Y: refused after five failures.
user("csr11b-x", "Correct-Horse-Battery-9"); user("csr11b-y", "Correct-Horse-Battery-9")
print("C2 x login:", flow.login(B, "csr11b-x", "Correct-Horse-Battery-9")[0])
spoof = {"X-Forwarded-For": "203.0.113.7"}
for i in range(1, 7):
    f = flow.Flow(B, "default-authentication-flow", headers=spoof)
    c = f.get()["component"]
    if c != "ak-stage-identification":
        print(f"C2 attempt {i}: {c}"); break
    f.post({"component": "ak-stage-identification", "uid_field": "csr11b-y"})
    f.post({"component": "ak-stage-password", "password": "wrong-password"})
print("C2 x again:", flow.login(B, "csr11b-x", "Correct-Horse-Battery-9")[0])
ips = {e["client_ip"] for e in requests.get(f"{B}/api/v3/events/events/?action=login_failed&page_size=50", headers=H).json()["results"]}
print("C3 login_failed client_ip:", sorted(ips))
ak = requests.get(f"{B}/api/v3/core/users/?username=akadmin", headers=H).json()["results"][0]
print("C4 compose akadmin active:", ak["is_active"])
PY
docker run --rm --network csr11b_default --memory 256m --entrypoint python3 -v $LIVE/flow.py:/tmp/flow.py:ro ghcr.io/goauthentik/server:2026.5.3 \
  -c "import sys; sys.path.insert(0,'/tmp'); import flow; print('C2 peer B first:', flow.Flow('http://authentik-server:9000','default-authentication-flow').get()['component'])"
```

Expected:
- `C1 ['ak-stage-prompt', 'ak-stage-authenticator-totp', 'me-before-code=None', 'xak-flow-redirect'] csr11b-nodev`;
- `C2 x login: xak-flow-redirect`;
- `C2 attempt 6: ak-stage-access-denied`: refused after five failures, despite X's success from the same address;
- `C2 x again: ak-stage-access-denied`;
- `C3 login_failed client_ip:` one address, and it is not `203.0.113.7`;
- `C4 compose akadmin active: True`;
- `C2 peer B first: ak-stage-identification`.

Tear the compose project down before kind: `$DC down -v > $LIVE/compose-down.log 2>&1; echo "down exit=$?"`. Expected: `down exit=0`. Then delete the worktree's generated `.env`, which is gitignored: `rm -f $WT/Iverson.Server/.env`.

- [ ] **Step 7: Kind: fresh install of the branch chart.** No install exists to upgrade (spec §8), so this is a fresh install. Ollama is off, because `CreateTenant` needs no model; TEI stays on, as in round 10's live checks. Run the slow steps with `run_in_background`.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh
cd $WT; free -g | sed -n 2p
KIND_EXPERIMENTAL_PROVIDER=podman kind create cluster --name iverson --config Iverson.Server/deploy/kind/kind-config.yaml
bash Iverson.Server/deploy/kind/setup.sh
TMPDIR=/var/tmp bash Iverson.Server/deploy/kind/build-and-load-image.sh csr11b iverson
python3 -c "import yaml; d=yaml.safe_load(open('Iverson.Server/deploy/helm/iverson/values-laptop.yaml')); d.setdefault('ollama',{})['enabled']=False; yaml.safe_dump(d, open('$LIVE/laptop-no-ollama.yaml','w'))"
TMPDIR=/var/tmp bash Iverson.Server/deploy/kind/build-and-load-image.sh csr11b iverson --model-images --values $LIVE/laptop-no-ollama.yaml
cd $CH && helm dependency build . >/dev/null
helm upgrade --install iverson . -n iverson --create-namespace -f values-laptop.yaml --set ollama.enabled=false \
  --set api.hpa.maxReplicas=1 --set api.image.tag=csr11b --set worker.image.tag=csr11b --wait --timeout 30m
kubectl -n iverson get pods --no-headers | awk '$3!="Running" && $3!="Completed"{print "NOT READY:", $0}'
ok=no; for i in $(seq 1 30); do [ "$(kubectl -n iverson exec deploy/iverson-authentik-worker -c worker -- ak shell -c "from authentik.flows.models import Flow; from authentik.stages.authenticator_totp.models import AuthenticatorTOTPStage; print(Flow.objects.filter(slug='default-authentication-flow').count(), AuthenticatorTOTPStage.objects.filter(name='default-authenticator-totp-setup').count())" 2>/dev/null | tail -1)" = "1 1" ] && { ok=yes; break; }; sleep 10; done; echo "stock objects present: $ok"
kubectl -n iverson exec deploy/iverson-authentik-worker -c worker -- ak apply_blueprint custom/recovery-flow.yaml custom/reputation.yaml > $LIVE/apply1.log 2>&1; echo "apply static exit=$?"
kubectl -n iverson exec deploy/iverson-authentik-worker -c worker -- ak apply_blueprint custom/superusers.yaml custom/orchestrator-federation.yaml custom/service-clients/service-clients.yaml custom/service-clients/admin-automation.yaml > $LIVE/apply2.log 2>&1; echo "apply templated exit=$?"
```

Expected: no `NOT READY` lines, `stock objects present: yes`, `apply static exit=0` and `apply templated exit=0`. These are the runbook §2 commands, after a wait for the stock login flow and TOTP setup stage the static files `!Find`.

- [ ] **Step 8: Kind live checks.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh
kubectl -n iverson port-forward svc/iverson-authentik 19000:9000 > $LIVE/pf-ak.log 2>&1 & echo $! > $LIVE/pf-ak.pid
kubectl -n iverson port-forward svc/iverson-api 18080:8080 > $LIVE/pf-api.log 2>&1 & echo $! > $LIVE/pf-api.pid
sleep 3
AKSH() { kubectl -n iverson exec deploy/iverson-authentik-worker -c worker -- ak shell -c "$1" 2>/dev/null | tail -${2:-1}; }
AKSH "
from authentik.core.models import User, Group
from authentik.flows.models import FlowStageBinding
b = User.objects.get(username='iverson-breakglass')
print('K1', 'akadmin active:', User.objects.get(username='akadmin').is_active, '| breakglass superuser:', b.is_superuser, 'usable pw:', b.has_usable_password(),
      '| deny orders:', sorted((x.target.slug, x.order) for x in FlowStageBinding.objects.filter(stage__name='iverson-refuse-superuser-on-public-listener')))"
kubectl -n iverson get sa iverson-admin-automation -o name
kubectl -n iverson get secret iverson-authentik-admin-automation-client -o jsonpath='{.data}' | python3 -c 'import sys,json; print("K1 client Secret keys:", sorted(json.load(sys.stdin)))'
cat > $LIVE/link.sh <<'X'
kubectl -n iverson exec deploy/iverson-authentik-worker -c worker -- ak shell -c '
from django.test import RequestFactory
from rest_framework.request import Request
from authentik.brands.utils import get_brand_for_request
from authentik.core.api.users import UserViewSet
from authentik.core.models import User
req = RequestFactory().get("/", HTTP_HOST="localhost:19000")
req.brand = get_brand_for_request(req)
view = UserViewSet()
view.request = Request(req)
view.get_object = lambda: User.objects.get(username="iverson-breakglass")
link, _ = view._create_recovery_link("hours=1")
print(link)' 2>/dev/null | tail -1
X
STATE="from authentik.core.models import User
from authentik.stages.authenticator_totp.models import TOTPDevice
u = User.objects.get(username='iverson-breakglass'); print('pw', u.password[-12:], 'devices', TOTPDevice.objects.filter(user=u).count())"
echo "K3 before: $(AKSH "$STATE")"
cd $LIVE && python3 -c "
import flow; print('K3 public recovery:', flow.recover('http://127.0.0.1:8080', '$(bash $LIVE/link.sh)', 'Correct-Horse-Battery-9', host='authentik.iverson.local')[0])"
echo "K3 after:  $(AKSH "$STATE")"
cd $LIVE && python3 -c "
import flow; print('K2 in-cluster:', *flow.recover('http://127.0.0.1:19000', '$(bash $LIVE/link.sh)', 'Correct-Horse-Battery-9'))"
cd $LIVE && python3 -c "
import flow; print('K3 public login:', flow.login('http://127.0.0.1:8080', 'iverson-breakglass', 'Correct-Horse-Battery-9', host='authentik.iverson.local')[0])"
AKSH "
from authentik.core.models import User
u = User.objects.create(username='csr11b-plain', name='csr11b-plain'); u.set_password('Correct-Horse-Battery-9'); u.save(); print('created')"
cd $LIVE && python3 -c "
import flow; print('K4 ordinary public login:', flow.login('http://127.0.0.1:8080', 'csr11b-plain', 'Correct-Horse-Battery-9', host='authentik.iverson.local')[0])
f = flow.Flow('http://127.0.0.1:8080', 'default-authentication-flow', host='authentik.iverson.local', headers={'X-Forwarded-For': '203.0.113.7'})
f.get(); f.post({'component': 'ak-stage-identification', 'uid_field': 'csr11b-plain'}); f.post({'component': 'ak-stage-password', 'password': 'wrong-password'})"
echo "K5 event client_ip: $(AKSH "from authentik.events.models import Event; print(Event.objects.filter(action='login_failed').order_by('-created').first().client_ip)")"
echo "K5 ingress saw:     $(kubectl -n ingress-nginx logs deploy/ingress-nginx-controller --tail=200 | command grep 'flows/executor/default-authentication-flow' | tail -1 | awk '{print $1}')"
```

Expected:
- `K1 akadmin active: False | breakglass superuser: True usable pw: False | deny orders: [('default-authentication-flow', 15), ('iverson-recovery', 0)]`;
- `serviceaccount/iverson-admin-automation`;
- `K1 client Secret keys: ['client-id']`;
- `K3 public recovery: ['ak-stage-access-denied']`, with `K3 before` and `K3 after` identical: no password and no device written;
- `K2 in-cluster: ['ak-stage-prompt', 'ak-stage-authenticator-totp', 'me-before-code=None', 'xak-flow-redirect'] iverson-breakglass`;
- `K3 public login: ak-stage-access-denied`;
- `created`;
- `K4 ordinary public login: xak-flow-redirect`;
- the two `K5` lines show the same address, and neither is `203.0.113.7`.

- [ ] **Step 9: Kind: orchestrator and admin-automation exchanges, and `CreateTenant` through both.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh
exchange() { # client_id assertion [extra curl args] -> MINTED <scope> | REFUSED <error>
  curl -s -H 'Host: iverson-authentik:9000' "${@:3}" http://127.0.0.1:19000/application/o/token/ -d grant_type=client_credentials -d client_id="$1" \
    -d client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer --data-urlencode client_assertion="$2" -d scope="$SCOPE" \
  | python3 -c 'import sys,json,base64; d=json.load(sys.stdin); t=d.get("access_token"); p=t.split(".")[1] if t else ""
print("MINTED " + str(json.loads(base64.urlsafe_b64decode(p+"="*(-len(p)%4))).get("scope")) if t else "REFUSED "+d.get("error",""))'; }
POD=$(kubectl -n iverson get pod -l app=iverson-api -o jsonpath='{.items[0].metadata.name}'); UID_=$(kubectl -n iverson get pod $POD -o jsonpath='{.metadata.uid}')
SCOPE=goauthentik.io/api
echo "orch pod-bound 10m: $(exchange iverson-admin-orchestrator "$(kubectl -n iverson create token iverson-api --audience iverson-authentik-orchestrator --duration 10m --bound-object-kind Pod --bound-object-name $POD --bound-object-uid $UID_)")"
echo "orch unbound 10m:   $(exchange iverson-admin-orchestrator "$(kubectl -n iverson create token iverson-api --audience iverson-authentik-orchestrator --duration 10m)")"
echo "orch pod-bound 1h:  $(exchange iverson-admin-orchestrator "$(kubectl -n iverson create token iverson-api --audience iverson-authentik-orchestrator --duration 1h --bound-object-kind Pod --bound-object-name $POD --bound-object-uid $UID_)")"
CID="$(kubectl -n iverson get secret iverson-authentik-admin-automation-client -o jsonpath='{.data.client-id}' | base64 -d)"; SCOPE="admin schema_admin tenant_id_admin"
echo "aa 10m:             $(exchange "$CID" "$(kubectl -n iverson create token iverson-admin-automation --audience iverson-authentik-admin-automation --duration 10m)")"
echo "aa 1h:              $(exchange "$CID" "$(kubectl -n iverson create token iverson-admin-automation --audience iverson-authentik-admin-automation --duration 1h)")"
echo "aa 10m, public:     $(exchange "$CID" "$(kubectl -n iverson create token iverson-admin-automation --audience iverson-authentik-admin-automation --duration 10m)" -H 'X-Iverson-Public-Ingress: 1')"
SEC="$(kubectl -n iverson get secret iverson-authentik-blueprints-service-clients -o jsonpath='{.data.admin-automation\.yaml}' | base64 -d | python3 -c 'import sys,re; print(re.search(r"client_secret: (\S+)", sys.stdin.read()).group(1))')"
echo "aa blueprint secret: $(curl -s -H 'Host: iverson-authentik:9000' http://127.0.0.1:19000/application/o/token/ -d grant_type=client_credentials -d client_id="$CID" --data-urlencode client_secret="$SEC" -d scope="$SCOPE" | python3 -c 'import sys,json; d=json.load(sys.stdin); print("MINTED" if "access_token" in d else "REFUSED "+d.get("error",""))')"
```

Expected:
- `orch pod-bound 10m: MINTED goauthentik.io/api`;
- `orch unbound 10m:   REFUSED invalid_grant`;
- `orch pod-bound 1h:  REFUSED invalid_grant`;
- `aa 10m:` `MINTED` with a scope holding `admin`, `schema_admin` and `tenant_id_admin`;
- `aa 1h:` and `aa 10m, public:` `REFUSED invalid_grant`;
- `aa blueprint secret: REFUSED invalid_grant`.

Now `CreateTenant` through the API, with an Operator token minted from the ServiceAccount token. The API itself calls Authentik with its federated orchestrator token. Build the scratch console (round-10 C1/C2's), pointing it at this worktree:

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh
mkdir -p $LIVE/createtenant && cd $LIVE/createtenant
cat > createtenant.csproj <<'X'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="/home/ben/repositories/Iverson/.worktrees/csr11-identity-plane/Iverson.Clients/DotNet/Iverson.Client.Core/Iverson.Client.Core.csproj" /></ItemGroup></Project>
X
cat > Program.cs <<'X'
// args: grpcUrl tenantId ; env TOKEN (admin-automation access token). Prints only the link's host and path.
using Grpc.Core; using Grpc.Net.Client; using Iverson.Client.Contracts;
using var channel = GrpcChannel.ForAddress(args[0]);
var client = new TenantLifecycleGrpcService.TenantLifecycleGrpcServiceClient(channel);
var tenant = await client.CreateTenantAsync(new CreateTenantRequest { TenantId = args[1], DisplayName = "CSR11 B live check",
    AdminUsername = args[1] + "-admin", AdminEmail = args[1] + "-admin@iverson.local" },
    new Metadata { { "authorization", $"Bearer {Environment.GetEnvironmentVariable("TOKEN")}" } });
var link = new Uri(tenant.AdminRecoveryLink);
Console.WriteLine($"link host={link.Scheme}://{link.Authority} path={link.AbsolutePath}");
X
systemd-run --user --scope -q -p MemoryMax=3G dotnet build -c Release 2>&1 | command grep -E "error|Build succeeded" | head -3
( CID="$(kubectl -n iverson get secret iverson-authentik-admin-automation-client -o jsonpath='{.data.client-id}' | base64 -d)"
  export TOKEN="$(curl -s -H 'Host: iverson-authentik:9000' http://127.0.0.1:19000/application/o/token/ -d grant_type=client_credentials -d client_id="$CID" \
    -d client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer \
    --data-urlencode client_assertion="$(kubectl -n iverson create token iverson-admin-automation --audience iverson-authentik-admin-automation --duration 10m)" \
    -d scope="admin schema_admin tenant_id_admin" | python3 -c 'import sys,json; print(json.load(sys.stdin)["access_token"])')"
  dotnet $LIVE/createtenant/bin/Release/net10.0/createtenant.dll http://127.0.0.1:18080 csr11b-live-check )
```

Expected: `Build succeeded`, then `link host=http://authentik.iverson.local path=/if/flow/iverson-recovery/`.

- [ ] **Step 10: Tear down and diff the user's state.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-b-sdd/env.sh
kill $(cat $LIVE/pf-ak.pid) $(cat $LIVE/pf-api.pid) 2>/dev/null
kind delete cluster --name iverson; kind get clusters
docker rmi docker.io/library/iverson-api:csr11b localhost/iverson-api:csr11b >/dev/null 2>&1; true
docker ps -a --format '{{.Names}}' | sort | diff $LIVE/before-containers.txt - && echo containers-unchanged
docker volume ls -q | sort | diff $LIVE/before-volumes.txt - && echo volumes-unchanged
docker network ls --format '{{.Name}}' | sort | diff $LIVE/before-networks.txt - && echo networks-unchanged
docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort | comm -23 $LIVE/before-images.txt - | head && echo no-image-removed-or-repointed
git -C $WT status --porcelain
```

Expected:
- `No kind clusters found`;
- `containers-unchanged`, `volumes-unchanged` and `networks-unchanged`;
- `no-image-removed-or-repointed`;
- an empty `git status`.

Newly pulled base images (the kind node, Calico, the TEI base) are acceptable; list them. Remove any `*:csr11b` or `iverson-tei-model:*` image this task built, and the `csr11b-*` compose images, if any.

## Known issues inherited from spec

- **Copied pod token window (#7, user decision 2026-10-09):** a token copied from a pod that is then deleted remains usable until its 600 s life ends; a live TokenReview (~15 s) was declined to avoid new RBAC and relay surface.
- **Cloud edge behaviour is unverified live:** the ALB annotation, Cloud Armor attachment and AGIC per-path WAF policy are documentation-level; only `terraform validate` and render checks run.
- **Azure `WAF_v2` (user decision 2026-10-11):** costs more; gRPC through the AGIC gateway already returns 400 (pre-existing); on the Authentik host the per-path policy runs OWASP in Prevention, so a managed-rule false positive can refuse a login there.
- **Reputation lockout:** a shared NAT can be refused for up to 24 h (expiry counted from the first failure) once its failed attempts sum to five (successful logins don't offset them; choosing an MFA setup stage counts one until the enrolment completes, so abandoned enrolments add up), until an operator clears the score.
- **Global `add_user`:** orchestrator rights still include it — the Authentik permission limit accepted in round 10.
- **Plaintext Authentik `:9080` hop and GCP HTTP:80** belong to sub-project C (#4, #6).
- **First apply on a fresh install:** the worker's first automatic apply of `reputation.yaml` and `superusers.yaml` can run before the stock authentication flow exists, which fails the file until the hourly re-apply; the runbook applies the changed files explicitly after an install or upgrade.
