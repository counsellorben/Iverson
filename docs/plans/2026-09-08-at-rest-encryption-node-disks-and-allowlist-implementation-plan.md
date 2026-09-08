# At-rest encryption: node disks and StorageClass allow-list — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-08-at-rest-encryption-node-disks-and-allowlist-design.md` (commit SHA: `51234e6`)

**Goal:** Bring AKS and GKE node disks under the same customer-managed key as their PersistentVolumes, and make the StorageClass allow-list enforced at admission rather than checked by hand.

**Architecture:** Two Terraform attributes per cloud module reuse the data-volume key each cloud already creates, so no new key, grant or binding is introduced. The allow-list becomes a `ValidatingAdmissionPolicy` plus binding, shipped as a local Helm chart inside the operators module and installed with a `helm_release`, because no pinned Kubernetes-provider version can apply both objects without a plan-time cluster dependency. The verification runbook gains the evidence for all of it.

**Tech stack:** Terraform 1.7.5 (CI) / 1.9.8 (local), providers `google ~> 5.30`, `azurerm ~> 3.90`, `kubernetes ~> 2.31`, `helm ~> 2.14`; Helm 3.15.0 (CI); Kubernetes 1.30, where `admissionregistration.k8s.io/v1` is stable.

---

## Global Constraints

Project-wide rules every task must hold to. Values are copied from the spec and from the pins in the repository.

- **Provider pins are not to be changed.** `kubernetes ~> 2.31` is what forces the admission policy through a Helm chart rather than a typed resource. Bumping it does not remove the constraint, because no version ships a binding resource.
- **Both node-disk settings are creation-time only.** Google does not allow `boot_disk_kms_key` on an existing node pool; `disk_encryption_set_id` forces a new AKS cluster. No Azure or GCP cluster exists, so both are set on the first apply. If a cluster is stood up before this lands, stop and revise the spec's Greenfield section first.
- **The allow-list is never hand-copied.** The seven class names reach the policy only by reference to the `kubernetes_storage_class` resources. An eighth class means one more reference in the release, next to the identical reference in `outputs.tf`.
- **`terraform fmt` is a write step, not only a check.** Inserting these attributes changes alignment in the surrounding blocks. CI runs `terraform -chdir=<root> fmt -check` without `-recursive`, which reaches only the root `main.tf` and never the module files every task here edits, so a task that skips the local format leaves the repository unformatted with CI still green.
- **Commit messages:** lowercase, imperative, no Conventional-Commits prefix, matching the existing history in this area (`encrypt gcp data volumes with a customer-managed key`).

## File Structure

**Create**
- `Iverson.Server/deploy/terraform/modules/operators/charts/pvc-storageclass-policy/Chart.yaml` — chart metadata, matching the umbrella subcharts' shape.
- `Iverson.Server/deploy/terraform/modules/operators/charts/pvc-storageclass-policy/values.yaml` — declares `allowedStorageClasses: []`.
- `Iverson.Server/deploy/terraform/modules/operators/charts/pvc-storageclass-policy/templates/policy.yaml` — renders the policy and its binding.

**Modify**
- `Iverson.Server/deploy/terraform/modules/cluster-gcp/main.tf` — one attribute in each of two `node_config` blocks.
- `Iverson.Server/deploy/terraform/modules/cluster-azure/main.tf` — one cluster attribute, one attribute on each of two pool declarations.
- `Iverson.Server/deploy/terraform/modules/operators/main.tf` — one `helm_release`.
- `.github/workflows/deploy-validate.yml` — one lint step.
- `.gitlab-ci.yml` — one lint line.
- `docs/runbooks/at-rest-encryption-verification.md` — four edit regions.

**Test** — none created. This area has no unit-test layer; the gates are `terraform fmt`, `terraform validate`, `helm lint`, and the runbook's own negative test, which needs a live cluster.

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and re-confirmed across two critical-design-review rounds. Trusted as ground truth here and NOT re-verified.

