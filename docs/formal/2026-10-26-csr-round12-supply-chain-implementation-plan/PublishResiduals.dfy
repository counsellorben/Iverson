// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md, T5 S1
// Source: .github/workflows/publish-images.yml the run: block of step 'Check whether the tag is published', and the case $BIND of step 'Verify the existing tag'
// Code ref: d0eab48b734ab0c7023d50d11a58dd51167028fa
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-26-csr-round12-supply-chain-implementation-plan/PublishResiduals.dfy

// ---------------------------------------------------------------------------------------------
// Strings: Bash strings are byte strings.
// ---------------------------------------------------------------------------------------------
newtype Byte = x: int | 0 <= x < 256
type Str = seq<Byte>

datatype Option<T> = None | Some(value: T)

// Converts an ASCII Dafny literal to bytes (used only for literals no proof needs to compare).
function B(s: string): Str
{
  if |s| == 0 then [] else [((s[0] as int) % 256) as Byte] + B(s[1..])
}

// Literals the proofs compare, written byte by byte.
const Status200: Str := [50, 48, 48]                        // "200"
const Status404: Str := [52, 48, 52]                        // "404"
const BindCommit: Str := [99, 111, 109, 109, 105, 116]      // "commit"
const BindRelease: Str := [114, 101, 108, 101, 97, 115, 101] // "release"
const BindContent: Str := [99, 111, 110, 116, 101, 110, 116] // "content"

// Flag and env literals of step 'Verify the existing tag'.
const IdentityRegexpFlag: Str := B("--certificate-identity-regexp")
const IdentityFlag: Str := B("--certificate-identity")
const ShaFlag: Str := B("--certificate-github-workflow-sha")
const IDENTITY: Str := B(@"^https://github\.com/counsellorben/Iverson/\.github/workflows/publish-images\.yml@refs/(heads/main|tags/v.+)$")
const WORKFLOW: Str := B("https://github.com/counsellorben/Iverson/.github/workflows/publish-images.yml")
const RefsTagsV: Str := B("@refs/tags/v")

datatype Cert = Cert(identity: Str, workflowSha: Str)

// ---------------------------------------------------------------------------------------------
// Approved axioms (library boundary).
// ---------------------------------------------------------------------------------------------
// `docker buildx imagetools inspect "$REF" --format '{{json .Manifest}}' 2>"$err"` on attempt `try`:
// (exit 0, stdout, stderr).
function Inspect(ref: Str, attempt: int): (bool, Str, Str)

// `grep -Fq -- "$REF: not found" "$err" || grep -Eq 'manifest unknown|name unknown|MANIFEST_UNKNOWN|NAME_UNKNOWN' "$err"`
predicate NotFoundText(ref: Str, err: Str)

// `grep -Eq 'denied:|unauthorized:|401 Unauthorized|403 Forbidden' "$err"`
predicate DeniedText(err: Str)

// `curl -s -o /dev/null -w '%{http_code}' ... ".../packages/container/$NAME"` stdout.
function PackagesApi(name: Str): Str

// `printf '%s' "$manifest" | python3 -c '... .get("digest", "")'` stdout.
function DigestOf(manifest: Str): Str

// `[[ $digest =~ ^sha256:[0-9a-f]{64}$ ]]`
predicate IsSha256Digest(d: Str)

// `[[ $TAG =~ ^[0-9a-f]{40}$ ]]`
predicate IsCommitSha(tag: Str)

// `[[ $TAG =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]`
predicate IsXYZ(tag: Str)

// `cosign verify "${cert_flags[@]}" ...` and `cosign verify-attestation ... "${cert_flags[@]}" ...` both pass.
predicate CosignAccepts(flags: seq<Str>, cert: Cert)

lemma {:axiom} ShaFlagBinds(flags: seq<Str>, i: int, cert: Cert)
  requires 0 <= i < |flags| - 1 && flags[i] == ShaFlag && CosignAccepts(flags, cert)
  ensures cert.workflowSha == flags[i + 1]

// ---------------------------------------------------------------------------------------------
// Transliteration: step 'Check whether the tag is published' (publish-images.yml:128-171).
// ---------------------------------------------------------------------------------------------
// `state` after the `for try in 1 2 3` loop, or the loop's `exit 1`.
datatype LoopState = SFound(manifest: Str) | SAbsent | SDenied | SFail

// One iteration `try` of the loop and the iterations after it.
function Loop(ref: Str, try: int): LoopState
  decreases 3 - try
{
  var r := Inspect(ref, try);
  if r.0 then SFound(r.1)                                   // state=found; break
  else if NotFoundText(ref, r.2) then SAbsent               // state=absent; break
  else if DeniedText(r.2) then                              // state=denied; sleep unless last
    (if try < 3 then Loop(ref, try + 1) else SDenied)
  else SFail                                                // cat; ::error::; exit 1
}

