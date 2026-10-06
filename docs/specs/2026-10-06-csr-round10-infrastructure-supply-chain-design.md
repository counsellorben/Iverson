# CSR Round 10 — Infrastructure and Supply Chain Remediation Design (Sub-project D)

**Source review:** `docs/criticalreviews/2026-10-03-iverson-critical-security-review-10.md` (reviewed at `a8c48db4`)

**Goal:** fix the remaining infrastructure and supply-chain findings from CSR round 10, at every remediation tier the review lists (primary, architectural, defense-in-depth):
- **#6 (Medium):** AKS keeps local accounts, Terraform deploys with a long-lived cluster-admin certificate, and that certificate sits in Terraform state, whose storage account allows shared-key access.
- **#15:** on AWS and Azure, every VM and pod in the VPC/VNet reaches the API, Prometheus and the console; Authentik's `9000` is open to any source (C1 deferred narrowing it here).
- **#17:** DNS egress admits any destination on port 53; Postgres and Kafka admit any destination on 443/6443; TEI and Ollama admit any destination on 443.
- **#18:** EKS nodes let pods reach instance metadata (hop limit 2).
- **#19:** build context includes `.env` files; stale console images (Node 20 is end-of-life and below the console's own engine floor); Ollama's model pin compares a 48-bit prefix; a committed `.pyc` embeds an old password; floating action tags, missing Dependabot ecosystems, unpinned `setup.ps1` operators, tag-only third-party images, unused console dependencies, and no image scanning.

Sub-projects A, B, C1 and E are merged. C2 (orchestrator workload identity) remains separate.

**Global constraints**
- **One branch, one merge.** No proto, SDK or API code change.
- **Static verification only for cloud Terraform.** No cloud account is used: `terraform fmt`/`init -backend=false -lockfile=readonly`/`validate`, `tfsec`, and the locked provider schemas (azurerm 3.117.1, aws 5.100.0, google 5.45.2, kubernetes 2.38.0, helm 2.17.0). Helm and NetworkPolicy behaviour is verified live on a throwaway kind cluster with Calico.
- **Security write-ups** (comments, test names, docs) stay at the level of conditions and behaviour, never step-by-step exploitation.
- **`docs/` is gitignored**, so commit docs files with `git add -f`.
- **The user's running compose stack** (`iversonserver` containers, volumes, networks, images) is never started, stopped or modified. Another session's `rfdt-qdrant`/`rfdt-tei` containers and `tei_models` volume are never touched. Live checks use throwaway resources the check creates and deletes.
- **Memory.** One heavy process at a time (WSL OOM kills the VM). Image builds run with `--memory` caps; kind image loads stage on disk, never in `/tmp` (RAM-backed here).

---

## 1. Ingress: who may reach the API, Prometheus, the console and Authentik (closes #15)

### 1a. The kubelet needs no allow rule

NetworkPolicy always admits traffic from the node a pod runs on: the Kubernetes spec states it, EKS's VPC CNI agent and Calico document it, and a live kind/Calico check confirmed it (Verified assumptions 1–4). Every peer and rule that exists only to admit kubelet probes is therefore removed.

`networkPolicy.clusterCidrs` keeps its name and its fail-closed guard (`templates/networkpolicies.yaml:3-10`), but now means only **the address ranges the platform's load balancer connects from**. The `values.yaml` comment (`values.yaml:14-27`) and each overlay's comment are rewritten to say so.

| Profile | Today | After | Why |
|---|---|---|---|
| AWS | `10.0.0.0/16` (whole VPC) | `10.0.128.0/20`, `10.0.144.0/20` | The module's public subnets, where the internet-facing ALB (target-type `ip`) places its interfaces. Pods and nodes use the private subnets (VPC CNI) |
| Azure | `10.1.0.0/16` (whole VNet) | `10.1.16.0/24` | The App Gateway (AGIC) subnet. Pods and nodes share `10.1.0.0/20` (Azure CNI) |
| GCP | `10.2.0.0/20`, `130.211.0.0/22`, `35.191.0.0/16` | `130.211.0.0/22`, `35.191.0.0/16` | Container-native load balancing connects from Google's fixed ranges; the node subnet only served the kubelet |
| laptop/local | `172.18.0.0/16` | unchanged | Local kind network; ingress-nginx is admitted by namespace |

### 1b. Rule changes (`templates/networkpolicies.yaml`)

