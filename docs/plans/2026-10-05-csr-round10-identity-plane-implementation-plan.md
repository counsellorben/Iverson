# CSR Round 10 — Identity Plane (C1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-10-05-csr-round10-identity-plane-design.md` (commit SHA: `22982cca`)

**Goal:** Publish only the Authentik paths a browser needs, mint admin-automation tokens only in-cluster, remove the bootstrap token and the console's refresh grant, accept public-host issuers, and send invitees 15-minute links on the public host that end at the console.

**Architecture:**
- **Edge:** the existing tls-proxy nginx sidecar gains a public listener on `9080` with a path allowlist and a marker header, and the Authentik Ingress points at it.
- **Blueprints:**
  - an application policy on the marker;
  - removal of the bootstrap token and of the console provider's refresh grant;
  - a redirect stage that ends the recovery flow at the console URL, which Helm renders into the blueprint.
- **API:**
  - both JWT schemes accept the root-path external issuer;
  - `IdpAdminClient` asks for 15-minute links and rewrites their host to `Authentik:PublicBaseUrl`.

**Tech stack:**
- Helm 3;
- Authentik 2026.5.3 blueprints;
- nginx-unprivileged 1.27 (the pinned digest);
- .NET 10 (ASP.NET Core JwtBearer, xUnit, FluentAssertions, NSubstitute, Testcontainers);
- docker compose (podman);
- kind with Calico and ingress-nginx;
- Playwright-core 1.63 driving the cached Chromium 149.

---

## Global Constraints

- **One branch, one merge.** No proto change, no SDK change, no console change.
- **Security write-ups** (comments, test names, docs) stay at the level of conditions and behaviour, never step-by-step exploitation.
- **`docs/` is gitignored**, so commit docs files with `git add -f`.
- **The user's running compose stack** (`iversonserver` containers, volumes, networks, images) is never started, stopped or modified. Live checks use isolated compose projects (`-p <name>`, `--project-directory`, `container_name:` stripped, `down -v`) and a throwaway kind cluster that the check creates and deletes.
- **Compose secrets** are read from `Iverson.Server/.env` and never printed.
- **Session rules (from the user):**
  - Never push.
  - Never touch the `rfdt-qdrant` and `rfdt-tei` containers or the `tei_models` volume.
  - Kill processes by PID, never with `pkill -f`.
  - Run one `dotnet` process at a time, and cap heavy runs with `systemd-run --user --scope -p MemoryMax=<n>G`, because a WSL out-of-memory kills the whole VM.
  - Don't edit `docs/security/tma.md`.

Paths below use:
- `BR=/home/ben/repositories/Iverson/.worktrees/csr10-identity-plane` (the branch worktree);
- `SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad`;
- `H=$BR/Iverson.Server/deploy/helm/iverson`.

## File Structure

- **Modify (Helm, authentik subchart, under `$H/charts/authentik/`):**
  - `templates/configmap-tls-proxy.yaml`: the `9080` public server.
  - `templates/service.yaml`: port `http-public: 9080`.
  - `templates/ingress.yaml`: backend port `9080`.
  - `templates/deployment-server.yaml`: tls-proxy `containerPort: 9080` and an `httpGet` readiness probe; drop `AUTHENTIK_BOOTSTRAP_TOKEN`.
  - `templates/deployment-worker.yaml`: drop `AUTHENTIK_BOOTSTRAP_TOKEN`.
  - `templates/secret.yaml`: stop generating and copying `bootstrap-token`.
  - `templates/secret-service-clients.yaml`:
    - the admin-automation policy and binding;
    - `state: absent` on the bootstrap token;
    - console provider grants and mapping;
    - explicit lifetimes.
  - `templates/blueprints-configmap.yaml`: render the console URL into `recovery-flow.yaml`.
  - `blueprints/recovery-flow.yaml`: the redirect stage and its binding.
  - `blueprints/compose-only/service-clients.yaml`: the admin-automation policy and binding, and the console provider grants.
- **Modify (Helm, other):**
  - `$H/templates/networkpolicies.yaml`: allow `9080` on `authentik-ingress`.
  - `$H/charts/api/templates/deployment.yaml`: the root-path `ExternalIssuer`, and `Authentik__PublicBaseUrl`.
- **Modify (compose):** `Iverson.Server/docker-compose.yml`:
  - `IVERSON_CONSOLE_URL` on `authentik-worker`;
  - `ExternalIssuer` and `PublicBaseUrl` on `iverson-api` and `iverson-worker`.
- **Modify (API):**
  - `Iverson.Server/Iverson.Api/Program.cs`: the acting-user `ValidIssuers`.
  - `Iverson.Server/Iverson.Api/Tenancy/IdpAdminClient.cs`: `IConfiguration`, `token_duration`, the public-host rewrite.
- **Modify (docs):** `docs/runbooks/grpc-admin-auth-cutover.md`: the blueprint check uses `ak shell`.
- **Test (modify):**
  - `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikTrustTests.cs`: a both-schemes external-issuer theory.
  - `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikAdminClientTests.cs`: link-duration and public-host tests; `CreateClient` takes a configuration.
  - `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikRecoveryFlowIntegrationTests.cs`: the constructor gets an empty configuration.
- **Scratch only (never committed, under `$SCR/c1/`):** render checks, the Playwright flow script, the `CreateTenant` console, live-check environment.

## Inherited from spec

Verified by `thorough-brainstorming` and the two design reviews. They are not re-verified here.

