# CSR Round 11 — Transport and Network Design (Sub-project C)

**Goal:** sub-project C of the round-11 remediation closes the plaintext hops and network-policy residuals in findings #4, #6 and #18 of `docs/criticalreviews/2026-10-08-iverson-critical-security-review-11.md`.

| Finding | Summary | Severity |
|---|---|---|
| #4 | Authentication-carrying hops are still plaintext: edge → Authentik `:9080`, edge → API, StarRocks, Redis, Authentik → Postgres | Medium |
| #6 | The GCP profile serves the Authentik login, the console and admin-api over HTTP on port 80 | Low |
| #18 | Network-policy residuals: the `ingress-nginx` namespace peer on every profile; the any-destination fallback toggles | Low |

Sub-projects A (server authorization) and B (identity plane) merged and were pushed at `d710aadb`. Sub-project D (clients, tooling and CI) covers the rest of round 11.

**Decisions (user, 2026-10-12):**
- **Scope:** all of #4, #6 and #18 in one spec (a C1 edge / C2 stores split was declined).
- **Helm only:** compose keeps its plaintext hops (known issue). The one compose change is removing the unused Redis.
- **API TLS:** native Kestrel TLS on both API ports, not an nginx sidecar (a sidecar would make the rate limiter's peer `127.0.0.1`).
- **Azure trusted roots:** two-phase: `helm install`, copy the chart CAs into Terraform, apply, then name the roots in Helm. CA keys never leave the cluster; the first install is plaintext edge → pod on Azure only.
- **#18 scope:** the `ingress-nginx` peer only on kind, plus an explicit acknowledgement for the fallback toggles. Operator-namespace default-deny and the AKS node-subnet fallback stay residuals.
- **StarRocks:** FE TLS with verifying clients; FE still accepts plaintext (not forced).
- **Azure port 80:** `ssl-redirect` on the four AGIC Ingresses.
- **Service mesh:** out of scope.

## 1. Chart certificates

Two new chart-generated Secrets follow `charts/authentik/templates/secret-internal-tls.yaml` exactly: lookup-or-generate, `genCA` + `genSignedCert`, the CA key never stored, `helm.sh/resource-policy: keep`, keys required on reuse.

| Secret | Subchart | Certificate names (CN first) |
|---|---|---|
| `<release>-api-tls` | api | `<release>-api`, `<release>-api.<ns>.svc`, `<release>-api.<ns>.svc.cluster.local` |
| `<release>-starrocks-tls` | starrocks | `<release>-starrocks-fe-service` + `.<ns>.svc` + `.<ns>.svc.cluster.local` |

Authentik's public listener reuses the existing `<release>-authentik-internal-tls` (same pod, same sidecar; CN `<release>-authentik`).

## 2. Edge → pod TLS (#4)

**Backend TLS switch.** `backendTls` is true on the nginx, alb and gce classes. On `azure-application-gateway` it is true per pod (API; Authentik) only once that pod's trusted-root name is set (§3). Every item below — Kestrel endpoints, probes, the Prometheus job, `listen 9080 ssl` and the edge annotations — follows it; when false the hop stays as today.

**API (no code change).** The api Deployment sets `Kestrel__Endpoints__Grpc__Url=https://*:8080`, `Kestrel__Endpoints__Http__Url=https://*:8081` and each endpoint's `Certificate__Path`/`Certificate__KeyPath` to the mounted `<release>-api-tls`. `appsettings.json` stays http, so compose and local runs are unchanged. Readiness and liveness probes get `scheme: HTTPS` (the kubelet skips verification). Prometheus's `iverson-api` job gets `scheme: https`, `tls_config.ca_file` (a mounted copy of `ca.crt`) and `tls_config.server_name: <release>-api` (it scrapes pod IPs, which are not certificate names).

**Authentik.** `configmap-tls-proxy.yaml`: `listen 9080 ssl;` with the same `ssl_certificate`, key and `ssl_protocols` as the 8443 server.

**Per-class edge settings** (set by the templates from `className`, not by profile values):

| class | API Ingress (8080) | admin-api Ingress (8081) | Authentik Ingress (9080) |
|---|---|---|---|
| nginx | `backend-protocol: GRPCS` | `backend-protocol: HTTPS` | `backend-protocol: HTTPS` |
| alb | `backend-protocol: HTTPS` (+ existing `backend-protocol-version: GRPC`), `healthcheck-protocol: HTTPS` | `backend-protocol: HTTPS`, `healthcheck-protocol: HTTPS`, `healthcheck-path: /health` | `backend-protocol: HTTPS`, `healthcheck-protocol: HTTPS`, `healthcheck-path: /-/health/live/` |
| gce | Service `cloud.google.com/app-protocols`: `grpc` → `HTTP2`, `http` → `HTTPS` (one annotation on the api Service) | (same Service annotation) | Service `app-protocols`: `http-public` → `HTTPS`; the BackendConfig gains `healthCheck: {type: HTTPS, port: 9080, requestPath: /-/health/live/}` |
| azure-application-gateway | `backend-protocol: https`, `appgw-trusted-root-certificate`, `backend-hostname: <release>-api`, `health-probe-hostname: <release>-api`, `health-probe-port: 8081`, `health-probe-path: /health` | same as API | `backend-protocol: https`, `appgw-trusted-root-certificate`, `backend-hostname`/`health-probe-hostname: <release>-authentik`, `health-probe-port: 9080`, `health-probe-path: /-/health/live/` |

The ALB health-check paths and the GCE Authentik health check are side-fixes in code this change touches: the defaults (`/`) already fail there (Authentik's `/` redirects; 8081 `/` has no route), but they would fail for certain once the protocol changes. GCE's admin-api check is inferred from its HTTPS readiness probe. The API's 8080 health check on GCE and AGIC stays the existing gRPC gap.

## 3. Azure trusted roots (two-phase)

- **Terraform:** a new `appgw_backend_trusted_roots` variable (`map(string)`, name → PEM, default `{}`) in the azure root, passed to `modules/cluster-azure`, renders one `trusted_root_certificate { name, data }` block per entry on the gateway.
- **Helm:** `api.ingress.appgwTrustedRootCertificate` (both API Ingresses, since one Kestrel serves 8080 and 8081) and `authentik.ingress.appgwTrustedRootCertificate` (root names); set → `backendTls` true for that pod's hops.
- **Runbook:** (1) `helm install`; (2) read `ca.crt` from `<release>-api-tls` and `<release>-authentik-internal-tls` into the variable and apply; (3) set the two values and `helm upgrade`.
- **Guard:** on the azure class, if `lookup` finds the hop's TLS Secret (the release exists) and the root name is empty, the render fails, pointing at the runbook. `helm template` without a cluster is unaffected.

## 4. Port 80 (#6)

- **GCE:** the api, admin-api, admin-ui and Authentik Ingress templates add `kubernetes.io/ingress.allow-http: "false"` on the `gce` class. All four have a `tls:` section and none uses Google-managed certificates.
- **Azure:** the same four templates add `appgw.ingress.kubernetes.io/ssl-redirect: "true"` on the AGIC class, so port 80 can only answer a redirect.
- AWS already redirects (`ssl-redirect: "443"`).

## 5. Stores (#4)

**StarRocks FE TLS.**
- `starRocksFeSpec.storageVolumes` gains `fe-tls` (`storageClassName: emptyDir`, a non-zero size, `mountPath: /opt/starrocks/fe/tls`).
- `starRocksFeSpec.initContainers` gains one container on the chart's existing `mysql` image. It reads `tls.crt`, `tls.key` and `ca.crt` from `<release>-starrocks-tls` through `env` (the operator may hash a mounted Secret's volume name, so it is not mounted), and writes a PKCS12 keystore to `fe-tls` with `openssl pkcs12 -export` on every pod start.
- `starRocksFeSpec.configMapInfo` points at a chart ConfigMap whose `fe.conf` is the image's full default plus `ssl_keystore_location`, `ssl_keystore_password` and `ssl_key_password`. The FE entrypoint symlinks the file over the default, so it must be complete.
- **Keystore password:** a fixed, non-secret constant in the chart, used by both the init container and `fe.conf`. The keystore exists only on the pod's emptyDir and holds the same key `<release>-starrocks-tls` already protects, so a password adds no protection, and a generated one would have to be copied into the ConfigMap.
- `ssl_force_secure_transport` stays false: the FE entrypoint's own `show frontends` / `ALTER SYSTEM ADD FOLLOWER` calls are plain `mysql`.

**StarRocks clients.**
- API and worker connection strings (app and provisioner) gain `SslMode=VerifyFull;SslCa=/etc/starrocks-tls/ca.crt`, with `ca.crt` mounted.
- `job-create-user.yaml`: every `mysql` call gains `--ssl-mode=VERIFY_IDENTITY --ssl-ca=…`. The two password literals then travel inside TLS; no hashed `IDENTIFIED … AS`.

**Authentik → Postgres.** The Authentik ConfigMap sets `AUTHENTIK_POSTGRESQL__SSLMODE: verify-full` and `AUTHENTIK_POSTGRESQL__SSLROOTCERT` to a mount of `<release>-postgres-ca` (`ca.crt`) in both Authentik Deployments — the CA and host (`<release>-postgres-rw`) the API and worker already verify with `SSL Mode=VerifyFull`.

**Redis is removed.** Authentik 2026.5.3 reads no Redis setting.
- Helm: the `redis` subchart (`charts/redis`, `redis-0.1.0.tgz`), its `Chart.yaml`/`Chart.lock` entries, the `redis:` blocks in `values.yaml` and the profiles, `AUTHENTIK_REDIS__HOST` and both `AUTHENTIK_REDIS__PASSWORD` references, and the Redis NetworkPolicies.
- Compose: the `redis` service, Authentik's `depends_on: redis` and `AUTHENTIK_REDIS__HOST`.
- Tests: `AuthentikContainerFixture` stops starting a Redis container.

## 6. Network policy (#18)

- **`ingress-nginx` peer:** the four ingress rules that admit it (API 8080, API 8081, admin-ui 8080, Authentik 9080) emit it only when the fronting Ingress has `className: nginx` (parent values `api.ingress.className`, `api.adminApiIngress.className`, `adminUi.ingress.className`, `authentik.ingress.className`). Other classes keep `clusterCidrs` (and Prometheus on 8081).
- **Fallback acknowledgement:** when `networkPolicy.dnsAnyDestination` or `apiServerAnyDestination` is true, the render fails unless `networkPolicy.keepFallbacks: true`. The message points at `docs/runbooks/csr10-infrastructure-cutover.md` §4, which applies the fallbacks by an upgrade and says a kept fallback must be recorded.

## 7. Testing

- **Render checks** (all five profiles, `helm dependency build` + `helm template`/`helm lint`, parsed): per-class backend and health-check annotations (§2 table); the api and Authentik Service `app-protocols`; the GCE BackendConfig health check; `allow-http: "false"` on the four GCE Ingresses and `ssl-redirect` on the four AGIC Ingresses; the Kestrel env and HTTPS probes; the Prometheus https job; `listen 9080 ssl`; the StarRocks storage volume, init container and `configMapInfo`; the client connection strings and job flags; Authentik's `sslmode`/`sslrootcert` env and mount; no Redis object or env; the `ingress-nginx` peer only on kind; the fallback gate both ways; the Azure guard both ways (a fake existing Secret through a scratch chart).
- **Terraform:** `fmt -check`, `validate` per root and tfsec in a scratch copy; the azure root with a non-empty `appgw_backend_trusted_roots`.
- **Suites:** the nine .NET suites and Client.Core stay green (the fixture change runs in Api.Tests).
- **Compose live** (isolated project): Authentik server and worker healthy without Redis; a login completes.
- **Kind live** (fresh install, laptop profile):
  - gRPC through ingress-nginx over GRPCS and the admin console over HTTPS; plaintext `8080`/`8081` refused in-cluster; probes green; Prometheus target `up` over https;
  - an Authentik login through the 9080 TLS listener;
  - StarRocks: the create-user job passes and its `mysql … status` shows a cipher; the API and worker serve reads and writes; a `VerifyFull` connection with a wrong CA fails;
  - Authentik's Postgres session shows SSL in `pg_stat_ssl`;
  - no Redis pod; CreateTenant end to end.

## 8. Rollout

- **One upgrade switches each hop:** the API pods restart with TLS and the Ingress annotations change in the same release; requests in flight during the rollout can fail once.
- **Orphan Secret:** `<release>-redis` carries `resource-policy: keep`; the runbook deletes it after the upgrade.
- **StarRocks:** the FE pods restart (new init container and `fe.conf`); clients reconnect over TLS.
- **Azure:** §3's two phases.
- **Rotation:** delete the hop's TLS Secret, upgrade, restart its pods (and on Azure, update the Terraform root); same as round 10.

## Known issues / accepted as out of scope

- **Compose (user decision 2026-10-12):** its API, StarRocks and Authentik → Postgres hops stay plaintext.
- **Service mesh / pod → store mTLS:** not in scope.
- **Edge certificate verification:** the nginx, ALB and GCE edges encrypt to pods but do not verify pod certificates; only the Azure gateway checks the root and CN.
- **Azure first-install window:** edge → pod stays plaintext on Azure until §3's phase 2.
- **Azure behaviour unverified live:** whether the AGIC probe uses HTTPS when `backend-protocol` is https, and whether AGIC removes trusted roots it did not create, are undocumented; Terraform re-asserts the roots on every apply. Only render checks and `terraform validate` run for Azure.
- **gRPC on GCE and AGIC:** the existing "still 400" gap (health checks for the gRPC port) is unchanged.
- **StarRocks plaintext still accepted:** FE serves plaintext to clients that do not request TLS (operator and FE scripts); every Iverson client requires TLS.
- **admin-ui static hop:** edge → admin-ui (static files, no credentials) stays plaintext.
- **#18 residuals (user decision 2026-10-12):** operator namespaces have no default-deny; the AKS node-subnet fallback remains.

## Verified assumptions

Evidence was gathered on 2026-10-12 at `d710aadb`: a scratch ASP.NET app and a throwaway `starrocks/allin1-ubuntu:4.1.1` container (removed); throwaway runs of the Authentik 2026.5.3 and `mysql:8.0.46` images; reads of the chart, operator v1.11.5 and StarRocks 4.1.1 source on GitHub, and vendor documentation.

**Probes**
1. **Kestrel serves TLS from PEM files by env alone:** `Kestrel__Endpoints__{Grpc,Http}__{Url,Certificate__Path,Certificate__KeyPath}` over an http `appsettings.json` gave HTTP/2 TLS 200 on one endpoint and HTTP/1.1 TLS 200 on the other; h2c and plain HTTP were refused.
2. **A plaintext endpoint beside TLS ones breaks `UseHttpsRedirection`:** plain requests returned 500 ("Cannot determine the https port … multiple values were found"). All-TLS endpoints behave.
3. **StarRocks FE 4.1.1 serves MySQL-protocol TLS from a PKCS12 keystore** built by `openssl pkcs12 -export` (`ssl_keystore_location` etc.): `openssl s_client -starttls mysql` showed TLSv1.3 and the CA-issued certificate. Plain `mysql` still connected and the BE registered (`Alive: true`).
4. **MySqlConnector 2.4.0 `SslMode=VerifyFull;SslCa=`** connected with the right CA and a SAN host; a wrong CA and an IP host both failed ("SSL Authentication Error"). StarRocks reports no `Ssl_cipher` status.
5. **`mysql:8.0.46` `--ssl-mode=VERIFY_IDENTITY --ssl-ca=`** connected with the right CA (`status`: "Cipher in use is TLS_AES_256_GCM_SHA384"); a wrong CA failed. The image ships OpenSSL 3.5.1 and runs as root.
6. **Authentik 2026.5.3 does not use Redis:** `lib/default.yml` has no redis section; no Python module reads it (one licence-comment hit); the Go binaries contain no Redis string. Its Postgres default is `sslmode: disable`, and `lib/config.py:350-351` passes `sslmode`/`sslrootcert` through.

**Chart, code and operator**
7. `appsettings.json` defines endpoints `Grpc` (http 8080, Http2) and `Http` (http 8081, Http1); no `ASPNETCORE_URLS`, `UseUrls` or `Listen*` overrides; `Program.cs:514` `UseHttpsRedirection`, no HSTS.
8. `ClientPartitionKey.cs:16-23` keys on `Connection.RemoteIpAddress`, reading `X-Forwarded-For` only from a trusted peer — unchanged by in-process TLS.
9. In-cluster callers of the API are limited by `<release>-api-ingress` (`templates/networkpolicies.yaml:38-63`) under the namespace default-deny: `ingress-nginx`, `clusterCidrs`, and Prometheus on 8081; kubelet probes. Prometheus scrapes 8081 via `dns_sd` A records (`charts/prometheus/templates/configmap.yaml:19-23`), image `prom/prometheus:v2.55.1`.
10. Rendered Ingresses: laptop has api (nginx, 8080) and authentik (nginx, 9080); local has all four on nginx; aws, gcp and azure have api 8080, admin-api 8081, admin-ui 8080, authentik 9080; every gce and azure Ingress has a `tls:` section; no `managed-certificates`.
11. Authentik 9080 is admitted only from `ingress-nginx`/`clusterCidrs` (`networkpolicies.yaml:540-560`); the tls-proxy already serves 8443 with `<release>-authentik-internal-tls`.
12. The API and worker reach Postgres with `SSL Mode=VerifyFull;Root Certificate=/etc/postgres-tls/ca.crt` from `<release>-postgres-ca` (`charts/api/templates/deployment.yaml:93, 277-282`); Authentik uses the same host `<release>-postgres-rw` (`charts/authentik/templates/configmap.yaml:7`) and runs as uid 1000.
13. Credentialed StarRocks clients are exactly: API app, worker app and provisioner connection strings (`charts/api/templates/deployment.yaml:101`, `charts/worker/templates/deployment.yaml:85, 94`) and `job-create-user.yaml:50-70`.
14. Redis consumers: only Authentik (configmap, both deployments), the Redis NetworkPolicies, `Chart.yaml`/`Chart.lock`, `values.yaml` and the cloud/laptop profiles, compose, and `AuthentikContainerFixture`; `charts/redis/templates/secret.yaml` keeps its Secret.
15. Operator v1.11.5: storage volumes are mounted under their own name, `storageClassName: emptyDir` yields an emptyDir (a size starting "0" skips the volume) (`pkg/k8sutils/templates/pod/mount.go:36-50, 16-30`); a Secret volume name may carry a hash (`getVolumeName`, `VolumeNameWithHash`); `initContainers` (with `volumeMounts`, `env`) pass through to the pod (`spec.go:274`); `configMapInfo` mounts at `/etc/starrocks/fe/conf` and sets `CONFIGMAP_MOUNT_PATH` (`fe_pod.go:33-85`).
16. StarRocks 4.1.1 `fe_entrypoint.sh`: `update_conf_from_configmap` symlinks each ConfigMap file over `conf/<file>` (lines 189-210); `show frontends` and `ALTER SYSTEM ADD FOLLOWER/OBSERVER` use plain `mysql` (lines 86, 229-232). `fe-ubuntu:4.1.1` runs as root (registry config).
17. The `ingress-nginx` peer appears at `templates/networkpolicies.yaml:47-48, 55-56, 470-471, 555-556`; parent values hold `api:`, `adminUi:` and `authentik:` sections (`values.yaml:188, 268, 284`).
18. No values file sets `dnsAnyDestination`/`apiServerAnyDestination` true (`values.yaml:32-33`); the cutover runbook applies them as fallbacks after failed post-deploy checks and allows keeping one if recorded (`docs/runbooks/csr10-infrastructure-cutover.md:129-136`).
19. azurerm `~> 3.90` (`terraform/azure/main.tf`) supports `trusted_root_certificate { name, data }` on `azurerm_application_gateway` (v3.117 docs); `modules/cluster-azure/waf.tf`'s `ignore_changes` does not list it.

**Vendor documentation**
20. Kubernetes: with `scheme: HTTPS` "the kubelet sends an HTTPS request skipping the certificate verification."
21. ingress-nginx: `backend-protocol` accepts `HTTPS` and `GRPCS`; `proxy-ssl-verify` defaults to off.
22. AWS LBC: `backend-protocol-version` is valid with `HTTPS`; `healthcheck-protocol` defaults to `HTTP` (it does not follow the backend protocol); `healthcheck-path` defaults to `/`.
23. GKE: `cloud.google.com/app-protocols` maps Service port names to `HTTPS`/`HTTP2`; Google Cloud load balancers accept any backend certificate (self-signed or unknown CA); health checks are inferred from the serving port's `httpGet` readiness probe, else BackendConfig `healthCheck`; `kubernetes.io/ingress.allow-http: "false"` disables HTTP, with the add-after-programming restriction applying only to `managed-certificates`.
24. AGIC: HTTPS to pods needs a well-known CA or `appgw-trusted-root-certificate` naming a root installed on the gateway; Application Gateway v2 checks the backend certificate's CN against the backend setting's host (`backend-hostname`), and pod-IP hosts are unsupported for HTTPS; the default probe is HTTP to `127.0.0.1` on `/`; the docs contradict each other on an HTTP listener without `ssl-redirect`.
