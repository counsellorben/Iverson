// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md, T6 S1
// Source: Iverson.Server/deploy/scripts/apply-tag-ruleset.sh the whole script
// Replay base: a6c008dce9e4c846f8c34456ddd6297636d31979
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-26-csr-round12-supply-chain-implementation-plan/TagRuleset.dfy

// ---------------------------------------------------------------------------
// Strings: bytes (Bash has no string unit of its own; as for Go).
// ---------------------------------------------------------------------------
newtype Byte = x: int | 0 <= x < 256
type Str = seq<Byte>

// Named literals: the script's literals as bytes.
// "counsellorben/Iverson" (line 21)
const DefaultRepo: Str := [99, 111, 117, 110, 115, 101, 108, 108, 111, 114, 98, 101, 110, 47, 73, 118, 101, 114, 115, 111, 110]
// "--repo" (line 26)
const RepoArg: Str := [45, 45, 114, 101, 112, 111]
// "--dry-run" (line 31)
const DryRunArg: Str := [45, 45, 100, 114, 121, 45, 114, 117, 110]
// "--activate" (line 35)
const ActivateArg: Str := [45, 45, 97, 99, 116, 105, 118, 97, 116, 101]
// "-h" and "--help" (line 39)
const ShortHelpArg: Str := [45, 104]
const HelpArg: Str := [45, 45, 104, 101, 108, 112]
// ".." in the pattern *..* (line 49)
const DotDot: Str := [46, 46]
// "current_user_can_bypass=always " (line 100; note the trailing space)
const CanBypassAlways: Str := [99, 117, 114, 114, 101, 110, 116, 95, 117, 115, 101, 114, 95, 99, 97, 110, 95, 98, 121, 112, 97, 115, 115, 61, 97, 108, 119, 97, 121, 115, 32]
// "bypass_actors=RepositoryRole:5:always" (line 100)
const AdminRoleActor: Str := [98, 121, 112, 97, 115, 115, 95, 97, 99, 116, 111, 114, 115, 61, 82, 101, 112, 111, 115, 105, 116, 111, 114, 121, 82, 111, 108, 101, 58, 53, 58, 97, 108, 119, 97, 121, 115]
const Newline: Byte := 10

// ---------------------------------------------------------------------------
// Library boundary.
// ---------------------------------------------------------------------------
datatype Call = List | Post | Get(id: Str) | Put(id: Str)
datatype Run = Run(calls: seq<Call>, exit: int)

// The --repo ERE ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ (line 49): axiomatised.
predicate RepoShape(repo: Str)

lemma {:axiom} DefaultRepoShape()
  ensures RepoShape(DefaultRepo)

// [[ $s == *"t"* ]] (and $s != *..*): t occurs in s, i.e. t is a prefix of some suffix of s.
predicate Contains(s: Str, t: Str)
  decreases |s|
{
  |t| <= |s| && (s[..|t|] == t || (|s| > 0 && Contains(s[1..], t)))
}

// printf '%s\n' "$s" | grep -c . : the number of non-empty lines of s.
// A non-empty line starts at each position k with s[k] != '\n' and (k == 0 or s[k-1] == '\n');
// the trailing '\n' printf adds only terminates the last line.
function NonEmptyLines(s: Str): nat
  decreases |s|
{
  if |s| == 0 then 0
  else NonEmptyLines(s[..|s| - 1])
       + (if s[|s| - 1] != Newline && (|s| == 1 || s[|s| - 2] == Newline) then 1 else 0)
}

// ---------------------------------------------------------------------------
// Transliteration.
// ---------------------------------------------------------------------------

// Lines 21-48: argument parsing. ParseExit(c) is `usage; exit c` (or -h/--help's exit 0).
datatype Parse = Parsed(repo: Str, dryRun: bool, activate: bool) | ParseExit(code: int)

function ParseArgs(args: seq<Str>, repo: Str, dryRun: bool, activate: bool): Parse
  decreases |args|
{
  if |args| == 0 then Parsed(repo, dryRun, activate)                     // while [[ $# -gt 0 ]]
  else if args[0] == RepoArg then                                          // --repo)
    if |args| >= 2 then ParseArgs(args[2..], args[1], dryRun, activate)    //   repo=$2; shift 2
    else ParseExit(2)                                                      //   [[ $# -ge 2 ]] || { usage; exit 2; }
  else if args[0] == DryRunArg then ParseArgs(args[1..], repo, true, activate)   // --dry-run)
  else if args[0] == ActivateArg then ParseArgs(args[1..], repo, dryRun, true)   // --activate)
  else if args[0] == ShortHelpArg || args[0] == HelpArg then ParseExit(0)        // -h|--help) usage; exit 0
  else ParseExit(2)                                                              // *) usage; exit 2
}

