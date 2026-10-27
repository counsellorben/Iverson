// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md, T11 S1
// Source: ci/pins.py norm, found, consistency
// Replay base: 680eb9cf5a6dc2d3dd6374446822969441bfed0f
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-26-csr-round12-supply-chain-implementation-plan/Pins.Consistency.dfy

// ---------------------------------------------------------------------------------------------------------
// Strings and library boundary
// ---------------------------------------------------------------------------------------------------------

newtype CodePoint = x: int | 0 <= x < 0x110000
type Str = seq<CodePoint>

datatype Option<T> = None | Some(value: T)

datatype Location = Location(file: Str, regex: Str)
datatype Row = Row(name: Str, source: Str, locations: seq<Location>)

// Approved axioms (uninterpreted, no contract).
function ReadFile(root: Str, file: Str): Option<Str>

function FindAll(regex: Str, text: Str): seq<Str>

// A Dafny string literal as Python code points (Dafny 4 char is a Unicode scalar value).
function S(s: string): Str
{
  seq(|s|, i requires 0 <= i < |s| => s[i] as int as CodePoint)
}

const V: CodePoint := 118                                  // "v"
const CannotRead: Str := S(": cannot read ")
const ColonSpace: Str := S(": ")
const HasNoMatchFor: Str := S(" has no match for ")
const LocationsDisagree: Str := S(": the locations disagree: ")
const CommaSpace: Str := S(", ")
const Equals: Str := S("=")

// ---------------------------------------------------------------------------------------------------------
// Transliteration of ci/pins.py:89-121
// ---------------------------------------------------------------------------------------------------------

// pins.py:89-90
function Norm(version: Str): Str
{
  if |version| > 0 && version[0] == V then version[1..] else version
}

// found's outcome: the list it returns, or the PinError it raises (its str()).
datatype FoundResult = FoundOk(out: seq<(Str, Str)>) | FoundErr(msg: Str)

// `[(loc["file"], v) for v in values]`
function Pairs(file: Str, values: seq<Str>): seq<(Str, Str)>
{
  seq(|values|, k requires 0 <= k < |values| => (file, values[k]))
}

// pins.py:93-105
function Found(row: Row, root: Str): FoundResult
{
  FoundLoop(row, root, 0, [])
}

// pins.py:96-105: the `for loc in row["locations"]` loop, from index i with accumulator out.
function FoundLoop(row: Row, root: Str, i: nat, out: seq<(Str, Str)>): FoundResult
  requires i <= |row.locations|
  decreases |row.locations| - i
{
  if i == |row.locations| then FoundOk(out)                                         // return out
  else
    var loc := row.locations[i];
    match ReadFile(root, loc.file)                                                    // open(...).read()
    case None =>                                                                      // except OSError
      FoundErr(row.name + CannotRead + loc.file + ColonSpace)                         // (e.strerror not modelled)
    case Some(text) =>
      var values := FindAll(loc.regex, text);                                         // re.findall(..., re.M)
      if values == [] then
        FoundErr(row.name + ColonSpace + loc.file + HasNoMatchFor + loc.regex)
      else
        FoundLoop(row, root, i + 1, out + Pairs(loc.file, values))                    // out += [...]
}

// `{norm(v) for _, v in values}`
function NormSet(values: seq<(Str, Str)>): set<Str>
{
  set p | p in values :: Norm(p.1)
}

// `", ".join(f"{f}={v}" for f, v in values)`
function Listing(values: seq<(Str, Str)>): Str
  decreases |values|
{
  if |values| == 0 then []
  else if |values| == 1 then values[0].0 + Equals + values[0].1
  else values[0].0 + Equals + values[0].1 + CommaSpace + Listing(values[1..])
}

// One iteration of consistency's loop body (pins.py:111-120): the errors it appends.
function RowErrors(row: Row, root: Str): seq<Str>
{
  match Found(row, root)
  case FoundErr(msg) => [msg]                                                         // errors.append(str(e)); continue
  case FoundOk(values) =>
    if |NormSet(values)| > 1 then
      [row.name + LocationsDisagree + Listing(values)]
    else
      []                                                                              // print(...) not modelled
}

