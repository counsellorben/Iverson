# Critical Design Review: 2026-09-08-at-rest-encryption-node-disks-and-allowlist-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-09-08-at-rest-encryption-node-disks-and-allowlist-design.md`
**Verified Assumptions section:** present

The §0 sweep below was completed against the current spec text before the round-1 review was
opened. Round 1 was then read for the never-re-raise rule and for §4.

## 0. Coverage enumeration

### Sections

| # | Section | Disposition |
|---|---|---|
| S1 | Header — Status / Scope / Driver / Predecessor | ok — `docs/specs/2026-08-24-at-rest-encryption-design.md` exists; `git log --oneline -1` resolves both `4c5823b` ("encrypt aws data volumes with a customer-managed key") and `8638e81` ("close the two deferred follow-ups: IMDSv2 on nodes, key vault network ACL") |
| S2 | Problem — the three exclusions | ok — `modules/cluster-azure/main.tf:152-199` is `azurerm_kubernetes_cluster.this` with no `disk_encryption_set_id`; `modules/cluster-gcp/main.tf:222` and `:261` are the two pool `node_config` blocks, neither with `boot_disk_kms_key`; `charts/qdrant/templates/statefulset.yaml:107-108` is `- name: snapshots` / `emptyDir: {}`; runbook `:136-151` is a manual `kubectl get pvc` enumeration |
| S3 | Problem — "Every chart mounts an `emptyDir` scratch volume" | dropped — literally false (grep of all 13 subcharts: `emptyDir` in redis, authentik, api, worker, jaeger, tei, ollama, qdrant; absent from admin-ui, prometheus, postgres, kafka, starrocks). Fails the literal-wrongness test: the design's mechanism needs *some* chart to write scratch data to the node disk, and the one it names (Qdrant snapshots) is real |
| S4 | Scope — In | ok — two Terraform attributes, a chart under `modules/operators`, runbook edits; all three map to files that exist |
| S5 | Scope — Out, with reasons | ok — each is a scope statement. The only negative claim ("No PVC exists outside `iverson` today") is not a safety argument for a deletion; re-grepped anyway and consistent with R4 |
| S6 | Design §1 — GKE node boot disks | → §1 (A1 fails); edit sites themselves ok — `:201` `general`, `:244` `pools`, key at `:73`, binding at `:84-89`, key-ring location at `:49` |
| S7 | Design §2 — AKS node OS disks | → §2.1 |
| S8 | Design §3 — What it does (the CEL policy) | ok — see R1, R2, R3 |
| S9 | Design §3 — Coverage | ok — see R4; the "no route to a bound volume that skips it" negative claim re-grepped independently, seven producers, all templating a class; generic ephemeral volumes and StatefulSet claim templates both materialise as core/v1 PVC CREATEs |
| S10 | Design §3 — How it is applied | ok — `modules/operators/main.tf:3` pins `kubernetes ~> 2.31`; the same pin is at `aws/main.tf:5`, `azure/main.tf:5`, `gcp/main.tf:5`. Five existing `helm_release` resources in the module (`cloudnative_pg`, `strimzi`, `starrocks_operator`, `cluster_autoscaler`, `aws_load_balancer_controller`) — the spec's "five operators it owns" is exact. The proposed release's `namespace` and `values` references give it implicit dependencies on `kubernetes_namespace.iverson` and all seven `kubernetes_storage_class` resources, so no `depends_on` is needed |
| S11 | Design §3 — Failure mode | ok — nothing downstream consumes a field from the denial |
| S12 | Design §4 — Evidence | ok on carriability — every runbook site the section names exists at the cited line (see A23 in §1 and D6/D7 below). Two candidates dropped: (a) the rewritten coverage sentence still omits check 2, which has an Azure/GCP paragraph at `:90-97` — dropped, the replacement sentence is true as written and loses no evidence; (b) the closing-section instruction gives one body two replacement contents (a coverage sentence *and* the three exclusions) under a heading that says "does not cover" — dropped, editorial placement, all five checks and their expectations survive intact |
| S13 | Design §5 — Greenfield | ok — no tracked state file; `disk_encryption_set_id` is ForceNew per A7 and `boot_disk_kms_key` is not addable per A3, and both land on the first apply. `kubelet_disk_type` is not named in §5 but is covered by the same posture — the cluster it sits on is being created, not mutated |
| S14 | Design §6 — Testing | ok — `terraform -chdir=<root> fmt -check` has no `-recursive`, so it does not reach `modules/`; the spec's claim is worded about the roots and is true as written. `terraform validate` from a root does load and type-check child modules. `.github/workflows/deploy-validate.yml:28-35` and `.gitlab-ci.yml:15-20` are both umbrella-only lint loops, so the one-line addition is the right shape, and both CI path filters (`Iverson.Server/deploy/**` / `Iverson.Server/deploy/**/*`) cover the new chart's location |
| S15 | Verified assumptions table + "Three assumptions changed the design" | → §1 |
| S16 | Known issues, accepted as out of scope | ok — `modules/cluster-aws/main.tf:24-25` does read "the companion Helm chart's Postgres backup retentionPolicy" and no such policy exists in `charts/postgres/templates/cluster.yaml`; correctly flagged as off-path |

