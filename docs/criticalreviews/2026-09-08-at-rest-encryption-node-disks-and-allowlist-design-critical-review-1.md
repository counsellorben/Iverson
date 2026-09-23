# Critical Design Review: 2026-09-08-at-rest-encryption-node-disks-and-allowlist-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-09-08-at-rest-encryption-node-disks-and-allowlist-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

### Sections

| # | Section | Disposition |
|---|---|---|
| S1 | Header / Status / Predecessor | ok — predecessor `docs/specs/2026-08-24-at-rest-encryption-design.md` exists; its runbook `docs/runbooks/at-rest-encryption-verification.md` exists and matches the cited line numbers |
| S2 | Problem | ok — checked all four citations: `modules/cluster-azure/main.tf:152-200` is `azurerm_kubernetes_cluster.this` with no `disk_encryption_set_id` (grep for `disk_encryption_set_id` in that file returns nothing); `modules/cluster-gcp/main.tf:222`/`:261` are the two `node_config` blocks, neither with `boot_disk_kms_key`; `charts/qdrant/templates/statefulset.yaml:107-108` is exactly `- name: snapshots` / `emptyDir: {}`; runbook check 4 (`:136-151`) is a manual `kubectl get pvc` enumeration |
| S3 | Scope — In | ok — the three deliverables map to real files: two Terraform attributes, a new chart under `modules/operators`, runbook edits |
| S4 | Scope — Out, with reasons | ok — each exclusion is a scope statement, not a design dependency; the "no PVC exists outside `iverson` today" claim is consistent with the PVC-producer grep in R3 below |
| S5 | Design §1 — GKE node boot disks | → §1 (all citations reconfirmed); no finding |
| S6 | Design §2 — AKS node OS disks | → §1 (A10 scope gap) → §3.1 |
| S7 | Design §3 — What it does (the CEL policy) | ok — see R1, R5, R6 |
| S8 | Design §3 — Coverage | ok — see R3; the "no route to a bound volume that skips it" negative claim was re-grepped independently, 7 producers, all templating a class |
| S9 | Design §3 — How it is applied | ok — see D1, D6; `helm_release` shape, local chart path, provider pins and the single-pass argument all reconfirmed |
| S10 | Design §3 — Failure mode | ok — a denied PVC surfaces as an admission error on the creating controller; nothing in the design depends on more than that |
| S11 | Design §4 — Evidence (runbook amendments) | → §2.1 |
| S12 | Design §5 — Greenfield | ok — no cloud state exists (`*.tfstate` is gitignored and no state file is tracked); both attributes are create-time-only per A3/A7 and both are set on first apply |
| S13 | Design §6 — Testing | ok — see D6, D7. `terraform fmt -check` is run with `-chdir=<root>` and no `-recursive`, so it does not reach the edited module files; `terraform validate` from the root does load and validate child modules, which is what catches an invalid attribute name. The spec's claim is worded about the roots and is true as written |
| S14 | Verified assumptions table | → §1 |
| S15 | Known issues, accepted as out of scope | ok — the stale `modules/cluster-aws/main.tf:25` comment is real (line 25 does reference "the companion Helm chart's Postgres backup retentionPolicy"; no such policy exists in `charts/postgres/templates/cluster.yaml`) and is correctly flagged as off-path |

### Rules and operands

