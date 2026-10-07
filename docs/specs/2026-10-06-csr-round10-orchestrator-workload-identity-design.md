# CSR Round 10: Orchestrator Workload Identity Design (Sub-project C2)

**Source review:** `docs/criticalreviews/2026-10-03-iverson-critical-security-review-10.md` (reviewed at `a8c48db4`), finding #4.

**Goal:** stop the API using a long-lived credential for Authentik's admin API on every Helm profile (kind/laptop, AWS, Azure, GCP).

Today the API sends `Authentik__AdminToken`, a never-expiring Authentik API token (`expiring: false`) held in the `<release>-authentik-admin-orchestrator-token` Secret. The `iverson-admin-orchestrator` user also has a generated password (`<release>-authentik-admin-orchestrator-user`) that nothing reads.

After this change, the API proves its identity with its own Kubernetes service-account token, a projected token with a dedicated audience, valid for 10 minutes and rotated by the kubelet. It exchanges that token at Authentik for a 5-minute access token. Both static credentials are removed from Helm.

C1 split this item out (C1 spec, "Split", and Verified assumptions 27–28). Sub-projects A, B, C1, D and E are merged and pushed.

**Decisions (user, 2026-10-06):**
- Every Helm profile switches. Compose keeps the static token.
- Authentik gets the cluster's service-account signing keys through a `kubectl proxy` relay sidecar in the Authentik worker. That was option A, chosen over anonymous JWKS access and over the clouds' public issuer URLs.

**Global constraints**
- **One branch, one merge.** No proto or SDK change.
- **Cloud profiles get static verification only:** render, kubeconform and the guards. Behaviour is verified live on a throwaway kind cluster.
- **Security write-ups** (comments, test names, docs) describe conditions and behaviour, never step-by-step exploitation.
- **`docs/` is gitignored:** commit docs files with `git add -f`.
- **The user's running compose stack** (`iversonserver` containers, volumes, networks, images) is never started, stopped or modified. Neither are another session's `rfdt-qdrant`/`rfdt-tei` containers or its `tei_models` volume. Live checks use throwaway resources, which they delete afterwards.
- **Memory:** run one heavy process at a time, because a WSL OOM kills the whole VM.

---

## 1. The API's credential

### 1a. Two modes, chosen by configuration

The `iverson.authentik` named HTTP client (`Program.cs:387-395`) gets its bearer token one of two ways:

| Mode | Used by | Configuration |
|---|---|---|
| Static | compose, Development test hosts | `Authentik:AdminToken` (unchanged) |
| Federated | every Helm profile | `Authentik:OrchestratorAssertionPath` (the projected-token file) and `Authentik:OrchestratorClientId` (`iverson-admin-orchestrator`) |

`AuthentikTrust.ValidateStartup` already gates the API role outside Development (`AuthentikTrust.cs:66-91`). It gains one rule: exactly one mode is configured. The startup check fails in two cases:
- both `Authentik:AdminToken` and `Authentik:OrchestratorAssertionPath` are set;
- neither is set.

In federated mode it also requires `Authentik:OrchestratorClientId`. The worker role and Development hosts skip the guard, as they do today.

Only `IdpAdminClient` uses the named client (`IdpAdminClient.cs:43`). Its methods and the two gRPC services that call it (`TenantAdminGrpcService`, `TenantLifecycleGrpcService`) don't change.

### 1b. The federated token handler

A new `DelegatingHandler` in `Iverson.Api/Tenancy`, attached to the named client in federated mode.

**Minting an access token:**
1. Read the assertion file fresh. The kubelet rewrites it before it expires, so the handler never caches the file contents.
2. POST to `/application/o/token/` on `Authentik:BaseUrl`. That is the same 8443 TLS hop, using the same `AuthentikTrust` handler that trusts the deployment CA. The form fields are:
   - `grant_type=client_credentials`
   - `client_id=<Authentik:OrchestratorClientId>`
   - `client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer`
   - `client_assertion=<file contents>`
   - `scope=goauthentik.io/api`
