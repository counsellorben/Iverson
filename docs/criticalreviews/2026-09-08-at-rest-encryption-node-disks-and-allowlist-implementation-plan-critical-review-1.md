# Critical Implementation Review: 2026-09-08-at-rest-encryption-node-disks-and-allowlist-implementation-plan (Round 1)

**Plan:** `/home/ben/repositories/Iverson/docs/plans/2026-09-08-at-rest-encryption-node-disks-and-allowlist-implementation-plan.md`
**Verified plan-level assumptions section:** present

⚠️ 1 commit since plan-write time (SHA `51234e6`); cited file:line references re-checked under §1. (The one commit is `f6fa41d`, the plan's own commit; no source file the plan cites has moved.)

The §0 sweep was built against the plan text before the two prior `-design-critical-review-*` files
were read. Those target the spec, not this plan, so the never-re-raise rule does not bind; they were
read to establish which spec-level questions are settled and which were deliberately deferred.

All empirical claims below were re-derived in a scratch copy of
`Iverson.Server/deploy/terraform` under the session scratchpad. The repository was not modified.

## 0. Coverage enumeration

### Tasks × surfaces

| # | Surface | Disposition |
|---|---|---|
| T0a | Header — Goal / Architecture / Tech stack | ok — `terraform v1.9.8` and `helm v3.16.4` locally; both CI files pin Terraform `1.7.5` (`.github/workflows/deploy-validate.yml:118`, `.gitlab-ci.yml:67`) and Helm `3.15.0` (`:23` / `:12`); `kubernetes_version` defaults to `"1.30"` in all three `modules/cluster-*/variables.tf` |
| T0b | Global Constraints — provider pins, creation-time-only, no hand-copied list, fmt-is-a-write-step, commit-message style | ok — `kubernetes ~> 2.31` / `helm ~> 2.14` at `modules/operators/main.tf:3-4`; `git log --oneline --all` resolves `ae42487 encrypt gcp data volumes with a customer-managed key`, confirming the lowercase-imperative convention; the fmt claim is T0c |
| T0c | Global Constraint — "CI runs `fmt -check` without `-recursive`, which never reaches the module files" | ok — empirically re-derived: injected a deliberately misaligned block into a scratch `modules/cluster-gcp/variables.tf`; `terraform -chdir=<scratch>/gcp fmt -check` exits 0 while `terraform fmt -check -recursive <scratch>` exits 3. P8 holds |
| T0d | File Structure — Create / Modify / Test lists | ok — three create paths do not exist (`ls -d modules/operators/charts` → No such file or directory); all six modify paths exist. Note the list names six modify targets while P1's text says "four files" (see §1) |
| T1a | Task 1 step prose — "insert into `node_config` … directly after each block's `service_account` line" | ok — `grep -n "service_account =" modules/cluster-gcp/main.tf` returns exactly `:225` and `:264`, both inside the two `node_config` blocks the plan names; no other candidate line |
| T1b | Task 1 code block — post-format `node_config` head | ok — applied to scratch, ran `terraform fmt -recursive`; the result is byte-identical to the plan's block for `general`, and identical for `pools` with `each.value.machine_type`, exactly as the prose says. The alignment column (`boot_disk_kms_key`, 17 chars) is what fmt produces |
| T1c | Task 1 commands — fmt / fmt -check / init / validate on `gcp` | ok — ran all four against the scratch copy: `fmt -check -recursive` flagged the file before the write step and exited 0 after; `terraform -chdir=<scratch>/gcp validate` → `Success! The configuration is valid.` |
| T1d | Task 1 wiring — `git add` scope | ok — the pristine tree is already `fmt -check -recursive` clean (exit 0), so the whole-tree `fmt -recursive` rewrites only the file the commit adds; no collateral is stranded |
| T2a | Task 2 step prose — cluster attribute after `sku_tier`; `kubelet_disk_type` after `vm_size` on both pools; "do not add `enable_host_encryption`" | ok — `azurerm_kubernetes_cluster.this` at `:152` with `sku_tier` at `:158`; `default_node_pool` `vm_size` at `:164`; `azurerm_kubernetes_cluster_node_pool.pools` `vm_size` at `:217`. All three insertion points unique and unambiguous |
| T2b | Task 2 code blocks — three post-format snippets | ok — applied to scratch and formatted; all three match the plan verbatim, including `disk_encryption_set_id` becoming the new alignment anchor (22 chars) and `role_based_access_control_enabled` staying in its own group across the blank line |
| T2c | Task 2 commands — fmt / init / validate on `azure` | ok — `terraform -chdir=<scratch>/azure validate` → `Success!`, so `disk_encryption_set_id` on the cluster and `kubelet_disk_type` on both pool declarations are all accepted by `azurerm ~> 3.90` |
| T2d | Task 2 wiring — cycle question | ok — `azurerm_role_assignment.aks_data_volumes_des` (`:119-123`) is the only resource naming both DES and cluster; DES → cluster → role assignment is acyclic, and `validate` confirms Terraform builds the graph |
| T3a | Task 3 Interfaces block (Consumes/Produces) | → contract rows C1–C3 below |
| T3b | Task 3 Step 1 — `Chart.yaml` / `values.yaml` | ok — matches `Iverson.Server/deploy/helm/iverson/charts/prometheus/Chart.yaml`'s shape field-for-field (apiVersion v2, type application, version 0.1.0, appVersion "1.0.0"); `helm lint` accepts it |
| T3c | Task 3 Step 2 — `templates/policy.yaml` | ok — every identifier checked, not just the CEL: `failurePolicy`, `matchConstraints.resourceRules[].{apiGroups,apiVersions,operations,resources}`, `validations[].{expression,reason,messageExpression}`, `policyName`, `validationActions`, `matchResources.namespaceSelector.matchLabels`. `reason: Invalid` is one of the four accepted reasons. Sprig `join` is pipeline-shaped (`list \| join sep`), which is what the template uses. Rendered output validated below |
| T3d | Task 3 Step 3 — the `helm_release` block | ok — all seven `kubernetes_storage_class` addresses resolve (`modules/operators/main.tf:139,148,157,166,175,184,193`) and `kubernetes_namespace.iverson` at `:12`; no existing release is named `pvc_storageclass_policy` (the five are `cloudnative_pg`, `strimzi`, `starrocks_operator`, `cluster_autoscaler`, `aws_load_balancer_controller`). The block is already `fmt`-clean as written — appending it left `modules/operators/main.tf` unflagged by `fmt -check -recursive` |
| T3e | Task 3 Step 4 — the two CI insertions | ok — applied both at the stated points to scratch copies and parsed with PyYAML: `.github/workflows/deploy-validate.yml` `jobs.helm-lint.steps[-1]` is the new step; `.gitlab-ci.yml` `helm-lint.script[2]` is the new line. Indentation in both snippets matches the surrounding file. Adding a `- ` item at indent 4 after `.gitlab-ci.yml:20` terminates the preceding `- \|` block scalar correctly. Both CI path filters (`Iverson.Server/deploy/**` / `Iverson.Server/deploy/**/*`) cover the new chart's location |
| T3f | Task 3 Step 5 — lint / template / fmt / validate | ok — `helm lint` → `1 chart(s) linted, 0 chart(s) failed` with the single `[INFO] Chart.yaml: icon is recommended` the plan predicts; `helm template … --set 'allowedStorageClasses={iverson-postgres,iverson-prometheus}'` renders `object.spec.storageClassName in ["iverson-postgres","iverson-prometheus"]` and a binding with `kubernetes.io/metadata.name: iverson`, both verbatim as predicted; `terraform -chdir=<scratch>/aws validate` → `Success!` |
| T3g | Task 3 Step 6 — `git add` scope | ok — `git check-ignore` returns non-zero for `modules/operators/charts/pvc-storageclass-policy/Chart.yaml`, so the new chart is committable without `-f`; `.gitignore:74`'s `charts/*.tgz` rule is scoped to the helm tree and to `.tgz` |
| T4a | Task 4 Step 1 — coverage-paragraph rewrite, and the claim that `:7-8` stands unchanged | ok on `:7-8`: the invariant is "every command runnable as written except the bracketed `<zone>` in check 3's GCP variant", and neither new command in Step 3 introduces a bracketed substitution — the Azure one resolves the node RG through `az aks show`, the GCP one derives project/zone/instance from `spec.providerID`. One candidate dropped, T4f |
| T4b | Task 4 Step 2 — check-4 enforcement evidence and negative test | ok — insertion anchor `## 4. No volume escaped the set` (`:136`) is unique and the section ends at `:151`; `kubectl get validatingadmissionpolicies` / `validatingadmissionpolicybindings` are the correct command-line plurals; `kubectl apply --dry-run=server` sends `dryRun=All` on a CREATE, which validating admission does evaluate, so the denial is reachable and nothing is persisted; the denial message from a ValidatingAdmissionPolicy names the policy, so "naming `iverson-pvc-storageclass`" holds. The submitted PVC is structurally complete (accessModes + storageClassName + resources.requests.storage) |
| T4c | Task 4 Step 3 — Azure variant | → §2.1 |
| T4d | Task 4 Step 3 — GCP variant | ok — `providerID` is `gce://<project>/<zone>/<instance>`; `sed 's#^gce://##'` then `IFS=/ read -r project zone instance` binds the three fields in the right order; `gcloud compute disks describe "$instance" --zone "$zone"` is the same call shape check 3 already uses at `:132`. `--project` is unused but check 3's existing GCP command omits it too, so the runbook's idiom is unchanged. The key the boot disk will report is the same object the StorageClass parameter names — `gcp/main.tf:70` passes `module.cluster.data_volumes_key_id`, i.e. `google_kms_crypto_key.data_volumes.id`, which is exactly what `boot_disk_kms_key` is set to — so "the same key check 3 shows" holds |
| T4e | Task 4 Step 4 — closing-section repoint | → §2.2 |
| T4f | Task 4 Step 1 — "Checks 1, 3 and 5 each have an Azure/GCP equivalent" while check 2 also carries Azure/GCP content (`:90-97`) | dropped — fails the literal-wrongness test. Check 2's *command* (`kubectl get storageclass -o yaml`) is identical on all three clouds; what differs is the expected output, which the paragraph does not speak to, so there is no "equivalent" for it to omit. The pre-amendment sentence made the same omission, so no evidence is lost, and no auditor is directed away from anything |
| T4g | Task 4 Step 5 — commit | ok — `git check-ignore docs/runbooks/at-rest-encryption-verification.md` returns non-zero and the file is in `git ls-files`, so the plain `git add` works (unlike `docs/specs/`, `docs/plans/`, `docs/criticalreviews/`, all ignored at `.gitignore:45-51`) |
| T5 | "Tasks NOT in this plan" / "Known issues inherited from spec" | ok — verbatim from the spec's Scope and Known-issues sections; the stale-comment claim is real (`modules/cluster-aws/main.tf:24-25` does read "the companion Helm chart's Postgres backup retentionPolicy") and correctly marked off-path |

### Cross-task interface contracts

| # | Contract | Disposition |
|---|---|---|
| C1 | Task 3 produces the policy/binding **object names and shape**; Task 4 Step 2 consumes them as the runbook's expected output | ok — checked field by field against the chart Task 3 creates, not against the spec's prose: policy name `iverson-pvc-storageclass`, binding name `iverson-pvc-storageclass`, `validationActions: [Deny]`, `namespaceSelector.matchLabels."kubernetes.io/metadata.name": iverson` (from `{{ .Release.Namespace }}` with `namespace = kubernetes_namespace.iverson.metadata[0].name`), and `validations[0].expression` containing the seven names. Every expectation the runbook states is present in the rendered object |
| C2 | Task 3's `helm_release` values → the CEL list literal → the "same seven classes check 2 lists" expectation in Task 4 Step 2 | ok — crosses three serialization boundaries and was traced through all of them on real data: Terraform emits `iverson-{postgres,starrocks,qdrant,kafka,ollama,tei,prometheus}` (`modules/operators/main.tf:141,150,159,168,177,186,195`); runbook check 2 (`:79-80`) lists exactly those seven strings; `toJson` on that list renders `["iverson-postgres",…]`, confirmed by running `helm template` |
| C3 | Tasks 1–2 produce the key/set the Task 4 Step 3 expectations name ("the same key check 3 shows" / "the same disk encryption set id check 2 shows") | ok — GCP: both `boot_disk_kms_key` and the StorageClass `disk-encryption-kms-key` parameter resolve to `google_kms_crypto_key.data_volumes.id` (`gcp/main.tf:70`). Azure: both the cluster's `disk_encryption_set_id` and the StorageClass `diskEncryptionSetID` parameter resolve to `azurerm_disk_encryption_set.data_volumes.id` (`azure/main.tf:70`). One key/set per cloud, as the runbook's closing sentence asserts |
| C4 | Task 4 Step 3's Azure command consumes a **root** output, `terraform -chdir=…/azure output -raw cluster_name` | ok — `azure/main.tf:75` re-exports `module.cluster.cluster_name`; `modules/cluster-azure/outputs.tf:1` sources it from `azurerm_kubernetes_cluster.this.name`, which is `var.cluster_name`, so `${CLUSTER}-rg` reproduces `azurerm_resource_group.this.name` at `modules/cluster-azure/main.tf:8`. Not covered by any P item — see the span check |
| C5 | Task ordering — P5's "Tasks 1–3 touch disjoint files and share no symbol" | ok — each task's whole-tree `terraform fmt -recursive` is idempotent over the other tasks' files because the pristine tree is already format-clean; Task 3's chart directory is invisible to `terraform init`/`validate` (all three roots validated with it present) |

### Rule-like content (both failure directions)

| # | Rule | Disposition |
|---|---|---|
| R1 | The CEL predicate — which PVCs are denied | ok, both directions, against the producers the repo can actually emit. Under-inclusion: `grep` over all 13 subchart `templates/` dirs finds seven PVC producers (`prometheus/pvc.yaml:7`, `qdrant/statefulset.yaml:119`, `ollama/statefulset.yaml:107`, `tei/statefulset.yaml:89`, `postgres/cluster.yaml:10`, `kafka/kafka.yaml:53`, `starrocks/starrockscluster.yaml:17,35`), every one templating `.Values.storageClassName`; none emits a class-less PVC that the policy would wrongly deny. Over-inclusion: the rendered list is exactly the seven Terraform-created class names, and `CREATE`-only matching cannot be sidestepped because `storageClassName` is immutable post-create |
| R2 | The chart's degenerate render — `allowedStorageClasses: []` under default values | ok — this is the input class CI actually feeds, since `helm lint` and the plan's own lint step use chart defaults. Renders `object.spec.storageClassName in []` and `"; allowed: "`, both valid YAML; `helm lint` exits 0. The empty list never reaches a cluster, because the `helm_release` always supplies all seven |
| R3 | Task 4 Step 3's Azure expectation — which rows the command can return, and what "no rows" can mean | → §2.1. Both directions fail: the row set includes disks the check is not about, and the stated failure signal cannot fire |
| R4 | Kubeconform schema conformance of the rendered pair | ok — `helm template … \| kubeconform -kubernetes-version 1.30.0` → `Valid: 2, Invalid: 0, Errors: 0, Skipped: 0`, naming both `ValidatingAdmissionPolicy` and `ValidatingAdmissionPolicyBinding`. P12 reproduced exactly |

## 1. Verified-plan-assumptions cross-check

Each P item re-checked by a fresh read of the cited evidence, or by re-running the cited command.

- **P1** — holds, with an inexact count. All paths exist; but the plan modifies **six** files (three modules, two CI files, the runbook), not "four". The evidence column enumerates all six, so nothing is unverified — only the sentence's count is wrong.
- **P2** — holds. `ls -d Iverson.Server/deploy/terraform/modules/operators/charts` → `No such file or directory`.
- **P3** — holds, exact. `modules/cluster-gcp/main.tf:73` `google_kms_crypto_key.data_volumes`; `modules/cluster-azure/main.tf:78` `azurerm_disk_encryption_set.data_volumes`; `modules/operators/main.tf:139,148,157,166,175,184,193` the seven storage classes; `:12` the namespace.
- **P4** — holds. The five existing `helm_release` names are `cloudnative_pg` (`:21`), `strimzi` (`:32`), `starrocks_operator` (`:59`), `cluster_autoscaler` (`:77`), `aws_load_balancer_controller` (`:110`). No collision.
- **P5** — holds. Verified by applying all three tasks' edits together to one scratch tree and validating all three roots.
- **P6** — holds, with a version note. `terraform v1.9.8`; local Helm is **v3.16.4**, not the v3.15.0 CI pins — immaterial for `lint`/`template` on this chart, which was run and produced the predicted output. Provider init succeeded on all three roots offline of any backend.
- **P7** — holds, exact. `.github/workflows/deploy-validate.yml:120-127` are the fmt/init/validate steps; `:28-35` the umbrella lint loop; `.gitlab-ci.yml:15-20` the same.
- **P8** — holds, and re-derived empirically rather than from the flag's documentation (see §0 T0c).
- **P9** — holds. All three roots `Success! The configuration is valid.` with every planned edit applied.
- **P10** — holds. `fmt -check -recursive` flagged `modules/cluster-azure/main.tf` and `modules/cluster-gcp/main.tf` before the write step, exit 3; clean after, exit 0. `modules/operators/main.tf` was never flagged — the `helm_release` block as written in the plan is already canonical.
- **P11** — holds, exact. Lint: `0 chart(s) failed`, one `[INFO] Chart.yaml: icon is recommended`. Template: `in ["iverson-postgres","iverson-prometheus"]` and `"; allowed: iverson-postgres, iverson-prometheus"`.
- **P12** — holds, exact. `Valid: 2, Invalid: 0`.
- **P13** — holds. `charts/prometheus/Chart.yaml` is apiVersion v2 / type application / version 0.1.0 / appVersion "1.0.0".
- **P14** — holds. `grep -rn "google_container_node_pool\." --include=*.tf .` returns nothing.
- **P15** — holds, exact. `modules/cluster-azure/main.tf:122` and `:216`; `modules/cluster-azure/outputs.tf:1,4`.
- **P16** — holds. `modules/operators/outputs.tf` declares only `storage_class_names`.
- **P17** — holds. `grep -rn "runbooks/at-rest-encryption-verification"` outside `docs/criticalreviews/` returns only the predecessor plan, this plan, and the two specs — all by path.
- **P18** — holds; the evidence's parenthetical is loose. `modules/cluster-azure/outputs.tf` exposes exactly the four named outputs, and none is the node resource group, so the assumption's substance stands. But "the root adds no more" is inexact: `azure/main.tf:78` adds `kubeconfig_command`. It is not a node-RG output, so the conclusion is unaffected.
- **P19** — holds, exact. `modules/cluster-azure/main.tf:8` is `name = "${var.cluster_name}-rg"`; `azure/main.tf:78` builds the identical string.
- **P20** — holds. Both plurals are used in the rendered evidence commands and match the API paths.
- **P21** — holds. `which az gcloud aws` returns nothing.

### Span check — plan dependencies no listed assumption covers, as scoped

1. **Azure CSI-provisioned data disks live in the AKS node resource group**, so Task 4 Step 3's `az disk list -g "$NODE_RG"` returns rows drawn from check 3's producer whether or not node OS disks are enumerable. No P item covers this, and the plan's own prose asserts it ("That group holds both the scale-set OS disks this check exists for and the CSI-provisioned data disks check 3 reads") without evidence. Verifiable in-round only for the *set* half — `azure/main.tf:70` puts the CSI disks under the same DES — which is exactly what makes the un-filtered expectation indistinguishable. → **§2.1**.
2. **The `azure` root re-exports `cluster_name`**, which Task 4 Step 3's first command consumes. P18 covers the module's outputs, not the root's. Verified in-round: `azure/main.tf:75`. No finding.
3. **`docs/runbooks/` is not gitignored and the new chart directory is not caught by `.gitignore:74`**, so both plain `git add`s in Tasks 3 and 4 succeed. No P item covers either. Verified in-round with `git check-ignore` and `git ls-files`. No finding.
4. **The whole-tree `terraform fmt -recursive` in Tasks 1–3 rewrites nothing outside the file each task commits.** P10 covers only the two module files the tasks edit. Verified in-round: `terraform fmt -check -recursive Iverson.Server/deploy/terraform` on the pristine tree exits 0, so `bootstrap/` and the three roots are already canonical. No finding.
5. **Both CI files remain valid YAML and the new steps land in the intended jobs.** P7 covers what CI runs today, not what the insertions produce. Verified in-round by applying both edits and parsing with PyYAML. No finding.
6. **Task 4 Step 4's numeric locator is consumed after Steps 1–3 have already changed the file's length.** No P item covers intra-task line-number stability. Verified in-round by simulating Steps 1–3. → **§2.2**.

## 2. Literal-wrongness findings

### 2.1 Task 4 Step 3's Azure command cannot fail in the way the plan's own re-derivation trigger requires, and evidences nothing check 3 does not already cover

**Description.** The spec's section 4 words check 5's Azure variant as: "list the disks in the node resource group …, **filter to OS disks**, and expect `encryption.type` of `EncryptionAtRestWithCustomerKey` and the same set id check 2 shows." Task 4 Step 3 drops the filter and expands the expectation to the whole listing, justifying it thus:

> That group holds both the scale-set OS disks this check exists for and the CSI-provisioned data disks check 3 reads; both are under the same set, so a single expectation covers the listing.

Two things break, in opposite directions.

*The stated failure signal becomes unreachable.* The same step ends with the plan's escape hatch, carried over from the round-1 design decision the user approved (option (a): ship the command, verify at first apply, re-derive if it comes back empty):

> **If this returns no rows, the check is not satisfied and must be re-derived against the agent pool's scale set**

That trigger was written for a filtered command, where "no rows" means "AKS does not expose node OS disks as standalone `Microsoft.Compute/disks` resources" — the proposition the spec's A10 explicitly declines to settle and section 4 explicitly defers to the first Azure apply. Un-filtered, the command lists the CSI-provisioned data disks as well, and those are created in the node resource group by `disk.csi.azure.com` for every bound PVC. By the time an auditor runs check 5, every store in the chart has bound volumes, so the listing is never empty. The unsettled question therefore produces a **green table** rather than the empty one the plan told the operator to watch for, and the re-derivation obligation the design committed to can never fire.

*The check stops attesting what it exists to attest.* Runbook check 5's own framing (`docs/runbooks/at-rest-encryption-verification.md:155-159`) is that it covers "not the PersistentVolumes checks 1–4 cover", and that "All four PVC checks above can pass in full while this one fails." The plan's Azure variant satisfies its expectation from rows that check 3 already reads (`:118-125` resolves the same CSI disks from `kubectl get pv`), so on Azure that independence is gone: check 5 passes on the strength of check 3's evidence. The spec's stated outcome for this amendment — that an auditor gets node-disk evidence on Azure — is not delivered by a table that need contain no node disk.

Note this is not the spec-level uncertainty being re-opened. The design's decision (ship the per-disk command, verify empirically, re-derive if empty) is settled and is not questioned here; what the plan does is remove the mechanism that decision depends on.

**Evidence.**
- Plan, Task 4 Step 3, Azure block: `az disk list --resource-group "$NODE_RG" --query '[].{name:name,encryptionType:encryption.type,diskEncryptionSetId:encryption.diskEncryptionSetId}' -o table`, followed by "Expected: every row shows …" and "**If this returns no rows** …".
- Spec `docs/specs/2026-09-08-at-rest-encryption-node-disks-and-allowlist-design.md:196-198`: "list the disks in the node resource group …, **filter to OS disks**, and expect …".
- Spec `:199-204`: "Nothing in this repository establishes that AKS exposes each node's OS disk as a standalone disk resource in that group … If it returns no rows, check 5's Azure variant must be re-derived against the agent pool's scale set before the runbook is handed to an auditor."
- `Iverson.Server/deploy/terraform/azure/main.tf:66-71` — the Azure StorageClass config is `provisioner = "disk.csi.azure.com"` with `diskEncryptionSetID = module.cluster.data_volumes_des_id`, i.e. the very same `azurerm_disk_encryption_set.data_volumes` the cluster attribute names. Every CSI data disk therefore satisfies the plan's row expectation on its own, and the StorageClass sets no `resourceGroup` parameter, so those disks are created in the node resource group.
- `docs/runbooks/at-rest-encryption-verification.md:118-125` — check 3's Azure variant already reads `encryption.type` and `encryption.diskEncryptionSetId` from exactly those disks.
- `docs/runbooks/at-rest-encryption-verification.md:155-159` — check 5's stated independence from checks 1–4.

**Proposed fix.** Restore a discriminator so the command's row set is the OS disks, and so an empty result still means what the plan says it means. Options, in the plan's own idiom:

- **(a) Filter, as the spec words it.** AKS names scale-set OS disks `…_OsDisk_…`, so `az disk list -g "$NODE_RG" --query "[?contains(name, 'OsDisk')].{…}" -o table` (or `[?ends_with(name, …)]`) restores both properties: the rows are OS disks, and "no rows" recovers its meaning as the re-derivation trigger. Cost: the JMESPath filter itself is unverified against a real node RG, which is the same acceptance the design already made for the command as a whole.
- **(b) Keep the un-filtered listing but change the trigger.** Replace "if this returns no rows" with a positive assertion the operator can check — e.g. "expect one row per node, named after that node's scale-set instance; if the listing contains only the CSI data disks check 3 already covers, the Azure variant must be re-derived against the agent pool's scale set". Costs nothing at execution time and keeps the plan's "single expectation covers the listing" simplification, but requires the runbook to say how to tell the two producers apart.
- **(c) Cross-reference instead of filtering.** Keep the listing and add a preceding `kubectl get pv -o jsonpath='{.items[*].spec.csi.volumeHandle}'` so the auditor can subtract check 3's disks from the table; whatever remains is the node-disk evidence. Longest of the three, and duplicates check 3's first command.

### 2.2 Task 4 Step 4 is the only edit in Task 4 located by line number, and its numbers are both under-scoped and stale by the time it runs

**Description.** Step 4 reads:

> **Step 4: Repoint the closing section** (`:186-188`). Keep the `## What this evidence set does not cover` heading. Replace its body with: …

Two problems with `(:186-188)`, which is the only positional locator Task 4 uses after Step 1.

*It under-scopes the region.* The section's body is `docs/runbooks/at-rest-encryption-verification.md:188-197` — ten lines, the whole closing paragraph. `:188` is where the paragraph *starts*, not where it ends (the spec's A23 cites it that way, as "`:186` closing heading and `:188` its paragraph"). An agent that treats `:186-188` as the replacement range leaves `:189-197` in place, and those lines are precisely the ones the amendment exists to remove: "Check 5 above is AWS-only. On Azure, AKS's `default_node_pool` sets no `disk_encryption_set_id`; on GCP, no node pool in `cluster-gcp` names a customer-managed key for its boot disk." The runbook would then assert, three paragraphs after Step 3 added Azure and GCP node-disk commands, that no such commands exist — and would contradict the coverage sentence Step 4 itself inserts directly above.

