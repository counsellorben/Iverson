# At-Rest Encryption Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-24-at-rest-encryption-design.md` (commit SHA: `4e5da93`)

**Goal:** Bring every cloud-production volume — the six PersistentVolumes and the EKS node root volumes — under a customer-managed key, with evidence an auditor can check.

**Architecture:** Each cloud module gains a data-volume CMK beside the cluster key it already creates. The key reaches all five existing StorageClasses through the single `storage_class_config.parameters` map that `modules/operators` shares across them, plus a sixth class for Prometheus. On AWS the same key also encrypts node root volumes via per-pool launch templates, which requires a second principal in the key policy.

**Tech stack:** Terraform 1.7.5 (CI pin), hashicorp/aws `~> 5.0`, hashicorp/azurerm `~> 3.90`, hashicorp/google `~> 5.30`, hashicorp/kubernetes `~> 2.31`; Helm charts under `Iverson.Server/deploy/helm/iverson`.

---

## Global Constraints

Project-wide rules every task must hold to. Copied from the spec.

- **Cloud production only.** `values-local.yaml` and `values-laptop.yaml` use the `"standard"` StorageClass and must not be touched. They must still pass `helm lint`.
- **Greenfield.** No cloud cluster is deployed, so no task carries a migration procedure. Every PVC is born encrypted on the first `terraform apply`.
- **Match the existing policy-authoring convention.** `modules/cluster-aws/main.tf` builds IAM policies with `jsonencode({...})` (see `aws_iam_role.ebs_csi_irsa` at `:223-239`), not `data "aws_iam_policy_document"`. New policies follow suit.
- **CI is the gate.** Every Terraform task must leave `terraform fmt -check`, `terraform init -backend=false`, `terraform validate` and `tfsec` green for the cloud it touched; the Helm task must additionally leave `helm lint` and `helm template | kubeconform` green across all five overlays.

## File Structure

**Modify**
- `Iverson.Server/deploy/terraform/modules/operators/main.tf` — add a sixth `kubernetes_storage_class` for Prometheus (Task 1)
- `Iverson.Server/deploy/terraform/modules/operators/outputs.tf` — add the `prometheus` key to `storage_class_names` (Task 1)
- `Iverson.Server/deploy/helm/iverson/values-aws.yaml`, `values-azure.yaml`, `values-gcp.yaml` — point Prometheus at `iverson-prometheus`; add nothing else (Task 1)
- `Iverson.Server/deploy/terraform/modules/cluster-aws/main.tf` — data-volume CMK, key policy, CSI KMS policy (Task 2); launch templates and the Auto Scaling key-policy statements (Task 3)
- `Iverson.Server/deploy/terraform/modules/cluster-aws/outputs.tf` — export the key ARN (Task 2)
- `Iverson.Server/deploy/terraform/aws/main.tf` — encryption parameters on `storage_class_config` (Task 2)
- `Iverson.Server/deploy/terraform/modules/cluster-azure/main.tf` — Key Vault, key, disk encryption set, identity grant (Task 4)
- `Iverson.Server/deploy/terraform/modules/cluster-azure/outputs.tf` — export the DES id (Task 4)
- `Iverson.Server/deploy/terraform/azure/main.tf` — `diskEncryptionSetID` on `storage_class_config` (Task 4)
- `Iverson.Server/deploy/terraform/modules/cluster-gcp/main.tf` — data-volume crypto key and service-agent binding (Task 5)
- `Iverson.Server/deploy/terraform/modules/cluster-gcp/outputs.tf` — export the key id (Task 5)
- `Iverson.Server/deploy/terraform/gcp/main.tf` — `disk-encryption-kms-key` on `storage_class_config` (Task 5)

**Create**
- `docs/runbooks/at-rest-encryption-verification.md` — the five evidence checks (Task 6)

**Test** — this plan has no unit-test surface. Terraform correctness is established by `validate` + `tfsec`, and Helm correctness by `lint` + `kubeconform`, which are the gates CI already runs. No new test infrastructure is introduced.

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and re-confirmed by two rounds of `critical-design-review`. **Not re-verified here.**