### Rules and operands

| # | Rule | Disposition |
|---|---|---|
| R1 | CEL membership — **PVC side** (`object.spec.storageClassName`) | ok — both directions. Under-inclusion: an absent key makes `has()` false → denied, which is the spec's stated intent; an explicit `""` is a present key so `has()` is true and `"" in [...]` is false → denied, also as stated. Over-inclusion: `storageClassName` is immutable post-create, so CREATE-only matching cannot be sidestepped by a later UPDATE |
| R2 | CEL membership — **list side** (the seven rendered names) | ok — this is the operand the spec asserts is clean, so it was tested against the real data on both sides. Terraform emits `iverson-{postgres,starrocks,qdrant,kafka,ollama,tei,prometheus}` (`modules/operators/main.tf:141,151,159,169,177,186,195`); the charts request exactly those seven strings at `values-aws.yaml:10,20,30,50,60,70,117`, `values-azure.yaml:10,20,30,50,60,70,110`, `values-gcp.yaml:11,21,31,51,61,71,111`. No eighth name, no drift, no case or hyphenation mismatch. (The chart-side names are a hand-written copy; the spec's "no hand-written copy" claim is scoped to the Terraform side and is true there.) |
| R3 | Binding scope — `namespaceSelector` on `kubernetes.io/metadata.name: iverson` | ok — `kubernetes_namespace.iverson` (`modules/operators/main.tf:12-19`) declares only the PSA label; `kubernetes.io/metadata.name` is applied by the API server on every write, so a Terraform-managed label map cannot strip it |
| R4 | Eligibility predicate — every PRODUCER of a PVC in `iverson` on a cloud profile | ok — enumerated by grepping all 13 subchart `templates/` dirs for `PersistentVolumeClaim`, `volumeClaimTemplates`, `storageVolumes`, `storageClass`: `prometheus/pvc.yaml:2,7`; `qdrant/statefulset.yaml:114,119`; `ollama/statefulset.yaml:102,107`; `tei/statefulset.yaml:84,89`; `postgres/cluster.yaml:8,10` (CNPG); `kafka/kafka.yaml:50,53` (Strimzi); `starrocks/starrockscluster.yaml:15,17,33,35` (FE meta + BE data). Seven producers, every one templating a class. The producer the spec does not name and that I checked separately — `authentik/templates/database.yaml` — is a CNPG `Database` CR (a logical database inside the existing cluster), which creates no PVC. `redis`, `jaeger`, `api`, `worker`, `admin-ui` declare none |
| R5 | `matchConstraints` — CREATE, core group, `persistentvolumeclaims` | ok — StatefulSet claim templates, the ephemeral-volume controller, and all three operators create ordinary core/v1 PVCs through the same admission path; there is no alternate create route to a bound volume |
| R6 | GKE edit-site rule — which blocks actually produce nodes | ok, with one correction routed to §1. `grep -n node_config modules/cluster-gcp/main.tf` returns exactly two hits, `:222` and `:261`, both inside the two pool resources; `google_container_cluster.this` (`:134-199`) has **no** `node_config` block at all. `remove_default_node_pool = true` at `:153`, `initial_node_count = 1` at `:154`. No `cluster_autoscaling` block and no `enable_autopilot`, so GKE cannot create a third un-keyed pool. See §1 A1 |
| R7 | AKS placement rule — `kubelet_disk_type` and DES coverage of every pool | ok — grep of `modules/cluster-azure/main.tf` for `kubelet_disk_type`, `os_disk_type`, `disk_size`, `local_ssd`, `ephemeral_storage`: zero hits, so A11's "no pool sets it" holds and the declaration is purely additive. Cluster-level `disk_encryption_set_id` covers `default_node_pool` (`:162-171`) and the seven `azurerm_kubernetes_cluster_node_pool.pools` (`:213`), which is every pool in the file |
| R8 | Ephemeral-storage backing — does the disk the design encrypts actually hold `emptyDir`? | ok — the same grep returns no `ephemeral_storage_local_ssd_config`, `local_nvme_ssd_block_config` or `local_ssd_count` in `modules/cluster-gcp/main.tf`, so GKE ephemeral storage stays on the boot disk that `boot_disk_kms_key` covers; on Azure `kubelet_disk_type = "OS"` is what the design declares for the same reason |
| R9 | Helm manifest install order — policy before binding | ok — neither kind is in Helm's `InstallOrder`, so both sort alphabetically and `ValidatingAdmissionPolicy` precedes `ValidatingAdmissionPolicyBinding`; and a binding that names a not-yet-existing policy is accepted rather than rejected, so the ordering is not load-bearing either way |

### Data-flow arrows

| # | Arrow (ends at an operation) | Disposition |
|---|---|---|
| D1 | `kubernetes_storage_class.*.metadata[0].name` → `yamlencode` → `helm_release.values` → `.Values.allowedStorageClasses` → `toJson` → rendered CEL list → **API-server CEL evaluation**. Crosses three serialization boundaries | ok — every parameter the terminal operation needs exists at each hop. `outputs.tf:3-9` already dereferences all seven `metadata[0].name`, proving the attribute path resolves; the names are static strings so they are known at plan time; `toJson` over a list of strings yields a double-quoted JSON array, which is a valid CEL list literal, and DNS-1123 names contain no character `encoding/json` would escape |
| D2 | `google_kms_crypto_key.data_volumes.id` → `node_config.boot_disk_kms_key` → **GCE disk-create**, whose second required input (the `cryptoKeyEncrypterDecrypter` grant) comes from a different resource | dropped — `google_kms_crypto_key_iam_binding.data_volumes` (`:84-90`) is parallel to the node pools rather than upstream of them, but the pools also depend on `google_container_cluster.this`, a multi-minute create, while the binding depends only on the key. Not a design-level break |
| D3 | `azurerm_disk_encryption_set.data_volumes.id` → `azurerm_kubernetes_cluster.disk_encryption_set_id` → **ARM cluster-create**, which needs the DES identity's vault wrap/unwrap grant | dropped — same shape as D2: `azurerm_key_vault_access_policy.des` (`:99-105`) is a sibling of the cluster rather than upstream of it, so Terraform may start both after the DES completes. A single ARM PUT against the vault versus a multi-minute AKS create with node disks provisioned at the end; a timing hazard, not a design defect, and an integration edge case belonging to critical-implementation-review. Cycle question re-derived independently: DES → cluster → `azurerm_role_assignment.aks_data_volumes_des` (`:119-123`) is acyclic |
| D4 | AKS node OS disk → **`az disk list`/`show` in the node RG** → `encryption.type` / `encryption.diskEncryptionSetId` | → §2.1. The consuming operation needs the OS disk to exist as a standalone `Microsoft.Compute/disks` resource. Section 4 and A10 now both say that is unestablished and deferred; section 2 still states it as settled fact |
| D5 | GKE node boot disk → **`gcloud compute disks describe <name> --zone <zone>`** → `diskEncryptionKey.kmsKeyName` | ok on the parameter round 1 forced (zone): `spec.providerID` is `gce://<project>/<zone>/<instance>` and the runbook's AWS variant already parses that field at `:166`. The disk-*name* parameter is sourced as "named after the instance"; round 1 examined this operand explicitly (its D5) and treated the naming as holding, and no new evidence contradicts it, so it is not reopened here |
| D6 | Runbook edit instructions → **the runbook's actual current text** | ok — every site exists as described. `:7-8` is the conventions paragraph and names `<zone>` in check 3's GCP variant as the only substitution; `:12-14` is the coverage paragraph and reads exactly as the spec quotes it, forward reference included; `:136` is `## 4. No volume escaped the set`; `:153` is `## 5. Node root volumes`; `:186` is `## What this evidence set does not cover` and `:188` opens its body. Two related candidates dropped in S12 |
| D7 | New chart path `modules/operators/charts/pvc-storageclass-policy/` → **`helm lint` in two CI files** | ok — no `.gitignore` rule catches it (the `charts/*.tgz` rule at `:74` is scoped to the helm tree and to `.tgz`), both CI path filters cover `Iverson.Server/deploy/**`, and both lint loops are umbrella-only today, so A27 holds. `tfsec Iverson.Server/deploy/terraform/` scans `.tf` only and is unaffected by a chart directory under that tree |
| D8 | Module edits → **`terraform validate`** at each cloud root | ok — `init -backend=false` walks child modules and installs their providers, and `validate` then type-checks `boot_disk_kms_key`, `disk_encryption_set_id`, `kubelet_disk_type` and the new `helm_release` against the pinned schemas (`.github/…:120-127`, `.gitlab-ci.yml:73-75`) |
| D9 | Denied PVC → **StatefulSet pod event / operator reconcile error** | ok — no design dependency beyond visibility |

## 1. Verified-assumptions cross-check

Repo-side citations were re-read against the working tree; vendor-documentation citations are
taken as ground truth per the skill.

- **A1** — **fails, as written.** The cited evidence holds (`modules/cluster-gcp/main.tf:153` is
  `remove_default_node_pool = true`; `google_container_node_pool.general` is at `:201`), but the
  assumption's *subject* does not exist: `grep -n node_config modules/cluster-gcp/main.tf` returns
  exactly two hits, `:222` and `:261`, both inside the pool resources. `google_container_cluster.this`
  (`:134-199`) declares no `node_config` block. So "the GCP cluster's inline `node_config`" — and
  section 1's "its inline `node_config` never produces nodes and is not touched" — name a block that
  is not in the file. What is actually there is `initial_node_count = 1` (`:154`) with
  `remove_default_node_pool = true`, which means GKE provisions one default-pool node with stock GKE
  defaults during cluster creation and the provider deletes the pool immediately after.
  **No §2 promotion:** the design's two edit sites (`:201`, `:244`) are the correct and complete set
  either way, the transient node carries no Iverson workload (it exists only during `terraform apply`,
  before the app chart is installed), and check 5's GCP variant enumerates live nodes. The defect is
  confined to the justification prose. It is recorded here because round 1 reconfirmed A1 by checking
  its two line citations rather than its subject.
- **A2** — reconfirmed. `:201` and `:244` are the only two `google_container_node_pool` resources;
  `local.extra_pools` (`:233-241`) has seven entries.
- **A3, A4** — vendor; ground truth.
- **A5** — reconfirmed on its repo half: `google_kms_crypto_key_iam_binding.data_volumes` at
  `:84-90` names `service-<project-number>@compute-system.iam.gserviceaccount.com` with
  `roles/cloudkms.cryptoKeyEncrypterDecrypter`. Vendor half ground truth.
- **A6** — reconfirmed. Key ring `location = var.region` at `:49`; cluster at `:136`; pools at `:204`
  and `:248`.
- **A7, A9** — vendor; ground truth.
- **A8** — reconfirmed and independently re-derived. `modules/cluster-azure/main.tf:78-105` is the DES
  plus its access policy; `azurerm_role_assignment.aks_data_volumes_des` at `:119-123` is the only
  resource referencing both DES and cluster. DES → cluster → role assignment is acyclic.
- **A10** — reconfirmed **as rewritten**. `os_disk_type` appears nowhere in
  `modules/cluster-azure/main.tf`, so the provider default applies to every pool; the amended text
  now explicitly declines to settle standalone-disk enumerability. See §2.1 — section 2 was not
  brought along.
- **A11** — reconfirmed. `kubelet_disk_type` appears nowhere in `modules/cluster-azure/main.tf`, so
  the declaration is purely additive.
- **A12** — reconfirmed. `:80` and `:154` are both `azurerm_resource_group.this.location`.
- **A13** — reconfirmed. `cluster-aws/variables.tf:13`, `cluster-azure/variables.tf:13`,
  `cluster-gcp/variables.tf:15` all `default = "1.30"`.
- **A14** — reconfirmed. `~> 2.31` at `modules/operators/main.tf:3`, `aws/main.tf:5`,
  `azure/main.tf:5`, `gcp/main.tf:5`. Provider-CHANGELOG half ground truth.
- **A15** — reconfirmed. `modules/operators/main.tf:12-19` declares only the PSA label.
- **A16** — reconfirmed, and widened by one site: `Iverson.Server/deploy/kind/setup.sh:104` ends
  `-n iverson`, and `docs/runbooks/grpc-admin-auth-cutover.md:33` is
  `helm upgrade --install iverson . -f values-<env>.yaml -n iverson --create-namespace`. Those are the
  only two install commands in the repo and both target `iverson`.
- **A17** — vendor; ground truth.
- **A18** — reconfirmed independently (§0 R4): seven producers, all templating a class; nothing under
  `Iverson.LoadTest`.
- **A19** — reconfirmed. `modules/operators/outputs.tf:3-9`.
- **A20** — reconfirmed, exact. `.github/workflows/deploy-validate.yml:120-127`;
  `.gitlab-ci.yml:73-75`.
- **A21** — reconfirmed **as rewritten**. Runbook `:118-125` is the Azure half of check 3, reading
  `encryption.type` and `encryption.diskEncryptionSetId` from `az disk show` over
  `kubectl get pv`-resolved CSI disks; the amended text now says in so many words that those are a
  different producer than node OS disks.
- **A22** — reconfirmed **as rewritten**. Runbook `:127-134` is check 3's GCP variant;
  `:166` is the AWS check-5 command that already parses `spec.providerID`.
- **A23** — reconfirmed, exact, all six sites: `:7-8` conventions, `:12-14` coverage, `:136` check 4,
  `:153` check 5, `:186` closing heading, `:188` its paragraph.
- **A24** — reconfirmed. `.gitignore:51` is `**/docs/specs/`.
- **A25** — vendor; ground truth.
- **A26** — reconfirmed. `modules/operators/main.tf:21-56` is `helm_release.cloudnative_pg` through
  `helm_release.strimzi`, the latter with `set` and `depends_on = [kubernetes_namespace.iverson]`.
- **A27** — reconfirmed, exact. `.github/workflows/deploy-validate.yml:28-35` and
  `.gitlab-ci.yml:15-20` lint only `Iverson.Server/deploy/helm/iverson`.

### Span check — design dependencies no listed assumption covers, as scoped

1. **Section 2's per-disk attestability claim.** Now uncovered by A10 as rewritten. → **§2.1**.
2. **`emptyDir` is actually backed by the disk each change encrypts.** Verified in-round: no
   `ephemeral_storage_local_ssd_config` / `local_nvme_ssd_block_config` / `local_ssd_count` in
   `modules/cluster-gcp/main.tf`, and no `os_disk_type` / `kubelet_disk_type` in
   `modules/cluster-azure/main.tf`. No finding.
3. **`EncryptionAtRestWithCustomerKey` is the value an Azure node OS disk under this DES reports.**
   No listed assumption covers the literal. `azurerm_disk_encryption_set.data_volumes`
   (`modules/cluster-azure/main.tf:78-94`) sets no `encryption_type`, so the disks it wraps report
   whatever the provider default produces — which is the same value check 3's Azure variant would show
   for data disks under the same set. Already inside the empirical verification A10 and section 4
   defer to the first Azure apply; no separate finding.
4. **The GKE default pool that `initial_node_count = 1` provisions during cluster creation.**
   Uncovered by A1/A2 as scoped. Verified in-round from `modules/cluster-gcp/main.tf:153-154`: the
   node is transient, pre-dates the app chart, and is not enumerated by any check. No finding.
5. **No third GKE pool and no VMSS-external AKS pool.** Verified in-round: no `cluster_autoscaling`,
   no `enable_autopilot` (`modules/cluster-gcp/main.tf`); every AKS pool is `default_node_pool` or a
   `azurerm_kubernetes_cluster_node_pool.pools` instance. No finding.
6. **The new chart is committable and inside both CI path filters.** Verified in-round against
   `.gitignore` and both CI trigger blocks. No finding.

## 2. Literal-wrongness findings

### 2.1 Section 2 still asserts the per-disk attestability that the round-1 fix removed from A10 and disclaimed in section 4

**Description.** Round 1's fix narrowed assumption A10 from "Node OS disks are managed disks,
**attestable per disk**" to "Node OS disks are managed disks", and appended: "This fixes the disk
type only. Whether each is enumerable as a standalone disk resource is not settled here and is
deferred to the first Azure apply, per section 4." Section 4 gained the matching disclaimer:
"Nothing in this repository establishes that AKS exposes each node's OS disk as a standalone disk
resource in that group, and no Azure cluster exists to test it against: AKS agent pools are backed
by scale sets, and A10 fixes the disk type only."

Section 2 was not brought along. It still reads:

> The provider defaults `os_disk_type` to Managed, so every node OS disk is a managed disk resource
> in the node resource group that `az disk` can attest per disk.

That sentence states as a settled consequence exactly the proposition A10 now refuses to settle and
section 4 now labels unestablished. It is not a paraphrase gap: it asserts both halves the
amendments removed — "in the node resource group" and "`az disk` can attest per disk" — and it
asserts them as a *derivation* from the `os_disk_type` default, which is the specific inference A10
was rewritten to disown ("This fixes the disk type only").

The consequence is not hypothetical. The user's chosen option on round 1's forced decision was (a) —
ship the per-disk command and verify empirically, **with an explicit follow-up to re-derive it
against the agent pool's scale set if it returns no rows**. That obligation lives in one sentence in
section 4. A plan-writer or runbook-writer working from section 2, which is where the AKS design is
specified, is told the question is closed, and will carry neither the caveat nor the follow-up. The
spec now says two different things about the same fact in two places, and the place that says the
wrong thing is the one that describes the change.

**Evidence.**
- Spec section 2, the sentence beginning "The provider defaults `os_disk_type` to Managed" (design
  §2, third sentence) — unchanged by `334528b`.
- Spec A10 as amended in `334528b`: "…This fixes the disk type only. Whether each is enumerable as a
  standalone disk resource is not settled here and is deferred to the first Azure apply, per section
  4."
- Spec section 4 as amended in `334528b`: "Nothing in this repository establishes that AKS exposes
  each node's OS disk as a standalone disk resource in that group…"
- `git show 334528b` — five edits, none in section 2.
- `modules/cluster-azure/main.tf`: `grep -n 'os_disk_type\|kubelet_disk_type\|disk_size'` returns
  nothing, so the only fact the repo establishes is the provider default `Managed` — which is what
  A10 now says and no more.

**Proposed fix.** Rewrite section 2's sentence so it claims only what A10 supports, and carries the
deferral rather than contradicting it. For example, replace it with: "The provider defaults
`os_disk_type` to Managed, so every node OS disk is a managed disk rather than an ephemeral one.
Whether AKS also exposes each as a standalone disk resource in the node resource group — what check
5's Azure variant reads — is not settled here; section 4 records the deferral and the follow-up if
the command returns no rows." One sentence, and the spec then says one thing about the fact in all
three places.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1 — the runbook's front matter was not listed as an edit site.** Resolved. Section 4
  gained "**The front matter changes with them.**", naming `:12-14` (rewritten to say checks 1, 3 and
  5 all have Azure/GCP equivalents, forward reference dropped) and `:7-8` (stands unchanged). Both
  paragraphs verified to read exactly as the spec quotes them. A23 was extended to six sites and each
  was reconfirmed against the runbook.
- **Round 1 §2.1, second bullet — check 5's GCP variant needed an unsourced `--zone`.** Resolved.
  Section 4 now sources it from `spec.providerID`, and A22 was rewritten to cover the parameter as
  well as the field, citing the runbook's own `:166` where the AWS variant already parses that field.
  The `:7-8` invariant survives.
- **Round 1 §2.1, third point — the design did not say what becomes of the
  `## What this evidence set does not cover` heading.** Resolved as a decision: the heading is kept
  and its body repointed at AKS etcd Secrets, the AWS VPC flow-log group and the Azure Log Analytics
  workspace.
- **Round 1 §3.1 — what check 5's Azure variant reads.** Decided: option (a). Section 4 now ships the
  per-disk command as written, records that nothing in the repo establishes AKS OS-disk enumerability,
  and commits to re-deriving the variant against the agent pool's scale set if it returns no rows.
  A10 and A21 were rescoped to match. §2.1 above is the residue in section 2, not a reopening of the
  decision.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 has one item, §3 is empty. §2.1 is a one-sentence
rewrite in section 2 that brings it into line with the A10/section 4 amendments the user already
approved; the round-1 decision itself is settled and is not reopened. §1's A1 failure needs no design
change (the GKE edit sites are correct and complete) but the justification prose in section 1 and A1
should stop referring to an inline `node_config` block the cluster resource does not have.
