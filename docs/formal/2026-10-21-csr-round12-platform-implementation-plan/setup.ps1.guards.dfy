// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T14 S4 (4d), T16 S3 (3c), T17 S5 (5e–5h), T20 S3 (3f–3i)
// Source: Iverson.Server/deploy/kind/setup.ps1 the kind version gate (3–9), the --only parser (43–51), Add-AmbientLabels (96–110), the dispatch (144–152)
// Code ref: 8ef849174092218f507041e82b8185facafb1937
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-21-csr-round12-platform-implementation-plan/setup.ps1.guards.dfy

// ===================== Shared vocabulary =====================

datatype Option<T> = None | Some(value: T)

// PowerShell strings: .NET UTF-16 code units (--unicode-char false).
type CsString = s: seq<char> | |s| < 0x8000_0000

predicate IsAscii(s: seq<char>)
{
  forall i :: 0 <= i < |s| ==> (s[i] as int) < 128
}

function AsciiLower(s: seq<char>): seq<char>
{
  seq(|s|, i requires 0 <= i < |s| => if 'A' <= s[i] <= 'Z' then ((s[i] as int) + 32) as char else s[i])
}

// ===================== Approved axioms =====================

function CaseFold(s: CsString): CsString
  ensures IsAscii(s) ==> CaseFold(s) == AsciiLower(s)

function VersionParse(s: CsString): Option<(int, int, int)>
  ensures IsDottedTriple(s) ==> VersionParse(s) == Some(TripleOf(s))

// ===================== Modelled library: .NET regex \s, PowerShell -split, TrimStart, -eq =====================

// .NET Regex `\s`: [\f\n\r\t\v\x85\p{Z}] (Zs, Zl, Zp).
predicate IsWs(c: char)
{
  c == '\t' || c == '\n' || c == '\u000B' || c == '\u000C' || c == '\r' || c == ' '
  || c == '\u0085' || c == ' ' || c == ' ' || (' ' <= c <= ' ')
  || c == ' ' || c == ' ' || c == ' ' || c == ' ' || c == '　'
}

function FirstWsFrom(s: seq<char>, i: nat): nat
  requires i <= |s|
  ensures i <= FirstWsFrom(s, i) <= |s|
  ensures FirstWsFrom(s, i) < |s| ==> IsWs(s[FirstWsFrom(s, i)])
  decreases |s| - i
{
  if i == |s| || IsWs(s[i]) then i else FirstWsFrom(s, i + 1)
}

function RunEndFrom(s: seq<char>, i: nat): nat
  requires i <= |s|
  ensures i <= RunEndFrom(s, i) <= |s|
  ensures i < |s| && IsWs(s[i]) ==> i < RunEndFrom(s, i)
  decreases |s| - i
{
  if i == |s| || !IsWs(s[i]) then i else RunEndFrom(s, i + 1)
}

// PowerShell `$s -split '\s+'`: Regex.Split on leftmost maximal whitespace runs; empty leading and
// trailing fields are kept, and an empty input gives one empty field.
function SplitWs(s: CsString): seq<CsString>
  ensures |SplitWs(s)| >= 1
  decreases |s|
{
  var i := FirstWsFrom(s, 0);
  if i == |s| then [s]
  else
    var j := RunEndFrom(s, i);
    [s[..i]] + SplitWs(s[j..])
}

// TokensPs(s): `s -split '\s+'`.
function TokensPs(s: CsString): seq<CsString>
{
  SplitWs(s)
}

function IndexOfFrom(s: seq<char>, ch: char, i: nat): nat
  requires i <= |s|
  ensures i <= IndexOfFrom(s, ch, i) <= |s|
  decreases |s| - i
{
  if i == |s| || s[i] == ch then i else IndexOfFrom(s, ch, i + 1)
}

// Split on every occurrence of one literal unit; PowerShell `-split '-'` (the regex `-` matches only '-').
function SplitOnChar(s: CsString, ch: char): seq<CsString>
  ensures |SplitOnChar(s, ch)| >= 1
  decreases |s|
{
  var i := IndexOfFrom(s, ch, 0);
  if i == |s| then [s] else [s[..i]] + SplitOnChar(s[i + 1..], ch)
}

// String.TrimStart(char): removes every leading occurrence (ordinal).
function TrimStartChar(s: CsString, ch: char): CsString
  decreases |s|
{
  if |s| > 0 && s[0] == ch then TrimStartChar(s[1..], ch) else s
}