| # | Assumption | Evidence |
|---|---|---|
| 1 | The browser path set is 1a's table | Chromium probe of login with TOTP enrolment, logout and recovery (spec row 1) |
| 2 | The console never calls `/api/v3` | grep of `Iverson.AdminUI/src` (spec row 2) |
| 3 | No tool reaches Authentik's admin API through the ingress | only `/api/v3/flows/executor/` outside the API (spec row 3) |
| 4 | Global-mode `iss` is the minting host's root | live mints (spec row 4) |
| 5 | Today the acting-user scheme accepts only `InternalIssuer` | `Program.cs:198` (spec row 5) |
| 6 | The console requests no refresh token; LoadTest's refresh use is `iverson-loadtest-human` only | spec row 6 |
| 7 | The provider defaults are access `hours=1`, refresh `days=30` | spec row 7 |
| 8 | Policies run on `client_credentials` | spec row 8 |
| 9–10 | Host and IP signals are spoofable behind a pass-through load balancer | spec rows 9–10 |
| 11 | The marker reaches policies as `HTTP_X_IVERSON_PUBLIC_INGRESS` | spec row 11 |
| 12 | Blueprints support policy bindings, `state: absent` and the redirect stage | spec row 12 |
| 13–14 | Bootstrap-token creation conditions and readers (the chart, the runbook) | spec rows 13–14 |
| 15–17 | The recovery endpoint takes `token_duration`; the link host comes from the request; the flow ends at `/if/user/` today | spec rows 15–17 |
| 18–20 | The recovery bindings key on `(target, stage)`, last order 3; the ConfigMap glob ships the file; the follower and the live test accept any final `xak-flow-redirect` | spec rows 18–20 |
| 21–22 | One Service and one Ingress; the current NetworkPolicy | spec rows 21–22 |
| 23–25 | The compose worker env; `IdpAdminClient`'s two test constructions; the compose console authority | spec rows 23–25 |
| 26 | GCE and AGIC need an HTTP readiness probe on a declared container port | documentation only (spec row 26) |
| 29–32 | The recovery sign-in carries into the console; the corrected listener serves full flows; `!Env` resolves in the applying worker behind a content-hash gate; the console's re-sign-in reuses the session | CDR-1 and CDR-2 live runs (spec rows 29–32) |

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | Every `Modify:` file exists at the path above | Read this session: `configmap-tls-proxy.yaml` (one `default.conf` key with the 8443 server), `service.yaml:8-12`, `ingress.yaml` (`number: 9000`), `deployment-server.yaml:150-185` (tls-proxy `ports` with only 8443; exec probe), `deployment-worker.yaml:60-65`, `secret.yaml`, `secret-service-clients.yaml:239-244` (`stringData.service-clients.yaml`, `entries:` at 4 spaces, items at 6), `blueprints-configmap.yaml:5-9`, `recovery-flow.yaml:155-160` (order-3 binding, then the brand entry), `compose-only/service-clients.yaml:118-175`, `networkpolicies.yaml:596-612`, `api/templates/deployment.yaml:152-154, 186-187`, `docker-compose.yml:399-420, 515, 530, 632, 640`, `Program.cs:188-199`, `IdpAdminClient.cs:41, 101-133, 345`, `docs/runbooks/grpc-admin-auth-cutover.md:51-71` |
| 2 | Code | The admin-automation application is created inside the provider loop, which closes at `secret-service-clients.yaml:335-336` (`{{- end }}` twice), before `iverson-oidc-default` | Read `:299-337` |
| 3 | Code | Entries in one blueprint apply in order, so a later `!Find [authentik_core.application, [slug, iverson-admin-automation]]` resolves | Spec row 30: the CDR-1 reviewer applied the policy and binding verbatim and got `invalid_grant` on 9080 and a mint on 9000 |
| 4 | Code | `JsonBody` serializes with `JsonSerializerDefaults.Web` (camelCase, which leaves `token_duration` unchanged), and the create body already sends `is_active` the same way | `IdpAdminClient.cs:345-348`; the existing test asserts `is_active` (`AuthentikAdminClientTests.cs:~104`) |
| 5 | Code | `AuthentikAdminClientTests`' fake handler records `Requests` and `RequestBodies`; `CreateClient` builds `new IdpAdminClient(factory, logger)` | `AuthentikAdminClientTests.cs:58-73`; `handler.RequestBodies[1]` is used at `:104` |
| 6 | Code | `UseSetting` reaches `Program.cs`' pre-Build configuration reads in `AuthTestWebApplicationFactory` | B's plan row 28 and its passing `BothSchemes_FetchMetadataOverTls_TrustTheCa_AndAcceptTheInternalIssuer` (`AuthentikTrustTests.cs:215-234`) |
| 7 | Consumer | The acting-user `ValidIssuers` change keeps B's wiring test true (it asserts `Contain(internal)`) | `AuthentikTrustTests.cs:231` |
| 8 | Consumer | `IdpAdminClient` is constructed in exactly one production registration and two tests | `Program.cs:392` (`AddSingleton<IIdpAdminClient, IdpAdminClient>`); `git grep "new IdpAdminClient("`: the two test files |
| 9 | Consumer | `AuthentikOrchestratorRoleBlueprintTests` reads both blueprint files as text, anchored on the role, `initialpermissions` and `is_superuser` entries; the new entries touch none of those anchors | `AuthentikOrchestratorRoleBlueprintTests.cs:52-148` |
| 10 | Consumer | `AuthentikContainerFixture` bind-mounts the repo's `blueprints/` directory at `/blueprints/custom`, so `recovery-flow.yaml` and `compose-only/service-clients.yaml` (with the new policy) apply in the live suite | `AuthentikContainerFixture.cs:28-49, 133-136` |
| 11 | Consumer | An invalid `x-acting-user-authorization` gets `Unauthenticated` (16); a valid one passes and can then get `PermissionDenied` (7) for an inactive tenant; the interceptor runs on every gRPC call | `ActingUserInterceptor.cs:12, 33-50`; `Program.cs:101` |
| 12 | Command | `helm template` renders all five profiles with the CI overrides after `helm dependency build`. Expected hosts: local and laptop `http://iverson.local`; aws, azure and gcp `https://iverson-ci-test.example.org` | Ran all five: rc=0. `values.yaml:39, 44`; `values-{aws,azure,gcp}.ci-override.yaml:2`, `values-{aws,azure,gcp}.yaml` (`externalScheme: "https"`) |
| 13 | Command | `kube-score` is installed, and B's baseline command (CI's flags) produces the per-profile CRITICAL list to diff | `which kube-score`; B plan Task 5 Step 1 |
| 14 | Command | `nginx -t` in the pinned image validates a mounted `default.conf` with any self-signed `tls.crt` and `tls.key` | Ran this session: `test is successful` (`$SCR/nginxt`) |
| 15 | Command | `ak shell -c` runs Python against Authentik's models (Django's `shell --command`); `authentik.flows.models.FlowToken` and `authentik.core.models.Token` exist | Image: `django.core.management.commands.shell` has `--command`; `flows/models.py:336`, `core/models.py:1220` |
| 16 | Command | Chromium launched with `proxy: { server: 'http://127.0.0.1:<port>' }` sends absolute-form requests that nginx routes by Host, with no port in `Host` | Ran this session against a Host-routed nginx: the `authentik.iverson.local` server answered with `http_host=authentik.iverson.local` |
| 17 | Command | kind maps host `127.0.0.1:8080` to the ingress's port 80 | `deploy/kind/kind-config.yaml:16-19` |
| 18 | Command | `Iverson.Client.Core` carries the generated `TenantLifecycleGrpcService` client in namespace `Iverson.Client.Contracts`, as LoadTest uses it | `tenant_lifecycle.proto:2`; `Iverson.LoadTest.csproj:10`; `Iverson.LoadTest/Program.cs:418-437` |
| 19 | Command | LoadTest provisions its tenant on `seed`, `write-path`, `read-path` and `all`, from `IVERSON_CLIENT_ID`/`_SECRET`/`TOKEN_ENDPOINT`/`CLIENT_SCOPE` plus the acting-user and tenant-admin passwords. It follows the recovery link, then logs the tenant admin in with TOTP enrolment | `Iverson.LoadTest/Program.cs:34, 38-74, 100-170` |
| 20 | Command | The kind Secrets the live check reads: `iverson-authentik-{loadtest,admin-automation,human-oidc,acting-user}-client` (`client-id`, plus `client-secret` for the confidential ones) and `iverson-authentik-smoke-test-user` (`password`) | `secret-service-clients.yaml:121-218` |
| 21 | Ordering | Tasks 1–5 touch disjoint lines. Tasks 3, 4 and 5 all edit `docker-compose.yml` (worker env `:399+`; `:515, :632`; `:530, :640`) and run in order; Task 2 does not touch it | File map above |
| 22 | Convention | Commit messages are lowercase imperative sentences ending with the `Co-Authored-By` line | `git log --oneline -8` |
| 23 | Command | `POST /api/v3/core/users/{pk}/set_password/` with `{"password": …}` sets a password and answers 204 (the compose bootstrap token holds the right) | `core/api/users.py:830-850` in the 2026.5.3 image |
| 24 | Consumer | The compose identity scenario and LoadTest log in their own users, the smoke and bypass users and the LoadTest tenant admin, with TOTP. The bypass group is an authorization bypass, not an MFA exemption. So Task 6's compose browser login uses a dedicated user | `compose-only/service-clients.yaml:282-301`; `mfa-enforcement.yaml` has no exemption; `Iverson.LoadTest/Program.cs:153-162` (the tenant admin's TOTP enrolment) |
| 25 | Command | The flow script's in-page `fetch` to `/application/o/token/` from a page on `BASE` mints the token through the browser's proxy path (the public host on kind). Playwright's `context.request` under the proxy tunnels with `CONNECT` and gets a 400. Logging out first deletes the session's unexchanged codes | CIR-1, live: stand-in ingress gave `console/second: token received` and `iss=http://authentik.iverson.local/ False`; compose shape gave `iss=http://localhost:9000/ False`; `context.request` gave nginx `400`, with `CONNECT authentik.iverson.local:80` in the proxy log; after logout `AuthorizationCode` rows for the user were 0, and stored codes gave `invalid_grant` |
| 26 | Behaviour | On the default scheme a rejected token gets HTTP 401 with no `grpc-status`; an accepted one gets `200` with `grpc-status: 0`. A bad acting-user token with a good service token gets `200` with `grpc-status: 16`. JwtBearer's IDX lines are not logged at the shipped level | CIR-1, live through the real `Program.cs` pipeline; `Iverson.Api/appsettings.json:5` (`"Microsoft.AspNetCore": "Warning"`) |
| 27 | Behaviour | On `values-laptop`, `iverson.local` carries a certificate and no admin-ui Ingress opts `/admin/` out of ingress-nginx's https redirect, so the proxied browser cannot load the redirect target; the first request off the Authentik origin is the flow's own target | `values-local.yaml:4-11`, `values-laptop.yaml:40-41` (`adminUi.enabled: false`), `charts/admin-ui/templates/ingress.yaml:39`; CIR-1, emulated: `recovery left Authentik for http://iverson.local/admin/` while the final page was `chrome-error://chromewebdata/` |
| 28 | Command | `ak shell -c` prints a banner (3 lines and a blank) before the value on stdout and logs JSON to stderr | CIR-1, live in the 2026.5.3 worker: 5 stdout lines ending in the value; `2>/dev/null \| tail -1` printed exactly `1` |
| 29 | Behaviour | The Helm blueprint's `state: absent` token entry applies without failing the blueprint and deletes an existing `authentik-bootstrap-token` | CIR-1, live: the edited Helm blueprint applied on an isolated Authentik that had the token; afterwards the token was gone |
| 30 | Behaviour | Authentik accepts `token_duration=minutes=15` on `/recovery/`, and the startup guard does not reject compose's `http://` `ExternalIssuer` | CIR-1, live: the link's `FlowToken` expired 14.9 minutes out; `Iverson.Api/Tenancy/AuthentikTrust.cs:66-95` checks only the metadata addresses, `Authentik:BaseUrl`, `InternalIssuer` presence and the CA |

## Tasks

### Task 1: The public Authentik listener (Helm)

**Files:**
- Modify: `$H/charts/authentik/templates/configmap-tls-proxy.yaml`
- Modify: `$H/charts/authentik/templates/service.yaml`
- Modify: `$H/charts/authentik/templates/ingress.yaml`
- Modify: `$H/charts/authentik/templates/deployment-server.yaml` (tls-proxy container)
- Modify: `$H/templates/networkpolicies.yaml` (`<release>-authentik-ingress`)