| # | Rule | Disposition |
|---|---|---|
| R1 | CEL: `has(object.spec.storageClassName) && object.spec.storageClassName in [...seven]` — **object side** | ok — both directions checked. Under-inclusion (wrongly denying a legitimate PVC): absent class → `storageClassName` is `*string` with `omitempty`, nil pointer omits the key, `has()` false, denied — matches the spec's stated intent. Explicit `""` → non-nil pointer to `""` is serialized (omitempty only elides nil), so `has()` is true and `"" in [...]` is false, denied — the spec's claim holds. Over-inclusion: `storageClassName` is immutable after create, so a CREATE-only match cannot be bypassed by a later UPDATE |
| R2 | Binding scope: `matchResources.namespaceSelector` on `kubernetes.io/metadata.name: iverson` | ok — `kubernetes_namespace.iverson` (`modules/operators/main.tf:12-19`) sets only `pod-security.kubernetes.io/enforce`; `kubernetes.io/metadata.name` is added by the API server and is immutable, so a Terraform-managed `labels` map cannot strip it |
| R3 | Eligibility predicate — **every producer** of PVCs in `iverson` on a cloud profile | ok — independent grep of all 13 subchart `templates/` dirs for `PersistentVolumeClaim` / `volumeClaimTemplates` / `storageVolumes` / `storageClass`: `prometheus/pvc.yaml:2,7`; `qdrant/statefulset.yaml:114,119`; `ollama/statefulset.yaml:102,107`; `tei/statefulset.yaml:84,89`; `postgres/cluster.yaml:8,10` (CNPG); `kafka/kafka.yaml:50,53` (Strimzi KafkaNodePool); `starrocks/starrockscluster.yaml:15,17,33,35` (StarRocks FE meta + BE data). Seven producers, every one templating `.Values.storageClassName`. `authentik`, `redis`, `jaeger`, `api`, `worker`, `admin-ui` declare none (`redis/deployment.yaml:72` and `authentik/job-revoke-cross-db.yaml:93` are `emptyDir`). Nothing under `Iverson.LoadTest`. The spec's "four directly + three via operators" split matches exactly |
| R4 | Allow-list operand: the seven Terraform `kubernetes_storage_class` names vs the seven names the charts actually request | ok — module names are `iverson-{postgres,starrocks,qdrant,kafka,ollama,tei,prometheus}` (`modules/operators/main.tf`); `values-aws.yaml:10,20,30,50,60,70,117`, `values-azure.yaml:10,20,30,50,60,70,110`, `values-gcp.yaml:11,21,31,51,61,71,111` request exactly those seven strings. No eighth name, no drift |
| R5 | `matchConstraints`: CREATE, core group, `persistentvolumeclaims` | ok — StatefulSet `volumeClaimTemplates` are materialised as ordinary core/v1 PVC CREATEs by the StatefulSet controller, and the three operators create PVCs the same way; there is no alternate create path |
| R6 | Admission ordering: `DefaultStorageClass` (mutating) runs before validating admission | ok — on all three clouds a default StorageClass exists (EKS `gp2`, GKE `standard-rwo`, AKS `default`), so a class-less PVC is defaulted to that name before the policy sees it. The spec's stated *mechanism* ("`has()` is false") is then not the one that fires, but the *outcome* is identical — the defaulted name is not in the seven, so it is denied either way. None of the seven is marked default, so the mutation cannot silently admit anything. No finding |
| R7 | GKE edit-site rule: which `node_config` blocks actually produce nodes | ok — `remove_default_node_pool = true` at `:153`; grep for `google_container_node_pool` returns exactly two resources (`:201`, `:244`); `local.extra_pools` has seven entries. Also checked the input class the spec does not name: no `cluster_autoscaling` block and no `enable_autopilot`, so GKE node auto-provisioning cannot create a third, un-keyed pool |
| R8 | AKS placement rule: `kubelet_disk_type` and DES coverage of every pool | ok — grep of `modules/cluster-azure/main.tf` for `kubelet_disk_type`, `os_disk_type`, `os_disk_size_gb`: zero hits, so A11's "no pool sets it" holds and the declaration is additive. Cluster-level `disk_encryption_set_id` covers `default_node_pool` and `azurerm_kubernetes_cluster_node_pool.pools` (seven entries), which is every pool in the file |

### Data-flow arrows

