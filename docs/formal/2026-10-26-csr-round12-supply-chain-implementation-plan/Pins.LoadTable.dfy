// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md, T11 S1
// Source: ci/pins.py _scalar, load_table
// Replay base: 680eb9cf5a6dc2d3dd6374446822969441bfed0f
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-26-csr-round12-supply-chain-implementation-plan/Pins.LoadTable.dfy

// ---- strings and library boundary ---------------------------------------------------------------------

newtype CodePoint = x: int | 0 <= x < 0x110000
type Str = seq<CodePoint>

datatype Option<T> = None | Some(value: T)

// The line tests of load_table, in the code's order (re.fullmatch patterns; Skip = blank or `#`).
datatype LineKind = Skip | Name(v: Str) | Field(key: Str, v: Str) | LocationsHeader | File(v: Str) | Regex(v: Str) | Other

// The dicts: `source` and `regex` are optional keys; `note` is read and dropped.
datatype TLocation = TLocation(file: Str, regex: Option<Str>)
datatype TRow = TRow(name: Str, source: Option<Str>, locations: seq<TLocation>)

// The PinError messages, one variant per raise site (the f-string text is not modelled).
datatype PinMessage =
  | Unterminated(value: Str)
  | NotInFormat(number: int, line: Str)
  | NeedsSourceAndLocation(name: Str)
  | NameUsedTwice(name: Str)
  | NoRegex(name: Str, file: Str)
  | NotOneGroup(name: Str, file: Str)

// Ok(rows) = return rows; Err = PinError raised; Crash = re.error from re.compile (not a PinError).
datatype TableResult = Ok(rows: seq<TRow>) | Err(message: PinMessage) | Crash

datatype ScalarResult = SOk(value: Str) | SErr(message: PinMessage)

// Approved axioms.
function Lines(text: Str): seq<Str>

function Classify(line: Str): LineKind

function GroupCount(regex: Str): Option<int>

// ---- literals ------------------------------------------------------------------------------------------

const Quote: CodePoint := 39                       // "'"
const SourceKey: Str := [115, 111, 117, 114, 99, 101] // "source"

// ---- str.strip() ---------------------------------------------------------------------------------------

// Python's str.isspace / Py_UNICODE_ISSPACE: the characters str.strip() removes.
predicate IsSpace(c: CodePoint)
{
  (0x09 <= c <= 0x0D) || (0x1C <= c <= 0x20) || c == 0x85 || c == 0xA0 || c == 0x1680
  || (0x2000 <= c <= 0x200A) || c == 0x2028 || c == 0x2029 || c == 0x202F || c == 0x205F || c == 0x3000
}

function LStrip(s: Str): Str
  decreases |s|
{
  if |s| > 0 && IsSpace(s[0]) then LStrip(s[1..]) else s
}

function RStrip(s: Str): Str
  decreases |s|
{
  if |s| > 0 && IsSpace(s[|s| - 1]) then RStrip(s[..|s| - 1]) else s
}

function Strip(s: Str): Str
{
  RStrip(LStrip(s))
}

// str.replace("''", "'"): non-overlapping, left to right.
function ReplaceDoubledQuotes(s: Str): Str
  decreases |s|
{
  if |s| == 0 then []
  else if |s| >= 2 && s[0] == Quote && s[1] == Quote then [Quote] + ReplaceDoubledQuotes(s[2..])
  else [s[0]] + ReplaceDoubledQuotes(s[1..])
}

// ---- _scalar (ci/pins.py:45-51) ------------------------------------------------------------------------

function Scalar(value0: Str): ScalarResult
{
  var value := Strip(value0);
  if |value| > 0 && value[0] == Quote then
    if |value| < 2 || value[|value| - 1] != Quote then SErr(Unterminated(value))
    else SOk(ReplaceDoubledQuotes(value[1..|value| - 1]))
  else SOk(value)
}

// ---- load_table (ci/pins.py:54-86) ---------------------------------------------------------------------

function LoadTable(text: Str): TableResult
{
  Scan(Lines(text), 0, [], false, false)
}

