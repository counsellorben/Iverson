// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T2 S3
// Source: Iverson.Server/Iverson.Api/Grpc/PayloadKeyCanonicalizer.cs `Canonicalize`
// Replay base: c79d7de067519edb3b325fbc0aa53332944c6d1c
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/PayloadKeyCanonicalizer.Canonicalize.dfy

// ---------------------------------------------------------------------------
// Approved axioms
// ---------------------------------------------------------------------------

function OrdinalUpper(s: seq<char>): seq<char>

type Value(==)

// ---------------------------------------------------------------------------
// Shared vocabulary
// ---------------------------------------------------------------------------

type CsString = s: seq<char> | |s| < 0x8000_0000

datatype Option<T> = None | Some(value: T)

predicate EqIC(a: seq<char>, b: seq<char>) { OrdinalUpper(a) == OrdinalUpper(b) }

predicate IsTenantColumn(n: seq<char>) { EqIC(n, "__TenantId") }

datatype StatusCode = OK | Cancelled | Unknown | InvalidArgument | DeadlineExceeded | NotFound | AlreadyExists | PermissionDenied | ResourceExhausted | FailedPrecondition | Aborted | OutOfRange | Unimplemented | Internal | Unavailable | DataLoss | Unauthenticated

datatype Column = Column(Name: CsString, SqlType: CsString, IsNullable: bool)

// Only the fields Canonicalize reads.
datatype Desc = Desc(TypeName: CsString, KeyColumn: Column, ScalarColumns: seq<Column>)

// schema.ScalarColumns.Select(c => c.Name).Prepend(schema.KeyColumn.Name)
function ColumnNames(d: Desc): seq<CsString> {
  [d.KeyColumn.Name] + seq(|d.ScalarColumns|, i requires 0 <= i < |d.ScalarColumns| => d.ScalarColumns[i].Name)
}

// ---------------------------------------------------------------------------
// Payload (protobuf Struct.Fields, a MapField: insertion order, distinct keys)
// ---------------------------------------------------------------------------

type Payload = seq<(CsString, Value)>

predicate DistinctKeys(p: Payload) { forall i, j :: 0 <= i < j < |p| ==> p[i].0 != p[j].0 }

predicate Has(q: Payload, k: seq<char>, v: Value) { exists j :: 0 <= j < |q| && q[j].0 == k && q[j].1 == v }

predicate Present(q: Payload, k: seq<char>) { exists j :: 0 <= j < |q| && q[j].0 == k }

datatype CanonResult = Canonical(p: Payload) | Refused(code: StatusCode, detail: seq<char>) | Faulted

function Matches(d: Desc, k: seq<char>): set<CsString> {
  set c | c in ColumnNames(d) && !IsTenantColumn(c) && EqIC(c, k)
}

// MapField lookup by ordinal key.
function PIndex(q: Payload, k: seq<char>): (r: Option<nat>)
  ensures r.None? ==> !Present(q, k)
  ensures r.Some? ==> r.value < |q| && q[r.value].0 == k
{
  if q == [] then None
  else if q[0].0 == k then Some(0)
  else match PIndex(q[1..], k)
    case None =>
      assert forall j :: 1 <= j < |q| ==> q[j] == q[1..][j - 1];
      None
    case Some(i) => Some(i + 1)
}

// MapField.Remove(k): drops the entry, keeping the order of the rest.
function PRemove(q: Payload, k: seq<char>): Payload {
  if q == [] then []
  else if q[0].0 == k then PRemove(q[1..], k)
  else [q[0]] + PRemove(q[1..], k)
}

// MapField indexer set: replaces a present key in place, appends an absent one.
function PSet(q: Payload, k: CsString, v: Value): Payload {
  match PIndex(q, k)
  case Some(i) => q[i := (k, v)]
  case None => q + [(k, v)]
}

// ---------------------------------------------------------------------------
// Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
// An association list; each entry's key is the spelling that first created it.
// ---------------------------------------------------------------------------

type ColumnDict = seq<(CsString, seq<CsString>)>

function IcTryGet(m: ColumnDict, k: seq<char>): (r: Option<nat>)
  ensures r.None? ==> forall i :: 0 <= i < |m| ==> !EqIC(m[i].0, k)
  ensures r.Some? ==> r.value < |m| && EqIC(m[r.value].0, k)
{
  if m == [] then None
  else if EqIC(m[0].0, k) then Some(0)
  else match IcTryGet(m[1..], k)
    case None =>
      assert forall i :: 1 <= i < |m| ==> m[i] == m[1..][i - 1];
      None
    case Some(i) => Some(i + 1)
}

// ---------------------------------------------------------------------------
// Transliteration
// ---------------------------------------------------------------------------

// Lines 32-40: build `columns`.
function BuildColumns(names: seq<CsString>, columns: ColumnDict): ColumnDict
  decreases |names|
{
  if names == [] then columns
  else
    var name := names[0];
    if IsTenantColumn(name) then BuildColumns(names[1..], columns)              // continue
    else
      match IcTryGet(columns, name)
      case None =>                                                              // columns[name] = spellings = []
        var columns1 := columns + [(name, [])];
        var spellings := columns1[|columns|].1;
        var spellings1 := if !(name in spellings) then spellings + [name] else spellings;
        BuildColumns(names[1..], columns1[|columns| := (columns1[|columns|].0, spellings1)])
      case Some(idx) =>
        var spellings := columns[idx].1;
        var spellings1 := if !(name in spellings) then spellings + [name] else spellings;
        BuildColumns(names[1..], columns[idx := (columns[idx].0, spellings1)])
}

function QuoteJoin(ss: seq<CsString>): seq<char> {
  if ss == [] then []
  else if |ss| == 1 then "'" + ss[0] + "'"
  else "'" + ss[0] + "', " + QuoteJoin(ss[1..])
}

