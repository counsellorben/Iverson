// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md, T5 S1
// Source: .github/workflows/publish-images.yml the run: block of step 'Check whether the tag is published', and the case $BIND of step 'Verify the existing tag'
// Replay base: a6c008dce9e4c846f8c34456ddd6297636d31979
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-26-csr-round12-supply-chain-implementation-plan/PublishResiduals.dfy

// ---------------------------------------------------------------------------
// Strings: Bash strings as byte sequences.
// ---------------------------------------------------------------------------

newtype Byte = x: int | 0 <= x < 256
type Str = seq<Byte>

datatype Option<T> = None | Some(value: T)

// The script's literals, as bytes.
const Status200: Str := [50, 48, 48]                                   // "200"
const Status404: Str := [52, 48, 52]                                   // "404"
const BindCommit: Str := [99, 111, 109, 109, 105, 116]                 // "commit"
const BindRelease: Str := [114, 101, 108, 101, 97, 115, 101]           // "release"
const BindContent: Str := [99, 111, 110, 116, 101, 110, 116]           // "content"
// "--certificate-github-workflow-sha"
const ShaFlag: Str := [45, 45, 99, 101, 114, 116, 105, 102, 105, 99, 97, 116, 101, 45, 103, 105, 116, 104, 117, 98, 45, 119, 111, 114, 107, 102, 108, 111, 119, 45, 115, 104, 97]
// "--certificate-identity"
const IdentityFlag: Str := [45, 45, 99, 101, 114, 116, 105, 102, 105, 99, 97, 116, 101, 45, 105, 100, 101, 110, 116, 105, 116, 121]
// "--certificate-identity-regexp"
const IdentityRegexpFlag: Str := [45, 45, 99, 101, 114, 116, 105, 102, 105, 99, 97, 116, 101, 45, 105, 100, 101, 110, 116, 105, 116, 121, 45, 114, 101, 103, 101, 120, 112]
// "@refs/tags/v"
const RefsTagsV: Str := [64, 114, 101, 102, 115, 47, 116, 97, 103, 115, 47, 118]
// IDENTITY: '^https://github\.com/counsellorben/Iverson/\.github/workflows/publish-images\.yml@refs/(heads/main|tags/v.+)$'
const Identity: Str := [94, 104, 116, 116, 112, 115, 58, 47, 47, 103, 105, 116, 104, 117, 98, 92, 46, 99, 111, 109, 47, 99, 111, 117, 110, 115, 101, 108, 108, 111, 114, 98, 101, 110, 47, 73, 118, 101, 114, 115, 111, 110, 47, 92, 46, 103, 105, 116, 104, 117, 98, 47, 119, 111, 114, 107, 102, 108, 111, 119, 115, 47, 112, 117, 98, 108, 105, 115, 104, 45, 105, 109, 97, 103, 101, 115, 92, 46, 121, 109, 108, 64, 114, 101, 102, 115, 47, 40, 104, 101, 97, 100, 115, 47, 109, 97, 105, 110, 124, 116, 97, 103, 115, 47, 118, 46, 43, 41, 36]
// WORKFLOW: https://github.com/counsellorben/Iverson/.github/workflows/publish-images.yml
const Workflow: Str := [104, 116, 116, 112, 115, 58, 47, 47, 103, 105, 116, 104, 117, 98, 46, 99, 111, 109, 47, 99, 111, 117, 110, 115, 101, 108, 108, 111, 114, 98, 101, 110, 47, 73, 118, 101, 114, 115, 111, 110, 47, 46, 103, 105, 116, 104, 117, 98, 47, 119, 111, 114, 107, 102, 108, 111, 119, 115, 47, 112, 117, 98, 108, 105, 115, 104, 45, 105, 109, 97, 103, 101, 115, 46, 121, 109, 108]

datatype Cert = Cert(identity: Str, workflowSha: Str)

// ---------------------------------------------------------------------------
// Approved axioms (library boundary).
// ---------------------------------------------------------------------------

