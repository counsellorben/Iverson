// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T25 S3
// Source: Iverson.Server/deploy/scripts/mesh-check.sh mtls_seen (70–76), the option parser (107–118)
// Replay base: 7c4d86687b557e7e33a52c30cc07e6728dfb4622
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-21-csr-round12-platform-implementation-plan/mesh-check.mtls_seen.dfy

// ---------------------------------------------------------------------------------------------
// Vocabulary: bash strings are byte sequences.
// ---------------------------------------------------------------------------------------------

newtype Byte = x: int | 0 <= x < 256

predicate IsByteString(s: string)
{
  forall i :: 0 <= i < |s| ==> s[i] as int < 256
}

function Bytes(s: string): seq<Byte>
  requires IsByteString(s)
{
  seq(|s|, i requires 0 <= i < |s| => s[i] as int as Byte)
}

// MatchAt(s, p, i): p occurs in s at offset i (ordinal, byte-wise).
predicate MatchAt(s: seq<Byte>, p: seq<Byte>, i: int)
{
  0 <= i <= |s| - |p| && s[i..i + |p|] == p
}

// Contains(s, p): p occurs in s at some offset.
predicate Contains(s: seq<Byte>, p: seq<Byte>)
{
  exists i :: 0 <= i <= |s| - |p| && MatchAt(s, p, i)
}

const Newline: seq<Byte> := [10 as Byte]

// The fixed strings of mtls_seen (lines 71–75).
const OpenedPrefix: seq<Byte> := Bytes("istio_tcp_connections_opened_total{")
const ReporterDest: seq<Byte> := Bytes("reporter=\"destination\"")
const MutualTls: seq<Byte> := Bytes("connection_security_policy=\"mutual_tls\"")
const SourceLabelHead: seq<Byte> := Bytes("source_workload=\"")
const DestLabelHead: seq<Byte> := Bytes("destination_workload=\"")
const Quote: seq<Byte> := Bytes("\"")

function SourceLabel(src: seq<Byte>): seq<Byte> { SourceLabelHead + src + Quote }
function DestLabel(dst: seq<Byte>): seq<Byte> { DestLabelHead + dst + Quote }

// The option words of the parser (lines 113–116).
const ReleaseFlag: seq<Byte> := Bytes("--release")
const NamespaceFlag: seq<Byte> := Bytes("--namespace")
const HFlag: seq<Byte> := Bytes("-h")
const HelpFlag: seq<Byte> := Bytes("--help")
const DefaultName: seq<Byte> := Bytes("iverson")

// ---------------------------------------------------------------------------------------------
// Library boundary (approved axiom): grep -qF on a text.
// ---------------------------------------------------------------------------------------------

function GrepQF(text: seq<Byte>, pat: seq<Byte>): bool
  ensures pat != [] && !Contains(pat, Newline) ==> (GrepQF(text, pat) <==> Contains(text, pat))

// ---------------------------------------------------------------------------------------------
// Pipeline model: each grep stage reads newline-terminated lines and keeps the matching ones.
// ---------------------------------------------------------------------------------------------

// grep -E '^istio_tcp_connections_opened_total\{': the line starts with the literal prefix.
predicate Opened(l: seq<Byte>)
{
  OpenedPrefix <= l
}

// One grep stage: keep, in order, the lines satisfying keep.
function Filter(ls: seq<seq<Byte>>, keep: seq<Byte> -> bool): seq<seq<Byte>>
{
  if ls == [] then []
  else (if keep(ls[0]) then [ls[0]] else []) + Filter(ls[1..], keep)
}

// The text a stage writes to the pipe: every kept line followed by a newline.
function Join(ls: seq<seq<Byte>>): seq<Byte>
{
  if ls == [] then [] else ls[0] + Newline + Join(ls[1..])
}

// ---------------------------------------------------------------------------------------------
// Transliteration: mtls_seen (mesh-check.sh 70–76).
// ---------------------------------------------------------------------------------------------

predicate MtlsSeen(lines: seq<seq<Byte>>, src: seq<Byte>, dst: seq<Byte>)
{
  var s1 := Filter(lines, l => Opened(l));                         // line 71: grep -E '^…opened_total\{' "$1"
  var s2 := Filter(s1, l => Contains(l, ReporterDest));            // line 72: grep -F 'reporter="destination"'
  var s3 := Filter(s2, l => Contains(l, SourceLabel(src)));        // line 73: grep -F "source_workload=\"$2\""
  var s4 := Filter(s3, l => Contains(l, DestLabel(dst)));          // line 74: grep -F "destination_workload=\"$3\""
  GrepQF(Join(s4), MutualTls)                                      // line 75: grep -qF '…="mutual_tls"'
}

