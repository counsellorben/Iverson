// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md, T6 S1
// Source: Iverson.Server/deploy/scripts/apply-tag-ruleset.sh the whole script
// Code ref: d0eab48b734ab0c7023d50d11a58dd51167028fa
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-26-csr-round12-supply-chain-implementation-plan/TagRuleset.dfy

// ---------------------------------------------------------------------------
// Strings: Bash values as byte sequences.
// ---------------------------------------------------------------------------
newtype Byte = x: int | 0 <= x < 256
type Str = seq<Byte>

const NL: Byte := 10

// Literals of the script, as bytes.
const RepoArg: Str := [45, 45, 114, 101, 112, 111] // "--repo"
const DryRunArg: Str := [45, 45, 100, 114, 121, 45, 114, 117, 110] // "--dry-run"
const ActivateArg: Str := [45, 45, 97, 99, 116, 105, 118, 97, 116, 101] // "--activate"
const ShortHelpArg: Str := [45, 104] // "-h"
const HelpArg: Str := [45, 45, 104, 101, 108, 112] // "--help"
const DefaultRepo: Str := [99, 111, 117, 110, 115, 101, 108, 108, 111, 114, 98, 101, 110, 47, 73, 118, 101, 114, 115, 111, 110] // "counsellorben/Iverson"
const DotDot: Str := [46, 46] // ".."
const CanBypassAlways: Str := [99, 117, 114, 114, 101, 110, 116, 95, 117, 115, 101, 114, 95, 99, 97, 110, 95, 98, 121, 112, 97, 115, 115, 61, 97, 108, 119, 97, 121, 115, 32] // "current_user_can_bypass=always "
const AdminRoleActor: Str := [98, 121, 112, 97, 115, 115, 95, 97, 99, 116, 111, 114, 115, 61, 82, 101, 112, 111, 115, 105, 116, 111, 114, 121, 82, 111, 108, 101, 58, 53, 58, 97, 108, 119, 97, 121, 115] // "bypass_actors=RepositoryRole:5:always"

// `" bypass_actors=RepositoryRole:5:always"`, with its leading space (line 101).
const AdminTail: Str := [32] + AdminRoleActor

// `[[ $s == *"t" ]]`: suffix test.
predicate EndsWith(s: Str, t: Str)
{
  |t| <= |s| && s[|s| - |t|..] == t
}

// `[[ $s == *"t"* ]]` / `[[ $s != *t* ]]`: substring test.
predicate Contains(s: Str, t: Str)
{
  exists k {:trigger s[k..k + |t|]} :: 0 <= k && k + |t| <= |s| && s[k..k + |t|] == t
}

// `grep -c .`: the number of non-empty lines (a final line without a newline counts).
// `cur` is whether the line being read so far is non-empty.
function CountNonEmptyLines(s: Str, cur: bool): nat
  decreases |s|
{
  if |s| == 0 then (if cur then 1 else 0)
  else if s[0] == NL then (if cur then 1 else 0) + CountNonEmptyLines(s[1..], false)
  else CountNonEmptyLines(s[1..], true)
}

// ---------------------------------------------------------------------------
// Library boundary.
// ---------------------------------------------------------------------------
// The `gh api` calls the script makes, in order. A failing `gh` call (which ends the run under
// `set -e`) is not modelled; each call succeeds with the output passed to TagRuleset.
datatype Call = List | Post | Get(id: Str) | Put(id: Str)
datatype Run = Run(calls: seq<Call>, exit: int)

// The `--repo` ERE `^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$` (approved axiom).
predicate RepoShape(repo: Str)

lemma {:axiom} DefaultRepoShape()
  ensures RepoShape(DefaultRepo)

// ---------------------------------------------------------------------------
// Transliteration.
// ---------------------------------------------------------------------------
// Lines 21-48: the option loop. `rest` is "$@" after the shifts so far.
datatype Parsed = ParseExit(code: int) | Opts(repo: Str, dryRun: bool, activate: bool)

function Parse(rest: seq<Str>, repo: Str, dryRun: bool, activate: bool): Parsed
  decreases |rest|
{
  if |rest| == 0 then Opts(repo, dryRun, activate)                 // while [[ $# -gt 0 ]]
  else if rest[0] == RepoArg then                                  // --repo)
    if !(|rest| >= 2) then ParseExit(2)                            //   [[ $# -ge 2 ]] || { usage; exit 2; }
    else Parse(rest[2..], rest[1], dryRun, activate)               //   repo=$2; shift 2
  else if rest[0] == DryRunArg then                                // --dry-run)
    Parse(rest[1..], repo, true, activate)                         //   dry_run=1; shift
  else if rest[0] == ActivateArg then                              // --activate)
    Parse(rest[1..], repo, dryRun, true)                           //   activate=1; shift
  else if rest[0] == ShortHelpArg || rest[0] == HelpArg then       // -h|--help)
    ParseExit(0)                                                   //   usage; exit 0
  else                                                             // *)
    ParseExit(2)                                                   //   usage; exit 2
}

