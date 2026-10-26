// Spec: /home/ben/repositories/Iverson/docs/specs/2026-10-26-csr-round12-supply-chain-design.md @ f6f5db2cbea0425d938739f73a8d6e9147479682
// Source: §6 Rollout (and §2.2 line 57), lines 187-192
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-26-csr-round12-supply-chain-design/RolloutOrder.dfy
// Round: 1

datatype Action = Merge | ScopeGitLabKey | Step2Checks | DeleteDependabotSecret | EnablePushProtection | CreateRulesetDisabled | ReadBackRuleset | ActivateRuleset | RemoveCollaborator | ApplyBranchProtection | ApplyAwsBootstrap

// Step 2 (line 190): merge, GitLab scoping only if a GitLab project, then checks.
function Step2Part(gitlabProject: bool): seq<Action> {
  [Merge] + (if gitlabProject then [ScopeGitLabKey] else []) + [Step2Checks]
}

// Step 3 (line 191): in the spec's listed order.
function Step3Part(step2Pass: bool, adminShown: bool, all22Green: bool): seq<Action> {
  (if step2Pass then [DeleteDependabotSecret] else [])
  + [EnablePushProtection, CreateRulesetDisabled, ReadBackRuleset]
  + (if adminShown then [ActivateRuleset] else [])
  + [RemoveCollaborator]
  + (if all22Green then [ApplyBranchProtection] else [])
}

// Step 4 (line 192).
function Step4Part(): seq<Action> { [ApplyAwsBootstrap] }

function Rollout(gitlabProject: bool, step2Pass: bool, adminShown: bool, all22Green: bool): seq<Action> {
  Step2Part(gitlabProject) + Step3Part(step2Pass, adminShown, all22Green) + Step4Part()
}

// Citation: "delete the Dependabot `NVD_API_KEY` (only after step 2's keyless pass)" (line 191)
lemma P1_DependabotSecretOnlyAfterPass(g: bool, s2: bool, a: bool, green: bool)
  requires DeleteDependabotSecret in Rollout(g, s2, a, green)
  ensures s2 && exists i, j :: 0 <= i < j < |Rollout(g, s2, a, green)| && Rollout(g, s2, a, green)[i] == Step2Checks && Rollout(g, s2, a, green)[j] == DeleteDependabotSecret
{
  var r := Rollout(g, s2, a, green);
  if !s2 {
    assert {:contradiction} DeleteDependabotSecret !in r;
  }
  var k := |Step2Part(g)|;
  assert r[k - 1] == Step2Checks;
  assert r[k] == DeleteDependabotSecret;
}

// Citation: "switches it to `active` only with `--activate` once the read-back shows the admin role" (line 57)
lemma P2_ActivateOnlyAfterAdminReadBack(g: bool, s2: bool, a: bool, green: bool)
  requires ActivateRuleset in Rollout(g, s2, a, green)
  ensures a && exists i, j :: 0 <= i < j < |Rollout(g, s2, a, green)| && Rollout(g, s2, a, green)[i] == ReadBackRuleset && Rollout(g, s2, a, green)[j] == ActivateRuleset
{
  var r := Rollout(g, s2, a, green);
  if !a {
    assert {:contradiction} ActivateRuleset !in r;
  }
  var base := |Step2Part(g)| + (if s2 then 1 else 0) + 2;
  assert r[base] == ReadBackRuleset;
  assert r[base + 1] == ActivateRuleset;
}

// Citation: "branch protection last, once all 22 checks are green" (line 191)
lemma P3_BranchProtectionLastAndGreen(g: bool, s2: bool, a: bool, green: bool)
  requires ApplyBranchProtection in Rollout(g, s2, a, green)
  ensures green
  ensures forall i :: 0 <= i < |Rollout(g, s2, a, green)| && Rollout(g, s2, a, green)[i] in {DeleteDependabotSecret, EnablePushProtection, CreateRulesetDisabled, ReadBackRuleset, ActivateRuleset, RemoveCollaborator} ==> exists j :: i < j < |Rollout(g, s2, a, green)| && Rollout(g, s2, a, green)[j] == ApplyBranchProtection
{
  var r := Rollout(g, s2, a, green);
  if !green {
    assert {:contradiction} ApplyBranchProtection !in r;
  }
  var n := |r|;
  assert r[n - 2] == ApplyBranchProtection;
  forall i | 0 <= i < n && r[i] in {DeleteDependabotSecret, EnablePushProtection, CreateRulesetDisabled, ReadBackRuleset, ActivateRuleset, RemoveCollaborator}
    ensures exists j :: i < j < n && r[j] == ApplyBranchProtection
  {
    assert i < n - 2;
  }
}

// Citation: "this comes after the merge because before it no job declares `environment: nvd-update` (VA 53). Then, checked on `main`:" (line 190)
lemma P4_GitLabScopingBetweenMergeAndChecks(s2: bool, a: bool, green: bool)
  ensures exists i, j, k :: 0 <= i < j < k < |Rollout(true, s2, a, green)| && Rollout(true, s2, a, green)[i] == Merge && Rollout(true, s2, a, green)[j] == ScopeGitLabKey && Rollout(true, s2, a, green)[k] == Step2Checks
{
  var r := Rollout(true, s2, a, green);
  assert r[0] == Merge && r[1] == ScopeGitLabKey && r[2] == Step2Checks;
}

// Citation: "first, where a GitLab project runs this pipeline (confirmed by the user at the step)" (line 190)
lemma P5_NoGitLabStepWithoutProject(s2: bool, a: bool, green: bool)
  ensures ScopeGitLabKey !in Rollout(false, s2, a, green)
{
}
