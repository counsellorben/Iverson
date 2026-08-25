# Critical Design Review: 2026-08-24-at-rest-encryption-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-24-at-rest-encryption-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

### Sections

| Row | Disposition |
|---|---|
| Problem | `ok` — re-read the three-cloud table against the root modules. `terraform/aws/main.tf:65-68` passes `{type = "gp3"}`; azure `:63` passes `{skuName = "PremiumV2_LRS"}`; gcp `:66-69` passes `{type = "pd-ssd"}`. No encryption parameter in any of the three. Claim holds. |
| Scope | `→ §3.1` — the in-scope set is "the six PersistentVolumes". Node-level storage that holds data at rest is neither included nor listed among the four out-of-scope items. |
| Design 1 — CMK per cloud | `→ §2.1` — the required grant is named for AWS and GCP but not for Azure. |
| Design 2 — parameters map | `→ §2.2` — the `diskEncryptionType` aside is false for the configured Azure disk SKU. |
| Design 3 — KMS both sides | `ok` — re-read `AmazonEBSCSIDriverPolicy` v15: every statement is `ec2:*`, zero KMS actions. The two-sided requirement (IAM policy + key policy) is correct; KMS is deny-by-default on both. |
| Design 4 — Prometheus SC | `ok` — Kubernetes docs confirm `""` "effectively disable[s] dynamic provisioning"; `values-local.yaml:97` gives Prometheus `"standard"`, so the chart is proven to work with a named class. Both halves of the spec's claim check out. |
| Design 5 — greenfield | `ok` — greenfield is user-asserted ground truth. Checked the consequence that would matter otherwise: StorageClass `parameters` is immutable in Kubernetes, so adding encryption keys to an existing class would force delete-and-recreate. With no cluster deployed there is no existing class to mutate, so this never arises as scoped. |
| Design 6 — audit evidence | `ok` — all four checks map to artifacts that exist. Check 4 (enumerate every PVC, confirm its class is one of the six) is sound *for PVCs*; the producer sweep below confirms the six is the complete PVC set. |
| Verified assumptions | See §1. |
| Known issues | `ok` — backups and the absent policy gate are both recorded with rationale and an explicit owner decision. Not re-raised. |

### Rules and operands

