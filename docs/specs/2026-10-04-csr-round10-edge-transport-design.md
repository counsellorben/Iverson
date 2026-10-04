# CSR Round 10 — Edge and Transport Remediation Design (Sub-project B)

**Source review:** `docs/criticalreviews/2026-10-03-iverson-critical-security-review-10.md` (reviewed at `a8c48db4`)

**Goal:** fix the edge and transport findings from CSR round 10:
- **#3:** the pre-authentication rate limiter is keyed on the proxy's IP, so every client behind an ingress shares one anonymous bucket. The post-auth limiter's IP fallback has the same collapse.
- **#5, the Authentik hop only:** the API fetches OIDC discovery and JWKS from Authentik, and sends its admin token to Authentik, over plaintext HTTP. Unknown key ids can also trigger a JWKS refetch on demand.
- **#21:** the enrichment response regex runs in quadratic time on unterminated fences, and the response body is read uncapped.

Sub-projects A (server authorization) and E (tooling) are merged. C (identity plane) and D (infrastructure and supply chain) are separate.

**Global constraints**
- **One branch, one merge.** No proto change, no SDK change.
- **Security write-ups** (comments, test names, docs) stay at the level of conditions and behaviour, never step-by-step exploitation.
- **`docs/` is gitignored**, so commit docs files with `git add -f`.
- **The user's running compose stack** (`iversonserver` containers, volumes, networks, images) is never started, stopped or modified. Live checks use isolated compose projects (`-p <name>`, `--project-directory`, `container_name:` stripped, `down -v`) and a throwaway kind cluster that the check creates and deletes.
- **Compose secrets** are read from `Iverson.Server/.env` and never printed.

---

## 1. Rate-limiter client key (closes #3)

The pre-auth limiter must stay ahead of authentication: round 9's #2 put it there so that garbage-token floods cannot force unmetered signature verifications. The fix is therefore to recover the real client address before authentication, not to move the limiter.

### 1a. `ClientPartitionKey`

A new static unit in `Iverson.Api` (for example `Iverson.Api/RateLimiting/ClientPartitionKey.cs`):

```csharp
public static string For(HttpContext ctx, TrustedProxyOptions opts)
```

- The peer is `ctx.Connection.RemoteIpAddress`, with an IPv4-mapped IPv6 address normalised to IPv4.
- If the peer lies inside one of `opts.Cidrs` and the request has an `X-Forwarded-For` header:
  - split the header on commas and trim each entry;
  - take the entry `opts.Hops` positions from the right (`Hops = 1` is the rightmost);
  - parse it as an IP address, accepting `ip:port` and `[ipv6]:port`;
  - return that address.
- Otherwise return the peer address. That covers an untrusted peer, a missing header, fewer than `Hops` entries, and an entry that does not parse.
- If there is no peer address, return `"anon"`, as today.

Nothing else in the app changes: `RemoteIpAddress` is not rewritten. Only the two limiters read it today (`Program.cs:129` and `:502`).

### 1b. Wiring

- **Pre-auth limiter** (`Program.cs:501-503`): the partition key becomes `ClientPartitionKey.For(ctx, opts)`.
- **Post-auth limiter** (`Program.cs:128-129`): the fallback after `sub` becomes `ClientPartitionKey.For(ctx, opts)`.
- Budgets, windows and exclusions are unchanged.

### 1c. Configuration

- A new section `RateLimiting:TrustedProxies` binds to `TrustedProxyOptions { string[] Cidrs; int Hops = 1; }`.
- An empty `Cidrs` means `X-Forwarded-For` is never trusted. That is the compose and Development case: compose clients connect to the API directly, so its behaviour is unchanged.
- Startup fails with a message naming the setting if any `Cidrs` entry does not parse as a CIDR (`System.Net.IPNetwork.Parse`) or if `Hops < 1`.

### 1d. Helm