- A1 — AWS EBS CSI accepts `encrypted` (`"true"`/`"false"`) and `kmsKeyId` (full ARN)
- A2 — Azure Disk CSI accepts `diskEncryptionSetID` (DES resource ID); optional `diskEncryptionType` alongside
- A3 — GCP PD CSI accepts `disk-encryption-kms-key` (driver source `pkg/parameters/constants.go:10`)
- A4 — `modules/operators` creates five StorageClasses, all from one shared parameters map
- A5 — `storage_class_config.parameters` is `map(string)`
- A6 — root modules can pass a `module.cluster` output into `storage_class_config` with no dependency cycle
- A7 — Prometheus is `storageClassName: ""` in all three cloud profiles
- A8 — the Prometheus chart honors `.Values.storageClassName` (`charts/prometheus/templates/pvc.yaml:7`)
- A9 — `aws_iam_role.ebs_csi_irsa` exists and carries only `AmazonEBSCSIDriverPolicy`, which grants **zero** KMS actions
- A10 — Redis has no PVC in cloud
- A11 — cloud Kafka is Strimzi KRaft and binds `iverson-kafka`
- A12 — every store in every cloud profile references a controlled StorageClass, except Prometheus
- A13 — nothing outside the operators module and the three cloud values files references the `iverson-*` class names
- A14 — `docs/runbooks/` is not gitignored
- A15 — all six charts template `storageClassName` from values
- A16 — the six named stores are the only PVC producers in cloud
- A17 — the provisioner strings are `ebs.csi.aws.com`, `disk.csi.azure.com`, `pd.csi.storage.gke.io`

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | All 13 files the plan modifies exist at the cited paths | Each read; e.g. `modules/cluster-aws/main.tf` 478 lines, `modules/operators/main.tf` 182 lines, `modules/cluster-azure/outputs.tf` 10 lines |
| P2 | File path | `docs/runbooks/` exists and is not gitignored | Directory present; `git check-ignore --no-index docs/runbooks/x.md` returns nothing |
| S1 | Signature | `aws_kms_key "eks_secrets"` uses `deletion_window_in_days = 30` and `enable_key_rotation = true` — Task 2 mirrors it | `modules/cluster-aws/main.tf:172-176` |
| S2 | Signature | The IRSA role is addressable as `aws_iam_role.ebs_csi_irsa` | `modules/cluster-aws/main.tf:223` |
| S3 | Signature | `aws_eks_node_group.pools` uses `for_each = local.node_pools`, so launch templates key the same way | `modules/cluster-aws/main.tf:414` |
| S4 | Signature | `google_kms_key_ring "gke"` exists; Task 5 adds a key to it rather than creating a second ring | `modules/cluster-gcp/main.tf:47` |
| S5 | Signature | `data "google_project" "this"` exists, so the project number is available for the service-agent member | `modules/cluster-gcp/main.tf:58` |
| S6 | Signature | `azurerm_resource_group "this"` exists with `location = var.location` | `modules/cluster-azure/main.tf:7-10` |
| S7 | Signature | Every StorageClass in operators is built from `var.storage_class_config.{provisioner,parameters}` | `modules/operators/main.tf:143-144, 152-153, 161-162, 170-171, 179-180` |
| S8 | Signature | **`modules/operators/outputs.tf` enumerates the five class names explicitly**, so Task 1 must add a sixth key there — not only a resource | Read of the file: five literal map keys |
| S9 | Signature | **No `data "aws_caller_identity"` exists in `cluster-aws`**, so Task 2 must add one for the account ID in the key policy | grep for `aws_caller_identity`/`aws_partition`/`aws_region` returned nothing |
| S10 | Signature | **No `azurerm_client_config` data source or `tenant_id` variable exists in `cluster-azure`**, so Task 4 must add one for the Key Vault | grep across `modules/cluster-azure/*.tf` and `terraform/azure/*.tf` returned nothing |
| C1 | Command | CI runs `terraform -chdir=<cloud> fmt -check`, `init -backend=false`, `validate` per cloud | `.github/workflows/deploy-validate.yml:107-128` |
| C2 | Command | CI runs `tfsec Iverson.Server/deploy/terraform/` | `deploy-validate.yml:104` |
| C3 | Command | CI lints five overlays: `values-local`, `values-laptop`, `values-aws`, `values-azure`, `values-gcp` | `deploy-validate.yml:31` |
| C4 | Command | Terraform 1.9.8, tfsec v1.28.6 and Helm v3.16.4 are installed locally, so every gate is runnable at implementation time | `terraform version`, `tfsec --version`, `helm version --short` |
| C5 | Command | Commit convention is a lowercase descriptive sentence; Conventional-Commits prefixes appear but are not universal | `git log --oneline -20` |
| O1 | Ordering | Task 3 adds statements to the key policy Task 2 creates → 2 must precede 3 | Task 3's `aws_kms_key_policy` references `aws_kms_key.data_volumes` |
| O2 | Ordering | Tasks 1, 4 and 5 share no symbol with each other or with 2/3 | Task 1 touches only `modules/operators` + values files; Tasks 4/5 touch only their own cloud module and root |
| V1 | Code validity | aws `~> 5.0` accepts `aws_launch_template.block_device_mappings.ebs.{volume_size,encrypted,kms_key_id}` and `aws_eks_node_group.launch_template` | Probe config with the pinned constraint: `terraform init` installed aws v5.100.0, `terraform validate` returned "Success! The configuration is valid." |
| V2 | Code validity | azurerm `~> 3.90` accepts `azurerm_key_vault.{purge_protection_enabled,soft_delete_retention_days}` and `azurerm_disk_encryption_set` with `identity { type = "SystemAssigned" }` | Same probe: azurerm v3.117.1 installed, validate succeeded |
| V3 | Code validity | google `~> 5.30` accepts `google_kms_crypto_key_iam_binding` | Already used in-repo at `modules/cluster-gcp/main.tf:60` |
| V4 | Code validity | The node groups set no `ami_type` and pin `kubernetes_version = "1.30"`, so they take the EKS-optimized AL2 x86_64 AMI, whose root device is `/dev/xvda` | `modules/cluster-aws/variables.tf:11-14`; no `ami_type` or `disk_size` anywhere in `cluster-aws` |
| U1 | Consumer impact | Adding a sixth key to `storage_class_names` is additive for its three consumers — each re-exports the whole map | `terraform/{aws,azure,gcp}/main.tf` each do `output "storage_class_names" { value = module.operators.storage_class_names }` |
| U2 | Consumer impact | Adding a parameter key to `storage_class_config.parameters` breaks no consumer — nothing enumerates the map's keys | All consumers are the five `parameters = var.storage_class_config.parameters` assignments |
| U3 | Consumer impact | Adding `launch_template` to `aws_eks_node_group.pools` does not break `aws_autoscaling_group_tag`, which reads `pools["general"].resources[0].autoscaling_groups[0].name` — a managed node group still creates an ASG | `modules/cluster-aws/main.tf:460-468`; EKS documents that managed node groups are "always deployed with a launch template to be used with the Amazon EC2 Auto Scaling group" |
| U4 | Consumer impact | Nothing outside the three cloud values files reads `prometheus.storageClassName` | grep across the repo for the `iverson-*` class names and the Prometheus values key |
| W1 | Sibling sweep | **Meta-class: every Terraform address the plan references resolves at its point of use.** Pre-existing — `aws_kms_key.eks_secrets`, `aws_iam_role.ebs_csi_irsa`, `aws_eks_node_group.pools`, `local.node_pools`, `aws_autoscaling_group_tag`, `google_kms_key_ring.gke`, `data.google_project.this`, `azurerm_resource_group.this`, the five `kubernetes_storage_class` resources, `module.operators`, `module.cluster`. Created by the plan — everything else. | Each grepped in its module; all present |
| W2 | Sibling sweep | All three root modules carry a `storage_class_config` block, and all three are edited (Tasks 2, 4, 5) — none left on plaintext | `terraform/aws/main.tf:65`, `azure/main.tf:61`, `gcp/main.tf:66` |
| W3 | Sibling sweep | `local.node_pools` has **seven** members — postgres, starrocks-fe, starrocks-be, qdrant, kafka, ollama, general — so Task 3 must use `for_each = local.node_pools`, not a hand-listed subset | `modules/cluster-aws/main.tf:401-410` |
| W4 | Sibling sweep | The two overlays Task 1 does not touch (`values-local`, `values-laptop`) must still lint; both use `"standard"` and are unaffected | `values-local.yaml:13,21,31,48,60,97`, `values-laptop.yaml:24,32,40,48` |

