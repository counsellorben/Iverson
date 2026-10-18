// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T1 S3
// Source: Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs `NormalizeDeclaredNames`
// Code ref: f7a0842fc0e707359e777ddf44f7c2e23050c968
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/SchemaBuilder.NormalizeDeclaredNames.dfy

// ---------------------------------------------------------------------------------------------
// Shared vocabulary
// ---------------------------------------------------------------------------------------------

type CsString = s: seq<char> | |s| < 0x8000_0000

// AuthorizationRules.OwnerField: both the primary-constructor initializer and the init accessor
// map "" to null (SchemaDescriptor.cs:162-166), so no AuthorizationRules value ever holds "".
type NonEmptyCsString = s: CsString | |s| > 0 witness "a"

datatype Option<T> = None | Some(value: T)
datatype Result<T> = Ok(value: T) | Threw

// Approved axiom.
function OrdinalUpper(s: seq<char>): seq<char>
  ensures |OrdinalUpper(s)| == |s|

predicate EqIC(a: seq<char>, b: seq<char>) { OrdinalUpper(a) == OrdinalUpper(b) }

// SchemaDescriptor.IsTenantColumn (SchemaDescriptor.cs:20-21)
predicate IsTenantColumn(n: seq<char>) { EqIC(n, "__TenantId") }

datatype Column = Column(Name: CsString, SqlType: CsString, IsNullable: bool)
datatype RelationKind = OneToOne | OneToMany | ManyToOne | ManyToMany
datatype Relation = Relation(PropertyName: CsString, Kind: RelationKind, RelatedTypeName: CsString, ForeignKey: CsString)
datatype RowPermission = RowPermission(Role: CsString, CanReadAll: bool, CanWriteAll: bool, CanDeleteAll: bool)
datatype FieldPermission = FieldPermission(FieldName: CsString, ReadableRoles: seq<CsString>, WritableRoles: seq<CsString>)
datatype AuthRules = AuthRules(OwnerField: Option<NonEmptyCsString>, RowPermissions: seq<RowPermission>, FieldPermissions: seq<FieldPermission>)
datatype VectorField = VectorField(PropertyName: CsString)
datatype ChunkField = ChunkField(PropertyName: CsString)
datatype EnrichmentKind = Summary | Keywords | Extracted
datatype Target = Target(ColumnName: CsString, Kind: EnrichmentKind, Hint: Option<CsString>)
datatype Desc = Desc(TypeName: CsString, TableName: CsString, KeyColumn: Column, ScalarColumns: seq<Column>,
                     VectorFields: seq<VectorField>, ChunkFields: seq<ChunkField>, Relations: seq<Relation>,
                     Authorization: Option<AuthRules>, TenantColumn: CsString, PopularitySignalColumn: Option<CsString>,
                     OwnerTenantId: Option<CsString>, EnrichmentTargets: seq<Target>, HasDocumentTemplate: bool)

// descriptor.ScalarColumns.Select(c => c.Name)
function SelectName(cs: seq<Column>): seq<CsString>
{
  seq(|cs|, i requires 0 <= i < |cs| => cs[i].Name)
}

// .Prepend(x)
function Prepend(xs: seq<CsString>, x: CsString): seq<CsString> { [x] + xs }

function ColumnNames(d: Desc): seq<CsString> { Prepend(SelectName(d.ScalarColumns), d.KeyColumn.Name) }

function Spellings(d: Desc, n: seq<char>): set<CsString> { set c | c in ColumnNames(d) && !IsTenantColumn(c) && EqIC(c, n) }

predicate Renamed(d: Desc, from: seq<char>, to: seq<char>) { if |Spellings(d, from)| == 1 then to in Spellings(d, from) else to == from }

// ---------------------------------------------------------------------------------------------
// LINQ / BCL models used by NormalizeDeclaredNames
// ---------------------------------------------------------------------------------------------

// A Dictionary<string, List<string>> with StringComparer.OrdinalIgnoreCase, as its entries in
// insertion order (enumeration order of a Dictionary with no removals): Key is the key the entry
// was created under; Elements is the List<string> value.
datatype Grouping = Grouping(Key: CsString, Elements: seq<CsString>)

