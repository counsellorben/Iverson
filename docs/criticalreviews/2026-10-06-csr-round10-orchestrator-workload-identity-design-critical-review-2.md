# Critical Design Review: 2026-10-06-csr-round10-orchestrator-workload-identity-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-10-06-csr-round10-orchestrator-workload-identity-design.md`
**Artifact HEAD at review:** db963636a97679a4562ab6df834fa95b30e7f4c7
**Verified Assumptions section:** present

## 0. Coverage enumeration

This sweep was built before CDR-1 was read. CDR-1 was then read for the never-re-raise rule and for §4.

**Probe key.** Every probe used throwaway resources: a kind cluster `cdr-c2-2` (node v1.31.0, created twice, deleted both times). Nothing else was created, and no existing container, volume, network or image was touched.

- **Q0. Amendment detection.**
  - `git rev-parse 19c2548e:<spec>` gave `f2161189…` and `git hash-object <spec>` gave `5f709775…`. They differ, so the amendment hunk set is `git diff 19c2548e -- <spec>`: 5 diff hunks, +21/−10 lines.
  - Forward window `git log 19c2548e..HEAD -- <spec>`: one commit, `db963636`, "applied 7 fixes from …-critical-review-1.md to …". That is the update skill's in-band shape.
  - `git merge-base --is-ancestor 19c2548e HEAD` is true, so the reverse window is empty.
  - `git status --porcelain -- <spec>` is empty, so the SHA anchor applies.
- **Q1. Relay token reload.** One pod in the worker's pod securityContext (`runAsUser`/`runAsGroup`/`fsGroup` 1000, `automountServiceAccountToken: false`). It held the §2c relay exactly as specified (`registry.k8s.io/kubectl:v1.37.1`, uid 65532, §2c args, exec startup probe, `readOnlyRootFilesystem`, 64Mi limit, projected token + `kube-root-ca.crt` + namespace) and a main container. The only deviation is the token lifetime: 600 s instead of 3600 s, with the same rotation mechanism. Every 30 s the main container logged three status codes:
  - `relay`: a JWKS GET through `127.0.0.1:8001`;
  - `stale`: the API server called directly with a copy of the main container's first token (`iat 1791379607`, `exp 1791380207`);
  - `fresh`: the API server called with the main container's current token file.

  Result: `relay=200` on every line, from `1791379616` to `1791380398`, with relay restarts 0. `stale=200` through `1791380248` and `stale=401` from `1791380278` on, which shows the API server enforcing expiry after its leeway. The kubelet rotated the files at `iat 1791380092`. A relay holding its first token would have turned 401 with `stale`. It did not, so `kubectl proxy` re-reads the rotated token.