// ---------------------------------------------------------------------------------------------
// Transliteration: the option parser (mesh-check.sh 107–118).
// ---------------------------------------------------------------------------------------------

datatype OptionsOutcome = Parsed(release: seq<Byte>, ns: seq<Byte>) | Exit(code: int)

// Lines 111–118: one iteration of the while/case per call; RELEASE and NS carried along.
function OptionsLoop(args: seq<seq<Byte>>, release: seq<Byte>, ns: seq<Byte>): OptionsOutcome
  decreases |args|
{
  if |args| == 0 then Parsed(release, ns)                                     // line 111: while [ $# -gt 0 ]
  else if args[0] == ReleaseFlag then                                          // line 113
    if |args| >= 2 then OptionsLoop(args[2..], args[1], ns) else Exit(2)
  else if args[0] == NamespaceFlag then                                        // line 114
    if |args| >= 2 then OptionsLoop(args[2..], release, args[1]) else Exit(2)
  else if args[0] == HFlag || args[0] == HelpFlag then Exit(0)                 // line 115
  else Exit(2)                                                                 // line 116
}

// Lines 36–37 (RELEASE=iverson, NS=iverson) and 111–118. Lines 107–109 return before the parser
// when the file is sourced; the script run as a command always reaches line 111.
function Options(args: seq<seq<Byte>>): OptionsOutcome
{
  OptionsLoop(args, DefaultName, DefaultName)
}

// ---------------------------------------------------------------------------------------------
// Helper lemmas.
// ---------------------------------------------------------------------------------------------

lemma FilterMembership(ls: seq<seq<Byte>>, keep: seq<Byte> -> bool, l: seq<Byte>)
  ensures l in Filter(ls, keep) <==> (l in ls && keep(l))
  decreases |ls|
{
  if ls != [] {
    FilterMembership(ls[1..], keep, l);
    assert ls == [ls[0]] + ls[1..];
  }
}

lemma ContainsOneByte(s: seq<Byte>, b: Byte)
  ensures Contains(s, [b]) <==> b in s
{
  if b in s {
    var i :| 0 <= i < |s| && s[i] == b;
    assert s[i..i + 1] == [b];
    assert MatchAt(s, [b], i);
  }
  if Contains(s, [b]) {
    var i :| 0 <= i <= |s| - |[b]| && MatchAt(s, [b], i);
    assert s[i] == s[i..i + 1][0] == b;
  }
}

lemma ContainsLeft(a: seq<Byte>, b: seq<Byte>, p: seq<Byte>)
  requires Contains(a, p)
  ensures Contains(a + b, p)
{
  var i :| 0 <= i <= |a| - |p| && MatchAt(a, p, i);
  assert (a + b)[i..i + |p|] == a[i..i + |p|];
  assert MatchAt(a + b, p, i);
}

lemma ContainsRight(a: seq<Byte>, b: seq<Byte>, p: seq<Byte>)
  requires Contains(b, p)
  ensures Contains(a + b, p)
{
  var i :| 0 <= i <= |b| - |p| && MatchAt(b, p, i);
  var j := |a| + i;
  assert (a + b)[j..j + |p|] == b[i..i + |p|];
  assert MatchAt(a + b, p, j);
}

// A newline-free pattern found in a ++ "\n" ++ b lies wholly in a or wholly in b.
lemma ContainsSplit(a: seq<Byte>, b: seq<Byte>, p: seq<Byte>)
  requires !Contains(p, Newline)
  requires Contains(a + Newline + b, p)
  ensures Contains(a, p) || Contains(b, p)
{
  var t := a + Newline + b;
  var i :| 0 <= i <= |t| - |p| && MatchAt(t, p, i);
  if i + |p| <= |a| {
    assert a[i..i + |p|] == t[i..i + |p|];
    assert MatchAt(a, p, i);
  } else if i >= |a| + 1 {
    var k := i - |a| - 1;
    assert b[k..k + |p|] == t[i..i + |p|];
    assert MatchAt(b, p, k);
  } else {
    // i <= |a| < i + |p|: the newline at t[|a|] falls inside the match.
    var d := |a| - i;
    assert 0 <= d < |p|;
    assert p[d] == t[i..i + |p|][d] == t[|a|] == 10 as Byte;
    ContainsOneByte(p, 10 as Byte);
  }
}

