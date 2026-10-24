// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T22 S4
// Source: Iverson.Server/deploy/starrocks/fe-tls-entrypoint.sh the append-each-setting-once loop (17–24)
// Replay base: 0c82f27cfa35fbee2e3255b1d65937a0def814d2
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-21-csr-round12-platform-implementation-plan/fe-tls-entrypoint.settings.dfy

// ---------------------------------------------------------------------------------------------
// Vocabulary
// ---------------------------------------------------------------------------------------------

newtype Byte = x: int | 0 <= x < 256

const NUL: Byte := 0
const NL: Byte := 10
const SP: Byte := 32
const EQ: Byte := 61
const DIGIT0: Byte := 48
const PLUS: Byte := 43
const MINUS: Byte := 45

// POSIX [[:space:]] (C locale): space, tab, newline, vertical tab, form feed, carriage return.
predicate IsSpace(b: Byte) { b == 32 || b == 9 || b == 10 || b == 11 || b == 12 || b == 13 }

// Script constants and string literals, as bytes (each comment gives the ASCII text).
// line 12: keystore=/run/iverson-fe-tls/fe.p12
const KEYSTORE: seq<Byte> := [47, 114, 117, 110, 47, 105, 118, 101, 114, 115, 111, 110, 45, 102, 101, 45, 116, 108, 115, 47, 102, 101, 46, 112, 49, 50]
// line 14: password=iverson-fe-keystore
const PASSWORD: seq<Byte> := [105, 118, 101, 114, 115, 111, 110, 45, 102, 101, 45, 107, 101, 121, 115, 116, 111, 114, 101]
// line 18 literal "ssl_keystore_location = "
const LIT_LOCATION: seq<Byte> := [115, 115, 108, 95, 107, 101, 121, 115, 116, 111, 114, 101, 95, 108, 111, 99, 97, 116, 105, 111, 110, 32, 61, 32]
// line 18 literal "ssl_keystore_password = "
const LIT_KS_PASSWORD: seq<Byte> := [115, 115, 108, 95, 107, 101, 121, 115, 116, 111, 114, 101, 95, 112, 97, 115, 115, 119, 111, 114, 100, 32, 61, 32]
// line 18 literal "ssl_key_password = "
const LIT_KEY_PASSWORD: seq<Byte> := [115, 115, 108, 95, 107, 101, 121, 95, 112, 97, 115, 115, 119, 111, 114, 100, 32, 61, 32]

// The three keys.
// "ssl_keystore_location"
const KEY_LOCATION: seq<Byte> := [115, 115, 108, 95, 107, 101, 121, 115, 116, 111, 114, 101, 95, 108, 111, 99, 97, 116, 105, 111, 110]
// "ssl_keystore_password"
const KEY_KS_PASSWORD: seq<Byte> := [115, 115, 108, 95, 107, 101, 121, 115, 116, 111, 114, 101, 95, 112, 97, 115, 115, 119, 111, 114, 100]
// "ssl_key_password"
const KEY_KEY_PASSWORD: seq<Byte> := [115, 115, 108, 95, 107, 101, 121, 95, 112, 97, 115, 115, 119, 111, 114, 100]

function Keys(): set<seq<Byte>> { {KEY_LOCATION, KEY_KS_PASSWORD, KEY_KEY_PASSWORD} }

// Lines of a file: split at each newline only (a NUL is an ordinary byte); a final newline ends
// the last line, and a final unterminated run of bytes is a line of its own.
function FirstNL(c: seq<Byte>): nat
  ensures FirstNL(c) <= |c|
  ensures FirstNL(c) < |c| ==> c[FirstNL(c)] == NL
  ensures forall m | 0 <= m < FirstNL(c) :: c[m] != NL
{
  if |c| == 0 then 0 else if c[0] == NL then 0 else 1 + FirstNL(c[1..])
}

opaque function Lines(c: seq<Byte>): seq<seq<Byte>>
  decreases |c|
{
  if |c| == 0 then []
  else
    var i := FirstNL(c);
    if i == |c| then [c] else [c[..i]] + Lines(c[i + 1..])
}

