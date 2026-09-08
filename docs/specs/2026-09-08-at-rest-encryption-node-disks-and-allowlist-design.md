# At-rest encryption: AKS and GKE node disks, and a StorageClass admission gate

**Status:** implemented and merged to local main 2026-09-08 (commits `9869c64`..`11d4a8b`); NOT pushed to origin. Section 4's Azure check-5 command ships deliberately unverified; the re-derivation obligation it names is still open until a real Azure apply.
**Scope:** cloud production only (Azure / GCP Terraform, plus one cluster policy applied on all three clouds)
**Driver:** compliance — closes three items the first at-rest design deliberately excluded
**Predecessor:** `docs/specs/2026-08-24-at-rest-encryption-design.md`, shipped `4c5823b`..`8638e81`

## Problem

The first design put every PersistentVolume on all three clouds, and the EKS node root
volumes, under a customer-managed key per cloud. It recorded three exclusions:

1. AKS node OS disks stay on a platform-managed key. No `disk_encryption_set_id` on the
   cluster (`modules/cluster-azure/main.tf:152-200`).
2. GKE node boot disks stay on a platform-managed key. No `boot_disk_kms_key` in either
   node pool's `node_config` (`modules/cluster-gcp/main.tf:222`, `:261`).
3. Nothing enforces the StorageClass allow-list. Runbook check 4 is a manual enumeration.

Items 1 and 2 matter because node disks hold data that never touches a PersistentVolume.
Every chart mounts an `emptyDir` scratch volume, and Qdrant's `snapshots` directory is one
of them (`charts/qdrant/templates/statefulset.yaml:107-108`): a Qdrant snapshot is a full
copy of a collection. On AWS that lands on the encrypted root volume. On Azure and GCP it
lands on a platform-key disk, so the runbook's "one key per cloud across every check"
claim is true on one cloud out of three.

Item 3 matters because the first design found a PVC outside the controlled set in all
three cloud profiles, and the only thing stopping it happening again is a runbook step.

## Scope

**In.** The two Terraform attributes; a ValidatingAdmissionPolicy and binding for PVCs in
the `iverson` namespace, applied by the operators module; the runbook amendments that
give an auditor evidence for all of it.

**Out, with reasons.**

- **AKS host encryption.** Encrypts the VM temp disk and cache with a platform key. The
  `emptyDir` data that motivates this design lives on the OS disk, which the Disk
  Encryption Set covers, so host encryption does not advance the customer-key control.
- **AKS etcd Secrets on a customer key.** Needs a Key Vault network posture the first
  design argued against (AKS KMS is not an Azure trusted service). Its own design.
- **AWS VPC flow-log group, Azure Log Analytics workspace.** Logs, not data. Still an
  accepted follow-up.
- **A separate node-disk key per cloud.** Three more keys and grants, and it breaks the
  one-key-per-cloud evidence story, for no stated requirement.
- **Policy coverage outside the `iverson` namespace.** No PVC exists outside it today; the
  control's documented boundary is that namespace.

## Design

### 1. GKE node boot disks

Both node pool resources gain one attribute in `node_config`:

```hcl
boot_disk_kms_key = google_kms_crypto_key.data_volumes.id
```

The edit sites are `google_container_node_pool.general` (`modules/cluster-gcp/main.tf:201`)
and `google_container_node_pool.pools` (`:244`). The cluster resource declares no
`node_config` of its own. It pairs `initial_node_count = 1` with
`remove_default_node_pool = true` (`:153-154`), so GKE provisions one stock default-pool
node during `terraform apply` and the provider deletes that pool immediately after. It
carries no workload and is not an edit site.

No new key and no new binding. The key already exists for the persistent disks
(`modules/cluster-gcp/main.tf:73`), and its binding grants the Compute Engine service
agent `roles/cloudkms.cryptoKeyEncrypterDecrypter` (`:84-89`), which Google names as the
principal for boot disks too. The key ring is in `var.region` (`:49`), the same location as
the regional cluster and both pools, which Google requires.