| # | Arrow (ends at an operation) | Disposition |
|---|---|---|
| D1 | `kubernetes_storage_class.*.metadata[0].name` → `yamlencode` → `helm_release.values` → chart `.Values.allowedStorageClasses` → `toJson` → rendered CEL list literal → **API-server CEL evaluation**. Crosses three serialization boundaries | ok — every parameter the final operation needs exists at each hop. The names are static strings in the module, so known at plan time (no unknown-value diff). `outputs.tf:3-9` already dereferences all seven the same way, proving the attribute path resolves. `toJson` on a list of strings yields `["a","b",...]`, which is a valid CEL list literal (double quotes are CEL string delimiters). The YAML-quoting of that literal inside the template is an implementation detail, not a design dependency |
| D2 | `google_kms_crypto_key.data_volumes.id` → `node_config.boot_disk_kms_key` → **GCE disk-create call**, whose second required input (`roles/cloudkms.cryptoKeyEncrypterDecrypter` for the Compute Engine service agent) is sourced from a *different* resource | dropped — candidate was: `google_kms_crypto_key_iam_binding.data_volumes` (`:84-90`) is not in the node pool's dependency graph (the pool references the key, not the binding), so Terraform may create them concurrently. Fails the literal-wrongness test: the node pool also depends on `google_container_cluster.this`, whose creation takes minutes, while the binding depends only on the key and completes in seconds — the binding is always in place before the first node pool create call is issued. Same latent shape already exists for `database_encryption` and has never been the failure mode |
| D3 | `azurerm_disk_encryption_set.data_volumes.id` → `azurerm_kubernetes_cluster.disk_encryption_set_id` → **ARM cluster-create**, which needs the DES identity's vault wrap/unwrap grant | dropped — same shape as D2: `azurerm_key_vault_access_policy.des` (`:99-105`) is parallel to the cluster rather than upstream of it, but the access policy completes in seconds and AKS node/OS-disk provisioning happens minutes into the cluster create. Also re-derived the cycle question independently: DES → cluster → `azurerm_role_assignment.aks_data_volumes_des` (`:119-123`) is acyclic, confirming A8 |
| D4 | AKS node OS disk → **`az disk list`/`az disk show` in the node resource group** → `encryption.type` / `encryption.diskEncryptionSetId` read | → §1 span gap → §3.1. The consuming operation needs the OS disk to exist as a standalone `Microsoft.Compute/disks` resource in the node RG; A10's cited evidence establishes only that `os_disk_type` defaults to `Managed`, and A21's cited evidence is check 3, which reads CSI-created *data* disks |
| D5 | GKE node boot disk → **`gcloud compute disks describe <name>`** → `diskEncryptionKey.kmsKeyName` read | → §2.1. Boot disks are zonal `compute.disks` resources named after the instance (A22 holds), but the describe call has a required `--zone` parameter the design's amendment does not source, and the runbook's front matter asserts that only one command needs a bracketed substitution |
| D6 | New chart path `modules/operators/charts/pvc-storageclass-policy/` → **`helm lint` in two CI files** | ok — `.gitignore` has no `charts/` rule that would catch it (line 74 is `Iverson.Server/deploy/helm/**/charts/*.tgz`, scoped to the helm tree and to `.tgz`), so the chart is committable. Both CI path filters cover it: `.github/workflows/deploy-validate.yml` `paths: Iverson.Server/deploy/**` and `.gitlab-ci.yml:5-8` `changes: Iverson.Server/deploy/**/*`. The lint loops are at `.github/…:28-35` and `.gitlab-ci.yml:15-20`, both currently umbrella-only, so A27 holds and a one-line addition is the right shape |
| D7 | Module edits → **`terraform validate`** at each cloud root | ok — `terraform -chdir=<root> init -backend=false` walks child modules and installs their providers; `validate` then type-checks `boot_disk_kms_key`, `disk_encryption_set_id`, `kubelet_disk_type` and the new `helm_release` against the pinned provider schemas. Confirmed at `.github/…:120-127` and `.gitlab-ci.yml:73-75` |
| D8 | Denied PVC → **StatefulSet pod event / operator reconcile error** | ok — no design dependency beyond the error being visible; nothing downstream consumes a field from the denial |

## 1. Verified-assumptions cross-check

Repo-side citations were re-read against the working tree; vendor-documentation citations are taken as ground truth per the skill.

