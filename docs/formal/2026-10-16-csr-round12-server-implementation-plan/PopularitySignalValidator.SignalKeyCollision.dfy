// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T4 S6
// Source: Iverson.Server/Iverson.Api/Grpc/PopularitySignalOptions.cs `PopularitySignalValidator.ValidateAtStartup`: the per-signal loop as T4 leaves it; the new collision check is the logic under test
// Code ref: f7a0842fc0e707359e777ddf44f7c2e23050c968
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/PopularitySignalValidator.SignalKeyCollision.dfy

// ---------------------------------------------------------------------------
// Approved axioms
// ---------------------------------------------------------------------------

predicate HasVectorOrChunkFields(d: Desc)

predicate IsEngagementEligible(d: Desc)

function CharToLowerInvariant(c: char): char

function OrdinalUpper(s: seq<char>): seq<char>

// ---------------------------------------------------------------------------
// Shared vocabulary
// ---------------------------------------------------------------------------

type CsString = s: seq<char> | |s| < 0x8000_0000

datatype Option<T> = None | Some(value: T)

predicate EqIC(a: seq<char>, b: seq<char>) { OrdinalUpper(a) == OrdinalUpper(b) }

datatype Column = Column(Name: CsString, SqlType: CsString, IsNullable: bool)

datatype RelationKind = OneToOne | OneToMany | ManyToOne | ManyToMany

datatype Relation = Relation(PropertyName: CsString, Kind: RelationKind, RelatedTypeName: CsString, ForeignKey: CsString)

datatype RowPermission = RowPermission(Role: CsString, CanReadAll: bool, CanWriteAll: bool, CanDeleteAll: bool)

datatype FieldPermission = FieldPermission(FieldName: CsString, ReadableRoles: seq<CsString>, WritableRoles: seq<CsString>)

datatype AuthRules = AuthRules(OwnerField: Option<CsString>, RowPermissions: seq<RowPermission>, FieldPermissions: seq<FieldPermission>)

// Only the SchemaDescriptor members this candidate reads (VectorFields/ChunkFields are read only
// through the approved HasVectorOrChunkFields, and IsEngagementEligible is approved too).
datatype Desc = Desc(TypeName: CsString, KeyColumn: Column, ScalarColumns: seq<Column>, Relations: seq<Relation>,
                     Authorization: Option<AuthRules>, PopularitySignalColumn: Option<CsString>)

// SchemaRegistry: a ConcurrentDictionary keyed by StringComparer.OrdinalIgnoreCase.
type Registry = seq<Desc>

// SchemaRegistry.Get (SchemaRegistry.cs:16-17): the first descriptor whose TypeName is EqIC.
function RegistryGet(r: Registry, name: seq<char>): Option<Desc>
{
  if |r| == 0 then None
  else if EqIC(r[0].TypeName, name) then Some(r[0])
  else RegistryGet(r[1..], name)
}

// NamingExtensions.cs:16-17: `char.ToLowerInvariant(name[0]) + name[1..]` (char + string concatenates).
function ToCamelCase(name: CsString): CsString
{
  if |name| == 0 then name else [CharToLowerInvariant(name[0])] + name[1..]
}

// PopularitySignalEntry(string ParentType, string Relation): both non-nullable.
datatype Signal = Signal(ParentType: CsString, Relation: CsString)

datatype Validation = Returned | InvalidOperation(message: seq<char>)

// ---------------------------------------------------------------------------
// PopularitySignalEligibility (modelled from PopularitySignalEligibility.cs; its result only
// selects a log line here, so it never changes the outcome)
// ---------------------------------------------------------------------------

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
// Transliteration: PopularitySignalValidator.ValidateAtStartup
// ---------------------------------------------------------------------------

const Section: seq<char> := "PopularitySignal"           // PopularitySignalOptions.Section
const EngagementSection: seq<char> := "Engagement"       // EngagementStoreOptions.Section

// HashSet<string>(StringComparer.OrdinalIgnoreCase).Add returns false when an EqIC element exists.
predicate SeenContains(seen: seq<CsString>, p: seq<char>)
{
  exists k :: 0 <= k < |seen| && EqIC(seen[k], p)
}