3. Read `access_token` and `expires_in` from the response. Authentik also returns an `id_token` and a `scope`, and the handler ignores both.

**Caching and refresh:**
- The handler caches the access token.
- It mints a new one when less than 60 seconds of `expires_in` remain.
- A single semaphore serialises minting, so concurrent requests never mint twice.

**Errors:** each of these throws an `InvalidOperationException` that names the step:
- the assertion file is missing or empty;
- the exchange returns a non-2xx response, including `invalid_grant`;
- the exchange hits a network error;
- the response has no `access_token`.

`CreateTenant` and `InviteUser` then fail just as they do today when Authentik is unreachable.

**What the handler leaves out:**
- It never logs the assertion or the access token.
- It does not retry on an error status. Authentik answers an invalid or expired bearer token with 403, the same status as a legitimate permission refusal (Verified assumption 7), so a status-triggered retry could not tell the two apart.

### 1c. The API pod

`charts/api/templates/deployment.yaml`:

- **Projected volume.** It holds a single `serviceAccountToken` source (`audience: iverson-authentik-orchestrator`, `expirationSeconds: 600`) and is mounted read-only at `/var/run/secrets/iverson/authentik-orchestrator/`.
  - `automountServiceAccountToken: false` stays as it is, so this is the pod's only Kubernetes credential.
  - The API server rejects a token with this audience (Verified assumption 23), so the token is good for this exchange and nothing else.
- **Environment.**
  - `Authentik__AdminToken` is replaced by `Authentik__OrchestratorAssertionPath=/var/run/secrets/iverson/authentik-orchestrator/token` and `Authentik__OrchestratorClientId=iverson-admin-orchestrator`.
  - The service account (`<release>-api`, `deployment.yaml:10-13`) is unchanged. Its token's `sub` is `system:serviceaccount:<namespace>:<release>-api`.

## 2. The Authentik side

Authentik 2026.5.3 checks only the signature and `exp` of a federated JWT. The audience check is disabled (`verify_aud: False`) and there is no issuer check (Verified assumption 13). Before it creates or resolves any user, though, it evaluates the application's policies with the decoded JWT in `request.context["oauth_jwt"]`. Pinning is therefore done by policy.

### 2a. A separate federation blueprint (`orchestrator-federation.yaml`)

The federation objects get a blueprint file of their own, delivered through `templates/blueprints-configmap.yaml`. It holds no credentials, so it goes in the ConfigMap like the other structural blueprints.

The reason for a separate file: a blueprint that fails validation applies none of its entries (Verified assumption 27). Saving the source fetches the JWKS, so if the relay were unreachable and these entries sat inside `service-clients.yaml`, none of the OAuth clients could be created or updated. With a separate file, a relay outage delays only federation, and Authentik re-applies the failed blueprint at its hourly discovery run.

| Object | Settings |
|---|---|
| Source property mapping `Iverson: k8s workload username` | `return {"username": "iverson-admin-orchestrator-workload"}`. This maps every accepted assertion to one fixed user and keeps the `:` of a Kubernetes `sub` out of usernames. It runs only after the policies below have passed. |
| OAuth source `iverson-k8s-workload` | `provider_type: openidconnect`; `oidc_jwks_url: http://127.0.0.1:8001/openid/v1/jwks`; blank authorization, token and profile URLs; no login or enrolment flow; `user_property_mappings` set to the mapping above. It exists only as the trust anchor for federation. |
| OAuth2 provider `iverson-admin-orchestrator` | `client_type: confidential`; `client_id: iverson-admin-orchestrator`; `grant_types: ["client_credentials"]`; `jwt_federation_sources` set to the source; `property_mappings` set to the managed `goauthentik.io/api` scope; `access_token_validity: minutes=5`; the same flows, signing key and `issuer_mode: global` as the existing service clients |
| Application `iverson-admin-orchestrator` | Uses the provider above, with `policy_engine_mode: all`. The default `any` would let either binding alone pass. |
| Expression policy `iverson-admin-orchestrator-workload`, binding order 0 | Passes only when `sub` equals `system:serviceaccount:<namespace>:<release>-api` and `aud` (a list or a string) contains `iverson-authentik-orchestrator`. Helm renders the namespace and release name. |
| Expression policy `iverson-admin-orchestrator-in-cluster-only`, binding order 1 | `return "HTTP_X_IVERSON_PUBLIC_INGRESS" not in request.http_request.META`. This is the same single-line rule as C1's admin-automation policy, kept as its own object so this blueprint doesn't depend on another blueprint's apply order. The public listener sets the header on every path it forwards, `/application/` included (`configmap-tls-proxy.yaml:26-37`). |