// One line matches the POSIX ERE ^[[:space:]]*<key>[[:space:]]*= at leading-space run [0, i) and
// '=' at j (the key holds no metacharacter).
predicate MatchAt(l: seq<Byte>, k: seq<Byte>, i: int, j: int)
{
  0 <= i && i + |k| <= j < |l| &&
  (forall m | 0 <= m < i :: IsSpace(l[m])) && l[i..i + |k|] == k &&
  (forall m | i + |k| <= m < j :: IsSpace(l[m])) && l[j] == EQ
}

predicate EreMatch(l: seq<Byte>, k: seq<Byte>)
{
  exists i, j | 0 <= i < |l| && 0 <= j < |l| :: MatchAt(l, k, i, j)
}

// HasSetting(conf, key): some line matches ^[[:space:]]*<key>[[:space:]]*=
predicate HasSetting(conf: seq<Byte>, k: seq<Byte>)
{
  exists l | l in Lines(conf) :: EreMatch(l, k)
}

// ---------------------------------------------------------------------------------------------
// Library boundary (modelled, no axioms)
// ---------------------------------------------------------------------------------------------

// ${setting%% =*}: remove the longest suffix matching the glob ' =*', i.e. everything from the
// first " =" on; unchanged when there is none.
function StripLongestSuffixSpEqStar(s: seq<Byte>): seq<Byte>
  decreases |s|
{
  if |s| < 2 then s
  else if s[0] == SP && s[1] == EQ then []
  else [s[0]] + StripLongestSuffixSpEqStar(s[1..])
}

// grep -aEq "^[[:space:]]*${key}[[:space:]]*=" "$conf": exit 0 iff some line matches. With -a
// (--binary-files=text) GNU grep reads a file holding NUL bytes as text: lines end at newlines
// only, and a NUL is an ordinary byte (probed on GNU grep 3.12).
function GrepLines(f: seq<Byte>): seq<seq<Byte>> { Lines(f) }

predicate GrepAEq(k: seq<Byte>, f: seq<Byte>)
{
  exists l | l in GrepLines(f) :: EreMatch(l, k)
}

// [ -s "$conf" ]: the file is non-empty.
predicate TestS(f: seq<Byte>) { |f| > 0 }

// tail -c 1 "$conf": the last byte, if any.
function TailC1(f: seq<Byte>): seq<Byte> { if |f| == 0 then [] else [f[|f| - 1]] }

// wc -l (reading stdin): the number of newline bytes, printed in decimal and then a newline.
function CountNL(s: seq<Byte>): nat
  decreases |s|
{
  if |s| == 0 then 0 else (if s[0] == NL then 1 else 0) + CountNL(s[1..])
}

function Decimal(n: nat): seq<Byte>
  decreases n
{
  if n < 10 then [(48 + n) as Byte] else Decimal(n / 10) + [(48 + n % 10) as Byte]
}

function WcL(s: seq<Byte>): seq<Byte> { Decimal(CountNL(s)) + [NL] }

// $( … ): bash drops every NUL byte, then strips trailing newlines.
function DropNuls(s: seq<Byte>): seq<Byte>
  decreases |s|
{
  if |s| == 0 then [] else (if s[0] == NUL then [] else [s[0]]) + DropNuls(s[1..])
}

function StripTrailingNLs(s: seq<Byte>): seq<Byte>
  decreases |s|
{
  if |s| > 0 && s[|s| - 1] == NL then StripTrailingNLs(s[..|s| - 1]) else s
}

function CommandSubst(out: seq<Byte>): seq<Byte> { StripTrailingNLs(DropNuls(out)) }

// [ "$x" -eq 0 ]: true iff x is an optionally signed run of decimal digits whose value is 0, i.e.
// an optional sign followed by one or more '0' bytes (a non-integer operand is an error, exit 2,
// so the test is false).
predicate AllZeros(x: seq<Byte>) { |x| > 0 && forall m | 0 <= m < |x| :: x[m] == DIGIT0 }

predicate TestEqZero(x: seq<Byte>)
{
  AllZeros(x) || (|x| > 0 && (x[0] == PLUS || x[0] == MINUS) && AllZeros(x[1..]))
}