// Dictionary.TryGetValue under OrdinalIgnoreCase over the entries.
function FindGrouping(gs: seq<Grouping>, x: CsString, i: nat): Option<nat>
  requires i <= |gs|
  decreases |gs| - i
  ensures var r := FindGrouping(gs, x, i);
    (r.Some? ==> i <= r.value < |gs| && EqIC(gs[r.value].Key, x)) &&
    (r.None? ==> forall k :: i <= k < |gs| ==> !EqIC(gs[k].Key, x))
{
  if i == |gs| then None
  else if EqIC(gs[i].Key, x) then Some(i)
  else FindGrouping(gs, x, i + 1)
}

// .Where(kv => kv.Value.Count == 1)
function WhereCountIsOne(gs: seq<Grouping>): seq<Grouping>
  ensures forall g :: g in WhereCountIsOne(gs) <==> g in gs && |g.Elements| == 1
{
  if gs == [] then []
  else
    assert forall g :: g in gs <==> g == gs[0] || g in gs[1..];
    (if |gs[0].Elements| == 1 then [gs[0]] else []) + WhereCountIsOne(gs[1..])
}

// List<string> indexer kv.Value[0]: ArgumentOutOfRangeException on an empty list.
function Index0(es: seq<CsString>): Result<CsString>
{
  if |es| > 0 then Ok(es[0]) else Threw
}

// A Dictionary<string, string> with StringComparer.OrdinalIgnoreCase, as its entries.
datatype Entry = Entry(Key: CsString, Value: CsString)

predicate ContainsKeyIC(dict: seq<Entry>, k: CsString)
{
  exists j :: 0 <= j < |dict| && EqIC(dict[j].Key, k)
}

// .ToDictionary(kv => kv.Key, kv => kv.Value[0], StringComparer.OrdinalIgnoreCase):
// key selector, element selector, then Dictionary.Add (ArgumentException on a duplicate key).
function ToDictionaryFrom(dict: seq<Entry>, gs: seq<Grouping>): Result<seq<Entry>>
  decreases |gs|
{
  if gs == [] then Ok(dict)
  else
    var key := gs[0].Key;
    var element := Index0(gs[0].Elements);
    if element.Threw? then Threw
    else if ContainsKeyIC(dict, key) then Threw
    else ToDictionaryFrom(dict + [Entry(key, element.value)], gs[1..])
}

// Dictionary.TryGetValue under OrdinalIgnoreCase.
function FindEntry(dict: seq<Entry>, name: CsString, j: nat): Option<nat>
  requires j <= |dict|
  decreases |dict| - j
  ensures var r := FindEntry(dict, name, j);
    (r.Some? ==> j <= r.value < |dict| && EqIC(dict[r.value].Key, name)) &&
    (r.None? ==> forall k :: j <= k < |dict| ==> !EqIC(dict[k].Key, name))
{
  if j == |dict| then None
  else if EqIC(dict[j].Key, name) then Some(j)
  else FindEntry(dict, name, j + 1)
}

// ---------------------------------------------------------------------------------------------
// Transliteration: SchemaBuilder.ColumnSpellingsByName (SchemaBuilder.cs:311-323)
// ---------------------------------------------------------------------------------------------

// if (!columns.TryGetValue(name, out var spellings))
//     columns[name] = spellings = [];
// Returns the dictionary and the index of the entry `spellings` refers to.
function TryGetOrAddEmpty(columns: seq<Grouping>, name: CsString): (r: (seq<Grouping>, nat))
  ensures r.1 < |r.0|
{
  match FindGrouping(columns, name, 0)
  case Some(i) => (columns, i)
  case None => (columns + [Grouping(name, [])], |columns|)
}

// Loop body after the tenant check:
// if (!columns.TryGetValue(name, out var spellings)) columns[name] = spellings = [];
// if (!spellings.Contains(name, StringComparer.Ordinal)) spellings.Add(name);
function AddSpelling(columns: seq<Grouping>, name: CsString): seq<Grouping>
{
  var r := TryGetOrAddEmpty(columns, name);
  var cols := r.0;
  var i := r.1;
  if name in cols[i].Elements then cols
  else cols[i := cols[i].(Elements := cols[i].Elements + [name])]
}

