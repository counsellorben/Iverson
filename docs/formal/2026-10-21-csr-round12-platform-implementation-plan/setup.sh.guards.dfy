// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T14 S4 (4d), T16 S3 (3c), T17 S5 (5a–5d), T20 S3 (3d–3e)
// Source: Iverson.Server/deploy/kind/setup.sh the kind version gate (4–11), the --only parser (44–52), ambient_enroll (106–115), the --only dispatch (136–140)
// Replay base: 7c4d86687b557e7e33a52c30cc07e6728dfb4622
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-21-csr-round12-platform-implementation-plan/setup.sh.guards.dfy

// ---------------------------------------------------------------------------------------------
// Shared vocabulary: bash strings are byte sequences.
// ---------------------------------------------------------------------------------------------
newtype Byte = x: int | 0 <= x < 256

const TAB: Byte := 9
const NL: Byte := 10
const SP: Byte := 32
const DASH: Byte := 45
const DOT: Byte := 46
const US: Byte := 95
const LOWER_V: Byte := 118

// ---------------------------------------------------------------------------------------------
// Generic byte-string helpers (bash / awk / coreutils semantics used by the candidate).
// ---------------------------------------------------------------------------------------------

// First index of b in s, or |s| when absent.
function IndexOf(s: seq<Byte>, b: Byte): (r: nat)
  ensures r <= |s|
  ensures forall i :: 0 <= i < r ==> s[i] != b
  ensures r < |s| ==> s[r] == b
  decreases |s|
{
  if |s| == 0 then 0 else if s[0] == b then 0 else 1 + IndexOf(s[1..], b)
}

// s split on every occurrence of the single byte b (an empty field between two adjacent bs is kept).
function SplitOn(s: seq<Byte>, b: Byte): seq<seq<Byte>>
  decreases |s|
{
  var i := IndexOf(s, b);
  if i == |s| then [s] else [s[..i]] + SplitOn(s[i + 1..], b)
}

// Tokens(s): s split on single spaces.
function Tokens(s: seq<Byte>): seq<seq<Byte>>
{
  SplitOn(s, SP)
}

function Pad(s: seq<Byte>): seq<Byte>
{
  [SP] + s + [SP]
}

predicate OccursAt(x: seq<Byte>, p: seq<Byte>, j: int)
{
  0 <= j && j + |p| <= |x| && x[j..j + |p|] == p
}

// bash `[[ x == *"p"* ]]` with p quoted (literal): p is a substring of x.
predicate Occurs(x: seq<Byte>, p: seq<Byte>)
{
  exists j :: 0 <= j <= |x| && OccursAt(x, p, j)
}

// bash `${x#*"lit"}`: remove the shortest prefix of x that matches `*lit`, i.e. the shortest prefix
// x[..k] ending in lit; x unchanged when no prefix matches.
function StripShortestStarLit(x: seq<Byte>, lit: seq<Byte>): seq<Byte>
{
  if |lit| > |x| then x else StripScan(x, lit, |lit|)
}

function StripScan(x: seq<Byte>, lit: seq<Byte>, k: int): seq<Byte>
  requires |lit| <= k <= |x|
  decreases |x| - k
{
  if x[k - |lit|..k] == lit then x[k..]
  else if k == |x| then x
  else StripScan(x, lit, k + 1)
}

// bash `${v//-/_}`.
function ReplaceAll(s: seq<Byte>, from: Byte, to: Byte): seq<Byte>
{
  seq(|s|, i requires 0 <= i < |s| => if s[i] == from then to else s[i])
}

// bash `${v%%-*}`: remove the longest suffix matching `-*`, i.e. cut at the first '-'.
function CutAtFirst(s: seq<Byte>, b: Byte): seq<Byte>
{
  s[..IndexOf(s, b)]
}

// `$(…)`: command substitution drops every trailing newline.
function StripTrailingNL(s: seq<Byte>): seq<Byte>
  decreases |s|
{
  if |s| > 0 && s[|s| - 1] == NL then StripTrailingNL(s[..|s| - 1]) else s
}

// awk default field splitting: fields are separated by runs of blanks (space, tab); leading blanks skipped.
predicate IsBlank(b: Byte)
{
  b == SP || b == TAB
}

