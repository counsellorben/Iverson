// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md, T11 S1
// Source: ci/pins.py _scalar, load_table
// Replay base: a6c008dce9e4c846f8c34456ddd6297636d31979
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-26-csr-round12-supply-chain-implementation-plan/Pins.LoadTable.dfy

// ---------------------------------------------------------------------------------------------------------
// Strings and library boundary
// ---------------------------------------------------------------------------------------------------------

newtype CodePoint = x: int | 0 <= x < 0x110000
type Str = seq<CodePoint>

datatype Option<T> = None | Some(value: T)

// The line tests of load_table, in the code's order (re.fullmatch on each pattern); the per-branch
// `row is not None` / `location is not None` conditions stay in the model below.
datatype LineKind = Skip | Name(v: Str) | Field(key: Str, v: Str) | LocationsHeader | File(v: Str) | Regex(v: Str) | Other

// text.splitlines()
function Lines(text: Str): seq<Str>

// The line tests: blank or '#' (Skip), r"- name: (.+)", r"  (source|note): (.+)", "  locations:",
// r"    - file: (.+)", r"      regex: (.+)", anything else (Other).
function Classify(line: Str): LineKind

// re.compile(regex, re.M).groups; None when re.compile raises re.error.
function GroupCount(regex: Str): Option<int>

// Named literals
const Quote: CodePoint := 39                          // "'"
const SourceKey: Str := [115, 111, 117, 114, 99, 101]  // "source"

// The dicts: {"name", "source"?, "note"?, "locations"} and {"file", "regex"?}; note is read and dropped.
datatype TLocation = TLocation(file: Str, regex: Option<Str>)
datatype TRow = TRow(name: Str, source: Option<Str>, locations: seq<TLocation>)

// The PinError raises, one variant per raise site (message formatting abstracted to its arguments).
datatype PinMessage =
  | Unterminated(value: Str)                 // _scalar
  | NotInFormat(number: int, line: Str)      // parse loop else-branch
  | NeedsSource(name: Str)                   // "needs a source and at least one location"
  | UsedTwice(name: Str)                     // "the name is used twice"
  | NoRegex(name: Str, file: Str)            // "location ... has no regex"
  | NotOneGroup(name: Str, file: Str)        // "must have exactly one group"

// load_table's outcomes: a returned list, a PinError, or re.error escaping re.compile.
datatype TableResult = Ok(rows: seq<TRow>) | Err(message: PinMessage) | Crash

// ---------------------------------------------------------------------------------------------------------
// _scalar
// ---------------------------------------------------------------------------------------------------------