Google states the setting cannot be added to an existing node pool. Section 5 covers why
that does not matter.

### 2. AKS node OS disks

The cluster resource gains one attribute:

```hcl
disk_encryption_set_id = azurerm_disk_encryption_set.data_volumes.id
```

This is cluster-level and applies to the OS disks of every agent pool. The provider
defaults `os_disk_type` to Managed, so every node OS disk is a managed disk rather than an
ephemeral one. Whether AKS also exposes each as a standalone disk resource in the node
resource group — what check 5's Azure variant reads — is not settled here; section 4
records the deferral and the follow-up if the command returns no rows.

The pools gain one declared attribute, on `default_node_pool` and on
`azurerm_kubernetes_cluster_node_pool.pools`:

```hcl
kubelet_disk_type = "OS"
```

This is what puts `emptyDir` volumes, the container runtime root and kubelet ephemeral
storage on the OS disk rather than the VM temp disk, which the Disk Encryption Set does
not cover. The AKS API defines exactly these two placements and states no default, and no
Microsoft document verification could reach states one either, so the design declares the
placement instead of relying on it.

Nothing else changes. The Disk Encryption Set already exists before the cluster with its
Key Vault wrap/unwrap access policy on its own identity (`modules/cluster-azure/main.tf:78-105`).
Microsoft's own example creates the cluster with a system-assigned identity, the set id,
and no prior grant, so the existing post-creation Reader grant for the cluster identity
(`:119-123`) stays exactly as it is: it is what the CSI driver needs for data disks. Adding
a cluster-to-set dependency creates no cycle, because nothing on the set's side references
the cluster.

The provider marks the attribute as forcing a new cluster. Section 5 covers why that is
acceptable now.

### 3. The StorageClass admission gate

**What it does.** A `ValidatingAdmissionPolicy` matching CREATE on
`persistentvolumeclaims` in the core API group, and a `ValidatingAdmissionPolicyBinding`
with `validationActions: [Deny]` whose `matchResources.namespaceSelector` selects the
`iverson` namespace by its immutable `kubernetes.io/metadata.name` label. `failurePolicy`
is left at its default, `Fail`.

The policy has one validation:

```cel
has(object.spec.storageClassName) && object.spec.storageClassName in ["iverson-postgres", "iverson-starrocks", "iverson-qdrant", "iverson-kafka", "iverson-ollama", "iverson-tei", "iverson-prometheus"]
```

The list literal is rendered by the chart from its values, as section "How it is applied"
describes; the names above are what it renders to today.

A claim with no class fails, because an absent class falls through to the cluster default,
which runbook check 4 already counts as an escape. A claim with an explicit empty string
fails too, since `""` is not in the list. The `messageExpression` names the claim, the
class it asked for, and the allowed set.

**Coverage.** Every in-scope volume is a PVC object passing admission: the four the charts
declare directly (Qdrant, Ollama, TEI, Prometheus) and the three operators create from
custom resources (CloudNativePG, Strimzi, StarRocks). StatefulSet claim templates become
PVCs through the same path. There is no route to a bound volume that skips it.

**How it is applied.** Not through the Kubernetes provider. The operators module pins that
provider to the 2.x series (`~> 2.31`), which has no policy resource; the 3.x series adds
a policy resource but no binding resource; and the generic manifest resource requires API
access at plan time, which would break the single-pass `terraform apply` every cloud root
relies on. Instead, a local chart inside the operators module is installed with a
`helm_release`, the same mechanism the module already uses for the five operators it owns.
A Helm release renders at apply, so the single pass survives.

The chart lives at `modules/operators/charts/pvc-storageclass-policy/` and has three
files: `Chart.yaml`, `values.yaml` declaring `allowedStorageClasses: []`, and one template
rendering the policy and the binding. The template writes the list into the CEL expression
with `toJson`, which yields a valid CEL list literal.

The release passes the seven names in from the StorageClass resources:

```hcl
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

The list is the same seven references `outputs.tf` already holds. There is no hand-written
copy of the names. Adding an eighth class means adding one reference here, next to the
output that must be updated for the same reason.

The policy and binding are cluster-scoped; the release namespace only holds Helm's release
record. Cloud-only follows from where it lives: Terraform never targets kind or the laptop
profile, whose PVCs use `standard`.

**Failure mode.** A rejected PVC surfaces as an admission error on whatever tried to create
it: a Pending StatefulSet pod with the denial in its events, or an operator reconcile
error. Loud and reversible, and reachable only with a class outside the seven.

### 4. Evidence

Amendments to `docs/runbooks/at-rest-encryption-verification.md`.

**Check 4 gains enforcement evidence and a negative test.** After the existing PVC
enumeration: show the policy and binding, with the allowed names visible in the
expression; then submit a PVC with `storageClassName: standard` in the `iverson` namespace
using `kubectl apply --dry-run=server`. Server-side dry run passes through admission
without persisting anything, so the denial is the success case and nothing is created.

**Check 5 gains Azure and GCP variants** and stops being AWS-only. Azure: list the disks in
the node resource group (the cluster resource exposes it as `node_resource_group`), filter
to OS disks, and expect `encryption.type` of `EncryptionAtRestWithCustomerKey` and the
same set id check 2 shows. Nothing in this repository establishes that AKS exposes each
node's OS disk as a standalone disk resource in that group, and no Azure cluster exists to
test it against: AKS agent pools are backed by scale sets, and A10 fixes the disk type
only. The command ships as written and is verified at the first Azure apply. If it returns
no rows, check 5's Azure variant must be re-derived against the agent pool's scale set
before the runbook is handed to an auditor. GCP: for each node, describe its boot disk,
which is named after the instance and lives in the zone that node's `spec.providerID`
carries, and expect the same `diskEncryptionKey.kmsKeyName` check 3 already reads.

**The front matter changes with them.** Two statements at the top of the runbook are edit
sites, because the amendments above falsify one and bear on the other. The coverage
paragraph says checks 1 and 3 have Azure and GCP equivalents while check 5 is AWS-only,
and forwards the reader to the closing note these amendments replace; it is rewritten to
say that checks 1, 3 and 5 all have Azure and GCP equivalents, with the forward reference
dropped. The conventions paragraph says every command is runnable as written except one
bracketed `<zone>` in check 3; sourcing check 5's GCP zone from `spec.providerID` keeps
that sentence true, so it stands unchanged.

**The closing paragraph** "AKS and GKE node OS disks are not covered by this runbook, and
not by design" is replaced by one sentence stating that node disks are under the same key
on all three clouds, so a single key reference should appear in every check. Its heading,
`## What this evidence set does not cover`, is kept, and its body is repointed at the three
surfaces the Scope section still excludes: AKS etcd Secrets, the AWS VPC flow-log group and
the Azure Log Analytics workspace.

### 5. Greenfield

Same posture as the first design. No Azure or GCP cluster exists. Both node-disk settings
are creation-time only (Google: not addable to an existing pool; Azure: forces a new
cluster), and both are set on the first `terraform apply`. If a cloud cluster is stood up
before this lands, this section must be rewritten: the AKS change becomes a cluster
rebuild and the GKE change becomes node pool replacement.

### 6. Testing

- `terraform fmt -check` and `terraform validate` on the three cloud roots, which the
  existing `terraform-validate` jobs in `.github/workflows/deploy-validate.yml` and
  `.gitlab-ci.yml` run.
- `helm lint` on the new local chart, added as one line to the existing `helm-lint` job in
  both CI files. Without it the policy YAML has no automated check at all; the current
  lint loop only covers the umbrella chart.
