// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T27 S14
// Source: $LIVE/kind-checks.sh alive() (its embedded Python) and the at-most-60 wait loop
// Replay base: 7c4d86687b557e7e33a52c30cc07e6728dfb4622
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-21-csr-round12-platform-implementation-plan/kind-checks.alive-wait.dfy

// ---------------------------------------------------------------------------
// Shared vocabulary
// ---------------------------------------------------------------------------

datatype Option<T> = None | Some(value: T)

// Python strings: sequences of code points.
newtype CodePoint = x: int | 0 <= x < 0x110000

// Outcome of the embedded Python program: a printed value, or an uncaught exception
// (the program then prints nothing).
datatype PyOutcome<T> = Ok(v: T) | ValueError | IndexError

const TAB: CodePoint := 9
const NL: CodePoint := 10
const COMMA: CodePoint := 44

// "Alive"
const AliveWord: seq<CodePoint> := [65, 108, 105, 118, 101]
// "none"
const NoneWord: seq<CodePoint> := [110, 111, 110, 101]
// ","
const CommaStr: seq<CodePoint> := [44]
// ",false,"
const CommaFalseComma: seq<CodePoint> := [44, 102, 97, 108, 115, 101, 44]
// ",none,"
const CommaNoneComma: seq<CodePoint> := [44, 110, 111, 110, 101, 44]
// ",,"
const CommaComma: seq<CodePoint> := [44, 44]

// ---------------------------------------------------------------------------
// Python library boundary, modelled
// ---------------------------------------------------------------------------

// str.isspace / the set str.strip() with no argument removes (_PyUnicode_IsWhitespace).
predicate IsPySpace(c: CodePoint)
{
  (9 <= c <= 13) || (0x1C <= c <= 0x1F) || c == 0x20 || c == 0x85 || c == 0xA0
  || c == 0x1680 || (0x2000 <= c <= 0x200A) || c == 0x2028 || c == 0x2029
  || c == 0x202F || c == 0x205F || c == 0x3000
}

// `not l.strip()`: strip() leaves the empty string exactly when every code point is whitespace.
predicate IsBlank(l: seq<CodePoint>)
{
  forall k :: 0 <= k < |l| ==> IsPySpace(l[k])
}

// l.rstrip("\n"): removes every trailing "\n".
function RStripNL(s: seq<CodePoint>): seq<CodePoint>
  decreases |s|
{
  if |s| > 0 && s[|s| - 1] == NL then RStripNL(s[..|s| - 1]) else s
}

// s.split(sep) with an explicit one-character separator: count(sep) + 1 pieces, empties kept.
function Split(s: seq<CodePoint>, sep: CodePoint): seq<seq<CodePoint>>
  ensures |Split(s, sep)| >= 1
  decreases |s|
{
  if |s| == 0 then [[]]
  else if s[0] == sep then [[]] + Split(s[1..], sep)
  else
    var r := Split(s[1..], sep);
    [[s[0]] + r[0]] + r[1..]
}

// One row of the comprehension: l.rstrip("\n").split("\t").
function Fields(l: seq<CodePoint>): seq<seq<CodePoint>>
{
  Split(RStripNL(l), TAB)
}

// First position of x in xs (list.index's search), None when absent.
function FirstIndex(xs: seq<seq<CodePoint>>, x: seq<CodePoint>): Option<nat>
  ensures FirstIndex(xs, x).Some? ==> FirstIndex(xs, x).value < |xs|
  decreases |xs|
{
  if |xs| == 0 then None
  else if xs[0] == x then Some(0)
  else match FirstIndex(xs[1..], x)
    case None => None
    case Some(k) => Some(k + 1)
}

// list.index(x): ValueError when absent.
function ListIndex(xs: seq<seq<CodePoint>>, x: seq<CodePoint>): PyOutcome<int>
{
  match FirstIndex(xs, x)
  case None => ValueError
  case Some(k) => Ok(k)
}

// r[i] on a list: negative indices count from the end; out of range is IndexError.
function PyIndex(r: seq<seq<CodePoint>>, i: int): PyOutcome<seq<CodePoint>>
{
  if 0 <= i < |r| then Ok(r[i])
  else if -|r| <= i < 0 then Ok(r[|r| + i])
  else IndexError
}

// ",".join(xs)
function JoinComma(xs: seq<seq<CodePoint>>): seq<CodePoint>
  decreases |xs|
{
  if |xs| == 0 then []
  else if |xs| == 1 then xs[0]
  else xs[0] + CommaStr + JoinComma(xs[1..])
}

// Substring containment: a bash `case` glob `*P*` with P free of glob metacharacters.
predicate Contains(s: seq<CodePoint>, p: seq<CodePoint>)
  decreases |s|
{
  |s| >= |p| && (s[..|p|] == p || (|s| > 0 && Contains(s[1..], p)))
}

// ---------------------------------------------------------------------------
// Transliteration: alive() (kind-checks.sh:6-11), the embedded Python on lines 7-10
// `out` is the sequence of lines Python's sys.stdin yields from `mysql --batch`.
// ---------------------------------------------------------------------------