*It is stale by the time Step 4 executes.* Steps 1–3 all modify the same file first: Step 1 replaces three lines with two (−1), Step 2 appends ~31 lines to section 4, Step 3 appends ~34 lines to section 5. Simulating Steps 1–2 against the real runbook puts `## What this evidence set does not cover` at line **217**, and Step 3's block moves it to roughly **251**. Line 186 in the post-Step-3 file is inside check 5's new GCP variant. Steps 2 and 3 avoid this by anchoring on heading text (`## 4. No volume escaped the set`, `## 5. Node root volumes`); Step 4 does not, and Step 1's `:12-14` is safe only because it runs first.

The heading text in Step 4's prose is an unambiguous anchor, so a careful agent recovers — but the plan is explicitly written for "a fresh agent with no context", and it is the only step in the plan whose numeric locator points at the wrong place at the moment it is consumed.

**Evidence.**
- Plan, Task 4 Step 4 first line: "**Step 4: Repoint the closing section** (`:186-188`)."
- `docs/runbooks/at-rest-encryption-verification.md` is 197 lines; `:186` is the heading, `:187` blank, `:188-197` the body. `sed -n '188,197p'` is a single paragraph beginning "**AKS and GKE node OS disks are not covered by this runbook, and not by design.**".
- Simulated Steps 1–2 against the real file: `## What this evidence set does not cover` lands at line 217; Step 3's block (34 lines) puts it near 251.
- Contrast Task 4 Steps 2 and 3, which say "Append to the end of section `## 4. No volume escaped the set`" and "Append to the end of section `## 5. Node root volumes`, before the closing section" — heading anchors, immune to the shift.

**Proposed fix.** Replace the locator with the same kind of anchor the neighbouring steps use, and name the region's extent: "**Step 4: Repoint the closing section.** Keep the `## What this evidence set does not cover` heading and delete the entire paragraph beneath it — the ten lines from `**AKS and GKE node OS disks are not covered by this runbook, and not by design.**` to the end of the file — replacing them with:". No content change; only the locator.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

n/a — first round for this plan basename. (`docs/criticalreviews/` holds two reviews of the source spec, `…-design-critical-review-1.md` and `-2.md`, and one for the predecessor's plan; nothing for this plan.)

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §1 has no failed assumptions (P1's "four files" and P18's "the root adds no more" are inexact wording over evidence that still supports each assumption's substance), §2 has two items, §3 is empty. Both §2 items live in Task 4 and neither touches Tasks 1–3, which reproduced exactly as claimed: all three cloud roots validate with every planned edit applied, the chart lints and renders the intended CEL list literal, and the rendered pair passes `kubeconform` against Kubernetes 1.30. §2.1 needs a decision on which of its three options to take before Task 4 is executed; §2.2 is a locator rewrite.