datatype ScanResult = ScanOk(renames: seq<(CsString, CsString)>) | ScanRefused(code: StatusCode, detail: seq<char>) | ScanFaulted

// Lines 42-67: the loop over payload.Fields.Keys, from index n.
function Scan(d: Desc, columns: ColumnDict, p: Payload, n: nat, keyFor: map<CsString, CsString>, renames: seq<(CsString, CsString)>): ScanResult
  requires n <= |p|
  decreases |p| - n
{
  if n == |p| then ScanOk(renames)
  else
    var key := p[n].0;
    match IcTryGet(columns, key)
    case None => Scan(d, columns, p, n + 1, keyFor, renames)                    // continue
    case Some(ci) =>
      var spellings := columns[ci].1;
      if |spellings| > 1 then
        ScanRefused(InvalidArgument,
          "Payload field '" + key + "' on '" + d.TypeName + "' matches more than one column " +
          "(" + QuoteJoin(spellings) + "), whose names differ only by " +
          "letter case. Re-register the type with property names that are distinct ignoring case.")
      else if |spellings| == 0 then ScanFaulted                                 // spellings[0]: ArgumentOutOfRangeException
      else
        var column := spellings[0];
        if column in keyFor then
          ScanRefused(InvalidArgument,
            "Payload fields '" + keyFor[column] + "' and '" + key + "' on '" + d.TypeName + "' both name column '" + column + "'; " +
            "send it once.")
        else
          var keyFor1 := keyFor[column := key];
          var renames1 := if key != column then renames + [(key, column)] else renames;
          Scan(d, columns, p, n + 1, keyFor1, renames1)
}

// Lines 69-74: apply the renames.
function ApplyRenames(q: Payload, renames: seq<(CsString, CsString)>): Option<Payload>
  decreases |renames|
{
  if renames == [] then Some(q)
  else
    var from := renames[0].0;
    var to := renames[0].1;
    match PIndex(q, from)
    case None => None                                                           // payload.Fields[from]: KeyNotFoundException
    case Some(i) =>
      var value := q[i].1;
      ApplyRenames(PSet(PRemove(q, from), to, value), renames[1..])
}

function Canonicalize(d: Desc, p: Payload): CanonResult {
  var columns := BuildColumns(ColumnNames(d), []);
  match Scan(d, columns, p, 0, map[], [])
  case ScanRefused(code, detail) => Refused(code, detail)
  case ScanFaulted => Faulted
  case ScanOk(renames) =>
    match ApplyRenames(p, renames)
    case None => Faulted
    case Some(q) => Canonical(q)
}

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

// Citation: "It renames each payload key that matches exactly one column to that column's spelling." (spec §2.1)
lemma P1_AKeyNamingOneColumnTakesItsSpelling(d: Desc, p: Payload, i: int, c: CsString)
  requires DistinctKeys(p) && 0 <= i < |p|
  requires Canonicalize(d, p).Canonical?
  requires Matches(d, p[i].0) == {c}
  ensures Has(Canonicalize(d, p).p, c, p[i].1)
  ensures |Canonicalize(d, p).p| == |p| && DistinctKeys(Canonicalize(d, p).p)
{
  CanonicalImpliesGood(d, p);
  var rs := ApplyPrep(d, p);
  var r := ApplyRenames(p, rs).value;
  assert Canonicalize(d, p).p == r;
  assert Has(p, p[i].0, p[i].1);
  if p[i].0 == c {
    forall t | 0 <= t < |rs| ensures rs[t].0 != p[i].0 {
      assert FromOf(d, p, 0, rs[t]);
      var m :| 0 <= m < |p| && rs[t].0 == p[m].0 && Matches(d, p[m].0) == {rs[t].1} && p[m].0 != rs[t].1;
      assert Matches(d, rs[t].0) == {rs[t].1};
      assert c in Matches(d, p[i].0);
    }
  } else {
    assert (p[i].0, c) in rs;
    var t :| 0 <= t < |rs| && rs[t] == (p[i].0, c);
    assert Has(p, rs[t].0, p[i].1);
  }
}

// Citation: "It refuses with `InvalidArgument` two payload keys that map to the same column, and a key that matches more than one column (a stored type with case-duplicate columns)." (spec §2.1)
lemma P2_TwoKeysNamingOneColumnAreRefused(d: Desc, p: Payload, i: int, j: int)
  requires DistinctKeys(p) && 0 <= i < j < |p|
  requires |Matches(d, p[i].0)| == 1 && Matches(d, p[j].0) == Matches(d, p[i].0)
  ensures Canonicalize(d, p).Refused? && Canonicalize(d, p).code == InvalidArgument
{
  ScanRefusesPair(d, p, 0, map[], [], i, j);
  ScanNoFault(d, p, 0, map[], []);
}

// Citation: "It refuses with `InvalidArgument` two payload keys that map to the same column, and a key that matches more than one column (a stored type with case-duplicate columns)." (spec §2.1)
lemma P3_AKeyMatchingSeveralColumnsIsRefused(d: Desc, p: Payload, i: int)
  requires DistinctKeys(p) && 0 <= i < |p|
  requires |Matches(d, p[i].0)| > 1
  ensures Canonicalize(d, p).Refused? && Canonicalize(d, p).code == InvalidArgument
{
  ScanRefusesMulti(d, p, 0, map[], [], i);
  ScanNoFault(d, p, 0, map[], []);
}