| # | Assumption | Original evidence |
|---|---|---|
| A1 | The GCP cluster resource declares no `node_config`; the two pool resources are the whole edit surface | grep returns only `:222`, `:261`, both inside pools; `:153-154` removes the default pool |
| A2 | `:201` and `:244` are the only two `google_container_node_pool` resources | `local.extra_pools` has seven entries |
| A3 | google `~> 5.30` supports `node_config.boot_disk_kms_key` | provider v5.30.0 docs |
| A4 | The key id format matches what the attribute expects | `projects/…/cryptoKeys/…`, which `google_kms_crypto_key.id` yields |
| A5 | The Compute Engine service agent is the boot-disk principal | GKE CMEK docs; binding at `cluster-gcp/main.tf:84-90` |
| A6 | Key ring, cluster and pools share a region | `:49`, `:136`, `:204`, `:248` all `var.region` |
| A7 | azurerm `~> 3.90` supports cluster-level `disk_encryption_set_id` covering all pools | provider v3.90.0 docs; forces a new resource |
| A8 | The cluster-to-set dependency creates no cycle | DES → cluster → role assignment is acyclic |
| A9 | No pre-creation grant to the cluster identity is required | Microsoft BYOK docs create with a system-assigned identity |
| A10 | Node OS disks are managed disks (disk type only; enumerability deferred) | `os_disk_type` absent, so the `Managed` default applies |
| A11 | No pool sets `kubelet_disk_type`, so the declaration is additive | grep returns nothing in `cluster-azure/main.tf` |
| A12 | The set and the cluster share a region | `:80` and `:154` are both the resource group's location |
| A13 | All three clouds run Kubernetes 1.30, where the policy API is stable | all three `variables.tf` default `"1.30"` |
| A14 | The pinned Kubernetes provider cannot apply the policy in a single-pass apply | `~> 2.31` everywhere; 3.0.0 adds a policy but no binding; the manifest resource needs plan-time API access |
| A15 | The `iverson` namespace is selectable by its immutable name label | `operators/main.tf:12-19` declares only the security label |
| A16 | The chart installs into `iverson` on every profile | every documented install command uses `-n iverson` |
| A17 | CEL `has()` and `in` over a list literal are valid in a validation | Kubernetes ValidatingAdmissionPolicy reference |
| A18 | No PVC producer was added since the first design | seven producers, all templating a class |
| A19 | The seven names are referenceable in the module | `operators/outputs.tf:3-9` already references all seven |
| A20 | CI runs the formatter check and validation per cloud | `deploy-validate.yml:120-127`; `.gitlab-ci.yml:73-75` |
| A21 | The node resource group is exposed, and the disk command reports encryption for the disks check 3 reads | those are data disks resolved from the volumes list, a different producer than OS disks |
| A22 | GCP boot disks expose the field check 3 reads, and the zone is on the node | runbook `:127-134`; `spec.providerID` parsed at `:166` |
| A23 | All six runbook edit sites exist | `:7-8`, `:12-14`, `:136`, `:153`, `:186`, `:188` |
| A24 | `docs/specs` is gitignored | `.gitignore:51` |
| A25 | helm `~> 2.14` installs a chart from a local path | provider v2.14.0 docs: "Chart name to be installed. A path may be used." |
| A26 | The module's existing releases are the shape to copy | `operators/main.tf:21-56` |
| A27 | CI lint does not cover a chart under the Terraform tree | both lint loops name only the umbrella chart |

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | All four files this plan modifies exist at the cited paths | `ls` on the three modules; runbook and both CI files read directly |
| P2 | File path | `modules/operators/charts/` does not exist, so Task 3 creates it with no collision | `ls -d modules/operators/charts` → `No such file or directory` |
| P3 | Reference (sibling set) | Every Terraform address the new code names resolves in the same module: `google_kms_crypto_key.data_volumes`, `azurerm_disk_encryption_set.data_volumes`, the seven `kubernetes_storage_class.*`, `kubernetes_namespace.iverson` | `cluster-gcp/main.tf:73`; `cluster-azure/main.tf:78`; `operators/main.tf:139,148,157,166,175,184,193`; `operators/main.tf:12` |
| P4 | Ordering | The operators module has no release named `pvc_storageclass_policy` | the five existing releases are cloudnative_pg, strimzi, starrocks_operator, cluster_autoscaler, aws_load_balancer_controller |
| P5 | Ordering | Tasks 1–3 touch disjoint files and share no symbol, so any order works | Task 1 `cluster-gcp`, Task 2 `cluster-azure`, Task 3 `operators` + chart + CI; no cross-references |
| P6 | Command | `terraform` and `helm` are available and provider init succeeds | `terraform v1.9.8`, `helm v3.16.4`; `terraform -chdir=gcp init -backend=false` completed against a scratch copy |
| P7 | Command | The exact CI invocations this plan copies are what CI runs | `deploy-validate.yml:120-127` fmt/init/validate; `:28-35` and `.gitlab-ci.yml:15-20` the lint loops |
| P8 | Command | The CI formatter check does NOT reach module files | the job runs `-chdir=<root> fmt -check` with no `-recursive`, which processes only that root directory |
| P9 | Code validity | Every new attribute and the release block are accepted by the pinned providers | edits applied to a scratch copy of the whole tree; `terraform validate` returned `Success!` on all three roots |
| P10 | Code validity | The inserted attributes change alignment, so `fmt` must run in write mode | `fmt -check -recursive` on the scratch copy flagged both module files; clean after `fmt -recursive` |
| P11 | Code validity | The chart lints and renders the intended CEL list literal | `helm lint` → `0 chart(s) failed` (one INFO about a missing icon); `helm template` rendered `in ["iverson-postgres",…,"iverson-prometheus"]` and the joined message |
| P12 | Code validity | Every field name in the policy and binding is real, and `Invalid` is an accepted reason | rendered output piped to `kubeconform -kubernetes-version 1.30.0` → `Valid: 2, Invalid: 0` |
| P13 | Code validity | `Chart.yaml` matches the repo's subchart convention | `charts/prometheus/Chart.yaml` is apiVersion v2, type application, version 0.1.0, appVersion "1.0.0" |
| P14 | Consumer impact | Nothing references the GCP node pool resources, so adding an attribute breaks no consumer | `grep -rn "google_container_node_pool\." --include=*.tf` returned nothing |
| P15 | Consumer impact | The AKS cluster's existing consumers survive the new attribute without a cycle | referenced at `cluster-azure/main.tf:122`, `:216`, `outputs.tf:1,4`; the new dependency is DES → cluster → role assignment |
| P16 | Consumer impact | Adding a release does not disturb the operators module's outputs | `outputs.tf` exposes only `storage_class_names`, untouched |
| P17 | Consumer impact | Nothing outside docs cross-references the runbook's front-matter text | only the predecessor spec and plan mention the runbook, by path |
| P18 | Command | The Azure node resource group is NOT a Terraform output and must be resolved through the cluster | `cluster-azure/outputs.tf` exposes cluster_name, kube_config, node_pool_labels, data_volumes_des_id only; the root adds no more |
| P19 | Command | The Azure resource group name is `<cluster_name>-rg` | `cluster-azure/main.tf:8` names it `"${var.cluster_name}-rg"`; `azure/main.tf:78` builds the same string |
| P20 | Command | The command-line plural resource names are `validatingadmissionpolicies` and `validatingadmissionpolicybindings` | Kubernetes API reference paths under `/apis/admissionregistration.k8s.io/v1/` |
| P21 | Command | The cloud command-line tools are absent locally, so check 5's new commands cannot be run at plan time | `which az gcloud aws` returned nothing; the runbook's existing checks 1, 3 and 5 already share this property |