// The `for number, line in enumerate(text.splitlines(), 1)` loop. `row` is `row is not None` (the current row
// is the last element of rows, by aliasing); `location` is `location is not None` (the current location is the
// last element of that row's locations). The requires clauses are the loop's aliasing invariants.
function Scan(lines: seq<Str>, i: nat, rows: seq<TRow>, row: bool, location: bool): TableResult
  requires i <= |lines|
  requires row ==> |rows| > 0
  requires location ==> row && |rows[|rows| - 1].locations| > 0
  decreases |lines| - i
{
  if i == |lines| then Validate(rows)
  else
    var number := i + 1;
    var line := lines[i];
    var kind := Classify(line);
    if kind.Skip? then
      Scan(lines, i + 1, rows, row, location)
    else if kind.Name? then
      match Scalar(kind.v)
      case SErr(e) => Err(e)
      case SOk(name) => Scan(lines, i + 1, rows + [TRow(name, None, [])], true, false)
    else if row && kind.Field? then
      match Scalar(kind.v)
      case SErr(e) => Err(e)
      case SOk(v) => Scan(lines, i + 1, SetField(rows, kind.key, v), row, location)
    else if row && kind.LocationsHeader? then
      Scan(lines, i + 1, rows, row, location)
    else if row && kind.File? then
      match Scalar(kind.v)
      case SErr(e) => Err(e)
      case SOk(f) => Scan(lines, i + 1, AddLocation(rows, f), row, true)
    else if location && kind.Regex? then
      match Scalar(kind.v)
      case SErr(e) => Err(e)
      case SOk(re) => Scan(lines, i + 1, SetRegex(rows, re), row, location)
    else
      Err(NotInFormat(number, line))
}

// row[m[1]] = value: `source` sets the source; `note` is dropped.
function SetField(rows: seq<TRow>, key: Str, v: Str): seq<TRow>
  requires |rows| > 0
{
  if key == SourceKey then rows[..|rows| - 1] + [rows[|rows| - 1].(source := Some(v))] else rows
}

// location = {"file": ...}; row["locations"].append(location)
function AddLocation(rows: seq<TRow>, f: Str): seq<TRow>
  requires |rows| > 0
{
  var r := rows[|rows| - 1];
  rows[..|rows| - 1] + [r.(locations := r.locations + [TLocation(f, None)])]
}

// location["regex"] = value
function SetRegex(rows: seq<TRow>, re: Str): seq<TRow>
  requires |rows| > 0 && |rows[|rows| - 1].locations| > 0
{
  var r := rows[|rows| - 1];
  var locs := r.locations;
  rows[..|rows| - 1] + [r.(locations := locs[..|locs| - 1] + [locs[|locs| - 1].(regex := Some(re))])]
}

// names = [r["name"] for r in rows]
function Names(rows: seq<TRow>): seq<Str>
{
  seq(|rows|, j requires 0 <= j < |rows| => rows[j].name)
}

// list.count
function Count(s: seq<Str>, x: Str): nat
  decreases |s|
{
  if |s| == 0 then 0 else (if s[0] == x then 1 else 0) + Count(s[1..], x)
}

function Validate(rows: seq<TRow>): TableResult
{
  CheckRows(rows, Names(rows), 0)
}

// for r in rows: ...; return rows
function CheckRows(rows: seq<TRow>, names: seq<Str>, j: nat): TableResult
  requires j <= |rows|
  decreases |rows| - j
{
  if j == |rows| then Ok(rows)
  else
    var r := rows[j];
    if r.source.None? || |r.locations| == 0 then Err(NeedsSourceAndLocation(r.name))
    else if Count(names, r.name) > 1 then Err(NameUsedTwice(r.name))
    else
      match CheckLocs(r, 0)
      case Some(failure) => failure
      case None => CheckRows(rows, names, j + 1)
}

