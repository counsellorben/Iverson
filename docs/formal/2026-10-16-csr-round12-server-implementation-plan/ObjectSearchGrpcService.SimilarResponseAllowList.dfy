// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T4 S6
// Source: Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs `WriteSimilarResponsesAsync`, lines from `var columnLookup = …` through the per-row `foreach (var kvp in payload)` loop, before `MaskDisallowedFields`
// Replay base: c79d7de067519edb3b325fbc0aa53332944c6d1c
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/ObjectSearchGrpcService.SimilarResponseAllowList.dfy

// ---------------------------------------------------------------------------
// Shared vocabulary (the subset this candidate uses)
// ---------------------------------------------------------------------------

type CsString = s: seq<char> | |s| < 0x8000_0000

datatype Column = Column(Name: CsString, SqlType: CsString, IsNullable: bool)

// Desc keeps only the fields this candidate reads (KeyColumn, ScalarColumns).
datatype Desc = Desc(KeyColumn: Column, ScalarColumns: seq<Column>)

function ColumnNames(d: Desc): seq<CsString>
{
  [d.KeyColumn.Name] + seq(|d.ScalarColumns|, i requires 0 <= i < |d.ScalarColumns| => d.ScalarColumns[i].Name)
}

// ---------------------------------------------------------------------------
// Approved axioms
// ---------------------------------------------------------------------------

function CharToLowerInvariant(c: char): char

type PValue

function ConvertPayloadValue(raw: CsString, sqlType: CsString): PValue

// ---------------------------------------------------------------------------
// Transliteration
// ---------------------------------------------------------------------------

// NamingExtensions.cs:16-17
//   name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..]
function ToCamelCase(name: CsString): CsString
{
  if |name| == 0 then name else [CharToLowerInvariant(name[0])] + name[1..]
}

// ObjectSearchGrpcService.cs:469-470
//   foreach (var col in schema.ScalarColumns) columnLookup[col.Name.ToCamelCase()] = col;
// In code order, later assignments overwriting earlier ones.
function AddScalarColumns(m: map<CsString, Column>, cols: seq<Column>): map<CsString, Column>
  decreases |cols|
{
  if |cols| == 0 then m
  else AddScalarColumns(m[ToCamelCase(cols[0].Name) := cols[0]], cols[1..])
}

// ObjectSearchGrpcService.cs:468-472
function BuildColumnLookup(schema: Desc): map<CsString, Column>
{
  var columnLookup0: map<CsString, Column> := map[];                          // :468
  var columnLookup1 := AddScalarColumns(columnLookup0, schema.ScalarColumns);  // :469-470
  var columnLookup2 := columnLookup1[ToCamelCase(schema.KeyColumn.Name) := schema.KeyColumn]; // :471
  columnLookup2["key" := schema.KeyColumn]                                     // :472
}

