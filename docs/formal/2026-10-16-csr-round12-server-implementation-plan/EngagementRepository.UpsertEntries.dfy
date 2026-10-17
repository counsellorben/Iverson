// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T3 S3
// Source: Iverson.Server/Iverson.StarRocks/EngagementRepository.cs `UpsertEntries`
// Replay base: c79d7de067519edb3b325fbc0aa53332944c6d1c
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/EngagementRepository.UpsertEntries.dfy

// ---- Shared vocabulary ----

type CsString = s: seq<char> | |s| < 0x8000_0000

datatype Option<T> = None | Some(value: T)
datatype Result<T> = Ok(value: T) | Threw

// ---- Candidate model ----

// System.Text.Json.JsonValueKind
datatype JsonKind = JObject | JArray | JString | JNumber | JTrue | JFalse | JNull | JUndefined
// System.Text.Json.JsonElement: its ValueKind and its raw text
datatype JsonEl = JsonEl(kind: JsonKind, raw: CsString)

// JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson):
// ParsedObject = a dictionary, its entries in enumeration order;
// ParsedNull   = the JSON literal null (Deserialize returns null);
// ParseFailed  = a JsonException.
datatype Parsed = ParsedObject(entries: seq<(CsString, JsonEl)>) | ParsedNull | ParseFailed

// EngagementColumnSchema(Name, SrType, IsNullable)
datatype EngCol = EngCol(Name: CsString, SqlType: CsString, IsNullable: bool)
// EngagementTableSchema(TableName, KeyColumn, Columns); SortKey is unused here and omitted
datatype EngTable = EngTable(TableName: CsString, KeyColumn: EngCol, Columns: seq<EngCol>)

// Approved axiom: the library deserializer.
function DeserializeToDictionary(json: CsString): Parsed

// schema.Columns.Select(c => c.Name)
function ColumnNameSet(cols: seq<EngCol>): set<CsString>
{
  set i | 0 <= i < |cols| :: cols[i].Name
}

// .Append(schema.KeyColumn.Name).ToHashSet(StringComparer.Ordinal)
// Ordinal string equality is UTF-16 code-unit sequence equality, so a Dafny set of seq<char>.
function KnownCols(s: EngTable): set<CsString>
{
  ColumnNameSet(s.Columns) + {s.KeyColumn.Name}
}

// The Where predicate.
predicate Keep(knownCols: set<CsString>, kv: (CsString, JsonEl))
{
  kv.0 in knownCols
  && kv.1.kind != JObject
  && kv.1.kind != JUndefined
}

// row.Where(...).ToList(): an order-preserving filter.
function FilterEntries(knownCols: set<CsString>, row: seq<(CsString, JsonEl)>): seq<(CsString, JsonEl)>
{
  if |row| == 0 then []
  else if Keep(knownCols, row[0]) then [row[0]] + FilterEntries(knownCols, row[1..])
  else FilterEntries(knownCols, row[1..])
}

function UpsertEntries(s: EngTable, json: CsString): Result<seq<(CsString, JsonEl)>>
{
  // var row = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson)
  //     ?? new Dictionary<string, JsonElement>();
  var parsed := DeserializeToDictionary(json);
  if parsed.ParseFailed? then Threw
  else
    var row := if parsed.ParsedNull? then [] else parsed.entries;
    // var knownCols = ...ToHashSet(StringComparer.Ordinal);
    var knownCols := KnownCols(s);
    // return row.Where(...).ToList();
    Ok(FilterEntries(knownCols, row))
}

// ---- Helper lemmas ----

lemma FilterEntriesMembership(knownCols: set<CsString>, row: seq<(CsString, JsonEl)>, e: (CsString, JsonEl))
  ensures e in FilterEntries(knownCols, row) <==> e in row && Keep(knownCols, e)
  decreases |row|
{
  if |row| > 0 {
    FilterEntriesMembership(knownCols, row[1..], e);
    assert row == [row[0]] + row[1..];
  }
}

lemma KnownColsMembership(s: EngTable, n: CsString)
  ensures n in KnownCols(s) <==> (n == s.KeyColumn.Name || exists c :: c in s.Columns && c.Name == n)
{
  if exists c :: c in s.Columns && c.Name == n {
    var c :| c in s.Columns && c.Name == n;
    var i :| 0 <= i < |s.Columns| && s.Columns[i] == c;
    assert n in ColumnNameSet(s.Columns);
  }
  if n in ColumnNameSet(s.Columns) {
    var i :| 0 <= i < |s.Columns| && s.Columns[i].Name == n;
    assert s.Columns[i] in s.Columns;
  }
}

// ---- Properties ----

// Citation: "`EngagementRepository.UpsertAsync` selects entries by exact column name (ordinal)." (spec §2.3) and "entries.Select(e => e.Key).Should().Equal("Id");" (plan T3 S1a, `AKeyThatMatchesAColumnOnlyIgnoringCase_IsNotSelected`)
lemma P1_OnlyExactColumnKeysWithNonObjectValuesAreSelected(s: EngTable, json: CsString)
  requires DeserializeToDictionary(json).ParsedObject?
  ensures UpsertEntries(s, json).Ok?
  ensures forall e :: e in UpsertEntries(s, json).value <==>
    e in DeserializeToDictionary(json).entries &&
    (e.0 == s.KeyColumn.Name || exists c :: c in s.Columns && c.Name == e.0) &&
    e.1.kind != JObject && e.1.kind != JUndefined
{
  var row := DeserializeToDictionary(json).entries;
  forall e: (CsString, JsonEl)
    ensures e in UpsertEntries(s, json).value <==>
      e in row &&
      (e.0 == s.KeyColumn.Name || exists c :: c in s.Columns && c.Name == e.0) &&
      e.1.kind != JObject && e.1.kind != JUndefined
  {
    FilterEntriesMembership(KnownCols(s), row, e);
    KnownColsMembership(s, e.0);
  }
}
