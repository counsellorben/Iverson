// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T2 S3
// Source: Iverson.Server/Iverson.Api/Grpc/ProtoPayloadHelper.cs `FoldKeys`, `ProtoValueToObject`, and `SerializePayload`'s column set
// Replay base: c79d7de067519edb3b325fbc0aa53332944c6d1c
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/StructSerializer.FoldKeys.dfy

// ---------------------------------------------------------------------------
// Approved axioms (the only trusted declarations)
// ---------------------------------------------------------------------------

function CharToUpperInvariant(c: char): char

type Double

// ---------------------------------------------------------------------------
// Shared vocabulary
// ---------------------------------------------------------------------------

type CsString = s: seq<char> | |s| < 0x8000_0000

datatype Option<T> = None | Some(value: T)

datatype StatusCode = OK | Cancelled | Unknown | InvalidArgument | DeadlineExceeded | NotFound | AlreadyExists | PermissionDenied | ResourceExhausted | FailedPrecondition | Aborted | OutOfRange | Unimplemented | Internal | Unavailable | DataLoss | Unauthenticated

datatype Column = Column(Name: CsString, SqlType: CsString, IsNullable: bool)

// Fields no header of this candidate names are omitted.
datatype Desc = Desc(TypeName: CsString, TableName: CsString, KeyColumn: Column, ScalarColumns: seq<Column>)

function ColumnNames(d: Desc): seq<CsString>
{
  [d.KeyColumn.Name] + seq(|d.ScalarColumns|, i requires 0 <= i < |d.ScalarColumns| => d.ScalarColumns[i].Name)
}

// ---------------------------------------------------------------------------
// Candidate model
// ---------------------------------------------------------------------------

// Google.Protobuf.WellKnownTypes.Value (KindCase None is not representable; see table).
datatype Val = VStr(s: CsString) | VNum(n: Double) | VBool(b: bool) | VNull | VList(items: seq<Val>) | VStruct(fields: seq<(CsString, Val)>)

// The folded Dictionary<string, object?>, in insertion order.
datatype Obj = OStr(s: CsString) | ONum(n: Double) | OBool(b: bool) | ONull | OList(items: seq<Obj>) | ODict(entries: seq<(seq<char>, Obj)>)

// FoldKeys' outcome: the dictionary, or the RpcException it throws (or lets propagate).
datatype FoldResult = Folded(entries: seq<(seq<char>, Obj)>) | Refused(code: StatusCode, detail: seq<char>)

// ProtoValueToObject's outcome: the object, or a propagated RpcException.
datatype ObjResult = Converted(obj: Obj) | Raised(code: StatusCode, detail: seq<char>)

predicate KeyIn(es: seq<(seq<char>, Obj)>, k: seq<char>) { exists j :: 0 <= j < |es| && es[j].0 == k }

// ProtoPayloadHelper.cs:27-28
function UpperFirst(s: CsString): CsString
{
  if |s| == 0 then s else [CharToUpperInvariant(s[0])] + s[1..]
}

// Dictionary<string, object?> indexer set: overwrite the existing key in place, else append.
function DictSet(es: seq<(seq<char>, Obj)>, k: seq<char>, v: Obj): seq<(seq<char>, Obj)>
  decreases |es|
{
  if |es| == 0 then [(k, v)]
  else if es[0].0 == k then [(k, v)] + es[1..]
  else [es[0]] + DictSet(es[1..], k, v)
}

// ProtoPayloadHelper.cs:23-25: the column set SerializePayload builds (ordinal HashSet).
function ColumnSet(schema: Option<Desc>): set<seq<char>>
{
  if schema.None? then {}   // s_noColumns
  else (set c | c in schema.value.ScalarColumns :: c.Name) + {schema.value.KeyColumn.Name}
}

// ProtoPayloadHelper.cs:22-25: SerializePayload up to (excluding) JsonSerializer.Serialize.
function SerializeFold(d: Option<Desc>, fields: seq<(CsString, Val)>): FoldResult
{
  FoldKeys(fields, ColumnSet(d))
}

// ProtoPayloadHelper.cs:30-47
function FoldKeys(fields: seq<(CsString, Val)>, columnNames: set<seq<char>>): FoldResult
  decreases VStruct(fields), 1
{
  FoldLoop(fields, columnNames, 0, map[], [])
}