### 2b. Changes to `service-clients.yaml` (no dependency on the relay)

Removing the static credential must never depend on the relay, so these changes go in the blueprint that always applies:

- **Add user `iverson-admin-orchestrator-workload`.**
  - It is a member of `iverson-admin-orchestrators`, and so holds the scoped `iverson-admin-orchestrator` role.
  - It has no password, which Authentik stores as unusable.
  - It must be pre-created:
    - A user created by an exchange instead gets an `expires` attribute, and Authentik deletes it once the assertion expires.
    - A user created that way also gets no group.
    - A pre-created user keeps its group through every exchange. An exchange overwrites only its name, email, path, type and attributes, and adds no `expires` (Verified assumption 12).
- **Remove the old user and its token.** The `iverson-admin-orchestrator` user and its `iverson-admin-orchestrator-token` token become `state: absent` entries, token first. Deleting the user deletes its password hash with it.
- **No re-granting.** The role's per-user permissions on users the old identity created survive the deletion, because InitialPermissions grants to the role, not to a user (`secret-service-clients.yaml:606-640`; Verified assumption 17). Nothing needs re-granting.
- **Remove the credential templates.** The `$adminOrchestratorPassword` and `$adminOrchestratorTokenKey` computations and the two Secrets (`secret-service-clients.yaml:95-110, 190-210`) are deleted. Helm deletes the Secrets on upgrade.
- **Comments.** The comment block that explained why the token never expires (`:675-682`) goes. The `RESIDUAL` note about `add_user` stays.

### 2c. The JWKS relay (`templates/deployment-worker.yaml`)

The relay is a native sidecar: an `initContainer` with `restartPolicy: Always`. Blueprints and the 3-hourly JWKS refresh run in the worker, and the worker container starts only after the sidecar's startup probe passes.

**Container:**
- **Image:** `registry.k8s.io/kubectl:<tag>`, held in the authentik subchart's `values.yaml` under `jwksRelay.image`. That is a nested key named `image`, so Dependabot's helm updater tracks it, and the image-scan workflow picks it up from the render. The plan resolves the exact tag; `v1.37.1` is current.
- **Command:** `proxy --address=127.0.0.1 --port=8001 --accept-paths=^/openid/v1/jwks$ --reject-methods=^(POST|PUT|PATCH|DELETE)$`.
- **Startup probe:** an exec probe, `["kubectl", "--server=http://127.0.0.1:8001", "get", "--raw", "/openid/v1/jwks"]`, with `periodSeconds: 1` and `failureThreshold: 30`. A TCP probe targets the pod IP and never reaches the loopback listener, so it restart-loops the sidecar (Verified assumption 26).
- **Security context:**
  - `runAsNonRoot: true` and `runAsUser: 65532`;
  - `readOnlyRootFilesystem: true`;
  - `allowPrivilegeEscalation: false`;
  - all capabilities dropped.
- **Resources:** requests of `10m` CPU and `32Mi` memory, with a `64Mi` memory limit.

**Credential.** A projected volume mounted read-only at `/var/run/secrets/kubernetes.io/serviceaccount`, where `kubectl` looks for in-cluster credentials. It holds:
- a `serviceAccountToken` with the default API audience and `expirationSeconds: 3600`;
- the `kube-root-ca.crt` ConfigMap's `ca.crt`;
- the pod's namespace, via the downward API.