// foreach (var name in xs) { if (SchemaDescriptor.IsTenantColumn(name)) continue; ... }
function ByNameFrom(columns: seq<Grouping>, xs: seq<CsString>): seq<Grouping>
  decreases |xs|
{
  if xs == [] then columns
  else if IsTenantColumn(xs[0]) then ByNameFrom(columns, xs[1..])
  else ByNameFrom(AddSpelling(columns, xs[0]), xs[1..])
}

// var columns = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
// foreach (var name in descriptor.ScalarColumns.Select(c => c.Name).Prepend(descriptor.KeyColumn.Name)) ...
// return columns;
function ColumnSpellingsByName(d: Desc): seq<Grouping>
{
  ByNameFrom([], Prepend(SelectName(d.ScalarColumns), d.KeyColumn.Name))
}

// ---------------------------------------------------------------------------------------------
// Transliteration: SchemaBuilder.NormalizeDeclaredNames (SchemaBuilder.cs:279-302)
// ---------------------------------------------------------------------------------------------

// string ColumnSpelling(string name) => columnSpelling.TryGetValue(name, out var column) ? column : name;
function ColumnSpelling(columnSpelling: seq<Entry>, name: CsString): CsString
{
  match FindEntry(columnSpelling, name, 0)
  case Some(k) => columnSpelling[k].Value
  case None => name
}

// descriptor.Relations.Select(r => r.Kind == OneToMany ? r : r with { ForeignKey = ColumnSpelling(r.ForeignKey) }).ToList()
function SelectRelations(columnSpelling: seq<Entry>, rs: seq<Relation>): seq<Relation>
{
  seq(|rs|, i requires 0 <= i < |rs| =>
    if rs[i].Kind == OneToMany then rs[i] else rs[i].(ForeignKey := ColumnSpelling(columnSpelling, rs[i].ForeignKey)))
}

// authorization.FieldPermissions.Select(fp => fp with { FieldName = ColumnSpelling(fp.FieldName) }).ToList()
function SelectFieldPermissions(columnSpelling: seq<Entry>, fps: seq<FieldPermission>): seq<FieldPermission>
{
  seq(|fps|, i requires 0 <= i < |fps| => fps[i].(FieldName := ColumnSpelling(columnSpelling, fps[i].FieldName)))
}

// AuthorizationRules.OwnerField init accessor: field = string.IsNullOrEmpty(value) ? null : value
function InitOwnerField(value: Option<CsString>): Option<NonEmptyCsString>
{
  if value.None? || |value.value| == 0 then None
  else var field: NonEmptyCsString := value.value; Some(field)
}

function NormalizeDeclaredNames(d: Desc): Result<Desc>
{
  var columnSpellingR :=
    ToDictionaryFrom([],
      WhereCountIsOne(
        ColumnSpellingsByName(d)));
  if columnSpellingR.Threw? then Threw
  else
    var columnSpelling := columnSpellingR.value;
    Ok(d.(
      Relations := SelectRelations(columnSpelling, d.Relations),
      Authorization :=
        (if d.Authorization.None? then None
         else
           var authorization := d.Authorization.value;
           Some(authorization.(
             OwnerField := InitOwnerField(
               if authorization.OwnerField.None? then None
               else Some(ColumnSpelling(columnSpelling, authorization.OwnerField.value))),
             FieldPermissions := SelectFieldPermissions(columnSpelling, authorization.FieldPermissions))))))
}

// ---------------------------------------------------------------------------------------------
// Proof helpers
// ---------------------------------------------------------------------------------------------

predicate NoDups<T(==)>(xs: seq<T>) { forall i, j :: 0 <= i < j < |xs| ==> xs[i] != xs[j] }