// ---------------------------------------------------------------------------------------------
// Transliteration of fe-tls-entrypoint.sh lines 17–24
// ---------------------------------------------------------------------------------------------

// line 18: the loop's word list.
opaque function Settings(): seq<seq<Byte>>
{
  [LIT_LOCATION + KEYSTORE, LIT_KS_PASSWORD + PASSWORD, LIT_KEY_PASSWORD + PASSWORD]
}

// line 21: if [ -s "$conf" ] && [ "$(tail -c 1 "$conf" | wc -l)" -eq 0 ]; then echo >> "$conf"; fi
function Line21(conf: seq<Byte>): seq<Byte>
{
  if TestS(conf) && TestEqZero(CommandSubst(WcL(TailC1(conf)))) then conf + [NL] else conf
}

// lines 19–23: one pass of the loop body (conf is the content of "$conf", line 17).
opaque function Step(conf: seq<Byte>, setting: seq<Byte>): seq<Byte>
{
  var key := StripLongestSuffixSpEqStar(setting);                          // line 19
  if !GrepAEq(key, conf) then                                             // line 20
    var c1 := Line21(conf);                                               // line 21
    c1 + setting + [NL]                                                   // line 22
  else conf                                                               // line 23
}

// lines 18 and 24: for setting in …; do … done
function Loop(conf: seq<Byte>, settings: seq<seq<Byte>>): seq<Byte>
  decreases |settings|
{
  if |settings| == 0 then conf else Loop(Step(conf, settings[0]), settings[1..])
}

function Append(conf: seq<Byte>): seq<Byte> { Loop(conf, Settings()) }

// ---------------------------------------------------------------------------------------------
// Properties (frozen)
// ---------------------------------------------------------------------------------------------

// Citation: plan check `t22_fe.sh` (T22 S1: two runs, then "fe.conf has '<line>' once" for each setting and "fe.conf has four lines"), generalised
lemma P1_EachSettingOnce(conf: seq<Byte>)
  ensures forall k :: k in Keys() ==> HasSetting(Append(conf), k)
  ensures Append(Append(conf)) == Append(conf)
{
  SettingsFacts();
  var st := Settings();
  forall s | s in st ensures GoodSetting(s) {}
  LoopSets(conf, st);
  var r := Append(conf);
  forall k | k in Keys() ensures HasSetting(r, k) {
    assert k == StripLongestSuffixSpEqStar(st[0]) || k == StripLongestSuffixSpEqStar(st[1]) || k == StripLongestSuffixSpEqStar(st[2]);
  }
  LoopIdentity(r, st);
}

// Citation: plan check `t22_fe.sh` ("the existing line is kept whole", from a file with no trailing newline "as an edited file may have")
lemma P2_ExistingLinesKept(conf: seq<Byte>)
  ensures |Lines(Append(conf))| >= |Lines(conf)| && Lines(Append(conf))[..|Lines(conf)|] == Lines(conf)
{
  LoopPrefix(conf, Settings());
}

// ---------------------------------------------------------------------------------------------
// The three settings
// ---------------------------------------------------------------------------------------------

opaque function S0(): seq<Byte> { LIT_LOCATION + KEYSTORE }
opaque function S1(): seq<Byte> { LIT_KS_PASSWORD + PASSWORD }
opaque function S2(): seq<Byte> { LIT_KEY_PASSWORD + PASSWORD }

lemma SettingsAre()
  ensures Settings() == [S0(), S1(), S2()]
{
  reveal Settings(), S0(), S1(), S2();
}

lemma S0Shape()
  ensures S0() == KEY_LOCATION + [SP, EQ] + ([SP] + KEYSTORE)
  ensures NL !in S0()
{
  reveal S0();
  assert LIT_LOCATION == KEY_LOCATION + [SP, EQ] + [SP];
}

lemma S1Shape()
  ensures S1() == KEY_KS_PASSWORD + [SP, EQ] + ([SP] + PASSWORD)
  ensures NL !in S1()
{
  reveal S1();
  assert LIT_KS_PASSWORD == KEY_KS_PASSWORD + [SP, EQ] + [SP];
}

