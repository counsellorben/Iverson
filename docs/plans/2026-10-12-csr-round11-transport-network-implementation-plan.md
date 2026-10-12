# CSR Round 11 — Transport and Network (Sub-project C) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-10-12-csr-round11-transport-network-design.md` (commit SHA: `92e44811`)

**Goal:** close the plaintext hops and network-policy residuals of CSR round-11 findings #4, #6 and #18, on Helm.

**Architecture:**
- **Edge → pod TLS.** The edge hops get backend TLS behind one switch per pod (`iverson.backendTls`). The API uses native Kestrel TLS from a chart-generated certificate; Authentik's existing tls-proxy serves 9080 over TLS. Per-class Ingress, Service and BackendConfig settings come from the templates, and the parent chart renders the Prometheus scrape config so its API job follows the API switch.
- **Stores.** StarRocks FE serves MySQL-protocol TLS from an init-container-built keystore, with verifying clients. Authentik verifies Postgres. The unused Redis is removed.
- **Azure.** Azure gets two-phase trusted roots (Terraform plus a Helm guard).
- **Network policy.** The `ingress-nginx` NetworkPolicy peer is emitted only for nginx-class Ingresses, and the any-destination fallbacks need an explicit acknowledgement.

**Tech stack:** Helm v3.16.4 (CI pins v3.15.0) with sprig; Kestrel (.NET 10); nginx-unprivileged 1.30.5; StarRocks 4.1.1 + operator v1.11.5; `mysql:8.0.46` (OpenSSL 3.5.1); Authentik 2026.5.3; CNPG; Prometheus v2.55.1; Terraform with azurerm `~> 3.90` (3.117.1 resolved); kind with ingress-nginx 4.12.8; Docker Compose 2.40.3 (podman).

---

## Global Constraints

Copied from the spec, plus the session rules every task must hold to.

**From the spec:**
- **Helm only.** Compose keeps its plaintext hops. The one compose change is removing the unused Redis (Task 6).
- **No API code change.** Kestrel TLS comes from env over an unchanged `appsettings.json` (spec §2).
- **Each chart CA key is never stored.** The new TLS Secrets follow `charts/authentik/templates/secret-internal-tls.yaml`: lookup-or-generate, `helm.sh/resource-policy: keep`, keys `required` on reuse.
- **Template-owned edge settings.** The per-class edge settings are set by the templates from `className`. When a profile sets the same annotation key, the chart's value wins (user decision 2026-10-12).
- **Security write-ups** (comments, test names, docs) describe conditions and behaviour, never step-by-step exploitation.
- **`docs/` is gitignored:** commit docs files with `git add -f`.
- **The user's running stack is off-limits.**
  - Never start, stop or modify its `iversonserver` containers, volumes, networks or images (`iverson-api:latest`, `iverson-api:0.1.0`, `localhost/iverson-api:0.1.0`), or another session's `rfdt-*` containers and `tei_models` volume.
  - Never run `docker compose up` against `Iverson.Server/docker-compose.yml`, except as Task 9's isolated `-p csr11c` project from a `container_name`-stripped copy.
  - Live checks and container probes use throwaway resources named `csr11c-*`, which they delete afterwards.
- **Memory.** Run one heavy process at a time, because a WSL OOM kills the whole VM. Wrap builds, test runs and probe containers in `systemd-run --user --scope -q -p MemoryMax=…` or `--memory`. Stage kind image loads under `/var/tmp`, never `/tmp`.
- **`docs/security/tma.md` is not edited.** The user's uncommitted TMA refresh holds it.

**Session rules:**
- **Location.** Work in the worktree `/home/ben/repositories/Iverson/.worktrees/csr11-transport-network` on branch `csr11-transport-network`. Use absolute paths, and never `cd` to the main checkout.
- **Commands.**
  - Use `command grep`, because plain `grep` is a gitignore-aware ugrep that skips `docs/`.
  - Every repo-wide grep excludes `.env*`. Nobody reads `Iverson.Server/.env` or the worktree's `.env` beyond the `val` helper Task 9 defines.
  - The docker CLI is podman: containers get no bridge IP, so publish on `127.0.0.1` or use a podman network with aliases.
  - Kill processes by PID, never with `pkill -f`.
  - Never use `git stash`.
  - dotnet builds and tests pass `-p:UseSharedCompilation=false`, then run `dotnet build-server shutdown`.
- **Helm.** Run `helm dependency build` in `Iverson.Server/deploy/helm/iverson` before every `helm template` or `helm lint`, because a stale `charts/*.tgz` renders old subchart content. Render cloud profiles with their `*.ci-override.yaml`, as CI does. The release is `iverson` in namespace `iverson`.
- **Commits.**
  - Add the task's files by explicit path. Never use `-A` or `.`, and never touch `.superpowers/` or `docs/security/tma.md`.
  - Messages are lowercase imperative and end with `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`. A subagent puts exactly this trailer, never its own model name.
- **Scratch.** Scratch files go under `$SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd`, never in the repo.
- **Shell state.** Each bash block runs in a fresh shell, and no variable or working directory carries over. Every block starts with `source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh`, which Task 0 writes. It defines `$SCR`, `$WT`, `$CH`, `$LIVE` and `$TRAILER`.

## File Structure

**Create:**
- `Iverson.Server/deploy/helm/iverson/charts/api/templates/secret-internal-tls.yaml`: the API's chart CA and certificate, `<release>-api-internal-tls` (Task 1).
- `Iverson.Server/deploy/helm/iverson/templates/prometheus-config.yaml`: the scrape config, rendered by the parent so the API job follows the API switch (Task 2).
- `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/secret-tls.yaml`: the FE's chart CA and certificate, `<release>-starrocks-tls` (Task 5).
- `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/configmap-fe.yaml`: the FE's full `fe.conf` plus the ssl lines (Task 5).
- `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/_helpers.tpl`: the keystore path and password constants (Task 5).
- `docs/runbooks/csr11-transport-network.md`: the operator runbook (Task 8).

**Modify:**
- Parent chart:
  - `templates/_helpers.tpl`: `iverson.backendTls`, `iverson.edgeAnnotations` (Task 1).
  - `templates/_validate.tpl`: the Azure trusted-root guard (Task 4).
  - `templates/networkpolicies.yaml`: the `ingress-nginx` peer gating and fallback gate (Task 7); the Redis policies removed (Task 6).
  - `values.yaml`: `networkPolicy.keepFallbacks` (Task 7); `redis:` removed (Task 6).
  - `values-{aws,azure,gcp,laptop,local}.yaml`: `redis:` removed (Task 6); `values-azure.yaml` gains the two root-name keys (Task 4).
  - `Chart.yaml`, `Chart.lock`: the redis dependency removed (Task 6).
- api subchart:
  - `values.yaml`: `ingress.appgwTrustedRootCertificate` (Task 1).
  - `templates/deployment.yaml`: Kestrel TLS env, mount and HTTPS probes (Task 1); StarRocks `SslMode`/`SslCa` and CA mount (Task 5).
  - `templates/ingress.yaml`, `admin-api-ingress.yaml`, `service.yaml`: per-class edge settings and port 80 (Task 1).
  - `templates/headless-service.yaml`: a comment repointed (Task 2).
- `charts/admin-ui/templates/ingress.yaml`: port 80 (Task 1).
- `charts/prometheus/templates/configmap.yaml`: deleted (Task 2). `charts/prometheus/templates/deployment.yaml`: the API CA mount (Task 2).
- `charts/worker/templates/deployment.yaml`: StarRocks `SslMode`/`SslCa` and CA mount (Task 5); a comment repointed (Task 2).
- authentik subchart:
  - `values.yaml`: `ingress.appgwTrustedRootCertificate` (Task 3).
  - `templates/configmap-tls-proxy.yaml`: `listen 9080 ssl` and the Host constant (Task 3).
  - `templates/deployment-server.yaml`: the tls-proxy probe scheme (Task 3); the Postgres CA mount and Redis password removal (Task 6).
  - `templates/ingress.yaml`, `service.yaml`, `backendconfig.yaml` (Task 3).
  - `templates/configmap.yaml`, `deployment-worker.yaml` (Task 6).
- `charts/starrocks/templates/starrockscluster.yaml`, `job-create-user.yaml` (Task 5).
- `charts/redis/`: deleted (Task 6).
- Terraform: `Iverson.Server/deploy/terraform/azure/main.tf`, `modules/cluster-azure/variables.tf`, `modules/cluster-azure/waf.tf` (Task 4).
- `Iverson.Server/docker-compose.yml`, `Iverson.Server/Iverson.LoadTest/scripts/stack.py` (Task 6).
- `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikContainerFixture.cs`, `AuthentikRecoveryFlowIntegrationTests.cs` (Task 6).
- `docs/runbooks/csr10-infrastructure-cutover.md`, `docs/runbooks/chunk-collection-cleanup-2026-07.md` (Task 8).

## Inherited from spec

The following assumptions were verified by `thorough-brainstorming` and its three design-review rounds, and are NOT re-verified here. They are trusted as ground truth; the spec holds each item's evidence. They are quoted in short form.

- **VA 1.** Kestrel serves TLS from PEM files by env alone
- **VA 2.** A plaintext endpoint beside TLS ones breaks `UseHttpsRedirection`
- **VA 3.** StarRocks FE 4.1.1 serves MySQL-protocol TLS from a PKCS12 keystore
- **VA 4.** MySqlConnector 2.4.0 `SslMode=VerifyFull;SslCa=`
- **VA 5.** `mysql:8.0.46` `--ssl-mode=VERIFY_IDENTITY --ssl-ca=`
- **VA 6.** Authentik 2026.5.3 does not use Redis
- **VA 7.** `appsettings.json` defines endpoints `Grpc` (http 8080, Http2) and `Http` (http 8081, Http1); no `ASPNETCORE_URLS`, `UseUrls` or `Listen*` overrides; …
- **VA 8.** `ClientPartitionKey.cs:16-23` keys on `Connection.RemoteIpAddress`, reading `X-Forwarded-For` only from a trusted peer — unchanged by in-process TLS.
- **VA 9.** In-cluster callers of the API are limited by `<release>-api-ingress` (`templates/networkpolicies.yaml:38-63`) under the namespace default-deny: …
- **VA 10.** Rendered Ingresses: laptop has api (nginx, 8080) and authentik (nginx, 9080); local has all four on nginx; aws, gcp and azure have api 8080, …
- **VA 11.** Authentik 9080 is admitted only from `ingress-nginx`/`clusterCidrs` (`networkpolicies.yaml:540-560`); the tls-proxy already serves 8443 with …
- **VA 12.** The API and worker reach Postgres with `SSL Mode=VerifyFull;Root Certificate=/etc/postgres-tls/ca.crt` from `<release>-postgres-ca` …
- **VA 13.** Credentialed StarRocks clients are exactly: API app, worker app and provisioner connection strings (`charts/api/templates/deployment.yaml:101`, …
- **VA 14.** Redis consumers: only Authentik (configmap, both deployments), the Redis NetworkPolicies, `Chart.yaml`/`Chart.lock`, `values.yaml` and the …
- **VA 15.** Operator v1.11.5: storage volumes are mounted under their own name, `storageClassName: emptyDir` yields an emptyDir (a size starting "0" skips the …
- **VA 16.** StarRocks 4.1.1 `fe_entrypoint.sh`: `update_conf_from_configmap` symlinks each ConfigMap file over `conf/<file>` (lines 189-210); `show frontends` …
- **VA 17.** The `ingress-nginx` peer appears at `templates/networkpolicies.yaml:47-48, 55-56, 470-471, 555-556`; parent values hold `api:`, `adminUi:` and …
- **VA 18.** No values file sets `dnsAnyDestination`/`apiServerAnyDestination` true (`values.yaml:32-33`); the cutover runbook applies them as fallbacks after …
- **VA 19.** azurerm `~> 3.90` (`terraform/azure/main.tf`) supports `trusted_root_certificate { name, data }` on `azurerm_application_gateway` (v3.117 docs); …
- **VA 20.** Kubernetes: with `scheme: HTTPS` "the kubelet sends an HTTPS request skipping the certificate verification."
- **VA 21.** ingress-nginx: `backend-protocol` accepts `HTTPS` and `GRPCS`; `proxy-ssl-verify` defaults to off.
- **VA 22.** AWS LBC: `backend-protocol-version` is valid with `HTTPS`; `healthcheck-protocol` defaults to `HTTP` (it does not follow the backend protocol); …
- **VA 23.** GKE: `cloud.google.com/app-protocols` maps Service port names to `HTTPS`/`HTTP2`; Google Cloud load balancers accept any backend certificate …
- **VA 24.** AGIC: HTTPS to pods needs a well-known CA or `appgw-trusted-root-certificate` naming a root installed on the gateway; Application Gateway v2 checks …
- **VA 25.** Kestrel PEM TLS under the API pod's constraints
- **VA 26.** Public certificate name
- **VA 27.** tls-proxy readiness probe
- **VA 28.** Authentik's internal TLS Secret predates this change
- **VA 29.** Authentik's issuer follows Host
- **VA 30.** Three Authentik containers load `<release>-authentik-config`
- **VA 31.** FE probes use the HTTP port
- **VA 32.** Parent templates see subchart ingress classes
- **VA 33.** Prometheus config
- **VA 34.** Prometheus 2.55.1 reloads config only at start, on SIGHUP or `/-/reload`
- **VA 35.** HTTP/2-only TLS Kestrel needs ALPN
- **VA 36.** nginx `grpcs` reaches HTTP/2-only TLS Kestrel
- **VA 37.** Kestrel loads the chart's key format
- **VA 38.** The keystore format loads in the FE's JDK
- **VA 39.** The Prometheus container probe needs real DNS for its target
- **VA 40.** The container-probe images are local

## Verified plan-level assumptions

These were newly introduced by this plan and verified at plan-write time (2026-10-12, at `92e44811`). Each task was drafted and then **replayed**: its own code blocks were applied to a fresh scratch worktree, and its bash blocks were run verbatim. The renders, checks and probes cited below come from those replays.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | Path | The files the plan creates do not exist; every file it modifies does | `git ls-tree HEAD` per directory: no `charts/api/templates/secret-internal-tls.yaml`, `templates/prometheus-config.yaml`, `charts/starrocks/templates/{secret-tls,configmap-fe,_helpers}.yaml` or `docs/runbooks/csr11-transport-network.md`. Every Modify target is listed by `ls`/`git ls-files` (the four drafting replays asserted create-must-not-exist and replace-exactly-once). |
| 2 | Command | Baseline: `helm dependency build`, then `helm template iverson . -n iverson -f values-<p>.yaml [-f values-<p>.ci-override.yaml]` and `helm lint` succeed on all five profiles | At `92e44811`: 5/5 `template=0`, `1 chart(s) linted, 0 chart(s) failed`; kubeconform Valid 95/74/101/101/101, Invalid 0 (local/laptop/aws/azure/gcp). Helm v3.16.4 locally; CI pins v3.15.0, not run (the functions used predate 3.15). |
| 3 | Signature | Named templates are shared between parent and subcharts in both directions | A scratch parent template called a subchart `define` and vice versa; precedent: `charts/api/templates/deployment.yaml:136-137` calls the parent's `iverson.embeddingBaseUrl`. So `iverson.backendTls` and `iverson.edgeAnnotations` live in the parent `_helpers.tpl` with an explicit dict argument. |
| 4 | Signature | The parent sees subchart values, including subchart defaults | Scratch parent template printed api/admin-api/admin-ui/authentik classNames: laptop `nginx|nginx||nginx`, aws `alb|alb|alb|alb`; `--set api.ingress.appgwTrustedRootCertificate` reaches the parent's `templates/prometheus-config.yaml`. |
| 5 | Code | The rendered Kestrel env serves TLS with the rendered certificate | `mcr.microsoft.com/dotnet/aspnet:10.0`, `--read-only --user 1000:1000`, the cert from the rendered `iverson-api-internal-tls`, and the API's own `Kestrel` section: HTTPS on 8081 (HTTP/1.1) and 8080 (HTTP/2), verified against the chart CA as `iverson-api`; plain HTTP and h2c got no response. Env names = spec VA 1/25/37. |
| 6 | Code | The parent-rendered Prometheus config is byte-identical to today's with the switch off, and scrapes over TLS with it on | `cmp` of the ConfigMap body vs HEAD's subchart render (azure, root unset; and local with `api.ingress.className=traefik`): identical. `promtool check config`: SUCCESS. A live `prom/prometheus:v2.55.1` (`--user 65534`) scraped a TLS Kestrel `up`; without `server_name` it was `down` (x509: no IP SANs). |
| 7 | Code | The prometheus pod reads a Secret volume mounted without `subPath` | prometheus runs `runAsUser: 65534`, `fsGroup: 65534` (`charts/prometheus/templates/deployment.yaml:35,37`); the new `api-ca` volume (default mode 0644, item `ca.crt`) was read live. |
| 8 | Code | nginx 1.30.5 accepts the rendered tls-proxy config, and 9080 TLS passes a constant Host | `nginx -t` passes on the laptop render (`listen 9080 ssl;` + 3 ssl lines) and on the azure render (plain branch). Live, over TLS on 9080 with the rendered `iverson-authentik-internal-tls`, the upstream saw `Host`/`X-Forwarded-Host: authentik.iverson.local` whatever Host was sent; plain HTTP to 9080 got 400. |
| 9 | Code | Rendered annotations match the spec §2 table on every class, with no duplicate keys | Parsed with a duplicate-refusing YAML loader on all five profiles. The literal `backend-protocol: "GRPC"` is replaced (once) by `GRPCS`; a profile `backend-protocol: HTTP` override renders `HTTPS` (chart wins). |
| 10 | Code | The StarRocksCluster changes validate against the operator 1.11.5 CRD | `helm show crds operator --repo https://starrocks.github.io/starrocks-kubernetes-operator --version 1.11.5` (md5 `a6bf1948967030a17add7b963d822546`): 0 jsonschema errors on local/aws/gcp/azure; the checker fails on a deliberately broken render. |
| 11 | Code | `fe.conf` is StarRocks 4.1.1's own default, which fe-ubuntu ships unchanged | `gh api repos/StarRocks/starrocks/contents/conf/fe.conf?ref=4.1.1` (sha `cc82b61d…`, md5 `defe03a7a2fe9cb76f2503aac32bd830`); `build.sh:602` copies it into `output/fe/conf/`, which `artifact.Dockerfile:66` copies into the image. The operator parses it with viper `properties` (probe pinned to the operator's `go.mod` versions). |
| 12 | Code | The rendered init container builds the keystore, and FE serves TLS from it; the rendered Job and client strings verify | Container probe on rendered artifacts: the init script ran in `mysql:8.0.46` as uid 1000, `--read-only`, `--cap-drop ALL`, rc=0, leaving only `fe.p12` (0644). An allin1 FE with the rendered `ssl_*` lines logged "enable ssl connection successfully" and kept its BE `Alive`. The full rendered Job completed (`status`: TLS_AES_256_GCM_SHA384). A wrong CA or non-SAN host failed; plaintext still answered. MySqlConnector: 3×OK with the chart CA, 3×`SSL Authentication Error` with a wrong one. |
| 13 | Ordering | The TLS Secrets exist before their consumers | `<release>-starrocks-tls` is an ordinary resource, so it exists before the post-install/post-upgrade Job and the FE init container. The api and authentik Secrets are ordinary resources mounted by their own pods. |
| 14 | Command | `helm dependency update` regenerates `Chart.lock` offline and deterministically for `file://` deps | Two independent runs gave digest `sha256:9f915316939a0c0b38c639961b7c1ba8c86aa37785c5e08accf2145ae9740011`; `helm dependency build` then succeeds (it rejects a stale-digest lock). |
| 15 | Code | Authentik 2026.5.3 runs without Redis (live) | `AuthentikRecoveryFlowIntegrationTests` + `AuthentikOrchestratorRoleBlueprintTests`, with the fixture starting Postgres only: 16/16 passed. |
| 16 | Consumer | Compose has three Authentik services wired to Redis | `authentik-migrate`, `-server`, `-worker`: 3× `AUTHENTIK_REDIS__HOST`, 3× `depends_on: redis`; all removed. The edited compose's `up --dry-run -d --no-deps qdrant tei-embed postgres authentik-server iverson-api` succeeds (compose 2.40.3). |
| 17 | Consumer | `stack.py` has no dry-run mode | `run_compose_up` builds `["docker","compose","up","-d","--no-deps",*services]`; verified by stubbing `subprocess.run`: the `query` command no longer names `redis`. |
| 18 | Consumer | No test, script or CI step parses the edited templates by path | `git grep` for chart paths in `*.cs`/`*.py`/`*.sh` finds only `AuthentikContainerFixture.cs:27`, ClientConformance (blueprints, untouched) and `build-and-load-image.sh` (values-laptop path). CI renders the five profiles with their overrides through kubeconform and kube-score; the kube-score CRITICAL/WARNING sets are unchanged on every profile. |
| 19 | Consumer | Nothing reads `charts/prometheus/templates/configmap.yaml` by path | The ConfigMap is mounted only by name (`deployment.yaml:75`); no checksum annotations exist. Two comments name the old path (`charts/api/templates/headless-service.yaml`, `charts/worker/templates/headless-service.yaml`); Task 2 repoints them. |
| 20 | Code | The Azure guard passes under `helm template` and fires on a live Secret | Real render (azure, names empty): rc=0 (`lookup` is empty without a cluster). Scratch copy with the lookup replaced by a stub Secret: both names empty rc=1, either empty rc=1, both set rc=0, gcp rc=0. |
| 21 | Code | The Terraform trusted roots validate, and `data` takes PEM text | `fmt -check` rc=0; `init -backend=false` + `validate` Success in `azure/` and `modules/cluster-azure` (terraform v1.9.8, azurerm 3.117.1); tfsec "No problems detected!". The provider schema has `trusted_root_certificate{ name, data (sensitive), key_vault_secret_id }`, and the provider base64-encodes `data` itself (`expandApplicationGatewayTrustedRootCertificates`). The variable is not `sensitive`, because a sensitive map cannot drive `for_each`. Not applied against Azure. |
| 22 | Code | The `ingress-nginx` peer gating never leaves an empty `from` | All five profiles: every rule's `from` is non-empty (`clusterCidrs` is non-empty, enforced at the top of `networkpolicies.yaml`). Peer present only on nginx-class Ingresses: local all four rules; laptop api 8080/8081 and authentik 9080 (admin-ui is disabled there); aws/azure/gcp none. Only the namespace peer changes. |
| 23 | Code | The fallback gate fires without `keepFallbacks`, and passes with it | `dnsAnyDestination=true` or `apiServerAnyDestination=true` alone: rc=1; with `keepFallbacks=true`: renders (10 `to: []` for DNS, 3 for the API server). The gate sits in `networkpolicies.yaml`'s `networkPolicy.enabled` block, so nothing fires when policies are off. |
| 24 | Consumer | The fallback gate breaks the cutover runbook's procedure unless the runbook changes | `docs/runbooks/csr10-infrastructure-cutover.md:133-134` sets the toggles by an upgrade; Task 8 adds `keepFallbacks: true` there. |
| 25 | Consumer | The chunk-cleanup runbook's API calls break under TLS | `docs/runbooks/chunk-collection-cleanup-2026-07.md:27-32` uses h2c on 8080. Its `kubectl exec … curl` variant was already broken: `mcr.microsoft.com/dotnet/aspnet:10.0` has no `curl` or `wget` (`command -v`). Task 8 rewrites it for TLS through a port-forward. |
| 26 | Code | The Task 9 CreateTenant console compiles against the client library | `dotnet build -c Release` of the console (Program.cs as in Task 9 Step 8) → `Build succeeded`. |
| 27 | Command | kind exposes ingress-nginx on `127.0.0.1:8080` (HTTP) and `127.0.0.1:8443` (HTTPS); the laptop host is `iverson.local` | `deploy/kind/kind-config.yaml` extraPortMappings 80→8080, 443→8443 on 127.0.0.1; `values.yaml:43` `ingressHost: "iverson.local"`; setup.sh creates a self-signed `iverson-api-tls` for `iverson.local`. |
| 28 | Command | The tls-proxy image has `curl`; the API image does not | `nginx-unprivileged:1.30.5-alpine`: `/usr/bin/curl`. `dotnet/aspnet:10.0`: neither `curl` nor `wget`. |
| 29 | Consumer | The authentik egress policy already allows Postgres 5432 | `templates/networkpolicies.yaml` authentik-egress rule to `cnpg.io/cluster` on 5432, so `verify-full` needs no new rule. |
| 30 | Code | Prometheus mounts the API CA on every profile | The subchart cannot see the API class, so the mount is unconditional. That is safe because `<release>-api-internal-tls` renders whenever the api subchart does (`api.enabled: true`; no profile overrides it). |
| 31 | Decision | Other ingress classes leave the switch off; a mismatched admin-api class fails the render | A class other than nginx/alb/gce/azure-application-gateway gets `backendTls` false (no backend protocol exists for it), so the hop stays as today. `admin-api-ingress.yaml` fails when the API serves TLS and `api.adminApiIngress.className` cannot reach it (user decision 2026-10-12). No profile hits it. On gce the backend protocol comes from the api Service's `app-protocols`, which follows `api.ingress.className`, so the guard also fails a `gce` admin-api under a non-`gce` API class (GCE's plain-HTTP default for an unmapped port accepted unverified, user decision 2026-10-12, CIR-1 §3.2). |
| 32 | Code | The create-user Job finishes on a multi-FE upgrade while plaintext and TLS FEs share the Service | CIR-1 probe: the rendered script behind a per-connection balancer mixing a TLS FE and a plaintext endpoint. With `AUTH` decided in a separate `if`, the Job was still running at 120 s at both 2:1 and 1:2. With `AUTH` decided inside the readiness loop (Task 5 Step 6c), it exited 0 at 2:1, and with no root password. At 1:2, a grant batch that hit the plaintext endpoint exited 1, which `backoffLimit: 6` retries. The fixed script passes `sh -n`, and the Step 8 checker counts 4 calls. |
| 33 | Consumer | `/admin/reconcile` answers only on 8080 | `Iverson.Api/Program.cs:621` `RequireListenerPort(8080)`; `ListenerPortGateAsync` (`:939-947`, registered at `:512`) returns 404 on any other local port. CIR-1 probe: a TLS HTTP/2 POST to 8080 with the rendered Kestrel env reached routing. |
| 34 | Code | The trusted-root block plans with a non-empty map | `terraform test` with a mocked azurerm provider (terraform v1.9.8, azurerm 3.117.1), PEMs extracted from the azure render: `Success! 2 passed, 0 failed.`; the reviewer's negative control (empty map asserting `== 1`) fails. |

## Tasks

Order: Task 1 first, then Tasks 2 and 3, which use Task 1's helpers. Task 4 consumes the root-name values from Tasks 1 and 3. Tasks 5, 6 and 7 are independent of each other and of Tasks 2–4; they anchor their edits on text, not line numbers, because several tasks touch the same files. Task 8 documents the result and Task 9 verifies it.

### Task 0: Branch

**Files:** none in the repo.

- [ ] **Step 1: Create the worktree and the scratch environment.**

```bash
cd /home/ben/repositories/Iverson && git status --porcelain -- . ':!docs/security/tma.md' ':!22' ':!fsc' ':!sego' | head
git -C /home/ben/repositories/Iverson worktree add -b csr11-transport-network /home/ben/repositories/Iverson/.worktrees/csr11-transport-network main
mkdir -p /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd
cat > /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh <<'X'
export SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd
export WT=/home/ben/repositories/Iverson/.worktrees/csr11-transport-network
export CH=$WT/Iverson.Server/deploy/helm/iverson
export LIVE=$SCR/live
export TRAILER='Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>'
X
git -C /home/ben/repositories/Iverson/.worktrees/csr11-transport-network log -1 --format=%H
```

Expected:
- The first command prints nothing, so the main checkout has no other uncommitted work.
- The `git worktree add` succeeds.
- The last line prints `main`'s HEAD, which is the commit that holds this plan.

### Task 1: API and console edge (#4 edge → API, #6)