// Citation: "Keys that match no column are left alone (unchanged behaviour)." (spec §2.1) and "The reserved tenant column `__TenantId` is excluded: its smuggling check (`AuthorizationFieldMasking.cs:91-98`) keeps running on the raw key and keeps its message." (spec §2.1)
lemma P4_KeysNamingNoColumnAndTheTenantKeyAreLeftAlone(d: Desc, p: Payload, i: int)
  requires DistinctKeys(p) && 0 <= i < |p|
  requires Canonicalize(d, p).Canonical?
  requires Matches(d, p[i].0) == {} || IsTenantColumn(p[i].0)
  ensures Has(Canonicalize(d, p).p, p[i].0, p[i].1)
{
  TenantMatchesNothing(d, p[i].0);
  CanonicalImpliesGood(d, p);
  var rs := ApplyPrep(d, p);
  var r := ApplyRenames(p, rs).value;
  assert Canonicalize(d, p).p == r;
  assert Has(p, p[i].0, p[i].1);
  forall t | 0 <= t < |rs| ensures rs[t].0 != p[i].0 {
    assert FromOf(d, p, 0, rs[t]);
    var m :| 0 <= m < |p| && rs[t].0 == p[m].0 && Matches(d, p[m].0) == {rs[t].1} && p[m].0 != rs[t].1;
    assert rs[t].1 in Matches(d, rs[t].0);
    assert Matches(d, p[i].0) == {};
  }
}

// Citation: "It refuses with `InvalidArgument` two payload keys that map to the same column, and a key that matches more than one column (a stored type with case-duplicate columns)." (spec §2.1) and "Keys that match no column are left alone (unchanged behaviour)." (spec §2.1)
lemma P5_NothingElseIsRefused(d: Desc, p: Payload)
  requires DistinctKeys(p)
  requires forall i :: 0 <= i < |p| ==> |Matches(d, p[i].0)| <= 1
  requires forall i, j :: 0 <= i < j < |p| && |Matches(d, p[i].0)| == 1 ==> Matches(d, p[j].0) != Matches(d, p[i].0)
  ensures Canonicalize(d, p).Canonical?
{
  var rs := ApplyPrep(d, p);
}

// ---------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------

ghost predicate Good(d: Desc, p: Payload) {
  (forall i :: 0 <= i < |p| ==> |Matches(d, p[i].0)| <= 1) &&
  (forall i, j :: 0 <= i < j < |p| && |Matches(d, p[i].0)| == 1 ==> Matches(d, p[j].0) != Matches(d, p[i].0))
}

lemma TenantMatchesNothing(d: Desc, k: seq<char>)
  ensures IsTenantColumn(k) ==> Matches(d, k) == {}
{
  if IsTenantColumn(k) {
    forall c: CsString ensures c !in Matches(d, k) {
      assert EqIC(c, k) ==> IsTenantColumn(c);
    }
  }
}

lemma CanonicalImpliesGood(d: Desc, p: Payload)
  requires DistinctKeys(p)
  requires Canonicalize(d, p).Canonical?
  ensures Good(d, p)
{
  forall i | 0 <= i < |p| ensures |Matches(d, p[i].0)| <= 1 {
    if |Matches(d, p[i].0)| > 1 {
      P3_AKeyMatchingSeveralColumnsIsRefused(d, p, i);
    }
  }
  forall i, j | 0 <= i < j < |p| && |Matches(d, p[i].0)| == 1 ensures Matches(d, p[j].0) != Matches(d, p[i].0) {
    if Matches(d, p[j].0) == Matches(d, p[i].0) {
      P2_TwoKeysNamingOneColumnAreRefused(d, p, i, j);
    }
  }
}

// --- columns ---------------------------------------------------------------

function Cols(d: Desc): ColumnDict { BuildColumns(ColumnNames(d), []) }

predicate SeqDistinct<T(==)>(s: seq<T>) { forall i, j :: 0 <= i < j < |s| ==> s[i] != s[j] }

ghost predicate KeysApart(columns: ColumnDict) {
  forall i, j :: 0 <= i < j < |columns| ==> !EqIC(columns[i].0, columns[j].0)
}

ghost predicate SpellingsSound(columns: ColumnDict, done: seq<CsString>) {
  forall i: int, s: CsString :: 0 <= i < |columns| && s in columns[i].1 ==> EqIC(s, columns[i].0) && s in done && !IsTenantColumn(s)
}

ghost predicate SpellingsDistinct(columns: ColumnDict) {
  forall i :: 0 <= i < |columns| ==> SeqDistinct(columns[i].1) && |columns[i].1| > 0
}

ghost predicate NamesCovered(columns: ColumnDict, done: seq<CsString>) {
  forall n: CsString :: n in done && !IsTenantColumn(n) ==> exists i :: 0 <= i < |columns| && n in columns[i].1
}

ghost predicate ColInv(columns: ColumnDict, done: seq<CsString>) {
  KeysApart(columns) && SpellingsSound(columns, done) && SpellingsDistinct(columns) && NamesCovered(columns, done)
}

lemma SoundAt(columns: ColumnDict, done: seq<CsString>, i: int, s: CsString)
  requires SpellingsSound(columns, done) && 0 <= i < |columns| && s in columns[i].1
  ensures EqIC(s, columns[i].0) && s in done && !IsTenantColumn(s)
{
}

lemma CoveredAt(columns: ColumnDict, done: seq<CsString>, n: CsString) returns (i: int)
  requires NamesCovered(columns, done) && n in done && !IsTenantColumn(n)
  ensures 0 <= i < |columns| && n in columns[i].1
{
  i :| 0 <= i < |columns| && n in columns[i].1;
}