// for loc in r["locations"]: ...   (None: every location passed)
function CheckLocs(r: TRow, k: nat): Option<TableResult>
  requires k <= |r.locations|
  decreases |r.locations| - k
{
  if k == |r.locations| then None
  else
    var loc := r.locations[k];
    if loc.regex.None? then Some(Err(NoRegex(r.name, loc.file)))
    else
      match GroupCount(loc.regex.value)
      case None => Some(Crash)
      case Some(g) => if g != 1 then Some(Err(NotOneGroup(r.name, loc.file))) else CheckLocs(r, k + 1)
}

// ---- helper lemmas -------------------------------------------------------------------------------------

ghost predicate RowGood(r: TRow, names: seq<Str>)
{
  r.source.Some? && |r.locations| > 0 && Count(names, r.name) <= 1
  && forall l :: l in r.locations ==> l.regex.Some? && GroupCount(l.regex.value) == Some(1)
}

ghost predicate RowsGood(rows: seq<TRow>)
{
  forall m :: 0 <= m < |rows| ==> RowGood(rows[m], Names(rows))
}

lemma CheckLocsSound(r: TRow, k: nat)
  requires k <= |r.locations|
  ensures CheckLocs(r, k).Some? ==> !CheckLocs(r, k).value.Ok?
  ensures CheckLocs(r, k).None? ==>
    forall m :: k <= m < |r.locations| ==> r.locations[m].regex.Some? && GroupCount(r.locations[m].regex.value) == Some(1)
  decreases |r.locations| - k
{
  if k < |r.locations| {
    CheckLocsSound(r, k + 1);
  }
}

lemma CheckRowsSound(rows: seq<TRow>, names: seq<Str>, j: nat)
  requires j <= |rows|
  ensures CheckRows(rows, names, j).Ok? ==>
    CheckRows(rows, names, j).rows == rows && forall m :: j <= m < |rows| ==> RowGood(rows[m], names)
  decreases |rows| - j
{
  if j < |rows| {
    var r := rows[j];
    CheckLocsSound(r, 0);
    CheckRowsSound(rows, names, j + 1);
    if CheckRows(rows, names, j).Ok? {
      assert CheckLocs(r, 0).None?;
      assert forall l :: l in r.locations ==> l.regex.Some? && GroupCount(l.regex.value) == Some(1) by {
        forall l | l in r.locations
          ensures l.regex.Some? && GroupCount(l.regex.value) == Some(1)
        {
          var m :| 0 <= m < |r.locations| && r.locations[m] == l;
        }
      }
      assert RowGood(r, names);
    }
  }
}

lemma ValidateSound(rows: seq<TRow>)
  ensures Validate(rows).Ok? ==> Validate(rows).rows == rows && RowsGood(rows)
{
  CheckRowsSound(rows, Names(rows), 0);
}

lemma ScanSound(lines: seq<Str>, i: nat, rows: seq<TRow>, row: bool, location: bool)
  requires i <= |lines|
  requires row ==> |rows| > 0
  requires location ==> row && |rows[|rows| - 1].locations| > 0
  ensures Scan(lines, i, rows, row, location).Ok? ==> RowsGood(Scan(lines, i, rows, row, location).rows)
  decreases |lines| - i
{
  if i == |lines| {
    ValidateSound(rows);
  } else {
    var kind := Classify(lines[i]);
    if kind.Skip? {
      ScanSound(lines, i + 1, rows, row, location);
    } else if kind.Name? {
      if Scalar(kind.v).SOk? {
        ScanSound(lines, i + 1, rows + [TRow(Scalar(kind.v).value, None, [])], true, false);
      }
    } else if row && kind.Field? {
      if Scalar(kind.v).SOk? {
        ScanSound(lines, i + 1, SetField(rows, kind.key, Scalar(kind.v).value), row, location);
      }
    } else if row && kind.LocationsHeader? {
      ScanSound(lines, i + 1, rows, row, location);
    } else if row && kind.File? {
      if Scalar(kind.v).SOk? {
        ScanSound(lines, i + 1, AddLocation(rows, Scalar(kind.v).value), row, true);
      }
    } else if location && kind.Regex? {
      if Scalar(kind.v).SOk? {
        ScanSound(lines, i + 1, SetRegex(rows, Scalar(kind.v).value), row, location);
      }
    }
  }
}

