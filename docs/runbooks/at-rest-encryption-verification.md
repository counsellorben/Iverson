# At-rest encryption — verification runbook

This is the auditor-facing evidence procedure for the cloud deployment's at-rest encryption
control. It answers one question five ways: is every persistent volume in the cluster — data
volumes and node root volumes alike — encrypted with a key this account controls, with no gaps.

Every command below is runnable as written except for the bracketed substitutions
(`<cluster-name>`, `<namespace>`) — `<namespace>` is always `iverson`, the release namespace
`kubernetes_namespace.iverson` in
`Iverson.Server/deploy/terraform/modules/operators/main.tf` creates.

Commands are written against AWS, the primary target of this control's Terraform. Checks 1 and 3
have an Azure/GCP equivalent, noted inline. Check 5 (node root volumes) is AWS-only by design —
see the coverage note at the end.

## 1. Key management

Confirms which key, who controls it, and whether it rotates. `terraform state show` is the
source of truth here because the key's rotation and deletion-window settings are declared, not
runtime-observed — check 3 below is what carries provider attestation.

```bash
terraform -chdir=Iverson.Server/deploy/terraform/aws state show 'module.cluster.aws_kms_key.data_volumes'
```

Expected output includes:

```
enable_key_rotation     = true
deletion_window_in_days = 30
```

`enable_key_rotation = true` means AWS rotates the key material automatically every year without
changing the key's ARN — no re-encryption of existing volumes is needed. `deletion_window_in_days
= 30` is the mandatory wait before a `terraform destroy` (or manual deletion) of this key can
actually take effect, giving 30 days to notice and cancel an accidental deletion before every
volume encrypted under it becomes unrecoverable.

**Azure equivalent:**

```bash
terraform -chdir=Iverson.Server/deploy/terraform/azure state show 'module.cluster.azurerm_key_vault_key.data_volumes'
```

This key currently has no rotation policy configured — an accepted follow-up recorded at the
resource's definition in `Iverson.Server/deploy/terraform/modules/cluster-azure/main.tf`, not an
oversight this runbook should paper over. Report it as-is: no automatic rotation, soft-delete
retention of 30 days (`azurerm_key_vault.data_volumes`'s `soft_delete_retention_days`) plus purge
protection in place of the deletion-window concept AWS uses.

**GCP equivalent:**

```bash
terraform -chdir=Iverson.Server/deploy/terraform/gcp state show 'module.cluster.google_kms_crypto_key.data_volumes'
```

Expected: `rotation_period = "7776000s"` (90 days).

## 2. Uniform application

Confirms all six StorageClasses — the entire allow-list, per Section 7's negative check — carry
the encryption parameter and key reference, not just the ones an auditor happens to sample.

```bash
kubectl get storageclass -o yaml
```

On AWS, every one of `iverson-postgres`, `iverson-starrocks`, `iverson-qdrant`, `iverson-kafka`,
`iverson-ollama`, `iverson-prometheus` shows:

```yaml
provisioner: ebs.csi.aws.com
parameters:
  encrypted: "true"
  kmsKeyId: <the same key ARN from check 1>
  type: gp3
```

On Azure the provisioner is `disk.csi.azure.com` with a `diskEncryptionSetID` parameter instead of
a bare `encrypted`/`kmsKeyId` pair (the disk encryption set itself is what makes encryption
mandatory — see `azurerm_disk_encryption_set.data_volumes`). On GCP the provisioner is
`pd.csi.storage.gke.io` with a `disk-encryption-kms-key` parameter. In all three cases, confirm the
key/set reference is identical across all six classes — six classes pointing at five different keys
would still fail this check even though each individual class looks encrypted.

## 3. Provider attestation

Confirms the cloud provider itself reports the volumes as encrypted, not just that Kubernetes was
asked to create them that way. Resolve the actual EBS volume IDs from the PersistentVolumes
Kubernetes created, then ask AWS about those specific volumes:

```bash
VOLUME_IDS=$(kubectl get pv -o jsonpath='{.items[*].spec.csi.volumeHandle}')

aws ec2 describe-volumes \
  --volume-ids $VOLUME_IDS \
  --query 'Volumes[].{VolumeId:VolumeId,Encrypted:Encrypted,KmsKeyId:KmsKeyId}' \
  --output table