// The non-tenant names, in order, with duplicates: the names the loop does not `continue` past.
function WhereNotTenant(xs: seq<CsString>): seq<CsString>
  ensures forall x :: x in WhereNotTenant(xs) <==> x in xs && !IsTenantColumn(x)
{
  if xs == [] then []
  else
    assert forall x :: x in xs <==> x == xs[0] || x in xs[1..];
    (if !IsTenantColumn(xs[0]) then [xs[0]] else []) + WhereNotTenant(xs[1..])
}

function W(d: Desc): seq<CsString> { WhereNotTenant(ColumnNames(d)) }
function Groups(d: Desc): seq<Grouping> { ColumnSpellingsByName(d) }
function DictR(d: Desc): Result<seq<Entry>> { ToDictionaryFrom([], WhereCountIsOne(Groups(d))) }

predicate KeysPairwise(gs: seq<Grouping>)
{
  forall i, j :: 0 <= i < j < |gs| ==> !EqIC(gs[i].Key, gs[j].Key)
}

predicate GroupInv(gs: seq<Grouping>, P: seq<CsString>)
{
  KeysPairwise(gs) &&
  (forall i :: 0 <= i < |gs| ==> |gs[i].Elements| > 0 && NoDups(gs[i].Elements)) &&
  (forall i, x {:trigger x in gs[i].Elements} :: 0 <= i < |gs| && x in gs[i].Elements ==> EqIC(x, gs[i].Key) && x in P) &&
  (forall x :: x in P ==> exists i :: 0 <= i < |gs| && x in gs[i].Elements)
}

lemma InvElem(gs: seq<Grouping>, P: seq<CsString>, i: int, x: CsString)
  requires GroupInv(gs, P)
  requires 0 <= i < |gs| && x in gs[i].Elements
  ensures EqIC(x, gs[i].Key) && x in P
{
}

// Proof-only: appending a spelling not yet present.
function AddToGrouping(gs: seq<Grouping>, x: CsString): seq<Grouping>
{
  match FindGrouping(gs, x, 0)
  case Some(i) => gs[i := gs[i].(Elements := gs[i].Elements + [x])]
  case None => gs + [Grouping(x, [x])]
}

