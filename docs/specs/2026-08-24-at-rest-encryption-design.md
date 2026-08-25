# At-rest encryption for cloud data volumes

**Status:** design approved, not implemented
**Scope:** cloud production only (AWS / Azure / GCP via Helm + Terraform)
**Driver:** compliance — a checkable control with auditable evidence

## Problem

Every stateful service in cloud production stores data on a PersistentVolume provisioned
by a StorageClass this repo creates. None of those StorageClasses set an encryption
parameter: `modules/operators/main.tf:139-182` creates five of them and passes only
`var.storage_class_config.parameters`, which each cloud root module populates with a disk
type and nothing else.

The consequence differs per cloud:

| Cloud | Parameters today | At-rest status |
|---|---|---|
| AWS | `{type = "gp3"}` | **Unencrypted unless the account's EBS encryption-by-default happens to be on.** Not set here, not asserted anywhere. |
| Azure | `{skuName = "PremiumV2_LRS"}` | Encrypted by platform default (SSE, platform-managed key) |
| GCP | `{type = "pd-ssd"}` | Encrypted by platform default (always on) |

So AWS has a real gap, and no cloud uses a customer-managed key for data volumes —
only Kubernetes Secrets do, via `aws_kms_key.eks_secrets` and the GKE
`database_encryption` block.

For a compliance control the question is not merely "are the bytes encrypted" but "with
which key, controlled by whom, and how do you prove it". Platform-default encryption
answers the first and none of the rest.

## Scope

**In scope.** The six PersistentVolumes in cloud production: Postgres, StarRocks, Qdrant,
Kafka, Ollama, Prometheus.

**Out of scope, with reasons.**

- **Redis** — the chart declares no PVC. Nothing at rest.
- **ZooKeeper** — does not exist in cloud. Kafka runs Strimzi in KRaft mode
  (`charts/kafka/templates/kafka.yaml:6`, `strimzi.io/kraft: enabled`).
- **Compose and kind** — the compliance boundary is production. `values-local.yaml` and
  `values-laptop.yaml` use the `"standard"` StorageClass and are untouched by this work.
- **Encryption in transit, application-level encryption, per-tenant keys.** Different
  controls, different designs.

## Design

### 1. A data-volume customer-managed key per cloud

Each cloud module gains a data-volume CMK beside the cluster key it already creates,
following the established pattern in `modules/cluster-aws/main.tf:172-176`:
`enable_key_rotation = true`, 30-day deletion window.

- **AWS** — `aws_kms_key "data_volumes"`, output as an ARN.
- **Azure** — a Key Vault key plus a Disk Encryption Set; the CSI driver consumes the DES
  resource ID, not the raw key.
- **GCP** — a Cloud KMS `CryptoKey` plus an IAM binding granting the Compute Engine
  service agent `roles/cloudkms.cryptoKeyEncrypterDecrypter`.

The three differ in scaffolding because the three CSI drivers accept different things.
The shape is the same: one key per cluster, rotation on, referenced by StorageClasses.

### 2. The key reaches every volume through the existing parameters map

`modules/operators` builds all five StorageClasses from the single
`var.storage_class_config.parameters` map, so encryption reaches every store through one
edit per cloud root module:

```hcl
# terraform/aws/main.tf
storage_class_config = {
  provisioner = "ebs.csi.aws.com"
  parameters = {
    type      = "gp3"
    encrypted = "true"
    kmsKeyId  = module.cluster.data_volumes_key_arn
  }
}
```

Azure adds `diskEncryptionSetID` (the DES resource ID). GCP adds
`disk-encryption-kms-key`. Postgres, StarRocks, Qdrant, Kafka and Ollama are all covered
by that single change — no per-store edits.

`module.operators` already consumes `module.cluster` outputs
(`lb_controller_irsa_role_arn`, `cluster_autoscaler_irsa_role_arn`), so passing a key
reference introduces no new pattern and no dependency cycle.

Azure additionally accepts an optional `diskEncryptionType`, defaulting to
`EncryptionAtRestWithCustomerKey`. Leave it at the default;
`EncryptionAtRestWithPlatformAndCustomerKeys` (double encryption) is available if an
auditor asks for it.

### 3. The AWS CSI driver needs KMS permission — on both sides