`automountServiceAccountToken` stays `false`.

**What the relay's service account can read.** The worker's service account (`<release>-authentik`) gets no new RBAC. The default `system:service-account-issuer-discovery` ClusterRoleBinding already lets every service account read the issuer discovery documents (Verified assumption 22). The relay answers only the JWKS path and refuses everything else.

### 2d. NetworkPolicy (`templates/networkpolicies.yaml`)

A new egress policy selects only `app: <release>-authentik-worker` and contains `{{ include "iverson.apiServerEgress" . }}` (`_helpers.tpl:71-81`).

D's per-overlay `apiServerCidrs`, its fail-closed guard and the `networkPolicy.apiServerAnyDestination` fallback all apply unchanged. The Authentik server pods get no API-server egress. Authentik needs no internet egress.

## 3. Compose, tests, upgrade and docs

### 3a. Compose

Nothing changes in compose:
- `docker-compose.yml` still sets `Authentik__AdminToken`;
- `scripts/generate-compose-secrets.sh` still generates it;
- the compose-only blueprint (`blueprints/compose-only/service-clients.yaml`) keeps the static user and token.

The Api.Tests Authentik fixture keeps using compose's static path for its existing tests.

### 3b. Tests

**Unit tests (Api.Tests), handler:**
- one mint serves calls until the 60-second margin, and the next call after that mints again;
- concurrent calls produce a single mint;
- every mint re-reads the assertion file;
- a missing file, an empty file, a non-2xx exchange and a response without `access_token` each throw an error that names the step and contains neither the assertion nor any token.

**Unit tests (Api.Tests), guard:**
- both modes set: rejected;
- neither set: rejected;
- federated mode without a client ID: rejected;
- the worker role and Development: skipped.

**Integration (`AuthentikContainerFixture`, live Authentik 2026.5.3).** The test generates an RSA keypair and creates the source with a static `oidc_jwks`, using the fixture's bootstrap token. It also creates the provider, the application and both policies, then mints through the real handler and checks:
- the right `sub` and `aud`: `POST users` and `recovery` succeed, and `rbac/roles` is refused;
- a wrong `sub`, a wrong `aud`, or the public-ingress marker header: `invalid_grant`, with no new Authentik user created.

**Live on kind (laptop profile, during plan execution):**
- the full chart installs;
- the relay serves the JWKS, and the source holds the cluster's key ID;
- `CreateTenant` returns a recovery link;
- a token for another service account gets `invalid_grant`;
- both orchestrator Secrets and the `iverson-admin-orchestrator` user are absent.

**Cloud overlays:** render, kubeconform and the guards only.

### 3c. Upgrading an existing cluster

A single `helm upgrade` switches everything over. During the rollout, `CreateTenant` and `InviteUser` can fail for a short window, for two reasons:
- old API pods hold a token that `service-clients.yaml` deletes;
- new API pods can't mint until the worker has applied `orchestrator-federation.yaml`.

No data is affected.