- New api values `api.trustedProxies.cidrs` (a list) and `api.trustedProxies.hops` (an integer, default 1), rendered into `charts/api/templates/deployment.yaml` as `RateLimiting__TrustedProxies__Cidrs__<i>` and `RateLimiting__TrustedProxies__Hops`.
- The chart `fail`s if `cidrs` is empty or contains a blank entry, mirroring the `networkPolicy.clusterCidrs` guard at `templates/networkpolicies.yaml:3-8`. `values.yaml` gives no default.
- Each profile sets its own values:

| Profile | `cidrs` | `hops` | Why |
|---|---|---|---|
| `values-local.yaml`, `values-laptop.yaml` | `10.244.0.0/16` | 1 | ingress-nginx connects from a pod IP. kind's default podSubnet is `10.244.0.0/16`, and the tigera-operator sizes Calico's pool from kubeadm's `podSubnet`. ingress-nginx's defaults replace `X-Forwarded-For` with `$remote_addr`, on gRPC locations too. NetworkPolicy admits only the ingress-nginx namespace from pod IPs on 8080. |
| `values-aws.yaml` | `10.0.128.0/20`, `10.0.144.0/20` | 1 | The two public subnets, where the internet-facing ALB is placed. Nodes sit in the private subnets. ALB appends the client IP by default. |
| `values-azure.yaml` | `10.1.16.0/24` | 1 | The Application Gateway subnet. It writes `ip:port`, which the parser accepts. |
| `values-gcp.yaml` | `130.211.0.0/22`, `35.191.0.0/16` | 2 | Google front-end ranges. Google appends `<client-ip>,<lb-ip>`, so the client is second from the right. |

The CI overrides (`values-*.ci-override.yaml`) layer on top of their profiles and need no change.

### 1e. Tests

- **`ClientPartitionKey` unit tests:**
  - a trusted peer with one entry;
  - a trusted peer with a spoofed left-hand entry, where the rightmost entry wins;
  - `Hops = 2` with `client, lb`;
  - `ip:port`, and IPv6 with and without a port;
  - an untrusted peer with a header, which returns the peer;
  - a trusted peer with no header, too few entries, or a malformed entry, which returns the peer;
  - an IPv4-mapped peer;
  - a null peer, which returns `"anon"`.
- **Configuration validation tests:** a bad CIDR, `Hops = 0`, and an empty list, which is accepted.
- **A pipeline test** (the existing `AuthTestWebApplicationFactory` pattern): with `TrustedProxies` set, two requests from the same trusted peer carrying different `X-Forwarded-For` values reach different pre-auth partitions. TestServer supplies no peer address, so the test sets `Connection.RemoteIpAddress` itself.
- **A Helm render check:** an empty `api.trustedProxies.cidrs` fails the render with the guard's message.

---

## 2. TLS for the API→Authentik hop (closes #5 for the Authentik hop)

### 2a. Certificates

**Helm.** A new Secret, `<release>-authentik-internal-tls`, in the authentik subchart, holding `tls.crt`, `tls.key` and `ca.crt`.
- It follows the chart's lookup-or-generate idiom (`charts/redis/templates/secret.yaml:9-13`, `charts/authentik/templates/secret-postgres.yaml:9-15`). If the Secret exists, its data is reused unchanged.
- Otherwise:
  - `genCA "iverson-authentik-ca" 3650` creates the CA;
  - `genSignedCert "<release>-authentik" nil (list "<release>-authentik" "<release>-authentik.<ns>.svc" "<release>-authentik.<ns>.svc.cluster.local") 3650 $ca` issues the server certificate.
- The CA private key is never stored, so nothing in the cluster can issue more certificates under this CA.
- **Rotation:** delete the Secret, run `helm upgrade`, then restart the Authentik server and the API pods so both pick up the new material.