- **A1** — reconfirmed. `modules/cluster-gcp/main.tf:153` is `remove_default_node_pool = true`; `google_container_node_pool.general` is at `:201`.
- **A2** — reconfirmed. `:201` and `:244` are the only two `google_container_node_pool` resources in the file; `local.extra_pools` (`:233-241`) has seven entries.
- **A3, A4, A5** — reconfirmed as far as repo evidence goes: the binding is at `modules/cluster-gcp/main.tf:84-90` and names `service-<project-number>@compute-system.iam.gserviceaccount.com` with `roles/cloudkms.cryptoKeyEncrypterDecrypter`. Provider-doc halves taken as ground truth.
- **A6** — reconfirmed. Key ring `location = var.region` at `:49`; cluster `location = var.region` at `:136`; pools at `:204` and `:248`.
- **A7** — vendor; ground truth.
- **A8** — reconfirmed and independently re-derived. `modules/cluster-azure/main.tf:78-105` is the DES plus its access policy; the only resource referencing both DES and cluster is `azurerm_role_assignment.aks_data_volumes_des` at `:119-123`. Adding `disk_encryption_set_id` gives DES → cluster → role assignment: acyclic.
- **A9** — vendor; ground truth.
- **A10** — reconfirmed *as far as its evidence reaches*. Grep of `modules/cluster-azure/main.tf` for `os_disk_type` returns nothing, so the provider default `Managed` applies to every pool. The second half of the claim — "attestable per disk" — is not established by that evidence; see the span check below.
- **A11** — reconfirmed. Grep for `kubelet_disk_type` in `modules/cluster-azure/main.tf` returns nothing, so no pool sets it today and the declaration is purely additive.
- **A12** — reconfirmed. `:80` DES `location = azurerm_resource_group.this.location`; `:154` cluster `location = azurerm_resource_group.this.location`.
- **A13** — reconfirmed. `modules/cluster-aws/variables.tf:13`, `cluster-azure/variables.tf:13`, `cluster-gcp/variables.tf:15` all `default = "1.30"`.
- **A14** — reconfirmed. `kubernetes = { … version = "~> 2.31" }` at `modules/operators/main.tf:3`, `aws/main.tf:5`, `azure/main.tf:5`, `gcp/main.tf:5`. Provider-CHANGELOG half taken as ground truth.
- **A15** — reconfirmed. `modules/operators/main.tf:12-19` creates the namespace with a `labels` map containing only the PSA label.
- **A16** — reconfirmed. `Iverson.Server/deploy/kind/setup.sh:104` ends with `-n iverson`.
- **A17** — vendor; ground truth.
- **A18** — reconfirmed independently (see §0 R3): seven PVC producers, all templating `.Values.storageClassName`; nothing under `Iverson.LoadTest`. Note the wording "the same seven files as the first design's A16" is loose — the predecessor's A16 counted six named stores and three direct declarers, whereas today there are seven and four (TEI joined). This spec's own §3 Coverage prose ("Qdrant, Ollama, TEI, Prometheus" + three operators) is the accurate statement and matches the grep.
- **A19** — reconfirmed. `modules/operators/outputs.tf:3-9` references all seven `metadata[0].name`.
- **A20** — reconfirmed, exact. `.github/workflows/deploy-validate.yml:120-127`; `.gitlab-ci.yml:73-75`.
- **A21** — reconfirmed only for its check-3 half: the runbook already reads `encryption.type` / `encryption.diskEncryptionSetId` from `az disk show` at `docs/runbooks/at-rest-encryption-verification.md:118-125`, but those are the CSI-created *data* disks resolved from `kubectl get pv`. The `node_resource_group` attribute half is vendor; ground truth. See span check.
- **A22** — vendor plus runbook check 3 (`:127-134`); reconfirmed as a claim about the *field*. See §2.1 for the invocation.
- **A23** — reconfirmed, exact. `:136` check 4, `:153` check 5, `:186-188` the closing coverage note.
- **A24** — reconfirmed. `.gitignore:51` is `**/docs/specs/`.
- **A25** — vendor; ground truth.
- **A26** — reconfirmed. `modules/operators/main.tf:21-56` is `helm_release.cloudnative_pg` through `helm_release.strimzi`, the latter with `set` and `depends_on = [kubernetes_namespace.iverson]`.
- **A27** — reconfirmed, exact. `.github/workflows/deploy-validate.yml:28-35` lints only `Iverson.Server/deploy/helm/iverson`; `.gitlab-ci.yml:18-19` the same.

### Span check — design dependencies no listed assumption covers, as scoped

