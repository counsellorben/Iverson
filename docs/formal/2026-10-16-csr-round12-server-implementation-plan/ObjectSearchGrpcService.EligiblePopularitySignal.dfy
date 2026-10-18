// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T4 S6
// Source: Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs `EligiblePopularitySignal`
// Code ref: f7a0842fc0e707359e777ddf44f7c2e23050c968
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/ObjectSearchGrpcService.EligiblePopularitySignal.dfy

// ---------------------------------------------------------------------------
// Approved axioms
// ---------------------------------------------------------------------------

function OrdinalUpper(s: seq<char>): seq<char>

// ---------------------------------------------------------------------------
// Shared vocabulary
// ---------------------------------------------------------------------------

type CsString = s: seq<char> | |s| < 0x8000_0000

datatype Option<T> = None | Some(value: T)

predicate EqIC(a: seq<char>, b: seq<char>) { OrdinalUpper(a) == OrdinalUpper(b) }

datatype RelationKind = OneToOne | OneToMany | ManyToOne | ManyToMany

datatype Relation = Relation(PropertyName: CsString, Kind: RelationKind, RelatedTypeName: CsString, ForeignKey: CsString)

datatype RowPermission = RowPermission(Role: CsString, CanReadAll: bool, CanWriteAll: bool, CanDeleteAll: bool)

datatype FieldPermission = FieldPermission(FieldName: CsString, ReadableRoles: seq<CsString>, WritableRoles: seq<CsString>)

datatype AuthRules = AuthRules(OwnerField: Option<CsString>, RowPermissions: seq<RowPermission>, FieldPermissions: seq<FieldPermission>)

// Only the SchemaDescriptor members this candidate (and IsEligible, which it calls) reads.
datatype Desc = Desc(TypeName: CsString, Relations: seq<Relation>, Authorization: Option<AuthRules>, PopularitySignalColumn: Option<CsString>)

// PopularitySignalOptions.cs:10 `public sealed record PopularitySignalEntry(string ParentType, string Relation)`.
datatype Signal = Signal(ParentType: CsString, Relation: CsString)

// SchemaRegistry: a ConcurrentDictionary keyed by StringComparer.OrdinalIgnoreCase.
type Registry = seq<Desc>

// SchemaRegistry.Get (SchemaRegistry.cs:16-17): TryGetValue under OrdinalIgnoreCase; the first
// descriptor whose TypeName is EqIC.
function RegistryGet(r: Registry, name: seq<char>): Option<Desc>
{
  if |r| == 0 then None
  else if EqIC(r[0].TypeName, name) then Some(r[0])
  else RegistryGet(r[1..], name)
}

// ---------------------------------------------------------------------------
// Called code: PopularitySignalEligibility (as in that candidate's model)
// ---------------------------------------------------------------------------

// PopularitySignalEligibility.cs:21-22
function IsEligibleIn(parent: Desc, relation: Relation, registry: Registry): bool
{
  IsEligible(parent, relation, RegistryGet(registry, relation.RelatedTypeName))
}

// PopularitySignalEligibility.cs:60-61
function Readers(rules: AuthRules): set<CsString>
{
  set rp | rp in rules.RowPermissions && rp.CanReadAll :: rp.Role
}

predicate EqualsICNullable(a: CsString, b: Option<CsString>)
{
  b.Some? && EqIC(a, b.value)
}

predicate WhereKeep(fp: FieldPermission, relation: Relation, child: Desc)
{
  |fp.ReadableRoles| > 0 &&
  (EqIC(fp.FieldName, relation.ForeignKey) ||
   EqualsICNullable(fp.FieldName, child.PopularitySignalColumn))
}

predicate IsSubsetOf(s: set<CsString>, other: seq<CsString>)
{
  s <= set r | r in other
}

function WhereAll(fps: seq<FieldPermission>, relation: Relation, child: Desc, parentReaders: set<CsString>): bool
{
  if |fps| == 0 then true
  else if WhereKeep(fps[0], relation, child) && !IsSubsetOf(parentReaders, fps[0].ReadableRoles) then false
  else WhereAll(fps[1..], relation, child, parentReaders)
}

// PopularitySignalEligibility.cs:24-58
function IsEligible(parent: Desc, relation: Relation, child: Option<Desc>): bool
{
  if relation.Kind != OneToMany || child.None? then false
  else if parent.Authorization.None? then true
  else
    var parentRules := parent.Authorization.value;
    if parentRules.OwnerField.Some? then false
    else if child.value.Authorization.None? then false
    else
      var childRules := child.value.Authorization.value;
      var parentReaders := Readers(parentRules);
      if !(parentReaders <= Readers(childRules)) then false
      else WhereAll(childRules.FieldPermissions, relation, child.value, parentReaders)
}

// ---------------------------------------------------------------------------
// Transliteration: ObjectSearchGrpcService.EligiblePopularitySignal
// ---------------------------------------------------------------------------

