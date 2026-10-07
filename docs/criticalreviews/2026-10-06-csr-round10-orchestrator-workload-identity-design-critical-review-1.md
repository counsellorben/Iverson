# Critical Design Review: 2026-10-06-csr-round10-orchestrator-workload-identity-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-10-06-csr-round10-orchestrator-workload-identity-design.md`
**Artifact HEAD at review:** 19c2548e21aa74536452a79432480e48c4330ca3
**Verified Assumptions section:** present

## 0. Coverage enumeration

**Probe key.** Every probe used throwaway resources, and all of them were deleted afterwards.
- **P1.** A kind cluster `cdr-c2` (node v1.31.0) with Helm v3.16.4. A two-Secret chart was installed: one Secret annotated `helm.sh/resource-policy: keep`, named like the orchestrator token Secret, and one plain Secret. `helm upgrade` then removed both from the template. Result: the keep Secret was still present after the upgrade, the plain Secret had been deleted, and the upgraded release manifest rendered 0 Secrets.
- **P2.** The same cluster. A pod built as §2c describes, inside the worker's own pod securityContext (`runAsUser: 1000`, `runAsGroup: 1000`, `fsGroup: 1000`, `automountServiceAccountToken: false`):
  - a native-sidecar relay, `registry.k8s.io/kubectl:v1.37.1`, running as uid 65532 with §2c's args, its exec startup probe, `readOnlyRootFilesystem` and the 64Mi limit;
  - the projected volume (token 3600 s, `kube-root-ca.crt`, namespace) at the in-cluster path;
  - a main container running as uid 1000.

  Result: relay restarts 0. The main container's first request returned `{"keys":[{"use":"sig","kty":"RSA","kid":"iJX7OTebJL6-…","alg":"RS256",…`. On the node, the token file was `-rw-r----- 0:1000`, and `ca.crt` and `namespace` were `0644 0:1000`.
- **P3.** An isolated Authentik 2026.5.3 compose project, `cdrc2p`, with its own Postgres and server. Workers:
  - `cdr-worker-relay` shares a network namespace with a container that serves a probe JWKS on `127.0.0.1:8001`;
  - `cdr-worker-norelay` was added for P4 and has nothing on `127.0.0.1:8001`.

  Blueprint `cdr/gate.yaml` holds: group `iverson-admin-orchestrators`; the §2a username mapping; an `openidconnect` source with a static `oidc_jwks` (probe RSA key, kid `cdr-probe-kid`); the §2a provider; the application with `policy_engine_mode: all`; the in-cluster-only policy at order 1; and, at order 0, the workload policy **with §2.2's proposed gate**:

  ```
  sub == "system:serviceaccount:cdr:cdr-api"
  and "iverson-authentik-orchestrator" in aud
  and ak_user_by(username=…) is not None
  and ak_is_group_member(u, name="iverson-admin-orchestrators")
  ```

  Assertions were minted from the probe key and exchanged at `/application/o/token/` with §1b's exact form fields. Results:
  - **(a)** Correct `sub`/`aud`, user absent: `{'error': 'invalid_grant'}`, workload users 0.
  - **(b)** User created through the API, with no group: `invalid_grant`.
  - **(c)** User added to the group: `{'access_token': …, 'expires_in': 300}`. The user's groups were `['iverson-admin-orchestrators']` and its attributes were `{'goauthentik.io/user/generated': True}`, with no `expires`.
  - **(d)** Wrong `sub` (`system:serviceaccount:cdr:other`), correct `aud`, user in the group: `invalid_grant`.
  - **(control)** User deleted, workload policy patched to the spec's ungated expression (`sub`/`aud` only): the exchange returned 200 and created the user with groups `[]` and attributes `{expires: …, generated: True}`.
- **P4.** P3's stack with both workers running. Blueprint `cdr/fed.yaml` holds a source with `oidc_jwks_url: http://127.0.0.1:8001/openid/v1/jwks` and a sentinel group. Twelve applies were triggered through `POST /api/v3/managed/blueprints/<pk>/apply/`, which dispatches to the shared queue (`blueprints/api.py:215`). The instance status after each apply was:

  `error, successful, error, successful, error, successful, error, successful, successful, error, successful, error`

  That is 6 `error` and 6 `successful`. `cdr-worker-norelay` logged 12 `apply_blueprint` lines, which is 6 tasks (one start and one finish each).
- **P5.** P3's stack restarted with the spec's ungated policy, to run §2.2's chain. A stand-in for `service-clients.yaml`, `later/svc.yaml`, holds only the workload user entry, with `groups` and no `attributes`. The assertion TTL was set to 60 s; that is the only deviation from 600 s, and the mechanism is the same. The steps:
  - **step0.** Exchange before pre-creation → 200. User pk 5, groups `[]`, attributes `{expires, generated}`.
  - **step1.** `svc.yaml` copied in; the file watcher applied it (`successful`) → groups `['iverson-admin-orchestrators']`, and `expires` was **retained**.
  - **step2.** At the 10:39 UTC `clean_temporary_users` run → workload users 0.
  - **step3.** The next exchange → 200. User pk 6, groups `[]`, attributes `{expires, generated}`.
  - **step4.** The stand-in instance's `last_applied_hash` (`56f77eebacb54df5…`) equalled the file's sha512 (`56f77eebacb54df5…`), and `last_applied` was unchanged. A manual discovery dispatch from `ak shell` was not observed to execute, so step4 rests on that hash equality plus the `blueprints/v1/tasks.py:185` conditional.
