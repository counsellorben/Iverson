// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T4 S5
// Source: Iverson.Server/Iverson.Api/Grpc/PopularitySignalEligibility.cs both `IsEligible` overloads and `Readers`
// Code ref: f7a0842fc0e707359e777ddf44f7c2e23050c968
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/PopularitySignalEligibility.IsEligible.dfy

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

// OwnerField: the C# init accessor normalises "" to null; the model takes the stored value as given
// (the code and the property both test only `is not null` / `None?`).
datatype AuthRules = AuthRules(OwnerField: Option<CsString>, RowPermissions: seq<RowPermission>, FieldPermissions: seq<FieldPermission>)

// Only the SchemaDescriptor members this candidate reads.
datatype Desc = Desc(TypeName: CsString, Authorization: Option<AuthRules>, PopularitySignalColumn: Option<CsString>)

// SchemaRegistry: a ConcurrentDictionary keyed by StringComparer.OrdinalIgnoreCase.
type Registry = seq<Desc>

// SchemaRegistry.Get: TryGetValue under OrdinalIgnoreCase; the first descriptor whose TypeName is EqIC.
function RegistryGet(r: Registry, name: seq<char>): Option<Desc>
{
  if |r| == 0 then None
  else if EqIC(r[0].TypeName, name) then Some(r[0])
  else RegistryGet(r[1..], name)
}

// ---------------------------------------------------------------------------
// Transliteration: PopularitySignalEligibility
// ---------------------------------------------------------------------------

// PopularitySignalEligibility.cs:21-22
function IsEligibleIn(parent: Desc, relation: Relation, registry: Registry): bool
{
  IsEligible(parent, relation, RegistryGet(registry, relation.RelatedTypeName))
}

// PopularitySignalEligibility.cs:60-61
// ToHashSet(StringComparer.Ordinal): ordinal string equality is seq<char> equality.
function Readers(rules: AuthRules): set<CsString>
{
  set rp | rp in rules.RowPermissions && rp.CanReadAll :: rp.Role
}

// string.Equals(a, b?, OrdinalIgnoreCase) with a non-null: false when b is null.
predicate EqualsICNullable(a: CsString, b: Option<CsString>)
{
  b.Some? && EqIC(a, b.value)
}

// The `.Where(...)` predicate at PopularitySignalEligibility.cs:54-56.
predicate WhereKeep(fp: FieldPermission, relation: Relation, child: Desc)
{
  |fp.ReadableRoles| > 0 &&
  (EqIC(fp.FieldName, relation.ForeignKey) ||
   EqualsICNullable(fp.FieldName, child.PopularitySignalColumn))
}

// HashSet<string>.IsSubsetOf(IEnumerable<string>) under the set's Ordinal comparer.
predicate IsSubsetOf(s: set<CsString>, other: seq<CsString>)
{
  s <= set r | r in other
}

// `.Where(keep).All(fp => parentReaders.IsSubsetOf(fp.ReadableRoles))`, enumerated in order.
function WhereAll(fps: seq<FieldPermission>, relation: Relation, child: Desc, parentReaders: set<CsString>): bool
{
  if |fps| == 0 then true
  else if WhereKeep(fps[0], relation, child) && !IsSubsetOf(parentReaders, fps[0].ReadableRoles) then false
  else WhereAll(fps[1..], relation, child, parentReaders)
}

// PopularitySignalEligibility.cs:24-58
function IsEligible(parent: Desc, relation: Relation, child: Option<Desc>): bool
{
  if relation.Kind != OneToMany || child.None? then false                     // :28-29
  else if parent.Authorization.None? then true                               // :32-33
  else
    var parentRules := parent.Authorization.value;
    if parentRules.OwnerField.Some? then false                               // :37-38
    else if child.value.Authorization.None? then false                       // :41-42
    else
      var childRules := child.value.Authorization.value;
      var parentReaders := Readers(parentRules);                             // :46
      if !(parentReaders <= Readers(childRules)) then false                  // :47-48
      else WhereAll(childRules.FieldPermissions, relation, child.value, parentReaders) // :53-57
}

// ---------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------

lemma WhereAllIsForall(fps: seq<FieldPermission>, relation: Relation, child: Desc, pr: set<CsString>)
  ensures WhereAll(fps, relation, child, pr) <==>
    (forall fp :: fp in fps && WhereKeep(fp, relation, child) ==> pr <= set r | r in fp.ReadableRoles)
{
  if |fps| > 0 {
    WhereAllIsForall(fps[1..], relation, child, pr);
    assert forall fp :: fp in fps <==> (fp == fps[0] || fp in fps[1..]);
  }
}

// ---------------------------------------------------------------------------
// Property lemmas
// ---------------------------------------------------------------------------

// Citation: "A missing child or a relation that isn't `OneToMany` makes the signal ineligible, even when `parent.Authorization` is null: that structural check comes first." (spec §3.1)
lemma P1_NotOneToManyOrMissingChild_IsNeverEligible(parent: Desc, relation: Relation, child: Option<Desc>)
  requires relation.Kind != OneToMany || child.None?
  ensures !IsEligible(parent, relation, child)
{
}

// Citation: "Let `PR(X)` be the roles with `CanReadAll` in `X.Authorization.RowPermissions`, compared ordinally. The predicate is true when either:" / "`parent.Authorization` is null (nobody can read the parent, so nothing leaks); or" / "all of the following hold:" / "`parent.Authorization.OwnerField` is null;" / "the child, resolved through the registry from the `OneToMany` relation, has non-null `Authorization`;" / "`PR(parent) ⊆ PR(child)`;" / "every `FieldPermission` of the child on the FK column or on `PopularitySignalColumn` with non-empty `ReadableRoles` satisfies `PR(parent) ⊆ ReadableRoles`." (spec §3.1; " / " separates source lines)
lemma P2_EligibleExactlyWhenEveryParentReaderReadsEveryCountedRow(parent: Desc, relation: Relation, child: Desc)
  requires relation.Kind == OneToMany
  ensures IsEligible(parent, relation, Some(child)) <==>
    (parent.Authorization.None? ||
     (var pr := set rp | rp in parent.Authorization.value.RowPermissions && rp.CanReadAll :: rp.Role;
      parent.Authorization.value.OwnerField.None? &&
      child.Authorization.Some? &&
      pr <= (set rp | rp in child.Authorization.value.RowPermissions && rp.CanReadAll :: rp.Role) &&
      (forall fp :: fp in child.Authorization.value.FieldPermissions && |fp.ReadableRoles| > 0 &&
         (EqIC(fp.FieldName, relation.ForeignKey) ||
          (child.PopularitySignalColumn.Some? && EqIC(fp.FieldName, child.PopularitySignalColumn.value))) ==>
         pr <= set r | r in fp.ReadableRoles)))
{
  if parent.Authorization.Some? && parent.Authorization.value.OwnerField.None? && child.Authorization.Some? {
    var pr := Readers(parent.Authorization.value);
    WhereAllIsForall(child.Authorization.value.FieldPermissions, relation, child, pr);
  }
}

// Citation: "the child, resolved through the registry from the `OneToMany` relation, has non-null `Authorization`;" (spec §3.1) and "PopularitySignalEligibility.IsEligible(Article(ReadableBy("readers")), Comments, registry).Should().BeFalse();" (plan T4 S3, `IsEligible_ChildNotRegistered_ReturnsFalse`)
lemma P3_AnUnregisteredChildIsNeverEligible(parent: Desc, relation: Relation, registry: Registry)
  requires RegistryGet(registry, relation.RelatedTypeName).None?
  ensures !IsEligibleIn(parent, relation, registry)
{
}