// PowerShell string -eq: invariant-culture, case-insensitive (the approved CaseFold).
predicate EqPs(x: CsString, y: CsString)
{
  CaseFold(x) == CaseFold(y)
}

// PowerShell `$arr -contains $v` (and `$v -in $arr`) with strings: each element compared with -eq.
function ContainsPs(arr: seq<CsString>, v: CsString): bool
  decreases |arr|
{
  if arr == [] then false else EqPs(arr[0], v) || ContainsPs(arr[1..], v)
}

// ===================== Property vocabulary =====================

predicate IsDigitChar(c: char) { '0' <= c <= '9' }

predicate IsDigits(s: seq<char>)
{
  |s| > 0 && forall i :: 0 <= i < |s| ==> IsDigitChar(s[i])
}

function DigitVal(c: char): nat
{
  if '0' <= c <= '9' then (c as int) - ('0' as int) else 0
}

function DigitsVal(s: seq<char>): nat
  decreases |s|
{
  if s == [] then 0 else DigitsVal(s[..|s| - 1]) * 10 + DigitVal(s[|s| - 1])
}

// IsDottedTriple(s): `<d>.<d>.<d>`, decimal digit runs, each number below 2^31.
predicate IsDottedTriple(s: CsString)
{
  var p := SplitOnChar(s, '.');
  |p| == 3 && IsDigits(p[0]) && IsDigits(p[1]) && IsDigits(p[2])
  && DigitsVal(p[0]) < 0x8000_0000 && DigitsVal(p[1]) < 0x8000_0000 && DigitsVal(p[2]) < 0x8000_0000
}

function TripleOf(s: CsString): (int, int, int)
{
  var p := SplitOnChar(s, '.');
  if |p| == 3 then (DigitsVal(p[0]), DigitsVal(p[1]), DigitsVal(p[2])) else (0, 0, 0)
}

function DigitChar(d: nat): char
  requires d < 10
{
  (('0' as int) + d) as char
}

function Dec(n: nat): seq<char>
  ensures |Dec(n)| <= 1 + n / 10
  decreases n
{
  if n < 10 then [DigitChar(n)] else Dec(n / 10) + [DigitChar(n % 10)]
}

function TripleText(a: nat, b: nat, c: nat): seq<char>
{
  Dec(a) + "." + Dec(b) + "." + Dec(c)
}

// `suffix` is empty or starts with '-' and holds no space (as for bash). A .NET string holds at most
// 0x3FFFFFDF units, so a suffix that fits in the `kind version` string is shorter than 2^30.
predicate IsVersionSuffixPs(x: CsString)
{
  |x| < 0x4000_0000 && (x == [] || (x[0] == '-' && ' ' !in x))
}

// The `kind version` output, `kind v<a>.<b>.<c><suffix> go… …`.
function KindVersionLinePs(a: nat, b: nat, c: nat, suffix: CsString): CsString
  requires a < 0x8000_0000 && b < 0x8000_0000 && c < 0x8000_0000 && |suffix| < 0x4000_0000
{
  "kind " + ("v" + TripleText(a, b, c) + suffix) + " go1.25.1 linux/amd64"
}

// Decimal port tokens joined by single spaces (the jsonpath '{..port}' output), as for bash.
predicate IsSpaceSeparatedPortsPs(s: CsString)
{
  (forall k :: 0 <= k < |s| ==> IsDigitChar(s[k]) || s[k] == ' ')
  && (|s| > 0 ==> s[0] != ' ' && s[|s| - 1] != ' ')
  && (forall k :: 0 <= k < |s| - 1 ==> !(s[k] == ' ' && s[k + 1] == ' '))
}

function PhasesPs(): seq<CsString>
{
  ["istio", "operator-namespaces", "ambient-labels", "gateway"]
}

// ===================== Transliteration =====================

// Refused: line 8's throw. The two other throwing paths are their own outcomes.
datatype GateOutcome = Refused | Passed | KindVersionNullThrown | VersionCastThrown

// System.Version.CompareTo on three-component versions (Revision -1 on both sides).
function VersionLt(x: (int, int, int), y: (int, int, int)): bool
{
  x.0 < y.0 || (x.0 == y.0 && (x.1 < y.1 || (x.1 == y.1 && x.2 < y.2)))
}