// Lines 95-105: the read-back GET, then the --activate check and PUT.
function ReadBack(calls: seq<Call>, id: Str, activate: bool, readback: Str): Run
{
  var calls1 := calls + [Get(id)];                                                         // line 96
  if activate then                                                                         // line 99
    if !(Contains(readback, CanBypassAlways) && Contains(readback, AdminRoleActor)) then  // line 100
      Run(calls1, 1)                                                                       // lines 101-102
    else
      Run(calls1 + [Put(id)], 0)                                                           // line 104
  else
    Run(calls1, 0)                                                                         // end of script
}

// The whole script. listOut: the list call's captured output ($id, line 81); postId: the POST's (line 86);
// readback: the GET's (line 96).
function TagRuleset(args: seq<Str>, listOut: Str, postId: Str, readback: Str): Run
{
  match ParseArgs(args, DefaultRepo, false, false)                   // lines 21-48
  case ParseExit(c) => Run([], c)
  case Parsed(repo, dryRun, activate) =>
    if !(RepoShape(repo) && !Contains(repo, DotDot)) then Run([], 2)  // line 49
    else if dryRun then Run([], 0)                                    // lines 74-78
    else
      var calls := [List];                                            // line 81
      var id := listOut;
      if !(NonEmptyLines(id) <= 1) then Run(calls, 1)                 // line 82
      else if !activate then                                          // line 84
        if |id| == 0 then                                             // line 85
          ReadBack(calls + [Post], postId, activate, readback)        // lines 86-87, then 95-
        else
          ReadBack(calls, id, activate, readback)                     // line 89, then 95-
      else if !(|id| > 0) then Run(calls, 1)                          // line 92
      else ReadBack(calls, id, activate, readback)                    // then 95-
}

// ---------------------------------------------------------------------------
// Helper lemmas.
// ---------------------------------------------------------------------------

lemma ParseActivateFromArg(args: seq<Str>, repo: Str, dryRun: bool, activate: bool)
  requires ParseArgs(args, repo, dryRun, activate).Parsed?
  requires ParseArgs(args, repo, dryRun, activate).activate
  ensures activate || ActivateArg in args
  decreases |args|
{
  if |args| == 0 {
  } else if args[0] == RepoArg {
    ParseActivateFromArg(args[2..], args[1], dryRun, activate);
    if ActivateArg in args[2..] {
      var k :| 0 <= k < |args[2..]| && args[2..][k] == ActivateArg;
      assert args[k + 2] == ActivateArg;
    }
  } else if args[0] == DryRunArg {
    ParseActivateFromArg(args[1..], repo, true, activate);
    if ActivateArg in args[1..] {
      var k :| 0 <= k < |args[1..]| && args[1..][k] == ActivateArg;
      assert args[k + 1] == ActivateArg;
    }
  } else if args[0] == ActivateArg {
    assert args[0] in args;
  } else {
  }
}

lemma ParseNoActivate(args: seq<Str>, repo: Str, dryRun: bool)
  requires ActivateArg !in args
  requires ParseArgs(args, repo, dryRun, false).Parsed?
  ensures !ParseArgs(args, repo, dryRun, false).activate
{
  if ParseArgs(args, repo, dryRun, false).activate {
    ParseActivateFromArg(args, repo, dryRun, false);
  }
}

lemma NoDotNoDotDot(s: Str)
  requires forall k :: 0 <= k < |s| ==> s[k] != 46
  ensures !Contains(s, DotDot)
  decreases |s|
{
  if |DotDot| <= |s| {
    assert s[..|DotDot|][0] == s[0];
    NoDotNoDotDot(s[1..]);
  }
}

lemma DefaultRepoAccepted()
  ensures RepoShape(DefaultRepo) && !Contains(DefaultRepo, DotDot)
{
  DefaultRepoShape();
  NoDotNoDotDot(DefaultRepo);
}

lemma OneLine(s: Str)
  requires |s| > 0 && forall k :: 0 <= k < |s| ==> s[k] != 10
  ensures NonEmptyLines(s) == 1
  decreases |s|
{
  if |s| == 1 {
    assert NonEmptyLines(s[..0]) == 0;
  } else {
    OneLine(s[..|s| - 1]);
  }
}

// Every run the script can produce is one of these shapes (Put only last, right after the matching Get).
lemma Shape(args: seq<Str>, listOut: Str, postId: Str, readback: Str)
  ensures var r := TagRuleset(args, listOut, postId, readback);
    (forall c :: c in r.calls ==> !c.Put?)
    || (exists pre: seq<Call>, id: Str ::
          r.calls == pre + [Get(id), Put(id)]
          && (forall c :: c in pre ==> !c.Put?)
          && ParseArgs(args, DefaultRepo, false, false).Parsed?
          && ParseArgs(args, DefaultRepo, false, false).activate
          && Contains(readback, CanBypassAlways) && Contains(readback, AdminRoleActor))
{
  var r := TagRuleset(args, listOut, postId, readback);
  var p := ParseArgs(args, DefaultRepo, false, false);
  if p.Parsed? && RepoShape(p.repo) && !Contains(p.repo, DotDot) && !p.dryRun
     && NonEmptyLines(listOut) <= 1 && p.activate && |listOut| > 0
     && Contains(readback, CanBypassAlways) && Contains(readback, AdminRoleActor) {
    assert r.calls == [List] + [Get(listOut), Put(listOut)];
    forall c | c in [List] ensures !c.Put? {}
  }
}