lemma ScanRefusesOther(lines: seq<Str>, i: nat, rows: seq<TRow>, row: bool, location: bool, k: int)
  requires i <= k < |lines| && Classify(lines[k]).Other?
  requires row ==> |rows| > 0
  requires location ==> row && |rows[|rows| - 1].locations| > 0
  ensures !Scan(lines, i, rows, row, location).Ok?
  decreases |lines| - i
{
  if i < k {
    var kind := Classify(lines[i]);
    if kind.Skip? {
      ScanRefusesOther(lines, i + 1, rows, row, location, k);
    } else if kind.Name? {
      if Scalar(kind.v).SOk? {
        ScanRefusesOther(lines, i + 1, rows + [TRow(Scalar(kind.v).value, None, [])], true, false, k);
      }
    } else if row && kind.Field? {
      if Scalar(kind.v).SOk? {
        ScanRefusesOther(lines, i + 1, SetField(rows, kind.key, Scalar(kind.v).value), row, location, k);
      }
    } else if row && kind.LocationsHeader? {
      ScanRefusesOther(lines, i + 1, rows, row, location, k);
    } else if row && kind.File? {
      if Scalar(kind.v).SOk? {
        ScanRefusesOther(lines, i + 1, AddLocation(rows, Scalar(kind.v).value), row, true, k);
      }
    } else if location && kind.Regex? {
      if Scalar(kind.v).SOk? {
        ScanRefusesOther(lines, i + 1, SetRegex(rows, Scalar(kind.v).value), row, location, k);
      }
    }
  }
}

lemma CountAtLeastTwo(s: seq<Str>, i: int, j: int)
  requires 0 <= i < j < |s| && s[i] == s[j]
  ensures Count(s, s[i]) >= 2
  decreases |s|
{
  if i > 0 {
    CountAtLeastTwo(s[1..], i - 1, j - 1);
  } else {
    CountAtLeastOne(s[1..], j - 1, s[i]);
  }
}

lemma CountAtLeastOne(s: seq<Str>, j: int, x: Str)
  requires 0 <= j < |s| && s[j] == x
  ensures Count(s, x) >= 1
  decreases |s|
{
  if j > 0 {
    CountAtLeastOne(s[1..], j - 1, x);
  }
}

// ---- properties ----------------------------------------------------------------------------------------

// Citation: test `test_the_table_refuses_a_malformed_row` (cases "needs a source", "has no regex", "exactly one group").
lemma P1_RowsAreComplete(text: Str, r: TRow, l: TLocation)
  requires LoadTable(text).Ok? && r in LoadTable(text).rows && l in r.locations
  ensures r.source.Some? && |r.locations| > 0 && l.regex.Some? && GroupCount(l.regex.value) == Some(1)
{
  ScanSound(Lines(text), 0, [], false, false);
  var rows := LoadTable(text).rows;
  var m :| 0 <= m < |rows| && rows[m] == r;
  assert RowGood(rows[m], Names(rows));
}

// Citation: same test, case `ROW + ROW` "used twice".
lemma P2_NamesDistinct(text: Str, i: int, j: int)
  requires LoadTable(text).Ok? && 0 <= i < j < |LoadTable(text).rows|
  ensures LoadTable(text).rows[i].name != LoadTable(text).rows[j].name
{
  ScanSound(Lines(text), 0, [], false, false);
  var rows := LoadTable(text).rows;
  var names := Names(rows);
  assert RowGood(rows[i], names);
  if rows[i].name == rows[j].name {
    assert names[i] == rows[i].name && names[j] == rows[j].name;
    CountAtLeastTwo(names, i, j);
    assert false;
  }
}

// Citation: same test, case "not in the table's format".
lemma P3_UnknownLineRefused(text: Str, k: int)
  requires 0 <= k < |Lines(text)| && Classify(Lines(text)[k]).Other?
  ensures !LoadTable(text).Ok?
{
  ScanRefusesOther(Lines(text), 0, [], false, false, k);
}