// setup.ps1 3–9
function KindGatePs(kindVersionOutput: CsString): GateOutcome
{
  // 5: $KindMin = [version]"0.33.0"   (a failed cast throws)
  var kindMin := VersionParse("0.33.0");
  if kindMin.None? then VersionCastThrown
  else
    // 6: $KindVersion = ("$(kind version)" -split '\s+')[1]   (an index past the end gives $null)
    var fields := SplitWs(kindVersionOutput);
    if |fields| < 2 then KindVersionNullThrown   // 7: $null.TrimStart('v') throws
    else
      var kindVersion := fields[1];
      // 7: [version](($KindVersion.TrimStart('v') -split '-')[0]) -lt $KindMin
      var head := SplitOnChar(TrimStartChar(kindVersion, 'v'), '-')[0];
      var v := VersionParse(head);
      if v.None? then VersionCastThrown
      else if VersionLt(v.value, kindMin.value) then Refused   // 8: throw "kind … is older than …"
      else Passed
}

// FullInstallPs: lines 154–244. RunPhasePs(p): the switch ran the arm whose label -eq p.
// NoArmMatchedExit0: line 151 reached with no arm run (added: the switch has no default arm).
datatype DispatchPs = FullInstallPs | RunPhasePs(p: CsString) | UsageThrown | NoArmMatchedExit0

// setup.ps1 43–51, then 144–152 / 154–244
function ParseAndDispatchPs(args: seq<CsString>): DispatchPs
{
  // 43: $Phases = @("istio", "operator-namespaces", "ambient-labels", "gateway")
  var phases := PhasesPs();
  // 44: $Only = ""
  // 45: if ($args.Count -gt 0)
  if |args| > 0 then
    // 46: $args.Count -ne 2 -or $args[0] -notin @('--only', '-only') -or $Phases -notcontains $args[1]   (-or short-circuits)
    if |args| != 2 || !ContainsPs(["--only", "-only"], args[0]) || !ContainsPs(phases, args[1]) then
      UsageThrown   // 47: throw "usage: …"
    else
      DispatchOnlyPs(args[1])   // 49: $Only = $args[1]
  else
    DispatchOnlyPs("")
}

// setup.ps1 144–152
function DispatchOnlyPs(only: CsString): DispatchPs
{
  // 144: if ($Only)   (an empty string is false)
  if only != "" then
    // 145–150: switch ($Only), case-insensitive -eq per arm; every matching arm runs
    var istio := EqPs(only, "istio");                 // 146: Install-Istio
    var opns := EqPs(only, "operator-namespaces");    // 147: Install-OperatorNamespacePolicies
    var amb := EqPs(only, "ambient-labels");          // 148: Add-AmbientLabels
    var gw := EqPs(only, "gateway");                  // 149: New-EdgeGateway
    if istio || opns || amb || gw then RunPhasePs(only) else NoArmMatchedExit0   // 151: exit 0
  else
    FullInstallPs   // 154–244
}

// setup.ps1 96–110: true iff line 109 labels the four namespaces.
// policies: the `kubectl get networkpolicy -n iverson -o name` output ($null when none; several lines are truthy).
// ports: the string "$(kubectl get networkpolicy -n iverson -o jsonpath='{..port}')".
predicate TruthyPs(x: Option<CsString>)
{
  x.Some? && x.value != ""
}

function EnrollPs(policies: Option<CsString>, ports: CsString): bool
{
  // 104: $Policies = kubectl get networkpolicy -n iverson -o name
  // 105: $Ports = "$(kubectl … -o jsonpath='{..port}')" -split '\s+'
  var portsArr := SplitWs(ports);
  // 106: if ($Policies -and -not ($Ports -contains '15008'))
  if TruthyPs(policies) && !ContainsPs(portsArr, "15008") then
    false   // 107: Write-Host "Not enrolled: …"
  else
    true    // 109: kubectl label namespace iverson cnpg-system kafka starrocks istio.io/dataplane-mode=ambient --overwrite
}

// ===================== Helper lemmas =====================

lemma FirstWsFromAt(s: seq<char>, i: nat, n: nat)
  requires i <= n <= |s|
  requires forall k :: i <= k < n ==> !IsWs(s[k])
  requires n == |s| || IsWs(s[n])
  ensures FirstWsFrom(s, i) == n
  decreases n - i
{
  if i < n { FirstWsFromAt(s, i + 1, n); }
}