- The policy's behaviour is exercised by check 4's negative test, which needs a live
  cluster and is not automated, matching how every other piece of the control's evidence
  is handled.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| A1 | The GCP cluster resource declares no `node_config`, so the two pool resources are the whole edit surface | `grep -n node_config modules/cluster-gcp/main.tf` returns only `:222` and `:261`, both inside the pool resources; `google_container_cluster.this` (`:134-199`) has none. `:153-154` pairs `remove_default_node_pool = true` with `initial_node_count = 1`, whose transient node is deleted along with the default pool |
| A2 | Two node pool resources cover every GKE node | `:201` `general`, `:244` `pools` (`for_each` over seven); no other `google_container_node_pool` |
| A3 | google `~> 5.30` supports `node_config.boot_disk_kms_key` | provider v5.30.0 `container_cluster` docs: "The Customer Managed Encryption Key used to encrypt the boot disk attached to each node in the node pool" |
| A4 | The key id is in the format the attribute expects | same docs: `projects/…/locations/…/keyRings/…/cryptoKeys/…`, which is what `google_kms_crypto_key.id` yields |
| A5 | The Compute Engine service agent is the boot-disk principal | GKE CMEK docs: "Grant … roles/cloudkms.cryptoKeyEncrypterDecrypter … to the Compute Engine service agent"; binding at `modules/cluster-gcp/main.tf:84-89` |
| A6 | Key ring, cluster and pools share a region | `:49` key ring `location = var.region`; `:136`, `:204`, `:248` all `var.region`; GKE docs: "key ring must have a location that matches the location of your GKE cluster" |
| A7 | azurerm `~> 3.90` supports cluster-level `disk_encryption_set_id` covering all pools | provider v3.90.0 docs: "The ID of the Disk Encryption Set which should be used for the Nodes and Volumes … Changing this forces a new resource" |
| A8 | Cluster-to-set dependency creates no cycle | `modules/cluster-azure/main.tf:78-105`: the set depends on vault, key and access policy only; the sole resource referencing both is `azurerm_role_assignment.aks_data_volumes_des` at `:119` |
| A9 | No pre-creation grant to the cluster identity is required | Microsoft BYOK docs: the create example passes `--node-osdisk-diskencryptionset-id` with default (system-assigned) identity; the Reader requirement is stated under data disks |
| A10 | Node OS disks are managed disks | azurerm docs: `os_disk_type` "Defaults to `Managed`"; no pool in `modules/cluster-azure/main.tf` sets it. This fixes the disk type only. Whether each is enumerable as a standalone disk resource is not settled here and is deferred to the first Azure apply, per section 4 |
| A11 | **Sharpened.** `emptyDir` lands on the OS disk | no pool sets `kubelet_disk_type`; azurerm docs: "Possible values are `OS` and `Temporary`", no default; AKS API `KubeletDiskType`: "Determines the placement of emptyDir volumes, container runtime data root, and Kubelet ephemeral storage", no default. Neither the CLI reference nor the docs source yielded a default, so section 2 declares `OS` |
| A12 | Set and cluster share a region | `:80` and `:154` both `azurerm_resource_group.this.location`; Microsoft: "the DiskEncryptionSet is located in the same region as your AKS cluster" |
| A13 | All three clouds run Kubernetes ≥ 1.30, where the policy API is v1 | `modules/cluster-{aws,azure,gcp}/variables.tf` default `"1.30"`; kubernetes.io: "Stable since Kubernetes v1.30" |
| A14 | **Falsified.** The pinned Kubernetes provider can apply the policy in a single-pass apply | `~> 2.31` in `modules/operators/main.tf:3` and all three roots; provider CHANGELOG: "Add support for ValidatingAdmissionPolicy" under 3.0.0; repo tree has `validating_admission_policy.md` and no binding resource; `manifest.md`: "requires API access during planning time … cannot be created in the same apply operation" |
| A15 | The `iverson` namespace is selectable by name label | `modules/operators/main.tf:12-19` creates it; kubernetes.io: "control plane sets an immutable label `kubernetes.io/metadata.name` on all namespaces" |
| A16 | The chart installs into `iverson` on every profile | `deploy/kind/setup.sh:104` and every documented install command use `-n iverson`; operator CRs inherit it |
| A17 | CEL `has()` and `in` over a list literal are valid in a validation | kubernetes.io ValidatingAdmissionPolicy page (CEL standard library) |
| A18 | **Dependents.** No PVC producer added since the first design | fresh grep of all chart templates: the same seven files as the first design's A16; nothing under `Iverson.LoadTest` |
| A19 | The seven names are referenceable in the module | `modules/operators/outputs.tf` already references all seven `metadata[0].name` |
| A20 | CI runs `terraform fmt -check` and `validate` per cloud | `.github/workflows/deploy-validate.yml:120-127`; `.gitlab-ci.yml:73-75` |
| A21 | The node resource group is exposed, and `az disk` reports encryption for the disks check 3 reads | azurerm docs: `node_resource_group` "The auto-generated Resource Group which contains the resources for this Managed Kubernetes Cluster"; runbook check 3 (`:118-125`) reads `encryption.type` and `encryption.diskEncryptionSetId` from `az disk show`, on CSI-provisioned data disks resolved from `kubectl get pv`. Those are a different producer than node OS disks; see A10 and section 4 |
| A22 | GCP boot disks expose the same field check 3 reads, and the zone that command requires is on the node | boot disks are persistent disks; check 3's `gcloud compute disks describe … diskEncryptionKey.kmsKeyName` applies (runbook `:127-134`). The zone comes from `spec.providerID`, shaped `gce://<project>/<zone>/<instance>`, the same field check 5's AWS variant already parses at `:166` |
| A23 | The runbook sites the design edits exist | `docs/runbooks/at-rest-encryption-verification.md:7-8` conventions paragraph, `:12-14` coverage paragraph, `:136` check 4, `:153` check 5, `:186` closing heading and `:188` its paragraph |
| A24 | `docs/specs` needs a forced add | `.gitignore:51` `**/docs/specs/` |
| A25 | helm `~> 2.14` installs a chart from a local path with no repository | provider v2.14.0 `release` docs: "Chart name to be installed. A path may be used."; example `chart = "./charts/example"` |
| A26 | The module's existing releases are the shape to copy | `modules/operators/main.tf:21-56`: `helm_release` with `name`, `chart`, `namespace`, `set`, `depends_on` on the namespace |
| A27 | CI `helm lint` does not cover a chart under the Terraform tree | `.github/workflows/deploy-validate.yml:28-35` lints only `Iverson.Server/deploy/helm/iverson`; same in `.gitlab-ci.yml:18-19` |