- **R.** Source reads of Authentik 2026.5.3, copied out of the image:
  - `blueprints/v1/tasks.py`, `blueprints/models.py`, `blueprints/apps.py`, `blueprints/api.py` and `blueprints/management/commands/apply_blueprint.py`;
  - `core/tasks.py` and `core/apps.py`;
  - `providers/oauth2/views/token.py`;
  - `sources/oauth/{api/source.py,tasks.py,apps.py}`;
  - `lib/expression/evaluator.py` and `policies/expression/evaluator.py`;
  - `tasks/broker.py` and `tasks/schedules/apps.py`.

### Sections

| # | Section | Disposition |
|---|---|---|
| S1 | Header, Goal, Decisions, Global constraints | ok. **Not load-bearing**: framing and user decisions only. The decisions (every Helm profile; the relay is option A; compose stays static) match §1c, §2c and §3a. The static-only cloud constraint is the user's acceptance of unverified cloud runtime behaviour (see §1, span U8) |
| S2 | §1a Two modes | ok. **Not load-bearing as a section row**: the guard rule itself is row R1, which carries the evidence |
| S3 | §1b Federated token handler | ok [existence]. The form fields match VA5 and VA19, and P3 exchanged with these exact fields. "Fail just as they do today" holds: `CreateTenant` runs a bare `catch { DeleteAsync; throw; }` (`TenantLifecycleGrpcService.cs:43-47`), which rethrows any exception type. Omitting the status retry is consistent with VA7 |
| S4 | §1c API pod | ok [presence]. The API pod is a single container with `runAsUser`/`runAsGroup`/`fsGroup` 1000 (`charts/api/templates/deployment.yaml:62-70`), and P2 shows the kubelet writes the projected token as 0640, group fsGroup. `expirationSeconds: 600` is the API's minimum |
| S5 | §2 intro (pinning by policy) | ok [existence]: VA13 (`token.py:418` `verify_aud: False`; policy at `:492` before user creation at `:494`) |
| S6 | §2a Federation blueprint | placement and rendering → §2.3; the pre-created-user dependency → §2.2; the object rows otherwise ok (R2, R3, R4, R12) |
| S7 | §2b `service-clients.yaml` changes | "Helm deletes the Secrets on upgrade" → §2.1; "It must be pre-created", with nothing ordering it → §2.2. The `state: absent` removal and no re-granting: ok (VA17). The comment-block cite `:675-682` is the block at `:676-683`: non-load-bearing |
| S8 | §2c JWKS relay | ok [compat]: P2 ran §2c's container spec inside the worker's pod securityContext. 0 restarts, JWKS served to a uid-1000 sibling, token readable through the fsGroup |
| S9 | §2d NetworkPolicy | ok [existence]: carried by R9 (selector; `deployment-worker.yaml:13` vs the server label at `networkpolicies.yaml:578-583`) and A11 (helper `_helpers.tpl:71-81`; laptop CIDRs `values-laptop.yaml:20`) |
| S10 | §3a Compose | "Nothing changes in compose" → §2.3 |
| S11 | §3b Tests | integration objects → §2.5. The kind live check's "both orchestrator Secrets … absent" passes only on the fresh install it performs and cannot see the upgrade orphan → §2.1. Unit test lists: ok [existence], not load-bearing (test enumeration, read against spec §1b/§1a) |
| S12 | §3c Upgrading | the window bound → §2.4; the runbook's "confirm both Secrets are gone" → §2.1 |
| S13 | Verified assumptions | §1 |
| S14 | Known issues | **Not load-bearing**: a list of accepted risks. Each item was read against the design. "Relay down while the blueprint applies" does not name the upgrade's coexisting relay-less worker, and "Rollout window" restates §3c → §2.4 |

### Rules and operands

**R1. Startup guard: exactly one mode** (`AdminToken` vs `OrchestratorAssertionPath`, plus `OrchestratorClientId`).

Population: every runtime host, plus every test that runs `AuthentikTrust.ValidateStartup` or configures the static or federated mode. Searched with `command grep -rn --exclude='.env*' "ValidateStartup\|Authentik:AdminToken\|Authentik__AdminToken\|OrchestratorAssertionPath\|AdminToken =>\|HttpClientName"` over `Iverson.Server`, excluding `bin`/`obj`. Disposition [totality], per member:

- **Runtime hosts:**
  - compose `iverson-api` (`ASPNETCORE_ENVIRONMENT=Production`, `docker-compose.yml:491`; sets only `AdminToken`, at `:534`): **over: ok**, passes in static mode.
  - Helm api after the change (federated keys only): **over: ok**.
  - Compose `iverson-worker` (`WORKLOAD_ROLE=worker`, `:620`; `AdminToken` at `:645`): **under: ok**, the guard is skipped (`AuthentikTrust.cs:68`).
  - Helm worker (no Authentik settings): skipped.
  - grep found no other `AdminToken` setter.
