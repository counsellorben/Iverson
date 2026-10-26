// Spec: /home/ben/repositories/Iverson/docs/specs/2026-10-26-csr-round12-supply-chain-design.md @ f6f5db2cbea0425d938739f73a8d6e9147479682
// Source: §4.5 One pin table, two checks (and §4.4 line 151), lines 153-159
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-26-csr-round12-supply-chain-design/PinChecks.dfy
// Round: 1

datatype Option<T> = None | Some(value: T)
datatype Version = Version(major: int, minor: int, patch: int)
// Source kinds: line 155 (`dependabot` rows; every other source kind fetches a newest release;
// `dockerhub-tag <repo> <series>` and `maven-central ... <series>` are series-bound), line 151 (eks-addon).
datatype Source = Dependabot | Fetched(seriesOnly: bool) | EksAddon
// Line 155: a name, a source, one or more locations (each yields a version).
datatype Row = Row(name: string, source: Source, locations: seq<Version>)

// Line 156: `pins.py consistency`: every row's locations hold the same version.
predicate Consistent(r: Row)
{
  |r.locations| == 0 || (forall k :: 0 <= k < |r.locations| ==> r.locations[k] == r.locations[0])
}

predicate Le(a: Version, b: Version)
{
  a.major < b.major
  || (a.major == b.major && a.minor < b.minor)
  || (a.major == b.major && a.minor == b.minor && a.patch <= b.patch)
}

// Newest version of a finite set (None when empty).
ghost function MaxOf(s: set<Version>): Option<Version>
  ensures MaxOf(s).Some? ==> MaxOf(s).value in s
  ensures s != {} ==> MaxOf(s).Some?
  decreases |s|
{
  if s == {} then None
  else
    var x :| x in s;
    var rest := MaxOf(s - {x});
    if rest.None? || Le(rest.value, x) then Some(x) else rest
}

lemma MaxOfIn(s: set<Version>)
  ensures MaxOf(s).Some? ==> MaxOf(s).value in s
{
}

// Line 157: newest release; newest patch within the pinned series for series-bound sources;
// compatible versions for EKS add-ons (`available` is what the source reports as eligible).
ghost function Newest(r: Row, available: set<Version>): Option<Version>
{
  match r.source
  case Dependabot => None
  case EksAddon => MaxOf(available)
  case Fetched(seriesOnly) =>
    if !seriesOnly then MaxOf(available)
    else if |r.locations| == 0 then None
    else MaxOf(set v | v in available && v.major == r.locations[0].major && v.minor == r.locations[0].minor)
}

// Line 157: "one issue `Pin behind: <name>` per stale row"; stale = pinned version is not the newest
// (line 155 chart-app-version: "stale when the pinned tag differs").
ghost predicate Stale(r: Row, available: set<Version>)
{
  match Newest(r, available)
  case None => false
  case Some(n) => |r.locations| > 0 && r.locations[0] != n
}

// Line 157 "for each non-`dependabot` row"; line 151: EKS rows skipped when no AWS role.
predicate Active(r: Row, roleSet: bool)
{
  r.source.Fetched? || (r.source == EksAddon && roleSet)
}

// Open the issue when stale, close it when current; a skipped row leaves its issue as it was.
ghost function Apply(r: Row, available: string -> set<Version>, roleSet: bool, open: set<string>): set<string>
{
  if !Active(r, roleSet) then open
  else if Stale(r, available(r.name)) then open + {r.name}
  else open - {r.name}
}

ghost function Freshness(rows: seq<Row>, available: string -> set<Version>, roleSet: bool, open: set<string>): set<string>
  decreases |rows|
{
  if |rows| == 0 then open
  else Apply(rows[0], available, roleSet, Freshness(rows[1..], available, roleSet, open))
}

predicate DistinctNames(rows: seq<Row>)
{
  forall i, j :: 0 <= i < |rows| && 0 <= j < |rows| && i != j ==> rows[i].name != rows[j].name
}

lemma NoRowKeepsOpen(rows: seq<Row>, available: string -> set<Version>, roleSet: bool, open: set<string>, n: string)
  requires forall x :: x in rows ==> x.name != n
  ensures (n in Freshness(rows, available, roleSet, open)) <==> (n in open)
  decreases |rows|
{
  if |rows| > 0 {
    assert rows[0] in rows;
    assert forall x :: x in rows[1..] ==> x in rows;
    NoRowKeepsOpen(rows[1..], available, roleSet, open, n);
  }
}