// pins.py:108-121
function Consistency(rows: seq<Row>, root: Str): seq<Str>
{
  ConsistencyLoop(rows, root, 0, [])
}

function ConsistencyLoop(rows: seq<Row>, root: Str, i: nat, errors: seq<Str>): seq<Str>
  requires i <= |rows|
  decreases |rows| - i
{
  if i == |rows| then errors
  else ConsistencyLoop(rows, root, i + 1, errors + RowErrors(rows[i], root))
}

// ---------------------------------------------------------------------------------------------------------
// Helper predicates and lemmas
// ---------------------------------------------------------------------------------------------------------

ghost predicate LocOk(l: Location, root: Str)
{
  ReadFile(root, l.file).Some? && FindAll(l.regex, ReadFile(root, l.file).value) != []
}

ghost predicate Matches(l: Location, root: Str, v: Str)
{
  ReadFile(root, l.file).Some? && v in FindAll(l.regex, ReadFile(root, l.file).value)
}

// Some pair of s has v as its version.
opaque ghost predicate HasSnd(s: seq<(Str, Str)>, v: Str)
{
  exists p :: p in s && p.1 == v
}

// Some location at index i or later matches v.
opaque ghost predicate MatchFrom(locs: seq<Location>, root: Str, i: nat, v: Str)
{
  exists j :: i <= j < |locs| && Matches(locs[j], root, v)
}

