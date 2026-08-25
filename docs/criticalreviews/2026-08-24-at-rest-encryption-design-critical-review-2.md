# Critical Design Review: 2026-08-24-at-rest-encryption-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-24-at-rest-encryption-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

Enumeration built against the current spec before consulting round 1. Design section 5 is
new surface and carries the round's verification budget; the rest of the spec is re-swept at
the same depth rather than assumed settled.

### Sections

| Row | Disposition |
|---|---|
| Problem (edited) | `ok` — the added paragraph's claim re-checked: `aws_eks_node_group.pools` (`modules/cluster-aws/main.tf:413-446`) declares no `launch_template`, no `disk_size`, no block device mapping. Accurate, and its "Section 5 covers them" pointer resolves to the node-volume section. |
| Scope (edited) | `ok` — in-scope now reads six PVs plus EKS node root volumes. Checked whether the AKS/GKE node-disk exclusion is undeclared: it is declared, with a reason, in section 5's closing paragraph. Declared-in-body rather than in the out-of-scope list is a placement choice, not an undeclared boundary. |
| Design 1 — CMK per cloud (edited) | `ok` — Azure bullet now names the DES managed-identity grant (`get`/`wrapKey`/`unwrapKey`) and Key Vault region co-location; GCP bullet names the Compute Engine service agent's KMS role. Both bindings now present, which is what round 1 found missing. |
| Design 2 — parameters map (edited) | `ok` — the `diskEncryptionType` paragraph now carries the Premium SSD v2 restriction and cites `terraform/azure/main.tf:63`, which does set `PremiumV2_LRS`. |
| Design 3 — KMS both sides (edited) | `ok` — the closing sentence no longer claims section 1 "describes" a binding it omitted; it now says section 1 names each and identifies both. Consistent with the edited section 1. |
| Design 4 — Prometheus SC | `ok` — unchanged from round 1 and re-checked: `values-*.yaml` still set `""` in all three cloud profiles, `charts/prometheus/templates/pvc.yaml:7` still templates the value, `values-local.yaml:97` still proves a named class works. |
| Design 5 — Node root volumes (NEW) | `ok` — see the three rules rows below; every named claim in this section was checked against AWS/Azure documentation or the repo. |
| Design 6 — Greenfield | `ok` — renumbered from 5, content unchanged. Re-checked the consequence that would matter otherwise: EKS documents that "existing node groups that don't use a custom launch template can't be updated directly. Instead, you must create a new node group" — greenfield means no node group exists, so the launch-template addition never hits that constraint. |
| Design 7 — Audit evidence (edited) | `ok` — five checks, each mapping to an obtainable artifact. Check 4's allow-list is now six classes, matching the six the design creates. |
| Verified assumptions (edited) | See §1. |
| Known issues | `ok` — cross-reference correctly followed the renumber to "Section 7's negative check". Backups and the absent policy gate unchanged and not re-raised. |

### Rules and operands

| Row | Disposition |
|---|---|
| §5 claim: "Instance type stays on the node group; the launch template must not also set one" | `ok` — EKS launch-template documentation: "specify zero or one Instance type under Launch template contents… If you specify an instance type in a launch template… then you can't specify any instance types in the console or using other tools that use the Amazon EKS API." The spec's direction is the correct one of the two. |
| §5 claim: `AWSServiceRoleForAutoScaling` is the principal needing the CMK grant | `ok` — verified as a two-step chain rather than assumed. EKS: managed node groups "are always deployed with a launch template to be used with the Amazon EC2 Auto Scaling group." Auto Scaling: the service-linked role's predefined permissions "include access to your AWS managed keys. However, they do not include access to your customer managed keys," and the required statements are exactly the five crypto actions plus `kms:CreateGrant` conditioned on `kms:GrantIsForAWSResource`. Both halves of the spec's list match. |
| §5 claim: "the service-linked role is AWS-managed and takes no attached policy of ours" (so key-policy-only) | `ok` — consistent with the above: the grant must be expressed in the key policy because an SLR's permissions cannot be extended by an attached policy. |
| §5 exclusion: "AKS and GKE node OS disks are encrypted by the platform by default" — **tested against the configured operand, not accepted by assertion** | `ok` — this one is conditional and the condition holds. Azure documents that temporary and ephemeral OS disks are *not* covered by SSE, "unless you enable encryption at host", but that "Azure VMs that are version 5 and above (such as Dsv5 or Dsv6) automatically encrypt their temporary disks and (if in use) their ephemeral OS disks." Every Azure node pool default is v5 — `Standard_E8ds_v5` (postgres, starrocks-be), `Standard_D2ds_v5` (kafka), `Standard_D4ds_v5` (general) in `modules/cluster-azure/variables.tf:26-83` — and no `os_disk`/ephemeral override exists in the module. The exclusion is sound as configured. |
| Under-inclusion on the in-scope predicate: is any data-bearing surface still uncovered? | `ok` — PVCs (six classes) and EKS node root volumes are now both covered; Azure/GCP node disks are covered by platform encryption per the row above. |
| Over-inclusion: does the six-class allow-list sweep in anything it shouldn't? | `ok` — each class binds exactly one store; adding `iverson-prometheus` collides with no existing name. |
| A16 negative claim: "the six named stores are the only PVC producers" | `ok` — re-run fresh across all 12 charts: `kafka`, `ollama`, `postgres`, `prometheus`, `qdrant`, `starrocks` reference a StorageClass; `admin-ui`, `api`, `authentik`, `jaeger`, `redis`, `worker` declare neither a PVC nor a class reference. |