A new section in `docs/runbooks/csr10-infrastructure-cutover.md` says three things:
- don't provision tenants while the upgrade rolls out;
- afterwards, confirm with one `InviteUser` that a recovery link comes back;
- confirm that both orchestrator Secrets are gone.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| 1 | The `iverson.authentik` named client sets its bearer token from `Authentik:AdminToken`, and a handler can be attached to its builder | `Program.cs:387-395` (`AddHttpClient(...).ConfigurePrimaryHttpMessageHandler(...)`) |
| 2 | The Authentik startup guard runs only for the API role outside Development | `AuthentikTrust.cs:66-91` (`if (workloadRole != "api" \|\| env.IsDevelopment()) return;`), called at `Program.cs:466` |
| 3 | Only `IdpAdminClient` uses the named client | grep: `HttpClientName` is used only at `IdpAdminClient.cs:55,141,206,214,243,251` and `Program.cs:387` |
| 4 | The 8443 in-cluster listener forwards `/application/o/token/` | `configmap-tls-proxy.yaml:11-16` (`listen 8443 ssl; location / { proxy_pass http://127.0.0.1:9000; }`) |
| 5 | The exchange returns `access_token` and `expires_in` | Live, isolated Authentik 2026.5.3: `200 {access_token, token_type: Bearer, scope: goauthentik.io/api, expires_in: 300, id_token}` |
| 6 | The access token carries exactly the orchestrator role's rights | Same probe: `GET users` 200; `POST users` gave pk 10; recovery on the created user 200; `rbac/roles` 403; recovery on `akadmin` 403 |
| 7 | An invalid or expired bearer token gets 403, not 401 | Same probe: a malformed bearer token got 403, and the deleted static token got 403 |
| 8 | The Api.Tests fixture holds an admin credential that can create the federation objects | `AuthentikContainerFixture.cs:111,242-244` (bootstrap token); C1 VA27 created a static-JWKS source and provider with an admin credential |
| 9 | A blueprint can create the `openidconnect` source with blank flow URLs and an `oidc_jwks_url`, and saving it fetches the JWKS | Probe: the v2 blueprint applied, and the source's `oidc_jwks` held the kind key ID `IIUrlSPw…` fetched from the URL; `sources/oauth/api/source.py:107-117` |
| 10 | The blueprint (and its fetch) runs in the worker | Probe: `ak apply_blueprint` in the worker container fetched from the in-network JWKS server; `blueprints/v1/tasks.py:191-236` (dramatiq actor) |
| 11 | A source user property mapping sets the federated username | Probe: after the exchange, no new user existed and `iverson-admin-orchestrator-workload` had been updated; `token.py` `__create_user_from_jwt` uses `mapped.get("username", …)` |
| 12 | The pre-created user keeps its group, and Authentik never deletes it | Probe after the exchange: groups `['iverson-admin-orchestrators']`, attributes `{generated: True}` with no `expires`. `core/tasks.py:51-65` deletes generated users only if they have `expires`, which `token.py` sets only when the exchange creates the user |
| 13 | Policies see `oauth_jwt` and run before any user is created; `aud` and `iss` aren't verified | `providers/oauth2/views/token.py:381-430` (`verify_aud: False`, no issuer check) and `__post_init_client_credentials_jwt` (`__check_policy_access(app, request, oauth_jwt=token)` before `__create_user_from_jwt`); probe: rejected tokens created no user |
| 14 | `policy_engine_mode: all` is valid and requires both bindings | Probe: the application read back `all`; a correct token with the marker header got `invalid_grant` |
| 15 | The public listener marks token-endpoint requests | `configmap-tls-proxy.yaml:26-37` (the server-level `proxy_set_header X-Iverson-Public-Ingress 1`, and `location /application/`) |
| 16 | Users created by the federated identity get the role's per-user grants | Probe: recovery on `late-user`, created through the exchange, returned 200 |
| 17 | `state: absent` removes the old user and token, and per-user grants on earlier users survive | Probe: old user and token count 0 afterwards; recovery on `early-user`, created by the old static token before the upgrade, returned 200 with the federated token |
| 18 | A blueprint user with no password gets an unusable one | Probe: `has_usable_password()` returned `False` |
| 19 | The provider attributes are valid, and the exchange needs no client secret | Probe: the provider blueprint entry applied, and the exchange sent only `client_id` plus the assertion |
| 20 | Wrong-audience and API-audience tokens are refused | Probe: `invalid_grant` for a token with audience `somebody-else` and for one with the default API audience |
| 21 | JWKS refresh runs every 3 hours, in the worker | `sources/oauth/apps.py:51-58` (crontab `*/3`); `sources/oauth/tasks.py` (dramatiq actor, keeps the old JWKS on fetch failure) |
| 22 | Any service account can read the JWKS with an API-audience token, but anonymous reads are refused | kind v1.31: ClusterRoleBinding `system:service-account-issuer-discovery` → `system:serviceaccounts`; anonymous GET 403, token GET 200 |
| 23 | A projected audience token works with automount off, and the API server rejects it | kind: the `aud: ['iverson-authentik-orchestrator']` token (ttl 600 s, RS256, kid equal to the JWKS kid) got 401 from the API server |
| 24 | The `kubectl proxy` relay runs non-root with a read-only root filesystem and serves only the JWKS | kind: `registry.k8s.io/kubectl:v1.35.0`, uid 65532, `readOnlyRootFilesystem: true`, 0 restarts; JWKS 200, `/api` and secrets 403 |
| 25 | The kubectl image exists on registry.k8s.io | `registry.k8s.io/v2/kubectl/manifests/v1.35.0` returned 200; the tags list includes `v1.37.1` |
| 26 | A native sidecar's exec startup probe holds back the main container, and a TCP probe can't reach loopback | kind: the main container's first request at start got 200. With a `tcpSocket` probe the sidecar restarted 4 times while logging `Starting to serve on 127.0.0.1:8001` |
| 27 | A blueprint with one invalid entry applies nothing, and is retried hourly | Probe: with the JWKS server stopped, a 3-entry blueprint failed validation and neither sentinel group was created. `blueprints/v1/tasks.py:185-223` updates `last_applied_hash` only on success, and `apps.py:164-171` runs discovery hourly |
| 28 | D's API-server egress helper can be included in a new worker-only policy | `_helpers.tpl:71-81` (takes the root context); already included at `networkpolicies.yaml:193,248` |
| 29 | The worker pod label is `app: <release>-authentik-worker` | `deployment-worker.yaml:9,13` |
| 30 | The API service account is `<release>-api` | `charts/api/templates/deployment.yaml:10-13,51` |
| 31 | Dependabot reads a nested `image:` key, and image-scan reads rendered images | D's VA49 (`find_images_in_hash` recurses); the image-scan List step greps every rendered `image:` line |
| 32 | The API already reaches Authentik on 8443 | `networkpolicies.yaml:547-550` |
| 33 | Nothing else reads the two orchestrator Secrets | grep: only `charts/api/templates/deployment.yaml`, `secret-service-clients.yaml`, the compose-only blueprint and `scripts/generate-compose-secrets.sh` (compose) |
| 34 | The LoadTest and conformance tooling don't use the orchestrator token | grep: LoadTest mentions only `ak shell` help text (`Iverson.LoadTest/Program.cs:377`) |
| 35 | Native sidecars are on in every target Kubernetes version | Kubernetes docs: beta and on by default from 1.29, GA in 1.33. Terraform defaults to 1.30 on AWS, Azure and GCP (`modules/cluster-*/variables.tf`); kind ran 1.31 |
| 36 | Managed clusters rotate their signing keys, so a static JWKS snapshot would go stale | AWS EKS docs (irsa-fetch-keys): "rotates the OIDC signing key every seven days"; HashiCorp KB: EKS and AKS rotate automatically |

## Known issues / accepted as out of scope

- **New signing keys on managed clusters (unverified).** Whether EKS, AKS and GKE publish a new signing key before they start signing with it can't be checked without a cloud account. If a cluster signs with a key Authentik hasn't fetched yet, exchanges fail with `invalid_grant` until the next 3-hourly refresh, and tenant provisioning fails during that window.
- **Relay down while the blueprint applies.** Federation waits for the hourly re-apply. `service-clients.yaml` and the removal of the static credential are unaffected.
- **Rollout window.** Tenant provisioning can fail briefly during the upgrade (§3c).
- **Compose keeps the static, never-expiring token** (user decision).
- **The orchestrator role's global `add_user`** can still create a user directly into a non-superuser group. That is an Authentik permission-model limit that C1 recorded and left as is. It is unchanged here.
- **One new third-party image,** `registry.k8s.io/kubectl`, pinned and tracked by Dependabot.
- **The Authentik admin UI's source form** (server process) can't re-validate this source, because the relay runs only in the worker pod. The source is managed by blueprint, so nothing uses that form.