lemma BuildColumnsInv(names: seq<CsString>, columns: ColumnDict, done: seq<CsString>)
  requires ColInv(columns, done)
  ensures ColInv(BuildColumns(names, columns), done + names)
  decreases |names|
{
  if names == [] {
    assert done + names == done;
  } else {
    var name := names[0];
    var done1 := done + [name];
    assert done + names == done1 + names[1..];
    if IsTenantColumn(name) {
      forall i: int, s: CsString | 0 <= i < |columns| && s in columns[i].1 ensures EqIC(s, columns[i].0) && s in done1 && !IsTenantColumn(s) {
        SoundAt(columns, done, i, s);
      }
      forall n: CsString | n in done1 && !IsTenantColumn(n) ensures exists i :: 0 <= i < |columns| && n in columns[i].1 {
        assert n in done;
        var i := CoveredAt(columns, done, n);
      }
      assert ColInv(columns, done1);
      BuildColumnsInv(names[1..], columns, done1);
    } else {
      match IcTryGet(columns, name)
      case None =>
        var c1 := columns + [(name, [name])];
        var c0 := columns + [(name, [])];
        var e: seq<CsString> := [];
        assert c0[|columns|] == (name, e);
        assert e + [name] == [name];
        assert c0[|columns| := (name, e + [name])] == c1;
        assert BuildColumns(names, columns) == BuildColumns(names[1..], c1);
        forall i, j | 0 <= i < j < |c1| ensures !EqIC(c1[i].0, c1[j].0) {
          assert c1[i] == columns[i];
          if j < |columns| { assert c1[j] == columns[j]; }
        }
        forall i: int, s: CsString | 0 <= i < |c1| && s in c1[i].1 ensures EqIC(s, c1[i].0) && s in done1 && !IsTenantColumn(s) {
          if i < |columns| {
            assert c1[i] == columns[i];
            SoundAt(columns, done, i, s);
          } else {
            assert s == name;
          }
        }
        forall i | 0 <= i < |c1| ensures SeqDistinct(c1[i].1) && |c1[i].1| > 0 {
          if i < |columns| { assert c1[i] == columns[i]; }
        }
        forall n: CsString | n in done1 && !IsTenantColumn(n) ensures exists i :: 0 <= i < |c1| && n in c1[i].1 {
          if n == name {
            assert n in c1[|columns|].1;
          } else {
            assert n in done;
            var i := CoveredAt(columns, done, n);
            assert c1[i] == columns[i];
          }
        }
        assert ColInv(c1, done1);
        BuildColumnsInv(names[1..], c1, done1);
      case Some(idx) =>
        var spellings := columns[idx].1;
        var spellings1 := if !(name in spellings) then spellings + [name] else spellings;
        var c1 := columns[idx := (columns[idx].0, spellings1)];
        assert BuildColumns(names, columns) == BuildColumns(names[1..], c1);
        assert forall i :: 0 <= i < |c1| ==> c1[i].0 == columns[i].0;
        assert forall i :: 0 <= i < |c1| && i != idx ==> c1[i] == columns[i];
        forall i, j | 0 <= i < j < |c1| ensures !EqIC(c1[i].0, c1[j].0) {
        }
        forall i: int, s: CsString | 0 <= i < |c1| && s in c1[i].1 ensures EqIC(s, c1[i].0) && s in done1 && !IsTenantColumn(s) {
          if i != idx {
            SoundAt(columns, done, i, s);
          } else if s != name {
            assert s in spellings;
            SoundAt(columns, done, i, s);
          }
        }
        forall i | 0 <= i < |c1| ensures SeqDistinct(c1[i].1) && |c1[i].1| > 0 {
          if i == idx && !(name in spellings) {
            assert SeqDistinct(spellings);
            forall a, b | 0 <= a < b < |spellings1| ensures spellings1[a] != spellings1[b] {
              if b == |spellings| { assert spellings1[a] == spellings[a]; }
              else { assert spellings1[a] == spellings[a] && spellings1[b] == spellings[b]; }
            }
          }
        }
        forall n: CsString | n in done1 && !IsTenantColumn(n) ensures exists i :: 0 <= i < |c1| && n in c1[i].1 {
          if n == name {
            assert n in c1[idx].1;
          } else {
            assert n in done;
            var i := CoveredAt(columns, done, n);
            assert n in c1[i].1;
          }
        }
        assert ColInv(c1, done1);
        BuildColumnsInv(names[1..], c1, done1);
    }
  }
}

lemma DistinctCard<T>(s: seq<T>)
  requires SeqDistinct(s)
  ensures |set x | x in s| == |s|
{
  if s != [] {
    DistinctCard(s[1..]);
    assert (set x | x in s) == {s[0]} + (set x | x in s[1..]);
    assert s[0] !in s[1..];
  }
}

lemma KeysApartEq(cols: ColumnDict, i: int, j: int)
  requires KeysApart(cols) && 0 <= i < |cols| && 0 <= j < |cols| && EqIC(cols[i].0, cols[j].0)
  ensures i == j
{
}

lemma ElemsOfSingleton<T>(s: seq<T>)
  requires |s| == 1
  ensures (set x | x in s) == {s[0]}
{
}

lemma ColsFacts(d: Desc, k: seq<char>)
  ensures IcTryGet(Cols(d), k).None? <==> Matches(d, k) == {}
  ensures IcTryGet(Cols(d), k).Some? ==>
    var s := Cols(d)[IcTryGet(Cols(d), k).value].1;
    (set x | x in s) == Matches(d, k) && |s| == |Matches(d, k)| &&
    (|s| == 1 ==> Matches(d, k) == {s[0]})
{
  var cols := Cols(d);
  var names := ColumnNames(d);
  BuildColumnsInv(names, [], []);
  assert [] + names == names;
  assert ColInv(cols, names);
  match IcTryGet(cols, k)
  case None =>
    forall c | c in Matches(d, k) ensures false {
      var i := CoveredAt(cols, names, c);
      SoundAt(cols, names, i, c);
    }
  case Some(ci) =>
    var s := cols[ci].1;
    assert SpellingsDistinct(cols);
    assert SeqDistinct(s) && |s| > 0;
    var M := Matches(d, k);
    forall c | c in M ensures c in s {
      var i := CoveredAt(cols, names, c);
      SoundAt(cols, names, i, c);
      assert EqIC(cols[i].0, cols[ci].0);
      KeysApartEq(cols, i, ci);
    }
    forall c | c in s ensures c in M {
      SoundAt(cols, names, ci, c);
    }
    assert (set x | x in s) == M;
    assert s[0] in M;
    DistinctCard(s);
    if |s| == 1 {
      ElemsOfSingleton(s);
    }
}

