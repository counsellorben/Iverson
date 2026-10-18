// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T1 S3
// Source: Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs `RegisterAsync`: the navigation/column loop that 3e adds after the navigation/foreign-key collision loop
// Code ref: f7a0842fc0e707359e777ddf44f7c2e23050c968
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/SchemaRegistrationOrchestrator.NavigationColumnCheck.dfy

// ---------------------------------------------------------------- shared vocabulary

type CsString = s: seq<char> | |s| < 0x8000_0000

datatype Option<T> = None | Some(value: T)

datatype StatusCode = OK | Cancelled | Unknown | InvalidArgument | DeadlineExceeded | NotFound | AlreadyExists | PermissionDenied | ResourceExhausted | FailedPrecondition | Aborted | OutOfRange | Unimplemented | Internal | Unavailable | DataLoss | Unauthenticated

// Approved axiom: StringComparison.OrdinalIgnoreCase folds each operand with this.
function OrdinalUpper(s: seq<char>): seq<char>

predicate EqIC(a: seq<char>, b: seq<char>) { OrdinalUpper(a) == OrdinalUpper(b) }

datatype Column = Column(Name: CsString, SqlType: CsString, IsNullable: bool)

datatype RelationKind = OneToOne | OneToMany | ManyToOne | ManyToMany

datatype Relation = Relation(PropertyName: CsString, Kind: RelationKind, RelatedTypeName: CsString, ForeignKey: CsString)

// Fields no header of this candidate names (VectorFields, ChunkFields, Authorization,
// TenantColumn, PopularitySignalColumn, OwnerTenantId, EnrichmentTargets,
// HasDocumentTemplate) are omitted. The tenant column, when present, is an entry of
// ScalarColumns and so is part of ColumnNames(d).
datatype Desc = Desc(TypeName: CsString, TableName: CsString, KeyColumn: Column, ScalarColumns: seq<Column>, Relations: seq<Relation>)

// d.KeyColumn.Name, then each d.ScalarColumns[i].Name in order:
// descriptor.ScalarColumns.Select(c => c.Name).Prepend(descriptor.KeyColumn.Name)
function ScalarNames(cs: seq<Column>): seq<CsString>
  ensures |ScalarNames(cs)| == |cs|
  ensures forall i :: 0 <= i < |cs| ==> ScalarNames(cs)[i] == cs[i].Name
{
  if |cs| == 0 then [] else [cs[0].Name] + ScalarNames(cs[1..])
}

function ColumnNames(d: Desc): seq<CsString>
{
  [d.KeyColumn.Name] + ScalarNames(d.ScalarColumns)
}

// ---------------------------------------------------------------- transliteration

datatype Check = Pass | Refused(code: StatusCode, detail: seq<char>)

// relation.Kind interpolated into the message (enum ToString()).
function KindName(k: RelationKind): seq<char>
{
  match k
  case OneToOne => "OneToOne"
  case OneToMany => "OneToMany"
  case ManyToOne => "ManyToOne"
  case ManyToMany => "ManyToMany"
}

// names.FirstOrDefault(name => string.Equals(name, propertyName, OrdinalIgnoreCase)),
// with the default (null) as None.
function FirstMatchIC(names: seq<CsString>, propertyName: CsString): Option<CsString>
{
  if |names| == 0 then None
  else if EqIC(names[0], propertyName) then Some(names[0])
  else FirstMatchIC(names[1..], propertyName)
}

// The foreach over descriptor.Relations, from index i; a throw is Refused.
function NavigationColumnLoop(d: Desc, i: nat): Check
  decreases |d.Relations| - i
{
  if i >= |d.Relations| then Pass
  else
    var relation := d.Relations[i];
    var column := FirstMatchIC(ColumnNames(d), relation.PropertyName);
    if column.Some? then
      Refused(InvalidArgument,
        "Relation '" + relation.PropertyName + "' (" + KindName(relation.Kind) + ") on '" + d.TypeName + "' has a " +
        "navigation-property name that matches column '" + column.value + "' ignoring case. A navigation " +
        "property must not share its name with any property, in any letter case.")
    else
      NavigationColumnLoop(d, i + 1)
}

function NavigationColumnCheck(d: Desc): Check
{
  NavigationColumnLoop(d, 0)
}

// ---------------------------------------------------------------- helper lemmas

lemma FirstMatchICSpec(names: seq<CsString>, p: CsString)
  ensures FirstMatchIC(names, p).Some? <==> exists c :: c in names && EqIC(p, c)
  ensures FirstMatchIC(names, p).Some? ==> FirstMatchIC(names, p).value in names
  decreases |names|
{
  if |names| > 0 {
    FirstMatchICSpec(names[1..], p);
    if EqIC(names[0], p) {
      assert EqIC(p, names[0]) && names[0] in names;
    } else {
      forall c | c in names && EqIC(p, c)
        ensures c in names[1..]
      {
        var k :| 0 <= k < |names| && names[k] == c;
        assert k != 0;
        assert names[1..][k - 1] == c;
      }
    }
  }
}

lemma LoopSpec(d: Desc, i: nat)
  requires i <= |d.Relations|
  ensures NavigationColumnLoop(d, i).Refused? <==>
    exists j, c :: i <= j < |d.Relations| && c in ColumnNames(d) && EqIC(d.Relations[j].PropertyName, c)
  ensures NavigationColumnLoop(d, i).Refused? ==> NavigationColumnLoop(d, i).code == InvalidArgument
  decreases |d.Relations| - i
{
  if i < |d.Relations| {
    FirstMatchICSpec(ColumnNames(d), d.Relations[i].PropertyName);
    LoopSpec(d, i + 1);
    if FirstMatchIC(ColumnNames(d), d.Relations[i].PropertyName).None? {
      forall j, c | i <= j < |d.Relations| && c in ColumnNames(d) && EqIC(d.Relations[j].PropertyName, c)
        ensures i + 1 <= j
      {
      }
    }
  }
}

// ---------------------------------------------------------------- properties

// Citation: "`RegisterSchema` refuses with `InvalidArgument`:" / "a relation navigation property whose name matches any column case-insensitively." (spec §2.4)
lemma P1_ANavigationNamedLikeAnyColumnIsRefused(d: Desc)
  ensures NavigationColumnCheck(d).Refused? <==>
    exists r, c :: r in d.Relations && c in ColumnNames(d) && EqIC(r.PropertyName, c)
  ensures NavigationColumnCheck(d).Refused? ==> NavigationColumnCheck(d).code == InvalidArgument
{
  LoopSpec(d, 0);
  if exists r, c :: r in d.Relations && c in ColumnNames(d) && EqIC(r.PropertyName, c) {
    var r, c :| r in d.Relations && c in ColumnNames(d) && EqIC(r.PropertyName, c);
    var j :| 0 <= j < |d.Relations| && d.Relations[j] == r;
    assert 0 <= j < |d.Relations| && c in ColumnNames(d) && EqIC(d.Relations[j].PropertyName, c);
  }
  if NavigationColumnCheck(d).Refused? {
    var j, c :| 0 <= j < |d.Relations| && c in ColumnNames(d) && EqIC(d.Relations[j].PropertyName, c);
    assert d.Relations[j] in d.Relations;
  }
}