## Tasks

### Task 1: Prometheus StorageClass

Closes the latent bug in which `storageClassName: ""` disables dynamic provisioning, leaving the Prometheus PVC permanently unbound, and brings the sixth volume under the same key as the other five.

**Files:**
- Modify: `Iverson.Server/deploy/terraform/modules/operators/main.tf`
- Modify: `Iverson.Server/deploy/terraform/modules/operators/outputs.tf`
- Modify: `Iverson.Server/deploy/helm/iverson/values-aws.yaml:107`, `values-azure.yaml:100`, `values-gcp.yaml:101`

**Interfaces:**
- Produces: the `iverson-prometheus` StorageClass name, consumed by the three cloud values files in this same task.

- [ ] **Step 1: Add the sixth StorageClass**, after `kubernetes_storage_class "ollama"` in `modules/operators/main.tf`, in exactly the shape of the existing five:

```hcl
resource "kubernetes_storage_class" "prometheus" {
  metadata {
    name = "iverson-prometheus"
  }
  storage_provisioner = var.storage_class_config.provisioner
  parameters          = var.storage_class_config.parameters
  volume_binding_mode = "WaitForFirstConsumer"
}
```

- [ ] **Step 2: Add its name to the module output** in `modules/operators/outputs.tf`, which enumerates keys explicitly:

```hcl
    prometheus = kubernetes_storage_class.prometheus.metadata[0].name
```

- [ ] **Step 3: Point the three cloud profiles at it.** In each of `values-aws.yaml`, `values-azure.yaml` and `values-gcp.yaml`, change the Prometheus entry from `storageClassName: ""` to `storageClassName: "iverson-prometheus"`. Change nothing else in these files. Do **not** touch `values-local.yaml` or `values-laptop.yaml`.

- [ ] **Step 4: Verify.** All five overlays must stay green, including the two untouched ones:

```bash
helm dependency build Iverson.Server/deploy/helm/iverson
for v in values-local values-laptop values-aws values-azure values-gcp; do
  helm lint Iverson.Server/deploy/helm/iverson -f "Iverson.Server/deploy/helm/iverson/$v.yaml"
done
terraform -chdir=Iverson.Server/deploy/terraform/aws fmt -check
terraform -chdir=Iverson.Server/deploy/terraform/aws init -backend=false
terraform -chdir=Iverson.Server/deploy/terraform/aws validate
```

- [ ] **Step 5: Confirm the rendered PVC carries the class.** The point of the task is that the PVC becomes bindable:

```bash
helm template iverson Iverson.Server/deploy/helm/iverson \
  -f Iverson.Server/deploy/helm/iverson/values-aws.yaml \
  | grep -A2 'kind: PersistentVolumeClaim'
```

Expected: `storageClassName: "iverson-prometheus"`, not `""`.

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/deploy/terraform/modules/operators/main.tf \
        Iverson.Server/deploy/terraform/modules/operators/outputs.tf \
        Iverson.Server/deploy/helm/iverson/values-aws.yaml \
        Iverson.Server/deploy/helm/iverson/values-azure.yaml \
        Iverson.Server/deploy/helm/iverson/values-gcp.yaml