// --- scan ------------------------------------------------------------------

lemma ScanNoFault(d: Desc, p: Payload, n: nat, keyFor: map<CsString, CsString>, renames: seq<(CsString, CsString)>)
  requires n <= |p|
  ensures !Scan(d, Cols(d), p, n, keyFor, renames).ScanFaulted?
  ensures Scan(d, Cols(d), p, n, keyFor, renames).ScanRefused? ==> Scan(d, Cols(d), p, n, keyFor, renames).code == InvalidArgument
  decreases |p| - n
{
  if n < |p| {
    var key := p[n].0;
    ColsFacts(d, key);
    match IcTryGet(Cols(d), key)
    case None => ScanNoFault(d, p, n + 1, keyFor, renames);
    case Some(ci) =>
      var spellings := Cols(d)[ci].1;
      if |spellings| == 1 {
        var column := spellings[0];
        if column !in keyFor {
          ScanNoFault(d, p, n + 1, keyFor[column := key], if key != column then renames + [(key, column)] else renames);
        }
      }
  }
}

lemma ScanRefusesMulti(d: Desc, p: Payload, n: nat, keyFor: map<CsString, CsString>, renames: seq<(CsString, CsString)>, i: int)
  requires n <= i < |p|
  requires |Matches(d, p[i].0)| > 1
  ensures Scan(d, Cols(d), p, n, keyFor, renames).ScanRefused?
  decreases |p| - n
{
  var key := p[n].0;
  ColsFacts(d, key);
  match IcTryGet(Cols(d), key)
  case None => ScanRefusesMulti(d, p, n + 1, keyFor, renames, i);
  case Some(ci) =>
    var spellings := Cols(d)[ci].1;
    if |spellings| == 1 {
      var column := spellings[0];
      if column !in keyFor {
        ScanRefusesMulti(d, p, n + 1, keyFor[column := key], if key != column then renames + [(key, column)] else renames, i);
      }
    }
}

lemma ScanRefusesIfTaken(d: Desc, p: Payload, n: nat, keyFor: map<CsString, CsString>, renames: seq<(CsString, CsString)>, j: int, c: CsString)
  requires n <= j < |p|
  requires c in keyFor && Matches(d, p[j].0) == {c}
  ensures Scan(d, Cols(d), p, n, keyFor, renames).ScanRefused?
  decreases |p| - n
{
  var key := p[n].0;
  ColsFacts(d, key);
  ColsFacts(d, p[j].0);
  match IcTryGet(Cols(d), key)
  case None => ScanRefusesIfTaken(d, p, n + 1, keyFor, renames, j, c);
  case Some(ci) =>
    var spellings := Cols(d)[ci].1;
    if |spellings| == 1 {
      var column := spellings[0];
      if column !in keyFor {
        ScanRefusesIfTaken(d, p, n + 1, keyFor[column := key], if key != column then renames + [(key, column)] else renames, j, c);
      }
    }
}

lemma ScanRefusesPair(d: Desc, p: Payload, n: nat, keyFor: map<CsString, CsString>, renames: seq<(CsString, CsString)>, i: int, j: int)
  requires n <= i < j < |p|
  requires |Matches(d, p[i].0)| == 1 && Matches(d, p[j].0) == Matches(d, p[i].0)
  ensures Scan(d, Cols(d), p, n, keyFor, renames).ScanRefused?
  decreases |p| - n
{
  var key := p[n].0;
  ColsFacts(d, key);
  match IcTryGet(Cols(d), key)
  case None => ScanRefusesPair(d, p, n + 1, keyFor, renames, i, j);
  case Some(ci) =>
    var spellings := Cols(d)[ci].1;
    if |spellings| == 1 {
      var column := spellings[0];
      if column !in keyFor {
        var keyFor1 := keyFor[column := key];
        var renames1 := if key != column then renames + [(key, column)] else renames;
        if n == i {
          ScanRefusesIfTaken(d, p, n + 1, keyFor1, renames1, j, column);
        } else {
          ScanRefusesPair(d, p, n + 1, keyFor1, renames1, i, j);
        }
      }
    }
}

ghost function Expected(d: Desc, p: Payload, n: nat): seq<(CsString, CsString)>
  requires n <= |p|
  decreases |p| - n
{
  if n == |p| then []
  else match IcTryGet(Cols(d), p[n].0)
    case None => Expected(d, p, n + 1)
    case Some(ci) =>
      var s := Cols(d)[ci].1;
      if |s| == 1 && p[n].0 != s[0] then [(p[n].0, s[0])] + Expected(d, p, n + 1) else Expected(d, p, n + 1)
}

lemma ScanOkIfGood(d: Desc, p: Payload, n: nat, keyFor: map<CsString, CsString>, renames: seq<(CsString, CsString)>)
  requires n <= |p| && Good(d, p)
  requires forall c :: c in keyFor ==> exists m :: 0 <= m < n && Matches(d, p[m].0) == {c}
  ensures Scan(d, Cols(d), p, n, keyFor, renames) == ScanOk(renames + Expected(d, p, n))
  decreases |p| - n
{
  if n == |p| {
    assert renames + [] == renames;
  } else {
    var key := p[n].0;
    ColsFacts(d, key);
    match IcTryGet(Cols(d), key)
    case None => ScanOkIfGood(d, p, n + 1, keyFor, renames);
    case Some(ci) =>
      var spellings := Cols(d)[ci].1;
      assert |spellings| == 1;
      var column := spellings[0];
      forall m | 0 <= m < n ensures Matches(d, p[m].0) != {column} {
        if |Matches(d, p[m].0)| == 1 {
          assert Matches(d, p[n].0) != Matches(d, p[m].0);
        }
      }
      assert column !in keyFor;
      var keyFor1 := keyFor[column := key];
      if key != column {
        ScanOkIfGood(d, p, n + 1, keyFor1, renames + [(key, column)]);
        assert renames + [(key, column)] + Expected(d, p, n + 1) == renames + ([(key, column)] + Expected(d, p, n + 1));
      } else {
        ScanOkIfGood(d, p, n + 1, keyFor1, renames);
      }
  }
}

