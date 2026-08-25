# Critical Implementation Review: 2026-08-24-at-rest-encryption-implementation-plan (Round 1)

**Plan:** `/home/ben/repositories/Iverson/docs/plans/2026-08-24-at-rest-encryption-implementation-plan.md`
**Verified plan-level assumptions section:** present

⚠️ 2 commits since plan-write time (SHA `4e5da93`); both are documentation (`c15200f` round-2 design review, `c542adc` the plan itself). No code drift. Cited file:line references re-checked under §1.

## 0. Coverage enumeration

### Tasks × surfaces

| Row | Disposition |
|---|---|
| T1 code blocks (StorageClass resource, output key, values edit) | `ok` — the resource mirrors the five existing classes field-for-field including `volume_binding_mode = "WaitForFirstConsumer"`; the output line matches the map shape in `operators/outputs.tf`. Checked that `WaitForFirstConsumer` is safe for a standalone PVC: the Prometheus PVC is consumed by the Prometheus pod, so binding proceeds on schedule rather than hanging. |
| T1 step prose | `ok` — "Change nothing else in these files. Do **not** touch `values-local.yaml` or `values-laptop.yaml`" is executable and correct; those two use `"standard"` and are outside the compliance boundary. |
| T1 commands | `→ §2.1` — the `helm lint` loop and terraform commands are right, but Step 5's rendered-output check cannot show what it claims. |
| T2 code blocks (caller identity, key, key policy, IAM policy, attachment, output, root wiring) | `ok` — the key policy includes the mandatory `EnableIAMUserPermissions` root statement, without which `aws_kms_key_policy` would lock the key. Both sides of the CSI grant present (key policy + IAM policy), matching the spec's two-sided requirement. `jsonencode` matches the module's existing convention. |
| T2 step prose | `ok` — Step 1's justification ("None exists in this module") re-verified: no `aws_caller_identity`/`aws_partition`/`aws_region` data source anywhere in `cluster-aws`. |
| T2 commands | `ok` — `fmt -check`, `init -backend=false`, `validate`, `tfsec` match `.github/workflows/deploy-validate.yml:104,116-128` exactly. |
| T3 code blocks (launch template, node-group wiring, key-policy statements) | `ok` — `for_each = local.node_pools` matches the node group's own iteration at `cluster-aws/main.tf:414`, so all seven pools are covered and `aws_launch_template.pools[each.key]` always resolves. `device_name = "/dev/xvda"` is correct for both AL2 and AL2023 x86_64 EKS AMIs, so it holds regardless of which the 1.30 default resolves to. `volume_size` is present, which EKS requires once a launch template is attached. |
| T3 step prose | `ok` — "leaving `instance_types` where it is — EKS rejects a node group that specifies instance types when its launch template also does" matches the documented rule; the launch template specifies no instance type. |
| T3 commands | `ok` — same CI-derived set. Step 5's `grep -c` is a weak check but its stated expectation ("at least 1") is reachable and true. |
| T4 code blocks (client config, Key Vault, key, two access policies, DES, output, root wiring) | `ok` — `depends_on = [azurerm_key_vault_access_policy.terraform]` on the key is load-bearing and present; without it key creation races the policy that permits it. `purge_protection_enabled = true` is required for a DES to consume the vault. The DES→access-policy ordering introduces no cycle: the vault precedes the DES, the DES precedes its own access policy. |
| T4 step prose | `ok` — Step 1's "None exists in this module" re-verified: no `azurerm_client_config` and no `tenant_id` variable in `cluster-azure`. |
| T4 commands | `ok` — CI-derived, `-chdir` pointed at `azure`. |
| T5 code blocks (crypto key, IAM binding, output, root wiring) | `ok` — the key goes on the existing `google_kms_key_ring.gke` (`cluster-gcp/main.tf:47`) and `data.google_project.this` (`:58`) supplies the project number. Checked the authoritative-binding hazard: `google_kms_crypto_key_iam_binding` replaces all bindings for that role on that key, which is safe here only because the key is new — putting it on `gke_secrets` would have clobbered the existing container-engine-robot binding. It does not. |
| T5 step prose | `ok` — the `compute-system` service agent is the PD-CMEK principal, distinct from the `container-engine-robot` agent the existing `gke_secrets` binding uses for application-layer secrets. The plan uses the right one for the right purpose. |
| T5 commands | `ok` — CI-derived, `-chdir` pointed at `gcp`. Also re-ran `terraform fmt` on the Task 5 map: the block as written is now canonical, so `fmt -check` passes. |
| T6 (runbook prose, no code) | `ok` — the five checks match spec §7 one-for-one, and the closing note about AKS/GKE node OS disks being on platform keys reproduces the design's stated position rather than inventing one. |