function SkipBlanks(s: seq<Byte>): seq<Byte>
  decreases |s|
{
  if |s| > 0 && IsBlank(s[0]) then SkipBlanks(s[1..]) else s
}

function TakeField(s: seq<Byte>): (r: seq<Byte>)
  ensures |r| <= |s| && r == s[..|r|]
  decreases |s|
{
  if |s| > 0 && !IsBlank(s[0]) then [s[0]] + TakeField(s[1..]) else []
}

// `$2` of one awk record (empty when NF < 2).
function AwkField2(rec: seq<Byte>): seq<Byte>
{
  var s1 := SkipBlanks(rec);
  var f1 := TakeField(s1);
  var rest := s1[|f1|..];
  TakeField(SkipBlanks(rest))
}

// `awk '{ print $2 }'`: one output line per input record; records end at '\n', and a final '\n' does
// not start an empty record.
function AwkPrint2(out: seq<Byte>): seq<Byte>
  decreases |out|
{
  if |out| == 0 then []
  else
    var i := IndexOf(out, NL);
    AwkField2(out[..i]) + [NL] + (if i == |out| then [] else AwkPrint2(out[i + 1..]))
}

// ---------------------------------------------------------------------------------------------
// Version-string vocabulary for the approved `sort -V | head -n 1` axiom.
// ---------------------------------------------------------------------------------------------
predicate IsDigits(s: seq<Byte>)
{
  |s| > 0 && forall i :: 0 <= i < |s| ==> 48 <= s[i] <= 57
}

function ParseDec(s: seq<Byte>): int
  decreases |s|
{
  if |s| == 0 then 0 else ParseDec(s[..|s| - 1]) * 10 + (s[|s| - 1] as int - 48)
}

// Decimal rendering of n with no leading zeros.
function Dec(n: nat): seq<Byte>
  decreases n
{
  if n < 10 then [(48 + n) as Byte] else Dec(n / 10) + [(48 + n % 10) as Byte]
}

// x is `v<d>.<d>.<d>` with decimal digit runs.
predicate IsVTriple(x: seq<Byte>)
{
  |x| > 0 && x[0] == LOWER_V &&
  var p := SplitOn(x[1..], DOT);
  |p| == 3 && IsDigits(p[0]) && IsDigits(p[1]) && IsDigits(p[2])
}

function VTriple(x: seq<Byte>): (int, int, int)
{
  if IsVTriple(x) then
    var p := SplitOn(x[1..], DOT);
    (ParseDec(p[0]), ParseDec(p[1]), ParseDec(p[2]))
  else (0, 0, 0)
}

// Lexicographic <= on triples.
predicate TripleLe(p: (int, int, int), q: (int, int, int))
{
  p.0 < q.0 || (p.0 == q.0 && (p.1 < q.1 || (p.1 == q.1 && p.2 <= q.2)))
}

function VStr(a: nat, b: nat, c: nat): seq<Byte>
{
  [LOWER_V] + Dec(a) + [DOT] + Dec(b) + [DOT] + Dec(c)
}

// ---------------------------------------------------------------------------------------------
// Approved axiom: GNU `printf '%s\n%s\n' x y | sort -V | head -n 1` on `v<a>.<b>.<c>` strings.
// ---------------------------------------------------------------------------------------------
function SortVFirst(x: seq<Byte>, y: seq<Byte>): seq<Byte>
  ensures IsVTriple(x) && IsVTriple(y) ==> SortVFirst(x, y) == (if TripleLe(VTriple(x), VTriple(y)) then x else y)

// ---------------------------------------------------------------------------------------------
// Transliteration 1: the kind version gate, setup.sh:4–11 (T16 S3 (3c)).
// ---------------------------------------------------------------------------------------------
datatype GateOutcome = Refused | Passed

const KIND_MIN: seq<Byte> := [118, 48, 46, 51, 51, 46, 48]           // "v0.33.0"