ghost predicate FromOf(d: Desc, p: Payload, lo: nat, r: (CsString, CsString)) {
  exists m :: lo <= m < |p| && r.0 == p[m].0 && Matches(d, p[m].0) == {r.1} && p[m].0 != r.1
}

lemma ExpectedHead(d: Desc, p: Payload, n: nat)
  requires n < |p|
  ensures var key := p[n].0; var r := IcTryGet(Cols(d), key);
    if r.Some? && |Cols(d)[r.value].1| == 1 && key != Cols(d)[r.value].1[0]
    then Expected(d, p, n) == [(key, Cols(d)[r.value].1[0])] + Expected(d, p, n + 1)
    else Expected(d, p, n) == Expected(d, p, n + 1)
{
}

lemma ExpectedFrom(d: Desc, p: Payload, n: nat)
  requires n <= |p|
  ensures forall t :: 0 <= t < |Expected(d, p, n)| ==> FromOf(d, p, n, Expected(d, p, n)[t])
  decreases |p| - n
{
  if n < |p| {
    ExpectedFrom(d, p, n + 1);
    ExpectedHead(d, p, n);
    var key := p[n].0;
    ColsFacts(d, key);
    var E := Expected(d, p, n);
    var E1 := Expected(d, p, n + 1);
    forall t | 0 <= t < |E| ensures FromOf(d, p, n, E[t]) {
      if |E| == |E1| {
        assert E == E1;
        assert FromOf(d, p, n + 1, E1[t]);
        var m :| n + 1 <= m < |p| && E1[t].0 == p[m].0 && Matches(d, p[m].0) == {E1[t].1} && p[m].0 != E1[t].1;
      } else if t == 0 {
        var ci := IcTryGet(Cols(d), key).value;
        assert E[0] == (key, Cols(d)[ci].1[0]);
        assert E[0].0 == p[n].0;
      } else {
        assert E[t] == E1[t - 1];
        assert FromOf(d, p, n + 1, E1[t - 1]);
        var m :| n + 1 <= m < |p| && E1[t - 1].0 == p[m].0 && Matches(d, p[m].0) == {E1[t - 1].1} && p[m].0 != E1[t - 1].1;
      }
    }
  }
}

lemma ExpectedComplete(d: Desc, p: Payload, n: nat)
  requires n <= |p|
  ensures forall m, c :: n <= m < |p| && Matches(d, p[m].0) == {c} && p[m].0 != c ==> (p[m].0, c) in Expected(d, p, n)
  decreases |p| - n
{
  if n < |p| {
    ExpectedComplete(d, p, n + 1);
    ExpectedHead(d, p, n);
    var key := p[n].0;
    ColsFacts(d, key);
    var E := Expected(d, p, n);
    var E1 := Expected(d, p, n + 1);
    assert forall x :: x in E1 ==> x in E;
    forall m, c | n <= m < |p| && Matches(d, p[m].0) == {c} && p[m].0 != c ensures (p[m].0, c) in E {
      if m == n {
        var ci := IcTryGet(Cols(d), key).value;
        var s := Cols(d)[ci].1;
        assert {s[0]} == {c};
        assert s[0] in {c};
        assert E[0] == (p[m].0, c);
      } else {
        assert (p[m].0, c) in E1;
      }
    }
  }
}

lemma ExpectedDistinct(d: Desc, p: Payload, n: nat)
  requires n <= |p| && DistinctKeys(p)
  ensures forall t, u :: 0 <= t < u < |Expected(d, p, n)| ==> Expected(d, p, n)[t].0 != Expected(d, p, n)[u].0
  decreases |p| - n
{
  if n < |p| {
    ExpectedDistinct(d, p, n + 1);
    ExpectedFrom(d, p, n + 1);
    ExpectedHead(d, p, n);
    var E := Expected(d, p, n);
    var E1 := Expected(d, p, n + 1);
    if |E| != |E1| {
      forall t, u | 0 <= t < u < |E| ensures E[t].0 != E[u].0 {
        assert E[u] == E1[u - 1];
        if t == 0 {
          assert E[0].0 == p[n].0;
          assert FromOf(d, p, n + 1, E1[u - 1]);
          var m :| n + 1 <= m < |p| && E1[u - 1].0 == p[m].0 && Matches(d, p[m].0) == {E1[u - 1].1} && p[m].0 != E1[u - 1].1;
          assert p[n].0 != p[m].0;
        } else {
          assert E[t] == E1[t - 1];
        }
      }
    }
  }
}

lemma ExpectedFacts(d: Desc, p: Payload, n: nat)
  requires n <= |p|
  ensures forall t :: 0 <= t < |Expected(d, p, n)| ==> FromOf(d, p, n, Expected(d, p, n)[t])
  ensures forall m, c :: n <= m < |p| && Matches(d, p[m].0) == {c} && p[m].0 != c ==> (p[m].0, c) in Expected(d, p, n)
  ensures DistinctKeys(p) ==> forall t, u :: 0 <= t < u < |Expected(d, p, n)| ==> Expected(d, p, n)[t].0 != Expected(d, p, n)[u].0
{
  ExpectedFrom(d, p, n);
  ExpectedComplete(d, p, n);
  if DistinctKeys(p) { ExpectedDistinct(d, p, n); }
}

