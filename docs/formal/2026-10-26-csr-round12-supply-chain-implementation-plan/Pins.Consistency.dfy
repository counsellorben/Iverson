// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md, T11 S1
// Source: ci/pins.py norm, found, consistency
// Replay base: a6c008dce9e4c846f8c34456ddd6297636d31979
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-26-csr-round12-supply-chain-implementation-plan/Pins.Consistency.dfy

// ---------------------------------------------------------------------------------------------------------
// Library boundary
// ---------------------------------------------------------------------------------------------------------

newtype CodePoint = x: int | 0 <= x < 0x110000
type Str = seq<CodePoint>

datatype Option<T> = None | Some(value: T)

datatype Location = Location(file: Str, regex: Str)
datatype Row = Row(name: Str, source: Str, locations: seq<Location>)

// open(os.path.join(root, file), encoding="utf-8").read(); None for OSError.
function ReadFile(root: Str, file: Str): Option<Str>

// re.findall(regex, text, re.M) with exactly one group.
function FindAll(regex: Str, text: Str): seq<Str>

// e.strerror of the OSError: the approved ReadFile returns None and carries no strerror, so it is an
// arbitrary fixed string here (only whether an error is appended matters to the properties).
const Strerror: Str

// The code's string literals, as code points.
const LitV: CodePoint := 118                                                         // "v"
const LitCannotRead: Str := [58, 32, 99, 97, 110, 110, 111, 116, 32, 114, 101, 97, 100, 32] // ": cannot read "
const LitColonSpace: Str := [58, 32]                                                 // ": "
const LitHasNoMatchFor: Str := [32, 104, 97, 115, 32, 110, 111, 32, 109, 97, 116, 99, 104, 32, 102, 111, 114, 32] // " has no match for "
const LitDisagree: Str := [58, 32, 116, 104, 101, 32, 108, 111, 99, 97, 116, 105, 111, 110, 115, 32, 100, 105, 115, 97, 103, 114, 101, 101, 58, 32] // ": the locations disagree: "
const LitEquals: Str := [61]                                                         // "="
const LitCommaSpace: Str := [44, 32]                                                 // ", "

// ---------------------------------------------------------------------------------------------------------
// Transliteration of ci/pins.py:89-121
// ---------------------------------------------------------------------------------------------------------

// pins.py:89-90  norm
function Norm(version: Str): Str
{
  if |version| > 0 && version[0] == LitV then version[1..] else version
}

// pins.py:93-105  found: a list of (file, version), or the PinError it raises.
datatype FoundResult = Values(out: seq<(Str, Str)>) | PinError(msg: Str)

function Found(row: Row, root: Str): FoundResult
{
  FoundFrom(row, root, 0, [])                                          // out = []
}

function Pairs(file: Str, values: seq<Str>): seq<(Str, Str)>
{
  seq(|values|, k requires 0 <= k < |values| => (file, values[k]))     // [(loc["file"], v) for v in values]
}

function FoundFrom(row: Row, root: Str, i: nat, out: seq<(Str, Str)>): FoundResult
  decreases |row.locations| - i
{
  if i >= |row.locations| then Values(out)                             // return out
  else
    var loc := row.locations[i];                                       // for loc in row["locations"]:
    match ReadFile(root, loc.file)                                     //   try: text = open(...).read()
    case None =>                                                       //   except OSError as e: raise PinError(...)
      PinError(row.name + LitCannotRead + loc.file + LitColonSpace + Strerror)
    case Some(text) =>
      var values := FindAll(loc.regex, text);                          //   values = re.findall(...)
      if values == [] then                                             //   if not values: raise PinError(...)
        PinError(row.name + LitColonSpace + loc.file + LitHasNoMatchFor + loc.regex)
      else
        FoundFrom(row, root, i + 1, out + Pairs(loc.file, values))     //   out += [...]
}

// ", ".join(f"{f}={v}" for f, v in values)
function Listing(values: seq<(Str, Str)>): Str
{
  if |values| == 0 then []
  else if |values| == 1 then values[0].0 + LitEquals + values[0].1
  else values[0].0 + LitEquals + values[0].1 + LitCommaSpace + Listing(values[1..])
}

