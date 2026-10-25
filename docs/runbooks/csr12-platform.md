# CSR Round-12 Platform — Operator Runbook

**Why:** CSR round 12's Platform sub-project (`docs/specs/2026-10-19-csr-round12-platform-design.md`)
puts every in-cluster hop under Istio ambient mTLS, moves kind's edge from ingress-nginx to an Istio
gateway and GCP's to GKE Gateway, gates the api's and admin-automation's workload identities behind an
admission policy that only the operators group passes, adds default-deny policies to the operator
namespaces, grants Operator from a per-install list of subjects instead of an Authentik group, throttles
logins per address and account, and serves TLS on compose's authentication-carrying hops. This runbook
covers the upgrade order, the operators group, compose, and the checks a first cloud deploy must make.
Operator onboarding, the operator list, login throttling and `AUTHENTIK_SECRET_KEY` rotation are in
`operator-access-onboarding.md` and `csr11-identity-plane.md`.

## 1. Before the upgrade

- **kind needs a new cluster.** The node image is pinned to Kubernetes 1.35.8
  (`Iverson.Server/deploy/kind/kind-config.yaml`), and kind changes a node image only by recreating the
  cluster. `setup.sh` and `setup.ps1` refuse a kind older than v0.33.0. A fresh install needs none of
  §3; §3 is the order for a cluster that already runs `main`'s chart.
- **Run `helm dependency build`** in `Iverson.Server/deploy/helm/iverson`. A stale `charts/*.tgz`
  renders the old subcharts.
- **The cloud profiles have no upgrade path.** No cloud cluster is deployed, so each is a fresh install
  at Kubernetes 1.35: one Terraform apply (Istio, the operator-namespace policy chart and the namespace
  labels), then the chart install, then the checks in §5.

## 2. The operators group

The admission policy `<release>-<namespace>-sensitive-identities` refuses everyone outside
`admission.operatorGroups`, cluster-admin included, when they:
- create a token for `<release>-api` or `<release>-admin-automation`;
- exec, attach, port-forward or proxy (through the API server) into a `<release>-api-*`, `<release>-worker-*`,
  `<release>-authentik-*` or `<release>-postgres-*` pod, add an ephemeral container to one, or change one
  of its container images, or proxy to the Services in front of those pods;
- create a pod, or create or update a workload, whose pod template uses either account.

So a `kubectl rollout restart` of the api, the blueprint applies and shells in the Authentik worker,
`psql` in a Postgres instance, and every `helm upgrade` that changes a protected workload (a chart-version
or api template change does) need the group.

- **kind and laptop** (group `iverson-operators`): add the group to your own identity by impersonation.

  ```bash
  OPS="--as=kubernetes-admin --as-group=kubeadm:cluster-admins --as-group=iverson-operators"
  kubectl $OPS -n iverson rollout restart deployment/iverson-api
  kubectl $OPS -n iverson port-forward svc/iverson-authentik 9000:9000
  ```

  Helm takes `--kube-as-user` together with `--kube-as-group` (§3 step 2); `--kube-as-group` alone is
  refused. `kubectl cnpg psql` does not pass impersonation flags to the `kubectl exec` it starts: give it a
  kubeconfig context whose user sets `as` and `as-groups` (with `--context`), or run a plain
  `kubectl $OPS exec` into the primary instance's `postgres` container.
- **Under PSA baseline** `kubectl debug` also needs `--profile=baseline`.
- **Clouds.** Operators hold the group through the platform: on AKS, the Entra group(s) in
  `cluster_admin_group_object_ids`; on EKS, the operator access entries' `kubernetes_groups`; on GKE, a
  Google Group under Google Groups for RBAC. §5 checks that it reaches the API server.

## 3. Upgrading an existing kind cluster

Run the steps in order. Steps 2 and 7 need the operators group.

Steps 1, 4, 5 and 6 run one phase of the kind setup script each: `Iverson.Server/deploy/kind/setup.sh
--only <phase>` (`setup.ps1 --only <phase>` on Windows) runs exactly what a fresh install runs for that
phase, and nothing else.