## Tasks

### Task 1: GKE node boot disks

**Files:**
- Modify: `Iverson.Server/deploy/terraform/modules/cluster-gcp/main.tf`

- [ ] **Step 1: Add the key to both node pools.** Insert `boot_disk_kms_key` into the `node_config` block of `google_container_node_pool.general` (around `:222`) and of `google_container_node_pool.pools` (around `:261`), directly after each block's `service_account` line. After formatting, each reads:

```hcl
  node_config {
    machine_type      = var.general_machine_type
    image_type        = "COS_CONTAINERD"
    service_account   = google_service_account.gke_nodes.email
    boot_disk_kms_key = google_kms_crypto_key.data_volumes.id
```

The `pools` block is identical except `machine_type = each.value.machine_type`. Add nothing else: the key, its binding to the Compute Engine service agent, and the region match all already exist (A4, A5, A6).

- [ ] **Step 2: Format and validate.**
```bash
terraform fmt -recursive Iverson.Server/deploy/terraform
terraform fmt -check -recursive Iverson.Server/deploy/terraform
terraform -chdir=Iverson.Server/deploy/terraform/gcp init -backend=false
terraform -chdir=Iverson.Server/deploy/terraform/gcp validate
```
Expected: the check prints nothing and validation prints `Success! The configuration is valid.` The format step is required, not optional (P8, P10).