function KindGate(kindVersionOutput: seq<Byte>): GateOutcome
{
  // 7: KIND_VERSION="$(kind version | awk '{ print $2 }')"
  var kindVersion := StripTrailingNL(AwkPrint2(kindVersionOutput));
  // 8: if [ "$(printf '%s\n%s\n' "$KIND_MIN" "${KIND_VERSION%%-*}" | sort -V | head -n 1)" != "$KIND_MIN" ]
  if SortVFirst(KIND_MIN, CutAtFirst(kindVersion, DASH)) != KIND_MIN then
    Refused   // 9–10: echo …; exit 1
  else
    Passed    // 11: fi
}

// The `kind version` line: `kind v<a>.<b>.<c><suffix> go1.25.1 linux/amd64`.
const KIND_SP: seq<Byte> := [107, 105, 110, 100, 32]                    // "kind "
const GO_TAIL: seq<Byte> := [32, 103, 111, 49, 46, 50, 53, 46, 49, 32, 108, 105, 110, 117, 120, 47, 97, 109, 100, 54, 52]
                                                                         // " go1.25.1 linux/amd64"
function KindVersionLine(a: nat, b: nat, c: nat, suffix: seq<Byte>): seq<Byte>
{
  KIND_SP + VStr(a, b, c) + suffix + GO_TAIL
}

predicate IsVersionSuffix(x: seq<Byte>)
{
  x == [] || (x[0] == DASH && SP !in x)
}

// ---------------------------------------------------------------------------------------------
// Transliteration 2: the --only parser, setup.sh:44–52 (T14 S4 (4d)), and the --only dispatch,
// setup.sh:136–140 (T20 S3 (3d–3e)).
// ---------------------------------------------------------------------------------------------
datatype Dispatch = FullInstall | RunPhase(p: seq<Byte>) | UsageExit2 | OtherExit(code: int)

const OnlyFlag: seq<Byte> := [45, 45, 111, 110, 108, 121]                                    // "--only"
const ISTIO: seq<Byte> := [105, 115, 116, 105, 111]                                           // "istio"
const OPNS: seq<Byte> := [111, 112, 101, 114, 97, 116, 111, 114, 45, 110, 97, 109, 101, 115, 112, 97, 99, 101, 115]
                                                                                              // "operator-namespaces"
const AMB: seq<Byte> := [97, 109, 98, 105, 101, 110, 116, 45, 108, 97, 98, 101, 108, 115]     // "ambient-labels"
const GW: seq<Byte> := [103, 97, 116, 101, 119, 97, 121]                                      // "gateway"
// 44: PHASES="istio operator-namespaces ambient-labels gateway"
const PHASES: seq<Byte> := ISTIO + [SP] + OPNS + [SP] + AMB + [SP] + GW

function Phases(): seq<seq<Byte>>
{
  [ISTIO, OPNS, AMB, GW]
}

// The functions setup.sh defines with a `phase_` name (54, 83, 117, 122).
const PHASE_PREFIX: seq<Byte> := [112, 104, 97, 115, 101, 95]                                 // "phase_"
const OPNS_U: seq<Byte> := [111, 112, 101, 114, 97, 116, 111, 114, 95, 110, 97, 109, 101, 115, 112, 97, 99, 101, 115]
                                                                                              // "operator_namespaces"
const AMB_U: seq<Byte> := [97, 109, 98, 105, 101, 110, 116, 95, 108, 97, 98, 101, 108, 115]   // "ambient_labels"

// bash invoking the simple command "$name": a defined function runs; anything else is
// "command not found", status 127, which `set -e` turns into the script's exit status.
function CallFunction(name: seq<Byte>): Dispatch
{
  if name == PHASE_PREFIX + ISTIO then RunPhase(ISTIO)
  else if name == PHASE_PREFIX + OPNS_U then RunPhase(OPNS)
  else if name == PHASE_PREFIX + AMB_U then RunPhase(AMB)
  else if name == PHASE_PREFIX + GW then RunPhase(GW)
  else OtherExit(127)
}

function ParseAndDispatch(args: seq<seq<Byte>>): Dispatch
{
  // 45: ONLY=""
  // 46: if [ "$#" -gt 0 ]; then
  if |args| > 0 then
    // 47: if [ "$#" -ne 2 ] || [ "$1" != "--only" ] || [[ " $PHASES " != *" $2 "* ]]; then
    if |args| != 2 || args[0] != OnlyFlag || !Occurs(Pad(PHASES), Pad(args[1])) then
      UsageExit2                       // 48–49: echo "usage: …" >&2; exit 2
    else
      DispatchOnly(args[1])            // 51: ONLY="$2"
  else
    DispatchOnly([])
}