### Cross-task interface contracts

| Row | Disposition |
|---|---|
| T2 Produces `module.cluster.data_volumes_key_arn` → T2 Step 6 consumes | `ok` — producing step (Step 5, `outputs.tf`) defines exactly the name the consuming step reads. |
| T2 → T3: T3's key-policy statements and launch template consume `aws_kms_key.data_volumes` and `data.aws_caller_identity.current` | `ok` — both are module-internal resources created in T2, referenced directly rather than through the output. Ordering constraint 2→3 is stated in the plan and real. |
| T4 Produces `module.cluster.data_volumes_des_id` → T4 Step 6 consumes | `ok` — name matches between `outputs.tf` and the root wiring. |
| T5 Produces `module.cluster.data_volumes_key_id` → T5 Step 4 consumes | `ok` — name matches. |
| T1 Produces the `iverson-prometheus` class name → T1 Step 3 consumes in three values files | `ok` — same task, literal string matches the `metadata.name` in Step 1. |
| T1 → `operators/outputs.tf` map consumers (three root modules re-export it) | `ok` — each root does `output "storage_class_names" { value = module.operators.storage_class_names }`; adding a sixth key is additive for all three. |

### Rule-like content (both failure directions)

| Row | Disposition |
|---|---|
| "all seven pools get a launch template" — under-inclusion | `ok` — `for_each = local.node_pools` is the same collection the node groups iterate; a pool cannot be missed without also missing its node group. |
| "all seven pools get a launch template" — over-inclusion | `ok` — no pool outside `local.node_pools` exists; `aws_autoscaling_group_tag` indexes `pools["general"]`, which remains valid. |
| "all three clouds get the encryption parameter" — under-inclusion | `ok` — Tasks 2, 4, 5 each edit their own root's `storage_class_config`; all three blocks confirmed present at `aws/main.tf:65`, `azure/main.tf:61`, `gcp/main.tf:66`. |
| Key-policy principal set: is every principal that must use the key granted? | `→ §3.1` — the CSI role and the Auto Scaling service-linked role are both granted, which is the complete set. The open question is not *which* principals but whether one of them exists at apply time. |

### Candidates generated and dropped

| Candidate | Why dropped |
|---|---|
| T4's Key Vault name `"${var.cluster_name}-dv-kv"` has no length validation; Azure caps vault names at 24 characters | With the module default `cluster_name = "iverson"` the name is 13 characters. Breakage requires someone to supply a much longer name — speculation about a configuration change, not a defect in the plan as written. |
| Key Vault names must be globally unique across Azure, and `iverson-dv-kv` is a generic string | A collision is possible but not deterministic; the plan's outcome does not literally fail as written. |
| `purge_protection_enabled = true` makes the vault undeletable for the retention period, so `terraform destroy` leaves residue | Does not affect the spec's outcome (encryption at rest), and purge protection is a prerequisite for the disk encryption set, so it isn't optional. |
| T1 edits `modules/operators/main.tf` but its `fmt -check` runs against the `aws` root directory, which does not format-check module files | The plan matches what CI does — CI also only fmt-checks the three root directories. No gate is bypassed that would otherwise catch it. |
| T3 Step 5's `grep -c` is a weak coverage proof | Weak, but its stated expectation is reachable and true; a weak check is not a broken one. |

## 1. Verified-plan-assumptions cross-check

All 28 rows reconfirmed under a fresh read; every cited `file:line` still resolves to the content the plan claims. Spot-checked in this round: `cluster-aws/main.tf:172` (key description), `:223` (`aws_iam_role "ebs_csi_irsa"`), `:414` (`for_each = local.node_pools`), `:460` (`aws_autoscaling_group_tag`), `cluster-gcp/main.tf:47` (key ring) and `:58` (`data "google_project"`), `cluster-azure/main.tf:7` (resource group), `operators/main.tf:143`, and the three `storage_class_config` blocks. The two commits since plan-write time are documentation only, so no citation could have moved.

V1/V2 (provider acceptance of the launch-template and Key Vault/DES shapes) were established at plan-write time by an actual `terraform init` + `validate` against the repo's own version constraints. Not re-litigated.

**Span check — dependencies with no covering assumption:**

1. *"The `AWSServiceRoleForAutoScaling` service-linked role exists in the target account at the moment the key policy is applied."* Task 3's key policy names this role's ARN as a principal. No listed assumption covers it, and the "Inherited from spec" list does not either — the spec named the role as the required principal but said nothing about its lifecycle. **Not verifiable from this repository**, since it is a property of the target AWS account rather than of the code. → **§3.1.**