- [ ] **Step 3: Commit**
```bash
git add Iverson.Server/deploy/terraform/modules/cluster-gcp/main.tf
git commit -m "encrypt gke node boot disks with the data-volume key"
```

### Task 2: AKS node OS disks

**Files:**
- Modify: `Iverson.Server/deploy/terraform/modules/cluster-azure/main.tf`

- [ ] **Step 1: Put the cluster's OS disks under the disk encryption set.** Add one attribute to `azurerm_kubernetes_cluster.this` (`:152`), after `sku_tier`. After formatting the block head reads:

```hcl
resource "azurerm_kubernetes_cluster" "this" {
  name                   = var.cluster_name
  location               = azurerm_resource_group.this.location
  resource_group_name    = azurerm_resource_group.this.name
  dns_prefix             = var.cluster_name
  kubernetes_version     = var.kubernetes_version
  sku_tier               = "Standard"
  disk_encryption_set_id = azurerm_disk_encryption_set.data_volumes.id
```

This is cluster-level and covers every agent pool's OS disk, so the pool resources need nothing for encryption. Do not add `enable_host_encryption`: the spec excludes it, because it protects the temp disk with a platform key and does not advance the customer-key control.

- [ ] **Step 2: Pin kubelet's disk placement on both pool declarations.** Add `kubelet_disk_type = "OS"` after `vm_size` in the cluster's `default_node_pool` block and in `azurerm_kubernetes_cluster_node_pool.pools` (`:213`). This is what keeps `emptyDir` volumes, the container runtime root and kubelet ephemeral storage on the OS disk the previous step encrypts, rather than the VM temp disk. After formatting:

```hcl
  default_node_pool {
    name              = "general"
    vm_size           = var.general_vm_size
    kubelet_disk_type = "OS"
```

```hcl
resource "azurerm_kubernetes_cluster_node_pool" "pools" {
  for_each              = local.extra_pools
  name                  = each.key
  kubernetes_cluster_id = azurerm_kubernetes_cluster.this.id
  vm_size               = each.value.vm_size
  kubelet_disk_type     = "OS"
```

- [ ] **Step 3: Format and validate.**
```bash
terraform fmt -recursive Iverson.Server/deploy/terraform
terraform fmt -check -recursive Iverson.Server/deploy/terraform
terraform -chdir=Iverson.Server/deploy/terraform/azure init -backend=false
terraform -chdir=Iverson.Server/deploy/terraform/azure validate
```
Expected: `Success! The configuration is valid.`

- [ ] **Step 4: Commit**
```bash
git add Iverson.Server/deploy/terraform/modules/cluster-azure/main.tf
git commit -m "encrypt aks node os disks with the data-volume disk encryption set"
```

### Task 3: The StorageClass admission gate