// Line 8: rows = [l.rstrip("\n").split("\t") for l in sys.stdin if l.strip()]
function RowsOf(out: seq<seq<CodePoint>>): seq<seq<seq<CodePoint>>>
  decreases |out|
{
  if |out| == 0 then []
  else (if !IsBlank(out[0]) then [Fields(out[0])] else []) + RowsOf(out[1..])
}

// Line 10's generator `r[i] for r in rows[1:]`, consumed by join (IndexError propagates).
function GenIndex(rows: seq<seq<seq<CodePoint>>>, i: int): PyOutcome<seq<seq<CodePoint>>>
  decreases |rows|
{
  if |rows| == 0 then Ok([])
  else match PyIndex(rows[0], i)
    case Ok(x) =>
      (match GenIndex(rows[1..], i)
       case Ok(xs) => Ok([x] + xs)
       case ValueError => ValueError
       case IndexError => IndexError)
    case ValueError => ValueError
    case IndexError => IndexError
}

// Lines 8-10: the program's outcome; Ok(s) means print(s) ran.
function AliveProgram(out: seq<seq<CodePoint>>): PyOutcome<seq<CodePoint>>
{
  var rows := RowsOf(out);                                                    // line 8
  var iR: PyOutcome<int> := if |rows| > 0 then ListIndex(rows[0], AliveWord)  // line 9
                            else Ok(-1);
  match iR
  case ValueError => ValueError
  case IndexError => IndexError
  case Ok(i) =>
    if i >= 0 then                                                            // line 10
      (match GenIndex(rows[1..], i)
       case Ok(col) => Ok(JoinComma(col))
       case ValueError => ValueError
       case IndexError => IndexError)
    else Ok(NoneWord)
}

// The text alive() prints (without print's terminating "\n"); a raise prints nothing.
function Alive(out: seq<seq<CodePoint>>): seq<CodePoint>
{
  match AliveProgram(out)
  case Ok(s) => s
  case ValueError => []
  case IndexError => []
}

// ---------------------------------------------------------------------------
// Specification vocabulary for P1
// ---------------------------------------------------------------------------

// The index of `Alive` among the header line's tab-separated fields.
function AliveIndex(h: seq<CodePoint>): Option<nat>
{
  FirstIndex(Fields(h), AliveWord)
}

// The header has an Alive field and every data row has more fields than its index.
predicate WellFormedBatch(out: seq<seq<CodePoint>>)
{
  |out| > 0 && AliveIndex(out[0]).Some?
  && forall k :: 1 <= k < |out| ==> |Fields(out[k])| > AliveIndex(out[0]).value
}

// The i-th tab-separated field of each line.
function Column(lines: seq<seq<CodePoint>>, i: nat): seq<seq<CodePoint>>
  requires forall k :: 0 <= k < |lines| ==> |Fields(lines[k])| > i
  decreases |lines|
{
  if |lines| == 0 then [] else [Fields(lines[0])[i]] + Column(lines[1..], i)
}

// ---------------------------------------------------------------------------
// Transliteration: the wait (kind-checks.sh:15-24)
// views[k]: the frontends' and backends' alive strings in iteration k, and whether the
// /health read in that iteration holds "starrocks":true.
// ---------------------------------------------------------------------------

// Environment responses past the supplied ones read as "no output, unhealthy".
function ViewAt(views: seq<(seq<CodePoint>, seq<CodePoint>, bool)>, k: nat): (seq<CodePoint>, seq<CodePoint>, bool)
{
  if k < |views| then views[k] else ([], [], false)
}

// Line 17's case word ",$(alive FE),$(alive BE),"
function Subject(f: seq<CodePoint>, b: seq<CodePoint>): seq<CodePoint>
{
  CommaStr + f + CommaStr + b + CommaStr
}

// Lines 16-23: `for _ in $(seq 1 60)`; k iterations already completed. Returns the
// iterations run, the breaking one included.
function ForLoop(views: seq<(seq<CodePoint>, seq<CodePoint>, bool)>, k: nat): nat
  requires k <= 60
  decreases 60 - k
{
  if k == 60 then 60                                                          // seq exhausted
  else
    var v := ViewAt(views, k);
    var subject := Subject(v.0, v.1);                                         // line 17
    if Contains(subject, CommaFalseComma) || Contains(subject, CommaNoneComma)
       || Contains(subject, CommaComma) then
      ForLoop(views, k + 1)                                                   // line 18 `;;`, line 22 sleep
    else if v.2 then
      k + 1                                                                   // lines 19-20 `&& break`
    else
      ForLoop(views, k + 1)                                                   // line 22 sleep 5
}

// Line 15: the loop runs only when `kubectl get pod iverson-starrocks-fe-0` succeeds.
function WaitLoop(fePodExists: bool, views: seq<(seq<CodePoint>, seq<CodePoint>, bool)>): nat
{
  if fePodExists then ForLoop(views, 0) else 0
}