lemma FirstWsFromAtLeast(s: seq<char>, i: nat, n: nat)
  requires i <= n <= |s|
  requires forall k :: i <= k < n ==> !IsWs(s[k])
  ensures FirstWsFrom(s, i) >= n
  decreases n - i
{
  if i < n { FirstWsFromAtLeast(s, i + 1, n); }
}

lemma FirstWsFromAtMost(s: seq<char>, i: nat, n: nat)
  requires i <= n < |s|
  requires IsWs(s[n])
  ensures FirstWsFrom(s, i) <= n
  decreases n - i
{
  if i < n && !IsWs(s[i]) { FirstWsFromAtMost(s, i + 1, n); }
}

lemma RunEndFromAt(s: seq<char>, i: nat, n: nat)
  requires i <= n <= |s|
  requires forall k :: i <= k < n ==> IsWs(s[k])
  requires n == |s| || !IsWs(s[n])
  ensures RunEndFrom(s, i) == n
  decreases n - i
{
  if i < n { RunEndFromAt(s, i + 1, n); }
}

lemma IndexOfFromAt(s: seq<char>, ch: char, i: nat, n: nat)
  requires i <= n <= |s|
  requires forall k :: i <= k < n ==> s[k] != ch
  requires n == |s| || s[n] == ch
  ensures IndexOfFrom(s, ch, i) == n
  decreases n - i
{
  if i < n { IndexOfFromAt(s, ch, i + 1, n); }
}

lemma SplitWsHead(s: CsString)
  ensures SplitWs(s)[0] == s[..FirstWsFrom(s, 0)]
{
}

lemma SplitOnCharHead(s: CsString, ch: char)
  ensures SplitOnChar(s, ch)[0] == s[..IndexOfFrom(s, ch, 0)]
{
}

lemma SplitOnCharNone(t: CsString, ch: char)
  requires forall k :: 0 <= k < |t| ==> t[k] != ch
  ensures SplitOnChar(t, ch) == [t]
{
  IndexOfFromAt(t, ch, 0, |t|);
}

lemma SplitOnCharCons(t: CsString, ch: char, r: CsString)
  requires forall k :: 0 <= k < |t| ==> t[k] != ch
  requires |t| + 1 + |r| < 0x8000_0000
  ensures SplitOnChar(t + [ch] + r, ch) == [t] + SplitOnChar(r, ch)
{
  var s: CsString := t + [ch] + r;
  IndexOfFromAt(s, ch, 0, |t|);
  assert s[..|t|] == t;
  assert s[|t| + 1..] == r;
}

lemma DecFacts(n: nat)
  ensures IsDigits(Dec(n)) && DigitsVal(Dec(n)) == n
  decreases n
{
  if n < 10 {
    var s := Dec(n);
    assert s == [DigitChar(n)];
    assert s[..0] == [];
  } else {
    DecFacts(n / 10);
    var s := Dec(n);
    assert s[..|s| - 1] == Dec(n / 10);
    assert DigitVal(s[|s| - 1]) == n % 10;
  }
}

lemma TripleParse(a: nat, b: nat, c: nat)
  requires a < 0x8000_0000 && b < 0x8000_0000 && c < 0x8000_0000
  ensures |TripleText(a, b, c)| < 0x8000_0000
  ensures IsDottedTriple(TripleText(a, b, c)) && TripleOf(TripleText(a, b, c)) == (a, b, c)
{
  DecFacts(a); DecFacts(b); DecFacts(c);
  var da: CsString := Dec(a);
  var db: CsString := Dec(b);
  var dc: CsString := Dec(c);
  var rest: CsString := db + ['.'] + dc;
  var t: CsString := TripleText(a, b, c);
  assert t == da + ['.'] + rest;
  assert forall k :: 0 <= k < |da| ==> da[k] != '.' by { assert forall k :: 0 <= k < |da| ==> IsDigitChar(da[k]); }
  assert forall k :: 0 <= k < |db| ==> db[k] != '.' by { assert forall k :: 0 <= k < |db| ==> IsDigitChar(db[k]); }
  assert forall k :: 0 <= k < |dc| ==> dc[k] != '.' by { assert forall k :: 0 <= k < |dc| ==> IsDigitChar(dc[k]); }
  SplitOnCharCons(da, '.', rest);
  SplitOnCharCons(db, '.', dc);
  SplitOnCharNone(dc, '.');
  assert SplitOnChar(t, '.') == [da, db, dc];
}