function DispatchOnly(only: seq<Byte>): Dispatch
{
  // 137: if [ -n "$ONLY" ]; then
  if |only| > 0 then
    CallFunction(PHASE_PREFIX + ReplaceAll(only, DASH, US))   // 138: "phase_${ONLY//-/_}"; 139: exit 0
  else
    FullInstall                                               // 142–: the fresh install
}

// ---------------------------------------------------------------------------------------------
// Transliteration 3: ambient_enroll, setup.sh:106–115 (T17 S5 (5a–5d)).
// policyNames and ports are the values of `$(kubectl get networkpolicy -n iverson -o name)` and
// `$(kubectl get networkpolicy -n iverson -o jsonpath='{..port}')`. Result: true when line 114
// labels the four namespaces.
// ---------------------------------------------------------------------------------------------
const Fifteen008: seq<Byte> := [49, 53, 48, 48, 56]                  // "15008"

function Enroll(policyNames: seq<Byte>, ports: seq<Byte>): bool
{
  var policies := policyNames;                                       // 108
  var portsV := [SP] + ports + [SP];                                 // 109: ports=" $(…) "
  // 110: if [ -n "$policies" ] && [ "${ports#* 15008 }" = "$ports" ]; then
  if |policies| > 0 && StripShortestStarLit(portsV, Pad(Fifteen008)) == portsV then
    false                                                            // 111–112: echo …; return 0
  else
    true                                                             // 114: kubectl label namespace …
}

predicate IsSpaceSeparatedPorts(s: seq<Byte>)
{
  s == [] || forall t :: t in Tokens(s) ==> IsDigits(t)
}

// ---------------------------------------------------------------------------------------------
// Helper lemmas.
// ---------------------------------------------------------------------------------------------
lemma SplitOnNone(p: seq<Byte>, b: Byte)
  requires b !in p
  ensures SplitOn(p, b) == [p]
{
  var i := IndexOf(p, b);
  assert forall k :: 0 <= k < |p| ==> p[k] in p && p[k] != b;
  assert i == |p|;
}

lemma SplitOnCons(p: seq<Byte>, q: seq<Byte>, b: Byte)
  requires b !in p
  ensures SplitOn(p + [b] + q, b) == [p] + SplitOn(q, b)
{
  var s := p + [b] + q;
  var i := IndexOf(s, b);
  assert forall k :: 0 <= k < |p| ==> s[k] == p[k] && p[k] in p && s[k] != b;
  assert s[|p|] == b;
  assert i == |p|;
  assert s[..i] == p;
  assert s[i + 1..] == q;
}

lemma DecDigits(n: nat)
  ensures IsDigits(Dec(n))
  decreases n
{
  if n >= 10 { DecDigits(n / 10); }
}

lemma ParseDecDec(n: nat)
  ensures ParseDec(Dec(n)) == n
  decreases n
{
  if n < 10 {
    assert Dec(n)[..0] == [];
  } else {
    var d := Dec(n / 10);
    var s := d + [(48 + n % 10) as Byte];
    assert s[..|s| - 1] == d;
    ParseDecDec(n / 10);
  }
}

lemma DigitsNotIn(s: seq<Byte>, b: Byte)
  requires IsDigits(s) && !(48 <= b <= 57)
  ensures b !in s
{
}

lemma VTripleOfVStr(a: nat, b: nat, c: nat)
  ensures IsVTriple(VStr(a, b, c))
  ensures VTriple(VStr(a, b, c)) == (a, b, c)
{
  var x := VStr(a, b, c);
  DecDigits(a); DecDigits(b); DecDigits(c);
  ParseDecDec(a); ParseDecDec(b); ParseDecDec(c);
  DigitsNotIn(Dec(a), DOT); DigitsNotIn(Dec(b), DOT); DigitsNotIn(Dec(c), DOT);
  assert x[1..] == Dec(a) + [DOT] + (Dec(b) + [DOT] + Dec(c));
  SplitOnCons(Dec(a), Dec(b) + [DOT] + Dec(c), DOT);
  SplitOnCons(Dec(b), Dec(c), DOT);
  SplitOnNone(Dec(c), DOT);
  assert SplitOn(x[1..], DOT) == [Dec(a), Dec(b), Dec(c)];
}