- **`api-ingress`** (8080, 8081): shape unchanged (ingress-nginx namespace, Prometheus pods on 8081, `clusterCidrs`); the narrowed ranges take effect. The comment drops the kubelet and VPC-wide wording.
- **`prometheus-ingress`** (9090): the `clusterCidrs` peers are removed. Only API pods are admitted.
- **`admin-ui-ingress`** (8080): the allow-all `from: []` rule becomes `clusterCidrs` ipBlocks beside the existing ingress-nginx namespace peer. On cloud the console is reached through the ALB/AGIC, like the API.
- **`authentik-ingress`**:
  - **9000:** the allow-all rule is deleted. Its only users are the kubelet (exempt), the tls-proxy sidecar (loopback, inside the pod) and `kubectl port-forward` (not subject to NetworkPolicy; verified live). The API reaches Authentik on 8443 since sub-project B.
  - **9080** (the public listener): the allow-all rule becomes ingress-nginx namespace plus `clusterCidrs`.
  - **8443:** unchanged (API pods only).

These four rules are the chart's complete set of `from: []` and `clusterCidrs` ingress peers (Verified assumption 8).

### 1c. Failure mode and the Azure check

If a platform did not exempt node traffic, the affected pods would never become Ready: a visible deploy failure, never a silent opening. Azure NPM's exemption rests on the Kubernetes spec and a user report, not a Microsoft document (Known issues). The operator runbook gains an AKS post-deploy readiness check and the fallback: add the node subnet `10.1.0.0/20` to `clusterCidrs`.

---

## 2. Egress: what pods may reach (closes #17)

### 2a. DNS goes only to kube-dns

The 14 DNS rules (`to: []` on 53) become one named helper in `templates/_helpers.tpl`, rendered at each site:

- `to: [{ namespaceSelector: { matchLabels: { kubernetes.io/metadata.name: kube-system } }, podSelector: { matchLabels: { k8s-app: kube-dns } } }]`, UDP and TCP 53.

`k8s-app=kube-dns` is the CoreDNS/kube-dns pod label and the kube-dns Service selector on EKS, AKS, GKE and kind. On EKS a NetworkPolicy matches traffic sent to a Service address only when its podSelector uses the Service's own selector labels, which this one does; the Service port equals the container port (53). No override value (YAGNI).

### 2b. API-server egress goes only to the control plane

A new value, `networkPolicy.apiServerCidrs`, has the same fail-closed guard as `clusterCidrs` (must be set, no blank entries). The Postgres and Kafka rules on 443/6443 (`templates/networkpolicies.yaml:194-212, 265-274`, the only API-server rules) change from any destination to these ranges:

| Profile | `apiServerCidrs` | Why |
|---|---|---|
| AWS | `10.0.0.0/20`, `10.0.16.0/20`, `10.0.128.0/20`, `10.0.144.0/20` | EKS places its control-plane interfaces in the cluster subnets (private and public) and recreates them on upgrade, so /32s would go stale |
| Azure | `10.1.17.0/28` | The API Server VNet Integration subnet (3b) |
| GCP | `172.16.0.0/28` | `master_ipv4_cidr_block`; nodes reach the control plane on its internal endpoint. The runbook confirms the in-cluster endpoint on first deploy |
| laptop/local | `172.18.0.0/16`, `10.89.0.0/16` | kind's node network under docker and under podman (podman's default pool; observed `10.89.1.3`). The control plane serves on 6443 |

Both ports stay listed: policy is evaluated after service translation, and kind/kubeadm serve the API on 6443.

### 2c. Model weights are baked into images; TEI and Ollama get no egress

Two new Dockerfiles build derived images from digest-pinned bases (each `@sha256:…` below is the multi-arch index digest of that exact tag, resolved from the registry when the plan is written):

