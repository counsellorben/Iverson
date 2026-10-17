// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T4 S6
// Source: Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs `ReadableDecayField`
// Replay base: c79d7de067519edb3b325fbc0aa53332944c6d1c
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/ObjectSearchGrpcService.ReadableDecayField.dfy

// ── Shared vocabulary ───────────────────────────────────────────────────────

type CsString = s: seq<char> | |s| < 0x8000_0000

datatype Option<T> = None | Some(value: T)

datatype Column = Column(Name: CsString, SqlType: CsString, IsNullable: bool)
datatype RelationKind = OneToOne | OneToMany | ManyToOne | ManyToMany
datatype Relation = Relation(PropertyName: CsString, Kind: RelationKind, RelatedTypeName: CsString, ForeignKey: CsString)
datatype RowPermission = RowPermission(Role: CsString, CanReadAll: bool, CanWriteAll: bool, CanDeleteAll: bool)
datatype FieldPermission = FieldPermission(FieldName: CsString, ReadableRoles: seq<CsString>, WritableRoles: seq<CsString>)
datatype AuthRules = AuthRules(OwnerField: Option<CsString>, RowPermissions: seq<RowPermission>, FieldPermissions: seq<FieldPermission>)
datatype VectorField = VectorField(PropertyName: CsString)
datatype ChunkField = ChunkField(PropertyName: CsString)
datatype EnrichmentKind = Summary | Keywords | Extracted
datatype Target = Target(ColumnName: CsString, Kind: EnrichmentKind, Hint: Option<CsString>)
datatype Desc = Desc(TypeName: CsString, TableName: CsString, KeyColumn: Column, ScalarColumns: seq<Column>, VectorFields: seq<VectorField>, ChunkFields: seq<ChunkField>, Relations: seq<Relation>, Authorization: Option<AuthRules>, TenantColumn: CsString, PopularitySignalColumn: Option<CsString>, OwnerTenantId: Option<CsString>, EnrichmentTargets: seq<Target>, HasDocumentTemplate: bool)

predicate EqIC(a: seq<char>, b: seq<char>) { OrdinalUpper(a) == OrdinalUpper(b) }

// ── Approved axioms ─────────────────────────────────────────────────────────

function ResolveDecayField(schema: Desc): Option<CsString>

function OrdinalUpper(s: seq<char>): seq<char>

// ── Transliteration ─────────────────────────────────────────────────────────

// IReadOnlySet<string>.Contains on a HashSet built with StringComparer.OrdinalIgnoreCase
// (RowFieldAuthorizationEvaluator.cs:121): EqIC membership.
predicate ContainsIC(set_: seq<CsString>, x: CsString)
{
  exists a :: a in set_ && EqIC(a, x)
}

// ObjectSearchGrpcService.cs:1112-1118
function ReadableDecayField(schema: Desc, allowed: Option<seq<CsString>>): Option<CsString>
{
  var decayField := ResolveDecayField(schema);
  if decayField.Some? && allowed.Some? && !ContainsIC(allowed.value, decayField.value)
  then None
  else decayField
}

// ── Properties ──────────────────────────────────────────────────────────────

// Citation: "the decay field is set to null when `decision.AllowedFields` is non-null and does not contain the decay column (case-insensitive, as `AllowedFields` already is)." (spec §3.2)
lemma P1_ADeniedDecayColumnGivesNoDecay(schema: Desc, allowed: Option<seq<CsString>>)
  requires ResolveDecayField(schema).Some? && allowed.Some?
  requires forall a :: a in allowed.value ==> !EqIC(a, ResolveDecayField(schema).value)
  ensures ReadableDecayField(schema, allowed).None?
{
}

// Citation: "the decay field is set to null when `decision.AllowedFields` is non-null and does not contain the decay column (case-insensitive, as `AllowedFields` already is)." (spec §3.2)
lemma P2_AReadableOrUnrestrictedDecayFieldIsKept(schema: Desc, allowed: Option<seq<CsString>>)
  requires allowed.None? || ResolveDecayField(schema).None? ||
           exists a :: a in allowed.value && EqIC(a, ResolveDecayField(schema).value)
  ensures ReadableDecayField(schema, allowed) == ResolveDecayField(schema)
{
}