// The step's outcome: exit 1, exit 0 with no digest output (build), or exit 0 with digest.
datatype LookupOutcome = LookupFail | LookupBuild | LookupFound(digest: Str)

function Lookup(ref: Str, name: Str): LookupOutcome
{
  match Loop(ref, 1)
  case SFail => LookupFail
  case SFound(manifest) =>
    var digest := DigestOf(manifest);
    if IsSha256Digest(digest) then LookupFound(digest) else LookupFail
  case SAbsent => LookupBuild
  case SDenied =>
    var status := PackagesApi(name);
    if status == Status404 then LookupBuild
    else if status == Status200 then LookupFail
    else LookupFail
}

// ---------------------------------------------------------------------------------------------
// Transliteration: `case $BIND` of step 'Verify the existing tag' (publish-images.yml:197-210).
// ---------------------------------------------------------------------------------------------
function CertFlags(bind: Str, tag: Str, githubSha: Str): Option<seq<Str>>
{
  if bind == BindCommit then
    (if !IsCommitSha(tag) then None
     else Some([IdentityRegexpFlag, IDENTITY, ShaFlag, tag]))
  else if bind == BindRelease then
    (if !IsXYZ(tag) then None
     else Some([IdentityFlag, WORKFLOW + RefsTagsV + tag, ShaFlag, githubSha]))
  else if bind == BindContent then
    Some([IdentityRegexpFlag, IDENTITY])
  else None
}

// ---------------------------------------------------------------------------------------------
// Helper lemmas.
// ---------------------------------------------------------------------------------------------
predicate DeniedAt(ref: Str, k: int)
{
  !Inspect(ref, k).0 && !NotFoundText(ref, Inspect(ref, k).2) && DeniedText(Inspect(ref, k).2)
}

lemma AllDeniedLoop(ref: Str)
  requires forall k :: 1 <= k <= 3 ==> !Inspect(ref, k).0 && !NotFoundText(ref, Inspect(ref, k).2) && DeniedText(Inspect(ref, k).2)
  ensures Loop(ref, 1) == SDenied
{
  assert DeniedAt(ref, 1) && DeniedAt(ref, 2) && DeniedAt(ref, 3);
  assert Loop(ref, 3) == SDenied;
  assert Loop(ref, 2) == Loop(ref, 3);
  assert Loop(ref, 1) == Loop(ref, 2);
}

lemma LoopAbsentOrDenied(ref: Str)
  ensures Loop(ref, 1) == SAbsent ==>
    exists k :: 1 <= k <= 3 && !Inspect(ref, k).0 && NotFoundText(ref, Inspect(ref, k).2) && forall j :: 1 <= j < k ==> !Inspect(ref, j).0 && !NotFoundText(ref, Inspect(ref, j).2) && DeniedText(Inspect(ref, j).2)
  ensures Loop(ref, 1) == SDenied ==>
    forall k :: 1 <= k <= 3 ==> !Inspect(ref, k).0 && !NotFoundText(ref, Inspect(ref, k).2) && DeniedText(Inspect(ref, k).2)
{
  var r1, r2, r3 := Inspect(ref, 1), Inspect(ref, 2), Inspect(ref, 3);
  if !r1.0 && NotFoundText(ref, r1.2) {
    assert Loop(ref, 1) == SAbsent;
    assert forall j :: 1 <= j < 1 ==> DeniedAt(ref, j);
    assert 1 <= 1 <= 3 && !Inspect(ref, 1).0 && NotFoundText(ref, Inspect(ref, 1).2);
  } else if !r1.0 && DeniedText(r1.2) {
    assert DeniedAt(ref, 1);
    assert Loop(ref, 1) == Loop(ref, 2);
    if !r2.0 && NotFoundText(ref, r2.2) {
      assert Loop(ref, 2) == SAbsent;
      assert forall j :: 1 <= j < 2 ==> DeniedAt(ref, j);
      assert 1 <= 2 <= 3 && !Inspect(ref, 2).0 && NotFoundText(ref, Inspect(ref, 2).2);
    } else if !r2.0 && DeniedText(r2.2) {
      assert DeniedAt(ref, 2);
      assert Loop(ref, 2) == Loop(ref, 3);
      if !r3.0 && NotFoundText(ref, r3.2) {
        assert Loop(ref, 3) == SAbsent;
        assert forall j :: 1 <= j < 3 ==> DeniedAt(ref, j);
        assert 1 <= 3 <= 3 && !Inspect(ref, 3).0 && NotFoundText(ref, Inspect(ref, 3).2);
      } else if !r3.0 && DeniedText(r3.2) {
        assert DeniedAt(ref, 3);
        assert Loop(ref, 3) == SDenied;
      } else {
        assert Loop(ref, 3) != SAbsent && Loop(ref, 3) != SDenied;
      }
    } else {
      assert Loop(ref, 2) != SAbsent && Loop(ref, 2) != SDenied;
    }
  } else {
    assert Loop(ref, 1) != SAbsent && Loop(ref, 1) != SDenied;
  }
}