- **`AuthentikTrustTests` cases:**
  - `Guard_RefusesTheApiRole_OutsideDevelopment` (12 `InlineData`, `:181-196`), `Guard_RefusesACaFileThatIsNotAPemCertificate` (`:198-205`) and `Guard_PassesAFullyConfiguredApiRole` (`:207-209`): `GoodConfig()` (`:159-172`) sets neither mode key, so under the new rule these throw the mode error. The dropped candidate is the fixture update (`GoodConfig` gains a mode key), which is plan-level (CIR), not a design defect.
  - `Guard_SkipsDevelopment` (`:211-213`) and `Guard_SkipsTheWorkerRole` (`:215-217`): ok, the skip is preserved.
  - The host tests `BothSchemes_*` (`:219-255`) and `IdpAdminClient_ReachesAuthentikThroughTheCaTrustingHandler` (`:257-268`) run on `AuthTestWebApplicationFactory`. That factory has no `UseEnvironment` (grep: 0 hits in `Iverson.Api.Tests`), and WebApplicationFactory's default is Development, so the guard is skipped: ok. `:264` unwraps `DelegatingHandler`s, so a federated handler would not break it, and the host sets no assertion path in any case: ok.
- **Fixture and other test clients:**
  - `AuthentikContainerFixture.AdminToken` (`:111`) and `OrchestratorToken`, consumed by `AuthentikRecoveryFlowIntegrationTests`. That test registers its own named client with a static bearer (`:68`) and never runs `Program.cs` or the guard: ok, static path unchanged (§3a).
  - `AuthentikAdminClientTests:72` substitutes `IHttpClientFactory` and never runs the guard: ok.

Both directions: **over: ok** (no valid host is rejected). **under: ok** [existence]: both-set and neither-set are each a stated reject in §1a.

| # | Rule | Disposition |
|---|---|---|
| R2 | Expression policy `iverson-admin-orchestrator-workload` (`sub ==`, `aud` contains) | **over: ok** [compat]. P3(d): a wrong-`sub` token signed by the trusted key got `invalid_grant`. VA20: the API SA's default-audience token and a `somebody-else` audience got `invalid_grant`. The namespace in `sub` separates same-named releases. **under: ok** [compat]. P3(c): the right `sub` with `aud` as a one-element list was admitted. The `sub` shape comes from VA30. The Helm-rendered values depend on a rendering path that the design lacks → §2.3 |
| R3 | Expression policy `…-in-cluster-only` (marker header absent) | **over: ok** [absence]. Target: the 8443 server block, `configmap-tls-proxy.yaml:10-19`. `command grep -n --exclude='.env*' "X-Iverson-Public-Ingress\|server {\|listen" configmap-tls-proxy.yaml` returns `10: server {`, `11: listen 8443 ssl;`, `25: server {`, `26: listen 9080;`, `29: proxy_set_header X-Iverson-Public-Ingress 1;`. The only marker line (29) lies inside the 9080 block (`:25-38`), and the 8443 block (`:10-20`) has none. **under: ok** [existence]: the 9080 server-level `proxy_set_header` (`:29`) covers `/application/` (`:30`; VA15) |
| R4 | Identity rule: the username mapping sends every accepted assertion to one fixed user | **over-merge: ok** [compat]. P3(d): a second `sub` signed by the same key is refused before the mapping runs, and P3(c) and (d) left exactly 1 workload user. Only one identity reaches the mapping. **under-merge: n/a** (one identity). The rule's mechanism needs that user to exist before the first exchange → §2.2 |
| R5 | Access-token cache: refresh with < 60 s of `expires_in` left; one semaphore | **Not load-bearing at design altitude**: the handler's caching and serialisation failure modes are implementation-time (CIR scope). **over (stale reuse): ok**: the 300 s lifetime (P3(c) `expires_in: 300`) is independent of the assertion, and the file is re-read on every mint. **under (extra mints): ok**: the semaphore serialises minting |
| R6 | Eligibility predicate `clean_temporary_users` (`attributes.generated` ∧ `attributes.expires`, `core/tasks.py:51-65`, every 5 min `core/apps.py:62-63`) × the producers of user `iverson-admin-orchestrator-workload` | Matrix. **(a)** Helm `service-clients.yaml` entry. over: ok [compat], since P3(c) shows a pre-created user keeps `{generated: True}` with no `expires` after exchanges, so it is never selected (VA12). under: n/a, nothing it should delete. **(b)** The exchange's create path (`token.py:577-578`, `if created and exp`). over: → §2.2 [compat], since P5 step2 deleted the only orchestrator identity even after it gained the group. under: ok [compat], since every exchange-created user carries both attributes (P3 control; P5 step0 and step3), so none escapes the predicate. **(c)** The §3b integration fixture: as specified it creates no user, so the exchange creates one as in (b). over: → §2.5, the fixture's identity is an expiring, groupless exchange-created user. under: ok [existence], since after §2.5's fix the fixture pre-creates the user, which then matches producer (a) and has no `expires`. **(d)** Compose has no federation objects → n/a |
| R7 | Blueprint all-or-nothing, failed apply retried at hourly discovery (VA27) | **over: ok** [compat]: VA27's probe (JWKS server stopped, 3-entry blueprint, neither sentinel created) and P4 (status `error` on the relay-less worker's applies). **under: → §2.4**: the retry, and the first apply, run in whichever worker takes the task, and the design assumes that worker has the relay (P4) |
| R8 | `kubectl proxy` `--accept-paths` / `--reject-methods` | **over: ok** [compat]: VA24's kind probe (`/api` and secrets 403). **under: ok** [compat]: P2 served `/openid/v1/jwks` through the exact §2c args |
| R9 | NetworkPolicy selector `app: <release>-authentik-worker` | **over: ok** [existence]: server pods carry `app: <release>-authentik-server`, a distinct value (`networkpolicies.yaml:578-583` lists both). **under: ok** [existence]: the relay is a container of the worker pod (`deployment-worker.yaml:13`) |
| R10 | Blueprint discovery scope: Authentik loads every `*.yaml` under its blueprints dir recursively (`blueprints/v1/tasks.py:126` `rglob("**/*.yaml")`) × the mounts and copies of `charts/authentik/blueprints/` | Matrix, enumerated by the search in §2.3's Evidence. Helm ConfigMap glob (`blueprints-configmap.yaml:12`), consumed by the Helm worker (`deployment-worker.yaml:84`) → intended, ok. The same ConfigMap in the Helm server (`deployment-server.yaml:141`) → n/a, because the server applies no blueprints (VA10; fixture doc `AuthentikContainerFixture.cs:36-39`). Compose `authentik-server` (`docker-compose.yml:378`) → n/a, same reason. Compose `authentik-worker` (`:421`) → **over: → §2.3**. Fixture server (`:253`) → n/a. Fixture worker (`:253`, path `:271`) → **over: → §2.3**. **under: ok** [existence]: the Helm worker mounts the ConfigMap at `/blueprints/custom` (`deployment-worker.yaml:82-85`) |
| R11 | Helm upgrade deletes resources that the new chart no longer renders × the resources this design removes | Matrix. `<release>-authentik-admin-orchestrator-user` Secret (`resource-policy: keep`, `secret-service-clients.yaml:191-199`) → §2.1. `<release>-authentik-admin-orchestrator-token` Secret (keep, `:201-209`) → §2.1. The `Authentik__AdminToken` env entry is a template field, not a resource → ok. The blueprint Secret is modified, not removed → ok. Restatements: §2b, §3b (kind check) and §3c (runbook) → §2.1. **over: ok / under: → §2.1** [compat] (P1) |
| R12 | `policy_engine_mode: all` | **over: ok** [compat]: VA14 shows a correct token plus the public-ingress marker is rejected (`invalid_grant`). **under: ok** [compat]: VA5/VA6 show a valid token without the marker admitted (200 with `access_token`), and P3(c) shows it in round under `all` with both bindings |