// Enum ToString() in the interpolation at :130.
function KindName(k: RelationKind): seq<char>
{
  match k
  case OneToOne => "OneToOne"
  case OneToMany => "OneToMany"
  case ManyToOne => "ManyToOne"
  case ManyToMany => "ManyToMany"
}

// The `FirstOrDefault` predicate at :111-113.
predicate Collides(c: Column, countKey: seq<char>)
{
  EqIC(c.Name, countKey) || EqIC(c.Name, countKey + "Buckets")
}

// `ScalarColumns.Append(KeyColumn).FirstOrDefault(...)` at :111, enumerated in order.
function FirstColliding(cs: seq<Column>, countKey: seq<char>): Option<Column>
{
  if |cs| == 0 then None
  else if Collides(cs[0], countKey) then Some(cs[0])
  else FirstColliding(cs[1..], countKey)
}

// `Relations.FirstOrDefault(r => string.Equals(r.PropertyName, signal.Relation, OrdinalIgnoreCase))` at :121-122.
function FirstRelation(rs: seq<Relation>, name: seq<char>): Option<Relation>
{
  if |rs| == 0 then None
  else if EqIC(rs[0].PropertyName, name) then Some(rs[0])
  else FirstRelation(rs[1..], name)
}

// PopularitySignalOptions.cs:70-164. `options.RecencyBoost > 0` is the bool recencyBoostPositive;
// logger is a no-op.
function ValidateAtStartup(signals: seq<Signal>, recencyBoostPositive: bool, registry: Registry, engagementEnabled: bool): Validation
{
  Loop(signals, 0, [], recencyBoostPositive, registry, engagementEnabled)       // :73-74
}