// ObjectSearchGrpcService.cs:477-481
//   foreach (var kvp in payload)
//     if (columnLookup.TryGetValue(kvp.Key, out var col))
//       protoStruct.Fields[col.Name] = ConvertPayloadValue(kvp.Value, col.SqlType);
function FillRow(columnLookup: map<CsString, Column>, fields: map<CsString, PValue>,
                 payload: seq<(CsString, CsString)>): map<CsString, PValue>
  decreases |payload|
{
  if |payload| == 0 then fields
  else
    var kvp := payload[0];
    var fields' :=
      if kvp.0 in columnLookup
      then fields[columnLookup[kvp.0].Name := ConvertPayloadValue(kvp.1, columnLookup[kvp.0].SqlType)]
      else fields;
    FillRow(columnLookup, fields', payload[1..])
}

// The Struct one row builds (ObjectSearchGrpcService.cs:468-481; :476 `new Struct()` is map[]).
function ResponseFields(schema: Desc, payload: seq<(CsString, CsString)>): map<CsString, PValue>
{
  FillRow(BuildColumnLookup(schema), map[], payload)
}

// ---------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------

lemma AddScalarColumnsValues(m: map<CsString, Column>, cols: seq<Column>, S: set<CsString>)
  requires forall k :: k in m ==> m[k].Name in S
  requires forall i :: 0 <= i < |cols| ==> cols[i].Name in S
  ensures forall k :: k in AddScalarColumns(m, cols) ==> AddScalarColumns(m, cols)[k].Name in S
  decreases |cols|
{
  if |cols| > 0 {
    var m' := m[ToCamelCase(cols[0].Name) := cols[0]];
    assert forall i :: 0 <= i < |cols[1..]| ==> cols[1..][i] == cols[i + 1];
    AddScalarColumnsValues(m', cols[1..], S);
  }
}

lemma LookupValuesAreColumns(schema: Desc)
  ensures forall k :: k in BuildColumnLookup(schema) ==> BuildColumnLookup(schema)[k].Name in ColumnNames(schema)
{
  var S := set n | n in ColumnNames(schema);
  var names := ColumnNames(schema);
  assert names[0] == schema.KeyColumn.Name;
  assert schema.KeyColumn.Name in S;
  forall i | 0 <= i < |schema.ScalarColumns|
    ensures schema.ScalarColumns[i].Name in S
  {
    assert names[i + 1] == schema.ScalarColumns[i].Name;
  }
  AddScalarColumnsValues(map[], schema.ScalarColumns, S);
}

lemma FillRowKeys(columnLookup: map<CsString, Column>, fields: map<CsString, PValue>,
                  payload: seq<(CsString, CsString)>, S: set<CsString>)
  requires forall k :: k in columnLookup ==> columnLookup[k].Name in S
  requires forall k :: k in fields ==> k in S
  ensures forall k :: k in FillRow(columnLookup, fields, payload) ==> k in S
  decreases |payload|
{
  if |payload| > 0 {
    var kvp := payload[0];
    var fields' :=
      if kvp.0 in columnLookup
      then fields[columnLookup[kvp.0].Name := ConvertPayloadValue(kvp.1, columnLookup[kvp.0].SqlType)]
      else fields;
    FillRowKeys(columnLookup, fields', payload[1..], S);
  }
}

lemma FillRowMonotone(columnLookup: map<CsString, Column>, fields: map<CsString, PValue>,
                      payload: seq<(CsString, CsString)>, k: CsString)
  requires k in fields
  ensures k in FillRow(columnLookup, fields, payload)
  decreases |payload|
{
  if |payload| > 0 {
    var kvp := payload[0];
    var fields' :=
      if kvp.0 in columnLookup
      then fields[columnLookup[kvp.0].Name := ConvertPayloadValue(kvp.1, columnLookup[kvp.0].SqlType)]
      else fields;
    FillRowMonotone(columnLookup, fields', payload[1..], k);
  }
}

lemma FillRowHit(columnLookup: map<CsString, Column>, fields: map<CsString, PValue>,
                 payload: seq<(CsString, CsString)>, i: int)
  requires 0 <= i < |payload| && payload[i].0 in columnLookup
  ensures columnLookup[payload[i].0].Name in FillRow(columnLookup, fields, payload)
  decreases |payload|
{
  var kvp := payload[0];
  var fields' :=
    if kvp.0 in columnLookup
    then fields[columnLookup[kvp.0].Name := ConvertPayloadValue(kvp.1, columnLookup[kvp.0].SqlType)]
    else fields;
  if i == 0 {
    FillRowMonotone(columnLookup, fields', payload[1..], columnLookup[payload[0].0].Name);
  } else {
    assert payload[1..][i - 1] == payload[i];
    FillRowHit(columnLookup, fields', payload[1..], i - 1);
  }
}

// ---------------------------------------------------------------------------
// Properties (frozen)
// ---------------------------------------------------------------------------

// Citation: "SearchSimilar builds its response objects from an **allow-list**: the descriptor's columns plus `key`. Every other Qdrant payload key is dropped." (spec §3.1)
lemma P1_ResponsesCarryOnlyDescriptorColumns(schema: Desc, payload: seq<(CsString, CsString)>)
  ensures forall k :: k in ResponseFields(schema, payload) ==> k in ColumnNames(schema)
{
  var S := set n | n in ColumnNames(schema);
  LookupValuesAreColumns(schema);
  FillRowKeys(BuildColumnLookup(schema), map[], payload, S);
}

// Citation: "the descriptor's columns plus `key`" (spec §3.1) and "written[0].Data.Fields.Keys.Should().BeEquivalentTo(["Id", "Title"]);" (plan T4 S1b, `SearchSimilar_StreamsOnlyDescriptorColumnsAndTheKey`)
lemma P2_ThePayloadKeyIsAnsweredAsTheKeyColumn(schema: Desc, payload: seq<(CsString, CsString)>, i: int)
  requires 0 <= i < |payload| && payload[i].0 == "key"
  ensures schema.KeyColumn.Name in ResponseFields(schema, payload)
{
  var lookup := BuildColumnLookup(schema);
  assert "key" in lookup && lookup["key"] == schema.KeyColumn;
  FillRowHit(lookup, map[], payload, i);
}