### Data-flow arrows

| # | Arrow → consuming operation | Disposition |
|---|---|---|
| A1 | kubelet projected token → handler file read in the API container | ok [presence]: P2's node listing `-rw-r----- 0:1000 token` under fsGroup 1000. The API container runs as uid/gid 1000 with fsGroup 1000 (`deployment.yaml:62-66`) |
| A2 | Handler → `POST /application/o/token/` (`grant_type`, `client_id`, `client_assertion_type`, `client_assertion`, `scope`) → TokenView | ok [compat]: P3 posted exactly these five fields and got `access_token` and `expires_in` (P3(c)). Read side: `token.py:331-334` dispatches on the assertion type and `:458-470` reads the assertion |
| A3 | TokenView → `kid` lookup in the persisted `source.oidc_jwks` (**persistence boundary**) | ok [presence]: VA9 (the persisted `oidc_jwks` held the kind key ID) against the filter at `token.py:403` `oidc_jwks__keys__contains=[{"kid": …}]`. P3 matched kid `cdr-probe-kid` from a blueprint-persisted static `oidc_jwks` |
| A4a | JWKS fetch, caller 1: blueprint apply in a worker with the relay | ok [compat]: VA9, VA10, P2, and P4's `successful` applies |
| A4b | JWKS fetch, caller 2: the 3-hourly `update_well_known_jwks` | ok [existence]: VA21. In a relay-less worker it logs the failure and keeps the old JWKS (`sources/oauth/tasks.py:63-71`) |
| A4c | JWKS fetch, caller 3: a blueprint apply that the old, relay-less worker executes during the RollingUpdate | → §2.4 (P4) |
| A4d | JWKS fetch, caller 4: the admin UI source form (server) | ok [existence]. **Not load-bearing**: the spec's Known issues already lists it as unused, since the source is blueprint-managed |
| A5 | Policy engine context → expression (`oauth_jwt`, `http_request`) | ok [compat]: P3(a)–(d) evaluated `request.context["oauth_jwt"]` and `ak_user_by` inside the token view's policy run (`token.py:145-160`, `:492`) |
| A6 | Exchange → user resolution (`update_or_create` on the mapped username) | user pre-created: ok [compat], P3(c) (groups kept, no `expires`; VA12). User absent: → §2.2 (P3 control, P5) |
| A7 | Access token → `IdpAdminClient` admin calls | ok [compat]: VA6 |
| A8 | Helm values (namespace, release) → blueprint text → ConfigMap → worker mount (**persistence boundary**) | → §2.3. The ConfigMap emits `.Files.Get` bytes unchanged for every file except recovery-flow's one substitution (`blueprints-configmap.yaml:6-19`) |
| A9 | Chart change → cluster Secrets (`helm upgrade` deletion) | → §2.1 (P1) |
| A10 | ConfigMap and Secret updates → worker mounts → `apply_blueprint`, which reads the **executing pod's** local file (`blueprints/models.py:104-114`) | → §2.4. It also feeds §2.2: `service-clients.yaml` can apply late |
| A11 | Relay → API server, through the NetworkPolicy | ok [existence]: the helper renders `apiServerCidrs` on 443/6443 (`_helpers.tpl:71-81`), and the laptop overlay sets `172.18.0.0/16` and `10.89.0.0/16` (`values-laptop.yaml:20`) |
| A12a | Exchange caller: the runtime handler (Helm profiles) | ok [compat], apart from §2.2 and §2.4. Its operations are rows A1, A2, A3, A5, A6 and A7, each dispositioned above. Exchange plus admin calls: VA5/VA6 and P3(c) |
| A12b | Exchange caller: the §3b integration test on `AuthentikContainerFixture` | → §2.5 |
| A12c | Exchange caller: the kind live check (full chart, fresh install) | ok [existence]. **Not load-bearing**: a verification step, not runtime behaviour. Its objects are the chart's own (§2a/§2b). It is blind to §2.1 (see S11) |