lemma S2Shape()
  ensures S2() == KEY_KEY_PASSWORD + [SP, EQ] + ([SP] + PASSWORD)
  ensures NL !in S2()
{
  reveal S2();
  assert LIT_KEY_PASSWORD == KEY_KEY_PASSWORD + [SP, EQ] + [SP];
}

predicate GoodSetting(s: seq<Byte>)
{
  NL !in s && EreMatch(s, StripLongestSuffixSpEqStar(s))
}

lemma SettingsFacts()
  ensures Settings() == [S0(), S1(), S2()]
  ensures StripLongestSuffixSpEqStar(S0()) == KEY_LOCATION
  ensures StripLongestSuffixSpEqStar(S1()) == KEY_KS_PASSWORD
  ensures StripLongestSuffixSpEqStar(S2()) == KEY_KEY_PASSWORD
  ensures GoodSetting(S0()) && GoodSetting(S1()) && GoodSetting(S2())
{
  SettingsAre();
  S0Shape(); S1Shape(); S2Shape();
  assert SP !in KEY_LOCATION && SP !in KEY_KS_PASSWORD && SP !in KEY_KEY_PASSWORD;
  StripKey(KEY_LOCATION, [SP] + KEYSTORE);
  StripKey(KEY_KS_PASSWORD, [SP] + PASSWORD);
  StripKey(KEY_KEY_PASSWORD, [SP] + PASSWORD);
  SettingMatches(KEY_LOCATION, [SP] + KEYSTORE);
  SettingMatches(KEY_KS_PASSWORD, [SP] + PASSWORD);
  SettingMatches(KEY_KEY_PASSWORD, [SP] + PASSWORD);
}

// ---------------------------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------------------------

lemma StripKey(k: seq<Byte>, rest: seq<Byte>)
  requires SP !in k
  ensures StripLongestSuffixSpEqStar(k + [SP, EQ] + rest) == k
  decreases |k|
{
  var s := k + [SP, EQ] + rest;
  if |k| > 0 {
    assert s[0] == k[0];
    assert s[1..] == k[1..] + [SP, EQ] + rest;
    StripKey(k[1..], rest);
    assert [k[0]] + k[1..] == k;
  }
}

lemma SettingMatches(k: seq<Byte>, rest: seq<Byte>)
  ensures EreMatch(k + [SP, EQ] + rest, k)
{
  var l := k + [SP, EQ] + rest;
  assert l[0..|k|] == k;
  assert l[|k|] == SP;
  assert l[|k| + 1] == EQ;
  assert MatchAt(l, k, 0, |k| + 1);
}

lemma LinesEmpty()
  ensures Lines([]) == []
{
  reveal Lines();
}

lemma FirstNLAt(a: seq<Byte>, b: seq<Byte>)
  requires NL !in a
  ensures FirstNL(a + [NL] + b) == |a|
  decreases |a|
{
  if |a| > 0 {
    assert a[0] in a;
    assert (a + [NL] + b)[1..] == a[1..] + [NL] + b;
    assert forall x | x in a[1..] :: x in a;
    FirstNLAt(a[1..], b);
  }
}

lemma LinesNLFirst(a: seq<Byte>, b: seq<Byte>)
  requires NL !in a
  ensures Lines(a + [NL] + b) == [a] + Lines(b)
{
  var c := a + [NL] + b;
  FirstNLAt(a, b);
  assert c[..|a|] == a;
  assert c[|a| + 1..] == b;
  reveal Lines();
}

lemma LinesLast(a: seq<Byte>)
  requires NL !in a
  ensures Lines(a + [NL]) == [a]
{
  LinesNLFirst(a, []);
  LinesEmpty();
  assert a + [NL] + [] == a + [NL];
}

lemma FirstNLPrefix(a: seq<Byte>, b: seq<Byte>)
  requires FirstNL(a) < |a|
  ensures FirstNL(a + b) == FirstNL(a)
  decreases |a|
{
  if a[0] != NL {
    assert (a + b)[1..] == a[1..] + b;
    FirstNLPrefix(a[1..], b);
  }
}