// ---------------------------------------------------------------------------
// Properties (frozen).
// ---------------------------------------------------------------------------

// Citation: design RolloutOrder.P2_ActivateOnlyAfterAdminReadBack: "switches it to `active` only with `--activate` once the read-back shows the admin role" (spec line 57).
lemma P1_PutOnlyAfterAdminReadBack(args: seq<Str>, listOut: Str, postId: Str, readback: Str, i: int)
  requires 0 <= i < |TagRuleset(args, listOut, postId, readback).calls| && TagRuleset(args, listOut, postId, readback).calls[i].Put?
  ensures ActivateArg in args && Contains(readback, CanBypassAlways) && Contains(readback, AdminRoleActor)
  ensures exists j :: 0 <= j < i && TagRuleset(args, listOut, postId, readback).calls[j] == Get(TagRuleset(args, listOut, postId, readback).calls[i].id)
{
  var r := TagRuleset(args, listOut, postId, readback);
  Shape(args, listOut, postId, readback);
  assert r.calls[i] in r.calls;
  var pre: seq<Call>, id: Str :| r.calls == pre + [Get(id), Put(id)]
    && (forall c :: c in pre ==> !c.Put?)
    && ParseArgs(args, DefaultRepo, false, false).Parsed?
    && ParseArgs(args, DefaultRepo, false, false).activate
    && Contains(readback, CanBypassAlways) && Contains(readback, AdminRoleActor);
  ParseActivateFromArg(args, DefaultRepo, false, false);
  assert |r.calls| == |pre| + 2;
  forall k | 0 <= k < |pre|
    ensures !r.calls[k].Put?
  {
    assert r.calls[k] == pre[k];
    assert pre[k] in pre;
  }
  assert !r.calls[|pre|].Put?;
  assert i == |pre| + 1;
  assert r.calls[i] == Put(id);
  assert r.calls[|pre|] == Get(id);
}

// Citation: Expected `[none ] rc=0 put=0 post=1`, `[disabled-ok ] rc=0 put=0 post=0` (plan T6 S2).
lemma P2_NoActivateNoPut(args: seq<Str>, listOut: Str, postId: Str, readback: Str)
  requires ActivateArg !in args
  ensures forall c :: c in TagRuleset(args, listOut, postId, readback).calls ==> !c.Put?
{
  var p := ParseArgs(args, DefaultRepo, false, false);
  if p.Parsed? {
    ParseNoActivate(args, DefaultRepo, false);
  }
  Shape(args, listOut, postId, readback);
}

// Citation: Expected `[none ] rc=0 put=0 post=1`.
lemma P3_AbsentRulesetCreatedOnce(postId: Str, readback: Str)
  ensures TagRuleset([], [], postId, readback) == Run([List, Post, Get(postId)], 0)
{
  DefaultRepoAccepted();
  assert NonEmptyLines([]) == 0;
}

// Citation: Expected `[disabled-ok ] rc=0 put=0 post=0`.
lemma P4_ExistingRulesetLeftUnchanged(listOut: Str, postId: Str, readback: Str)
  requires |listOut| > 0 && forall k :: 0 <= k < |listOut| ==> listOut[k] != 10
  ensures TagRuleset([], listOut, postId, readback) == Run([List, Get(listOut)], 0)
{
  DefaultRepoAccepted();
  OneLine(listOut);
}

// Citation: Expected `[disabled-ok --activate] rc=0 put=1 post=0`, `[disabled-never --activate] rc=1 put=0 post=0`, `[none --activate] rc=1 put=0 post=0`.
lemma P5_ActivateOutcomes(listOut: Str, postId: Str, readback: Str)
  requires |listOut| > 0 && forall k :: 0 <= k < |listOut| ==> listOut[k] != 10
  ensures Contains(readback, CanBypassAlways) && Contains(readback, AdminRoleActor) ==> TagRuleset([ActivateArg], listOut, postId, readback) == Run([List, Get(listOut), Put(listOut)], 0)
  ensures !Contains(readback, CanBypassAlways) ==> TagRuleset([ActivateArg], listOut, postId, readback) == Run([List, Get(listOut)], 1)
  ensures TagRuleset([ActivateArg], [], postId, readback) == Run([List], 1)
{
  DefaultRepoAccepted();
  OneLine(listOut);
  assert NonEmptyLines([]) == 0;
  assert ParseArgs([ActivateArg], DefaultRepo, false, false) == Parsed(DefaultRepo, false, true) by {
    assert [ActivateArg][1..] == [];
  }
}