lemma ContainsJoin(ls: seq<seq<Byte>>, p: seq<Byte>)
  requires p != [] && !Contains(p, Newline)
  ensures Contains(Join(ls), p) <==> exists l :: l in ls && Contains(l, p)
  decreases |ls|
{
  if ls == [] {
    // Join([]) is empty, so no non-empty pattern occurs in it.
  } else {
    ContainsJoin(ls[1..], p);
    var rest := Join(ls[1..]);
    assert Join(ls) == ls[0] + Newline + rest;
    if Contains(Join(ls), p) {
      ContainsSplit(ls[0], rest, p);
      if Contains(ls[0], p) {
        assert ls[0] in ls;
      } else {
        var l :| l in ls[1..] && Contains(l, p);
        assert l in ls;
      }
    }
    if exists l :: l in ls && Contains(l, p) {
      var l :| l in ls && Contains(l, p);
      if l == ls[0] {
        ContainsLeft(ls[0], Newline + rest, p);
        assert ls[0] + (Newline + rest) == Join(ls);
      } else {
        assert l in ls[1..];
        ContainsRight(ls[0] + Newline, rest, p);
        assert (ls[0] + Newline) + rest == Join(ls);
      }
    }
  }
}

lemma MutualTlsShape()
  ensures MutualTls != [] && !Contains(MutualTls, Newline)
{
  ContainsOneByte(MutualTls, 10 as Byte);
  var s := "connection_security_policy=\"mutual_tls\"";
  assert MutualTls == Bytes(s);
  assert forall i :: 0 <= i < |s| ==> s[i] != '\n';
  assert 10 as Byte !in MutualTls;
}

lemma OptionsLoopCodes(args: seq<seq<Byte>>, release: seq<Byte>, ns: seq<Byte>)
  ensures OptionsLoop(args, release, ns).Exit? ==>
            (OptionsLoop(args, release, ns).code == 0 || OptionsLoop(args, release, ns).code == 2)
  decreases |args|
{
  if |args| >= 2 && (args[0] == ReleaseFlag || args[0] == NamespaceFlag) {
    if args[0] == ReleaseFlag {
      OptionsLoopCodes(args[2..], args[1], ns);
    } else {
      OptionsLoopCodes(args[2..], release, args[1]);
    }
  }
}

// ---------------------------------------------------------------------------------------------
// Properties (frozen).
// ---------------------------------------------------------------------------------------------

// Citation: tests "FE -> BE mutual TLS reported by the destination", "a source-side report does not count", "a plaintext connection does not count" (T25 S1), generalised; "ztunnel reporting mTLS for FE ↔ BE" (§6 line 518)
lemma P1_MtlsSeen(lines: seq<seq<Byte>>, src: seq<Byte>, dst: seq<Byte>)
  requires forall l :: l in lines ==> !Contains(l, Newline)
  requires !Contains(src, Newline) && !Contains(dst, Newline)
  ensures MtlsSeen(lines, src, dst) <==> exists l :: l in lines && Opened(l) && Contains(l, ReporterDest)
                                                  && Contains(l, SourceLabel(src)) && Contains(l, DestLabel(dst)) && Contains(l, MutualTls)
{
  var s1 := Filter(lines, l => Opened(l));
  var s2 := Filter(s1, l => Contains(l, ReporterDest));
  var s3 := Filter(s2, l => Contains(l, SourceLabel(src)));
  var s4 := Filter(s3, l => Contains(l, DestLabel(dst)));
  MutualTlsShape();
  ContainsJoin(s4, MutualTls);
  assert MtlsSeen(lines, src, dst) <==> exists l :: l in s4 && Contains(l, MutualTls);
  forall l
    ensures l in s4 <==> (l in lines && Opened(l) && Contains(l, ReporterDest)
                          && Contains(l, SourceLabel(src)) && Contains(l, DestLabel(dst)))
  {
    FilterMembership(lines, l => Opened(l), l);
    FilterMembership(s1, l => Contains(l, ReporterDest), l);
    FilterMembership(s2, l => Contains(l, SourceLabel(src)), l);
    FilterMembership(s3, l => Contains(l, DestLabel(dst)), l);
  }
}

// Citation: tests "mesh-check: --help exits 0", "mesh-check: an unknown option exits 2"
lemma P2_Options(args: seq<seq<Byte>>)
  ensures Options(args).Exit? ==> (Options(args).code == 0 || Options(args).code == 2)
  ensures (|args| >= 1 && (args[0] == HelpFlag || args[0] == HFlag)) ==> Options(args) == Exit(0)
  ensures (|args| >= 1 && args[0] != HelpFlag && args[0] != HFlag && args[0] != ReleaseFlag && args[0] != NamespaceFlag) ==> Options(args) == Exit(2)
{
  OptionsLoopCodes(args, DefaultName, DefaultName);
  if |args| >= 1 && (args[0] == HelpFlag || args[0] == HFlag) {
    assert args[0] != ReleaseFlag && args[0] != NamespaceFlag by {
      assert |HelpFlag| == 6 && |HFlag| == 2 && |ReleaseFlag| == 9 && |NamespaceFlag| == 11;
    }
  }
}
