# CSR Round 11 — Identity Plane Design (Sub-project B)

**Goal:** sub-project B of the round-11 remediation hardens the Authentik identity plane against findings #5, #7, #8 and #9 of `docs/criticalreviews/2026-10-08-iverson-critical-security-review-11.md`.

| Finding | Summary | Severity |
|---|---|---|
| #5 | MFA on unenrolled accounts goes to whoever signs in first; the public login has no throttling | Low |
| #7 | The orchestrator pin policy bounds neither the federated assertion's lifetime nor its pod binding | Low |
| #8 | On cloud profiles, Authentik records a client-chosen IP address | Low |
| #9 | Standing Authentik credentials remain (`akadmin`, admin-automation client secret) | Low |

Sub-project A (server authorization and query safety) merged at `f19478d6`. Sub-projects C (transport and network) and D (clients, tooling and CI) cover the rest of round 11.

**Decisions (user, 2026-10-09):**
- **Scope:** all of #5, #7, #8 and #9, in **one spec with phased tasks** (a B1/B2/B3 split was declined).
- **#5 throttling:** an Authentik reputation policy **and** cloud edge WAF rate limits (Terraform, AWS/GCP/Azure).
- **#9:** all four remedies — refuse superusers on the public listener, drop the bootstrap password from the server pod, federate admin-automation, and a named break-glass admin that enrols MFA at creation; `akadmin` is then deactivated.
- **Forks (after probes):** #7 claim checks only (no live TokenReview); #8 the tls-proxy mirrors `ClientPartitionKey`; Azure moves to a `WAF_v2` Application Gateway; reputation is keyed on client IP only; LoadTest takes an admin-automation service-account token from a file named by an env var; compose keeps its client secret.
- **Forced decisions (after verification):** admin-automation federation lives in its own self-contained blueprint file; break-glass enrols through a recovery-flow link generated in the worker, and the recovery flow also refuses superusers on the public listener.
- **CDR-1 decisions (2026-10-11):** the reputation threshold is −5 (Authentik floors scores there); the .NET SDK gains an additive service-token provider so LoadTest's schema registration works on Helm; Azure's per-path WAF policy runs in Prevention with OWASP on the Authentik host; edge path matches URL-decode before matching (a probe showed Authentik serves percent-encoded executor paths).
- **CDR-2 decisions (2026-10-11):** no install exists yet, so the Azure gateway subnet keeps `10.1.16.0/24` without meeting an add-on-created subnet; Terraform grants the AGIC add-on identity `Network Contributor` on the gateway's resource group.
- **Plan-time decisions (2026-10-11):** a probe showed Authentik's stock reputation policy lets an address's successful logins offset its failures against another account, so the login throttle is an expression policy that sums only the address's failing reputation rows (VA 45); `docs/security/tma.md` is not edited on this branch, because it holds an uncommitted refresh (the next refresh records this change).

**Global constraints**
- **One branch, one merge.** No proto change. One additive .NET SDK change (§5): `AddIversonClient` gains an optional service-token provider; `IversonClientCredentials` and the other SDKs are untouched.
- **Conformance:** no requirement changes (Verified assumption 33).
- **Security write-ups** (comments, test names, docs) describe conditions and behaviour, never step-by-step exploitation.
- **`docs/` is gitignored:** commit with `git add -f`.
- **The user's running stack is off-limits** (`iversonserver` containers, volumes, networks, images; another session's `rfdt-*` and `tei_models`). Live checks use isolated compose projects, a throwaway kind cluster and throwaway containers, removed afterwards. Never `docker compose up` against `Iverson.Server/docker-compose.yml` without an isolated `-p` project.
- **Memory:** one heavy process at a time, capped with `systemd-run --user --scope -p MemoryMax=…`.

Paths below are relative to `Iverson.Server/deploy/helm/iverson/` unless stated (`charts/authentik/…`, `charts/api/…`, `values-*.yaml`).

## 1. Client address (#8)

Every later control that keys on the client address (reputation, audit, forensics) needs a trustworthy one first.