1. **AKS node OS disks are enumerable as standalone `Microsoft.Compute/disks` resources in the node resource group.** Check 5's Azure variant ("list the disks in the node resource group … filter to OS disks") consumes this. A10 establishes managed-disk *type* from `os_disk_type`'s default; A21 establishes that `az disk` reports encryption for the disks check 3 already reads, which are CSI-provisioned data disks resolved from `kubectl get pv`. Neither reaches the enumerability of AKS agent-pool OS disks, which are backed by a Virtual Machine Scale Set rather than by standalone VMs. Not verifiable in-round — no Azure cluster exists and no repo artifact settles it. → **§3.1**.
2. **`gcloud compute disks describe` for a node boot disk needs a zone.** A22 covers the field the command reads, not the parameters the command requires. Verified in-round against the runbook's own text: check 3's GCP variant already carries a `<zone>` placeholder (`:132`) precisely because `compute disks` are zonal, and the runbook's front matter (`:7-8`) names that as the *only* substitution in the document. → **§2.1**.
3. **The runbook's orientation paragraph is an edit site.** A23 lists three edit sites; `:12-14` ("Check 5 (node root volumes) is AWS-only by design — see the coverage note at the end") is a fourth that the amendments directly falsify. → **§2.1**.
4. **GKE cannot create a node pool outside the two the design edits.** Verified in-round: no `cluster_autoscaling` (node auto-provisioning) block and no `enable_autopilot` in `modules/cluster-gcp/main.tf`, so A2's two-resource enumeration is closed. No finding.
5. **The new chart is committable and inside both CI path filters.** Verified in-round against `.gitignore:74` and both CI trigger blocks. No finding.
6. **No cloud StorageClass in the seven is cluster-default, and `DefaultStorageClass` mutation cannot admit an unlisted class.** Verified in-round (§0 R6). No finding.

## 2. Literal-wrongness findings

### 2.1 The runbook amendments falsify two statements in the runbook's own front matter, and neither is listed as an edit site

**Description.** Section 4 amends check 4, check 5 and the closing coverage note (the three sites A23 enumerates). But `docs/runbooks/at-rest-encryption-verification.md` states its coverage and its command conventions twice more, at the top of the file, and both statements become false the moment the amendments land:

- `:12-14` — "Commands are written against AWS, the primary target of this control's Terraform. **Checks 1 and 3 have an Azure/GCP equivalent, noted inline. Check 5 (node root volumes) is AWS-only by design — see the coverage note at the end.**" After the amendment, check 5 has Azure and GCP variants, and the coverage note it forwards the reader to has been replaced by a sentence saying the opposite. The design's stated outcome for section 4 is a runbook that gives an auditor evidence that node disks are under the same key on all three clouds, with "a single key reference … in every check". An auditor reading the document top-to-bottom is told, in the paragraph whose entire job is to say what is covered where, that node-disk evidence on Azure and GCP does not exist — and is pointed at a note that no longer says that. The artifact contradicts itself about its own scope.
- `:7-8` — "Every command below is runnable as written **except for the bracketed substitution `<zone>` in check 3's GCP variant** — the Compute Engine zone the node's disk lives in." Check 5's new GCP variant reads a node's *boot* disk, which is a zonal `compute.disks` resource; `gcloud compute disks describe NAME` has a required zonal qualifier and will not run non-interactively without one. So after the amendment there are two commands needing a zone, not one, and the invariant at `:7-8` is false. (Section 4 says only "for each node, describe its boot disk (named after the instance)" and does not source a zone. It is derivable — a node's `spec.providerID` is `gce://<project>/<zone>/<instance>`, the same handle check 5's AWS variant already parses at `:166` — but the design does not say so, and the front-matter claim has to change either way.)

Additionally, the replacement text the design specifies leaves the section it edits self-contradictory: `:186` is the heading `## What this evidence set does not cover`, and the design replaces its body with a sentence stating that node disks *are* covered everywhere. The design's own Scope section still records three genuinely uncovered surfaces (AKS etcd Secrets, the AWS VPC flow-log group, the Azure Log Analytics workspace), so the section has content it could carry; the design does not say what becomes of the heading.