git commit -m "give prometheus a real storage class in the three cloud profiles"
```

### Task 2: AWS data-volume CMK

**Files:**
- Modify: `Iverson.Server/deploy/terraform/modules/cluster-aws/main.tf`
- Modify: `Iverson.Server/deploy/terraform/modules/cluster-aws/outputs.tf`
- Modify: `Iverson.Server/deploy/terraform/aws/main.tf:65-68`

**Interfaces:**
- Produces: `module.cluster.data_volumes_key_arn`, consumed by `terraform/aws/main.tf` in this task and by Task 3's key policy.

- [ ] **Step 1: Add the account-id data source.** None exists in this module, and the key policy needs it:

```hcl
data "aws_caller_identity" "current" {}
```

- [ ] **Step 2: Create the key**, beside `aws_kms_key.eks_secrets` and mirroring its settings:

```hcl
resource "aws_kms_key" "data_volumes" {
  description             = "${var.cluster_name} data volume encryption (PersistentVolumes and node root volumes)"
  deletion_window_in_days = 30
  enable_key_rotation     = true
}
```

- [ ] **Step 3: Attach a key policy granting the EBS CSI driver.** The root statement is mandatory — without it the key becomes unmanageable by anyone. Task 3 extends this same resource, so keep it as a standalone `aws_kms_key_policy`:

```hcl
resource "aws_kms_key_policy" "data_volumes" {
  key_id = aws_kms_key.data_volumes.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid       = "EnableIAMUserPermissions"
        Effect    = "Allow"
        Principal = { AWS = "arn:aws:iam::${data.aws_caller_identity.current.account_id}:root" }
        Action    = "kms:*"
        Resource  = "*"
      },
      {
        Sid       = "AllowEBSCSIDriverUseOfTheKey"
        Effect    = "Allow"
        Principal = { AWS = aws_iam_role.ebs_csi_irsa.arn }
        Action    = ["kms:Encrypt", "kms:Decrypt", "kms:ReEncrypt*", "kms:GenerateDataKey*", "kms:DescribeKey"]
        Resource  = "*"
      },
      {
        Sid       = "AllowEBSCSIDriverAttachmentOfPersistentResources"
        Effect    = "Allow"
        Principal = { AWS = aws_iam_role.ebs_csi_irsa.arn }
        Action    = "kms:CreateGrant"
        Resource  = "*"
        Condition = { Bool = { "kms:GrantIsForAWSResource" = "true" } }
      }
    ]
  })
}
```

- [ ] **Step 4: Grant the CSI role on the IAM side too.** KMS is deny-by-default on both sides, and `AmazonEBSCSIDriverPolicy` carries no KMS actions at all (inherited assumption A9):

```hcl
resource "aws_iam_policy" "ebs_csi_kms" {
  name = "${var.cluster_name}-ebs-csi-kms"
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect   = "Allow"
        Action   = ["kms:Encrypt", "kms:Decrypt", "kms:ReEncrypt*", "kms:GenerateDataKey*", "kms:DescribeKey"]
        Resource = aws_kms_key.data_volumes.arn
      },
      {
        Effect    = "Allow"
        Action    = "kms:CreateGrant"
        Resource  = aws_kms_key.data_volumes.arn
        Condition = { Bool = { "kms:GrantIsForAWSResource" = "true" } }
      }
    ]
  })
}

resource "aws_iam_role_policy_attachment" "ebs_csi_kms" {
  role       = aws_iam_role.ebs_csi_irsa.name
  policy_arn = aws_iam_policy.ebs_csi_kms.arn
}
```

- [ ] **Step 5: Export the ARN** in `modules/cluster-aws/outputs.tf`:

```hcl
output "data_volumes_key_arn" { value = aws_kms_key.data_volumes.arn }
```

- [ ] **Step 6: Wire it into the StorageClasses.** In `terraform/aws/main.tf`, extend the existing block — this one edit reaches all six classes:

```hcl
  storage_class_config = {
    provisioner = "ebs.csi.aws.com"
    parameters = {
      type      = "gp3"
      encrypted = "true"
      kmsKeyId  = module.cluster.data_volumes_key_arn
    }
  }
```

- [ ] **Step 7: Verify**
```bash
terraform -chdir=Iverson.Server/deploy/terraform/aws fmt -check
terraform -chdir=Iverson.Server/deploy/terraform/aws init -backend=false
terraform -chdir=Iverson.Server/deploy/terraform/aws validate
tfsec Iverson.Server/deploy/terraform/
```

- [ ] **Step 8: Commit**
```bash
git add Iverson.Server/deploy/terraform/modules/cluster-aws/main.tf \
        Iverson.Server/deploy/terraform/modules/cluster-aws/outputs.tf \
        Iverson.Server/deploy/terraform/aws/main.tf