// ---------------------------------------------------------------------------------------------
// Properties.
// ---------------------------------------------------------------------------------------------
// Citation: design PublishResiduals.P1_DeniedThriceExistingPackageFails: "200 → the package exists, so the denial is a real failure: fail the run." (spec line 64); Expected `api=200 rc=1` (plan T5 S2).
lemma P1_DeniedThriceExistingPackageFails(ref: Str, name: Str)
  requires forall k :: 1 <= k <= 3 ==> !Inspect(ref, k).0 && !NotFoundText(ref, Inspect(ref, k).2) && DeniedText(Inspect(ref, k).2)
  requires PackagesApi(name) == Status200
  ensures Lookup(ref, name) == LookupFail
{
  AllDeniedLoop(ref);
  assert Status200 != Status404;
}

// Citation: design PublishResiduals.P2_DeniedThriceAbsentPackageBuilds: "404 → absent, build." (spec line 64); Expected `api=404 rc=0 … building it`.
lemma P2_DeniedThriceAbsentPackageBuilds(ref: Str, name: Str)
  requires forall k :: 1 <= k <= 3 ==> !Inspect(ref, k).0 && !NotFoundText(ref, Inspect(ref, k).2) && DeniedText(Inspect(ref, k).2)
  requires PackagesApi(name) == Status404
  ensures Lookup(ref, name) == LookupBuild
{
  AllDeniedLoop(ref);
}

// Citation: design PublishResiduals.P3_BuildOnlyWhenTagAbsent: "The publish workflow never moves a published tag" (Goals, spec line 26).
lemma P3_BuildOnlyWhenTagAbsent(ref: Str, name: Str)
  requires Lookup(ref, name) == LookupBuild
  ensures (exists k :: 1 <= k <= 3 && !Inspect(ref, k).0 && NotFoundText(ref, Inspect(ref, k).2) && forall j :: 1 <= j < k ==> !Inspect(ref, j).0 && !NotFoundText(ref, Inspect(ref, j).2) && DeniedText(Inspect(ref, j).2))
       || ((forall k :: 1 <= k <= 3 ==> !Inspect(ref, k).0 && !NotFoundText(ref, Inspect(ref, k).2) && DeniedText(Inspect(ref, k).2)) && PackagesApi(name) == Status404)
{
  LoopAbsentOrDenied(ref);
  match Loop(ref, 1)
  case SAbsent =>
  case SDenied =>
    assert PackagesApi(name) == Status404;
  case SFail =>
    assert false;
  case SFound(m) =>
    assert false;
}

// Citation: design PublishResiduals.P4_OtherApiAnswerFails: "Any other answer fails the run." (spec line 64); Expected `api=500 rc=1`, `api=000 rc=1`.
lemma P4_OtherApiAnswerFails(ref: Str, name: Str)
  requires forall k :: 1 <= k <= 3 ==> !Inspect(ref, k).0 && !NotFoundText(ref, Inspect(ref, k).2) && DeniedText(Inspect(ref, k).2)
  requires PackagesApi(name) != Status200 && PackagesApi(name) != Status404
  ensures Lookup(ref, name) == LookupFail
{
  AllDeniedLoop(ref);
}

// Citation: design PublishResiduals.P5_ReleaseBindsCommit: "The existing-tag check for `bind=release` adds `--certificate-github-workflow-sha "$GITHUB_SHA"`" (spec line 65); Expected "one line binding the release check to `$GITHUB_SHA`" (plan T5 S2).
lemma P5_ReleaseBindsCommit(tag: Str, githubSha: Str, cert: Cert)
  requires CertFlags(BindRelease, tag, githubSha).Some? && CosignAccepts(CertFlags(BindRelease, tag, githubSha).value, cert)
  ensures cert.workflowSha == githubSha
{
  assert BindRelease != BindCommit;
  var flags := CertFlags(BindRelease, tag, githubSha).value;
  assert flags == [IdentityFlag, WORKFLOW + RefsTagsV + tag, ShaFlag, githubSha];
  ShaFlagBinds(flags, 2, cert);
}