`aws_iam_role.ebs_csi_irsa` carries exactly one policy, the AWS-managed
`AmazonEBSCSIDriverPolicy` (`modules/cluster-aws/main.tf:240-243`). **That policy grants
no KMS actions at all** — version 15 contains only `ec2:*` statements.

So this step is required for the cluster to function, not a hardening extra: with a CMK
on the StorageClass and no KMS grant, volume provisioning fails and the cluster comes up
with no storage.

KMS is deny-by-default on both sides, so **both** of these are needed:

1. An IAM policy on `ebs_csi_irsa` granting `kms:CreateGrant`, `kms:Encrypt`,
   `kms:Decrypt`, `kms:GenerateDataKey*` and `kms:DescribeKey` on the new key.
2. A key policy on the CMK granting that role those actions.

Satisfying one and missing the other produces the same failure as doing neither. GCP and
Azure have equivalent service-agent bindings, described in section 1.

### 4. Prometheus gets a real StorageClass — which also fixes a latent bug

All three cloud values files set `prometheus.storageClassName: ""`
(`values-aws.yaml:107`, `values-azure.yaml:100`, `values-gcp.yaml:101`), and the chart
templates that value straight onto the PVC
(`charts/prometheus/templates/pvc.yaml:7`).

An empty `storageClassName` is **not** "use the cluster default". Kubernetes treats it as
an explicit opt-out: *"Claims that request the class `""` effectively disable dynamic
provisioning for themselves."* The PVC binds only to a manually created PV, and no such PV
exists in any of these profiles.

**This is a pre-existing deployment bug in all three cloud profiles**, independent of
encryption: on the first cloud deploy the Prometheus PVC never binds and the pod sits
Pending. It has not been hit because cloud production has not been deployed yet.

The fix is to add a sixth `kubernetes_storage_class "prometheus"` to the operators module
and point the three cloud values files at `iverson-prometheus`. That brings the sixth
volume under the same key and makes the PVC bindable. `values-local.yaml:97` already gives
Prometheus `"standard"`, so the chart is known to work with a named StorageClass — the
cloud profiles are the outlier.

### 5. Greenfield — no migration

A PersistentVolume cannot be encrypted in place; every migration path ends in moving data
to a new volume. Cloud production has not been deployed, so there is no data to move.
Every PVC is born encrypted on the first `terraform apply`, and this design carries no
migration procedure.

If that changes before implementation — if a cloud cluster is stood up with real data
first — this section must be rewritten. The paths differ sharply per store: Ollama and
Prometheus are disposable, Qdrant and StarRocks are derived from Postgres and rebuildable,
Kafka supports broker-by-broker JBOD replacement, and Postgres requires a CNPG rolling
replica replacement onto the new StorageClass followed by a failover.

### 6. Audit evidence

A compliance control that cannot be demonstrated is not finished. Three artifacts, all
falling out of the sections above, plus one negative check.

1. **Key management** — the Terraform resource shows `enable_key_rotation = true` and the
   deletion window. Answers "which key, who controls it, is it rotated".
2. **Uniform application** — `kubectl get storageclass -o yaml` shows the encryption
   parameter and key reference on all six classes.
3. **Provider attestation** — `aws ec2 describe-volumes` showing `Encrypted: true` with a
   matching `KmsKeyId`, or the Azure/GCP equivalents. This one matters most because it is
   attested by the provider rather than self-reported.
4. **No volume escaped the set** — enumerate every PVC in the namespace and confirm each
   one's StorageClass is one of the six. Section 4 is the reason this check exists: a PVC
   can carry a StorageClass outside the controlled set without any error, and that
   happened here in all three cloud profiles.

These commands and their expected output go in a new
`docs/runbooks/at-rest-encryption-verification.md`. That directory is not gitignored.

## Verified assumptions

Each was checked against the codebase or upstream source before this spec was written.