// ProtoPayloadHelper.cs:34-45: the foreach, one iteration per call.
function FoldLoop(fields: seq<(CsString, Val)>, columnNames: set<seq<char>>, idx: nat,
                  originalNameOf: map<seq<char>, seq<char>>, folded: seq<(seq<char>, Obj)>): FoldResult
  requires idx <= |fields|
  decreases VStruct(fields), 0, |fields| - idx
{
  if idx == |fields| then Folded(folded)
  else
    var key := fields[idx].0;
    var value := fields[idx].1;
    var upper: CsString := if key in columnNames then key else UpperFirst(key);
    if upper in originalNameOf then
      Refused(InvalidArgument,
              "Payload fields '" + originalNameOf[upper] + "' and '" + key + "' both fold to '" + upper + "' after " +
              "case-normalization; rename one of them.")
    else
      var originalNameOf' := originalNameOf[upper := key];
      assert fields[idx] in fields;
      match ProtoValueToObject(value)
      case Raised(c, m) => Refused(c, m)
      case Converted(o) => FoldLoop(fields, columnNames, idx + 1, originalNameOf', DictSet(folded, upper, o))
}

// ProtoPayloadHelper.cs:49-58
function ProtoValueToObject(v: Val): ObjResult
  decreases v, 2
{
  match v
  case VStr(s) => Converted(OStr(s))
  case VNum(n) => Converted(ONum(n))
  case VBool(b) => Converted(OBool(b))
  case VNull => Converted(ONull)
  case VList(items) => ConvertList(items, 0, [])
  case VStruct(fs) =>
    match FoldKeys(fs, {})   // s_noColumns
    case Folded(es) => Converted(ODict(es))
    case Refused(c, m) => Raised(c, m)
}

// ProtoPayloadHelper.cs:55: Values.Select(ProtoValueToObject).ToList(), in order, first throw propagates.
function ConvertList(items: seq<Val>, idx: nat, acc: seq<Obj>): ObjResult
  requires idx <= |items|
  decreases VList(items), 0, |items| - idx
{
  if idx == |items| then Converted(OList(acc))
  else
    assert items[idx] in items;
    match ProtoValueToObject(items[idx])
    case Raised(c, m) => Raised(c, m)
    case Converted(o) => ConvertList(items, idx + 1, acc + [o])
}

// ---------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------

lemma ColumnSetIsColumnNames(d: Desc, k: seq<char>)
  ensures k in ColumnSet(Some(d)) <==> k in ColumnNames(d)
{
  var cn := ColumnNames(d);
  if k in ColumnSet(Some(d)) {
    if k == d.KeyColumn.Name {
      assert cn[0] == k;
    } else {
      var c :| c in d.ScalarColumns && c.Name == k;
      var i :| 0 <= i < |d.ScalarColumns| && d.ScalarColumns[i] == c;
      assert cn[1 + i] == k;
    }
  }
  if k in cn {
    var i :| 0 <= i < |cn| && cn[i] == k;
    if i == 0 {
    } else {
      assert d.ScalarColumns[i - 1] in d.ScalarColumns;
      assert d.ScalarColumns[i - 1].Name == k;
    }
  }
}

lemma DictSetAppendsFreshKey(es: seq<(seq<char>, Obj)>, k: seq<char>, v: Obj)
  requires !KeyIn(es, k)
  ensures DictSet(es, k, v) == es + [(k, v)]
  decreases |es|
{
  if |es| > 0 {
    assert es[0].0 != k;
    DictSetAppendsFreshKey(es[1..], k, v);
  }
}

lemma KeyInAppend(es: seq<(seq<char>, Obj)>, e: (seq<char>, Obj), k: seq<char>)
  ensures KeyIn(es + [e], k) <==> (KeyIn(es, k) || e.0 == k)
{
  var es' := es + [e];
  if KeyIn(es', k) {
    var j :| 0 <= j < |es'| && es'[j].0 == k;
    if j < |es| { assert es[j].0 == k; }
  }
  if KeyIn(es, k) {
    var j :| 0 <= j < |es| && es[j].0 == k;
    assert es'[j].0 == k;
  }
  if e.0 == k {
    assert es'[|es|].0 == k;
  }
}

// The loop's facts: the result extends `folded`, and every remaining field i lands at
// some index under its folded key, holding ProtoValueToObject of its value.
lemma FoldLoopFacts(fields: seq<(CsString, Val)>, columnNames: set<seq<char>>, idx: nat,
                    originalNameOf: map<seq<char>, seq<char>>, folded: seq<(seq<char>, Obj)>)
  requires idx <= |fields|
  requires forall k :: k in originalNameOf <==> KeyIn(folded, k)
  requires FoldLoop(fields, columnNames, idx, originalNameOf, folded).Folded?
  ensures |folded| <= |FoldLoop(fields, columnNames, idx, originalNameOf, folded).entries|
  ensures FoldLoop(fields, columnNames, idx, originalNameOf, folded).entries[..|folded|] == folded
  ensures forall i | idx <= i < |fields| ::
            exists k :: (0 <= k < |FoldLoop(fields, columnNames, idx, originalNameOf, folded).entries| &&
                         FoldLoop(fields, columnNames, idx, originalNameOf, folded).entries[k].0 ==
                           (if fields[i].0 in columnNames then fields[i].0 else UpperFirst(fields[i].0)) &&
                         ProtoValueToObject(fields[i].1) == Converted(FoldLoop(fields, columnNames, idx, originalNameOf, folded).entries[k].1))
  decreases |fields| - idx
{
  var r := FoldLoop(fields, columnNames, idx, originalNameOf, folded).entries;
  if idx < |fields| {
    var key := fields[idx].0;
    var upper: CsString := if key in columnNames then key else UpperFirst(key);
    assert upper !in originalNameOf;
    var o := ProtoValueToObject(fields[idx].1).obj;
    var originalNameOf' := originalNameOf[upper := key];
    assert !KeyIn(folded, upper);
    DictSetAppendsFreshKey(folded, upper, o);
    var folded' := folded + [(upper, o)];
    assert DictSet(folded, upper, o) == folded';
    forall k ensures k in originalNameOf' <==> KeyIn(folded', k) {
      KeyInAppend(folded, (upper, o), k);
    }
    assert r == FoldLoop(fields, columnNames, idx + 1, originalNameOf', folded').entries;
    FoldLoopFacts(fields, columnNames, idx + 1, originalNameOf', folded');
    assert r[..|folded'|] == folded';
    assert r[..|folded|] == r[..|folded'|][..|folded|];
    assert folded'[..|folded|] == folded;
    assert r[..|folded|] == folded;
    assert r[|folded|] == r[..|folded'|][|folded|] == folded'[|folded|] == (upper, o);
    assert fields[idx].0 == key;
    assert ProtoValueToObject(fields[idx].1) == Converted(r[|folded|].1);
  }
}

lemma FoldKeysFacts(fields: seq<(CsString, Val)>, columnNames: set<seq<char>>, i: int)
  requires 0 <= i < |fields|
  requires FoldKeys(fields, columnNames).Folded?
  ensures exists k :: 0 <= k < |FoldKeys(fields, columnNames).entries| &&
            FoldKeys(fields, columnNames).entries[k].0 ==
              (if fields[i].0 in columnNames then fields[i].0 else UpperFirst(fields[i].0)) &&
            ProtoValueToObject(fields[i].1) == Converted(FoldKeys(fields, columnNames).entries[k].1)
{
  FoldLoopFacts(fields, columnNames, 0, map[], []);
}

// ---------------------------------------------------------------------------
// Properties (frozen)
// ---------------------------------------------------------------------------

// Citation: "`ProtoPayloadHelper.FoldKeys` stops upper-casing keys that already equal a column name; other keys keep `UpperFirst`." (spec §2.3)
lemma P1_ExactColumnKeysKeepTheirSpelling_OtherTopLevelKeysAreUpperFirsted(d: Desc, fields: seq<(CsString, Val)>, i: int)
  requires 0 <= i < |fields|
  requires SerializeFold(Some(d), fields).Folded?
  ensures KeyIn(SerializeFold(Some(d), fields).entries,
                if fields[i].0 in ColumnNames(d) then fields[i].0 else UpperFirst(fields[i].0))
{
  ColumnSetIsColumnNames(d, fields[i].0);
  FoldKeysFacts(fields, ColumnSet(Some(d)), i);
}

// Citation: "row.GetProperty("Meta").GetProperty("Url").GetString().Should().Be("nested");" (plan T2 S1, `Post_KeysThatAreNotColumns_AreStillUpperFirstedAtEveryLevel`, generalised)
lemma P2_NestedKeysAreAlwaysUpperFirsted(d: Desc, fields: seq<(CsString, Val)>, i: int, j: int)
  requires 0 <= i < |fields| && fields[i].1.VStruct? && 0 <= j < |fields[i].1.fields|
  requires SerializeFold(Some(d), fields).Folded?
  ensures exists k :: 0 <= k < |SerializeFold(Some(d), fields).entries| &&
    SerializeFold(Some(d), fields).entries[k].0 ==
      (if fields[i].0 in ColumnNames(d) then fields[i].0 else UpperFirst(fields[i].0)) &&
    SerializeFold(Some(d), fields).entries[k].1.ODict? &&
    KeyIn(SerializeFold(Some(d), fields).entries[k].1.entries, UpperFirst(fields[i].1.fields[j].0))
{
  var es := SerializeFold(Some(d), fields).entries;
  ColumnSetIsColumnNames(d, fields[i].0);
  FoldKeysFacts(fields, ColumnSet(Some(d)), i);
  var k :| 0 <= k < |es| &&
           es[k].0 == (if fields[i].0 in ColumnSet(Some(d)) then fields[i].0 else UpperFirst(fields[i].0)) &&
           ProtoValueToObject(fields[i].1) == Converted(es[k].1);
  var nested := fields[i].1.fields;
  assert FoldKeys(nested, {}).Folded?;
  assert es[k].1 == ODict(FoldKeys(nested, {}).entries);
  FoldKeysFacts(nested, {}, j);
}