## 1. Verified-assumptions cross-check

1. Holds: `Program.cs:387-395` `AddHttpClient(...)…ConfigurePrimaryHttpMessageHandler(...)`.
2. Holds: `AuthentikTrust.cs:68` early return, called at `Program.cs:466`.
3. Holds for production code. The cited grep missed three test-side uses: `AuthentikAdminClientTests.cs:72`, `AuthentikTrustTests.cs:264` and `AuthentikRecoveryFlowIntegrationTests.cs:68`. Each builds or unwraps its own client (see R1), and the `:264` check walks through `DelegatingHandler`s.
4. Holds: `configmap-tls-proxy.yaml:10-19`.
5. Holds: probe (ground truth); P3(c) reproduced `expires_in: 300`.
6. Holds: probe (ground truth).
7. Holds: probe (ground truth).
8. Holds: `AuthentikContainerFixture.cs:111` (`AdminToken => BootstrapToken`) and `:244` (`AUTHENTIK_BOOTSTRAP_TOKEN`).
9. Holds: `sources/oauth/api/source.py:107-117`.
10. Holds: `blueprints/v1/tasks.py:191-236`.
11. Holds: `token.py:559` `mapped.get("username", …)`.
12. Holds: `core/tasks.py:51-65`; `token.py:577-578` sets `expires` only on create (P3 control).
13. Holds: `token.py:381-430`, `:492` before `:494`; P3(a) and (d) created no user.
14. Holds: probe (ground truth).
15. Holds: `configmap-tls-proxy.yaml:25-30`.
16. Holds: probe (ground truth).
17. Holds: probe (ground truth).
18. Holds: probe (ground truth).
19. Holds: probe (ground truth); P3 exchanged with no client secret.
20. Holds: probe (ground truth).
21. Holds: `sources/oauth/apps.py:56` (`*/3`); `sources/oauth/tasks.py:63-71` (`continue` on failure).
22. Holds: kind probe (ground truth).
23. Holds: kind probe (ground truth).
24. Holds, and re-confirmed in round at `v1.37.1` inside the worker's pod securityContext (P2).
25. Holds: probe (ground truth).
26. Holds: kind probe (ground truth); P2's exec startup probe also passed.
27. Holds: `blueprints/v1/tasks.py:185` (re-apply on hash mismatch), `:211-223` (hash updated only on success); `blueprints/apps.py:170-171` (hourly, `send_on_startup`).
28. Holds: `_helpers.tpl:71-81`; `networkpolicies.yaml:193,248`.
29. Holds: `deployment-worker.yaml:9,13`.
30. Holds: `charts/api/templates/deployment.yaml:10-13,51`.
31. Holds: D VA49. `.github/dependabot.yml` helm `directories` includes `/Iverson.Server/deploy/helm/iverson/charts/*`.
32. Holds: `networkpolicies.yaml:545-550`.
33. Holds: grep for `admin-orchestrator` outside `docs/` finds the two Secret names only in `charts/api/templates/deployment.yaml:193` and `secret-service-clients.yaml`; compose and the fixture use env-var names.
34. Holds: grep finds no orchestrator-token use in `Iverson.LoadTest`.
35. Holds: `modules/cluster-{aws,azure}/variables.tf:13` and `cluster-gcp/variables.tf:15` default `"1.30"`.
36. Holds: external documentation (not re-fetched).

**Span check.** Design dependencies that no listed item covers:
- **U1.** "The workload user exists before the first exchange." Uncovered, and false under independent blueprint apply → §2.2.
- **U2.** "The federation blueprint's apply runs in a pod that has the relay." Uncovered for the RollingUpdate's coexisting old worker → §2.4.
- **U3.** "Helm removes the two orchestrator Secrets on upgrade." Uncovered, and false (P1) → §2.1.
- **U4.** "Only Helm reads the new blueprint file." Uncovered, and false (compose and fixture mounts) → §2.3.
- **U5.** "The blueprint text carries the Helm-rendered namespace and release." Uncovered, and the ConfigMap does not render it → §2.3.
- **U6.** "The relay works inside the worker's pod securityContext, with fsGroup 1000 and a second container at uid 1000." VA24 does not state the pod shape. Verified in round by P2 → ok.
- **U7.** "A newer `kubectl` relay works against the clusters' older API servers." Verified in round: P2 ran `v1.37.1` against a `v1.31.0` server → ok.
- **U8.** "The managed clouds' API servers serve `/openid/v1/jwks` with the keys that sign projected tokens." Not probeable here. The Global constraints record the user's decision that cloud profiles get static verification only, so this is an accepted risk, not an unsurfaced one.
- **U9.** "The integration test's federated identity holds the orchestrator role." Uncovered → §2.5.

## 2. Literal-wrongness findings