| Row | Disposition |
|---|---|
| Eligibility predicate: "the six PersistentVolumes in cloud production" — enumerated every PVC **producer**, not just the stores the spec names | `ok` for PVCs — grepped all 12 charts for `kind: PersistentVolumeClaim` / `volumeClaimTemplates`: only `ollama`, `qdrant`, `prometheus` declare one directly. `postgres`, `kafka`, `starrocks` produce theirs via operator CRs, all templating `storageClassName` from values. `authentik`, `jaeger`, `redis`, `api`, `worker`, `admin-ui` declare none — their `storage:` matches are `ephemeral-storage` resource limits. The six is complete **as a set of PVCs**. `→ §3.1` for the data-bearing storage that is not a PVC. |
| Under-inclusion on that predicate (a data-bearing volume the control silently misses) | `→ §3.1` — `jaeger/templates/deployment.yaml:66-68` mounts `emptyDir: {}`, which lands on the node root volume, as do all container writable layers. |
| Over-inclusion on that predicate (a volume swept in that shouldn't be) | `ok` — the six classes are per-store and named; nothing else binds them (see A13). |
| Negative claim: "None of those StorageClasses set an encryption parameter" | `ok` — `modules/operators/main.tf:139-182`, five classes, all passing only `var.storage_class_config.parameters`; the three root modules populate that map with disk type alone. |
| Negative claim: "Redis has no PVC" | `ok` — no `PersistentVolumeClaim` or `volumeClaimTemplates` anywhere in `charts/redis/templates/`. |
| Negative claim: "ZooKeeper does not exist in cloud" | `ok` — `charts/kafka/templates/kafka.yaml:6` sets `strimzi.io/kraft: enabled`; no `zookeeper` key in any of the three cloud values files. |
| Negative claim (A13): "nothing else references these class names" | `ok` — re-grepped `iverson-postgres\|-kafka\|-starrocks\|-qdrant\|-ollama`: hits only in `modules/operators/main.tf` and the three cloud values files. Doc hits are container names. |
| Identity rule: the StorageClass name set, with a sixth added | `ok` — checked over-merge: `iverson-prometheus` collides with no existing class name, and each store binds its own. No two stores share a class, so no conflation. |

### Data-flow arrows

| Row | Disposition |
|---|---|
| cluster module KMS key → root module → `operators` var → `kubernetes_storage_class.parameters` → CSI driver | `ok` — `parameters` is `map(string)` (`modules/operators/variables.tf`), so `encrypted = "true"` and an ARN are both type-valid. No persistence boundary crossed. |
| KMS key policy ← `ebs_csi_irsa` role ARN, **and** IAM policy ← key ARN (cycle risk) | `ok` — the role resource itself depends on neither. Ordering role → key (needs role ARN) → IAM policy (needs key ARN) → attachment is acyclic. `terraform/aws/main.tf:61-64` already passes `module.cluster` outputs into `module.operators`, so the wiring pattern exists. |
| `values-*.yaml` `storageClassName` → chart template → PVC / operator CR field | `ok` — traced all six consuming operations to a real field: postgres `cluster.yaml:10` (`storageClass`), kafka `kafka.yaml:53` (`class`), starrocks `starrockscluster.yaml:17,35` (FE `fe-meta` and BE `be-data`, both from the one value), qdrant `statefulset.yaml:119`, ollama `statefulset.yaml:108`, prometheus `pvc.yaml:7`. |
| provisioned volume → auditor evidence (`aws ec2 describe-volumes`) | `ok` — `Encrypted` and `KmsKeyId` are real fields on that API, so evidence item 3 is obtainable as written. |

## 1. Verified-assumptions cross-check

All fifteen reconfirmed under a fresh read of the cited evidence. Nothing re-litigated.

A1–A3 re-verified against upstream this round: AWS `encrypted`/`kmsKeyId` and Azure `diskEncryptionSetID` from the drivers' own parameter docs; GCP `disk-encryption-kms-key` from driver source `pkg/parameters/constants.go:10`, which remains the right target since the user guides don't document CMEK. A4–A15 re-checked against the repo; the citations in the spec's table all resolve to the lines they name.

**Span check — dependencies with no covering assumption:**

1. *"The six named stores are the complete set of PVC producers."* A12 verifies that every store **named in the values files** binds a controlled class — it does not establish that no other component creates a PVC. **Verified in-round:** grepped all 12 charts; only ollama, qdrant and prometheus declare PVCs directly, the three operator charts produce theirs from values, and the remaining six charts declare none. Gap closes clean.
2. *"The provisioner strings in the root modules are the CSI drivers whose parameters A1–A3 verify."* A1–A3 verify parameter names per driver but nothing ties those drivers to this deployment. **Verified in-round:** `ebs.csi.aws.com`, `disk.csi.azure.com`, `pd.csi.storage.gke.io` read from the three root modules. Gap closes clean.
3. *"Azure's CMK path needs nothing beyond a Key Vault key and a Disk Encryption Set."* No assumption covers Azure's prerequisites. **Verified in-round and FAILS** — see §2.1.
4. *"Node-level storage holds nothing the control needs to cover."* No assumption covers it, and it is not true. **→ §3.1.**

## 2. Literal-wrongness findings

### 2.1 — Azure's disk encryption set needs a Key Vault grant the spec never names

**Description.** Design §3 states that "GCP and Azure have equivalent service-agent bindings, described in section 1." Section 1 describes that binding for GCP (`roles/cloudkms.cryptoKeyEncrypterDecrypter` for the Compute Engine service agent) but for Azure names only "a Key Vault key plus a Disk Encryption Set." The DES's system-assigned managed identity must additionally be granted permission on the Key Vault, and the spec never says so.

By the spec's own standard in §3 — "required for the cluster to function, not a hardening extra" — this is the same failure class it correctly identifies for AWS, left out for one of the three clouds.

**Evidence.** Azure's managed-disk encryption documentation gives the creation workflow: step 4, "a system-assigned managed identity is created in Microsoft Entra ID and associated with the disk encryption set"; step 5, "The Azure key vault administrator then grants the managed identity permission to perform operations in the key vault"; steps 7-8, managed disks then "use the managed identity to send requests to the Azure Key Vault" to wrap and unwrap the data encryption key. Without the grant the unwrap fails, and the same document notes that when a key becomes inaccessible "disk I/O (read or write operations) start to fail one hour after."

The document also imposes a co-location constraint the spec doesn't state: "Managed disks and the Key Vault or managed HSM must be in the same Azure region."

**Proposed fix.** In Design §1, extend the Azure bullet to name both requirements: the DES's system-assigned managed identity must be granted key wrap/unwrap/get on the Key Vault, and the Key Vault must be in the same region as the disks. Then correct the sentence in §3 so it does not claim section 1 covers a binding it omits.

### 2.2 — The Azure double-encryption option is unavailable on the configured disk SKU

**Description.** Design §2 closes by offering `EncryptionAtRestWithPlatformAndCustomerKeys` as "double encryption … available if an auditor asks for it." It is not available for the disk type this deployment actually provisions, so acting on that sentence produces a StorageClass that cannot provision.

**Evidence.** `terraform/azure/main.tf:63` sets `parameters = { skuName = "PremiumV2_LRS" }` — Premium SSD v2. Azure's disk-encryption documentation states plainly: "Double encryption at rest isn't currently supported with either Ultra Disks or Premium SSD v2 disks."

**Proposed fix.** Either strike the offer, or keep it with the restriction attached — noting that taking it up requires changing `skuName` away from `PremiumV2_LRS`, which is a performance decision rather than a free toggle.

## 3. Forced decisions

### 3.1 — Whether node-level storage is inside the compliance boundary

**The choice.** The control covers the six PersistentVolumes. It does not cover the EKS node root volumes, which also hold data at rest in this deployment. The spec neither includes them nor lists them among its four out-of-scope items, so the boundary is undeclared rather than decided.

**Why it's forced.** Two facts make this a real decision rather than a hypothetical:

- Data genuinely lands there. `charts/jaeger/templates/deployment.yaml:66-68` mounts `emptyDir: {}`, which is backed by the node's root volume; every container writable layer and all kubelet ephemeral storage sits on the same disk. Jaeger holds request traces.
- Those volumes are secured by precisely the conditional the spec rejects as insufficient for data volumes. `aws_eks_node_group.pools` (`modules/cluster-aws/main.tf:413-446`) sets no `launch_template`, no `disk_size` and no block device mapping, so nodes take the EKS default root volume, whose encryption depends on the account's EBS encryption-by-default setting — the same unasserted account-level flag the Problem section calls out as the AWS gap.

The consequence is specific to the spec's compliance driver, and it is a false-assurance risk rather than a data-loss one: §6's evidence set would come back fully green — six classes, all CMK-encrypted, every PVC accounted for — while unencrypted trace data sits on the node disks of the same cluster. An auditor asking "is data at rest encrypted" would be shown an artifact that does not answer the question it appears to answer.

**The options.**

- **(a) Extend scope.** Add a launch template to the node groups with `block_device_mappings` setting `encrypted = true` and the same CMK, and add a fifth evidence check covering node volumes. Largest change; makes the control's claim true as stated.
- **(b) Declare out of scope with a stated reason** — e.g. that only derived/ephemeral data lands there and the auditor's boundary is persistent stores. Cheapest; requires the reason to survive an auditor who knows what `emptyDir` is.
- **(c) Narrow the claim.** Keep the scope and retitle the control to something the evidence actually supports, such as "persistent volume encryption", so nothing over-claims.

Not picking between these: the right answer depends on where the auditor draws the boundary, which is outside the codebase.

## 5. Recommendation

🛑 **Surface forced decisions to user** — §3 is non-empty.

§2's two items are both bounded corrections to the spec text with evidence attached, and neither changes the design's shape. §3.1 needs an answer before the spec is planned against, because option (a) adds work to the Terraform module and options (b) and (c) change what the spec claims.