lemma KindMinIsVStr()
  ensures KIND_MIN == VStr(0, 33, 0)
{
  assert Dec(0) == [48];
  assert Dec(3) == [51];
  assert Dec(33) == Dec(3) + [51];
}

lemma VStrShape(a: nat, b: nat, c: nat)
  ensures |VStr(a, b, c)| > 0 && VStr(a, b, c)[0] == LOWER_V
  ensures VStr(a, b, c)[|VStr(a, b, c)| - 1] != NL && VStr(a, b, c)[|VStr(a, b, c)| - 1] != DASH
  ensures forall i :: 0 <= i < |VStr(a, b, c)| ==> !IsBlank(VStr(a, b, c)[i]) && VStr(a, b, c)[i] != NL && VStr(a, b, c)[i] != DASH
{
  DecDigits(a); DecDigits(b); DecDigits(c);
  var x := VStr(a, b, c);
  forall i | 0 <= i < |x|
    ensures !IsBlank(x[i]) && x[i] != NL && x[i] != DASH
  {
    if i == 0 {
    } else if i < 1 + |Dec(a)| {
      assert x[i] == Dec(a)[i - 1];
    } else if i == 1 + |Dec(a)| {
    } else if i < 2 + |Dec(a)| + |Dec(b)| {
      assert x[i] == Dec(b)[i - 2 - |Dec(a)|];
    } else if i == 2 + |Dec(a)| + |Dec(b)| {
    } else {
      assert x[i] == Dec(c)[i - 3 - |Dec(a)| - |Dec(b)|];
    }
  }
}

lemma TakeFieldNoBlankPrefix(x: seq<Byte>, y: seq<Byte>)
  requires forall i :: 0 <= i < |x| ==> !IsBlank(x[i])
  ensures TakeField(x + y) == x + TakeField(y)
  decreases |x|
{
  if |x| > 0 {
    assert (x + y)[0] == x[0] && !IsBlank((x + y)[0]);
    assert TakeField(x + y) == [(x + y)[0]] + TakeField((x + y)[1..]);
    assert (x + y)[1..] == x[1..] + y;
    TakeFieldNoBlankPrefix(x[1..], y);
    assert x == [x[0]] + x[1..];
    calc {
      TakeField(x + y);
      [x[0]] + TakeField(x[1..] + y);
      [x[0]] + (x[1..] + TakeField(y));
      ([x[0]] + x[1..]) + TakeField(y);
      x + TakeField(y);
    }
  } else {
    assert x + y == y;
  }
}

lemma StripTrailingNLKeepsPrefix(p: seq<Byte>, q: seq<Byte>)
  requires |p| > 0 && p[|p| - 1] != NL
  ensures |StripTrailingNL(p + q)| >= |p| && StripTrailingNL(p + q)[..|p|] == p
  decreases |q|
{
  if |q| == 0 {
    assert p + q == p;
  } else if q[|q| - 1] == NL {
    assert (p + q)[..|p + q| - 1] == p + q[..|q| - 1];
    StripTrailingNLKeepsPrefix(p, q[..|q| - 1]);
  } else {
    assert (p + q)[..|p|] == p;
  }
}

// `${v%%-*}` of a value that starts with V then '-' (or is V): V.
lemma CutAtDash(v: seq<Byte>, z: seq<Byte>)
  requires forall i :: 0 <= i < |v| ==> v[i] != DASH
  requires z == v || (|z| > |v| && z[..|v|] == v && z[|v|] == DASH)
  ensures CutAtFirst(z, DASH) == v
{
  var i := IndexOf(z, DASH);
  assert forall k :: 0 <= k < |v| ==> z[k] == v[k] && z[k] != DASH;
  assert i >= |v|;
  if z != v {
    assert z[|v|] == DASH;
    assert i == |v|;
  }
}