### 2.1 Both orchestrator Secrets survive the upgrade, because they are annotated `helm.sh/resource-policy: keep`

**Description.** §2b states "Helm deletes the Secrets on upgrade". Both Secrets carry `"helm.sh/resource-policy": keep` (`secret-service-clients.yaml:191-199` and `:201-209`). That annotation makes Helm skip deleting a resource when an upgrade stops rendering it. On every upgraded cluster both Secrets therefore remain. Their contents (a token key and a password) are dead once the blueprint's `state: absent` entries delete the Authentik token and user, but the Secrets still exist.

This breaks two steps:
- the §3c runbook step "confirm that both orchestrator Secrets are gone", which fails on every upgraded cluster;
- the §3b kind live check "both orchestrator Secrets … are absent". It runs on a fresh install, where the Secrets were never created, so it passes vacuously and cannot detect the orphan.

A fresh install is unaffected.

**Evidence.** [compat] P1 (Helm v3.16.4, kind v1.31.0): after `helm upgrade` dropped both Secrets from the template, the keep-annotated Secret was still present, the plain Secret was deleted, and the upgraded manifest rendered 0 Secrets.

**Proposed fix.**
- §2b: replace "Helm deletes the Secrets on upgrade" with: "Both Secrets carry `helm.sh/resource-policy: keep`, so an upgrade leaves them in place; the cutover runbook deletes `<release>-authentik-admin-orchestrator-token` and `<release>-authentik-admin-orchestrator-user` after the upgrade. A fresh install never creates them."
- §3c: add the delete as a runbook step, ahead of "confirm that both orchestrator Secrets are gone".
- §3b: the kind live check performs an upgrade from the current chart, runs the runbook step, and only then asserts absence. Asserting absence after a fresh install cannot fail.

Evidence:
- [compat] P1, as above: the keep Secret survived the upgrade and the plain Secret did not.
- [negative] grep for `admin-orchestrator` outside `docs/` (excluding `.git`, worktrees, `bin`/`obj`): the two Secret names appear only in `charts/api/templates/deployment.yaml:193` (removed by this design) and `secret-service-clients.yaml`. Nothing else reads them, so deleting them is safe (also VA33).

### 2.2 Nothing orders the first exchange after the workload user's pre-creation, and an early exchange leaves provisioning permanently broken

**Description.** §2b requires `iverson-admin-orchestrator-workload` to be pre-created by `service-clients.yaml`. VA12 shows why: a user that the exchange creates instead gets `attributes.expires` and no group. Nothing in the design makes the first exchange wait for that pre-creation:
- The two blueprints apply independently. Separating them is the point of §2a.
- An upgrade dispatches them as separate apply tasks, and §2.4 shows that either one can be delayed until the hourly discovery.

If the federation objects are live before the user exists, any exchange creates the user with `expires` set to the assertion's `exp` and with no group. The runbook's own confirming `InviteUser` is such an exchange. The resulting access token is refused on every admin call. What follows:
1. When `service-clients.yaml` applies, it adds the group but leaves `expires` in place. None of this chart's blueprint user entries set `attributes`.
2. `clean_temporary_users` runs every 5 minutes and deletes the user once `exp` has passed (`core/tasks.py:51-65`, `core/apps.py:62-63`). The only thing that prevents this is a second exchange that overwrites `attributes` first, and the handler's 5-minute cache delays the next exchange by about 4 minutes.
3. After that deletion, every exchange recreates a groupless user. `service-clients.yaml` never re-applies, because discovery re-applies only on a content-hash change (`blueprints/v1/tasks.py:185`). Provisioning stays broken until someone intervenes by hand.

**Evidence.**
- [compat] P3 control: with the spec's policy and the user absent, the exchange returned 200 and created a user with groups `[]` and `{expires, generated}`.
- [compat] P5 steps 0–3 ran the chain end to end. step1: the blueprint stand-in added the group, and `expires` was retained. step2: the cleanup run deleted the user. step3: the next exchange recreated it groupless with `expires`.
- [presence] P5 step4: the stand-in instance's `last_applied_hash` equalled the file's sha512.
- [existence] `blueprints/v1/tasks.py:185`: discovery dispatches an apply only when those differ.

**Proposed fix.** Make federation fail closed until the pre-created user exists. The `iverson-admin-orchestrator-workload` expression policy additionally requires that `ak_user_by(username="iverson-admin-orchestrator-workload")` returns a user for which `ak_is_group_member(user, name="iverson-admin-orchestrators")` is true. Policies run before any user is created, so an early exchange gets `invalid_grant` and creates nothing, whichever blueprint applies first. §3b integration gains a case: a correct `sub`/`aud` before the user exists gets `invalid_grant`, and no user is created.

Evidence:
- [compat] P3 ran this gated expression in a live 2026.5.3 token exchange:
  - (a) user absent: `invalid_grant`, workload users 0;
  - (b) user present without the group: `invalid_grant`;
  - (c) user in the group: `access_token`, `expires_in: 300`, no `expires` attribute;
  - (d) wrong `sub`: `invalid_grant`.
- [existence] `token.py:492` (policy) precedes `:494` (user creation), which is why (a) created no user.
- [existence] `lib/expression/evaluator.py:68,71` puts `ak_is_group_member` and `ak_user_by` in the evaluator globals, and `policies/expression/evaluator.py:21` defines `class PolicyEvaluator(BaseEvaluator)`.