```

Expected: every row shows `Encrypted: True` and a `KmsKeyId` matching the ARN from check 1.
`Encrypted` here is a property AWS reports on the volume resource itself — there is no
StorageClass-parameter equivalent to misconfigure that would fool this specific check.

**Azure equivalent** (per-disk, via the managed disk's own `encryption` block):

```bash
DISK_IDS=$(kubectl get pv -o jsonpath='{.items[*].spec.csi.volumeHandle}')
for id in $DISK_IDS; do
  az disk show --ids "$id" --query '{name:name,encryptionType:encryption.type,diskEncryptionSetId:encryption.diskEncryptionSetId}' -o table
done
```

**GCP equivalent:**

```bash
DISK_IDS=$(kubectl get pv -o jsonpath='{.items[*].spec.csi.volumeHandle}')
for id in $DISK_IDS; do
  gcloud compute disks describe "$(basename "$id")" --zone <zone> --format='value(diskEncryptionKey.kmsKeyName)'
done
```

## 4. No volume escaped the set

Confirms every PVC in the namespace is bound to one of the six encrypted StorageClasses — a PVC
created against `standard` or left with no `storageClassName` (falling through to a cluster
default) would slip past checks 1–3 entirely since those only look at the six classes this control
defines.

```bash
kubectl get pvc -n iverson -o custom-columns='NAME:.metadata.name,STORAGECLASS:.spec.storageClassName' --no-headers \
  | awk '{print $2}' | sort -u
```

Expected: the output is a subset of exactly these six values —
`iverson-postgres`, `iverson-starrocks`, `iverson-qdrant`, `iverson-kafka`, `iverson-ollama`,
`iverson-prometheus`. Any other value, including a blank line (no StorageClass set), is an escape
and fails this check.

## 5. Node root volumes

Confirms the EBS volume backing each node's OS/root disk — not the PersistentVolumes checks 1–4
cover — is also encrypted under the same key. This matters independently of the other four: a node
root volume holds kubelet's local state, container image layers, and anything an application wrote
to ephemeral local storage, including trace/debug data that never touches a PVC. All four PVC
checks above can pass in full while this one fails.

`aws ec2 describe-instances` does not report volume encryption — it returns block-device mappings
naming volume IDs only, not their encryption status. This is necessarily two steps: resolve the
root volume IDs first, then query those IDs for encryption status.

```bash
INSTANCE_IDS=$(kubectl get nodes -o jsonpath='{.items[*].spec.providerID}' | tr ' ' '\n' | sed 's#.*/##')

# Step 1: resolve each node's root volume ID (device /dev/xvda, per
# aws_launch_template.pools in Iverson.Server/deploy/terraform/modules/cluster-aws/main.tf)
ROOT_VOLUME_IDS=$(aws ec2 describe-instances \
  --instance-ids $INSTANCE_IDS \
  --query 'Reservations[].Instances[].BlockDeviceMappings[?DeviceName==`/dev/xvda`].Ebs.VolumeId' \
  --output text)

# Step 2: check those volume IDs' actual encryption status
aws ec2 describe-volumes \
  --volume-ids $ROOT_VOLUME_IDS \
  --query 'Volumes[].{VolumeId:VolumeId,Encrypted:Encrypted,KmsKeyId:KmsKeyId}' \
  --output table
```

Expected: every row shows `Encrypted: True` and a `KmsKeyId` matching the same ARN from checks 1
and 3 — node root volumes and PersistentVolumes share `aws_kms_key.data_volumes`, so one key ARN
should appear across every check in this runbook.

## What this evidence set does not cover

**AKS and GKE node OS disks are not covered by this runbook, and not by design.** Check 5 above is
AWS-only. On Azure, AKS's `default_node_pool` sets no `disk_encryption_set_id`; on GCP, no node
pool in `cluster-gcp` names a customer-managed key for its boot disk. Both platforms still encrypt
those disks — with a platform-managed key the cloud provider controls, not the customer-managed
key (`azurerm_disk_encryption_set.data_volumes` / `google_kms_crypto_key.data_volumes`) this
runbook's other checks verify. This is the design's stated position, not a gap this runbook missed:
node OS disks on those two platforms were deliberately left off the customer-managed-key boundary
that PersistentVolumes and (on AWS only) node root volumes are held to. An auditor asking for
node-disk evidence on an Azure or GCP deployment should be pointed here, not handed a command that
doesn't exist.