**Files:**
- Create: `Iverson.Server/deploy/terraform/modules/operators/charts/pvc-storageclass-policy/Chart.yaml`
- Create: `Iverson.Server/deploy/terraform/modules/operators/charts/pvc-storageclass-policy/values.yaml`
- Create: `Iverson.Server/deploy/terraform/modules/operators/charts/pvc-storageclass-policy/templates/policy.yaml`
- Modify: `Iverson.Server/deploy/terraform/modules/operators/main.tf`
- Modify: `.github/workflows/deploy-validate.yml`
- Modify: `.gitlab-ci.yml`

**Interfaces:**
- Consumes: the seven `kubernetes_storage_class` resources and `kubernetes_namespace.iverson`, all pre-existing in this module (P3).
- Produces: nothing later tasks depend on. Task 4 documents this task's output but does not import from it.

- [ ] **Step 1: Create the chart metadata and values.**

`Chart.yaml`:
```yaml
apiVersion: v2
name: pvc-storageclass-policy
description: Denies PersistentVolumeClaims outside the encrypted StorageClass allow-list
type: application
version: 0.1.0
appVersion: "1.0.0"
```

`values.yaml`:
```yaml
# Populated by the helm_release in modules/operators/main.tf from the
# kubernetes_storage_class resources. Empty here so the chart never carries a
# hand-written copy of the class names.
allowedStorageClasses: []
```

- [ ] **Step 2: Create the policy template.** `templates/policy.yaml`:

```yaml
apiVersion: admissionregistration.k8s.io/v1
kind: ValidatingAdmissionPolicy
metadata:
  name: iverson-pvc-storageclass
spec:
  failurePolicy: Fail
  matchConstraints:
    resourceRules:
      - apiGroups: [""]
        apiVersions: ["v1"]
        operations: ["CREATE"]
        resources: ["persistentvolumeclaims"]
  validations:
    - expression: >-
        has(object.spec.storageClassName) &&
        object.spec.storageClassName in {{ .Values.allowedStorageClasses | toJson }}
      reason: Invalid
      messageExpression: >-
        "PersistentVolumeClaim " + object.metadata.name + " requests StorageClass " +
        (has(object.spec.storageClassName) ? object.spec.storageClassName : "<none>") +
        "; allowed: {{ .Values.allowedStorageClasses | join ", " }}"
---
apiVersion: admissionregistration.k8s.io/v1
kind: ValidatingAdmissionPolicyBinding
metadata:
  name: iverson-pvc-storageclass
spec:
  policyName: iverson-pvc-storageclass
  validationActions: [Deny]
  matchResources:
    namespaceSelector:
      matchLabels:
        kubernetes.io/metadata.name: {{ .Release.Namespace }}
```

`toJson` on a list of strings yields a valid CEL list literal; a claim with no class fails `has()`, and an explicit empty string is not in the list, so both are denied (P11, P12).

- [ ] **Step 3: Install it from the operators module.** Append to `modules/operators/main.tf`:

```hcl
# The Kubernetes provider cannot apply this pair: 2.x has no policy resource,
# 3.x adds a policy but no binding, and kubernetes_manifest needs API access at
# plan time, which would break the single-pass apply every cloud root relies on.
# A helm_release renders at apply, so the single pass survives.
resource "helm_release" "pvc_storageclass_policy" {
  name      = "pvc-storageclass-policy"
  chart     = "${path.module}/charts/pvc-storageclass-policy"
  namespace = kubernetes_namespace.iverson.metadata[0].name
  values = [yamlencode({
    allowedStorageClasses = [
      kubernetes_storage_class.postgres.metadata[0].name,
      kubernetes_storage_class.starrocks.metadata[0].name,
      kubernetes_storage_class.qdrant.metadata[0].name,
      kubernetes_storage_class.kafka.metadata[0].name,
      kubernetes_storage_class.ollama.metadata[0].name,
      kubernetes_storage_class.tei.metadata[0].name,
      kubernetes_storage_class.prometheus.metadata[0].name,
    ]
  })]
}
```

- [ ] **Step 4: Add the chart to both lint jobs.** Without this the policy YAML has no automated check at all; the existing loops cover only the umbrella chart (A27).