// The whole script. `listOut`, `postId` and `readback` are the values the command substitutions
// on lines 81, 86 and 96 yield.
function TagRuleset(args: seq<Str>, listOut: Str, postId: Str, readback: Str): Run
{
  match Parse(args, DefaultRepo, false, false)                     // lines 21-23 defaults
  case ParseExit(code) => Run([], code)
  case Opts(repo, dryRun, activate) =>
    if !(RepoShape(repo) && !Contains(repo, DotDot)) then          // line 49
      Run([], 2)
    else if dryRun then                                            // lines 74-78
      Run([], 0)
    else
      var id := listOut;                                           // line 81
      var calls0 := [List];
      if !(CountNonEmptyLines(id + [NL], false) <= 1) then         // line 82
        Run(calls0, 1)
      else if !activate then                                       // line 84
        var calls1 := if |id| == 0 then calls0 + [Post] else calls0;  // lines 85-89: POST only when -z $id
        var id1 := if |id| == 0 then postId else id;               // line 86: id=$(... POST ...)
        var calls2 := calls1 + [Get(id1)];                         // line 96
        Run(calls2, 0)                                             // line 99: activate = 0
      else if !(|id| > 0) then                                     // line 92
        Run(calls0, 1)
      else
        var calls2 := calls0 + [Get(id)];                          // line 96
        if !(Contains(readback, CanBypassAlways) && EndsWith(readback, AdminTail)) then  // line 101
          Run(calls2, 1)                                           // lines 102-103
        else
          Run(calls2 + [Put(id)], 0)                               // line 105
}

// ---------------------------------------------------------------------------
// Helper lemmas.
// ---------------------------------------------------------------------------
lemma ParseActivateNeedsArg(rest: seq<Str>, repo: Str, dryRun: bool, activate: bool)
  ensures Parse(rest, repo, dryRun, activate).Opts? && Parse(rest, repo, dryRun, activate).activate
          ==> activate || ActivateArg in rest
  decreases |rest|
{
  if |rest| == 0 {
  } else if rest[0] == RepoArg {
    if |rest| >= 2 {
      ParseActivateNeedsArg(rest[2..], rest[1], dryRun, activate);
      if ActivateArg in rest[2..] {
        var k :| 0 <= k < |rest[2..]| && rest[2..][k] == ActivateArg;
        assert rest[k + 2] == ActivateArg;
      }
    }
  } else if rest[0] == DryRunArg {
    ParseActivateNeedsArg(rest[1..], repo, true, activate);
    if ActivateArg in rest[1..] {
      var k :| 0 <= k < |rest[1..]| && rest[1..][k] == ActivateArg;
      assert rest[k + 1] == ActivateArg;
    }
  } else if rest[0] == ActivateArg {
    assert rest[0] in rest;
  }
}

lemma ActivateNeedsArg(args: seq<Str>)
  ensures Parse(args, DefaultRepo, false, false).Opts? && Parse(args, DefaultRepo, false, false).activate
          ==> ActivateArg in args
{
  ParseActivateNeedsArg(args, DefaultRepo, false, false);
}

lemma DefaultRepoHasNoDotDot()
  ensures !Contains(DefaultRepo, DotDot)
{
  forall k {:trigger DefaultRepo[k..k + 2]} | 0 <= k && k + 2 <= |DefaultRepo|
    ensures DefaultRepo[k..k + 2] != DotDot
  {
    assert DefaultRepo[k..k + 2][0] == DefaultRepo[k];
    assert DefaultRepo[k] != 46 by {
      assert DefaultRepo[0] != 46 && DefaultRepo[1] != 46 && DefaultRepo[2] != 46 && DefaultRepo[3] != 46;
      assert DefaultRepo[4] != 46 && DefaultRepo[5] != 46 && DefaultRepo[6] != 46 && DefaultRepo[7] != 46;
      assert DefaultRepo[8] != 46 && DefaultRepo[9] != 46 && DefaultRepo[10] != 46 && DefaultRepo[11] != 46;
      assert DefaultRepo[12] != 46 && DefaultRepo[13] != 46 && DefaultRepo[14] != 46 && DefaultRepo[15] != 46;
      assert DefaultRepo[16] != 46 && DefaultRepo[17] != 46 && DefaultRepo[18] != 46 && DefaultRepo[19] != 46;
    }
  }
}

lemma DefaultRepoPasses()
  ensures RepoShape(DefaultRepo) && !Contains(DefaultRepo, DotDot)
{
  DefaultRepoShape();
  DefaultRepoHasNoDotDot();
}

// A newline-free line followed by one newline is one non-empty line when it, or the line
// read so far, is non-empty.
lemma CountOneLine(s: Str, cur: bool)
  requires forall k :: 0 <= k < |s| ==> s[k] != NL
  ensures CountNonEmptyLines(s + [NL], cur) == (if cur || |s| > 0 then 1 else 0)
  decreases |s|
{
  if |s| == 0 {
    assert s + [NL] == [NL];
    var one: Str := [NL];
    assert one[1..] == [];
  } else {
    assert (s + [NL])[0] == s[0];
    assert (s + [NL])[1..] == s[1..] + [NL];
    CountOneLine(s[1..], true);
  }
}

