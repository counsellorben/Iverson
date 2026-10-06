# Critical Design Review: 2026-10-06-csr-round10-infrastructure-supply-chain-design (Round 3)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-10-06-csr-round10-infrastructure-supply-chain-design.md`
**Artifact HEAD at review:** cbb20108a647228019cd0e58459635e40f08b741
**Verified Assumptions section:** present

Anchor basis: `git log -1 -- <spec>` → `cbb20108…`; `git status --porcelain -- <spec>` → empty.

Amendment detection against round 2's anchor (`**Artifact HEAD at review:** 8a4a68c6…`, SHA form). Content-identity check: `git rev-parse 8a4a68c6:<spec>` = `dfe31449…` and `git hash-object <spec>` = `3fc19ddb…`. They differ, so the amendment set is `git diff 8a4a68c6 -- <spec>`. Forward window: `git log 8a4a68c6..HEAD -- <spec>` lists one commit, `cbb20108 applied 2 fixes from …-critical-review-2.md to …`, which has the in-band shape. Reverse window: `git log HEAD..8a4a68c6` is empty. The diff has two hunks: §5b (rewritten as two passes) and Verified assumption 56. Both are round-2 fix sites. Family (c), out-of-band amendments, is empty, and that is proven at content level (row F3).

The §0 enumeration was built before rounds 1 and 2 were read; only the anchor line of round 2 was read first. Probes ran in `…/scratchpad/cdr-d3/`:
- `helm dependency build` and `helm template` on a scratch copy of the chart, for `values-laptop` as shipped (pass 1), for the pass-2 flags, and for the pass-2 flags plus the fix; `kubeconform` on the fixed render;
- a `python3` parse of the renders for PVC classes, Ingress classes, secret references and effective CPU requests;
- upstream sources fetched with `curl`: terraform-provider-azurerm v3.117.1 `kubernetes_cluster_resource.go`; Kubernetes v1.30.0 `setdefault/admission.go`, `pv_controller.go` and `core/helper/helpers.go`; kind v0.24.0 `const_storage.go` (the local `kind` is v0.24.0);
- Docker Hub tag lookups.

No cluster, container or cloud account was used.

## 0. Coverage enumeration

### Sections