// One iteration of consistency's loop, pins.py:111-120. An element of the result is either an appended
// error string or the IndexError that `values[0]` raises (it propagates out of consistency).
datatype Err = Message(text: Str) | IndexErrorRaised

function NormSet(values: seq<(Str, Str)>): set<Str>
{
  set p | p in values :: Norm(p.1)                                     // {norm(v) for _, v in values}
}

function RowErrors(row: Row, root: Str): seq<Err>
{
  match Found(row, root)                                               // try: values = found(row, root)
  case PinError(msg) => [Message(msg)]                                 // except PinError: errors.append(str(e)); continue
  case Values(values) =>
    if |NormSet(values)| > 1 then                                      // if len({...}) > 1:
      [Message(row.name + LitDisagree + Listing(values))]              //   errors.append(...)
    else if |values| == 0 then [IndexErrorRaised]                      // else: print(... values[0][1]) -> IndexError
    else []                                                            //   (print not modelled)
}

// pins.py:108-121  consistency. The result is the returned list, or [IndexErrorRaised] when it raises.
function Consistency(rows: seq<Row>, root: Str): seq<Err>
{
  ConsistencyFrom(rows, root, 0, [])                                   // errors = []
}

function ConsistencyFrom(rows: seq<Row>, root: Str, i: nat, errors: seq<Err>): seq<Err>
  decreases |rows| - i
{
  if i >= |rows| then errors                                           // return errors
  else
    var e := RowErrors(rows[i], root);                                 // for row in rows: ...
    if IndexErrorRaised in e then [IndexErrorRaised]                   // the IndexError leaves consistency
    else ConsistencyFrom(rows, root, i + 1, errors + e)
}

// ---------------------------------------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------------------------------------

// Citation: design PinChecks.P1_ConsistencyIffLocationsEqual: "every row's locations hold the same version. Fails on a mismatch." (spec line 156); plan tests `test_consistency_fails_on_a_second_match_in_one_file_that_differs`, `test_consistency_fails_when_a_location_matches_nothing`.
lemma P1_RowPassesIffEveryLocationMatchesAndAllAgree(row: Row, root: Str)
  ensures RowErrors(row, root) == [] <==>
    (forall l :: l in row.locations ==> ReadFile(root, l.file).Some? && FindAll(l.regex, ReadFile(root, l.file).value) != [])
    && (forall l1, l2, a, b :: l1 in row.locations && l2 in row.locations && ReadFile(root, l1.file).Some? && ReadFile(root, l2.file).Some? && a in FindAll(l1.regex, ReadFile(root, l1.file).value) && b in FindAll(l2.regex, ReadFile(root, l2.file).value) ==> Norm(a) == Norm(b))
{
  if |row.locations| > 0 {
    P1_HoldsForRowsWithALocation(row, root);
  }
  // With no locations the right-hand side holds vacuously, but RowErrors is [IndexErrorRaised]
  // (EmptyRowRaises): the property fails on the real code there.
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
  ConsistencyFromSpec(rows, root, 0, []);
  assert forall r :: r in rows <==> exists j :: 0 <= j < |rows| && rows[j] == r;
}

// ---------------------------------------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------------------------------------

predicate LocOk(root: Str, l: Location)
{
  ReadFile(root, l.file).Some? && FindAll(l.regex, ReadFile(root, l.file).value) != []
}

function Matches(root: Str, l: Location): set<Str>
{
  if ReadFile(root, l.file).Some? then (set a | a in FindAll(l.regex, ReadFile(root, l.file).value)) else {}
}

function MatchSet(root: Str, locs: seq<Location>, i: nat): set<Str>
  decreases |locs| - i
{
  if i >= |locs| then {} else Matches(root, locs[i]) + MatchSet(root, locs, i + 1)
}

function Seconds(vs: seq<(Str, Str)>): set<Str>
{
  set p | p in vs :: p.1
}