// `docker buildx imagetools inspect "$REF" --format '{{json .Manifest}}' 2>"$err"` on attempt `attempt`:
// (exit status 0, stdout, stderr).
function Inspect(ref: Str, attempt: int): (bool, Str, Str)

// grep -Fq -- "$REF: not found" "$err" || grep -Eq 'manifest unknown|name unknown|MANIFEST_UNKNOWN|NAME_UNKNOWN' "$err"
predicate NotFoundText(ref: Str, err: Str)

// grep -Eq 'denied:|unauthorized:|401 Unauthorized|403 Forbidden' "$err"
predicate DeniedText(err: Str)

// curl -s -o /dev/null -w '%{http_code}' ... ".../packages/container/$NAME"
function PackagesApi(name: Str): Str

// printf '%s' "$manifest" | python3 -c '... .get("digest", "")'
function DigestOf(manifest: Str): Str

// [[ $digest =~ ^sha256:[0-9a-f]{64}$ ]]
predicate IsSha256Digest(d: Str)

// [[ $TAG =~ ^[0-9a-f]{40}$ ]]
predicate IsCommitSha(tag: Str)

// [[ $TAG =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]
predicate IsXYZ(tag: Str)

// cosign verify "${cert_flags[@]}" ... && cosign verify-attestation ... "${cert_flags[@]}" ... both pass
predicate CosignAccepts(flags: seq<Str>, cert: Cert)

lemma {:axiom} ShaFlagBinds(flags: seq<Str>, i: int, cert: Cert)
  requires 0 <= i < |flags| - 1 && flags[i] == ShaFlag && CosignAccepts(flags, cert)
  ensures cert.workflowSha == flags[i + 1]

// ---------------------------------------------------------------------------
// Transliteration: step 'Check whether the tag is published' (publish-images.yml:128-171).
// ---------------------------------------------------------------------------

// The step's outcome: exit 1; exit 0 with no `digest` output; exit 0 with `digest=<d>`.
datatype LookupResult = LookupFail | LookupBuild | LookupFound(digest: Str)

// The shell variable `state` (with `manifest` carried for the found state).
datatype LoopState = Unset | Found(manifest: Str) | Absent | Denied

// The for loop's outcome: `exit 1` inside the loop, or falling out of / breaking from it.
datatype LoopOut = LoopExit1 | LoopDone(state: LoopState)

// for try in 1 2 3; do ... done   (lines 135-151)
function Loop(ref: Str, try: int, state: LoopState): LoopOut
  requires 1 <= try <= 4
  decreases 4 - try
{
  if try > 3 then LoopDone(state)
  else
    var r := Inspect(ref, try);
    if r.0 then LoopDone(Found(r.1))                         // state=found; break
    else if NotFoundText(ref, r.2) then LoopDone(Absent)     // state=absent; break
    else if DeniedText(r.2) then Loop(ref, try + 1, Denied)  // state=denied; sleep 5 (not modelled)
    else LoopExit1                                           // cat; ::error::; exit 1
}

// The whole run: block (lines 129-171).
function Lookup(ref: Str, name: Str): LookupResult
{
  match Loop(ref, 1, Unset)
  case LoopExit1 => LookupFail
  case LoopDone(state) =>
    match state
    case Found(manifest) =>
      var digest := DigestOf(manifest);
      if IsSha256Digest(digest) then LookupFound(digest) else LookupFail
    case Absent => LookupBuild
    case Denied =>
      var status := PackagesApi(name);
      if status == Status404 then LookupBuild
      else if status == Status200 then LookupFail
      else LookupFail
    case Unset => LookupBuild  // no case arm matches: exit 0, no digest output (unreachable)
}

// ---------------------------------------------------------------------------
// Transliteration: `case $BIND in` of step 'Verify the existing tag' (publish-images.yml:197-210).
// The cert_flags array, or None for `exit 1`.
// ---------------------------------------------------------------------------