**Files:**
- Create: `Iverson.Server/deploy/helm/iverson/charts/api/templates/secret-internal-tls.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/templates/_helpers.tpl`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/api/values.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml`, `ingress.yaml`, `admin-api-ingress.yaml` and `service.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/admin-ui/templates/ingress.yaml`

**Interfaces:**
- Produces two named templates in the parent `templates/_helpers.tpl`. Helm loads parent and subchart templates into one set, so the subcharts (Tasks 1 and 3) and the parent (Task 2) all call them. The api subchart already calls the parent's `iverson.embeddingEnv` this way.
  - `iverson.backendTls`: called as `include "iverson.backendTls" (dict "className" <ingress class> "trustedRoot" <appgwTrustedRootCertificate>)`. It prints `true` or nothing, so callers compare with `eq … "true"`.
  - `iverson.edgeAnnotations`: called as `include "iverson.edgeAnnotations" (dict "className" "backendTls" "grpc" "trustedRoot" "host" "probePort" "probePath") | fromYaml`. It returns the Ingress annotations the chart sets for that class.
- Produces the Secret `<release>-api-internal-tls` (keys `tls.crt`, `tls.key`, `ca.crt`). It renders on every profile. Task 2 mounts its `ca.crt` in Prometheus, and the Azure runbook copies its `ca.crt` into Terraform.
- Produces the value `api.ingress.appgwTrustedRootCertificate` (default `""`), which the Azure guard task reads.
- Produces the scripts `$SCR/edge/render.sh`, `$SCR/edge/nodup.py`, `$SCR/edge/objdiff.py`, `$SCR/edge/kestrel.sh` and the baseline renders in `$SCR/edge/base/`, which Tasks 2 and 3 reuse.

The switch: the nginx, alb and gce classes are always on. `azure-application-gateway` is on only once the trusted-root name is set. Any other class (including the subchart default `""`) is off. The chart sets no backend-protocol for such a class, so its edge would keep speaking plaintext to a TLS port; off keeps today's hop.

- [ ] **Step 1: Baseline renders and check tools.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh; mkdir -p $SCR/edge
cat > $SCR/edge/render.sh <<'SH'
#!/usr/bin/env bash
# Renders, lints and kubeconforms the five profiles into $1 (release iverson, -n iverson), as CI does.
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
O=$1; mkdir -p $O; cd $CH && helm dependency build . >/dev/null
for v in values-local values-laptop values-aws values-azure values-gcp; do o="-f $v.yaml"; [ -f $v.ci-override.yaml ] && o="$o -f $v.ci-override.yaml"
  helm template iverson . -n iverson $o > $O/$v.yaml; e=$?
  echo "$v template=$e lint=$(helm lint . $o 2>&1 | tail -1) | $(kubeconform -kubernetes-version 1.30.0 -summary -ignore-missing-schemas < $O/$v.yaml 2>&1 | tail -1 | sed 's/.*- //')"; done
SH
cat > $SCR/edge/nodup.py <<'PY'
# A YAML loader that refuses duplicate mapping keys (PyYAML otherwise keeps the last one silently),
# plus helpers for reading a rendered profile.
import yaml
from yaml.constructor import ConstructorError
class NoDupLoader(yaml.SafeLoader): pass
def _mapping(loader, node, deep=False):
    keys = [loader.construct_object(k, deep=deep) for k, _ in node.value]
    dups = sorted({str(k) for k in keys if keys.count(k) > 1})
    if dups: raise ConstructorError(None, None, f"duplicate keys {dups}", node.start_mark)
    return loader.construct_mapping(node, deep)
NoDupLoader.add_constructor(yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, _mapping)
def load(path): return [d for d in yaml.load_all(open(path), Loader=NoDupLoader) if d]
def get(docs, kind, name): return next((d for d in docs if d["kind"] == kind and d["metadata"]["name"] == name), None)
PY
cat > $SCR/edge/objdiff.py <<'PY'
# objdiff.py <before.yaml> <after.yaml>: the objects added, removed or changed between two renders.
# Secret data is ignored: genCA output differs on every render.
import sys
from nodup import load   # nodup.py sits beside this script
def objs(p):
    out = {}
    for d in load(p):
        if d["kind"] == "Secret": d = {k: v for k, v in d.items() if k not in ("data", "stringData")}
        out[(d["kind"], d["metadata"]["name"])] = d
    return out
a, b = objs(sys.argv[1]), objs(sys.argv[2])
print(" ".join(f"{'+' if k not in a else '-' if k not in b else '~'}{k[0]}/{k[1]}" for k in sorted(set(a) | set(b)) if a.get(k) != b.get(k)) or "none")
PY
chmod +x $SCR/edge/render.sh
python3 -c "import sys; sys.path.insert(0, '$SCR/edge'); import nodup, yaml
try: yaml.load('a: 1\na: 2', Loader=nodup.NoDupLoader); print('nodup FAILED to refuse')
except yaml.YAMLError as e: print('nodup refuses:', str(e).splitlines()[0])"
$SCR/edge/render.sh $SCR/edge/base
```

Expected:

```text
nodup refuses: duplicate keys ['a']
values-local template=0 lint=1 chart(s) linted, 0 chart(s) failed | Valid: 95, Invalid: 0, Errors: 0, Skipped: 6
values-laptop template=0 lint=1 chart(s) linted, 0 chart(s) failed | Valid: 74, Invalid: 0, Errors: 0, Skipped: 5
values-aws template=0 lint=1 chart(s) linted, 0 chart(s) failed | Valid: 101, Invalid: 0, Errors: 0, Skipped: 6
values-azure template=0 lint=1 chart(s) linted, 0 chart(s) failed | Valid: 101, Invalid: 0, Errors: 0, Skipped: 6
values-gcp template=0 lint=1 chart(s) linted, 0 chart(s) failed | Valid: 101, Invalid: 0, Errors: 0, Skipped: 7
```

(If an earlier task in this plan has already changed the object counts, the `Valid` numbers differ. What matters is `template=0`, `0 chart(s) failed` and `Invalid: 0, Errors: 0`.)

- [ ] **Step 2: The two helpers.** Append to the end of `templates/_helpers.tpl`:

```yaml

{{/*
CSR round-11 #4: whether the edge reaches a pod over TLS ("backendTls"), from the pod's Ingress
class. Called with (dict "className" <ingress class> "trustedRoot" <appgwTrustedRootCertificate>) by
the api and Authentik subcharts and by the parent's Prometheus config; prints "true" or nothing.
nginx, alb and gce encrypt to any backend certificate. Application Gateway verifies it, so it needs
the chart CA installed as a trusted root first: true only once the root's name is set. Any other
class gets no backend-protocol settings from this chart, so its edge would keep speaking plaintext
to a TLS port: false, and the hop stays as before.
*/}}
{{- define "iverson.backendTls" -}}
{{- if has .className (list "nginx" "alb" "gce") -}}
true
{{- else if and (eq .className "azure-application-gateway") .trustedRoot -}}
true
{{- end -}}
{{- end -}}

{{/*
CSR round-11 #4, #6: the Ingress annotations this chart sets per class, as YAML for `fromYaml`;
callers merge them over the profile's own annotations, so a key appears once and the chart's value
wins. Argument: (dict "className" "backendTls" "grpc" "trustedRoot" "host" "probePort" "probePath").
- Port 80, on every call: gce turns HTTP off; Application Gateway answers it only with a redirect.
- backendTls (iverson.backendTls of the pod behind the Ingress) sets the edge → pod protocol and,
  where the health check does not follow it, the health check: on alb the protocol, and the path
  except for gRPC (ALB's gRPC check has its own default); on Application Gateway the trusted root,
  the host name the pod certificate carries (host), and the probe port and path.
- grpc marks the API's gRPC Ingress: nginx speaks GRPC to it, GRPCS once backendTls is on.
*/}}
{{- define "iverson.edgeAnnotations" -}}
{{- $a := dict -}}
{{- if eq .className "gce" -}}
{{- $_ := set $a "kubernetes.io/ingress.allow-http" "false" -}}
{{- else if eq .className "azure-application-gateway" -}}
{{- $_ := set $a "appgw.ingress.kubernetes.io/ssl-redirect" "true" -}}
{{- end -}}
{{- if and (eq .className "nginx") .grpc -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/backend-protocol" (ternary "GRPCS" "GRPC" .backendTls) -}}
{{- else if .backendTls -}}
{{- if eq .className "nginx" -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/backend-protocol" "HTTPS" -}}
{{- else if eq .className "alb" -}}
{{- $_ := set $a "alb.ingress.kubernetes.io/backend-protocol" "HTTPS" -}}
{{- $_ := set $a "alb.ingress.kubernetes.io/healthcheck-protocol" "HTTPS" -}}
{{- if not .grpc -}}
{{- $_ := set $a "alb.ingress.kubernetes.io/healthcheck-path" .probePath -}}
{{- end -}}
{{- else if eq .className "azure-application-gateway" -}}
{{- $_ := set $a "appgw.ingress.kubernetes.io/backend-protocol" "https" -}}
{{- $_ := set $a "appgw.ingress.kubernetes.io/appgw-trusted-root-certificate" .trustedRoot -}}
{{- $_ := set $a "appgw.ingress.kubernetes.io/backend-hostname" .host -}}
{{- $_ := set $a "appgw.ingress.kubernetes.io/health-probe-hostname" .host -}}
{{- $_ := set $a "appgw.ingress.kubernetes.io/health-probe-port" (toString .probePort) -}}
{{- $_ := set $a "appgw.ingress.kubernetes.io/health-probe-path" .probePath -}}
{{- end -}}
{{- end -}}
{{- toYaml $a -}}
{{- end -}}
```

- [ ] **Step 3: The API's certificate.** Create `charts/api/templates/secret-internal-tls.yaml` with exactly:

```yaml
{{- /* CSR round-11 #4: the certificate Kestrel serves on 8080 and 8081 when the edge reaches the API
   over TLS (iverson.backendTls), and the CA Prometheus and, on Azure, the Application Gateway trust
   for it. Lookup-or-generate like charts/authentik/templates/secret-internal-tls.yaml: an existing
   Secret is reused unchanged, so upgrades keep the same CA. The CA private key is never stored, so
   nothing in the cluster can issue more certificates under it. Rendered on every profile, so the CA
   exists before the Azure trusted-root step. Rotate by deleting this Secret, upgrading, then
   restarting the API pods. The name must never equal a public ingress certificate's. */}}
{{- $name := printf "%s-api-internal-tls" .Release.Name }}
{{- if eq $name .Values.ingress.tlsSecretName }}
{{- fail (printf "api.ingress.tlsSecretName must not be %s: that is the chart-owned internal TLS Secret" $name) }}
{{- end }}
{{- if eq $name .Values.adminApiIngress.tlsSecretName }}
{{- fail (printf "api.adminApiIngress.tlsSecretName must not be %s: that is the chart-owned internal TLS Secret" $name) }}
{{- end }}
apiVersion: v1
kind: Secret
metadata:
  name: {{ $name }}
  annotations:
    "helm.sh/resource-policy": keep
type: Opaque
data:
{{- $existing := lookup "v1" "Secret" .Release.Namespace $name }}
{{- if $existing }}
{{- range $key := list "tls.crt" "tls.key" "ca.crt" }}
  {{ $key }}: {{ index $existing.data $key | required (printf "Secret %s has no %s: delete it and upgrade to regenerate it (see the comment at the top of this template)" $name $key) }}
{{- end }}
{{- else }}
{{- $svc := printf "%s-api" .Release.Name }}
{{- $ca := genCA "iverson-api-ca" 3650 }}
{{- $cert := genSignedCert $svc nil (list $svc (printf "%s.%s.svc" $svc .Release.Namespace) (printf "%s.%s.svc.cluster.local" $svc .Release.Namespace)) 3650 $ca }}
  tls.crt: {{ $cert.Cert | b64enc }}
  tls.key: {{ $cert.Key | b64enc }}
  ca.crt: {{ $ca.Cert | b64enc }}
{{- end }}
```

- [ ] **Step 4: The trusted-root value.** In `charts/api/values.yaml`, replace:

```yaml
  # any operator who still (harmlessly but confusingly) set it.
  tlsSecretName: ""
```

with:

```yaml
  # any operator who still (harmlessly but confusingly) set it.
  tlsSecretName: ""
  # CSR round-11 #4: on className azure-application-gateway, the name of the Application Gateway
  # trusted root holding this release's <release>-api-internal-tls ca.crt. Empty keeps the
  # gateway → API hops plaintext; set, both API Ingresses (8080 and 8081, one Kestrel) and the
  # Prometheus scrape switch to TLS. Other classes ignore it (iverson.backendTls in the parent's
  # templates/_helpers.tpl).
  appgwTrustedRootCertificate: ""
```

- [ ] **Step 5: Kestrel TLS in the api Deployment.** Four edits in `charts/api/templates/deployment.yaml`.
  1. Replace:

```yaml
{{- end }}
{{- end }}
apiVersion: v1
kind: ServiceAccount
```

with:

```yaml
{{- end }}
{{- end }}
{{- $backendTls := eq (include "iverson.backendTls" (dict "className" .Values.ingress.className "trustedRoot" .Values.ingress.appgwTrustedRootCertificate)) "true" }}
apiVersion: v1
kind: ServiceAccount
```

  2. Replace:

```yaml
            - name: Prometheus__BaseUrl
              value: "http://{{ .Release.Name }}-prometheus:9090"
            {{- end }}
          resources:
```

with:

```yaml
            - name: Prometheus__BaseUrl
              value: "http://{{ .Release.Name }}-prometheus:9090"
            {{- end }}
            {{- if $backendTls }}
            # CSR round-11 #4: both endpoints serve TLS from <release>-api-internal-tls (the ingress,
            # Prometheus and the kubelet are their only callers). Overrides appsettings.json's http
            # URLs, which compose and local runs keep. All-TLS: a plaintext endpoint beside TLS ones
            # breaks UseHttpsRedirection.
            - name: Kestrel__Endpoints__Grpc__Url
              value: "https://*:8080"
            - name: Kestrel__Endpoints__Grpc__Certificate__Path
              value: "/etc/iverson/api-tls/tls.crt"
            - name: Kestrel__Endpoints__Grpc__Certificate__KeyPath
              value: "/etc/iverson/api-tls/tls.key"
            - name: Kestrel__Endpoints__Http__Url
              value: "https://*:8081"
            - name: Kestrel__Endpoints__Http__Certificate__Path
              value: "/etc/iverson/api-tls/tls.crt"
            - name: Kestrel__Endpoints__Http__Certificate__KeyPath
              value: "/etc/iverson/api-tls/tls.key"
            {{- end }}
          resources:
```

  3. Replace:

```yaml
              # Http1 endpoint the same /health routes are also mapped to.
              port: 8081
            initialDelaySeconds: 10
          livenessProbe:
            httpGet:
              path: /health/live
              port: 8081
            initialDelaySeconds: 30
```

with:

```yaml
              # Http1 endpoint the same /health routes are also mapped to.
              port: 8081
              {{- if $backendTls }}
              scheme: HTTPS
              {{- end }}
            initialDelaySeconds: 10
          livenessProbe:
            httpGet:
              path: /health/live
              port: 8081
              {{- if $backendTls }}
              scheme: HTTPS
              {{- end }}
            initialDelaySeconds: 30
```

  4. Replace:

```yaml
            - name: tmp
              mountPath: /tmp
      volumes:
```

with:

```yaml
            - name: tmp
              mountPath: /tmp
            {{- if $backendTls }}
            - name: api-tls
              mountPath: /etc/iverson/api-tls
              readOnly: true
            {{- end }}
      volumes:
```

  and replace the file's last two lines:

```yaml
        - name: tmp
          emptyDir: {}
```

with:

```yaml
        - name: tmp
          emptyDir: {}
        {{- if $backendTls }}
        - name: api-tls
          secret:
            secretName: {{ .Release.Name }}-api-internal-tls
            items:
              - key: tls.crt
                path: tls.crt
              - key: tls.key
                path: tls.key
        {{- end }}
```

- [ ] **Step 6: The api Ingress.** In `charts/api/templates/ingress.yaml`, replace:

```yaml
{{- $oidcOrigin := printf "%s://authentik.%s" .Values.global.externalScheme .Values.global.ingressHost }}
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: {{ .Release.Name }}-api
  annotations:
    {{- with .Values.ingress.annotations }}
    {{- toYaml . | nindent 4 }}
    {{- end }}
    {{- if eq .Values.ingress.className "nginx" }}
    nginx.ingress.kubernetes.io/backend-protocol: "GRPC"
    nginx.ingress.kubernetes.io/configuration-snippet: |
```

with:

```yaml
{{- $oidcOrigin := printf "%s://authentik.%s" .Values.global.externalScheme .Values.global.ingressHost }}
{{- /* CSR round-11 #4, #6: the class's edge → API protocol and port-80 settings (iverson.edgeAnnotations,
   parent templates/_helpers.tpl), merged over the profile's annotations. */}}
{{- $backendTls := eq (include "iverson.backendTls" (dict "className" .Values.ingress.className "trustedRoot" .Values.ingress.appgwTrustedRootCertificate)) "true" }}
{{- $edge := include "iverson.edgeAnnotations" (dict "className" .Values.ingress.className "backendTls" $backendTls "grpc" true "trustedRoot" .Values.ingress.appgwTrustedRootCertificate "host" (printf "%s-api" .Release.Name) "probePort" 8081 "probePath" "/health") | fromYaml }}
{{- $annotations := mergeOverwrite (deepCopy (default dict .Values.ingress.annotations)) $edge }}
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: {{ .Release.Name }}-api
  annotations:
    {{- with $annotations }}
    {{- toYaml . | nindent 4 }}
    {{- end }}
    {{- if eq .Values.ingress.className "nginx" }}
    nginx.ingress.kubernetes.io/configuration-snippet: |
```

The nginx `backend-protocol` moves from the literal line into the merged map (GRPC → GRPCS), so a profile that sets the same key no longer renders it twice.

- [ ] **Step 7: The admin-api Ingress.** In `charts/api/templates/admin-api-ingress.yaml`, replace:

```yaml
{{- end }}
{{- end }}
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: {{ .Release.Name }}-admin-api
  annotations:
    {{- toYaml .Values.adminApiIngress.annotations | nindent 4 }}
```

with:

```yaml
{{- end }}
{{- end }}
{{- /* CSR round-11 #4, #6: one Kestrel serves 8080 and 8081, so this Ingress follows the API pod's
   backendTls (keyed on api.ingress) and must be able to reach it over TLS. On gce the protocol comes
   from the api Service's app-protocols, which follows api.ingress.className. */}}
{{- $backendTls := eq (include "iverson.backendTls" (dict "className" .Values.ingress.className "trustedRoot" .Values.ingress.appgwTrustedRootCertificate)) "true" }}
{{- if and $backendTls (or (ne (include "iverson.backendTls" (dict "className" .Values.adminApiIngress.className "trustedRoot" .Values.ingress.appgwTrustedRootCertificate)) "true") (and (eq .Values.adminApiIngress.className "gce") (ne .Values.ingress.className "gce"))) }}
{{- fail (printf "api.adminApiIngress.className %q cannot reach the API over TLS, which api.ingress.className %q turns on (CSR round-11 #4): give both API Ingresses the same class" .Values.adminApiIngress.className .Values.ingress.className) }}
{{- end }}
{{- $edge := include "iverson.edgeAnnotations" (dict "className" .Values.adminApiIngress.className "backendTls" $backendTls "grpc" false "trustedRoot" .Values.ingress.appgwTrustedRootCertificate "host" (printf "%s-api" .Release.Name) "probePort" 8081 "probePath" "/health") | fromYaml }}
{{- $annotations := mergeOverwrite (deepCopy (default dict .Values.adminApiIngress.annotations)) $edge }}
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: {{ .Release.Name }}-admin-api
  annotations:
    {{- with $annotations }}
    {{- toYaml . | nindent 4 }}
    {{- end }}
```

The anchor `{{- end }}` / `{{- end }}` / `apiVersion:` is unique: it is the close of the placeholder and lookup guards above it.

- [ ] **Step 8: The api Service (GCE).** In `charts/api/templates/service.yaml`, replace:

```yaml
  name: {{ .Release.Name }}-api
spec:
```

with:

```yaml
  name: {{ .Release.Name }}-api
  {{- /* CSR round-11 #4: GCE's load balancer speaks to each port by its name's entry: HTTP/2 over
     TLS to gRPC, HTTPS to the HTTP/1.1 port (iverson.backendTls). */}}
  {{- if and (eq .Values.ingress.className "gce") (eq (include "iverson.backendTls" (dict "className" .Values.ingress.className "trustedRoot" .Values.ingress.appgwTrustedRootCertificate)) "true") }}
  annotations:
    cloud.google.com/app-protocols: '{"grpc": "HTTP2", "http": "HTTPS"}'
  {{- end }}
spec:
```

- [ ] **Step 9: The admin-ui Ingress (port 80 only).** In `charts/admin-ui/templates/ingress.yaml`, replace:

```yaml
{{- $oidcOrigin := printf "%s://authentik.%s" .Values.global.externalScheme .Values.global.ingressHost }}
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: {{ .Release.Name }}-admin-ui
  annotations:
    nginx.ingress.kubernetes.io/rewrite-target: /$2
    {{- with .Values.ingress.annotations }}
    {{- toYaml . | nindent 4 }}
    {{- end }}
```

with:

```yaml
{{- $oidcOrigin := printf "%s://authentik.%s" .Values.global.externalScheme .Values.global.ingressHost }}
{{- /* CSR round-11 #6: the class's port-80 setting only (iverson.edgeAnnotations); the edge → admin-ui
   hop serves static files and stays plaintext. */}}
{{- $edge := include "iverson.edgeAnnotations" (dict "className" .Values.ingress.className "backendTls" false) | fromYaml }}
{{- $annotations := mergeOverwrite (dict "nginx.ingress.kubernetes.io/rewrite-target" "/$2") (deepCopy (default dict .Values.ingress.annotations)) $edge }}
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: {{ .Release.Name }}-admin-ui
  annotations:
    {{- toYaml $annotations | nindent 4 }}
```

- [ ] **Step 10: Render, parse and check the guards.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
$SCR/edge/render.sh $SCR/edge/t1
for v in values-local values-laptop values-aws values-azure values-gcp; do echo "$v: $(python3 $SCR/edge/objdiff.py $SCR/edge/base/$v.yaml $SCR/edge/t1/$v.yaml)"; done
cd $CH
helm template iverson . -n iverson -f values-azure.yaml -f values-azure.ci-override.yaml --set api.ingress.appgwTrustedRootCertificate=iverson-api-ca > $SCR/edge/t1-azure-root.yaml
helm template iverson . -n iverson -f values-aws.yaml -f values-aws.ci-override.yaml --set-string 'api.ingress.annotations.alb\.ingress\.kubernetes\.io/backend-protocol=HTTP' > $SCR/edge/t1-aws-collide.yaml
for s in api.ingress.tlsSecretName=iverson-api-internal-tls api.adminApiIngress.tlsSecretName=iverson-api-internal-tls api.adminApiIngress.className=traefik; do
  helm template iverson . -n iverson -f values-local.yaml --set $s 2>&1 | head -1 | cut -c1-120; done
helm template iverson . -n iverson -f values-azure.yaml -f values-azure.ci-override.yaml --set api.adminApiIngress.className=traefik >/dev/null && echo "azure, root unset, admin-api traefik: renders"
python3 - $SCR <<'PY'
import sys, re
sys.path.insert(0, sys.argv[1] + "/edge"); from nodup import load, get
E = sys.argv[1] + "/edge"
KEYS = r"backend-protocol|healthcheck|health-probe|backend-hostname|trusted-root|allow-http|ssl-redirect|rewrite-target"
def show(path, label):
    docs = load(path)
    c = get(docs, "Deployment", "iverson-api")["spec"]["template"]["spec"]["containers"][0]
    kes = {e["name"][len("Kestrel__Endpoints__"):]: e["value"] for e in c["env"] if e["name"].startswith("Kestrel__")}
    urls = ",".join(v for k, v in kes.items() if k.endswith("__Url"))
    certs = sorted({v for k, v in kes.items() if "Certificate" in k})
    mnt = [m["mountPath"] for m in c["volumeMounts"] if m["name"] == "api-tls"]
    vol = [v["secret"]["secretName"] for v in get(docs, "Deployment", "iverson-api")["spec"]["template"]["spec"]["volumes"] if v["name"] == "api-tls"]
    sec = get(docs, "Secret", "iverson-api-internal-tls")
    print(f"{label}: kestrel={len(kes)} urls={urls or '-'} certs={certs or '-'} mount={mnt or '-'} vol={vol or '-'} "
          f"probes={c['readinessProbe']['httpGet'].get('scheme', 'HTTP')}/{c['livenessProbe']['httpGet'].get('scheme', 'HTTP')} "
          f"secret={sorted(sec['data'])} {sec['metadata']['annotations']['helm.sh/resource-policy']}")
    svc = get(docs, "Service", "iverson-api")["metadata"].get("annotations")
    print(f"  svc {svc}")
    for n in ("iverson-api", "iverson-admin-api", "iverson-admin-ui"):
        ing = get(docs, "Ingress", n)
        if ing:
            a = ing["metadata"].get("annotations") or {}
            print(f"  {n}[{ing['spec']['ingressClassName']}] " + " ".join(f"{k.split('/')[1]}={v}" for k, v in sorted(a.items()) if re.search(KEYS, k)))
for v in ("values-local", "values-laptop", "values-aws", "values-azure", "values-gcp"):
    show(f"{E}/t1/{v}.yaml", v)
show(f"{E}/t1-azure-root.yaml", "azure+root")
show(f"{E}/t1-aws-collide.yaml", "aws+profile backend-protocol HTTP")
base = get(load(f"{E}/base/values-local.yaml"), "Ingress", "iverson-api")["metadata"]["annotations"]
now = get(load(f"{E}/t1/values-local.yaml"), "Ingress", "iverson-api")["metadata"]["annotations"]
print("local api Ingress, other annotations unchanged:", {k: v for k, v in base.items() if "backend-protocol" not in k} == {k: v for k, v in now.items() if "backend-protocol" not in k})
PY
```

Expected (the loader refusing duplicate keys means every printed render has none):
- five render lines with `template=0`, `0 chart(s) failed` and `Invalid: 0, Errors: 0`;
- the object diffs:

```text
values-local: ~Deployment/iverson-api ~Ingress/iverson-admin-api ~Ingress/iverson-api +Secret/iverson-api-internal-tls
values-laptop: ~Deployment/iverson-api ~Ingress/iverson-api +Secret/iverson-api-internal-tls
values-aws: ~Deployment/iverson-api ~Ingress/iverson-admin-api ~Ingress/iverson-api +Secret/iverson-api-internal-tls
values-azure: ~Ingress/iverson-admin-api ~Ingress/iverson-admin-ui ~Ingress/iverson-api +Secret/iverson-api-internal-tls
values-gcp: ~Deployment/iverson-api ~Ingress/iverson-admin-api ~Ingress/iverson-admin-ui ~Ingress/iverson-api +Secret/iverson-api-internal-tls ~Service/iverson-api
```

- the three guard lines, then the azure line:

```text
Error: execution error at (iverson/charts/api/templates/secret-internal-tls.yaml:10:4): api.ingress.tlsSecretName must n
Error: execution error at (iverson/charts/api/templates/secret-internal-tls.yaml:13:4): api.adminApiIngress.tlsSecretNam
Error: execution error at (iverson/charts/api/templates/admin-api-ingress.yaml:44:4): api.adminApiIngress.className "tra
azure, root unset, admin-api traefik: renders
```

- the parsed objects:

```text
values-local: kestrel=6 urls=https://*:8080,https://*:8081 certs=['/etc/iverson/api-tls/tls.crt', '/etc/iverson/api-tls/tls.key'] mount=['/etc/iverson/api-tls'] vol=['iverson-api-internal-tls'] probes=HTTPS/HTTPS secret=['ca.crt', 'tls.crt', 'tls.key'] keep
  svc None
  iverson-api[nginx] backend-protocol=GRPCS
  iverson-admin-api[nginx] backend-protocol=HTTPS
  iverson-admin-ui[nginx] rewrite-target=/$2 ssl-redirect=false