- [ ] **Step 1: Baseline `kube-score`** (CI's flags) before editing.

```bash
mkdir -p $SCR/c1 && cd $H && helm dependency build . >/dev/null
for v in values-local values-laptop values-aws values-azure values-gcp; do x=""; [ -f $v.ci-override.yaml ] && x="-f $v.ci-override.yaml"
  helm template iverson . -f $v.yaml $x | kube-score score --ignore-test pod-networkpolicy --ignore-test container-image-pull-policy --ignore-test container-security-context-user-group-id - \
  | awk '/^[a-z].*\//{obj=$1" "$2} /CRITICAL/{print obj" |"$0}' | sort > $SCR/c1/ks-before-$v.txt; done
```

- [ ] **Step 2: Add the `9080` server.** In `configmap-tls-proxy.yaml`, append after the existing `server { … }` block, inside the same `default.conf: |` scalar and at the same indentation:

```nginx
    # CSR round-10 #4: the public listener behind the Authentik Ingress. It forwards only the paths a
    # browser needs (login, MFA enrolment, recovery, logout, OIDC), overwrites X-Forwarded-Host, and
    # marks every request, so the admin-automation policy can tell public traffic from in-cluster
    # callers. Everything else, the admin interface and admin API included, answers 404 here.
    server {
      listen 9080;
      proxy_set_header Host $http_host;
      proxy_set_header X-Forwarded-Host $http_host;
      proxy_set_header X-Iverson-Public-Ingress 1;
      location /application/ { proxy_pass http://127.0.0.1:9000; }
      location /if/flow/ { proxy_pass http://127.0.0.1:9000; }
      location /api/v3/flows/executor/ { proxy_pass http://127.0.0.1:9000; }
      location /flows/-/default/ { proxy_pass http://127.0.0.1:9000; }
      location /static/ { proxy_pass http://127.0.0.1:9000; }
      location = / { proxy_pass http://127.0.0.1:9000; }
      location = /-/health/live/ { proxy_pass http://127.0.0.1:9000; }
      location / { return 404; }
    }
```

The three `proxy_set_header` lines stay at server level; no location sets its own, so all of them inherit the three. `X-Forwarded-Proto` and `X-Forwarded-For` are passed through untouched.

- [ ] **Step 3: Service, Ingress, NetworkPolicy.**

`service.yaml`, appended to `ports:`:
```yaml
    - name: http-public
      port: 9080
```

`ingress.yaml`: the backend's `number: 9000` becomes `number: 9080`.

`networkpolicies.yaml`, in `<release>-authentik-ingress`, after the `9000` rule (`:604-612`):
```yaml
    # CSR round-10 #4: the tls-proxy sidecar's public listener, which the Authentik Ingress targets,
    # and the load-balancer health checks that follow it.
    - from: []
      ports: [{ protocol: TCP, port: 9080 }]
```

- [ ] **Step 4: tls-proxy port and probe.** In `deployment-server.yaml`'s tls-proxy container:
  - append to `ports:`:
    ```yaml
                - containerPort: 9080
                  name: http-public
    ```
  - replace the exec `readinessProbe` and its comment block with:

```yaml
          # An HTTP probe on the public listener (CSR round-10 #4): GCE and AGIC derive the Ingress
          # backend's health check from the readiness probe on the port the container declares, and
          # 9080 needs no NetworkPolicy exception beyond its own rule. Both listeners share one nginx
          # process and config, so it vouches for 8443 too. Authentik's cheap /-/health/live/ path with
          # the server container's timeout and threshold keeps a slow Django response from taking the
          # only Authentik pod out of its Service.
          readinessProbe:
            httpGet: { path: /-/health/live/, port: 9080 }
            periodSeconds: 10
            timeoutSeconds: 5
            failureThreshold: 5
```

- [ ] **Step 5: Render checks.** Write `$SCR/c1/render.sh`, which renders every profile to `$SCR/c1/render/<v>.yaml`:

```bash
#!/usr/bin/env bash
set -euo pipefail
H=/home/ben/repositories/Iverson/.worktrees/csr10-identity-plane/Iverson.Server/deploy/helm/iverson
OUT=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/c1/render
mkdir -p $OUT; cd $H && helm dependency build . >/dev/null
for v in values-local values-laptop values-aws values-azure values-gcp; do x=""; [ -f $v.ci-override.yaml ] && x="-f $v.ci-override.yaml"
  helm template iverson . -f $v.yaml $x > $OUT/$v.yaml; done
```

Write `$SCR/c1/check_t1.py`:

```python
import sys, yaml, glob, os
OUT = os.path.dirname(os.path.abspath(__file__)) + "/render"
ALLOW = ["location /application/ { proxy_pass http://127.0.0.1:9000; }",
         "location /if/flow/ { proxy_pass http://127.0.0.1:9000; }",
         "location /api/v3/flows/executor/ { proxy_pass http://127.0.0.1:9000; }",
         "location /flows/-/default/ { proxy_pass http://127.0.0.1:9000; }",
         "location /static/ { proxy_pass http://127.0.0.1:9000; }",
         "location = / { proxy_pass http://127.0.0.1:9000; }",
         "location = /-/health/live/ { proxy_pass http://127.0.0.1:9000; }",
         "location / { return 404; }",
         "proxy_set_header X-Iverson-Public-Ingress 1;", "proxy_set_header X-Forwarded-Host $http_host;", "listen 9080;"]
bad = 0
for f in sorted(glob.glob(f"{OUT}/*.yaml")):
    docs = [d for d in yaml.safe_load_all(open(f)) if d]
    by = {(d["kind"], d["metadata"]["name"]): d for d in docs}
    conf = by[("ConfigMap", "iverson-authentik-tls-proxy")]["data"]["default.conf"]
    checks = {
        "allowlist": all(a in conf for a in ALLOW),
        "service 9080": any(p["port"] == 9080 for p in by[("Service", "iverson-authentik")]["spec"]["ports"]),
        "ingress 9080": by[("Ingress", "iverson-authentik")]["spec"]["rules"][0]["http"]["paths"][0]["backend"]["service"]["port"]["number"] == 9080,
        "netpol 9080": any(r.get("from") == [] and r["ports"] == [{"protocol": "TCP", "port": 9080}]
                           for r in by[("NetworkPolicy", "iverson-authentik-ingress")]["spec"]["ingress"]),
    }
    tp = next(c for c in by[("Deployment", "iverson-authentik-server")]["spec"]["template"]["spec"]["containers"] if c["name"] == "tls-proxy")
    checks["containerPort 9080"] = any(p["containerPort"] == 9080 for p in tp["ports"])
    checks["httpGet probe"] = tp["readinessProbe"].get("httpGet") == {"path": "/-/health/live/", "port": 9080} and "exec" not in tp["readinessProbe"]
    for k, ok in checks.items():
        if not ok: bad += 1; print("FAIL", os.path.basename(f), k)
print("t1 checks:", "PASS" if bad == 0 else f"{bad} FAIL")
sys.exit(bad != 0)
```

Run: `bash $SCR/c1/render.sh && python3 $SCR/c1/check_t1.py`. Expected: `t1 checks: PASS`.

- [ ] **Step 6: `nginx -t` on the rendered config.**

```bash
mkdir -p $SCR/c1/nginx/tls && cd $SCR/c1/nginx && openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj /CN=t -keyout tls/tls.key -out tls/tls.crt 2>/dev/null && chmod 644 tls/*
python3 -c "import yaml; d=[x for x in yaml.safe_load_all(open('$SCR/c1/render/values-laptop.yaml')) if x and x['kind']=='ConfigMap' and x['metadata']['name']=='iverson-authentik-tls-proxy'][0]; open('default.conf','w').write(d['data']['default.conf'])"
docker run --rm -v $PWD/default.conf:/etc/nginx/conf.d/default.conf:ro,Z -v $PWD/tls:/etc/iverson/tls:ro,Z \
  docker.io/nginxinc/nginx-unprivileged:1.27-alpine@sha256:65e3e85dbaed8ba248841d9d58a899b6197106c23cb0ff1a132b7bfe0547e4c0 nginx -t 2>&1 | tail -1
```

Expected: `nginx: configuration file /etc/nginx/nginx.conf test is successful`.

- [ ] **Step 7: `kube-score` diff.** Re-run Step 1's loop into `ks-after-$v.txt`, then `diff ks-before-$v.txt ks-after-$v.txt` for each profile. Expected: no new lines. A removed line is fine.

- [ ] **Step 8: Commit.**

```bash
cd $BR && git add Iverson.Server/deploy/helm/iverson/charts/authentik/templates/{configmap-tls-proxy,service,ingress,deployment-server}.yaml Iverson.Server/deploy/helm/iverson/templates/networkpolicies.yaml
git commit -m "serve only the browser paths of Authentik through a public listener in the tls-proxy sidecar

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 2: Blueprints and the bootstrap token

**Files:**
- Modify: `$H/charts/authentik/templates/secret-service-clients.yaml`
- Modify: `$H/charts/authentik/blueprints/compose-only/service-clients.yaml`
- Modify: `$H/charts/authentik/templates/deployment-server.yaml:117-119`
- Modify: `$H/charts/authentik/templates/deployment-worker.yaml:63-65`
- Modify: `$H/charts/authentik/templates/secret.yaml`
- Modify: `docs/runbooks/grpc-admin-auth-cutover.md:51-71`

- [ ] **Step 1: Helm service-clients blueprint.** In `secret-service-clients.yaml`:
  1. In the provider loop's `{{- if eq $name "admin-automation" }}` branch, add `access_token_validity: "hours=1"` before its `property_mappings:`, at the same indentation.
  2. Immediately after the loop's closing `{{- end }}` / `{{- end }}` (before the `iverson-oidc-default` provider), insert, at the 6-space item indentation:

```yaml
      # CSR round-10 #4: the admin-automation client mints Operator tokens, so it mints only from inside
      # the cluster. The tls-proxy sidecar's public listener sets X-Iverson-Public-Ingress on every
      # request it forwards; a request without it did not come through the Ingress.
      - model: authentik_policies_expression.expressionpolicy
        id: admin-automation-in-cluster-only
        identifiers:
          name: iverson-admin-automation-in-cluster-only
        attrs:
          expression: |
            return "HTTP_X_IVERSON_PUBLIC_INGRESS" not in request.http_request.META
      - model: authentik_policies.policybinding
        identifiers:
          policy: !KeyOf admin-automation-in-cluster-only
          target: !Find [authentik_core.application, [slug, iverson-admin-automation]]
          order: 0
      # CSR round-10 #4: nothing uses the bootstrap superuser's API token after first boot, and the
      # chart no longer provisions one; this removes the token an earlier release created.
      - model: authentik_core.token
        state: absent
        identifiers:
          identifier: authentik-bootstrap-token
```

  3. In `iverson-oidc-default`, replace `grant_types: ["authorization_code", "refresh_token"]` and the three-item `property_mappings` with:

```yaml
          # CSR round-10 #12: the console never asks for a refresh token (Iverson.AdminUI's
          # AuthProvider requests no offline_access), so this provider issues none.
          grant_types: ["authorization_code"]
          access_token_validity: "hours=1"
          property_mappings:
            - !Find [authentik_providers_oauth2.scopemapping, [scope_name, groups]]
            - !Find [authentik_providers_oauth2.scopemapping, [scope_name, tenant_id]]
```

- [ ] **Step 2: Compose blueprint.** In `compose-only/service-clients.yaml`:
  - after the `iverson-admin-automation` application entry, insert the same policy and binding as in Step 1.2, but at that file's 2-space item indentation, without the bootstrap-token entry, with this comment: `# CSR round-10 #4 parity with Helm: compose has no public listener, so the marker never appears and the policy always passes.`
  - in its `iverson-oidc-default`, change `grant_types` to `["authorization_code"]`.

- [ ] **Step 3: Stop provisioning the bootstrap token (Helm).**
  - Delete the `AUTHENTIK_BOOTSTRAP_TOKEN` entry (three lines) from `deployment-server.yaml` and from `deployment-worker.yaml`.
  - In `secret.yaml`, delete the `bootstrap-token:` line from both the `lookup` branch and the generate branch.

- [ ] **Step 4: Runbook.** In `docs/runbooks/grpc-admin-auth-cutover.md`, replace the code block under "Confirming the blueprint actually applied" (the `TOKEN=…bootstrap-token…` / `curl` / `python3` block) with:

````markdown
```bash
LOADTEST_SECRET_ID=$(kubectl -n <ns> get secret <release>-authentik-loadtest-client -o jsonpath='{.data.client-id}' | base64 -d)
kubectl -n <ns> exec deploy/<release>-authentik-worker -- ak shell -c "
from authentik.providers.oauth2.models import OAuth2Provider
print('matches secret:', OAuth2Provider.objects.get(name='iverson-loadtest').client_id == '$LOADTEST_SECRET_ID')
"
```

The check reads the provider inside the worker pod, so it needs no Authentik API token (the chart no longer provisions the bootstrap token, CSR round-10 #4).
````

- [ ] **Step 5: Render checks.** Write `$SCR/c1/check_t2.py`. For every profile it parses the ConfigMap and Secret documents from `render/<v>.yaml`, and the blueprint text inside `iverson-authentik-blueprints-service-clients`'s `stringData["service-clients.yaml"]`, using a YAML loader that maps every `!Tag` node to its plain value. It asserts:
  - `AUTHENTIK_BOOTSTRAP_TOKEN` appears nowhere in the render;
  - `iverson-authentik-app` has no `bootstrap-token` key;
  - the blueprint has an `expressionpolicy` named `iverson-admin-automation-in-cluster-only` whose expression contains `HTTP_X_IVERSON_PUBLIC_INGRESS`, a `policybinding` whose policy is `admin-automation-in-cluster-only`, and a `token` entry with `state: absent` and identifier `authentik-bootstrap-token`;
  - `iverson-oidc-default` has `grant_types == ["authorization_code"]`, `access_token_validity == "hours=1"`, and no `offline_access` mapping;
  - `iverson-admin-automation` has `access_token_validity == "hours=1"`.

```python
import sys, yaml, glob, os
OUT = os.path.dirname(os.path.abspath(__file__)) + "/render"
class L(yaml.SafeLoader): pass
L.add_multi_constructor("!", lambda l, s, n: l.construct_sequence(n) if isinstance(n, yaml.SequenceNode) else l.construct_scalar(n))
bad = 0
for f in sorted(glob.glob(f"{OUT}/*.yaml")):
    text = open(f).read()
    docs = [d for d in yaml.safe_load_all(text) if d]
    by = {(d["kind"], d["metadata"]["name"]): d for d in docs}
    bp = yaml.load(by[("Secret", "iverson-authentik-blueprints-service-clients")]["stringData"]["service-clients.yaml"], Loader=L)
    e = bp["entries"]
    prov = {x["identifiers"]["name"]: x["attrs"] for x in e if x["model"] == "authentik_providers_oauth2.oauth2provider"}
    checks = {
        "no bootstrap env": "AUTHENTIK_BOOTSTRAP_TOKEN" not in text,
        "no bootstrap key": "bootstrap-token" not in (by[("Secret", "iverson-authentik-app")].get("data") or {}),
        "policy": any(x["model"] == "authentik_policies_expression.expressionpolicy" and x["identifiers"]["name"] == "iverson-admin-automation-in-cluster-only"
                      and "HTTP_X_IVERSON_PUBLIC_INGRESS" in x["attrs"]["expression"] for x in e),
        "binding": any(x["model"] == "authentik_policies.policybinding" and x["identifiers"]["policy"] == "admin-automation-in-cluster-only" for x in e),
        "token absent": any(x["model"] == "authentik_core.token" and x.get("state") == "absent" and x["identifiers"]["identifier"] == "authentik-bootstrap-token" for x in e),
        "console grants": prov["iverson-oidc-default"]["grant_types"] == ["authorization_code"],
        "console validity": prov["iverson-oidc-default"].get("access_token_validity") == "hours=1",
        "console no offline_access": "offline_access" not in str(prov["iverson-oidc-default"]["property_mappings"]),
        "admin-automation validity": prov["iverson-admin-automation"].get("access_token_validity") == "hours=1",
    }
    for k, ok in checks.items():
        if not ok: bad += 1; print("FAIL", os.path.basename(f), k)
print("t2 checks:", "PASS" if bad == 0 else f"{bad} FAIL")
sys.exit(bad != 0)
```

Run: `bash $SCR/c1/render.sh && python3 $SCR/c1/check_t1.py && python3 $SCR/c1/check_t2.py`. Expected: two `PASS` lines. The `AUTHENTIK_BOOTSTRAP_TOKEN` check greps the whole render, comments included, so no new comment may name the variable.

Then load `compose-only/service-clients.yaml` with the same tag-tolerant loader, and check that it has the same policy and binding and that its `iverson-oidc-default` has `grant_types == ["authorization_code"]`:

```bash
python3 - <<'PY'
import yaml
class L(yaml.SafeLoader): pass
L.add_multi_constructor("!", lambda l, s, n: l.construct_sequence(n) if isinstance(n, yaml.SequenceNode) else l.construct_scalar(n))
e = yaml.load(open("/home/ben/repositories/Iverson/.worktrees/csr10-identity-plane/Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml"), Loader=L)["entries"]
print("compose policy", any(x["model"].endswith("expressionpolicy") and x["identifiers"]["name"] == "iverson-admin-automation-in-cluster-only" for x in e),
      "binding", any(x["model"] == "authentik_policies.policybinding" for x in e),
      "grants", [x["attrs"]["grant_types"] for x in e if x["model"].endswith("oauth2provider") and x["identifiers"]["name"] == "iverson-oidc-default"])
PY
```

Expected: `compose policy True binding True grants [['authorization_code']]`.

- [ ] **Step 6: `kube-score` diff** as in Task 1 Step 7. Expected: no new lines against `ks-before-*`.

- [ ] **Step 7: The blueprint text tests.**

```bash
cd $BR/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~AuthentikOrchestratorRoleBlueprintTests" 2>&1 | grep -E "^(Passed|Failed)!"
```

Expected: `Passed!` with 0 failed.

- [ ] **Step 8: Commit.**

```bash
cd $BR && git add Iverson.Server/deploy/helm/iverson/charts/authentik/templates/{secret-service-clients,deployment-server,deployment-worker,secret}.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml
git add -f docs/runbooks/grpc-admin-auth-cutover.md
git commit -m "mint admin-automation tokens only in-cluster, drop the bootstrap token and the console's refresh grant

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 3: The recovery flow ends at the console

**Files:**
- Modify: `$H/charts/authentik/blueprints/recovery-flow.yaml` (after the order-3 binding, `:155-160`)
- Modify: `$H/charts/authentik/templates/blueprints-configmap.yaml`
- Modify: `Iverson.Server/docker-compose.yml` (`authentik-worker` `environment:`)

- [ ] **Step 1: The redirect stage.** In `recovery-flow.yaml`, insert after the `iverson-recovery-user-login` binding (order 3) and before the brand entry:

```yaml
  # CSR round-10 #4/#16: end the flow on the console, not on Authentik's user interface, which the
  # public listener does not serve. Helm renders the profile's console URL in place of this !Env
  # (blueprints-configmap.yaml); compose and the test fixture resolve the default, compose's console.
  - id: iverson-recovery-redirect-console
    model: authentik_stages_redirect.redirectstage
    identifiers:
      name: iverson-recovery-redirect-console
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

Also update the file's stage-chain comment above the bindings: `prompt (0) -> MFA validation (1) -> user_write (2) -> user_login (3)` becomes `… -> user_login (3) -> redirect to the console (4)`.

- [ ] **Step 2: Render the URL into the blueprint.** `blueprints-configmap.yaml`'s `data:` becomes:

```yaml
data:
{{- /* CSR round-10 #4/#16: the console URL is written into recovery-flow.yaml here, not read from the
   worker's environment. Authentik resolves !Env in whichever worker applies a file, and re-applies a
   file only when its content changes, so an environment value can stick at the default during a
   rolling upgrade and never follow a host change. */}}
{{- $consoleUrl := printf "%s://%s/admin/" .Values.global.externalScheme .Values.global.ingressHost }}
{{- range $path, $bytes := .Files.Glob "blueprints/*.yaml" }}
  {{ base $path }}: |
{{ $.Files.Get $path | replace "!Env [IVERSON_CONSOLE_URL, \"http://localhost:5173/\"]" ($consoleUrl | quote) | indent 4 }}
{{- end }}
```

- [ ] **Step 3: Compose worker.** In `docker-compose.yml`'s `authentik-worker` `environment:` map, after `IVERSON_ADMIN_ORCHESTRATOR_TOKEN`, add:

```yaml
      # CSR round-10 #4/#16: where the recovery flow sends an invitee (recovery-flow.yaml's !Env).
      IVERSON_CONSOLE_URL: http://localhost:5173/
```

- [ ] **Step 4: Render check and drift mutation.** Write `$SCR/c1/check_t3.py`. It asserts that each profile's rendered `iverson-authentik-blueprints` ConfigMap `recovery-flow.yaml` contains no `IVERSON_CONSOLE_URL`, and that its redirect-stage entry has `target_static` equal to the profile URL (`http://iverson.local/admin/` for local and laptop, `https://iverson-ci-test.example.org/admin/` for the cloud three):

```python
import sys, yaml, glob, os
OUT = os.path.dirname(os.path.abspath(__file__)) + "/render"
class L(yaml.SafeLoader): pass
L.add_multi_constructor("!", lambda l, s, n: l.construct_sequence(n) if isinstance(n, yaml.SequenceNode) else l.construct_scalar(n))
WANT = {"values-local": "http://iverson.local/admin/", "values-laptop": "http://iverson.local/admin/"}
bad = 0
for f in sorted(glob.glob(f"{OUT}/*.yaml")):
    v = os.path.basename(f)[:-5]
    cm = [d for d in yaml.safe_load_all(open(f)) if d and d["kind"] == "ConfigMap" and d["metadata"]["name"] == "iverson-authentik-blueprints"][0]
    rf = cm["data"]["recovery-flow.yaml"]
    st = [x for x in yaml.load(rf, Loader=L)["entries"] if x.get("id") == "iverson-recovery-redirect-console"]
    want = WANT.get(v, "https://iverson-ci-test.example.org/admin/")
    ok = "IVERSON_CONSOLE_URL" not in rf and len(st) == 1 and st[0]["attrs"] == {"mode": "static", "target_static": want}
    if not ok: bad += 1; print("FAIL", v, st)
print("t3 checks:", "PASS" if bad == 0 else f"{bad} FAIL")
sys.exit(bad != 0)
```

Run `bash $SCR/c1/render.sh && python3 $SCR/c1/check_t3.py`. Expected: `t3 checks: PASS`.

Then the mutation:
1. Temporarily change the file's default to `http://localhost:5174/`.
2. Re-render and re-run. Expected: five `FAIL` lines.
3. Restore the file (`git -C $BR diff --stat` shows only the intended edit), re-render, and confirm `PASS` again.

- [ ] **Step 5: The live Authentik suite** (Testcontainers; it mounts the repo's `blueprints/` directory).

```bash
cd $BR/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~AuthentikRecoveryFlowIntegrationTests" 2>&1 | grep -E "^\s+Failed |^(Passed|Failed)!"
```

Expected: `Passed!` with 0 failed. `RecoveryFlow_UserWithNoEnrolledFactor_CompletesAndSignsTheUserIn` still sees `xak-flow-redirect` and a signed-in user.

- [ ] **Step 6: Commit.**

```bash
cd $BR && git add Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/recovery-flow.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap.yaml Iverson.Server/docker-compose.yml
git commit -m "end the recovery flow on the console, with Helm rendering the console URL into the blueprint

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 4: Accept the public-host issuer on both schemes

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Program.cs:196-198`
- Modify: `$H/charts/api/templates/deployment.yaml:153-154`
- Modify: `Iverson.Server/docker-compose.yml:515, :632`
- Test: `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikTrustTests.cs`

- [ ] **Step 1: Failing test.** Add to `AuthentikTrustTests`, after `BothSchemes_FetchMetadataOverTls_TrustTheCa_AndAcceptTheInternalIssuer`:

```csharp
    [Theory]
    [InlineData(JwtBearerDefaults.AuthenticationScheme)]
    [InlineData("ActingUser")]
    public void BothSchemes_AcceptTheExternalIssuer(string scheme)
    {
        // Authentik's global issuer mode stamps tokens minted through the public host with that host's
        // root, so each scheme must accept it next to the internal issuer.
        using var factory = _baseFactory.WithWebHostBuilder(b => b
            .UseSetting("Authentication:InternalIssuer", "http://iverson-authentik:9000/")
            .UseSetting("Authentication:ExternalIssuer", "https://authentik.iverson.example.com/"));

        var opts = factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(scheme);

        opts.TokenValidationParameters.ValidIssuers.Should()
            .Contain("https://authentik.iverson.example.com/").And.Contain("http://iverson-authentik:9000/");
    }
```

Run:
```bash
cd $BR/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~AuthentikTrustTests.BothSchemes_AcceptTheExternalIssuer" 2>&1 | grep -E "^\s+Failed |^(Passed|Failed)!"
```
Expected: 1 failed (the `ActingUser` case) and 1 passed.

- [ ] **Step 2: Implement.** In `Program.cs`, replace the acting-user scheme's

```csharp
        // See the default scheme: acting-user tokens minted on 9000 carry the internal issuer.
        if (cfg["Authentication:InternalIssuer"] is { Length: > 0 } internalIssuer)
            options.TokenValidationParameters.ValidIssuers = [internalIssuer];
```

with

```csharp
        // See the default scheme: acting-user tokens minted on 9000 carry the internal issuer, and
        // tokens minted through the public host carry the external one (Authentik's global issuer mode
        // stamps the host's root).
        string[] actingIssuers = [.. new[] { cfg["Authentication:InternalIssuer"], cfg["Authentication:ExternalIssuer"] }
            .OfType<string>().Where(issuer => issuer.Length > 0)];
        if (actingIssuers.Length > 0)
            options.TokenValidationParameters.ValidIssuers = actingIssuers;
```

Re-run Step 1's command. Expected: `Passed!`, 2 passed.

- [ ] **Step 3: Mutation check.** Temporarily drop `cfg["Authentication:ExternalIssuer"]` from the array, re-run, and expect the `ActingUser` case to fail. Then restore it.

- [ ] **Step 4: Configuration.**
  - Helm `api/templates/deployment.yaml`: the `Authentication__ExternalIssuer` value becomes `"{{ .Values.global.externalScheme }}://authentik.{{ .Values.global.ingressHost }}/"`.
  - `docker-compose.yml` `:515` and `:632`: `- Authentication__ExternalIssuer=http://localhost:9000/`.

- [ ] **Step 5: Render check.** Write `$SCR/c1/check_t4.py`. It asserts the `iverson-api` Deployment's `Authentication__ExternalIssuer` env value equals `http://authentik.iverson.local/` (local and laptop) or `https://authentik.iverson-ci-test.example.org/` (the cloud three):

```python
import sys, yaml, glob, os
OUT = os.path.dirname(os.path.abspath(__file__)) + "/render"
bad = 0
for f in sorted(glob.glob(f"{OUT}/*.yaml")):
    v = os.path.basename(f)[:-5]
    host = "http://authentik.iverson.local" if v in ("values-local", "values-laptop") else "https://authentik.iverson-ci-test.example.org"
    dep = [d for d in yaml.safe_load_all(open(f)) if d and d["kind"] == "Deployment" and d["metadata"]["name"] == "iverson-api"][0]
    env = {e["name"]: e.get("value") for e in dep["spec"]["template"]["spec"]["containers"][0]["env"]}
    if env.get("Authentication__ExternalIssuer") != host + "/": bad += 1; print("FAIL", v, env.get("Authentication__ExternalIssuer"))
print("t4 checks:", "PASS" if bad == 0 else f"{bad} FAIL")
sys.exit(bad != 0)
```

Run `bash $SCR/c1/render.sh && python3 $SCR/c1/check_t4.py`. Expected: `PASS`.

- [ ] **Step 6: Commit.**

```bash
cd $BR && git add Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikTrustTests.cs Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml Iverson.Server/docker-compose.yml
git commit -m "accept the public Authentik host's root issuer on both JWT schemes

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 5: Recovery links last 15 minutes and carry the public host

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Tenancy/IdpAdminClient.cs:41, 101-133`
- Modify: `$H/charts/api/templates/deployment.yaml` (after `Authentik__BaseUrl`, `:186-187`)
- Modify: `Iverson.Server/docker-compose.yml` (after `:530` and `:640`)
- Test: `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikAdminClientTests.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikRecoveryFlowIntegrationTests.cs:73`

- [ ] **Step 1: Failing tests.**

In `AuthentikAdminClientTests`:
- add `using Microsoft.Extensions.Configuration;`;
- give the three-out-parameter `CreateClient` an optional `IConfiguration? configuration = null` last parameter, constructing `new IdpAdminClient(factory, logger, configuration ?? new ConfigurationBuilder().Build())`;
- add the two-parameter overload `CreateClient(FakeHttpMessageHandler handler, IConfiguration configuration, out FakeHttpMessageHandler exposedHandler) => CreateClient(handler, out exposedHandler, out _, configuration)`;
- add these tests:

```csharp
    private static HttpResponseMessage[] OnboardingResponses(string link) =>
    [
        JsonResponse(HttpStatusCode.OK,
            """{"pagination":{"next":0},"results":[{"pk":"11111111-1111-1111-1111-111111111111","name":"tenant-admins"}]}"""),
        JsonResponse(HttpStatusCode.Created, """{"pk":42,"username":"new-user","email":"new-user@example.invalid"}"""),
        JsonResponse(HttpStatusCode.OK, $$"""{"link":"{{link}}"}"""),
    ];

    [Fact]
    public async Task CreateUserAsync_AsksForAFifteenMinuteRecoveryLink()
    {
        var sut = CreateClient(new FakeHttpMessageHandler(OnboardingResponses("http://authentik.local/if/flow/iverson-recovery/?flow_token=abc123")), out var handler);

        await sut.CreateUserAsync("new-user", "new-user@example.invalid", "tenant-a", ["tenant-admins"]);

        using var body = JsonDocument.Parse(handler.RequestBodies[2]!);
        body.RootElement.GetProperty("token_duration").GetString().Should().Be("minutes=15");
    }

    [Theory]
    [InlineData("https://authentik.iverson.example.com", "https://authentik.iverson.example.com/if/flow/iverson-recovery/?flow_token=abc123")]
    [InlineData("http://localhost:9000", "http://localhost:9000/if/flow/iverson-recovery/?flow_token=abc123")]
    public async Task CreateUserAsync_ReturnsTheLinkOnThePublicAuthentikHost(string publicBaseUrl, string expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Authentik:PublicBaseUrl"] = publicBaseUrl }).Build();
        var sut = CreateClient(new FakeHttpMessageHandler(OnboardingResponses("https://iverson-authentik:8443/if/flow/iverson-recovery/?flow_token=abc123")),
            configuration, out _);

        var result = await sut.CreateUserAsync("new-user", "new-user@example.invalid", "tenant-a", ["tenant-admins"]);

        result.RecoveryLink.Should().Be(expected);
    }
```

The unchanged-link case is the existing `CreateUserAsync_ResolvesGroupThenCreatesUserThenTriggersRecovery` (no configuration; it asserts the link verbatim).

In `AuthentikRecoveryFlowIntegrationTests.cs:73`, add `using Microsoft.Extensions.Configuration;` and pass `new ConfigurationBuilder().Build()` as the third argument.

The build fails until Step 2, since the constructor has no third parameter yet. That's expected: Step 2 adds it.

- [ ] **Step 2: Implement.** In `IdpAdminClient.cs`:

```csharp
public sealed class IdpAdminClient(IHttpClientFactory httpClientFactory, ILogger<IdpAdminClient> logger, IConfiguration configuration) : IIdpAdminClient
{
    public const string HttpClientName = "iverson.authentik";

    // CSR round-10 #16: an invite link is a bearer credential until first use; 15 minutes, not the
    // tenant default of 30.
    internal const string RecoveryLinkDuration = "minutes=15";
```

In `TriggerPasswordRecoveryAsync`:
- `JsonBody(new { })` becomes `JsonBody(new { token_duration = RecoveryLinkDuration })`;
- `var link = linkProp.GetString();` becomes `var link = PublicLink(linkProp.GetString()!);`.

Add next to `JsonBody`:

```csharp
    // Authentik builds the link from the host this client called, which is the in-cluster address an
    // invited user cannot open. Authentik:PublicBaseUrl is the host they can; without it (Development,
    // tests) the link is returned as Authentik built it.
    private string PublicLink(string link)
    {
        if (configuration["Authentik:PublicBaseUrl"] is not { Length: > 0 } publicBaseUrl)
            return link;
        var publicBase = new Uri(publicBaseUrl);
        return new UriBuilder(new Uri(link)) { Scheme = publicBase.Scheme, Host = publicBase.Host, Port = publicBase.Port }.Uri.AbsoluteUri;
    }
```

Run:
```bash
cd $BR/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~AuthentikAdminClientTests" 2>&1 | grep -E "^\s+Failed |^(Passed|Failed)!"
```
Expected: `Passed!`, 0 failed.

- [ ] **Step 3: Mutation checks.** Each must fail its test, then be restored:
  - back to `JsonBody(new { })`: `CreateUserAsync_AsksForAFifteenMinuteRecoveryLink` fails;
  - `PublicLink` returning `link` unconditionally: both `CreateUserAsync_ReturnsTheLinkOnThePublicAuthentikHost` cases fail.

- [ ] **Step 4: Configuration.**

Helm `api/templates/deployment.yaml`, after the `Authentik__BaseUrl` entry:
```yaml
            # CSR round-10 #16: the host invited users open recovery links on.
            - name: Authentik__PublicBaseUrl
              value: "{{ .Values.global.externalScheme }}://authentik.{{ .Values.global.ingressHost }}"
```

`docker-compose.yml`, after `- Authentik__BaseUrl=https://authentik-server:8443` in both `iverson-api` and `iverson-worker`:
```yaml
      - Authentik__PublicBaseUrl=http://localhost:9000
```

- [ ] **Step 5: Render check.** Write `$SCR/c1/check_t5.py`:

```python
import sys, yaml, glob, os
OUT = os.path.dirname(os.path.abspath(__file__)) + "/render"
bad = 0
for f in sorted(glob.glob(f"{OUT}/*.yaml")):
    v = os.path.basename(f)[:-5]
    host = "http://authentik.iverson.local" if v in ("values-local", "values-laptop") else "https://authentik.iverson-ci-test.example.org"
    dep = [d for d in yaml.safe_load_all(open(f)) if d and d["kind"] == "Deployment" and d["metadata"]["name"] == "iverson-api"][0]
    env = {e["name"]: e.get("value") for e in dep["spec"]["template"]["spec"]["containers"][0]["env"]}
    if env.get("Authentik__PublicBaseUrl") != host: bad += 1; print("FAIL", v, env.get("Authentik__PublicBaseUrl"))
print("t5 checks:", "PASS" if bad == 0 else f"{bad} FAIL")
sys.exit(bad != 0)
```

Run `bash $SCR/c1/render.sh && python3 $SCR/c1/check_t4.py && python3 $SCR/c1/check_t5.py`. Expected: two `PASS` lines.

- [ ] **Step 6: The live Authentik suite again**, because the constructor changed. Step 5 of Task 3's command. Expected: `Passed!`.

- [ ] **Step 7: Commit.**

```bash
cd $BR && git add Iverson.Server/Iverson.Api/Tenancy/IdpAdminClient.cs Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikAdminClientTests.cs Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikRecoveryFlowIntegrationTests.cs Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml Iverson.Server/docker-compose.yml
git commit -m "ask for 15-minute recovery links and return them on the public Authentik host

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 6: Full suites and live verification

This task makes no commits. Its helpers live in `$SCR/c1/live` and are never committed. Standing rules:
- Snapshot the user's containers, volumes, networks and images before starting, and diff them after teardown.
- Never print a `.env` value, a Kubernetes Secret value or a token. Values reach programs only through exported variables or 0600 files that are deleted afterwards.
- Every block starts with `source $SCR/c1/live/env.sh`.

- [ ] **Step 1: Run the full server suites,** one project at a time.

```bash
cd $BR/Iverson.Server
for p in Iverson.Api.Tests Iverson.ClientConformance.Tests Iverson.Embeddings.Tests Iverson.Events.Tests Iverson.LoadTest.Tests Iverson.Patterns.Tests Iverson.Sql.Tests Iverson.StarRocks.Tests Iverson.Vector.Tests; do
  echo "$p: $(systemd-run --user --scope -q -p MemoryMax=7G dotnet test $p/$p.csproj 2>&1 | grep -E '^(Passed|Failed)!' | tail -1)"; done
```

Expected: every line reads `Passed!`, with 0 failed.

- [ ] **Step 2: Prepare,** and check the user's stack is down: `docker ps --format '{{.Names}}'`. If the list is not empty, **stop and ask the user**; never stop their containers yourself.

Write `$SCR/c1/live/env.sh`:

```bash
MAIN=/home/ben/repositories/Iverson
BR=$MAIN/.worktrees/csr10-identity-plane
SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad
LIVE=$SCR/c1/live
ENVF=$BR/Iverson.Server/.env
REAL_HOME=/home/ben
PW=$SCR/cprobe/pw            # playwright-core 1.63 + otplib 12, installed this session
DC="docker compose -p csr10c -f $LIVE/compose.yml --project-directory $BR/Iverson.Server --env-file $ENVF"
val() { grep "^$1=" "$ENVF" | cut -d= -f2-; }
scratch_env() {
  export HOME=$LIVE/home NUGET_PACKAGES=$REAL_HOME/.nuget/packages PYTHONUSERBASE=$REAL_HOME/.local
  export GOMODCACHE=$REAL_HOME/go/pkg/mod MAVEN_OPTS=-Dmaven.repo.local=$REAL_HOME/.m2/repository npm_config_cache=$REAL_HOME/.npm
}
grpc() { # url header-file... : prints the HTTP status line, grpc-status and grpc-message of a 5-byte GetSchema call
  local url=$1; shift; local hs=(); for h in "$@"; do hs+=(-H "@$h"); done
  printf '\0\0\0\0\0' | curl -s -D - -o /dev/null --http2-prior-knowledge -H 'content-type: application/grpc' -H 'te: trailers' "${hs[@]}" \
    --data-binary @- "$url/iverson.ObjectMappingService/GetSchema" | grep -i -E "^HTTP/|^grpc-(status|message)" | tr -d '\r' | tr '\n' ' '; echo; }
```

Then:

```bash
source $SCR/c1/live/env.sh; mkdir -p $LIVE/home; umask 077
bash $BR/scripts/generate-compose-secrets.sh >/dev/null
grep -v container_name $BR/Iverson.Server/docker-compose.yml | sed 's/^    image: iverson-api$/    image: csr10c-api/' > $LIVE/compose.yml
grep -c 'image: csr10c-api' $LIVE/compose.yml
docker ps -a --format '{{.Names}}' | sort > $LIVE/before-containers.txt; docker volume ls -q | sort > $LIVE/before-volumes.txt
docker network ls --format '{{.Name}}' | sort > $LIVE/before-networks.txt; docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort > $LIVE/before-images.txt
```

Expected: `grep -c` prints `2`.

- [ ] **Step 3: Write the Playwright flow script,** `$LIVE/c1flows.js`. It drives three flows and records every path requested, any 404s, and the final URL:
  - a console-client login with TOTP enrolment;
  - optionally an authorize with a second client in the same browser session;
  - optionally a recovery link in a fresh context.

  Codes and PKCE verifiers go to a 0600 file. Nothing secret is printed.

```javascript
// env: BASE, CLIENT, REDIRECT, USERNAME, PASSWORD, [PROXY], [CLIENT2, REDIRECT2], [RECOVERY_LINK], [SKIP_LOGIN=1], OUT, CODES
const { chromium } = require(process.env.PW + '/node_modules/playwright-core');
const { authenticator } = require(process.env.PW + '/node_modules/otplib');
const crypto = require('crypto'), fs = require('fs');
const E = process.env, BASE = E.BASE;
const b64u = b => b.toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
const out = { phases: {}, notes: [] }, codes = {};

async function track(ctx, phase) {
  out.phases[phase] = { paths: [], notFound: [] };
  ctx.on('request', r => { const u = new URL(r.url()); if (u.origin === BASE) out.phases[phase].paths.push(`${r.method()} ${u.pathname}`); });
  ctx.on('response', r => { const u = new URL(r.url()); if (u.origin === BASE && r.status() === 404) out.phases[phase].notFound.push(u.pathname); });
}

async function runFlow(page, challenges, cred) {
  let handled = 0;
  for (let step = 0; step < 25; step++) {
    for (let i = 0; i < 60 && challenges.length <= handled; i++) {
      if (!page.url().startsWith(BASE)) return page.url();
      await page.waitForTimeout(250);
    }
    await page.waitForTimeout(800);
    if (!page.url().startsWith(BASE) || challenges.length <= handled) return page.url();
    handled = challenges.length;
    const last = challenges[challenges.length - 1] || {}, comp = last.component || '';
    out.notes.push(comp);
    if (comp === 'ak-stage-identification') {
      await page.locator('input[name="uidField"]').fill(cred.username);
      if (await page.locator('input[name="password"]').count()) await page.locator('input[name="password"]').fill(cred.password);
      await page.locator('button[type="submit"]').first().click();
    } else if (comp === 'ak-stage-password') {
      await page.locator('input[name="password"]').fill(cred.password);
      await page.locator('button[type="submit"]').first().click();
    } else if (comp === 'ak-stage-prompt') {
      const inputs = page.locator('input[type="password"]');
      for (let i = 0; i < await inputs.count(); i++) await inputs.nth(i).fill(cred.newPassword);
      await page.locator('button[type="submit"]').first().click();
    } else if (comp === 'ak-stage-authenticator-validate') {
      const totp = (last.configuration_stages || []).find(s => /TOTP/.test(s.verbose_name || ''));
      if (!totp) throw new Error('no TOTP configuration stage');
      await page.locator(`button:has-text("${totp.name}"), button:has-text("${totp.verbose_name}")`).first().click();
    } else if (comp === 'ak-stage-authenticator-totp') {
      const c = challenges.find(x => x && x.config_url);
      await page.locator('input[name="code"]').fill(authenticator.generate(new URL(c.config_url).searchParams.get('secret')));
      await page.locator('button[type="submit"]').first().click();
    } else if (comp === 'ak-stage-consent') {
      await page.locator('button[type="submit"]').first().click();
    } else if (comp === 'ak-stage-access-denied' || comp === 'ak-stage-flow-error') {
      throw new Error(`flow refused: ${comp}`);
    }
  }
  return page.url();
}

async function authorize(ctx, name, client, redirect, cred) {
  const page = await ctx.newPage(), challenges = [];
  page.on('response', async r => { if (r.url().includes('/api/v3/flows/executor/')) { try { challenges.push(await r.json()); } catch {} } });
  let callback = null; page.on('request', r => { if (r.url().startsWith(redirect)) callback = r.url(); });
  const verifier = b64u(crypto.randomBytes(32));
  const q = new URLSearchParams({ client_id: client, redirect_uri: redirect, response_type: 'code', scope: 'openid profile email groups tenant_id',
    state: name, code_challenge: b64u(crypto.createHash('sha256').update(verifier).digest()), code_challenge_method: 'S256' });
  await page.goto(`${BASE}/application/o/authorize/?${q}`).catch(() => {});
  for (let i = 0; i < 20 && !callback; i++) { await runFlow(page, challenges, cred); await page.waitForTimeout(500); }
  out.notes.push(`${name}: ${callback ? 'code received' : 'NO CODE'}`);
  if (callback) codes[name] = { code: new URL(callback).searchParams.get('code'), verifier, client, redirect };
  // Exchange now, before logout: ending the Authentik session deletes its unexchanged codes. A same-origin fetch
  // from a page on BASE goes through the browser's own proxy path, so the token is minted through the public host.
  if (callback) {
    const tp = await ctx.newPage();
    await tp.goto(`${BASE}/-/health/live/`);
    const t = await tp.evaluate(async f => (await fetch('/application/o/token/', { method: 'POST', body: new URLSearchParams(f) })).json(),
      { grant_type: 'authorization_code', client_id: client, redirect_uri: redirect, code: codes[name].code, code_verifier: verifier });
    await tp.close();
    codes[name].token = t.access_token; codes[name].refresh = 'refresh_token' in t;
    out.notes.push(`${name}: token ${t.access_token ? 'received' : 'REFUSED ' + t.error}`);
  }
  await page.close();
}

(async () => {
  const launch = { executablePath: E.HOME_REAL + '/.cache/ms-playwright/chromium-1228/chrome-linux64/chrome' };
  if (E.PROXY) launch.proxy = { server: E.PROXY };
  const browser = await chromium.launch(launch);
  if (E.SKIP_LOGIN !== '1') {
    const cred = { username: E.USERNAME, password: E.PASSWORD };
    const ctx = await browser.newContext(); await track(ctx, 'login');
    await authorize(ctx, 'console', E.CLIENT, E.REDIRECT, cred);
    if (E.CLIENT2) await authorize(ctx, 'second', E.CLIENT2, E.REDIRECT2, cred);
    await track(ctx, 'logout');
    const p = await ctx.newPage();
    await p.goto(`${BASE}/application/o/iverson-api/end-session/`).catch(() => {}); await p.waitForTimeout(4000);
    out.notes.push(`logout landed ${new URL(p.url()).pathname}`);
  }
  if (E.RECOVERY_LINK) {
    const rctx = await browser.newContext(); await track(rctx, 'recovery');
    const rp = await rctx.newPage(), ch = [];
    // The first request off the Authentik origin is the flow's redirect target, recorded before any redirect the
    // target host itself answers with (ingress-nginx sends http to https on a host with a certificate).
    let leftFor = null; rp.on('request', r => { if (!leftFor && new URL(r.url()).origin !== BASE) leftFor = r.url(); });
    rp.on('response', async r => { if (r.url().includes('/api/v3/flows/executor/')) { try { ch.push(await r.json()); } catch {} } });
    await rp.goto(E.RECOVERY_LINK).catch(() => {});
    const end = await runFlow(rp, ch, { newPassword: crypto.randomBytes(18).toString('base64url') + 'aA1!' });
    await rp.waitForTimeout(3000);
    out.notes.push(`recovery ended at ${end.split('?')[0]}`);
    out.notes.push(`recovery left Authentik for ${leftFor}`);
  }
  await browser.close();
  for (const k of Object.keys(out.phases)) { out.phases[k].paths = [...new Set(out.phases[k].paths)].sort(); out.phases[k].notFound = [...new Set(out.phases[k].notFound)]; }
  fs.writeFileSync(E.OUT, JSON.stringify(out, null, 1));
  fs.writeFileSync(E.CODES, JSON.stringify(codes), { mode: 0o600 });
  console.log('done');
})().catch(e => { fs.writeFileSync(E.OUT, JSON.stringify({ ...out, error: e.message }, null, 1)); console.error('ERR', e.message); process.exit(1); });
```

Also write `$LIVE/readtoken.sh`. It writes the token the flow script already exchanged to a 0600 header file, as `authorization: Bearer …` (or `x-acting-user-authorization: Bearer …` with `ACTING=1`), and prints its `iss` and whether the token response carried a refresh token. The flow script exchanges each code itself, before its logout, because ending the Authentik session deletes the session's unexchanged codes (CIR-1 §2.1). It exchanges with a same-origin `fetch` from a page on `BASE`, so on kind the token is minted through the public host. Playwright's `context.request` is not a substitute: under the proxy setting it tunnels with `CONNECT`, which the ingress refuses.

```bash
#!/usr/bin/env bash
# usage: readtoken.sh <codes.json> <name> <header-file> — writes the header from the token the flow script exchanged
set -euo pipefail; umask 077
codes=$1 name=$2 hdr=$3
key=authorization; [ "${ACTING:-}" = 1 ] && key=x-acting-user-authorization
python3 -c "import json,base64; c=json.load(open('$codes'))['$name']; t=c['token']; p=json.loads(base64.urlsafe_b64decode(t.split('.')[1]+'==')); open('$hdr','w').write('$key: Bearer '+t); print('iss='+p['iss'], c['refresh'])"
```

- [ ] **Step 4: Stand up the isolated compose stack.** Run this with `run_in_background`: `source $SCR/c1/live/env.sh; $DC up -d --build > $LIVE/up.log 2>&1; echo "up exit=$?" >> $LIVE/up.log`.
  - Wait until `docker inspect -f '{{.State.Health.Status}}' csr10c-iverson-api-1` prints `healthy` (up to 10 minutes).
  - Then confirm `docker logs csr10c-iverson-api-1 2>&1 | grep -c "must be an https:// URL\|is required outside Development"` prints `0`.

- [ ] **Step 5: Compose: the identity scenario,** exactly as B's Task 7 Step 6, with this plan's `env.sh`:

```bash
source $SCR/c1/live/env.sh
( scratch_env
  export IVERSON_CLIENT_ID=dev-iverson-loadtest-client-id IVERSON_CLIENT_SECRET="$(val IVERSON_LOADTEST_CLIENT_SECRET)"
  export IVERSON_TOKEN_ENDPOINT=http://localhost:9000/application/o/token/ IVERSON_CLIENT_SCOPE="schema_admin tenant_id_loadtest"
  export IVERSON_ACTING_USER_BYPASS_PASSWORD="$(val IVERSON_BYPASS_PASSWORD)" IVERSON_OTHER_TENANT_PASSWORD="$(val IVERSON_SMOKE_TEST_PASSWORD)"
  cd $BR/Iverson.Server/Iverson.ClientConformance && systemd-run --user --scope -q -p MemoryMax=4G dotnet run -- --scenarios identity ) > $LIVE/identity.out 2>&1; echo "harness exit=$?"
tail -4 $LIVE/identity.out
```

Expected: `harness exit=0`, and the `identity` row reads `ok` in all five language columns.

- [ ] **Step 6: Compose: LoadTest's tenant-admin bootstrap through the new recovery flow** (the fresh stack has no LoadTest tenant).

```bash
source $SCR/c1/live/env.sh
( scratch_env
  export IVERSON_CLIENT_ID=dev-iverson-admin-automation-client-id IVERSON_CLIENT_SECRET="$(val IVERSON_ADMIN_AUTOMATION_CLIENT_SECRET)"
  export IVERSON_TOKEN_ENDPOINT=http://localhost:9000/application/o/token/ IVERSON_CLIENT_SCOPE="admin schema_admin tenant_id_admin"
  export IVERSON_ACTING_USER_PASSWORD="$(val IVERSON_SMOKE_TEST_PASSWORD)" IVERSON_ACTING_USER_BYPASS_PASSWORD="$(val IVERSON_BYPASS_PASSWORD)"
  export IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD="$(openssl rand -base64 24)aA1!"
  # Authentik applies blueprints asynchronously: wait until the admin-automation client stops answering invalid_client.
  until [ "$(curl -s -d grant_type=client_credentials -d client_id="$IVERSON_CLIENT_ID" --data-urlencode client_secret="$IVERSON_CLIENT_SECRET" \
      -d scope="$IVERSON_CLIENT_SCOPE" "$IVERSON_TOKEN_ENDPOINT" | python3 -c 'import sys,json; print(json.load(sys.stdin).get("error",""))')" != invalid_client ]; do sleep 10; done
  # write-path, not seed: seed ignores --count and always writes 400,000 articles (DirectSeeder.cs:27), which exhausts the VM.
  cd $BR/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=4G dotnet run --project Iverson.LoadTest -- write-path --count 10 --concurrency 1 ) > $LIVE/loadtest.out 2>&1; echo "loadtest exit=$?"
grep -E "recovery link|Logged the tenant admin in|ready\.|failed" $LIVE/loadtest.out
```

Expected:
- `loadtest exit=0`;
- `Set the tenant admin's password from CreateTenant's recovery link.`;
- `Logged the tenant admin in, enrolling its TOTP device.`;
- `Tenant 'iverson-loadtest-dynamic' ready.`

- [ ] **Step 7: Compose: a dev-console login whose token the API accepts.** The identity scenario and LoadTest log their users in with their own TOTP enrolment, so a browser enrolment on one of their users would lock them out. This step uses a dedicated user, created with compose's bootstrap token (compose keeps it, spec 2b).

```bash
source $SCR/c1/live/env.sh; umask 077
( B="Authorization: Bearer $(val AUTHENTIK_BOOTSTRAP_TOKEN)"; A=http://localhost:9000/api/v3
  export PASSWORD="$(openssl rand -base64 18)aA1!"
  pk=$(curl -s -H "$B" -H 'Content-Type: application/json' -X POST $A/core/users/ -d '{"username":"c1-console-check","name":"C1 console check","path":"users","is_active":true}' \
       | python3 -c 'import sys,json; print(json.load(sys.stdin)["pk"])')
  python3 -c 'import json,os; print(json.dumps({"password": os.environ["PASSWORD"]}))' > $LIVE/pw.json
  echo "set_password: $(curl -s -o /dev/null -w '%{http_code}' -H "$B" -H 'Content-Type: application/json' -X POST $A/core/users/$pk/set_password/ --data @$LIVE/pw.json)"
  rm -f $LIVE/pw.json
  export PW BASE=http://localhost:9000 CLIENT=dev-iverson-human-oidc-client-id REDIRECT=http://localhost:5173/callback HOME_REAL=$REAL_HOME
  export USERNAME=c1-console-check OUT=$LIVE/compose-flows.json CODES=$LIVE/compose-codes.json
  node $LIVE/c1flows.js )
python3 -c "import json; d=json.load(open('$LIVE/compose-flows.json')); print(d['notes'], d.get('error',''))"
bash $LIVE/readtoken.sh $LIVE/compose-codes.json console $LIVE/console.hdr
grpc http://127.0.0.1:8080 $LIVE/console.hdr
printf 'authorization: Bearer not-a-token' > $LIVE/garbage.hdr; grpc http://127.0.0.1:8080 $LIVE/garbage.hdr
rm -f $LIVE/console.hdr $LIVE/compose-codes.json
```

Expected:
- `set_password: 204`;
- the notes show TOTP enrolment, `console: code received` and `console: token received`;
- `readtoken.sh` prints `iss=http://localhost:9000/ False`;
- the console token: `HTTP/2 200` with `grpc-status: 0`;
- the garbage token: `HTTP/2 401` with no `grpc-status` (the default scheme rejects before the gRPC service).

- [ ] **Step 8: Tear compose down.** `source $SCR/c1/live/env.sh; $DC down -v > $LIVE/down.log 2>&1; docker rmi csr10c-api >/dev/null 2>&1; true`. Kind's ingress also binds host `8080`, so compose must be down first.

- [ ] **Step 9: Stand up kind.** Run the slow steps with `run_in_background`.

```bash
source $SCR/c1/live/env.sh; cd $BR
KIND_EXPERIMENTAL_PROVIDER=podman kind create cluster --name iverson --config Iverson.Server/deploy/kind/kind-config.yaml
bash Iverson.Server/deploy/kind/setup.sh
TMPDIR=/var/tmp bash Iverson.Server/deploy/kind/build-and-load-image.sh csr10c iverson
cd Iverson.Server/deploy/helm/iverson && helm dependency build . >/dev/null
helm upgrade --install iverson . -n iverson -f values-laptop.yaml \
  --set api.hpa.maxReplicas=1 --set api.image.tag=csr10c --set worker.image.tag=csr10c --wait --timeout 30m
```

Then wait until `kubectl -n iverson exec deploy/iverson-authentik-worker -- ak shell -c "from authentik.policies.expression.models import ExpressionPolicy; print(ExpressionPolicy.objects.filter(name='iverson-admin-automation-in-cluster-only').count())" 2>/dev/null | tail -1` prints `1` (blueprints apply asynchronously; `ak shell` prints a banner before the value and logs to stderr).

- [ ] **Step 10: kind: readiness, blocked paths, admin-automation, bootstrap token.**

```bash
source $SCR/c1/live/env.sh
kubectl -n iverson get pod -l app=iverson-authentik-server -o jsonpath='{.items[0].status.containerStatuses[*].name}{" "}{.items[0].status.containerStatuses[*].ready}'; echo
for p in /if/admin/ /if/user/ /api/v3/core/users/ /if/flow/default-authentication-flow/ /-/health/live/; do
  echo "$p $(curl -s -o /dev/null -w '%{http_code}' -H 'Host: authentik.iverson.local' http://127.0.0.1:8080$p)"; done
kubectl -n iverson port-forward svc/iverson-authentik 19000:9000 > $LIVE/pf-ak.log 2>&1 & echo $! > $LIVE/pf-ak.pid
kubectl -n iverson port-forward svc/iverson-api 18080:8080 > $LIVE/pf-api.log 2>&1 & echo $! > $LIVE/pf-api.pid
sleep 3
( export CID="$(kubectl -n iverson get secret iverson-authentik-admin-automation-client -o jsonpath='{.data.client-id}' | base64 -d)"
  export CSEC="$(kubectl -n iverson get secret iverson-authentik-admin-automation-client -o jsonpath='{.data.client-secret}' | base64 -d)"
  mint() { curl -s "$@" -d grant_type=client_credentials -d client_id="$CID" --data-urlencode client_secret="$CSEC" -d scope="admin schema_admin tenant_id_admin" \
    | python3 -c 'import sys,json; d=json.load(sys.stdin); print("MINTED" if "access_token" in d else "REFUSED "+d.get("error",""))'; }
  echo "through the ingress:  $(mint -H 'Host: authentik.iverson.local' http://127.0.0.1:8080/application/o/token/)"
  echo "through 9000 (port-forward): $(mint -H 'Host: iverson-authentik:9000' http://127.0.0.1:19000/application/o/token/)" )
kubectl -n iverson exec deploy/iverson-authentik-worker -- ak shell -c "from authentik.core.models import Token; print('bootstrap token rows:', Token.objects.filter(identifier='authentik-bootstrap-token').count())" 2>/dev/null | tail -1   # worker pod: the laptop profile's 512Mi limit OOM-kills the server container when ak shell loads
kubectl -n iverson get secret iverson-authentik-app -o jsonpath='{.data}' | python3 -c "import sys,json; print('secret keys:', sorted(json.load(sys.stdin)))"
```

Expected:
- `server tls-proxy true true`;
- `404` for the first three paths, `200` for the last two;
- `through the ingress:  REFUSED invalid_grant`;
- `through 9000 (port-forward): MINTED`;
- `bootstrap token rows: 0`;
- secret keys `['bootstrap-password', 'secret-key']`.

Then run the runbook's new check from Task 2 Step 4 with `<ns>=iverson` and `<release>=iverson`. Expected: `matches secret: True`.

- [ ] **Step 11: kind: browser flows through the ingress, and the issuer on both schemes.**

```bash
source $SCR/c1/live/env.sh; umask 077
( export PW BASE=http://authentik.iverson.local PROXY=http://127.0.0.1:8080 HOME_REAL=$REAL_HOME
  export CLIENT="$(kubectl -n iverson get secret iverson-authentik-human-oidc-client -o jsonpath='{.data.client-id}' | base64 -d)" REDIRECT=http://iverson.local/admin/callback
  export CLIENT2="$(kubectl -n iverson get secret iverson-authentik-acting-user-client -o jsonpath='{.data.client-id}' | base64 -d)" REDIRECT2=http://iverson.local/admin/acting-user/callback
  export USERNAME=iverson-acting-user-smoke-test PASSWORD="$(kubectl -n iverson get secret iverson-authentik-smoke-test-user -o jsonpath='{.data.password}' | base64 -d)"
  export OUT=$LIVE/kind-flows.json CODES=$LIVE/kind-codes.json
  node $LIVE/c1flows.js )
python3 -c "import json; d=json.load(open('$LIVE/kind-flows.json')); print(d['notes'], {k: v['notFound'] for k, v in d['phases'].items()}, d.get('error',''))"
bash $LIVE/readtoken.sh $LIVE/kind-codes.json console $LIVE/console.hdr
ACTING=1 bash $LIVE/readtoken.sh $LIVE/kind-codes.json second $LIVE/acting.hdr
( export CID="$(kubectl -n iverson get secret iverson-authentik-loadtest-client -o jsonpath='{.data.client-id}' | base64 -d)"
  export CSEC="$(kubectl -n iverson get secret iverson-authentik-loadtest-client -o jsonpath='{.data.client-secret}' | base64 -d)"
  curl -s -H 'Host: iverson-authentik:9000' -d grant_type=client_credentials -d client_id="$CID" --data-urlencode client_secret="$CSEC" -d scope="schema_admin tenant_id_loadtest" \
    http://127.0.0.1:19000/application/o/token/ | python3 -c "import sys,json; open('$LIVE/service.hdr','w').write('authorization: Bearer '+json.load(sys.stdin)['access_token'])" )
printf 'x-acting-user-authorization: Bearer not-a-token' > $LIVE/acting-garbage.hdr
echo "console (default scheme): $(grpc http://127.0.0.1:18080 $LIVE/console.hdr)"
echo "acting user, real:        $(grpc http://127.0.0.1:18080 $LIVE/service.hdr $LIVE/acting.hdr)"
echo "acting user, garbage:     $(grpc http://127.0.0.1:18080 $LIVE/service.hdr $LIVE/acting-garbage.hdr)"
rm -f $LIVE/console.hdr $LIVE/acting.hdr $LIVE/service.hdr $LIVE/kind-codes.json
```

Expected:
- the notes show TOTP enrolment, `console: code received`, `console: token received`, `second: code received` and `second: token received`;
- no `notFound` paths in the login and logout phases;
- both `readtoken.sh` calls print `iss=http://authentik.iverson.local/ False`;
- `console (default scheme)`: `HTTP/2 200` with `grpc-status: 0`;
- `acting user, real`: `HTTP/2 200` with a `grpc-status` other than `16`;
- `acting user, garbage`: `HTTP/2 200` with `grpc-status: 16` and `Acting-user token is invalid.`

- [ ] **Step 12: kind: a recovery link from `CreateTenant` on the public host, 15 minutes, ending at the console.**

Write `$LIVE/createtenant/` as a scratch console:

```bash
mkdir -p $LIVE/createtenant && cd $LIVE/createtenant
cat > createtenant.csproj <<'X'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="/home/ben/repositories/Iverson/.worktrees/csr10-identity-plane/Iverson.Clients/DotNet/Iverson.Client.Core/Iverson.Client.Core.csproj" /></ItemGroup></Project>
X
cat > Program.cs <<'X'
// args: grpcUrl tenantId ; env TOKEN (admin-automation access token). Writes the recovery link to LINK_FILE (0600).
using Grpc.Core; using Grpc.Net.Client; using Iverson.Client.Contracts;
using var channel = GrpcChannel.ForAddress(args[0]);
var client = new TenantLifecycleGrpcService.TenantLifecycleGrpcServiceClient(channel);
var tenant = await client.CreateTenantAsync(new CreateTenantRequest { TenantId = args[1], DisplayName = "C1 live check",
    AdminUsername = args[1] + "-admin", AdminEmail = args[1] + "-admin@iverson.local" },
    new Metadata { { "authorization", $"Bearer {Environment.GetEnvironmentVariable("TOKEN")}" } });
File.WriteAllText(Environment.GetEnvironmentVariable("LINK_FILE")!, tenant.AdminRecoveryLink);
var link = new Uri(tenant.AdminRecoveryLink);
Console.WriteLine($"link host={link.Scheme}://{link.Authority} path={link.AbsolutePath}");
X
dotnet build -c Release 2>&1 | grep -E "error|Build succeeded" | head -3
```

Then:

```bash
source $SCR/c1/live/env.sh; umask 077
( export CID="$(kubectl -n iverson get secret iverson-authentik-admin-automation-client -o jsonpath='{.data.client-id}' | base64 -d)"
  export CSEC="$(kubectl -n iverson get secret iverson-authentik-admin-automation-client -o jsonpath='{.data.client-secret}' | base64 -d)"
  export TOKEN="$(curl -s -H 'Host: iverson-authentik:9000' -d grant_type=client_credentials -d client_id="$CID" --data-urlencode client_secret="$CSEC" \
    -d scope="admin schema_admin tenant_id_admin" http://127.0.0.1:19000/application/o/token/ | python3 -c 'import sys,json; print(json.load(sys.stdin)["access_token"])')"
  export LINK_FILE=$LIVE/link.txt
  dotnet $LIVE/createtenant/bin/Release/net10.0/createtenant.dll http://127.0.0.1:18080 c1-live-check )
kubectl -n iverson exec deploy/iverson-authentik-worker -- ak shell -c "
from django.utils.timezone import now
from authentik.flows.models import FlowToken
t = FlowToken.objects.filter(flow__slug='iverson-recovery').order_by('-expires').first()
print('minutes to expiry:', round((t.expires - now()).total_seconds() / 60, 1))" 2>/dev/null | tail -1
( export PW BASE=http://authentik.iverson.local PROXY=http://127.0.0.1:8080 HOME_REAL=$REAL_HOME RECOVERY_LINK="$(cat $LIVE/link.txt)" SKIP_LOGIN=1
  export OUT=$LIVE/kind-recovery.json CODES=$LIVE/kind-codes2.json
  node $LIVE/c1flows.js )
python3 -c "import json; d=json.load(open('$LIVE/kind-recovery.json')); print([n for n in d['notes'] if n.startswith('recovery')], d['phases'].get('recovery',{}).get('notFound'), d.get('error',''))"
rm -f $LIVE/link.txt $LIVE/kind-codes2.json
```

Expected:
- `link host=http://authentik.iverson.local path=/if/flow/iverson-recovery/`;
- `minutes to expiry:` between 14 and 15;
- `recovery left Authentik for http://iverson.local/admin/` (the `recovery ended at` note is informational: on the laptop profile ingress-nginx redirects `iverson.local` to https, which the proxied browser cannot follow);
- no `notFound` paths in the recovery phase.


- [ ] **Step 13: Tear kind down and diff the user's state.**

```bash
source $SCR/c1/live/env.sh
kill $(cat $LIVE/pf-ak.pid) $(cat $LIVE/pf-api.pid) 2>/dev/null
kind delete cluster --name iverson; kind get clusters
docker rmi docker.io/library/iverson-api:csr10c localhost/iverson-api:csr10c >/dev/null 2>&1; true
docker ps -a --format '{{.Names}}' | sort | diff $LIVE/before-containers.txt - && echo containers-unchanged
docker volume ls -q | sort | diff $LIVE/before-volumes.txt - && echo volumes-unchanged
docker network ls --format '{{.Name}}' | sort | diff $LIVE/before-networks.txt - && echo networks-unchanged
docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort | diff $LIVE/before-images.txt - && echo images-unchanged
```

Expected:
- `No kind clusters found`, and all four `*-unchanged` lines.
- A difference limited to newly pulled base images (kind node, nginx, Calico) is acceptable; list it.
- A dangling `<none>` image whose Created time falls inside this run is a build stage of ours: remove it and re-diff.
- A changed or removed user image or tag is not acceptable.

- [ ] **Step 14: Remove the worktree's generated files.** `rm -rf $BR/Iverson.Server/.env $BR/Iverson.Server/deploy/compose-tls; git -C $BR status --short`. Expected: no output.

## Known issues inherited from spec

- **`9000` stays reachable from inside the VPC** on AWS and Azure, and a direct pod-IP request there bypasses the public listener. That is #15's exposure, and sub-project D narrows it.
- **No public self-service.** With `/if/user/` blocked publicly, users cannot manage their own MFA devices or Authentik settings. A lost device needs an operator, using the in-cluster admin interface. A login started directly at the Authentik host, not from the console, ends on a 404 after authentication.
- **The orchestrator token still never expires** until C2 lands. After 1a, using it needs reach to `9000` or `8443` from inside the cluster or VPC.
- **The orchestrator's ability to create a user directly into `operators`** is an Authentik permission-model limit (2026.5.3 has no group-scoped `add_user`). It is recorded in the blueprint and untouched here.
- **#16 is only partly closed: invitees stay MFA-free past the link.** The recovery flow signs the user in, so every console sign-in in that browser, including the console's re-sign-in at access-token expiry, runs no second factor until that Authentik session ends (a console sign-out, the browser dropping its session cookie, or the session's own expiry, 24 hours after recovery in CDR-2's run); the first factor is enrolled at the first login after that (user's choice, CDR-1 §3.1 D; extent per CDR-2 §2.2). The link now lives 15 minutes, carries the public host, and still travels the ingress→pod hop in plaintext behind the edge's TLS (#5's mesh item).
- **The cloud half of the issuer match is unverified.** Helm's `ExternalIssuer` assumes each cloud load balancer sends `X-Forwarded-Proto: https`; 4d proves the match on ingress-nginx only (user's choice, CDR-1 §3.2 A).
- **Load-balancer health-check paths on ALB** are not set for the Authentik Ingress. `/` answers with a redirect through the new listener, exactly as it does on `9000` today. Whether that marks the target unhealthy on AWS is unverified here.