### Data-flow arrows

| Row | Disposition |
|---|---|
| data-volume CMK → node group `launch_template` → `block_device_mappings` → instance root volume | `ok` — the launch template's block device mapping applies to the launched instance regardless of which AMI EKS injects, so the key reaches the root volume. |
| CMK key policy ← two principals (EBS CSI IRSA role, ASG service-linked role) | `ok` — both are additive statements on one key policy; the CSI role additionally needs its IAM-side policy (section 3), the SLR does not and cannot have one. No conflict between the two paths. |
| provisioned node volume → auditor evidence (check 5) | `ok` — `describe-instances` yields the root volume ID and the spec's "resolving each root volume" then reaches `Encrypted`/`KmsKeyId`, which check 3 already establishes come from `describe-volumes`. Evidence is obtainable as described. |

### Candidates generated and dropped

| Candidate | Why dropped |
|---|---|
| §5 doesn't name the block device (`/dev/xvda`); a mismatched device name adds a second volume and leaves the root unencrypted | Real failure mode, but it is implementation detail of the launch template — `critical-implementation-review`'s surface once a plan exists, not a design defect. |
| §5 doesn't specify `volume_size`, and EKS documents that with a launch template the size "must be specified in the launch template" | Does not break the asked-for behavior: with no size the BDM inherits the AMI snapshot size, which for the EKS-optimized AMI matches the 20 GiB default the node groups get today. Node-group `disk_size` is prohibited under a launch template, but these node groups don't set it. |
| Check 5 names `describe-instances`, which does not itself return `Encrypted` | The sentence describes resolving the volume first, and check 3 already establishes `describe-volumes` as the source. Imprecision, not breakage. |
| Scope's out-of-scope list doesn't enumerate AKS/GKE node OS disks | Declared with a reason in section 5. Raising it would be round 1's §3.1 in different clothing. |
| The AKS exclusion holds only because the VM sizes are v5; a v4 override would break it | "What if the configuration changes" is speculation about a future the spec doesn't take on. True as configured. |

## 1. Verified-assumptions cross-check

All seventeen reconfirmed under a fresh read. A1–A15 re-checked this round; citations resolve
to the lines they name. A16 and A17 are new since round 1 and were verified independently
rather than taken from the update: A16 by re-running the producer sweep across all 12 charts
(six reference a class, six declare nothing), A17 by reading the three provisioner strings at
`terraform/aws/main.tf:66`, `azure/main.tf:62`, `gcp/main.tf:67`.

**Span check — dependencies introduced by section 5 with no covering assumption:**

1. *"EKS managed node groups accept a custom launch template with instance types left on the node group."* Load-bearing for section 5's prescription; no listed assumption covers it. **Verified in-round** against the EKS launch-template documentation's prohibited-settings rules. Closes clean.
2. *"EKS node launches go through an EC2 Auto Scaling group, making `AWSServiceRoleForAutoScaling` the principal."* Load-bearing for the key-policy addition; no listed assumption covers it. **Verified in-round** via the EKS and Auto Scaling documentation chain. Closes clean.
3. *"AKS and GKE node OS disks are platform-encrypted, so no equivalent work is needed."* Load-bearing for section 5's exclusion; no listed assumption covers it. **Verified in-round** — holds because every configured Azure VM size is v5 or above, which Azure documents as auto-encrypting temporary and ephemeral OS disks. Closes clean.

None of the three required a §3 escalation; all were verifiable from documentation and the repo.

## 2. Literal-wrongness findings

No literal-wrongness findings.

Five candidates were generated and all five failed the test — recorded with their reasons in
§0 rather than promoted to make the round look productive. The two that came closest are
launch-template details (device name, volume size) that belong to implementation review, not
design review.

## 3. Forced decisions

No forced decisions found.

Round 1's forced decision was the compliance boundary; the spec now picks a side and states
it, and section 5's verification did not surface a second undeclared boundary.

## 4. Previously addressed

- **Round 1 §2.1** — Azure's disk encryption set needed a Key Vault grant the spec never named. Design section 1's Azure bullet now names the managed-identity grant and the region co-location constraint, and section 3's closing sentence no longer over-claims what section 1 covers.
- **Round 1 §2.2** — the double-encryption offer was false for the configured disk SKU. Design section 2 now states it is unavailable on `PremiumV2_LRS`, cites the Terraform line, and notes that taking it up means moving off Premium SSD v2.
- **Round 1 §3.1** — the compliance boundary was undeclared for node-level storage. Resolved by extending scope: node root volumes are in scope, Design section 5 covers them, and Audit evidence gained a fifth check. The resolution also added the Auto Scaling key-policy principal, which neither the spec nor round 1 had identified.

## 5. Recommendation

✅ **Approve as-is** — §2 and §3 are both empty. The spec is ready for implementation planning.

The section-5 addition is the round's main surface, and every claim in it that could be
checked against a source was checked: the launch-template rules, the Auto Scaling principal
and its exact key-policy actions, and the Azure/GCP exclusion against the configured VM
generation. The two residual risks worth carrying into planning are the launch template's
block device name and volume size — both dropped here as implementation detail, and both
appropriate for `critical-implementation-review` once a plan exists.