values-laptop: kestrel=6 urls=https://*:8080,https://*:8081 certs=['/etc/iverson/api-tls/tls.crt', '/etc/iverson/api-tls/tls.key'] mount=['/etc/iverson/api-tls'] vol=['iverson-api-internal-tls'] probes=HTTPS/HTTPS secret=['ca.crt', 'tls.crt', 'tls.key'] keep
  svc None
  iverson-api[nginx] backend-protocol=GRPCS
values-aws: kestrel=6 urls=https://*:8080,https://*:8081 certs=['/etc/iverson/api-tls/tls.crt', '/etc/iverson/api-tls/tls.key'] mount=['/etc/iverson/api-tls'] vol=['iverson-api-internal-tls'] probes=HTTPS/HTTPS secret=['ca.crt', 'tls.crt', 'tls.key'] keep
  svc None
  iverson-api[alb] backend-protocol=HTTPS backend-protocol-version=GRPC healthcheck-protocol=HTTPS ssl-redirect=443
  iverson-admin-api[alb] backend-protocol=HTTPS healthcheck-path=/health healthcheck-protocol=HTTPS ssl-redirect=443
  iverson-admin-ui[alb] ssl-redirect=443 rewrite-target=/$2
values-azure: kestrel=0 urls=- certs=- mount=- vol=- probes=HTTP/HTTP secret=['ca.crt', 'tls.crt', 'tls.key'] keep
  svc None
  iverson-api[azure-application-gateway] ssl-redirect=true
  iverson-admin-api[azure-application-gateway] ssl-redirect=true
  iverson-admin-ui[azure-application-gateway] ssl-redirect=true rewrite-target=/$2
values-gcp: kestrel=6 urls=https://*:8080,https://*:8081 certs=['/etc/iverson/api-tls/tls.crt', '/etc/iverson/api-tls/tls.key'] mount=['/etc/iverson/api-tls'] vol=['iverson-api-internal-tls'] probes=HTTPS/HTTPS secret=['ca.crt', 'tls.crt', 'tls.key'] keep
  svc {'cloud.google.com/app-protocols': '{"grpc": "HTTP2", "http": "HTTPS"}'}
  iverson-api[gce] ingress.allow-http=false
  iverson-admin-api[gce] ingress.allow-http=false
  iverson-admin-ui[gce] ingress.allow-http=false rewrite-target=/$2
azure+root: kestrel=6 urls=https://*:8080,https://*:8081 certs=['/etc/iverson/api-tls/tls.crt', '/etc/iverson/api-tls/tls.key'] mount=['/etc/iverson/api-tls'] vol=['iverson-api-internal-tls'] probes=HTTPS/HTTPS secret=['ca.crt', 'tls.crt', 'tls.key'] keep
  svc None
  iverson-api[azure-application-gateway] appgw-trusted-root-certificate=iverson-api-ca backend-hostname=iverson-api backend-protocol=https health-probe-hostname=iverson-api health-probe-path=/health health-probe-port=8081 ssl-redirect=true
  iverson-admin-api[azure-application-gateway] appgw-trusted-root-certificate=iverson-api-ca backend-hostname=iverson-api backend-protocol=https health-probe-hostname=iverson-api health-probe-path=/health health-probe-port=8081 ssl-redirect=true
  iverson-admin-ui[azure-application-gateway] ssl-redirect=true rewrite-target=/$2
aws+profile backend-protocol HTTP: kestrel=6 urls=https://*:8080,https://*:8081 certs=['/etc/iverson/api-tls/tls.crt', '/etc/iverson/api-tls/tls.key'] mount=['/etc/iverson/api-tls'] vol=['iverson-api-internal-tls'] probes=HTTPS/HTTPS secret=['ca.crt', 'tls.crt', 'tls.key'] keep
  svc None
  iverson-api[alb] backend-protocol=HTTPS backend-protocol-version=GRPC healthcheck-protocol=HTTPS ssl-redirect=443
  iverson-admin-api[alb] backend-protocol=HTTPS healthcheck-path=/health healthcheck-protocol=HTTPS ssl-redirect=443
  iverson-admin-ui[alb] ssl-redirect=443 rewrite-target=/$2
local api Ingress, other annotations unchanged: True
```

The aws profile already carries `backend-protocol-version: GRPC` and `ssl-redirect: "443"`. They merge with the chart's keys into one map. A profile value for a key the chart sets (here `backend-protocol: HTTP`) is replaced by the chart's value, not duplicated.

- [ ] **Step 11: Kestrel serves the rendered certificate with the rendered env.** This step writes a scratch app (the API's own `Kestrel` section, `/health` and a one-line `/metrics`) and `$SCR/edge/kestrel.sh`, which Task 2 reuses. It then runs the app as uid 1000 on a read-only root filesystem, as the Deployment does. The certificate comes from the rendered Secret and the env from the rendered Deployment.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh; K=$SCR/edge/kestrel; mkdir -p $K/app
cat > $K/app/kp.csproj <<'XML'
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
XML
cat > $K/app/Program.cs <<'CS'
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.UseHttpsRedirection();
app.MapGet("/health", (HttpContext c) => $"ok proto={c.Request.Protocol} https={c.Request.IsHttps} port={c.Connection.LocalPort}");
app.MapGet("/metrics", () => "# TYPE kp_up gauge\nkp_up 1\n");
app.Run();
CS
python3 -c "import json; s = json.load(open('$WT/Iverson.Server/Iverson.Api/appsettings.json')); json.dump({'Kestrel': s['Kestrel']}, open('$K/app/appsettings.json', 'w'))"
cd $K/app && systemd-run --user --scope -q -p MemoryMax=3G dotnet publish -c Release -o $K/out -p:UseSharedCompilation=false 2>&1 | tail -1; dotnet build-server shutdown >/dev/null 2>&1
cat > $SCR/edge/kestrel.sh <<'SH'
#!/usr/bin/env bash
# kestrel.sh up <rendered profile.yaml> | down. "up" writes the rendered iverson-api-internal-tls
# files and Kestrel__* env to $SCR/edge/kestrel/, then starts the scratch app on the podman network
# csr11c-edge-net under the names the chart's clients use (iverson-api, and the headless Service's
# DNS name for Prometheus's dns_sd).
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh; K=$SCR/edge/kestrel
if [ "$1" = down ]; then docker rm -f csr11c-edge-kestrel >/dev/null 2>&1; docker network rm csr11c-edge-net >/dev/null 2>&1; exit 0; fi
mkdir -p $K/tls
python3 - "$2" "$K" <<'PY'
import sys, yaml, base64
docs = [d for d in yaml.safe_load_all(open(sys.argv[1])) if d]
sec = next(d for d in docs if d["kind"] == "Secret" and d["metadata"]["name"] == "iverson-api-internal-tls")
for k in ("tls.crt", "tls.key", "ca.crt"): open(f"{sys.argv[2]}/tls/{k}", "wb").write(base64.b64decode(sec["data"][k]))
c = next(d for d in docs if d["kind"] == "Deployment" and d["metadata"]["name"] == "iverson-api")["spec"]["template"]["spec"]["containers"][0]
open(f"{sys.argv[2]}/kestrel.env", "w").write("".join(f'{e["name"]}={e["value"]}\n' for e in c["env"] if e["name"].startswith("Kestrel__")))
PY
chmod 644 $K/tls/*
docker network create csr11c-edge-net >/dev/null
docker run -d --name csr11c-edge-kestrel --memory 256m --network csr11c-edge-net \
  --network-alias iverson-api --network-alias iverson-api-headless.iverson.svc.cluster.local \
  --read-only --user 1000:1000 --tmpfs /tmp --env-file $K/kestrel.env \
  -v $K/out:/app:ro -v $K/tls:/etc/iverson/api-tls:ro -w /app mcr.microsoft.com/dotnet/aspnet:10.0 dotnet kp.dll >/dev/null
sleep 4
SH
chmod +x $SCR/edge/kestrel.sh
$SCR/edge/kestrel.sh up $SCR/edge/t1/values-local.yaml
cat $K/kestrel.env
openssl x509 -in $K/tls/tls.crt -noout -subject -ext subjectAltName | tr -s ' \n' ' '; echo; openssl verify -CAfile $K/tls/ca.crt $K/tls/tls.crt
docker logs csr11c-edge-kestrel 2>&1 | command grep 'Now listening'
C="docker run --rm --network csr11c-edge-net -v $K/tls/ca.crt:/ca.crt:ro docker.io/curlimages/curl:8.18.0"
$C -sS --cacert /ca.crt https://iverson-api:8081/health -w ' [%{http_version}]\n'
$C -sS --http2 --cacert /ca.crt https://iverson-api:8080/health -w ' [%{http_version}]\n'
$C -s http://iverson-api:8081/health -o /dev/null -w 'plain http 8081: %{http_code}\n'
$C -s --http2-prior-knowledge http://iverson-api:8080/health -o /dev/null -w 'h2c 8080: %{http_code}\n'
$SCR/edge/kestrel.sh down; echo "leftover containers: $(docker ps -a --format '{{.Names}}' | command grep -c csr11c-edge)"
```

Expected:

```text
  kp -> /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/edge/kestrel/out/
Kestrel__Endpoints__Grpc__Url=https://*:8080
Kestrel__Endpoints__Grpc__Certificate__Path=/etc/iverson/api-tls/tls.crt
Kestrel__Endpoints__Grpc__Certificate__KeyPath=/etc/iverson/api-tls/tls.key
Kestrel__Endpoints__Http__Url=https://*:8081
Kestrel__Endpoints__Http__Certificate__Path=/etc/iverson/api-tls/tls.crt
Kestrel__Endpoints__Http__Certificate__KeyPath=/etc/iverson/api-tls/tls.key
subject=CN=iverson-api X509v3 Subject Alternative Name: DNS:iverson-api, DNS:iverson-api.iverson.svc, DNS:iverson-api.iverson.svc.cluster.local 
/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/edge/kestrel/tls/tls.crt: OK
      Now listening on: https://[::]:8080
      Now listening on: https://[::]:8081
ok proto=HTTP/1.1 https=True port=8081 [1.1]
ok proto=HTTP/2 https=True port=8080 [2]
plain http 8081: 000
h2c 8080: 000
leftover containers: 0
```

Both ports answer TLS, verified against the chart CA under the Service name. Plaintext gets no HTTP response.

- [ ] **Step 12: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh; cd $WT
git add Iverson.Server/deploy/helm/iverson/templates/_helpers.tpl Iverson.Server/deploy/helm/iverson/charts/api/templates/secret-internal-tls.yaml Iverson.Server/deploy/helm/iverson/charts/api/values.yaml Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml Iverson.Server/deploy/helm/iverson/charts/api/templates/ingress.yaml Iverson.Server/deploy/helm/iverson/charts/api/templates/admin-api-ingress.yaml Iverson.Server/deploy/helm/iverson/charts/api/templates/service.yaml Iverson.Server/deploy/helm/iverson/charts/admin-ui/templates/ingress.yaml
git commit -m "serve the api over tls to the edge and close port 80 on gce and azure

$TRAILER"
```

### Task 2: Prometheus scrape follows the API switch

**Files:**
- Create: `Iverson.Server/deploy/helm/iverson/templates/prometheus-config.yaml`
- Delete: `Iverson.Server/deploy/helm/iverson/charts/prometheus/templates/configmap.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/prometheus/templates/deployment.yaml`
- Modify (comments only): `Iverson.Server/deploy/helm/iverson/charts/api/templates/headless-service.yaml`, `charts/worker/templates/headless-service.yaml`

**Interfaces:**
- Consumes `iverson.backendTls`, the value `api.ingress.appgwTrustedRootCertificate` and the Secret `<release>-api-internal-tls` (Task 1).
- Consumes `$SCR/edge/{render.sh,nodup.py,objdiff.py,kestrel.sh}`, `$SCR/edge/base/` and `$SCR/edge/t1/` (Task 1).

The prometheus Deployment mounts the CA on every profile, because the subchart cannot see the api class. That works because the Secret renders on every profile where `api.enabled` is true, and all five profiles set it. On a profile where the switch is off, the file is mounted but unused.

- [ ] **Step 1: The parent renders the scrape config.** Create `templates/prometheus-config.yaml` with exactly:

```yaml
{{- if .Values.global.prometheusEnabled }}
{{- /* CSR round-11 #4: moved here from charts/prometheus/templates/configmap.yaml, because only the
   parent sees the api subchart's ingress class, which decides whether the API serves TLS
   (iverson.backendTls). Same name: charts/prometheus/templates/deployment.yaml mounts it. */}}
{{- $apiTls := eq (include "iverson.backendTls" (dict "className" .Values.api.ingress.className "trustedRoot" .Values.api.ingress.appgwTrustedRootCertificate)) "true" }}
apiVersion: v1
kind: ConfigMap
metadata:
  name: {{ .Release.Name }}-prometheus-config
data:
  prometheus.yml: |
    global:
      scrape_interval: 30s
    # dns_sd_configs, not kubernetes_sd_configs: the prometheus chart's ServiceAccount and
    # pod spec both set automountServiceAccountToken: false and there is no RBAC object
    # granting it list/watch on pods/endpoints, so an API-server-based service discovery
    # mechanism has nothing to authenticate with. DNS discovery needs none of that — it
    # just resolves the headless Services' A records (charts/api and charts/worker's
    # headless-service.yaml, Task 8), which returns one address per ready pod instead of
    # the ClusterIP VIP's single load-balanced address, fixing the non-monotonic
    # counters/flickering gauges that come from scraping across 2-5 HPA replicas as if
    # they were one target.
    scrape_configs:
      - job_name: iverson-api
        {{- if $apiTls }}
        # CSR round-11 #4: the API serves TLS. Targets are pod IPs, which the certificate does
        # not name, so the name checked is the Service's. The CA is a Secret volume without
        # subPath, so a rotated CA reaches the running Prometheus.
        scheme: https
        tls_config:
          ca_file: /etc/iverson/api-ca/ca.crt
          server_name: {{ .Release.Name }}-api
        {{- end }}
        dns_sd_configs:
          - names: ["{{ .Release.Name }}-api-headless.{{ .Release.Namespace }}.svc.cluster.local"]
            type: A
            port: 8081
      - job_name: iverson-worker
        dns_sd_configs:
          - names: ["{{ .Release.Name }}-worker-headless.{{ .Release.Namespace }}.svc.cluster.local"]
            type: A
            port: 8081
{{- end }}
```

The `data` block is the subchart file's, byte for byte, apart from the four-line `{{- if $apiTls }}` insert.

- [ ] **Step 2: Remove the subchart's copy.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh; cd $WT
git rm -q Iverson.Server/deploy/helm/iverson/charts/prometheus/templates/configmap.yaml
```

- [ ] **Step 3: Mount the API CA in Prometheus.** In `charts/prometheus/templates/deployment.yaml`, replace:

```yaml
            - name: storage
              mountPath: /prometheus
```

with:

```yaml
            - name: storage
              mountPath: /prometheus
            # CSR round-11 #4: the CA the iverson-api scrape job verifies the API with, once the API
            # serves TLS (the parent's templates/prometheus-config.yaml). Not a subPath mount, so a
            # rotated CA reaches the running Prometheus, which rereads ca_file.
            - name: api-ca
              mountPath: /etc/iverson/api-ca
              readOnly: true
```

and replace:

```yaml
        - name: storage
          persistentVolumeClaim:
            claimName: {{ .Release.Name }}-prometheus
```

with:

```yaml
        - name: storage
          persistentVolumeClaim:
            claimName: {{ .Release.Name }}-prometheus
        - name: api-ca
          secret:
            secretName: {{ .Release.Name }}-api-internal-tls
            items:
              - key: ca.crt
                path: ca.crt
```

The Secret volume keeps the default mode (0644), so Prometheus's uid 65534 can read the file whatever `fsGroup` does.

- [ ] **Step 4: Point the headless Services' comments at the new file.** In `charts/api/templates/headless-service.yaml`, replace:

```yaml
# charts/prometheus/templates/configmap.yaml) can resolve one A record per pod IP instead
```

with:

```yaml
# the parent chart's templates/prometheus-config.yaml) can resolve one A record per pod IP instead
```

In `charts/worker/templates/headless-service.yaml`, replace:

```yaml
# charts/prometheus/templates/configmap.yaml) needs one A record per pod IP so its
```

with:

```yaml
# the parent chart's templates/prometheus-config.yaml) needs one A record per pod IP so its
```

- [ ] **Step 5: Render; the config is unchanged byte for byte when the switch is off.** The CA file is mounted without `subPath`. The off case is compared twice: the azure profile (root unset), and local forced off through an unknown class.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
$SCR/edge/render.sh $SCR/edge/t2
for v in values-local values-laptop values-aws values-azure values-gcp; do echo "$v: $(python3 $SCR/edge/objdiff.py $SCR/edge/t1/$v.yaml $SCR/edge/t2/$v.yaml)"; done
cd $CH
helm template iverson . -n iverson -f values-local.yaml --set api.ingress.className=traefik --show-only templates/prometheus-config.yaml > $SCR/edge/t2-local-off.yaml
body() { awk -v src="# Source: $2" '$0 == src {f = 1; next} /^---/ {f = 0} f' $1; }
cmp <(body $SCR/edge/base/values-azure.yaml iverson/charts/prometheus/templates/configmap.yaml) <(body $SCR/edge/t2/values-azure.yaml iverson/templates/prometheus-config.yaml) && echo "azure (root unset): ConfigMap bytes identical to base"
cmp <(body $SCR/edge/base/values-local.yaml iverson/charts/prometheus/templates/configmap.yaml) <(body $SCR/edge/t2-local-off.yaml iverson/templates/prometheus-config.yaml) && echo "local, switch forced off: ConfigMap bytes identical to base"
echo "compared body: $(body $SCR/edge/base/values-azure.yaml iverson/charts/prometheus/templates/configmap.yaml | wc -l) lines"
python3 - $SCR <<'PY'
import sys, yaml
sys.path.insert(0, sys.argv[1] + "/edge"); from nodup import load, get
E = sys.argv[1] + "/edge"
for v in ("values-local", "values-laptop", "values-aws", "values-azure", "values-gcp"):
    docs = load(f"{E}/t2/{v}.yaml")
    cm = get(docs, "ConfigMap", "iverson-prometheus-config")
    if not cm: print(f"{v}: no prometheus"); continue
    job = yaml.safe_load(cm["data"]["prometheus.yml"])["scrape_configs"][0]
    pod = get(docs, "Deployment", "iverson-prometheus")["spec"]["template"]["spec"]
    m = [x for x in pod["containers"][0]["volumeMounts"] if x["name"] == "api-ca"]
    vol = [x["secret"] for x in pod["volumes"] if x["name"] == "api-ca"]
    print(f"{v}: {job['job_name']} scheme={job.get('scheme', 'http')} tls={job.get('tls_config')} mount={m} vol={vol}")
PY
```

Expected:

```text
values-local: ~ConfigMap/iverson-prometheus-config ~Deployment/iverson-prometheus
values-laptop: none
values-aws: ~ConfigMap/iverson-prometheus-config ~Deployment/iverson-prometheus
values-azure: ~Deployment/iverson-prometheus
values-gcp: ~ConfigMap/iverson-prometheus-config ~Deployment/iverson-prometheus
azure (root unset): ConfigMap bytes identical to base
local, switch forced off: ConfigMap bytes identical to base
compared body: 28 lines
values-local: iverson-api scheme=https tls={'ca_file': '/etc/iverson/api-ca/ca.crt', 'server_name': 'iverson-api'} mount=[{'name': 'api-ca', 'mountPath': '/etc/iverson/api-ca', 'readOnly': True}] vol=[{'secretName': 'iverson-api-internal-tls', 'items': [{'key': 'ca.crt', 'path': 'ca.crt'}]}]
values-laptop: no prometheus
values-aws: iverson-api scheme=https tls={'ca_file': '/etc/iverson/api-ca/ca.crt', 'server_name': 'iverson-api'} mount=[{'name': 'api-ca', 'mountPath': '/etc/iverson/api-ca', 'readOnly': True}] vol=[{'secretName': 'iverson-api-internal-tls', 'items': [{'key': 'ca.crt', 'path': 'ca.crt'}]}]
values-azure: iverson-api scheme=http tls=None mount=[{'name': 'api-ca', 'mountPath': '/etc/iverson/api-ca', 'readOnly': True}] vol=[{'secretName': 'iverson-api-internal-tls', 'items': [{'key': 'ca.crt', 'path': 'ca.crt'}]}]
values-gcp: iverson-api scheme=https tls={'ca_file': '/etc/iverson/api-ca/ca.crt', 'server_name': 'iverson-api'} mount=[{'name': 'api-ca', 'mountPath': '/etc/iverson/api-ca', 'readOnly': True}] vol=[{'secretName': 'iverson-api-internal-tls', 'items': [{'key': 'ca.crt', 'path': 'ca.crt'}]}]
```

The render lines show `template=0`, `0 chart(s) failed` and `Invalid: 0, Errors: 0` for all five profiles. Laptop has `prometheusEnabled: false` and renders neither object.

- [ ] **Step 6: The rendered job scrapes a TLS Kestrel.** This step runs `promtool check config`, then a throwaway Prometheus as uid 65534 against Task 1's scratch Kestrel, both from the same local render. The negative control drops `server_name`, which proves the name is load-bearing: the targets are pod IPs. `promtool` does not check that `ca_file` exists; the live run does.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh; P=$SCR/edge/prom; mkdir -p $P/conf $P/ca
$SCR/edge/kestrel.sh up $SCR/edge/t2/values-local.yaml
python3 -c "import yaml; cm = next(d for d in yaml.safe_load_all(open('$SCR/edge/t2/values-local.yaml')) if d and d['kind'] == 'ConfigMap' and d['metadata']['name'] == 'iverson-prometheus-config'); open('$P/conf/prometheus.yml', 'w').write(cm['data']['prometheus.yml'])"
cp $SCR/edge/kestrel/tls/ca.crt $P/ca/ca.crt; chmod 644 $P/ca/ca.crt $P/conf/prometheus.yml
docker run --rm --user 65534:65534 --entrypoint promtool -v $P/conf:/etc/prometheus:ro -v $P/ca:/etc/iverson/api-ca:ro docker.io/prom/prometheus:v2.55.1 check config /etc/prometheus/prometheus.yml 2>&1 | command grep -E 'SUCCESS|FAILED'
docker run -d --name csr11c-edge-prom --memory 256m --network csr11c-edge-net --user 65534:65534 --read-only --tmpfs /prometheus:mode=1777 \
  -p 127.0.0.1:19090:9090 -v $P/conf:/etc/prometheus:ro -v $P/ca:/etc/iverson/api-ca:ro \
  docker.io/prom/prometheus:v2.55.1 --config.file=/etc/prometheus/prometheus.yml --storage.tsdb.path=/prometheus >/dev/null
api_target() { curl -s 127.0.0.1:19090/api/v1/targets | python3 -c "import json, sys; t = [t for t in json.load(sys.stdin)['data']['activeTargets'] if t['labels']['job'] == 'iverson-api'][0]; print(t['scrapeUrl'].split('//')[0], t['health'], t['lastError'][:120])"; }
for i in $(seq 1 30); do s=$(api_target 2>/dev/null); case "$s" in *" up "*|*" up"|*down*) break;; esac; sleep 3; done; echo "rendered job: $s"
sed -i '/server_name/d' $P/conf/prometheus.yml; docker kill -s HUP csr11c-edge-prom >/dev/null
for i in $(seq 1 30); do s=$(api_target 2>/dev/null); case "$s" in *down*) break;; esac; sleep 3; done; echo "without server_name: $s"
docker rm -f csr11c-edge-prom >/dev/null; $SCR/edge/kestrel.sh down; echo "leftover containers: $(docker ps -a --format '{{.Names}}' | command grep -c csr11c-edge)"
```

Expected:

```text
 SUCCESS: /etc/prometheus/prometheus.yml is valid prometheus config file syntax
rendered job: https: up
without server_name: https: down Get "https://10.89.X.Y:8081/metrics": tls: failed to verify certificate: x509: cannot validate certificate for 10.89
leftover containers: 0
```

The address in the last line varies with the network.

- [ ] **Step 7: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh; cd $WT
git add Iverson.Server/deploy/helm/iverson/templates/prometheus-config.yaml Iverson.Server/deploy/helm/iverson/charts/prometheus/templates/deployment.yaml Iverson.Server/deploy/helm/iverson/charts/api/templates/headless-service.yaml Iverson.Server/deploy/helm/iverson/charts/worker/templates/headless-service.yaml
git commit -m "render the prometheus scrape config in the parent so the api job follows backend tls

$TRAILER"
```

(The `git rm` in Step 2 already staged the deletion.)

### Task 3: Authentik edge

**Files:**
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/values.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/configmap-tls-proxy.yaml`, `deployment-server.yaml`, `ingress.yaml`, `service.yaml` and `backendconfig.yaml`

**Interfaces:**
- Consumes `iverson.backendTls` and `iverson.edgeAnnotations` (Task 1), and `$SCR/edge/{render.sh,nodup.py,objdiff.py}` and `$SCR/edge/t2/`.
- Produces the value `authentik.ingress.appgwTrustedRootCertificate` (default `""`), which the Azure guard task reads.

The public listener reuses `<release>-authentik-internal-tls`, which the tls-proxy already mounts at `/etc/iverson/tls`. Its CN `<release>-authentik` is the Application Gateway `backend-hostname`.

The BackendConfig renders only when `authentik.ingress.securityPolicy` is set. On the `gce` class the Authentik Ingress already refuses an empty `securityPolicy`, so on GCE the BackendConfig, and the health check this task adds to it, always exist.

- [ ] **Step 1: The trusted-root value.** In `charts/authentik/values.yaml`, replace:

```yaml
  wafAclArn: ""
  securityPolicy: ""
  wafPolicyId: ""
```

with:

```yaml
  wafAclArn: ""
  securityPolicy: ""
  wafPolicyId: ""
  # CSR round-11 #4: on className azure-application-gateway, the name of the Application Gateway
  # trusted root holding this release's <release>-authentik-internal-tls ca.crt. Empty keeps the
  # gateway → Authentik 9080 hop plaintext; set, the tls-proxy serves TLS on 9080. Other classes
  # ignore it (iverson.backendTls in the parent's templates/_helpers.tpl).
  appgwTrustedRootCertificate: ""
```

- [ ] **Step 2: TLS and a constant Host on 9080.** Two edits in `templates/configmap-tls-proxy.yaml`.
  1. Replace the first four lines:

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: {{ .Release.Name }}-authentik-tls-proxy
```

with:

```yaml
{{- $backendTls := eq (include "iverson.backendTls" (dict "className" .Values.ingress.className "trustedRoot" .Values.ingress.appgwTrustedRootCertificate)) "true" }}
apiVersion: v1
kind: ConfigMap
metadata:
  name: {{ .Release.Name }}-authentik-tls-proxy