git commit -m "encrypt aws data volumes with a customer-managed key"
```

### Task 3: AWS node root volumes

Depends on Task 2 — extends the key policy that task creates.

**Files:**
- Modify: `Iverson.Server/deploy/terraform/modules/cluster-aws/main.tf`

**Interfaces:**
- Consumes: `aws_kms_key.data_volumes` and `aws_kms_key_policy.data_volumes` from Task 2.

- [ ] **Step 1: Add a launch template per node pool.** Use the same `for_each` as the node groups so all seven pools are covered. `/dev/xvda` is the root device of the EKS-optimized AL2 x86_64 AMI, which these groups take because they set no `ami_type` and the cluster pins 1.30; naming a different device would add a second volume and leave the root unencrypted. `volume_size` must be set here because EKS prohibits `disk_size` on a node group once a launch template is attached:

```hcl
resource "aws_launch_template" "pools" {
  for_each    = local.node_pools
  name_prefix = "${var.cluster_name}-${each.key}-"

  block_device_mappings {
    device_name = "/dev/xvda"
    ebs {
      volume_size           = 20
      volume_type           = "gp3"
      encrypted             = true
      kms_key_id            = aws_kms_key.data_volumes.arn
      delete_on_termination = true
    }
  }
}
```

- [ ] **Step 2: Reference it from the node groups.** Add to `aws_eks_node_group.pools`, leaving `instance_types` where it is — EKS rejects a node group that specifies instance types when its launch template also does:

```hcl
  launch_template {
    id      = aws_launch_template.pools[each.key].id
    version = aws_launch_template.pools[each.key].latest_version
  }
```

- [ ] **Step 3: Add the Auto Scaling principal to the key policy.** Node root volumes are created by Auto Scaling, not by the CSI driver, and the service-linked role has no access to customer-managed keys by default. It is AWS-managed and takes no attached policy, so this is key-policy-only. Add both statements to the `Statement` list in `aws_kms_key_policy.data_volumes`:

```hcl
      {
        Sid    = "AllowAutoScalingUseOfTheKey"
        Effect = "Allow"
        Principal = {
          AWS = "arn:aws:iam::${data.aws_caller_identity.current.account_id}:role/aws-service-role/autoscaling.amazonaws.com/AWSServiceRoleForAutoScaling"
        }
        Action   = ["kms:Encrypt", "kms:Decrypt", "kms:ReEncrypt*", "kms:GenerateDataKey*", "kms:DescribeKey"]
        Resource = "*"
      },
      {
        Sid    = "AllowAutoScalingAttachmentOfPersistentResources"
        Effect = "Allow"
        Principal = {
          AWS = "arn:aws:iam::${data.aws_caller_identity.current.account_id}:role/aws-service-role/autoscaling.amazonaws.com/AWSServiceRoleForAutoScaling"
        }
        Action    = "kms:CreateGrant"
        Resource  = "*"
        Condition = { Bool = { "kms:GrantIsForAWSResource" = "true" } }
      }
```

- [ ] **Step 4: Verify**
```bash
terraform -chdir=Iverson.Server/deploy/terraform/aws fmt -check
terraform -chdir=Iverson.Server/deploy/terraform/aws init -backend=false
terraform -chdir=Iverson.Server/deploy/terraform/aws validate
tfsec Iverson.Server/deploy/terraform/
```

- [ ] **Step 5: Confirm all seven pools are covered.** A subset would leave node volumes unencrypted on the missed pools:

```bash
grep -c 'for_each    = local.node_pools' Iverson.Server/deploy/terraform/modules/cluster-aws/main.tf
```

Expected: at least 1 for the launch template, and `aws_eks_node_group.pools` still using `for_each = local.node_pools`.

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/deploy/terraform/modules/cluster-aws/main.tf
git commit -m "encrypt eks node root volumes with the data-volume key"
```

### Task 4: Azure CMK path

**Files:**
- Modify: `Iverson.Server/deploy/terraform/modules/cluster-azure/main.tf`
- Modify: `Iverson.Server/deploy/terraform/modules/cluster-azure/outputs.tf`
- Modify: `Iverson.Server/deploy/terraform/azure/main.tf:61-64`

**Interfaces:**
- Produces: `module.cluster.data_volumes_des_id`, consumed by `terraform/azure/main.tf` in this task.

- [ ] **Step 1: Add the tenant data source.** None exists in this module, and Key Vault requires a tenant id:

```hcl
data "azurerm_client_config" "current" {}
```

- [ ] **Step 2: Create the Key Vault and key.** Purge protection is required for a disk encryption set; without it the DES cannot be created:

```hcl
resource "azurerm_key_vault" "data_volumes" {
  name                       = "${var.cluster_name}-dv-kv"
  location                   = azurerm_resource_group.this.location
  resource_group_name        = azurerm_resource_group.this.name
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  purge_protection_enabled   = true
  soft_delete_retention_days = 30
}

resource "azurerm_key_vault_key" "data_volumes" {
  name         = "data-volumes"
  key_vault_id = azurerm_key_vault.data_volumes.id
  key_type     = "RSA"
  key_size     = 2048
  key_opts     = ["decrypt", "encrypt", "sign", "unwrapKey", "verify", "wrapKey"]

  depends_on = [azurerm_key_vault_access_policy.terraform]
}
```