// str.isspace() code points (Python 3; matches the set str.strip() with no argument removes)
predicate IsSpace(c: CodePoint)
{
  (9 <= c <= 13) || (0x1c <= c <= 0x20) || c == 0x85 || c == 0xa0 || c == 0x1680
  || (0x2000 <= c <= 0x200a) || c == 0x2028 || c == 0x2029 || c == 0x202f || c == 0x205f || c == 0x3000
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

// value.strip()
function Strip(s: Str): Str
{
  RStrip(LStrip(s))
}

// s.replace("''", "'"): left to right, non-overlapping
function ReplaceDoubledQuotes(s: Str): Str
  decreases |s|
{
  if |s| == 0 then []
  else if |s| >= 2 && s[0] == Quote && s[1] == Quote then [Quote] + ReplaceDoubledQuotes(s[2..])
  else [s[0]] + ReplaceDoubledQuotes(s[1..])
}

datatype ScalarResult = SOk(value: Str) | SErr(message: PinMessage)

function Scalar(value0: Str): ScalarResult
{
  var value := Strip(value0);
  if |value| > 0 && value[0] == Quote then
    if |value| < 2 || value[|value| - 1] != Quote then
      SErr(Unterminated(value))
    else
      SOk(ReplaceDoubledQuotes(value[1..|value| - 1]))
  else
    SOk(value)
}

// ---------------------------------------------------------------------------------------------------------
// load_table: the parse loop
// ---------------------------------------------------------------------------------------------------------

datatype ParseResult = Parsed(rows: seq<TRow>) | ParseErr(message: PinMessage)

// `row` is always the last element of `rows` (it is appended when created), so `row is not None` is
// `hasRow`, and `location` is always the last location of that row, so `location is not None` is `hasLoc`.
// The two requires below are invariants of this helper's own state, proved at every call site.
function ParseLines(lines: seq<Str>, i: nat, rows: seq<TRow>, hasRow: bool, hasLoc: bool): ParseResult
  requires i <= |lines|
  requires hasRow ==> |rows| > 0
  requires hasLoc ==> hasRow && |rows[|rows| - 1].locations| > 0
  decreases |lines| - i
{
  if i == |lines| then Parsed(rows)
  else
    var line := lines[i];
    var number := i + 1;
    match Classify(line)
    case Skip =>
      ParseLines(lines, i + 1, rows, hasRow, hasLoc)
    case Name(v) =>
      (match Scalar(v)
       case SErr(e) => ParseErr(e)
       case SOk(name) => ParseLines(lines, i + 1, rows + [TRow(name, None, [])], true, false))
    case Field(key, v) =>
      if hasRow then
        (match Scalar(v)
         case SErr(e) => ParseErr(e)
         case SOk(x) =>
           var row := rows[|rows| - 1];
           var row' := if key == SourceKey then row.(source := Some(x)) else row;  // note: dropped
           ParseLines(lines, i + 1, rows[..|rows| - 1] + [row'], hasRow, hasLoc))
      else ParseErr(NotInFormat(number, line))
    case LocationsHeader =>
      if hasRow then ParseLines(lines, i + 1, rows, hasRow, hasLoc)
      else ParseErr(NotInFormat(number, line))
    case File(v) =>
      if hasRow then
        (match Scalar(v)
         case SErr(e) => ParseErr(e)
         case SOk(f) =>
           var row := rows[|rows| - 1];
           var row' := row.(locations := row.locations + [TLocation(f, None)]);
           ParseLines(lines, i + 1, rows[..|rows| - 1] + [row'], hasRow, true))
      else ParseErr(NotInFormat(number, line))
    case Regex(v) =>
      if hasLoc then
        (match Scalar(v)
         case SErr(e) => ParseErr(e)
         case SOk(x) =>
           var row := rows[|rows| - 1];
           var locs := row.locations;
           var loc' := locs[|locs| - 1].(regex := Some(x));
           var row' := row.(locations := locs[..|locs| - 1] + [loc']);
           ParseLines(lines, i + 1, rows[..|rows| - 1] + [row'], hasRow, hasLoc))
      else ParseErr(NotInFormat(number, line))
    case Other =>
      ParseErr(NotInFormat(number, line))
}

// ---------------------------------------------------------------------------------------------------------
// load_table: the checks
// ---------------------------------------------------------------------------------------------------------

function Names(rows: seq<TRow>): seq<Str>
{
  seq(|rows|, k requires 0 <= k < |rows| => rows[k].name)
}

// names.count(x)
function Count(s: seq<Str>, x: Str): nat
  decreases |s|
{
  if |s| == 0 then 0 else (if s[0] == x then 1 else 0) + Count(s[1..], x)
}

datatype Check = Pass | Fail(outcome: TableResult)

function CheckLocations(r: TRow, j: nat): Check
  requires j <= |r.locations|
  decreases |r.locations| - j
{
  if j == |r.locations| then Pass
  else
    var loc := r.locations[j];
    if loc.regex.None? then Fail(Err(NoRegex(r.name, loc.file)))
    else match GroupCount(loc.regex.value)
      case None => Fail(Crash)
      case Some(g) =>
        if g != 1 then Fail(Err(NotOneGroup(r.name, loc.file)))
        else CheckLocations(r, j + 1)
}

function CheckRows(rows: seq<TRow>, names: seq<Str>, i: nat): TableResult
  requires i <= |rows|
  decreases |rows| - i
{
  if i == |rows| then Ok(rows)
  else
    var r := rows[i];
    if r.source.None? || |r.locations| == 0 then Err(NeedsSource(r.name))
    else if Count(names, r.name) > 1 then Err(UsedTwice(r.name))
    else match CheckLocations(r, 0)
      case Fail(o) => o
      case Pass => CheckRows(rows, names, i + 1)
}

function LoadTable(text: Str): TableResult
{
  match ParseLines(Lines(text), 0, [], false, false)
  case ParseErr(e) => Err(e)
  case Parsed(rows) =>
    var names := Names(rows);
    CheckRows(rows, names, 0)
}

// ---------------------------------------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------------------------------------

lemma CheckLocationsPass(r: TRow, j: nat, k: int)
  requires j <= |r.locations|
  requires CheckLocations(r, j) == Pass
  requires j <= k < |r.locations|
  ensures r.locations[k].regex.Some? && GroupCount(r.locations[k].regex.value) == Some(1)
  decreases |r.locations| - j
{
  if j < k {
    CheckLocationsPass(r, j + 1, k);
  }
}

lemma CheckLocationsFailNotOk(r: TRow, j: nat)
  requires j <= |r.locations|
  ensures CheckLocations(r, j).Fail? ==> !CheckLocations(r, j).outcome.Ok?
  decreases |r.locations| - j
{
  if j < |r.locations| {
    CheckLocationsFailNotOk(r, j + 1);
  }
}

lemma CheckRowsOk(rows: seq<TRow>, names: seq<Str>, i: nat, k: int)
  requires i <= |rows|
  requires CheckRows(rows, names, i).Ok?
  requires i <= k < |rows|
  ensures rows[k].source.Some? && |rows[k].locations| > 0 && Count(names, rows[k].name) <= 1
  ensures CheckLocations(rows[k], 0) == Pass
  decreases |rows| - i
{
  CheckLocationsFailNotOk(rows[i], 0);
  if i < k {
    CheckRowsOk(rows, names, i + 1, k);
  }
}

lemma CheckRowsReturnsRows(rows: seq<TRow>, names: seq<Str>, i: nat)
  requires i <= |rows|
  requires CheckRows(rows, names, i).Ok?
  ensures CheckRows(rows, names, i).rows == rows
  decreases |rows| - i
{
  if i < |rows| {
    CheckLocationsFailNotOk(rows[i], 0);
    CheckRowsReturnsRows(rows, names, i + 1);
  }
}

lemma CountAtLeastOne(s: seq<Str>, x: Str, j: int)
  requires 0 <= j < |s| && s[j] == x
  ensures Count(s, x) >= 1
  decreases |s|
{
  if j > 0 {
    CountAtLeastOne(s[1..], x, j - 1);
  }
}

lemma CountAtLeastTwo(s: seq<Str>, x: Str, i: int, j: int)
  requires 0 <= i < j < |s| && s[i] == x && s[j] == x
  ensures Count(s, x) >= 2
  decreases |s|
{
  if i == 0 {
    CountAtLeastOne(s[1..], x, j - 1);
  } else {
    CountAtLeastTwo(s[1..], x, i - 1, j - 1);
  }
}

lemma LoadTableOkParsed(text: Str) returns (rows: seq<TRow>)
  requires LoadTable(text).Ok?
  ensures ParseLines(Lines(text), 0, [], false, false) == Parsed(rows)
  ensures CheckRows(rows, Names(rows), 0).Ok?
  ensures LoadTable(text).rows == rows
{
  rows := ParseLines(Lines(text), 0, [], false, false).rows;
  CheckRowsReturnsRows(rows, Names(rows), 0);
}

lemma ParseFailsBeforeOther(lines: seq<Str>, i: nat, rows: seq<TRow>, hasRow: bool, hasLoc: bool, k: int)
  requires i <= |lines|
  requires hasRow ==> |rows| > 0
  requires hasLoc ==> hasRow && |rows[|rows| - 1].locations| > 0
  requires i <= k < |lines| && Classify(lines[k]).Other?
  ensures ParseLines(lines, i, rows, hasRow, hasLoc).ParseErr?
  decreases |lines| - i
{
  if i < k {
    var line := lines[i];
    match Classify(line)
    case Skip =>
      ParseFailsBeforeOther(lines, i + 1, rows, hasRow, hasLoc, k);
    case Name(v) =>
      if Scalar(v).SOk? {
        ParseFailsBeforeOther(lines, i + 1, rows + [TRow(Scalar(v).value, None, [])], true, false, k);
      }
    case Field(key, v) =>
      if hasRow && Scalar(v).SOk? {
        var row := rows[|rows| - 1];
        var row' := if key == SourceKey then row.(source := Some(Scalar(v).value)) else row;
        ParseFailsBeforeOther(lines, i + 1, rows[..|rows| - 1] + [row'], hasRow, hasLoc, k);
      }
    case LocationsHeader =>
      if hasRow {
        ParseFailsBeforeOther(lines, i + 1, rows, hasRow, hasLoc, k);
      }
    case File(v) =>
      if hasRow && Scalar(v).SOk? {
        var row := rows[|rows| - 1];
        var row' := row.(locations := row.locations + [TLocation(Scalar(v).value, None)]);
        ParseFailsBeforeOther(lines, i + 1, rows[..|rows| - 1] + [row'], hasRow, true, k);
      }
    case Regex(v) =>
      if hasLoc && Scalar(v).SOk? {
        var row := rows[|rows| - 1];
        var locs := row.locations;
        var loc' := locs[|locs| - 1].(regex := Some(Scalar(v).value));
        var row' := row.(locations := locs[..|locs| - 1] + [loc']);
        ParseFailsBeforeOther(lines, i + 1, rows[..|rows| - 1] + [row'], hasRow, hasLoc, k);
      }
    case Other =>
  }
}

// ---------------------------------------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------------------------------------

// Citation: test `test_the_table_refuses_a_malformed_row` (cases "needs a source", "has no regex", "exactly one group").
lemma P1_RowsAreComplete(text: Str, r: TRow, l: TLocation)
  requires LoadTable(text).Ok? && r in LoadTable(text).rows && l in r.locations
  ensures r.source.Some? && |r.locations| > 0 && l.regex.Some? && GroupCount(l.regex.value) == Some(1)
{
  var rows := LoadTableOkParsed(text);
  var k :| 0 <= k < |rows| && rows[k] == r;
  CheckRowsOk(rows, Names(rows), 0, k);
  var m :| 0 <= m < |r.locations| && r.locations[m] == l;
  CheckLocationsPass(r, 0, m);
}

// Citation: same test, case `ROW + ROW` "used twice".
lemma P2_NamesDistinct(text: Str, i: int, j: int)
  requires LoadTable(text).Ok? && 0 <= i < j < |LoadTable(text).rows|
  ensures LoadTable(text).rows[i].name != LoadTable(text).rows[j].name
{
  var rows := LoadTableOkParsed(text);
  var names := Names(rows);
  if rows[i].name == rows[j].name {
    CheckRowsOk(rows, names, 0, i);
    CountAtLeastTwo(names, rows[i].name, i, j);
    assert false;
  }
}

// Citation: same test, case "not in the table's format".
lemma P3_UnknownLineRefused(text: Str, k: int)
  requires 0 <= k < |Lines(text)| && Classify(Lines(text)[k]).Other?
  ensures !LoadTable(text).Ok?
{
  ParseFailsBeforeOther(Lines(text), 0, [], false, false, k);
}