```

  2. Replace:

```yaml
    # callers. Everything else, the admin interface and admin API included, answers 404 here.
    server {
      listen 9080;
      proxy_set_header Host $http_host;
      proxy_set_header X-Forwarded-Host $http_host;
```

with:

```yaml
    # callers. Everything else, the admin interface and admin API included, answers 404 here.
    # CSR round-11 #4: TLS from the edge when iverson.backendTls is on for this Ingress class. Host is
    # the one name the Ingress routes here, not the request's: Application Gateway replaces Host with
    # its backend host name, and Authentik builds its issuer and URLs from Host.
    server {
{{- if $backendTls }}
      listen 9080 ssl;
      ssl_certificate     /etc/iverson/tls/tls.crt;
      ssl_certificate_key /etc/iverson/tls/tls.key;
      ssl_protocols       TLSv1.2 TLSv1.3;
{{- else }}
      listen 9080;
{{- end }}
      proxy_set_header Host authentik.{{ .Values.global.ingressHost }};
      proxy_set_header X-Forwarded-Host authentik.{{ .Values.global.ingressHost }};
```

The Host constant applies whatever the switch, per spec §2. On every class but AGIC the Ingress routes only that host to 9080, so the value is unchanged there.

- [ ] **Step 3: The tls-proxy readiness probe.** Two edits in `templates/deployment-server.yaml`.
  1. Replace the first four lines:

```yaml
apiVersion: v1
kind: ServiceAccount
metadata:
  name: {{ .Release.Name }}-authentik
```

with:

```yaml
{{- $backendTls := eq (include "iverson.backendTls" (dict "className" .Values.ingress.className "trustedRoot" .Values.ingress.appgwTrustedRootCertificate)) "true" }}
apiVersion: v1
kind: ServiceAccount
metadata:
  name: {{ .Release.Name }}-authentik
```

  2. Replace:

```yaml
          # An HTTP probe on the public listener (CSR round-10 #4): GCE and AGIC derive the Ingress
          # backend's health check from the readiness probe on the port the container declares, and
          # 9080 needs no NetworkPolicy exception beyond its own rule. Both listeners share one nginx
          # process and config, so it vouches for 8443 too. Authentik's cheap /-/health/live/ path with
          # the server container's timeout and threshold keeps a slow Django response from taking the
          # only Authentik pod out of its Service.
          readinessProbe:
            httpGet: { path: /-/health/live/, port: 9080 }
```

with:

```yaml
          # A probe on the public listener (CSR round-10 #4): GCE and AGIC derive the Ingress
          # backend's health check from the readiness probe on the port the container declares, and
          # 9080 needs no NetworkPolicy exception beyond its own rule. Both listeners share one nginx
          # process and config, so it vouches for 8443 too. Authentik's cheap /-/health/live/ path with
          # the server container's timeout and threshold keeps a slow Django response from taking the
          # only Authentik pod out of its Service. HTTPS when 9080 serves TLS (CSR round-11 #4): an
          # nginx ssl listener answers plain HTTP with 400.
          readinessProbe:
            httpGet: { path: /-/health/live/, port: 9080{{ if $backendTls }}, scheme: HTTPS{{ end }} }
```

The probe change also changes the pod template, so a switch flip (including Azure's phase 3) restarts the server pod. That restart is what makes nginx load the new `listen 9080 ssl`, because the Deployment carries no ConfigMap checksum.

- [ ] **Step 4: The Authentik Ingress.** In `templates/ingress.yaml`, replace:

```yaml
{{- with .Values.ingress.wafPolicyId }}
{{- $_ := set $annotations "appgw.ingress.kubernetes.io/waf-policy-for-path" . }}
{{- end }}
```

with:

```yaml
{{- with .Values.ingress.wafPolicyId }}
{{- $_ := set $annotations "appgw.ingress.kubernetes.io/waf-policy-for-path" . }}
{{- end }}
{{- /* CSR round-11 #4, #6: the class's edge → tls-proxy 9080 protocol and port-80 settings
   (iverson.edgeAnnotations, parent templates/_helpers.tpl). */}}
{{- $backendTls := eq (include "iverson.backendTls" (dict "className" .Values.ingress.className "trustedRoot" .Values.ingress.appgwTrustedRootCertificate)) "true" }}
{{- $_ := mergeOverwrite $annotations (include "iverson.edgeAnnotations" (dict "className" .Values.ingress.className "backendTls" $backendTls "grpc" false "trustedRoot" .Values.ingress.appgwTrustedRootCertificate "host" (printf "%s-authentik" .Release.Name) "probePort" 9080 "probePath" "/-/health/live/") | fromYaml) }}
```

- [ ] **Step 5: The Authentik Service (GCE).** In `templates/service.yaml`, replace:

```yaml
  name: {{ .Release.Name }}-authentik
  {{- /* CSR round-11 #5: GCE attaches backendconfig.yaml (the Cloud Armor policy) to the public
  listener's backend. */}}
  {{- with .Values.ingress.securityPolicy }}
  annotations:
    cloud.google.com/backend-config: '{"ports": {"9080": "{{ $.Release.Name }}-authentik"}}'
  {{- end }}
spec:
```

with:

```yaml
  name: {{ .Release.Name }}-authentik
  {{- /* CSR round-11 #5: GCE attaches backendconfig.yaml (the Cloud Armor policy and health check)
  to the public listener's backend. CSR round-11 #4: GCE speaks HTTPS to the public listener when
  it serves TLS (iverson.backendTls). */}}
  {{- $gceTls := and (eq .Values.ingress.className "gce") (eq (include "iverson.backendTls" (dict "className" .Values.ingress.className "trustedRoot" .Values.ingress.appgwTrustedRootCertificate)) "true") }}
  {{- if or .Values.ingress.securityPolicy $gceTls }}
  annotations:
    {{- with .Values.ingress.securityPolicy }}
    cloud.google.com/backend-config: '{"ports": {"9080": "{{ $.Release.Name }}-authentik"}}'
    {{- end }}
    {{- if $gceTls }}
    cloud.google.com/app-protocols: '{"http-public": "HTTPS"}'
    {{- end }}
  {{- end }}
spec:
```

- [ ] **Step 6: The GCE health check.** In `templates/backendconfig.yaml`, replace:

```yaml
spec:
  securityPolicy:
    name: {{ . | quote }}
{{- end }}
```

with:

```yaml
spec:
  securityPolicy:
    name: {{ . | quote }}
  {{- if and (eq $.Values.ingress.className "gce") (eq (include "iverson.backendTls" (dict "className" $.Values.ingress.className "trustedRoot" $.Values.ingress.appgwTrustedRootCertificate)) "true") }}
  # CSR round-11 #4: the public listener serves TLS; Authentik's / redirects, so the check uses the
  # liveness path the tls-proxy forwards.
  healthCheck:
    type: HTTPS
    port: 9080
    requestPath: /-/health/live/
  {{- end }}
{{- end }}
```

- [ ] **Step 7: Render and parse.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
$SCR/edge/render.sh $SCR/edge/t3
for v in values-local values-laptop values-aws values-azure values-gcp; do echo "$v: $(python3 $SCR/edge/objdiff.py $SCR/edge/t2/$v.yaml $SCR/edge/t3/$v.yaml)"; done
cd $CH
helm template iverson . -n iverson -f values-azure.yaml -f values-azure.ci-override.yaml --set authentik.ingress.appgwTrustedRootCertificate=iverson-authentik-ca > $SCR/edge/t3-azure-root.yaml
python3 - $SCR <<'PY'
import sys, re
sys.path.insert(0, sys.argv[1] + "/edge"); from nodup import load, get
E = sys.argv[1] + "/edge"
KEYS = r"backend-protocol|healthcheck|health-probe|backend-hostname|trusted-root|allow-http|ssl-redirect"
def show(path, label):
    docs = load(path)
    conf = get(docs, "ConfigMap", "iverson-authentik-tls-proxy")["data"]["default.conf"]
    pub = conf[conf.index("listen 9080"):]
    print(f"{label}: {re.findall(r'listen 9080[^;]*;', conf)} ssl_lines={len(re.findall(r'ssl_(?:certificate|certificate_key|protocols) ', pub))} "
          + " ".join(re.findall(r"proxy_set_header ((?:Host|X-Forwarded-Host) [^;]+);", pub)))
    tp = next(c for c in get(docs, "Deployment", "iverson-authentik-server")["spec"]["template"]["spec"]["containers"] if c["name"] == "tls-proxy")
    a = get(docs, "Ingress", "iverson-authentik")["metadata"]["annotations"]
    print(f"  probe={tp['readinessProbe']['httpGet'].get('scheme', 'HTTP')} ingress: " + " ".join(f"{k.split('/')[1]}={v}" for k, v in sorted(a.items()) if re.search(KEYS, k)))
    print(f"  svc={get(docs, 'Service', 'iverson-authentik')['metadata'].get('annotations')} backendconfig={[d['spec'] for d in docs if d['kind'] == 'BackendConfig']}")
for v in ("values-local", "values-laptop", "values-aws", "values-azure", "values-gcp"):
    show(f"{E}/t3/{v}.yaml", v)
show(f"{E}/t3-azure-root.yaml", "azure+root")
PY
```

Expected:

```text
values-local: ~ConfigMap/iverson-authentik-tls-proxy ~Deployment/iverson-authentik-server ~Ingress/iverson-authentik
values-laptop: ~ConfigMap/iverson-authentik-tls-proxy ~Deployment/iverson-authentik-server ~Ingress/iverson-authentik
values-aws: ~ConfigMap/iverson-authentik-tls-proxy ~Deployment/iverson-authentik-server ~Ingress/iverson-authentik
values-azure: ~ConfigMap/iverson-authentik-tls-proxy ~Ingress/iverson-authentik
values-gcp: ~BackendConfig/iverson-authentik ~ConfigMap/iverson-authentik-tls-proxy ~Deployment/iverson-authentik-server ~Ingress/iverson-authentik ~Service/iverson-authentik
values-local: ['listen 9080 ssl;'] ssl_lines=3 Host authentik.iverson.local X-Forwarded-Host authentik.iverson.local
  probe=HTTPS ingress: backend-protocol=HTTPS
  svc=None backendconfig=[]
values-laptop: ['listen 9080 ssl;'] ssl_lines=3 Host authentik.iverson.local X-Forwarded-Host authentik.iverson.local
  probe=HTTPS ingress: backend-protocol=HTTPS
  svc=None backendconfig=[]
values-aws: ['listen 9080 ssl;'] ssl_lines=3 Host authentik.iverson-ci-test.example.org X-Forwarded-Host authentik.iverson-ci-test.example.org
  probe=HTTPS ingress: backend-protocol=HTTPS healthcheck-path=/-/health/live/ healthcheck-protocol=HTTPS ssl-redirect=443
  svc=None backendconfig=[]
values-azure: ['listen 9080;'] ssl_lines=0 Host authentik.iverson-ci-test.example.org X-Forwarded-Host authentik.iverson-ci-test.example.org
  probe=HTTP ingress: ssl-redirect=true
  svc=None backendconfig=[]
values-gcp: ['listen 9080 ssl;'] ssl_lines=3 Host authentik.iverson-ci-test.example.org X-Forwarded-Host authentik.iverson-ci-test.example.org
  probe=HTTPS ingress: ingress.allow-http=false
  svc={'cloud.google.com/backend-config': '{"ports": {"9080": "iverson-authentik"}}', 'cloud.google.com/app-protocols': '{"http-public": "HTTPS"}'} backendconfig=[{'securityPolicy': {'name': 'iverson-authentik-login'}, 'healthCheck': {'type': 'HTTPS', 'port': 9080, 'requestPath': '/-/health/live/'}}]
azure+root: ['listen 9080 ssl;'] ssl_lines=3 Host authentik.iverson-ci-test.example.org X-Forwarded-Host authentik.iverson-ci-test.example.org
  probe=HTTPS ingress: appgw-trusted-root-certificate=iverson-authentik-ca backend-hostname=iverson-authentik backend-protocol=https health-probe-hostname=iverson-authentik health-probe-path=/-/health/live/ health-probe-port=9080 ssl-redirect=true
  svc=None backendconfig=[]
```

The render lines show `template=0`, `0 chart(s) failed` and `Invalid: 0, Errors: 0` for all five profiles. Azure's tls-proxy ConfigMap changes with the root unset, because of the Host constant. Its server Deployment does not change.

- [ ] **Step 8: The rendered tls-proxy config under nginx 1.30.5.** This step runs `nginx -t` on the laptop render (ssl branch) and the azure render (plain branch). Then, with the laptop config and the rendered `<release>-authentik-internal-tls` certificate, a loopback echo server stands in for Authentik on 9000 and reports the headers it received. The requests are sent with a hostile `Host` and `X-Forwarded-Host`.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh; N=$SCR/edge/nginx; mkdir -p $N; cd $N
IMG=docker.io/nginxinc/nginx-unprivileged:1.30.5-alpine
docker run --rm --entrypoint nginx $IMG -v 2>&1
python3 - $SCR/edge <<'PY'
import sys, yaml, base64
E = sys.argv[1]
for v in ("values-laptop", "values-azure"):
    docs = [d for d in yaml.safe_load_all(open(f"{E}/t3/{v}.yaml")) if d]
    cm = next(d for d in docs if d["kind"] == "ConfigMap" and d["metadata"]["name"] == "iverson-authentik-tls-proxy")
    open(f"{E}/nginx/{v}.conf", "w").write(cm["data"]["default.conf"])
    if v == "values-laptop":
        sec = next(d for d in docs if d["kind"] == "Secret" and d["metadata"]["name"] == "iverson-authentik-internal-tls")
        for k in ("tls.crt", "tls.key"): open(f"{E}/nginx/{k}", "wb").write(base64.b64decode(sec["data"][k]))
PY
cat > echo.conf <<'NGX'
server { listen 127.0.0.1:9000; location / { add_header X-Got-Host "[$http_host]" always; add_header X-Got-XFH "[$http_x_forwarded_host]" always; add_header X-Got-Public "[$http_x_iverson_public_ingress]" always; return 204; } }
NGX
chmod 644 *.conf tls.*
mounts() { echo "-v $N/$1.conf:/etc/nginx/conf.d/default.conf:ro -v $N/echo.conf:/etc/nginx/conf.d/zz-echo.conf:ro -v $N/tls.crt:/etc/iverson/tls/tls.crt:ro -v $N/tls.key:/etc/iverson/tls/tls.key:ro"; }
for v in values-laptop values-azure; do echo "$v: $(docker run --rm --memory 64m $(mounts $v) --entrypoint nginx $IMG -t 2>&1 | tail -1)"; done
docker run -d --name csr11c-edge-nginx --memory 64m $(mounts values-laptop) $IMG >/dev/null; sleep 2
X="docker exec csr11c-edge-nginx curl -sk -o /dev/null"
echo "9080 TLS, hostile Host:"; $X -D - -H 'Host: evil.example' -H 'X-Forwarded-Host: evil.example' https://127.0.0.1:9080/-/health/live/ | tr -d '\r' | command grep -E '^(HTTP|X-Got)'
echo "9080 plain HTTP (the old probe): $($X -w '%{http_code}' http://127.0.0.1:9080/-/health/live/)"
echo "9080 TLS /if/admin/: $($X -w '%{http_code}' https://127.0.0.1:9080/if/admin/)"
echo "8443 TLS keeps the request Host:"; $X -D - -H 'Host: iverson-authentik:8443' https://127.0.0.1:8443/-/health/live/ | tr -d '\r' | command grep -E '^X-Got-Host'
docker rm -f csr11c-edge-nginx >/dev/null; echo "leftover containers: $(docker ps -a --format '{{.Names}}' | command grep -c csr11c-edge)"
```

Expected:

```text
nginx version: nginx/1.30.5
values-laptop: nginx: configuration file /etc/nginx/nginx.conf test is successful
values-azure: nginx: configuration file /etc/nginx/nginx.conf test is successful
9080 TLS, hostile Host:
HTTP/1.1 204 No Content
X-Got-Host: [authentik.iverson.local]
X-Got-XFH: [authentik.iverson.local]
X-Got-Public: [1]
9080 plain HTTP (the old probe): 400
9080 TLS /if/admin/: 404
8443 TLS keeps the request Host:
X-Got-Host: [iverson-authentik:8443]
leftover containers: 0
```

- [ ] **Step 9: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh; cd $WT
git add Iverson.Server/deploy/helm/iverson/charts/authentik/values.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/templates/configmap-tls-proxy.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/templates/deployment-server.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/templates/ingress.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/templates/service.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/templates/backendconfig.yaml
git commit -m "serve authentik's public listener over tls to the edge

$TRAILER"
```

### Task 4: Azure trusted roots (spec §3)

**Files:**
- Modify: `Iverson.Server/deploy/terraform/azure/main.tf`
- Modify: `Iverson.Server/deploy/terraform/modules/cluster-azure/variables.tf`
- Modify: `Iverson.Server/deploy/terraform/modules/cluster-azure/waf.tf`
- Modify: `Iverson.Server/deploy/helm/iverson/templates/_validate.tpl`
- Modify: `Iverson.Server/deploy/helm/iverson/values-azure.yaml`

**Interfaces:**
- Consumes (Tasks 1 and 3): the Helm values `api.ingress.appgwTrustedRootCertificate` and `authentik.ingress.appgwTrustedRootCertificate` (default `""` in `charts/api/values.yaml` and `charts/authentik/values.yaml`). The guard reads them with `dig`, so it also renders correctly while the subchart defaults are absent. Task 1 also introduces the `<release>-api-internal-tls` Secret the guard keys on; the guard is inert until a cluster holds it.
- Produces: the Terraform variable `appgw_backend_trusted_roots` (consumed by the runbook, Task 8) and the render guard text that names `docs/runbooks/csr11-transport-network.md`.

**Design notes.**
- `data` is PEM text. The azurerm provider (3.117.1, `expandApplicationGatewayTrustedRootCertificates`) wraps it with `utils.Base64EncodeIfNot`, so the variable takes the PEM read from `ca.crt` as is. The provider schema marks `data` sensitive, so the variable itself is deliberately not `sensitive` (a sensitive map cannot drive `for_each`); a root certificate is public.
- `lifecycle.ignore_changes` in `waf.tf` lists `ssl_certificate` but not `trusted_root_certificate`, so Terraform keeps owning the roots. AGIC only references them by name through the Ingress annotation.
- The guard is a no-op under `helm template` (no cluster, so `lookup` is empty) and therefore cannot break the CI renders.

- [ ] **Step 1: Terraform variable, module wiring, gateway block**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
cd $WT/Iverson.Server/deploy/terraform
python3 -I - <<'PYEOF'
def sub(path, old, new):
    s = open(path).read()
    assert s.count(old) == 1, (path, old)
    open(path, 'w').write(s.replace(old, new))

sub("azure/main.tf", '''provider "azurerm" {
  features {}
}
''', '''# CSR round-11 #4: the roots Application Gateway trusts for backend TLS, keyed by the name the Helm
# value api.ingress.appgwTrustedRootCertificate / authentik.ingress.appgwTrustedRootCertificate
# carries (comma-separated for several). Each value is PEM text: the azurerm provider base64-encodes
# it for the gateway. Empty until the chart has issued its internal CAs (docs/runbooks/csr11-transport-network.md).
variable "appgw_backend_trusted_roots" {
  type    = map(string)
  default = {}
}

provider "azurerm" {
  features {}
}
''')
sub("azure/main.tf", '''  authentik_login_rate_window    = var.authentik_login_rate_window
}
''', '''  authentik_login_rate_window    = var.authentik_login_rate_window
  appgw_backend_trusted_roots    = var.appgw_backend_trusted_roots
}
''')
sub("modules/cluster-azure/variables.tf", '''variable "authentik_login_rate_limit" { type = number }
''', '''variable "authentik_login_rate_limit" { type = number }

# CSR round-11 #4: backend-TLS trust roots for the gateway, name -> PEM text (the provider
# base64-encodes it). Names are what the Ingress annotation appgw-trusted-root-certificate refers to.
variable "appgw_backend_trusted_roots" {
  type    = map(string)
  default = {}
}
''')
sub("modules/cluster-azure/waf.tf", '''  lifecycle {
    ignore_changes = [
      backend_address_pool, backend_http_settings,''', '''  # Terraform owns the trust roots (AGIC only references them by name), so they are not ignored below.
  dynamic "trusted_root_certificate" {
    for_each = var.appgw_backend_trusted_roots
    content {
      name = trusted_root_certificate.key
      data = trusted_root_certificate.value
    }
  }

  lifecycle {
    ignore_changes = [
      backend_address_pool, backend_http_settings,''')
PYEOF
git diff --stat -- .
```

Expected: three files changed, 26 insertions.

- [ ] **Step 2: Verify the Terraform (fmt, validate both roots, tfsec)**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
cd $WT/Iverson.Server/deploy/terraform
terraform fmt -check -recursive . ; echo "fmt rc=$?"
rm -rf $SCR/tf-az && mkdir -p $SCR/tf-az && cp -r . $SCR/tf-az/
export TF_PLUGIN_CACHE_DIR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/udd2/plugcache
flock -o $SCR/heavy.lock -c '
  cd '$SCR'/tf-az/azure && terraform init -backend=false -input=false >/dev/null 2>&1 && terraform validate -no-color | head -2
  cd ../modules/cluster-azure && terraform init -backend=false -input=false >/dev/null 2>&1 && terraform validate -no-color | head -2
  tfsec . --no-colour 2>&1 | tail -3'
```

Expected: `fmt rc=0`, `Success! The configuration is valid.` twice, `No problems detected!`. (`terraform plan` needs Azure credentials, so the rendered block is not planned here; the provider source line is the encoding evidence.)

Then plan the trusted-root block with a non-empty map (spec §7), using a mocked azurerm provider so no credentials are needed. The two PEMs are the chart's own CAs from the azure render:

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
cd $CH && helm dependency build . >/dev/null
for n in api authentik; do
  helm template iverson . -n iverson -f values-azure.yaml -f values-azure.ci-override.yaml -s charts/$n/templates/secret-internal-tls.yaml \
    | python3 -c 'import sys,yaml,base64; print(base64.b64decode(yaml.safe_load(sys.stdin)["data"]["ca.crt"]).decode(), end="")' > $SCR/tf-az/$n-ca.pem
done
mkdir -p $SCR/tf-az/modules/cluster-azure/tests
cat > $SCR/tf-az/modules/cluster-azure/tests/roots.tftest.hcl <<X
mock_provider "azurerm" {
  mock_data "azurerm_client_config" {
    defaults = {
      tenant_id       = "00000000-0000-0000-0000-000000000000"
      object_id       = "00000000-0000-0000-0000-000000000000"
      subscription_id = "00000000-0000-0000-0000-000000000000"
    }
  }
}

variables {
  cluster_name                   = "iverson"
  location                       = "eastus"
  api_authorized_ip_ranges       = ["203.0.113.0/24"]
  key_vault_authorized_ip_ranges = ["203.0.113.0/24"]
  cluster_admin_group_object_ids = ["00000000-0000-0000-0000-000000000000"]
  authentik_host                 = "authentik.example.com"
  authentik_login_rate_limit     = 100
  authentik_login_rate_window    = 300
}

run "empty" {
  command = plan
  assert {
    condition     = length(azurerm_application_gateway.agic.trusted_root_certificate) == 0
    error_message = "an empty map renders no trusted roots"
  }
}

run "two_roots" {
  command = plan
  variables {
    appgw_backend_trusted_roots = {
      "iverson-api-ca"       = file("$SCR/tf-az/api-ca.pem")
      "iverson-authentik-ca" = file("$SCR/tf-az/authentik-ca.pem")
    }
  }
  assert {
    condition     = toset([for r in azurerm_application_gateway.agic.trusted_root_certificate : r.name]) == toset(["iverson-api-ca", "iverson-authentik-ca"])
    error_message = "one trusted root per map key"
  }
  assert {
    condition     = [for r in azurerm_application_gateway.agic.trusted_root_certificate : r.data if r.name == "iverson-api-ca"][0] == file("$SCR/tf-az/api-ca.pem")
    error_message = "the root carries the PEM as given"
  }
}
X
export TF_PLUGIN_CACHE_DIR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/udd2/plugcache
flock -o $SCR/heavy.lock -c 'cd '$SCR'/tf-az/modules/cluster-azure && systemd-run --user --scope -q -p MemoryMax=2G terraform test -no-color | tail -1'
```

Expected: `Success! 2 passed, 0 failed.` The test runs in the scratch copy, which the block above initialised; nothing is added to the repo.

- [ ] **Step 3: Helm guard in `_validate.tpl`, empty keys in `values-azure.yaml`**

The new block goes at the end of the `iverson.validateNoPlaceholders` define, after the existing `lookup` guard.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
cd $CH
python3 -I - <<'PYEOF'
p = "templates/_validate.tpl"
s = open(p).read()
tail = "{{- end }}\n{{- end -}}\n"
assert s.endswith(tail)
new = '''{{- end }}
{{- /*
CSR round-11 #4 (two-phase Azure rollout): Application Gateway trusts a backend certificate
only through a trusted root that Terraform uploaded to the gateway (appgw_backend_trusted_roots)
and that the Ingress names. The API's internal-TLS Secret exists once the chart's certificates
are in place (phase 1), and from then on both pods' root names must be set, or the gateway
would keep speaking plaintext to pods that now serve TLS. Both hops key on the API Secret:
<release>-authentik-internal-tls has existed since round 10, so its presence does not mean
phase 1 ran. lookup resolves empty under `helm template` / --dry-run (no cluster), so CI
renders are unaffected. See docs/runbooks/csr11-transport-network.md.
*/}}
{{- $apiVals := default dict .Values.api }}
{{- $akVals := default dict .Values.authentik }}
{{- $azureClass := "azure-application-gateway" }}
{{- if or (eq (dig "ingress" "className" "" $apiVals) $azureClass) (eq (dig "ingress" "className" "" $akVals) $azureClass) }}
{{- $apiTlsSecret := lookup "v1" "Secret" .Release.Namespace (printf "%s-api-internal-tls" .Release.Name) }}
{{- if $apiTlsSecret }}
{{- if or (not (dig "ingress" "appgwTrustedRootCertificate" "" $apiVals)) (not (dig "ingress" "appgwTrustedRootCertificate" "" $akVals)) }}
{{- fail (printf "the Application Gateway class needs api.ingress.appgwTrustedRootCertificate and authentik.ingress.appgwTrustedRootCertificate: Secret %s-api-internal-tls exists, so the chart has issued its internal CAs, but at least one trusted-root name is empty. Upload the roots to the gateway and set both names (docs/runbooks/csr11-transport-network.md)." .Release.Name) }}
{{- end }}
{{- end }}
{{- end }}
{{- end -}}
'''
open(p, "w").write(s[:-len(tail)] + new)

p = "values-azure.yaml"
s = open(p).read()
old = '''    tlsSecretName: "iverson-api-tls"
'''
assert s.count(old) == 1
s = s.replace(old, old + '''    # CSR round-11 #4: name of the gateway trusted root (a key of the Terraform variable
    # appgw_backend_trusted_roots) that signs the API's internal certificate. Empty until the
    # runbook's phase 2; once set, the API's backend hops use TLS. A non-empty value is required
    # on every upgrade after <release>-api-internal-tls exists (docs/runbooks/csr11-transport-network.md).
    appgwTrustedRootCertificate: ""
''')
old = '''    tlsSecretName: "iverson-authentik-tls"
'''
assert s.count(old) == 1
s = s.replace(old, old + '''    # CSR round-11 #4: as api.ingress.appgwTrustedRootCertificate, for the Authentik pod's root.
    appgwTrustedRootCertificate: ""
''')
open(p, "w").write(s)
PYEOF
git diff --stat -- templates/_validate.tpl values-azure.yaml
```

Expected: `_validate.tpl` 21 insertions, `values-azure.yaml` 7 insertions.

- [ ] **Step 4: Prove the guard both ways**

`lookup` returns empty under `helm template`, so the guard's firing path needs a stand-in for the Secret. The procedure copies the chart to scratch and replaces only the one `lookup` expression in the copy with a fake existing Secret; the worktree chart is untouched.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
cd $CH && helm dependency build . >/dev/null
rm -rf $SCR/guard && mkdir -p $SCR/guard && cp -r $CH $SCR/guard/c && cd $SCR/guard/c
sed -i 's|lookup "v1" "Secret" .Release.Namespace (printf "%s-api-internal-tls" .Release.Name)|dict "metadata" (dict "name" "stub")|' templates/_validate.tpl
echo "stub installed: $(command grep -c 'dict "metadata" (dict "name" "stub")' templates/_validate.tpl) occurrence"
A="-f values-azure.yaml -f values-azure.ci-override.yaml"
t() { helm template iverson . -n iverson $A "$@" 2>&1 >/dev/null | head -c 220; echo " rc=${PIPESTATUS[0]}"; }
echo "--- stub Secret, both names empty"; t
echo "--- stub Secret, api name only"; t --set api.ingress.appgwTrustedRootCertificate=iverson-api-ca
echo "--- stub Secret, authentik name only"; t --set authentik.ingress.appgwTrustedRootCertificate=iverson-authentik-ca
echo "--- stub Secret, both names set"; t --set api.ingress.appgwTrustedRootCertificate=iverson-api-ca --set authentik.ingress.appgwTrustedRootCertificate=iverson-authentik-ca
echo "--- stub Secret, gcp class"; helm template iverson . -n iverson -f values-gcp.yaml -f values-gcp.ci-override.yaml >/dev/null; echo "rc=$?"
echo "--- real chart (lookup empty), both names empty"; cd $CH && helm template iverson . -n iverson $A >/dev/null; echo "rc=$?"
```

Expected: `stub installed: 1 occurrence`; the first three stub cases print `Error: execution error at (iverson/templates/networkpolicies.yaml:1:4): the Application Gateway class needs api.ingress.appgwTrustedRootCertificate and authentik.ingress.appgwTrustedRootCertificate: Secret iverson-api-internal-tls exists, ...` with `rc=1`; the other three print `rc=0`.

- [ ] **Step 5: Render the azure profile and lint**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
cd $CH && helm dependency build . >/dev/null
for p in laptop local aws azure gcp; do
  f="-f values-$p.yaml"; [ -f values-$p.ci-override.yaml ] && f="$f -f values-$p.ci-override.yaml"
  helm template iverson . -n iverson $f >/dev/null; echo "$p render rc=$?"
  helm lint . $f 2>&1 | command grep "chart(s) linted"
done
```

Expected: five `render rc=0` and five `1 chart(s) linted, 0 chart(s) failed`.

- [ ] **Step 6: Commit**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
cd $WT
git add Iverson.Server/deploy/terraform/azure/main.tf Iverson.Server/deploy/terraform/modules/cluster-azure/variables.tf Iverson.Server/deploy/terraform/modules/cluster-azure/waf.tf Iverson.Server/deploy/helm/iverson/templates/_validate.tpl Iverson.Server/deploy/helm/iverson/values-azure.yaml
git commit -m "add azure gateway trusted roots and the two-phase render guard

$TRAILER"
```

---

### Task 5: StarRocks FE TLS and verifying clients (#4; spec §1 row 2, §5)

**Files:**
- Create: `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/_helpers.tpl`
- Create: `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/secret-tls.yaml`
- Create: `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/configmap-fe.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/starrockscluster.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/job-create-user.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/worker/templates/deployment.yaml`

**Interfaces:**
- Produces, for the runbook task:
  - the Secret `<release>-starrocks-tls` (`tls.crt`, `tls.key`, `ca.crt`; `resource-policy: keep`). Rotation (runbook §3): delete it and upgrade; while the upgrade waits on the create-user hook, delete the FE pods; then restart the API and worker pods.
  - the ConfigMap `<release>-starrocks-fe-config` (key `fe.conf`).
  - Rollout behaviour: on the upgrade that adds TLS, the create-user Job, the API and the worker treat a not-yet-restarted plaintext FE as unavailable, until the operator has restarted the FE pods with TLS. The Job waits in its "waiting for FE..." loop, and the API and worker readiness gates keep the new pods unready.
- Touches `charts/api/templates/deployment.yaml`, which Task 1 also edits. This task's anchors are the StarRocks connection-string line and the `postgres-tls` mount and volume blocks, which Task 1 does not change. The two tasks insert into the same `volumeMounts` and `volumes` lists, at different anchors.
- Consumes nothing from Tasks 1–4.

The StarRocks subchart renders only when `global.engagementEnabled` is true. The laptop profile turns it off, so this task's objects render on local, aws, gcp and azure only. The api and worker mounts sit inside the same `engagementEnabled` guard as the StarRocks env vars.

- [ ] **Step 1: Fetch the reference files.** You need StarRocks 4.1.1's `conf/fe.conf`, which is the file the fe-ubuntu image ships unchanged: `build.sh` copies it into `output/fe/conf`, the artifacts image copies `output`, and `fe-ubuntu.Dockerfile` copies the artifacts. You also need the operator 1.11.5 CRDs for the schema check. Both are pinned by md5.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
mkdir -p $SCR/task5/render
gh api 'repos/StarRocks/starrocks/contents/conf/fe.conf?ref=4.1.1' --jq .content | base64 -d > $SCR/task5/fe.conf.4.1.1
helm show crds operator --repo https://starrocks.github.io/starrocks-kubernetes-operator --version 1.11.5 > $SCR/task5/sr-crds.yaml
md5sum $SCR/task5/fe.conf.4.1.1 $SCR/task5/sr-crds.yaml | awk '{print $1}'
```

Expected:
```
defe03a7a2fe9cb76f2503aac32bd830
a6bf1948967030a17add7b963d822546
```

- [ ] **Step 2: The keystore constants.** These go in a new helper file, because the init container and `fe.conf` both use them. Create `$CH/charts/starrocks/templates/_helpers.tpl`:

```gotemplate
{{- /*
CSR round 11 #4: where the FE keystore lives and its password. The init container in
starrockscluster.yaml writes the keystore and configmap-fe.yaml's fe.conf points FE at it, so both
read these two values from here.

The password is a fixed constant, not a secret: the keystore exists only on the FE pod's emptyDir
and holds the same key <release>-starrocks-tls already protects, so a password adds no protection,
and a generated one would have to be copied into the ConfigMap.
*/}}
{{- define "starrocks.feKeystorePath" -}}
/opt/starrocks/fe/tls/fe.p12
{{- end }}

{{- define "starrocks.feKeystorePassword" -}}
iverson-fe-keystore
{{- end }}
```

- [ ] **Step 3: The FE certificate.** Create `$CH/charts/starrocks/templates/secret-tls.yaml`, which follows `charts/authentik/templates/secret-internal-tls.yaml`. StarRocks has no Ingress, so there is no public-certificate name for it to collide with.

```yaml
{{- /* CSR round 11 #4: the certificate StarRocks FE serves on its MySQL port (9030) and the CA the API,
   the worker and the create-user Job verify it with. Lookup-or-generate like
   charts/authentik/templates/secret-internal-tls.yaml: an existing Secret is reused unchanged, so
   upgrades keep the same CA. The CA private key is never stored, so nothing in the cluster can issue
   more certificates under it. The certificate names the FE Service, the host every client dials.
   Rotate by deleting this Secret and upgrading; while the upgrade waits on the create-user hook,
   delete the FE pods; then restart the API and worker pods (docs/runbooks/csr11-transport-network.md §3). */}}
{{- $name := printf "%s-starrocks-tls" .Release.Name }}
apiVersion: v1
kind: Secret
metadata:
  name: {{ $name }}
  annotations:
    "helm.sh/resource-policy": keep
type: Opaque
data:
{{- $existing := lookup "v1" "Secret" .Release.Namespace $name }}
{{- if $existing }}
{{- range $key := list "tls.crt" "tls.key" "ca.crt" }}
  {{ $key }}: {{ index $existing.data $key | required (printf "Secret %s has no %s: delete it and upgrade to regenerate it (see the comment at the top of this template)" $name $key) }}
{{- end }}
{{- else }}
{{- $svc := printf "%s-starrocks-fe-service" .Release.Name }}
{{- $ca := genCA "iverson-starrocks-ca" 3650 }}
{{- $cert := genSignedCert $svc nil (list $svc (printf "%s.%s.svc" $svc .Release.Namespace) (printf "%s.%s.svc.cluster.local" $svc .Release.Namespace)) 3650 $ca }}
  tls.crt: {{ $cert.Cert | b64enc }}
  tls.key: {{ $cert.Key | b64enc }}
  ca.crt: {{ $ca.Cert | b64enc }}
{{- end }}
```

- [ ] **Step 4: FE's `fe.conf`.** Create `$CH/charts/starrocks/templates/configmap-fe.yaml`. Its body is Step 1's `fe.conf.4.1.1` line for line, indented four spaces, with the trailing spaces trimmed from three comment lines (`# Choose one if …`, `# Advanced configurations`, `# sys_log_verbose_modules =`). The three `ssl_*` lines follow it. Step 8's check compares the two files.

```yaml
{{- /*
CSR round 11 #4: FE's fe.conf. The FE entrypoint links this file over the image's own fe.conf, so it
must be complete: everything above the CSR line is conf/fe.conf of StarRocks 4.1.1 verbatim (the file
the fe-ubuntu:4.1.1 image ships), with trailing spaces trimmed from three comment lines. When the FE
image is bumped, replace that part with the new tag's conf/fe.conf.
The operator also reads this file to learn the FE ports (http_port, rpc_port, query_port,
edit_log_port).
*/}}
apiVersion: v1
kind: ConfigMap
metadata:
  name: {{ .Release.Name }}-starrocks-fe-config
data:
  fe.conf: |
    # Licensed to the Apache Software Foundation (ASF) under one
    # or more contributor license agreements.  See the NOTICE file
    # distributed with this work for additional information
    # regarding copyright ownership.  The ASF licenses this file
    # to you under the Apache License, Version 2.0 (the
    # "License"); you may not use this file except in compliance
    # with the License.  You may obtain a copy of the License at
    #
    #   http://www.apache.org/licenses/LICENSE-2.0
    #
    # Unless required by applicable law or agreed to in writing,
    # software distributed under the License is distributed on an
    # "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
    # KIND, either express or implied.  See the License for the
    # specific language governing permissions and limitations
    # under the License.

    #####################################################################
    ## The uppercase properties are read and exported by bin/start_fe.sh.
    ## To see all Frontend configurations,
    ## see fe/fe-core/src/main/java/com/starrocks/common/Config.java

    # the output dir of stderr/stdout/gc
    LOG_DIR = ${STARROCKS_HOME}/log

    DATE = "$(date +%Y%m%d-%H%M%S)"
    JAVA_OPTS="-Dlog4j2.formatMsgNoLookups=true -Xmx8192m -XX:+UseG1GC -Xlog:gc*:${LOG_DIR}/fe.gc.log.$DATE:time -XX:ErrorFile=${LOG_DIR}/hs_err_pid%p.log -Djava.security.policy=${STARROCKS_HOME}/conf/udf_security.policy"

    ##
    ## the lowercase properties are read by main program.
    ##

    # DEBUG, INFO, WARN, ERROR, FATAL
    sys_log_level = INFO

    # store metadata, create it if it is not exist.
    # Default value is ${STARROCKS_HOME}/meta
    # meta_dir = ${STARROCKS_HOME}/meta

    http_port = 8030
    rpc_port = 9020
    query_port = 9030
    edit_log_port = 9010

    # Enable jaeger tracing by setting jaeger_grpc_endpoint
    # jaeger_grpc_endpoint = http://localhost:14250

    # Choose one if there are more than one ip except loopback address.
    # Note that there should at most one ip match this list.
    # If no ip match this rule, will choose one randomly.
    # use CIDR format, e.g. 10.10.10.0/24
    # Default value is empty.
    # priority_networks = 10.10.10.0/24;192.168.0.0/16

    # Advanced configurations
    # log_roll_size_mb = 1024
    # sys_log_dir = ${STARROCKS_HOME}/log
    # sys_log_roll_num = 10
    # sys_log_verbose_modules =
    # audit_log_dir = ${STARROCKS_HOME}/log
    # audit_log_modules = slow_query, query
    # audit_log_roll_num = 10
    # meta_delay_toleration_second = 10
    # qe_max_connection = 1024
    # max_conn_per_user = 100
    # qe_query_timeout_second = 300
    # qe_slow_log_ms = 5000

    # CSR round 11 #4: serve TLS on the MySQL port from the keystore the FE pod's init container
    # builds (starrockscluster.yaml). ssl_force_secure_transport stays false: the FE and BE
    # entrypoint scripts connect with plain mysql.
    ssl_keystore_location = {{ include "starrocks.feKeystorePath" . }}
    ssl_keystore_password = {{ include "starrocks.feKeystorePassword" . }}
    ssl_key_password = {{ include "starrocks.feKeystorePassword" . }}
```

- [ ] **Step 5: FE spec.** In `$CH/charts/starrocks/templates/starrockscluster.yaml`, replace:

```yaml
        mountPath: /opt/starrocks/fe/meta
    nodeSelector:
      {{- toYaml .Values.fe.nodeSelector | nindent 6 }}
```

with:

```yaml
        mountPath: /opt/starrocks/fe/meta
      # CSR round 11 #4: holds the FE keystore. The operator mounts a storage volume under its own
      # name and turns storageClassName emptyDir into an emptyDir (no PVC); a size starting with
      # "0" would drop the volume, so the size is a token non-zero value.
      - name: fe-tls
        storageClassName: emptyDir
        storageSize: 1Mi
        mountPath: /opt/starrocks/fe/tls
    # CSR round 11 #4: builds the PKCS12 keystore fe.conf names from <release>-starrocks-tls on every
    # pod start. The Secret reaches it through env, not a volume: the operator may add a hash to a
    # mounted Secret's volume name. The image is the chart's mysql client image, which ships openssl.
    initContainers:
      - name: fe-keystore
        image: {{ .Values.createUser.image | quote }}
        securityContext:
          runAsNonRoot: true
          runAsUser: 1000
          allowPrivilegeEscalation: false
          readOnlyRootFilesystem: true
          capabilities: { drop: ["ALL"] }
          seccompProfile: { type: RuntimeDefault }
        resources:
          requests: { cpu: "100m", memory: "64Mi" }
          limits: { cpu: "250m", memory: "128Mi" }
        env:
          - name: TLS_CRT
            valueFrom: { secretKeyRef: { name: {{ .Release.Name }}-starrocks-tls, key: tls.crt } }
          - name: TLS_KEY
            valueFrom: { secretKeyRef: { name: {{ .Release.Name }}-starrocks-tls, key: tls.key } }
          - name: CA_CRT
            valueFrom: { secretKeyRef: { name: {{ .Release.Name }}-starrocks-tls, key: ca.crt } }
          - name: KEYSTORE_PASSWORD
            value: {{ include "starrocks.feKeystorePassword" . | quote }}
        command: ["sh", "-c"]
        args:
          - |
            set -e
            umask 077
            cd /opt/starrocks/fe/tls
            printf '%s\n' "$TLS_CRT" > tls.crt
            printf '%s\n' "$TLS_KEY" > tls.key
            printf '%s\n' "$CA_CRT" > ca.crt
            openssl pkcs12 -export -name fe -in tls.crt -inkey tls.key -certfile ca.crt \
              -passout env:KEYSTORE_PASSWORD -out fe.p12.tmp
            rm -f tls.crt tls.key ca.crt
            # FE runs as root (the image default) and this container as uid 1000: 0644 lets FE read
            # the keystore without relying on CAP_DAC_OVERRIDE. The emptyDir is private to the pod.
            chmod 0644 fe.p12.tmp
            mv -f fe.p12.tmp {{ include "starrocks.feKeystorePath" . }}
        volumeMounts:
          - name: fe-tls
            mountPath: /opt/starrocks/fe/tls
    configMapInfo:
      configMapName: {{ .Release.Name }}-starrocks-fe-config
      resolveKey: fe.conf
    nodeSelector:
      {{- toYaml .Values.fe.nodeSelector | nindent 6 }}
```

- [ ] **Step 6: The create-user Job.** Make four replacements in `$CH/charts/starrocks/templates/job-create-user.yaml`:
  - mount the CA;
  - define `TLS`;
  - put `$TLS` on all four `mysql` calls (the two in the `until` condition, the `SHOW BACKENDS` loop and the grant batch), and decide `AUTH` inside the `until`, only on a connection that completed TLS.

The host stays `$FE_HOST`, which is the certificate's CN.

6a. Replace:

```yaml
        seccompProfile: { type: RuntimeDefault }
      containers:
```

with:

```yaml
        seccompProfile: { type: RuntimeDefault }
      # CSR round 11 #4: the CA that issued FE's certificate (secret-tls.yaml). That Secret is an
      # ordinary release resource, so it exists before this post-install/post-upgrade hook runs.
      volumes:
        - name: starrocks-tls
          secret:
            secretName: {{ .Release.Name }}-starrocks-tls
            items:
              - key: ca.crt
                path: ca.crt
      containers:
```

6b. Replace:

```yaml
              valueFrom: { secretKeyRef: { name: {{ .Release.Name }}-starrocks-provisioner, key: password } }
          command: ["sh", "-c"]
```

with:

```yaml
              valueFrom: { secretKeyRef: { name: {{ .Release.Name }}-starrocks-provisioner, key: password } }
          volumeMounts:
            - name: starrocks-tls
              mountPath: /etc/starrocks-tls
              readOnly: true
          command: ["sh", "-c"]
```

6c. Replace:

```yaml
              FE_HOST="{{ .Release.Name }}-starrocks-fe-service"
              BE_REPLICAS="{{ .Values.be.replicas }}"
              until mysql -h "$FE_HOST" -P 9030 -u root -e "SELECT 1" >/dev/null 2>&1 \
                 || mysql -h "$FE_HOST" -P 9030 -u root -p"$ROOT_PW" -e "SELECT 1" >/dev/null 2>&1; do
                echo "waiting for FE..."; sleep 5
              done
              if mysql -h "$FE_HOST" -P 9030 -u root -p"$ROOT_PW" -e "SELECT 1" >/dev/null 2>&1; then
                AUTH="-p$ROOT_PW"
              else
                AUTH=""
              fi
```

with:

```yaml
              FE_HOST="{{ .Release.Name }}-starrocks-fe-service"
              BE_REPLICAS="{{ .Values.be.replicas }}"
              # CSR round 11 #4: every call requires TLS and verifies FE's certificate against the
              # chart CA and FE_HOST, so the passwords below travel only inside TLS. An FE that
              # does not serve TLS yet (an upgrade before the FE pods restart) counts as not ready.
              # AUTH is decided only by a connection that completed TLS: while the operator rolls
              # a multi-FE StatefulSet, the FE Service mixes restarted and plaintext pods.
              TLS="--ssl-mode=VERIFY_IDENTITY --ssl-ca=/etc/starrocks-tls/ca.crt"
              until
                if mysql $TLS -h "$FE_HOST" -P 9030 -u root -p"$ROOT_PW" -e "SELECT 1" >/dev/null 2>&1; then
                  AUTH="-p$ROOT_PW"
                elif mysql $TLS -h "$FE_HOST" -P 9030 -u root -e "SELECT 1" >/dev/null 2>&1; then
                  AUTH=""
                else
                  false
                fi
              do
                echo "waiting for FE..."; sleep 5
              done
```

6d. Replace the two single lines below. Each is one line in the file; the lines are long and only their start changes.

```yaml
              until [ "$(mysql -h "$FE_HOST" -P 9030 -u root $AUTH --skip-column-names --batch -e "SHOW BACKENDS;" 2>/dev/null | wc -l)" -ge "$BE_REPLICAS" ]; do
```

with:

```yaml
              until [ "$(mysql $TLS -h "$FE_HOST" -P 9030 -u root $AUTH --skip-column-names --batch -e "SHOW BACKENDS;" 2>/dev/null | wc -l)" -ge "$BE_REPLICAS" ]; do
```

and:

```yaml
              mysql -h "$FE_HOST" -P 9030 -u root $AUTH -e "
```

with:

```yaml
              mysql $TLS -h "$FE_HOST" -P 9030 -u root $AUTH -e "
```

- [ ] **Step 7: The API and worker clients.** In both `$CH/charts/api/templates/deployment.yaml` and `$CH/charts/worker/templates/deployment.yaml`, make three changes: append the TLS keys to each StarRocks connection string, then add the CA mount and the CA volume after the `postgres-tls` ones.

7a. **api only.** Replace:

```yaml
              value: "Server={{ .Release.Name }}-starrocks-fe-service;Port=9030;User Id=iverson_app;Password=$(STARROCKS_APP_PASSWORD);"
```

with:

```yaml
              value: "Server={{ .Release.Name }}-starrocks-fe-service;Port=9030;User Id=iverson_app;Password=$(STARROCKS_APP_PASSWORD);SslMode=VerifyFull;SslCa=/etc/starrocks-tls/ca.crt;"
```

7b. **worker only.** Replace:

```yaml
              value: "Server={{ .Release.Name }}-starrocks-fe-service;Port=9030;User Id=iverson_app;Password=$(STARROCKS_APP_PASSWORD);"
```

with:

```yaml
              value: "Server={{ .Release.Name }}-starrocks-fe-service;Port=9030;User Id=iverson_app;Password=$(STARROCKS_APP_PASSWORD);SslMode=VerifyFull;SslCa=/etc/starrocks-tls/ca.crt;"
```

and:

```yaml
              value: "Server={{ .Release.Name }}-starrocks-fe-service;Port=9030;User Id=iverson_provisioner;Password=$(STARROCKS_PROVISIONER_PASSWORD);"
```

with:

```yaml
              value: "Server={{ .Release.Name }}-starrocks-fe-service;Port=9030;User Id=iverson_provisioner;Password=$(STARROCKS_PROVISIONER_PASSWORD);SslMode=VerifyFull;SslCa=/etc/starrocks-tls/ca.crt;"
```

7c. **api.** Replace:

```yaml
            - name: postgres-tls
              mountPath: /etc/postgres-tls
              readOnly: true
```

with:

```yaml
            - name: postgres-tls
              mountPath: /etc/postgres-tls
              readOnly: true
            {{- if .Values.global.engagementEnabled }}
            # CSR round 11 #4: the CA ConnectionStrings__StarRocks verifies FE's certificate with.
            - name: starrocks-tls
              mountPath: /etc/starrocks-tls
              readOnly: true
            {{- end }}
```

7d. **worker.** Replace the same three lines with:

```yaml
            - name: postgres-tls
              mountPath: /etc/postgres-tls
              readOnly: true
            {{- if .Values.global.engagementEnabled }}
            # CSR round 11 #4: the CA both StarRocks connection strings verify FE's certificate with.
            - name: starrocks-tls
              mountPath: /etc/starrocks-tls
              readOnly: true
            {{- end }}
```

7e. **Both files.** Replace:

```yaml
        - name: postgres-tls
          secret:
            secretName: {{ .Release.Name }}-postgres-ca
            items:
              - key: ca.crt
                path: ca.crt
```

with:

```yaml
        - name: postgres-tls
          secret:
            secretName: {{ .Release.Name }}-postgres-ca
            items:
              - key: ca.crt
                path: ca.crt
        {{- if .Values.global.engagementEnabled }}
        - name: starrocks-tls
          secret:
            secretName: {{ .Release.Name }}-starrocks-tls
            items:
              - key: ca.crt
                path: ca.crt
        {{- end }}
```

- [ ] **Step 8: Render, lint and check.** Write `$SCR/task5/check-starrocks.py`:

```python
"""Task 5 render checks: python3 -I check-starrocks.py <render-dir> <upstream fe.conf> <operator CRDs>"""
import base64, subprocess, sys, tempfile, os, re
import jsonschema, yaml

rdir, upstream, crds = sys.argv[1], sys.argv[2], sys.argv[3]
crd = [d for d in yaml.safe_load_all(open(crds)) if d and d["metadata"]["name"] == "starrocksclusters.starrocks.com"][0]
schema = [v for v in crd["spec"]["versions"] if v["name"] == "v1"][0]["schema"]["openAPIV3Schema"]
CA_PATH = "/etc/starrocks-tls/ca.crt"
SSL = "SslMode=VerifyFull;SslCa=" + CA_PATH + ";"
fails = []

def check(cond, msg):
    if not cond:
        fails.append(msg)

def env_of(c):
    return {e["name"]: e for e in c.get("env", [])}

for p in ["laptop", "local", "aws", "gcp", "azure"]:
    docs = [d for d in yaml.safe_load_all(open(f"{rdir}/{p}.yaml")) if d]
    by = {(d["kind"], d["metadata"]["name"]): d for d in docs}
    api = by[("Deployment", "iverson-api")]["spec"]["template"]["spec"]
    wrk = by[("Deployment", "iverson-worker")]["spec"]["template"]["spec"]
    engaged = ("StarRocksCluster", "iverson-starrocks") in by
    if p == "laptop":
        check(not engaged, f"{p}: StarRocksCluster rendered")
        for k in [("Secret", "iverson-starrocks-tls"), ("ConfigMap", "iverson-starrocks-fe-config")]:
            check(k not in by, f"{p}: {k} rendered with engagement off")
        for name, spec in [("api", api), ("worker", wrk)]:
            check(all(v["name"] != "starrocks-tls" for v in spec["volumes"]), f"{p}: {name} has starrocks-tls volume")
            check(all(m["name"] != "starrocks-tls" for m in spec["containers"][0]["volumeMounts"]), f"{p}: {name} mounts starrocks-tls")
        print(f"{p}: engagement off, no StarRocks objects or mounts")
        continue
    check(engaged, f"{p}: no StarRocksCluster")

    # Secret: CN + three SANs, signed by ca.crt
    sec = by[("Secret", "iverson-starrocks-tls")]
    check(sec["metadata"]["annotations"].get("helm.sh/resource-policy") == "keep", f"{p}: secret not keep")
    check(sorted(sec["data"]) == ["ca.crt", "tls.crt", "tls.key"], f"{p}: secret keys {sorted(sec['data'])}")
    with tempfile.TemporaryDirectory() as t:
        for k, v in sec["data"].items():
            open(os.path.join(t, k), "wb").write(base64.b64decode(v))
        txt = subprocess.run(["openssl", "x509", "-in", f"{t}/tls.crt", "-noout", "-subject", "-ext", "subjectAltName"],
                             capture_output=True, text=True).stdout
        svc = "iverson-starrocks-fe-service"
        check(f"CN=iverson-starrocks-fe-service" in txt.replace(" ", ""), f"{p}: CN wrong: {txt}")
        sans = sorted(re.findall(r"DNS:([^,\s]+)", txt))
        check(sans == sorted([svc, f"{svc}.iverson.svc", f"{svc}.iverson.svc.cluster.local"]), f"{p}: SANs {sans}")
        v = subprocess.run(["openssl", "verify", "-CAfile", f"{t}/ca.crt", f"{t}/tls.crt"], capture_output=True, text=True)
        check(v.returncode == 0, f"{p}: cert does not verify against ca.crt: {v.stdout}{v.stderr}")

    # ConfigMap: upstream fe.conf (trailing spaces trimmed) followed by the three ssl lines
    conf = by[("ConfigMap", "iverson-starrocks-fe-config")]["data"]["fe.conf"].splitlines()
    up = [l.rstrip() for l in open(upstream).read().splitlines()]
    check(conf[: len(up)] == up, f"{p}: fe.conf does not start with upstream conf/fe.conf")
    kv = dict(l.split("=", 1) for l in (x.replace(" ", "") for x in conf[len(up):]) if l and not l.startswith("#"))
    check(kv == {"ssl_keystore_location": "/opt/starrocks/fe/tls/fe.p12",
                 "ssl_keystore_password": "iverson-fe-keystore",
                 "ssl_key_password": "iverson-fe-keystore"}, f"{p}: ssl lines {kv}")
    check(not any(l.strip().startswith("ssl_force_secure_transport") for l in conf), f"{p}: ssl_force_secure_transport set")

    # StarRocksCluster: valid against the operator's CRD schema, then the FE spec fields
    cr = by[("StarRocksCluster", "iverson-starrocks")]
    errs = [e.message for e in jsonschema.Draft7Validator(schema).iter_errors(cr)]
    check(not errs, f"{p}: StarRocksCluster fails the v1.11.5 CRD schema: {errs[:3]}")
    fe = cr["spec"]["starRocksFeSpec"]
    sv = {v["name"]: v for v in fe["storageVolumes"]}
    check(sv.get("fe-tls") == {"name": "fe-tls", "storageClassName": "emptyDir", "storageSize": "1Mi",
                               "mountPath": "/opt/starrocks/fe/tls"}, f"{p}: fe-tls volume {sv.get('fe-tls')}")
    check(fe["configMapInfo"] == {"configMapName": "iverson-starrocks-fe-config", "resolveKey": "fe.conf"},
          f"{p}: configMapInfo {fe.get('configMapInfo')}")
    ic = fe["initContainers"]
    check(len(ic) == 1 and ic[0]["name"] == "fe-keystore", f"{p}: initContainers {[c['name'] for c in ic]}")
    job = by[("Job", "iverson-starrocks-create-user")]["spec"]["template"]["spec"]
    check(ic[0]["image"] == job["containers"][0]["image"] == "mysql:8.0.46", f"{p}: init image {ic[0]['image']}")
    e = env_of(ic[0])
    for var, key in [("TLS_CRT", "tls.crt"), ("TLS_KEY", "tls.key"), ("CA_CRT", "ca.crt")]:
        check(e[var]["valueFrom"]["secretKeyRef"] == {"name": "iverson-starrocks-tls", "key": key}, f"{p}: {var}")
    check(e["KEYSTORE_PASSWORD"]["value"] == "iverson-fe-keystore", f"{p}: KEYSTORE_PASSWORD")
    check(ic[0]["volumeMounts"] == [{"name": "fe-tls", "mountPath": "/opt/starrocks/fe/tls"}], f"{p}: init mounts")
    check("mv -f fe.p12.tmp /opt/starrocks/fe/tls/fe.p12" in ic[0]["args"][0], f"{p}: init writes elsewhere")
    sc = ic[0]["securityContext"]
    check(sc["runAsUser"] == 1000 and sc["readOnlyRootFilesystem"] and sc["capabilities"] == {"drop": ["ALL"]},
          f"{p}: init securityContext {sc}")

    # Clients: connection strings and the CA mount
    for name, spec, keys in [("api", api, ["ConnectionStrings__StarRocks"]),
                             ("worker", wrk, ["ConnectionStrings__StarRocks", "ConnectionStrings__StarRocksProvisioner"])]:
        ce = env_of(spec["containers"][0])
        for k in keys:
            check(ce[k]["value"].startswith("Server=iverson-starrocks-fe-service;Port=9030;")
                  and ce[k]["value"].endswith(SSL), f"{p}: {name} {k} = {ce[k]['value']}")
        vols = [v for v in spec["volumes"] if v["name"] == "starrocks-tls"]
        check(vols == [{"name": "starrocks-tls", "secret": {"secretName": "iverson-starrocks-tls",
                        "items": [{"key": "ca.crt", "path": "ca.crt"}]}}], f"{p}: {name} volume {vols}")
        mnts = [m for m in spec["containers"][0]["volumeMounts"] if m["name"] == "starrocks-tls"]
        check(mnts == [{"name": "starrocks-tls", "mountPath": "/etc/starrocks-tls", "readOnly": True}], f"{p}: {name} mount {mnts}")

    # create-user Job: every mysql call carries $TLS, which holds the verifying flags
    script = job["containers"][0]["args"][0]
    calls = [l for l in script.splitlines() if re.search(r"\bmysql\s", l) and not l.strip().startswith("#")]
    check(len(calls) == 4 and all("mysql $TLS -h \"$FE_HOST\"" in l for l in calls), f"{p}: job mysql calls {calls}")
    check(f'TLS="--ssl-mode=VERIFY_IDENTITY --ssl-ca={CA_PATH}"' in script, f"{p}: job TLS flags")
    check('FE_HOST="iverson-starrocks-fe-service"' in script, f"{p}: job host")
    check(job["volumes"] == [{"name": "starrocks-tls", "secret": {"secretName": "iverson-starrocks-tls",
                              "items": [{"key": "ca.crt", "path": "ca.crt"}]}}], f"{p}: job volumes")
    check(job["containers"][0]["volumeMounts"] == [{"name": "starrocks-tls", "mountPath": "/etc/starrocks-tls", "readOnly": True}],
          f"{p}: job mounts")
    print(f"{p}: secret/configmap/CR/clients/job checked")

print("FAIL\n" + "\n".join(fails) if fails else "ALL STARROCKS CHECKS PASS")
sys.exit(1 if fails else 0)
```

Then render all five profiles, lint them, run kubeconform as CI does, and run the checker:

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
cd $CH && helm dependency build . >/dev/null
for p in laptop local aws gcp azure; do
  o=""; [ -f values-$p.ci-override.yaml ] && o="-f values-$p.ci-override.yaml"
  helm template iverson . -n iverson -f values-$p.yaml $o > $SCR/task5/render/$p.yaml || echo "$p TEMPLATE FAILED"
  echo "$p: $(helm lint . -f values-$p.yaml $o 2>&1 | tail -1) | $(kubeconform -kubernetes-version 1.30.0 -summary -ignore-missing-schemas < $SCR/task5/render/$p.yaml 2>&1 | tail -1)"
done
python3 -I $SCR/task5/check-starrocks.py $SCR/task5/render $SCR/task5/fe.conf.4.1.1 $SCR/task5/sr-crds.yaml
```

Expected: each profile lints with `1 chart(s) linted, 0 chart(s) failed`, and kubeconform reports `Invalid: 0, Errors: 0`. The checker prints:

```
laptop: engagement off, no StarRocks objects or mounts
local: secret/configmap/CR/clients/job checked
aws: secret/configmap/CR/clients/job checked
gcp: secret/configmap/CR/clients/job checked
azure: secret/configmap/CR/clients/job checked
ALL STARROCKS CHECKS PASS
```

- [ ] **Step 9: Container probe.** The laptop kind run deploys no StarRocks, so this probe runs the rendered artifacts in throwaway containers (spec §7).

First, build the MySqlConnector 2.4.0 probe console. Write `$SCR/mcprobe/mc.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
<ItemGroup><PackageReference Include="MySqlConnector" Version="2.4.0" /></ItemGroup></Project>
```

and `$SCR/mcprobe/Program.cs`:

```csharp
using MySqlConnector;
foreach (var cs in args)
{
    try
    {
        await using var c = new MySqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = new MySqlCommand("show status like 'Ssl_cipher'", c);
        await using var r = await cmd.ExecuteReaderAsync();
        var cipher = await r.ReadAsync() ? r.GetString(1) : "(none)";
        Console.WriteLine($"OK  cipher='{cipher}'  <- {cs.Split(';').First(p => p.StartsWith("SslMode"))}");
    }
    catch (Exception ex) { Console.WriteLine($"ERR {ex.GetType().Name}: {ex.Message.Split('\n')[0]}  <- {cs}"); }
}
```

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
systemd-run --user --scope -q -p MemoryMax=3G dotnet build $SCR/mcprobe/mc.csproj -c Release -o $SCR/mcprobe/out -p:UseSharedCompilation=false -nologo -v q 2>&1 | tail -3
dotnet build-server shutdown >/dev/null 2>&1; ls $SCR/mcprobe/out/mc.dll
```

Expected: `0 Error(s)` and the dll path.

Next, write `$SCR/task5/probe.sh` (it removes its containers and network on exit):

```bash
#!/bin/bash
# Task 5 container probe. Everything it runs comes from the local-profile render ($SCR/task5/render/local.yaml):
# the Secret's PEMs, the FE init container's script, fe.conf's ssl lines, the create-user Job's script and
# the api/worker connection strings. allin1-ubuntu:4.1.1 stands in for the operator-managed FE pod.
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
T=$SCR/task5
P=$T/probe
NET=csr11c-starrocks-net
FE=csr11c-starrocks-fe
ROOT_PW=probe-root-pw; APP_PW=probe-app-pw; PROVISIONER_PW=probe-prov-pw
cleanup() {
  podman rm -f $FE csr11c-starrocks-init csr11c-starrocks-job csr11c-starrocks-cli csr11c-starrocks-mc >/dev/null 2>&1
  podman network rm $NET >/dev/null 2>&1
}
trap cleanup EXIT
cleanup
rm -rf $P && mkdir -p $P/pem $P/tls $P/ca $P/wrongca
chmod 0777 $P/tls   # the kubelet creates an emptyDir 0777; the init container runs as uid 1000

python3 -I - $T/render/local.yaml $T/fe.conf.4.1.1 $P <<'PY'
import sys, yaml, base64
docs = [d for d in yaml.safe_load_all(open(sys.argv[1])) if d]
by = {(d["kind"], d["metadata"]["name"]): d for d in docs}
up, P = open(sys.argv[2]).read().splitlines(), sys.argv[3]
sec = by[("Secret", "iverson-starrocks-tls")]["data"]
for k, v in sec.items():
    open(f"{P}/pem/{k}", "wb").write(base64.b64decode(v))
open(f"{P}/ca/ca.crt", "wb").write(base64.b64decode(sec["ca.crt"]))
fe = by[("StarRocksCluster", "iverson-starrocks")]["spec"]["starRocksFeSpec"]
open(f"{P}/init.sh", "w").write(fe["initContainers"][0]["args"][0])
conf = by[("ConfigMap", "iverson-starrocks-fe-config")]["data"]["fe.conf"].splitlines()
open(f"{P}/ssl.conf", "w").write("\n" + "\n".join(conf[len(up):]) + "\n")
job = by[("Job", "iverson-starrocks-create-user")]["spec"]["template"]["spec"]["containers"][0]
open(f"{P}/job.sh", "w").write(job["args"][0])
for n in ["api", "worker"]:
    for e in by[("Deployment", f"iverson-{n}")]["spec"]["template"]["spec"]["containers"][0]["env"]:
        if e["name"].startswith("ConnectionStrings__StarRocks"):
            open(f"{P}/cs-{n}-{e['name']}", "w").write(e["value"])
PY
openssl req -x509 -newkey rsa:2048 -nodes -keyout $P/wrongca/key.pem -out $P/wrongca/ca.crt -days 1 -subj /CN=wrong-ca 2>/dev/null

echo "== 1. rendered init container script in mysql:8.0.46 (uid 1000, read-only root, no capabilities)"
podman run --rm --name csr11c-starrocks-init --memory 256m --user 1000:1000 --read-only --cap-drop ALL \
  -e TLS_CRT="$(cat $P/pem/tls.crt)" -e TLS_KEY="$(cat $P/pem/tls.key)" -e CA_CRT="$(cat $P/pem/ca.crt)" \
  -e KEYSTORE_PASSWORD=iverson-fe-keystore \
  -v $P/tls:/opt/starrocks/fe/tls docker.io/library/mysql:8.0.46 sh -c "$(cat $P/init.sh)"
echo "init rc=$?"; ls $P/tls

echo "== 2. allin1 FE with the rendered ssl lines, keystore at the rendered path, alias iverson-starrocks-fe-service"
podman network create $NET >/dev/null
podman run -d --name $FE --memory 4g --network $NET --network-alias iverson-starrocks-fe-service \
  -v $P/tls:/opt/starrocks/fe/tls:ro -v $P/ssl.conf:/probe/ssl.conf:ro \
  --entrypoint bash docker.io/starrocks/allin1-ubuntu:4.1.1 \
  -c 'cat /probe/ssl.conf >> /data/deploy/starrocks/fe/conf/fe.conf; cd /data/deploy && exec /usr/bin/tini-static -- ./entrypoint.sh' >/dev/null
for i in $(seq 1 90); do
  podman exec $FE mysql -h127.0.0.1 -P9030 -uroot --ssl-mode=DISABLED -e "SHOW BACKENDS\G" 2>/dev/null \
    | grep -q 'Alive: true' && { echo "FE up, BE Alive: true"; break; }
  sleep 5
done
podman exec $FE grep -E '^ssl_' /data/deploy/starrocks/fe/conf/fe.conf

run_mysql() { # name, CA dir mounted at the chart's path, script
  podman run --rm --name "$1" --memory 256m --network $NET --user 1000:1000 --read-only --cap-drop ALL \
    -e ROOT_PW=$ROOT_PW -e APP_PW=$APP_PW -e PROVISIONER_PW=$PROVISIONER_PW \
    -v "$2":/etc/starrocks-tls:ro docker.io/library/mysql:8.0.46 sh -c "$3"
}
TLS='--ssl-mode=VERIFY_IDENTITY --ssl-ca=/etc/starrocks-tls/ca.crt'
echo "== 3. rendered create-user Job script (local profile: be.replicas 1), chart CA"
run_mysql csr11c-starrocks-job $P/ca "$(cat $P/job.sh)"; echo "job rc=$?"
echo "== 4. the Job's flags: status as iverson_app (created by step 3)"
run_mysql csr11c-starrocks-cli $P/ca "mysql $TLS -h iverson-starrocks-fe-service -P 9030 -u iverson_app -p\"\$APP_PW\" -e status 2>/dev/null | grep -E 'Current user|SSL:'"
echo "== 5. the Job's flags, wrong CA"
run_mysql csr11c-starrocks-cli $P/wrongca "mysql $TLS -h iverson-starrocks-fe-service -P 9030 -u root -p\"\$ROOT_PW\" -e 'SELECT 1' 2>&1 | grep -v Warning; exit \${PIPESTATUS[0]}"; echo "mysql rc=$?"
echo "== 6. the Job's flags, chart CA, a host the certificate does not name"
run_mysql csr11c-starrocks-cli $P/ca "mysql $TLS -h $FE -P 9030 -u root -p\"\$ROOT_PW\" -e 'SELECT 1' 2>&1 | grep -v Warning; exit \${PIPESTATUS[0]}"; echo "mysql rc=$?"
echo "== 7. plaintext still accepted (ssl_force_secure_transport stays false)"
run_mysql csr11c-starrocks-cli $P/ca "mysql --ssl-mode=DISABLED -h iverson-starrocks-fe-service -P 9030 -u root -p\"\$ROOT_PW\" --skip-column-names -e 'SELECT 1' 2>/dev/null"

echo "== 8. MySqlConnector 2.4.0 with the three rendered connection strings (\$(VAR) expanded as the kubelet does)"
CS1=$(sed "s/\$(STARROCKS_APP_PASSWORD)/$APP_PW/" $P/cs-api-ConnectionStrings__StarRocks)
CS2=$(sed "s/\$(STARROCKS_APP_PASSWORD)/$APP_PW/" $P/cs-worker-ConnectionStrings__StarRocks)
CS3=$(sed "s/\$(STARROCKS_PROVISIONER_PASSWORD)/$PROVISIONER_PW/" $P/cs-worker-ConnectionStrings__StarRocksProvisioner)
for ca in ca wrongca; do
  echo "-- CA mounted at /etc/starrocks-tls: $ca"
  podman run --rm --name csr11c-starrocks-mc --memory 512m --network $NET --user 1000:1000 --read-only \
    -v $SCR/mcprobe/out:/app:ro -v $P/$ca:/etc/starrocks-tls:ro mcr.microsoft.com/dotnet/aspnet:10.0 \
    dotnet /app/mc.dll "$CS1" "$CS2" "$CS3" 2>&1 | sed -E 's/Password=[^;]*/Password=***/'
done
echo PROBE DONE
```

Run it on its own: the FE container takes up to 4g.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
bash $SCR/task5/probe.sh 2>&1 | tee $SCR/task5/probe.log
echo "left: $(podman ps -a --format '{{.Names}}' | grep -c '^csr11c-starrocks-') containers, $(podman network ls --format '{{.Name}}' | grep -c '^csr11c-starrocks-net$') networks"
```

Expected:
- `init rc=0`, and `fe.p12` is the only file left in the keystore directory.
- `FE up, BE Alive: true`, then the three `ssl_*` lines.
- `job rc=0`.
- Step 4 prints `Current user: 'iverson_app'@…` and `SSL: Cipher in use is TLS_AES_256_GCM_SHA384`.
- Steps 5 and 6 print `ERROR 2026 (HY000): SSL connection error: error:0A000086:SSL routines::certificate verify failed` and `mysql rc=1`.
- Step 7 prints `1`.
- Step 8, with the chart CA, prints three lines `OK  cipher='(none)'  <- SslMode=VerifyFull`. StarRocks reports no `Ssl_cipher` status (spec VA 4); a VerifyFull connection that opens is TLS. With the wrong CA it prints three lines `ERR MySqlException: SSL Authentication Error`.
- `PROBE DONE`, then `left: 0 containers, 0 networks`.

- [ ] **Step 10: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
cd $WT
git add Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/_helpers.tpl \
  Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/secret-tls.yaml \
  Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/configmap-fe.yaml \
  Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/starrockscluster.yaml \
  Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/job-create-user.yaml \
  Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml \
  Iverson.Server/deploy/helm/iverson/charts/worker/templates/deployment.yaml
git commit -m "serve tls on the starrocks fe mysql port and verify it from every client

$TRAILER"
git show --stat HEAD | tail -8
```

Expected: 7 files changed. If Task 1 is already committed, `charts/api/templates/deployment.yaml` holds only this task's hunks.

### Task 6: Authentik stores (#4: Authentik → Postgres verification; Redis removal)

**Files:**
- Modify: `$CH/charts/authentik/templates/configmap.yaml`, `$CH/charts/authentik/templates/deployment-server.yaml`, `$CH/charts/authentik/templates/deployment-worker.yaml`
- Modify: `$CH/templates/networkpolicies.yaml` (the Redis rules only; Task 7 edits other regions of this file)
- Modify: `$CH/Chart.yaml`, `$CH/Chart.lock`, `$CH/values.yaml`, `$CH/values-{aws,azure,gcp,laptop,local}.yaml`
- Delete: `$CH/charts/redis/` (`Chart.yaml`, `values.yaml`, `templates/{deployment,secret,service}.yaml`)
- Modify: `Iverson.Server/docker-compose.yml`, `Iverson.Server/Iverson.LoadTest/scripts/stack.py`
- Modify: `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikContainerFixture.cs`, `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikRecoveryFlowIntegrationTests.cs`

**Interfaces:**
- Consumes the CNPG Secret `<release>-postgres-ca` (key `ca.crt`), created by the Postgres operator for the `<release>-postgres` cluster. `charts/api/templates/deployment.yaml:277-282` and `charts/worker/templates/deployment.yaml:181-186` already mount it. This task mounts it at `/etc/postgres-tls/ca.crt`, the same path the API uses.
- Produces nothing for other tasks. Regions shared with other tasks and kept disjoint: `deployment-server.yaml` and `deployment-worker.yaml` (Task 3 edits the `tls-proxy` readiness probe in the server file only; this task edits the `migrate` and `server` `env`/`volumeMounts` and the end of `volumes`), and `networkpolicies.yaml` (Task 7 edits the `ingress-nginx` peers; this task edits the Authentik egress rule and the file's tail). Either order of Tasks 6 and 7 applies cleanly.
- The orphan `<release>-redis` Secret (`helm.sh/resource-policy: keep`) is deleted by the rollout runbook (spec §8), not by this task.

All Python in this task is run from the repo root of `$WT` and asserts that every anchor matches exactly once (or the stated count), so a drifted file fails loudly instead of silently skipping.

- [ ] **Step 1: Authentik verifies the Postgres server certificate.**

Replace in `$CH/charts/authentik/templates/configmap.yaml`

```yaml
  AUTHENTIK_REDIS__HOST: "{{ .Release.Name }}-redis"
  AUTHENTIK_POSTGRESQL__HOST: "{{ .Release.Name }}-postgres-rw"
  AUTHENTIK_POSTGRESQL__NAME: "authentik"
  AUTHENTIK_POSTGRESQL__USER: "authentik"
```

with

```yaml
  AUTHENTIK_POSTGRESQL__HOST: "{{ .Release.Name }}-postgres-rw"
  AUTHENTIK_POSTGRESQL__NAME: "authentik"
  AUTHENTIK_POSTGRESQL__USER: "authentik"
  # CSR round-11 #4: the connection verifies the server certificate against the CNPG CA that the
  # three containers loading this ConfigMap mount from <release>-postgres-ca.
  AUTHENTIK_POSTGRESQL__SSLMODE: "verify-full"
  AUTHENTIK_POSTGRESQL__SSLROOTCERT: "/etc/postgres-tls/ca.crt"
```

The CA file is the one the API verifies the same host (`<release>-postgres-rw`) with. Authentik passes `sslmode` and `sslrootcert` through to libpq (spec verified assumption 6).

- [ ] **Step 2: Mount the CA in `migrate`, `server` and `worker`; drop the three Redis password entries.**

Run:

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh; cd $WT
python3 - <<'PYEOF'
import os
CH = os.environ["CH"]

def sub(path, old, new, count=1):
    s = open(path).read()
    assert s.count(old) == count, (path, old[:60], s.count(old))
    open(path, "w").write(s.replace(old, new))

REDIS_ENV = '''            # CSR round-2 finding #11: Redis now requires auth (charts/redis's --requirepass) —
            # this must match the password in that Secret exactly.
            - name: AUTHENTIK_REDIS__PASSWORD
              valueFrom:
                secretKeyRef: { name: {{ .Release.Name }}-redis, key: password }
'''
CA_MOUNT = '''            - name: postgres-tls
              mountPath: /etc/postgres-tls
              readOnly: true
'''
CA_VOLUME = '''        - name: postgres-tls
          secret:
            secretName: {{ .Release.Name }}-postgres-ca
            items:
              - key: ca.crt
                path: ca.crt
'''

ds = CH + "/charts/authentik/templates/deployment-server.yaml"
sub(ds, REDIS_ENV, "", count=2)                      # migrate + server
# migrate initContainer: it has no volumeMounts yet; its resources block is followed by `containers:`
sub(ds, '''              memory: {{ .Values.resources.server.limits.memory | quote }}
      containers:''', '''              memory: {{ .Values.resources.server.limits.memory | quote }}
          volumeMounts:
''' + CA_MOUNT + '''      containers:''')
# server container: append to its volumeMounts (the next line is the tls-proxy comment)
sub(ds, '''            - name: blueprints-service-clients
              mountPath: /blueprints/custom/service-clients
              readOnly: true
        # CSR round-10 #5''', '''            - name: blueprints-service-clients
              mountPath: /blueprints/custom/service-clients
              readOnly: true
''' + CA_MOUNT + '''        # CSR round-10 #5''')
# pod volumes: append after the last one
sub(ds, '''        - name: tls-proxy-tmp
          emptyDir: {}
''', '''        - name: tls-proxy-tmp
          emptyDir: {}
''' + CA_VOLUME)
# stale comment naming Redis as a sibling
sub(ds, "# Prometheus/Redis above, where this was independently confirmed safe).",
        "# Prometheus above, where this was independently confirmed safe).")

dw = CH + "/charts/authentik/templates/deployment-worker.yaml"
sub(dw, REDIS_ENV, "")
sub(dw, '''            - name: blueprints-service-clients
              mountPath: /blueprints/custom/service-clients
              readOnly: true
      volumes:''', '''            - name: blueprints-service-clients
              mountPath: /blueprints/custom/service-clients
              readOnly: true
''' + CA_MOUNT + '''      volumes:''')
sub(dw, '''        - name: jwks-relay-www
          emptyDir: {}
''', '''        - name: jwks-relay-www
          emptyDir: {}
''' + CA_VOLUME)
print("deployments ok")
PYEOF
```

Expected: `deployments ok`. Only the worker's main `worker` container gets the mount; its `jwks-relay` sidecar does not load the Authentik ConfigMap. The `jwks-relay` volume list and the server's existing `internal-tls` volume are untouched.

The Secret volume carries the default file mode (0644) and each pod already sets `fsGroup: 1000` with `runAsUser: 1000`, so the Authentik process reads `ca.crt`; the API's identical mount works the same way.

- [ ] **Step 3: Remove the Redis subchart, its NetworkPolicies and its values.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh; cd $WT
git rm -rq "$CH/charts/redis"
python3 - <<'PYEOF'
import os, re
CH = os.environ["CH"]

def sub(path, old, new, count=1):
    s = open(path).read()
    assert s.count(old) == count, (path, old[:60], s.count(old))
    open(path, "w").write(s.replace(old, new))

# Authentik egress: drop the rule to Redis (anchored on its text)
np = CH + "/templates/networkpolicies.yaml"
sub(np, '''    - to: [{ podSelector: { matchLabels: { app: {{ .Release.Name }}-redis } } }]
      ports: [{ protocol: TCP, port: 6379 }]
''', "")
# the two Redis policies are the last objects in the file's `{{- if }}` block: cut from the first
# redis policy's separator to the closing `{{- end }}`
s = open(np).read()
start = s.index("---\napiVersion: networking.k8s.io/v1\nkind: NetworkPolicy\nmetadata:\n  name: {{ .Release.Name }}-redis-ingress")
end = s.index("{{- end }}", s.index("-redis-egress"))
assert s[end:].strip() == "{{- end }}", s[end:]
assert s[start:end].count("kind: NetworkPolicy") == 2
open(np, "w").write(s[:start] + s[end:])

# dependency entry
sub(CH + "/Chart.yaml", '''  - name: redis
    version: "0.1.0"
    repository: "file://charts/redis"
    condition: redis.enabled
''', "")

# `redis:` blocks: a top-level key plus its indented body and trailing blank line
for f in ["values.yaml", "values-aws.yaml", "values-azure.yaml", "values-gcp.yaml",
          "values-laptop.yaml", "values-local.yaml"]:
    p = CH + "/" + f
    s = open(p).read()
    t = re.sub(r'^redis:\n(?:  .*\n)+\n?', '', s, count=1, flags=re.M)
    assert t != s and "\nredis:" not in t and not t.startswith("redis:"), f
    t = t.replace("worker/prometheus/redis/authentik", "worker/prometheus/authentik")
    open(p, "w").write(t)
print("redis removed")
PYEOF
```

Expected: `redis removed`. The `iverson.dnsEgress` and other helpers have no Redis reference; `_validate.tpl` and `_helpers.tpl` need no edit (`grep -rn -i redis "$CH/templates"` is empty after this step, checked in Step 6).

- [ ] **Step 4: Regenerate `Chart.lock`.** `helm dependency update` recomputes the digest from `Chart.yaml`'s dependency list and the resolved versions; all dependencies are `file://`, so it needs no registry (it only prints a repository-index refresh for the user's configured repos).

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh; cd $CH
helm dependency update . 2>&1 | tail -3
git diff --stat Chart.lock
helm dependency build . 2>&1 | tail -2
```

Expected: `Saving 12 charts` and `Deleting outdated charts`; `Chart.lock` loses the `redis` entry and gets a new `digest:` and `generated:`; the `build` succeeds (it refuses a lock whose digest does not match `Chart.yaml`, so success proves the lock is consistent). `charts/redis-0.1.0.tgz` is gitignored build output (`.gitignore:76`, `charts/*.tgz`); `helm dependency update` deletes the stale archive and every later `helm dependency build` yields exactly the 12 remaining ones. Do not stage any `.tgz`.

- [ ] **Step 5: Compose and `stack.py`.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh; cd $WT/Iverson.Server
python3 - <<'PYEOF'
def edit(path, pairs):
    s = open(path).read()
    for old, new, count in pairs:
        assert s.count(old) == count, (path, old[:60], s.count(old))
        s = s.replace(old, new)
    open(path, "w").write(s)

# compose: the redis service, and the env + depends_on entry in each of authentik-migrate/-server/-worker
p = "docker-compose.yml"
s = open(p).read()
blk = s[s.index("  redis:\n    image: redis"):s.index("  # The ONE process that migrates the authentik database.")]
assert blk.count("container_name: iverson-redis") == 1 and blk.count("\n\n") == 1
edit(p, [(blk, "", 1),
         ("      AUTHENTIK_REDIS__HOST: redis\n", "", 3),
         ("      redis:\n        condition: service_healthy\n", "", 3)])

p = "Iverson.LoadTest/scripts/stack.py"
edit(p, [
  ("    query   qdrant, tei-embed, postgres, redis, authentik-server, iverson-api",
   "    query   qdrant, tei-embed, postgres, authentik-server, iverson-api", 1),
  ("errors rather than reporting readiness. Postgres/redis/authentik-server have no readiness",
   "errors rather than reporting readiness. Postgres/authentik-server have no readiness", 1),
  ("(the thing that gates Kestrel binding 8080) only ever touches postgres, not redis or\nauthentik:",
   "(the thing that gates Kestrel binding 8080) only ever touches postgres, not\nauthentik:", 1),
  ("already implies postgres, redis, and authentik-server were all reachable",
   "already implies postgres and authentik-server were both reachable", 1),
  ('"query": ["qdrant", "tei-embed", "postgres", "redis", "authentik-server", "iverson-api"],',
   '"query": ["qdrant", "tei-embed", "postgres", "authentik-server", "iverson-api"],', 1),
  ('    "redis": "iverson-redis",\n', "", 1),
  ("(postgres, redis, authentik-server)", "(postgres, authentik-server)", 1),
])
print("compose + stack.py ok")
PYEOF
```

Expected: `compose + stack.py ok`. The `# ── Identity ──` section header stays (Authentik is still in it). The `redis:` entry is the only compose service removed; `depends_on` of `iverson-api` never named it, and `stack.py`'s `READY_CHECKS` never polled it. The remaining prose in `stack.py` that named Redis (module docstring lines 48-55 and the `READY_CHECKS` comment) is reworded in the same pass; `grep -n -i redis stack.py` must print nothing.

- [ ] **Step 6: The Authentik test fixture stops starting Redis.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh; cd $WT/Iverson.Server/Iverson.Api.Tests/Tenancy
python3 - <<'PYEOF'
def edit(path, pairs):
    s = open(path).read()
    for old, new, count in pairs:
        assert s.count(old) == count, (path, old[:60], s.count(old))
        s = s.replace(old, new)
    open(path, "w").write(s)

edit("AuthentikContainerFixture.cs", [
  ("(Postgres + Redis + the\n/// authentik-migrate/-server/-worker trio,", "(Postgres + the\n/// authentik-migrate/-server/-worker trio,", 1),
  ("    private readonly PostgreSqlContainer _postgres;\n    private readonly IContainer _redis;\n",
   "    private readonly PostgreSqlContainer _postgres;\n", 1),
  ('''        // Network aliases below ("postgres"/"redis") are what every Authentik env var
        // (AUTHENTIK_POSTGRESQL__HOST / AUTHENTIK_REDIS__HOST) points at — matching
        // docker-compose.yml's own service-name-as-hostname convention exactly. WithNetwork(...)
        // is a BUILDER method, unavailable once .Build() has produced the container, so both
        // must be applied here rather than in InitializeAsync.''',
   '''        // The network alias below ("postgres") is what every Authentik env var
        // (AUTHENTIK_POSTGRESQL__HOST) points at — matching docker-compose.yml's own
        // service-name-as-hostname convention exactly. WithNetwork(...) is a BUILDER method,
        // unavailable once .Build() has produced the container, so both must be applied here
        // rather than in InitializeAsync.''', 1),
  ('''            .Build();

        _redis = new ContainerBuilder()
            .WithImage("redis:7.4-alpine")
            .WithCommand("redis-server", "--save", "")
            .WithNetwork(_network)
            .WithNetworkAliases("redis")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "ping"))
            .Build();
''', "            .Build();\n", 1),
  ("await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());", "await _postgres.StartAsync();", 1),
  ("        await SafeDisposeAsync(_redis);\n", "", 1),
  ('            .WithEnvironment("AUTHENTIK_REDIS__HOST", "redis")\n', "", 2),
])
edit("AuthentikRecoveryFlowIntegrationTests.cs", [
  ("(5 containers: network + Postgres + Redis + authentik-\n/// migrate + authentik-server + authentik-worker)",
   "(a network and 4 containers: Postgres + authentik-\n/// migrate + authentik-server + authentik-worker)", 1),
])
print("fixture ok")
PYEOF
cd $WT; command grep -rn --exclude='.env*' --exclude-dir=bin --exclude-dir=obj --exclude-dir=node_modules --exclude-dir=.git -i redis Iverson.Server | command grep -v EngagementStoreDisabled
```

Expected: `fixture ok`, then no output from the final grep (it covers the chart, values, compose, `stack.py` and every test and fixture under `Iverson.Server`). The fixture keeps `using DotNet.Testcontainers.Builders` and `ContainerBuilder`/`Wait` (the migrate and Authentik containers use them). `AuthentikContainerFixture` is consumed only by `AuthentikRecoveryFlowIntegrationTests` (`IClassFixture`); `AuthentikOrchestratorRoleBlueprintTests` only names it in a comment.

Repo-wide dispositions (`command grep -rn --exclude='.env*' -i redis` over the tracked tree, excluding `bin`/`obj`/`node_modules`): no kind script, Terraform file, `.github` workflow, runbook or `setup.sh` names Redis. What remains is historical record and is left as written: `docs/specs/*`, `docs/plans/*`, `docs/criticalreviews/*` and the benchmark gate notes (dated records of past work; several plans quote the old `stack.py query` tier or `up --dry-run … redis …` commands as what they ran then), and `docs/authentik-migration-startup-race.md:124` (a dated investigation of the pre-fix chart that describes compose's old `depends_on: {postgres, redis}`).

- [ ] **Step 7: Verify: render all five profiles and check the objects.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh; cd $CH
helm dependency build . >/dev/null
mkdir -p $SCR/r6
for p in aws azure gcp laptop local; do
  f="-f values-$p.yaml"; [ -f values-$p.ci-override.yaml ] && f="$f -f values-$p.ci-override.yaml"
  helm template iverson . -n iverson $f > $SCR/r6/$p.yaml || echo "RENDER FAIL $p"
  echo "$p: $(helm lint . $f 2>&1 | tail -1)"
done
python3 - <<PYEOF
import yaml
for p in "aws azure gcp laptop local".split():
    path = "$SCR/r6/%s.yaml" % p
    txt = open(path).read()
    assert "redis" not in txt.lower(), p
    docs = [d for d in yaml.safe_load_all(txt) if d]
    cm = next(d for d in docs if d["kind"] == "ConfigMap" and d["metadata"]["name"] == "iverson-authentik-config")["data"]
    assert cm["AUTHENTIK_POSTGRESQL__SSLMODE"] == "verify-full"
    assert cm["AUTHENTIK_POSTGRESQL__SSLROOTCERT"] == "/etc/postgres-tls/ca.crt"
    dep = {d["metadata"]["name"]: d["spec"]["template"]["spec"] for d in docs if d["kind"] == "Deployment"}
    s, w = dep["iverson-authentik-server"], dep["iverson-authentik-worker"]
    ctrs = ([c for c in s["initContainers"] if c["name"] == "migrate"] +
            [c for c in s["containers"] if c["name"] == "server"] +
            [c for c in w["containers"] if c["name"] == "worker"])
    assert len(ctrs) == 3
    for c in ctrs:
        assert any(m["name"] == "postgres-tls" and m["mountPath"] == "/etc/postgres-tls" and m.get("readOnly") for m in c["volumeMounts"]), c["name"]
        assert not any("REDIS" in e["name"] for e in c.get("env", [])), c["name"]
    for pod in (s, w):
        vols = [v for v in pod["volumes"] if v["name"] == "postgres-tls"]
        assert len(vols) == 1 and vols[0]["secret"]["secretName"] == "iverson-postgres-ca"
        assert vols[0]["secret"]["items"] == [{"key": "ca.crt", "path": "ca.crt"}]
    print(p, "ok", len(docs), "objects")
PYEOF
```

Expected: five `N chart(s) linted, 0 chart(s) failed` lines (`1 chart(s) linted, 0 chart(s) failed`) and `aws ok 101 objects`, `azure ok 101 objects`, `gcp ok 102 objects`, `laptop ok 73 objects`, `local ok 95 objects` (the counts are this task's own render with nothing else applied; later tasks change them, so the counts are informational and only the asserts gate).

- [ ] **Step 8: Verify: compose parses and `stack.py query` still plans.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh; cd $WT/Iverson.Server
# scratch copy without container_name (so nothing can collide with the user's stack) and a dummy env file for the `${VAR:?}` guards; the real .env is not read
mkdir -p $SCR/c6
grep -o '\${[A-Z_]*[:?}-]' docker-compose.yml | sed 's/\${//;s/[:?}-]$//' | sort -u | sed 's/$/=x/' > $SCR/c6/dummy.env
grep -v '^\s*container_name:' docker-compose.yml > $SCR/c6/compose.yml
C="docker compose -p csr11c6 --project-directory $WT/Iverson.Server --env-file $SCR/c6/dummy.env -f $SCR/c6/compose.yml"
$C config --services | sort | tr '\n' ' '; echo
echo "redis mentions in resolved config: $($C config | grep -ci redis)"
$C up --dry-run -d --no-deps qdrant tei-embed postgres authentik-server iverson-api 2>&1 | grep -c "Started"
python3 -I -c "import ast;ast.parse(open('Iverson.LoadTest/scripts/stack.py').read());print('stack.py parses')"
python3 - <<PYEOF
import sys, subprocess
sys.path.insert(0, "$WT/Iverson.Server/Iverson.LoadTest/scripts")
import stack
seen = []
class R: returncode = 0
subprocess.run = lambda cmd, **k: (seen.append(cmd), R())[1]
stack.run_compose_up(stack.TIERS["query"])
print(seen[0])
assert "redis" not in seen[0] and "redis" not in stack.CONTAINER and set(stack.CONTAINER) >= set(stack.TIERS["query"])
PYEOF
```

Expected: the service list has no `redis` (`authentik-migrate authentik-server authentik-tls-proxy authentik-worker iverson-api iverson-worker jaeger kafka ollama ollama-init postgres prometheus qdrant starrocks starrocks-init tei-embed zookeeper`); `redis mentions in resolved config: 0`; the dry run starts five services (the grep prints `5`, none of them real containers); `stack.py parses`; the command list is `['docker', 'compose', 'up', '-d', '--no-deps', 'qdrant', 'tei-embed', 'postgres', 'authentik-server', 'iverson-api']`. `stack.py` has no dry-run mode of its own, so its command builder runs under a stubbed `subprocess.run`; the real `--dry-run` is the compose invocation above on the scratch copy (`--dry-run` creates, starts and stops nothing).

- [ ] **Step 9: Verify: Authentik runs without Redis (container-backed).** The fixture now starts only Postgres, migrate, server and worker, so a green run is the live confirmation of spec verified assumption 6.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh; cd $WT/Iverson.Server
flock -o $SCR/heavy.lock -c "systemd-run --user --scope -q -p MemoryMax=6G dotnet build Iverson.Api.Tests -p:UseSharedCompilation=false 2>&1 | tail -3; dotnet build-server shutdown >/dev/null 2>&1"
flock -o $SCR/heavy.lock -c "systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests --no-build --filter 'FullyQualifiedName~AuthentikRecoveryFlowIntegrationTests|FullyQualifiedName~AuthentikOrchestratorRoleBlueprintTests' 2>&1 | tail -6; dotnet build-server shutdown >/dev/null 2>&1"
```

Expected: `0 Error(s)`; then `Passed!  - Failed: 0, Passed: 16, Skipped: 0, Total: 16`.

- [ ] **Step 10: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh; cd $WT
git add "$CH/charts/authentik/templates/configmap.yaml" "$CH/charts/authentik/templates/deployment-server.yaml" "$CH/charts/authentik/templates/deployment-worker.yaml" "$CH/templates/networkpolicies.yaml" "$CH/Chart.yaml" "$CH/Chart.lock" "$CH/values.yaml" "$CH/values-aws.yaml" "$CH/values-azure.yaml" "$CH/values-gcp.yaml" "$CH/values-laptop.yaml" "$CH/values-local.yaml" Iverson.Server/docker-compose.yml Iverson.Server/Iverson.LoadTest/scripts/stack.py Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikContainerFixture.cs Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikRecoveryFlowIntegrationTests.cs
git status --short | grep -v '^[MD] ' || true
git commit -m "verify authentik's postgres connection and remove the unused redis

$TRAILER"
```

Expected: the status filter prints nothing (every change is staged and nothing else is modified); the commit lists 21 changed files (the five `charts/redis` deletions that Step 3's `git rm` staged included).

### Task 7: Network policy (#18, spec §6)

**Files:**
- Modify: `Iverson.Server/deploy/helm/iverson/templates/networkpolicies.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/values.yaml`

**Design notes.**
- **Which className governs which rule.** Each rule follows the Ingress that targets its port: API 8080 by `api.ingress.className`; API 8081 by `api.adminApiIngress.className` (the admin-api Ingress is the only Ingress on 8081); admin-ui 8080 by `adminUi.ingress.className`; Authentik 9080 by `authentik.ingress.className`. The parent sees subchart defaults in `.Values.<subchart>` (checked: `api.adminApiIngress.className` resolves to the subchart default `nginx` on laptop and local, which set none). The 8081 rule keeps its Prometheus peer whatever the class.
- **No empty `from`.** The render already fails on an empty or blank `networkPolicy.clusterCidrs` (top of the file), and every `from` list ends with the `range` over it. Dropping the namespace peer therefore leaves at least the `clusterCidrs` entries (and the Prometheus peer on 8081). The report script in Step 3 asserts a non-empty `from` on every ingress rule it parses.
- **Laptop's admin-ui policy loses the peer.** `values-laptop.yaml` sets `adminUi.enabled: false`, so `adminUi.ingress.className` is `""`; the policy for a pod that does not run stops admitting the namespace. `local` (admin-ui enabled, className `nginx`) is unchanged.
- **Fallback gate placement.** The gate sits beside the other `clusterCidrs` / `apiServerCidrs` refusals inside the `networkPolicy.enabled` block: with policies disabled neither flag renders anything, so there is nothing to acknowledge.
- Task 6 edits the Redis policies in the same file; its regions are disjoint from these and either order applies. Anchor on text, not line numbers.

- [ ] **Step 1: Capture the baseline NetworkPolicy renders (before any edit in this task)**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
cd $CH && helm dependency build . >/dev/null
rm -rf $SCR/np && mkdir -p $SCR/np/before $SCR/np/after
for p in laptop local aws azure gcp; do
  f="-f values-$p.yaml"; [ -f values-$p.ci-override.yaml ] && f="$f -f values-$p.ci-override.yaml"
  helm template iverson . -n iverson $f > $SCR/np/before/$p.yaml; echo "$p rc=$?"
done
```

Expected: five `rc=0`. (These files hold generated Secrets; they stay in `$SCR` and are compared only through the NetworkPolicy documents.)

- [ ] **Step 2: Gate the four ingress-nginx peers, add the fallback acknowledgement**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
cd $CH
python3 -I - <<'PYEOF'
p = "templates/networkpolicies.yaml"
s = open(p).read()
peer = '''        - namespaceSelector:
            matchLabels: { kubernetes.io/metadata.name: ingress-nginx }
'''
assert s.count(peer) == 4
# document order: API 8080, API 8081, admin-ui 8080, Authentik 9080
gates = ["$nginxApi", "$nginxAdminApi", "$nginxAdminUi", "$nginxAuthentik"]
out = []
parts = s.split(peer)
for i, part in enumerate(parts):
    out.append(part)
    if i < 4:
        out.append("        {{- if " + gates[i] + " }}\n" + peer + "        {{- end }}\n")
s = "".join(out)

old = '''# Default-deny: nothing in the namespace can talk to anything else unless a
'''
new = '''{{- /*
CSR round-11 #18: the ingress-nginx namespace is admitted only where the Ingress fronting the
port is served by ingress-nginx (className "nginx"); every other class reaches the pod from
clusterCidrs alone. Each rule is gated on the className of the Ingress that targets its port:
API 8080 by api.ingress, API 8081 by api.adminApiIngress (Prometheus stays admitted there
whatever the class), admin-ui 8080 by adminUi.ingress, Authentik 9080 by authentik.ingress. A
rule's `from` list never becomes empty: clusterCidrs is non-empty (refused above otherwise), so
dropping the namespace peer narrows the rule instead of turning it into an allow-all.
*/}}
{{- $nginxApi := eq (dig "ingress" "className" "" (default dict .Values.api)) "nginx" }}
{{- $nginxAdminApi := eq (dig "adminApiIngress" "className" "" (default dict .Values.api)) "nginx" }}
{{- $nginxAdminUi := eq (dig "ingress" "className" "" (default dict .Values.adminUi)) "nginx" }}
{{- $nginxAuthentik := eq (dig "ingress" "className" "" (default dict .Values.authentik)) "nginx" }}
# Default-deny: nothing in the namespace can talk to anything else unless a
'''
assert s.count(old) == 1
s = s.replace(old, new)

old = '''{{- range .Values.networkPolicy.clusterCidrs }}
{{- if not (trim (toString .)) }}
{{- fail "networkPolicy.clusterCidrs contains a blank entry;'''
assert s.count(old) == 1
gate = '''{{- if or .Values.networkPolicy.dnsAnyDestination .Values.networkPolicy.apiServerAnyDestination }}
{{- if not .Values.networkPolicy.keepFallbacks }}
{{- fail "networkPolicy.dnsAnyDestination / apiServerAnyDestination render port-only egress rules that admit any destination. They are first-deploy fallbacks: replace them with the scoped rules, or set networkPolicy.keepFallbacks: true to record that the fallback is kept on purpose (docs/runbooks/csr10-infrastructure-cutover.md section 4)." }}
{{- end }}
{{- end }}
'''
s = s.replace(old, gate + old)
open(p, "w").write(s)

p = "values.yaml"
s = open(p).read()
old = '''  apiServerAnyDestination: false
'''
assert s.count(old) == 1
s = s.replace(old, old + '''  # Acknowledges a kept fallback: with dnsAnyDestination or apiServerAnyDestination true the render
  # fails unless this is true as well, so a fallback applied for the first deploy is not left in
  # place unnoticed (docs/runbooks/csr10-infrastructure-cutover.md section 4).
  keepFallbacks: false
''')
open(p, "w").write(s)
PYEOF
git diff --stat -- templates/networkpolicies.yaml values.yaml
```

Expected: `networkpolicies.yaml` 26 insertions, `values.yaml` 4 insertions.

- [ ] **Step 3: Write the comparison script**

Create `$SCR/np_report.py` (not in the repo):

```python
import sys
import yaml

base, prof = sys.argv[1], sys.argv[2]


def load(path):
    return {
        d["metadata"]["name"]: d
        for d in yaml.safe_load_all(open(path))
        if d and d.get("kind") == "NetworkPolicy"
    }


before = load(f"{base}/before/{prof}.yaml")
after = load(f"{base}/after/{prof}.yaml")
assert sorted(before) == sorted(after), "policy set changed"
print(f"== {prof}: {len(after)} NetworkPolicies, same names before/after")

for name in sorted(after):
    for rule in after[name]["spec"].get("ingress", []):
        frm = rule.get("from")
        assert frm, f"empty or missing from in {name}"
        ns = [x for x in frm if x.get("namespaceSelector")]
        prom = [x for x in frm if "prometheus" in str(x)]
        if ns or prom:
            ports = [x["port"] for x in rule["ports"]]
            print(f"   {name} {ports}: ingress-nginx peer={'yes' if ns else 'NO'} "
                  f"prometheus peer={'yes' if prom else 'no'} from-entries={len(frm)}")

changed = [n for n in after if after[n] != before[n]]
print("   changed policies:", changed)
for n in changed:
    for b, a in zip(before[n]["spec"]["ingress"], after[n]["spec"]["ingress"]):
        if b != a:
            removed = [e for e in b["from"] if e not in a["from"]]
            added = [e for e in a["from"] if e not in b["from"]]
            assert all("ingress-nginx" in str(e) for e in removed) and not added, (n, removed, added)
            print(f"     {n} {[x['port'] for x in a['ports']]}: removed ingress-nginx namespace peer only")
    assert before[n]["spec"].get("egress") == after[n]["spec"].get("egress"), f"egress changed in {n}"
```

- [ ] **Step 4: Render all five profiles and report which rules carry the peer**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
cd $CH && helm dependency build . >/dev/null
for p in laptop local aws azure gcp; do
  f="-f values-$p.yaml"; [ -f values-$p.ci-override.yaml ] && f="$f -f values-$p.ci-override.yaml"
  helm template iverson . -n iverson $f > $SCR/np/after/$p.yaml; echo "$p render rc=$?"
  helm lint . $f 2>&1 | command grep "chart(s) linted"
  python3 -I $SCR/np_report.py $SCR/np $p
done
```

Expected, per profile (the `lint` line is `1 chart(s) linted, 0 chart(s) failed` each time):

```
== laptop: 23 NetworkPolicies, same names before/after
   iverson-api-ingress [8080]: ingress-nginx peer=yes prometheus peer=no from-entries=2
   iverson-api-ingress [8081]: ingress-nginx peer=yes prometheus peer=yes from-entries=3
   iverson-authentik-ingress [9080]: ingress-nginx peer=yes prometheus peer=no from-entries=2
   iverson-worker-ingress [8081]: ingress-nginx peer=NO prometheus peer=yes from-entries=1
   changed policies: ['iverson-admin-ui-ingress']
     iverson-admin-ui-ingress [8080]: removed ingress-nginx namespace peer only
== local: 27 NetworkPolicies, same names before/after
   iverson-admin-ui-ingress [8080]: ingress-nginx peer=yes ...
   iverson-api-ingress [8080] / [8081] / iverson-authentik-ingress [9080]: ingress-nginx peer=yes
   changed policies: []
== aws, azure, gcp: 27 NetworkPolicies each
   iverson-api-ingress [8081]: ingress-nginx peer=NO prometheus peer=yes (from-entries=3 on aws and gcp, 2 on azure)
   iverson-worker-ingress [8081]: ingress-nginx peer=NO prometheus peer=yes from-entries=1
   changed policies: ['iverson-api-ingress', 'iverson-admin-ui-ingress', 'iverson-authentik-ingress']
     iverson-api-ingress [8080], iverson-api-ingress [8081], iverson-admin-ui-ingress [8080], iverson-authentik-ingress [9080]: removed ingress-nginx namespace peer only
```

(The script prints one line per rule; the lines above are the same facts, abbreviated for local and the three cloud profiles.) The assertions inside the script fail the step if any `from` list is empty, if a change adds a peer or removes anything but the namespace peer, or if an egress section differs.

- [ ] **Step 5: Prove the fallback gate both ways**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
cd $CH
for flag in dnsAnyDestination apiServerAnyDestination; do
  echo "== $flag=true"; helm template iverson . -n iverson -f values-laptop.yaml --set networkPolicy.$flag=true 2>&1 >/dev/null | head -c 170; echo " rc=${PIPESTATUS[0]}"
  echo "== $flag=true keepFallbacks=true: any-destination egress rules rendered:"
  helm template iverson . -n iverson -f values-laptop.yaml --set networkPolicy.$flag=true --set networkPolicy.keepFallbacks=true | command grep -c "to: \[\]"
done
echo "== keepFallbacks alone"; helm template iverson . -n iverson -f values-laptop.yaml --set networkPolicy.keepFallbacks=true >/dev/null; echo "rc=$?"
echo "== networkPolicy disabled with a fallback set"; helm template iverson . -n iverson -f values-laptop.yaml --set networkPolicy.enabled=false --set networkPolicy.dnsAnyDestination=true >/dev/null; echo "rc=$?"
```

Expected: both unacknowledged cases print `Error: execution error at (iverson/templates/networkpolicies.yaml:16:4): networkPolicy.dnsAnyDestination / apiServerAnyDestination render port-only egress rules` and `rc=1`; the acknowledged cases print a positive count (9 for DNS, 3 for the API server); the last two print `rc=0`.

- [ ] **Step 6: Commit**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
cd $WT
git add Iverson.Server/deploy/helm/iverson/templates/networkpolicies.yaml Iverson.Server/deploy/helm/iverson/values.yaml
git commit -m "admit ingress-nginx only for nginx-class ingresses and require keepFallbacks

$TRAILER"
```

### Task 8: Runbook and docs

**Files:**
- Create: `docs/runbooks/csr11-transport-network.md`
- Modify: `docs/runbooks/csr10-infrastructure-cutover.md` (§4 fallbacks), `docs/runbooks/chunk-collection-cleanup-2026-07.md` (step 3)

**Interfaces:**
- Consumes the names the earlier tasks introduced:
  - from Tasks 1, 3 and 4: `api.ingress.appgwTrustedRootCertificate`, `authentik.ingress.appgwTrustedRootCertificate` and the Terraform variable `appgw_backend_trusted_roots`;
  - from Tasks 1 and 5: the Secrets `<release>-api-internal-tls` and `<release>-starrocks-tls`;
  - from Task 7: `networkPolicy.keepFallbacks`.
- Task 4's guard message points at this runbook.

- [ ] **Step 1: Create the runbook** with exactly this content:

````markdown
# CSR Round-11 Transport and Network — Operator Runbook

**Why:** the hops that carried passwords, tokens and tenant data in plaintext inside the cluster are
now encrypted. The edge reaches the API and Authentik's public listener over TLS, StarRocks clients
verify the FE's certificate, and Authentik verifies Postgres's. Redis is gone (Authentik no longer
uses it). GCP no longer serves HTTP on port 80, and Azure redirects it. The `ingress-nginx`
NetworkPolicy peer exists only where an nginx Ingress fronts the pod, and an any-destination
fallback needs an explicit acknowledgement. Compose is unchanged apart from Redis.

## 1. What the upgrade does

One `helm upgrade` switches each hop. The API pods and the Authentik server pod restart with TLS
in the same release as the Ingress annotations, so a request in flight during the rollout can fail
once. The StarRocks FE pods restart with a keystore and a new `fe.conf`. Until they are back, the
create-user Job, the API and the worker treat the still-plaintext FE as not ready and retry. This
clears within the upgrade's timeout.

After the upgrade, delete the orphaned Redis Secret. Its `resource-policy: keep` stops Helm from
deleting it:

```bash
kubectl -n <ns> delete secret <release>-redis
```

## 2. Azure: trusted roots (two phases)

The Application Gateway uses HTTPS to a pod only if it holds the CA that signed the pod's
certificate. The chart generates those CAs, so Azure takes two phases. Until the second phase,
the Azure edge reaches the API and Authentik in plaintext, and the chart refuses an upgrade that
skips it.

1. `helm install` (or the first `helm upgrade` carrying this change). This creates
   `<release>-api-internal-tls`.
2. Copy both CAs into the Terraform variable `appgw_backend_trusted_roots` (azure root), and apply:

   ```bash
   kubectl -n <ns> get secret <release>-api-internal-tls -o jsonpath='{.data.ca\.crt}' | base64 -d > api-ca.pem
   kubectl -n <ns> get secret <release>-authentik-internal-tls -o jsonpath='{.data.ca\.crt}' | base64 -d > authentik-ca.pem
   ```

   A `.tfvars` file cannot call `file()`, so write the map as JSON beside the azure root, then
   run `terraform apply` there:

   ```bash
   python3 -c 'import json; json.dump({"appgw_backend_trusted_roots": {"iverson-api-ca": open("api-ca.pem").read(), "iverson-authentik-ca": open("authentik-ca.pem").read()}}, open("trusted-roots.auto.tfvars.json", "w"), indent=2)'
   ```
3. Set `api.ingress.appgwTrustedRootCertificate: "iverson-api-ca"` and
   `authentik.ingress.appgwTrustedRootCertificate: "iverson-authentik-ca"` in `values-azure.yaml`,
   then `helm upgrade`. Then restart Prometheus, which reads its scrape config only at start and
   would otherwise keep scraping the API over plain HTTP:

   ```bash
   kubectl -n <ns> rollout restart deployment/<release>-prometheus
   ```

## 3. Rotating a chart certificate

Each chart CA's key is discarded after it signs its one certificate. To rotate, delete the Secret,
upgrade, then restart its pods:

| Secret | Restart after the upgrade |
|---|---|
| `<release>-api-internal-tls` | the API Deployment. Prometheus picks up the new CA file without a restart. |
| `<release>-authentik-internal-tls` | the Authentik server Deployment and the API Deployment |
| `<release>-starrocks-tls` | the FE pods while `helm upgrade` is still waiting on its create-user hook (`kubectl -n <ns> delete pod -l app.starrocks.ownerreference/name=<release>-starrocks-fe` once the upgrade has recreated the Secret). The hook waits until FE serves the new certificate, so the upgrade completes only after the FE pods restart; raise `--timeout` if they need more than the default 5 minutes. Then restart the API and worker Deployments. |

On Azure the gateway trusts only the CA in `appgw_backend_trusted_roots`. The new CA exists only
after the upgrade has regenerated the Secret. So, after the upgrade, copy its new `ca.crt` over the
matching PEM, as in §2 step 2, apply, and then restart the pods. The Azure edge to that pod fails
from the apply until the restarted pods are Ready.

The StarRocks operator does not restart the FE when only its `fe.conf` ConfigMap changes. After any
later edit to `charts/starrocks/templates/configmap-fe.yaml`, delete the FE pods as above.

## 4. Any-destination fallbacks

`networkPolicy.dnsAnyDestination` and `networkPolicy.apiServerAnyDestination` now render only
together with `networkPolicy.keepFallbacks: true`. Setting it records that the profile keeps the
fallback open, as `docs/runbooks/csr10-infrastructure-cutover.md` §4 requires. Remove all three
once the narrower rule works.

## 5. Reaching the API from a workstation

The API's ports 8080 (gRPC) and 8081 (admin, health, metrics) now serve only TLS, from a
certificate named `<release>-api`. Through a port-forward, verify that name:

```bash
kubectl -n <ns> port-forward svc/<release>-api 8081:8081 &
kubectl -n <ns> get secret <release>-api-internal-tls -o jsonpath='{.data.ca\.crt}' | base64 -d > api-ca.pem
curl --cacert api-ca.pem --connect-to <release>-api:8081:127.0.0.1:8081 https://<release>-api:8081/health
```

On Azure before phase 3 of §2, the API still serves plain HTTP.
````

- [ ] **Step 2: The cutover runbook's fallbacks.** In `docs/runbooks/csr10-infrastructure-cutover.md`, replace:

```markdown
2. If still blocked, set `networkPolicy.apiServerAnyDestination: true`.
3. For DNS failures, set `networkPolicy.dnsAnyDestination: true`.

A profile that keeps a fallback leaves that half of CSR round-10 finding #17 open there; record it.
```

with:

```markdown
2. If still blocked, set `networkPolicy.apiServerAnyDestination: true`.
3. For DNS failures, set `networkPolicy.dnsAnyDestination: true`.

Either fallback renders only with `networkPolicy.keepFallbacks: true` beside it (CSR round-11 #18).
A profile that keeps a fallback leaves that half of CSR round-10 finding #17 open there; record it.
```

- [ ] **Step 3: The chunk-cleanup runbook's API calls.** In `docs/runbooks/chunk-collection-cleanup-2026-07.md`, replace:

````markdown
   ```
   kubectl -n <ns> port-forward svc/<release>-api 8080:8080 &
   curl --http2-prior-knowledge -X POST http://localhost:8080/admin/reconcile/{TypeName}
   ```
   or by exec'ing into a running API pod:
   ```
   kubectl -n <ns> exec deploy/<release>-api -- curl --http2-prior-knowledge -X POST http://localhost:8080/admin/reconcile/{TypeName}
   ```
````

with:

````markdown
   ```
   kubectl -n <ns> port-forward svc/<release>-api 8080:8080 &
   kubectl -n <ns> get secret <release>-api-internal-tls -o jsonpath='{.data.ca\.crt}' | base64 -d > api-ca.pem
   curl --http2 --cacert api-ca.pem --connect-to <release>-api:8080:127.0.0.1:8080 -X POST https://<release>-api:8080/admin/reconcile/{TypeName}
   ```
   The API serves only TLS since CSR round 11, and `/admin/*` is on the HTTP/2-only 8080 listener,
   so the request uses `--http2` and verifies the certificate name `<release>-api`
   (`docs/runbooks/csr11-transport-network.md` §5). The API image has no `curl`, so the request runs
   from the workstation, not inside the pod.
````

- [ ] **Step 4: Check the docs.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh; cd $WT
command grep -n 'http2-prior-knowledge\|exec deploy/<release>-api' docs/runbooks/chunk-collection-cleanup-2026-07.md; echo "h2c left: $?"
command grep -c 'keepFallbacks' docs/runbooks/csr10-infrastructure-cutover.md docs/runbooks/csr11-transport-network.md
for k in appgwTrustedRootCertificate appgw_backend_trusted_roots api-internal-tls starrocks-tls keepFallbacks; do
  printf '%s: runbook %s, code %s\n' $k "$(command grep -c -- "$k" docs/runbooks/csr11-transport-network.md)" "$(command grep -rl --exclude='.env*' -- "$k" Iverson.Server/deploy | wc -l)"; done
```

Expected:
- `h2c left: 1`: no h2c or in-pod `curl` remains.
- Both files count `keepFallbacks` at least once.
- Every name has a runbook count of at least 1 and a code count of at least 1, so the runbook names only what the chart and Terraform define.

- [ ] **Step 5: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh; cd $WT
git add -f docs/runbooks/csr11-transport-network.md docs/runbooks/csr10-infrastructure-cutover.md docs/runbooks/chunk-collection-cleanup-2026-07.md
git commit -m "document the transport and network change for operators

$TRAILER"
```

### Task 9: Verification

No commits. The live part is heavy: run one heavy process at a time, and check `free -g` first. The compose and kind checks run one after the other, never together.

- [ ] **Step 1: Full suites.** These run one at a time. The container-backed classes are inside them, including the Authentik fixture, which now starts no Redis.

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-transport-network/Iverson.Server
for p in Iverson.Api.Tests Iverson.ClientConformance.Tests Iverson.Embeddings.Tests Iverson.Events.Tests Iverson.LoadTest.Tests Iverson.Patterns.Tests Iverson.Sql.Tests Iverson.StarRocks.Tests Iverson.Vector.Tests; do
  echo "$p: $(systemd-run --user --scope -q -p MemoryMax=7G dotnet test $p/$p.csproj -p:UseSharedCompilation=false 2>&1 | command grep -E '^(Passed|Failed)!' | tail -1)"; done
cd ../Iverson.Clients/DotNet && echo "Client.Core.Tests: $(systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj -p:UseSharedCompilation=false 2>&1 | command grep -E '^(Passed|Failed)!' | tail -1)"
dotnet build-server shutdown >/dev/null 2>&1
```

Expected: every line reads `Passed!` with 0 failed.

- [ ] **Step 2: Re-run the tasks' checks on the final branch.** The plan-time combined replay ran Tasks 1–8 in order on one tree, then re-ran every check on the final tree. Run these steps' bash blocks again, unchanged. Each must print what its own task's step expects:
  - Task 4 Steps 2, 4 and 5;
  - Task 5 Steps 1, 8 and 9;
  - Task 6 Steps 7 and 8;
  - Task 7 Steps 4 and 5;
  - Task 8 Step 4;
  - Task 1 Step 11, Task 2 Step 6 and Task 3 Step 8 (the Kestrel, Prometheus and nginx container probes).

  **Do not re-run** the steps below.
  - **Edit steps (Task 4 Step 3, Task 7 Step 2):** their asserts still pass after the edit, so a second run duplicates the guard and gates and still renders.
  - **Baseline captures (Task 1 Step 1, Task 7 Step 1):** they overwrite the baselines the checks compare against.
  - **Per-task diffs (Task 1 Step 10, Task 2 Step 5, Task 3 Step 7):** their object-diff lines compare against earlier-task baselines. Step 3's final renders replace them.

- [ ] **Step 3: Final renders.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh; mkdir -p $SCR/final
cd $CH && helm dependency build . >/dev/null
for v in values-local values-laptop values-aws values-azure values-gcp; do o="-f $v.yaml"; [ -f $v.ci-override.yaml ] && o="$o -f $v.ci-override.yaml"
  helm template iverson . -n iverson $o > $SCR/final/$v.yaml; e=$?
  echo "$v template=$e lint=$(helm lint . $o 2>&1 | tail -1) | $(kubeconform -kubernetes-version 1.30.0 -summary -ignore-missing-schemas < $SCR/final/$v.yaml 2>&1 | tail -1)"; done
python3 - $SCR/final <<'PY'
import sys, yaml
for v in ("values-local", "values-laptop", "values-aws", "values-azure", "values-gcp"):
    text = open(f"{sys.argv[1]}/{v}.yaml").read()
    docs = [d for d in yaml.safe_load_all(text) if d]
    redis = [f"{d['kind']}/{d['metadata']['name']}" for d in docs if "redis" in d["metadata"]["name"].lower()] + (["env"] if "REDIS" in text else [])
    secrets = {d["metadata"]["name"] for d in docs if d["kind"] == "Secret"}
    tls = sorted({t["secretName"] for d in docs if d["kind"] == "Ingress" for t in d["spec"].get("tls", [])})
    print(v, "redis:", redis or "none", "| chart TLS secrets:", sorted(s for s in secrets if s.endswith(("-internal-tls", "-starrocks-tls"))), "| ingress secrets:", tls, "| clash:", sorted(secrets & set(tls)) or "none")
PY
git -C $WT log --format='%h %s | %(trailers:key=Co-Authored-By,valueonly,separator=)' main..HEAD
```

Expected:
- Every profile reads `template=0 lint=1 chart(s) linted, 0 chart(s) failed`, with kubeconform `Invalid: 0, Errors: 0`. At plan time the Valid counts were 92/69/98/98/98 for local/laptop/aws/azure/gcp.
- Every profile prints `redis: none` and `clash: none`.
- The chart TLS secrets are:
  - laptop: `iverson-api-internal-tls` and `iverson-authentik-internal-tls`;
  - every other profile: those two plus `iverson-starrocks-tls`.
- Eight commits (Tasks 1–8), each with the `Claude Opus 5.5 (1M context)` trailer.

- [ ] **Step 4: Record the user's state, and write the live driver.**
  - Check the user's stack is down: `docker ps --format '{{.Names}}'`. If the list isn't empty, **stop and ask the user**. Never stop their containers yourself: compose binds host port 9000 and kind binds 8080 and 8443.
  - Record their state:

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh; mkdir -p $LIVE
docker ps -a --format '{{.Names}}' | sort > $LIVE/before-containers.txt
docker volume ls -q | sort > $LIVE/before-volumes.txt
docker network ls --format '{{.Name}}' | sort > $LIVE/before-networks.txt
docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort > $LIVE/before-images.txt
```

  - Write `$LIVE/flow.py`, the flow-executor driver both live checks use, with exactly this content. It is sub-project B's driver unchanged: a browser-shaped client that follows each 302 by GETting the same executor URL.

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

- [ ] **Step 5: Compose live (isolated project `csr11c`, Authentik services only).** This uses round 11's pattern: a separate project name, `container_name:` stripped, `--project-directory` on the worktree, and the worktree's own generated `.env`, which `generate-compose-secrets.sh` writes into the worktree, never into the main checkout's `Iverson.Server/.env`.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
cat >> $SCR/env.sh <<'X'
ENVF=$WT/Iverson.Server/.env
DC="docker compose -p csr11c -f $LIVE/compose.yml --project-directory $WT/Iverson.Server --env-file $ENVF"
val() { command grep "^$1=" "$ENVF" | cut -d= -f2-; }
X
source $SCR/env.sh; umask 077
bash $WT/scripts/generate-compose-secrets.sh >/dev/null
command grep -v container_name $WT/Iverson.Server/docker-compose.yml > $LIVE/compose.yml
echo "redis in compose: $(command grep -c -i redis $LIVE/compose.yml)"
free -g | sed -n 2p
$DC up -d authentik-server authentik-worker > $LIVE/compose-up.log 2>&1; echo "up exit=$?"
for i in $(seq 1 60); do [ "$(docker inspect -f '{{.State.Health.Status}}' csr11c-authentik-server-1 2>/dev/null)" = healthy ] && break; sleep 10; done; echo "server: $(docker inspect -f '{{.State.Health.Status}}' csr11c-authentik-server-1)"
echo "worker: $(docker inspect -f '{{.State.Status}}' csr11c-authentik-worker-1)"
docker ps --format '{{.Names}}' | command grep '^csr11c-' | sort | tr '\n' ' '; echo
for i in $(seq 1 30); do curl -s -H "Authorization: Bearer $(val AUTHENTIK_BOOTSTRAP_TOKEN)" "http://127.0.0.1:9000/api/v3/flows/instances/?slug=default-authentication-flow" | python3 -c 'import sys,json; sys.exit(0 if json.load(sys.stdin)["pagination"]["count"]==1 else 1)' 2>/dev/null && break; sleep 10; done; echo "login flow present: $?"
cd $LIVE && TOKEN="$(val AUTHENTIK_BOOTSTRAP_TOKEN)" python3 - <<'PY'
import os, requests, flow
B = "http://127.0.0.1:9000"; H = {"Authorization": f"Bearer {os.environ['TOKEN']}"}
pk = requests.post(f"{B}/api/v3/core/users/", json={"username": "csr11c-user", "name": "csr11c-user"}, headers=H).json()["pk"]
requests.post(f"{B}/api/v3/core/users/{pk}/set_password/", json={"password": "Correct-Horse-Battery-9"}, headers=H).raise_for_status()
print("C1 login:", flow.login(B, "csr11c-user", "Correct-Horse-Battery-9")[0])
PY
$DC up --dry-run -d --no-deps qdrant tei-embed postgres authentik-server iverson-api > $LIVE/stackpy-dryrun.log 2>&1; echo "stack.py query dry-run exit=$?"
```

Expected:
- `redis in compose: 0`.
- `up exit=0`, `server: healthy`, `worker: running`.
- The `csr11c-` list holds the running Postgres, Authentik server and worker containers and no Redis (the migrate container has exited 0, and `docker ps` lists only running containers).
- `login flow present: 0`.
- `C1 login: xak-flow-redirect`: a login completes with no Redis running.
- `stack.py query dry-run exit=0`: the command `stack.py query` runs (Task 6 Step 5) is accepted by compose.

Tear the compose project down before kind: `$DC down -v > $LIVE/compose-down.log 2>&1; echo "down exit=$?"`. Expected: `down exit=0`. Then delete the worktree's generated `.env`, which is gitignored: `rm -f $WT/Iverson.Server/.env`.

- [ ] **Step 6: Kind: fresh install of the branch chart.** No install exists to upgrade, so this is a fresh install of the laptop profile. Ollama is off, because `CreateTenant` needs no model. Run the slow steps with `run_in_background`.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
cd $WT; free -g | sed -n 2p
KIND_EXPERIMENTAL_PROVIDER=podman kind create cluster --name iverson --config Iverson.Server/deploy/kind/kind-config.yaml
bash Iverson.Server/deploy/kind/setup.sh
TMPDIR=/var/tmp bash Iverson.Server/deploy/kind/build-and-load-image.sh csr11c iverson
python3 -c "import yaml; d=yaml.safe_load(open('Iverson.Server/deploy/helm/iverson/values-laptop.yaml')); d.setdefault('ollama',{})['enabled']=False; yaml.safe_dump(d, open('$LIVE/laptop-no-ollama.yaml','w'))"
TMPDIR=/var/tmp bash Iverson.Server/deploy/kind/build-and-load-image.sh csr11c iverson --model-images --values $LIVE/laptop-no-ollama.yaml
cd $CH && helm dependency build . >/dev/null
helm upgrade --install iverson . -n iverson --create-namespace -f values-laptop.yaml --set ollama.enabled=false \
  --set api.hpa.maxReplicas=1 --set api.image.tag=csr11c --set worker.image.tag=csr11c --wait --timeout 30m
kubectl -n iverson get pods --no-headers | awk '$3!="Running" && $3!="Completed"{print "NOT READY:", $0}'
ok=no; for i in $(seq 1 30); do [ "$(kubectl -n iverson exec deploy/iverson-authentik-worker -c worker -- ak shell -c "from authentik.flows.models import Flow; from authentik.stages.authenticator_totp.models import AuthenticatorTOTPStage; print(Flow.objects.filter(slug='default-authentication-flow').count(), AuthenticatorTOTPStage.objects.filter(name='default-authenticator-totp-setup').count(), Flow.objects.filter(slug='initial-setup').count())" 2>/dev/null | tail -1)" = "1 1 1" ] && { ok=yes; break; }; sleep 10; done; echo "stock objects present: $ok"
kubectl -n iverson exec deploy/iverson-authentik-worker -c worker -- ak apply_blueprint custom/recovery-flow.yaml custom/reputation.yaml > $LIVE/apply1.log 2>&1; echo "apply static exit=$?"
kubectl -n iverson exec deploy/iverson-authentik-worker -c worker -- ak apply_blueprint custom/superusers.yaml custom/orchestrator-federation.yaml custom/service-clients/service-clients.yaml custom/service-clients/admin-automation.yaml > $LIVE/apply2.log 2>&1; echo "apply templated exit=$?"
```

Expected:
- No `NOT READY` lines. Every probe, including the API's and the tls-proxy's HTTPS probes, is passing.
- `stock objects present: yes`.
- `apply static exit=0` and `apply templated exit=0`.

These follow `docs/runbooks/csr11-identity-plane.md` §2.

- [ ] **Step 7: Kind live checks.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
echo "K1 redis objects: $(kubectl -n iverson get all,secret,networkpolicy,configmap -o name | command grep -c -i redis)"
kubectl -n iverson port-forward svc/iverson-api 18080:8080 18081:8081 > $LIVE/pf-api.log 2>&1 & echo $! > $LIVE/pf-api.pid
kubectl -n iverson port-forward svc/iverson-authentik 19000:9000 > $LIVE/pf-ak.log 2>&1 & echo $! > $LIVE/pf-ak.pid
sleep 3
kubectl -n iverson get secret iverson-api-internal-tls -o jsonpath='{.data.ca\.crt}' | base64 -d > $LIVE/api-ca.pem
echo "K2 8081 https: $(curl -s -o /dev/null -w '%{http_code}' --cacert $LIVE/api-ca.pem --connect-to iverson-api:18081:127.0.0.1:18081 https://iverson-api:18081/health)"
echo "K2 8081 plain: $(curl -s -o /dev/null -w '%{http_code}' --max-time 5 http://127.0.0.1:18081/health)"
echo "K2 8080 h2c:   $(curl -s -o /dev/null -w '%{http_code}' --max-time 5 --http2-prior-knowledge http://127.0.0.1:18080/)"
echo "K2 8080 h2:    $(curl -s -o /dev/null -w '%{http_version}' --http2 --cacert $LIVE/api-ca.pem --connect-to iverson-api:18080:127.0.0.1:18080 https://iverson-api:18080/)"
echo "K3 9080 plain: $(kubectl -n iverson exec deploy/iverson-authentik-server -c tls-proxy -- curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:9080/-/health/live/)"
echo "K3 9080 tls:   $(kubectl -n iverson exec deploy/iverson-authentik-server -c tls-proxy -- curl -sk -o /dev/null -w '%{http_code}' https://127.0.0.1:9080/-/health/live/)"
kubectl -n iverson exec deploy/iverson-authentik-worker -c worker -- ak shell -c "
from authentik.core.models import User
u = User.objects.create(username='csr11c-plain', name='csr11c-plain'); u.set_password('Correct-Horse-Battery-9'); u.save(); print('created')" 2>/dev/null | tail -1
cd $LIVE && python3 -c "
import flow; print('K4 public login:', flow.login('http://127.0.0.1:8080', 'csr11c-plain', 'Correct-Horse-Battery-9', host='authentik.iverson.local')[0])"
echo "K4 ingress upstream: $(kubectl -n ingress-nginx logs deploy/ingress-nginx-controller --tail=200 | command grep 'flows/executor/default-authentication-flow' | tail -1 | command grep -o -E '[0-9.]+:9080' | head -1)"
echo "K5 pg_stat_ssl: $(kubectl -n iverson exec iverson-postgres-1 -c postgres -- psql -d postgres -tAc "select a.usename || '|' || bool_and(s.ssl) || '|' || count(*) from pg_stat_ssl s join pg_stat_activity a using (pid) where a.usename = 'authentik' group by a.usename")"
```

Expected:
- `K1 redis objects: 0`.
- `K2 8081 https: 200`, verified against the chart CA as `iverson-api`. `K2 8081 plain: 000` and `K2 8080 h2c: 000`: plaintext gets no response. `K2 8080 h2: 2`: the gRPC port negotiates HTTP/2 over TLS.
- `K3 9080 plain: 400` and `K3 9080 tls: 200`.
- `created`, then `K4 public login: xak-flow-redirect`: a login through ingress-nginx crosses the 9080 TLS hop. `K4 ingress upstream` shows a pod address on port 9080.
- `K5 pg_stat_ssl: authentik|true|N` with N ≥ 1: every Authentik session to Postgres uses SSL.

- [ ] **Step 8: Kind: `CreateTenant` through the GRPCS edge.** The admin-automation token is minted the way `docs/runbooks/csr11-identity-plane.md` §4 does. The console calls `CreateTenant` through ingress-nginx (`https://iverson.local`, connected to `127.0.0.1:8443`, pinning the kind edge certificate). So the request crosses the client → edge and edge → API (GRPCS) hops.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
mkdir -p $LIVE/createtenant && cd $LIVE/createtenant
cat > createtenant.csproj <<'X'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="/home/ben/repositories/Iverson/.worktrees/csr11-transport-network/Iverson.Clients/DotNet/Iverson.Client.Core/Iverson.Client.Core.csproj" /></ItemGroup></Project>
X
cat > Program.cs <<'X'
// args: tenantId ; env TOKEN (admin-automation access token), EDGE_CERT (PEM file of the kind edge
// certificate, Secret iverson-api-tls). Calls CreateTenant through ingress-nginx (https://iverson.local,
// connected to 127.0.0.1:8443), so the request crosses the GRPCS edge → API hop. Prints only the
// recovery link's host and path.
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Net.Client;
using Iverson.Client.Contracts;

var edgeCert = X509Certificate2.CreateFromPem(File.ReadAllText(Environment.GetEnvironmentVariable("EDGE_CERT")!));
var handler = new SocketsHttpHandler
{
    ConnectCallback = async (_, ct) =>
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync("127.0.0.1", 8443, ct);
        return new NetworkStream(socket, ownsSocket: true);
    },
    SslOptions = new SslClientAuthenticationOptions
    {
        RemoteCertificateValidationCallback = (_, cert, _, _) =>
            cert is not null && cert.GetCertHashString() == edgeCert.GetCertHashString(),
    },
};
using var channel = GrpcChannel.ForAddress("https://iverson.local", new GrpcChannelOptions { HttpHandler = handler });
var client = new TenantLifecycleGrpcService.TenantLifecycleGrpcServiceClient(channel);
var tenant = await client.CreateTenantAsync(new CreateTenantRequest { TenantId = args[0], DisplayName = "CSR11 C live check",
    AdminUsername = args[0] + "-admin", AdminEmail = args[0] + "-admin@iverson.local" },
    new Metadata { { "authorization", $"Bearer {Environment.GetEnvironmentVariable("TOKEN")}" } });
var link = new Uri(tenant.AdminRecoveryLink);
Console.WriteLine($"link host={link.Scheme}://{link.Authority} path={link.AbsolutePath}");
X
systemd-run --user --scope -q -p MemoryMax=3G dotnet build -c Release -p:UseSharedCompilation=false 2>&1 | command grep -E " error |Build succeeded" | head -3; dotnet build-server shutdown >/dev/null 2>&1
kubectl -n iverson get secret iverson-api-tls -o jsonpath='{.data.tls\.crt}' | base64 -d > $LIVE/edge-cert.pem
( CID="$(kubectl -n iverson get secret iverson-authentik-admin-automation-client -o jsonpath='{.data.client-id}' | base64 -d)"
  export TOKEN="$(curl -s -H 'Host: iverson-authentik:9000' http://127.0.0.1:19000/application/o/token/ -d grant_type=client_credentials -d client_id="$CID" \
    -d client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer \
    --data-urlencode client_assertion="$(kubectl -n iverson create token iverson-admin-automation --audience iverson-authentik-admin-automation --duration 10m)" \
    -d scope="admin schema_admin tenant_id_admin" | python3 -c 'import sys,json; print(json.load(sys.stdin)["access_token"])')"
  EDGE_CERT=$LIVE/edge-cert.pem dotnet $LIVE/createtenant/bin/Release/net10.0/createtenant.dll csr11c-live-check )
echo "K6 ingress upstream: $(kubectl -n ingress-nginx logs deploy/ingress-nginx-controller --tail=200 | command grep 'TenantLifecycleGrpcService/CreateTenant' | tail -1 | command grep -o -E '[0-9.]+:8080' | head -1)"
```

Expected:
- `Build succeeded`.
- `link host=http://authentik.iverson.local path=/if/flow/iverson-recovery/`: CreateTenant succeeded through the GRPCS edge.
- `K6 ingress upstream` shows a pod address on port 8080.

- [ ] **Step 9: Tear down and diff the user's state.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-c-sdd/env.sh
kill $(cat $LIVE/pf-ak.pid) $(cat $LIVE/pf-api.pid) 2>/dev/null
KIND_EXPERIMENTAL_PROVIDER=podman kind delete cluster --name iverson; KIND_EXPERIMENTAL_PROVIDER=podman kind get clusters
docker rmi docker.io/library/iverson-api:csr11c localhost/iverson-api:csr11c >/dev/null 2>&1; true
docker images --format '{{.Repository}}:{{.Tag}}' | command grep -E ':csr11c$|iverson-tei-model:'
docker ps -a --format '{{.Names}}' | sort | diff $LIVE/before-containers.txt - && echo containers-unchanged
docker volume ls -q | sort | diff $LIVE/before-volumes.txt - && echo volumes-unchanged
docker network ls --format '{{.Name}}' | sort | diff $LIVE/before-networks.txt - && echo networks-unchanged
docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort | comm -23 $LIVE/before-images.txt - | head && echo no-image-removed-or-repointed
echo "new images:"; docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort | comm -13 $LIVE/before-images.txt -
git -C $WT status --porcelain
```

Expected:
- `No kind clusters found`.
- `containers-unchanged`, `volumes-unchanged` and `networks-unchanged`.
- `no-image-removed-or-repointed`.
- An empty `git status`.

Remove whatever this task built: any listed `iverson-tei-model:*` image, plus any untagged `<none>` image under "new images" created during this task's build window (check `docker image inspect <id> --format '{{.Created}}'`). Newly pulled base images (the kind node, Calico, the TEI base) are acceptable; list them.

## Known issues inherited from spec

These are inherited verbatim. They exist in the implementation by design, as accepted by the user during brainstorming and review.

- **Compose (user decision 2026-10-12):** its API, StarRocks and Authentik → Postgres hops stay plaintext.
- **Service mesh / pod → store mTLS:** not in scope.
- **Edge certificate verification:** the nginx, ALB and GCE edges encrypt to pods but do not verify pod certificates; only the Azure gateway checks the root and CN.
- **Azure first-install window:** edge → pod stays plaintext on Azure until §3's phase 2.
- **Azure behaviour unverified live:** whether the AGIC probe uses HTTPS when `backend-protocol` is https, and whether AGIC removes trusted roots it did not create, are undocumented; Terraform re-asserts the roots on every apply. The Host replacement the 9080 Host fix relies on (Application Gateway replaces Host with `backend-hostname`) is taken from vendor documentation. Only render checks and `terraform validate` run for Azure.
- **gRPC on GCE and AGIC:** the existing "still 400" gap (health checks for the gRPC port) is unchanged.
- **Operator and console wiring render-checked (user decision 2026-10-12, CDR-2 §3.1):** the StarRocks operator's handling of the init container and `configMapInfo`, and the admin-api Ingress's HTTPS backend, are checked by render checks and container probes (the Prometheus container probe scrapes the same TLS Kestrel 8081 endpoint the admin-api Ingress targets); the admin-ui Ingress changes only its port-80 and peer settings (§4, §6), which the render checks cover, not a live operator or ingress run.
- **ALB ALPN (user decision 2026-10-12, CDR-2 §3.2):** gRPC from ALB to the API's HTTP/2-only TLS endpoint needs ALB to negotiate ALPN `h2` (RFC 9113 §3.2); Kestrel drops a TLS client that offers none. AWS documentation is silent; not verified live.
- **StarRocks plaintext still accepted:** FE serves plaintext to clients that do not request TLS (operator and FE scripts); every Iverson client requires TLS.
- **admin-ui static hop:** edge → admin-ui (static files, no credentials) stays plaintext.
- **#18 residuals (user decision 2026-10-12):** operator namespaces have no default-deny; the AKS node-subnet fallback remains.
