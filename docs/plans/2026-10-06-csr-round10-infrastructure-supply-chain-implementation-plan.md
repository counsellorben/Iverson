# CSR Round 10 Sub-project D — Infrastructure and Supply Chain Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-10-06-csr-round10-infrastructure-supply-chain-design.md` (commit SHA: `52275c13`)

**Goal:** close CSR round-10 findings #6, #15, #17, #18 and #19 at every remediation tier the review lists.

**Architecture:**
- **Helm:** NetworkPolicies narrow ingress to load-balancer ranges, since the kubelet is always admitted. They scope DNS to kube-dns and the API-server rules to per-profile ranges, with values-only fallbacks.
- **Model images:** TEI and Ollama serve images with their weights baked in, built from Dockerfiles inside their subcharts and tagged by a hash of those Dockerfiles, so they need no egress.
- **Dependabot:** chart images become `image:` scalars it can track.
- **Terraform:**
  - AKS moves to Entra/Azure RBAC with a user-assigned identity and API Server VNet Integration.
  - The Azure state account is locked to Entra auth and an IP allowlist.
  - EKS drops the metadata-service hop limit to 1.
- **Supply chain:** build contexts exclude secrets, images are pinned, CI pins actions and scans images.

**Tech stack:**
- Helm 3.16 locally; CI uses alpine/helm 3.15 on GitLab and `azure/setup-helm` v5.0.1 on GitHub.
- kubeconform 0.6.7, kube-score 1.19.0.
- Terraform ≥1.7 with azurerm 3.117.1, aws 5.100.0, google 5.45.2, kubernetes 2.38.0 and helm 2.17.0 (locked).
- kind v0.24.0 on podman, Calico v3.32.2.
- TEI cpu-1.8.3 and Ollama 0.12.11.
- Node 22 and nginx-unprivileged 1.30.5.
- trivy-action v0.36.0 and trivy v0.75.0.

---

## Global Constraints

- **One branch, one merge.** No proto, SDK or API code change. Work in the worktree `.worktrees/csr10-infra-supply-chain` on branch `csr10-infra-supply-chain`.
- **Terraform is checked statically only, never applied.** No cloud account is used. The checks are `terraform fmt`, `init -backend=false -lockfile=readonly`, `validate`, `tfsec`, and the locked provider schemas: azurerm 3.117.1, aws 5.100.0, google 5.45.2, kubernetes 2.38.0 and helm 2.17.0. Helm and NetworkPolicy behaviour is verified live on a throwaway kind cluster with Calico.
- **Security write-ups** (comments, test names, docs) stay at the level of conditions and behaviour, never step-by-step exploitation.
- **`docs/` is gitignored**, so commit docs files with `git add -f`.
- **The user's running compose stack** (`iversonserver` containers, volumes, networks, images) is never started, stopped or modified. Another session's `rfdt-qdrant`/`rfdt-tei` containers and `tei_models` volume are never touched. Live checks use throwaway resources the check creates and deletes. Do not run `docker compose up` against `Iverson.Server/docker-compose.yml`; validate compose edits with `docker compose config` only.
- **Memory.** One heavy process at a time, because a WSL OOM kills the whole VM.
  - Image builds run with `--memory` caps. Kind image loads stage on disk under `/var/tmp`, never in `/tmp`, which is RAM-backed tmpfs here.
  - Check `free -g` before each heavy step.
  - Kill processes by PID, never with `pkill -f`.
- **Re-package subcharts before rendering.** Run `helm dependency build` in the chart directory before every `helm template`/`helm lint`. A stale `charts/*.tgz` renders old subchart content, including an old model-image Dockerfile hash.
- **`kube-score` already fails on `main`** (pre-existing criticals in authentik, redis and three NetworkPolicies). The Helm check is therefore "no new criticals compared with the baseline", never "exit 0".

## File Structure

**Create:**
- `Iverson.Server/deploy/helm/iverson/charts/tei/model-image/Dockerfile`: TEI with one model's pinned weights baked in.
- `Iverson.Server/deploy/helm/iverson/charts/ollama/model-image/Dockerfile`: Ollama with the generative model baked in, built only if the full manifest digest matches.
- `.github/workflows/image-scan.yml`: trivy scans of the first-party images (gating) and the chart's third-party images (report-only).
- `docs/runbooks/csr10-infrastructure-cutover.md`: operator steps for the cloud cutover.

**Modify, Helm (`Iverson.Server/deploy/helm/iverson/`):**
- `templates/networkpolicies.yaml`: ingress narrowing, DNS and API-server helpers, guards, empty TEI/Ollama egress.
- `templates/_helpers.tpl`: `iverson.dnsEgress` and `iverson.apiServerEgress`.
- `values.yaml`, `values-{laptop,local,aws,azure,gcp}.yaml`: `clusterCidrs`, `apiServerCidrs`, fallbacks, `modelImageRegistry`, image scalars, removal of TEI/Ollama storage values.
- `charts/tei/{templates/statefulset.yaml,values.yaml}`, `charts/ollama/{templates/statefulset.yaml,values.yaml}`: model images and no PVCs.
- `charts/{qdrant,jaeger,prometheus,redis,authentik}/{templates/*,values.yaml}`, `charts/authentik/templates/job-revoke-cross-db.yaml`, `charts/starrocks/{templates/job-create-user.yaml,values.yaml}`: image scalars.

**Modify, kind:** `Iverson.Server/deploy/kind/build-and-load-image.{sh,ps1}` (model-image mode) and `setup.{sh,ps1}` (closing note; `.ps1` version pins).

**Modify, build and CI:**
- `.dockerignore` and `Iverson.Server/.dockerignore`
- `Iverson.Server/Iverson.Api/Dockerfile`, `Iverson.Server/Iverson.Launcher/Dockerfile`, `Iverson.AdminUI/Dockerfile`
- `Iverson.Server/docker-compose.yml`
- `Iverson.AdminUI/package.json` and `package-lock.json`
- `.github/dependabot.yml`, `.github/workflows/admin-ui.yml`
- Delete from the index: `Iverson.Server/deploy/scripts/__pycache__/mint_acting_user_token.cpython-314.pyc`

**Modify, Terraform (`Iverson.Server/deploy/terraform/`):**
- `modules/cluster-azure/{main,variables,outputs}.tf`
- `azure/main.tf`
- `bootstrap/azure/main.tf`
- `modules/cluster-aws/{main,outputs}.tf`
- `modules/operators/{main,variables}.tf`
- `aws/main.tf`

## Inherited from spec

These were verified by `thorough-brainstorming` and three design reviews, and are not re-verified here. Row numbers are the spec's.