function CertFlags(bind: Str, tag: Str, githubSha: Str): Option<seq<Str>>
{
  if bind == BindCommit then
    if !IsCommitSha(tag) then None
    else Some([IdentityRegexpFlag, Identity, ShaFlag, tag])
  else if bind == BindRelease then
    if !IsXYZ(tag) then None
    else Some([IdentityFlag, Workflow + RefsTagsV + tag, ShaFlag, githubSha])
  else if bind == BindContent then
    Some([IdentityRegexpFlag, Identity])
  else None
}

// ---------------------------------------------------------------------------
// Helper lemmas.
// ---------------------------------------------------------------------------

predicate DeniedAt(ref: Str, k: int)
{
  !Inspect(ref, k).0 && !NotFoundText(ref, Inspect(ref, k).2) && DeniedText(Inspect(ref, k).2)
}

predicate NotFoundAt(ref: Str, k: int)
{
  !Inspect(ref, k).0 && NotFoundText(ref, Inspect(ref, k).2)
}

lemma AllDeniedLoop(ref: Str)
  requires DeniedAt(ref, 1) && DeniedAt(ref, 2) && DeniedAt(ref, 3)
  ensures Loop(ref, 1, Unset) == LoopDone(Denied)
{
  assert Loop(ref, 4, Denied) == LoopDone(Denied);
  assert Loop(ref, 3, Denied) == Loop(ref, 4, Denied);
  assert Loop(ref, 2, Denied) == Loop(ref, 3, Denied);
  assert Loop(ref, 1, Unset) == Loop(ref, 2, Denied);
}

lemma LoopCases(ref: Str)
  ensures Loop(ref, 1, Unset) == LoopDone(Absent) ==>
            NotFoundAt(ref, 1)
            || (DeniedAt(ref, 1) && NotFoundAt(ref, 2))
            || (DeniedAt(ref, 1) && DeniedAt(ref, 2) && NotFoundAt(ref, 3))
  ensures Loop(ref, 1, Unset) == LoopDone(Denied) ==> DeniedAt(ref, 1) && DeniedAt(ref, 2) && DeniedAt(ref, 3)
  ensures Loop(ref, 1, Unset) != LoopDone(Unset)
{
  var l4 := Loop(ref, 4, Denied);
  assert l4 == LoopDone(Denied);
  var l3 := Loop(ref, 3, Denied);
  assert l3 == LoopDone(Denied) ==> DeniedAt(ref, 3);
  assert l3 == LoopDone(Absent) ==> NotFoundAt(ref, 3);
  assert l3 != LoopDone(Unset);
  var l2 := Loop(ref, 2, Denied);
  assert l2 == LoopDone(Denied) ==> DeniedAt(ref, 2) && l3 == LoopDone(Denied);
  assert l2 == LoopDone(Absent) ==> NotFoundAt(ref, 2) || (DeniedAt(ref, 2) && l3 == LoopDone(Absent));
  assert l2 != LoopDone(Unset);
}

// ---------------------------------------------------------------------------
// Properties (frozen).
// ---------------------------------------------------------------------------

// Citation: design PublishResiduals.P1_DeniedThriceExistingPackageFails: "200 → the package exists, so the denial is a real failure: fail the run." (spec line 64); Expected `api=200 rc=1` (plan T5 S2).
lemma P1_DeniedThriceExistingPackageFails(ref: Str, name: Str)
  requires forall k :: 1 <= k <= 3 ==> !Inspect(ref, k).0 && !NotFoundText(ref, Inspect(ref, k).2) && DeniedText(Inspect(ref, k).2)
  requires PackagesApi(name) == Status200
  ensures Lookup(ref, name) == LookupFail
{
  assert DeniedAt(ref, 1) && DeniedAt(ref, 2) && DeniedAt(ref, 3);
  AllDeniedLoop(ref);
}