lemma TailImpliesActor(s: Str)
  requires EndsWith(s, AdminTail)
  ensures Contains(s, AdminRoleActor)
{
  var k := |s| - |AdminTail| + 1;
  assert s[|s| - |AdminTail|..] == AdminTail;
  assert s[k..k + |AdminRoleActor|] == AdminTail[1..];
  assert AdminTail[1..] == AdminRoleActor;
}

lemma ParseNoArgs()
  ensures Parse([], DefaultRepo, false, false) == Opts(DefaultRepo, false, false)
{
}

lemma ParseActivateOnly()
  ensures Parse([ActivateArg], DefaultRepo, false, false) == Opts(DefaultRepo, false, true)
{
  assert [ActivateArg][0] == ActivateArg;
  assert ActivateArg != RepoArg;
  assert ActivateArg != DryRunArg;
  assert [ActivateArg][1..] == [];
}

// ---------------------------------------------------------------------------
// Properties.
// ---------------------------------------------------------------------------
// Citation: design RolloutOrder.P2_ActivateOnlyAfterAdminReadBack: "switches it to `active` only with `--activate` once the read-back shows the admin role" (spec line 57).
lemma P1_PutOnlyAfterAdminReadBack(args: seq<Str>, listOut: Str, postId: Str, readback: Str, i: int)
  requires 0 <= i < |TagRuleset(args, listOut, postId, readback).calls| && TagRuleset(args, listOut, postId, readback).calls[i].Put?
  ensures ActivateArg in args && Contains(readback, CanBypassAlways) && Contains(readback, AdminRoleActor)
  ensures exists j :: 0 <= j < i && TagRuleset(args, listOut, postId, readback).calls[j] == Get(TagRuleset(args, listOut, postId, readback).calls[i].id)
{
  ActivateNeedsArg(args);
  var r := TagRuleset(args, listOut, postId, readback);
  var p := Parse(args, DefaultRepo, false, false);
  assert p.Opts? && RepoShape(p.repo) && !Contains(p.repo, DotDot) && !p.dryRun;
  assert CountNonEmptyLines(listOut + [NL], false) <= 1;
  assert p.activate && |listOut| > 0;
  assert Contains(readback, CanBypassAlways) && EndsWith(readback, AdminTail);
  TailImpliesActor(readback);
  assert r.calls == [List, Get(listOut), Put(listOut)];
  assert i == 2;
  assert r.calls[1] == Get(r.calls[i].id);
}

// Citation: Expected `[none ] rc=0 put=0 post=1`, `[disabled-ok ] rc=0 put=0 post=0` (plan T6 S2).
lemma P2_NoActivateNoPut(args: seq<Str>, listOut: Str, postId: Str, readback: Str)
  requires ActivateArg !in args
  ensures forall c :: c in TagRuleset(args, listOut, postId, readback).calls ==> !c.Put?
{
  ActivateNeedsArg(args);
}

// Citation: Expected `[none ] rc=0 put=0 post=1`.
lemma P3_AbsentRulesetCreatedOnce(postId: Str, readback: Str)
  ensures TagRuleset([], [], postId, readback) == Run([List, Post, Get(postId)], 0)
{
  ParseNoArgs();
  DefaultRepoPasses();
  CountOneLine([], false);
  var empty: Str := [];
  assert empty + [NL] == [NL];
}

// Citation: Expected `[disabled-ok ] rc=0 put=0 post=0`.
lemma P4_ExistingRulesetLeftUnchanged(listOut: Str, postId: Str, readback: Str)
  requires |listOut| > 0 && forall k :: 0 <= k < |listOut| ==> listOut[k] != 10
  ensures TagRuleset([], listOut, postId, readback) == Run([List, Get(listOut)], 0)
{
  ParseNoArgs();
  DefaultRepoPasses();
  CountOneLine(listOut, false);
}

// Citation: Expected `[disabled-ok --activate] rc=0 put=1 post=0`, `[disabled-never --activate] rc=1 put=0 post=0`, `[none --activate] rc=1 put=0 post=0`.
lemma P5_ActivateOutcomes(listOut: Str, postId: Str, readback: Str)
  requires |listOut| > 0 && forall k :: 0 <= k < |listOut| ==> listOut[k] != 10
  ensures Contains(readback, CanBypassAlways) && Contains(readback, AdminRoleActor) ==> TagRuleset([ActivateArg], listOut, postId, readback) == Run([List, Get(listOut), Put(listOut)], 0)
  ensures !Contains(readback, CanBypassAlways) ==> TagRuleset([ActivateArg], listOut, postId, readback) == Run([List, Get(listOut)], 1)
  ensures TagRuleset([ActivateArg], [], postId, readback) == Run([List], 1)
{
  ParseActivateOnly();
  DefaultRepoPasses();
  CountOneLine(listOut, false);
  CountOneLine([], false);
  var empty: Str := [];
  assert empty + [NL] == [NL];
}