- **One source of truth.** `trustedProxies.cidrs` and `trustedProxies.hops` move from the api subchart (`charts/api/values.yaml:53-55`, set under `api:` in each `values-*.yaml`) to `global.trustedProxies`. The umbrella `values.yaml` gains the default (`cidrs: []`, `hops: 1`); both subcharts read it nil-safely (`dig`). Per-profile values carry over unchanged: aws `10.0.128.0/20, 10.0.144.0/20` h1; azure `10.1.16.0/24` h1; gcp `130.211.0.0/22, 35.191.0.0/16` h2; laptop `10.244.0.0/16` h1; local `10.244.0.0/16` h1. The api chart's render guards (`charts/api/templates/deployment.yaml:1-6`) and env (`:145-150`) read the global key.
- **The tls-proxy normalises the address.** In `charts/authentik/templates/configmap-tls-proxy.yaml`:
  - a `geo $remote_addr` block marks the peer trusted when it is inside `global.trustedProxies.cidrs`;
  - a `map` over `$http_x_forwarded_for` selects the entry `hops` positions from the right (regex captures for 1 and 2) when the peer is trusted, and falls back to `$remote_addr` otherwise (including an empty or malformed header);
  - a second `map` reduces the selected entry to a bare address — a trailing `:port` is stripped from IPv4 and `[v6]:port` is unwrapped — as `ClientPartitionKey`'s `IPEndPoint.TryParse` does (AGIC on Azure writes `ip:port`; VA 40);
  - the public `:9080` server sets `X-Forwarded-For` to that single address;
  - the in-cluster `:8443` server sets `X-Forwarded-For $remote_addr`.
  This is the same rule as `ClientPartitionKey`, from the same values.
- **Authentik trusts only the sidecar.** `charts/authentik/templates/configmap.yaml` sets `AUTHENTIK_LISTEN__TRUSTED_PROXY_CIDRS=127.0.0.1/32,::1/128`. Authentik records the left-most `X-Forwarded-For` entry from a trusted peer; the sidecar now always sends exactly one.
- **Compose:** the compose tls-proxy has no public listener; the only change is the same `AUTHENTIK_LISTEN__TRUSTED_PROXY_CIDRS` on the compose `authentik-server`, which stops Authentik trusting the bridge peer of its host-published `:9000`.
- **Kind/laptop:** ingress-nginx overwrites `X-Forwarded-For` with the peer it saw; the 1-hop map yields the real client.

## 2. Login hardening in Authentik (#5)

- **Recovery enrols a device.** `charts/authentik/blueprints/recovery-flow.yaml`'s authenticator-validation stage changes from `not_configured_action: skip` to `configure`, with `configuration_stages` naming Authentik's stock TOTP setup stage (the stage `mfa-enforcement.yaml` already `!Find`s). A user with no device cannot finish recovery without enrolling; a user with one is asked for a code. Every invitee, new tenant admin and the break-glass admin therefore bind their second factor in their own recovery session, before any password login exists.
- **Reputation throttling.** A new chart blueprint entry creates an expression policy that sums the client address's failing reputation rows (score below 0) and binds it **flow-level** on `default-authentication-flow`, targeting the flow's policy-binding row (VA 45). It refuses an address once those rows sum to −5 (five failed passwords; a failed MFA code counts two), before identification, with Authentik's access-denied stage; successful logins do not offset failures. Authentik still writes the rows (each `(address, username)` row floored at −5; VA 39). Scores expire after `reputation.expiry` (default 86400 s, from the first failure); an operator can clear one with `DELETE /api/v3/policies/reputation/scores/{uuid}/` or the admin UI. The runbook documents both.
- The per-session `failed_attempts_before_cancel` is unchanged.

## 3. Superusers and the bootstrap credential (#9; Helm only)

- **Superusers refused on the public listener.** A shared expression policy returns true when the pending user is a superuser **and** the request carries `X-Iverson-Public-Ingress` (set only by the `:9080` listener). It is bound to deny stages (`authentik_stages_deny.denystage`; binding defaults `evaluate_on_plan: false`, `re_evaluate_policies: true`):
  - in `default-authentication-flow` at order 15 — after identification (10), before password (20), so a superuser's password is never checked on the public listener;
  - in the recovery flow as its first stage, before the prompt (the other stages renumbered), so a superuser's recovery link opened on the public listener is refused before any password or device is written.
  Ordinary users on the public listener, and superusers on the in-cluster route, are unaffected.
