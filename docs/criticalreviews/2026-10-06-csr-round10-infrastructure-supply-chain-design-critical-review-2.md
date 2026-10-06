# Critical Design Review: 2026-10-06-csr-round10-infrastructure-supply-chain-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-10-06-csr-round10-infrastructure-supply-chain-design.md`
**Artifact HEAD at review:** 8a4a68c64f2e6d1eb3f7beb2cd83958fe08f3626
**Verified Assumptions section:** present

Anchor basis: `git log -1 --format=%H -- <spec>` → `8a4a68c6…`; `git status --porcelain -- <spec>` → empty (0 lines).

Amendment detection against round 1's anchor (`**Artifact HEAD at review:** b6b09c8c…`, SHA form). Content-identity check: `git rev-parse b6b09c8c:<spec>` = `47873feb…` and `git hash-object <spec>` = `dfe31449…`, which differ, so the amendment set is `git diff b6b09c8c -- <spec>`. Forward window: `git log b6b09c8c..HEAD -- <spec>` lists one commit, `8a4a68c6 applied 7 fixes from …-critical-review-1.md to …`, which matches the update skill's in-band shape. Reverse window: `git log HEAD..b6b09c8c` is empty. Every hunk of the diff falls in §2a, §2b, §2c, §4d, §4e, §5a, §5b, the Verified-assumptions rows 14 and 48–58, and Known issues, all of which are round-1 fix sites. Family (c), out-of-band amendments, is therefore empty, and that is proven at content level (row A7).

The §0 enumeration was built before round 1's review was read. Probes ran in `/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/cdr-d2/`:
- `helm dependency build` and `helm template` in scratch copies of the chart, plus `kubeconform`;
- a scratch `charts/tei/model-image/Dockerfile` with a `.Files.Get | sha256sum` probe template;
- upstream sources fetched with `curl`: Kubernetes v1.30 Ingress validation, apimachinery `IsDNS1123Subdomain`, dependabot-core helm `file_parser.rb` and docker `file_fetcher.rb`, amazon-network-policy-controller-k8s `endpoints.go`, and terraform-provider-azurerm v3.117.1 `kubernetes_cluster_resource.go`;
- Docker Hub and ghcr.io tag and digest lookups;
- `python3` over the rendered manifests to sum effective CPU requests.

No cluster, container or cloud account was used.

## 0. Coverage enumeration

### Sections

