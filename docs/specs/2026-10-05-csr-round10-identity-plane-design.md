# CSR Round 10 — Identity Plane Remediation Design (Sub-project C1)

**Source review:** `docs/criticalreviews/2026-10-03-iverson-critical-security-review-10.md` (reviewed at `a8c48db4`)

**Goal:** fix the identity-plane findings from CSR round 10:
- **#4 (internet reach and two of its credentials):** Authentik's whole host is published, admin interface and admin API included. The bootstrap superuser token is kept forever. The admin-automation client can mint Operator tokens from anywhere.
- **#12:** the console's OIDC provider allows 30-day refresh tokens the console never uses.
- **#16 (partly):** recovery (invite) links are MFA-free bearer credentials for 30 minutes. C1 shortens them and points them at the public host; the invitee's first console session stays MFA-free (see Known issues).
- **The `ExternalIssuer` defect:** tokens minted through the public Authentik host are rejected by the API.
- **A latent defect found while designing this:** recovery links carry Authentik's in-cluster host, which an invited user cannot open.

**Split.** The remaining #4 item, an expiring orchestrator credential, moves to **C2**, its own spec. The probe below shows that workload identity (Kubernetes service-account JWT federation) removes the static orchestrator token entirely, but each profile publishes its service-account keys differently, and AKS needs Terraform changes that belong with sub-project D. Sub-projects A, B and E are merged. D (infrastructure and supply chain) is separate.

**Global constraints**
- **One branch, one merge.** No proto change, no SDK change, no console change.
- **Security write-ups** (comments, test names, docs) stay at the level of conditions and behaviour, never step-by-step exploitation.
- **`docs/` is gitignored**, so commit docs files with `git add -f`.
- **The user's running compose stack** (`iversonserver` containers, volumes, networks, images) is never started, stopped or modified. Live checks use isolated compose projects (`-p <name>`, `--project-directory`, `container_name:` stripped, `down -v`) and a throwaway kind cluster that the check creates and deletes.
- **Compose secrets** are read from `Iverson.Server/.env` and never printed.

---

## 1. The public Authentik listener (closes #4's internet reach)

Today the Authentik Ingress sends every path to the server's port 9000 (`charts/authentik/templates/ingress.yaml:29`). The admin interface (`/if/admin/`), the user self-service interface (`/if/user/`) and the whole admin API (`/api/v3`) are therefore reachable from the internet, and any leaked Authentik credential is usable from anywhere.

### 1a. A second server in the tls-proxy sidecar

`charts/authentik/templates/configmap-tls-proxy.yaml` gains a second nginx `server`, on plain `9080`, beside the existing `8443` TLS server. The ingress terminates public TLS, as it does today.

- **Server-level headers**, inherited by every location below (none of them sets its own):
  - `proxy_set_header Host $http_host;`
  - `proxy_set_header X-Forwarded-Host $http_host;`: overwrites any client-supplied value.
  - `X-Forwarded-Proto` and `X-Forwarded-For` are passed through unchanged, so Authentik sees what it sees today.
  - `proxy_set_header X-Iverson-Public-Ingress 1;`: set on every request through this listener.
- **Allowlist.** These are the only locations that proxy; each carries `proxy_pass http://127.0.0.1:9000;` (nginx allows `proxy_pass` only in `location` context), and `location / { return 404; }` covers everything else:

| Location | Why a browser needs it |
|---|---|
| `/application/` | OIDC authorize, token, userinfo, JWKS, discovery, end-session (and SAML) |
| `/if/flow/` | the flow interface (login, MFA enrolment, recovery, logout) |
| `/api/v3/flows/executor/` | the flow interface's only API calls |
| `/flows/-/default/` | the default-flow redirects after logout |
| `/static/` | the flow interface's scripts, styles, fonts and icons |
| `= /` | where Authentik lands after logout; it redirects into the default flow |
| `= /-/health/live/` | load-balancer health checks (see 1c) |

  Everything else returns 404 publicly: `/if/admin/`, `/if/user/`, every other `/api/v3` path, `/media/`, `/ws/`.
- **Compose is unchanged.** It has no ingress, and its `9000` is bound to `127.0.0.1`.