lemma SecondsAppend(out: seq<(Str, Str)>, file: Str, values: seq<Str>)
  ensures Seconds(out + Pairs(file, values)) == Seconds(out) + (set a | a in values)
{
  var ps := Pairs(file, values);
  forall a | a in Seconds(out + ps)
    ensures a in Seconds(out) + (set a | a in values)
  {
    var p :| p in out + ps && p.1 == a;
    if p !in out {
      assert p in ps;
      var k :| 0 <= k < |ps| && ps[k] == p;
      assert values[k] == a;
    }
  }
  forall a | a in Seconds(out) + (set a | a in values)
    ensures a in Seconds(out + ps)
  {
    if a in Seconds(out) {
      var p :| p in out && p.1 == a;
      assert p in out + ps;
    } else {
      var k :| 0 <= k < |values| && values[k] == a;
      assert ps[k] == (file, a);
      assert ps[k] in out + ps;
    }
  }
}

lemma FoundFromSpec(row: Row, root: Str, i: nat, out: seq<(Str, Str)>)
  requires i <= |row.locations|
  ensures var r := FoundFrom(row, root, i, out);
    (r.Values? <==> forall j :: i <= j < |row.locations| ==> LocOk(root, row.locations[j]))
    && (r.Values? ==> Seconds(r.out) == Seconds(out) + MatchSet(root, row.locations, i))
    && (r.Values? ==> (|r.out| == 0 <==> |out| == 0 && i == |row.locations|))
  decreases |row.locations| - i
{
  if i < |row.locations| {
    var loc := row.locations[i];
    if LocOk(root, loc) {
      var values := FindAll(loc.regex, ReadFile(root, loc.file).value);
      SecondsAppend(out, loc.file, values);
      FoundFromSpec(row, root, i + 1, out + Pairs(loc.file, values));
      assert Matches(root, loc) == (set a | a in values);
      var r := FoundFrom(row, root, i, out);
      if r.Values? {
        assert |out + Pairs(loc.file, values)| > 0;
      }
    }
  }
}

lemma MatchSetMembership(root: Str, locs: seq<Location>, i: nat, a: Str)
  requires i <= |locs|
  ensures a in MatchSet(root, locs, i) <==> exists j :: i <= j < |locs| && a in Matches(root, locs[j])
  decreases |locs| - i
{
  if i < |locs| {
    MatchSetMembership(root, locs, i + 1, a);
  }
}

lemma NormSetIsNormOfSeconds(vs: seq<(Str, Str)>)
  ensures forall x :: x in NormSet(vs) <==> exists a :: a in Seconds(vs) && Norm(a) == x
{
  forall x | x in NormSet(vs)
    ensures exists a :: a in Seconds(vs) && Norm(a) == x
  {
    var p :| p in vs && Norm(p.1) == x;
    assert p.1 in Seconds(vs);
  }
  forall x | exists a :: a in Seconds(vs) && Norm(a) == x
    ensures x in NormSet(vs)
  {
    var a :| a in Seconds(vs) && Norm(a) == x;
    var p :| p in vs && p.1 == a;
  }
}

lemma CardAtMostOne<T>(s: set<T>)
  ensures |s| <= 1 <==> forall x, y :: x in s && y in s ==> x == y
{
  if |s| <= 1 {
    forall x, y | x in s && y in s
      ensures x == y
    {
      if x != y {
        assert {x, y} <= s;
        assert |{x, y}| == 2;
        SubsetCard({x, y}, s);
      }
    }
  } else {
    var x :| x in s;
    var rest := s - {x};
    assert |rest| >= 1;
    var y :| y in rest;
    assert x in s && y in s && x != y;
  }
}

lemma SubsetCard<T>(a: set<T>, b: set<T>)
  requires a <= b
  ensures |a| <= |b|
{
  assert b == a + (b - a);
}

