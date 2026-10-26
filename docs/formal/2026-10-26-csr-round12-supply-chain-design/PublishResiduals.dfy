// Spec: /home/ben/repositories/Iverson/docs/specs/2026-10-26-csr-round12-supply-chain-design.md @ f6f5db2cbea0425d938739f73a8d6e9147479682
// Source: §2.3 items 1-3 (and Goals line 26, identity regexp lines 91 and 103), lines 63-66
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-26-csr-round12-supply-chain-design/PublishResiduals.dfy
// Round: 1

datatype Answer = Found | NotFound | Denied | OtherError
datatype ApiAnswer = Api200 | Api404 | ApiOther
datatype Outcome = Build | CheckExisting | Fail
datatype Bind = Commit | Release | Content
datatype Cert = Cert(identity: string, workflowSha: string)

const Workflow: string

// Approved axiom: the identity regexp of lines 91/103 (opaque).
predicate IdentityMatchesRegexp(identity: string)

// Line 64: after the third denial, query the package API.
function ApiOutcome(api: ApiAnswer): Outcome
{
  match api
  case Api404 => Build   // 404 -> absent, build
  case Api200 => Fail    // 200 -> package exists, real failure
  case ApiOther => Fail  // any other answer fails the run
}

// Line 64: three registry tries in order. Found -> existing signed-tag check;
// NotFound -> build; Denied -> try again; after the third denial, the API query.
// A non-denial, non-found, non-notfound answer (OtherError) fails the run.
function LookupFrom(answers: seq<Answer>, i: nat, api: ApiAnswer): Outcome
  requires i <= |answers|
  decreases |answers| - i
{
  if i == |answers| then ApiOutcome(api)
  else match answers[i]
    case Found => CheckExisting
    case NotFound => Build
    case Denied => LookupFrom(answers, i + 1, api)
    case OtherError => Fail
}

function Lookup(answers: seq<Answer>, api: ApiAnswer): Outcome
  requires |answers| == 3
{
  LookupFrom(answers, 0, api)
}

// Line 189 / 72-73 (workflow), items 2: commit binds sha to the tag; release binds exact
// identity and workflow sha to GITHUB_SHA; content keeps the regexp only.
function WorkflowAccepts(b: Bind, tag: string, githubSha: string, c: Cert): bool
{
  match b
  case Commit => IdentityMatchesRegexp(c.identity) && c.workflowSha == tag
  case Release => c.identity == Workflow + "@refs/tags/v" + tag && c.workflowSha == githubSha
  case Content => IdentityMatchesRegexp(c.identity)
}

// Item 3: runbook. Commit: sha <tag>; release: exact identity + sha of git rev-list -n1 vX.Y.Z.
function RunbookAccepts(b: Bind, tag: string, tagCommit: string, c: Cert): bool
{
  match b
  case Commit => IdentityMatchesRegexp(c.identity) && c.workflowSha == tag
  case Release => c.identity == Workflow + "@refs/tags/v" + tag && c.workflowSha == tagCommit
  case Content => IdentityMatchesRegexp(c.identity)
}

lemma AllDeniedLookup(answers: seq<Answer>, i: nat, api: ApiAnswer)
  requires i <= |answers|
  requires forall k :: i <= k < |answers| ==> answers[k] == Denied
  ensures LookupFrom(answers, i, api) == ApiOutcome(api)
  decreases |answers| - i
{
  if i < |answers| {
    AllDeniedLookup(answers, i + 1, api);
  }
}

lemma LookupFromBuild(answers: seq<Answer>, i: nat, api: ApiAnswer)
  requires i <= |answers|
  requires LookupFrom(answers, i, api) == Build
  ensures (exists k :: i <= k < |answers| && answers[k] == NotFound && (forall j :: i <= j < k ==> answers[j] == Denied))
       || ((forall k :: i <= k < |answers| ==> answers[k] == Denied) && api == Api404)
  decreases |answers| - i
{
  if i == |answers| {
    assert api == Api404;
  } else {
    match answers[i]
    case Found =>
    case NotFound =>
      assert i <= i < |answers| && answers[i] == NotFound;
    case OtherError =>
    case Denied =>
      LookupFromBuild(answers, i + 1, api);
      if exists k :: i + 1 <= k < |answers| && answers[k] == NotFound && (forall j :: i + 1 <= j < k ==> answers[j] == Denied) {
        var k :| i + 1 <= k < |answers| && answers[k] == NotFound && (forall j :: i + 1 <= j < k ==> answers[j] == Denied);
        assert forall j :: i <= j < k ==> answers[j] == Denied;
      }
  }
}

// Citation: "200 → the package exists, so the denial is a real failure: fail the run." (§2.3 item 1, line 64)
lemma P1_DeniedThriceExistingPackageFails(answers: seq<Answer>)
  requires |answers| == 3 && forall i :: 0 <= i < 3 ==> answers[i] == Denied
  ensures Lookup(answers, Api200) == Fail
{
  AllDeniedLookup(answers, 0, Api200);
}

// Citation: "after the third denial, query `GET /users/counsellorben/packages/container/<name>` with the job's token (`packages: read`). 404 → absent, build." (line 64)
lemma P2_DeniedThriceAbsentPackageBuilds(answers: seq<Answer>)
  requires |answers| == 3 && forall i :: 0 <= i < 3 ==> answers[i] == Denied
  ensures Lookup(answers, Api404) == Build
{
  AllDeniedLookup(answers, 0, Api404);
}

// Citation: "The publish workflow never moves a published tag" (Goals, line 26)
lemma P3_BuildOnlyWhenTagAbsent(answers: seq<Answer>, api: ApiAnswer)
  requires |answers| == 3
  requires Lookup(answers, api) == Build
  ensures (exists i :: 0 <= i < 3 && answers[i] == NotFound && (forall j :: 0 <= j < i ==> answers[j] == Denied))
       || ((forall i :: 0 <= i < 3 ==> answers[i] == Denied) && api == Api404)
{
  LookupFromBuild(answers, 0, api);
}

// Citation: "Any other answer fails the run." (line 64)
lemma P4_OtherApiAnswerFails(answers: seq<Answer>)
  requires |answers| == 3 && forall i :: 0 <= i < 3 ==> answers[i] == Denied
  ensures Lookup(answers, ApiOther) == Fail
{
  AllDeniedLookup(answers, 0, ApiOther);
}

// Citation: "binds release signatures to their commit" (Goals, line 26)
lemma P5_ReleaseBindsCommit(tag: string, githubSha: string, c: Cert)
  requires WorkflowAccepts(Release, tag, githubSha, c)
  ensures c.workflowSha == githubSha
{
}

// Citation: "its runbook verifies as strictly as the workflow" (Goals, line 26)
lemma P6_RunbookAsStrictAsWorkflow(b: Bind, tag: string, tagCommit: string, c: Cert)
  requires RunbookAccepts(b, tag, tagCommit, c)
  ensures WorkflowAccepts(b, tag, tagCommit, c)
{
}