| # | Section | Disposition |
|---|---|---|
| S1 | Header, Goal, Global constraints | ok, not load-bearing (process rules). |
| S2 | §1a kubelet / `clusterCidrs` table | see R1 |
| S3 | §1b rule changes (cells: api-ingress, prometheus-ingress, admin-ui-ingress, authentik-ingress) | [bidirectional]. Direction 1, each listed rule carries an allow-all or `clusterCidrs` peer today: ok. `networkpolicies.yaml` re-read in full: `clusterCidrs` ranges at `:50, :59, :582`, `from: []` at `:514, :605, :615`. Direction 2, no unlisted rule carries one: ok, because those six lines are the only matches in the file. Per-rule behaviour is in R4, R5 and S4. |
| S4 | §1b Authentik 9000 deletion | over: n/a, because a deletion cannot admit anything. / under (an in-cluster caller of 9000 loses access): ok [negative]. `command grep -rn 'authentik[^ ]*:9000'` over yaml/yml/sh/ps1/cs/py/ts/json (excluding `.worktrees`, `.claude`, `bin`, `obj`) finds: the API chart's `Authentication__Authority`, `ActingUser__Authority` and `InternalIssuer` strings (`charts/api/templates/deployment.yaml:152, 174, 200`), with discovery on 8443 (`:194-196`, `MetadataAddress`); compose (`docker-compose.yml:516-649`, which has no NetworkPolicy); host-side tools and tests (`TokenBroker.cs`, `mint_acting_user_token.py`, `AuthentikTrustTests.cs`, `AuthentikFlowExecutorClientTests.cs`). The tls-proxy reaches 9000 on loopback (`configmap-tls-proxy.yaml:16, 30-36`). The kubelet probes on 9000 (`deployment-server.yaml:128, 134`) are covered by VA1–4 (recorded, not re-run), and port-forward by VA5 (recorded, not re-run). |
| S5 | §1c Azure check | ok, not load-bearing: the failure is visible and the fallback is stated. |
| S6 | §2a DNS helper | see R2 |
| S7 | §2b `apiServerCidrs` table and guard | see R3 |
| S8 | §2b first-deploy gate and fallbacks | ok [existence]: decided in round 1 (§3.1, option A) and unchanged since round 2 (no hunk). |
| S9 | §2c model-image Dockerfiles | ok [existence] (recorded, not re-run): VA19–23. Building them needs the model weights, which is out of budget in-round. |
| S10 | §2c chart changes (TEI, Ollama StatefulSets) | over (a removal takes away something the container still needs): ok [existence]. Both templates were read in full. TEI has no `env:`, its args name no path (`charts/tei/templates/statefulset.yaml:54`), and `/data` appears only in its PVC mount. In Ollama, `/data` appears in the init container (`:51-52`, `:89-90`), which is removed, and in the main container's env (`:103-104`) and mount (`:119-121`), which are removed or repointed. / under (a `/data` reference survives): ok [existence]; §5a asserts that none remains. |
| S11 | §2c NetworkPolicy and upgrade note | ok [existence]: `tei-egress` (`:452-462`) and `ollama-egress` (`:481-493`) are the only policies that §2c empties, and `volumeClaimTemplates` sit at `tei :87`, `ollama :127`. |
| S12 | §2c build and load | ok, not load-bearing: the plan picks the mechanism. `build-and-load-image.sh:49` builds with the repo root as context. |
| S13 | §2c sizes | ok, not load-bearing. |
| S14 | §3a AKS cluster access | ok [existence], read: in the re-fetched v3.117.1 `kubernetes_cluster_resource.go`, `azure_active_directory_role_based_access_control` updates in place (`:2209-2230`, `updateCluster = true` when `Managed`), as does `local_account_disabled` (`:2278-2281`). The only `kube_config` consumers are `azure/main.tf:47-58` and `modules/cluster-azure/outputs.tf`. Apply-time behaviour is static-only, by the spec's constraint. |
| S15 | §3b VNet Integration and user-assigned identity | ok [existence], read: `ForceNewIfChange("api_server_access_profile.0.subnet_id", …)` (`:87`) forces replacement only on removal. `api_server_access_profile` updates through `:2242-2247` and `identity` through `:2455-2464`. `command grep -rn 'principal_id\|identity\[0\]' --include=*.tf` finds `cluster-azure/main.tf:122` (the DES Reader assignment, which the spec moves) as the only consumer of the cluster identity. |
| S16 | §3c state account | ok [existence]: `bootstrap/azure/main.tf` has no `backend` (VA34), and the container is created through `storage_account_name` (`:49`), which is the data-plane path that §3c's `storage_use_azuread` addresses. Static only. |
| S17 | §3d EKS IMDS | ok [existence]: `metadata_options` at `cluster-aws/main.tf:553`, with the hop limit at `:556`. `outputs.tf` has no `vpc_id` (VA38). The LB release is `count = var.cloud == "aws"` (`operators/main.tf:111`), and the gcp and azure `module "operators"` call sites pass no `vpc_id`. `command grep -rl 'AWSSDK\|Amazon\.' --include=*.csproj Iverson.Server` → none (VA37). |
| S18 | §4a build context | see R7 |
| S19 | §4b console and nginx images | ok [existence]: Docker Hub API (run this round) shows `nginx-unprivileged:1.30.5-alpine` and `1.30-alpine` sharing `sha256:15c994d10d6d…`, and `node:22-alpine` present. The three nginx sites are `Iverson.AdminUI/Dockerfile:24`, `Iverson.Server/docker-compose.yml:442` and `charts/authentik/templates/deployment-server.yaml:150` (VA45). |
| S20 | §4c small items | ok [existence]: the `.pyc` is tracked (`git ls-files` hit) and `.gitignore:82-83` reads `__pycache__/`, `*.pyc`. `admin-ui.yml` uses `@v7` at `:31, :34, :136`, while the other workflows use `checkout@3d3c42e5… # v7.0.1` and `setup-node@82076278… # v7.0.0`. `setup.sh` pins Calico at `:32` and `:49`, and the other five charts at `:112, :126, :146, :158, :173`; `setup.ps1` has no `--version`. Console packages unused: ok [negative]. `command grep -rln 'grpc-web\|google-protobuf\|from .long.\|require(.long.)' src scripts vite.config.ts vitest.config.ts` → no output (no file imports any of the three). |
| S21 | §4d chart images as `image:` scalars | see R6 |
| S22 | §4d Dependabot rosters | over: n/a, because an extra entry is a no-op job. / under: ok [totality]. Dockerfiles: `find -name 'Dockerfile*'` → AdminUI plus Api, Events, Launcher, Sql and Vector (6), each listed, plus the two new `model-image` directories. Lockfiles: 6 roots, each listed. Compose: the only tracked file is `Iverson.Server/docker-compose.yml`; `.superpowers/…/compose.rrf-bench.yml` is ignored (`.gitignore:45`). |
| S23 | §4d compose digest pins | ok, not load-bearing: all 17 third-party compose images are tag-only today (`grep '^\s*image:'`), so the bullet describes a change. |
| S24 | §4e image scan | ok [existence] (recorded, not re-run): the round-1 `gh api` resolution of v0.36.0 to `ed142fd0…`. |
| S25 | §5a rendered assertions | ok, not load-bearing (a test list). |
| S26 | §5b preamble: two passes and the CPU justification **(a: round-2 fix site)** | ok [existence], arithmetic from this round's renders. Effective CPU requests, counting each pod as the larger of its biggest init container and the sum of its app containers: Deployments and StatefulSets 1.46 for `values-laptop` as shipped (Ollama 0.50, because of its init container), plus CNPG 0.25 and Kafka 0.25, gives 1.96, the deploy known to fit. Pass 2 is 1.41 + 0.50 = 1.91, which is at most 1.96. Pass 1 after §2c removes the init container is 1.71. Laptop plus both additions after §2c is 2.16, which is more than 1.96. Both halves of the claim hold. |
| S27 | §5b pass 1 **(a)** | ok [presence] on the render (the render was run; no live consumer was run), ok [existence] on the pairings. Each refusal has a positive control: API 8080 with ingress-nginx (`values-laptop` api Ingress class `nginx`); API 8081 with the kubelet readiness probe on 8081 (`charts/api/templates/deployment.yaml:231-238`); Authentik 9000 with port-forward (VA5, recorded); Authentik 9080 with ingress-nginx. Storage, from the pass-1 render: VCTs `iverson-ollama`, `iverson-qdrant` and `iverson-tei-bge-base` are all `standard`; there is no standalone PVC. ingress-nginx runs on hostPort, not hostNetwork (`setup.sh:114`), so its traffic has a pod IP and matches the namespace peer. |
| S28 | §5b pass 2 **(a)** | → §2.1 (the Prometheus PVC never binds, so Prometheus never runs). The other pass-2 cells are in R8. |
| S29 | §5c–§5e | ok, not load-bearing. |
| S30 | Verified assumptions | see §1 |
| S31 | Known issues | ok, not load-bearing: no hunk since round 2, and the text agrees with §2b's gate and with S8. |