- [ ] **Step 3: Let the deploying principal manage keys**, otherwise Step 2's key creation is denied:

```hcl
resource "azurerm_key_vault_access_policy" "terraform" {
  key_vault_id = azurerm_key_vault.data_volumes.id
  tenant_id    = data.azurerm_client_config.current.tenant_id
  object_id    = data.azurerm_client_config.current.object_id

  key_permissions = ["Create", "Delete", "Get", "List", "Purge", "Recover", "Update", "GetRotationPolicy"]
}
```

- [ ] **Step 4: Create the disk encryption set and grant its identity.** The DES gets a system-assigned managed identity, and without the grant the data encryption key cannot be unwrapped — disk I/O then fails within the hour:

```hcl
resource "azurerm_disk_encryption_set" "data_volumes" {
  name                = "${var.cluster_name}-des"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  key_vault_key_id    = azurerm_key_vault_key.data_volumes.id

  identity {
    type = "SystemAssigned"
  }
}

resource "azurerm_key_vault_access_policy" "des" {
  key_vault_id = azurerm_key_vault.data_volumes.id
  tenant_id    = azurerm_disk_encryption_set.data_volumes.identity[0].tenant_id
  object_id    = azurerm_disk_encryption_set.data_volumes.identity[0].principal_id

  key_permissions = ["Get", "WrapKey", "UnwrapKey"]
}
```

- [ ] **Step 5: Export the DES id** in `modules/cluster-azure/outputs.tf`:

```hcl
output "data_volumes_des_id" { value = azurerm_disk_encryption_set.data_volumes.id }
```

- [ ] **Step 6: Wire it into the StorageClasses.** In `terraform/azure/main.tf`. Leave `diskEncryptionType` at its default — the double-encryption alternative is unavailable on Premium SSD v2:

```hcl
  storage_class_config = {
    provisioner = "disk.csi.azure.com"
    parameters = {
      skuName             = "PremiumV2_LRS"
      diskEncryptionSetID = module.cluster.data_volumes_des_id
    }
  }
```

- [ ] **Step 7: Verify**
```bash
terraform -chdir=Iverson.Server/deploy/terraform/azure fmt -check
terraform -chdir=Iverson.Server/deploy/terraform/azure init -backend=false
terraform -chdir=Iverson.Server/deploy/terraform/azure validate
tfsec Iverson.Server/deploy/terraform/
```

- [ ] **Step 8: Commit**
```bash
git add Iverson.Server/deploy/terraform/modules/cluster-azure/main.tf \
        Iverson.Server/deploy/terraform/modules/cluster-azure/outputs.tf \
        Iverson.Server/deploy/terraform/azure/main.tf
git commit -m "encrypt azure data volumes with a customer-managed key"
```

### Task 5: GCP CMK path

**Files:**
- Modify: `Iverson.Server/deploy/terraform/modules/cluster-gcp/main.tf`
- Modify: `Iverson.Server/deploy/terraform/modules/cluster-gcp/outputs.tf`
- Modify: `Iverson.Server/deploy/terraform/gcp/main.tf:66-69`

**Interfaces:**
- Produces: `module.cluster.data_volumes_key_id`, consumed by `terraform/gcp/main.tf` in this task.

- [ ] **Step 1: Add the key to the existing ring.** `google_kms_key_ring "gke"` is already cluster-scoped, so this reuses it rather than creating a second ring. Rotation period matches the existing `gke_secrets` key:

```hcl
resource "google_kms_crypto_key" "data_volumes" {
  name            = "data-volumes"
  key_ring        = google_kms_key_ring.gke.id
  rotation_period = "7776000s" # 90 days
}
```

- [ ] **Step 2: Grant the Compute Engine service agent.** This is the principal that encrypts persistent disks; `data.google_project.this` already exists for the project number:

```hcl
resource "google_kms_crypto_key_iam_binding" "data_volumes" {
  crypto_key_id = google_kms_crypto_key.data_volumes.id
  role          = "roles/cloudkms.cryptoKeyEncrypterDecrypter"
  members = [
    "serviceAccount:service-${data.google_project.this.number}@compute-system.iam.gserviceaccount.com",
  ]
}
```