**Compose.** `scripts/generate-compose-secrets.sh` also creates `Iverson.Server/deploy/compose-tls/` (mode 0700) containing `ca.crt`, `tls.crt` and `tls.key`.
- The server certificate's SAN is `DNS:authentik-server`.
- It uses openssl, which the script already depends on.
- It writes the files only when `tls.crt` is missing, like the script's existing append-if-missing behaviour. The CA key is deleted after signing.
- The files are mode 0644 inside the 0700 directory, and each file is bind-mounted individually. Under rootless podman a container uid cannot read a host file that is 0600, and the 0700 directory keeps other host users out.
- `.gitignore` gains `Iverson.Server/deploy/compose-tls/`.

### 2b. TLS sidecar in front of Authentik

**Port 8443.** Authentik binds 9000, 9443 (its own self-signed listener), 9300 and others inside its pod (`authentik/lib/default.yml`, `listen:`), so the sidecar uses **8443**. Authentik's own 9443 stays unreachable: neither the Service nor any NetworkPolicy admits it.

**Helm**, `charts/authentik/templates/deployment-server.yaml`:
- **A new container, `tls-proxy`:**
  - Image: `nginxinc/nginx-unprivileged:1.27-alpine`, pinned by the digest `Iverson.AdminUI/Dockerfile:24` uses.
  - It runs as the pod's uid and gid 1000, not the image default of 101. With the pod's `fsGroup: 1000`, that lets it read the Secret's key.
  - `allowPrivilegeEscalation: false`, `readOnlyRootFilesystem: true`, `capabilities: { drop: ["ALL"] }`, and an `emptyDir` mounted at `/tmp`.
  - It mounts the `<release>-authentik-internal-tls` Secret (`tls.crt`, `tls.key`) and a new ConfigMap holding `default.conf`:

    ```nginx
    server {
      listen 8443 ssl;
      ssl_certificate     /etc/iverson/tls/tls.crt;
      ssl_certificate_key /etc/iverson/tls/tls.key;
      location / {
        proxy_pass http://127.0.0.1:9000;
        proxy_set_header Host $http_host;
        proxy_set_header X-Forwarded-Proto https;
      }
    }
    ```

  - It has a TCP readiness probe on 8443.
- **The Service** (`charts/authentik/templates/service.yaml`) gains port 8443, named `https`.
- **Port 9000 stays** for the ingress controller and the kubelet probes.

**How Authentik builds its URLs.** It derives `issuer` and `jwks_uri` from `Host` and `X-Forwarded-Proto`. It trusts those headers from `127.0.0.0/8` by default (`trusted_proxy_cidrs`), and the chart does not override that. Discovery over 8443 therefore returns `https://<release>-authentik:8443/…`.

**Compose:** a new `authentik-tls-proxy` service.
- Same image and `default.conf`.
- `network_mode: "service:authentik-server"`, so it shares the server's network namespace and the proxy-to-Authentik hop stays on loopback.
- It mounts `deploy/compose-tls/tls.crt` and `tls.key` individually.
- `depends_on: authentik-server`.

The API reaches it as `authentik-server:8443`.

**NetworkPolicy**, `templates/networkpolicies.yaml`:
- **`<release>-authentik-ingress`:** the API-pod rule moves from port 9000 to 8443. The allow-all 9000 rule for probes and ingress is unchanged.
- **`<release>-api-egress`:** the rule to `authentik-server` moves from 9000 to 8443. No other workload has an egress rule to Authentik.

### 2c. API side

**New settings.** No existing setting changes meaning.

| Setting | Helm value | Compose value |
|---|---|---|
| `Authentication:MetadataAddress` | `https://<release>-authentik:8443/application/o/iverson-api/.well-known/openid-configuration` | `https://authentik-server:8443/application/o/iverson-api/.well-known/openid-configuration` |
| `Authentication:ActingUser:MetadataAddress` | same | same |
| `Authentication:InternalIssuer` | `http://<release>-authentik:9000/` | `http://authentik-server:9000/` |
| `Authentik:BaseUrl` (changed value) | `https://<release>-authentik:8443` | `https://authentik-server:8443` |
| `Authentik:CaCertificatePath` | `/etc/iverson/authentik-ca/ca.crt` | `/etc/iverson/authentik-ca/ca.crt` |