lemma TripleTextChars(a: nat, b: nat, c: nat)
  ensures forall k :: 0 <= k < |TripleText(a, b, c)| ==> IsDigitChar(TripleText(a, b, c)[k]) || TripleText(a, b, c)[k] == '.'
  ensures |TripleText(a, b, c)| > 0 && IsDigitChar(TripleText(a, b, c)[0])
{
  DecFacts(a); DecFacts(b); DecFacts(c);
  var t := TripleText(a, b, c);
  var da, db, dc := Dec(a), Dec(b), Dec(c);
  assert t == da + "." + db + "." + dc;
  forall k | 0 <= k < |t|
    ensures IsDigitChar(t[k]) || t[k] == '.'
  {
    if k < |da| { assert t[k] == da[k]; }
    else if k == |da| { assert t[k] == '.'; }
    else if k < |da| + 1 + |db| { assert t[k] == db[k - |da| - 1]; }
    else if k == |da| + 1 + |db| { assert t[k] == '.'; }
    else { assert t[k] == dc[k - |da| - 2 - |db|]; }
  }
  assert t[0] == da[0];
}

lemma KindMinParses()
  ensures VersionParse("0.33.0") == Some((0, 33, 0))
{
  assert Dec(3) == "3";
  assert Dec(33) == Dec(3) + [DigitChar(3)];
  assert Dec(33) == "33";
  assert Dec(0) == "0";
  assert TripleText(0, 33, 0) == "0.33.0";
  TripleParse(0, 33, 0);
}

predicate PlainAscii(c: char)
{
  (c as int) < 128 && !('A' <= c <= 'Z')
}

predicate NoUpper(s: seq<char>)
{
  forall k :: 0 <= k < |s| ==> PlainAscii(s[k])
}

lemma CaseFoldPlain(s: CsString)
  requires NoUpper(s)
  ensures CaseFold(s) == s
{
  assert IsAscii(s);
  assert AsciiLower(s) == s;
}

lemma SplitWsPlain(s: CsString)
  requires NoUpper(s)
  ensures forall i :: 0 <= i < |SplitWs(s)| ==> NoUpper(SplitWs(s)[i])
  decreases |s|
{
  var i := FirstWsFrom(s, 0);
  if i < |s| {
    var j := RunEndFrom(s, i);
    SplitWsPlain(s[j..]);
    assert SplitWs(s) == [s[..i]] + SplitWs(s[j..]);
  }
}

lemma ContainsPlain(arr: seq<CsString>, v: CsString)
  requires forall i :: 0 <= i < |arr| ==> NoUpper(arr[i])
  requires NoUpper(v)
  ensures ContainsPs(arr, v) <==> v in arr
  decreases |arr|
{
  if arr != [] {
    CaseFoldPlain(arr[0]);
    CaseFoldPlain(v);
    ContainsPlain(arr[1..], v);
    assert arr == [arr[0]] + arr[1..];
  }
}

lemma ContainsExists(arr: seq<CsString>, v: CsString)
  ensures ContainsPs(arr, v) <==> exists p :: p in arr && CaseFold(p) == CaseFold(v)
  decreases |arr|
{
  if arr != [] {
    ContainsExists(arr[1..], v);
    assert arr == [arr[0]] + arr[1..];
  }
}

// `-notin @('--only', '-only')` is the negation of a two-element -eq scan.
lemma OnlyFlagFold(s: CsString)
  ensures ContainsPs(["--only", "-only"], s) <==> (CaseFold(s) == CaseFold("--only") || CaseFold(s) == CaseFold("-only"))
{
  var arr: seq<CsString> := ["--only", "-only"];
  assert arr[1..] == ["-only"];
  assert arr[1..][1..] == [];
}

// No phase name folds to the fold of "", so a phase that -contains admits is non-empty.
lemma PhaseNonEmpty(v: CsString)
  requires ContainsPs(PhasesPs(), v)
  ensures v != ""
{
  ContainsExists(PhasesPs(), v);
  var e: CsString := "";
  assert NoUpper(e);
  CaseFoldPlain(e);
  var ps := PhasesPs();
  forall i | 0 <= i < |ps| ensures CaseFold(ps[i]) == ps[i] && ps[i] != "" {
    assert NoUpper(ps[i]);
    CaseFoldPlain(ps[i]);
  }
  var q :| q in ps && CaseFold(q) == CaseFold(v);
  assert CaseFold(v) != CaseFold(e);
}