- **Bootstrap password off the internet-facing pod.** `AUTHENTIK_BOOTSTRAP_PASSWORD` is removed from `charts/authentik/templates/deployment-server.yaml` (:114-116). Bootstrap runs in both server and worker, gated by the setup flag, so the worker (which keeps the variable, `deployment-worker.yaml:102-104`) still creates `akadmin` on a fresh install. Once `akadmin` is deactivated the remaining copy is inert.
- **Named break-glass admin.** A chart blueprint declares group `authentik Admins` with `is_superuser: true` (the stock `events-default.yaml` may create it without) and user `iverson-breakglass` in it, with no usable password.
- **Activation (runbook).** An operator runs a short `ak shell -c` snippet in the worker pod that calls the same recovery-link code the API's user recovery action uses (`UserViewSet._create_recovery_link`), with a request carrying the in-cluster host. The operator opens the link over the in-cluster route (port-forward); the recovery flow forces TOTP enrolment, then signs the admin in. `ak create_recovery_key` is not used: its link signs the user straight in, bypassing the flow.
- **`akadmin` deactivated.** A blueprint entry sets `akadmin` `is_active: false`. It survives restarts and a deactivated `akadmin` cannot log in; break-glass is always reachable through a fresh recovery link from the worker, so there is no lockout window.
- **Placement.** `akadmin` inactive, `authentik Admins`, `iverson-breakglass`, the superuser policy and both deny bindings go in a new templated key in `charts/authentik/templates/blueprints-configmap.yaml` (Helm only: compose and the Api.Tests fixture mount only the static `blueprints/` directory, and `OrchestratorGroup_IsNotASuperuserGroup` forbids `is_superuser: true` in `secret-service-clients.yaml`). The reputation policy and binding (§2) go in a new static `charts/authentik/blueprints/*.yaml` file, so compose and the fixture apply them.
- **Docs that log in as `akadmin` on Helm move to the break-glass procedure:** `docs/user-management-and-security.md:130-131`, `docs/runbooks/operator-access-onboarding.md:27-29`, `docs/runbooks/admin-console-landing-page-usage.md:100-102`. `docs/security/tma.md` is left to its next refresh (plan-time decision). The compose instructions stay as they are: `docs/user-management-and-security.md:126,128`, `docs/runbooks/operator-access-onboarding.md:20-21`, `docs/runbooks/admin-console-landing-page-usage.md:81`, `Iverson.AdminUI/README.md:27-28`.
- **Compose is unchanged** (keeps `akadmin` and bootstrap credentials for its fixtures and tooling).

## 4. Orchestrator pin bounds (#7)

- The orchestrator pin policy (`charts/authentik/templates/blueprints-configmap.yaml`, templated key `orchestrator-federation.yaml`, policy at :88-99) keeps its `sub`, `aud` and pre-created-user checks and adds two claim checks on `request.context["oauth_jwt"]`:
  - `exp - iat <= 600`;
  - a `kubernetes.io.pod` claim is present.
  Authentik already rejects an expired `exp`, so `exp - iat <= 600` alone bounds a token's usable age to 600 s; no separate age check is needed.
- These reject unbound `kubectl create token` tokens (no pod claim) and any token requested with a longer lifetime (the API server grants up to a year on request).
- The legitimate path is unaffected: the API's projected token has `expirationSeconds: 600` and is pod-bound (`charts/api/templates/deployment.yaml:290-292`); `AuthentikOrchestratorTokenHandler` re-reads the file on every mint.
- **Runbook:** restrict pod creation, exec and `serviceaccounts/token` in the namespace to the deploy identity.

## 5. Federated admin-automation (#9; Helm only)

- **Cluster identity.** A new `<rel>-admin-automation` ServiceAccount (`automountServiceAccountToken: false`); no pod runs as it.
- **One self-contained blueprint file.** A new key `admin-automation.yaml` in the existing `<rel>-authentik-blueprints-service-clients` Secret (rendered by `charts/authentik/templates/secret-service-clients.yaml`, mounted in server and worker) holds every admin-automation object, with no reference to another file:
  - its own `authentik_sources_oauth.oauthsource` on the same relay JWKS URL, with its own source property mapping returning the admin-automation username;
  - the pre-created admin-automation user (an exchange-created user would expire);
  - the `admin`, `schema_admin` and `tenant_id_admin` scope mappings, repeated (idempotent by identifiers) so the file never depends on another file's apply order;
  - the `iverson-admin-automation` provider and application, moved out of the service-clients loop, with `jwt_federation_sources` naming the new source, `client_id` from the existing lookup of `<rel>-authentik-admin-automation-client`, and `client_secret` set to a fresh random 64-character value on every render (assertion grants never check it; a blank secret would make secret-based grants unauthenticated; the old secret stops working);
  - the in-cluster-only marker policy (moved) and a pin policy, both bound with `policy_engine_mode: all`: `sub == system:serviceaccount:<ns>:<rel>-admin-automation`, `aud` contains `iverson-authentik-admin-automation`, `exp - iat <= 600` (no pod claim — these tokens are not pod-bound).
  Moved entries keep their identifiers, so they update the existing objects; omitted entries are never deleted.