**Issuer.** Every provider uses `issuer_mode: global`, so a token's `iss` is `{scheme}://{host}/` of the request that minted it. Today both schemes accept `http://<authentik>:9000/` only because JwtBearer appends the metadata document's issuer to `ValidIssuers` (`JwtBearerHandler.cs:254-255`). The `ActingUser` scheme sets no `ValidIssuers` at all. With metadata on 8443 the metadata issuer becomes `https://<authentik>:8443/`. Therefore:
- **Default scheme:** `ValidIssuers = [Authority, ExternalIssuer, InternalIssuer]`.
- **`ActingUser` scheme:** `ValidIssuers = [InternalIssuer]`.
- JwtBearer still appends the metadata issuer, so the accepted set is today's plus `https://<authentik>:8443/`.
- `Authority` and `ExternalIssuer` keep their values. `mint_acting_user_token.py`, LoadTest and the conformance harness, which mint on 9000, are unaffected.

**Trust handler.** One factory builds a `SocketsHttpHandler` that validates the server certificate against only the CA at `Authentik:CaCertificatePath`:
- an `X509Chain` with `ChainPolicy.TrustMode = CustomRootTrust`, the CA in `CustomTrustStore`, and revocation checking off, since a private CA has no revocation endpoint;
- it also rejects `RemoteCertificateNameMismatch`.

It is used by:
- the default scheme's `BackchannelHttpHandler`;
- the `ActingUser` scheme's `BackchannelHttpHandler`;
- the `IdpAdminClient` named HttpClient, via `ConfigurePrimaryHttpMessageHandler`.

When `Authentik:CaCertificatePath` is unset (Development and the test suites), the factory returns a default handler.

**Both JWT schemes:**
- `MetadataAddress` is set from the new settings.
- `RequireHttpsMetadata = true`, replacing `false` and the plaintext-justifying comment at `Program.cs:191-197`.
- `RefreshOnIssuerKeyNotFound` stays at its default. Over TLS a forced refresh can only fetch Authentik's genuine keys.
- The `Authentik:BaseUrl` fallback at `Program.cs:401` (`http://authentik-server:9000`) is removed. The comment above it, which describes the admin-token hop as accepted plaintext, is updated.

**Startup guard.** It applies when `WorkloadRole == "api"` and the environment is not Development. The API refuses to start, naming the setting, if:
- `Authentication:MetadataAddress`, `Authentication:ActingUser:MetadataAddress`, `Authentik:BaseUrl` or `Authentication:InternalIssuer` is missing; or
- either `MetadataAddress` or `Authentik:BaseUrl` is not `https://`; or
- `Authentik:CaCertificatePath` is missing, or the file is not a readable PEM certificate.

The Helm worker (`charts/worker`) has no Authentik settings and no Authentik egress, and is untouched. JwtBearer creates no metadata manager when both `Authority` and `MetadataAddress` are empty (`JwtBearerPostConfigureOptions.cs`). The guard does not run for the worker role.

**Deployment wiring:**
- **Helm**, `charts/api/templates/deployment.yaml`: the new environment variables, the 8443 URLs, and a volume that mounts only the `ca.crt` key of `<release>-authentik-internal-tls` at `/etc/iverson/authentik-ca`.
- **Compose:**
  - `iverson-api` and `iverson-worker` get the same settings and a read-only mount of `deploy/compose-tls/ca.crt`.
  - The compose worker needs them because it mirrors the API's Authentik settings. JwtBearer with `RequireHttpsMetadata = true` throws on an http address, so the worker can't keep the old ones.
  - `iverson-api.depends_on` adds `authentik-tls-proxy`.