lemma P1_HoldsForRowsWithALocation(row: Row, root: Str)
  requires |row.locations| > 0
  ensures RowErrors(row, root) == [] <==>
    (forall l :: l in row.locations ==> ReadFile(root, l.file).Some? && FindAll(l.regex, ReadFile(root, l.file).value) != [])
    && (forall l1, l2, a, b :: l1 in row.locations && l2 in row.locations && ReadFile(root, l1.file).Some? && ReadFile(root, l2.file).Some? && a in FindAll(l1.regex, ReadFile(root, l1.file).value) && b in FindAll(l2.regex, ReadFile(root, l2.file).value) ==> Norm(a) == Norm(b))
{
  var locs := row.locations;
  FoundFromSpec(row, root, 0, []);
  var r := Found(row, root);
  var allOk := forall l :: l in locs ==> ReadFile(root, l.file).Some? && FindAll(l.regex, ReadFile(root, l.file).value) != [];
  var agree := forall l1, l2, a, b :: l1 in locs && l2 in locs && ReadFile(root, l1.file).Some? && ReadFile(root, l2.file).Some? && a in FindAll(l1.regex, ReadFile(root, l1.file).value) && b in FindAll(l2.regex, ReadFile(root, l2.file).value) ==> Norm(a) == Norm(b);
  assert allOk <==> forall j :: 0 <= j < |locs| ==> LocOk(root, locs[j]) by {
    if allOk {
      forall j | 0 <= j < |locs| ensures LocOk(root, locs[j]) { assert locs[j] in locs; }
    }
    if forall j :: 0 <= j < |locs| ==> LocOk(root, locs[j]) {
      forall l | l in locs ensures LocOk(root, l) {
        var j :| 0 <= j < |locs| && locs[j] == l;
      }
    }
  }
  if r.PinError? {
    assert !allOk;
  } else {
    var vs := r.out;
    assert allOk;
    assert |vs| > 0;
    assert Seconds(vs) == MatchSet(root, locs, 0);
    // a in Seconds(vs) <==> a matches some location
    forall a
      ensures a in Seconds(vs) <==> exists l :: l in locs && a in Matches(root, l)
    {
      MatchSetMembership(root, locs, 0, a);
      if a in Seconds(vs) {
        var j :| 0 <= j < |locs| && a in Matches(root, locs[j]);
        assert locs[j] in locs;
      }
      if exists l :: l in locs && a in Matches(root, l) {
        var l :| l in locs && a in Matches(root, l);
        var j :| 0 <= j < |locs| && locs[j] == l;
      }
    }
    NormSetIsNormOfSeconds(vs);
    CardAtMostOne(NormSet(vs));
    var ns := NormSet(vs);
    assert agree <==> forall x, y :: x in ns && y in ns ==> x == y by {
      if agree {
        forall x, y | x in ns && y in ns ensures x == y {
          var a :| a in Seconds(vs) && Norm(a) == x;
          var b :| b in Seconds(vs) && Norm(b) == y;
          var l1 :| l1 in locs && a in Matches(root, l1);
          var l2 :| l2 in locs && b in Matches(root, l2);
        }
      }
      if forall x, y :: x in ns && y in ns ==> x == y {
        forall l1, l2, a, b | l1 in locs && l2 in locs && ReadFile(root, l1.file).Some? && ReadFile(root, l2.file).Some? && a in FindAll(l1.regex, ReadFile(root, l1.file).value) && b in FindAll(l2.regex, ReadFile(root, l2.file).value)
          ensures Norm(a) == Norm(b)
        {
          assert a in Matches(root, l1);
          assert b in Matches(root, l2);
          assert a in Seconds(vs);
          assert b in Seconds(vs);
          assert Norm(a) in ns;
          assert Norm(b) in ns;
        }
      }
    }
  }
}

// Helper: the failure of P1 is confined to rows with no locations, where the real code raises IndexError.
lemma EmptyRowRaises(row: Row, root: Str)
  requires row.locations == []
  ensures RowErrors(row, root) == [IndexErrorRaised]
{
  assert NormSet([]) == {};
}

lemma ConsistencyFromSpec(rows: seq<Row>, root: Str, i: nat, errors: seq<Err>)
  requires i <= |rows|
  ensures ConsistencyFrom(rows, root, i, errors) == [] <==>
    errors == [] && forall j :: i <= j < |rows| ==> RowErrors(rows[j], root) == []
  decreases |rows| - i
{
  if i < |rows| {
    var e := RowErrors(rows[i], root);
    if IndexErrorRaised !in e {
      ConsistencyFromSpec(rows, root, i + 1, errors + e);
    }
  }
}
