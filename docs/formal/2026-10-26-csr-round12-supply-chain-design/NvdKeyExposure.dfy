// Spec: /home/ben/repositories/Iverson/docs/specs/2026-10-26-csr-round12-supply-chain-design.md @ f6f5db2cbea0425d938739f73a8d6e9147479682
// Source: §2.1 The NVD key (and Goals line 24, Decision 5), lines 40-51
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-26-csr-round12-supply-chain-design/NvdKeyExposure.dfy
// Round: 1

datatype Platform = GitHub | GitLab
datatype Event = PullRequest | PushMain | Schedule
datatype Job = NuGetAudit | NpmAuditAdminUi | NpmAuditTsSdk | Govulncheck | Owasp | PipAuditPySdk | PipAuditAgents | NvdUpdate | DotnetBuildTest | TsSdkBuildTest

// Does the job run on this platform and event? updateSucceeded: nvd-update's outcome when it runs.
predicate Runs(pl: Platform, j: Job, e: Event, updateSucceeded: bool)
{
  match j
  case NvdUpdate =>
    // GitHub (line 41): if: github.event_name != 'pull_request'. GitLab (line 49): schedule only.
    if pl == GitHub then e != PullRequest else e == Schedule
  case Owasp =>
    if pl == GitHub then
      // line 46: every event, needs nvd-update, if: !cancelled() && (result == 'success' || 'skipped')
      // nvd-update is skipped on PullRequest; otherwise it ran and must have succeeded.
      (e == PullRequest || updateSucceeded)
    else
      // line 49: schedule only, needs: [nvd-update] (must succeed)
      e == Schedule && updateSucceeded
  case _ => true  // other jobs: not constrained by the rule; modelled as running on every event
}

// Only nvd-update receives the key (line 49 "it alone receives"; line 46 "with no key").
predicate HasKey(pl: Platform, j: Job, e: Event)
{
  j == NvdUpdate
}

// Jobs that run repository/dependency build code. nvd-update runs no repository code (line 42-43).
predicate ExecutesProjectCode(j: Job)
{
  j != NvdUpdate
}

// GitLab scan's first script line: fails when NVD_API_KEY is defined (even empty).
function GitLabScanProceeds(keyPresent: bool): bool
{
  !keyPresent
}

// Citation: "The NVD API key is never in a job that executes repository or dependency build code, on any event, on GitHub or GitLab (#10)." (Goals, line 24)
lemma P1_KeyNeverWithProjectCode(pl: Platform, j: Job, e: Event, ok: bool)
  requires Runs(pl, j, e, ok) && HasKey(pl, j, e)
  ensures !ExecutesProjectCode(j)
{
}

// Citation: "`owasp-dependency-check-java`** runs on every event" (line 46) and "the branch's PR run exercises the keyless `owasp-dependency-check-java` path" (line 203)
lemma P2_GitHubScanRunsOnPullRequests(ok: bool)
  ensures Runs(GitHub, Owasp, PullRequest, ok)
{
}

// Citation: "`if: ${{ !cancelled() && (needs.nvd-update.result == 'success' || needs.nvd-update.result == 'skipped') }}`" (line 46)
lemma P3_GitHubScanRunsWhenUpdateSucceeds(e: Event)
  ensures Runs(GitHub, Owasp, e, true)
{
}

// Citation: "so it alone receives the environment-scoped `NVD_API_KEY`" (line 49) and "with `NVD_API_KEY: ${{ secrets.NVD_API_KEY }}`" (line 45)
lemma P4_UpdateHoldsKeyWhenItRuns(pl: Platform, e: Event, ok: bool)
  requires Runs(pl, NvdUpdate, e, ok)
  ensures HasKey(pl, NvdUpdate, e)
{
}

// Citation: "its first script line `test -z \"${NVD_API_KEY+x}\" || { echo \"NVD_API_KEY must be scoped to the nvd-update environment\"; exit 1; }`" (line 49)
lemma P5_GitLabScanRefusesAKey(keyPresent: bool)
  ensures GitLabScanProceeds(keyPresent) <==> !keyPresent
{
}