| # | Assumption | Evidence |
|---|---|---|
| A1 | AWS EBS CSI accepts `encrypted` and `kmsKeyId` | aws-ebs-csi-driver `docs/parameters.md` — `encrypted` (`"true"`/`"false"`, default false), `kmsKeyId` (full ARN) |
| A2 | Azure Disk CSI accepts `diskEncryptionSetID` | azuredisk-csi-driver `docs/driver-parameters.md` — takes a DES resource ID; optional `diskEncryptionType` alongside |
| A3 | GCP PD CSI accepts `disk-encryption-kms-key` | Driver source, `pkg/parameters/constants.go:10` — `ParameterKeyDiskEncryptionKmsKey = "disk-encryption-kms-key"`. The user-guide docs do not cover CMEK; the constant is authoritative |
| A4 | 5 StorageClasses, all from one shared parameters map | `modules/operators/main.tf` — 5 `kubernetes_storage_class` resources, all 5 referencing `var.storage_class_config.parameters` |
| A5 | `parameters` is `map(string)` | `modules/operators/variables.tf` — `parameters = map(string)`; `encrypted = "true"` and an ARN are both type-valid |
| A6 | Root modules can pass a cluster output into `storage_class_config` without a cycle | `terraform/aws/main.tf:58-68` — `module.operators` already takes `module.cluster.lb_controller_irsa_role_arn` and `module.cluster.cluster_autoscaler_irsa_role_arn` |
| A7 | Prometheus is `storageClassName: ""` in all three cloud profiles | `values-aws.yaml:107`, `values-azure.yaml:100`, `values-gcp.yaml:101` |
| A8 | The Prometheus chart honors `.Values.storageClassName` | `charts/prometheus/templates/pvc.yaml:7` |
| A9 | The EBS CSI driver has an IRSA role we control, and it lacks KMS permissions | `modules/cluster-aws/main.tf:389-399` (addon with `service_account_role_arn`), `:240-243` (only `AmazonEBSCSIDriverPolicy` attached). That policy, v15, contains zero KMS actions |
| A10 | Redis has no PVC in cloud | No PVC or `volumeClaimTemplates` in `charts/redis/templates/` |
| A11 | Cloud Kafka is Strimzi KRaft and binds `iverson-kafka` | `charts/kafka/templates/kafka.yaml:6` (`strimzi.io/kraft: enabled`), `:51-53` (`persistent-claim`, `class` from values). No ZooKeeper in any cloud values file |
| A12 | **Recurrence set** — every store in every cloud profile references a controlled StorageClass | Checked all six stores across all three cloud values files: five correct in all three, Prometheus empty in all three. One leak, replicated three times |
| A13 | **Dependents** — nothing else depends on these names or parameters | `iverson-*` StorageClass names appear only in `modules/operators/main.tf` and the three cloud values files. Doc matches are container names, not classes. `values-local.yaml` / `values-laptop.yaml` use `"standard"` and are unaffected |
| A14 | `docs/runbooks/` is not gitignored | `git check-ignore` returns nothing for it |
| A15 | All six charts template `storageClassName` from values | postgres `cluster.yaml:10` (`storageClass`), kafka `kafka.yaml:53` (`class`), starrocks `starrockscluster.yaml:17,35` (FE and BE), qdrant `statefulset.yaml:119`, ollama `statefulset.yaml:108`, prometheus `pvc.yaml:7` |

Two assumptions were falsified or sharpened by verification, and the design above reflects
the corrected version:

- **A7/A8 correction.** The design originally claimed Prometheus's empty `storageClassName`
  made it inherit an uncontrolled cluster default. Kubernetes documents the opposite —
  `""` disables dynamic provisioning. The remedy is unchanged; the justification became a
  latent-bug fix rather than a uniformity argument. See section 4.
- **A9 sharpening.** The KMS IAM work was originally scoped as a permission to add. It is
  a prerequisite for the cluster having any storage at all, and it is two-sided (IAM policy
  and key policy). See section 3.

## Known issues, accepted as out of scope

**There are no configured backups for any datastore.** The CloudNativePG cluster declares
`bootstrap.initdb` and nothing else (`charts/postgres/templates/cluster.yaml:23`) — no
`backup.barmanObjectStore`, no schedule, no object-store target. No runbook in
`docs/runbooks/` covers backup or restore for any store.

This does not block the work as scoped, because greenfield means there is no data to lose.
It is recorded here because it becomes load-bearing the moment cloud production holds real
data: every future storage migration, including any re-keying of these volumes, depends on
having a restorable copy. Ben's call to keep it out of this design; it warrants its own
project before production data exists.

**No CI or admission-policy gate enforces the StorageClass allow-list.** Section 6's
negative check is a documented manual procedure. A Kyverno/OPA policy rejecting PVCs
outside the six classes is the natural next step and would convert the check from
point-in-time to continuous, but a documented verification procedure satisfies the
compliance requirement as stated. Deliberately not included rather than overlooked.