### 2d. Tests

- **Trust handler:** a local Kestrel TLS server with a CA and a certificate generated in-test (`CertificateRequest`). The handler accepts a certificate the CA signed. It rejects:
  - a certificate signed by another CA;
  - a certificate whose SAN does not match the host;
  - a self-signed certificate.
- **Startup guard:** one case for each refusal condition. Also: Development skips the guard, the worker role skips it, and a fully configured api role starts.
- **JwtBearer wiring:** for both schemes, with the settings present, the resolved options have `RequireHttpsMetadata = true`, the configured `MetadataAddress`, `InternalIssuer` in `ValidIssuers`, and the CA-trusting backchannel handler.
- **The existing pipeline suites stay green.** `AuthTestWebApplicationFactory` runs in Development and post-configures `Authority = null` with no `MetadataAddress`.
- **Secret-name render check:** for every profile, the internal Secret's name differs from `authentik.ingress.tlsSecretName`.

---

## 3. Enrichment regex and response cap (closes #21)

`Iverson.Embeddings/EnrichmentService.cs`:
- **Regex.** `FencedBlock` uses `RegexOptions.Singleline | RegexOptions.NonBacktracking` in place of `RegexOptions.Compiled`. Probed against the current pattern:
  - identical `Groups[1]` and match index on 8 fence cases;
  - on a 128 KB unterminated input, 0 ms against 6,558 ms (32 KB: 2 ms against 436 ms).
- **Response cap.**
  - The request is sent with `HttpCompletionOption.ResponseHeadersRead`.
  - The send and the bounded body read share one `CancellationTokenSource`, linked to the caller's token, with `CancelAfter(EnrichmentServiceOptions.Timeout)`. That keeps the body under the same bound `HttpClient.Timeout` gives the whole response today.
  - If the linked source fires while the caller's token has not, the service rethrows `new TaskCanceledException(…, new TimeoutException())`. That is the shape `HttpClient` itself produces on timeout, and the one `EnrichmentConsumer.cs:233-234` skips. Without the translation the cancellation surfaces with an inner `IOException`, which that filter does not match.
  - The body is read through a bounded loop with a cap of 1 MiB, an internal constant. A completion capped at 256 tokens is a few KB.
  - A body over the cap throws `InvalidOperationException` ("Enrichment backend response exceeded N bytes").
  - It deliberately does not use `MaxResponseContentBufferSize`. That raises an `HttpRequestException` with a null status, which `TransientFailures.cs:57` classifies as a connection failure and retries. `InvalidOperationException` is non-transient, the same class as an unparseable reply.
- **Tests:**
  - The existing `ExtractJson` and `EnrichmentService` tests stay green unchanged.
  - New: a 128 KB unterminated-fence input completes under 500 ms.
  - New: an oversize body throws `InvalidOperationException`, and a body just under the cap parses.
  - New: a handler whose body stalls past a short `Timeout` raises `TaskCanceledException` with an inner `TimeoutException`, and `EnrichmentConsumer` takes its timeout-skip path.

---

## 4. Live verification

1. **Compose**, in an isolated project built from the branch:
   - generate scratch TLS material and secrets;
   - bring the stack up;
   - run the conformance `identity` scenario with all five languages. That proves the API validates real service and acting-user tokens with metadata fetched over 8443 and trusted through the CA.
   - Also confirm that `curl --cacert` to `authentik-server:8443` returns discovery with `https` URLs, that the API's 9000 path is no longer used, and that a pre-auth 429 still occurs only per client.
2. **kind**, a throwaway cluster that the check creates and deletes:
   - run `setup.sh`, build and load the branch image, and `helm upgrade --install` with `values-local.yaml`;
   - confirm the Calico pool is `10.244.0.0/16`;
   - confirm the `tls-proxy` container is Ready and serves the Secret's certificate;
   - confirm the API pod is Ready, its metadata fetch on 8443 succeeds, and a token is accepted;
   - confirm two clients through ingress-nginx with different source addresses get separate pre-auth partitions, and that a spoofed `X-Forwarded-For` from the client is ignored.