- **The `<rel>-authentik-admin-automation-client` Secret keeps `client-id`** (the API's `Authentication__ValidAudiences__3`, `charts/api/templates/deployment.yaml:170-172`) and stops carrying `client-secret`; the template's `lookup` of it guards the missing key with `hasKey`. Token claims are unchanged: the scope mappings are fixed values and Operator/SchemaAdmin are driven by the `scope` claim (`OperatorAuthorizationPolicy.cs:14`).
- **Tooling.** `Iverson.LoadTest` gains an assertion mode. When `IVERSON_CLIENT_ASSERTION_FILE` names a file:
  - the credential gate (`Program.cs:114-119`) accepts it in place of `IVERSON_CLIENT_SECRET`, so tenant provisioning runs;
  - Operator tokens are requested with `client_credentials` + `client_assertion` (IdentityModel `ClientAssertion`; `Program.cs:389-413`), re-reading the file on each mint — runs that outlast the 1 h Operator token need a refreshed file;
  - the SDK registration (`Program.cs:185`) passes the new service-token provider (below), backed by that minting, instead of `IversonClientCredentials`.
  Otherwise LoadTest uses the client secret as today (compose).
- **SDK (additive, .NET only).** `AddIversonClient` (`Iverson.Clients/DotNet/Iverson.Client.Core/ServiceCollectionExtensions.cs:38-46`) gains an optional service-token provider that authenticates the mapping client — and the persistence, retrieval and search clients when no `dataPlaneTokenProvider` is given — in place of `credentials` (today the mapping client is attached only from `credentials`, `:94-98`; VA 41). Supplying both is refused; existing callers are unaffected.
- **Onboarding.** LoadTest's `AuthentikFlowExecutorClient.SetPasswordFromRecoveryLinkAsync` handles the recovery flow's TOTP setup challenge (§2) the way `MintAsync` already does (`Auth/AuthentikFlowExecutorClient.cs:248-251`), caching the secret where `MintAsync` reads it (`:120`, `:132`).
- The conformance harness does not use admin-automation and is unchanged.
- **Runbook:** mint with `kubectl create token <rel>-admin-automation --audience iverson-authentik-admin-automation --duration 10m`, write it to a file, point `IVERSON_CLIENT_ASSERTION_FILE` at it; note that the old client secret stops working on upgrade. `docs/runbooks/grpc-admin-auth-cutover.md:38` is updated.

## 6. Cloud edge rate limits (#5; Terraform + Helm wiring)

- **What is limited:** requests from one client address to `/api/v3/flows/executor/` on the Authentik host — the path every login and MFA submission uses. Defaults 100 requests per 300 s, from Terraform variables `authentik_login_rate_limit` and `authentik_login_rate_window`. The edge sees the true client address; no header trust is involved. Each rule URL-decodes the path before matching (AWS `URL_DECODE` text transformation, Azure `UrlDecode` transform, GCP CEL decoding), because Authentik decodes and serves percent-encoded executor paths (VA 43).
- **AWS** (`Iverson.Server/deploy/terraform/aws`, aws provider 5.100.0): a regional `aws_wafv2_web_acl` with a `rate_based_statement` (`aggregate_key_type = "IP"`, path-prefix `scope_down_statement`) that blocks; output the ACL ARN. Helm value `authentik.ingress.wafAclArn` renders `alb.ingress.kubernetes.io/wafv2-acl-arn` on the Authentik Ingress only (each Ingress has its own ALB; LB Controller v2.9.0 supports the annotation).
- **GCP** (google 5.45.2): a `google_compute_security_policy` with a `rate_based_ban` rule matched on the path (10-minute ban); output the policy name. On the gcp profile Helm renders a `BackendConfig` with `securityPolicy` and annotates the Authentik Service (`charts/authentik/templates/service.yaml`, which gains an annotations block) with `cloud.google.com/backend-config` for port `9080`, the port the GCE ingress targets.
- **Azure** (azurerm 3.117.1): Terraform creates a `WAF_v2` `azurerm_application_gateway` in a `10.1.16.0/24` subnet of the cluster VNet, in `azurerm_resource_group.this` (keeping the trusted CIDR valid), and passes its ID to the AKS AGIC add-on (`ingress_application_gateway.gateway_id`, replacing `subnet_cidr` in `modules/cluster-azure/main.tf:265`), with `ignore_changes` on what AGIC manages. Terraform grants the add-on identity (`ingress_application_gateway[0].ingress_application_gateway_identity[0].object_id`) `Network Contributor` on `azurerm_resource_group.this`, the grant azure-cli makes client-side for a configured gateway id (VA 44). An `azurerm_web_application_firewall_policy` carries a `RateLimitRule` custom rule (group by client address, matched on host and path) and is attached to the Authentik Ingress via `appgw.ingress.kubernetes.io/waf-policy-for-path` (valid for a `/` path; the policy must exist first). The per-path policy runs in **Prevention** (required for the rate limit to block) with its required OWASP managed rule set, so managed rules can block requests on the Authentik host; the gateway-level policy for the other hosts runs in **Detection** (VA 42). The `WAF_v2` SKU costs more.
- **Operator step:** Terraform does not feed Helm values; the runbook copies each output into the profile's values file, and a render guard refuses an empty WAF value on a profile that requires it.

## 7. Testing

- **Render checks** (`helm dependency build` + `helm template`/`helm lint` on all five profiles): the moved `trustedProxies` keys and guards; the tls-proxy `geo`/`map` and `X-Forwarded-For` lines; `AUTHENTIK_LISTEN__TRUSTED_PROXY_CIDRS`; no bootstrap password on the server container; the new blueprint entries (recovery `configure`, failure-only reputation policy + binding, deny stages in both flows, `authentik Admins`, `iverson-breakglass`, `akadmin` inactive, pin `exp - iat` + pod claim, the admin-automation file, its ServiceAccount, no `client-secret` key); WAF annotation / BackendConfig / Service annotation per cloud profile.
- **Unit tests:** LoadTest's two token-request shapes (secret vs assertion file) and its credential gate; `SetPasswordFromRecoveryLinkAsync` with a TOTP setup stage; the SDK's service-token provider (attached to all four clients; refused together with `credentials`).
- **Terraform:** `terraform fmt -check` and `terraform validate` per root in a scratch copy (`init -backend=false`).
- **nginx map:** a throwaway container run of the rendered tls-proxy config against forwarded-header chains (trusted/untrusted peer, 1/2 hops, multiple headers, empty/malformed, `ip:port` and `[v6]:port` entries).
- **Compose live** (isolated project): a recovery link for a user with no device cannot complete without enrolling; after five failed logins from one address (which has also signed in successfully), the next attempt is refused while another address still logs in; Authentik records the real peer.
- **Kind live** (throwaway cluster, laptop profile):
  - recovery enrolment and break-glass: the worker-generated link enrols TOTP and signs in over the in-cluster route; break-glass is refused on the public listener (login, and recovery with no password or device written); `akadmin` is inactive; an ordinary user logs in on the public listener;
  - orchestrator: the API still mints; an unbound `kubectl create token` for `<rel>-api` and a pod-bound token requested with `--duration 1h` are refused;
  - admin-automation: a minted SA token yields an Operator token the API accepts; the old client secret and a 1h-duration SA token are refused;
  - client address: Authentik events carry the real client address.
- **Api.Tests changes:** `RecoveryFlow_UserWithNoEnrolledFactor_CompletesAndSignsTheUserIn` is replaced by a fact that a no-device recovery reaches TOTP setup, then completes and signs in once a code is submitted; `OrchestratorFederation_MintsOnlyForThePinnedWorkload_OnceTheUserExists` gets a positive `Assertion()` carrying `kubernetes.io.pod`, and a pod-less and an `exp = iat + 3600` assertion join its refused cases (`AuthentikRecoveryFlowIntegrationTests.cs:103, 256, 497`).
- **Suites:** the nine .NET suites stay green; conformance unchanged.

## 8. Rollout

- **Blueprint pickup:** Authentik re-applies a blueprint when its file hash changes, on a file watcher plus an hourly schedule; the runbook adds an explicit `ak apply_blueprint` for each changed file after an install or upgrade (round-10 pattern), static files before the templated ones, because the templated deny bindings `!Find` flows defined in static files and a missing referent fails the whole file (VA 29).
- **Order on an existing install:** `akadmin` is deactivated and break-glass exists without a password until an operator generates its recovery link; the admin-automation client secret stops working at once (callers switch to minted SA tokens); reputation scores start empty; the cloud WAF needs a Terraform apply and the outputs copied into the values file. No install exists yet (user, 2026-10-11), so on Azure the new gateway subnet is never created beside an add-on-created `10.1.16.0/24` subnet.
- **Compose** changes Authentik's trusted-proxy CIDRs and, through the shared static blueprints, gains recovery TOTP enrolment and reputation throttling; it keeps `akadmin`, the bootstrap credentials and its client secret.

## Known issues / accepted as out of scope

- **Copied pod token window (#7, user decision 2026-10-09):** a token copied from a pod that is then deleted remains usable until its 600 s life ends; a live TokenReview (~15 s) was declined to avoid new RBAC and relay surface.
- **Cloud edge behaviour is unverified live:** the ALB annotation, Cloud Armor attachment and AGIC per-path WAF policy are documentation-level; only `terraform validate` and render checks run.
- **Azure `WAF_v2` (user decision 2026-10-11):** costs more; gRPC through the AGIC gateway already returns 400 (pre-existing); on the Authentik host the per-path policy runs OWASP in Prevention, so a managed-rule false positive can refuse a login there.
- **Reputation lockout:** a shared NAT can be refused for up to 24 h (expiry counted from the first failure) once its failed attempts sum to five (successful logins don't offset them; choosing an MFA setup stage counts one until the enrolment completes, so abandoned enrolments add up), until an operator clears the score.
- **Global `add_user`:** orchestrator rights still include it — the Authentik permission limit accepted in round 10.
- **Plaintext Authentik `:9080` hop and GCP HTTP:80** belong to sub-project C (#4, #6).
- **First apply on a fresh install:** the worker's first automatic apply of `reputation.yaml` and `superusers.yaml` can run before the stock authentication flow exists, which fails the file until the hourly re-apply; the runbook applies the changed files explicitly after an install or upgrade.

## Verified assumptions

Evidence was gathered on 2026-10-09 at `f19478d6`: live probes on an isolated Authentik 2026.5.3 compose project, a throwaway kind cluster and nginx containers (all removed); reads of the chart, the Authentik 2026.5.3 image source, and the Terraform roots (provider schemas from `terraform init -backend=false` in a scratch copy).

**Probes**
1. **No built-in login throttling.** 24 fresh-session wrong passwords all returned HTTP 200 at constant latency; only the per-plan `failed_attempts_before_cancel` (5) applies.
2. **Reputation refuses when bound flow-level and negated,** keyed on client IP before identification (any username from a low-score address is refused); a success adds only +1.
3. **Authentik takes the left-most `X-Forwarded-For` entry from a trusted peer** and ignores the header from an untrusted one (records the peer). Default trusted CIDRs include the private ranges (`lib/default.yml`).
4. **Superuser refusal works only as a deny stage after identification** with `re_evaluate_policies` (sees the pending user); a flow-level binding is too early and a password-stage binding only skips.
5. **Recovery with `configure` forces TOTP enrolment** before `user_login`; a device holder validates instead (stage source + round-10 live Chromium).
6. **`akadmin` deactivation survives restarts** with or without the bootstrap env, and blocks login; a blueprint-created superuser with no usable password works and is recoverable.
7. **Expression policies read and compare** numeric `iat`/`exp` and the nested `kubernetes.io` claim.
8. **Kind tokens:** projected/pod-bound tokens carry `kubernetes.io.pod`; an unbound `kubectl create token` does not; the API server grants requested durations up to a year.
9. **TokenReview** rejects a pod-bound token ~15 s after the pod is deleted; a JWKS signature+exp check still accepts it.
10. **nginx `real_ip_recursive`** yields the right-most untrusted entry — wrong for GCP's 2-hop chain, hence the `map`.
11. **The `geo` + `map` selection works** on the chart's nginx 1.30.5: trusted peer "a, b, c" → c (1 hop) / b (2 hops); two headers arrive joined with ", "; empty, trailing-comma or untrusted-peer chains fall back to the peer.

**Chart and code**
12. Only the api chart reads `trustedProxies` (`charts/api/templates/deployment.yaml:1-6, 145-150`, `charts/api/values.yaml:53-55`, one block per profile); both subcharts already read `global.*`.
13. The tls-proxy image is `nginxinc/nginx-unprivileged:1.30.5-alpine`; neither listener sets `X-Forwarded-For` today; only `:9080` sets the marker, and the Ingress targets only `:9080`.
14. Authentik's client IP comes from `ClientIPMiddleware` (first `X-Forwarded-For` entry, IP-validated) — a single sidecar-written entry is recorded as is.
15. The compose tls-proxy has one `:8443` server and no public listener; compose publishes Authentik `:9000` on the host.
16. Kind's ingress-nginx (`kind/setup.sh:110-118`) sets no forwarded-header options, so it overwrites `X-Forwarded-For`.
17. No blueprint or policy reads the client IP; the API does not rely on `X-Forwarded-For` toward Authentik.
18. `recovery-flow.yaml` binds prompt 0, mfa-validation 1 (`skip`), user-write 2, user-login 3, redirect 4; the TOTP setup stage is Authentik's stock stage.
19. `default-authentication-flow` binds identification 10, password 20, mfa-validate 30, login 100; chart blueprints already patch it by slug.
20. `ReputationPolicy` has `check_ip`, `check_username`, `threshold` (default −5) and passes when the score is low (hence negate); `reputation.expiry` defaults to 86400; scores can be deleted via API/UI.
21. `denystage` exists; binding defaults are `evaluate_on_plan=False`, `re_evaluate_policies=True`; one policy can bind to several stages and flows; in recovery the policy's `request.user` is the recovering user.
22. Bootstrap (`post_startup_setup_bootstrap`) runs in both server and worker, gated by the setup flag; only the server container among the server pod's containers reads `AUTHENTIK_BOOTSTRAP_PASSWORD`.
23. The stock bootstrap creates group `authentik Admins` superuser; `events-default.yaml` may create it first without — hence the chart declares it.
24. `ak create_recovery_key` prints a direct-login link that bypasses the flow; `UserViewSet._create_recovery_link` builds a recovery-flow link and can be called from `ak shell -c` with a request carrying the host.
25. Nothing on the Helm automation path logs in as `akadmin`; three runbooks and the TMA do (listed in §3).
26. The orchestrator pin is the templated `orchestrator-federation.yaml` (`blueprints-configmap.yaml:25`, policy :88-99) and reads `request.context["oauth_jwt"]`; the source user mapping is source-wide and hard-codes the orchestrator user.
27. The API's projected token: audience `iverson-authentik-orchestrator`, `expirationSeconds: 600`; `AuthentikOrchestratorTokenHandler` re-reads it per mint.
28. The provider federation field is `jwt_federation_sources`; the assertion path never compares the client secret; a blank secret would make secret grants unauthenticated; blueprints apply with `partial=True`.
29. A missing cross-file `!Find` fails the whole file and file apply order is not guaranteed; importer matches by identifiers and never deletes omitted entries; source slugs are unique but JWKS URLs need not be.
30. The relay serves the single cluster JWKS, so any ServiceAccount's tokens verify; chart ServiceAccounts use `automountServiceAccountToken: false`.
31. The service-clients blueprint is one Secret (`<rel>-authentik-blueprints-service-clients`) mounted in server and worker without `items:`, so a second key mounts automatically; a changed random secret only triggers one idempotent re-apply.
32. LoadTest (`Program.cs:389-413`) mints admin-automation tokens with IdentityModel 7.0.0 (`ClientAssertion` supported); conformance uses the loadtest client; on Helm only `secret-service-clients.yaml` touches the admin-automation `client-secret`.
33. The `admin`/`schema_admin` mappings return `{}` and `tenant_id_admin` is fixed; Operator/SchemaAdmin come from the `scope` claim; only `sub` changes and its consumers treat it as opaque — conformance unaffected.
34. Terraform: roots `terraform/{aws,gcp,azure}`; ALB from the AWS LB Controller (chart 1.9.0 = v2.9.0, `modules/operators/main.tf:110`); GCE built-in ingress; Azure AGIC add-on with `subnet_cidr` (`modules/cluster-azure/main.tf:265`), gateway `Standard_v2`; no WAF/Cloud Armor anywhere.
35. Pinned providers aws 5.100.0, google 5.45.2, azurerm 3.117.1 validate the planned resources (`rate_based_statement` with `scope_down`, `rate_based_ban` with expression match, `RateLimitRule`, `WAF_v2` gateway, AKS `gateway_id`); `terraform validate` passes per root in a scratch copy.
36. No ALB ingress group exists, so each Ingress has its own ALB; LB Controller v2.9 supports `wafv2-acl-arn`; AGIC `waf-policy-for-path` applies to a `/` path with an existing policy; BackendConfig `securityPolicy` is attached via a per-port Service annotation; the Authentik Service has no annotations block today.
37. On Azure all four Ingresses use the AGIC gateway; gRPC paths already return 400 there.
38. Blueprint files re-apply on hash change via a file watcher plus an hourly schedule; the chart has no checksum annotation.
39. **Reputation floor.** Authentik clamps each `(ip, identifier)` score at the tenant's `reputation_lower_limit` (`policies/reputation/signals.py:27,35`), default −5 (`tenants/models.py:30`); `Tenant` is `InternallyManagedMixin` (`:44`), which blueprints cannot write (`blueprints/v1/importer.py:95`). Probe A2 refused at −5.
40. **Bare-address map.** On nginx 1.30.5, the second `map` reduces `203.0.113.7:51234` and `spoof, 203.0.113.7:51234` to `203.0.113.7` and `[2001:db8::1]:443` to `2001:db8::1`, leaves bare IPv4/IPv6 and `::ffff:` forms unchanged, and every output parses under Python's `ipaddress.ip_address()`; AGIC writes `ip:port` (`values-azure.yaml:106`).
41. **SDK registration.** `AddIversonClient(services, grpcEndpoint, credentials, dataPlaneTokenProvider, actingUserTokenProvider, allowInsecureChannelCallCredentials, params entityAssemblies)` (`Iverson.Client.Core/ServiceCollectionExtensions.cs:38-46`) attaches the mapping client only from `credentials` (`:94-98`); LoadTest gates tenant provisioning on `IVERSON_CLIENT_SECRET` (`Program.cs:114-119`) and passes the same credentials to the SDK (`:185`).
42. **Azure WAF policy.** In azurerm 3.117.1 `managed_rules` / `managed_rule_set` are required and `mode` is policy-wide (default `Prevention`); Detection mode does not block, so the rate limit needs Prevention. In Prevention a single Critical managed-rule match blocks.
43. **Encoded executor paths.** Authentik 2026.5.3 decodes percent-encoded path segments and serves the executor for `%65xecutor`, an encoded slash and `%66lows` (directly and through the chart's nginx, which decodes before location matching); wrong passwords sent that way record `login_failed` and the same reputation decrement. Wrong-case hex (`%45xecutor`) is not served (404).
44. **AGIC add-on grant.** azure-cli's `add_ingress_appgw_addon_role_assignment` (`acs/addonconfiguration.py`, `dev`) assigns `Network Contributor` to the add-on identity at the configured gateway's resource-group scope, client-side; Terraform's `gateway_id` makes no such grant and `modules/cluster-azure/main.tf` has no assignment for that identity. azurerm 3.117.1's `azurerm_kubernetes_cluster.ingress_application_gateway` exports a computed `ingress_application_gateway_identity` (`client_id`, `object_id`, `user_assigned_identity_id`) (`terraform providers schema -json`).
45. **Failure-only throttle (plan-time probe, 2026-10-11).** The stock `ReputationPolicy` sums every row for the address (`policies/reputation/models.py`), so with any positive row (a success, +1) failures against one username, floored at −5, never reach the threshold. An expression policy summing `Reputation.objects.filter(ip=str(ak_client_ip), score__lt=0)` refused the address on the 6th attempt after a success from it and when failures rotated across three usernames, left another address unaffected, and raised no policy exception. A `policybinding` whose target is `!Find [authentik_flows.flow, …]` resolves to the flow's `flow_uuid`, not the `pbm_uuid` the binding needs, so it fails every re-apply; `!Find [authentik_policies.policybindingmodel, [flow__slug, …]]` re-applies cleanly.