Three assumptions changed the design:

- **A11 sharpened.** The design was presented as touching no AKS pool resource. Because
  no source states where AKS places `emptyDir` by default, section 2 now declares
  `kubelet_disk_type = "OS"` on the default pool and the extra pools. Two lines; the claim
  the runbook makes about node disks is then declared rather than assumed.
- **A1 correction.** The design was presented as editing the cluster's inline `node_config`
  plus the pools. The cluster declares no `node_config` at all, and removes its default
  pool; the `general` pool is a separate resource. Two edit sites either way, but section 1
  now names the right ones.
- **A14 falsified.** The policy was presented as applied through the Kubernetes provider.
  No pinned or available provider version can apply both policy and binding without a
  plan-time cluster dependency. Section 3 now applies it through a local chart and a Helm
  release, chosen by Ben over a two-phase apply. A27 followed from that: the new chart
  needs its own lint line.

## Known issues, accepted as out of scope

**AKS etcd Secrets, the AWS flow-log group and the Azure Log Analytics workspace stay on
platform keys.** Recorded in the Scope section with reasons. Ben's call, 2026-09-08.

**No datastore backups.** Unchanged from the first design. Becomes load-bearing the moment
production holds data. Separate project.

**A stale comment.** `modules/cluster-aws/main.tf:25` refers to "the companion Helm chart's
Postgres backup retentionPolicy". No such policy exists in the chart. Not on this design's
path; noted so nobody reads it as evidence that backups exist.