**Evidence.**
- `docs/runbooks/at-rest-encryption-verification.md:7-8`, `:12-14`, `:186` (heading), `:188` (the sentence the design replaces).
- The spec's A23 lists exactly `:136`, `:153`, `:186-188` as the runbook sites the design edits — `:7-8` and `:12-14` are not among them.
- `docs/runbooks/at-rest-encryption-verification.md:132` — check 3's GCP variant already uses `--zone <zone>`, confirming that `compute disks describe` is zone-qualified in this runbook's own idiom.
- `docs/runbooks/at-rest-encryption-verification.md:166` — the AWS check-5 command already derives per-node identity from `spec.providerID`, the same field that carries the GCP zone.

**Proposed fix.** Extend section 4's edit list (and A23) to include the runbook's front matter, with three concrete edits:
1. Rewrite `:12-14` so it says checks 1, 3 and 5 all have Azure/GCP equivalents, and drop the forward reference to a coverage note that no longer makes that claim.
2. Either state in section 4 that check 5's GCP variant derives the zone from `spec.providerID` (making it runnable as written and leaving `:7-8` true), or amend `:7-8` to name both bracketed substitutions.
3. Say explicitly what happens to the `## What this evidence set does not cover` heading — either delete the section and move the one-sentence coverage statement into the intro, or keep the heading and repoint its body at the three surfaces the Scope section still excludes.

## 3. Forced decisions

### 3.1 What check 5's Azure variant reads for OS-disk attestation

**The choice.** Section 4 specifies the Azure evidence as: "list the disks in the node resource group (the cluster resource exposes it as `node_resource_group`), filter to OS disks, and expect `encryption.type` of `EncryptionAtRestWithCustomerKey` and the same set id check 2 shows." That is a per-disk read via the `Microsoft.Compute/disks` API, mirroring check 3. The alternative is a scale-set-profile read via `az vmss show`.

**Why it's forced.** AKS agent pools are backed by Virtual Machine Scale Sets, not standalone VMs. The design's per-disk command only returns rows if each node's OS disk is exposed as a standalone `Microsoft.Compute/disks` resource in the node resource group — and no listed assumption establishes that. A10's evidence is the provider default `os_disk_type = "Managed"`, which fixes the disk *type* but says nothing about whether the instance's OS disk is addressable as a top-level disk resource. A21's evidence is check 3's existing `az disk show`, which operates on CSI-provisioned *data* disks resolved from `kubectl get pv` — a different producer entirely. If AKS's scale-set OS disks are not enumerable that way, check 5's Azure variant returns an empty table and the design's central promise ("a single key reference should appear in every check") has no Azure evidence behind it. This cannot be settled at design time: no Azure cluster has ever been deployed, and no artifact in this repository answers it.

**The options.**
- **(a) Keep the per-disk command and verify empirically at the first Azure apply.** Symmetric with check 3 and with the GCP variant, and the strongest evidence if it works. Cost: the runbook ships with a command that may return nothing, and the discovery happens in front of the first auditor rather than in review. Requires an explicit follow-up to re-derive the command if it comes back empty.
- **(b) Read the pool's OS-disk profile instead** — `az vmss show -g <node RG> -n <vmss> --query 'virtualMachineProfile.storageProfile.osDisk.managedDisk.diskEncryptionSet.id'`, per agent pool. Certain to return a value and attests the same DES id, but it is a *declared-configuration* read at the scale-set level rather than a per-instance attestation — weaker than check 3's evidence, and asymmetric with the GCP variant, which stays per-disk. It also needs the VMSS names enumerated (`az vmss list -g <node RG>`), a step the design does not currently include.
- **(c) Ship both**, with the per-disk read as the primary and the scale-set profile read as the fallback the runbook tells the auditor to use when the first returns nothing. Costs a longer check 5 and an explicit statement of which one an auditor should treat as authoritative.

## 4. Previously addressed

n/a — first round for this spec basename. (`docs/criticalreviews/` contains reviews for the predecessor `2026-08-24-at-rest-encryption-design`, a different basename; nothing for this spec.)

## 5. Recommendation

🛑 **Surface forced decisions to user** — §3 is non-empty. §3.1 needs a decision on what check 5's Azure variant reads before the runbook amendment can be written, and §2.1's runbook edit-site gap should be folded into the same section-4 revision.