// The foreach body, one iteration per call (:74-163); `seen` is seenParentTypes.
function Loop(signals: seq<Signal>, j: int, seen: seq<CsString>, recencyBoostPositive: bool, registry: Registry, engagementEnabled: bool): Validation
  requires 0 <= j <= |signals|
  decreases |signals| - j
{
  if j == |signals| then Returned
  else
    var signal := signals[j];
    if SeenContains(seen, signal.ParentType) then                                 // :76
      InvalidOperation(Section + ": ParentType '" + signal.ParentType + "' is " +
        "configured more than once. Only one signal per ParentType is supported — " +
        "the read side resolves a single signal per type via FirstOrDefault, so a " +
        "second entry would be written but silently never ranked on.")           // :77-81
    else
      var seen' := seen + [signal.ParentType];                                    // :76 (Add succeeded)
      if !engagementEnabled then                                                  // :83
        InvalidOperation(Section + ": Signals is non-empty but " +
          EngagementSection + ":Enabled is false — this feature has no " +
          "runtime degrade path for a disabled engagement store.")                // :84-87
      else
        var parentSchema := RegistryGet(registry, signal.ParentType);             // :89
        if parentSchema.None? then                                                // :90
          Loop(signals, j + 1, seen', recencyBoostPositive, registry, engagementEnabled) // :92-98 (log; continue)
        else
          var p := parentSchema.value;
          if !HasVectorOrChunkFields(p) then                                      // :101
            InvalidOperation(Section + ": ParentType '" + signal.ParentType + "' has no " +
              "vector or chunk fields, so it has no Qdrant point to ever patch.") // :102-104
          else
            var countKey := ToCamelCase(signal.Relation) + "Count";               // :110
            var collidingColumn := FirstColliding(p.ScalarColumns + [p.KeyColumn], countKey); // :111-113
            if collidingColumn.Some? then                                         // :114
              InvalidOperation(Section + ": column '" + collidingColumn.value.Name + "' on " +
                "'" + signal.ParentType + "' has the name of a payload key the signal for relation " +
                "'" + signal.Relation + "' writes ('" + countKey + "' or '" + countKey + "Buckets'). Rename the " +
                "column or remove the signal.")                                   // :115-119
            else
              var relation := FirstRelation(p.Relations, signal.Relation);       // :121-122
              if relation.None? then                                              // :123
                InvalidOperation(Section + ": '" + signal.Relation + "' is not a relation on " +
                  "'" + signal.ParentType + "'.")                                 // :123-125
              else if relation.value.Kind != OneToMany then                       // :127
                InvalidOperation(Section + ": relation '" + signal.Relation + "' on " +
                  "'" + signal.ParentType + "' is " + KindName(relation.value.Kind) + ", not OneToMany — a count is only " +
                  "meaningful for a one-to-many relation.")                       // :128-131
              else
                var childSchema := RegistryGet(registry, relation.value.RelatedTypeName); // :133
                if childSchema.None? then                                         // :134
                  InvalidOperation(Section + ": related type '" + relation.value.RelatedTypeName + "' " +
                    "(from relation '" + signal.Relation + "') is not a registered schema.") // :134-136
                else if !IsEngagementEligible(childSchema.value) then             // :138
                  InvalidOperation(Section + ": child type '" + relation.value.RelatedTypeName + "' is " +
                    "not StarRocks-eligible (it declares a OneToMany relation of its own), so its " +
                    "count can never be computed.")                               // :139-142
                else
                  var eligible := IsEligible(p, relation.value, childSchema);    // :146 (selects a log line only)
                  var recencyWarn := recencyBoostPositive && childSchema.value.PopularitySignalColumn.None?; // :156 (log only)
                  Loop(signals, j + 1, seen', recencyBoostPositive, registry, engagementEnabled) // next iteration
}

// ---------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------

lemma FirstCollidingFinds(cs: seq<Column>, countKey: seq<char>, c: Column)
  requires c in cs && Collides(c, countKey)
  ensures FirstColliding(cs, countKey).Some?
{
  if !Collides(cs[0], countKey) {
    assert c in cs[1..];
    FirstCollidingFinds(cs[1..], countKey, c);
  }
}

// Every iteration before i either throws InvalidOperation or continues; iteration i throws.
lemma LoopRefusesAt(signals: seq<Signal>, j: int, i: int, seen: seq<CsString>, recencyBoostPositive: bool, registry: Registry, engagementEnabled: bool, c: Column)
  requires 0 <= j <= i < |signals|
  requires RegistryGet(registry, signals[i].ParentType).Some?
  requires c == RegistryGet(registry, signals[i].ParentType).value.KeyColumn ||
           c in RegistryGet(registry, signals[i].ParentType).value.ScalarColumns
  requires Collides(c, ToCamelCase(signals[i].Relation) + "Count")
  ensures Loop(signals, j, seen, recencyBoostPositive, registry, engagementEnabled).InvalidOperation?
  decreases i - j
{
  var signal := signals[j];
  var seen' := seen + [signal.ParentType];
  if j < i {
    if !SeenContains(seen, signal.ParentType) && engagementEnabled {
      LoopRefusesAt(signals, j + 1, i, seen', recencyBoostPositive, registry, engagementEnabled, c);
    }
  } else {
    var p := RegistryGet(registry, signal.ParentType).value;
    var countKey := ToCamelCase(signal.Relation) + "Count";
    assert c in p.ScalarColumns + [p.KeyColumn];
    FirstCollidingFinds(p.ScalarColumns + [p.KeyColumn], countKey, c);
  }
}

// ---------------------------------------------------------------------------
// Property lemmas
// ---------------------------------------------------------------------------

// Citation: "It also refuses a configured signal whose key (`{relation camelCase}Count` or `…CountBuckets`) case-insensitively matches a parent column." (spec §3.1)
lemma P1_ASignalKeyCollidingWithAParentColumnIsRefused(signals: seq<Signal>, recencyBoostPositive: bool, registry: Registry, engagementEnabled: bool, i: int, c: Column)
  requires 0 <= i < |signals|
  requires RegistryGet(registry, signals[i].ParentType).Some?
  requires c == RegistryGet(registry, signals[i].ParentType).value.KeyColumn ||
           c in RegistryGet(registry, signals[i].ParentType).value.ScalarColumns
  requires EqIC(c.Name, ToCamelCase(signals[i].Relation) + "Count") ||
           EqIC(c.Name, ToCamelCase(signals[i].Relation) + "CountBuckets")
  ensures ValidateAtStartup(signals, recencyBoostPositive, registry, engagementEnabled).InvalidOperation?
{
  var k := ToCamelCase(signals[i].Relation);
  assert (k + "Count") + "Buckets" == k + "CountBuckets";
  assert Collides(c, k + "Count");
  LoopRefusesAt(signals, 0, i, [], recencyBoostPositive, registry, engagementEnabled, c);
}