lemma TosNotPresent(d: Desc, p: Payload, rs: seq<(CsString, CsString)>)
  requires DistinctKeys(p) && Good(d, p)
  requires forall t :: 0 <= t < |rs| ==> FromOf(d, p, 0, rs[t])
  ensures forall t :: 0 <= t < |rs| ==> Present(p, rs[t].0) && !Present(p, rs[t].1)
{
  forall t | 0 <= t < |rs| ensures Present(p, rs[t].0) && !Present(p, rs[t].1) {
    assert FromOf(d, p, 0, rs[t]);
    var m :| 0 <= m < |p| && rs[t].0 == p[m].0 && Matches(d, p[m].0) == {rs[t].1} && p[m].0 != rs[t].1;
    var to := rs[t].1;
    assert to in Matches(d, p[m].0);
    assert to in ColumnNames(d) && !IsTenantColumn(to);
    forall m' | 0 <= m' < |p| ensures p[m'].0 != to {
      var km := p[m'].0;
      var Mm := Matches(d, p[m].0);
      var Mk := Matches(d, km);
      SingletonOf(Mk, to);
      assert km == to ==> to in Mk;
      assert |Mk| <= 1;
      assert m < m' && |Mm| == 1 ==> Mk != Mm;
      assert m' < m && |Mk| == 1 ==> Mm != Mk;
    }
  }
}

lemma RenamesDistinct(d: Desc, p: Payload, rs: seq<(CsString, CsString)>)
  requires DistinctKeys(p) && Good(d, p)
  requires forall t :: 0 <= t < |rs| ==> FromOf(d, p, 0, rs[t])
  requires forall t, u :: 0 <= t < u < |rs| ==> rs[t].0 != rs[u].0
  ensures forall t, u :: 0 <= t < u < |rs| ==> rs[t].0 != rs[u].0 && rs[t].1 != rs[u].1
{
  forall t, u | 0 <= t < u < |rs| ensures rs[t].1 != rs[u].1 {
    assert FromOf(d, p, 0, rs[t]) && FromOf(d, p, 0, rs[u]);
    var mt :| 0 <= mt < |p| && rs[t].0 == p[mt].0 && Matches(d, p[mt].0) == {rs[t].1} && p[mt].0 != rs[t].1;
    var mu :| 0 <= mu < |p| && rs[u].0 == p[mu].0 && Matches(d, p[mu].0) == {rs[u].1} && p[mu].0 != rs[u].1;
    assert mt != mu;
    if mt < mu {
      assert Matches(d, p[mu].0) != Matches(d, p[mt].0);
    } else {
      assert Matches(d, p[mt].0) != Matches(d, p[mu].0);
    }
  }
}

// Establishes, from Good and DistinctKeys, that the scan succeeds and that ApplyRenames
// succeeds with the result described by ApplyOK.
lemma ApplyPrep(d: Desc, p: Payload) returns (rs: seq<(CsString, CsString)>)
  requires DistinctKeys(p) && Good(d, p)
  ensures forall t :: 0 <= t < |rs| ==> FromOf(d, p, 0, rs[t])
  ensures forall m, c :: 0 <= m < |p| && Matches(d, p[m].0) == {c} && p[m].0 != c ==> (p[m].0, c) in rs
  ensures ApplyRenames(p, rs).Some?
  ensures Canonicalize(d, p) == Canonical(ApplyRenames(p, rs).value)
  ensures var r := ApplyRenames(p, rs).value; |r| == |p| && DistinctKeys(r)
  ensures forall k, v :: Has(p, k, v) && (forall t :: 0 <= t < |rs| ==> rs[t].0 != k) ==> Has(ApplyRenames(p, rs).value, k, v)
  ensures forall t, v :: 0 <= t < |rs| && Has(p, rs[t].0, v) ==> Has(ApplyRenames(p, rs).value, rs[t].1, v)
{
  rs := Expected(d, p, 0);
  ScanOkIfGood(d, p, 0, map[], []);
  assert [] + rs == rs;
  assert Scan(d, BuildColumns(ColumnNames(d), []), p, 0, map[], []) == ScanOk(rs);
  ExpectedFacts(d, p, 0);
  TosNotPresent(d, p, rs);
  RenamesDistinct(d, p, rs);
  ApplyOK(p, rs);
}

lemma SingletonOf<T>(s: set<T>, x: T)
  ensures x in s && |s| <= 1 ==> s == {x}
{
  if x in s && |s| <= 1 {
    assert |s - {x}| == |s| - 1;
    assert s - {x} == {};
    forall y | y in s ensures y == x {
      assert y !in s - {x};
    }
  }
}

// --- payload ---------------------------------------------------------------

lemma HasCons(q: Payload, k: seq<char>, v: Value)
  requires q != []
  ensures Has(q, k, v) <==> (q[0].0 == k && q[0].1 == v) || Has(q[1..], k, v)
  ensures Present(q, k) <==> q[0].0 == k || Present(q[1..], k)
{
  if Has(q, k, v) {
    var j :| 0 <= j < |q| && q[j].0 == k && q[j].1 == v;
    if j > 0 { assert q[1..][j - 1] == q[j]; }
  }
  if Has(q[1..], k, v) {
    var j :| 0 <= j < |q[1..]| && q[1..][j].0 == k && q[1..][j].1 == v;
    assert q[j + 1] == q[1..][j];
  }
  if Present(q, k) {
    var j :| 0 <= j < |q| && q[j].0 == k;
    if j > 0 { assert q[1..][j - 1] == q[j]; }
  }
  if Present(q[1..], k) {
    var j :| 0 <= j < |q[1..]| && q[1..][j].0 == k;
    assert q[j + 1] == q[1..][j];
  }
}

lemma PRemoveFacts(q: Payload, k: seq<char>)
  requires DistinctKeys(q)
  ensures DistinctKeys(PRemove(q, k))
  ensures |PRemove(q, k)| == if Present(q, k) then |q| - 1 else |q|
  ensures forall k', v :: Has(PRemove(q, k), k', v) <==> Has(q, k', v) && k' != k
  ensures forall k' :: Present(PRemove(q, k), k') <==> Present(q, k') && k' != k
{
  if q != [] {
    var tl := q[1..];
    assert DistinctKeys(tl) by {
      forall i, j | 0 <= i < j < |tl| ensures tl[i].0 != tl[j].0 {
        assert tl[i] == q[i + 1] && tl[j] == q[j + 1];
      }
    }
    PRemoveFacts(tl, k);
    var r := PRemove(q, k);
    var rt := PRemove(tl, k);
    forall k', v ensures Has(q, k', v) <==> (q[0].0 == k' && q[0].1 == v) || Has(tl, k', v) {
      HasCons(q, k', v);
    }
    forall k' ensures Present(q, k') <==> q[0].0 == k' || Present(tl, k') {
      HasCons(q, k', q[0].1);
    }
    forall j | 0 <= j < |tl| ensures tl[j].0 != q[0].0 {
      assert tl[j] == q[j + 1];
    }
    assert !Present(tl, q[0].0);
    if q[0].0 == k {
      assert r == rt;
    } else {
      assert r == [q[0]] + rt;
      assert r[1..] == rt;
      forall k', v ensures Has(r, k', v) <==> (q[0].0 == k' && q[0].1 == v) || Has(rt, k', v) {
        HasCons(r, k', v);
      }
      forall k' ensures Present(r, k') <==> q[0].0 == k' || Present(rt, k') {
        HasCons(r, k', q[0].1);
      }
      forall i, j | 0 <= i < j < |r| ensures r[i].0 != r[j].0 {
        assert r[j] == rt[j - 1];
        if i == 0 {
          assert Present(rt, r[j].0);
        } else {
          assert r[i] == rt[i - 1];
        }
      }
    }
  }
}