lemma AddInv(gs: seq<Grouping>, P: seq<CsString>, x: CsString)
  requires GroupInv(gs, P)
  requires x !in P
  ensures GroupInv(AddToGrouping(gs, x), P + [x])
{
  var gs' := AddToGrouping(gs, x);
  match FindGrouping(gs, x, 0)
  case Some(i) =>
    assert gs' == gs[i := gs[i].(Elements := gs[i].Elements + [x])];
    assert x !in gs[i].Elements;
    forall k, y | 0 <= k < |gs'| && y in gs'[k].Elements
      ensures EqIC(y, gs'[k].Key) && y in P + [x]
    {
      if k == i {
        assert gs'[k].Key == gs[i].Key;
        assert gs'[k].Elements == gs[i].Elements + [x];
        if y in gs[i].Elements {
          assert EqIC(y, gs[i].Key) && y in P;
        } else {
          assert y == x;
          assert EqIC(gs[i].Key, x);
        }
      } else {
        assert gs'[k] == gs[k];
        assert EqIC(y, gs[k].Key) && y in P;
      }
    }
    forall y | y in P + [x]
      ensures exists k :: 0 <= k < |gs'| && y in gs'[k].Elements
    {
      if y == x {
        assert y in gs'[i].Elements;
      } else {
        assert y in P;
        var k :| 0 <= k < |gs| && y in gs[k].Elements;
        assert y in gs'[k].Elements;
      }
    }
    forall k | 0 <= k < |gs'|
      ensures |gs'[k].Elements| > 0 && NoDups(gs'[k].Elements)
    {
      if k == i {
        var es := gs[i].Elements;
        assert gs'[k].Elements == es + [x];
        forall a, b | 0 <= a < b < |es + [x]| ensures (es + [x])[a] != (es + [x])[b] {
          if b == |es| {
            assert (es + [x])[a] == es[a];
            assert es[a] in es;
          }
        }
      }
    }
  case None =>
    assert gs' == gs + [Grouping(x, [x])];
    forall k, y | 0 <= k < |gs'| && y in gs'[k].Elements
      ensures EqIC(y, gs'[k].Key) && y in P + [x]
    {
      if k < |gs| {
        assert gs'[k] == gs[k];
        assert EqIC(y, gs[k].Key) && y in P;
      } else {
        assert gs'[k] == Grouping(x, [x]);
        assert y == x;
        assert EqIC(x, x);
      }
    }
    forall y | y in P + [x]
      ensures exists k :: 0 <= k < |gs'| && y in gs'[k].Elements
    {
      if y == x {
        assert y in gs'[|gs|].Elements;
      } else {
        assert y in P;
        var k :| 0 <= k < |gs| && y in gs[k].Elements;
        assert gs'[k] == gs[k];
      }
    }
}

// The built loop body is a no-op on a spelling already seen, and AddToGrouping otherwise.
lemma AddSpellingEq(gs: seq<Grouping>, P: seq<CsString>, x: CsString)
  requires GroupInv(gs, P)
  ensures x in P ==> AddSpelling(gs, x) == gs
  ensures x !in P ==> AddSpelling(gs, x) == AddToGrouping(gs, x)
{
  match FindGrouping(gs, x, 0)
  case Some(i) =>
    assert TryGetOrAddEmpty(gs, x) == (gs, i);
    if x in P {
      var k :| 0 <= k < |gs| && x in gs[k].Elements;
      InvElem(gs, P, k, x);
      assert EqIC(gs[k].Key, gs[i].Key);
      KeysUnique(gs, k, i);
    } else {
      if x in gs[i].Elements {
        InvElem(gs, P, i, x);
      }
      assert x !in gs[i].Elements;
      assert AddSpelling(gs, x) == gs[i := gs[i].(Elements := gs[i].Elements + [x])];
      assert AddToGrouping(gs, x) == gs[i := gs[i].(Elements := gs[i].Elements + [x])];
    }
  case None =>
    assert TryGetOrAddEmpty(gs, x) == (gs + [Grouping(x, [])], |gs|);
    forall k | 0 <= k < |gs| && x in gs[k].Elements ensures EqIC(gs[k].Key, x) {
      InvElem(gs, P, k, x);
    }
    // No key matches x, so no entry holds x, so x was never seen.
    assert x !in P;
    {
      var cols := gs + [Grouping(x, [])];
      assert cols[|gs|] == Grouping(x, []);
      assert x !in cols[|gs|].Elements;
      assert cols[|gs|].(Elements := cols[|gs|].Elements + [x]) == Grouping(x, [x]);
      assert AddSpelling(gs, x) == cols[|gs| := Grouping(x, [x])];
      assert cols[|gs| := Grouping(x, [x])] == gs + [Grouping(x, [x])];
      assert AddToGrouping(gs, x) == gs + [Grouping(x, [x])];
    }
}

lemma ByNameFromInv(gs: seq<Grouping>, P: seq<CsString>, xs: seq<CsString>)
  requires GroupInv(gs, P)
  ensures GroupInv(ByNameFrom(gs, xs), P + WhereNotTenant(xs))
  decreases |xs|
{
  if xs == [] {
    assert P + WhereNotTenant(xs) == P;
  } else {
    var x := xs[0];
    if IsTenantColumn(x) {
      assert WhereNotTenant(xs) == WhereNotTenant(xs[1..]);
      ByNameFromInv(gs, P, xs[1..]);
    } else {
      assert WhereNotTenant(xs) == [x] + WhereNotTenant(xs[1..]);
      assert P + WhereNotTenant(xs) == (P + [x]) + WhereNotTenant(xs[1..]);
      AddSpellingEq(gs, P, x);
      if x in P {
        assert forall y :: y in P + [x] <==> y in P;
        assert GroupInv(gs, P + [x]);
      } else {
        AddInv(gs, P, x);
      }
      ByNameFromInv(AddSpelling(gs, x), P + [x], xs[1..]);
    }
  }
}

lemma GroupsInv(d: Desc)
  ensures GroupInv(Groups(d), W(d))
{
  assert GroupInv([], []);
  ByNameFromInv([], [], ColumnNames(d));
  assert [] + WhereNotTenant(ColumnNames(d)) == W(d);
}

lemma WhereKeepsPairwise(gs: seq<Grouping>)
  requires KeysPairwise(gs)
  ensures KeysPairwise(WhereCountIsOne(gs))
  decreases |gs|
{
  if gs != [] {
    WhereKeepsPairwise(gs[1..]);
    var rest := WhereCountIsOne(gs[1..]);
    forall g | g in rest ensures !EqIC(gs[0].Key, g.Key) {
      assert g in gs[1..];
      var k :| 0 <= k < |gs[1..]| && gs[1..][k] == g;
      assert gs[k + 1] == g;
    }
  }
}

lemma ToDictionaryOk(dict: seq<Entry>, gs: seq<Grouping>)
  requires KeysPairwise(gs)
  requires forall g :: g in gs ==> |g.Elements| == 1
  requires forall j, g :: 0 <= j < |dict| && g in gs ==> !EqIC(dict[j].Key, g.Key)
  ensures ToDictionaryFrom(dict, gs).Ok?
  ensures forall e :: e in ToDictionaryFrom(dict, gs).value <==>
    e in dict || exists g :: g in gs && e == Entry(g.Key, g.Elements[0])
  decreases |gs|
{
  if gs != [] {
    var g0 := gs[0];
    assert g0 in gs;
    assert !ContainsKeyIC(dict, g0.Key);
    var dict' := dict + [Entry(g0.Key, g0.Elements[0])];
    forall j, g | 0 <= j < |dict'| && g in gs[1..] ensures !EqIC(dict'[j].Key, g.Key) {
      var k :| 0 <= k < |gs[1..]| && gs[1..][k] == g;
      assert gs[k + 1] == g;
      if j == |dict| {
        assert dict'[j].Key == gs[0].Key;
      } else {
        assert dict'[j] == dict[j];
      }
    }
    forall i, j | 0 <= i < j < |gs[1..]| ensures !EqIC(gs[1..][i].Key, gs[1..][j].Key) {
      assert gs[1..][i] == gs[i + 1] && gs[1..][j] == gs[j + 1];
    }
    ToDictionaryOk(dict', gs[1..]);
    assert forall g :: g in gs <==> g == g0 || g in gs[1..];
  }
}

lemma DictFacts(d: Desc)
  ensures GroupInv(Groups(d), W(d))
  ensures DictR(d).Ok?
  ensures forall e :: e in DictR(d).value <==>
    exists g :: g in Groups(d) && |g.Elements| == 1 && e == Entry(g.Key, g.Elements[0])
{
  GroupsInv(d);
  WhereKeepsPairwise(Groups(d));
  ToDictionaryOk([], WhereCountIsOne(Groups(d)));
}

lemma NormalizeUsesDict(d: Desc)
  ensures DictR(d).Ok? ==> NormalizeDeclaredNames(d).Ok?
  ensures NormalizeDeclaredNames(d).Ok? ==> (DictR(d).Ok? &&
    var n := NormalizeDeclaredNames(d).value;
    var dict := DictR(d).value;
    n.Relations == SelectRelations(dict, d.Relations) &&
    n.Authorization.Some? == d.Authorization.Some? &&
    (d.Authorization.Some? ==>
      n.Authorization.value.OwnerField == InitOwnerField(
        if d.Authorization.value.OwnerField.None? then None
        else Some(ColumnSpelling(dict, d.Authorization.value.OwnerField.value))) &&
      n.Authorization.value.FieldPermissions == SelectFieldPermissions(dict, d.Authorization.value.FieldPermissions)))
{
}

lemma CardAtLeastTwo<T>(s: set<T>, a: T, b: T)
  requires a in s && b in s && a != b
  ensures |s| != 1
{
  var r := s - {a};
  assert b in r;
  assert |r| == |s| - 1;
  assert r != {};
}

lemma KeysUnique(gs: seq<Grouping>, i: int, j: int)
  requires KeysPairwise(gs)
  requires 0 <= i < |gs| && 0 <= j < |gs|
  ensures EqIC(gs[i].Key, gs[j].Key) ==> i == j
{
}

lemma SingletonGroupInDict(d: Desc, hi: int)
  requires 0 <= hi < |Groups(d)|
  ensures DictR(d).Ok?
  ensures |Groups(d)[hi].Elements| == 1 ==>
    Entry(Groups(d)[hi].Key, Groups(d)[hi].Elements[0]) in DictR(d).value
{
  DictFacts(d);
  assert Groups(d)[hi] in Groups(d);
}

lemma EntryInDictIsFound(dict: seq<Entry>, e: Entry, name: CsString)
  ensures e in dict && EqIC(e.Key, name) ==> FindEntry(dict, name, 0).Some?
{
  if e in dict && EqIC(e.Key, name) {
    var k :| 0 <= k < |dict| && dict[k] == e;
    assert EqIC(dict[k].Key, name);
  }
}

// The heart of the proof: ColumnSpelling maps a name to its column's spelling iff exactly one
// non-tenant column matches it ignoring case; otherwise it is the identity.
lemma ColumnSpellingRenames(d: Desc, name: CsString)
  ensures DictR(d).Ok?
  ensures Renamed(d, name, ColumnSpelling(DictR(d).value, name))
{
  DictFacts(d);
  var D := W(d);
  var gs := Groups(d);
  var dict := DictR(d).value;
  match FindEntry(dict, name, 0)
  case Some(k) =>
    var e := dict[k];
    assert e in dict;
    var g :| g in gs && |g.Elements| == 1 && e == Entry(g.Key, g.Elements[0]);
    var gi :| 0 <= gi < |gs| && gs[gi] == g;
    var v := g.Elements[0];
    assert v in gs[gi].Elements;
    InvElem(gs, D, gi, v);
    assert gs[gi].Key == g.Key;
    assert EqIC(g.Key, name);
    assert v in Spellings(d, name);
    forall c | c in Spellings(d, name) ensures c == v {
      assert c in D;
      var hi :| 0 <= hi < |gs| && c in gs[hi].Elements;
      InvElem(gs, D, hi, c);
      assert EqIC(gs[hi].Key, gs[gi].Key);
      KeysUnique(gs, hi, gi);
      assert g.Elements == [v];
    }
    assert Spellings(d, name) == {v};
    assert ColumnSpelling(dict, name) == v;
  case None =>
    assert ColumnSpelling(dict, name) == name;
    if Spellings(d, name) != {} {
      var c :| c in Spellings(d, name);
      assert c in D;
      var hi :| 0 <= hi < |gs| && c in gs[hi].Elements;
      InvElem(gs, D, hi, c);
      var h := gs[hi];
      assert EqIC(h.Key, name);
      SingletonGroupInDict(d, hi);
      EntryInDictIsFound(dict, Entry(h.Key, h.Elements[0]), name);
      assert |h.Elements| != 1;
      assert |h.Elements| >= 2;
      var a, b := h.Elements[0], h.Elements[1];
      assert a in gs[hi].Elements && b in gs[hi].Elements;
      InvElem(gs, D, hi, a);
      InvElem(gs, D, hi, b);
      assert a != b;
      assert a in Spellings(d, name);
      assert b in Spellings(d, name);
      CardAtLeastTwo(Spellings(d, name), a, b);
    }
}

// The rewritten owner field is never "": it is either the declared (non-empty) name or a column
// name equal to it ignoring case, which by the approved OrdinalUpper contract has the same length.
lemma OwnerSpellingNonEmpty(d: Desc, x: NonEmptyCsString)
  ensures DictR(d).Ok?
  ensures |ColumnSpelling(DictR(d).value, x)| > 0
{
  ColumnSpellingRenames(d, x);
  var y := ColumnSpelling(DictR(d).value, x);
  if |Spellings(d, x)| == 1 {
    assert y in Spellings(d, x);
    assert OrdinalUpper(y) == OrdinalUpper(x);
    assert |OrdinalUpper(y)| == |y| && |OrdinalUpper(x)| == |x|;
  }
}

// ---------------------------------------------------------------------------------------------
// Properties (frozen)
// ---------------------------------------------------------------------------------------------

// Citation: "Fix: `SchemaBuilder.BuildDescriptor` rewrites these to the matched column's spelling." (spec §2.2) and "descriptor.Authorization!.OwnerField.Should().Be("OwnerId");" (plan T1 S1a, `BuildDescriptor_RewritesDeclaredNamesToTheMatchedColumnsSpelling`)
lemma P1_OwnerAndFieldPermissionNamesTakeTheColumnsSpelling(d: Desc)
  requires NormalizeDeclaredNames(d).Ok?
  ensures var n := NormalizeDeclaredNames(d).value;
    n.Authorization.Some? == d.Authorization.Some? &&
    (d.Authorization.Some? ==>
      n.Authorization.value.OwnerField.Some? == d.Authorization.value.OwnerField.Some? &&
      (d.Authorization.value.OwnerField.Some? ==>
        Renamed(d, d.Authorization.value.OwnerField.value, n.Authorization.value.OwnerField.value)) &&
      |n.Authorization.value.FieldPermissions| == |d.Authorization.value.FieldPermissions| &&
      forall i :: 0 <= i < |d.Authorization.value.FieldPermissions| ==>
        Renamed(d, d.Authorization.value.FieldPermissions[i].FieldName, n.Authorization.value.FieldPermissions[i].FieldName))
{
  NormalizeUsesDict(d);
  var n := NormalizeDeclaredNames(d).value;
  var dict := DictR(d).value;
  if d.Authorization.Some? {
    var a := d.Authorization.value;
    var na := n.Authorization.value;
    if a.OwnerField.Some? {
      var x := a.OwnerField.value;
      ColumnSpellingRenames(d, x);
      var y := ColumnSpelling(dict, x);
      // The init accessor turns "" into null; y is never "".
      OwnerSpellingNonEmpty(d, x);
      assert |y| > 0;
      assert na.OwnerField.Some? && na.OwnerField.value == y;
    }
    forall i | 0 <= i < |a.FieldPermissions|
      ensures Renamed(d, a.FieldPermissions[i].FieldName, na.FieldPermissions[i].FieldName)
    {
      ColumnSpellingRenames(d, a.FieldPermissions[i].FieldName);
    }
  }
}

// Citation: "For relations, only the foreign keys of `OneToOne`, `ManyToOne` and `ManyToMany` relations are rewritten: a `OneToMany` foreign key names a column of the related type, not of this one." (spec §2.2)
lemma P2_OnlyNonOneToManyForeignKeysAreRewritten(d: Desc)
  requires NormalizeDeclaredNames(d).Ok?
  ensures var n := NormalizeDeclaredNames(d).value;
    |n.Relations| == |d.Relations| &&
    forall i :: 0 <= i < |d.Relations| ==>
      n.Relations[i] == d.Relations[i].(ForeignKey := n.Relations[i].ForeignKey) &&
      (if d.Relations[i].Kind == OneToMany
       then n.Relations[i].ForeignKey == d.Relations[i].ForeignKey
       else Renamed(d, d.Relations[i].ForeignKey, n.Relations[i].ForeignKey))
{
  NormalizeUsesDict(d);
  var n := NormalizeDeclaredNames(d).value;
  forall i | 0 <= i < |d.Relations|
    ensures n.Relations[i] == d.Relations[i].(ForeignKey := n.Relations[i].ForeignKey) &&
      (if d.Relations[i].Kind == OneToMany
       then n.Relations[i].ForeignKey == d.Relations[i].ForeignKey
       else Renamed(d, d.Relations[i].ForeignKey, n.Relations[i].ForeignKey))
  {
    ColumnSpellingRenames(d, d.Relations[i].ForeignKey);
  }
}

// Citation: "A stored type that already has them stays loaded, but a payload key that matches more than one column is refused." (spec, Decisions 7) and "registry.IsRegistered("Note").Should().BeTrue();" (plan T1 S1a, `LoadAsync_ANameMatchingCaseDuplicateColumns_IsLeftAsDeclared_AndTheTypeStillLoads`)
lemma P3_NormalizationNeverFails(d: Desc)
  ensures NormalizeDeclaredNames(d).Ok?
{
  DictFacts(d);
  NormalizeUsesDict(d);
}