No other uncovered dependency: every other fact the tasks rest on is either in the plan's table, in the inherited list, or verified in §0 above.

## 2. Literal-wrongness findings

### 2.1 — Task 1's Step 5 verification cannot show the field it claims to check, and its output invites a false pass

**Description.** Step 5 exists to prove the task's whole point — that the Prometheus PVC now carries a real StorageClass instead of `""`. The command it gives returns three lines that do not include `storageClassName`, so the stated expected output can never appear. Worse than useless: the output it *does* produce contains the string `iverson-prometheus`, so an implementer scanning for that string will read a failure as a pass.

**Evidence.** Running the step's command verbatim against the current chart:

```
$ helm template iverson Iverson.Server/deploy/helm/iverson \
    -f Iverson.Server/deploy/helm/iverson/values-aws.yaml \
  | grep -A2 'kind: PersistentVolumeClaim'
kind: PersistentVolumeClaim
metadata:
  name: iverson-prometheus
```

The rendered PVC is:

```yaml
kind: PersistentVolumeClaim
metadata:
  name: iverson-prometheus
spec:
  accessModes: ["ReadWriteOnce"]
  storageClassName: ""
```

`storageClassName` sits **five** lines after `kind:`, not two. The `metadata.name` is `iverson-prometheus` because that is the release-prefixed PVC name — it is unrelated to the StorageClass, and its coincidental match with the expected value is exactly what makes the broken check dangerous.

**Proposed fix.** Widen the context and assert on the field rather than the substring:

```bash
helm template iverson Iverson.Server/deploy/helm/iverson \
  -f Iverson.Server/deploy/helm/iverson/values-aws.yaml \
  | grep -A6 'kind: PersistentVolumeClaim' | grep 'storageClassName'
```

Expected: `storageClassName: "iverson-prometheus"`. Before the task's change the same command prints `storageClassName: ""`, so the check distinguishes the two states — which the current one does not.

## 3. Forced decisions

### 3.1 — Whether the Auto Scaling service-linked role is created by this plan or assumed to pre-exist

**The choice.** Task 3 adds two key-policy statements naming
`arn:aws:iam::<account>:role/aws-service-role/autoscaling.amazonaws.com/AWSServiceRoleForAutoScaling`.
That role is created on demand the first time an account uses Auto Scaling. The plan neither creates it nor states it as a prerequisite, so on an account that has never run Auto Scaling its existence at apply time is unestablished.

**Why it's forced.** Three things make this a decision rather than a detail:

- **The dependency graph applies the key policy before any ASG exists.** `aws_kms_key_policy.data_volumes` depends only on the key, the CSI role, and the caller-identity data source. The node groups — which are what would cause the role to be created — depend on the launch template, which depends on the key. Nothing orders the key policy after them.
- **Greenfield is the stated deployment scenario.** The spec's whole migration section rests on nothing being deployed yet, which is precisely the condition under which a never-used service-linked role is plausible.
- **The two obvious mitigations fail in opposite conditions**, so neither can be picked as a safe default. `aws_iam_service_linked_role` errors if the role already exists — the common case in an established account. Assuming it exists errors on a fresh one.

I could not settle from documentation whether KMS rejects a key policy naming a non-existent principal outright. The nearest evidence is AWS's Auto Scaling key-policy guidance, which states for the cross-account grant path that "the service-linked role name specified as the grantee principal must be the name of an existing role." That is suggestive for the grant path, not dispositive for the key-policy path — so this is recorded as unverified rather than as a confirmed break.

**The options.**

- **(a) Create it in Terraform**, with `aws_iam_service_linked_role` for `autoscaling.amazonaws.com`, and accept that deployments into accounts that already have the role need an import or a guard variable.
- **(b) Assume it exists** and document it as a named prerequisite in the plan and the verification runbook, accepting a first-apply failure on a fresh account with a known remedy.
- **(c) Verify empirically before choosing** — attempt the apply against the real target account and let the outcome decide, which costs one apply cycle and removes the guesswork entirely.

Not picking between these: the right answer depends on the target account's history, which is outside this repository.

## 5. Recommendation

🛑 **Surface forced decisions to user** — §3 is non-empty.

§2.1 is a bounded fix to one verification command, with the replacement given. §3.1 needs an answer before execution: option (a) adds a resource to Task 3, option (b) adds prerequisite text to the plan and runbook, and option (c) changes nothing in the plan but gates execution on a trial apply.
