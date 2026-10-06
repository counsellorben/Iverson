# Critical Design Review: 2026-10-06-csr-round10-infrastructure-supply-chain-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-10-06-csr-round10-infrastructure-supply-chain-design.md`
**Artifact HEAD at review:** b6b09c8cbb689a00f6a53f437b9abe3861238d53
**Verified Assumptions section:** present

Anchor basis: `git log -1 --format=%H -- <spec>` → `b6b09c8c…`; `git status --porcelain -- <spec>` → empty. Round 1: no prior reviews for this basename, so amendment family (c) is skipped; families (a)/(b) do not apply.

Probes ran in `/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/cdr-d1/`: a scratch copy of the chart (`helm dependency build` + `helm template`), a throwaway `ollama/ollama:0.12.11` container (`--rm --memory 1g --network none --read-only`), a three-file scratch Helm chart, the dependabot-core helm sources fetched with `curl` (their `find_images_in_hash`/`handle_string_value`/`handle_array_value` methods were run verbatim, with Sorbet `sig` lines stripped, under `docker.io/library/ruby:3.3-alpine --network none`), Docker Hub tag lookups with `curl`, and an `all-minilm:22m` pull into a scratch models directory that was then served read-only from a second throwaway `ollama/ollama:0.12.11` container. No cluster and no cloud account were used.

## 0. Coverage enumeration

### Sections