// The awk `$2` of `kind <V><R>`, V non-blank and non-empty starting with a non-blank byte.
lemma AwkField2OfKindLine(v: seq<Byte>, r: seq<Byte>)
  requires |v| > 0 && forall i :: 0 <= i < |v| ==> !IsBlank(v[i])
  ensures AwkField2(KIND_SP + v + r) == v + TakeField(r)
{
  var rec := KIND_SP + v + r;
  var kind: seq<Byte> := [107, 105, 110, 100];
  assert rec == kind + ([SP] + v + r);
  assert SkipBlanks(rec) == rec;
  TakeFieldNoBlankPrefix(kind, [SP] + v + r);
  assert TakeField([SP] + v + r) == [];
  var f1 := TakeField(rec);
  assert f1 == kind;
  assert rec[|f1|..] == [SP] + v + r;
  assert ([SP] + v + r)[1..] == v + r;
  assert SkipBlanks([SP] + v + r) == SkipBlanks(v + r);
  assert SkipBlanks(v + r) == v + r;
  TakeFieldNoBlankPrefix(v, r);
}

lemma KindVersionIsV(a: nat, b: nat, c: nat, suffix: seq<Byte>)
  requires IsVersionSuffix(suffix)
  ensures CutAtFirst(StripTrailingNL(AwkPrint2(KindVersionLine(a, b, c, suffix))), DASH) == VStr(a, b, c)
{
  var v := VStr(a, b, c);
  VStrShape(a, b, c);
  var out := KindVersionLine(a, b, c, suffix);
  var i := IndexOf(out, NL);
  var n := |KIND_SP| + |v|;
  assert forall k :: 0 <= k < |KIND_SP| ==> out[k] == KIND_SP[k] && out[k] != NL;
  assert forall k :: |KIND_SP| <= k < n ==> out[k] == v[k - |KIND_SP|];
  if suffix == [] {
    assert out == KIND_SP + v + GO_TAIL;
    assert forall k :: n <= k < |out| ==> out[k] == GO_TAIL[k - n];
    assert forall k :: 0 <= k < |out| ==> out[k] != NL;
    assert i == |out|;
    assert out[..i] == KIND_SP + v + GO_TAIL;
    AwkField2OfKindLine(v, GO_TAIL);
    assert TakeField(GO_TAIL) == [];
    assert AwkPrint2(out) == v + [NL];
    assert StripTrailingNL(v + [NL]) == StripTrailingNL((v + [NL])[..|v + [NL]| - 1]);
    assert (v + [NL])[..|v + [NL]| - 1] == v;
    assert StripTrailingNL(v) == v;
    CutAtDash(v, v);
  } else {
    assert out[n] == suffix[0] == DASH;
    assert forall k :: 0 <= k <= n ==> out[k] != NL;
    assert i > n;
    var r := out[n + 1..i];
    assert out[..i] == KIND_SP + v + ([DASH] + r);
    AwkField2OfKindLine(v, [DASH] + r);
    var vd := v + [DASH];
    assert forall k :: 0 <= k < |vd| ==> !IsBlank(vd[k]);
    TakeFieldNoBlankPrefix([DASH], r);
    var f2 := AwkField2(out[..i]);
    assert f2 == vd + TakeField(r);
    var rest := (if i == |out| then [] else AwkPrint2(out[i + 1..]));
    assert AwkPrint2(out) == vd + (TakeField(r) + [NL] + rest);
    StripTrailingNLKeepsPrefix(vd, TakeField(r) + [NL] + rest);
    var z := StripTrailingNL(AwkPrint2(out));
    assert z[..|v|] == v && z[|v|] == DASH by {
      assert z[..|vd|] == vd;
      assert z[..|v|] == z[..|vd|][..|v|];
    }
    CutAtDash(v, z);
  }
}

lemma StripScanSame(x: seq<Byte>, lit: seq<Byte>, k: int)
  requires 0 < |lit| <= k <= |x|
  ensures StripScan(x, lit, k) == x <==> forall j :: k - |lit| <= j ==> !OccursAt(x, lit, j)
  decreases |x| - k
{
  if x[k - |lit|..k] == lit {
    assert OccursAt(x, lit, k - |lit|);
    assert |x[k..]| < |x|;
  } else if k == |x| {
    forall j | k - |lit| <= j
      ensures !OccursAt(x, lit, j)
    {
      if j > k - |lit| { assert j + |lit| > |x|; }
    }
  } else {
    StripScanSame(x, lit, k + 1);
    assert !OccursAt(x, lit, k - |lit|);
  }
}