// Lines of a + b, when a is empty or ends with a newline.
lemma LinesConcat(a: seq<Byte>, b: seq<Byte>)
  requires |a| == 0 || a[|a| - 1] == NL
  ensures Lines(a + b) == Lines(a) + Lines(b)
  decreases |a|
{
  if |a| == 0 {
    assert a + b == b;
    LinesEmpty();
  } else {
    var i := FirstNL(a);
    assert a[|a| - 1] == NL;
    assert i < |a|;
    var c := a + b;
    FirstNLPrefix(a, b);
    assert c[..i] == a[..i];
    assert c[i + 1..] == a[i + 1..] + b;
    LinesConcat(a[i + 1..], b);
    reveal Lines();
  }
}

// Adding a newline to a non-empty file not ending in one leaves its lines unchanged.
lemma LinesCloseLast(a: seq<Byte>)
  requires |a| > 0 && a[|a| - 1] != NL
  ensures Lines(a + [NL]) == Lines(a)
  decreases |a|
{
  var i := FirstNL(a);
  var c := a + [NL];
  if i == |a| {
    FirstNLAt(a, []);
    assert a + [NL] + [] == c;
    assert c[..|a|] == a;
    assert c[|a| + 1..] == [];
    LinesEmpty();
    reveal Lines();
  } else {
    FirstNLPrefix(a, [NL]);
    assert c[..i] == a[..i];
    assert c[i + 1..] == a[i + 1..] + [NL];
    assert |a[i + 1..]| > 0;
    LinesCloseLast(a[i + 1..]);
    reveal Lines();
  }
}

// Line 21 by the file's last byte: a newline is written unless the file is empty or already ends
// with one (a NUL last byte included).
lemma Line21Cases(conf: seq<Byte>)
  ensures |conf| == 0 ==> Line21(conf) == conf
  ensures |conf| > 0 && conf[|conf| - 1] == NL ==> Line21(conf) == conf
  ensures |conf| > 0 && conf[|conf| - 1] != NL ==> Line21(conf) == conf + [NL]
{
  if |conf| > 0 {
    var b := conf[|conf| - 1];
    assert TailC1(conf) == [b];
    assert [b][1..] == [];
    assert CountNL([b]) == (if b == NL then 1 else 0) + CountNL([]);
    var w := WcL([b]);
    if b == NL {
      assert Decimal(1) == [49];
      assert w == [49, NL];
    } else {
      assert Decimal(0) == [48];
      assert w == [48, NL];
    }
    assert w[1..] == [NL] && [NL][1..] == [];
    assert DropNuls(w) == [w[0]] + DropNuls([NL]);
    assert DropNuls([NL]) == [NL] + DropNuls([]);
    assert DropNuls(w) == w;
    assert w[..|w| - 1] == [w[0]];
    assert w[0] != NL;
    assert StripTrailingNLs([w[0]]) == [w[0]];
    assert StripTrailingNLs(w) == StripTrailingNLs([w[0]]);
    assert CommandSubst(w) == [w[0]];
    assert AllZeros([w[0]]) <==> w[0] == DIGIT0;
  }
}

predicate IsPrefix(p: seq<seq<Byte>>, q: seq<seq<Byte>>) { |q| >= |p| && q[..|p|] == p }

// Appending a setting line after line 21 keeps the existing lines as a prefix.
lemma AppendLinePrefix(conf: seq<Byte>, setting: seq<Byte>)
  ensures IsPrefix(Lines(conf), Lines(Line21(conf) + setting + [NL]))
  ensures var c1 := Line21(conf); |c1| == 0 || c1[|c1| - 1] == NL
  ensures Lines(Line21(conf)) == Lines(conf)
{
  var c1 := Line21(conf);
  Line21Cases(conf);
  assert c1 + setting + [NL] == c1 + (setting + [NL]);
  if |conf| == 0 {
    LinesEmpty();
  } else if conf[|conf| - 1] == NL {
    LinesConcat(conf, setting + [NL]);
  } else {
    LinesCloseLast(conf);
    LinesConcat(c1, setting + [NL]);
  }
}

lemma StepPrefix(conf: seq<Byte>, setting: seq<Byte>)
  ensures IsPrefix(Lines(conf), Lines(Step(conf, setting)))
{
  reveal Step();
  AppendLinePrefix(conf, setting);
}