- **`Iverson.Server/deploy/images/tei-model/Dockerfile`** — from `ghcr.io/huggingface/text-embeddings-inference:cpu-1.8.3@sha256:…`. A build stage runs TEI once against the pinned `--model-id`/`--revision` to populate its hub cache under `/models` (the same code path TEI uses today), fails the build if TEI never reports healthy, and removes lock files. The runtime stage copies `/models` and sets `HUGGINGFACE_HUB_CACHE=/models` and `HF_HUB_OFFLINE=1`.
- **`Iverson.Server/deploy/images/ollama-model/Dockerfile`** — from `ollama/ollama:0.12.11@sha256:…`. A build stage pulls the model and compares the full SHA-256 of its local manifest with `global.generativeModelDigest`, failing the build on any mismatch (closes #19's 48-bit prefix check). The runtime stage copies `/models` and sets `OLLAMA_MODELS=/models`.

**Chart changes:**
- **Image names** are derived, so the pin and the image cannot diverge: TEI `{{ global.modelImageRegistry }}iverson-tei-model:<slug>-<revision[0:12]>`; Ollama `{{ global.modelImageRegistry }}iverson-ollama-model:<model with ':' and '/' → '-'>-<digest[0:12]>`. `global.modelImageRegistry` defaults to empty (kind) and is set per cloud like the `iverson-api` registry.
- **TEI StatefulSet:** args unchanged; the PVC, its mount and `volumeClaimTemplates` are removed; `storageSize`/`storageClassName` leave every overlay.
- **Ollama StatefulSet:** the `pull-model` init container, the PVC and `volumeClaimTemplates` are removed; `HOME` points at the existing writable `tmp` emptyDir (the root filesystem stays read-only).
- **NetworkPolicy:** `tei-egress` and `ollama-egress` keep no egress rules at all (no 443, no DNS).
- **Upgrade:** `volumeClaimTemplates` is immutable, so an existing release's TEI and Ollama StatefulSets must be deleted before upgrading, and their old PVCs removed. The runbook says so.

**Build and load:**
- `deploy/kind/build-and-load-image.sh` and `.ps1` also build and load the model images. Image saves stage under `/var/tmp` (disk), not `/tmp`, because a 5.7 GB Ollama image staged in RAM can exhaust the VM.
- Cloud deploys push the model images alongside `iverson-api` (deployment docs updated).
- Compose keeps downloading at runtime: it has no NetworkPolicy, so nothing changes there.

**Sizes (built locally):** TEI + bge-base ≈ 1.1 GB; Ollama 0.12.11 base is 3.75 GB, plus qwen2.5:3b ≈ 1.9 GB.

---

## 3. Cloud infrastructure (closes #6 and #18; supports 2b on AKS)

### 3a. AKS cluster access (#6)

`modules/cluster-azure/main.tf`:
- `local_account_disabled = true`.
- `azure_active_directory_role_based_access_control { managed = true, azure_rbac_enabled = true, tenant_id = data.azurerm_client_config.current.tenant_id, admin_group_object_ids = var.cluster_admin_group_object_ids }`. `managed = true` is mandatory in azurerm 3.x. `cluster_admin_group_object_ids` is a new required variable (the Entra group(s) for human operators), threaded through `azure/main.tf`.
- `azurerm_role_assignment` "Azure Kubernetes Service RBAC Cluster Admin" on the cluster for the deploying principal (`data.azurerm_client_config.current.object_id`). The operators module depends on it. A new assignment can take up to five minutes to propagate; the runbook says to re-run a first apply that fails on that.
- **Outputs:** `kube_config` is deleted (its only consumer is `azure/main.tf`). `host` and `cluster_ca_certificate` (from `kube_config[0]`) become plain outputs: under Entra the kubeconfig carries no client certificate.

`azure/main.tf`: the `kubernetes` and `helm` providers authenticate with `exec { api_version = "client.authentication.k8s.io/v1beta1", command = "kubelogin", args = ["get-token", "--login", "azurecli", "--server-id", "6dae42f8-4368-4678-94ff-3960e28e3630"] }` instead of the client certificate. The deploy docs list `kubelogin` as a prerequisite.

**Runbook:** run `az aks rotate-certs` once after the cutover, so certificates already copied from old state stop working (defense-in-depth tier).

### 3b. AKS API Server VNet Integration and the control-plane identity

- A new subnet `10.1.17.0/28`, delegated to `Microsoft.ContainerService/managedClusters`.
- `api_server_access_profile { authorized_ip_ranges = var.api_authorized_ip_ranges, vnet_integration_enabled = true, subnet_id = azurerm_subnet.apiserver.id }`. A comment records that these two attributes are deprecated preview-API fields in azurerm 3.x (renamed `virtual_network_integration_enabled` in 4.46), that the feature is one-way, and that enabling it changes the API server's IP (hostname unchanged).
- **The cluster identity becomes user-assigned.** VNet Integration needs the cluster identity to hold Network Contributor on the API-server and node subnets before provisioning, which a system-assigned identity cannot (it exists only once the cluster does). A new `azurerm_user_assigned_identity` for the control plane gets Network Contributor on `azurerm_subnet.aks` and the new subnet; `identity { type = "UserAssigned", identity_ids = [...] }`; the cluster depends on both role assignments. The existing disk-encryption-set Reader assignment (`main.tf:117-121`) moves to the new identity's principal. azurerm 3.117.1 updates `identity` in place (no ForceNew), and adding `subnet_id` is in place (only removing it forces replacement).

### 3c. Terraform state account (#6 architectural tier)

`bootstrap/azure/main.tf` (local state, no backend):
- The storage account sets `shared_access_key_enabled = false` and `network_rules { default_action = "Deny", ip_rules = var.state_authorized_ip_ranges, bypass = ["AzureServices"] }`. `state_authorized_ip_ranges` is required and validated non-empty, like `api_authorized_ip_ranges`.
- The provider sets `storage_use_azuread = true` (azurerm creates containers through the data plane, which needs Entra auth once shared keys are off).
- The deploying principal gets Storage Blob Data Contributor scoped to the **storage account** (a container-scoped assignment cannot exist before the container, and the container cannot be created without it); the container `depends_on` the assignment. This is the only data-plane grant: blob read is restricted to the deploy identity.

`azure/main.tf`'s backend block adds `use_azuread_auth = true`.

**Runbook ordering for an existing account:** grant the data role and add the operator's IP before disabling shared keys; re-run if propagation lags.

### 3d. EKS instance metadata (#18)

- `modules/cluster-aws/main.tf:553-557`: `http_put_response_hop_limit = 1`. The comment is rewritten: every AWS-calling workload gets its identity from IRSA, so pods no longer need instance metadata.
- `modules/cluster-aws` gains a `vpc_id` output; `modules/operators` gains a `vpc_id` variable (default `null`, used only when `cloud == "aws"`). The AWS Load Balancer Controller release sets `region` and `vpcId` (it otherwise reads the VPC ID from metadata).
- The EBS CSI controller (IRSA) receives `AWS_REGION` from EKS's IRSA webhook; its node plugin falls back to Kubernetes node metadata when metadata is unreachable (documented for v1.37). The add-on's `configuration_values` is not changed.
- The cluster autoscaler already sets `awsRegion` and uses IRSA; `aws-node` and `kube-proxy` run on the host network.

---

## 4. Supply chain and build hygiene (closes #19)

### 4a. Build context

- Root `.dockerignore` adds `**/.env`, `**/.env.*`, `.claude/`, `.worktrees/`, `docs/`. `Iverson.Server/.dockerignore` adds `**/.env`, `**/.env.*`.
- `Iverson.Server/Iverson.Api/Dockerfile:23` (`COPY . .`) becomes explicit copies of the nine directories the publish needs: `Iverson.Server/{Iverson.Api,Iverson.Sql,Iverson.StarRocks,Iverson.Embeddings,Iverson.Vector,Iverson.Patterns,Iverson.Events}/`, `Iverson.Clients/DotNet/Iverson.Client.Contracts/`, `Iverson.Clients/Common/Proto/`.
- `Iverson.Server/Iverson.Launcher/Dockerfile:7` (`COPY . .`, context `Iverson.Server/`) becomes `COPY Iverson.Launcher/ Iverson.Launcher/`.
- The console's tracked `.env.development` is not read by `vite build` (production mode), so excluding it is safe.

### 4b. Console and nginx images

- `Iverson.AdminUI/Dockerfile:13`: `node:22-alpine@sha256:…` (22.23.x satisfies `package.json`'s `>=22.22.0`; Node 20 is end-of-life).
- nginx moves from `nginx-unprivileged:1.27-alpine` to the current stable line, `1.30-alpine`, at all three uses: `Iverson.AdminUI/Dockerfile:24` and `docker-compose.yml:442` keep digest pins (Dependabot's docker and docker-compose ecosystems refresh digests); the Authentik sidecar in the chart (`deployment-server.yaml:150`) follows 4d.

### 4c. Small items

- **`.pyc`:** `git rm --cached Iverson.Server/deploy/scripts/__pycache__/mint_acting_user_token.cpython-314.pyc`. `.gitignore:82-83` already excludes it; the file predates those lines. The old password stays in history; it was LoadTest's former tenant-admin default, replaced by a required environment variable in sub-project E. History is not rewritten.
- **`admin-ui.yml`:** `actions/checkout@v7` and `actions/setup-node@v7` are pinned to the SHAs the other workflows use (`3d3c42e5…` v7.0.1, `82076278…` v7.0.0).
- **`setup.ps1`:** every operator chart is pinned to `setup.sh`'s versions (Calico v3.32.2 ×2, ingress-nginx 4.12.8, cloudnative-pg 0.29.0, strimzi 1.1.0, starrocks operator 1.11.5, metrics-server 3.9.0).
- **Unused console dependencies:** `@improbable-eng/grpc-web`, `google-protobuf` and `long` are removed from `dependencies` (no import anywhere; proto generation is a manual script whose output directory is gitignored and unused). The emotion packages stay (MUI peers).

### 4d. Third-party chart images and Dependabot

- **Chart images become Dependabot-readable scalars.** Each subchart's `imageTag` value becomes `image: "<repo>:<exact-tag>"`, and templates use `{{ .Values.image }}`. Dependabot's helm ecosystem reads `image:` scalars and `repository`+`tag` pairs, and skips digest-pinned images, so chart images use exact tags, not digests. Floating tags become exact: `postgres:16` → `16.15`, `mysql:8.0` → `8.0.46`, `redis:7.4-alpine` → `7.4.11-alpine` (resolved at plan time and re-checked at execution). The Authentik tls-proxy's digest pin becomes the tracked tag `1.30.x-alpine`, trading immutability for automated updates.
- TEI and Ollama no longer appear in the chart as third-party images (2c); their bases are digest-pinned in the model Dockerfiles.
- CNPG and Strimzi operand images come from their operators' defaults and are pinned through the operators' pinned chart versions; unchanged.
- **`.github/dependabot.yml`** gains:
  - `docker` for every Dockerfile directory (`/Iverson.AdminUI`, `/Iverson.Server/Iverson.Api`, `/Iverson.Server/Iverson.Launcher`, `/Iverson.Server/Iverson.Events`, `/Iverson.Server/Iverson.Sql`, `/Iverson.Server/Iverson.Vector`, and the two model-image directories);
  - `docker-compose` for `/Iverson.Server`;
  - `terraform` for `/Iverson.Server/deploy/terraform/{aws,azure,gcp,bootstrap/azure}`;
  - `helm` for the umbrella chart and each subchart directory (the helm fetcher reads one directory per entry).
- Compose third-party images are digest-pinned (Dependabot's compose updater refreshes `tag@sha256` pins).

### 4e. Image scanning

A new workflow, `.github/workflows/image-scan.yml` (pull requests, pushes to main, weekly):
- builds `iverson-api` and `iverson-admin-ui` and scans them with trivy, failing on fixable HIGH or CRITICAL findings;
- renders the chart for each cloud overlay, extracts every third-party image reference, and scans each report-only;
- pins `aquasecurity/trivy-action` by full commit SHA at v0.36.0 (`ed142fd0…`), above the 2026-03 compromise's affected range (GHSA-69fq-xp46-6x23), and pins the trivy version.

The model images are too large to build in CI; their base images are covered by Dependabot's docker ecosystem.

---

## 5. Tests and verification

### 5a. Helm (all five overlays)
- `helm dependency build`, then `helm lint`, `helm template | kubeconform`, `kube-score` as `deploy-validate.yml` runs them.
- Rendered assertions: no NetworkPolicy contains `from: []` or a `to: []` rule; every DNS rule is the kube-dns helper; API-server rules use `apiServerCidrs`; TEI/Ollama egress policies have no rules; `clusterCidrs` per 1a; image references are exact tags and model images use the derived names; the guards fail on unset or blank `apiServerCidrs`.

### 5b. kind live (throwaway cluster, Calico v3.32.2, `values-laptop`)
- Every pod reaches Ready under the new rules.
- From a pod outside each allowed set: 8080, 8081, 9090, Authentik 9000 and 9080 refused. Port-forward to Authentik 9000 works.
- DNS resolves through kube-dns; a query to an outside resolver fails.
- CNPG and Strimzi operands run (they reach the API server through `apiServerCidrs`).
- TEI and Ollama serve from baked images with no egress; a probe pod sharing their labels cannot resolve or connect outward.
- The existing laptop smoke flow (LoadTest `write-path --count 10 --concurrency 1`, the API and console through ingress) passes.

### 5c. Images
- A canary `.env` and worktree file in a scratch copy of the context never reach the Api and console build stages.
- `iverson-api` and `iverson-admin-ui` build and start; the console's `npm ci`, `vite build` and tests pass on Node 22.
- The model images build; the Ollama build fails with a wrong digest that shares the real digest's 12-character prefix.
- trivy runs locally against the two first-party images.

### 5d. Terraform (static)
- `terraform fmt -check -recursive`; `init -backend=false -lockfile=readonly` and `validate` for `aws`, `azure`, `gcp`, `bootstrap/azure`; `tfsec`. No new provider is introduced.

### 5e. Workflows
- `actionlint` if available; otherwise YAML parse plus a review that every `uses:` is SHA-pinned.

---

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| 1 | NetworkPolicy always admits a pod's own node | Kubernetes NetworkPolicy docs: "traffic to and from the node where a Pod is running is always allowed, regardless of the IP address of the Pod or the node" |
| 2 | EKS VPC CNI agent admits node traffic (kubelet probes) with no rule | EKS best-practices network-security: "only allowed connections into the pod are those from the pod's node and those allowed by the ingress rules"; aws-network-policy-agent #635 (node IP auto-allowed for probes; ClusterNetworkPolicy gap fixed in 1.4, not our case) |
| 3 | Calico admits host-to-local-workload traffic | Tigera docs (protect-hosts): "Calico allows all connections from processes running on the host to guest workloads on the host" |
| 4 | On kind/Calico, a pod under default-deny with no ingress rule passes HTTP probes | Live (Calico v3.32.2, kind via podman): `web` Deployment with readiness/liveness on 8080 reached `1/1 Running` with only a default-deny policy |
| 5 | `kubectl port-forward` is not subject to NetworkPolicy | Live: port-forward to the default-denied `web` returned HTTP 200 |
| 6 | Port 9000 has no in-cluster callers | API uses `Authentik__BaseUrl` and metadata on 8443 (`charts/api/templates/deployment.yaml:186-196`); `:9000` values there are issuer strings. Other `authentik:9000` references are host-side tools and tests (LoadTest, ClientConformance `TokenBroker.cs`, `mint_acting_user_token.py`) |
| 7 | AWS public subnets are `10.0.128.0/20`, `10.0.144.0/20`, tagged for the ALB; private are `10.0.0.0/20`, `10.0.16.0/20`; EKS `vpc_config` uses both sets | `modules/cluster-aws/main.tf:87-112` (`cidrsubnet(var.vpc_cidr,4,0/1)` and `(…,8/9)`, `kubernetes.io/role/elb`), `:263` (`concat(private, public)`); `vpc_cidr` default `10.0.0.0/16`, not overridden |
| 8 | The chart's NetworkPolicies live in one file; its allow-all ingress rules are exactly admin-ui 8080, Authentik 9000 and 9080 | `grep -l 'kind: NetworkPolicy'` → `templates/networkpolicies.yaml` only; `from: []` at `:514, :605, :615` |
| 9 | Azure AGIC subnet is `10.1.16.0/24`; aks subnet `10.1.0.0/20`; `10.1.17.0/28` free | `modules/cluster-azure/main.tf:126-137, 194-196`; AKS default `service_cidr` is outside the VNet |
| 10 | GKE pods are not in the node subnet; LB ranges are fixed | `values-gcp.yaml:4-17` (VPC-native, Dataplane V2, container-native LB); `modules/cluster-gcp/main.tf:147-148, 172` |
| 11 | All 14 DNS rules are `to: []`; only Postgres and Kafka have API-server rules; only TEI and Ollama have 443 | `grep 'port: 53'` → 14, every preceding `to:` is `[]`; `port: (443|6443)` at `:212, :274, :462, :493` |
| 12 | kube-dns selector, labels and ports | Live kind: kube-dns Service selector `{"k8s-app":"kube-dns"}`, 53→53; `kube-system` labelled `kubernetes.io/metadata.name=kube-system`. EKS/AKS/GKE docs use `-l k8s-app=kube-dns` |
| 13 | A kube-dns-only egress rule resolves names and blocks outside resolvers | Live: `nslookup kubernetes.default…` answered; `nslookup … 8.8.8.8` failed |
| 14 | EKS API-server traffic must be allowed by DNAT'd endpoint (ClusterIP pre-DNAT resolution) and the EKS ENIs move on upgrade | amazon-network-policy-controller-k8s README (pre-DNAT resolution; podSelector must match Service selector); EKS network-reqs: interfaces "deleted" and recreated on version update, in the cluster subnets |
| 15 | On kind the API endpoint is the node IP on 6443, and under podman that is `10.89.x` | Live: EndpointSlice `kubernetes` → `10.89.1.3:6443` |
| 16 | An ipBlock on the node network admits API-server traffic via the `kubernetes` Service, and nothing else does | Live: `nc kubernetes.default 443` OPEN with ipBlock `10.89.0.0/16`; BLOCKED for a pod with only the DNS rule |
| 17 | AKS API IP is public and can change without VNet Integration; with it, an ILB VIP in the delegated subnet | Microsoft Learn outbound-rules ("your API server IP might change"); api-server-vnet-integration ("projects the API server endpoint directly into a delegated subnet") |
| 18 | GKE nodes reach the control plane on the internal endpoint in `master_ipv4_cidr_block` (`172.16.0.0/28`) | GKE private-cluster concept ("Nodes will communicate with the control plane on the internal endpoint only"); `modules/cluster-gcp/main.tf:183`. The in-cluster Endpoints content is not documented (Known issues) |
| 19 | TEI serves offline from a read-only cache as uid 1000 | Live: TEI cpu-1.8 with `--network none --read-only --user 1000:1000`, `HF_HUB_OFFLINE=1`, cache mounted `:ro` → Ready, `/embed` answered |
| 20 | A TEI image can be built with the pinned revision baked in | Live: prototype Dockerfile (2c) built `1.13 GB`; run with no network, read-only, uid 1000, no volume → `/embed` answered |
| 21 | Ollama serves offline from a read-only models directory as uid 1000 with `HOME` on tmpfs | Live: `ollama/ollama:0.12.11`, `--network none --read-only --user 1000:1000 -e HOME=/tmp`, models `:ro` → embedding returned |
| 22 | An Ollama image can be built with the model baked in and the full digest checked | Live: prototype (2c) built with the correct digest; a digest sharing the real one's first 12 characters failed the build with the FATAL message |
| 23 | The Ollama manifest file's SHA-256 is the registry's full digest; `ollama list` shows 12 characters | Live (`all-minilm:22m`): `sha256sum` of the manifest = `ollama-content-digest` header = `1b226e28…75ef`; list ID `1b226e2802db` |
| 24 | `global.generativeModelDigest` holds a full 64-hex digest | `values.yaml:60` |
| 25 | The API and worker never ask Ollama to pull | `grep api/pull|PullModel` in `Iverson.Server/**/*.cs`: none; they only set the base URL and model name |
| 26 | Hugging Face weights redirect to a CDN host, and TEI honours `HF_ENDPOINT` | Live: `resolve/<rev>/model.safetensors` → 302 to `us.aws.cdn.hf.co`; TEI with `HF_ENDPOINT=http://mirror.invalid` requested that host (supports the image choice over a mirror) |
| 27 | azurerm 3.117.1 accepts every attribute named in 3a–3c | Locked-provider schema dump and scratch `terraform validate`: `local_account_disabled`; AAD block `managed`, `azure_rbac_enabled`, `tenant_id`, `admin_group_object_ids`; `api_server_access_profile.{authorized_ip_ranges, vnet_integration_enabled, subnet_id}`; subnet `delegation`; storage `shared_access_key_enabled`, `network_rules`; provider `storage_use_azuread`; container scope via `resource_manager_id` |
| 28 | VNet Integration needs a pre-provisioned cluster identity with Network Contributor on both subnets | Microsoft Learn api-server-vnet-integration (bring-your-own VNet: identity needs permissions on the API-server and cluster subnets; missing permissions "can cause a provisioning failure") |
| 29 | azurerm 3.117.1 updates `identity` in place; adding `api_server_access_profile.subnet_id` does not force replacement | Provider source v3.117.1 `kubernetes_cluster_resource.go`: `if d.HasChange("identity") { … existing.Model.Identity = expandedIdentity }`; `ForceNewIfChange("api_server_access_profile.0.subnet_id", old != "" && new == "")` |
| 30 | With local accounts disabled, `kube_admin_config` is empty and `kube_config` carries no client certificate; kubelogin's AKS server ID is `6dae42f8-…` | azurerm docs (`kube_admin_config` only with local accounts enabled); kubelogin docs (AKS AAD server application ID) |
| 31 | kubernetes 2.38.0 and helm 2.17.0 providers support `exec` | Schema dump; scratch azure root with exec validated |
| 32 | `kube_config` output's only consumer is `azure/main.tf` | `grep` of the repo (incl. `docs/`, `.github`, scripts): `azure/main.tf:47-58` only, plus historical plans |
| 33 | `use_azuread_auth` is an azurerm backend setting; shared-key-off containers need `storage_use_azuread` | Terraform azurerm backend docs; azurerm v3.90 storage_account docs ("when Shared Key Access is disabled, you will need to enable the `storage_use_azuread` flag") |
| 34 | `bootstrap/azure` keeps local state | `bootstrap/azure/main.tf` has no `backend` block |
| 35 | aws 5.100.0 accepts hop limit 1; LB controller chart 1.9.0 takes `region` and `vpcId`; EBS CSI controller skips metadata given a region, and its node plugin falls back to Kubernetes metadata | Schema dump; aws-load-balancer-controller v2.9.0 `values.yaml`; aws-ebs-csi-driver v1.37.0 `docs/install.md` |
| 36 | EKS's IRSA webhook injects `AWS_REGION` | EKS Workshop (IRSA verification shows `AWS_REGION`/`AWS_DEFAULT_REGION` set "automatically to the same as our EKS cluster"); amazon-eks-pod-identity-webhook README |
| 37 | Only the LB controller and EBS CSI depend on pod metadata on EKS | Enumeration of `modules/operators` releases and EKS add-ons: autoscaler (IRSA + `awsRegion`), `aws-node`/`kube-proxy` (host network), CNPG/Strimzi/StarRocks (no AWS identity); no AWS SDK in `Iverson.Server/**/*.cs` |
| 38 | `cluster-aws` has no `vpc_id` output; a `vpc_id` variable with default `null` keeps all three operators call sites valid | Read `outputs.tf`; scratch validate of `aws/`, `azure/`, `gcp/` |
| 39 | All four roots validate offline with the planned changes and no new provider | Scratch-modified `aws/`, `azure/`, `bootstrap/azure` and unchanged `gcp/` validated with `-lockfile=readonly`; `tfsec` clean |
| 40 | The `.dockerignore` patterns exclude root and nested `.env*`, `.worktrees/`, `.claude/`, `docs/` | Live canary build: only `.dockerignore`, `Dockerfile` and the kept source file reached the image |
| 41 | The Api build needs exactly the nine directories in 4a; no props/global.json/nuget.config exist | Transitive `ProjectReference` closure of `Iverson.Api.csproj` plus `Protobuf Include` paths; no `Directory.*.props`, `global.json`, `nuget.config` |
| 42 | The Launcher Dockerfile is unreferenced and needs only its own directory | No reference in compose, scripts or CI; no project or package references |
| 43 | `vite build` does not read `.env.development` | Vite production mode; `.env.development` is the only tracked `.env*` file in `Iverson.AdminUI` |
| 44 | nginx stable is 1.30; `node:22-alpine` is 22.23.3 | Registry metadata: `nginx-unprivileged` `1.30-alpine`/`stable-alpine` share a digest; Node 22.23.3 satisfies `>=22.22.0` |
| 45 | nginx-unprivileged is used at exactly three sites | `Iverson.AdminUI/Dockerfile:24`, `docker-compose.yml:442`, `charts/authentik/templates/deployment-server.yaml:150` |
| 46 | `setup.sh` and `setup.ps1` install the same six charts; only `setup.sh` pins versions | Side-by-side read |
| 47 | Floating chart tags and their current exact versions | Registry lookups: `postgres:16`→16.15, `mysql:8.0`→8.0.46, TEI `cpu-1.8`→cpu-1.8.3, `redis:7.4-alpine`→7.4.11-alpine; CNPG operand from operator default, Strimzi from operator |
| 48 | `imageTag` has no consumers outside the chart | `grep`: 7 subchart templates and values, parent `values.yaml`, aws/azure/gcp overlays; none in CI, scripts, runbooks or tests |
| 49 | Dependabot helm reads `image:` scalars and skips digest-pinned images; reads one directory per entry | dependabot-core `helm/file_parser.rb` (`key == "image" && value.include?(":")`), `file_updater/image_updater.rb` ("digest-pinned images resolve by digest, so a tag-only bump would silently keep the old image" → `next`), `file_fetcher.rb` (`repo_contents` of the entry's directory) |
| 50 | Dependabot docker and docker-compose refresh `tag@sha256` pins; terraform updates providers, not `helm_release` | dependabot-core `docker/file_parser.rb` (`IMAGE_SPEC … @sha256`), docker_compose delegating to the docker checker, `terraform/file_parser.rb` (no helm handling) |
| 51 | The three console packages are unused | No import in `src/`, tests or `vite.config.ts`; `generated/` absent and gitignored |
| 52 | trivy-action v0.36.0 is at `ed142fd0…`; the 2026-03 compromise affected older versions | GitHub API; GHSA-69fq-xp46-6x23 (CVE-2026-33634) |
| 53 | The checkout/setup-node SHAs used elsewhere are v7.0.1/v7.0.0 | `.github/workflows/dependency-scan.yml:17-18` and peers; upstream tags |

---

## Known issues / accepted as out of scope

- **Azure NPM's kubelet exemption is not documented by Microsoft** (spec and user report only). If it does not hold, AKS pods fail readiness at deploy; the runbook gives the check and the fallback (node subnet in `clusterCidrs`). Azure NPM on Linux is retired on 2028-09-30; moving AKS to Cilium is not in scope.
- **GKE's in-cluster API endpoint** is inferred from the private-cluster documentation; the runbook checks `kubectl get endpointslices -n default -l kubernetes.io/service-name=kubernetes` on first deploy.
- **AKS changes are statically verified only:** the identity swap (system- to user-assigned), VNet Integration enablement (one-way, API IP change) and first-apply role propagation are covered by runbook steps, not by an apply. The VNet Integration attributes are deprecated in azurerm 3.x and must be renamed when the provider moves to 4.46+.
- **EBS CSI node plugin on Kubernetes metadata** assumes one ENI and one EBS volume when computing attach limits (driver docs); a later driver upgrade to v1.46+ with its volume-limit feature would need metadata again.
- **Compose still downloads models at runtime**, and the `.pyc`'s old password remains in git history.
- **Third-party image scan results are report-only;** only first-party images fail CI.
- **The laptop/local `apiServerCidrs`** covers docker's and podman's default kind networks; a kind network on another range needs the value overridden.