lemma FoundLoopOk(row: Row, root: Str, i: nat, out: seq<(Str, Str)>)
  requires i <= |row.locations|
  ensures FoundLoop(row, root, i, out).FoundOk? <==> forall j :: i <= j < |row.locations| ==> LocOk(row.locations[j], root)
  decreases |row.locations| - i
{
  if i < |row.locations| {
    var loc := row.locations[i];
    if LocOk(loc, root) {
      var out' := out + Pairs(loc.file, FindAll(loc.regex, ReadFile(root, loc.file).value));
      assert FoundLoop(row, root, i, out) == FoundLoop(row, root, i + 1, out');
      FoundLoopOk(row, root, i + 1, out');
    }
  }
}

lemma HasSndAppend(out: seq<(Str, Str)>, file: Str, values: seq<Str>, v: Str)
  ensures HasSnd(out + Pairs(file, values), v) <==> HasSnd(out, v) || v in values
{
  reveal HasSnd();
  var out' := out + Pairs(file, values);
  if v in values {
    var k :| 0 <= k < |values| && values[k] == v;
    assert out'[|out| + k] == (file, v);
  }
  if HasSnd(out', v) {
    var p :| p in out' && p.1 == v;
    var k :| 0 <= k < |out'| && out'[k] == p;
    if k >= |out| {
      assert Pairs(file, values)[k - |out|] == p;
    } else {
      assert out[k] == p;
    }
  }
  if HasSnd(out, v) {
    var p :| p in out && p.1 == v;
    var k :| 0 <= k < |out| && out[k] == p;
    assert out'[k] == p;
  }
}

lemma MatchFromStep(locs: seq<Location>, root: Str, i: nat, v: Str)
  requires i < |locs|
  ensures MatchFrom(locs, root, i, v) <==> Matches(locs[i], root, v) || MatchFrom(locs, root, i + 1, v)
{
  reveal MatchFrom();
  if MatchFrom(locs, root, i, v) {
    var j :| i <= j < |locs| && Matches(locs[j], root, v);
    if j > i {
      assert i + 1 <= j < |locs| && Matches(locs[j], root, v);
    }
  }
  if MatchFrom(locs, root, i + 1, v) {
    var j :| i + 1 <= j < |locs| && Matches(locs[j], root, v);
    assert i <= j;
  }
}

lemma FoundLoopValues(row: Row, root: Str, i: nat, out: seq<(Str, Str)>, v: Str)
  requires i <= |row.locations|
  requires FoundLoop(row, root, i, out).FoundOk?
  ensures HasSnd(FoundLoop(row, root, i, out).out, v) <==> HasSnd(out, v) || MatchFrom(row.locations, root, i, v)
  decreases |row.locations| - i
{
  if i == |row.locations| {
    reveal MatchFrom();
  } else {
    var loc := row.locations[i];
    assert ReadFile(root, loc.file).Some?;
    var text := ReadFile(root, loc.file).value;
    var values := FindAll(loc.regex, text);
    assert values != [];
    var out' := out + Pairs(loc.file, values);
    assert FoundLoop(row, root, i, out) == FoundLoop(row, root, i + 1, out');
    FoundLoopValues(row, root, i + 1, out', v);
    HasSndAppend(out, loc.file, values, v);
    MatchFromStep(row.locations, root, i, v);
  }
}

lemma CardGt1(s: set<Str>)
  ensures |s| > 1 <==> exists a, b :: a in s && b in s && a != b
{
  if exists a, b :: a in s && b in s && a != b {
    var a, b :| a in s && b in s && a != b;
    var r := s - {a};
    assert s == r + {a};
    assert a !in r;
    assert |s| == |r| + 1;
    assert b in r;
    assert |r| != 0;
  }
  if |s| > 1 {
    assert s != {};
    var a :| a in s;
    var r := s - {a};
    assert s == r + {a};
    assert |s| == |r| + 1;
    assert r != {};
    var b :| b in r;
    assert a in s && b in s && a != b;
  }
}

lemma NormSetDisagree(values: seq<(Str, Str)>)
  ensures |NormSet(values)| > 1 <==>
    exists p, q :: p in values && q in values && Norm(p.1) != Norm(q.1)
{
  CardGt1(NormSet(values));
  if exists p, q :: p in values && q in values && Norm(p.1) != Norm(q.1) {
    var p, q :| p in values && q in values && Norm(p.1) != Norm(q.1);
    assert Norm(p.1) in NormSet(values) && Norm(q.1) in NormSet(values);
  }
  if |NormSet(values)| > 1 {
    var a, b :| a in NormSet(values) && b in NormSet(values) && a != b;
    var p :| p in values && Norm(p.1) == a;
    var q :| q in values && Norm(q.1) == b;
  }
}

lemma LoopNoErrors(rows: seq<Row>, root: Str, i: nat, errors: seq<Str>)
  requires i <= |rows|
  ensures ConsistencyLoop(rows, root, i, errors) == [] <==>
    (errors == [] && forall j :: i <= j < |rows| ==> RowErrors(rows[j], root) == [])
  decreases |rows| - i
{
  if i < |rows| {
    LoopNoErrors(rows, root, i + 1, errors + RowErrors(rows[i], root));
  }
}

// The row's values disagree exactly when two locations' matches differ after Norm.
lemma DisagreeIffMatchesDiffer(row: Row, root: Str)
  requires Found(row, root).FoundOk?
  ensures (exists p, q :: p in Found(row, root).out && q in Found(row, root).out && Norm(p.1) != Norm(q.1)) <==>
    !(forall l1, l2, a, b :: l1 in row.locations && l2 in row.locations && Matches(l1, root, a) && Matches(l2, root, b) ==> Norm(a) == Norm(b))
{
  var values := Found(row, root).out;
  if exists p, q :: p in values && q in values && Norm(p.1) != Norm(q.1) {
    var p, q :| p in values && q in values && Norm(p.1) != Norm(q.1);
    FoundLoopValues(row, root, 0, [], p.1);
    FoundLoopValues(row, root, 0, [], q.1);
    reveal HasSnd();
    reveal MatchFrom();
    assert HasSnd(values, p.1);
    assert HasSnd(values, q.1);
    var j1 :| 0 <= j1 < |row.locations| && Matches(row.locations[j1], root, p.1);
    var j2 :| 0 <= j2 < |row.locations| && Matches(row.locations[j2], root, q.1);
    var l1, l2 := row.locations[j1], row.locations[j2];
    assert l1 in row.locations && l2 in row.locations && Matches(l1, root, p.1) && Matches(l2, root, q.1) && Norm(p.1) != Norm(q.1);
  } else {
    forall l1, l2, a, b | l1 in row.locations && l2 in row.locations && Matches(l1, root, a) && Matches(l2, root, b)
      ensures Norm(a) == Norm(b)
    {
      var j1 :| 0 <= j1 < |row.locations| && row.locations[j1] == l1;
      var j2 :| 0 <= j2 < |row.locations| && row.locations[j2] == l2;
      FoundLoopValues(row, root, 0, [], a);
      FoundLoopValues(row, root, 0, [], b);
      reveal HasSnd();
      reveal MatchFrom();
      assert MatchFrom(row.locations, root, 0, a);
      assert MatchFrom(row.locations, root, 0, b);
      var p :| p in values && p.1 == a;
      var q :| q in values && q.1 == b;
    }
  }
}

// ---------------------------------------------------------------------------------------------------------
// Properties (frozen)
// ---------------------------------------------------------------------------------------------------------

// Citation: design PinChecks.P1_ConsistencyIffLocationsEqual: "every row's locations hold the same version. Fails on a mismatch." (spec line 156); plan tests `test_consistency_fails_on_a_second_match_in_one_file_that_differs`, `test_consistency_fails_when_a_location_matches_nothing`.
lemma P1_RowPassesIffEveryLocationMatchesAndAllAgree(row: Row, root: Str)
  ensures RowErrors(row, root) == [] <==>
    (forall l :: l in row.locations ==> ReadFile(root, l.file).Some? && FindAll(l.regex, ReadFile(root, l.file).value) != [])
    && (forall l1, l2, a, b :: l1 in row.locations && l2 in row.locations && ReadFile(root, l1.file).Some? && ReadFile(root, l2.file).Some? && a in FindAll(l1.regex, ReadFile(root, l1.file).value) && b in FindAll(l2.regex, ReadFile(root, l2.file).value) ==> Norm(a) == Norm(b))
{
  FoundLoopOk(row, root, 0, []);
  if forall j :: 0 <= j < |row.locations| ==> LocOk(row.locations[j], root) {
    forall l | l in row.locations ensures LocOk(l, root) {
      var j :| 0 <= j < |row.locations| && row.locations[j] == l;
    }
  } else {
    var j :| 0 <= j < |row.locations| && !LocOk(row.locations[j], root);
    assert row.locations[j] in row.locations;
  }
  if Found(row, root).FoundOk? {
    NormSetDisagree(Found(row, root).out);
    DisagreeIffMatchesDiffer(row, root);
  }
}

// Citation: "every match of every location's regex must agree (a leading `v` is ignored)" (plan T11 S1); test `test_consistency_passes_when_every_match_agrees_ignoring_a_leading_v`.
lemma P2_LeadingVIgnored(x: Str)
  requires |x| == 0 || x[0] != 118
  ensures Norm([118] + x) == Norm(x)
{
  assert ([118] + x)[1..] == x;
}

// Citation: "every row's locations hold the same version. Fails on a mismatch." (spec line 156); test `test_the_repositorys_table_is_consistent`.
lemma P3_TableFailsIffSomeRowFails(rows: seq<Row>, root: Str)
  ensures Consistency(rows, root) == [] <==> forall r :: r in rows ==> RowErrors(r, root) == []
{
  LoopNoErrors(rows, root, 0, []);
  if forall j :: 0 <= j < |rows| ==> RowErrors(rows[j], root) == [] {
    forall r | r in rows ensures RowErrors(r, root) == [] {
      var j :| 0 <= j < |rows| && rows[j] == r;
    }
  }
}