// Citation: design PublishResiduals.P2_DeniedThriceAbsentPackageBuilds: "404 → absent, build." (spec line 64); Expected `api=404 rc=0 … building it`.
lemma P2_DeniedThriceAbsentPackageBuilds(ref: Str, name: Str)
  requires forall k :: 1 <= k <= 3 ==> !Inspect(ref, k).0 && !NotFoundText(ref, Inspect(ref, k).2) && DeniedText(Inspect(ref, k).2)
  requires PackagesApi(name) == Status404
  ensures Lookup(ref, name) == LookupBuild
{
  assert DeniedAt(ref, 1) && DeniedAt(ref, 2) && DeniedAt(ref, 3);
  AllDeniedLoop(ref);
}

// Citation: design PublishResiduals.P3_BuildOnlyWhenTagAbsent: "The publish workflow never moves a published tag" (Goals, spec line 26).
lemma P3_BuildOnlyWhenTagAbsent(ref: Str, name: Str)
  requires Lookup(ref, name) == LookupBuild
  ensures (exists k :: 1 <= k <= 3 && !Inspect(ref, k).0 && NotFoundText(ref, Inspect(ref, k).2) && forall j :: 1 <= j < k ==> !Inspect(ref, j).0 && !NotFoundText(ref, Inspect(ref, j).2) && DeniedText(Inspect(ref, j).2))
       || ((forall k :: 1 <= k <= 3 ==> !Inspect(ref, k).0 && !NotFoundText(ref, Inspect(ref, k).2) && DeniedText(Inspect(ref, k).2)) && PackagesApi(name) == Status404)
{
  LoopCases(ref);
  var l := Loop(ref, 1, Unset);
  assert l == LoopDone(Absent) || (l == LoopDone(Denied) && PackagesApi(name) == Status404);
  if l == LoopDone(Absent) {
    if NotFoundAt(ref, 1) {
      assert forall j :: 1 <= j < 1 ==> DeniedAt(ref, j);
    } else if DeniedAt(ref, 1) && NotFoundAt(ref, 2) {
      assert forall j :: 1 <= j < 2 ==> DeniedAt(ref, j);
    } else {
      assert DeniedAt(ref, 1) && DeniedAt(ref, 2) && NotFoundAt(ref, 3);
      assert forall j :: 1 <= j < 3 ==> DeniedAt(ref, j);
    }
  } else {
    assert forall k :: 1 <= k <= 3 ==> DeniedAt(ref, k);
  }
}

// Citation: design PublishResiduals.P4_OtherApiAnswerFails: "Any other answer fails the run." (spec line 64); Expected `api=500 rc=1`, `api=000 rc=1`.
lemma P4_OtherApiAnswerFails(ref: Str, name: Str)
  requires forall k :: 1 <= k <= 3 ==> !Inspect(ref, k).0 && !NotFoundText(ref, Inspect(ref, k).2) && DeniedText(Inspect(ref, k).2)
  requires PackagesApi(name) != Status200 && PackagesApi(name) != Status404
  ensures Lookup(ref, name) == LookupFail
{
  assert DeniedAt(ref, 1) && DeniedAt(ref, 2) && DeniedAt(ref, 3);
  AllDeniedLoop(ref);
}

// Citation: design PublishResiduals.P5_ReleaseBindsCommit: "The existing-tag check for `bind=release` adds `--certificate-github-workflow-sha "$GITHUB_SHA"`" (spec line 65); Expected "one line binding the release check to `$GITHUB_SHA`" (plan T5 S2).
lemma P5_ReleaseBindsCommit(tag: Str, githubSha: Str, cert: Cert)
  requires CertFlags(BindRelease, tag, githubSha).Some? && CosignAccepts(CertFlags(BindRelease, tag, githubSha).value, cert)
  ensures cert.workflowSha == githubSha
{
  assert BindRelease != BindCommit by { assert BindRelease[0] != BindCommit[0]; }
  var flags := CertFlags(BindRelease, tag, githubSha).value;
  assert flags == [IdentityFlag, Workflow + RefsTagsV + tag, ShaFlag, githubSha];
  ShaFlagBinds(flags, 2, cert);
}