In `.github/workflows/deploy-validate.yml`, after the existing `helm lint against all 5 values overlays` step in the `helm-lint` job:
```yaml
      - name: helm lint the PVC StorageClass admission-policy chart
        run: helm lint Iverson.Server/deploy/terraform/modules/operators/charts/pvc-storageclass-policy
```

In `.gitlab-ci.yml`, as the last line of the `helm-lint` job's `script`:
```yaml
    - helm lint Iverson.Server/deploy/terraform/modules/operators/charts/pvc-storageclass-policy
```

- [ ] **Step 5: Lint and validate.**
```bash
helm lint Iverson.Server/deploy/terraform/modules/operators/charts/pvc-storageclass-policy
helm template pvc-storageclass-policy Iverson.Server/deploy/terraform/modules/operators/charts/pvc-storageclass-policy \
  --namespace iverson --set 'allowedStorageClasses={iverson-postgres,iverson-prometheus}'
terraform fmt -recursive Iverson.Server/deploy/terraform
terraform fmt -check -recursive Iverson.Server/deploy/terraform
terraform -chdir=Iverson.Server/deploy/terraform/aws init -backend=false
terraform -chdir=Iverson.Server/deploy/terraform/aws validate
```
Expected: lint reports `0 chart(s) failed` (an INFO about a missing icon is normal and not a failure); the template renders `in ["iverson-postgres","iverson-prometheus"]` and a binding selecting `kubernetes.io/metadata.name: iverson`; validation prints `Success!`. Any cloud root exercises the operators module; `aws` is the cheapest.

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/deploy/terraform/modules/operators/charts/pvc-storageclass-policy \
        Iverson.Server/deploy/terraform/modules/operators/main.tf \
        .github/workflows/deploy-validate.yml .gitlab-ci.yml
git commit -m "deny pvcs outside the encrypted storageclass allow-list at admission"
```

### Task 4: Runbook amendments

Lands last so the runbook never documents evidence for changes not yet on the branch. It imports nothing from Tasks 1–3.

**Files:**
- Modify: `docs/runbooks/at-rest-encryption-verification.md`

- [ ] **Step 1: Rewrite the coverage paragraph** (`:12-14`). Replace the three lines beginning "Commands are written against AWS" with:

```
Commands are written against AWS, the primary target of this control's Terraform. Checks 1, 3
and 5 each have an Azure/GCP equivalent, noted inline.
```

The forward reference to the closing note is dropped, because that note no longer says check 5 is AWS-only. Leave the conventions paragraph at `:7-8` unchanged: Step 3 sources the GCP zone from the node itself, so its one-substitution claim stays true.

- [ ] **Step 2: Add enforcement evidence and a negative test to check 4.** Append to the end of section `## 4. No volume escaped the set`:

````
The allow-list is enforced, not only enumerated. Show the policy and its binding:

```bash
kubectl get validatingadmissionpolicies iverson-pvc-storageclass -o yaml
kubectl get validatingadmissionpolicybindings iverson-pvc-storageclass -o yaml
```

Expected: the policy's `validations[0].expression` names the same seven classes check 2 lists,
and the binding carries `validationActions: [Deny]` with a `namespaceSelector` matching
`kubernetes.io/metadata.name: iverson`.

Then confirm it actually denies. A server-side dry run passes through admission without
persisting anything, so nothing is created by this check:

```bash
kubectl apply -n iverson --dry-run=server -f - <<'EOF'
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: allowlist-negative-test
spec:
  accessModes: ["ReadWriteOnce"]
  storageClassName: standard
  resources:
    requests:
      storage: 1Gi
EOF
```

Expected: the command FAILS, naming `iverson-pvc-storageclass` in the denial. Success is the
failure case here — it means the gate is absent, or bound to some other namespace.
````

- [ ] **Step 3: Give check 5 its Azure and GCP variants.** Append to the end of section `## 5. Node root volumes`, before the closing section:

````
**Azure equivalent.** AKS node OS disks live in the cluster's node resource group, which is
generated by the AKS resource provider and is not a Terraform output — resolve it through the
cluster:

```bash
CLUSTER=$(terraform -chdir=Iverson.Server/deploy/terraform/azure output -raw cluster_name)
NODE_RG=$(az aks show --name "$CLUSTER" --resource-group "${CLUSTER}-rg" \
  --query nodeResourceGroup -o tsv)

az disk list --resource-group "$NODE_RG" \
  --query '[].{name:name,encryptionType:encryption.type,diskEncryptionSetId:encryption.diskEncryptionSetId}' \
  -o table
```

Expected: every row shows `EncryptionAtRestWithCustomerKey` and the same disk encryption set id
check 2 shows. That group holds both the scale-set OS disks this check exists for and the
CSI-provisioned data disks check 3 reads; both are under the same set, so a single expectation
covers the listing. **If this returns no rows, the check is not satisfied and must be
re-derived against the agent pool's scale set** — see the design's section 4, which ships this
command unverified by deliberate choice, because no Azure cluster existed when it was written.

**GCP equivalent.** A node's boot disk carries the instance's name and lives in the instance's
zone; `spec.providerID` supplies both, in the form `gce://<project>/<zone>/<instance>`:

```bash
kubectl get nodes -o jsonpath='{range .items[*]}{.spec.providerID}{"\n"}{end}' \
  | sed 's#^gce://##' \
  | while IFS=/ read -r project zone instance; do
      gcloud compute disks describe "$instance" --zone "$zone" \
        --format='value(name,diskEncryptionKey.kmsKeyName)'
    done
```

Expected: every line names a disk and the same key check 3 shows.
````

- [ ] **Step 4: Repoint the closing section** (`:186-188`). Keep the `## What this evidence set does not cover` heading. Replace its body with:

```
Node disks are now under the same customer-managed key as PersistentVolumes on all three
clouds, so one key reference should appear in every check above.

Three surfaces remain outside this control, by decision rather than oversight: AKS etcd
Secrets, which stay on a platform-managed key because bringing them under a customer key needs
a Key Vault network posture this deployment deliberately does not take; the AWS VPC flow-log
CloudWatch group; and the Azure Log Analytics workspace. The latter two hold logs, not data.
An auditor asking for evidence on any of the three should be pointed here.
```

- [ ] **Step 5: Commit**
```bash
git add docs/runbooks/at-rest-encryption-verification.md
git commit -m "extend the at-rest verification runbook to node disks and the admission gate"
```

## Tasks NOT in this plan

Inherited verbatim from the spec's Scope section. A new spec → plan cycle is required to add any of these.

- **AKS host encryption.** Encrypts the VM temp disk and cache with a platform key. The `emptyDir` data that motivates this design lives on the OS disk, which the Disk Encryption Set covers, so host encryption does not advance the customer-key control.
- **AKS etcd Secrets on a customer key.** Needs a Key Vault network posture the first design argued against (AKS KMS is not an Azure trusted service). Its own design.
- **AWS VPC flow-log group, Azure Log Analytics workspace.** Logs, not data. Still an accepted follow-up.
- **A separate node-disk key per cloud.** Three more keys and grants, and it breaks the one-key-per-cloud evidence story, for no stated requirement.
- **Policy coverage outside the `iverson` namespace.** No PVC exists outside it today; the control's documented boundary is that namespace.

## Known issues inherited from spec

These exist in the implementation by design, accepted by the user during brainstorming.

**AKS etcd Secrets, the AWS flow-log group and the Azure Log Analytics workspace stay on platform keys.** Recorded in the Scope section with reasons. Ben's call, 2026-09-08.

**No datastore backups.** Unchanged from the first design. Becomes load-bearing the moment production holds data. Separate project.

**A stale comment.** `modules/cluster-aws/main.tf:25` refers to "the companion Helm chart's Postgres backup retentionPolicy". No such policy exists in the chart. Not on this design's path; noted so nobody reads it as evidence that backups exist.