lemma LoopPrefix(conf: seq<Byte>, settings: seq<seq<Byte>>)
  ensures IsPrefix(Lines(conf), Lines(Loop(conf, settings)))
  decreases |settings|
{
  if |settings| > 0 {
    var c2 := Step(conf, settings[0]);
    StepPrefix(conf, settings[0]);
    LoopPrefix(c2, settings[1..]);
    var r := Loop(conf, settings);
    assert r == Loop(c2, settings[1..]);
    assert Lines(r)[..|Lines(conf)|] == Lines(r)[..|Lines(c2)|][..|Lines(conf)|];
  }
}

lemma HasSettingPrefix(a: seq<Byte>, b: seq<Byte>, k: seq<Byte>)
  requires IsPrefix(Lines(a), Lines(b)) && HasSetting(a, k)
  ensures HasSetting(b, k)
{
  var l :| l in Lines(a) && EreMatch(l, k);
  var n :| 0 <= n < |Lines(a)| && Lines(a)[n] == l;
  assert Lines(b)[n] == Lines(b)[..|Lines(a)|][n];
}

// With -a, grep sees exactly the file's lines.
lemma GrepIsHasSetting(conf: seq<Byte>, k: seq<Byte>)
  ensures GrepAEq(k, conf) == HasSetting(conf, k)
{
}

lemma StepSets(conf: seq<Byte>, setting: seq<Byte>)
  requires GoodSetting(setting)
  ensures HasSetting(Step(conf, setting), StripLongestSuffixSpEqStar(setting))
  ensures forall k :: HasSetting(conf, k) ==> HasSetting(Step(conf, setting), k)
{
  var key := StripLongestSuffixSpEqStar(setting);
  reveal Step();
  var r := Step(conf, setting);
  if !GrepAEq(key, conf) {
    var c1 := Line21(conf);
    AppendLinePrefix(conf, setting);
    assert r == c1 + (setting + [NL]);
    LinesConcat(c1, setting + [NL]);
    LinesLast(setting);
    assert Lines(r)[|Lines(c1)|] == setting;
    assert setting in Lines(r);
    forall k | HasSetting(conf, k) ensures HasSetting(r, k) { HasSettingPrefix(conf, r, k); }
  } else {
    GrepIsHasSetting(conf, key);
  }
}

// The loop sets every key of its settings and keeps every key already set.
lemma LoopSets(conf: seq<Byte>, settings: seq<seq<Byte>>)
  requires forall s | s in settings :: GoodSetting(s)
  ensures forall s | s in settings :: HasSetting(Loop(conf, settings), StripLongestSuffixSpEqStar(s))
  ensures forall k :: HasSetting(conf, k) ==> HasSetting(Loop(conf, settings), k)
  decreases |settings|
{
  if |settings| > 0 {
    var setting := settings[0];
    assert setting in settings;
    var c2 := Step(conf, setting);
    StepSets(conf, setting);
    var rest := settings[1..];
    assert forall s | s in rest :: s in settings;
    LoopSets(c2, rest);
    var r := Loop(conf, settings);
    assert r == Loop(c2, rest);
    forall s | s in settings ensures HasSetting(r, StripLongestSuffixSpEqStar(s)) {
      if s != setting {
        var n :| 0 <= n < |settings| && settings[n] == s;
        assert rest[n - 1] == s;
      }
    }
  }
}

// When every key is already set, the loop changes nothing.
lemma LoopIdentity(conf: seq<Byte>, settings: seq<seq<Byte>>)
  requires forall s | s in settings :: HasSetting(conf, StripLongestSuffixSpEqStar(s))
  ensures Loop(conf, settings) == conf
  decreases |settings|
{
  if |settings| > 0 {
    assert settings[0] in settings;
    GrepIsHasSetting(conf, StripLongestSuffixSpEqStar(settings[0]));
    reveal Step();
    assert Step(conf, settings[0]) == conf;
    assert forall s | s in settings[1..] :: s in settings;
    LoopIdentity(conf, settings[1..]);
  }
}