### 2.3 The federation blueprint, placed as a file under `charts/authentik/blueprints/`, is loaded by compose and the test fixture, and the ConfigMap cannot render its namespace and release

**Description.** §2a delivers the blueprint "through `templates/blueprints-configmap.yaml` … like the other structural blueprints". That ConfigMap's only content source is `.Files.Glob "blueprints/*.yaml"` (`:12`), so the file must live in `charts/authentik/blueprints/`. That directory has other readers:
- Compose bind-mounts it at `/blueprints/custom` (`docker-compose.yml:378` in `authentik-server`, `:421` in `authentik-worker`; both lines read `- ./deploy/helm/iverson/charts/authentik/blueprints:/blueprints/custom:ro`).
- `AuthentikContainerFixture` mounts the same directory (`:253`, path `:271`).

Authentik loads every `*.yaml` under that directory recursively (`blueprints/v1/tasks.py:126`; the fixture's `compose-only/` file is discovered the same way, as its docs at `:287-291` note). In compose and in the fixture, nothing listens on `127.0.0.1:8001`. The source's JWKS fetch therefore fails validation, and the blueprint stays in ERROR, retried every hour. That contradicts §3a's "Nothing changes in compose".

Separately, the policy row needs the Helm-rendered namespace and release. The ConfigMap emits every file verbatim except for recovery-flow's single substitution (`:6-19`: "every other blueprint is emitted unchanged"), so the design as written gives that file no rendering path.

**Evidence.**
- [negative] The mount population: see the search in the fix's Evidence below.
- [compat] Fail-closed behaviour when the fetch target is absent: P4's six applies executed in the worker with nothing on `127.0.0.1:8001` ended in `error`, and VA27's probe shows a failed blueprint applies none of its entries.
- [existence] `blueprints-configmap.yaml:6-19` (no rendering path).

**Proposed fix.** Render the federation blueprint inline in `blueprints-configmap.yaml`, instead of adding a file under `charts/authentik/blueprints/`. After the `range`, append a templated key `orchestrator-federation.yaml: |` whose body uses `.Release.Namespace` and `.Release.Name` directly, the way `secret-service-clients.yaml` already renders `service-clients.yaml` inline. The worker mounts that ConfigMap at `/blueprints/custom`, so the blueprint's path is `custom/orchestrator-federation.yaml`. Compose and the fixture never see it, so §3a holds as written.

Evidence:
- [negative] The search covered every mount or copy of the blueprints dir, across all file types:

  ```
  command grep -rnI --exclude='.env*' --exclude-dir=.git --exclude-dir=node_modules \
    --exclude-dir=.worktrees --exclude-dir=.claude --exclude-dir=bin --exclude-dir=obj \
    --exclude-dir=docs \
    -E "authentik/blueprints|/blueprints[/:\"' ]|blueprints_dir|BLUEPRINTS_DIR|blueprintsDir" .
  ```

  Volume and mount hits:
  - `docker-compose.yml:378`, `:421`;
  - `deployment-server.yaml:141,144` and `deployment-worker.yaml:84,87` (the Helm ConfigMap and Secret mounts);
  - `AuthentikContainerFixture.cs:253` (bind mount of `:271`'s path).

  Every other hit is a comment or doc string naming `compose-only/service-clients.yaml`: `docker-compose.yml:11`, `Iverson.ClientConformance/TokenBroker.cs:36`, `Iverson.ClientConformance/Program.cs:326`, `deploy/scripts/mint_acting_user_token.py:32`, `AuthentikOrchestratorRoleBlueprintTests.cs:16`, and `.superpowers/` archive notes.

  Dockerfiles and compose files anywhere, found with `find … -iname '*compose*.y*ml' -o -iname 'Dockerfile*'`: `docker-compose.yml`, `.superpowers/rrf-plan-drafting/compose.rrf-bench.yml`, and seven Dockerfiles (AdminUI, Events, Api, Vector, Sql, Launcher, tei/ollama model-image). Running `command grep -c -i blueprint` on each non-main file returned 0.
- [existence] `secret-service-clients.yaml:234-244` is an inline-rendered blueprint with `{{ }}` values. `blueprints-configmap.yaml:12-19` is a `range` that a following templated key can extend.

### 2.4 The upgrade's outage is not bounded by the rollout: the relay-less old worker can take the federation apply, which then waits for the hourly discovery

**Description.** §3c bounds the provisioning failure to "a short window" during the rollout, followed by one confirming `InviteUser`. Several facts break that bound:
- The worker Deployment has `replicas: 1` and no `strategy` (`deployment-worker.yaml:5-9`). The default RollingUpdate therefore keeps the old worker, which has no relay, running until the new one is Ready. Readiness has `initialDelaySeconds: 30` (`:70-75`).
- Authentik's tasks pass through one Postgres-backed queue that every worker consumes (`tasks/broker.py`, `PostgresBroker`).
- Two discoveries can run during the overlap: the new worker's startup discovery (`blueprints/apps.py:170-171`, `send_on_startup=True`) and the old pod's file-watcher discovery (`blueprints/v1/tasks.py:92`). Either can execute in the old worker. A discovery there reads the old pod's mount, which may still be stale.
- An apply reads the executing pod's local file (`blueprints/models.py:104-114`) and fetches the JWKS from that pod's `127.0.0.1:8001`. The old worker has no relay, so `orchestrator-federation.yaml` lands in ERROR or is not seen at all. `service-clients.yaml` can likewise be read from a stale mount.
- Nothing re-applies either blueprint until the next hourly discovery (`blueprints/apps.py:170`), by which time only the new worker remains.

So the outage can last about an hour after the rollout completes. The runbook's confirming `InviteUser` can fail with no recovery step, and without §2.2's fix it can itself be the exchange that creates the groupless user.

**Evidence.**
- [compat] P4: two workers on one broker, one with a loopback JWKS relay and one without. Of 12 queued applies of a blueprint whose source fetches `http://127.0.0.1:8001/openid/v1/jwks`, 6 were executed by the relay-less worker (12 `apply_blueprint` start/finish log lines), and the instance status after each of those was `error`.
- [existence] `deployment-worker.yaml:5-9` has no `strategy` key, so the Kubernetes default RollingUpdate applies, and the readiness delay is at `:70-75`.
- [existence] VA27: a failed instance is re-applied only at discovery.

**Proposed fix.**
- §3c: say that the window ends when `service-clients.yaml` and the federation blueprint have applied in the new worker. That normally happens at the new worker's startup, but it can wait for the next hourly discovery if the old worker took the task.
- Runbook: after the rollout, and before the confirming `InviteUser`, run in the new worker container:

  ```
  ak apply_blueprint custom/service-clients/service-clients.yaml custom/orchestrator-federation.yaml
  ```

  The command is idempotent with the later hourly discovery.

Evidence:
- [existence] `blueprints/management/commands/apply_blueprint.py:20-37` resolves each argument through `BlueprintInstance(path=…).retrieve()` relative to the blueprints dir (`models.py:104-114`), applies the blueprints in argument order, and exits 1 on the first invalid one. VA10 already ran `ak apply_blueprint` in the worker container.
- [existence] The paths:
  - The Helm Secret is mounted at `/blueprints/custom/service-clients` with key `service-clients.yaml` (`deployment-worker.yaml:86-88`, `secret-service-clients.yaml:240`).
  - The blueprints ConfigMap is mounted at `/blueprints/custom` (`deployment-worker.yaml:82-85`), so §2.3's key `orchestrator-federation.yaml` is at `custom/orchestrator-federation.yaml`.
  - The fixture's `custom/compose-only/service-clients.yaml` (`AuthentikContainerFixture.cs:52`) and P3/P4's `cdr/gate.yaml` and `later/svc.yaml` instance paths confirm that paths are relative to `/blueprints`.

### 2.5 The §3b integration test cannot pass as specified: its federated identity has no role

**Description.** The integration test creates "the source … the provider, the application and both policies" and expects `POST users` and `recovery` to succeed with the minted token. The fixture's compose-only blueprint provides the role and the group (`compose-only/service-clients.yaml:342-373`). Its only users are the smoke-test user (`:282`), the bypass user (`:302`) and the static orchestrator (`:374`). The listed objects include neither the source's username property mapping nor a pre-created `iverson-admin-orchestrator-workload`.

The exchange therefore creates a user named `<provider>-<sub>` (`token.py:559`) with no group. That is the role-less identity VA12 and C1 VA27 describe. `POST users` is refused, so the test's success assertions fail.

**Evidence.**
- [existence] `compose-only/service-clients.yaml:282,302,374` are the only user entries, and `token.py:559` is the username fallback.
- [compat] P3 control: an exchange with no pre-created user produced a groupless user.
- [existence] C1 VA27's live check needed a pre-created `<provider>-<sub>` user in `iverson-admin-orchestrators` for exactly this reason. Read: `docs/specs/2026-10-05-csr-round10-identity-plane-design.md:285` (row 27 records the pre-created user in the orchestrators group as part of the passing setup).

**Proposed fix.** The test also creates the username source property mapping, set as the source's `user_property_mappings`, and uses the bootstrap token to pre-create `iverson-admin-orchestrator-workload` as a member of `iverson-admin-orchestrators`, mirroring §2a and §2b. With §2.2's fix, it first asserts that the exchange is refused with no user created while the user is absent.

Evidence:
- [existence] `compose-only/service-clients.yaml:367-373` gives the group the `iverson-admin-orchestrator` role in the fixture.
- [compat] P3 used exactly this set of objects: a mapping on the source, a user created through the API with the bootstrap token, then added to the group. Its exchange got `access_token` in (c) and was refused while the user was absent in (a).

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes.** Address §2.1–§2.5 and then proceed to planning. §2.2 and §2.4 interact: §2.2's policy gate keeps a delayed `service-clients.yaml` apply from producing a groupless identity, and §2.4's runbook step shortens the window during which the gate refuses.

Slot audit: the independent audit reported 10 items (its item 11, no §4, is not a violation for a first round). All 10 were closed in this revision; the re-audit found one more untagged bullet (§2.5), which the controller tagged. Slot audit: pass after 11 fixes (independent audit).
- Every load-bearing `ok` carries a closed-vocabulary tag with its evidence, and the rows that are not load-bearing are marked as such.
- R1's population is enumerated per member.
- R12 and R6 show both directions per cell.
- §2.3's `[negative]` search is widened and shown.
- Every Evidence bullet is tagged.
- All three `UNVERIFIED` items are resolved in round:
  - the §2.2 combined expression, by P3;
  - the §2.4 federation path, now fixed by §2.3's single concrete variant;
  - the §2.4 two-worker claim, by P4.

  No `UNVERIFIED` text remains, so nothing flows to §3.