| # | Section | Disposition |
|---|---|---|
| S1 | Header, Goal, Global constraints | ok, not load-bearing (process rules). The provider versions named in the constraints match the six committed lockfiles (awk over `.terraform.lock.hcl`: azurerm 3.117.1, aws 5.100.0, google 5.45.2, kubernetes 2.38.0, helm 2.17.0, plus tls 4.4.1 in `aws`). |
| S2 | §1a kubelet / `clusterCidrs` table | ok [existence]: the round-1 diff has no hunk in §1, so round 1 R1's [totality] dispositions still apply. The per-overlay cells are in R4. |
| S3 | §1b rule changes (cells: api-ingress, prometheus-ingress, admin-ui-ingress, authentik-ingress) | [bidirectional]. Direction 1, every listed rule carries an allow-all or `clusterCidrs` peer today: ok. api-ingress `:50,:59`; prometheus-ingress `:582`; admin-ui-ingress `:514`; authentik-ingress `:605,:615`. Direction 2, no unlisted rule carries one: ok. `from: []` occurs only at `:514, :605, :615`, and `clusterCidrs` ranges only at `:50, :59, :582` (re-read in-round). There is no hunk in §1 since round 1. Live checkability of each cell under §5b is R6. |
| S4 | §1c Azure check | ok, not load-bearing: the failure is visible and the fallback is stated. |
| S5 | §2a DNS helper and the `dnsAnyDestination` fallback **(a: fix neighborhood)** | over (an any-destination DNS rule survives at defaults): ok [existence]. The fallback defaults to `false`, and §5a asserts that at defaults no `to: []` renders and that the toggle renders `to: []` for exactly its own rules. / under (a site that needs DNS loses it): ok [totality]. The site matrix is R8. EKS matching of the helper is [existence] at read tier. Its run tier is the spec's accepted Known-issues residue, decided through round-1 §3.1 option A (the first-deploy gate), and is not an open item here. The read-tier support is now stronger: `endpoints.go:228-259` resolves the peer's namespaces (kube-system), then calls `getMatchingServiceClusterIPs(ctx, peer.PodSelector, ns, …)` for each one, so the kube-dns ClusterIP is added for this peer shape. A sub-candidate was dropped: "rendered at each site" (14) conflicts with §2c's TEI/Ollama having no DNS rule (12 sites), but §2c says so explicitly, so nothing is ambiguous. |
| S6 | §2b AWS `apiServerCidrs` (ClusterIP `/32`s) **(a)** | over (the `/32`s admit something other than the API server): ok [existence]. `10.100.0.1` and `172.20.0.1` are the first addresses of the two ranges EKS may pick (VA54), and `command grep -n -E 'service_ipv4_cidr\|kubernetes_network_config' modules/cluster-aws/main.tf` gives rc=1, so EKS picks. / under (API-server egress still missed): ok [existence] at read tier. The run tier is the spec's accepted residue under the §2b first-deploy gate (round-1 §3.1 option A). VA14 was re-read: `endpoints.go:219-225` appends an ipBlock as-is and `continue`s, before `:259` (Service ClusterIP resolution), so listing the ClusterIP itself is the matching shape. |
| S7 | §2b first-deploy gate **(a)** | dropped. Candidate: "a pod in the namespace resolves `kubernetes.default`" fails for a freshly created pod, because default-deny (`networkpolicies.yaml:11-19`) selects it and no egress policy matches its labels. A false DNS alarm would push an operator to `dnsAnyDestination`. It fails the literal-wrongness test, because a satisfiable reading exists (exec into an admitted workload pod), and choosing the probe is runbook wording that the plan writes. |
| S8 | §2c Dockerfile location (`charts/{tei,ollama}/model-image/`) **(a)** | ok [compat]. Run: a scratch `charts/tei/model-image/Dockerfile` is packaged and read by `$.Files.Get` (see A1). Dependabot reaches the new location (R3/VA58). |
| S9 | §2c derived image names **(a)** | see R1 and A1–A3 |
| S10 | §2c TEI/Ollama StatefulSet changes **(a)** | over (a removal takes away something the container still needs): ok [existence]. The removed items are the Ollama `pull-model` init container (`:43-92`), the `/data` env entries (`:103-104`), and the PVC mounts (`ollama :119-121`, `tei :79-81`). Each existed only to fill or read the PVC, and the baked image supplies `/models` instead (VA20 and VA22, recorded, not re-run). `HOME` is kept but repointed to the existing `tmp` emptyDir (`:124-126`). / under (a `/data` reference survives): ok [existence]. `command grep -n '/data'` over both StatefulSet templates finds only `ollama :51-52` (init, removed), `:89-90` (init mount, removed), `:103-104` (main env, removed or repointed), `:119-121` (main mount, removed), and `tei :79-81` (mount, removed). TEI has no `env:` and its args name no path (`charts/tei/templates/statefulset.yaml:54`). §5a asserts that none remain. |
| S11 | §2c NetworkPolicy + upgrade note | over (the note's delete-before-upgrade step removes something still needed): ok [existence]. The `volumeClaimTemplates` (`charts/tei/templates/statefulset.yaml:87`, `charts/ollama/templates/statefulset.yaml:127`) are consumed only by the mounts S10 removes, and the old PVCs hold only re-downloadable weights. / under (an upgrade path the note misses): ok [existence]. Removing `volumeClaimTemplates` is an immutable-field change, so an in-place `helm upgrade` of an existing release fails visibly, which is what the note pre-empts. The two `-egress` policies keeping no rules is R8's tei/ollama cells. |
| S12 | §2c Build and load (hash-tagging restatement) **(a)** | see A2, A3 |
| S13 | §2c Sizes / AWS node disk | dropped. This is round 1's S12 (the AWS pools' 20 GB root volume, `cluster-aws/main.tf:539`). It was dropped as speculative there and is not re-raised. The evidence changed only in kind: the Dockerfile-hash tag means an engine bump now pulls a complete new Ollama image beside the old one during a roll. Steady state still fits by round 1's estimate: amd64 base 2.06 GB compressed (Docker Hub API) / 3.75 GB unpacked, plus a ≈1.9 GB model. The roll-time peak is transient and recoverable by kubelet image GC once the old pod stops, so it remains speculative. The stale `storageSize` hints in `setup.sh:180` and `setup.ps1:102` are printed text with no behaviour, also dropped. |
| S14 | §3a AKS cluster access | ok [existence]: `kubernetes_cluster_resource.go` v3.117.1 updates `azure_active_directory_role_based_access_control` (`:2209`) and `local_account_disabled` (`:2278`) in place. Apply-time behaviour is static-only by the spec's stated constraint. |
| S15 | §3b VNet Integration / user-assigned identity | ok [existence]: `vnet_integration_enabled` (`:1507-1511`) is `Optional` with no ForceNew and is updated through `HasChange("api_server_access_profile")` (`:2242`). `subnet_id` forces replacement only on removal (`:87-89`, VA29 re-read). |
| S16 | §3c state account | dropped. Candidate: Azure Storage IP rules reject `/31` and `/32` prefixes, so an operator who reuses `api_authorized_ip_ranges`-style `/32` CIDRs for `state_authorized_ip_ranges` gets an apply error. It fails the literal-wrongness test: the error is loud, and the variable's validation and description are plan-level. |
| S17 | §3d EKS IMDS | ok [existence]: no hunk since round 1 (A7), so round 1's R13 [totality] enumeration of the IMDS-dependent workloads stands unchanged. |
| S18 | §4a build context | ok [existence]: no hunk since round 1 (A7), so round 1's R11/R12 [totality] dispositions stand. The `Iverson.Server/{Events,Sql,Vector}` Dockerfiles copy only their own directory (`COPY Iverson.<X>/ Iverson.<X>/` at `:9`). |
| S19 | §4b console / nginx | ok [existence]. Docker Hub API: `nginx-unprivileged:1.30-alpine` and `1.30.5-alpine` share `sha256:15c994d1…`; `node:22-alpine` and `22.23.3-alpine` share `sha256:0a7108bf…`. |
| S20 | §4c small items | ok [existence]: no hunk since round 1 (A7). `.gitignore` still has `__pycache__/` and `*.pyc`, and the `.pyc` is still tracked (`git ls-files` hit). |
| S21 | §4d chart images as `image:` scalars, overlay pins deleted, template literals moved **(a)** | ok [totality]; see R3 (P5 matrix). |
| S22 | §4d Dependabot rosters **(a)** | over (an entry names a directory with nothing to track): n/a, because an extra entry is a no-op update job. / under (a Dockerfile or root without an entry): ok [totality], shown this round. **terraform**: R5 (P2). **docker**: the covered set is `git ls-files \| grep -i -E '(^\|/)(dockerfile\|containerfile)[^/]*$'` → `Iverson.AdminUI/Dockerfile`, `Iverson.Server/Iverson.{Api,Events,Launcher,Sql,Vector}/Dockerfile` (6), each named in §4d's docker list, plus the two new `charts/{tei,ollama}/model-image` directories, also named. The residual set (tracked Dockerfiles with no entry) is empty. The fetcher selects regular files matching `/dockerfile\|containerfile/i` in the entry's directory (`file_fetcher.rb:12, 59-60`, re-read). |
| S23 | §4e image scan | dropped. Candidate: "renders the chart for each cloud overlay" fails if the overlay is rendered alone. Run: `helm template … -f values-{aws,azure,gcp}.yaml` exits rc=1 three times on the placeholder guard (`<ACM_CERT_ARN>`, or `ingressHost` still the shipped placeholder). `deploy-validate.yml` already layers `values-<cloud>.ci-override.yaml` for exactly this. The spec's wording allows that, so it is a plan detail. Excluding the `iverson-*-model` names from "third-party" agrees with §4d's "TEI and Ollama no longer appear in the chart as third-party images". |
| S24 | §5a rendered assertions **(a)** | ok, not load-bearing (a test list): each round-1 fix has its assertion (fallback toggles, overlay and template image literals, `/data`, Dockerfile-hash tag change). |
| S25 | §5b kind live run **(a)** | → §2.1 (the console's paired check and smoke cannot run under the specified flags) and → §3.1 (whether the specified profile fits the 4-CPU host is UNVERIFIED) |
| S26 | §5c–§5e | ok, not load-bearing |
| S27 | Verified assumptions | see §1 |
| S28 | Known issues **(a)** | ok, not load-bearing (accepted-risk text): the rewritten runtime-matching bullet agrees with §2b's gate and fallbacks, and the GKE endpoint check is retained. |

### Rules and operands

| # | Rule | Disposition |
|---|---|---|
| R1 | Derived model-image tag as image identity (`<slug>-<rev12>-<df12>`, `<model>-<digest12>-<df12>`) **(a)** | over-merge (two engines or two model pins under one tag): ok [compat]. Engine: the probe rendered `bge-base-a5beb1e3e68b-4c4b45bcfb5b`, then `…-372586a12b01` after the FROM line was edited `cpu-1.8.3 → cpu-1.8.4`. Model: ok [existence]. The model components (`.slug` plus `.revision[0:12]`; sanitised `generativeModel` plus `digest[0:12]`) are the round-1 R9 inputs unchanged. A duplicate slug already collides on `<release>-tei-<slug>` object names (`charts/tei/templates/statefulset.yaml:5,11`), and VA24 holds 64 hex (`values.yaml:60`, re-read). / under-merge (identical inputs, two tags): ok [existence]. The tag is a pure function of values fields and file bytes; per-machine line-ending differences are A3's concern. |
| R2 | `dnsAnyDestination` / `apiServerAnyDestination` toggles **(a)** | over (a toggle opens rules other than its own): ok [existence]; §5a asserts "renders `to: []` for exactly its own rules". / under: n/a, because a toggle can only replace a narrow rule with a port-only rule, never remove one, so it cannot block traffic. |
| R3 | "Dependabot helm reads every third-party chart pin" × every image site (P5) **(a)** | over: n/a, because an extra readable pin is a no-op update job. / under: ok [totality]. Every template image site from `command grep -rn 'image:' charts/*/templates` was dispositioned against the post-change form: prometheus `deployment.yaml:41`, redis `:37`, jaeger `:37` and qdrant `statefulset.yaml:45` move to `.Values.image`; authentik server `:54`, `:84` and worker `:27` move to `.Values.image`; tls-proxy `deployment-server.yaml:150` moves to `tlsProxy.image`; `job-revoke-cross-db.yaml:32` moves to `revokeCrossDb.image`; `job-create-user.yaml:31` moves to `createUser.image`; starrocks fe/be are already scalars (`starrockscluster.yaml:7,25`); TEI `statefulset.yaml:47` and Ollama `:45` (init, removed) and `:95` become derived first-party names; api, worker and admin-ui are first-party maps (dropped as in round 1). Each moved key is literally `image`, which `fp.rb:173` matches (`key.to_s == "image"`). Overlay pins: see R4. |
| R4 | Overlay population (P1: all 8 `values-*.yaml`) × {`clusterCidrs` set, `apiServerCidrs` set, no image pin after §4d, how CI renders it} | over (an overlay keeps a stale value or pin): ok [totality]. `command grep -n -E 'imageTag\|image:'` over the overlays → only `values-aws.yaml:140,149,156`, `values-azure.yaml:131,140,147` and `values-gcp.yaml:139,148,155`, all deleted by §4d (VA48 re-read). / under (a rendered combination lacks a required value): ok [totality], one cell per overlay. **aws, azure, gcp**: each sets `clusterCidrs` today and gets a §1a/§2b row. **laptop, local**: same, via the laptop/local rows. **aws.ci-override, azure.ci-override, gcp.ci-override**: no `networkPolicy` key, and `deploy-validate.yml` always layers each over its base overlay (`extra_arg`), so they inherit. No overlay or combination renders without one of the five base overlays. |
| R5 | Terraform roots (P2: all 6 locked roots) × {Dependabot entry, §5d validate, changed by spec} **(a)** | over: n/a, because an extra Dependabot root is a no-op job and an extra validate is harmless. / under: ok [totality] (`git ls-files … .terraform.lock.hcl` → 6). `aws`: listed, validated, changed (§3d). `azure`: listed, validated, changed (§3a, §3c backend). `gcp`: listed, validated, its called `operators` module changed (`vpc_id` var). `bootstrap/azure`: listed, validated, changed (§3c). `bootstrap/aws` and `bootstrap/gcp`: listed, not validated, unchanged by the spec (ok). |
| R6 | §5b paired positive checks × "the 5b profile renders an Ingress the cluster admits and ingress-nginx serves" (P3) **(b: round-1 §2.4 fix text)** | over: n/a, because a positive check that wrongly passes is round 1's vacuous-refusal concern, closed by the pairing. / under → §2.1. Per cell, from the rendered `values-laptop` + `--set global.prometheusEnabled=true --set adminUi.enabled=true`: api 8080 via ingress, ok [existence]: render line 3242 has `ingressClassName: "nginx"`, the class `setup.sh` installs; live serving is recorded by the existing laptop smoke flow, not re-run. Authentik 9080 via ingress, ok [existence] on the same basis (render line 3306); API pod → `prometheus:9090`, no Ingress involved: ok [existence], render. `iverson-api-egress` has `to: podSelector app=iverson-prometheus` on 9090, and `iverson-prometheus-ingress` has `from: podSelector app=iverson-api` on 9090 (python parse of the §2.1-fixed render); live reachability is exercised by the §5b run itself. Prometheus → api 8081, no Ingress: ok [existence], render. `iverson-prometheus-egress` has `to: app=iverson-api, app=iverson-worker` on 8081, and `iverson-api-ingress` has `from: podSelector app=iverson-prometheus` on 8081. Port-forward to Authentik 9000, no Ingress: ok [existence] (recorded, not re-run): VA5 records port-forward bypassing NetworkPolicy (HTTP 200 to a default-denied pod). **console 8080 via ingress → §2.1** (line 3215 `ingressClassName: ""`); **smoke "the console through ingress" → §2.1** (same Ingress). |
| R7 | §5b profile × "every pod schedules on this 4-CPU host" (P6) | over: n/a, because a capacity check has no over-inclusion failure. A pod that schedules when it "shouldn't" is not a failure of §5b. / under (a §5b pod stays Pending): UNVERIFIED → §3.1. Read and arithmetic tier only, because no kind cluster may be created in-round. Effective CPU requests, computed from the renders with init-container max included, are in §3.1. |
| R9 | **Closure for §2.1:** admin-ui Ingress `className` × every overlay that can render it (P7), plus the admin-api Ingress that §2.1's optional fix mentions | Rule: "the rendered console Ingress names a class the profile's ingress controller serves". Over (names a class of a different controller): ok per cell below. Under (empty or missing class): one cell → §2.1. Renders run this round, with `-f values-<o>.yaml` plus `-f values-<o>.ci-override.yaml` where one exists, as `deploy-validate.yml` does; python parse of the Ingress objects. **local**: admin-ui `nginx`, admin-api `nginx`: ok [compat]. **aws (+ci-override)**: `alb` / `alb`: ok [compat]. **azure (+ci-override)**: `azure-application-gateway` / `azure-application-gateway`: ok [compat]. **gcp (+ci-override)**: `gce` / `gce`: ok [compat]. **laptop (as shipped)**: no admin-ui or admin-api Ingress rendered (`adminUi.enabled: false`, `api.adminConsoleEnabled` unset), ok [compat]: nothing to serve, by design. **laptop + §5b flags**: admin-ui `''` → §2.1; with `--set api.adminConsoleEnabled=true` added, admin-api `nginx`, ok [compat] (the api subchart's own default `adminApiIngress.className: "nginx"`, `charts/api/values.yaml:29-30`). **aws/azure/gcp `.ci-override.yaml` alone**: `command grep -c className` → 0 in each; they set no class and are only ever layered on their base overlay, so they inherit its class, ok [existence]. **Is the empty default intentionally overridden elsewhere?** Yes. The umbrella `adminUi.ingress.className: ""` is a per-environment placeholder that every console-running overlay sets itself (`values-local.yaml:180`; `values-aws.yaml:187`; `values-azure.yaml:167`; `values-gcp.yaml:175`). `values-laptop` sets none, because it disables the console. §5b's `adminUi.enabled=true` turns on a component on a profile that never supplies the class every other console profile supplies. |
| R8 | DNS rule sites (P4: all 14 `port: 53` rules) × post-change form | over (a site keeps any-destination at defaults): ok [totality]. The 14 sites are the egress policies of api, worker, postgres, kafka, starrocks, starrocks-create-user, authentik-revoke-cross-db, qdrant, tei, ollama, jaeger, prometheus, authentik and redis (`grep -c 'port: 53'` = 14, re-read). Twelve get the helper and two (tei, ollama) get none. / under (a site that needs DNS gets none): ok [existence] (recorded, not re-run): TEI and Ollama serve with `--network none` (VA19–VA22). |

### Data-flow arrows

| # | Arrow | Disposition |
|---|---|---|
| A1 | `model-image/Dockerfile` → `$.Files.Get` → `sha256sum \| trunc 12` → rendered tag. Chart call site, two load paths **(a)** | ok [compat], run. Subchart loaded as a directory (no tgz): tag suffix `4c4b45bcfb5b`. After `helm dependency build` (tgz present): `4c4b45bcfb5b`. After editing the file with the stale tgz still present: the live file's `372586a12b01`. So neither load path renders a hash that differs from the file on disk. The result would have come out the other way if the tgz had shadowed the directory. |
| A2 | Same Dockerfile → `sha256sum \| cut -c1-12` → `build-and-load-image.sh` tag **(a)** | ok [existence]: a standalone `sha256sum … \| cut -c1-12` gives `4c4b45bcfb5b`, equal to the A1 render. The script itself was not run: the hashing step does not exist yet (`build-and-load-image.sh` today has no hash code), and it builds images. This shows that the command §2c names produces the chart's value. |
| A3 | Same Dockerfile → hash → `build-and-load-image.ps1` tag **(a)** | dropped. Candidate: PowerShell's `Get-FileHash` prints uppercase hex, which would not match the chart's lowercase. The spec states the invariant ("the same Dockerfile hash the chart renders"), and casing is plan-level. Line endings: chart and script read the same checkout on one machine, so they agree. |
| A4 | `values-laptop` + §5b flags → `helm install` → API-server admission of the admin-ui Ingress | → §2.1 |
| A5 | `values-laptop` + §5b flags → scheduler (CPU requests vs 4 allocatable CPUs) | → §3.1 (UNVERIFIED) |
| A6 | cloud overlay → image-scan render → third-party extraction | dropped; see S23 |
| A8 | §2.1 fix → live `GET /admin` through ingress-nginx returns 200 | UNVERIFIED → §3.1 (verified only by executing §5b; no kind cluster may be created in-round). |
| A9 | `ingressClassName: ""` → API-server rejection (the §2.1 description's first branch) | UNVERIFIED, not load-bearing → §3.1. §2.1's verdict holds under both branches (rejected, or admitted but unserved by the `nginx` controller), and the fix removes the empty class, so the branch is never exercised. It flows to §3.1 as an accepted residue. |
| A7 | **(c) Amendment hunks since `b6b09c8c`** | ok [totality]: content-identity unequal; `git diff b6b09c8c -- <spec>` hunks are all in-band (one commit, `8a4a68c6 applied 7 fixes …`); reverse window empty; no out-of-band hunk exists to review. |
| B1 | **(b) Round-1 fix texts intersected by this round's discoveries** | Round-1 §2.4 fix text "ingress-nginx serves `/admin` from the console's port 8080" (its own Evidence: "UNVERIFIED: that it returns 200 live") ∩ the laptop render's `ingressClassName: ""` → §2.1. Round-1 §2.4 Evidence "UNVERIFIED: that the extra pods fit the 9 GB VM" was never routed to §3; it intersects the overlay's own CPU budget → §3.1. Round-1 §3.2 option A "UNVERIFIED: Dependabot docker at the new location" ∩ VA58: resolved, ok [existence], read. `file_fetcher.rb:12` has `DOCKER_REGEXP = /dockerfile\|containerfile/i`, and `:59-60` selects regular files of the entry's directory, which §4d now names (S22). Round-1 §2.1 "UNVERIFIED: the `kubernetes` Service takes the first address" ∩ VA54: resolved, ok [existence] (recorded, not re-run): VA54 records `kubernetes.default` → `10.96.0.1` for service range `10.96.0.0/12` on live kind. |

Row tally: 28 section rows + 9 rule rows + 9 arrow rows + 1 family-(b) row = **47 rows: 32 ok, 9 → findings, 6 dropped**.
- The nine rows → findings are S25, R6, R7, R9, A4, A5, A8, A9 and B1.
- The six dropped rows are S7, S13, S16, S23, A3 and A6.
- S9, S12 and S27 are pointer rows, counted as ok; their dispositions are the rows they name.
- S5's dropped sub-candidate is reported inside that ok row and is not counted separately.

## 1. Verified-assumptions cross-check

**Re-read or re-run this round (all hold):**
- 6: `charts/api/templates/deployment.yaml:186-196`, BaseUrl and MetadataAddress on 8443.
- 8: `from: []` at `:514, :605, :615`.
- 11: 14 `port: 53`; 443/6443 at `:212, :274, :462, :493`.
- 14: `endpoints.go:219-225` vs `:259`, and the README "pre-DNAT" reading now matches the spec text.
- 24: `values.yaml:60`.
- 29: `kcr.go:87-89` and the identity update path.
- 34: no `backend` in `bootstrap/azure/main.tf`.
- 44: Docker Hub digests.
- 45: three nginx-unprivileged sites; the compose file is `Iverson.Server/docker-compose.yml:442`.
- 47: `postgres:16` = `16.15`, `mysql:8.0` = `8.0.46` and `redis:7.4-alpine` = `7.4.11-alpine` by Docker Hub digest; ghcr.io `cpu-1.8` = `cpu-1.8.3` (`sha256:8de25e75…`).
- 48: overlay lines and values.
- 49: `fp.rb:15, 192`.
- 54: the Terraform half by grep; the kind half recorded.
- 55: `statefulset.yaml:103`.
- 56: `values-laptop.yaml:24, 40-41` plus a render run.
- 57: re-run, [compat], see A1.
- 58: `file_fetcher.rb:12, 59-60`.

**Accepted as ground truth, unchanged since round 1 and not re-fetched this round:** 1–5, 7, 9, 10, 12, 13, 15–23, 25–28, 30–33, 35–43, 46, 50–53.

**Failed:** none.

**Span check (uncovered dependencies):**
- U1. That the §5b profile renders an Ingress for the console that the API server admits and ingress-nginx serves. No VA covers it; VA56 covers only that the workloads render. Verified in-round: it does not → §2.1.
- U2. That the §5b profile's pods fit the 4-CPU kind host (`nproc` = 4). No VA covers it. It cannot be verified without a cluster → §3.1.

## 2. Literal-wrongness findings

### 2.1 §5b's console checks cannot run: under `values-laptop` the console Ingress renders `ingressClassName: ""`

**Description.** Round 1's fix runs §5b on `values-laptop` with `--set global.prometheusEnabled=true --set adminUi.enabled=true`, and pairs the admin-ui refusal with "ingress-nginx serves `/admin` from the console's 8080". The smoke flow also exercises "the console through ingress". `values-laptop` never sizes or configures `adminUi` beyond `enabled: false`, so the console Ingress takes the umbrella default `adminUi.ingress.className: ""` (`values.yaml`, `adminUi.ingress`). The admin-ui template renders that verbatim as `spec.ingressClassName: ""` (`charts/admin-ui/templates/ingress.yaml`, `ingressClassName: {{ .Values.ingress.className | quote }}`). Kubernetes validates a non-nil `ingressClassName` as a DNS-1123 subdomain, and the empty string fails that. So `helm install` of the §5b profile is rejected at the console Ingress. Neither the positive control nor the console smoke can run, and the live verification §5b specifies for `admin-ui-ingress` is impossible as written. Even if such an Ingress were admitted, ingress-nginx serves only its own class (`nginx`), so the control could not pass. The other two Ingresses in the profile are fine (R6).

**Evidence.**
- [compat] Run: `helm template iverson <scratch chart> -f values-laptop.yaml --set global.prometheusEnabled=true --set adminUi.enabled=true` → rc=0, and the admin-ui Ingress has `ingressClassName: ""` (line 3215). The api and authentik Ingresses have `"nginx"` (lines 3242, 3306).
- [existence] Kubernetes v1.30 `pkg/apis/networking/validation/validation.go:339-341`: `if spec.IngressClassName != nil { for _, msg := range ValidateIngressClassName(*spec.IngressClassName, false) …`, with `ValidateIngressClassName = apimachineryvalidation.NameIsDNSSubdomain` (`:508`). apimachinery v0.30 `IsDNS1123Subdomain` (`validation.go:214-223`) fails any value that does not match `^[a-z0-9]([-a-z0-9]*[a-z0-9])?(\.[a-z0-9]([-a-z0-9]*[a-z0-9])?)*$`.
- [existence] A `python3` `re.fullmatch` of that exact pattern: `''` → False, `'nginx'` → True.
- UNVERIFIED (run tier, not load-bearing; row A9 → §3.1): the live API-server rejection. No kind cluster may be created in-round. The finding does not depend on it, because the "admitted but unserved" branch also fails the control.

**Proposed fix.**
- Add `--set adminUi.ingress.className=nginx` to §5b's flag list, in the §5b heading and in the sentence that justifies the flags.
- Extend VA56 to record that, with the three flags, the console Ingress renders `ingressClassName: "nginx"`.
- Optional, and only if the console smoke is meant to exercise console → API calls: also `--set api.adminConsoleEnabled=true`, which renders the `admin-api` Ingress (class `nginx`, from the api subchart's own default; R9 cell) and `AdminConsole__Origin`.

Evidence:
- [compat] Run: the same render plus `--set adminUi.ingress.className=nginx` → rc=0. All three Ingresses render `ingressClassName: "nginx"` (lines 3222, 3249, 3313), and `kubeconform -kubernetes-version 1.30.0 -ignore-missing-schemas` reports 90 resources, 85 valid, 0 invalid, 0 errors, 5 skipped.
- [compat] Run: adding `--set api.adminConsoleEnabled=true` renders `iverson-admin-api` with `ingressClassName: "nginx"` and host `admin-api.iverson.local`, plus `AdminConsole__Origin: http://iverson.local`.
- [existence] `deploy/kind/setup.sh:110-117` installs ingress-nginx 4.12.8 with `controller.allowSnippetAnnotations=true` and `annotations-risk-level=Critical`, which the admin-ui Ingress's `nginx` branch (`configuration-snippet`) needs.
- UNVERIFIED (row A8 → §3.1): that `/admin` returns 200 live. Run tier is unavailable in-round.
- [compat] Closure (R9): every other overlay that renders the console Ingress already names its controller's class. Laptop is the only profile where it is empty, and only once §5b enables the console.

## 3. Forced decisions

### 3.1 Whether the §5b profile fits the 4-CPU kind host, and how to make room if it does not

**The choice.** Whether to run §5b with the specified flags and handle a scheduling failure if one occurs, or to size the two added components in the flags up front.

**Why it is forced.** §5b requires "Every pod reaches Ready" on a throwaway kind cluster on this machine (Global constraints: WSL, one heavy process at a time; `nproc` = 4). `values-laptop.yaml:8-11` states the profile's budget: "1.7 CPU of requests; system + operators measured ~2.0; metrics-server ~0.1. Total ~3.8 of 4.0 … Margin is thin — roughly 200m." Effective CPU requests computed from the renders, counting each pod as the larger of its biggest init container and the sum of its app containers:

| Render | Effective CPU requests |
|---|---|
| `values-laptop`, current chart (the deploy that works today) | 1.96: Ollama's `pull-model` init container (500m) dominates its 250m app container |
| `values-laptop` after §2c removes that init container | 1.71 |
| §5b profile after §2c: Prometheus 250m plus admin-ui 2 × 100m added | 2.16, which is +0.20 over today's deploy and +0.45 over post-§2c laptop |

The overlay's stated margin was computed against 1.7, without the init container, so the real margin of today's deploy is unknown and may be under 200m. A Pending pod makes "Every pod reaches Ready" fail for reasons unrelated to NetworkPolicy. Round 1 flagged the memory side ("UNVERIFIED: that the extra pods fit the 9 GB VM") in a fix's Evidence line without routing it here. The memory requests are 5712 Mi for today's laptop and 6480 Mi for the §5b profile.

**Options.**
- **A. Run as specified and verify empirically.** Keep the flags. If a pod stays Pending, apply option B's sizing and re-run.
  Evidence: UNVERIFIED: whether 2.16 CPU of workload requests plus the system share fits in 4 allocatable CPUs. No kind cluster may be created in-round.
- **B. Size the additions in the §5b flags.** Add `--set adminUi.replicas=1 --set-string adminUi.resources.requests.cpu=50m --set-string adminUi.resources.requests.memory=64Mi` (`values-local`'s console sizing), and optionally `--set-string prometheus.resources.requests.cpu=100m`. Only requests change, and limits stay as rendered.
  Evidence: [existence] `values-local.yaml:174-178`: `adminUi.replicas: 1`, requests `50m`/`64Mi`. Arithmetic from the same renders: 1.71 + 0.05 + 0.25 = 2.01 with Prometheus at its default (+0.05 over today's working deploy), or 1.86 with Prometheus at 100m (−0.10, below today's deploy). UNVERIFIED: that today's laptop deploy on this host has at least 50m to spare, which is needed only for the Prometheus-default variant.
- **C. Split §5b into two passes.** Pass 1 is today's laptop profile plus the TEI/Ollama, DNS, API-server and Authentik checks. Pass 2 adds Prometheus and the console with `--set ollama.enabled=false`, which frees 250m CPU and 2 GiB of memory requests.
  Evidence: [existence] the Ollama StatefulSet's app request is 250m/2Gi in `values-laptop.yaml` (render: `iverson-ollama` 0.25 CPU, 2048 Mi). UNVERIFIED: that API/worker pods reach Ready with Ollama absent (they reference it only for enrichment calls).

**Residues verified only by the same run** (rows A8 and A9). Every option executes §5b, and that run is the only place these can be observed. The §2.1-fixed `/admin` control returning 200 is verified, or fails visibly, at that run under A, B or C alike. The API server's rejection of an empty `ingressClassName` is not load-bearing: §2.1 holds under either branch, and the fix removes the state. It is accepted as a residue under every option. Neither residue distinguishes the options.

**Why no option dominates.** B's 100m-Prometheus variant puts requests below a deploy that works today, so it removes the CPU risk at the cost of running two components at non-default requests (which do not affect NetworkPolicy behaviour). A keeps the spec's flags exactly and costs at most one failed run. C keeps default sizing for every component but doubles the deploy time on a memory-tight host, and runs the console and Prometheus checks without Ollama. For the CPU question alone, B weakly dominates A. Whether to deviate from default requests in a verification run is the user's call, and only C keeps default sizing with headroom.

## 4. Previously addressed

- Round-1 §2.1 (EKS ipBlock cannot match ClusterIP traffic): AWS `apiServerCidrs` now lists `10.100.0.1/32` and `172.20.0.1/32` beside the cluster subnets. VA14 has been rewritten and VA54 added, and the first-deploy gate covers the run tier.
- Round-1 §2.2 (Dependabot misses overlay and template images): the nine overlay `imageTag` pins are deleted, and the three template literals move to subchart `image` keys. A §5a assertion was added.
- Round-1 §2.3 (`OLLAMA_MODELS=/data` survives): the env entry is removed. VA55 was added, along with a §5a `/data` assertion.
- Round-1 §2.4 (vacuous laptop checks): the flags and paired positive controls were added. The fix's console control is the subject of this round's §2.1.
- Round-1 §2.5 (terraform roster): all six locked roots are listed.
- Round-1 §3.1 (managed-dataplane matching): option A was chosen. §2b now has the first-deploy gate with the `dnsAnyDestination`/`apiServerAnyDestination` fallbacks, and Known issues was rewritten.
- Round-1 §3.2 (engine bump never reaches pods): option A was chosen. The Dockerfiles moved into the subcharts, and the tag carries the Dockerfile hash (VA57, VA58).

## 5. Recommendation

🛑 **Surface forced decisions to user.**
- §3.1: whether §5b's profile fits the 4-CPU kind host, and how to make room (run and see, size the console and Prometheus requests in the flags, or split the run).
- §2.1: add `--set adminUi.ingress.className=nginx` to §5b, because without it the console Ingress renders `ingressClassName: ""` and the console checks cannot run.