// ===================== Properties =====================

// Citation: "`setup.sh`/`setup.ps1` refuse a kind older than v0.33.0" (§3.6 line 330)
lemma P1_KindGate(a: nat, b: nat, c: nat, suffix: CsString)
  requires IsVersionSuffixPs(suffix) && a < 0x8000_0000 && b < 0x8000_0000 && c < 0x8000_0000
  ensures KindGatePs(KindVersionLinePs(a, b, c, suffix)) == (if a == 0 && b < 33 then Refused else Passed)
{
  KindMinParses();
  TripleParse(a, b, c);
  TripleTextChars(a, b, c);
  var T: CsString := TripleText(a, b, c);
  var X: CsString := "v" + T + suffix;
  var line := KindVersionLinePs(a, b, c, suffix);
  assert line == "kind " + X + " go1.25.1 linux/amd64";
  // field 0: "kind"
  FirstWsFromAt(line, 0, 4);
  RunEndFromAt(line, 4, 5);
  var rest: CsString := line[5..];
  assert SplitWs(line) == [line[..4]] + SplitWs(rest);
  assert rest == X + " go1.25.1 linux/amd64";
  // field 1: "v<T><suffix prefix>"
  assert forall k :: 0 <= k < 1 + |T| ==> !IsWs(rest[k]) by {
    forall k | 0 <= k < 1 + |T| ensures !IsWs(rest[k]) {
      if k == 0 { assert rest[k] == 'v'; } else { assert rest[k] == T[k - 1]; }
    }
  }
  FirstWsFromAtLeast(rest, 0, 1 + |T|);
  assert rest[|X|] == ' ';
  FirstWsFromAtMost(rest, 0, |X|);
  var m := FirstWsFrom(rest, 0);
  SplitWsHead(rest);
  var field := SplitWs(line)[1];
  assert field == rest[..m];
  var sp: CsString := suffix[..m - 1 - |T|];
  var Y: CsString := T + sp;
  assert field == ['v'] + Y;
  assert Y[0] == T[0];
  assert TrimStartChar(field, 'v') == TrimStartChar(Y, 'v');
  assert TrimStartChar(Y, 'v') == Y;
  // (… -split '-')[0]
  assert sp == [] || sp[0] == '-';
  assert forall k :: 0 <= k < |T| ==> Y[k] != '-' by {
    forall k | 0 <= k < |T| ensures Y[k] != '-' { assert Y[k] == T[k]; }
  }
  IndexOfFromAt(Y, '-', 0, |T|);
  SplitOnCharHead(Y, '-');
  assert Y[..|T|] == T;
  assert SplitOnChar(TrimStartChar(field, 'v'), '-')[0] == T;
  assert VersionParse(T) == Some((a, b, c));
}

// Citation: **Expected:** (T14 S4) "… then `exit=2` … `setup.ps1` … mirrors the bash block line for line."
lemma P2_OnlyRunsOnePhase(args: seq<CsString>)
  ensures ParseAndDispatchPs(args) == (if args == [] then FullInstallPs
                                       else if |args| == 2 && (CaseFold(args[0]) == CaseFold("--only") || CaseFold(args[0]) == CaseFold("-only"))
                                               && (exists p :: p in PhasesPs() && CaseFold(p) == CaseFold(args[1]))
                                       then RunPhasePs(args[1]) else UsageThrown)
{
  if |args| == 2 {
    OnlyFlagFold(args[0]);
    ContainsExists(PhasesPs(), args[1]);
    if ContainsPs(PhasesPs(), args[1]) {
      PhaseNonEmpty(args[1]);
    }
  }
}

// Citation: "On kind they are labelled only after the chart's 15008 rules exist (§5)" (§4.1 line 344); **Expected:** (T14 S4) "mirrors the bash block line for line"
lemma P3_EnrolOnlyWith15008(policies: Option<CsString>, ports: CsString)
  requires IsSpaceSeparatedPortsPs(ports)
  ensures EnrollPs(policies, ports) <==> (policies.None? || policies.value == "" || "15008" in TokensPs(ports))
{
  assert NoUpper(ports) by {
    forall k | 0 <= k < |ports| ensures PlainAscii(ports[k]) { assert IsDigitChar(ports[k]) || ports[k] == ' '; }
  }
  SplitWsPlain(ports);
  var f: CsString := "15008";
  assert NoUpper(f);
  ContainsPlain(SplitWs(ports), f);
}