### 1b. Service, Ingress and NetworkPolicy

- **Service** (`charts/authentik/templates/service.yaml`): add `- name: http-public, port: 9080`.
- **Ingress** (`charts/authentik/templates/ingress.yaml`): the backend port becomes `9080`. The same template serves all five profiles, so every ingress class (nginx, alb, gce, azure-application-gateway) moves together.
- **NetworkPolicy** (`templates/networkpolicies.yaml`, `<release>-authentik-ingress`): add an allow-all rule for port `9080`, for the ingress controller and load-balancer health checks. The existing allow-all rule on `9000` stays: in-cluster callers and probes use it. Narrowing `9000` to the cluster is #15's job (sub-project D).
- In-cluster callers keep `9000` (token minting by in-cluster clients) and `8443` (the API's discovery, JWKS and admin calls).

### 1c. The sidecar's readiness probe

The tls-proxy container's probe changes from the exec `curl` against `8443` (`deployment-server.yaml:173-178`) to:

```yaml
readinessProbe:
  httpGet: { path: /-/health/live/, port: 9080 }
  periodSeconds: 10
  timeoutSeconds: 5
  failureThreshold: 5
```

- **Why:** GCE and AGIC infer a backend's health check from the HTTP readiness probe of the container serving that backend port. The tls-proxy container now serves the Ingress backend, and with an exec probe they would fall back to `/`, which Authentik answers with a redirect, so the backend would be marked unhealthy.
- **Declared port:** the tls-proxy container's `ports` gains `- containerPort: 9080` (`name: http-public`). GKE and AGIC infer from a readiness probe only on a port the container declares; today it declares only `8443` (`deployment-server.yaml:158-160`).
- **Coverage is unchanged:** both listeners live in one nginx process with one config, so a passing probe on `9080` also proves `8443` is listening.
- **B's reason for the exec probe no longer applies:** B avoided a kubelet `httpGet` on `8443` so that no NetworkPolicy had to admit it. `9080` is admitted by 1b's rule anyway.
- The timeout and threshold keep B's final values.

---

## 2. Blueprints

### 2a. Admin-automation mints only in-cluster (closes #4's admin-automation item)

A new expression policy, bound to the `iverson-admin-automation` application:

```yaml
- model: authentik_policies_expression.expressionpolicy
  id: admin-automation-in-cluster-only
  identifiers: { name: iverson-admin-automation-in-cluster-only }
  attrs:
    expression: |
      # The public listener sets this header on every request it forwards, so a request without it
      # did not come through the ingress: this client mints only from inside the cluster.
      return "HTTP_X_IVERSON_PUBLIC_INGRESS" not in request.http_request.META
- model: authentik_policies.policybinding
  identifiers:
    policy: !KeyOf admin-automation-in-cluster-only
    target: !Find [authentik_core.application, [slug, iverson-admin-automation]]
    order: 0
```

- **Where:** in the Helm service-clients blueprint (`charts/authentik/templates/secret-service-clients.yaml`), after the admin-automation application. Also in `blueprints/compose-only/service-clients.yaml` for parity; in compose the header never appears, so the policy always passes there.
- **Effect:**
  - a `client_credentials` request through the public listener gets `invalid_grant`;
  - in-cluster CI (the documented use, `docs/user-management-and-security.md:475`), port-forwards and compose are unaffected;
  - other clients have no binding and are unaffected.
- **Why not host or IP:** Authentik honours a client-supplied `X-Forwarded-Host` from any private-range peer, and its client IP is the leftmost `X-Forwarded-For` entry (verified assumptions 9–10). Either would be spoofable behind a load balancer that passes those headers through.

### 2b. The bootstrap token is removed (closes #4's bootstrap-token item)

Nothing in the Helm chart uses `AUTHENTIK_BOOTSTRAP_TOKEN` after first boot. Authentik's own bootstrap blueprint creates the `authentik-bootstrap-token` API token only when the variable is set.

- `charts/authentik/templates/deployment-server.yaml:117-119` and `deployment-worker.yaml:63-65`: remove the `AUTHENTIK_BOOTSTRAP_TOKEN` env entries.
- `charts/authentik/templates/secret.yaml`: stop generating `bootstrap-token`, and stop copying it in the `lookup` branch. An upgrade rewrites the Secret without the key; `secret-key` and `bootstrap-password` are untouched.
- **The Helm service-clients blueprint** gains:

  ```yaml
  - model: authentik_core.token
    state: absent
    identifiers: { identifier: authentik-bootstrap-token }
  ```

  This deletes the token on existing deployments and is a no-op on fresh installs.
- **`docs/runbooks/grpc-admin-auth-cutover.md:57`** reads the `bootstrap-token` key to call the admin API when confirming the blueprint applied. That check becomes an `ak shell` read of the provider's `client_id` inside the worker pod (`kubectl exec`), which needs no API token.
- **Compose keeps the bootstrap token.** Its dev tooling and the Authentik test fixture (`Iverson.Api.Tests/Tenancy/AuthentikContainerFixture.cs`) use it.
- **The akadmin password stays** as the break-glass credential. Logging in with it goes through the default authentication flow, which enforces MFA (`blueprints/mfa-enforcement.yaml`), and after 1a the admin interface is reachable only in-cluster.

### 2c. The console provider issues no refresh tokens (closes #12)

In the Helm service-clients blueprint, `iverson-oidc-default` (`secret-service-clients.yaml:337-372`):
- `grant_types: ["authorization_code"]`: drop `refresh_token`.
- Remove the `offline_access` scope mapping (`:371`).
- Add `access_token_validity: "hours=1"`. That's Authentik's default today, now stated explicitly.

Also:
- **`iverson-admin-automation`** (the `{{- if eq $name "admin-automation" }}` branch of the provider loop): add `access_token_validity: "hours=1"`, also today's default.
- **`iverson-acting-user`** already states `hours=2`.
- **The compose blueprint's `iverson-oidc-default`** (`compose-only/service-clients.yaml:166`) drops `refresh_token` from its grants. It never carried the `offline_access` mapping.
- **The test-only `iverson-loadtest-human` client** keeps its refresh grant, because LoadTest's acting-user token provider uses it.

The console requests no `offline_access` and runs no silent renewal (`Iverson.AdminUI/src/auth/AuthProvider.tsx:27-57`), so it is unaffected.

### 2d. The recovery flow ends at the console

`blueprints/recovery-flow.yaml` (shipped to Helm by `blueprints-configmap.yaml`'s glob, and mounted by compose) gains a redirect stage bound after the user-login stage:

```yaml
- id: iverson-recovery-redirect-console
  model: authentik_stages_redirect.redirectstage
  identifiers: { name: iverson-recovery-redirect-console }
  attrs:
    mode: static
    target_static: !Env [IVERSON_CONSOLE_URL, "http://localhost:5173/"]
- model: authentik_flows.flowstagebinding
  identifiers:
    target: !KeyOf flow
    stage: !KeyOf iverson-recovery-redirect-console
  attrs:
    order: 4
```

- The binding follows the file's own convention: `order` in attrs, keyed on `(target, stage)`.
- **Environment:**
  - Helm `deployment-worker.yaml` sets `IVERSON_CONSOLE_URL={{ .Values.global.externalScheme }}://{{ .Values.global.ingressHost }}/admin/`;
  - compose's `authentik-worker` sets `IVERSON_CONSOLE_URL=http://localhost:5173/`.
- **Effect:** an invited user who sets a password lands on the console instead of `/if/user/`, which 1a blocks. The recovery flow has already signed the user in, so the console's authorize request issues a code without running the authentication flow: the invitee's first console session carries no second factor. MFA enrolment happens at the first login in a fresh session, when the default authentication flow forces it (user's choice, CDR-1 §3.1 D).

---

## 3. API and configuration

### 3a. Issuer (closes the `ExternalIssuer` defect)

Authentik's global issuer mode stamps `iss` with the minting request's own scheme and host, at the root path: `https://authentik.<host>/`. The API's `ExternalIssuer` is the provider path, so it never matches. Since B, the acting-user scheme accepts only `InternalIssuer`.

- **Helm** (`charts/api/templates/deployment.yaml:153-154`): `Authentication__ExternalIssuer` becomes `{{ .Values.global.externalScheme }}://authentik.{{ .Values.global.ingressHost }}/`.
- **Compose** (`docker-compose.yml:515`, `:632`): `Authentication__ExternalIssuer=http://localhost:9000/`, the issuer of tokens the dev console mints through its authority `http://localhost:9000/application/o/iverson-api/` (`Iverson.AdminUI/.env.development:2`).
- **`Program.cs:196-198`, the acting-user scheme:** `ValidIssuers` becomes the non-empty values of `[Authentication:InternalIssuer, Authentication:ExternalIssuer]`. JwtBearer still appends the metadata document's issuer, as it does today.
- **The default scheme** (`Program.cs:162-171`) already lists `ExternalIssuer`; only its value changes.

### 3b. Recovery links (closes #16's lifetime, and the in-cluster-host defect)

In `IdpAdminClient.TriggerPasswordRecoveryAsync` (`Iverson.Api/Tenancy/IdpAdminClient.cs:101-133`):

- **Lifetime:** the `/recovery/` body becomes `{"token_duration": "minutes=15"}`, a constant in the client (it is `{}` today, which means the tenant default of 30 minutes).
- **Public host:** `Authentik:PublicBaseUrl` is read from configuration. When it is set, the returned link's scheme and authority are replaced with it, keeping path and query (the `flow_token`). When it is unset (Development, tests), the link is returned unchanged.
- **Constructor:** `IdpAdminClient` takes `IConfiguration`. DI resolves it (`Program.cs`, `AddSingleton<IIdpAdminClient, IdpAdminClient>`). The two test constructions (`AuthentikAdminClientTests.cs:73`, `AuthentikRecoveryFlowIntegrationTests.cs:73`) pass an empty configuration, which keeps every existing assertion true.
- **Settings:**
  - Helm (`charts/api/templates/deployment.yaml`) sets `Authentik__PublicBaseUrl={{ .Values.global.externalScheme }}://authentik.{{ .Values.global.ingressHost }}`;
  - compose sets `Authentik__PublicBaseUrl=http://localhost:9000` for the API and the worker.
- **Unchanged:** the link is still returned only in the gRPC response and never logged. LoadTest's recovery follower takes only the flow slug and `flow_token` from the link (`Iverson.LoadTest.Tests/Auth/AuthentikFlowExecutorClientTests.cs:66`), so the rewrite does not affect it.

---

## 4. Tests and verification

### 4a. Unit and integration

- **Api.Tests, scheme wiring:** extend B's `BothSchemes_FetchMetadataOverTls_TrustTheCa_AndAcceptTheInternalIssuer` pattern. Assert that each scheme's `ValidIssuers` contains `Authentication:ExternalIssuer`. The acting-user case fails on today's code.
- **Api.Tests, `AuthentikAdminClientTests` (fake handler):**
  - the `/recovery/` request body carries `token_duration` = `minutes=15`;
  - with `Authentik:PublicBaseUrl` set, the link's scheme and host are replaced and its path and query are intact;
  - without it, the link is unchanged.
- **The live Authentik suite** (`AuthentikRecoveryFlowIntegrationTests`) runs unchanged against the new blueprints. `RecoveryFlow_UserWithNoEnrolledFactor_CompletesAndSignsTheUserIn` asserts `xak-flow-redirect` after the password and a signed-in session; the redirect stage runs after the login stage, so both still hold. The suite also proves Authentik accepts `token_duration`.
- **Mutation checks:** each new test must fail when its change is reverted.

### 4b. Helm

- **Render all five profiles** (cloud profiles with their placeholders filled for the render) and assert:
  - the `9080` server and its allowlist;
  - the Ingress backend on `9080`;
  - the Service port;
  - the NetworkPolicy rule;
  - the `httpGet` readiness probe;
  - the tls-proxy container declares `containerPort: 9080`;
  - no `AUTHENTIK_BOOTSTRAP_TOKEN` anywhere;
  - `IVERSON_CONSOLE_URL`, `ExternalIssuer` and `PublicBaseUrl` with their profile values.
- **`nginx -t`** on the rendered sidecar config in the pinned image.

### 4c. Compose live (isolated project)

- The conformance `identity` scenario in all five languages.
- LoadTest's tenant-admin bootstrap, which follows `CreateTenant`'s recovery link.
- A Chromium login on the dev console client through `localhost:9000` (password, then TOTP enrolment, then the code exchange). The API accepts the resulting token, which proves the compose issuer fix.

### 4d. kind live (`values-laptop`, ingress-nginx)

Chromium is pointed at `authentik.iverson.local` through the ingress.

- **Browser flows:**
  - console-client login with TOTP enrolment;
  - logout;
  - a recovery link from `CreateTenant`, which must carry the public host, expire about 15 minutes out, and end at `/admin/`.
- **Blocked paths:** `/if/admin/`, `/if/user/` and `/api/v3/core/users/` return 404 through the ingress.
- **Admin-automation:** a mint through the ingress returns `invalid_grant`, and a mint through a port-forward to `9000` succeeds.
- **Issuer:** tokens minted through the public host pass the API on the default and acting-user schemes.
- **Sidecar readiness:** the server pod shows `tls-proxy` Ready under the `9080` probe, through the NetworkPolicy.
- **Bootstrap token:** `authentik-bootstrap-token` no longer exists, checked with `ak shell` in the server pod.
- **Your state:** containers, volumes, networks and images are snapshotted before the check and diffed after teardown.

---

## Verified assumptions

Probes ran on an isolated Authentik 2026.5.3 (compose subset, project `csr10cprobe`). Browser flows were driven by Chromium 149 through `playwright-core`.

| # | Assumption | Evidence |
|---|---|---|
| 1 | The paths a browser needs are those in 1a's table | Chromium drove the console-client authorize request through password, forced TOTP enrolment and the code callback; then end-session; then an orchestrator-created recovery link. Non-static paths seen: `/application/o/authorize/`, `/application/o/iverson-api/end-session/`, `/if/flow/{default-authentication-flow,default-invalidation-flow,iverson-recovery}/`, `/api/v3/flows/executor/<slug>/`, `/flows/-/default/authentication/`, `/`. Recovery alone also loaded `/if/user/` and its five `/api/v3` calls (users/me, applications, admin/version, enterprise/license/summary, events/notifications). No websocket |
| 2 | The console never calls Authentik's `/api/v3` | `grep` of `Iverson.AdminUI/src` for `api/v3`, `/if/user`, `/if/admin`: no hits |
| 3 | No tool reaches Authentik's admin API through the ingress | Only `/api/v3/flows/executor/` appears outside the API (`AuthentikFlowExecutorClient.cs:311, 363`, `deploy/scripts/mint_acting_user_token.py:507`); it is allowlisted |
| 4 | Global-mode `iss` is the minting host's root | Login via `127.0.0.1:9000` gave `iss=http://127.0.0.1:9000/`. A `client_credentials` mint with `Host: authentik.probe.invalid` and `X-Forwarded-Proto: https` gave `iss=https://authentik.probe.invalid/` |
| 5 | The acting-user scheme accepts only the internal issuer today | `Program.cs:198`, `ValidIssuers = [internalIssuer]` |
| 6 | The console requests no refresh token, and the provider issues one only on `offline_access` | `AuthProvider.tsx:27-57` (no `offline_access`, `automaticSilentRenew: false`). The live login token response had no `refresh_token`. The grant and mapping are at `secret-service-clients.yaml:367, 371`. LoadTest's refresh use is via `iverson-loadtest-human` only (`AuthentikFlowExecutorClient.cs:320, 415`) |
| 7 | Authentik's provider defaults are access `hours=1`, refresh `days=30` | `providers/oauth2/models.py:267-276` in the image |
| 8 | Application policies are evaluated on `client_credentials` grants | `providers/oauth2/views/token.py:368, 492, 531` call `__check_policy_access`. Live: a bound policy refused one mint and allowed another |
| 9 | Authentik honours a client-supplied `X-Forwarded-Host` from a private-range peer | Live: a mint with `X-Forwarded-Host: authentik-server:9000` passed a host policy that refused the public host, and its token's `iss` used the forwarded host |
| 10 | Authentik's client IP is the leftmost `X-Forwarded-For` entry, and its Go layer always adds the header | `root/middleware.py:191-208` takes `ips[0]`. Live (policy execution log): `X-Forwarded-For: 10.1.2.3, 203.0.113.9` gave `ip=10.1.2.3`; with no header sent, the policy still saw `X-Forwarded-For=<peer>` |
| 11 | A custom request header reaches policies as `HTTP_X_IVERSON_PUBLIC_INGRESS` | Live: the policy logged `public='1'` with the header and `None` without it. The marker policy refused and allowed accordingly |
| 12 | Blueprints support policy bindings, `state: absent`, and the redirect stage | `/blueprints/example/flows-login-2fa.yaml:72-80` (`authentik_policies.policybinding`), `/blueprints/example/flows-recovery-email-verification.yaml:158` (`state: absent`), `blueprints/v1/common.py:61` (`ABSENT`), `schema.json:4707` (`authentik_stages_redirect.redirectstage` with `mode`, `target_static`) |
| 13 | The bootstrap token exists only when `AUTHENTIK_BOOTSTRAP_TOKEN` is set, with identifier `authentik-bootstrap-token` | `/blueprints/system/bootstrap.yaml:15, 36-45` (`conditions: [!If [!Context token]]`) |
| 14 | Only the server and worker deployments and `secret.yaml` use the bootstrap token in Helm, plus one runbook | `grep` for `bootstrap-token`/`BOOTSTRAP_TOKEN` outside docs: `deployment-server.yaml:117`, `deployment-worker.yaml:63`, `secret.yaml`. Plus compose, `generate-compose-secrets.sh`, `AuthentikContainerFixture.cs` and the AdminUI README, all compose-side. In docs, `docs/runbooks/grpc-admin-auth-cutover.md:57` reads the Helm Secret's key (2b). `ak shell` with stdin runs in the 2026.5.3 worker (CDR-1); the runbook's exact command against a Helm pod is UNVERIFIED |
| 15 | The recovery endpoint takes `token_duration`, and the link host comes from the request | `core/api/users.py:490-493, 693-695` (`token_duration`), `:707` (`build_absolute_uri`) |
| 16 | The recovery link is not usable from outside the cluster today | Assumption 15. The API calls Authentik at `Authentik:BaseUrl` (`https://<release>-authentik:8443` since B, `deployment.yaml:186`). LoadTest's follower deliberately ignores the link's host (`AuthentikFlowExecutorClientTests.cs:66`) |
| 17 | The recovery flow ends at `/if/user/` today | Live: the recovery link completed the prompt stage, then `xak-flow-redirect`, and landed on `/if/user/#/library` |
| 18 | The recovery bindings key on `(target, stage)` with `order` in attrs, and the last order is 3 | `recovery-flow.yaml:125-160` |
| 19 | The Helm blueprints ConfigMap ships `recovery-flow.yaml` | `blueprints-configmap.yaml` ranges over `.Files.Glob "blueprints/*.yaml"` (`compose-only/` is excluded) |
| 20 | LoadTest's follower and the live recovery test accept a final redirect to another URL | `AuthentikFlowExecutorClient.cs:200-201, 398-399` (returns on any `xak-flow-redirect`); `AuthentikRecoveryFlowIntegrationTests.cs:312-325` asserts the component and the signed-in user, not the target |
| 21 | One Service and one Ingress serve Authentik on every profile | `charts/authentik/templates/service.yaml` (ports 9000, 8443) and `ingress.yaml:29`. The profiles set only class, annotations and TLS secret (`values-{aws,azure,gcp}.yaml`) |
| 22 | The current NetworkPolicy admits any source on 9000 and only the API on 8443 | `templates/networkpolicies.yaml:595-611` |
| 23 | Both workers can carry `IVERSON_CONSOLE_URL` | Helm `deployment-worker.yaml:46` (`env:`); compose `authentik-worker` `environment:` (`docker-compose.yml:399`). Blueprints are applied by the worker, which resolves `!Env` |
| 24 | `IdpAdminClient` has no configuration dependency today, and two tests construct it | `IdpAdminClient.cs:41` (primary constructor with `IHttpClientFactory`, `ILogger`); `AuthentikAdminClientTests.cs:73`, `AuthentikRecoveryFlowIntegrationTests.cs:73` |
| 25 | The compose console's authority is `localhost:9000` | `Iverson.AdminUI/.env.development:2`; compose `ExternalIssuer` today at `docker-compose.yml:515, 632` |
| 26 | GCE and AGIC derive health checks from the serving container's HTTP readiness probe, on a port the container declares | Documented behaviour of both controllers (GKE "Troubleshoot Ingress health checks": the probe port must be the `containerPort`, which must match the Service `targetPort`; AGIC "Probes": probing a port not exposed on the pod is unsupported). **Not verified live** (no cloud here). The design depends on it only through keeping an `httpGet` probe on the declared serving port |
| 27 (C2) | JWT federation yields a short-lived JWT the admin API accepts with exactly the orchestrator role | Live: an OAuth source with a static JWKS, a provider with `jwt_federation_sources` and the `goauthentik.io/api` scope, and a pre-created `<provider>-<sub>` user in `iverson-admin-orchestrators`. A self-signed assertion minted a 300-second token: `GET users` 200, `POST user` 201, recovery on its own user 200, `GET rbac/roles` 403. Another `sub` signed by the same key minted a token with no rights (403) |
| 28 (C2) | A self-renewing API token bounds nothing | `core/api/tokens.py:145-158` (any user may create API tokens for itself and gets `view_token_key`); `core/models.py:1256-1272` (expired API-intent tokens rotate their key rather than being deleted) |
| 29 | The recovery flow's sign-in carries into the console | CDR-1, live: after a recovery that ended in `xak-flow-redirect` to the console, the console client's authorize request issued a code with no flow challenge; the token had `amr=null` and the user had 0 TOTP and 0 WebAuthn devices. Authentik runs the authentication flow at authorize only for an unauthenticated user or on `prompt=login`/`max_age` (`providers/oauth2/views/authorize.py:428-489`) |
| 30 | The 1a listener, as corrected, serves complete browser flows | CDR-1, live: `nginx -t` passes on the per-location `proxy_pass` layout in the pinned image (the server-level layout fails: `"proxy_pass" directive is not allowed here`). Through it on `9080`: console login with forced TOTP enrolment, logout and recovery with zero 404s; `/-/health/live/` 200; the 2a policy returned `invalid_grant` on `9080` and minted on `9000` |

---

## Known issues / accepted as out of scope

- **`9000` stays reachable from inside the VPC** on AWS and Azure, and a direct pod-IP request there bypasses the public listener. That is #15's exposure, and sub-project D narrows it.
- **No public self-service.** With `/if/user/` blocked publicly, users cannot manage their own MFA devices or Authentik settings. A lost device needs an operator, using the in-cluster admin interface. A login started directly at the Authentik host, not from the console, ends on a 404 after authentication.
- **The orchestrator token still never expires** until C2 lands. After 1a, using it needs reach to `9000` or `8443` from inside the cluster or VPC.
- **The orchestrator's ability to create a user directly into `operators`** is an Authentik permission-model limit (2026.5.3 has no group-scoped `add_user`). It is recorded in the blueprint and untouched here.
- **#16 is only partly closed: invitees stay MFA-free past the link.** The recovery flow signs the user in, so the first console session after recovery runs no second factor; the first factor is enrolled at the first login in a fresh session (user's choice, CDR-1 §3.1 D). The link now lives 15 minutes, carries the public host, and still travels the ingress→pod hop in plaintext behind the edge's TLS (#5's mesh item).
- **The cloud half of the issuer match is unverified.** Helm's `ExternalIssuer` assumes each cloud load balancer sends `X-Forwarded-Proto: https`; 4d proves the match on ingress-nginx only (user's choice, CDR-1 §3.2 A).
- **Load-balancer health-check paths on ALB** are not set for the Authentik Ingress. `/` answers with a redirect through the new listener, exactly as it does on `9000` today. Whether that marks the target unhealthy on AWS is unverified here.