- **Q2. Gate, upstream refusing.** Pod `relay-gate` is Q1's pod with one change: the relay's projected token has `audience: not-the-api-server`, so the API server refuses it. Result: phase `Pending`, relay `started=false`, `restarts` 2 at 67 s and 3 at 120 s, main `waiting: PodInitializing`. The event read `Startup probe failed: error: You must be logged in to the server (Unauthorized)`. Control: Q1's pod, which differs only in the audience, went Ready.
- **Q3. Gate, RBAC refusing, and failure after start.** The `system:service-account-issuer-discovery` ClusterRoleBinding was deleted, then pod `relay-rbac` (Q1's spec, correct audience) was created. Result: `Pending`, relay `restarts=2`, main `PodInitializing`. At the same time, Q1's already-started pod stayed Running with main Ready and relay restarts 0, and its relay answered `403` (`relay=403`).
- **Q3b. Gate, API server unreachable.** On a fresh cluster, pod `relay-unreach` is Q1's spec with the relay container's `KUBERNETES_SERVICE_HOST` set to a non-routable `10.255.255.1`. Result: `Pending`, relay `restarts=3`, main `PodInitializing`. The event read `Startup probe failed: command timed out … after 1s`.
- **Q4. Rolling update with a stuck gated pod.**
  - Deployment `w`: `replicas: 1`, no `strategy` (as `deployment-worker.yaml:5-9`). Its template was updated to add Q2's refusing gated relay. The old pod stayed Running and kept logging `OLD`, and the new pod stayed `Pending`.
  - Deployment `w2`: the same, plus a ConfigMap volume. One `kubectl apply` changed the ConfigMap content from `version-one` to `version-two` and the template as for `w`. The old pod read `version-two` from its mount 80 s later (`1791380192`), while the new pod was `Pending` with 5 relay restarts.
- **Q5. Startup-check variants, each run with Q2's refusing upstream and with a correct-audience twin.**

  | Variant | Refusing upstream | Correct audience (loopback GETs) |
  |---|---|---|
  | `relay-noprobe`: §2c without `startupProbe` | relay started, 0 restarts; main started | — |
  | `relay-tcp`: `--address=0.0.0.0`, `tcpSocket: 8001` startup probe | relay started, 0 restarts; main started | `relay-tcp-ok`: `/openid/v1/jwks` 200 |
  | `relay-www`: `--www=/etc --www-prefix=/static/`, `--accept-paths` extended with `^/static/$`, exec probe `get --raw /static/` | relay started, 0 restarts; main started | `relay-www-ok`: jwks 200, `/static/` 200 (a listing of the image's `/etc`), `/api` 403, **`/static/passwd` 200** |
  | `relay-empty`: `--www=/www --www-prefix=/ready/`, `/www` a read-only empty `emptyDir`, exec probe `get --raw /ready/`, `--accept-paths` unchanged | `relay-empty-bad`: relay started, 0 restarts; main Ready; jwks **401**, `/ready/` 200, `/api` 403 | `relay-empty-ok`: jwks 200, `/ready/` 200 (empty listing), `/api` 403, `/ready/x` 404 |

  The `relay-www-ok` row shows that `--accept-paths` does not govern the `--www` handler, because `/static/passwd` was served although only `^/static/$` was accepted.
- **Q6. kube-score.** kube-score 1.19.0, run with the exact flags of `deploy-validate.yml:108-121` on the **unmodified** chart. `values-laptop`: exit 1, 10 CRITICAL. `values-aws` (with its ci-override): exit 1, 13 CRITICAL. The worker is among them, for example "Container Ephemeral Storage Request and Limit".
- **R. Source reads of Authentik 2026.5.3.**
  - `providers/oauth2/views/token.py:145-165, 331-510, 548-579`;
  - `sources/oauth/tasks.py` (whole), `sources/oauth/apps.py:51-58`, `sources/oauth/api/source.py:90-140`;
  - `blueprints/v1/tasks.py:55-248`, `blueprints/apps.py:150-175`, `blueprints/management/commands/apply_blueprint.py`;
  - `lib/expression/evaluator.py:60-80, 138-151`;
  - `core/tasks.py:45-65`.

### Amendment hunks (family c)

| # | Hunk | Disposition |
|---|---|---|
| C0 | Detection | ok [existence]: Q0. The content check is unequal and the only window commit is in-band (`db963636`). Each hunk below is attributed to a CDR-1 fix where one exists. VA41 and the "Managed API servers' JWKS" bullet have no CDR-1 §2 fix text, because they record CDR-1 §1's U6/U7 and U8. Both are still reviewed at fix rigor |
| H1 | §2a lead: inline templated key `orchestrator-federation.yaml` after the `range` (CDR-1 §2.3) | ok [existence]. `blueprints-configmap.yaml:12-19` is a `range` a following key can extend. The worker mounts the ConfigMap at `/blueprints/custom` (`deployment-worker.yaml:82-85`), so the path is `custom/orchestrator-federation.yaml`. Compose (`docker-compose.yml:378,421`) and the fixture (`AuthentikContainerFixture.cs:253,271`) mount only `charts/authentik/blueprints/`, which the new key never enters. Restatements: VA39, the §3c runbook path, §3a: consistent |
| H2 | §2a workload-policy gate (CDR-1 §2.2) | ok [compat]: VA38 (P3 a–d). Re-read `lib/expression/evaluator.py:138-151`: `expr_user_by` returns `None` when no user matches, and `expr_is_group_member` dereferences its argument. The spec's wording "returns a user for which … is true" therefore carries the None check |
| H3 | §2b bullet: "The workload policy (§2a) refuses every exchange until it exists" (restates CDR-1 §2.2) | ok [existence]: `token.py:492` (policy) precedes `:494` (user creation) |
| H4 | §2b: both Secrets keep-annotated; the runbook deletes them (CDR-1 §2.1) | ok [existence]: `secret-service-clients.yaml:196,206`; VA37 |
| H5 | §3b integration sequence (CDR-1 §2.5 + §2.2) | ok [existence]. The fixture applies `compose-only/service-clients.yaml` (`AuthentikContainerFixture.cs:52, 305-340`), which provisions the group and role (CDR-1 cite `compose-only/service-clients.yaml:342-373`), so the bootstrap-token pre-creation has a group to join. Unit order is implementation-level |
| H6 | §3b kind: upgrade from `main`, assert after the runbook deletion (CDR-1 §2.1) | ok. **Not load-bearing**: a verification step |
| H7 | §3c window paragraph and the five-step runbook (CDR-1 §2.4, §2.1) | "That normally happens at the new worker's startup" and "apply both blueprints in the new worker container" assume a new worker that starts → §3.1 (Q2–Q4). The rest is ok [existence]: `apply_blueprint.py` resolves each path through `BlueprintInstance(path=…).retrieve()`, applies them in order and calls `sys_exit(1)` on the first invalid one. The `service-clients` Secret is at `/blueprints/custom/service-clients` (`deployment-worker.yaml:86-88`) |
| H8 | VA37–VA41 | VA37–40: ok [existence], each read against the CDR-1 record it cites (P1, P3/P5, §2.3's mount list, P4) and matching it. VA41: ok [compat], P2 as recorded, and Q1 reproduces the same pod shape for 13 minutes |
| H9 | Known issues: "Rollout window" reworded (CDR-1 §2.4); "Managed API servers' JWKS (unverified)" added (CDR-1 U8) | "Rollout window" leans on the new worker's start → §3.1. The JWKS bullet names the risk but not its reach: under §2c's gate, an API server that refuses or cannot serve the JWKS stops the whole worker from starting (Q3, Q3b) → §3.1 |

### Fix neighbourhoods (family a)

| # | CDR-1 fix | Sites and restatements | Disposition |
|---|---|---|---|
| F1 | §2.1 keep Secrets | §2b (H4); §3b kind (H6); §3c runbook steps 3 and 5; VA37; VA33 | ok [negative]. `command grep -rn --exclude='.env*' -e 'admin-orchestrator-token' -e 'admin-orchestrator-user'` over `scripts`, `Iverson.Server/deploy`, `docs/runbooks` and `Iverson.Server/scripts` hits only `charts/api/templates/deployment.yaml:193` and `secret-service-clients.yaml:95,103,194,204`. The `:671` hit and compose-only `:387` are the Authentik token *identifier*, not the Secret. Nothing else reads them, so the runbook's delete is safe |
| F2 | §2.2 gated policy | §2a policy row (H2); §2b bullet (H3); §3b first check (H5); VA38 | ok [compat]: VA38/P3 |
| F3 | §2.3 inline key | §2a lead (H1); VA39; §3a | ok [negative]. `ls charts/authentik/blueprints` = `compose-only/ mfa-enforcement.yaml operators-group.yaml recovery-flow.yaml saml-provider.yaml`: no federation file, so §3a's "Nothing changes in compose" holds |
| F4 | §2.4 window and runbook apply | §3c (H7); runbook step 2; Known issues "Rollout window" (H9); VA40 | the new-worker dependency → §3.1; the rest ok [existence] (H7's `apply_blueprint.py` and mount reads) |
| F5 | §2.5 integration identity | §3b (H5) | ok [existence] (H5's fixture reads) |

### Intersected fix texts (family b)

| # | Fix text × later discovery | Disposition |
|---|---|---|
| B1 | CDR-1 §2.4 text ("normally … at the new worker's startup"; "run in the new worker container") × Q2–Q4 | → §3.1. When the relay cannot serve at pod start, no new worker container exists. The old worker keeps running and reads the new mounts (Q4) |
| B2 | CDR-1 §2.3 text × the server Deployment also mounting the ConfigMap (`deployment-server.yaml:140-144`) | ok [existence]. **Not load-bearing**: the server applies no blueprints (VA10) |
| B3 | CDR-1 §2.2 gate × Q3 (RBAC refusal) | ok [existence]. **Not load-bearing**: the policy runs in the server's token view (`token.py:492`) and does not touch the relay |
| B4 | VA41 / CDR-1 P2 (point in time) × Q1 | ok [compat]: Q1 extends P2's pod past the first token's expiry; `relay=200` throughout |
| B5 | D's post-review commits on shared surfaces × §2c/§2d: `8bfd179f` (each subchart `values.yaml` is the single image pin), `ea79cf34` (egress helpers trimmed) | ok [existence]. `charts/authentik/values.yaml` already holds nested `tlsProxy.image`/`revokeCrossDb.image`, the shape §2c's `jwksRelay.image` follows. The include's indentation (`\| nindent 4` at `networkpolicies.yaml:193,248`) is not load-bearing at design altitude |

### Sections

| # | Section | Disposition |
|---|---|---|
| S1 | Header, Goal, Decisions, Global constraints | ok. **Not load-bearing**: framing and user decisions. "A, B, C1, D and E are merged and pushed": D landed as non-merge commits on `main` (`4c5a4e9c` … `1f37f1be`), and `iverson.apiServerEgress` exists (`_helpers.tpl:71`) |
| S2 | §1a Two modes | ok. **Not load-bearing as a section row**: the guard rule is R1, which carries the evidence |
| S3 | §1b handler | ok [existence]. Form fields re-read against `token.py:331-334` (assertion-type dispatch) and `:458-470`; the confidential-client secret check (`:176-187`) covers only code, refresh and device grants, consistent with VA19 |
| S4 | §1c API pod | ok [existence]: `charts/api/templates/deployment.yaml:9-13, 51-52` (SA `<release>-api`, automount false); the env to replace is `:191-193` |
| S5 | §2 intro | ok [existence]: VA13 (ground truth), re-read at `token.py:418` `verify_aud: False` |
| S6 | §2a federation blueprint (rationale ¶2: "a relay outage delays only federation") | object rows ok [compat] (H2's VA38 probe; H1's mount reads; R3–R5). The rationale's claim → §3.1 (R8) |
| S7 | §2b (lead: "Removing the static credential must never depend on the relay") | the bullets are ok [existence] (H3's `token.py:492/494`, H4's `:196,206`, F1's grep). The lead's claim → §3.1 (R8) |
| S8 | §2c JWKS relay | container spec ok [compat] (Q1). The startup probe gates the worker on the upstream → §3.1 |
| S9 | §2d NetworkPolicy | ok [existence]: helper `_helpers.tpl:71-81`; the worker label `deployment-worker.yaml:13` is distinct from the server's (`networkpolicies.yaml:571`) |
| S10 | §3a Compose | ok [negative] (F3's listing: no federation file under the mounted dir) |
| S11 | §3b Tests | ok [existence] (H5's fixture reads; H6 is not load-bearing). Unit-test lists are **not load-bearing** |
| S12 | §3c Upgrading | → §3.1 (H7, B1) |
| S13 | Verified assumptions | §1 |
| S14 | Known issues | "Relay down while the blueprint applies": ok [compat] only after start — Q3 (already-started pod): main stays Ready while the relay answers 403, relay restarts 0, and a failed apply is re-applied hourly (R8 T3 (i)–(iii)); before start it is → §3.1; "Rollout window" and "Managed API servers' JWKS" → §3.1 (H9) |

### Rules and operands

| # | Rule | Disposition |
|---|---|---|
| R1 | §1a guard: exactly one mode, plus the client ID in federated mode | Population per member: compose api (`ASPNETCORE_ENVIRONMENT=Production`, `docker-compose.yml:492`; `AdminToken` only) → static, passes; Helm api → federated, passes; compose worker (`:616`) and Helm worker → skipped (`AuthentikTrust.cs:68`); Development hosts → skipped. **over: ok** [totality]: no valid host above is rejected. **under: ok** [existence]: both-set and neither-set are stated rejects. `AuthentikTrustTests.GoodConfig` (`:158-171`) sets no mode key: CDR-1 R1 dropped that as plan-level and it is not re-raised |
| R2 | §1b refresh at < 60 s of `expires_in`; one semaphore | **Not load-bearing** at design altitude (CIR). over (stale reuse): ok, `expires_in: 300` (VA5). under (extra mints): ok, the semaphore |
| R3 | Workload policy: `sub` ==, `aud` contains, gated user | **over: ok** [compat]. VA38 (wrong `sub` refused); VA20 (other audiences refused); the namespace inside `sub` separates same-named releases; the gate refuses an absent or groupless user (VA38 a, b). **under: ok** [compat]: VA38(c) admitted the right `sub` with `aud` as a list |
| R4 | In-cluster-only policy (marker header absent) | **over: ok** [absence]. Search: `command grep -n --exclude='.env*' "X-Iverson-Public-Ingress\|server {\|listen" configmap-tls-proxy.yaml` → `10: server {`, `11: listen 8443 ssl;`, `25: server {`, `26: listen 9080;`, `29: proxy_set_header X-Iverson-Public-Ingress 1;` (plus comment lines 8 and 21). The only marker line, `:29`, lies inside the 9080 block (`:25-38`); the 8443 block (`:10-20`) has none. **under: ok** [existence]: `:29` is server-level and `/application/` (`:30`) sets no `proxy_set_header` of its own, so it inherits the marker |
| R5 | Identity rule: every accepted assertion maps to one fixed user | **over-merge: ok** [compat]: only one `sub` passes R3 (VA38 d). **under-merge: n/a**: one identity |
| R6 | Blueprint discovery scope (`blueprints/v1/tasks.py:122-133` `rglob`, hidden parts skipped) × the dirs mounted into Authentik | **over: ok** [existence]: compose and fixture mount only `charts/authentik/blueprints/` (F3). **under: ok** [existence]: the Helm worker mounts the ConfigMap that holds the inline key (H1). ConfigMap symlink targets under `..data` are skipped as hidden, as for the existing keys |
| R7 | Relay `--accept-paths` / `--reject-methods` | **over: ok** [compat]: `/api` 403 (Q5 `relay-empty-ok`, `relay-www-ok`). **under: ok** [compat]: `/openid/v1/jwks` 200 (Q1, Q5) |
| R8 | The claim "a relay failure delays only federation; `service-clients.yaml` and the static-credential removal are unaffected" (§2a ¶2, §2b lead, Known issues bullet 2) × relay-failure timing × worker function | **Mismatch → §3.1**. The full matrix follows this table |
| R9 | Relay token lifetime (3600 s) × the 3-hourly refresh and hourly re-apply that use the relay | **over (relay keeps a dead token): ok** [compat]: Q1, where the relay answered 200 after the first token's expiry while the stale control got 401. **under: n/a** — the row's rule is "does the relay keep using a token after it has expired"; the only under-inclusion would be the relay dropping a still-valid token, and Q1 shows it served 200 throughout the first token's validity, so there is no under-direction failure to test |
| R10 | §3c window end: "when both blueprints have applied in the new worker" | **over: ok** [existence]: an apply in either worker of `service-clients.yaml` is enough for that half (VA40). **under: → §3.1**: the end condition is unreachable while the new worker cannot start (Q4) |

**R8 matrix: relay-failure timing × worker function, both directions per cell.**

Timing subclasses:
- **T1:** the relay cannot serve the JWKS when a worker pod starts and no other worker runs. Examples are a fresh install, an eviction, a node drain or a pod delete.
- **T2:** the same during a rolling upgrade, while the old worker still runs.
- **T3:** the relay fails after the worker has started.

Functions: **(i)** `service-clients.yaml`, which carries the static-credential removal and the workload user; **(ii)** the other structural blueprints; **(iii)** `orchestrator-federation.yaml`; **(iv)** Authentik's other worker tasks.

"over" means the claim calls a function unaffected that is affected. "under" means the recovery path the claim names ("waits for the hourly re-apply") does not run.

Members probed per subclass:
- T1 with three upstream failure modes: refused token (Q2), RBAC refusal (Q3) and unreachable API server (Q3b);
- T2 (Q4);
- T3 (Q3, the already-started pod).

| Cell | over | under |
|---|---|---|
| T1 (i) | → §3.1 [compat]: the main container never starts (Q2, Q3, Q3b) | n/a: the claim names no recovery path for this function |
| T1 (ii) | → §3.1 [compat]: same container (Q2, Q3, Q3b) | n/a: the claim names no recovery path for this function |
| T1 (iii) | n/a: the claim already calls federation affected | → §3.1 [compat]: no worker runs discovery, so there is no hourly re-apply (Q2, Q3, Q3b) |
| T1 (iv) | → §3.1 [compat]: same container (Q2, Q3, Q3b) | n/a: the claim names no recovery path for this function |
| T2 (i) | ok [compat]: the old worker keeps running and reads the new mount (Q4 `w2`: `version-two` after 80 s), and a relay-less worker executes applies (VA40/P4). The static credential is removed as claimed | n/a: the claim names no recovery path for this function |
| T2 (ii) | ok [compat]: old pod Running throughout (Q4) | n/a: the claim names no recovery path for this function |
| T2 (iii) | n/a: the claim already calls federation affected | → §3.1 [compat]: the hourly re-apply runs only in the relay-less old worker, where it ends in `error` (VA40/P4), so federation does not recover until the relay serves. Combined with T2 (i), provisioning stays down with no runbook step available |
| T2 (iv) | ok [compat]: old pod Running (Q4) | n/a: the claim names no recovery path for this function |
| T3 (i) | ok [compat]: main stays Ready while the relay answers 403 (Q3, already-started pod; relay restarts 0) | n/a: the claim names no recovery path for this function |
| T3 (ii) | ok [compat]: same container (Q3) | n/a: the claim names no recovery path for this function |
| T3 (iii) | n/a: the claim already calls federation affected | ok [compat]: a failed apply leaves the hash unset and discovery re-applies hourly (VA27; `blueprints/v1/tasks.py:185, 208-223`), in a worker that is running (Q3) |
| T3 (iv) | ok [compat]: same container (Q3) | n/a: the claim names no recovery path for this function |

### Data-flow arrows

| # | Arrow → consuming operation | Disposition |
|---|---|---|
| A1 | kubelet projected token → the handler's file read | ok [presence]: CDR-1 P2 file mode `0640 0:1000` under fsGroup 1000 |
| A2 | handler → `POST /application/o/token/` with five fields → TokenView | ok [compat]: VA38 P3 posted these fields; `token.py:331-334, 458-470` |
| A3 | TokenView → `kid` lookup in persisted `oidc_jwks` (**persistence boundary**) | ok [presence]: VA9 (persisted kind key ID) against `token.py:402-403` `oidc_jwks__keys__contains=[{"kid": …}]` |
| A4 | access token → `IdpAdminClient` admin calls | ok [compat]: VA6 |
| A5a | JWKS fetch, caller 1: blueprint apply (`source.py:107-117`) | ok [compat]: VA9/VA10, CDR-1 P4's successful applies. At pod start it goes through the gate → §3.1 |
| A5b | JWKS fetch, caller 2: the 3-hourly `update_well_known_jwks` | ok [existence]: `sources/oauth/tasks.py` second loop `OAuthSource.objects.all().exclude(oidc_jwks_url="")`, so it covers a source with no well-known URL. It keeps the old JWKS on failure (`continue`). The relay's token freshness over that cadence: R9 |
| A5c | JWKS fetch, caller 3: the runbook's `ak apply_blueprint` in the worker container | ok [existence]: it runs in the worker pod's network namespace, so `127.0.0.1:8001` is the relay. It presumes a started new worker → §3.1 (B1) |
| A6 | relay → API server | ok [compat]: Q1. Through NetworkPolicy: `apiServerCidrs` on 443/6443 (`_helpers.tpl:71-81`) |
| A7 | policy context → expression (`oauth_jwt`, `ak_user_by`) | ok [compat]: VA38 P3 |
| A8 | exchange → `update_or_create` on the mapped username → access token user | ok [compat]: VA12, VA38(c); `token.py:548-579`, `expires` set only when `created` (`:577-578`) |
| A9 | Helm values (`.Release.Namespace`, `.Release.Name`) → inline key → policy `sub` vs the API SA's `sub` | ok [existence]: both come from the same release, and the SA is `{{ .Release.Name }}-api` (`charts/api/templates/deployment.yaml:12, 51`) |
| A10 | `jwksRelay.image` → render → image-scan list and Dependabot | ok [existence]: `image-scan.yml` List step `sed -n 's/^ *image: …/p'` over the cloud renders; Dependabot `helm` covers `charts/*` (`.github/dependabot.yml`). VA31 |
| A11 | runbook paths → `BlueprintInstance(path).retrieve()` | ok [existence]: H7 |
| A12 | integration test → bootstrap-token API creates (source with static `oidc_jwks`, blank URLs) | ok [existence]. `source.py:107-117` fetches only when a URL is set or inferred. Blank URL strings satisfy the `url not in attrs` check (`:119-127`). C1 VA27 created such a source |
| A13 | kubelet ConfigMap/Secret sync → old worker's mounts during a rollout | ok [compat]: Q4 `w2`. Feeds R8 T2 |

### Dropped candidates

| # | Candidate | Disposition |
|---|---|---|
| D1 | The relay sets no CPU limit, so deploy-validate's kube-score would flag it | dropped. The unmodified chart already exits 1 with 10 and 13 CRITICAL findings (Q6), so the relay adds no new gate failure. Every other chart container sets one, which is style, not literal wrongness |
| D2 | `kubectl proxy` caches its first token, so the relay dies after 3600 s | dropped [compat]: disproved by Q1 (R9) |

## 1. Verified-assumptions cross-check

1. Holds: `Program.cs:387-395`.
2. Holds: `AuthentikTrust.cs:68`; called at `Program.cs:466`.
3. Holds for production code: `IdpAdminClient.cs:55,141,206,214,243,251`, `Program.cs:387`. The three test-side uses CDR-1 noted are unchanged.
4. Holds: `configmap-tls-proxy.yaml:10-20`.
5. Holds: probe (ground truth); also VA38(c) `expires_in: 300`.
6. Holds: probe (ground truth).
7. Holds: probe (ground truth).
8. Holds: `AuthentikContainerFixture.cs:111, 244`.
9. Holds: `sources/oauth/api/source.py:107-117`.
10. Holds: `blueprints/v1/tasks.py:190-238` (dramatiq `apply_blueprint`).
11. Holds: `token.py:557-559` `mapped.get("username", …)`.
12. Holds: `core/tasks.py:51-65`; `token.py:577-578` sets `expires` only when created.
13. Holds: `token.py:418`; `:492` before `:494`.
14. Holds: probe (ground truth).
15. Holds: `configmap-tls-proxy.yaml:25-30`.
16. Holds: probe (ground truth).
17. Holds: probe (ground truth).
18. Holds: probe (ground truth).
19. Holds: probe (ground truth); `token.py:176-187` exempts client_credentials from the secret check.
20. Holds: probe (ground truth).
21. Holds: `sources/oauth/apps.py:51-58` (`*/3`); `sources/oauth/tasks.py` JWKS loop.
22. Holds: kind probe (ground truth). Q3 shows the converse: without the binding the relay gets 403.
23. Holds: kind probe (ground truth).
24. Holds: Q1 and Q5 reconfirm at `v1.37.1` (`/api` 403, JWKS 200).
25. Holds: probe (ground truth).
26. Holds: Q2 reconfirms that the exec startup probe holds back the main container.
27. Holds: `blueprints/v1/tasks.py:185, 208-223`; `blueprints/apps.py:163-175`.
28. Holds: `_helpers.tpl:71-81`; `networkpolicies.yaml:193,248`.
29. Holds: `deployment-worker.yaml:9,13`.
30. Holds: `charts/api/templates/deployment.yaml:10-13,51`.
31. Holds: D VA49; `image-scan.yml` List step; `.github/dependabot.yml` helm `charts/*`.
32. Holds: `networkpolicies.yaml:547-550`.
33. Holds: F1's grep.
34. Holds. The `AdminToken` substring hits in `Iverson.LoadTest/Program.cs:118,149,158,186` are `tenantAdminTokenProvider` (acting-user), not the orchestrator token. `:377` is `ak shell` help text.
35. Holds: `modules/cluster-{aws,azure}/variables.tf:13`, `cluster-gcp/variables.tf:15` default `"1.30"`.
36. Holds: re-fetched in round. AWS EKS user guide, irsa-fetch-keys: "Amazon EKS rotates the OIDC signing key every seven days."
37. Holds: `secret-service-clients.yaml:196,206`; CDR-1 P1.
38. Holds: `lib/expression/evaluator.py:68,71`; CDR-1 P3/P5.
39. Holds: `docker-compose.yml:378,421`; `AuthentikContainerFixture.cs:253`; `blueprints-configmap.yaml:12-19`; `deployment-worker.yaml:83-84`.
40. Holds: `deployment-worker.yaml:6`; CDR-1 P4; `apply_blueprint.py`.
41. Holds: CDR-1 P2; reproduced by Q1.

**Span check.**
- **U1.** "The relay keeps authenticating after its first projected token expires." Uncovered: VA24 and VA41 are point-in-time. Verified in round by Q1 → ok.
- **U2.** "A relay that cannot serve the JWKS when a worker pod starts holds back only federation." Uncovered, and false: Q2, Q3 and Q3b show the worker's main container never starts → §3.1.
- **U3.** "During a rollout whose new worker is stuck, the old worker sees the new blueprint content." Uncovered. Verified by Q4 (`w2`); it feeds §3.1's T2 cells.
- **U4.** "The 3-hourly refresh fetches `oidc_jwks_url` for a source with no well-known URL." VA21 covers the cadence only. Verified by reading `sources/oauth/tasks.py`, whose second loop excludes only blank `oidc_jwks_url` → ok.

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

### 3.1 What the worker's start waits for: the relay's upstream (§2c as written) or only the relay's own listener

**The choice.** The spec asserts two properties that cannot both hold:
- **Worker gated on the relay.** §2c: "the worker container starts only after the sidecar's startup probe passes". That probe is a JWKS GET through the relay to the API server.
- **Relay independence.** §2a ¶2: "a relay outage delays only federation". §2b's lead: "Removing the static credential must never depend on the relay". Known issues: "`service-clients.yaml` and the removal of the static credential are unaffected".

With the probe as written, a relay that cannot reach the API server's JWKS when a worker pod starts keeps the whole Authentik worker from starting. That covers every blueprint, `service-clients.yaml` included, and every background task, and it lasts until the relay serves. This was confirmed for a refused token, an RBAC refusal and an unreachable API server (§0 R8, cells T1).

During an upgrade, the old relay-less worker keeps running and applies the new `service-clients.yaml`, which removes the static credential. Federation's hourly re-apply then runs only in that relay-less worker and fails. Provisioning is down with no runbook remedy, because §3c's step needs a new worker container that does not exist (cells T2, B1).

The same reach applies to the Known issue "Managed API servers' JWKS (unverified)". If a cloud API server does not serve the JWKS to the relay, the first cloud deploy has no Authentik worker at all, not just no federation.

**Why it's forced.** The properties trade against each other. Gating on the upstream guarantees that the startup apply of `orchestrator-federation.yaml` finds a working relay. Not gating on it makes a relay failure delay only federation. Which property to keep is a product choice, and the spec's text is wrong under either until it matches the choice. Every variant probed reduces to one of the two options below.

**Option A: keep §2c's gate, and make the spec's claims match it.**
- §2a ¶2, the §2b lead and the "Relay down" Known issue are scoped to a relay that fails **after** the worker has started.
- A new Known issue states that a relay unable to serve the JWKS at worker pod start keeps the whole Authentik worker down. On an upgrade, the old worker removes the static credential while federation waits on the relay.
- The "Managed API servers' JWKS" bullet states that reach.
- The §3c runbook checks that the new worker pod is Ready before its `ak apply_blueprint` step.

Evidence:
- [compat] Q2, Q3, Q3b: with the upstream refused or unreachable, the relay restarted 2–3 times and main stayed `PodInitializing`. The control pod, differing only in audience, went Ready.
- [compat] Q4: with `replicas: 1` and no `strategy`, the old pod stayed Running and read the updated ConfigMap 80 s later, while the new pod stayed Pending.
- [compat] VA40/P4: a relay-less worker executes blueprint applies, and the federation apply ends in `error` there.

**Option B: gate the worker on the relay's local listener only.**
- §2c's relay gains `--www=/www` and `--www-prefix=/ready/`, with `/www` a read-only, empty `emptyDir`.
- The exec startup probe becomes `kubectl --server=http://127.0.0.1:8001 get --raw /ready/`. That path is answered by `kubectl proxy` itself, not by the API server.
- The worker still starts only after the proxy is listening, so its startup discovery cannot reach a closed port. A refusing or unreachable API server no longer holds the worker back; it fails only the federation apply, which re-applies hourly (VA27). §2a, §2b and the "Relay down" Known issue then hold as written, and §3c's runbook step stays as written.
- Costs:
  - one `emptyDir`;
  - one loopback path that serves an empty listing;
  - a cold start whose upstream is briefly unavailable defers federation to the hourly discovery instead of waiting for it;
  - the directory must stay empty, because `--accept-paths` does not filter the `--www` handler;
  - VA26's text about the exec probe changes.

Evidence:
- [compat] Q5 `relay-empty-bad` (upstream refused): relay started with 0 restarts and main Ready, while the JWKS answered 401. That is the decoupling.
- [compat] Q5 `relay-empty-ok`: `/openid/v1/jwks` 200, `/ready/` 200 with an empty listing, `/api` 403, `/ready/x` 404. The JWKS service and the path filter are unchanged.
- [compat] Q5 `relay-www-ok`: `/static/passwd` 200 under `--accept-paths=^/openid/v1/jwks$,^/static/$`. This shows the `--www` handler bypasses the path filter, which is why the directory must be an empty volume and not an image path.
- [existence] VA27 and `blueprints/v1/tasks.py:185, 208-223`: a failed apply is retried at the hourly discovery.

**Why no other variant dominates.**
- [compat] **No `startupProbe`.** The worker starts under a refused upstream (Q5 `relay-noprobe`), but it may start before the proxy has bound its port, a race that B's probe removes. B gives the same decoupling plus that ordering, for one `emptyDir`.
- [compat] **`--address=0.0.0.0` with a `tcpSocket` probe.** It decouples and keeps the ordering (Q5 `relay-tcp`, and `relay-tcp-ok` loopback 200), but it opens the relay on the pod IP. Default-deny ingress (`networkpolicies.yaml:24-27`) covers that only where NetworkPolicy is enforced. B has the same properties on loopback only.
- [compat] **`--www` on an image directory.** It exposes that directory to the pod (Q5 `relay-www-ok` listed `/etc`); B's empty volume exposes nothing.
- [existence] **A and B together.** Not possible: one startup probe either goes through to the API server (A) or does not (B).

## 4. Previously addressed

- CDR-1 §2.1 (Secrets survive the upgrade): resolved. §2b states the keep annotation, the runbook deletes both Secrets, the kind check upgrades before asserting, and VA37 was added (H4, H6, F1).
- CDR-1 §2.2 (early exchange creates an expiring, groupless user): resolved. The workload policy's gate is in §2a, restated in §2b and tested first in §3b; VA38 was added (H2, H3, H5).
- CDR-1 §2.3 (blueprint file seen by compose and fixture; no rendering path): resolved. The key is rendered inline after the `range`, and VA39 was added (H1, F3).
- CDR-1 §2.4 (window not bounded by the rollout): resolved for a new worker that starts. §3c and the runbook were amended and VA40 was added. Its reliance on a started new worker is the new §3.1 (B1), not a re-raise: Q2–Q4 are new evidence.
- CDR-1 §2.5 (integration identity has no role): resolved. §3b creates the mapping and pre-creates the user in the group (H5).

## 5. Recommendation

🛑 **Surface forced decisions to user.** §3.1 needs a pick: keep §2c's upstream gate and rewrite the independence claims (A), or gate only on the relay's own listener (B). §2 is empty, and every CDR-1 finding is resolved.

Self-audit against the slot grammar:
- Population closure: R8's matrix is fully dispositioned (12 cells, both directions), with members probed per subclass: T1 three ways (Q2, Q3, Q3b), T2 (Q4), T3 (Q3).
- Load-bearing `ok` rows carry closed-vocabulary tags with shown evidence. The rows that are not load-bearing say so.
- Every `Evidence:` bullet under §3.1's options is tagged.
- Rule rows R1–R10 show over and under.
- Nothing is left unverified. Each span gap was resolved in round (U1, U3, U4) or flows to §3.1 (U2).
- One anchor header, in SHA form.

Slot audit: pass after 3 fixes (independent audit; the controller tagged S14, gave R9's under-direction reason, and re-fetched VA36).