lemma Key(rows: seq<Row>, available: string -> set<Version>, roleSet: bool, open: set<string>, r: Row)
  requires DistinctNames(rows) && r in rows
  ensures (r.name in Freshness(rows, available, roleSet, open))
          <==> (if Active(r, roleSet) then Stale(r, available(r.name)) else r.name in open)
  decreases |rows|
{
  assert DistinctNames(rows[1..]) by {
    forall i, j | 0 <= i < |rows[1..]| && 0 <= j < |rows[1..]| && i != j
      ensures rows[1..][i].name != rows[1..][j].name
    {
      assert rows[1..][i] == rows[i + 1];
      assert rows[1..][j] == rows[j + 1];
    }
  }
  if r == rows[0] {
    forall x | x in rows[1..]
      ensures x.name != r.name
    {
      var k :| 1 <= k < |rows| && rows[k] == x;
      assert rows[k].name != rows[0].name;
    }
    NoRowKeepsOpen(rows[1..], available, roleSet, open, r.name);
  } else {
    var k :| 0 <= k < |rows| && rows[k] == r;
    assert k >= 1;
    assert rows[1..][k - 1] == r;
    assert r.name != rows[0].name;
    Key(rows[1..], available, roleSet, open, r);
  }
}

// Citation: "`pins.py consistency` (a `deploy-validate` step on every PR): every row's locations hold the same version. Fails on a mismatch." (line 156)
lemma P1_ConsistencyIffLocationsEqual(r: Row)
  ensures Consistent(r) <==> forall i, j :: 0 <= i < |r.locations| && 0 <= j < |r.locations| ==> r.locations[i] == r.locations[j]
{
  if Consistent(r) {
    forall i, j | 0 <= i < |r.locations| && 0 <= j < |r.locations|
      ensures r.locations[i] == r.locations[j]
    {
      assert r.locations[i] == r.locations[0];
      assert r.locations[j] == r.locations[0];
    }
  }
}

// Citation: "for each non-`dependabot` row" (line 157)
lemma P2_DependabotRowsUntouched(rows: seq<Row>, available: string -> set<Version>, roleSet: bool, open: set<string>, r: Row)
  requires DistinctNames(rows) && r in rows && r.source == Dependabot
  ensures (r.name in Freshness(rows, available, roleSet, open)) <==> (r.name in open)
{
  Key(rows, available, roleSet, open, r);
}

// Citation: "opens or updates one issue `Pin behind: <name>` per stale row, closes it when current" (line 157)
lemma P3_IssueOpenIffStale(rows: seq<Row>, available: string -> set<Version>, roleSet: bool, open: set<string>, r: Row)
  requires DistinctNames(rows) && r in rows && Consistent(r)
  requires r.source.Fetched? || (r.source == EksAddon && roleSet)
  ensures (r.name in Freshness(rows, available, roleSet, open)) <==> Stale(r, available(r.name))
{
  Key(rows, available, roleSet, open, r);
}

// Citation: "the newest release (newest patch within the pinned series for `kindest/node`" (line 157)
lemma P4_SeriesNewestStaysInSeries(r: Row, available: set<Version>, v: Version)
  requires r.source == Fetched(true) && |r.locations| > 0 && Newest(r, available) == Some(v)
  ensures v.major == r.locations[0].major && v.minor == r.locations[0].minor
{
  var c := set w | w in available && w.major == r.locations[0].major && w.minor == r.locations[0].minor;
  MaxOfIn(c);
}

// Citation: "unset → the EKS rows report \"skipped: no AWS role\" and the rest run." (line 151)
lemma P5_NoRoleLeavesEksUntouched(rows: seq<Row>, available: string -> set<Version>, open: set<string>, r: Row)
  requires DistinctNames(rows) && r in rows && r.source == EksAddon
  ensures (r.name in Freshness(rows, available, false, open)) <==> (r.name in open)
{
  Key(rows, available, false, open, r);
}

// Citation: "and the rest run." (line 151)
lemma P6_NoRoleStillChecksOthers(rows: seq<Row>, available: string -> set<Version>, open: set<string>, r: Row)
  requires DistinctNames(rows) && r in rows && r.source.Fetched?
  ensures (r.name in Freshness(rows, available, false, open)) <==> (r.name in Freshness(rows, available, true, open))
{
  Key(rows, available, false, open, r);
  Key(rows, available, true, open, r);
}