| # | Assumption | Evidence |
|---|---|---|
| 1–3 | NetworkPolicy admits a pod's own node (k8s spec; EKS VPC CNI; Calico) | k8s docs, EKS best practices, Tigera docs |
| 4–5 | On kind/Calico, probes pass under default-deny with no rule; port-forward is not filtered | live kind runs |
| 6 | Port 9000 has no in-cluster callers | `charts/api/templates/deployment.yaml:186-196` |
| 7 | AWS public subnets `10.0.128.0/20`, `10.0.144.0/20`; private `10.0.0.0/20`, `10.0.16.0/20`; EKS uses both | `cluster-aws/main.tf:87-112, 263` |
| 8 | Allow-all ingress rules are admin-ui 8080, Authentik 9000 and 9080 | `networkpolicies.yaml:514, 605, 615` |
| 9 | AGIC subnet `10.1.16.0/24`, aks `10.1.0.0/20`, `10.1.17.0/28` free | `cluster-azure/main.tf` |
| 10 | GKE pods not in the node subnet | `values-gcp.yaml`, `cluster-gcp/main.tf` |
| 11 | 14 DNS rules `to: []`; API-server rules only Postgres/Kafka; 443 only TEI/Ollama | grep |
| 12–13, 15–16 | kube-dns selector, labels and ports; DNS via kube-dns works; kind API endpoint and ipBlock matching | live kind |
| 14, 54 | EKS matches pre-DNAT on the `kubernetes` ClusterIP; `10.100.0.1`/`172.20.0.1` | controller source, EKS docs |
| 17–18 | AKS API IP and VNet Integration; GKE internal endpoint | Microsoft Learn, GKE docs |
| 19–23 | TEI and Ollama serve offline read-only as uid 1000; images buildable; full Ollama digest | live runs |
| 24–26 | Digest value; no runtime pulls; HF redirects/`HF_ENDPOINT` | `values.yaml:60`, grep, live |
| 27–33 | azurerm attributes; UAI need; in-place identity; Entra kubeconfig; providers `exec`; `kube_config` consumers; backend Entra auth | schema dumps, docs, provider source |
| 34–39 | Bootstrap local state; EKS IMDS/IRSA; operators wiring; offline validate | read, docs, scratch validate |
| 40–47 | `.dockerignore` patterns; Api COPY set; Launcher; vite; image versions; nginx sites; setup pins; floating tags | canary build, reads, registry |
| 48–53 | `imageTag` consumers; Dependabot helm/docker/compose/terraform behaviour; unused deps; trivy-action; action SHAs | grep, dependabot-core source, GitHub API |
| 55–58 | Ollama `/data` env override; laptop renders; subchart Dockerfile hash renders; Dependabot docker reads entry dirs | probes, renders |

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | Path | `charts/{tei,ollama}/model-image/`, `docs/runbooks/csr10-infrastructure-cutover.md`, `.github/workflows/image-scan.yml` are new | `ls` → "No such file or directory" for each |
| 2 | Path | Overlays `values-{laptop,local,aws,azure,gcp}.yaml` plus `values-{aws,azure,gcp}.ci-override.yaml` exist; CI renders each cloud overlay with its ci-override | `ls`; `deploy-validate.yml:28-40`; `.gitlab-ci.yml:39-45` |
| 3 | Symbol | `iverson.validateNoPlaceholders` is defined in `templates/_validate.tpl:23` and included at `networkpolicies.yaml:1`; umbrella `_helpers.tpl` helpers are callable from `networkpolicies.yaml` | read; the prototype render used the new helpers |
| 4 | Symbol | Subchart templates see `.Values.global.*` and `$.Files.Get "model-image/Dockerfile"` from inside a `range` | prototype render: `iverson-tei-model:bge-base-a5beb1e3e68b-c2d6955415bf`, suffix equal to `sha256sum` of the file |
| 5 | Command | Helm v3.16.4 locally (3.15 in GitLab CI) has `sha256sum`, `trunc`, `replace`, `fail`, `required` | `helm version`; prototype renders |
| 6 | Code | The prototype of Tasks 1–4 renders all 5 overlays with kubeconform 0 invalid, and the §5a assertions pass | scratch prototype: local 100/94 valid, laptop 79/74, aws/azure/gcp 106/100, 0 invalid; assertions PASS ×5 |
| 7 | Code | Tasks 1–4 add no kube-score critical; the baseline already has 18–22 per overlay | `comm` of baseline against prototype criticals: no new or removed lines on any overlay |
| 8 | Code | Guards: unset `apiServerCidrs` → `fail` "must list at least one CIDR"; `--set networkPolicy.apiServerCidrs={}` → `fail` "blank entry" | prototype `helm template` error lines (`networkpolicies.yaml:7:4`, `:11:4`) |
| 9 | Code | `dnsAnyDestination=true` renders `to: []` only for port-53 rules; `apiServerAnyDestination=true` renders `to: []` only for postgres-egress and kafka-egress on 443/6443 | prototype renders |
| 10 | Code | Editing `charts/ollama/model-image/Dockerfile` and re-running `helm dependency build` changes the rendered tag suffix; a stale `.tgz` does not | prototype: `…-e9deef361c4d` → `…-946d9ceb1d45` after dep build; unchanged without it |
| 11 | Code | `egress: []` with `policyTypes: ["Egress"]` validates and denies all egress | kubeconform on the prototype renders |
| 12 | Code | The `--model-images` script logic (full render, awk/sed parse) yields TEI `MODEL_ID`/`REVISION` and Ollama `MODEL`/full `DIGEST`, and builds only TEI when Ollama is disabled | dry run against the prototype: 2 builds; with `ollama.enabled=false`, 1 build |
| 13 | Command | `kind load image-archive` exists in kind v0.24.0; `/var/tmp` is ext4 on disk and `/tmp` is tmpfs | `kind load image-archive --help`; `df -hT` |
| 14 | Code | All Terraform edits (Tasks 7–8) pass `fmt -check` and `validate` for aws, azure, gcp and bootstrap/azure, and `tfsec` reports no problems | scratch copy with locked providers: 4 × "Success! The configuration is valid."; `tfsec`: "No problems detected!" |
| 15 | Code | `modules/cluster-azure/main.tf` has two `identity { type = "SystemAssigned" }` blocks (the DES at `:91`, the cluster at `:175`); Task 7 anchors on the cluster's block, followed by the `# network_policy` comment | grep `-A3 'identity {'` |
| 16 | Code | `aws_vpc.this` is the VPC resource in cluster-aws | `cluster-aws/main.tf:14` |
| 17 | Digest | Multi-arch index digests at plan time: <ul><li>TEI `cpu-1.8.3` `sha256:8de25e75ce39617f17f2f6c77d60a4f75b65e779ed5005420eb1400072a15c1c`</li><li>ollama `0.12.11` `sha256:3d8a05e3432d50ea57594fabe971e46cc8fe963a0f9f8c40400bd56cd5388e47`</li><li>node `22-alpine` `sha256:0a7108bf6c7bf5de370ffb1a3ed6be93d405b43ff159f681a8d18c0e2bc2e402`</li><li>nginx-unprivileged `1.30.5-alpine` `sha256:15c994d10d6d78658721c3bcafff14cb281fba2a4bdf9d5ba92c416a472516e3`</li></ul>Compose digests are in Task 5 | registry `Docker-Content-Digest` (index Accept header) via `scratchpad/planverify-helm/digest.sh` |
| 18 | Command | trivy-action `v0.36.0` is commit `ed142fd0673e97e23eac54620cfb913e5ce36c25`, with inputs `image-ref`, `exit-code`, `severity`, `ignore-unfixed`, `version`, `format`, `scan-type`; image `aquasec/trivy:0.75.0` is `sha256:af6acf9a6b85dfe389a1941505c0ce9efef52a4719635e1a962f022a3d855daa` | GitHub API tag dereference; `action.yaml` at that SHA; registry |
| 19 | Command | `actions/setup-node` v7.0.0 = `820762786026740c76f36085b0efc47a31fe5020`; `azure/setup-helm` v5.0.1 = `9bc31f4ebc9c6b171d7bfbaa5d006ae7abdb4310` | GitHub API; `deploy-validate.yml:21` |
| 20 | Command | `actionlint` is not installed; `python3` with PyYAML 6.0.3 is | `which`; `import yaml` |
| 21 | Command | Host Node is v22.16.0, below the console's `>=22.22.0`, so npm prints `EBADENGINE` warnings without failing. The Node 22.23 build check runs in Docker | `node -v`; `package.json` engines |
| 22 | Consumer | `setup.sh:180` and `setup.ps1:102` refer to `tei.storageSize`/`ollama.storageSize`, so Task 3 rewrites them; `at-rest-encryption-verification.md` stays valid (its PVC check is a subset of seven classes, still created by Terraform) | grep; read `:70-85, 143-152` |
| 23 | Consumer | No tfvars or examples in the repo need the new required variables (`cluster_admin_group_object_ids`, `state_authorized_ip_ranges`) | `git ls-files` shows no `*.tfvars` |
| 24 | Consumer | Every Helm caller (`deploy-validate.yml`, `.gitlab-ci.yml`, `setup.{sh,ps1}`) renders overlays that set `apiServerCidrs` | callers grep; all 5 overlays set it in the prototype |
| 25 | Consumer | Removing `kube_config` and adding `vpc_id` (default `null`) keeps all three operators call sites valid; for non-AWS clouds the LB controller release has `count = 0` | scratch validate of aws, azure and gcp |
| 26 | Behaviour | LoadTest `write-path` cannot run against kind from the host: its closing Kafka lag report (`WritePathRunner.cs:218-237`) connects to Kafka, whose brokers advertise in-cluster addresses. §5b pass 1 uses checks proven on kind instead (spec amended `52275c13`, the user's choice) | read; memory of the unexercised kind target |
| 27 | Behaviour | gRPC through ingress-nginx on kind needs TLS with ALPN h2 on 8443 | spec 2026-09-24 api-ingress-nginx-grpc, live verified there |
| 28 | Path | The user's compose stack is not running now; ports 8080/8443 are free | `docker ps` empty; `ss -ltn` no match |
| 29 | Ordering | Without `depends_on = [module.cluster]`, no `module.operators` resource waits for `azurerm_role_assignment.deployer_cluster_admin`; with it, they all do. The operators module has no `data` sources | `terraform graph` (Terraform 1.9.8) on the patched azure root: 0 operator→`deployer_cluster_admin` edges before, 12 after; `validate` passes; `grep -c '^data ' modules/operators/*.tf` → 0 (CIR-1 §2.2, re-run at UIP) |
| 30 | Ordering | In one bootstrap apply, the storage account is updated before `deployer_state_data` is created, and the container waits for the role | `terraform graph`: `deployer_state_data -> azurerm_storage_account.state`, `azurerm_storage_container.state -> deployer_state_data` (CIR-1 §2.3, re-run at UIP) |
| 31 | Command | `terraform validate -no-color` output ends in a blank line, so `\| tail -1` prints nothing; `\| head -1` prints `Success! …` | `od -c`: `…is valid.\n\n` (CIR-1 §2.8, re-run at UIP) |
| 32 | Command | `docker compose config -q` needs the untracked `Iverson.Server/.env`; `--no-interpolate -q` validates without it and still rejects a malformed `image:` line | `git archive` copy: plain `config` exits non-zero, `--no-interpolate` → `compose-ok` (UIP run); malformed-line rejection (CIR-1 §2.6) |
| 33 | Code | `curlimages/curl:8.18.0` has BusyBox `nc` (`-z -w`) and `nslookup`; a pod selected only by `default-deny` has no egress | `docker run … nc -z -w 2 127.0.0.1 1` → `closed-rc=1`; BusyBox v1.37.0 (UIP run); laptop render: only `iverson-default-deny` selects `app=csr10d-outsider` (CIR-1 §2.1) |
| 34 | Code | With the plan's `.dockerignore`, `.env*` files inside `Iverson.AdminUI/` and `Iverson.Server/Iverson.Api/` (incl. the tracked `.env.development`) never reach a build that copies those directories; with `HEAD`'s they do | canary build copying both directories: no `.env*` with the plan's additions (UIP run); `HEAD`'s `.dockerignore` let `/api/.env`, `/src/.env.development`, `/src/.env.local` through (CIR-1 §2.4) |

## Tasks

### Task 1: Ingress narrowing (#15)

**Files:**
- Modify: `Iverson.Server/deploy/helm/iverson/templates/networkpolicies.yaml` (api-ingress comment; admin-ui-ingress; prometheus-ingress; authentik-ingress)
- Modify: `Iverson.Server/deploy/helm/iverson/values.yaml` (networkPolicy comment)
- Modify: `values-aws.yaml`, `values-azure.yaml`, `values-gcp.yaml`, `values-laptop.yaml`, `values-local.yaml` (the comment before `networkPolicy:`; `clusterCidrs` on aws/azure/gcp)

**Interfaces:**
- Produces: `networkPolicy.clusterCidrs`, which now means "load-balancer source ranges". Task 2 adds keys under the same `networkPolicy:` block.

- [ ] **Step 1: Rewrite the api-ingress comment.** In `networkpolicies.yaml`, replace the whole comment block that starts `# api is reachable from outside the namespace two ways` and ends just before `apiVersion: networking.k8s.io/v1` of `{{ .Release.Name }}-api-ingress` with:

```yaml
# api is reachable from outside the namespace two ways: through ingress-nginx
# (local/laptop, where it runs as an in-cluster namespace) or from the address ranges
# `.Values.networkPolicy.clusterCidrs` lists for the profile: the ranges the platform's
# load balancer connects from (AWS/Azure/GCP have no ingress-nginx namespace). An empty
# list is refused near the top of this file, since it would render an ingress rule that
# matches every source. The kubelet's readiness/liveness probes need no rule: traffic from
# the node a pod runs on is always admitted. Port 8081 also admits Prometheus, which
# scrapes /metrics from the dedicated Http1 health port rather than from 8080.
```

- [ ] **Step 2: admin-ui-ingress.**
  - Replace its comment's first three lines (`# admin-ui is a static nginx server … as its main ingress traffic. It needs no egress`) with the block below.
  - Replace its `spec:` with the `spec:` below, keeping the rest of the comment (`rule of its own …`) as is.
  - This deletes the second rule (`- from: []   # kubelet readiness/liveness probes…` and its `ports:`).

```yaml
# admin-ui is a static nginx server reachable from outside the namespace through
# ingress-nginx (local/laptop) or the profile's load balancer (`clusterCidrs`), on 8080.
# Its kubelet probes need no rule (node traffic is always admitted). It needs no egress
```

```yaml
spec:
  podSelector:
    matchLabels: { app: {{ .Release.Name }}-admin-ui }
  policyTypes: ["Ingress"]
  ingress:
    - from:
        - namespaceSelector:
            matchLabels: { kubernetes.io/metadata.name: ingress-nginx }
        {{- range .Values.networkPolicy.clusterCidrs }}
        - ipBlock: { cidr: {{ . }} }
        {{- end }}
      ports:
        - { protocol: TCP, port: 8080 }
```

- [ ] **Step 3: prometheus-ingress.** Replace from the API `podSelector` peer through the `{{- end }}` of its `clusterCidrs` range, which removes the range and its four kubelet comment lines, with:

```yaml
        - podSelector: { matchLabels: { app: {{ .Release.Name }}-api } }
               # api's own query path into Prometheus's HTTP API (the api-egress rule's
               # counterpart) — for the admin console landing page's live status. The
               # kubelet's /-/ready probe needs no rule: node traffic is always admitted.
```

- [ ] **Step 4: authentik-ingress.** Replace everything from `    - from: []   # kubelet readiness/liveness probes: authentik's server process serves both app` through `      ports: [{ protocol: TCP, port: 9080 }]` with the block below. That covers the 9000 rule, the `# CSR round-10 #4` comment and the 9080 allow-all. The 8443 rule above it stays.

```yaml
    # CSR round-10 #4: the tls-proxy sidecar's public listener, which the Authentik Ingress targets.
    # Port 9000 has no rule: its only callers are the kubelet (node traffic is always admitted),
    # the sidecar over loopback and `kubectl port-forward`, none of which NetworkPolicy filters.
    - from:
        - namespaceSelector:
            matchLabels: { kubernetes.io/metadata.name: ingress-nginx }
        {{- range .Values.networkPolicy.clusterCidrs }}
        - ipBlock: { cidr: {{ . }} }
        {{- end }}
      ports: [{ protocol: TCP, port: 9080 }]
```

- [ ] **Step 5: values.yaml comment.** Replace the block from `# networkPolicy.clusterCidrs has no default here on purpose.` up to, but not including, `networkPolicy:` with:

```yaml
# networkPolicy.clusterCidrs has no default here on purpose. It lists the address ranges
# the platform's load balancer connects from, for the api (8080, 8081), admin-ui (8080) and
# Authentik public-listener (9080) ingress rules. The kubelet needs no entry: NetworkPolicy
# always admits traffic from the node a pod runs on. That set is a different value per
# profile, so every values-*.yaml overlay MUST set its own. templates/networkpolicies.yaml
# fails the render with `fail` (not `required`, which lets an empty list through) if this is
# left unset or contains a blank entry (the shape `--set clusterCidrs={}` produces), rather
# than silently rendering an ingress rule that matches every source.
```

- [ ] **Step 6: Overlays.** In each file, replace the comment lines directly above `networkPolicy:` (the contiguous `#` lines) and the `clusterCidrs` line as follows:

`values-aws.yaml`:
```yaml
# modules/cluster-aws's two public subnets, where the internet-facing ALB (target-type ip)
# places its interfaces. Pods and nodes use the private subnets (VPC CNI), so they are not
# admitted. See values.yaml's networkPolicy comment.
networkPolicy:
  clusterCidrs: ["10.0.128.0/20", "10.0.144.0/20"]
```

`values-azure.yaml`:
```yaml
# modules/cluster-azure's App Gateway (AGIC) subnet. Pods and nodes share 10.1.0.0/20
# (Azure CNI), so they are not admitted. See values.yaml's networkPolicy comment.
networkPolicy:
  clusterCidrs: ["10.1.16.0/24"]
```

`values-gcp.yaml`:
```yaml
# GCLB and its health checks connect straight to pod IPs from Google's fixed ranges
# (container-native load balancing). The node subnet is not needed: the kubelet's probes
# are always admitted. See values.yaml's networkPolicy comment.
networkPolicy:
  clusterCidrs: ["130.211.0.0/22", "35.191.0.0/16"]
```

For `values-laptop.yaml` and `values-local.yaml`, keep `clusterCidrs: ["172.18.0.0/16"]`. In `values-local.yaml` keep the `# global.externalScheme is untouched, still "http".` line and the blank line before the block. Replace only the paragraph beginning `# 172.18.0.0/16 is kind's Docker bridge network` with:

```yaml
# kind has no load balancer: ingress-nginx is admitted by namespace. clusterCidrs must still
# list at least one range (see values.yaml's networkPolicy comment), so it names kind's docker
# network, which nothing else needs.
```

- [ ] **Step 7: Verify.** Run from the chart directory:

```bash
cd Iverson.Server/deploy/helm/iverson && helm dependency build . >/dev/null
for v in values-local values-laptop values-aws values-azure values-gcp; do x=""; [ -f $v.ci-override.yaml ] && x="-f $v.ci-override.yaml"
  helm template iverson . -f $v.yaml $x | python3 -c '
import sys, yaml
d = [x for x in yaml.safe_load_all(sys.stdin) if x and x.get("kind") == "NetworkPolicy"]
bad = [n["metadata"]["name"] for n in d for r in n["spec"].get("ingress") or [] if r.get("from") == []]
ak = [p["port"] for n in d if n["metadata"]["name"].endswith("authentik-ingress") for r in n["spec"]["ingress"] for p in r["ports"]]
print(sys.argv[1], "allow-all:", bad, "authentik ports:", sorted(ak))' $v; done
```

Expected: every overlay prints `allow-all: []` and `authentik ports: [8443, 9080]`.

- [ ] **Step 8: Commit.**

```bash
git add Iverson.Server/deploy/helm/iverson/templates/networkpolicies.yaml Iverson.Server/deploy/helm/iverson/values.yaml Iverson.Server/deploy/helm/iverson/values-{aws,azure,gcp,laptop,local}.yaml
git commit -m "narrow NetworkPolicy ingress to load-balancer ranges and drop kubelet-only allowances"
```

### Task 2: Egress for DNS and the API server (#17)

**Files:**
- Modify: `templates/_helpers.tpl` (append two helpers)
- Modify: `templates/networkpolicies.yaml` (guard; 14 DNS sites; 2 API-server sites)
- Modify: `values.yaml` (fallback values; comment); all 5 overlays (`apiServerCidrs`)

**Interfaces:**
- Consumes: Task 1's `networkPolicy:` blocks in the overlays.
- Produces:
  - Helpers `iverson.dnsEgress` and `iverson.apiServerEgress`.
  - Values `networkPolicy.apiServerCidrs`, `networkPolicy.dnsAnyDestination` and `networkPolicy.apiServerAnyDestination`.

- [ ] **Step 1: Append to `templates/_helpers.tpl`:**

```yaml

{{/*
The DNS egress rule every NetworkPolicy with policyTypes Egress needs: kube-dns pods only, on
53. Matched by the kube-dns Service's own selector (k8s-app=kube-dns), which EKS's network-policy
agent requires for traffic sent to a Service address. networkPolicy.dnsAnyDestination is the
first-deploy fallback: any destination on 53.
*/}}
{{- define "iverson.dnsEgress" -}}
{{- if .Values.networkPolicy.dnsAnyDestination }}
- to: []   # DNS (fallback: networkPolicy.dnsAnyDestination)
{{- else }}
- to: [{ namespaceSelector: { matchLabels: { kubernetes.io/metadata.name: kube-system } }, podSelector: { matchLabels: { k8s-app: kube-dns } } }]   # DNS
{{- end }}
  ports: [{ protocol: UDP, port: 53 }, { protocol: TCP, port: 53 }]
{{- end -}}

{{/*
The Kubernetes API server egress rule for operator-managed pods that call it directly (CNPG,
Strimzi): networkPolicy.apiServerCidrs on 443 and 6443. Both ports: kind/kubeadm serve the API on
6443, and engines that match after the Service address is rewritten see the endpoint's port.
networkPolicy.apiServerAnyDestination is the first-deploy fallback: any destination.
*/}}
{{- define "iverson.apiServerEgress" -}}
{{- if .Values.networkPolicy.apiServerAnyDestination }}
- to: []   # Kubernetes API server (fallback: networkPolicy.apiServerAnyDestination)
{{- else }}
- to:   # Kubernetes API server
  {{- range .Values.networkPolicy.apiServerCidrs }}
  - ipBlock: { cidr: {{ . }} }
  {{- end }}
{{- end }}
  ports: [{ protocol: TCP, port: 443 }, { protocol: TCP, port: 6443 }]
{{- end -}}
```

- [ ] **Step 2: Guard.** In `networkpolicies.yaml`, insert directly before the existing line `{{- range .Values.networkPolicy.clusterCidrs }}` (line 6):

```yaml
{{- if not .Values.networkPolicy.apiServerCidrs }}
{{- fail "networkPolicy.apiServerCidrs must list at least one CIDR; it scopes the Postgres and Kafka API-server egress rules" }}
{{- end }}
{{- range .Values.networkPolicy.apiServerCidrs }}
{{- if not (trim (toString .)) }}
{{- fail "networkPolicy.apiServerCidrs contains a blank entry; every entry must be a CIDR" }}
{{- end }}
{{- end }}
```

- [ ] **Step 3: DNS sites.** Every one of the 14 two-line DNS rules has this exact shape at four-space indent:

```
    - to: []   # DNS<optional suffix>
      ports: [{ protocol: UDP, port: 53 }, { protocol: TCP, port: 53 }]
```

Replace each pair with the single line:

```yaml
    {{- include "iverson.dnsEgress" . | nindent 4 }}
```

Do it mechanically and check the count:

```bash
cd Iverson.Server/deploy/helm/iverson && python3 - <<'PY'
import re
p = "templates/networkpolicies.yaml"; s = open(p).read()
s, n = re.subn(r"^    - to: \[\]   # DNS[^\n]*\n      ports: \[\{ protocol: UDP, port: 53 \}, \{ protocol: TCP, port: 53 \}\]$",
               '    {{- include "iverson.dnsEgress" . | nindent 4 }}', s, flags=re.M)
assert n == 14, n; open(p, "w").write(s); print("replaced", n)
PY
```

- [ ] **Step 4: API-server sites.**
  - In `postgres-egress`, replace the rule from `    - to: []   # Kubernetes API server: CNPG's own initdb/bootstrap job` through its `      ports: [{ protocol: TCP, port: 443 }, { protocol: TCP, port: 6443 }]` with block A below.
  - In `kafka-egress`, replace the rule from `    - to: []   # Kubernetes API server: the broker process resolves` through its `ports:` line with block B below.

Block A, `postgres-egress`:
```yaml
    # Kubernetes API server: CNPG's instance manager (baked into the postgres image, not a
    # sidecar) calls it directly to sync Cluster status; without it the Cluster never leaves
    # "Setting up primary".
    {{- include "iverson.apiServerEgress" . | nindent 4 }}
```

Block B, `kafka-egress`:
```yaml
    # Kubernetes API server: the broker resolves ${strimziSecretName:...} config placeholders
    # through Strimzi's KubernetesSecretConfigProvider at startup; without it the broker
    # crash-loops before storage format/listener startup.
    {{- include "iverson.apiServerEgress" . | nindent 4 }}
```

- [ ] **Step 5: values.yaml.**
  - Append to the networkPolicy comment block that Task 1 rewrote, after its last line and before `networkPolicy:`, the block below.
  - Then change `networkPolicy:\n  enabled: true` to the second block below.

```yaml
#
# networkPolicy.apiServerCidrs has no default either. It lists the address ranges Postgres
# (CNPG) and Kafka (Strimzi) pods may reach on 443/6443 to talk to the Kubernetes API server,
# guarded the same way. Every overlay sets its own.
#
# networkPolicy.dnsAnyDestination and networkPolicy.apiServerAnyDestination are first-deploy
# fallbacks only (docs/runbooks/csr10-infrastructure-cutover.md): true renders the port-only
# `to: []` rule for DNS (53) or the API server (443/6443) instead of the scoped one.
```

```yaml
networkPolicy:
  enabled: true
  dnsAnyDestination: false
  apiServerAnyDestination: false
```

- [ ] **Step 6: Overlays.** Add `apiServerCidrs` directly under each `clusterCidrs:` line:

`values-aws.yaml`:
```yaml
  # The four cluster subnets EKS places its control-plane interfaces in, plus the `kubernetes`
  # Service ClusterIP for both service ranges EKS may pick (cluster-aws sets none): the VPC
  # CNI network-policy agent matches pod egress before the Service address is rewritten.
  apiServerCidrs: ["10.0.0.0/20", "10.0.16.0/20", "10.0.128.0/20", "10.0.144.0/20", "10.100.0.1/32", "172.20.0.1/32"]
```

`values-azure.yaml`:
```yaml
  # The API Server VNet Integration subnet (modules/cluster-azure).
  apiServerCidrs: ["10.1.17.0/28"]
```

`values-gcp.yaml`:
```yaml
  # modules/cluster-gcp's master_ipv4_cidr_block (private cluster internal endpoint).
  apiServerCidrs: ["172.16.0.0/28"]
```

`values-laptop.yaml` and `values-local.yaml`:
```yaml
  # kind's node network under docker (172.18.0.0/16) and podman (its default pool,
  # 10.89.0.0/16); the control plane serves on 6443.
  apiServerCidrs: ["172.18.0.0/16", "10.89.0.0/16"]
```

- [ ] **Step 7: Verify.** Render all 5 overlays as in Task 1 Step 7, but with this checker:

```python
import sys, yaml
d = [x for x in yaml.safe_load_all(sys.stdin) if x and x.get("kind") == "NetworkPolicy"]
DNS = [{"namespaceSelector": {"matchLabels": {"kubernetes.io/metadata.name": "kube-system"}}, "podSelector": {"matchLabels": {"k8s-app": "kube-dns"}}}]
bad = []
for n in d:
    for r in n["spec"].get("egress") or []:
        ports = sorted((p["protocol"], p["port"]) for p in r.get("ports", []))
        if ports == [("TCP", 53), ("UDP", 53)] and r["to"] != DNS: bad.append(n["metadata"]["name"] + ":dns")
        if ports == [("TCP", 443), ("TCP", 6443)] and not all("ipBlock" in p for p in r["to"]): bad.append(n["metadata"]["name"] + ":api")
print(sys.argv[1], "bad:", bad)
```

Expected: `bad: []` on every overlay, with TEI/Ollama's 443 rules not yet touched; Task 3 removes them. Then check the guards and fallbacks on laptop:

```bash
helm template iverson . -f values-laptop.yaml --set-json 'networkPolicy.apiServerCidrs=null' 2>&1 | grep -o 'apiServerCidrs must list at least one CIDR'
helm template iverson . -f values-laptop.yaml --set 'networkPolicy.apiServerCidrs={}' 2>&1 | grep -o 'apiServerCidrs contains a blank entry'
helm template iverson . -f values-laptop.yaml --set networkPolicy.apiServerAnyDestination=true | python3 -c '
import sys, yaml
for x in yaml.safe_load_all(sys.stdin):
    if x and x.get("kind") == "NetworkPolicy":
        for r in x["spec"].get("egress") or []:
            if r.get("to") == []: print(x["metadata"]["name"], sorted(p["port"] for p in r["ports"]))'
helm template iverson . -f values-laptop.yaml --set networkPolicy.dnsAnyDestination=true | python3 -c '
import sys, yaml
ports = {tuple(sorted((p["protocol"], p["port"]) for p in r["ports"])) for x in yaml.safe_load_all(sys.stdin)
         if x and x.get("kind") == "NetworkPolicy" for r in x["spec"].get("egress") or [] if r.get("to") == []}
print("dns fallback port sets:", sorted(ports))'
```

Expected: both guard messages print; the API-server fallback prints only `iverson-postgres-egress [443, 6443]` and `iverson-kafka-egress [443, 6443]` (plus the TEI/Ollama 443 rules until Task 3); and the DNS fallback prints `dns fallback port sets: [(('TCP', 53), ('UDP', 53))]` (plus TEI/Ollama's `(('TCP', 443),)` until Task 3).

- [ ] **Step 8: Commit.** `git add` the helpers, `networkpolicies.yaml`, `values.yaml` and the 5 overlays, then `git commit -m "scope DNS egress to kube-dns and API-server egress to per-profile ranges, with values-only fallbacks"`.

### Task 3: Model images; TEI and Ollama get no egress (#17, #19 Ollama digest)

**Files:**
- Create: `charts/tei/model-image/Dockerfile`, `charts/ollama/model-image/Dockerfile`
- Modify: `charts/tei/templates/statefulset.yaml`, `charts/tei/values.yaml`, `charts/ollama/templates/statefulset.yaml`, `charts/ollama/values.yaml`
- Modify: `values.yaml` (tei/ollama blocks; `global.modelImageRegistry`; the generativeModel/Digest comments), the 5 overlays (tei/ollama storage keys)
- Modify: `templates/networkpolicies.yaml` (`tei-egress`, `ollama-egress`)
- Modify: `Iverson.Server/deploy/kind/build-and-load-image.sh`, `build-and-load-image.ps1`, `setup.sh:180`, `setup.ps1:102`

**Interfaces:**
- Consumes: Task 2's `iverson.dnsEgress` sites in `tei-egress`/`ollama-egress`, which this task deletes.
- Produces:
  - Image names `iverson-tei-model:<slug>-<rev12>-<dockerfile12>` and `iverson-ollama-model:<model>-<digest12>-<dockerfile12>`.
  - The Ollama StatefulSet annotations `iverson.io/model` and `iverson.io/model-digest`.
  - The script mode `--model-images`, which Task 10 uses.

- [ ] **Step 1: Create `charts/tei/model-image/Dockerfile`:**

```dockerfile
# TEI with one embedding model's weights baked in, so the serving pod needs no network (CSR
# round-10 #17). The chart derives this image's tag from the model slug, the pinned revision and
# a hash of this file; deploy/kind/build-and-load-image.sh --model-images builds it.
# Base pinned by multi-arch index digest (resolved 2026-10-06).
FROM ghcr.io/huggingface/text-embeddings-inference:cpu-1.8.3@sha256:8de25e75ce39617f17f2f6c77d60a4f75b65e779ed5005420eb1400072a15c1c AS fetch
ARG MODEL_ID
ARG REVISION
ENV HUGGINGFACE_HUB_CACHE=/models
# Run TEI once against the pinned revision to fill its hub cache (the same download path it uses
# at runtime), and fail the build if it never reports healthy.
RUN set -e; text-embeddings-router --model-id "$MODEL_ID" --revision "$REVISION" --port 8080 >/tmp/log 2>&1 & pid=$!; \
    for i in $(seq 1 150); do if curl -sf localhost:8080/health >/dev/null; then ok=1; break; fi; sleep 2; done; \
    kill $pid; wait $pid || true; [ "${ok:-}" = 1 ] || { tail -20 /tmp/log; exit 1; }; \
    find /models -name '*.lock' -delete

FROM ghcr.io/huggingface/text-embeddings-inference:cpu-1.8.3@sha256:8de25e75ce39617f17f2f6c77d60a4f75b65e779ed5005420eb1400072a15c1c
COPY --from=fetch /models /models
ENV HUGGINGFACE_HUB_CACHE=/models HF_HUB_OFFLINE=1
```

- [ ] **Step 2: Create `charts/ollama/model-image/Dockerfile`:**

```dockerfile
# Ollama with the generative model baked in, so the serving pod needs no network (CSR round-10
# #17). The build fails unless the pulled manifest's full SHA-256 equals DIGEST (CSR round-10
# #19: the previous check compared a 12-character prefix). The chart derives this image's tag
# from the model, the digest and a hash of this file.
# Base pinned by multi-arch index digest (resolved 2026-10-06).
FROM docker.io/ollama/ollama:0.12.11@sha256:3d8a05e3432d50ea57594fabe971e46cc8fe963a0f9f8c40400bd56cd5388e47 AS fetch
ARG MODEL
ARG DIGEST
ENV OLLAMA_MODELS=/models HOME=/tmp
RUN set -e; ollama serve >/tmp/log 2>&1 & pid=$!; sleep 3; ollama pull "$MODEL"; kill $pid; \
    name="${MODEL%%:*}"; tag="${MODEL#*:}"; case "$name" in */*) ;; *) name="library/$name";; esac; \
    actual=$(sha256sum "/models/manifests/registry.ollama.ai/$name/$tag" | cut -d' ' -f1); \
    [ "$actual" = "$DIGEST" ] || { echo "FATAL: $MODEL manifest $actual != pinned $DIGEST" >&2; exit 1; }

FROM docker.io/ollama/ollama:0.12.11@sha256:3d8a05e3432d50ea57594fabe971e46cc8fe963a0f9f8c40400bd56cd5388e47
COPY --from=fetch /models /models
ENV OLLAMA_MODELS=/models
```

- [ ] **Step 3: TEI StatefulSet** (`charts/tei/templates/statefulset.yaml`).

Replace the image line with:
```yaml
          image: "{{ $.Values.global.modelImageRegistry }}iverson-tei-model:{{ .slug }}-{{ trunc 12 .revision }}-{{ $.Files.Get "model-image/Dockerfile" | sha256sum | trunc 12 }}"
```

Replace the two comment lines above `startupProbe` (`# The first start downloads the weights from the Hub into /data; every later start loads` / `# them from the PVC in under a minute.`) with:
```yaml
          # The weights are baked into the image (charts/tei/model-image), so start-up only loads them.
```

Make three deletions:
- the `tei-data` volumeMount, keeping the `tmp` mount;
- the whole `volumeClaimTemplates:` block, from `  volumeClaimTemplates:` through `            storage: {{ $.Values.storageSize }}`;
- from `charts/tei/values.yaml`, the `storageSize`, `storageClassName` and `imageTag` lines.

- [ ] **Step 4: Ollama StatefulSet** (`charts/ollama/templates/statefulset.yaml`).

Add annotations under `metadata.name` of the StatefulSet:
```yaml
  annotations:
    # Build inputs for this StatefulSet's image (deploy/kind/build-and-load-image.sh reads them).
    iverson.io/model: {{ .Values.global.generativeModel | quote }}
    iverson.io/model-digest: {{ required "global.generativeModelDigest must pin the full manifest digest (CSR round-2 finding #6)" .Values.global.generativeModelDigest | quote }}
```

Delete the whole `initContainers:` block, from `      initContainers:` (line 43) up to, but not including, `      containers:` (line 93).

Replace the image line with:
```yaml
          image: "{{ .Values.global.modelImageRegistry }}iverson-ollama-model:{{ .Values.global.generativeModel | replace ":" "-" | replace "/" "-" }}-{{ trunc 12 .Values.global.generativeModelDigest }}-{{ .Files.Get "model-image/Dockerfile" | sha256sum | trunc 12 }}"
```

Replace the `env:` block (`OLLAMA_MODELS=/data`, `HOME=/data`) with:
```yaml
          env:
            # The model is baked in at /models (the image's OLLAMA_MODELS); HOME only needs a
            # writable directory for Ollama's key pair.
            - { name: HOME, value: "/tmp" }
```

Make three deletions:
- the `ollama-data` volumeMount;
- the `volumeClaimTemplates:` block;
- from `charts/ollama/values.yaml`, the `storageSize`, `storageClassName` and `imageTag` lines.

- [ ] **Step 5: Umbrella and overlay values.**
  - In `values.yaml`'s `ollama:` and `tei:` blocks, delete `storageSize`, `storageClassName` and `imageTag`.
  - Insert after the `generativeModelDigest:` line:

```yaml
  # Registry prefix for the two model images built from charts/{tei,ollama}/model-image (CSR
  # round-10 #17), e.g. "123456789012.dkr.ecr.us-east-1.amazonaws.com/". Empty on kind, where
  # deploy/kind/build-and-load-image.sh --model-images loads them into the cluster directly.
  modelImageRegistry: ""
```

  - Reword the two comments above `generativeModel` and `generativeModelDigest`:
    - "the ollama subchart PULLS it" becomes "the ollama model image BAKES it in".
    - The digest paragraph's "pull-model initContainer pulls the floating tag … refuses to start on a mismatch" becomes: "the ollama model image build (charts/ollama/model-image/Dockerfile) pulls the floating tag and fails unless the pulled manifest's full SHA-256 equals this value".
    - Keep the provenance sentence ("This is the manifest digest reported by the registry … as of 2026-09-10"), but drop "(also the short ID `ollama list` reports locally)".
  - In every overlay's `ollama:` and `tei:` blocks, delete `storageSize:` and `storageClassName:`. In `values-local.yaml`, also delete the 4-line comment above each `storageSize` (`# ollama|tei renders as a StatefulSet, so this feeds volumeClaimTemplates…`).

- [ ] **Step 6: Egress policies.** In `networkpolicies.yaml`, replace each `egress:` list of `tei-egress` and `ollama-egress` with:

```yaml
  # The weights are baked into the image (charts/tei/model-image), so TEI needs no egress at all.
  egress: []
```

```yaml
  # The model is baked into the image (charts/ollama/model-image), so Ollama needs no egress at all.
  egress: []
```

- [ ] **Step 7: `build-and-load-image.sh` model-image mode.**
  - Add a `--model-images` flag to the argument loop (`MODEL_IMAGES=1; shift`; default `MODEL_IMAGES=0`), and a `--values PATH` flag (`VALUES_REL`, default `Iverson.Server/deploy/helm/iverson/values-laptop.yaml`, repo-root-relative or absolute).
  - Document both in the usage header. In this mode the positional `tag` is ignored and `cluster-name` still applies, e.g. `build-and-load-image.sh 0.1.0 iverson --model-images`.
  - When `MODEL_IMAGES=1`, run the block below after `TAG`, `CLUSTER_NAME` and `REPO_ROOT` are set and instead of the app-image build, then exit 0.

```bash
CHART="${REPO_ROOT}/Iverson.Server/deploy/helm/iverson"
[[ "${VALUES_REL}" = /* ]] && VALUES="${VALUES_REL}" || VALUES="${REPO_ROOT}/${VALUES_REL}"
# The chart is the single source of the model pins and of the image tags (which hash the model
# Dockerfiles). Rebuild the packaged subcharts first: a stale charts/*.tgz would render the tag of
# an older Dockerfile.
(cd "${CHART}" && helm dependency build >/dev/null)
RENDER="$(helm template iverson "${CHART}" -f "${VALUES}")"

stage_and_load() {   # $1 = image as the chart renders it (bare name:tag)
  local qualified="docker.io/library/$1" archive
  archive="/var/tmp/$(echo "$1" | tr ':/' '__').tar"
  docker tag "$1" "${qualified}"   # same docker.io/library qualification as the app image, below
  # Stage on disk: the Ollama image is ~5.7 GB and /tmp may be RAM-backed.
  docker save -o "${archive}" "${qualified}"
  kind load image-archive "${archive}" --name "${CLUSTER_NAME}"
  rm -f "${archive}"
}

# TEI: one image per embeddingModels entry; image and --model-id/--revision come from the render.
while IFS='|' read -r image model_id revision; do
  [ -n "${image}" ] || continue
  echo "Building ${image}..."
  docker build --memory 3g -t "${image}" --build-arg MODEL_ID="${model_id}" --build-arg REVISION="${revision}" \
    -f "${CHART}/charts/tei/model-image/Dockerfile" "${CHART}/charts/tei/model-image"
  stage_and_load "${image}"
done < <(printf '%s\n' "${RENDER}" | awk '
  /image: "[^"]*iverson-tei-model:/ { match($0, /"[^"]+"/); img = substr($0, RSTART+1, RLENGTH-2) }
  /args: \["--model-id"/ { split($0, a, "\""); print img "|" a[4] "|" a[8] }')

# Ollama: image plus the model and full digest from the StatefulSet's annotations.
ollama_image=$(printf '%s\n' "${RENDER}" | sed -n 's/.*image: "\([^"]*iverson-ollama-model:[^"]*\)".*/\1/p' | head -1)
if [ -n "${ollama_image}" ]; then
  model=$(printf '%s\n' "${RENDER}" | sed -n 's/.*iverson.io\/model: "\(.*\)"/\1/p' | head -1)
  digest=$(printf '%s\n' "${RENDER}" | sed -n 's/.*iverson.io\/model-digest: "\(.*\)"/\1/p' | head -1)
  echo "Building ${ollama_image}..."
  docker build --memory 3g -t "${ollama_image}" --build-arg MODEL="${model}" --build-arg DIGEST="${digest}" \
    -f "${CHART}/charts/ollama/model-image/Dockerfile" "${CHART}/charts/ollama/model-image"
  stage_and_load "${ollama_image}"
fi
```

- [ ] **Step 8: `build-and-load-image.ps1` parity.**
  - Add `[switch]$ModelImages` and `[string]$Values = "Iverson.Server/deploy/helm/iverson/values-laptop.yaml"` to `param(...)`, and document them in the usage header.
  - When `$ModelImages` is set, run the same logic in PowerShell:
    - `Push-Location` to the chart, `helm dependency build`, `Pop-Location`.
    - `$render = helm template iverson $chart -f $valuesPath | Out-String`.
    - For TEI, match lines with `[regex]::Matches($render, 'image: "([^"]*iverson-tei-model:[^"]*)"')`, and pair each with the next `args: \["--model-id", "([^"]+)", "--revision", "([^"]+)"` match in order.
    - For Ollama, use `'image: "([^"]*iverson-ollama-model:[^"]*)"'`, `'iverson\.io/model: "([^"]+)"'` and `'iverson\.io/model-digest: "([^"]+)"'`.
    - For each image:
      - `docker build --memory 3g -t $img --build-arg ... -f <dockerfile> <dir>`;
      - `docker tag $img "docker.io/library/$img"`;
      - `$archive = Join-Path ([IO.Path]::GetTempPath()) (($img -replace '[:/]','_') + '.tar')`;
      - `docker save -o $archive "docker.io/library/$img"`;
      - `kind load image-archive $archive --name $ClusterName`;
      - `Remove-Item $archive`.
    - Then `exit 0`.
  - `pwsh` is not installed here (plan row 20), so this file gets a static review only: every `bash` step in Step 7 has a matching PowerShell line.

- [ ] **Step 9: Setup notes.** Replace the closing `Note:` line in `setup.sh` (line 180) with:

```bash
echo "Note: TEI and Ollama run model images with their weights baked in. Before installing the chart, build and load them: deploy/kind/build-and-load-image.sh 0.1.0 iverson --model-images (add --values <overlay> to match the overlay you install)."
```

Replace `setup.ps1`'s line 102 with the equivalent `Write-Host` line (`deploy/kind/build-and-load-image.ps1 -ModelImages`, optionally `-Values <overlay>`; `-ClusterName` applies).

- [ ] **Step 10: Verify.**

```bash
cd Iverson.Server/deploy/helm/iverson && helm dependency build . >/dev/null
helm template iverson . -f values-laptop.yaml | python3 -c '
import sys, yaml, re
docs = [d for d in yaml.safe_load_all(sys.stdin) if d]
for d in docs:
    n = d["metadata"]["name"]
    if d["kind"] == "StatefulSet" and ("tei" in n or "ollama" in n):
        c = d["spec"]["template"]["spec"]
        print(n, c["containers"][0]["image"], "init:", bool(c.get("initContainers")), "vct:", bool(d["spec"].get("volumeClaimTemplates")),
              "data refs:", "/data" in str(c))
    if d["kind"] == "NetworkPolicy" and n in ("iverson-tei-egress", "iverson-ollama-egress"):
        print(n, "egress:", d["spec"].get("egress"))'
bash -n ../../kind/build-and-load-image.sh && echo syntax-ok
```

Expected:
- `iverson-tei-bge-base iverson-tei-model:bge-base-a5beb1e3e68b-<12 hex> init: False vct: False data refs: False`;
- `iverson-ollama iverson-ollama-model:qwen2.5-3b-357c53fb659c-<12 hex> init: False vct: False data refs: False`;
- both egress policies `egress: []`;
- `syntax-ok`.

Then confirm the suffix tracks the file: `sha256sum charts/{tei,ollama}/model-image/Dockerfile | cut -c1-12` equals each tag's last segment.

- [ ] **Step 11: Commit.** `git add` the two new Dockerfiles, the four TEI/Ollama subchart files, `values.yaml`, the 5 overlays, `networkpolicies.yaml`, the two build scripts and the two setup scripts. Then `git commit -m "bake TEI and Ollama model weights into images so neither pod needs egress, and check the full model digest at build"`.

### Task 4: Chart images Dependabot can read (#19)

**Files:**
- Modify: `charts/{qdrant,jaeger,prometheus,redis}/{values.yaml,templates/*}`, `charts/authentik/{values.yaml,templates/deployment-server.yaml,templates/deployment-worker.yaml,templates/job-revoke-cross-db.yaml}`, `charts/starrocks/{values.yaml,templates/job-create-user.yaml}`
- Modify: `values.yaml`; `values-{aws,azure,gcp}.yaml` (delete pins)

- [ ] **Step 1: Subchart image scalars.** Replace each templated image line with `image: {{ .Values.image | quote }}`, and the subchart `values.yaml` key `imageTag: "<t>"` with `image: "<repo>:<t>"`:

| Subchart | Template line(s) | values.yaml |
|---|---|---|
| qdrant | `statefulset.yaml:45` | `image: "qdrant/qdrant:v1.18.2"` |
| jaeger | `deployment.yaml:37` | `image: "jaegertracing/all-in-one:1.62.0"` |
| prometheus | `deployment.yaml:41` | `image: "prom/prometheus:v2.55.1"` |
| redis | `deployment.yaml:37` | `image: "redis:7.4.11-alpine"` (was floating `7.4-alpine`) |
| authentik | `deployment-server.yaml:54, 84`, `deployment-worker.yaml:27` | `image: "ghcr.io/goauthentik/server:2026.5.3"` |

- [ ] **Step 2: Template literals into values.**
  - `charts/authentik/templates/deployment-server.yaml:150` becomes `image: {{ .Values.tlsProxy.image | quote }}`.
  - `charts/authentik/templates/job-revoke-cross-db.yaml:32` becomes `image: {{ .Values.revokeCrossDb.image | quote }}`.
  - `charts/starrocks/templates/job-create-user.yaml:31` becomes `image: {{ .Values.createUser.image | quote }}`.
  - In `charts/authentik/values.yaml`, after the `image:` line, add the block below. The top-level `tlsProxy` key is distinct from the existing `resources.tlsProxy`.

```yaml
tlsProxy:
  image: "nginxinc/nginx-unprivileged:1.30.5-alpine"
revokeCrossDb:
  image: "postgres:16.15"
```

  - Append to `charts/starrocks/values.yaml`:

```yaml
createUser:
  image: "mysql:8.0.46"
```

- [ ] **Step 3: Umbrella values.**
  - In `values.yaml`, replace `imageTag: …` with the same `image:` scalar in the `qdrant`, `jaeger`, `prometheus`, `redis` and `authentik` blocks.
  - Under `authentik:`, after its `image:` line, add `tlsProxy: { image: … }` and `revokeCrossDb: { image: … }` with the values from Step 2, as two-space-indented nested maps.
  - Under `starrocks:`, after the `be:` block, add:

```yaml
  createUser:
    image: "mysql:8.0.46"
```

- [ ] **Step 4: Overlays.** Delete the nine `imageTag:` lines in `values-aws.yaml` (`:140, :149, :156`), `values-azure.yaml` (`:131, :140, :147`) and `values-gcp.yaml` (`:139, :148, :155`). Each equals its base value. Then delete any `prometheus:`, `redis:` or `authentik:` key left with no children.

- [ ] **Step 5: Verify.**

```bash
cd Iverson.Server/deploy/helm/iverson && helm dependency build . >/dev/null
grep -rn 'imageTag' . --include='*.yaml' | grep -v '/charts/.*\.tgz' || echo "no imageTag left"
grep -n -E '^\s*(image|imageTag):' values-{aws,azure,gcp,laptop,local}.yaml values-*.ci-override.yaml || echo "no image pins in overlays"
grep -rn -E 'image: "[a-z].*:' charts/*/templates/ || echo "no image literals in templates"
for v in values-local values-laptop values-aws values-azure values-gcp; do x=""; [ -f $v.ci-override.yaml ] && x="-f $v.ci-override.yaml"
  helm template iverson . -f $v.yaml $x | grep -E '^\s+image:' | sort -u; done | sort -u
```

Expected:
- `no imageTag left`;
- `no image pins in overlays`;
- `no image literals in templates`, though `iverson-*` first-party templated lines may match the last grep, which is fine;
- the image list shows only exact tags: `qdrant/qdrant:v1.18.2`, `jaegertracing/all-in-one:1.62.0`, `prom/prometheus:v2.55.1`, `redis:7.4.11-alpine`, `ghcr.io/goauthentik/server:2026.5.3`, `nginxinc/nginx-unprivileged:1.30.5-alpine`, `postgres:16.15`, `mysql:8.0.46`, `starrocks/{fe,be}-ubuntu:4.1.1`, the two model images and the first-party images.

- [ ] **Step 6: Commit.** `git add` the subchart and umbrella files and the three overlays, then `git commit -m "move chart images to exact-tag image scalars Dependabot can read, out of overlays and template literals"`.

### Task 5: Build context, images and console dependencies (#19)

**Files:**
- Modify: `.dockerignore`, `Iverson.Server/.dockerignore`
- Modify: `Iverson.Server/Iverson.Api/Dockerfile:23`, `Iverson.Server/Iverson.Launcher/Dockerfile:7`, `Iverson.AdminUI/Dockerfile:1-24`
- Modify: `Iverson.Server/docker-compose.yml` (image lines)
- Modify: `Iverson.AdminUI/package.json`, `Iverson.AdminUI/package-lock.json`
- Delete from index: `Iverson.Server/deploy/scripts/__pycache__/mint_acting_user_token.cpython-314.pyc`

- [ ] **Step 1: `.dockerignore`.** Append to the root `.dockerignore`:

```
**/.env
**/.env.*
.claude/
.worktrees/
docs/
```

Append `**/.env` and `**/.env.*` to `Iverson.Server/.dockerignore`.

- [ ] **Step 2: Api Dockerfile.** Replace `COPY . .` (line 23) with:

```dockerfile
# Explicit copies (CSR round-10 #19): only the projects the publish needs reach the build stage.
COPY Iverson.Server/Iverson.Api/ Iverson.Server/Iverson.Api/
COPY Iverson.Server/Iverson.Sql/ Iverson.Server/Iverson.Sql/
COPY Iverson.Server/Iverson.StarRocks/ Iverson.Server/Iverson.StarRocks/
COPY Iverson.Server/Iverson.Embeddings/ Iverson.Server/Iverson.Embeddings/
COPY Iverson.Server/Iverson.Vector/ Iverson.Server/Iverson.Vector/
COPY Iverson.Server/Iverson.Patterns/ Iverson.Server/Iverson.Patterns/
COPY Iverson.Server/Iverson.Events/ Iverson.Server/Iverson.Events/
COPY Iverson.Clients/DotNet/Iverson.Client.Contracts/ Iverson.Clients/DotNet/Iverson.Client.Contracts/
COPY Iverson.Clients/Common/Proto/ Iverson.Clients/Common/Proto/
```

- [ ] **Step 3: Launcher Dockerfile.** Replace `COPY . .` (line 7) with `COPY Iverson.Launcher/ Iverson.Launcher/`.

- [ ] **Step 4: Console Dockerfile.**
  - Replace `FROM node:20-alpine@sha256:fb4c…` with `FROM node:22-alpine@sha256:0a7108bf6c7bf5de370ffb1a3ed6be93d405b43ff159f681a8d18c0e2bc2e402 AS build`.
  - Replace `FROM nginxinc/nginx-unprivileged:1.27-alpine@sha256:65e3…` with `FROM nginxinc/nginx-unprivileged:1.30.5-alpine@sha256:15c994d10d6d78658721c3bcafff14cb281fba2a4bdf9d5ba92c416a472516e3 AS runtime`.
  - In the two comments above, change the tag names and "as of 2026-09-13" to `22-alpine`/`1.30.5-alpine` and "as of 2026-10-06". Add to the node comment: "Node 22: package.json requires >=22.22.0 and Node 20 is end-of-life (CSR round-10 #19)."

- [ ] **Step 5: Compose digests.** In `Iverson.Server/docker-compose.yml`, change each `image:` line (current line numbers) to:

| Line | New reference |
|---|---|
| 35 | `postgres:16.15@sha256:65b16a8b326e0cfbdf33fa7e783f2a0cb352a61448616ccccfd616ef42aa0f65` |
| 54 | `starrocks/allin1-ubuntu:4.1.1@sha256:44e657e582fb53e96df249e793f864f3159b82767f5c2aaa06cb173ec7a34550` |
| 93 | `mysql:8.0.46@sha256:7dcddc01f13bab2f15cde676d44d01f61fc9f99fe7785e86196dfc07d358ae2b` |
| 115 | `qdrant/qdrant:v1.18.2@sha256:75eab8c4ba42096724fdcfde8b4de0b5713d529dde32f285a1f86fdcb2c9e50c` |
| 133 | `ollama/ollama:0.12.11@sha256:3d8a05e3432d50ea57594fabe971e46cc8fe963a0f9f8c40400bd56cd5388e47` |
| 147 | `curlimages/curl:8.18.0@sha256:d94d07ba9e7d6de898b6d96c1a072f6f8266c687af78a74f380087a0addf5d17` |
| 164, 182 | `ghcr.io/huggingface/text-embeddings-inference:cpu-1.8.3@sha256:8de25e75ce39617f17f2f6c77d60a4f75b65e779ed5005420eb1400072a15c1c` |
| 210 | `ghcr.io/huggingface/text-generation-inference:3.3.4-intel-cpu@sha256:38c57c4c0ab9150caa3a6e69e1403d84e2f06fe2278bbe013944f6d2c240f1e1` |
| 229 | `confluentinc/cp-zookeeper:7.6.0@sha256:9babd1c0beaf93189982bdbb9fe4bf194a2730298b640c057817746c19838866` |
| 240 | `confluentinc/cp-kafka:7.6.0@sha256:24cdd3a7fa89d2bed150560ebea81ff1943badfa61e51d66bb541a6b0d7fb047` |
| 268 | `jaegertracing/all-in-one:1.62.0@sha256:836e9b69c88afbedf7683ea7162e179de63b1f981662e83f5ebb68badadc710f` |
| 284 | `prom/prometheus:v2.55.1@sha256:2659f4c2ebb718e7695cb9b25ffa7d6be64db013daba13e05c875451cf51b0d3` |
| 301 | `redis:7.4.11-alpine@sha256:858f009f9709ce576febc734aa78b8f6d624b82571f9ddb6bda4377c833b3499` |
| 334, 352, 395 | `ghcr.io/goauthentik/server:2026.5.3@sha256:377ee38726785f98aafc3665202ad198a366fa979b0f61c0660a9c05e3a9b1b3` |
| 442 | `nginxinc/nginx-unprivileged:1.30.5-alpine@sha256:15c994d10d6d78658721c3bcafff14cb281fba2a4bdf9d5ba92c416a472516e3` |

Lines 484 and 608 (`iverson-api`) are first-party and unchanged. Before editing, re-resolve any digest older than a day with `scratchpad`'s `digest.sh` equivalent: a `HEAD` request on `/v2/<repo>/manifests/<tag>` with the OCI index Accept header, reading `Docker-Content-Digest`. Then validate with `docker compose -f Iverson.Server/docker-compose.yml config --no-interpolate -q && echo compose-ok`, which validates structure and image syntax without the untracked `Iverson.Server/.env` (absent from a fresh worktree) and starts nothing.

- [ ] **Step 6: `.pyc`.** `git rm --cached Iverson.Server/deploy/scripts/__pycache__/mint_acting_user_token.cpython-314.pyc`. `.gitignore:82-83` already ignores it.

- [ ] **Step 7: Console dependencies.**

```bash
cd Iverson.AdminUI && npm uninstall @improbable-eng/grpc-web google-protobuf long   # EBADENGINE warning on host Node 22.16 is expected
node -e 'const d=require("./package.json").dependencies; console.log(["@improbable-eng/grpc-web","google-protobuf","long"].filter(k=>k in d))'
npm ci && npm run build && npm test
```

Expected: `[]`, then a passing build and tests.

- [ ] **Step 8: Verify the images build** (heavy; one at a time; `free -g` first):

```bash
cd <worktree root>
docker build --memory 4g -f Iverson.Server/Iverson.Api/Dockerfile -t csr10d-api:check . 2>&1 | tail -2
docker build --memory 2g -f Iverson.AdminUI/Dockerfile -t csr10d-adminui:check . 2>&1 | tail -2
docker run --rm -d --name csr10d-ui -p 127.0.0.1:18099:8080 -e OIDC_CLIENT_ID=x -e OIDC_AUTHORITY=http://idp.invalid/application/o/iverson/ \
  -e API_BASE_URL=http://api.invalid -e EXTERNAL_SCHEME=http -e OIDC_ORIGIN=http://idp.invalid csr10d-adminui:check
sleep 3; curl -s -o /dev/null -w 'console %{http_code}\n' http://127.0.0.1:18099/; docker rm -f csr10d-ui >/dev/null
```

Expected: both builds succeed, and `console 200`. Keep `csr10d-api:check` and `csr10d-adminui:check` for Task 10, where they are retagged and loaded into kind (no second, uncapped build).

- [ ] **Step 9: Commit.** `git add` the two `.dockerignore` files, the three Dockerfiles, `docker-compose.yml`, `package.json` and `package-lock.json`, with the `.pyc` removal staged by Step 6. Then `git commit -m "keep secrets out of build contexts, move the console to Node 22 and nginx 1.30, pin compose images by digest, drop unused console packages"`.

### Task 6: CI pins, Dependabot, image scanning and setup.ps1 (#19)

**Files:**
- Modify: `.github/workflows/admin-ui.yml:31, 34, 136`, `.github/dependabot.yml`, `Iverson.Server/deploy/kind/setup.ps1`
- Create: `.github/workflows/image-scan.yml`

**Interfaces:**
- Consumes: Task 3's model-image directories, as Dependabot docker entries.

- [ ] **Step 1: `admin-ui.yml`.**
  - Change `uses: actions/checkout@v7` (lines 31, 136) to `uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1`.
  - Change `uses: actions/setup-node@v7` (line 34) to `uses: actions/setup-node@820762786026740c76f36085b0efc47a31fe5020 # v7.0.0`.

- [ ] **Step 2: `dependabot.yml`.** Append these entries, each with `schedule: { interval: "weekly" }` in the file's block style:
  - `docker` with `directories:`:
    - `/Iverson.AdminUI`
    - `/Iverson.Server/Iverson.Api`
    - `/Iverson.Server/Iverson.Launcher`
    - `/Iverson.Server/Iverson.Events`
    - `/Iverson.Server/Iverson.Sql`
    - `/Iverson.Server/Iverson.Vector`
    - `/Iverson.Server/deploy/helm/iverson/charts/tei/model-image`
    - `/Iverson.Server/deploy/helm/iverson/charts/ollama/model-image`
  - `docker-compose` with `directory: "/Iverson.Server"`.
  - `terraform` with `directories:` `/Iverson.Server/deploy/terraform/aws`, `/azure`, `/gcp`, `/bootstrap/aws`, `/bootstrap/azure` and `/bootstrap/gcp`.
  - `helm` with `directories:` `/Iverson.Server/deploy/helm/iverson` and `/Iverson.Server/deploy/helm/iverson/charts/*`.

- [ ] **Step 3: Create `.github/workflows/image-scan.yml`:**

```yaml
name: Image Scan

# CSR round-10 #19. First-party images fail on fixable HIGH/CRITICAL findings; the third-party
# images the chart deploys are scanned and reported only, since a fix there means a version bump.
# trivy-action is pinned by commit SHA above the 2026-03 compromise's affected range
# (GHSA-69fq-xp46-6x23); the trivy version is pinned too.

on:
  pull_request: {}
  push:
    branches: [main]
  schedule:
    - cron: '0 5 * * 0'

permissions:
  contents: read

jobs:
  first-party:
    runs-on: ubuntu-latest
    strategy:
      fail-fast: false
      matrix:
        include:
          - { name: iverson-api, dockerfile: Iverson.Server/Iverson.Api/Dockerfile }
          - { name: iverson-admin-ui, dockerfile: Iverson.AdminUI/Dockerfile }
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
      - name: Build ${{ matrix.name }}
        run: docker build -f ${{ matrix.dockerfile }} -t ${{ matrix.name }}:scan .
      - name: Scan ${{ matrix.name }}
        uses: aquasecurity/trivy-action@ed142fd0673e97e23eac54620cfb913e5ce36c25 # v0.36.0
        with:
          image-ref: ${{ matrix.name }}:scan
          version: v0.75.0
          severity: HIGH,CRITICAL
          ignore-unfixed: true
          exit-code: "1"

  third-party:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
      - name: Set up Helm
        uses: azure/setup-helm@9bc31f4ebc9c6b171d7bfbaa5d006ae7abdb4310 # v5.0.1
      - name: List the chart's third-party images
        run: |
          set -e
          C=Iverson.Server/deploy/helm/iverson
          helm dependency build "$C"
          for v in values-aws values-azure values-gcp; do
            helm template iverson "$C" -f "$C/$v.yaml" -f "$C/$v.ci-override.yaml"
          done | sed -n 's/^ *image: "\{0,1\}\([^" ]*\)"\{0,1\}$/\1/p' \
               | grep -v -E '(^|/)iverson-' | sort -u > images.txt
          cat images.txt
      - name: Scan (report only)
        run: |
          set -e
          for img in $(cat images.txt); do
            echo "::group::$img"
            docker run --rm aquasec/trivy:0.75.0@sha256:af6acf9a6b85dfe389a1941505c0ce9efef52a4719635e1a962f022a3d855daa \
              image --severity HIGH,CRITICAL --ignore-unfixed --exit-code 0 --no-progress "$img"
            echo "::endgroup::"
          done
```

- [ ] **Step 4: `setup.ps1` pins.** Add `--version` lines matching `setup.sh`:
  - `` --version v3.32.2 ` `` to both Calico invocations (after `--repo`);
  - `4.12.8` to ingress-nginx;
  - `0.29.0` to cloudnative-pg;
  - `1.1.0` to strimzi-kafka-operator;
  - `1.11.5` to the starrocks operator;
  - `3.9.0` to metrics-server.

  Each goes on its own `` --version <v> ` `` line, directly after the `--repo` line.

- [ ] **Step 5: Verify.**

```bash
python3 -c 'import yaml,sys; [yaml.safe_load(open(f)) for f in sys.argv[1:]]; print("yaml ok")' .github/dependabot.yml .github/workflows/*.yml
grep -n -E 'uses: [^ ]+@' .github/workflows/*.yml | grep -v -E '@[0-9a-f]{40}( |$)' || echo "every uses: is SHA-pinned"
grep -c -- '--version' Iverson.Server/deploy/kind/setup.ps1   # expect 7
```

- [ ] **Step 6: Commit.** `git add .github/workflows/admin-ui.yml .github/workflows/image-scan.yml .github/dependabot.yml Iverson.Server/deploy/kind/setup.ps1`, then `git commit -m "pin console workflow actions by SHA, add Dependabot docker/compose/terraform/helm coverage, scan images with trivy, pin setup.ps1 operators"`.

### Task 7: Terraform for AKS access, VNet Integration and the state account (#6)

**Files:**
- Modify: `Iverson.Server/deploy/terraform/modules/cluster-azure/main.tf`, `variables.tf`, `outputs.tf`
- Modify: `Iverson.Server/deploy/terraform/azure/main.tf`
- Modify: `Iverson.Server/deploy/terraform/bootstrap/azure/main.tf`

- [ ] **Step 1: `modules/cluster-azure/main.tf`.**

  **(a) Disk encryption set role.** In `azurerm_role_assignment.aks_data_volumes_des`, set `principal_id = azurerm_user_assigned_identity.control_plane.principal_id`. Change the comment above it to say "the cluster's control-plane identity (user-assigned, see below)". Then add after that resource:

```hcl
# The control plane's identity is user-assigned so it can hold Network Contributor on both
# subnets before the cluster is created: API Server VNet Integration needs those rights at
# provisioning time, which a system-assigned identity (created with the cluster) cannot have.
resource "azurerm_user_assigned_identity" "control_plane" {
  name                = "${var.cluster_name}-control-plane"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
}

resource "azurerm_role_assignment" "control_plane_aks_subnet" {
  scope                = azurerm_subnet.aks.id
  role_definition_name = "Network Contributor"
  principal_id         = azurerm_user_assigned_identity.control_plane.principal_id
}

resource "azurerm_role_assignment" "control_plane_apiserver_subnet" {
  scope                = azurerm_subnet.apiserver.id
  role_definition_name = "Network Contributor"
  principal_id         = azurerm_user_assigned_identity.control_plane.principal_id
}

# With local accounts disabled, Terraform itself authenticates through Entra (kubelogin) and
# needs a Kubernetes data-plane role. A new assignment can take up to five minutes to apply;
# docs/runbooks/csr10-infrastructure-cutover.md says to re-run a first apply that fails on it.
resource "azurerm_role_assignment" "deployer_cluster_admin" {
  scope                = azurerm_kubernetes_cluster.this.id
  role_definition_name = "Azure Kubernetes Service RBAC Cluster Admin"
  principal_id         = data.azurerm_client_config.current.object_id
}
```

  **(b) API-server subnet.** After `resource "azurerm_subnet" "aks"`, add:

```hcl
# API Server VNet Integration (CSR round-10 #17): projects the API server into this delegated
# subnet, so networkPolicy.apiServerCidrs can name a fixed range instead of a public IP that may
# change. One-way, and enabling it changes the API server's IP (the hostname stays).
resource "azurerm_subnet" "apiserver" {
  name                 = "${var.cluster_name}-apiserver-subnet"
  resource_group_name  = azurerm_resource_group.this.name
  virtual_network_name = azurerm_virtual_network.this.name
  address_prefixes     = ["10.1.17.0/28"]

  delegation {
    name = "aks-apiserver"
    service_delegation {
      name    = "Microsoft.ContainerService/managedClusters"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}
```

  **(c) In `azurerm_kubernetes_cluster.this`:**
  - After `role_based_access_control_enabled = true`, add:

```hcl
  # CSR round-10 #6: no local accounts, so no static cluster-admin certificate exists to copy
  # out of state. `managed = true` is mandatory in azurerm 3.x for AKS-managed Entra integration.
  local_account_disabled = true

  azure_active_directory_role_based_access_control {
    managed                = true
    azure_rbac_enabled     = true
    tenant_id              = data.azurerm_client_config.current.tenant_id
    admin_group_object_ids = var.cluster_admin_group_object_ids
  }
```

  - Replace the cluster's own block. The anchor is `  identity {\n    type = "SystemAssigned"\n  }` directly followed by the blank line and `  # network_policy`. Do not touch the disk-encryption-set block at `:91` (plan row 15).

```hcl
  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.control_plane.id]
  }

  depends_on = [
    azurerm_role_assignment.control_plane_aks_subnet,
    azurerm_role_assignment.control_plane_apiserver_subnet,
  ]
```

  - Replace `api_server_access_profile { authorized_ip_ranges = var.api_authorized_ip_ranges }` with:

```hcl
  # vnet_integration_enabled and subnet_id are deprecated preview-API fields in azurerm 3.x;
  # azurerm 4.46+ renames the first to virtual_network_integration_enabled.
  api_server_access_profile {
    authorized_ip_ranges     = var.api_authorized_ip_ranges
    vnet_integration_enabled = true
    subnet_id                = azurerm_subnet.apiserver.id
  }
```

- [ ] **Step 2: `variables.tf`.** Append:

```hcl

# No default: the Entra group(s) whose members administer the cluster through Azure RBAC.
variable "cluster_admin_group_object_ids" {
  type = list(string)
  validation {
    condition     = length(var.cluster_admin_group_object_ids) > 0
    error_message = "cluster_admin_group_object_ids must name at least one Entra group."
  }
}
```

- [ ] **Step 3: `outputs.tf`.** Replace the `kube_config` output with:

```hcl
# Under Entra the kubeconfig carries no client certificate; the providers authenticate with
# kubelogin (azure/main.tf), so only the endpoint and CA are needed.
output "host" { value = azurerm_kubernetes_cluster.this.kube_config[0].host }
output "cluster_ca_certificate" { value = azurerm_kubernetes_cluster.this.kube_config[0].cluster_ca_certificate }
```

- [ ] **Step 4: `azure/main.tf`.**
  - In the backend block, add `use_azuread_auth = true` under `key`.
  - Add `variable "cluster_admin_group_object_ids" { type = list(string) }`, with a one-line comment, after `key_vault_authorized_ip_ranges`, and pass it into `module "cluster"`.
  - Add to `module "operators"`, as its first argument: `depends_on = [module.cluster]` with the comment `# The deploying principal's Kubernetes RBAC role must exist before any operator resource.` Without it, no operator resource waits for `azurerm_role_assignment.deployer_cluster_admin` (plan row 29).
  - Replace both provider blocks with the code below.
  - Keep `output "kubeconfig_command"`. With Entra it fetches the user kubeconfig, which uses kubelogin.

```hcl
# Entra authentication through kubelogin (must be on PATH): local accounts are disabled, so
# there is no client certificate. --login azurecli uses the signed-in Azure CLI identity.
provider "kubernetes" {
  host                   = module.cluster.host
  cluster_ca_certificate = base64decode(module.cluster.cluster_ca_certificate)
  exec {
    api_version = "client.authentication.k8s.io/v1beta1"
    command     = "kubelogin"
    args        = ["get-token", "--login", "azurecli", "--server-id", "6dae42f8-4368-4678-94ff-3960e28e3630"]
  }
}

provider "helm" {
  kubernetes {
    host                   = module.cluster.host
    cluster_ca_certificate = base64decode(module.cluster.cluster_ca_certificate)
    exec {
      api_version = "client.authentication.k8s.io/v1beta1"
      command     = "kubelogin"
      args        = ["get-token", "--login", "azurecli", "--server-id", "6dae42f8-4368-4678-94ff-3960e28e3630"]
    }
  }
}
```

- [ ] **Step 5: `bootstrap/azure/main.tf`.**
  - Before `provider "azurerm"`, add:

```hcl
# No default: the public addresses `terraform` runs from. The state account denies every
# other network (CSR round-10 #6).
variable "state_authorized_ip_ranges" {
  type = list(string)
  validation {
    condition     = length(var.state_authorized_ip_ranges) > 0
    error_message = "state_authorized_ip_ranges must contain at least one address."
  }
}
```

  - Inside `provider "azurerm"`, add `storage_use_azuread = true`, with a comment that containers are created through the data plane, which needs Entra auth once shared keys are off.
  - After the provider, add `data "azurerm_client_config" "current" {}`.
  - In `azurerm_storage_account.state`, add:

```hcl
  shared_access_key_enabled       = false

  network_rules {
    default_action = "Deny"
    ip_rules       = var.state_authorized_ip_ranges
    bypass         = ["AzureServices"]
  }
```

  - After the storage account, add:

```hcl
# The only data-plane grant on the state account: blob read/write for the deploying identity.
# Scoped to the account because a container-scoped assignment cannot exist before the container,
# and the container cannot be created without it.
resource "azurerm_role_assignment" "deployer_state_data" {
  scope                = azurerm_storage_account.state.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = data.azurerm_client_config.current.object_id
}
```

  - In `azurerm_storage_container.state`, add `depends_on = [azurerm_role_assignment.deployer_state_data]`.

- [ ] **Step 6: Verify** (provider download needs network; delete `.terraform` dirs afterwards):

```bash
cd Iverson.Server/deploy/terraform && terraform fmt -check -recursive . && echo fmt-ok
for d in azure bootstrap/azure; do (cd $d && terraform init -backend=false -lockfile=readonly >/dev/null && terraform validate -no-color | head -1); done
tfsec . --no-color | tail -2
find . -type d -name .terraform -prune -exec rm -rf {} +; find . -name .terraform.lock.hcl -newer azure/main.tf -print   # lockfiles must be unchanged
git status --short .
```

Expected:
- `fmt-ok`;
- two lines of `Success! The configuration is valid.`;
- `No problems detected!`;
- `git status` lists only the five edited `.tf` files, with no lockfile change.

- [ ] **Step 7: Commit.** `git add` the five files, then `git commit -m "AKS: Entra-only access with Azure RBAC, user-assigned control-plane identity, API Server VNet Integration; lock the state account to Entra auth and an IP allowlist"`.

### Task 8: Terraform for EKS instance metadata (#18)

**Files:**
- Modify: `modules/cluster-aws/main.tf:547-557`, `modules/cluster-aws/outputs.tf`, `modules/operators/variables.tf`, `modules/operators/main.tf` (aws_load_balancer_controller), `aws/main.tf`

- [ ] **Step 1: Hop limit.** In `modules/cluster-aws/main.tf`, set `http_put_response_hop_limit = 1`. Replace the comment above `metadata_options` with:

```hcl
  # IMDSv2 required, hop limit 1 (CSR round-10 #18): pods cannot reach instance metadata, so the
  # node role's credentials stay off-limits to them. Every AWS-calling workload gets its identity
  # from IRSA instead: the load-balancer controller and cluster autoscaler are given region and
  # VPC explicitly (modules/operators), the EBS CSI controller receives AWS_REGION from the IRSA
  # webhook, and its node plugin falls back to Kubernetes node metadata. aws-node and kube-proxy
  # run on the host network and are unaffected.
```

- [ ] **Step 2: `vpc_id` output.** Append to `modules/cluster-aws/outputs.tf`: `output "vpc_id" { value = aws_vpc.this.id }`.

- [ ] **Step 3: Operators.** Append to `modules/operators/variables.tf`:

```hcl

variable "vpc_id" {
  type    = string
  default = null # only meaningful when cloud == "aws"
}
```

In `helm_release.aws_load_balancer_controller`, after the `clusterName` `set` block, add:

```hcl
  # Without instance metadata (hop limit 1, cluster-aws) the controller cannot discover these.
  set {
    name  = "region"
    value = var.aws_region
  }
  set {
    name  = "vpcId"
    value = var.vpc_id
  }
```

- [ ] **Step 4: AWS root.** In `aws/main.tf`'s `module "operators"`, add `vpc_id = module.cluster.vpc_id`, aligned with the existing arguments, after `aws_region`.

- [ ] **Step 5: Verify.** Run the Task 7 Step 6 loop for `aws` and `gcp`, plus `fmt -check` and `tfsec` on the whole tree. Expected: `Success!` ×2, `fmt-ok`, `No problems detected!`, and no lockfile change.

- [ ] **Step 6: Commit.** `git add` the five files, then `git commit -m "EKS: instance metadata hop limit 1, and give the load-balancer controller its region and VPC"`.

### Task 9: The cutover runbook

**Files:**
- Create: `docs/runbooks/csr10-infrastructure-cutover.md`

- [ ] **Step 1: Write the runbook.** Keep it at the level of conditions, commands and expected results (no exploitation detail). Use these sections, in order:
  1. **Prerequisites:**
     - `kubelogin` on PATH for the Azure root.
     - The Entra admin group's object ID, for `cluster_admin_group_object_ids`.
     - The deployer's public IP, for `state_authorized_ip_ranges`.
     - A registry for the model images.
  2. **Model images:**
     - Building: `deploy/kind/build-and-load-image.sh 0.1.0 <cluster> --model-images --values <overlay>` on kind.
     - On cloud, the same `docker build` commands with `-t <registry>/<image>` and `docker push`.
     - `global.modelImageRegistry` set to the registry prefix with its trailing `/`.
     - The tag changes whenever a model pin or a model Dockerfile changes, so rebuild and push before `helm upgrade`.
  3. **Upgrading an existing release:**
     - Delete the TEI and Ollama StatefulSets, whose `volumeClaimTemplates` are immutable, with `kubectl -n <ns> delete sts <release>-ollama <release>-tei-<slug>`.
     - Then delete their PVCs: `kubectl -n <ns> delete pvc -l app=<release>-ollama` and the TEI equivalent.
     - Then `helm upgrade`.
  4. **First-deploy gate (every cloud):**
     - `kubectl -n <ns> exec deploy/<release>-api -- getent hosts kubernetes.default` resolves. If `getent` is absent, use a debug container sharing the pod.
     - `kubectl -n <ns> get cluster.postgresql.cnpg.io <release>-postgres` reports a healthy phase.
     - The Kafka broker pods are Ready.
     - `kubectl get endpointslices -n default -l kubernetes.io/service-name=kubernetes` shows an address inside the profile's `apiServerCidrs` (GKE confirms the inferred endpoint).
     - Fallbacks, applied by values per profile:
       1. Add the `kubernetes` Service ClusterIP (`kubectl get svc kubernetes -n default`) as a `/32` to `apiServerCidrs`.
       2. If still blocked, set `networkPolicy.apiServerAnyDestination: true`.
       3. For DNS, set `networkPolicy.dnsAnyDestination: true`.
     - A profile that keeps a fallback leaves that half of #17 open there.
  5. **AKS ingress readiness:**
     - After deploy, every pod reaches Ready.
     - If the api, admin-ui or Authentik pods stay unready on probe timeouts, Azure NPM is not exempting node traffic. Add the node subnet `10.1.0.0/20` to `clusterCidrs`.
  6. **AKS Entra cutover:**
     - The first apply after this change swaps the control-plane identity (in place) and enables VNet Integration (one-way; the API server IP changes, the hostname stays).
     - If the operators module fails with 403 right after the role assignment, wait five minutes and re-apply.
     - Afterwards, run `az aks rotate-certs -g <rg> -n <cluster>` once, so certificates copied from old state stop working.
     - `az aks get-credentials` now returns a kubelogin-based kubeconfig.
  7. **Terraform state account:**
     - In a single apply, Terraform updates the account (shared keys off, network deny) before it creates the data role (plan row 30), so an existing account needs the role first, out of band.
     - For an existing account, in order:
       1. `az role assignment create --role "Storage Blob Data Contributor" --assignee <deployer object id> --scope <state storage account id>`.
       2. Wait about five minutes for the assignment to propagate.
       3. `terraform import azurerm_role_assignment.deployer_state_data <assignment id>` in `bootstrap/azure` (the id printed by step 1).
       4. Apply `bootstrap/azure` with your IP in `state_authorized_ip_ranges`.
     - A new account needs none of this: the single apply creates the account, the role and then the container.
     - Then `terraform init -reconfigure` in `azure/`. The backend now uses Entra auth (`use_azuread_auth`).
  8. **EKS:**
     - The hop-limit change replaces the node groups' launch template version, so nodes roll.
     - Check that the load-balancer controller and EBS CSI pods are Running, and that a PVC binds.

- [ ] **Step 2: Commit.** `git add -f docs/runbooks/csr10-infrastructure-cutover.md`, then `git commit -m "add the CSR round-10 infrastructure cutover runbook"`.

### Task 10: Verification (§5a, §5c, §5d, §5e, then §5b on kind)

**Files:** none created in the repo. Scratch lives under `$SCR/csr10d/`, where `$SCR` is the session scratchpad.

- [ ] **Step 1: §5a render assertions and kube-score baseline.**
  - Create `$SCR/csr10d/assert.py` with the checker below.
  - Render all 5 overlays with ci-overrides, as in Task 1 Step 7, and pipe each through `kubeconform -kubernetes-version 1.30.0 -summary -ignore-missing-schemas` and the checker.
  - Then compare `kube-score` criticals, using the same three `--ignore-test` flags as `deploy-validate.yml`, against renders of `main` (`git stash`-free: render `main` in a `git worktree add --detach $SCR/csr10d/base main` checkout).

```python
"""§5a: python3 assert.py <overlay> < render.yaml"""
import sys, yaml, re
name = sys.argv[1]
docs = [d for d in yaml.safe_load_all(sys.stdin) if d]
EXPECT = {"values-aws": ["10.0.128.0/20", "10.0.144.0/20"], "values-azure": ["10.1.16.0/24"],
          "values-gcp": ["130.211.0.0/22", "35.191.0.0/16"], "values-laptop": ["172.18.0.0/16"], "values-local": ["172.18.0.0/16"]}[name]
DNS = [{"namespaceSelector": {"matchLabels": {"kubernetes.io/metadata.name": "kube-system"}}, "podSelector": {"matchLabels": {"k8s-app": "kube-dns"}}}]
fails = []
nps = {d["metadata"]["name"]: d for d in docs if d.get("kind") == "NetworkPolicy"}
for n, d in nps.items():
    for r in d["spec"].get("ingress") or []:
        if r.get("from") == []: fails.append(f"{n}: ingress from: []")
        fails += [f"{n}: ipBlock {p['ipBlock']['cidr']}" for p in r.get("from") or [] if "ipBlock" in p and p["ipBlock"]["cidr"] not in EXPECT]
    for r in d["spec"].get("egress") or []:
        if r.get("to") == []: fails.append(f"{n}: egress to: []")
        ports = sorted((p["protocol"], p["port"]) for p in r.get("ports", []))
        if ports == [("TCP", 53), ("UDP", 53)] and r.get("to") != DNS: fails.append(f"{n}: DNS not kube-dns")
        if ports == [("TCP", 443), ("TCP", 6443)] and not all("ipBlock" in p for p in r["to"]): fails.append(f"{n}: API not ipBlock")
for n in ("iverson-tei-egress", "iverson-ollama-egress"):
    if nps.get(n, {}).get("spec", {}).get("egress"): fails.append(f"{n}: has egress")
if "iverson-prometheus-ingress" in nps:
    peers = [p for r in nps["iverson-prometheus-ingress"]["spec"]["ingress"] for p in r["from"]]
    if peers != [{"podSelector": {"matchLabels": {"app": "iverson-api"}}}]: fails.append(f"prometheus peers {peers}")
if any(p["port"] == 9000 for r in nps["iverson-authentik-ingress"]["spec"]["ingress"] for p in r["ports"]): fails.append("authentik 9000")
API = {"values-aws": ["10.0.0.0/20", "10.0.16.0/20", "10.0.128.0/20", "10.0.144.0/20", "10.100.0.1/32", "172.20.0.1/32"],
       "values-azure": ["10.1.17.0/28"], "values-gcp": ["172.16.0.0/28"],
       "values-laptop": ["172.18.0.0/16", "10.89.0.0/16"], "values-local": ["172.18.0.0/16", "10.89.0.0/16"]}[name]
for n, d in nps.items():   # API-server rules carry exactly the profile's apiServerCidrs
    for r in d["spec"].get("egress") or []:
        if sorted((p["protocol"], p["port"]) for p in r.get("ports", [])) == [("TCP", 443), ("TCP", 6443)]:
            got = [p["ipBlock"]["cidr"] for p in r["to"] if "ipBlock" in p]
            if got != API: fails.append(f"{n}: API cidrs {got}")
for n, port in (("iverson-api-ingress", 8080), ("iverson-api-ingress", 8081), ("iverson-admin-ui-ingress", 8080), ("iverson-authentik-ingress", 9080)):
    rules = [r for r in nps[n]["spec"]["ingress"] if any(p["port"] == port for p in r["ports"])]
    got = sorted(p["ipBlock"]["cidr"] for r in rules for p in r["from"] if "ipBlock" in p)
    if got != sorted(EXPECT): fails.append(f"{n}:{port} cidrs {got}")   # load-balancer ranges present, not just none unexpected
for d in docs:
    spec = d.get("spec", {}); tpl = (spec.get("template") or {}).get("spec") or {}
    for c in (tpl.get("initContainers") or []) + (tpl.get("containers") or []):
        img = c.get("image", "")
        if img.startswith(("iverson-api", "iverson-admin-ui")): continue
        if img.startswith(("iverson-tei-model:", "iverson-ollama-model:")):
            if not re.fullmatch(r"iverson-(tei|ollama)-model:[A-Za-z0-9_.-]+-[0-9a-f]{12}-[0-9a-f]{12}", img): fails.append(f"bad {img}")
            continue
        tag = img.split("@")[0].rsplit(":", 1)[-1]
        if not re.search(r"\d+\.\d+", tag) or tag in ("16", "8.0", "cpu-1.8", "7.4-alpine", "1.27-alpine"): fails.append(f"non-exact {img}")
        if ("tei" in d["metadata"]["name"] or "ollama" in d["metadata"]["name"]) and "/data" in str(c): fails.append(f"{d['metadata']['name']} /data")
    if d.get("kind") == "StatefulSet" and ("tei" in d["metadata"]["name"] or "ollama" in d["metadata"]["name"]) and spec.get("volumeClaimTemplates"):
        fails.append(f"{d['metadata']['name']} volumeClaimTemplates")
print(f"[{name}] {'PASS' if not fails else 'FAIL'}", *fails, sep="\n  ")
sys.exit(bool(fails))
```

Expected:
- kubeconform 0 invalid on all 5 overlays;
- `PASS` ×5;
- `comm -13 base new` on the kube-score criticals is empty for every overlay;
- Task 2 Step 7's guard and fallback checks re-pass.

- [ ] **Step 2: §5d Terraform.**
  - `terraform fmt -check -recursive`.
  - The init/validate loop for `aws`, `azure`, `gcp` and `bootstrap/azure`.
  - `tfsec .`
  - Delete the `.terraform` dirs.
  - Expected as in Task 7 Step 6.

- [ ] **Step 3: §5e workflows.** The Task 6 Step 5 YAML parse and SHA-pin check.

- [ ] **Step 4: §5c images.**

  **(a) Build-context canary.**

```bash
mkdir -p $SCR/csr10d/ctx && git archive HEAD | tar -x -C $SCR/csr10d/ctx
cd $SCR/csr10d/ctx && echo CANARY > .env && echo CANARY > Iverson.Server/.env.canary && mkdir -p .worktrees/x .claude/w && echo CANARY > .worktrees/x/.env && echo CANARY > .claude/w/f && echo CANARY > Iverson.AdminUI/.env.local && echo CANARY > Iverson.Server/Iverson.Api/.env
docker build --memory 4g --target build -f Iverson.Server/Iverson.Api/Dockerfile -t csr10d-ctx-api . >/dev/null
docker run --rm --entrypoint sh csr10d-ctx-api -c 'grep -rl CANARY /src 2>/dev/null | head; echo api-canaries-done'
docker build --memory 2g --target build -f Iverson.AdminUI/Dockerfile -t csr10d-ctx-ui . >/dev/null
docker run --rm --entrypoint sh csr10d-ctx-ui -c 'grep -rl CANARY / --exclude-dir=proc --exclude-dir=sys 2>/dev/null | head; echo ui-canaries-done'
docker run --rm --entrypoint sh csr10d-ctx-ui -c 'test ! -e /src/.env.development && echo tracked-env-excluded'
docker rmi csr10d-ctx-api csr10d-ctx-ui >/dev/null; rm -rf $SCR/csr10d/ctx
```

Expected: no file paths before `api-canaries-done` or before `ui-canaries-done`, then `tracked-env-excluded`. The two in-directory canaries (`Iverson.AdminUI/.env.local`, `Iverson.Server/Iverson.Api/.env`) are what make each check falsifiable: they sit inside directories the builds copy, so only `.dockerignore`'s `**/.env*` keeps them out (plan row 34).

  **(b) Model images and the digest-failure check.** Run these after Task 10's kind cluster exists, inside Step 5 (`--model-images`). Separately, run this once without a cluster:

```bash
docker build --memory 2g --build-arg MODEL=qwen2.5:3b --build-arg DIGEST=357c53fb659c0000000000000000000000000000000000000000000000000000 \
  -f Iverson.Server/deploy/helm/iverson/charts/ollama/model-image/Dockerfile Iverson.Server/deploy/helm/iverson/charts/ollama/model-image 2>&1 | grep -E 'FATAL|Error' | head -2
```

Expected: a `FATAL: qwen2.5:3b manifest 357c53fb659c5076… != pinned 357c53fb659c0000…` line, and the build fails. The bad digest shares the real one's 12-character prefix. Remove the dangling build stage it leaves, after checking the image's creation time is from this run.

  **(c) trivy, locally.**

```bash
mkdir -p /var/tmp/csr10d && docker save -o /var/tmp/csr10d/api.tar csr10d-api:check && docker save -o /var/tmp/csr10d/ui.tar csr10d-adminui:check
for t in api ui; do docker run --rm -v /var/tmp/csr10d:/in:ro aquasec/trivy:0.75.0@sha256:af6acf9a6b85dfe389a1941505c0ce9efef52a4719635e1a962f022a3d855daa \
  image --input /in/$t.tar --severity HIGH,CRITICAL --ignore-unfixed --exit-code 0 --no-progress 2>&1 | grep -E '^Total|Total:' ; done
rm -rf /var/tmp/csr10d
```

Record the totals. A non-zero total is a finding to report, not a step failure: CI gates on it, and fixing base-image CVEs is a Dependabot bump.

- [ ] **Step 5: §5b pass 1 on kind** (`values-laptop`; heavy; `free -g` ≥ 6 GB first).
  - Record the user's state with exactly Step 7's commands:

```bash
mkdir -p $SCR/csr10d
docker ps -a --format '{{.Names}}' | sort > $SCR/csr10d/before-containers.txt
docker volume ls -q | sort > $SCR/csr10d/before-volumes.txt
docker network ls --format '{{.Name}}' | sort > $SCR/csr10d/before-networks.txt
docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort > $SCR/csr10d/before-images.txt
```

  - Then run, slow steps with `run_in_background`:

```bash
cd <worktree root>
KIND_EXPERIMENTAL_PROVIDER=podman kind create cluster --name iverson --config Iverson.Server/deploy/kind/kind-config.yaml
bash Iverson.Server/deploy/kind/setup.sh
docker tag csr10d-api:check docker.io/library/iverson-api:csr10d && TMPDIR=/var/tmp kind load docker-image docker.io/library/iverson-api:csr10d --name iverson   # Task 5's capped build; no uncapped rebuild
bash Iverson.Server/deploy/kind/build-and-load-image.sh csr10d iverson --model-images
cd Iverson.Server/deploy/helm/iverson && helm dependency build . >/dev/null
helm upgrade --install iverson . -n iverson -f values-laptop.yaml \
  --set api.hpa.maxReplicas=1 --set api.image.tag=csr10d --set worker.image.tag=csr10d --wait --timeout 30m
kubectl -n iverson get pods --no-headers | awk '{print $1, $2, $3}'
```

Expected: every pod `Running` with all containers ready, and Jobs `Completed`. This includes `iverson-postgres-*` (CNPG reached the API server), the Kafka broker and entity-operator (Strimzi reached it), `iverson-tei-bge-base-0` and `iverson-ollama-0`.

  Then run the refusal/positive pairs, DNS and models. The probe pods carry a borrowed label and a readiness probe that never passes, so they join no Service but are subject to that label's NetworkPolicy.

```bash
probe() { # name labels image cmd...
  local n=$1 l=$2 img=$3; shift 3
  kubectl -n iverson run $n --restart=Never --labels="$l" --image=$img \
    --overrides='{"spec":{"containers":[{"name":"'$n'","image":"'$img'","command":["sleep","3600"],"readinessProbe":{"exec":{"command":["false"]}}}]}}' >/dev/null
  kubectl -n iverson wait --for=jsonpath='{.status.phase}'=Running pod/$n --timeout=120s >/dev/null; }
CURL=curlimages/curl:8.18.0@sha256:d94d07ba9e7d6de898b6d96c1a072f6f8266c687af78a74f380087a0addf5d17
# The outsider needs egress of its own, or default-deny makes every refusal below vacuous.
outsider_egress() { kubectl -n iverson apply -f - >/dev/null <<'NP'
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata: { name: csr10d-outsider-egress }
spec:
  podSelector: { matchLabels: { app: csr10d-outsider } }
  policyTypes: ["Egress"]
  egress: [{}]
NP
}
outsider_egress
probe outsider app=csr10d-outsider $CURL
for t in "iverson-api 8080" "iverson-api 8081" "iverson-authentik 9000" "iverson-authentik 9080"; do set -- $t
  kubectl -n iverson exec outsider -- nc -z -w 5 $1 $2 && echo "outsider -> $1:$2 ADMITTED (fail)" || echo "outsider -> $1:$2 refused/timeout"; done
probe apiprobe app=iverson-api $CURL
kubectl -n iverson exec apiprobe -- nslookup kubernetes.default.svc.cluster.local >/dev/null 2>&1 && echo "dns via kube-dns ok"
kubectl -n iverson exec apiprobe -- nslookup example.com 8.8.8.8 >/dev/null 2>&1 && echo "OUTSIDE RESOLVER REACHABLE (fail)" || echo "outside resolver blocked"
kubectl -n iverson exec apiprobe -- curl -s -m 30 http://iverson-tei-bge-base:8080/embed -H content-type:application/json -d '{"inputs":"hi"}' | head -c 40; echo
kubectl -n iverson exec apiprobe -- curl -s -m 300 http://iverson-ollama:11434/api/generate -d '{"model":"qwen2.5:3b","prompt":"hi","stream":false,"options":{"num_predict":1}}' | head -c 80; echo
probe teiprobe iverson.io/component=tei $CURL
kubectl -n iverson exec teiprobe -- curl -s -m 5 -o /dev/null -w '%{http_code}\n' https://huggingface.co/ || echo "tei-labelled egress blocked"
probe ollamaprobe app=iverson-ollama $CURL
kubectl -n iverson exec ollamaprobe -- curl -s -m 5 -o /dev/null -w '%{http_code}\n' https://registry.ollama.ai/ || echo "ollama-labelled egress blocked"
kubectl -n iverson delete pod outsider apiprobe teiprobe ollamaprobe --wait=false; kubectl -n iverson delete networkpolicy csr10d-outsider-egress
kubectl -n iverson port-forward svc/iverson-authentik 19000:9000 > $SCR/csr10d/pf.log 2>&1 & echo $! > $SCR/csr10d/pf.pid; sleep 3
curl -s -o /dev/null -w 'port-forward 9000: %{http_code}\n' -H 'Host: iverson-authentik:9000' http://127.0.0.1:19000/-/health/live/
kill $(cat $SCR/csr10d/pf.pid)
```

Expected:
- `outsider -> …:… refused/timeout` for all four (no `ADMITTED (fail)`).
- `dns via kube-dns ok` and `outside resolver blocked`, both from `apiprobe`, whose only DNS rule is the kube-dns helper.
- A JSON vector prefix from TEI and a JSON `"response"` from Ollama.
- `tei-labelled egress blocked` and `ollama-labelled egress blocked`.
- `port-forward 9000: 200` (or `204`).

  Then the positive control through ingress-nginx: an authenticated gRPC call over TLS on 8443.

```bash
( CID="$(kubectl -n iverson get secret iverson-authentik-loadtest-client -o jsonpath='{.data.client-id}' | base64 -d)"
  CSEC="$(kubectl -n iverson get secret iverson-authentik-loadtest-client -o jsonpath='{.data.client-secret}' | base64 -d)"
  TOKEN="$(curl -s -H 'Host: authentik.iverson.local' http://127.0.0.1:8080/application/o/token/ -d grant_type=client_credentials \
    -d client_id="$CID" --data-urlencode client_secret="$CSEC" | python3 -c 'import sys,json; print(json.load(sys.stdin)["access_token"])')"
  printf '\0\0\0\0\0' | curl -sk -D - -o /dev/null --http2 --resolve iverson.local:8443:127.0.0.1 \
    -H 'content-type: application/grpc' -H 'te: trailers' -H "authorization: Bearer $TOKEN" --data-binary @- \
    https://iverson.local:8443/iverson.ObjectMappingService/GetSchema | grep -i -E '^HTTP/|^grpc-status' | tr -d '\r' | tr '\n' ' '; echo )
```

Expected: `HTTP/2 200` followed by a `grpc-status:` header (any value). The API answered through ingress-nginx on 8080 (an unreachable backend yields an nginx 502/503 with no `grpc-status`), and the token was minted through the Authentik public listener (9080) via ingress-nginx.

- [ ] **Step 6: §5b pass 2.** In a new shell, re-define `probe`, `outsider_egress` and `CURL` from Step 5 first.

```bash
docker tag csr10d-adminui:check docker.io/library/iverson-admin-ui:csr10d && TMPDIR=/var/tmp kind load docker-image docker.io/library/iverson-admin-ui:csr10d --name iverson   # Task 5's capped build
cd Iverson.Server/deploy/helm/iverson && helm upgrade --install iverson . -n iverson -f values-laptop.yaml \
  --set api.hpa.maxReplicas=1 --set api.image.tag=csr10d --set worker.image.tag=csr10d --set adminUi.image.tag=csr10d \
  --set global.prometheusEnabled=true --set adminUi.enabled=true --set adminUi.ingress.className=nginx \
  --set prometheus.storageClassName=standard --set ollama.enabled=false --wait --timeout 30m
kubectl -n iverson get pods --no-headers | awk '$3!="Running" && $3!="Completed"{print "NOT READY:", $0}'
outsider_egress
probe outsider app=csr10d-outsider $CURL
for t in "iverson-prometheus 9090" "iverson-admin-ui 8080"; do set -- $t
  kubectl -n iverson exec outsider -- nc -z -w 5 $1 $2 && echo "outsider -> $1:$2 ADMITTED (fail)" || echo "outsider -> $1:$2 refused/timeout"; done
probe apiprobe app=iverson-api $CURL
kubectl -n iverson exec apiprobe -- curl -s -m 10 -o /dev/null -w 'api -> prometheus:9090 %{http_code}\n' http://iverson-prometheus:9090/-/ready
curl -s -o /dev/null -w 'ingress /admin %{http_code}\n' -H 'Host: iverson.local' http://127.0.0.1:8080/admin/
kubectl -n iverson delete pod outsider apiprobe --wait=false; kubectl -n iverson delete networkpolicy csr10d-outsider-egress
```

Expected:
- no `NOT READY` lines;
- both outsider probes print `refused/timeout` (no `ADMITTED (fail)`);
- `api -> prometheus:9090 200`;
- `ingress /admin 200`.

- [ ] **Step 7: Tear down and diff the user's state.**

```bash
kind delete cluster --name iverson; kind get clusters
docker rmi docker.io/library/iverson-api:csr10d localhost/iverson-api:csr10d docker.io/library/iverson-admin-ui:csr10d localhost/iverson-admin-ui:csr10d csr10d-api:check csr10d-adminui:check >/dev/null 2>&1
docker images --format '{{.Repository}}:{{.Tag}}' | grep -E 'iverson-(tei|ollama)-model' | xargs -r docker rmi >/dev/null
docker ps -a --format '{{.Names}}' | sort | diff $SCR/csr10d/before-containers.txt - && echo containers-unchanged
docker volume ls -q | sort | diff $SCR/csr10d/before-volumes.txt - && echo volumes-unchanged
docker network ls --format '{{.Name}}' | sort | diff $SCR/csr10d/before-networks.txt - && echo networks-unchanged
docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort > $SCR/csr10d/after-images.txt
[ -z "$(comm -23 $SCR/csr10d/before-images.txt $SCR/csr10d/after-images.txt)" ] && echo no-image-removed-or-repointed
comm -13 $SCR/csr10d/before-images.txt $SCR/csr10d/after-images.txt   # additions: kind's node image, build stages
```

Expected: `containers-unchanged`, `volumes-unchanged`, `networks-unchanged` and `no-image-removed-or-repointed`. The `comm -13` additions are expected (`kind create` pulls a `kindest/node` image; builds leave stages). Remove an added image only after checking its creation time is from this task.

- [ ] **Step 8: No commit** (verification only). Record results in the SDD ledger.

## Known issues inherited from spec

- **Azure NPM's kubelet exemption is not documented by Microsoft** (spec and user report only). If it does not hold, AKS pods fail readiness at deploy; the runbook gives the check and the fallback (node subnet in `clusterCidrs`). Azure NPM on Linux is retired on 2028-09-30; moving AKS to Cilium is not in scope.
- **Runtime matching of the new egress rules on the managed dataplanes** (the API-server ipBlocks on AKS and GKE, the kube-dns helper on EKS, AKS and GKE, and the AWS ClusterIP entries) is unverified; Kubernetes leaves it to the implementation, and GKE Dataplane V2 has been observed to deny ipBlock rules to API-server addresses. The first-deploy gate (2b) detects a miss as a failed data-tier deploy, and the values-only fallbacks restore the port-only rules for that profile, leaving that half of #17 open there. GKE's in-cluster API endpoint is likewise inferred; the runbook checks `kubectl get endpointslices -n default -l kubernetes.io/service-name=kubernetes` on first deploy.
- **AKS changes are statically verified only:** the identity swap (system- to user-assigned), VNet Integration enablement (one-way, API IP change) and first-apply role propagation are covered by runbook steps, not by an apply. The VNet Integration attributes are deprecated in azurerm 3.x and must be renamed when the provider moves to 4.46+.
- **EBS CSI node plugin on Kubernetes metadata** assumes one ENI and one EBS volume when computing attach limits (driver docs); a later driver upgrade to v1.46+ with its volume-limit feature would need metadata again.
- **Compose still downloads models at runtime**, and the `.pyc`'s old password remains in git history.
- **Third-party image scan results are report-only;** only first-party images fail CI.
- **The laptop/local `apiServerCidrs`** covers docker's and podman's default kind networks; a kind network on another range needs the value overridden.