lemma ApplyOK(q: Payload, rs: seq<(CsString, CsString)>)
  requires DistinctKeys(q)
  requires forall t :: 0 <= t < |rs| ==> Present(q, rs[t].0) && !Present(q, rs[t].1)
  requires forall t, u :: 0 <= t < u < |rs| ==> rs[t].0 != rs[u].0 && rs[t].1 != rs[u].1
  ensures ApplyRenames(q, rs).Some?
  ensures var r := ApplyRenames(q, rs).value; |r| == |q| && DistinctKeys(r)
  ensures forall k, v :: Has(q, k, v) && (forall t :: 0 <= t < |rs| ==> rs[t].0 != k) ==> Has(ApplyRenames(q, rs).value, k, v)
  ensures forall t, v :: 0 <= t < |rs| && Has(q, rs[t].0, v) ==> Has(ApplyRenames(q, rs).value, rs[t].1, v)
  decreases |rs|
{
  if rs != [] {
    var from := rs[0].0;
    var to := rs[0].1;
    var i := PIndex(q, from).value;
    var v0 := q[i].1;
    var q1 := PRemove(q, from);
    PRemoveFacts(q, from);
    assert !Present(q1, to);
    var q2 := PSet(q1, to, v0);
    assert q2 == q1 + [(to, v0)];
    assert ApplyRenames(q, rs) == ApplyRenames(q2, rs[1..]);
    // facts on q2
    assert DistinctKeys(q2) by {
      forall a, b | 0 <= a < b < |q2| ensures q2[a].0 != q2[b].0 {
        if b == |q1| {
          assert q2[a] == q1[a];
          assert Present(q1, q1[a].0);
        } else {
          assert q2[a] == q1[a] && q2[b] == q1[b];
        }
      }
    }
    assert forall k, v :: Has(q1, k, v) ==> Has(q2, k, v) by {
      forall k, v | Has(q1, k, v) ensures Has(q2, k, v) {
        var j :| 0 <= j < |q1| && q1[j].0 == k && q1[j].1 == v;
        assert q2[j] == q1[j];
      }
    }
    assert Has(q2, to, v0) by { assert q2[|q1|] == (to, v0); }
    assert forall k :: Present(q2, k) <==> Present(q1, k) || k == to by {
      forall k ensures Present(q2, k) <==> Present(q1, k) || k == to {
        if Present(q2, k) {
          var j :| 0 <= j < |q2| && q2[j].0 == k;
          if j < |q1| { assert q2[j] == q1[j]; }
        }
        if Present(q1, k) {
          var j :| 0 <= j < |q1| && q1[j].0 == k;
          assert q2[j] == q1[j];
        }
        if k == to { assert q2[|q1|].0 == to; }
      }
    }
    var rs1 := rs[1..];
    forall t | 0 <= t < |rs1| ensures Present(q2, rs1[t].0) && !Present(q2, rs1[t].1) {
      assert rs1[t] == rs[t + 1];
    }
    forall t, u | 0 <= t < u < |rs1| ensures rs1[t].0 != rs1[u].0 && rs1[t].1 != rs1[u].1 {
      assert rs1[t] == rs[t + 1] && rs1[u] == rs[u + 1];
    }
    ApplyOK(q2, rs1);
    var r := ApplyRenames(q2, rs1).value;
    forall k, v | Has(q, k, v) && (forall t :: 0 <= t < |rs| ==> rs[t].0 != k) ensures Has(r, k, v) {
      assert rs[0].0 != k;
      assert Has(q1, k, v);
      forall t | 0 <= t < |rs1| ensures rs1[t].0 != k { assert rs1[t] == rs[t + 1]; }
    }
    forall t, v | 0 <= t < |rs| && Has(q, rs[t].0, v) ensures Has(r, rs[t].1, v) {
      if t == 0 {
        var j :| 0 <= j < |q| && q[j].0 == from && q[j].1 == v;
        assert j == i;
        forall t' | 0 <= t' < |rs1| ensures rs1[t'].0 != to {
          assert rs1[t'] == rs[t' + 1];
          assert Present(q, rs1[t'].0);
        }
      } else {
        assert rs[t] == rs1[t - 1];
        assert rs[t].0 != from;
        assert Has(q1, rs[t].0, v);
      }
    }
  }
}