// ObjectSearchGrpcService.cs:1120-1121
// `_popularitySignal.Signals.FirstOrDefault(s => string.Equals(s.ParentType, schema.TypeName, OrdinalIgnoreCase))`
function FirstSignalFor(signals: seq<Signal>, typeName: CsString): Option<Signal>
{
  if |signals| == 0 then None
  else if EqIC(signals[0].ParentType, typeName) then Some(signals[0])
  else FirstSignalFor(signals[1..], typeName)
}

// ObjectSearchGrpcService.cs:1124-1125
// `schema.Relations.FirstOrDefault(r => string.Equals(r.PropertyName, signal.Relation, OrdinalIgnoreCase))`
function FirstRelationNamed(relations: seq<Relation>, name: CsString): Option<Relation>
{
  if |relations| == 0 then None
  else if EqIC(relations[0].PropertyName, name) then Some(relations[0])
  else FirstRelationNamed(relations[1..], name)
}

// ObjectSearchGrpcService.cs:1118-1129
function EligiblePopularitySignal(signals: seq<Signal>, schema: Desc, registry: Registry): Option<Signal>
{
  var signal := FirstSignalFor(signals, schema.TypeName);                      // :1120-1121
  if signal.None? then None                                                    // :1122
  else
    var relation := FirstRelationNamed(schema.Relations, signal.value.Relation); // :1124-1125
    if relation.Some? && IsEligibleIn(schema, relation.value, registry)          // :1126
    then signal                                                                  // :1127
    else None                                                                    // :1128
}

// ---------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------

lemma FirstSignalForWitness(signals: seq<Signal>, typeName: CsString)
  ensures FirstSignalFor(signals, typeName).Some? ==>
    exists i :: 0 <= i < |signals| &&
      FirstSignalFor(signals, typeName).value == signals[i] &&
      EqIC(signals[i].ParentType, typeName)
{
  if |signals| > 0 && !EqIC(signals[0].ParentType, typeName) {
    FirstSignalForWitness(signals[1..], typeName);
    if FirstSignalFor(signals, typeName).Some? {
      var j :| 0 <= j < |signals[1..]| &&
        FirstSignalFor(signals[1..], typeName).value == signals[1..][j] &&
        EqIC(signals[1..][j].ParentType, typeName);
      assert signals[j + 1] == signals[1..][j];
    }
  } else if |signals| > 0 {
    assert FirstSignalFor(signals, typeName).value == signals[0];
  }
}

lemma FirstRelationNamedWitness(relations: seq<Relation>, name: CsString)
  ensures FirstRelationNamed(relations, name).Some? ==>
    exists k :: 0 <= k < |relations| &&
      FirstRelationNamed(relations, name).value == relations[k] &&
      EqIC(relations[k].PropertyName, name)
{
  if |relations| > 0 && !EqIC(relations[0].PropertyName, name) {
    FirstRelationNamedWitness(relations[1..], name);
    if FirstRelationNamed(relations, name).Some? {
      var j :| 0 <= j < |relations[1..]| &&
        FirstRelationNamed(relations[1..], name).value == relations[1..][j] &&
        EqIC(relations[1..][j].PropertyName, name);
      assert relations[j + 1] == relations[1..][j];
    }
  } else if |relations| > 0 {
    assert FirstRelationNamed(relations, name).value == relations[0];
  }
}

// ---------------------------------------------------------------------------
// Property lemmas
// ---------------------------------------------------------------------------

// Citation: "`ObjectSearchGrpcService` skips ineligible signals when ranking (`PopularityFor`, `RetrievePopularityOrDegradeAsync`)." (spec §3.1)
lemma P1_OnlyAnEligibleSignalIsRankedOn(signals: seq<Signal>, schema: Desc, registry: Registry)
  ensures EligiblePopularitySignal(signals, schema, registry).Some? ==>
    exists i, k :: 0 <= i < |signals| && 0 <= k < |schema.Relations| &&
      EligiblePopularitySignal(signals, schema, registry).value == signals[i] &&
      EqIC(signals[i].ParentType, schema.TypeName) &&
      EqIC(schema.Relations[k].PropertyName, signals[i].Relation) &&
      IsEligibleIn(schema, schema.Relations[k], registry)
{
  if EligiblePopularitySignal(signals, schema, registry).Some? {
    var signal := FirstSignalFor(signals, schema.TypeName);
    FirstSignalForWitness(signals, schema.TypeName);
    var i :| 0 <= i < |signals| && signal.value == signals[i] && EqIC(signals[i].ParentType, schema.TypeName);
    var relation := FirstRelationNamed(schema.Relations, signal.value.Relation);
    FirstRelationNamedWitness(schema.Relations, signal.value.Relation);
    var k :| 0 <= k < |schema.Relations| && relation.value == schema.Relations[k] &&
      EqIC(schema.Relations[k].PropertyName, signal.value.Relation);
    assert EqIC(schema.Relations[k].PropertyName, signals[i].Relation);
    assert IsEligibleIn(schema, schema.Relations[k], registry);
  }
}