1. **Istio and the Gateway API CRDs.** Install Istio 1.31.2 (`base`, `istiod` with the `ambient`
   profile, `istio-cni` and `ztunnel`, from the vendored charts) and the Gateway API v1.6.3 standard CRDs.
   Nothing is enrolled yet, so traffic does not change:

   ```bash
   Iverson.Server/deploy/kind/setup.sh --only istio
   ```
2. **Load the branch's images, then upgrade the release as the operators group.** The chart runs the
   api, the worker and the console at tag `0.1.0` with `imagePullPolicy: IfNotPresent`, so the node
   keeps serving `main`'s build under that tag until the branch's is loaded. `main`'s api checks
   StarRocks readiness with `SHOW BACKENDS`, which needs the `OPERATE ON SYSTEM` this upgrade revokes,
   so its pods would go unready for good once the revoke runs. On Windows, run
   `Iverson.Server/deploy/kind/build-and-load-image.ps1` for the api and worker image (add
   `-ClusterName <name>` if the cluster is not named `iverson`); it builds only that image, so build and
   load the console by hand: `docker build -f Iverson.AdminUI/Dockerfile -t
   docker.io/library/iverson-admin-ui:0.1.0 .`, then `kind load docker-image
   docker.io/library/iverson-admin-ui:0.1.0 --name iverson`. Elsewhere, from the repository root (pass
   `0.1.0 <cluster>` to each script if the cluster is not named `iverson`):

   ```bash
   Iverson.Server/deploy/kind/build-and-load-image.sh
   Iverson.Server/deploy/kind/build-and-load-image.sh 0.1.0 iverson --dockerfile Iverson.AdminUI/Dockerfile --image-name iverson-admin-ui
   cd Iverson.Server/deploy/helm/iverson && helm dependency build .
   helm upgrade --install --wait --timeout 10m iverson . -f values-local.yaml -n iverson \
     --kube-as-user kubernetes-admin --kube-as-group kubeadm:cluster-admins --kube-as-group iverson-operators
   ```

   Use `values-laptop.yaml` on the laptop profile. This renders the 15008 rules, the operator → operand
   rules, the mesh policies, the admission policy, the gateway routes and the Authentik changes; the
   15008 rules and the mesh policies do nothing until step 5. The upgrade moves Authentik from 2026.5.3
   to 2026.8.4: the new worker migrates the database while the old server pods keep serving, and the new
   server pods wait for the migration, which can outlast Helm's default 5-minute timeout, hence
   `--timeout 10m`. The post-upgrade revoke of `OPERATE ON SYSTEM` runs once the new pods are Ready; a
   terminating previous-release pod may still fail readiness briefly, as with the role revoke
   (`csr12-server.md` §1).

   **The kind edge is down from this step until step 6.** The chart stops rendering its Ingresses here
   (kind's routes are now `GRPCRoute` and `HTTPRoute` objects on the gateway step 6 creates), so
   ingress-nginx serves nothing for the release in between. Reach the services in-cluster meanwhile
   (`kubectl $OPS port-forward`, §2).

   The console's pod template does not change in this upgrade, so restart it to run the new build
   (local; laptop has no console): `kubectl -n iverson rollout restart deployment/iverson-admin-ui`.

   **2a. Restart Prometheus** where it runs (local; laptop has none). It reads its scrape config only at
   start, so until then it scrapes the old 8081 targets instead of 8082:

   ```bash
   kubectl -n iverson rollout restart deployment/iverson-prometheus
   ```
3. **Restart the StarRocks FE.** `ssl_force_secure_transport` is read only at start, and the StarRocks
   operator does not roll the FE when only its `fe.conf` ConfigMap changes:

   ```bash
   kubectl -n iverson delete pod -l app.starrocks.ownerreference/name=iverson-starrocks-fe
   ```
4. **The edge namespace, then the operator-namespace policies.** The phase creates `istio-system` and
   `iverson-edge` (never enrolled) where they are missing, then installs the operator-namespace policy
   chart (`Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies`):

   ```bash
   Iverson.Server/deploy/kind/setup.sh --only operator-namespaces
   ```
5. **Enrol the four namespaces together.** The phase labels `iverson`, `cnpg-system`, `kafka` and
   `starrocks` once the release's NetworkPolicies list 15008 (step 2); otherwise it labels nothing and
   says so. Running pods are captured without a restart:

   ```bash
   Iverson.Server/deploy/kind/setup.sh --only ambient-labels
   ```
6. **Replace ingress-nginx with the gateway.** Both want the node's host ports 80 and 443, so remove
   ingress-nginx first, then create the gateway in `iverson-edge`. The edge, down since step 2, returns
   once the gateway pod is Ready:

   ```bash
   helm uninstall ingress-nginx -n ingress-nginx
   kubectl delete namespace ingress-nginx
   Iverson.Server/deploy/kind/setup.sh --only gateway
   ```
7. **Apply the changed Authentik blueprints** as `csr11-identity-plane.md` §2 describes, adding the
   operators-group flags of §2 to every `kubectl exec`.

Then run the three probe scripts in `Iverson.Server/deploy/scripts/` (`authentik-probes.sh`,
`mesh-check.sh`, `admission-check.sh`); each one's header gives its arguments.

## 4. Compose

Compose's authentication-carrying hops now serve TLS from a per-checkout CA: the api and the worker
(Kestrel on 8080, 8081 and 8082), Authentik (through `authentik-tls-proxy`, published on 9443; 9000 is no
longer published), Postgres, the StarRocks FE and Qdrant. TEI, Ollama and Jaeger stay plaintext; they
carry no credentials.

1. **Re-run `scripts/generate-compose-secrets.sh`.** It writes a new CA, now carrying `keyUsage`, and
   five certificates into `Iverson.Server/deploy/compose-tls/`. It rewrites all of them together whenever
   one is missing or ends within 30 days; after any rewrite, recreate the stack, because each server
   reads its certificate at start.
2. **Rebuild the api image and recreate the stack:**
   `cd Iverson.Server && docker compose build iverson-api && docker compose up -d --force-recreate`.
   `iverson-api` and `iverson-worker` run the one `iverson-api` image, which `up` does not rebuild
   once it exists. With `main`'s build, the worker's `/health` fails once `starrocks-init` revokes
   `OPERATE ON SYSTEM`, and the api waits for a healthy worker.
3. **Unset `IVERSON_ACTING_USER_HOST_HEADER`** if you exported `authentik-server:9000` for compose.
   Through the tls-proxy that Host yields the issuer `https://authentik-server:9000/`, which the API
   rejects with a bare 401. Compose clients need no Host override: the API accepts
   `https://localhost:9443/`.
4. **Trust the CA on the host.** Each runtime reads its own setting:

   | Client | Setting |
   |---|---|
   | .NET and Go (LoadTest, the conformance harness, the .NET and Go drivers) | `SSL_CERT_FILE` |
   | Python's urllib (the mint script, `stack.py`, `ingest.py`, token requests) | `SSL_CERT_FILE` |
   | Python's grpcio (`pattern_leg.py`, the Python driver, the agent) | `GRPC_DEFAULT_SSL_ROOTS_FILE_PATH` |
   | Node (the TypeScript driver) | `NODE_EXTRA_CA_CERTS` |
   | Java (the Java driver) | a PKCS12 truststore in `-Djavax.net.ssl.trustStore` (a PEM file is not accepted) |
   | The console in a browser | the browser's or the operating system's certificate store |
   | Prometheus (inside compose) | `tls_config.ca_file`; compose mounts the CA |

   `SSL_CERT_FILE` replaces the default roots, so a process that also reaches public endpoints
   (`dotnet run` restoring packages, `go build` fetching modules, the agent's Anthropic client) needs a
   bundle of the system roots plus the compose CA. From the repository root:

   ```bash
   mkdir -p ~/.cache/iverson
   cat /etc/ssl/certs/ca-certificates.crt Iverson.Server/deploy/compose-tls/ca.crt > ~/.cache/iverson/compose-ca-bundle.pem
   export SSL_CERT_FILE=~/.cache/iverson/compose-ca-bundle.pem
   export GRPC_DEFAULT_SSL_ROOTS_FILE_PATH="$PWD/Iverson.Server/deploy/compose-tls/ca.crt"
   export NODE_EXTRA_CA_CERTS="$PWD/Iverson.Server/deploy/compose-tls/ca.crt"
   cp "$(dirname "$(dirname "$(readlink -f "$(command -v java)")")")/lib/security/cacerts" ~/.cache/iverson/compose-truststore.p12
   keytool -importcert -noprompt -alias iverson-compose-ca -file Iverson.Server/deploy/compose-tls/ca.crt \
     -keystore ~/.cache/iverson/compose-truststore.p12 -storepass changeit
   export JAVA_TOOL_OPTIONS="-Djavax.net.ssl.trustStore=$HOME/.cache/iverson/compose-truststore.p12 -Djavax.net.ssl.trustStorePassword=changeit"
   ```

   `/etc/ssl/certs/ca-certificates.crt` is the Debian and Ubuntu bundle; other distributions name theirs
   differently. Repeat the bundle, the truststore and the browser import after the script rewrites the CA.
5. **Host addresses.** Dial `localhost`, not `127.0.0.1`: only Kestrel's certificate names an IP.

   | Service | From the host |
   |---|---|
   | API, gRPC | `https://localhost:8080` |
   | API, admin, health and traces | `https://localhost:8081` |
   | API, `/metrics` and `/build` | `https://localhost:8082` |
   | Authentik (login, admin interface, token endpoint) | `https://localhost:9443`; issuer `https://localhost:9443/`, token endpoint `https://localhost:9443/application/o/token/` |
   | Postgres | `localhost:5432`; TLS offered and plaintext still accepted; `sslmode=verify-full` needs the host `localhost` |
   | StarRocks FE | `localhost:9030`; TLS offered, not forced |
   | Qdrant | `https://localhost:6333` (REST), `localhost:6334` (gRPC over TLS) |

## 5. The first deploy on a cloud

Cloud profiles were verified by render and static checks only. Run these at the first deploy; each
mismatch fails closed (a refused command, a FailedCreate event, or an edge 503), not open.

- **The operators group reaches the API server.** As an operator principal:

  ```bash
  kubectl auth whoami          # Groups lists a group named in admission.operatorGroups
  kubectl auth can-i create pods --subresource=exec -n <ns>
  kubectl auth can-i create pods --subresource=portforward -n <ns>
  kubectl auth can-i create serviceaccounts --subresource=token -n <ns>
  kubectl auth can-i create validatingadmissionpolicies.admissionregistration.k8s.io
  kubectl auth can-i create validatingadmissionpolicybindings.admissionregistration.k8s.io
  kubectl auth can-i create clusterroles.rbac.authorization.k8s.io
  kubectl auth can-i create clusterrolebindings.rbac.authorization.k8s.io
  ```

  Every `can-i` answers `yes`. The last four are what the protected `helm upgrade` writes. On GKE these
  rights come from project IAM, outside Terraform; on AKS the admin group is bound to cluster-admin
  through `azure_rbac_enabled`.
- **The controllers reach the API server under their usual names.** After the install,
  `kubectl -n <ns> get events -o custom-columns=REASON:.reason,OBJECT:.involvedObject.name,MESSAGE:.message | command grep sensitive-identities`
  prints nothing. A line there means a controller's username differs from kind's: the replicaset, job,
  statefulset and cronjob controllers record the refusal as `FailedCreate`, the deployment controller as
  `ReplicaSetCreateError`, and every refusal message names the policy `<release>-<namespace>-sensitive-identities`.
- **AWS: the EBS CSI driver add-on version, before the first `terraform apply`.** AWS publishes no
  version table for this add-on, so the pin in `Iverson.Server/deploy/terraform/modules/cluster-aws/main.tf`
  (`aws_eks_addon.ebs_csi`, `v1.66.1-eksbuild.1`) follows the upstream release. Check it against the
  versions AWS offers for Kubernetes 1.35:

  ```bash
  aws eks describe-addon-versions --addon-name aws-ebs-csi-driver --kubernetes-version 1.35 \
    --query 'addons[0].addonVersions[].addonVersion' --output text
  ```

  If the pin is not listed, set `addon_version` to the newest listed version before applying; a wrong
  string fails the apply at the add-on.
- **AWS: the cluster is not replaced.** A second `terraform plan` in `Iverson.Server/deploy/terraform/aws`
  after the first apply shows no change to `aws_eks_cluster.this`. Its bootstrap flag forces
  replacement, so any diff there plans a new cluster; do not apply it.
- **GKE: the service-account issuer.** Terraform computes it from the project, location and cluster
  name, a format Google does not document: run the comparison in `csr11-identity-plane.md` §7. Until
  `authentik.serviceAccountIssuer` matches what the cluster publishes, both workload-token pins refuse
  every exchange.
- **GKE: the main host's two certificates.** The Gateway's `main` listener names both
  `api.ingress.tlsSecretName` and `adminUi.ingress.tlsSecretName` for the one host; that GKE accepts two
  certificates there is unverified. If `kubectl -n <ns> describe gateway <release>-edge` shows the
  `main` listener without both `Programmed=True` and `ResolvedRefs=True`, or the host serves no certificate, set `adminUi.ingress.tlsSecretName:
  ""` in `values-gcp.yaml` (the listener then carries the api's certificate alone, for the same host)
  and upgrade.
- **GKE: gRPC-Web through the Gateway.** The class's documentation does not mention it, and the console
  reaches the api over REST, so call a gRPC-Web method directly. Sign in to the console as an operator,
  copy the access token from the browser (the `Authorization: Bearer …` header of any `/admin/console/`
  request in the developer tools' network panel), and send an empty `ListTenants` call through the
  Gateway:

  ```bash
  TOKEN='<the copied access token>'
  printf '\x00\x00\x00\x00\x00' > /tmp/empty.grpcweb
  curl -s -D - -o /tmp/listtenants.out -X POST "https://<ingressHost>/iverson.TenantLifecycleGrpcService/ListTenants" \
    -H 'content-type: application/grpc-web+proto' -H 'x-grpc-web: 1' -H "authorization: Bearer $TOKEN" \
    --data-binary @/tmp/empty.grpcweb
  command grep -a -o 'grpc-status: *[0-9]*' /tmp/listtenants.out
  ```

  The headers show status 200 and `content-type: application/grpc-web`, and the last line prints
  `grpc-status: 0`. A 4xx or 5xx from the load balancer, or no `grpc-status`, means the Gateway does not
  carry gRPC-Web.
- **Every edge answers.** The api, Authentik and the console must not answer 503 at the edge. A 503 means
  the load balancer's source address falls outside the PERMISSIVE ports' `ipBlocks` rule
  (`clusterCidrs`, plus `nodeSubnetFallback` where set).
- **AKS: the node-subnet fallback.** If the api, admin-ui or Authentik pods stay unready on probe
  timeouts, or the edge answers 503, because Azure NPM does not exempt node traffic, set the node subnet
  as a fallback peer in `values-azure.yaml` and upgrade:

  ```yaml
  networkPolicy:
    nodeSubnetFallback: ["10.1.0.0/20"]
    keepFallbacks: true
  ```

  The value renders only with `keepFallbacks: true`, which records that the profile keeps a fallback
  (`csr10-infrastructure-cutover.md` §4).
- **EKS: security groups for pods.** None are enabled today. If they ever are, run the VPC CNI with
  `POD_SECURITY_GROUP_ENFORCING_MODE=standard`, set through the `vpc-cni` add-on's
  `configuration_values` in `Iverson.Server/deploy/terraform/modules/cluster-aws/main.tf` (its `env`
  map): in strict mode the CNI drops the link-local kubelet probes the mesh relies on.
- **Istio ambient on AKS and GKE** has no vendor support statement. Check that every pod in
  `istio-system` is Ready and run `mesh-check.sh` against the cluster.