3. **CI lint:** run `helm lint` against all 5 profiles, as `deploy-validate.yml` does.

---

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| 1 | Only the two limiters read `RemoteIpAddress` | `grep RemoteIpAddress` over non-test `.cs`: `Iverson.Api/Program.cs:129`, `:502` only |
| 2 | The pre-auth limiter must stay before authentication | Round-9 review #2: a request with a bad token is rejected by `UseAuthorization` and never reaches a later limiter. Middleware order: `Program.cs:522-538` |
| 3 | `ForwardedHeadersMiddleware`-style parsing accepts `ip:port` and ignores spoofed left-hand entries; Google's shape collapses at one hop | Probed on .NET 10: `203.0.113.7:51234` gave `203.0.113.7`; `spoof, client` gave `client`; an untrusted peer kept its own IP; a `client, lb` peer at ForwardLimit 1 gave the LB IP |
| 4 | ingress-nginx's defaults replace XFF with `$remote_addr` on gRPC locations | ingress-nginx `template.go` returns `grpc_set_header` for GRPC backends; `nginx.tmpl`'s else branch sets `X-Forwarded-For $remote_addr` |
| 5 | ALB appends the client IP by default | AWS ALB docs, "x-forwarded-headers": the default `routing.http.xff_header_processing.mode` is `append` |
| 6 | Application Gateway writes `IP:port` | Azure "how Application Gateway works": "comma-separated list of IP:port" |
| 7 | Google appends `<client-ip>,<lb-ip>` from `130.211.0.0/22` and `35.191.0.0/16` | Google Cloud external Application LB docs |
| 8 | kind's pod CIDR is `10.244.0.0/16` | kind `pkg/apis/config/v1alpha4/default.go:51-52` sets that default podSubnet. tigera-operator `pkg/controller/ippool/defaults.go:109-116` and `kubeadm.go` read kubeadm-config's `podSubnet`. `setup.sh` sets no pool. To be re-confirmed in live check 2. |
| 9 | Laptop is a kind and ingress-nginx profile | `values-laptop.yaml:13-17` ("this profile also runs on kind"); `className: "nginx"` at `:95` |
| 10 | The AWS ALB lives in the public subnets `10.0.128.0/20` and `10.0.144.0/20`, and nodes in the private ones | `modules/cluster-aws/main.tf:87-112`: public subnets `cidrsubnet(vpc_cidr, 4, i+8)` tagged `kubernetes.io/role/elb`; node group `subnet_ids = private` at `:565`; `vpc_cidr` default `10.0.0.0/16` (`variables.tf:16-18`) |
| 11 | The Azure Application Gateway subnet is `10.1.16.0/24` | `modules/cluster-azure/main.tf:194-196` |
| 12 | The chart has a `fail` guard idiom for empty CIDR lists | `templates/networkpolicies.yaml:3-8` |
| 13 | CI lints all 5 profiles, with the ci-overrides layered on top | `.github/workflows/deploy-validate.yml` loops `values-local values-laptop values-aws values-azure values-gcp`; the ci-overrides set only hosts and annotations |
| 14 | Authentik's native 9443 is not a usable path without database and brand changes | Probed on 2026.5.3: a discovered certificate on the brand was ignored until `default: true`, then took effect after a refresh. Source `internal/web/brand_tls/brand_tls.go` refreshes every 3 minutes and serves a fallback before that |
| 15 | A TLS proxy in front of 9000 yields https `issuer` and `jwks_uri` | Probed on 2026.5.3 with an nginx proxy setting `Host $http_host` and `X-Forwarded-Proto https`: discovery returned `https://aksidecar:19444/…`, validated against the test CA |
| 16 | Authentik trusts `X-Forwarded-*` from loopback, and the chart does not override it | `authentik/lib/default.yml` `trusted_proxy_cidrs` includes `127.0.0.0/8`; no `TRUSTED_PROXY` or `LISTEN__` setting in `charts/authentik/` |
| 17 | Port 8443 is free in the Authentik pod | `authentik/lib/default.yml` `listen:` uses 9000, 9443, 3389, 6636, 1812, 9300, 9900, 9901, 6669; the chart exposes only 9000 |
| 18 | nginx-unprivileged runs as uid 1000 with a read-only root, all capabilities dropped and a writable `/tmp`, serving TLS on 8443 | Probed: `--user 1000:1000 --read-only --tmpfs /tmp --cap-drop ALL` served `CN=authentik-server` |
| 19 | The Authentik server pod runs as uid and gid 1000, with fsGroup 1000 | `charts/authentik/templates/deployment-server.yaml:25-30` |
| 20 | AdminUI pins nginx-unprivileged by digest | `Iverson.AdminUI/Dockerfile:24` |
| 21 | Helm renders `genCA` and `genSignedCert`, and the chart uses lookup-or-generate for Secrets | Helm v3.16.4 rendered a probe chart; idiom at `charts/redis/templates/secret.yaml:9-13`, `charts/authentik/templates/secret-postgres.yaml:9-15` |
| 22 | Only the API has egress to Authentik, on 9000, and Authentik's ingress admits API pods on 9000 | `templates/networkpolicies.yaml:90-91` (api-egress), `:594-610` (authentik-ingress); the worker chart has no Authentik egress |
| 23 | Every provider uses `issuer_mode: global`, and the global issuer is `{scheme}://{host}/` of the request | `charts/authentik/templates/secret-service-clients.yaml:315,366,397,481`; compose blueprint `:89,111,132,165,257`; Authentik `providers/oauth2/models.py:349-352`; the mint script comments at `:461-468` |
| 24 | JwtBearer appends the metadata issuer to `ValidIssuers` | aspnetcore `JwtBearerHandler.cs:254-255` (release/10.0) |
| 25 | JwtBearer with `RequireHttpsMetadata = true` throws on an http address, and skips the manager when both addresses are empty | aspnetcore `JwtBearerPostConfigureOptions.cs` (release/10.0) |
| 26 | The `ActingUser` scheme sets no `ValidIssuers` | `Program.cs:207-233` |
| 27 | The Helm worker has no Authentik settings; the compose worker mirrors the API's | `charts/worker/templates/deployment.yaml` has no `Authentication__` or `Authentik__`; `docker-compose.yml` `iverson-worker` sets `Authentication__Authority`, `Authentik__BaseUrl` and others |
| 28 | Compose runs the API and worker as Production | `docker-compose.yml` `ASPNETCORE_ENVIRONMENT=Production` on both |
| 29 | The test suites run in Development and null out `Authority` | `AuthTestWebApplicationFactory.cs` has no `UseEnvironment` (WebApplicationFactory defaults to Development) and post-configures `Authority = null` on both schemes at `:82-95` |
| 30 | Nothing else consumes `Authentik:BaseUrl`, `Authentication:Authority` or a metadata address | `grep` over non-test `.cs`: `Program.cs:184-210` and `:401-406` only |
| 31 | `network_mode: "service:X"` shares the network namespace under this machine's compose | Probed: the sidecar reached the backend on `127.0.0.1`, and a client reached the sidecar's port through the backend's service name |
| 32 | The secrets script is append-if-missing with openssl, and `.env` is gitignored | `scripts/generate-compose-secrets.sh:14-37`; `.gitignore:60` |
| 33 | NonBacktracking accepts the fence pattern with identical results and linear time | Probed on .NET 10 (8 cases identical; timings in §3) |
| 34 | An `HttpRequestException` with a null status is classified transient | `Iverson.Api/Consumers/TransientFailures.cs:56-59` |
| 35 | The enrichment HttpClient is registered in `AddEnrichment` | `Iverson.Embeddings/ServiceCollectionExtensions.cs:31-39` |
| 36 | `System.Net.IPNetwork` is available | Used in the .NET 10 probe (`IPNetwork.Parse`) |
| 37 | On kind, the API's peer is the ingress-nginx pod IP, not a node IP | `deploy/kind/setup.sh:114-115`: `controller.hostPort.enabled=true`, `controller.service.type=ClusterIP`. The invocation at `:110-118` sets no `hostNetwork`. (CDR-1 span check) |
| 38 | Kestrel peers arrive as IPv4-mapped IPv6, which the §1a normalisation handles | `Iverson.Api/appsettings.json:12`: `"Url": "http://*:8080"`, a dual-stack bind. (CDR-1 span check) |
| 39 | .NET custom-root chain building accepts the Helm `genCA`/`genSignedCert` output, and rejects a foreign CA and a wrong host | CDR-1 probe P2 used the spec's exact template, Helm v3.16.4, and `X509Chain` with `CustomRootTrust` on .NET 10. It gave `chain=True` with an empty status, `otherCA=False` and `hostMatch(wrong)=False`. |
| 40 | Moving `Authentik:BaseUrl` to 8443 doesn't break the recovery-link consumer | `LoadTest/Auth/AuthentikFlowExecutorClient.cs:350-364` takes only the link's slug and `flow_token`, and rebuilds the URL from its own `BaseUrl`. (CDR-1 A8 grep) |
| 41 | TestServer supplies no peer address, so the §1e pipeline test must set `Connection.RemoteIpAddress` itself | CDR-1 span check D3 |
| 42 | `ResponseHeadersRead` takes the body out of `HttpClient.Timeout`. A linked CTS bounds it, but surfaces with an inner `IOException` | Probed on .NET 10 with the real `SocketsHttpHandler` against a server that sends headers, then trickles 100 bytes. A buffered read with `Timeout=1s` gave `TaskCanceledException` wrapping `TimeoutException` at 1,024 ms. `ResponseHeadersRead` with `Timeout=1s` completed at 10,051 ms, never timing out. `ResponseHeadersRead` with a linked CTS at 1 s gave `TaskCanceledException` wrapping `IOException` at 1,004 ms. |
| 43 | The internal Secret name `<release>-authentik-internal-tls` is used by no profile; `<release>-authentik-tls` is already the Azure and GCP public ingress certificate | `values-azure.yaml:158`, `values-gcp.yaml:166` (`tlsSecretName: "iverson-authentik-tls"`); `git grep "internal-tls"` outside `docs/` has 0 hits (CDR-1 §2.2) |

---

## Known issues / accepted as out of scope

- **Trusted-proxy residual.** A host inside a trusted load-balancer subnet, or on kind a pod in the ingress-nginx namespace, can choose its own pre-auth bucket by setting `X-Forwarded-For`. Those subnets hold only the load balancers, so this needs a foothold inside them.
- **Other plaintext hops in #5** remain plaintext: StarRocks without `SslMode`, Jaeger, TEI and Ollama, ingress-to-pod, and ingress-to-Authentik. They need a mesh or per-service TLS.
- **Edge rate limiting** (WAF, Cloud Armor, nginx limit annotations) belongs to sub-project D.
- **GCP and Azure gRPC** paths still return 400 (an existing ingress gap). The limiter fix covers whatever traffic reaches the API, including the admin API on those profiles.
- **`ExternalIssuer` never matches global-mode tokens**, because their issuer is the root path, not the app path. That is the round-10 identity-plane defect and belongs to sub-project C.
- **The Helm CA is valid for 10 years and has no automatic rotation.** Rotation is manual (§2a).