lemma StripSame(x: seq<Byte>, lit: seq<Byte>)
  requires |lit| > 0
  ensures StripShortestStarLit(x, lit) == x <==> !Occurs(x, lit)
{
  if |lit| > |x| {
    forall j | 0 <= j <= |x|
      ensures !OccursAt(x, lit, j)
    {
    }
  } else {
    StripScanSame(x, lit, |lit|);
    if Occurs(x, lit) { var j :| 0 <= j <= |x| && OccursAt(x, lit, j); assert |lit| - |lit| <= j; }
  }
}

// " w " occurs in " s " exactly when w is one of s's single-space tokens.
lemma PadOccursIffToken(s: seq<Byte>, w: seq<Byte>)
  requires |w| > 0 && SP !in w
  ensures Occurs(Pad(s), Pad(w)) <==> w in Tokens(s)
  decreases |s|
{
  var P := Pad(s);
  var W := Pad(w);
  assert |W| == |w| + 2 && W[0] == SP && W[|w| + 1] == SP;
  assert forall k :: 1 <= k <= |w| ==> W[k] == w[k - 1] && W[k] != SP;
  var i := IndexOf(s, SP);
  if i == |s| {
    assert Tokens(s) == [s];
    assert forall k :: 1 <= k <= |s| ==> P[k] == s[k - 1] && P[k] != SP;
    if w == s { assert OccursAt(P, W, 0); }
    if Occurs(P, W) {
      var j :| 0 <= j <= |P| && OccursAt(P, W, j);
      assert P[j] == W[0];
      assert j == 0;
      assert P[|w| + 1] == W[|w| + 1];
      assert |w| == |s|;
      assert P[1..|s| + 1] == s;
      assert W[1..|w| + 1] == w;
      assert forall k :: 0 <= k < |W| ==> P[k] == W[k];
      assert P[1..|w| + 1] == W[1..|w| + 1];
      assert w == s;
    }
  } else {
    var t := s[..i];
    var r := s[i + 1..];
    assert s == t + [SP] + r;
    assert Tokens(s) == [t] + Tokens(r);
    assert P == [SP] + t + [SP] + r + [SP];
    assert P[|t| + 1..] == Pad(r);
    assert forall k :: 1 <= k <= |t| ==> P[k] == t[k - 1] && P[k] != SP;
    assert P[|t| + 1] == SP;
    PadOccursIffToken(r, w);
    if w == t { assert P[0..|W|] == W; assert OccursAt(P, W, 0); }
    if w in Tokens(r) {
      var j' :| 0 <= j' <= |Pad(r)| && OccursAt(Pad(r), W, j');
      var j := j' + |t| + 1;
      assert P[j..j + |W|] == Pad(r)[j'..j' + |W|];
      assert OccursAt(P, W, j);
    }
    if Occurs(P, W) {
      var j :| 0 <= j <= |P| && OccursAt(P, W, j);
      assert P[j] == W[0];
      if j == 0 {
        assert forall k :: 0 <= k < |W| ==> P[k] == W[k];
        assert |w| == |t|;
        assert P[1..|t| + 1] == t;
        assert W[1..|w| + 1] == w;
        assert P[1..|w| + 1] == W[1..|w| + 1];
        assert w == t;
      } else {
        assert j >= |t| + 1;
        assert Pad(r)[j - |t| - 1..j - |t| - 1 + |W|] == P[j..j + |W|];
        assert OccursAt(Pad(r), W, j - |t| - 1);
      }
    }
  }
}

// ---------------------------------------------------------------------------------------------
// Properties (frozen).
// ---------------------------------------------------------------------------------------------

// Citation: "`setup.sh`/`setup.ps1` refuse a kind older than v0.33.0" (§3.6 line 330); plan check `t16_kind.sh` (T16 S1: "kind v0.24.0 must be refused before any helm or kubectl call; kind v0.33.0 must get past the check"), generalised
lemma P1_KindGate(a: nat, b: nat, c: nat, suffix: seq<Byte>)
  requires IsVersionSuffix(suffix)
  ensures KindGate(KindVersionLine(a, b, c, suffix)) == (if a == 0 && b < 33 then Refused else Passed)
{
  KindVersionIsV(a, b, c, suffix);
  VTripleOfVStr(a, b, c);
  VTripleOfVStr(0, 33, 0);
  KindMinIsVStr();
  var v := VStr(a, b, c);
  var first := SortVFirst(KIND_MIN, v);
  if TripleLe((0, 33, 0), (a, b, c)) {
    assert first == KIND_MIN;
  } else {
    assert first == v;
    assert v != KIND_MIN;
  }
}

// Citation: **Expected:** (T14 S4) "`usage: Iverson.Server/deploy/kind/setup.sh [--only <phase>]   (phases: …)` then `exit=2`" for `--only no-such-phase`, generalised; Task 27 Interfaces "runs a single phase with `--only <phase>`"
lemma P2_OnlyRunsOnePhase(args: seq<seq<Byte>>)
  ensures ParseAndDispatch(args) == (if args == [] then FullInstall
                                     else if |args| == 2 && args[0] == OnlyFlag && args[1] in Phases() then RunPhase(args[1])
                                     else UsageExit2)
{
  if args == [] {
  } else if |args| == 2 && args[0] == OnlyFlag && args[1] in Phases() {
    var w := args[1];
    var PP := Pad(PHASES);
    assert PP == [SP] + ISTIO + [SP] + OPNS + [SP] + AMB + [SP] + GW + [SP];
    if w == ISTIO {
      assert OccursAt(PP, Pad(w), 0);
      assert ReplaceAll(w, DASH, US) == ISTIO;
    } else if w == OPNS {
      assert OccursAt(PP, Pad(w), 6);
      assert ReplaceAll(w, DASH, US) == OPNS_U;
    } else if w == AMB {
      assert OccursAt(PP, Pad(w), 26);
      assert ReplaceAll(w, DASH, US) == AMB_U;
    } else {
      assert w == GW;
      assert OccursAt(PP, Pad(w), 41);
      assert ReplaceAll(w, DASH, US) == GW;
    }
  } else if |args| == 2 && args[0] == OnlyFlag && Occurs(Pad(PHASES), Pad(args[1])) {
    // The failing case: " $2 " occurs in " $PHASES " but $2 is not one phase name (two or more adjacent
    // phase names joined by a space, e.g. "ambient-labels gateway"); see P2_Witness: OtherExit(127).
  } else {
  }
}

// Citation: "On kind they are labelled only after the chart's 15008 rules exist (§5)" (§4.1 line 344); plan check `t17_enroll.sh` (T17 S1: fresh → label; `8080 15008 8081 15008` → label; `8080 8081 5432` → none; `150080 8081` → none), generalised
lemma P3_EnrolOnlyWith15008(policyNames: seq<Byte>, ports: seq<Byte>)
  requires IsSpaceSeparatedPorts(ports)
  ensures Enroll(policyNames, ports) <==> (policyNames == [] || Fifteen008 in Tokens(ports))
{
  assert SP !in Fifteen008;
  StripSame([SP] + ports + [SP], Pad(Fifteen008));
  PadOccursIffToken(ports, Fifteen008);
}

// ---------------------------------------------------------------------------------------------
// P2 counterexample, proved on the transliteration: `setup.sh --only "ambient-labels gateway"`.
// ---------------------------------------------------------------------------------------------
lemma P2_Witness()
  ensures var args := [OnlyFlag, AMB + [SP] + GW];
          args[1] !in Phases() &&
          ParseAndDispatch(args) == OtherExit(127)
{
  var w := AMB + [SP] + GW;
  var PP := Pad(PHASES);
  assert PP == [SP] + ISTIO + [SP] + OPNS + [SP] + AMB + [SP] + GW + [SP];
  assert PP[26..26 + |Pad(w)|] == Pad(w);
  assert OccursAt(PP, Pad(w), 26);
  assert |w| == 22;
  assert |PHASE_PREFIX + ReplaceAll(w, DASH, US)| == 28;
}