- [ ] **Step 3: Export the key id** in `modules/cluster-gcp/outputs.tf`:

```hcl
output "data_volumes_key_id" { value = google_kms_crypto_key.data_volumes.id }
```

- [ ] **Step 4: Wire it into the StorageClasses.** In `terraform/gcp/main.tf`:

```hcl
  storage_class_config = {
    provisioner = "pd.csi.storage.gke.io"
    parameters = {
      type                      = "pd-ssd"
      "disk-encryption-kms-key" = module.cluster.data_volumes_key_id
    }
  }
```

- [ ] **Step 5: Verify**
```bash
terraform -chdir=Iverson.Server/deploy/terraform/gcp fmt -check
terraform -chdir=Iverson.Server/deploy/terraform/gcp init -backend=false
terraform -chdir=Iverson.Server/deploy/terraform/gcp validate
tfsec Iverson.Server/deploy/terraform/
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/deploy/terraform/modules/cluster-gcp/main.tf \
        Iverson.Server/deploy/terraform/modules/cluster-gcp/outputs.tf \
        Iverson.Server/deploy/terraform/gcp/main.tf
git commit -m "encrypt gcp data volumes with a customer-managed key"
```

### Task 6: Verification runbook

**Files:**
- Create: `docs/runbooks/at-rest-encryption-verification.md`

- [ ] **Step 1: Write the runbook** with the spec's five evidence checks, each as a runnable command with its expected output:

1. **Key management** — the Terraform resource showing `enable_key_rotation = true` and the deletion window. Answers which key, who controls it, whether it rotates.
2. **Uniform application** — `kubectl get storageclass -o yaml`, showing the encryption parameter and key reference on all six classes.
3. **Provider attestation** — `aws ec2 describe-volumes` showing `Encrypted: true` and a matching `KmsKeyId`, or the Azure/GCP equivalents. Provider-attested rather than self-reported.
4. **No volume escaped the set** — enumerate every PVC in the namespace and confirm each one's StorageClass is one of the six.
5. **Node root volumes** — resolve each node's root volume from `aws ec2 describe-instances`, then confirm `Encrypted: true` with the same `KmsKeyId` via `describe-volumes`. Without this the other four can pass while unencrypted trace data sits on node disks.

State plainly what the evidence set does **not** cover: AKS and GKE node OS disks are encrypted with platform-managed keys, not this CMK, which is the design's stated position rather than an oversight.

- [ ] **Step 2: Commit**
```bash
git add docs/runbooks/at-rest-encryption-verification.md
git commit -m "add the at-rest encryption verification runbook"
```

## Tasks NOT in this plan

Inherited from the spec's "Out of scope, with reasons":

- **Redis** — the chart declares no PVC. Nothing at rest.
- **ZooKeeper** — does not exist in cloud. Kafka runs Strimzi in KRaft mode (`charts/kafka/templates/kafka.yaml:6`, `strimzi.io/kraft: enabled`).
- **Compose and kind** — the compliance boundary is production. `values-local.yaml` and `values-laptop.yaml` use the `"standard"` StorageClass and are untouched by this work.
- **Encryption in transit, application-level encryption, per-tenant keys.** Different controls, different designs.

## Known issues inherited from spec

**There are no configured backups for any datastore.** The CloudNativePG cluster declares `bootstrap.initdb` and nothing else (`charts/postgres/templates/cluster.yaml:23`) — no `backup.barmanObjectStore`, no schedule, no object-store target. No runbook in `docs/runbooks/` covers backup or restore for any store.

This does not block the work as scoped, because greenfield means there is no data to lose. It is recorded here because it becomes load-bearing the moment cloud production holds real data: every future storage migration, including any re-keying of these volumes, depends on having a restorable copy. Ben's call to keep it out of this design; it warrants its own project before production data exists.

**No CI or admission-policy gate enforces the StorageClass allow-list.** Section 7's negative check is a documented manual procedure. A Kyverno/OPA policy rejecting PVCs outside the six classes is the natural next step and would convert the check from point-in-time to continuous, but a documented verification procedure satisfies the compliance requirement as stated. Deliberately not included rather than overlooked.