### Rules and operands

| # | Rule | Disposition |
|---|---|---|
| R1 | `clusterCidrs` = the ranges the profile's load balancer connects from | over (admits a non-LB source): ok [existence]. AWS `10.0.128.0/20, 10.0.144.0/20` = `cidrsubnet(vpc_cidr, 4, 8..9)` public subnets (`cluster-aws/main.tf:101-112`), while node groups use `aws_subnet.private[*]`. Azure `10.1.16.0/24` is the AGIC `subnet_cidr` (`cluster-azure/main.tf:194-196`), and nodes are on `10.1.0.0/20` (`:136`). GCP keeps only the GFE ranges. / under (an LB source is not listed): ok [existence], one cell per cloud Ingress. AWS: the api, admin-api, authentik and admin-ui Ingresses are all `scheme: internet-facing` with `target-type: ip` (`values-aws.yaml:89-90, 109-110, 179-180, 195-196`), so the ALB sits in the `kubernetes.io/role/elb` subnets (`main.tf:111`). Azure: all four use class `azure-application-gateway` (`values-azure.yaml:88, 99, 158, 167`). GCP: all four use `gce` (`values-gcp.yaml:96, 107, 166, 175`). Laptop: unchanged. |
| R2 | DNS helper: kube-system plus `k8s-app=kube-dns`, 53 | over: ok [existence]; the peer ANDs namespace and pod selectors on port 53 only. / under: ok [totality]: `grep -c 'port: 53'` = 14 sites (api, worker, postgres, kafka, starrocks, create-user, revoke-cross-db, qdrant, tei, ollama, jaeger, prometheus, authentik, redis); 12 get the helper and tei/ollama get none (S10, S11). Prometheus discovers targets with `dns_sd_configs` (`charts/prometheus/templates/configmap.yaml:20-26`), so the helper covers it. The managed-dataplane run tier was decided in round 1 (§3.1, option A). |
| R3 | `apiServerCidrs` replaces `to: []` on 443/6443 | over: ok [existence]: the only API-server rules are `:194-212` and `:265-274` (VA11, re-read). / under (a pod that needs the API server is missed): ok [negative]. `command grep -nE 'port: (443\|6443)' templates/networkpolicies.yaml` → `:212` and `:274` (443 plus 6443: postgres-egress and kafka-egress), and `:462` and `:493` (443 only: tei-egress and ollama-egress, both emptied by §2c). No other egress policy admits 443/6443 today, so §2b narrows no rule that some other workload relies on. The two policies select CNPG and Strimzi pods; the Strimzi selector includes the entity operator. The managed-dataplane run tier was decided in round 1 (§3.1, option A). |
| R4 | `prometheus-ingress` admits API pods only | over: ok [existence]: the only remaining peer is `podSelector app=<release>-api` (`:578-579`). / under (another caller of 9090): ok [negative]. `command grep -rn 'prometheus:9090'` over the chart → only the API's `Prometheus__BaseUrl` (`charts/api/templates/deployment.yaml:220`). No Ingress targets Prometheus (`kind: Ingress` appears only in admin-ui, api ×2 and authentik). The kubelet `/-/ready` probe is covered by VA1–4 (recorded). |
| R5 | admin-ui 8080 and Authentik 9080 = ingress-nginx namespace plus `clusterCidrs` | over: ok [existence], same peers as api-ingress 8080. / under (a legitimate source is not admitted): ok [existence]. On cloud, the LB sources are R1's cells. On laptop, ingress-nginx uses hostPort with a pod IP (`setup.sh:114`), and the kind config has one node (`kind-config.yaml`). |
| R6 | "Dependabot helm reads every third-party chart pin" × every image site | over: n/a, because an extra pin is a no-op job. / under: ok [totality]. `command grep -rn 'image' charts/*/templates` gives these template sites: authentik server `:54, :84` and worker `:27`; tls-proxy `:150`; revoke `:32`; jaeger `:37`; prometheus `:41`; qdrant `:45`; redis `:37`; create-user `:31`; starrocks fe/be `:7, :25` (already scalars); tei `:47` and ollama `:45, :95` (becoming derived first-party names); api, worker and admin-ui are first-party. Overlay pins: `values-{aws,azure,gcp}.yaml` lines `140/149/156`, `131/140/147` and `139/148/155`, all deleted (VA48). Exact tags, Docker Hub (run this round): `postgres:16.15`, `mysql:8.0.46` and `redis:7.4.11-alpine` each return 200. |
| R7 | Build-context exclusion and the Api's explicit copies | over (excludes something a build needs): ok [totality]. The console and Api contexts are the repo root (`admin-ui.yml:144`, `build-and-load-image.sh:49`, `docker-compose.yml:482-483, 606-607`), so the root `.dockerignore` governs both. Events, Sql and Vector copy only their own directory. / under (the Api publish misses a directory): ok [totality], re-run. `command grep -n 'Include="[^"]*\.\.'` over the 8 csprojs → 7 `ProjectReference`s inside the listed set, plus `Protobuf Include="../../Common/Proto/*.proto"`. `find -maxdepth 4` for `Directory.*.props/targets`, `global.json` and `nuget.config` → none (VA41). |
| R8 | Pass 2 × "every component the flags turn on, or leave on, is configured for kind" (population: every value the flags enable that an overlay must supply, plus every storage consumer) **(b)** | over (a value names something kind does not have): ok [existence]. Every class rendered is `standard`, kind's only StorageClass (kind v0.24.0 `const_storage.go:136-143`), and the Ingress classes are `nginx`. / under, per cell, from the pass-2 render: **Prometheus `storageClassName` → §2.1** (`''`). Prometheus `storageSize` 20Gi: ok [existence], because local-path does not reserve capacity. Prometheus resources: ok, counted in S26. Prometheus image: ok, a public pull. admin-ui `ingress.className`: ok [presence], the render has `nginx` (round-2 fix; no live ingress-nginx was run). admin-ui replicas and resources: ok, counted in S26. admin-ui `OIDC_CLIENT_ID` secret: ok [presence]; the render contains `Secret iverson-authentik-human-oidc-client`. admin-ui image: ok, §5b says the scripts load it. Storage consumers: Prometheus PVC → §2.1. Qdrant VCT, TEI VCT, CNPG `storageClass` and KafkaNodePool `class`: ok [presence], each `standard` in the render (no binding was run; the laptop deploy binds with `standard` today, recorded, not re-run). |
| R9 | Pass 2 "API and worker readiness does not depend on Ollama" **(b: round-2 §3.1 option C residue)** | over: n/a, because the claim is that one dependency is absent, so the only failure is a missed dependency, which is under. / under (readiness does depend on Ollama): ok [negative], read. `Program.cs:527-532` injects exactly `IRecordStoreQueryExecutor`, `IEngagementStoreHealthCheck`, `IVectorSchemaManager`, `IEventBrokerHealthCheck`, `IOptions<EngagementStoreOptions>` and `IMemoryCache`. `:548` is `await Task.WhenAll(pgTask, srTask, vectorTask, kafkaTask);`, and readiness is `ReadinessPolicy.Evaluate` over those four results. None of them is an Ollama or enrichment client. `/health/live` is `Results.Ok(new { status = "alive" })` (`:519`). The worker probes the same two paths (`charts/worker/templates/deployment.yaml:139-148`). |
| R10 | Derived model-image tag as image identity | over-merge (two engines or two model pins under one tag): ok [existence] (recorded, not re-run). Round 2's R1/A1 rendered the tag changing when the Dockerfile's FROM line changed, and the model components are values fields (`.slug` plus `.revision[0:12]`; model plus `digest[0:12]`). / under-merge (identical inputs giving two tags): ok [existence] (recorded, not re-run). The tag is a pure function of values fields and file bytes (round 2's R1). There is no hunk in §2c since round 2. |

### Data-flow arrows

| # | Arrow | Disposition |
|---|---|---|
| A1 | `model-image/Dockerfile` → chart `$.Files.Get \| sha256sum` and → build-script `sha256sum` (two call sites) | ok [existence] (recorded, not re-run): VA57 and round 2's A1/A2. No hunk since then. |
| A2 | `cluster-azure` host and CA → `azure/main.tf` `provider "kubernetes"` (`:46-51`) and `provider "helm"` (`:53-60`) (two call sites) | ok [existence]: the spec replaces both with the exec block. |
| A3 | `cluster-aws.vpc_id` → `operators.vpc_id` → LB controller `vpcId` | ok [existence]; see S17. |
| A4 | **Persistence boundary:** baked image `ENV` → pod-spec env (the runtime override) | ok [existence]: TEI sets no env; Ollama's `/data` env is removed (S10, VA55). |
| A5 | `values-laptop` + pass-2 flags → Prometheus PVC → admission and PV controller → Prometheus pod scheduling → the 9090 positive control | → §2.1 |
| A6 | Pass-2 render → admin-ui Ingress → ingress-nginx `/admin` | ok [presence] on the render (class `nginx`; no live consumer was run). The live 200 is round 2's A8 residue, carried as decided (F2). |
| A7 | `bootstrap/azure` outputs → `azure` backend with `use_azuread_auth` | ok [existence]: outputs at `bootstrap/azure/main.tf:53-55`. |

### Iterative-review families

| # | Family | Disposition |
|---|---|---|
| F1 | **(a) Fix neighbourhoods:** round-2 sites (the §5b heading and both passes; VA56) and their restatements | S26–S28, R8, R9. Restatement check: the pass-2 flag list appears twice, in the §5b heading (spec line 212) and in VA56 (line 289), and the two strings are identical (`grep` hits both). The round-1 sites (§2a, §2b, §2c, §4d, §4e, §5a, VA14, VA48–58 and Known issues) are unchanged since round 2 and were re-swept as S6–S11, S21–S22, S24–S25 and S31. |
| F2 | **(b) Intersected fix texts** | Round 2's R6 cell "API pod → `prometheus:9090` … live reachability is exercised by the §5b run itself" intersects this round's PVC discovery → §2.1. Round 2's §3.1 option C Evidence "UNVERIFIED: that API/worker pods reach Ready with Ollama absent" is resolved by R9: ok [negative]. Round 2's A8 (`/admin` 200 live) and A9 (live rejection of an empty `ingressClassName`) residues were accepted under round 2's §3.1, which the spec settled by adopting option C. They are carried as decided, not re-raised, and the A9 state no longer renders (R8). They are listed in §3's residue note. |
| F3 | **(c) Amendment hunks since `8a4a68c6`** | ok [totality]: content identity is unequal, `git diff 8a4a68c6 -- <spec>` has two hunks (§5b, VA56), both from in-band commit `cbb20108`, and the reverse window is empty. No out-of-band hunk exists. |
| F4 | UNVERIFIED: live Pending of the Prometheus PVC under the pass-2 flags (run tier; no kind cluster may be created in-round) | Not load-bearing for the fix: `standard` is the class every other laptop claim binds with today, so the fix is correct whether or not the empty-class claim would bind. → §3 residue note. |

Row tally: 31 section rows + 10 rule rows + 7 arrow rows + 4 family rows = **52 rows: 47 ok, 5 → findings, 0 dropped**.
- Four of the five rows → findings go to §2.1: S28, R8, A5 and F2.
- F4 is an UNVERIFIED residue → §3 residue note.
- The span gap U1 is a §1 item, not a §0 row, and also → §2.1.
- S2, S6, S7, S18, S21 and S30 are pointer rows, counted as ok; their dispositions are the rows they name.

## 1. Verified-assumptions cross-check

**Re-read or re-run this round (all hold):**
- 6: `charts/api/templates/deployment.yaml:186-196`, `Authentik__BaseUrl` and `MetadataAddress` on 8443.
- 7: `cluster-aws/main.tf:87-112`, `:263`.
- 8: `from: []` at `:514, :605, :615`.
- 9: `cluster-azure/main.tf:132-137`, `194-196`.
- 11: 14 `port: 53`; 443/6443 at `:212, :274, :462, :493`.
- 18: `cluster-gcp/main.tf:183`.
- 24: `values.yaml:60`.
- 25: `api/pull|PullModel` → none.
- 29: `kcr.go:87`, `:2455-2464`.
- 32: `kube_config` consumers.
- 34: no `backend` in `bootstrap/azure`.
- 37: no AWS SDK.
- 38: `outputs.tf`.
- 41: re-run (R7).
- 44, 47: Docker Hub (S19, R6).
- 45, 46, 48: by `grep`.
- 51: no imports.
- 53: workflow SHAs.
- 55: `statefulset.yaml:103`.
- 56: re-rendered; three Ingresses `nginx`, Prometheus and admin-ui rendered, no Ollama workload, and `/health` as stated.

**Accepted as ground truth, unchanged since round 2 and not re-fetched:** 1–5, 10, 12–17, 19–23, 26–28, 30, 31, 33, 35, 36, 39, 40, 42, 43, 49, 50, 52, 54, 57, 58.

**Failed:** none.

**Span check (uncovered dependencies):**
- U1. That every claim the pass-2 profile renders binds on the throwaway kind cluster. VA56 covers what renders, not whether it schedules. Verified in-round: the Prometheus PVC does not bind → §2.1.

## 2. Literal-wrongness findings

### 2.1 §5b pass 2 cannot run its Prometheus checks: under `values-laptop` the Prometheus PVC renders `storageClassName: ""` and never binds on kind

**Description.** Pass 2 turns on Prometheus with `--set global.prometheusEnabled=true` on top of `values-laptop`. That overlay disables Prometheus, so it never sets `prometheus.storageClassName`, and the claim takes the umbrella default `""` (`values.yaml`, `prometheus.storageClassName: ""`). The template renders it verbatim (`charts/prometheus/templates/pvc.yaml:7`, `storageClassName: {{ .Values.storageClassName | quote }}`). Kubernetes treats a non-nil empty class as an explicit request for a classless PV:
- The default-class admission plugin skips the claim, because `PersistentVolumeClaimHasClass` is true for any non-nil `StorageClassName`.
- The PV controller's retroactive defaulting skips it for the same reason.
- No provisioning is attempted: the unbound-claim switch provisions only when the class is not `""`, and otherwise records "no persistent volumes available for this claim and no storage class is set" and leaves the claim Pending.

kind's only StorageClass is `standard` (local-path), and `setup.sh`/`setup.ps1` create no PV. So the claim stays Pending, and the Prometheus pod, which mounts it (`volumes: storage → persistentVolumeClaim iverson-prometheus`), never schedules.

Pass 2 then fails as written:
- "Every pod reaches Ready" fails.
- The positive control "an API pod reaches `prometheus:9090`" cannot pass. That leaves the 9090 refusal indistinguishable from an absent listener, which is the vacuous outcome the pairing exists to rule out.

Every other claim in both passes uses `standard` (R8, S27), and `values-local`, the other kind profile that runs Prometheus, sets `prometheus.storageClassName: "standard"` (`values-local.yaml:153-155`).

**Evidence.**
- [presence] Run: `helm template iverson <scratch chart> -f values-laptop.yaml --set global.prometheusEnabled=true --set adminUi.enabled=true --set adminUi.ingress.className=nginx --set ollama.enabled=false` → rc=0. Python parse: `PVC iverson-prometheus ''` (20Gi). The other claims are `VCT iverson-qdrant 'standard'`, `VCT iverson-tei-bge-base 'standard'`, CNPG `storageClass: standard`, and KafkaNodePool `class: standard`. The Prometheus Deployment's volumes include `persistentVolumeClaim: {claimName: iverson-prometheus}`.
- [existence] Kubernetes v1.30.0, read:
  - `plugin/pkg/admission/storage/storageclass/setdefault/admission.go`, `Admit` returns early on `helper.PersistentVolumeClaimHasClass(pvc)`.
  - `pkg/apis/core/helper/helpers.go:465-476` returns true when `claim.Spec.StorageClassName != nil`.
  - `pkg/controller/volume/persistentvolume/pv_controller.go:951` (`assignDefaultStorageClass`) has the same guard, and `:364-380` provisions only in `case GetPersistentVolumeClaimClass(claim) != ""`; the `default:` branch emits the "no storage class is set" event and marks the claim Pending.
- [existence] kind v0.24.0 (the local `kind version`) `pkg/build/nodeimage/const_storage.go:136-143`: one StorageClass, `standard`, provisioner `rancher.io/local-path`, and no PersistentVolume objects. `command grep -n -i 'storageclass\|persistentvolume\|local-path\|standard' setup.sh setup.ps1 kind-config.yaml` → no match.
- UNVERIFIED (run tier, row F4 → §3 residue note): the live Pending state, because no kind cluster may be created in-round. The finding rests on the render plus the controller code path above.

**Proposed fix.**
- Add `--set prometheus.storageClassName=standard` to pass 2's flag list in both places it appears: the §5b pass-2 heading and VA56.
- Extend VA56 to record that, with the pass-2 flags, the Prometheus PVC renders `storageClassName: "standard"`. It is the only standalone PVC in either pass, and every claim in both passes is then `standard`.

Evidence:
- [compat] Run: the same render plus `--set prometheus.storageClassName=standard` → rc=0. `PVC iverson-prometheus 'standard'`. A `diff` against the unfixed render shows the only non-random line change is `storageClassName: ""` → `"standard"`; the other differing lines are the chart's per-render generated secrets. `kubeconform -kubernetes-version 1.30.0 -summary -ignore-missing-schemas` → 87 resources, 82 valid, 0 invalid, 0 errors, 5 skipped.
- [existence] The effective CPU requests are identical with and without the flag (`cpu.py` output equal), so S26's capacity argument is unchanged.
- [existence] `standard` is the class every `values-laptop` claim uses (`values-laptop.yaml:46, 54, 62, 70, 83`) and the class `values-local` gives Prometheus on kind (`:155`); both profiles deploy on this host today (recorded, not re-run).
- UNVERIFIED (row F4 → §3 residue note): live binding. This is not load-bearing for the fix, because `standard` is the class every other laptop claim already binds with.

## 3. Forced decisions

No forced decisions found.

Residues routed here, none of which needs a decision:
- **F4, the live Pending state of the empty-class Prometheus claim** (run tier). It is not load-bearing for §2.1's fix, which is correct under either outcome. It is observed, or made moot by the fix, at the §5b run itself. Accepted.
- **Round 2's A8 and A9** (`/admin` returning 200 live; the live rejection of an empty `ingressClassName`). Both were accepted under round 2's §3.1, which the spec settled by adopting option C. A9's state no longer renders. Carried as decided.

## 4. Previously addressed

- Round-2 §2.1 (the console Ingress renders `ingressClassName: ""`): pass 2 now sets `--set adminUi.ingress.className=nginx`, and VA56 records all three Ingresses as `nginx`. Re-rendered this round (R8).
- Round-2 §3.1 (does the §5b profile fit the 4-CPU host?): option C was adopted. §5b runs two passes, and pass 2 drops Ollama. This round's arithmetic: pass 2 = 1.91 effective CPU, against 1.96 for today's working deploy (S26). Option C's residue about readiness without Ollama is closed by VA56's `/health` citation, which was re-read (R9).

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes.**
- §2.1: add `--set prometheus.storageClassName=standard` to §5b pass 2 (heading and VA56). Without it, the Prometheus claim renders `storageClassName: ""`, never binds on kind, and pass 2's Prometheus checks and its "every pod reaches Ready" check cannot pass.