| # | Section | Disposition |
|---|---|---|
| S1 | Header, Goal (#6/#15/#17/#18/#19), Global constraints | ok: each goal bullet maps to a body section (#15 → §1, #17 → §2, #6/#18 → §3, #19 → §4). The constraints are process rules and not load-bearing. |
| S2 | §1a The kubelet needs no allow rule | under (a removed peer also served a non-kubelet caller): ok [negative]. The non-kubelet callers of each port are dispositioned in R2 (9000: none in-cluster), R3 (9090: API pods only, kept) and R1 (8080/8081/admin-ui/9080: LB and ingress-nginx sources, kept through `clusterCidrs` or the namespace peer). / over (the node exemption admits more than the kubelet): ok [existence]: it admits only the pod's own node (VA1), which is the platform's own exemption and not something the chart can narrow. Detail: The kubelet wording is in the comments at `networkpolicies.yaml:34-36` (api 8081), `:584-587` (prometheus), `:514-519` (admin-ui) and `:605-611` (authentik 9000). The node exemption comes from VA1, whose quote I re-read on kubernetes.io ("traffic to and from the node where a Pod is running is always allowed"), and VA4 (live). |
| S3 | §1a `clusterCidrs` table | see R1 |
| S4 | §1b Rule changes | see R2, R3, R4 |
| S5 | §1c Failure mode and the Azure check | ok, not load-bearing: the readiness failure is visible, and the fallback is stated in the spec. |
| S6 | §2a DNS goes only to kube-dns | see R5 |
| S7 | §2b API-server egress | see R6 and the closure rows → §2.1, §3.1 |
| S8 | §2c Dockerfiles (TEI and Ollama model images) | ok, split by evidence tier. **Re-run part: [compat].** VA21: `all-minilm:22m` pulled into a scratch dir, then served by `ollama/ollama:0.12.11 --network none --read-only --user 1000:1000 --tmpfs /tmp -e HOME=/tmp -e OLLAMA_MODELS=/models -v …:/models:ro`; `POST /api/embed` → `[GIN] … 200 … POST "/api/embed"` with an embedding vector in the body. Re-run in-round, VA23: `sha256sum` of the local manifest = `sha256sum` of the registry manifest body (`curl https://registry.ollama.ai/v2/library/all-minilm/manifests/22m`) = `1b226e2802dbb772b5fc32a58f103ca1804ef7501331012de126ab22f67475ef`, and `ollama list` ID `1b226e2802db` is its 12-char prefix. **Recorded part: [existence]** (recorded, not re-run; the spec's Verified assumption is accepted as ground truth per the skill and not re-litigated). VA19 (TEI serves offline from a read-only cache as uid 1000), VA20 (the TEI image builds with the revision baked in) and VA22 (the Ollama image builds and the full-digest check fails a prefix-sharing digest) are cited exactly as the spec records them. Nothing in this row relies on them beyond what they state. The arrows are A1–A3. |
| S9 | §2c Chart changes | → §2.3 (the Ollama env keeps pointing at the removed PVC path); the identity of derived names is R9 → §3.2 |
| S10 | §2c Upgrade note | ok [existence]: `volumeClaimTemplates` are at `charts/tei/templates/statefulset.yaml:87` and `charts/ollama/templates/statefulset.yaml:127`. Removing them is an immutable-field change, so deleting the StatefulSet first is required, as the spec says. |
| S11 | §2c Build and load | ok, not load-bearing: the plan chooses the save/load mechanism. Today the script calls `kind load docker-image` (`build-and-load-image.sh:66`). |
| S12 | §2c Sizes | dropped: the AWS Ollama pool's root volume is 20 GB (`cluster-aws/main.tf` launch template `volume_size = 20`). By estimate the baked ~5.7 GB image plus its compressed layers fits under kubelet's image-GC threshold. The failure is speculative, so it fails the literal-wrongness test. |
| S13 | §3a AKS cluster access | ok [negative]: `command grep -rn kube_config` (tf/md/yml/sh/ps1, excluding historical plans) finds only `azure/main.tf:47-58` and `modules/cluster-azure/outputs.tf:3`. Both provider blocks are consumers (A5). |
| S14 | §3b VNet Integration and user-assigned identity | under (a consumer of the cluster identity the spec does not move): ok [totality]: `grep -E 'identity\[0\]\|principal_id' modules/cluster-azure azure` finds exactly one consumer of the cluster identity (`main.tf:122`, the DES Reader assignment the spec moves). / over (moving something that is not the cluster identity's): ok [totality]: the same grep's only other hits, `:101-102`, belong to the DES's own identity, and the spec leaves them unchanged. Provider in-place semantics rest on VA27–VA29 (not re-run). |
| S15 | §3c Terraform state account | ok [negative]: `grep -E 'ARM_ACCESS_KEY\|access_key\|sas_token\|account-key'` over `Iverson.Server`, `.github` and `docs/runbooks` returns nothing, so no shared-key consumer breaks. `bootstrap/azure/main.tf` has no backend (VA34 reconfirmed). |
| S16 | §3d EKS instance metadata | see R13 |
| S17 | §3d rollout side effect | dropped: changing `metadata_options` creates a new launch-template version, and with `version = latest_version` EKS replaces nodes in every managed node group, stateful pools included. That is an operational concern; hop limit 1 is still achieved. |
| S18 | §4a Build context | see R11, R12 |
| S19 | §4b Console and nginx images | ok [existence]: the three nginx sites are at `Iverson.AdminUI/Dockerfile:24`, `docker-compose.yml:442` and `deployment-server.yaml:150` (VA45 reconfirmed by `grep`). `package.json` engines `>=22.22.0`. |
| S20 | §4c Small items | ok [existence]: the `.pyc` is tracked (`git ls-files` hit) and `.gitignore:82-83` = `__pycache__/`, `*.pyc`. `admin-ui.yml` has floating `@v7` at `:31`, `:34` and `:136`. `setup.ps1` has the six `helm upgrade` calls and no `--version`. `command grep` for the three console packages in `src/`, `vite.config.ts` and `vitest.config.ts` returns nothing. |
| S21 | §4d Third-party chart images and Dependabot | R10 → §2.2; directory roster R14 → §2.5 |
| S22 | §4e Image scanning | ok [existence]: `gh api …/git/ref/tags/v0.36.0` → tag object → commit `ed142fd0673e…`. GHSA-69fq-xp46-6x23 lists trivy-action `< 0.35.0`, setup-trivy `< 0.2.6` and trivy `= 0.69.4`. v0.36.0's `action.yaml:129` pins setup-trivy at v0.2.6. |
| S23 | §5a Helm tests | ok (assertion list). §2.2's fix adds one assertion. |
| S24 | §5b kind live checks | → §2.4. The four §1b rules are dispositioned per cell in §2.4. |
| S25 | §5c–§5e Image, Terraform and workflow checks | ok, not load-bearing |
| S26 | Verified assumptions | see §1 |
| S27 | Known issues | ok, but incomplete: they cover GKE endpoint inference and the Azure NPM kubelet exemption, not how the new egress rules (API-server ipBlock, kube-dns helper) match on each managed dataplane → §3.1 |

### Rules and operands

| # | Rule | Disposition |
|---|---|---|
| R1 | `clusterCidrs` = the LB source ranges per profile (ingress admission) | over: ok [totality]. AWS public subnets hold only the ALB, NAT and EKS ENIs, because node groups use `aws_subnet.private[*]` (`cluster-aws/main.tf:565`) and VPC CNI draws pod IPs from the node's subnet. Azure `10.1.16.0/24` is only the AGIC `subnet_cidr` (`cluster-azure/main.tf:195`); every node pool sits on `aks` `10.1.0.0/20` (`:136,:167,:222`). GCP's ranges belong to Google. / under: ok [totality], every Ingress source per profile. AWS: every ALB Ingress is `internet-facing` + `target-type: ip`, so the source is ALB nodes in the public subnets (values-aws `api`, `adminApiIngress`, `authentik`, `adminUi`). Azure: AGIC App Gateway instances. GCP: `gce` class with GFE ranges kept. laptop/local: ingress-nginx runs `hostPort` (`setup.sh:114`), so the source is a pod IP matched by the namespaceSelector. |
| R2 | Delete Authentik 9000's allow-all | over: n/a (a removal). / under: ok [negative]. `command grep -rn ':9000'` over sh/ps1/yml/yaml/py/cs/ts finds API env issuer strings (`charts/api/templates/deployment.yaml:152,174,200`, not connections); `mint_acting_user_token.py` uses `kubectl port-forward` (`:389-426`, exempt per VA5); compose and client tests are host-side or `localhost`. No in-cluster caller. |
| R3 | `prometheus-ingress` admits API pods only | over (admits a non-API source): ok [existence]: after the change the rule's only peer is `podSelector: { app: <release>-api }` (`networkpolicies.yaml:578-579`); the `clusterCidrs` peers at `:582-588` are the ones removed. / under: ok [negative]. `grep 'prometheus:9090'` → only `charts/api/templates/deployment.yaml:220`. Prometheus's own probe is kubelet traffic (exempt), and Prometheus scrapes outward via `dns_sd_configs` (`charts/prometheus/templates/configmap.yaml`). |
| R4 | "These four rules are the complete set" | [bidirectional] ok. `from: []` sits at `networkpolicies.yaml:514,605,615`, and `clusterCidrs` ranges at `:50,:59,:582`, so api, prometheus, admin-ui and authentik are the set. No NetworkPolicy exists in subcharts (VA8). |
| R5 | DNS helper: kube-system + `k8s-app=kube-dns`, 53/UDP+TCP | over: ok [existence]: the peer ANDs `namespaceSelector kubernetes.io/metadata.name=kube-system` with `podSelector k8s-app=kube-dns` on 53 only (spec §2a), so only kube-dns pods match; on kind, VA13 records an outside resolver failing. / under, per dataplane: **EKS** `UNVERIFIED` at run tier → §3.1. Read-tier evidence only: the controller's `getMatchingServiceClusterIPs` adds a Service ClusterIP when the peer's podSelector matches the Service selector (`amazon-network-policy-controller-k8s` `pkg/resolvers/endpoints.go`), and kube-dns's selector is `k8s-app: kube-dns` (VA12). **AKS** `UNVERIFIED` at run tier → §3.1. Label evidence only: CoreDNS carries `k8s-app=kube-dns` (VA12). **GKE**: the provider question is ok [negative]: `command grep -n -E 'dns_config\|dns_cache_config\|cluster_dns\|dns_cache' modules/cluster-gcp/main.tf` → no output (rc=1), so the Standard cluster keeps the kube-dns default (GKE service-discovery docs) and NodeLocal DNSCache is off. The matching question is `UNVERIFIED` at run tier → §3.1. **kind** ok [existence] (recorded, not re-run; the spec's Verified assumption is accepted as ground truth per the skill and not re-litigated): VA13 records "`nslookup kubernetes.default…` answered; `nslookup … 8.8.8.8` failed". TEI and Ollama get no DNS rule, by design. |
| R6 | `apiServerCidrs` replaces `to: []` on 443/6443 | over, per profile. **AWS** dropped: the four ranges are the cluster subnets (`cluster-aws/main.tf:90,105`), so postgres and kafka can also reach any node or pod in them on 443/6443. That breadth is the spec's stated trade-off ("/32s would go stale") and a security-tuning concern, not literal wrongness. **Azure `10.1.17.0/28`** ok [existence]: a new subnet delegated to `Microsoft.ContainerService/managedClusters` (§3b) that holds only the API-server ILB; VA9 records it as free in the `10.1.0.0/16` VNet (`cluster-azure/main.tf:127`; `aks` `10.1.0.0/20` at `:136`; AGIC `10.1.16.0/24` at `:195`). **GCP `172.16.0.0/28`** ok [existence]: `master_ipv4_cidr_block` (`cluster-gcp/main.tf:183`), reserved for the control plane; the node subnet is `10.2.0.0/20` (values-gcp). **laptop/local `172.18.0.0/16`, `10.89.0.0/16`** ok [existence]: kind node networks only; pods use `10.244.0.0/16` (values-laptop/local `trustedProxies.cidrs`), which is outside both. / under: → §2.1, §3.1, per the closure rows below. |
| R6-closure-a | (ipBlock API-server rule × dataplane). Each cell is the under-inclusion direction (legitimate API-server traffic blocked); over-inclusion is ok for every cell per R6 | **kind/Calico:** ok [existence] (recorded, not re-run, since there was no kind cluster this round; the spec's Verified assumption is accepted as ground truth per the skill and not re-litigated). VA16 records: "`nc kubernetes.default 443` OPEN with ipBlock `10.89.0.0/16`; BLOCKED for a pod with only the DNS rule". **EKS:** → §2.1. **AKS (Azure NPM):** `UNVERIFIED`: whether NPM evaluates before or after the DNAT, and whether it sees the VNet-integration ILB VIP; not runnable in-round → §3.1. **GKE (Dataplane V2/Cilium):** `UNVERIFIED`: whether a CIDR policy selects the `kube-apiserver` reserved identity of the external control plane (cilium/cilium#20550 shows ipBlock to API-server endpoints denied in Cilium 1.11) → §3.1. |
| R6-closure-b | (ipBlock egress peers in the chart after the change × the EKS pre-DNAT rule) | under (a peer missed by the sweep): [totality] ok. over: n/a, because the EKS pre-DNAT rule can only cause an ipBlock to miss traffic (the agent compares the ClusterIP, which no subnet range contains), never to admit extra traffic. Over-inclusion of the ranges themselves is dispositioned in R6 "over". After the change the chart's only ipBlock egress peers are the two `apiServerCidrs` rules (`networkpolicies.yaml:194-212` → postgres-egress, `:265-274` → kafka-egress); both → §2.1. Every other ipBlock is ingress (`clusterCidrs`), where ALB, AGIC and GFE connect to pod IPs with no Service DNAT. Pre-existing podSelector egress peers to Services fall under a different rule, outside this design's change set: dropped (TEI's Service is headless, `charts/tei/templates/service.yaml` `clusterIP: None`). |
| R7 | TEI and Ollama get no egress | over (admits some egress): ok [existence]: §2c leaves `tei-egress`/`ollama-egress` with no rules, and the namespace default-deny (`networkpolicies.yaml:11-19`, `podSelector: {}`, `policyTypes: [Ingress, Egress]`) applies to every pod, so no egress path exists. / under: ok, three parts. TEI offline: [existence] (VA19/VA20 recorded, not re-run; the spec's Verified assumption is accepted as ground truth per the skill and not re-litigated). Ollama offline: [compat], re-run in-round (S8: `--network none --read-only` serve of `all-minilm:22m` → `/api/embed` 200). The API never pulls: [negative]. The API never asks Ollama to pull (`command grep -rn -E 'api/pull\|PullModel' --include=*.cs Iverson.Server` → empty, VA25). |
| R8 | Ollama full-digest check | over (accepts a wrong model): ok [existence] (recorded, not re-run, because building the model image needs the 1.9 GB model; the spec's Verified assumption is accepted as ground truth per the skill and not re-litigated). VA22 records: "a digest sharing the real one's first 12 characters failed the build with the FATAL message". / under (rejects the right model): ok, two parts. Part 1 is [existence] (recorded, not re-run; the spec's Verified assumption is accepted as ground truth per the skill and not re-litigated): VA22 records "built with the correct digest" (spec-recorded tier). Part 2 is [compat]: VA23's premise, that the manifest file's SHA-256 is the full registry digest, was re-run in-round (see S8: local manifest = registry manifest body = `1b226e28…75ef`). |
| R9 | Derived model-image name as image identity | over-merge: engine → §3.2, because two different engine bases build under the same tag. Model: ok [existence]. TEI `<slug>-<revision12>` is distinct per entry, and a duplicate slug already collides on `<release>-tei-<slug>` object names (`charts/tei/templates/statefulset.yaml:5,11`); Ollama's sanitised name plus `digest[0:12]` separates colliding sanitised names by digest (VA24: 64 hex). / under-merge (one model and engine given two names): ok [existence]: the name is a pure function of `values` fields (`.slug`, `.revision`, `generativeModel`, `generativeModelDigest`), so identical inputs render identical names. |
| R10 | "Chart images become Dependabot-readable scalars" × every third-party image reference | over: n/a (an extra readable pin is harmless). / under: → §2.2. The matrix is dispositioned in §2.2: base `values.yaml` cells ok; 9 overlay cells and 3 template-literal cells fail. First-party `api/worker/adminUi` `repository`+`tag` maps are dropped (not third party). |
| R11 | `.dockerignore` additions | over (excludes something a build needs): ok [totality]. The only tracked `.env*` is `Iverson.AdminUI/.env.development` (VA43), which a production `vite build` does not read. `grep -E '^\s*COPY.*docs'` over every tracked Dockerfile is empty. `.claude/` and `.worktrees/` are untracked. / under (lets a secret through): ok [totality]. Root-context builds (Api, AdminUI, compose `context: ..` at `docker-compose.yml:482,606`) get the root rules; `Iverson.Server`-context builds (Events, Sql, Vector, Launcher) get the `Iverson.Server/.dockerignore` rules. |
| R12 | Api explicit COPY of nine directories | under: ok [totality]. Every `Include=` in the 8 csprojs is a `ProjectReference` inside the listed set, plus `Protobuf Include="../../Common/Proto/*.proto"`. `git ls-files` shows no `Directory.*`, `global.json` or `nuget.config` (rc=1). / over (the copy brings in files a build should not see): ok [totality]: the nine directories are source projects. Their `.env`, `bin` and `obj` content is excluded by the root `.dockerignore` (`**/bin`, `**/obj` today; `**/.env*` added by §4a), and §5c's canary check covers it. |
| R13 | Eligibility: workloads that need IMDS on EKS (hop limit 1) | under (a producer the sweep misses): [totality] ok, producers enumerated. Add-ons: `vpc-cni` (host network) and `aws-ebs-csi-driver` (IRSA, pinned `v1.37.0-eksbuild.1` at `cluster-aws/main.tf:504`, node-plugin fallback documented for that version). Helm releases (`modules/operators/main.tf`): autoscaler (`awsRegion` + IRSA), LB controller (→ `region`/`vpcId`), cnpg, strimzi and starrocks-operator (no AWS identity), pvc-policy. Chart: `grep -i -E 's3\|amazonaws\|169\.254\|barman\|backup'` over the templates is empty, and no csproj references an AWS SDK. / over (a listed workload wrongly assumed to lose nothing): ok [existence], the LB controller gets `region`/`vpcId`, EBS CSI gets IRSA `AWS_REGION` (VA36), and the autoscaler already sets `awsRegion`. |
| R14 | Dependabot directory rosters | over: n/a (an extra entry only adds a no-op update job). / under:<br>**docker**: ok [totality]: `git ls-files \| grep -i dockerfile` → AdminUI, Api, Events, Launcher, Sql, Vector (six), all listed, plus the two new model directories.<br>**docker-compose**: ok [totality]: `git ls-files \| grep -i -E 'compose.*\.ya?ml$'` → only `Iverson.Server/docker-compose.yml` (the other hit, `charts/authentik/blueprints/compose-only/service-clients.yaml`, is an Authentik blueprint, not a compose file).<br>**helm**: ok [totality]: `git ls-files \| grep 'Chart\.yaml$'` → the umbrella plus 13 subcharts, all covered by "the umbrella chart and each subchart directory". The fifteenth, `terraform/modules/operators/charts/pvc-storageclass-policy`, has no images (`grep image` over its templates: no output) and no chart dependencies, so there is nothing to track.<br>**terraform**, one cell per locked root (`git ls-files \| grep '\.terraform\.lock\.hcl$'`): `aws` ok; `azure` ok; `gcp` ok; `bootstrap/azure` ok; `bootstrap/aws` → §2.5; `bootstrap/gcp` → §2.5. |

### Data-flow arrows

| # | Arrow | Disposition |
|---|---|---|
| A1 | `global.embeddingModels[*]` → TEI image name. Two call sites: the chart template and the build script | Chart: ok [existence], the `range` exposes `.slug` and `.revision` (`charts/tei/templates/statefulset.yaml:1,54`). Build script: ok, not load-bearing. The plan chooses the source, and if it diverges the result is a visible `ImagePullBackOff`, which is the spec's stated intent. |
| A2 | `global.generativeModel` / `generativeModelDigest` → Ollama image name + Dockerfile check | ok [presence]: `values.yaml:60` holds 64 hex (VA24). |
| A3 | **Persistence boundary**: image `ENV` (baked) → pod-spec `env` (runtime override) | TEI: ok [negative]: `command grep -n -E 'env:\|HF_\|HUGGINGFACE\|/data\|args:' charts/tei/templates/statefulset.yaml` → `:54` (args `--model-id/--revision/--auto-truncate/--port`, no cache path), `:70` (comment) and `:81` (`mountPath: /data`, removed by §2c). There is no `env:`, so nothing overrides the image's `HUGGINGFACE_HUB_CACHE`/`HF_HUB_OFFLINE`. **Ollama: → §2.3.** |
| A4 | `cluster-aws.vpc_id` → `operators.vpc_id` → LB controller `vpcId` | ok [existence]. The only caller that passes it is `aws/main.tf` `module "operators"`; the LB release is `count = var.cloud == "aws"` (`operators/main.tf:111`); azure/gcp take default `null` (VA38). |
| A5 | `cluster-azure` host/CA → `azure/main.tf` `provider "kubernetes"` (`:46-51`) and `provider "helm"` (`:53-60`): two call sites | ok [existence]. The spec replaces both with the exec block. |
| A6 | `bootstrap/azure` outputs → `azure` backend (`-backend-config`) + `use_azuread_auth` | ok [existence]: `bootstrap/azure/main.tf:53-55` outputs `resource_group_name`, `storage_account_name`, `container_name`, which are exactly the three keys `azure/main.tf:137-142` says are supplied by `-backend-config` (the block itself sets only `key`). §3c adds `use_azuread_auth` to that block and changes none of the three outputs. |
| A7 | `values-laptop.yaml` → §5b live checks | → §2.4. Per-rule cells: api-ingress ok, authentik-ingress ok, prometheus-ingress → §2.4, admin-ui-ingress → §2.4 (table in §2.4). |
| A8 | Dependabot helm: fetch → parse → update | → §2.2 |
| A9 | Rendered chart → image-scan third-party extraction | ok, not load-bearing (the plan picks the filter) |
| A10 | Dependabot docker bumps a model Dockerfile base → rebuilt model image → running TEI/Ollama pods | → §3.2 |

Row tally: 27 section rows + 16 rule rows (R1–R14 plus R6-closure-a and R6-closure-b) + 10 arrow rows = **53 rows: 35 ok, 16 → findings, 2 dropped**. The 16 rows that lead to findings are S7, S9, S21, S24, S27, R5 (runtime residue → §3.1), R6, R6-closure-a, R6-closure-b, R9, R10, R14, A3, A7, A8, A10. The dropped rows are S12 and S17. Pointer rows (S3, S4, S6, S16, S18, S26) are counted as ok; their dispositions are the rows they name.

## 1. Verified-assumptions cross-check

**Reconfirmed by a fresh read in this round:** 1 (the kubernetes.io quote), 6 (`charts/api/templates/deployment.yaml:186-196`: BaseUrl and MetadataAddress on 8443), 7 (`cluster-aws/main.tf:90,105,263`), 8 (`:514,:605,:615`), 9 (`cluster-azure/main.tf:132-137,194-196`), 11 (14 × `to: []` on 53; 443/6443 at `:212,:274,:462,:493`), 18 (`cluster-gcp/main.tf:183`), 24 (`values.yaml:60`), 25 (grep empty), 32 (grep), 34, 38 (`cluster-aws/outputs.tf` has no `vpc_id`), 41 (csproj `Include` closure; `git ls-files` shows no props or config), 42, 43, 45, 46, 48 (`command grep -rln imageTag` → 7 subchart templates and values, the umbrella `values.yaml`, and the aws/azure/gcp overlays), 49 (as stated: `handle_string_value`, `key == "image" && value.include?(":")`; the fetcher reads one directory), 51, 52 (via `gh api` and the advisory API), 21 and 23 (re-run against `all-minilm:22m`; see S8), 53 (holds; one nit: the `setup-node` SHA is at `dependency-scan.yml:32`, not `:17-18`).

**Accepted as ground truth; cited evidence not re-fetched or re-run in this round** (live kind and container runs, provider schema dumps, vendor docs): 2, 3, 4, 5, 10, 12, 13, 15, 16, 17, 19, 20, 22, 26, 27, 28, 29, 30, 31, 33, 35, 36, 37 (independently re-enumerated as R13: consistent), 39, 40, 44, 47, 50.

**Failed:** **14.** It cites the amazon-network-policy-controller-k8s README for "EKS API-server traffic must be allowed by DNAT'd endpoint (ClusterIP pre-DNAT resolution)". A fresh read of that README says the opposite of what 2b relies on: "When a pod reaches another pod through its Service ClusterIP, the network policy controller resolves the traffic through the Service IP (pre-DNAT)", meaning a rule must match the ClusterIP, not the DNAT'd endpoint. The controller source (`pkg/resolvers/endpoints.go`) adds Service ClusterIPs only for podSelector peers; an ipBlock peer `continue`s before that step. → §2.1.

**Span check (uncovered dependencies):**
- U1. Which values files Dependabot's helm parser reads (`VALUES_YAML = /.*values\.ya?ml$/i`). VA49 cites the key matcher but not this file filter. Verified in-round → §2.2.
- U2. That no pod-spec env overrides the derived images' baked `ENV` (`OLLAMA_MODELS`, `HUGGINGFACE_HUB_CACHE`). No VA covers this. Verified in-round → §2.3.
- U3. That the new egress rules match on the managed dataplanes at runtime: the API-server ipBlock on AKS (NPM) and GKE (Dataplane V2), and the kube-dns helper on EKS, AKS and GKE. VA13 and VA16 cover only kind/Calico. Kubernetes documents this as undefined ("Connections from pods to Service IPs that get rewritten to cluster-external IPs may or may not be subject to ipBlock-based policies"). Unverifiable in-round → §3.1.
- U4. That a base-image bump in a model Dockerfile reaches running pods. The derived tag carries no engine component → §3.2.
- U5. That `values-laptop` renders the components §5b's live checks exercise. Verified in-round: it does not → §2.4.

## 2. Literal-wrongness findings

### 2.1 On EKS, `apiServerCidrs` cannot match API-server traffic, so CNPG and Kafka never start on AWS

**Description.** §2b gives AWS `apiServerCidrs` as the four cluster subnets, because "EKS places its control-plane interfaces in the cluster subnets". The CNPG instance manager and the Strimzi broker and entity-operator reach the API server through the `kubernetes` Service ClusterIP (in-cluster config, `KUBERNETES_SERVICE_HOST`). The EKS VPC CNI network-policy agent evaluates pod egress at the pod's host-side veth, before kube-proxy's Service DNAT, so the destination it sees is the ClusterIP. The controller only adds ClusterIPs for podSelector peers whose selector matches a Service selector. The `kubernetes` Service has no selector, and these rules are ipBlocks. The ipBlocks therefore never match, and on AWS both rules (postgres-egress and kafka-egress, the only two members of the family; see R6-closure-b) deny the traffic. This is exactly the failure their own comments describe: the Cluster "never leaves 'Setting up primary'" (`networkpolicies.yaml:194-197`) and the broker "crash-loops" (`:265-268`). The spec's AWS check is static-only (`helm template`), which cannot catch this. VA14 records the README's sentence with the meaning reversed (§1).

**Evidence.** These are read-tier claims; no EKS cluster was available in-round, consistent with the spec's static-only constraint.
- [negative] `amazon-network-policy-controller-k8s` `pkg/resolvers/endpoints.go`: `if peer.IPBlock != nil { … egressEndpoints = append(…, r.buildIPBlockEndpoint(…)); continue }` runs before `getMatchingServiceClusterIPs`, which is reached only for `peer.PodSelector`.
- [existence] The controller README: traffic to a Service ClusterIP "resolves … through the Service IP (pre-DNAT)".
- [existence] The `aws-network-policy-agent` README: probes are "attached to pod's host Veth interface".
- [negative] `command grep -n -E 'service_ipv4_cidr|kubernetes_network_config' modules/cluster-aws/main.tf` returns nothing, so EKS picks the service range itself.

**Proposed fix.**
- Add the `kubernetes` Service ClusterIP as a `/32` to the AWS `apiServerCidrs`, and keep the subnet ranges.
- Because `cluster-aws` leaves `service_ipv4_cidr` unset and EKS assigns "either 10.100.0.0/16 or 172.20.0.0/16" (and the value cannot change after creation), list both `10.100.0.1/32` and `172.20.0.1/32`. Pinning `service_ipv4_cidr` in Terraform instead would force replacement of existing clusters whose range differs.
- Rewrite §2b's AWS "Why" cell and VA14 to say traffic is matched pre-DNAT on the ClusterIP.
- Add an AWS post-deploy check to the runbook: CNPG `Cluster` healthy and Kafka brokers Ready. §3.1 option B would apply the same dual-entry approach to the other clouds.

Evidence:
- [existence] AWS EKS `KubernetesNetworkConfigRequest` / CloudFormation `KubernetesNetworkConfig` docs: "If you don't specify a block, Kubernetes assigns addresses from either the 10.100.0.0/16 or 172.20.0.0/16 CIDR blocks … you can't change this value after the cluster is created".
- UNVERIFIED: that the `kubernetes` Service always takes the first address of the service range. I did not read the apiserver source this round.
- UNVERIFIED: that the agent admits the flow once the ClusterIP `/32` is listed. No EKS was available.

### 2.2 Dependabot will not update the cloud profiles' chart images or the three template-literal images

**Description.** §4d's mechanism ("each subchart's `imageTag` value becomes `image:`") assumes Dependabot's helm ecosystem reads every place a chart image is pinned. It does not.
1. The parser reads only files whose names end in `values.yaml`. The cloud overlays pin `prometheus`, `redis` and `authentik` images themselves (VA48 lists them as `imageTag` consumers that get renamed). After the rename those overlay pins are invisible to Dependabot, and because overlays override `values.yaml`, they silently keep AWS, Azure and GCP on the old images while every Dependabot PR appears to bump the chart.
2. The fetcher lists only regular files in the entry's directory, so `templates/` is never read. The spec gives the tls-proxy's move to a tag the rationale "trading immutability for automated updates", but that image (`charts/authentik/templates/deployment-server.yaml:150`), `postgres:16` (`charts/authentik/templates/job-revoke-cross-db.yaml:32`) and `mysql:8.0` (`charts/starrocks/templates/job-create-user.yaml:31`) are template literals with no values key under the spec's mechanism. For the tls-proxy, the change gives up immutability and gains no automated updates.

**Population matrix** (rule: "Dependabot helm reads it" × every third-party chart image reference):

| Cell | Read? | Disposition |
|---|---|---|
| Umbrella `values.yaml`: qdrant, jaeger, prometheus, redis, authentik (tei and ollama leave the chart per §2c) | yes (`values.yaml` matches; `image:` scalar) | ok |
| Subchart `values.yaml` × the same 5 | yes | ok |
| `starrocks.fe.image` / `be.image` (already scalars) | yes (`find_images_in_hash` recurses) | ok |
| `values-aws.yaml:140,149,156`; `values-azure.yaml:131,140,147`; `values-gcp.yaml:139,148,155` (9 cells) | **no**: filename does not match `/.*values\.ya?ml$/i` | → §2.2 |
| `values-laptop.yaml`, `values-local.yaml`, `values-aws.ci-override.yaml`, `values-azure.ci-override.yaml`, `values-gcp.ci-override.yaml` | not parsed either (same filename filter) | ok [negative]: they carry no image pin. `grep -c -E '^\s*(imageTag\|image\|repository\|tag):'` → 0 in each of these five files (3 in each of aws/azure/gcp). |
| tls-proxy, `postgres:16`, `mysql:8.0` (3 template literals) | **no**: under `templates/`, and no `image` key | → §2.2 |
| First-party `api`/`worker`/`adminUi` `image.repository`+`tag` | parsed as Docker Hub names | dropped (not third-party; a failed lookup changes no pin) |
| CNPG and Strimzi operands | not in the chart | ok (pinned through operator chart versions, as the spec says) |

**Evidence.**
- [negative] `helm/lib/dependabot/helm/file_parser.rb` (dependabot-core `main`, fetched with `curl`): `VALUES_YAML = /.*values\.ya?ml$/i`; `helm_values_files` selects by that pattern; `handle_string_value` matches only `key == "image"` (with `:`) or `repository`+`tag`.
- [totality] The parser's own `VALUES_YAML` constant run in Ruby (`ruby:3.3-alpine`) over `Dir.children` of the umbrella chart: `values.yaml VALUES_YAML=true`; `Chart.yaml` and all eight `values-*.yaml` → `false`. A `python3` run of the same regex agrees.
- [negative] `file_fetcher.rb`: `repo_contents(raise_errors: false).select { |f| f.type == "file" && f.name.match?(FILENAME_REGEX) }`, so subdirectories such as `templates/` are skipped.
- [presence] The overlay lines above, from `command grep -n imageTag values-{aws,azure,gcp}.yaml`.

**Proposed fix.**
- Delete the `prometheus.imageTag`, `redis.imageTag` and `authentik.imageTag` lines from the three cloud overlays. Each equals its base value (`v2.55.1`, `7.4-alpine`, `2026.5.3` in both the overlays and `values.yaml`), so the umbrella `values.yaml` becomes the single pin, and no overlay carries an image.
- Move the three template literals into their subcharts' `values.yaml` under a key literally named `image` (for example `tlsProxy: { image: "nginxinc/nginx-unprivileged:1.30.5-alpine" }` and `revokeCrossDb: { image: "postgres:16.15" }` in authentik, and `createUser: { image: "mysql:8.0.46" }` in starrocks). Templates read those values.
- Add a §5a assertion: no third-party image reference appears in any `values-*.yaml` overlay or as a literal in a template.

Evidence:
- [compat] Run in-round. dependabot-core's `find_images_in_hash`, `handle_string_value` and `handle_array_value` were copied verbatim (Sorbet `sig` blocks and one `T.let` wrapper stripped) and run in `ruby:3.3-alpine` on a sample values hash. Output: `authentik.image`, `authentik.tlsProxy.image => nginxinc/nginx-unprivileged:1.30.5-alpine`, `authentik.revokeCrossDb.image => postgres:16.15`, `starrocks.fe.image` and `api.image.repository => iverson-api:0.1.0` were detected, while a sibling key `tlsProxyImage` was **not**. So nested `image` keys work and a differently named key would be silently missed. This is the real parsing logic, not the full Dependabot job (no registry lookup or update step).
- [presence] Overlay = base: `values-{aws,azure,gcp}.yaml` set `prometheus.imageTag: "v2.55.1"`, `redis.imageTag: "7.4-alpine"`, `authentik.imageTag: "2026.5.3"`; `values.yaml` sets the same three values.
- [existence] The exact tags exist: `curl https://hub.docker.com/v2/repositories/<repo>/tags/<tag>` → HTTP 200 for `library/postgres:16.15`, `library/mysql:8.0.46`, `library/redis:7.4.11-alpine` and `nginxinc/nginx-unprivileged:1.30-alpine`. A tag listing of `nginxinc/nginx-unprivileged` filtered to `1.30.*-alpine` returns `1.30.0` through `1.30.5`, so `1.30.5-alpine` is today's newest; the spec's `1.30.x` must name one.
- [existence] `deploy-validate.yml` renders all five overlays, so deleting the overlay lines leaves the overlays rendering from base values.

### 2.3 The Ollama container keeps `OLLAMA_MODELS=/data`, so the baked image cannot serve

**Description.** §2c bakes the model under `/models` and sets `OLLAMA_MODELS=/models` in the image. The chart changes it lists remove the init container and the PVC and repoint `HOME`, but they leave the main container's `env: - { name: OLLAMA_MODELS, value: "/data" }` (`charts/ollama/templates/statefulset.yaml:103`). Pod-spec env overrides image `ENV`. With the `/data` mount gone and the root filesystem read-only, `ollama serve` exits at startup and the StatefulSet crash-loops. Even with a writable `/data` it would find no models.

Sibling sweep (container env and args naming the removed PVC path):
- Ollama `HOME=/data`: handled by the spec (moves to tmp).
- Ollama init container: removed by the spec.
- TEI: no env; args `--model-id/--revision/--auto-truncate/--port` name no cache path (`charts/tei/templates/statefulset.yaml:54`); the `/data` mount is removed by the spec. ok.

**Evidence.** [compat] A run entering through `ollama serve` with the chart's surviving env:

```
docker run --rm --memory 1g --network none --read-only --user 1000:1000 --tmpfs /tmp \
  -e HOME=/tmp -e OLLAMA_MODELS=/data docker.io/ollama/ollama:0.12.11 serve
```

The log shows `OLLAMA_MODELS:/data`, then `Error: mkdir /data: read-only file system: ensure path elements are traversable`, the server exits, and `ollama list` reports "could not connect". The run would have come out the other way if Ollama tolerated a missing models directory on a read-only root.

**Proposed fix.** Delete the `OLLAMA_MODELS` env entry from the Ollama container so the image's `/models` applies, or set it explicitly to `/models`. Add a §5a rendered assertion that no TEI or Ollama container env, args or volumeMount references `/data`.

Evidence:
- [compat] VA21: Ollama 0.12.11 serves from a read-only models directory as uid 1000 with `HOME=/tmp`.
- [compat] The probe above shows a container `-e` overrides the path Ollama uses.
- UNVERIFIED: a run of the derived image itself (not built in-round).

### 2.4 §5b's live check on `values-laptop` cannot exercise two of the four §1b rules, and its 9090 refusal passes vacuously

**Description.** §5b runs on `values-laptop`, which sets `global.prometheusEnabled: false` and `adminUi.enabled: false`.
- The rendered laptop chart has no Prometheus and no console, so the `prometheus-ingress` policy is not rendered at all.
- `admin-ui-ingress` renders but selects no pods.
- "9090 refused" therefore passes with or without the new rule, because nothing listens.
- "the API and console through ingress" cannot run.

The two rules §1b changes for #15 besides api and authentik (prometheus 9090 and the console's former allow-all) get no live verification, although the spec's global constraint says NetworkPolicy behaviour "is verified live on a throwaway kind cluster".

Per-rule cells (§1b's four rules × "live-checkable on `values-laptop`"):

| §1b rule | Policy rendered? | Selected workload rendered? | Disposition |
|---|---|---|---|
| `api-ingress` (8080, 8081) | yes (`iverson-api-ingress`, ports 8080/8081) | yes (`iverson-api` Deployment) | ok [existence]: live-checkable as §5b describes |
| `authentik-ingress` (9000, 9080, 8443) | yes (`iverson-authentik-ingress`, ports 8443/9000/9080) | yes (`iverson-authentik-server` Deployment) | ok [existence]: live-checkable, including the port-forward-to-9000 check |
| `prometheus-ingress` (9090) | **no** | **no** | → §2.4 (vacuous refusal) |
| `admin-ui-ingress` (8080) | yes (`iverson-admin-ui-ingress`) | **no** | → §2.4 (selects no pods; console smoke cannot run) |

**Evidence.**
- [presence] `values-laptop.yaml`: `prometheusEnabled: false`, `adminUi: enabled: false`.
- [totality] `helm template iverson . -f values-laptop.yaml` in a scratch chart copy (run twice this round) renders 5 Deployments (`api`, `authentik-server`, `authentik-worker`, `redis`, `worker`) and 24 NetworkPolicies, with no `iverson-prometheus-ingress` and with `iverson-admin-ui-ingress` present but no admin-ui workload.

**Proposed fix.**
- Run §5b with `--set global.prometheusEnabled=true --set adminUi.enabled=true`, and build and load `iverson-admin-ui`.
- Pair each refusal with a positive control that must succeed: an API pod reaches `prometheus:9090`; ingress-nginx serves `/admin` from the console's port 8080. A refusal then cannot be confused with an absent listener.

Evidence:
- [existence] `helm template … -f values-laptop.yaml --set global.prometheusEnabled=true --set adminUi.enabled=true` exits 0 and renders `iverson-prometheus`, `iverson-prometheus-ingress` and the `iverson-admin-ui` Deployment.
- [existence] Positive-control target: `charts/admin-ui/templates/ingress.yaml` routes host `global.ingressHost`, path `/admin(/|$)(.*)`, to Service `<release>-admin-ui` port 8080; `service.yaml` maps 8080→8080; the container listens on 8080 (`deployment.yaml:53`). On kind the nginx class also needs `allowSnippetAnnotations`, which `setup.sh` sets (per the ingress template comment). So the control is "ingress-nginx serves `/admin` on the console's 8080". UNVERIFIED: that it returns 200 live (no kind cluster this round).
- UNVERIFIED: that the extra pods fit the 9 GB VM. `values-laptop` sets no Prometheus resources, so the base request of 512Mi and limit of 1Gi apply.

### 2.5 The Dependabot `terraform` roster omits two locked roots

**Description.** §4d adds `terraform` for `aws`, `azure`, `gcp` and `bootstrap/azure`. `bootstrap/aws` and `bootstrap/gcp` are Terraform roots with committed lockfiles too, so their providers would stay unmonitored. #19's "no Dependabot coverage for … terraform" stays open for them.

**Evidence.** [totality] `git ls-files | command grep '\.terraform\.lock\.hcl$'` lists six roots. Cells: `aws` ok (listed), `azure` ok (listed), `gcp` ok (listed), `bootstrap/azure` ok (listed), `bootstrap/aws` → §2.5, `bootstrap/gcp` → §2.5.

**Proposed fix.** Add `/Iverson.Server/deploy/terraform/bootstrap/aws` and `/Iverson.Server/deploy/terraform/bootstrap/gcp` to the `terraform` entry.

Evidence: [totality] the same `git ls-files` listing (six lockfiles; the spec names four roots).

## 3. Forced decisions

### 3.1 How the new egress rules are admitted on managed dataplanes where their runtime matching is unverified (API-server ipBlock on AKS/GKE; kube-dns helper on EKS/AKS/GKE)

**The choice.** Whether to rely, untested, on how the managed dataplanes match (a) §2b's API-server ipBlocks on AKS and GKE and (b) §2a's kube-dns helper on EKS, AKS and GKE, and what to do if either does not hold. Both have been observed only on kind/Calico (VA13, VA16).

**Why it is forced.** §2b's Azure (`10.1.17.0/28`) and GCP (`172.16.0.0/28`) values rest on assumptions about where the policy engine sees the destination. Kubernetes documents this as implementation-defined: "Connections from pods to Service IPs that get rewritten to cluster-external IPs may or may not be subject to ipBlock-based policies" (kubernetes.io, Network Policies, ipBlock). The one managed dataplane checkable from source (EKS) evaluates pre-DNAT (§2.1). On GKE Dataplane V2 (Cilium), ipBlock rules to API-server endpoint IPs have been observed denied, because those IPs carry the `kube-apiserver` reserved identity (cilium/cilium#20550; resolved there only by the Cilium-specific `toEntities: kube-apiserver`, which a Kubernetes NetworkPolicy cannot express). The failure is visible (CNPG and Kafka never Ready), but it is a failed first deploy of the data tier on that cloud. The spec's Known issues accept GKE's endpoint address, not the matching semantics.

The DNS residue (R5) has the same shape. On EKS the helper's match depends on the controller adding kube-dns's ClusterIP for a podSelector that equals the Service selector; that is read from source, not run. On AKS and GKE it depends on the engine matching the post-DNAT kube-dns pod IPs; I have label and provider evidence only. A DNS miss is louder than an API-server miss: every pod that resolves names fails, also on first deploy.

**Options.**
- **A. Verify on first deploy.** Keep §2a/§2b as written. Add a runbook gate per cloud (an in-namespace pod resolves `kubernetes.default`; CNPG `Cluster` healthy; Kafka Ready) with documented fallbacks: for the API server, add the `kubernetes` Service ClusterIP `/32`, then, if still blocked, restore a port-only rule for that profile; for DNS, restore the port-only DNS rule for that profile.
  Evidence: [existence] the failure modes are in `networkpolicies.yaml:194-197, 265-268`. UNVERIFIED: whether either fallback suffices on GKE.
- **B. Dual entries now on every cloud.** List both the endpoint range and the `kubernetes` Service ClusterIP `/32` per profile, so the rule matches whether the engine evaluates pre- or post-DNAT. AKS's default `service_cidr` is `10.0.0.0/16` (VA9: outside the VNet), so the ClusterIP would be `10.0.0.1`. GKE's service range is auto-allocated (`cluster-gcp/main.tf` `ip_allocation_policy {}`), so its ClusterIP must be read on first deploy.
  Evidence: [existence] `cluster-azure/main.tf` sets no `service_cidr`; [existence] `cluster-gcp/main.tf` `ip_allocation_policy {}`. UNVERIFIED: AKS default `10.0.0.0/16` (not re-fetched this round); UNVERIFIED: that a CIDR entry selects the `kube-apiserver` identity on GKE Dataplane V2.
- **C. Keep the port-only rules on unverified dataplanes.** Use the new rules on kind (verified, VA13/VA16). A per-profile switch keeps `to: []` on 443/6443 for AKS and GKE, and on 53 for EKS, AKS and GKE, until a deploy confirms them (the AWS API-server rule can use §2.1's fix). This leaves #17's DNS half open on every cloud and its API-server half open on two.
  Evidence: [existence] the current rule shapes are at `networkpolicies.yaml:194-212, 265-274` (API server) and the 14 `to: []` DNS rules (VA11). UNVERIFIED: none further.

**Why no option dominates.** B addresses only the API-server half, not DNS. It lowers the API-server risk on AKS but does not resolve the GKE identity question, so it does not dominate A. C is the only option that cannot fail a deploy, but it gives up part of #17 on every cloud. A closes #17 everywhere but accepts a failed first deploy as the detection mechanism. A hybrid (B with A's gate) still leaves GKE unverified, so it differs from A only in cost on AKS.

### 3.2 The model-image tags carry no engine version, so a base-image bump never reaches running pods

**The choice.** How a change to a model Dockerfile's base (TEI or Ollama engine) becomes visible to the chart.

**Why it is forced.** §2c derives the tags only from the model pin (`<slug>-<revision[0:12]>`, `<model>-<digest[0:12]>`). §4e relies on "their base images are covered by Dependabot's docker ecosystem". When Dependabot bumps `text-embeddings-inference:cpu-1.8.3@sha256:…` or `ollama/ollama:0.12.11@sha256:…`, the rebuilt image gets the same tag. The rendered StatefulSet is unchanged, so `helm upgrade` rolls nothing, and with `imagePullPolicy: IfNotPresent` (`charts/tei/templates/statefulset.yaml:48`; Ollama sets none, which means IfNotPresent for a non-`latest` tag) nodes that cached the old tag keep it even across restarts. The patched engine never deploys on cloud, and new and old nodes can run different engines under one tag. The spec's claim that "the pin and the image cannot diverge" holds for the model, not the engine.

**Options.**
- **A. Chart-side content hash.** Move the two Dockerfiles inside the chart directory, render the tag suffix as `.Files.Get "<path>/Dockerfile" | sha256sum | trunc 12`, and have the build script compute the same hash. Any Dockerfile change, Dependabot's included, changes the tag and rolls the StatefulSet. Any byte change rebuilds a ~5.7 GB Ollama image.
  Evidence: [compat] scratch chart probe: rendered `tag: e86664a9decc` (equal to `sha256sum … \| cut -c1-12`), and after editing the FROM line `1.8.3→1.8.4` it rendered `tag: 6bdd43100c2a`. UNVERIFIED: that Dependabot's docker entries work with the Dockerfiles at their new location (the directory entries would change).
- **B. Explicit engine value in the chart.** Add `tei.engineTag` / `ollama.engineTag` values that are part of the derived name and are passed as a build argument. The Dockerfile base is pinned twice (Dockerfile and values) and kept in step by hand; a forgotten values bump yields a tag mismatch that shows up as `ImagePullBackOff`.
  Evidence: [existence] the chart values already carry `tei.imageTag` / `ollama.imageTag` (`values.yaml`), which §2c removes. UNVERIFIED: whether Dependabot's docker updater rewrites a `FROM` that interpolates an `ARG`; if the engine tag moves into an ARG, Dependabot tracking could be lost.
- **C. Accept and document.** Keep the tags. The runbook says that after a base bump you rebuild, push, and `kubectl rollout restart` with `imagePullPolicy: Always` for the model StatefulSets.
  Evidence: [existence] `charts/tei/templates/statefulset.yaml:48` (`IfNotPresent`). UNVERIFIED: none further.

**Why no option dominates.** A is the only option that is automatic end to end, but it moves build inputs into the chart package and couples the tag to file bytes. B keeps files where §2c puts them but creates a second pin that Dependabot does not maintain. C has no code cost but makes every security bump of the engine a manual rollout. A hybrid of A's hash with B's location is not available, because Helm's `.Files` reads only files inside the chart.

## 5. Recommendation

🛑 **Surface forced decisions to user.** §3 has two items: how the new egress rules (API-server ipBlock on AKS/GKE, the kube-dns helper on EKS/AKS/GKE) are admitted given untested runtime matching, and how engine bumps reach the model images. §2 has five literal-wrongness fixes: EKS API-server ClusterIP, Dependabot overlay and template blind spots, Ollama `OLLAMA_MODELS`, the vacuous laptop live checks, and the terraform roster.