// `,f,b,` contains none of `,false,`, `,none,`, `,,`, and healthy.
predicate Done(f: seq<CodePoint>, b: seq<CodePoint>, healthy: bool)
{
  var s := Subject(f, b);
  !Contains(s, CommaFalseComma) && !Contains(s, CommaNoneComma) && !Contains(s, CommaComma)
  && healthy
}

// ---------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------

lemma RowsOfNoBlank(out: seq<seq<CodePoint>>)
  requires forall l :: l in out ==> !IsBlank(l)
  ensures |RowsOf(out)| == |out|
  ensures forall k :: 0 <= k < |out| ==> RowsOf(out)[k] == Fields(out[k])
  decreases |out|
{
  if |out| > 0 {
    assert out[0] in out;
    assert forall l :: l in out[1..] ==> l in out;
    RowsOfNoBlank(out[1..]);
    assert RowsOf(out) == [Fields(out[0])] + RowsOf(out[1..]);
  }
}

lemma GenIndexColumn(lines: seq<seq<CodePoint>>, i: nat)
  requires forall l :: l in lines ==> !IsBlank(l)
  requires forall k :: 0 <= k < |lines| ==> |Fields(lines[k])| > i
  ensures GenIndex(RowsOf(lines), i) == Ok(Column(lines, i))
  decreases |lines|
{
  if |lines| > 0 {
    assert lines[0] in lines;
    assert forall l :: l in lines[1..] ==> l in lines;
    assert RowsOf(lines) == [Fields(lines[0])] + RowsOf(lines[1..]);
    assert RowsOf(lines)[1..] == RowsOf(lines[1..]);
    GenIndexColumn(lines[1..], i);
    assert PyIndex(RowsOf(lines)[0], i) == Ok(Fields(lines[0])[i]);
  }
}

lemma ForLoopFacts(views: seq<(seq<CodePoint>, seq<CodePoint>, bool)>, k: nat)
  requires k < 60
  ensures k + 1 <= ForLoop(views, k) <= 60
  ensures ForLoop(views, k) < 60 ==>
    var v := ViewAt(views, ForLoop(views, k) - 1); Done(v.0, v.1, v.2)
  decreases 60 - k
{
  var v := ViewAt(views, k);
  var subject := Subject(v.0, v.1);
  if Contains(subject, CommaFalseComma) || Contains(subject, CommaNoneComma)
     || Contains(subject, CommaComma) {
    if k + 1 < 60 { ForLoopFacts(views, k + 1); }
  } else if v.2 {
    assert ForLoop(views, k) == k + 1;
    assert Done(v.0, v.1, v.2);
  } else {
    if k + 1 < 60 { ForLoopFacts(views, k + 1); }
  }
}

// ---------------------------------------------------------------------------
// Approved axioms: none
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

// Citation: **Expected:** (T27 S14) "`local K2 frontends alive: true backends alive: true` (one FE and one BE on the local profile; every row `true`)"
lemma P1_AliveColumn(out: seq<seq<CodePoint>>)
  requires forall l :: l in out ==> !IsBlank(l)
  requires |out| == 0 || WellFormedBatch(out)
  ensures Alive(out) == (if |out| == 0 then NoneWord else JoinComma(Column(out[1..], AliveIndex(out[0]).value)))
{
  RowsOfNoBlank(out);
  if |out| > 0 {
    var rows := RowsOf(out);
    var i := AliveIndex(out[0]).value;
    assert rows[0] == Fields(out[0]);
    assert ListIndex(rows[0], AliveWord) == Ok(i);
    assert out[0] in out;
    assert RowsOf(out) == [Fields(out[0])] + RowsOf(out[1..]);
    assert rows[1..] == RowsOf(out[1..]);
    assert forall l :: l in out[1..] ==> l in out;
    forall k | 0 <= k < |out[1..]|
      ensures |Fields(out[1..][k])| > i
    {
      assert out[1..][k] == out[k + 1];
    }
    GenIndexColumn(out[1..], i);
  }
}

// Citation: **Expected:** (T27 S14) "printed after the script's wait of at most 5 minutes (the hook releases Helm once the BE is listed, which is before its process starts); a `false` after the wait is a failure"
lemma P2_WaitBounded(fePodExists: bool, views: seq<(seq<CodePoint>, seq<CodePoint>, bool)>)
  requires |views| >= 60
  ensures !fePodExists ==> WaitLoop(fePodExists, views) == 0
  ensures fePodExists ==> 1 <= WaitLoop(fePodExists, views) <= 60
  ensures fePodExists && WaitLoop(fePodExists, views) < 60 ==> Done(views[WaitLoop(fePodExists, views) - 1].0, views[WaitLoop(fePodExists, views) - 1].1, views[WaitLoop(fePodExists, views) - 1].2)
{
  if fePodExists {
    ForLoopFacts(views, 0);
    var w := ForLoop(views, 0);
    assert ViewAt(views, w - 1) == views[w - 1];
  }
}
